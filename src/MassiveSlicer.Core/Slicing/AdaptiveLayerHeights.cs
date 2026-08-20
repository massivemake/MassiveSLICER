using System.Numerics;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Computes adaptive Z slice positions using surface-normal analysis.
///
/// Based on Wasserfall et al. "Adaptive Slicing for the FDM Process Revisited" (CASE 2017)
/// with Vojtech Bubnik's triangle-area error metric, as implemented in OrcaSlicer / PrusaSlicer.
///
/// The core insight: stairstepping is most visible on surfaces that slope gently from horizontal.
/// Near-vertical faces impose no constraint because each layer adds only a tiny horizontal step.
/// Quality factor [0=finest, 1=coarsest] linearly scales the allowed surface deviation between
/// MinLayerHeight and MaxLayerHeight, which drives layer thickness via the slope formula.
/// </summary>
public static class AdaptiveLayerHeights
{
    private readonly struct FaceZ
    {
        public float ZMin { get; init; }
        public float ZMax { get; init; }
        public float NCos { get; init; }  // |n.z|            — vertical component of unit normal
        public float NSin { get; init; }  // sqrt(n.x²+n.y²) — horizontal component
        /// <summary>
        /// Triangle area (mm²). NOT used to choose a layer height — the height is a plain
        /// minimum over every straddling face, so a sliver has the same authority as a wall.
        /// Recorded so <see cref="LastReasons"/> can say whether that is actually happening.
        /// </summary>
        public float Area { get; init; }
    }

    /// <summary>
    /// Why one layer ended up the thickness it did. Diagnostics only — nothing reads this
    /// to make a decision.
    ///
    /// The open question it exists to answer: layer thickness is the minimum demand of any
    /// single triangle crossing that Z, unweighted, so one tiny sliver can pin a whole layer
    /// thin. If <see cref="BindingArea"/> on the thin layers turns out to be small relative
    /// to the rest, area-weighting the minimum is worth doing. If it does not, the jumping
    /// has another cause and weighting would be a fix for the wrong thing.
    /// </summary>
    public sealed record LayerHeightReason(
        float Z,
        float Height,
        float BindingArea,
        float BindingSlopeDeg,
        float MeanStraddlingArea,
        int   FacesStraddling,
        bool  SnappedToFaceBottom,
        bool  AtFloor,
        bool  AtMax,
        int   FacesGated = 0,
        bool  GateChangedTheOutcome = false);

    /// <summary>
    /// Per-layer reasons from the most recent completed <see cref="ComputeZPositions"/> call.
    ///
    /// Published as a finished array in one reference assignment, never built in place: slicing
    /// runs off the UI thread, so a reader (the console command) would otherwise enumerate a list
    /// that is still being appended to and throw "Collection was modified". A reader now sees
    /// either the previous complete set or the new complete set — never a torn one.
    /// </summary>
    public static IReadOnlyList<LayerHeightReason> LastReasons => s_lastReasons;

    private static LayerHeightReason[] s_lastReasons = [];

