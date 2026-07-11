using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Lightning;

/// <summary>
/// X-bracing matching LFAM reference prints: continuous dual-wall diagonal
/// channels <b>into</b> the wall. Free edges are <b>straight</b> world-space
/// diagonals on a smooth baseline (chord or cylinder unwrap) — they do not
/// follow perimeter wiggles. The real path is only used to stitch hairpins in/out.
/// </summary>
public static class XBracingPlanner
{
    /// <summary>
    /// World-stable along-wall baseline. U is linear distance along this baseline.
    /// Orientation is locked in <see cref="OpenPathDetourState"/> so zig-zag path
    /// reverse does not flip in/out or mirror the X each layer.
    /// </summary>
    private readonly struct StraightBaseline
    {
        public Vector2 Origin { get; init; }
        public Vector2 Unit { get; init; }
        public float Length { get; init; }
        public bool IsCylinder { get; init; }
        public Vector2 CylinderCenter { get; init; }
        /// <summary>Reference radius (avg); wall points use local path radius.</summary>
        public float Radius { get; init; }
        public float Theta0 { get; init; }
        public float ThetaSign { get; init; }
        /// <summary>World-stable into-wall unit (same every layer for this contour).</summary>
        public Vector2 InwardUnit { get; init; }
        /// <summary>
        /// This layer's coverage in the LOCKED world frame (U measured from the
        /// locked origin/Theta0, NOT re-shifted per layer). On panels with slanted
        /// or scalloped edges the covered U range moves with Z — the world-anchored
        /// cell grid stays put and ribs enter/exit through the edges.
        /// </summary>
        public float UMin { get; init; }
        public float UMax { get; init; }

        public float ThetaAt(float u)
        {
            u = Math.Clamp(u, UMin, MathF.Max(UMax, UMin));
            if (IsCylinder && Radius > 1e-3f)
                return Theta0 + ThetaSign * (u / Radius);
            return 0f;
        }

        /// <summary>Smooth along-wall sample (cylinder uses ref radius).</summary>
        public Vector2 PointAt(float u)
        {
            u = Math.Clamp(u, UMin, MathF.Max(UMax, UMin));
            if (IsCylinder && Radius > 1e-3f)
            {
                float th = ThetaAt(u);
                return CylinderCenter + new Vector2(MathF.Cos(th), MathF.Sin(th)) * Radius;
            }
            return Origin + Unit * u;
        }

        public Vector2 TangentAt(float u)
        {
            if (IsCylinder && Radius > 1e-3f)
            {
                float th = ThetaAt(u);
                return new Vector2(-MathF.Sin(th), MathF.Cos(th)) * ThetaSign;
            }
            return Unit;
        }
    }

    /// <summary>Locked baseline orientation for one contour (survives path reverse).</summary>
    public readonly struct BaselineLock
    {
        public bool IsCylinder { get; init; }
        public Vector2 CylinderCenter { get; init; }
        public float Theta0 { get; init; }
        public float ThetaSign { get; init; }
        public Vector2 Origin { get; init; }
        public Vector2 Unit { get; init; }
        public Vector2 InwardUnit { get; init; }
    }

    /// <summary>Carries previous-layer hairpins so each new hairpin stays ≥60% supported.</summary>
    public sealed class OpenPathDetourState
    {
        /// <summary>Key = contourIndex * 100000 + cell * 2 + diag (0/1).</summary>
        public Dictionary<int, Hairpin> Prev { get; } = new();
        public Dictionary<int, Hairpin> Curr { get; } = new();
        /// <summary>All previous-layer hairpins (for nearest-neighbour catch when keys miss).</summary>
        public List<Hairpin> PrevList { get; } = new();
        /// <summary>
        /// World Z of the first layer that had open single-skin paths (part bottom / bed).
        /// Used so bed-supported births get full depth even when the mesh sits far above Z=0.
        /// </summary>
        public float? FirstOpenPathZ { get; set; }
        /// <summary>Per-contour locked baseline orientation (key = contour index).</summary>
        public Dictionary<int, BaselineLock> BaselineLocks { get; } = new();

        public void AdvanceLayer()
        {
            Prev.Clear();
            PrevList.Clear();
            foreach (var kv in Curr)
            {
                Prev[kv.Key] = kv.Value;
                PrevList.Add(kv.Value);
            }
            Curr.Clear();
        }
    }

    public readonly struct Hairpin
    {
        public float S { get; init; }
        public Vector2 Mouth { get; init; }
        public Vector2 Tip { get; init; }
        public float Depth { get; init; }
    }

    /// <summary>Minimum fraction of each hairpin that must rest on the previous layer.</summary>
    public const float MinSupportFraction = 0.60f;

