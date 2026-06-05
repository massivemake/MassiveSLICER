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
    }

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
    public static float[] ComputeZPositions(
        IReadOnlyList<Vector3[]> meshes,
        float zMin, float zMax,
        float firstLayerHeight,
        float minLayerHeight, float maxLayerHeight,
        float qualityFactor)
    {
        qualityFactor = Math.Clamp(qualityFactor, 0f, 1f);

        var faces = BuildFaces(meshes);
        // Sort by ZMin so the range-scan loop below can exit early.
        faces.Sort((a, b) => a.ZMin.CompareTo(b.ZMin));

        var positions = new List<float>();
        float z = zMin + firstLayerHeight;
        int currentFacet = 0;

        while (z < zMax - 1e-4f)
        {
            positions.Add(z);
            float h = NextLayerHeight(faces, z, qualityFactor,
                minLayerHeight, maxLayerHeight, ref currentFacet);
            z += h;
        }

        return [.. positions];
    }

    private static float NextLayerHeight(
        List<FaceZ> faces, float printZ, float quality,
        float minH, float maxH, ref int currentFacet)
    {
        float height = maxH;

        // Map quality [0,1] → max allowed surface deviation [minH, maxH].
        // quality=0 → maxDev=minH  (tight tolerance, thin layers everywhere)
        // quality=1 → maxDev=maxH  (loose tolerance, thick layers where geometry allows)
        float maxDev = minH + quality * (maxH - minH);

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
                float h = LayerHeightFromSlope(f, maxDev);
                if (h < height) height = h;
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

                float reducedH = LayerHeightFromSlope(f, maxDev);
                float zDiff    = f.ZMin - printZ;

                if (reducedH < zDiff)
                    // The face's bottom is already above the proposed layer — snap to it.
                    height = zDiff;
                else if (reducedH < height)
                    height = reducedH;
            }
            height = MathF.Max(height, minH);
        }

        return height;
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
                    NSin = MathF.Sqrt(n.X * n.X + n.Y * n.Y)
                });
            }
        }
        return faces;
    }
}