    /// <summary>
    /// Returns the list of Z values to slice at.
    /// </summary>
    /// <param name="meshes">Flat triangle soups (every 3 verts = one triangle) in world space.</param>
    /// <param name="zMin">Mesh bottom Z.</param>
    /// <param name="zMax">Mesh top Z.</param>
    /// <param name="firstLayerHeight">Height of the very first layer.</param>
    /// <param name="minLayerHeight">Minimum allowed layer height (mm).</param>
    /// <param name="maxLayerHeight">Maximum allowed layer height (mm). Typically == nominal LayerHeight.</param>
    /// <param name="qualityFactor">0 = finest detail (min layers), 1 = fastest (max layers).</param>
    /// <param name="minFaceAreaMm2">
    /// Smallest triangle allowed to dictate a thickness. 0 or less = every triangle votes.
    /// See <see cref="Models.SliceSettings.AdaptiveMinFaceAreaMm2"/> for why this exists.
    /// </param>
    public static float[] ComputeZPositions(
        IReadOnlyList<Vector3[]> meshes,
        float zMin, float zMax,
        float firstLayerHeight,
        float minLayerHeight, float maxLayerHeight,
        float qualityFactor,
        float minFaceAreaMm2 = 0f,
        bool recordReasons = true)
    {
        qualityFactor = Math.Clamp(qualityFactor, 0f, 1f);

        var faces = BuildFaces(meshes);
        // Sort by ZMin so the range-scan loop below can exit early.
        faces.Sort((a, b) => a.ZMin.CompareTo(b.ZMin));

        var positions = new List<float>();
        float z = zMin + firstLayerHeight;
        int currentFacet = 0;

        // Accumulated locally and published at the end — see LastReasons.
        var reasons = new List<LayerHeightReason>();

        while (z < zMax - 1e-4f)
        {
            positions.Add(z);
            float h = NextLayerHeight(faces, z, qualityFactor,
                minLayerHeight, maxLayerHeight, minFaceAreaMm2, ref currentFacet, out var why);

            reasons.Add(new LayerHeightReason(
                z, h,
                why.BindingArea,
                why.BindingSlopeDeg,
                why.MeanStraddlingArea,
                why.FacesStraddling,
                why.Snapped,
                AtFloor: h <= minLayerHeight + 1e-4f,
                AtMax:   h >= maxLayerHeight - 1e-4f,
                FacesGated: why.FacesGated,
                GateChangedTheOutcome: why.GateChangedTheOutcome));

            z += h;
        }

        // One reference assignment, after the walk is finished: a concurrent reader gets the old
        // complete array or the new complete array, never a partially built one.
        // Only a real slice publishes these. The layer preview calls this on every settings
        // keystroke, and it used to overwrite the static that adaptive-height-debug reads — so the
        // diagnostic described a ladder that was not the toolpath on screen.
        if (recordReasons) s_lastReasons = [.. reasons];

        return [.. positions];
    }

    /// <summary>What decided one layer's height. Populated alongside the height, never read by it.</summary>
    private struct HeightReason
    {
        public float BindingArea;
        public float BindingSlopeDeg;
        public float MeanStraddlingArea;
        public int   FacesStraddling;
        public int   FacesGated;
        public bool  GateChangedTheOutcome;
        public bool  Snapped;
    }

    private static float NextLayerHeight(
        List<FaceZ> faces, float printZ, float quality,
        float minH, float maxH, float minFaceAreaMm2, ref int currentFacet, out HeightReason why)
    {
        float height = maxH;
        why = default;
        double areaSum = 0.0;

        // Map quality [0,1] → max allowed surface deviation [minH, maxH].
        // quality=0 → maxDev=minH  (tight tolerance, thin layers everywhere)
        // quality=1 → maxDev=maxH  (loose tolerance, thick layers where geometry allows)
        float maxDev = minH + quality * (maxH - minH);

        // ── Should the gate apply to THIS layer? ──────────────────────────────────
        // A cross-section that is nothing but slivers must not silently jump to full thickness —
        // that would trade a jumpy-but-cautious answer for a confidently wrong one. So decide up
        // front: if nothing in the widest window this layer could consult clears the gate, stand
        // the gate down and let every triangle vote, exactly as before.
        //
        // Decided BEFORE measuring rather than as a fallback afterwards, because the second pass's
        // range depends on the tentative height — so a gated and an ungated run would consult
        // different face sets and could not be compared after the fact. (Getting that wrong is
        // what made the first version of this silently take full thickness.)
        bool gating = minFaceAreaMm2 > 0f;
        if (gating)
        {
            bool anyClears = false;
            for (int i = currentFacet; i < faces.Count; i++)
            {
                var f = faces[i];
                if (f.ZMin >= printZ + maxH) break;          // widest the second pass can reach
                if (f.ZMax < printZ + 1e-4f) continue;
                if (f.Area >= minFaceAreaMm2) { anyClears = true; break; }
            }
            gating = anyClears;
        }

        // ── First pass: scan active facets from the last known position ────────
        // Facets are sorted by ZMin. We walk forward from currentFacet, looking for
        // any facet that straddles printZ (ZMin < printZ < ZMax).
        int orderedId = currentFacet;
        bool firstHit = false;
        for (; orderedId < faces.Count; orderedId++)
        {
            var f = faces[orderedId];
            // Sorted list: once ZMin ≥ printZ nothing further can intersect from below.
            if (f.ZMin >= printZ) break;
            if (f.ZMax > printZ)
            {
                // Remember where the scan should restart next call.
                if (!firstHit) { firstHit = true; currentFacet = orderedId; }
                // Skip faces whose top just barely touches printZ (degenerate contact).
                if (f.ZMax < printZ + 1e-4f) continue;

                why.FacesStraddling++;
                areaSum += f.Area;

                float h = LayerHeightFromSlope(f, maxDev);

                if (gating && f.Area < minFaceAreaMm2)
                {
                    why.FacesGated++;
                    if (h < height) why.GateChangedTheOutcome = true;
                    continue;
                }

                if (h < height) { height = h; RecordBinding(ref why, f, snapped: false); }
            }
        }

        height = MathF.Max(height, minH);

        // ── Second pass: check newly revealed facets inside the tentative height ─
        // A face starting inside [printZ, printZ+height) might further restrict height.
        if (height > minH)
        {
            for (; orderedId < faces.Count; orderedId++)
            {
                var f = faces[orderedId];
                if (f.ZMin >= printZ + height) break;
                if (f.ZMax < printZ + 1e-4f) continue;

                why.FacesStraddling++;
                areaSum += f.Area;

                float reducedH = LayerHeightFromSlope(f, maxDev);
                float zDiff    = f.ZMin - printZ;

                // A sliver must not pull the boundary down, and must not snap it onto its own
                // bottom edge either — that snap is how a tessellation vertex ends up deciding
                // a thickness. Gate it the same way, on the same fallback rule.
                if (gating && f.Area < minFaceAreaMm2)
                {
                    why.FacesGated++;
                    if (reducedH < height || reducedH < zDiff) why.GateChangedTheOutcome = true;
                    continue;
                }

                if (reducedH < zDiff)
                {
                    // The face's bottom is already above the proposed layer — snap to it.
                    height = zDiff;
                    RecordBinding(ref why, f, snapped: true);
                }
                else if (reducedH < height)
                {
                    height = reducedH;
                    RecordBinding(ref why, f, snapped: false);
                }
            }
            height = MathF.Max(height, minH);
        }

        why.MeanStraddlingArea = why.FacesStraddling > 0
            ? (float)(areaSum / why.FacesStraddling) : 0f;

        return height;
    }