    /// <summary>
    /// Single-skin / zig-zag path: insert dual-wall hairpin detours into open
    /// contours. Depth grows ≤ MaxStep per layer and each hairpin must keep
    /// ≥ <see cref="MinSupportFraction"/> of its length supported by the prior
    /// layer's hairpin (prevents floating spikes into free air).
    /// </summary>
    /// <param name="isBedLayer">
    /// True for the first slice plane of the part (layer index 0). Absolute world Z is
    /// not used — the mesh often sits well above Z=0 on the print bed.
    /// </param>
    public static int ApplyOpenPathDetours(
        List<List<Vector2>> contours,
        List<bool> closed,
        float z,
        float layerHeight,
        SliceSettings settings,
        OpenPathDetourState? state = null,
        bool isBedLayer = false,
        List<List<Vector2>>? wallSolidRings = null)
    {
        if (!settings.XBracingEnabled || contours.Count == 0) return 0;
        state ??= new OpenPathDetourState();

        float bead = MathF.Max(settings.BeadWidth, 0.1f);
        float wantDepth = MathF.Max(settings.XBracingDepthMm, bead * 3f);
        float span = MathF.Max(settings.XBracingSpanMm, bead * 10f);
        float angleDeg = Math.Clamp(settings.XBracingAngleDeg, 10f, 60f);
        bool extendEdges = settings.XBracingExtendEdges;

        // --- Stable constant-depth X ---
        // At constant depth, tip translates with the mouth: |Δtip| ≈ |Δmouth| ≈ idealDs.
        // Free-edge budget must ALLOW that step (the X lean IS the free-edge lean).
        // Fighting it by cutting depth causes jagged oscillating free edges.
        // Lightning overhang only gates how fast depth grows *into* the wall.
        float lh = MathF.Max(layerHeight, 0.1f);
        float overhangDeg = Math.Clamp(settings.LightningOverhangDeg, 5f, 80f);
        float tanOver = MathF.Tan(overhangDeg * MathF.PI / 180f);

        // Depth growth into the wall (along projection) — overhang-limited.
        float maxStep = MathF.Min(lh * tanOver, 0.5f * bead);
        maxStep = MathF.Max(maxStep, lh * 0.25f);

        // X cell height from user angle. Soft-cap only if step would exceed ~1 bead
        // (physical dual-wall track), not if it exceeds a tight tip clamp.
        float tanAngle = MathF.Max(MathF.Tan(angleDeg * MathF.PI / 180f), 0.15f);
        float cellH = span / tanAngle;
        float maxBeadStep = bead * 0.95f;
        float idealDsRaw = span * lh / MathF.Max(cellH, 1e-3f);
        if (idealDsRaw > maxBeadStep)
            cellH = span * lh / maxBeadStep;
        cellH = MathF.Max(cellH, settings.LayerHeight * 4f);

        float idealDs = span * lh / cellH; // ≈ lh * tan(effective angle)
        // Mouth track: follow ideal diagonal. Catch-up headroom must comfortably
        // exceed idealDs or a leg that lags once (curvature slowdown) can never
        // rejoin its diagonal — the ideal keeps marching at idealDs per layer.
        float maxDs = MathF.Max(idealDs * 1.6f, lh * 0.35f);
        maxDs = MathF.Min(maxDs, bead * 1.25f);

        // Tip free-edge: must be ≥ idealDs so constant-depth X is possible.
        // On CURVED walls growDir follows the local surface normal, so the tip
        // sweeps depth·δθ per layer even when the mouth moves only idealDs —
        // the budget is the physical bead-overlap bound (tip bead still lands on
        // the previous tip bead), not the mouth step.
        float overBudget = MathF.Max(lh * tanOver * 0.85f, lh * 0.12f);
        float maxLateral = MathF.Max(idealDs * 1.12f, overBudget);
        maxLateral = MathF.Max(maxLateral, idealDs + lh * 0.15f);
        maxLateral = MathF.Max(maxLateral, bead * 0.75f);
        maxLateral = MathF.Min(maxLateral, bead * 1.35f);

        // Support radius covers the designed mouth/tip step.
        float supportR = MathF.Max(bead * 1.05f, maxLateral * 1.2f);

        // Detect open single-skin paths on this layer (candidates for hairpins).
        bool anyOpenCandidate = false;
        for (int ci = 0; ci < contours.Count; ci++)
        {
            if (ci < closed.Count && closed[ci]) continue;
            if (contours[ci].Count >= 2 && PolyLengthOpen(contours[ci]) >= bead * 8f)
            {
                anyOpenCandidate = true;
                break;
            }
        }
        // Record first open-path Z (part bottom). Do NOT use absolute Z≈0 — imports
        // sit on the bed at whatever world Z the cell uses (often hundreds of mm).
        if (anyOpenCandidate && state.FirstOpenPathZ is null)
            state.FirstOpenPathZ = z;

        // Bed-supported layer:
        //  1) slicer says this is layer index 0, or
        //  2) same Z as the first open-path layer (handles empty bottom planes).
        float bedBand = MathF.Max(lh, MathF.Max(settings.FirstLayerHeight, settings.LayerHeight)) * 0.75f + 0.5f;
        bool onPrintBed = isBedLayer
            || (state.FirstOpenPathZ is float bedZ && MathF.Abs(z - bedZ) <= bedBand);
        // Birth on free air: short stub. Birth on the bed: full depth (bed supports the whole pin).
        float birthDepth = onPrintBed ? wantDepth : MathF.Min(wantDepth, maxStep);

        // Linear phase in each X cell: two diagonals CROSS to form a true X.
        //   cellT=0: A at left, B at right
        //   cellT=0.5: meet at mid (one dual pin, both keys)
        //   cellT=1: A at right, B at left  (legs continued past the meet — not bounce)
        // Bounce (triangle) only completed a diamond over full cellH≈span/tan(θ);
        // on shorter parts that looked like an upside-down Y (approach only).
        float cellT = ((z % cellH) + cellH) % cellH / cellH;
        float meetMergeS = MathF.Max(bead * 2.0f, maxDs * 1.5f);

        int hairpins = 0;
        for (int ci = 0; ci < contours.Count; ci++)
        {
            if (ci < closed.Count && closed[ci]) continue;
            var path = contours[ci];
            if (path.Count < 2) continue;

            float totalLen = PolyLengthOpen(path);
            if (totalLen < bead * 8f) continue;

            // World-stable baseline (locked across zig-zag reverse).
            if (!TryBuildBaseline(path, settings, state, ci, out var baseline)
                || baseline.Length < bead * 8f)
                continue;

            // Closed wall ring for this contour (pre single-skin extract) — clamp depth.
            List<Vector2>? wallRing = null;
            if (wallSolidRings is not null && ci < wallSolidRings.Count
                && wallSolidRings[ci].Count >= 3)
                wallRing = wallSolidRings[ci];

            // How deep the wall actually is along a mid-face inward probe.
            float solidCap = wantDepth;
            if (wallRing is not null)
            {
                float measured = MeasureWallThickness(path, totalLen, wallRing, bead);
                if (measured > bead * 0.5f)
                    solidCap = MathF.Min(wantDepth, MathF.Max(bead * 1.25f, measured - bead * 0.35f));
            }
            float contourWant = solidCap;
            float contourBirth = onPrintBed ? contourWant : MathF.Min(contourWant, maxStep);

            // World-anchored cell grid: cells sit at absolute multiples of span in the
            // LOCKED frame. On scalloped/slanted panels each layer covers a moving
            // window [UMin, UMax] of that grid — diagonals stay world-straight and
            // ribs enter/exit through the edges instead of sliding along them.
            float covMin = baseline.UMin;
            float covMax = baseline.UMax;
            float edgeTol = bead * 1.0f;
            int cStart = (int)MathF.Floor(covMin / span);
            int cEnd = (int)MathF.Ceiling(covMax / span);
            bool InCoverage(float uu) => uu >= covMin - edgeTol && uu <= covMax + edgeTol;

            // Sites: IdealMouth on straight baseline; PathMouth for perimeter stitch only.
            var sites = new List<(float U, Vector2 IdealMouth, Vector2 PathMouth, Vector2 Tip, int KeyA, int KeyB)>();
            for (int c = cStart; c < cEnd; c++)
            {
                float local0 = c * span;
                if (!extendEdges
                    && (local0 < covMin - 0.01f || local0 + span > covMax + 0.01f))
                    continue;

                // Full-span diagonals on the WORLD cell (cross-through X). Never
                // shrink the cell to this layer's coverage — that bends the diagonal.
                float uA = local0 + cellT * span;
                float uB = local0 + span - cellT * span;
                int keyA = ci * 100_000 + c * 2 + 0;
                int keyB = ci * 100_000 + c * 2 + 1;
                float uSep = MathF.Abs(uA - uB);

                if (uSep <= meetMergeS)
                {
                    float uMid = 0.5f * (uA + uB);
                    if (!InCoverage(uMid)) continue; // rib exits through the edge
                    float meetBirth = MathF.Max(contourBirth, contourWant);
                    if (PlaceSupportedHairpin(
                            path, totalLen, baseline, keyA, keyB,
                            uMid, contourWant, bead, maxStep, maxDs, maxLateral,
                            meetBirth, supportR, settings, state,
                            out var site, wallRing))
                        sites.Add((site.U, site.IdealMouth, site.PathMouth, site.Tip, keyA, keyB));
                }
                else
                {
                    if (InCoverage(uA) && PlaceSupportedHairpin(
                            path, totalLen, baseline, keyA, keyA,
                            uA, contourWant, bead, maxStep, maxDs, maxLateral,
                            contourBirth, supportR, settings, state,
                            out var siteA, wallRing))
                        sites.Add((siteA.U, siteA.IdealMouth, siteA.PathMouth, siteA.Tip, keyA, keyA));

                    if (InCoverage(uB) && PlaceSupportedHairpin(
                            path, totalLen, baseline, keyB, keyB,
                            uB, contourWant, bead, maxStep, maxDs, maxLateral,
                            contourBirth, supportR, settings, state,
                            out var siteB, wallRing))
                        sites.Add((siteB.U, siteB.IdealMouth, siteB.PathMouth, siteB.Tip, keyB, keyB));
                }
            }
            if (sites.Count == 0) continue;

            // Resolve near-duplicate baseline U: keep deeper, re-register keys.
            // Always keep IdealMouth (never path stitch) so stack aim stays on the baseline.
            // EXCEPT diverging twins: two legs sharing last layer's pin (cell-boundary
            // "∨" vertex or post-cross split) that now straddle it are SUPPOSED to
            // separate — re-merging them every layer pins the whole X lattice to the
            // cell boundaries (legs can only escape 2·maxDs per layer, less than the
            // merge window, so the merge erased their progress forever).
            sites.Sort((a, b) => a.U.CompareTo(b.U));
            for (int i = sites.Count - 1; i > 0; i--)
            {
                if (MathF.Abs(sites[i].U - sites[i - 1].U) >= bead * 1.5f) continue;

                var a = sites[i - 1];
                var b = sites[i];

                if (state.Prev.TryGetValue(a.KeyA, out var pa)
                    && state.Prev.TryGetValue(b.KeyA, out var pb)
                    && MathF.Abs(pa.S - pb.S) < 1e-3f
                    && (a.U - pa.S) * (b.U - pb.S) < -1e-6f)
                    continue; // diverging twins — let them split
                float dA = Vector2.Distance(a.IdealMouth, a.Tip);
                float dB = Vector2.Distance(b.IdealMouth, b.Tip);
                var keep = dA >= dB ? a : b;
                float uM = 0.5f * (a.U + b.U);
                float depthKeep = Vector2.Distance(keep.IdealMouth, keep.Tip);
                var hair = new Hairpin
                {
                    S = uM, Mouth = keep.IdealMouth, Tip = keep.Tip, Depth = depthKeep,
                };
                foreach (int k in new[] { a.KeyA, a.KeyB, b.KeyA, b.KeyB })
                    state.Curr[k] = hair;
                sites[i - 1] = (uM, keep.IdealMouth, keep.PathMouth, keep.Tip,
                    a.KeyA, b.KeyB != a.KeyA ? b.KeyB : a.KeyB);
                sites.RemoveAt(i);
            }

            var emitSites = sites
                .Select(t => (t.U, t.PathMouth, t.Tip, baseline.TangentAt(t.U)))
                .ToList();
            contours[ci] = InsertHairpins(path, totalLen, emitSites, bead);
            hairpins += emitSites.Count;
        }

        state.AdvanceLayer();

        if (hairpins > 0 && (onPrintBed
                || (int)(z / 15f) != (int)((z - 0.01f) / 15f)
                || z < (state.FirstOpenPathZ ?? 0f) + settings.LayerHeight * 2f))
        {
            // After AdvanceLayer, pins live in Prev/PrevList.
            float maxPin = 0f, minPin = float.MaxValue;
            foreach (var h in state.PrevList)
            {
                maxPin = MathF.Max(maxPin, h.Depth);
                minPin = MathF.Min(minPin, h.Depth);
            }
            if (state.PrevList.Count == 0) minPin = 0f;
            System.Console.WriteLine(
                $"[x-bracing] single-skin detours z={z:0.#} hairpins={hairpins} " +
                $"want={wantDepth:0.#} pinDepth=[{minPin:0.#}..{maxPin:0.#}] " +
                $"birth={birthDepth:0.#} bed={onPrintBed} maxStep={maxStep:0.##} " +
                $"maxDs={maxDs:0.##} cellH={cellH:0.#}");
        }
        return hairpins;
    }

    /// <param name="keyPrimary">Stack key for this pin (and support parent lookup).</param>
    /// <param name="keySecondary">Second key to register (same as primary normally;
    /// both diagonal keys at an X-cross so A and B stacks continue through).</param>
    /// <param name="uIdeal">Baseline parameter (straight), not path arc length.</param>
    private static bool PlaceSupportedHairpin(
        List<Vector2> path, float totalLen,
        StraightBaseline baseline,
        int keyPrimary, int keySecondary,
        float uIdeal, float wantDepth, float bead, float maxStep, float maxDs,
        float maxLateral, float birthDepth, float supportR, SliceSettings settings,
        OpenPathDetourState state,
        out (float U, Vector2 IdealMouth, Vector2 PathMouth, Vector2 Tip) site,
        List<Vector2>? wallRing = null)
    {
        site = default;
        float uLo = baseline.UMin;
        float uHi = MathF.Max(baseline.UMax, baseline.UMin);
        uIdeal = Math.Clamp(uIdeal, uLo, uHi);

        // Same-key previous only — do NOT steal the other X leg's parent.
        Hairpin prev = default;
        bool hasPrev = false;
        if (state.Prev.TryGetValue(keyPrimary, out var keyed) && keyed.Depth > 1e-4f)
        {
            prev = keyed;
            hasPrev = true;
        }
        else if (keySecondary != keyPrimary
                 && state.Prev.TryGetValue(keySecondary, out var keyedB) && keyedB.Depth > 1e-4f)
        {
            prev = keyedB;
            hasPrev = true;
        }
        if (keySecondary != keyPrimary
            && state.Prev.TryGetValue(keyPrimary, out var pA) && pA.Depth > 1e-4f
            && state.Prev.TryGetValue(keySecondary, out var pB) && pB.Depth > 1e-4f)
        {
            prev = pA.Depth >= pB.Depth ? pA : pB;
            hasPrev = true;
        }

        // Track ideal U on the straight baseline (Hairpin.S stores U).
        float u = uIdeal;
        if (hasPrev)
        {
            float du = uIdeal - prev.S;
            if (MathF.Abs(du) > maxDs)
                u = prev.S + MathF.Sign(du) * maxDs;
            u = Math.Clamp(u, uLo, uHi);
        }

        // Mouth = real path point nearest baseline U; tip = mouth + grow × depth.
        // Depth is always |tip−mouth| (never a tip planned off-path that draws long rays).
        Vector2 pathMouth = default, growDir = default, tip = default;

        float minDepth = MathF.Max(bead * 0.35f, maxStep * 0.35f);
        float depth;
        if (hasPrev)
        {
            // Monotonic grow to wantDepth, then hold constant.
            if (prev.Depth >= wantDepth * 0.9f)
                depth = wantDepth;
            else
                depth = MathF.Min(wantDepth, MathF.Max(prev.Depth, prev.Depth + maxStep));
            depth = Math.Clamp(depth, minDepth, wantDepth);
        }
        else
        {
            depth = Math.Clamp(MathF.Max(birthDepth, minDepth), minDepth, wantDepth);
        }

        void PlaceTip()
        {
            if (!TryPathMouthAndGrow(path, totalLen, baseline, u, settings, hasPrev ? prev : null,
                    out pathMouth, out growDir, wallRing))
            {
                growDir = Vector2.Zero;
                return;
            }
            // Fit depth so tip stays inside the wall solid (no shoot-through spikes).
            float dCap = wantDepth;
            if (wallRing is { Count: >= 3 })
            {
                dCap = FitDepthInsideWall(pathMouth, growDir, wallRing, wantDepth, bead);
                if (dCap < bead * 0.75f)
                {
                    // Grow never entered solid — face normal fallback.
                    var faceIn = FaceInwardNormal(path, totalLen,
                        NearestArcS(path, totalLen, pathMouth));
                    if (faceIn.LengthSquared() > 0.5f)
                    {
                        float faceCap = FitDepthInsideWall(pathMouth, faceIn, wallRing, wantDepth, bead);
                        if (faceCap >= bead * 0.75f)
                        {
                            growDir = faceIn;
                            dCap = faceCap;
                        }
                    }
                }
                if (dCap < bead * 0.5f)
                    dCap = wantDepth; // no solid ring usable — keep planned depth
            }
            depth = Math.Clamp(MathF.Min(depth, dCap), minDepth, wantDepth);
            tip = pathMouth + growDir * depth;
        }

        PlaceTip();
        if (growDir.LengthSquared() < 0.5f)
            return false;

        if (hasPrev)
        {
            // Free-edge lateral: lag U only — keep depth (no thrash, no stretch).
            if (Vector2.Distance(tip, prev.Tip) > maxLateral + 1e-3f)
            {
                float sLo = prev.S;
                float sHi = u;
                float uMinProg = prev.S + MathF.Sign(uIdeal - prev.S)
                    * MathF.Min(maxDs * 0.5f, MathF.Abs(uIdeal - prev.S) * 0.5f);
                for (int i = 0; i < 12; i++)
                {
                    u = Math.Clamp(0.5f * (sLo + sHi), uLo, uHi);
                    PlaceTip();
                    if (Vector2.Distance(tip, prev.Tip) > maxLateral)
                        sHi = u;
                    else
                        sLo = u;
                }
                u = Math.Clamp(sLo, uLo, uHi);
                if (MathF.Abs(u - prev.S) < MathF.Abs(uMinProg - prev.S) - 1e-4f)
                    u = Math.Clamp(uMinProg, uLo, uHi);
                // Hold parent depth if still overshooting after U lag.
                depth = Math.Clamp(MathF.Max(depth, prev.Depth), minDepth, wantDepth);
                PlaceTip();
            }

            int guard = 0;
            while (SupportFraction(pathMouth, tip, prev.Mouth, prev.Tip, supportR) < MinSupportFraction
                   && guard++ < 12)
            {
                float uNext = u * 0.7f + prev.S * 0.3f;
                float du = uNext - prev.S;
                if (MathF.Abs(du) > maxDs)
                    uNext = prev.S + MathF.Sign(du) * maxDs;
                uNext = Math.Clamp(uNext, uLo, uHi);
                if (MathF.Abs(uNext - u) < 1e-4f) break;
                u = uNext;
                depth = Math.Clamp(MathF.Max(prev.Depth, MathF.Min(depth, wantDepth)), minDepth, wantDepth);
                PlaceTip();
            }

            if (SupportFraction(pathMouth, tip, prev.Mouth, prev.Tip, supportR) < MinSupportFraction * 0.85f)
            {
                // Hold at the parent, never die: a rib that skips a layer leaves a
                // gap and is reborn as a stub that takes ~wantDepth/maxStep layers
                // to regrow — reference ribs are continuous. A pin held at prev's
                // position/depth is supported by construction.
                u = prev.S;
                depth = Math.Clamp(MathF.Max(prev.Depth, MathF.Min(wantDepth, prev.Depth + maxStep)),
                    minDepth, wantDepth);
                PlaceTip();
                if (SupportFraction(pathMouth, tip, prev.Mouth, prev.Tip, supportR) < MinSupportFraction * 0.75f)
                {
                    depth = Math.Clamp(prev.Depth, minDepth, wantDepth);
                    PlaceTip();
                }
            }
        }

        // Final enforce: tip exactly mouth + grow × depth (never longer than wantDepth).
        depth = Math.Clamp(depth, minDepth, wantDepth);
        tip = pathMouth + growDir * depth;
        float drawn = Vector2.Distance(pathMouth, tip);
        if (drawn < minDepth * 0.9f)
        {
            if (!hasPrev) return false;
            // Degenerate placement — carry the parent pin forward verbatim (rib
            // continuity beats one layer of march).
            u = prev.S;
            pathMouth = prev.Mouth;
            tip = prev.Tip;
            depth = prev.Depth;
        }

        var hair = new Hairpin
        {
            S = u, Mouth = pathMouth, Tip = tip, Depth = depth,
        };
        state.Curr[keyPrimary] = hair;
        if (keySecondary != keyPrimary)
            state.Curr[keySecondary] = hair;

        // IdealMouth == PathMouth (same point); Tip is pathMouth + grow×depth.
        site = (u, pathMouth, pathMouth, tip);
        return true;
    }

    /// <summary>
    /// Real perimeter mouth at baseline U + world-stable into-wall grow unit.
    /// Always snaps to the open path so drawn hairpin length == depth.
    /// </summary>
    private static bool TryPathMouthAndGrow(
        List<Vector2> path, float totalLen, StraightBaseline baseline, float u,
        SliceSettings settings, Hairpin? prev,
        out Vector2 mouth, out Vector2 growDir,
        List<Vector2>? wallRing = null)
    {
        mouth = default;
        growDir = default;
        u = Math.Clamp(u, baseline.UMin, MathF.Max(baseline.UMax, baseline.UMin));

        // Baseline U only chooses where along the wall; mouth is always on the path.
        // Both mappings INVERT the world coordinate along the path (first crossing
        // walking a canonical direction): the rib mouth lands where the wall actually
        // reaches the target angle/offset, so diagonals are world-straight and never
        // fold under crests/troughs of a wavy wall (nearest-point projection folded —
        // the mouth had to teleport across the bump, the support clamps vetoed it,
        // and the march deadlocked; proportional arc length wandered with the waves).
        float pathS = baseline.IsCylinder && baseline.Radius > 1e-3f
            ? ArcSAtAngle(path, totalLen, baseline.CylinderCenter,
                baseline.Theta0, baseline.ThetaAt(u) - baseline.Theta0)
            : ArcSAtCoord(path, totalLen, baseline.Origin, baseline.Unit, u);
        mouth = PointAtArcOpen(path, totalLen, pathS);

        // Path left-normal (open face is oriented into the wall by ExtractSingleSkinOpenFaces).
        var faceIn = FaceInwardNormal(path, totalLen, pathS);

        var projected = BraceDirAt(settings, mouth);
        if (projected.LengthSquared() > 0.5f)
            growDir = projected;
        else if (faceIn.LengthSquared() > 0.5f)
            growDir = faceIn;
        else if (baseline.InwardUnit.LengthSquared() > 0.5f)
            growDir = baseline.InwardUnit;
        else
            return false;

        // If we have a wall solid, force grow into the solid. Prefer projection that
        // still has a component into the face (cylinder aim), else fall back to face normal.
        if (wallRing is { Count: >= 3 })
        {
            float enter = RaycastEnterDepth(mouth, growDir, wallRing, settings.BeadWidth * 20f);
            float enterNeg = RaycastEnterDepth(mouth, -growDir, wallRing, settings.BeadWidth * 20f);
            if (enter < settings.BeadWidth * 0.75f && enterNeg >= settings.BeadWidth * 0.75f)
                growDir = -growDir;
            else if (enter < settings.BeadWidth * 0.75f && faceIn.LengthSquared() > 0.5f)
            {
                // Cylinder dir never enters solid — use face inward (still printable X lean via U).
                float faceEnter = RaycastEnterDepth(mouth, faceIn, wallRing, settings.BeadWidth * 20f);
                if (faceEnter >= settings.BeadWidth * 0.75f)
                    growDir = faceIn;
            }
        }

        // Match previous pin hemisphere (no layer-to-layer in/out flip).
        if (prev is Hairpin p && p.Depth > 1e-4f)
        {
            var prevGrow = p.Tip - p.Mouth;
            if (prevGrow.LengthSquared() > 1e-6f && Vector2.Dot(growDir, prevGrow) < 0f)
                growDir = -growDir;
        }

        return growDir.LengthSquared() > 0.5f;
    }