    /// <summary>
    /// Notes which face is currently setting the height. Observational only — it never
    /// feeds back into the choice.
    /// </summary>
    private static void RecordBinding(ref HeightReason why, in FaceZ f, bool snapped)
    {
        why.BindingArea = f.Area;
        // NSin/NCos = tan(surface slope from horizontal): 0° is a flat face, 90° a vertical wall.
        why.BindingSlopeDeg = MathF.Atan2(f.NSin, f.NCos) * (180f / MathF.PI);
        why.Snapped = snapped;
    }

    // Vojtech's triangle-area error metric (from OrcaSlicer/PrusaSlicer SlicingAdaptive.cpp).
    // Returns the maximum safe layer height that keeps surface deviation within maxDev.
    // The min with maxDev/0.184 caps the result for near-horizontal faces where the
    // Vojtech term would otherwise collapse to zero.
    private static float LayerHeightFromSlope(in FaceZ face, float maxDev)
    {
        float vojtech = face.NCos > 1e-5f
            ? 1.44f * maxDev * MathF.Sqrt(face.NSin / face.NCos)
            : float.MaxValue;
        return MathF.Min(maxDev / 0.184f, vojtech);
    }

    private static List<FaceZ> BuildFaces(IReadOnlyList<Vector3[]> meshes)
    {
        var faces = new List<FaceZ>();
        foreach (var verts in meshes)
        {
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                var v0 = verts[i]; var v1 = verts[i + 1]; var v2 = verts[i + 2];
                var n   = Vector3.Cross(v1 - v0, v2 - v0);
                float len = n.Length();
                if (len < 1e-8f) continue;
                n /= len;
                faces.Add(new FaceZ
                {
                    ZMin = MathF.Min(MathF.Min(v0.Z, v1.Z), v2.Z),
                    ZMax = MathF.Max(MathF.Max(v0.Z, v1.Z), v2.Z),
                    NCos = MathF.Abs(n.Z),
                    NSin = MathF.Sqrt(n.X * n.X + n.Y * n.Y),
                    // len is the cross-product magnitude, i.e. twice the triangle area.
                    Area = len * 0.5f
                });
            }
        }
        return faces;
    }
}