    /// <summary>
    /// Arc length where the path's world coordinate along <paramref name="unit"/>
    /// first crosses <paramref name="uTarget"/>. The walk always runs in +unit
    /// direction (path order canonicalized) so zig-zag reversal picks the same
    /// crossing every layer. Falls back to the vertex with the nearest coordinate.
    /// </summary>
    private static float ArcSAtCoord(
        List<Vector2> path, float totalLen, Vector2 origin, Vector2 unit, float uTarget)
    {
        int n = path.Count;
        if (n < 2 || totalLen < 1e-6f) return 0f;
        bool rev = Vector2.Dot(path[^1] - path[0], unit) < 0f;

        float acc = 0f;
        float bestS = 0f, bestErr = float.MaxValue;
        int i0 = rev ? n - 1 : 0;
        int step = rev ? -1 : 1;
        float cPrev = Vector2.Dot(path[i0] - origin, unit);
        var pPrev = path[i0];
        for (int k = 1; k < n; k++)
        {
            int i = i0 + step * k;
            var p = path[i];
            float c = Vector2.Dot(p - origin, unit);
            float seg = Vector2.Distance(pPrev, p);
            if (seg > 1e-8f)
            {
                if ((cPrev - uTarget) * (c - uTarget) <= 0f && MathF.Abs(c - cPrev) > 1e-8f)
                {
                    float t = Math.Clamp((uTarget - cPrev) / (c - cPrev), 0f, 1f);
                    float sWalk = acc + seg * t;
                    return rev ? Math.Clamp(totalLen - sWalk, 0f, totalLen)
                               : Math.Clamp(sWalk, 0f, totalLen);
                }
                float err = MathF.Abs(c - uTarget);
                if (err < bestErr)
                {
                    bestErr = err;
                    float sWalk = acc + seg;
                    bestS = rev ? totalLen - sWalk : sWalk;
                }
                acc += seg;
            }
            cPrev = c;
            pPrev = p;
        }
        // No crossing (target past an end) — nearest coordinate.
        float e0 = MathF.Abs(Vector2.Dot(path[i0] - origin, unit) - uTarget);
        if (e0 < bestErr) bestS = rev ? totalLen : 0f;
        return Math.Clamp(bestS, 0f, totalLen);
    }

    /// <summary>
    /// Arc length where the path's unwrapped CCW angle from <paramref name="th0"/>
    /// first crosses <paramref name="dTarget"/> (radians, ≥ 0). Walk direction is
    /// canonicalized to increasing angle so zig-zag reversal is invisible.
    /// </summary>
    private static float ArcSAtAngle(
        List<Vector2> path, float totalLen, Vector2 center, float th0, float dTarget)
    {
        int n = path.Count;
        if (n < 2 || totalLen < 1e-6f) return 0f;

        static float Wrap(float a)
        {
            while (a > MathF.PI) a -= MathF.PI * 2f;
            while (a <= -MathF.PI) a += MathF.PI * 2f;
            return a;
        }

        // Unwrapped delta-from-th0 at every vertex, walking the path as stored.
        var deltas = new float[n];
        var thPrev = MathF.Atan2(path[0].Y - center.Y, path[0].X - center.X);
        deltas[0] = Wrap(thPrev - th0); // coverage may start CW of th0 (negative delta)
        for (int i = 1; i < n; i++)
        {
            var d = path[i] - center;
            if (d.LengthSquared() < 1e-10f) { deltas[i] = deltas[i - 1]; continue; }
            float th = MathF.Atan2(d.Y, d.X);
            deltas[i] = deltas[i - 1] + Wrap(th - thPrev);
            thPrev = th;
        }

        // Canonical direction: increasing angle.
        bool rev = deltas[^1] < deltas[0];

        float acc = 0f;
        float bestS = 0f, bestErr = float.MaxValue;
        int i0 = rev ? n - 1 : 0;
        int step = rev ? -1 : 1;
        float dPrev = deltas[i0];
        var pPrev = path[i0];
        for (int k = 1; k < n; k++)
        {
            int i = i0 + step * k;
            var p = path[i];
            float dc = deltas[i];
            float seg = Vector2.Distance(pPrev, p);
            if (seg > 1e-8f)
            {
                if ((dPrev - dTarget) * (dc - dTarget) <= 0f && MathF.Abs(dc - dPrev) > 1e-9f)
                {
                    float t = Math.Clamp((dTarget - dPrev) / (dc - dPrev), 0f, 1f);
                    float sWalk = acc + seg * t;
                    return rev ? Math.Clamp(totalLen - sWalk, 0f, totalLen)
                               : Math.Clamp(sWalk, 0f, totalLen);
                }
                float err = MathF.Abs(dc - dTarget);
                if (err < bestErr)
                {
                    bestErr = err;
                    float sWalk = acc + seg;
                    bestS = rev ? totalLen - sWalk : sWalk;
                }
                acc += seg;
            }
            dPrev = dc;
            pPrev = p;
        }
        float e0 = MathF.Abs(deltas[i0] - dTarget);
        if (e0 < bestErr) bestS = rev ? totalLen : 0f;
        return Math.Clamp(bestS, 0f, totalLen);
    }

    /// <summary>Left-of-travel unit normal on an open path (into wall after face orient).</summary>
    private static Vector2 FaceInwardNormal(List<Vector2> path, float totalLen, float s)
    {
        float ds = MathF.Max(totalLen * 0.002f, 0.5f);
        var p0 = PointAtArcOpen(path, totalLen, MathF.Max(0f, s - ds));
        var p1 = PointAtArcOpen(path, totalLen, MathF.Min(totalLen, s + ds));
        var tan = p1 - p0;
        float tl = tan.Length();
        if (tl < 1e-6f) return Vector2.Zero;
        tan /= tl;
        return new Vector2(-tan.Y, tan.X);
    }

    /// <summary>
    /// Walk along <paramref name="grow"/> from mouth (on boundary) until we leave the
    /// wall polygon. Returns how far we stayed inside (0 if never entered).
    /// </summary>
    private static float RaycastEnterDepth(
        Vector2 mouth, Vector2 grow, List<Vector2> wallRing, float maxScan)
    {
        if (grow.LengthSquared() < 0.5f) return 0f;
        float step = MathF.Max(0.25f, maxScan / 80f);
        float lastIn = 0f;
        bool seenIn = false;
        for (float d = step; d <= maxScan; d += step)
        {
            if (PointInPolygon(mouth + grow * d, wallRing))
            {
                seenIn = true;
                lastIn = d;
            }
            else if (seenIn)
            {
                // Left solid after entering — far face.
                return lastIn;
            }
        }
        return seenIn ? lastIn : 0f;
    }

    /// <summary>
    /// Max depth along <paramref name="grow"/> from <paramref name="mouth"/> that stays
    /// inside the wall polygon. Mouth is typically on the boundary, so we raycast in.
    /// Returns 0 if grow never enters the solid (caller should flip/fallback).
    /// </summary>
    private static float FitDepthInsideWall(
        Vector2 mouth, Vector2 grow, List<Vector2> wallRing, float want, float bead)
    {
        float solid = RaycastEnterDepth(mouth, grow, wallRing, MathF.Max(want * 1.5f, bead * 40f));
        if (solid < bead * 0.5f)
            return 0f; // did not enter — not a usable grow direction
        // Leave a small margin before the far face so the free edge stays in material.
        float usable = MathF.Max(bead * 0.75f, solid - bead * 0.35f);
        return MathF.Min(want, usable);
    }

    /// <summary>Median inward thickness of the open face into the closed wall ring.</summary>
    private static float MeasureWallThickness(
        List<Vector2> openPath, float totalLen, List<Vector2> wallRing, float bead)
    {
        float[] samples = new float[5];
        int n = 0;
        for (int i = 1; i <= 5; i++)
        {
            float s = totalLen * (i / 6f);
            var mouth = PointAtArcOpen(openPath, totalLen, s);
            var left = FaceInwardNormal(openPath, totalLen, s);
            if (left.LengthSquared() < 0.5f) continue;
            float dPos = RaycastEnterDepth(mouth, left, wallRing, bead * 40f);
            float dNeg = RaycastEnterDepth(mouth, -left, wallRing, bead * 40f);
            float d = MathF.Max(dPos, dNeg);
            if (d > bead * 0.4f)
                samples[n++] = d;
        }
        if (n == 0) return 0f;
        Array.Sort(samples, 0, n);
        return samples[n / 2];
    }

    private static bool PointInPolygon(Vector2 p, List<Vector2> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var pi = poly[i];
            var pj = poly[j];
            if (((pi.Y > p.Y) != (pj.Y > p.Y))
                && (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y + 1e-20f) + pi.X))
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Fraction of points along current hairpin within <paramref name="supportR"/>
    /// of the previous hairpin segment. Mouth region (first ~bead) also counts as
    /// wall-supported so short birth stubs score well.
    /// </summary>
    public static float SupportFraction(
        Vector2 mouth, Vector2 tip, Vector2 prevMouth, Vector2 prevTip, float supportR)
    {
        const int samples = 20;
        float r2 = supportR * supportR;
        float len = Vector2.Distance(mouth, tip);
        // Fraction of length treated as wall-supported near the mouth (one bead-ish).
        float wallT = len > 1e-5f ? Math.Clamp(supportR * 0.35f / len, 0.05f, 0.25f) : 0.15f;
        int hits = 0;
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            var p = mouth + (tip - mouth) * t;
            if (t <= wallT) { hits++; continue; }
            if (DistToSegmentSq(p, prevMouth, prevTip) <= r2)
                hits++;
        }
        return hits / (float)(samples + 1);
    }

    private static float DistToSegmentSq(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float ab2 = ab.LengthSquared();
        if (ab2 < 1e-12f) return Vector2.DistanceSquared(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / ab2, 0f, 1f);
        var q = a + ab * t;
        return Vector2.DistanceSquared(p, q);
    }

    /// <summary>
    /// Dual-wall hairpin: path → tip at fixed depth → offset tip by bead along baseline
    /// → back to path. Tip is always re-based from the on-path mouth so drawn length
    /// equals planned depth (never a long ray to an off-path tip).
    /// </summary>
    private static List<Vector2> InsertHairpins(
        List<Vector2> path, float totalLen,
        List<(float U, Vector2 PathMouth, Vector2 Tip, Vector2 Along)> sites, float bead)
    {
        if (path.Count < 2 || sites.Count == 0)
            return new List<Vector2>(path);

        float beadOff = MathF.Max(bead, 0.1f);

        var sorted = sites
            .Select(t =>
            {
                float s = NearestArcS(path, totalLen, t.PathMouth);
                float plannedDepth = Vector2.Distance(t.PathMouth, t.Tip);
                var grow = plannedDepth > 1e-6f
                    ? (t.Tip - t.PathMouth) / plannedDepth
                    : Vector2.Zero;
                return (S: s, t.U, Depth: plannedDepth, Grow: grow, t.Along);
            })
            .OrderBy(t => t.S)
            .ToList();

        var dst = new List<Vector2>(path.Count + sorted.Count * 4);
        float sCursor = 0f;

        void AddPt(Vector2 p)
        {
            if (dst.Count == 0 || Vector2.DistanceSquared(dst[^1], p) > 1e-8f)
                dst.Add(p);
        }

        AddPt(path[0]);

        for (int si = 0; si < sorted.Count; si++)
        {
            var (s, _, depth, grow, along) = sorted[si];
            s = Math.Clamp(s, 0f, totalLen);

            if (s > sCursor + 1e-4f)
                AppendOpenPathRange(path, totalLen, sCursor, s, dst);

            var mouthIn = PointAtArcOpen(path, totalLen, s);
            if (depth < 1e-4f || grow.LengthSquared() < 0.5f)
            {
                sCursor = s;
                continue;
            }

            // Re-base tip from the actual path entry — drawn length == planned depth.
            var tipIn = mouthIn + grow * depth;

            var alongU = along;
            if (alongU.LengthSquared() < 1e-8f)
                alongU = TangentAtArcOpen(path, totalLen, s);
            if (alongU.LengthSquared() > 1e-8f)
                alongU = Vector2.Normalize(alongU);
            else
                alongU = Vector2.UnitX;

            // Dual-wall: second tip offset along baseline; same depth back to path.
            var tipOut = tipIn + alongU * beadOff;
            var mouthOutIdeal = tipOut - grow * depth;

            float sOut = NearestArcS(path, totalLen, mouthOutIdeal);
            if (sOut < s + beadOff * 0.25f)
                sOut = Math.Clamp(s + beadOff, 0f, totalLen);
            if (si + 1 < sorted.Count)
                sOut = MathF.Min(sOut, MathF.Max(s + beadOff * 0.35f, sorted[si + 1].S - beadOff * 0.25f));
            sOut = Math.Clamp(sOut, 0f, totalLen);

            if (sOut <= s + 1e-3f)
            {
                var mouthOutTan = mouthIn + alongU * beadOff;
                var tipOutTan = mouthOutTan + grow * depth;
                AddPt(mouthIn);
                AddPt(tipIn);
                AddPt(tipOutTan);
                AddPt(mouthOutTan);
                sCursor = s;
                continue;
            }

            var mouthOut = PointAtArcOpen(path, totalLen, sOut);
            // Keep constant depth on the return leg from the actual exit mouth.
            var tipOutOnPath = mouthOut + grow * depth;

            AddPt(mouthIn);
            AddPt(tipIn);
            AddPt(tipOutOnPath);
            AddPt(mouthOut);

            sCursor = sOut;
        }

        if (sCursor < totalLen - 1e-4f)
            AppendOpenPathRange(path, totalLen, sCursor, totalLen, dst);

        return dst;
    }

    /// <summary>
    /// Build world-stable baseline. First layer locks orientation into
    /// <paramref name="state"/>; later layers reuse that lock so zig-zag reverse
    /// cannot flip in/out or mirror U.
    /// </summary>
    private static bool TryBuildBaseline(
        List<Vector2> path, SliceSettings settings, OpenPathDetourState state, int contourIndex,
        out StraightBaseline baseline)
    {
        baseline = default;
        if (path.Count < 2) return false;

        bool useCylinder = string.Equals(
            settings.XBracingProjectionType, "Cylinder", StringComparison.OrdinalIgnoreCase);
        var c = new Vector2(settings.XBracingCylinderX, settings.XBracingCylinderY);

        // Average radius from path samples.
        float rSum = 0f;
        int rN = 0;
        for (int i = 0; i < path.Count; i++)
        {
            float r = (path[i] - c).Length();
            if (r < 1e-4f) continue;
            rSum += r;
            rN++;
        }
        float Ravg = rN > 0 ? rSum / rN : 0f;

        if (useCylinder && Ravg > 1e-2f)
        {
            // Angle span of path (order-independent): min→max unwrapped along path.
            float thStart = MathF.Atan2(path[0].Y - c.Y, path[0].X - c.X);
            float dthPath = AccumulateAngleSpan(path, c, thStart);
            float thEnd = thStart + dthPath;

            // World-stable U: always increase CCW (ThetaSign = +1) from the lower angle
            // endpoint of the span (so path reverse does not flip U).
            float thLo = dthPath >= 0f ? thStart : thEnd;
            float spanAbs = MathF.Abs(dthPath);
            float baseLen = spanAbs * Ravg;
            if (baseLen < 1e-2f) return false;

            // Into-wall: projection at mid-arc; lock first time.
            float thMid = thLo + 0.5f * spanAbs;
            var midMouth = c + new Vector2(MathF.Cos(thMid), MathF.Sin(thMid)) * Ravg;
            var inward = BraceDirAt(settings, midMouth);
            if (inward.LengthSquared() < 0.5f)
                inward = -new Vector2(MathF.Cos(thMid), MathF.Sin(thMid)); // toward axis

            if (!state.BaselineLocks.TryGetValue(contourIndex, out var lockData))
            {
                lockData = new BaselineLock
                {
                    IsCylinder = true,
                    CylinderCenter = c,
                    Theta0 = thLo,
                    ThetaSign = 1f,
                    Origin = c + new Vector2(MathF.Cos(thLo), MathF.Sin(thLo)) * Ravg,
                    Unit = new Vector2(-MathF.Sin(thLo), MathF.Cos(thLo)),
                    InwardUnit = inward,
                };
                state.BaselineLocks[contourIndex] = lockData;
            }
            else
            {
                // Keep locked Theta0/Sign/Inward; refresh center if user moved gizmo.
                inward = lockData.InwardUnit.LengthSquared() > 0.5f ? lockData.InwardUnit : inward;
                thLo = lockData.Theta0;
            }

            // Coverage window in the LOCKED frame (both ends — scalloped panels
            // uncover/cover grid cells as Z changes).
            MeasureAngleRangeFrom(path, c, thLo, out float dMin, out float dMax);
            float covLo = dMin * Ravg;
            float covHi = dMax * Ravg;
            if (covHi - covLo < 1e-2f) return false;

            baseline = new StraightBaseline
            {
                Origin = c + new Vector2(MathF.Cos(thLo), MathF.Sin(thLo)) * Ravg,
                Unit = new Vector2(-MathF.Sin(thLo), MathF.Cos(thLo)),
                Length = covHi - covLo,
                IsCylinder = true,
                CylinderCenter = c,
                Radius = Ravg,
                Theta0 = thLo,
                ThetaSign = 1f,
                InwardUnit = inward,
                UMin = covLo,
                UMax = covHi,
            };
            return true;
        }

        // Planar / fallback: axis-aligned extent of path projected on a stable unit.
        // Prefer locked unit; else principal direction from first→last by max extent.
        Vector2 unit;
        Vector2 origin;
        Vector2 inwardP;
        if (state.BaselineLocks.TryGetValue(contourIndex, out var pLock) && !pLock.IsCylinder)
        {
            unit = pLock.Unit;
            origin = pLock.Origin;
            inwardP = pLock.InwardUnit;
        }
        else
        {
            // Stable unit: from the endpoint with smaller X (then Y) to the other —
            // independent of path travel order.
            var e0 = path[0];
            var e1 = path[^1];
            bool swap = e1.X < e0.X - 1e-6f
                || (MathF.Abs(e1.X - e0.X) <= 1e-6f && e1.Y < e0.Y);
            if (swap) (e0, e1) = (e1, e0);
            var chord = e1 - e0;
            float clen = chord.Length();
            if (clen < 1e-3f) return false;
            unit = chord / clen;
            origin = e0;
            // Inward from plane projection or left of unit (fixed, not path travel).
            var mid = origin + unit * (clen * 0.5f);
            inwardP = BraceDirAt(settings, mid);
            if (inwardP.LengthSquared() < 0.5f)
                inwardP = new Vector2(-unit.Y, unit.X);
            state.BaselineLocks[contourIndex] = new BaselineLock
            {
                IsCylinder = false,
                Origin = origin,
                Unit = unit,
                InwardUnit = inwardP,
                Theta0 = 0f,
                ThetaSign = 1f,
                CylinderCenter = default,
            };
        }

        // Project all path points onto unit to get length this layer.
        float uMin = float.MaxValue, uMax = float.MinValue;
        for (int i = 0; i < path.Count; i++)
        {
            float uu = Vector2.Dot(path[i] - origin, unit);
            if (uu < uMin) uMin = uu;
            if (uu > uMax) uMax = uu;
        }
        float plen = uMax - uMin;
        if (plen < 1e-3f) return false;
        // Origin stays LOCKED — the world cell grid must not follow this layer's
        // leftmost point (slanted edges would bend every diagonal).

        baseline = new StraightBaseline
        {
            Origin = origin,
            Unit = unit,
            Length = plen,
            IsCylinder = false,
            CylinderCenter = default,
            Radius = 0f,
            Theta0 = 0f,
            ThetaSign = 1f,
            InwardUnit = inwardP,
            UMin = uMin,
            UMax = uMax,
        };
        return true;
    }

    /// <summary>Signed angle span walking the path around cylinder center (unwrapped).</summary>
    private static float AccumulateAngleSpan(List<Vector2> path, Vector2 center, float th0)
    {
        float total = 0f;
        float prev = th0;
        for (int i = 1; i < path.Count; i++)
        {
            var d = path[i] - center;
            if (d.LengthSquared() < 1e-8f) continue;
            float th = MathF.Atan2(d.Y, d.X);
            float step = th - prev;
            while (step > MathF.PI) step -= MathF.PI * 2f;
            while (step <= -MathF.PI) step += MathF.PI * 2f;
            total += step;
            prev = th;
        }
        return total;
    }

    /// <summary>
    /// Angular coverage window of the path around <paramref name="center"/> relative
    /// to locked <paramref name="th0"/>: continuous unwrap along the path, so slanted
    /// panels report both how far CW (dMin, possibly negative) and CCW (dMax) they
    /// reach this layer.
    /// </summary>
    private static void MeasureAngleRangeFrom(
        List<Vector2> path, Vector2 center, float th0, out float dMin, out float dMax)
    {
        dMin = float.MaxValue;
        dMax = float.MinValue;
        float acc = 0f;
        bool first = true;
        float thPrev = 0f;
        foreach (var pt in path)
        {
            var d = pt - center;
            if (d.LengthSquared() < 1e-10f) continue;
            float th = MathF.Atan2(d.Y, d.X);
            if (first)
            {
                float a0 = th - th0;
                while (a0 > MathF.PI) a0 -= MathF.PI * 2f;
                while (a0 <= -MathF.PI) a0 += MathF.PI * 2f;
                acc = a0;
                first = false;
            }
            else
            {
                float step = th - thPrev;
                while (step > MathF.PI) step -= MathF.PI * 2f;
                while (step <= -MathF.PI) step += MathF.PI * 2f;
                acc += step;
            }
            thPrev = th;
            if (acc < dMin) dMin = acc;
            if (acc > dMax) dMax = acc;
        }
        if (first) { dMin = 0f; dMax = 0f; }
    }

    /// <summary>
    /// CCW angular coverage of path points from locked <paramref name="th0"/>
    /// (max unwrapped delta in [0, 2π)).
    /// </summary>
    private static float MeasureAngleCoverageFrom(List<Vector2> path, Vector2 center, float th0)
    {
        float maxCcW = 0f;
        for (int i = 0; i < path.Count; i++)
        {
            var d = path[i] - center;
            if (d.LengthSquared() < 1e-8f) continue;
            float th = MathF.Atan2(d.Y, d.X);
            float delta = th - th0;
            while (delta < 0f) delta += MathF.PI * 2f;
            while (delta >= MathF.PI * 2f) delta -= MathF.PI * 2f;
            if (delta > maxCcW) maxCcW = delta;
        }
        // Also walk path accumulation in case coverage exceeds π.
        float acc = MathF.Abs(AccumulateAngleSpan(path, center, th0));
        return MathF.Max(maxCcW, acc);
    }

    /// <summary>Arc-length on open path of the point nearest to <paramref name="p"/>.</summary>
    private static float NearestArcS(List<Vector2> path, float totalLen, Vector2 p)
    {
        if (path.Count < 2 || totalLen < 1e-6f) return 0f;
        float bestS = 0f;
        float bestD = float.MaxValue;
        float acc = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            var ab = b - a;
            float ab2 = ab.LengthSquared();
            float t = ab2 > 1e-12f ? Math.Clamp(Vector2.Dot(p - a, ab) / ab2, 0f, 1f) : 0f;
            var q = a + ab * t;
            float d = Vector2.DistanceSquared(p, q);
            if (d < bestD)
            {
                bestD = d;
                bestS = acc + MathF.Sqrt(ab2) * t;
            }
            acc += MathF.Sqrt(ab2);
        }
        return Math.Clamp(bestS, 0f, totalLen);
    }

    /// <summary>Append open-path points from arc-length s0 to s1 (inclusive of s1).</summary>
    private static void AppendOpenPathRange(
        List<Vector2> path, float totalLen, float s0, float s1, List<Vector2> dst)
    {
        s0 = Math.Clamp(s0, 0f, totalLen);
        s1 = Math.Clamp(s1, 0f, totalLen);
        if (s1 <= s0 + 1e-5f) return;

        float acc = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            float seg = Vector2.Distance(a, b);
            float segStart = acc;
            float segEnd = acc + seg;

            // Emit vertex at s0 if it falls in this segment and we haven't passed it.
            if (s0 >= segStart - 1e-5f && s0 <= segEnd + 1e-5f && seg > 1e-8f)
            {
                float t0 = Math.Clamp((s0 - segStart) / seg, 0f, 1f);
                var p0 = a + (b - a) * t0;
                if (dst.Count == 0 || Vector2.DistanceSquared(dst[^1], p0) > 1e-8f)
                    dst.Add(p0);
            }

            // Interior vertex b if fully inside (s0, s1).
            if (segEnd > s0 + 1e-4f && segEnd < s1 - 1e-4f)
            {
                if (dst.Count == 0 || Vector2.DistanceSquared(dst[^1], b) > 1e-8f)
                    dst.Add(b);
            }

            // Emit vertex at s1 if it falls in this segment.
            if (s1 >= segStart - 1e-5f && s1 <= segEnd + 1e-5f && seg > 1e-8f)
            {
                float t1 = Math.Clamp((s1 - segStart) / seg, 0f, 1f);
                var p1 = a + (b - a) * t1;
                if (dst.Count == 0 || Vector2.DistanceSquared(dst[^1], p1) > 1e-8f)
                    dst.Add(p1);
            }

            acc = segEnd;
            if (acc >= s1 - 1e-4f) break;
        }
    }

    private static Vector2 TangentAtArcOpen(List<Vector2> path, float totalLen, float s)
    {
        float ds = MathF.Max(totalLen * 0.002f, 0.5f);
        var p0 = PointAtArcOpen(path, totalLen, MathF.Max(0f, s - ds));
        var p1 = PointAtArcOpen(path, totalLen, MathF.Min(totalLen, s + ds));
        return p1 - p0;
    }

    private static float PolyLengthOpen(List<Vector2> path)
    {
        float len = 0;
        for (int i = 1; i < path.Count; i++)
            len += Vector2.Distance(path[i - 1], path[i]);
        return len;
    }

    private static Vector2 PointAtArcOpen(List<Vector2> path, float totalLen, float s)
    {
        if (path.Count == 0) return default;
        if (totalLen < 1e-6f) return path[0];
        s = Math.Clamp(s, 0f, totalLen);
        float acc = 0;
        for (int i = 1; i < path.Count; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            float seg = Vector2.Distance(a, b);
            if (acc + seg >= s - 1e-5f)
            {
                float t = seg > 1e-6f ? (s - acc) / seg : 0f;
                return a + (b - a) * t;
            }
            acc += seg;
        }
        return path[^1];
    }

    /// <summary>
    /// XY unit brace direction at <paramref name="mouth"/> from the selected projection:
    /// planar (plane normal) or cylinder (radial — toward axis by default, outward if flipped).
    /// Zero when no usable direction (falls back to path left-normal).
    /// </summary>
    public static Vector2 BraceDirAt(SliceSettings settings, Vector2 mouth)
    {
        if (string.Equals(settings.XBracingProjectionType, "Cylinder", StringComparison.OrdinalIgnoreCase))
        {
            // Vector from axis to mouth; default pull is toward the axis (−radial).
            var fromAxis = mouth - new Vector2(settings.XBracingCylinderX, settings.XBracingCylinderY);
            float len = fromAxis.Length();
            if (len < 1e-3f) return Vector2.Zero;
            var unit = fromAxis / len;
            return settings.XBracingCylinderFlipDirection ? unit : -unit;
        }

        float ty = settings.XBracingPlaneTiltY * MathF.PI / 180f;
        float tx = settings.XBracingPlaneTiltX * MathF.PI / 180f;
        var n = new Vector2(
            MathF.Sin(ty),
            -MathF.Sin(tx) * MathF.Cos(ty));
        float nLen = n.Length();
        if (nLen < 0.15f) return Vector2.Zero;
        return n / nLen;
    }

    /// <summary>Legacy name — planar direction only (mouth-independent).</summary>
    public static Vector2 BraceDirFromPlane(SliceSettings settings)
        => BraceDirAt(settings, Vector2.Zero);

    private static bool TryOpenPathInward(
        List<Vector2> path, float totalLen, float s, SliceSettings settings, Vector2 mouth,
        out Vector2 inward)
    {
        inward = default;
        float ds = MathF.Max(totalLen * 0.002f, 0.5f);
        var p0 = PointAtArcOpen(path, totalLen, MathF.Max(0f, s - ds));
        var p1 = PointAtArcOpen(path, totalLen, MathF.Min(totalLen, s + ds));
        var tan = p1 - p0;
        float tl = tan.Length();
        if (tl < 1e-6f) return false;
        tan /= tl;
        // Left normal of travel — fallback when projection gives no usable dir.
        var left = new Vector2(-tan.Y, tan.X);

        var projected = BraceDirAt(settings, mouth);
        if (projected.LengthSquared() > 0.5f)
        {
            inward = projected;
            return true;
        }
        inward = left;
        return true;
    }

    public static void Apply(
        LightningPlan plan,
        IReadOnlyList<List<List<Vector2>>> fillPolysPerLayer,
        IReadOnlyList<float> zPositions,
        IReadOnlyList<float> layerHeights,
        SliceSettings settings)
    {
        if (!settings.XBracingEnabled) return;
        int n = fillPolysPerLayer.Count;
        if (n == 0 || plan.Layers.Length < n) return;

        float bead  = MathF.Max(settings.BeadWidth, 0.1f);
        float wantDepth = MathF.Max(settings.XBracingDepthMm, bead * 3f);
        float span  = MathF.Max(settings.XBracingSpanMm, bead * 10f);
        float angleDeg = Math.Clamp(settings.XBracingAngleDeg, 10f, 60f);
        float cellH = span / MathF.Max(MathF.Tan(angleDeg * MathF.PI / 180f), 0.15f);
        cellH = MathF.Max(cellH, settings.LayerHeight * 4f);
        bool extendEdges = settings.XBracingExtendEdges;

        float zMin = zPositions.Count > 0 ? zPositions[0] : 0f;
        float zMax = zPositions.Count > 0 ? zPositions[^1] : zMin;
        // Very light top/bottom exclusion (cellH*0.15 was skipping most short parts).
        float heightPad = MathF.Max(settings.LayerHeight * 1.5f, 2f);

        int nextId = 0;
        foreach (var lp in plan.Layers)
            foreach (var t in lp.Trees)
                if (t.Id >= nextId) nextId = t.Id + 1;

        int bracesPlaced = 0, layersUsed = 0;
        int rejectDepth = 0, rejectSplit = 0, rejectCrowd = 0;
        int skipZ = 0, skipEmpty = 0, skipOuter = 0, skipFace = 0, attempts = 0;
        float depthUsedSum = 0f;
        int depthSamples = 0;
        int stripLayers = 0;

        for (int li = 0; li < n; li++)
        {
            float z = li < zPositions.Count ? zPositions[li] : zMin;
            if (z < zMin + heightPad || z > zMax - heightPad)
            {
                skipZ++;
                continue;
            }

            var polys = fillPolysPerLayer[li];
            var region = BuildWallRegion(polys, bead, wantDepth, out bool usedStrip);
            if (usedStrip) stripLayers++;
            if (region.Count == 0)
            {
                skipEmpty++;
                continue;
            }
            int outerCount = CountOuters(region);

            // Longest boundary path by perimeter (any winding — thin walls often
            // come back CW from EvenOdd union of double-shells).
            PathD? outer = null;
            double bestPerim = 0;
            foreach (var path in region)
            {
                if (path.Count < 4) continue;
                double perim = PathPerimeter(path);
                if (perim > bestPerim) { bestPerim = perim; outer = path; }
            }
            if (outer is null || bestPerim < bead * 8)
            {
                skipOuter++;
                continue;
            }
            // Ensure outer is CCW so left-of-tangent is a consistent starting guess.
            if (Clipper.Area(outer) < 0)
                outer.Reverse();

            var poly = ToPoly(outer);
            float totalLen = PolyLength(poly);
            if (totalLen < bead * 8)
            {
                skipOuter++;
                continue;
            }

            // Prefer a long face; on smooth curves LongFaces may return empty → full ring.
            float minFaceLen = MathF.Max(span * 0.2f, bead * 8f);
            var faces = LongFaces(poly, totalLen, minFaceLen);
            (float faceS0, float faceLen) face;
            if (faces.Count == 0)
                face = (0f, totalLen);
            else
            {
                face = faces[0];
                foreach (var f in faces)
                    if (f.Len > face.faceLen) face = f;
            }
            if (face.faceLen < bead * 8f)
            {
                skipFace++;
                continue;
            }

            float zRel = z - zMin - heightPad;
            if (zRel < 0f) { skipZ++; continue; }
            float cellT = (zRel % cellH) / cellH;
            if (cellT < 0f) cellT += 1f;
            bool midCross = MathF.Abs(cellT - 0.5f) < 0.035f;

            int layerFingers = 0;
            int nCells = extendEdges
                ? Math.Max(1, (int)MathF.Ceiling(face.faceLen / span))
                : Math.Max(1, (int)MathF.Floor(face.faceLen / span));
            if (nCells < 1) nCells = 1;

            for (int c = 0; c < nCells; c++)
            {
                float local0 = c * span;
                if (!extendEdges && local0 + span > face.faceLen + 0.01f) break;

                float cellSpan = MathF.Min(span, MathF.Max(face.faceLen - local0, bead * 5f));
                if (cellSpan < bead * 4f) continue;

                float faceEnd = face.faceS0 + face.faceLen;
                float sA = Math.Clamp(face.faceS0 + local0 + cellT * cellSpan, face.faceS0, faceEnd);
                float sB = Math.Clamp(face.faceS0 + local0 + cellSpan - cellT * cellSpan, face.faceS0, faceEnd);

                attempts++;
                if (TryAddInteriorFinger(
                        plan.Layers[li], poly, totalLen, sA, region, outerCount,
                        wantDepth, bead, settings, ref nextId,
                        ref rejectDepth, ref rejectSplit, ref rejectCrowd, out float dA))
                {
                    layerFingers++;
                    depthUsedSum += dA;
                    depthSamples++;
                }
                if (!midCross)
                {
                    attempts++;
                    if (TryAddInteriorFinger(
                            plan.Layers[li], poly, totalLen, sB, region, outerCount,
                            wantDepth, bead, settings, ref nextId,
                            ref rejectDepth, ref rejectSplit, ref rejectCrowd, out float dB))
                    {
                        layerFingers++;
                        depthUsedSum += dB;
                        depthSamples++;
                    }
                }
            }

            if (layerFingers > 0)
            {
                bracesPlaced += layerFingers;
                layersUsed++;
            }
        }

        float avgDepth = depthSamples > 0 ? depthUsedSum / depthSamples : 0f;
        var line =
            $"[x-bracing] INTERIOR depthWant={wantDepth:0.#}mm avgDepth={avgDepth:0.#}mm " +
            $"span={span:0.#}mm angle={angleDeg:0.#}° cellH={cellH:0.#}mm " +
            $"edges={(extendEdges ? "on" : "off")} fingers={bracesPlaced} layers={layersUsed}/{n} " +
            $"attempts={attempts} rejectDepth={rejectDepth} rejectSplit={rejectSplit} rejectCrowd={rejectCrowd} " +
            $"skipZ={skipZ} skipEmpty={skipEmpty} skipOuter={skipOuter} skipFace={skipFace} stripLayers={stripLayers}";
        plan.UncoveredLog.Add(line);
        System.Console.WriteLine(line);
        if (bracesPlaced == 0)
        {
            var warn =
                "[x-bracing] WARNING: 0 interior braces. " +
                (skipEmpty == n
                    ? "No closed fill regions — wall contours may be open/surface; strip rebuild failed."
                    : skipOuter > 0
                        ? "Contours found but no usable outer perimeter."
                        : rejectDepth > 0
                            ? "Wall thinner than Depth at mouth samples — lower Depth."
                            : rejectSplit > 0
                                ? "Braces would split the wall (too deep for thickness)."
                                : "Check Span vs wall length and that Realtime resliced after enabling.");
            plan.UncoveredLog.Add(warn);
            System.Console.WriteLine(warn);
        }
    }

    /// <summary>
    /// Build a solid region for bracing. Prefer standard fill union; if that is
    /// empty (open surface chains), inflate open polylines into strips wide enough
    /// to hold Depth so freestanding wall panels still get X braces.
    /// </summary>
    private static PathsD BuildWallRegion(
        List<List<Vector2>> polys, float bead, float wantDepth, out bool usedStrip)
    {
        usedStrip = false;
        if (polys is null || polys.Count == 0) return new PathsD();

        var region = LightningPlanner.ToPathsD(polys, bead);
        if (CountOuters(region) > 0)
            return region;

        // Open / degenerate contours: inflate each chain into a strip.
        // Width ≥ 2*Depth so an interior brace of length Depth fits from either side.
        double halfW = Math.Max(wantDepth, bead * 3f);
        var strips = new PathsD();
        foreach (var poly in polys)
        {
            if (poly.Count < 2) continue;
            var path = new PathD(poly.Count);
            foreach (var p in poly) path.Add(new PointD(p.X, p.Y));
            bool closed = poly.Count >= 3
                && Vector2.DistanceSquared(poly[0], poly[^1]) <= 1.0f;
            if (closed && Clipper.Area(path) == 0)
            {
                // Collapsed closed ring — treat as open chain without duplicate end.
                if (path.Count > 1) path.RemoveAt(path.Count - 1);
                closed = false;
            }
            var open = new PathsD { path };
            var fat = Clipper.InflatePaths(open, halfW, JoinType.Round,
                closed ? EndType.Joined : EndType.Round, 2.0);
            if (fat.Count > 0) strips.AddRange(fat);
        }
        if (strips.Count == 0) return new PathsD();
        usedStrip = true;
        return Clipper.Union(strips, FillRule.NonZero);
    }

    private static bool TryAddInteriorFinger(
        LightningLayerPlan layer, List<Vector2> poly, float totalLen, float s,
        PathsD region, int outerCount,
        float wantDepth, float bead, SliceSettings settings, ref int nextId,
        ref int rejectDepth, ref int rejectSplit, ref int rejectCrowd, out float usedDepth)
    {
        usedDepth = 0f;
        var mouth = PointAtArc(poly, totalLen, s);
        // Snap mouth onto region boundary (outer may be strip-inflated vs original poly).
        mouth = LightningPlanner.ClosestOnRegionBoundary(region, mouth);

        if (!TryInwardNormal(poly, totalLen, s, region, mouth, settings, out var inward))
        {
            rejectDepth++;
            return false;
        }

        float solid = FitDepth(mouth, inward, region, MathF.Max(wantDepth, bead * 40f), bead);
        float depth = MathF.Min(wantDepth, solid - bead * 1.0f);
        if (depth < bead * 1.5f)
        {
            rejectDepth++;
            return false;
        }

        var tip = mouth + inward * depth;

        float crowdR = bead * 4f;
        foreach (var t in layer.Trees)
        {
            if (Vector2.DistanceSquared(t.Anchor, mouth) < crowdR * crowdR)
            {
                rejectCrowd++;
                return false;
            }
        }

        if (WouldSplitRegion(region, mouth, tip, bead, outerCount))
        {
            bool ok = false;
            for (float d = depth * 0.85f; d >= bead * 1.5f; d *= 0.85f)
            {
                tip = mouth + inward * d;
                if (!WouldSplitRegion(region, mouth, tip, bead, outerCount))
                {
                    depth = d;
                    ok = true;
                    break;
                }
            }
            if (!ok)
            {
                rejectSplit++;
                return false;
            }
        }

        var tree = new LightningTree
        {
            Id = nextId++,
            Anchor = mouth,
            External = false,
            Cavity = false,
            PaintColumn = false,
        };
        tree.Branches.Add(new LightningBranch([mouth, tip]));
        layer.Trees.Add(tree);
        usedDepth = depth;
        return true;
    }

    private static bool WouldSplitRegion(
        PathsD region, Vector2 mouth, Vector2 tip, float bead, int outerCount)
    {
        float half = bead * 0.5f;
        var dir = mouth - tip;
        float dl = dir.Length();
        if (dl < 1e-5f) return true;
        dir /= dl;
        var ext = mouth + dir * bead;

        var line = new PathD
        {
            new PointD(ext.X, ext.Y),
            new PointD(mouth.X, mouth.Y),
            new PointD(tip.X, tip.Y),
        };
        var slit = Clipper.InflatePaths(new PathsD { line }, half, JoinType.Round, EndType.Round, 2.0);
        if (slit.Count == 0) return true;

        var candidate = Clipper.Difference(region, slit, FillRule.NonZero);
        candidate = Clipper.SimplifyPaths(candidate, 0.05, false);
        if (candidate.Count == 0) return true;
        return CountOuters(candidate) > outerCount;
    }

    private static float FitDepth(
        Vector2 mouth, Vector2 inward, PathsD region, float want, float bead)
    {
        float lo = bead * 1.25f;
        float hi = want;
        if (hi < lo) return 0f;
        if (!LightningPlanner.SegmentInsideRegion(region, mouth, mouth + inward * lo, bead))
            return 0f;

        float best = lo;
        float trial = lo;
        while (trial < hi)
        {
            float next = MathF.Min(hi, trial * 1.4f + bead);
            if (LightningPlanner.SegmentInsideRegion(region, mouth, mouth + inward * next, bead))
            {
                best = next;
                trial = next;
                if (best >= hi - 0.01f) return hi;
            }
            else break;
        }
        float fail = MathF.Min(hi, trial * 1.4f + bead);
        for (int i = 0; i < 12; i++)
        {
            float mid = 0.5f * (best + fail);
            if (LightningPlanner.SegmentInsideRegion(region, mouth, mouth + inward * mid, bead))
                best = mid;
            else
                fail = mid;
        }
        return best;
    }

    private static int CountOuters(PathsD paths)
    {
        int n = 0;
        foreach (var p in paths)
            if (Clipper.Area(p) > 0) n++;
        return n;
    }

    private static List<(float S0, float Len)> LongFaces(List<Vector2> poly, float totalLen, float minLen)
    {
        var faces = new List<(float, float)>();
        if (poly.Count < 2) return faces;

        float acc = 0f;
        float runStart = 0f;
        float runLen = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            float seg = Vector2.Distance(poly[i], poly[(i + 1) % poly.Count]);
            var t0 = poly[(i + 1) % poly.Count] - poly[i];
            var t1 = poly[(i + 2) % poly.Count] - poly[(i + 1) % poly.Count];
            float turn = 0f;
            float l0 = t0.Length(), l1 = t1.Length();
            if (l0 > 1e-6f && l1 > 1e-6f)
                turn = MathF.Abs(MathF.Acos(Math.Clamp(Vector2.Dot(t0 / l0, t1 / l1), -1f, 1f)));

            runLen += seg;
            if (turn > 0.6f || i == poly.Count - 1)
            {
                if (runLen >= minLen)
                    faces.Add((runStart, runLen));
                runStart = acc + seg;
                runLen = 0f;
            }
            acc += seg;
        }
        if (faces.Count >= 2)
        {
            var first = faces[0];
            var last = faces[^1];
            if (MathF.Abs(last.Item1 + last.Item2 - totalLen) < 1e-2f && first.Item1 < 1e-2f)
            {
                faces[0] = (last.Item1, last.Item2 + first.Item2);
                faces.RemoveAt(faces.Count - 1);
            }
        }
        return faces;
    }

    private static List<Vector2> ToPoly(PathD path)
    {
        var pts = new List<Vector2>(path.Count);
        foreach (var p in path)
            pts.Add(new Vector2((float)p.x, (float)p.y));
        if (pts.Count > 2 && Vector2.DistanceSquared(pts[0], pts[^1]) < 1e-6f)
            pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    private static double PathPerimeter(PathD path)
    {
        double len = 0;
        for (int i = 0; i < path.Count; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % path.Count];
            double dx = b.x - a.x, dy = b.y - a.y;
            len += Math.Sqrt(dx * dx + dy * dy);
        }
        return len;
    }

    private static float PolyLength(List<Vector2> poly)
    {
        float len = 0;
        for (int i = 0; i < poly.Count; i++)
            len += Vector2.Distance(poly[i], poly[(i + 1) % poly.Count]);
        return len;
    }

    private static Vector2 PointAtArc(List<Vector2> poly, float totalLen, float s)
    {
        if (totalLen < 1e-6f || poly.Count == 0) return poly.Count > 0 ? poly[0] : default;
        s = ((s % totalLen) + totalLen) % totalLen;
        float acc = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            float seg = Vector2.Distance(a, b);
            if (acc + seg >= s - 1e-5f)
            {
                float t = seg > 1e-6f ? (s - acc) / seg : 0f;
                return a + (b - a) * t;
            }
            acc += seg;
        }
        return poly[0];
    }

    private static bool TryInwardNormal(
        List<Vector2> poly, float totalLen, float s, PathsD region, Vector2 mouth,
        SliceSettings settings, out Vector2 inward)
    {
        inward = default;
        float probe = MathF.Max(beadGuess(region), 2f);

        // Prefer projected brace direction (plane or cylinder radial), then validate
        // that the direction actually enters the wall region.
        var projected = BraceDirAt(settings, mouth);
        if (projected.LengthSquared() > 0.5f)
        {
            if (LightningPlanner.InsideRegion(region, mouth + projected * probe))
            {
                inward = projected;
                return true;
            }
            if (LightningPlanner.InsideRegion(region, mouth - projected * probe))
            {
                inward = -projected;
                return true;
            }
        }

        float ds = MathF.Max(totalLen * 0.002f, 0.5f);
        var p0 = PointAtArc(poly, totalLen, s - ds);
        var p1 = PointAtArc(poly, totalLen, s + ds);
        var tan = p1 - p0;
        float tl = tan.Length();
        if (tl < 1e-6f) return false;
        tan /= tl;
        inward = new Vector2(-tan.Y, tan.X);
        bool inIn = LightningPlanner.InsideRegion(region, mouth + inward * probe);
        bool inOut = LightningPlanner.InsideRegion(region, mouth - inward * probe);
        if (inIn && !inOut) return true;
        if (inOut && !inIn) { inward = -inward; return true; }
        for (float d = probe; d < probe * 6f; d += probe)
        {
            if (LightningPlanner.InsideRegion(region, mouth + inward * d)) return true;
            if (LightningPlanner.InsideRegion(region, mouth - inward * d))
            {
                inward = -inward;
                return true;
            }
        }
        return false;
    }

    private static float beadGuess(PathsD _) => 2f;
}
