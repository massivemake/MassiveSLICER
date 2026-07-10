using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Lightning;

/// <summary>
/// Top-down demand propagation for Lightning Bridge infill.
///
/// Walking from the top layer down, each layer inherits the layer-above's finger
/// trees with every leaf tip retracted by that layer's max lateral step — so printed
/// bottom-up, a finger grows at most one step per layer and always sits on the finger
/// below it. New fingers are rooted wherever the layer-above's boundary is farther
/// from this layer's material than the support radius (outward flares, T-bars,
/// islands widening above).
/// </summary>
public static class LightningPlanner
{
    /// <summary>True for Formbound Bridge (dual-wall) or Formbound Buttress (solid ramps).</summary>
    public static bool IsFormboundPattern(InfillPattern p) =>
        p is InfillPattern.LightningBridge or InfillPattern.FormboundButtress;

    /// <summary>True when the pattern builds multi-bead solid ramp polygons.</summary>
    public static bool IsButtressPattern(InfillPattern p) =>
        p == InfillPattern.FormboundButtress;

    /// <param name="fillPolysPerLayer">Closed fill polygons per layer, bottom-up,
    /// plane-local 2D (outer CCW / holes CW as produced by the slicers).</param>
    /// <param name="layerHeights">Height of each layer (adaptive-aware).</param>
    /// <param name="frames">Per-layer plane frames for slicers whose frame ROTATES
    /// between layers (Multi-Planar). When set, everything inherited or compared
    /// across layers is remapped through world space into the receiving layer's
    /// frame — plane-local coordinates of the same physical point can drift by
    /// several mm per layer on a rotating stack, which would otherwise read as
    /// phantom unsupported arcs (and spawn a fresh finger row every layer).</param>
    /// <param name="solidAt">Mesh-truth oracle: (layerIndex, planeLocalPoint) → is
    /// there REAL material there? Grazing cuts over pocket rims emit the rim curve
    /// without its host wall, and 2D contour parity can only read that as a solid
    /// island — new-finger demand is vetoed when the mesh says the claimed solid
    /// is void (otherwise each 1–2 layer phantom seeds a ladder of bridging for
    /// dozens of layers under geometry that doesn't exist; Drone V52, 2026-07-09).</param>
    public static LightningPlan Build(
        IReadOnlyList<List<List<Vector2>>> fillPolysPerLayer,
        IReadOnlyList<float> layerHeights,
        SliceSettings settings,
        IReadOnlyList<(Vector3 Origin, Vector3 U, Vector3 V)>? frames = null,
        Func<int, Vector2, bool>? solidAt = null,
        IReadOnlyList<IReadOnlyList<Vector2>>? manualDemand = null)
    {
        int n = fillPolysPerLayer.Count;
        var plan = new LightningPlan(n);
        if (n == 0) return plan;

        float bead    = MathF.Max(settings.BeadWidth, 0.1f);
        float tanA    = MathF.Tan(Math.Clamp(settings.LightningOverhangDeg, 5f, 80f) * MathF.PI / 180f);
        float spacing = settings.LightningBranchSpacingMm > 0f
            ? settings.LightningBranchSpacingMm
            : 4f * bead;
        bool  buttress = IsButtressPattern(settings.InfillPattern);
        // Horizontal support bar length (single bead). 0 → auto = spacing.
        float barLen = settings.LightningButtressBarMm > 0f
            ? settings.LightningButtressBarMm
            : spacing;
        bool  preferInterior = settings.LightningPreferInteriorMouths;

        float MaxStep(int i) => MathF.Min(MathF.Max(layerHeights[i], 0.1f) * tanA, 0.5f * bead);

        int nextTreeId = 0;
        // Trees whose anchor lost its footing mid-descent (boundary swept away, e.g.
        // an angled-slicing notch) — removed from EVERY layer after the build so no
        // layer keeps a finger whose support column ends in mid-air below it.
        var orphaned = new HashSet<int>();

        var regions = new PathsD[n];
        for (int i = 0; i < n; i++)
            regions[i] = ToPathsD(fillPolysPerLayer[i], bead);

        for (int i = n - 2; i >= 0; i--)
        {
            var layerPlan = plan.Layers[i];
            var region    = regions[i];
            if (region.Count == 0)
            {
                // Nothing on this plane at all — every tree above is dangling in air.
                foreach (var t in plan.Layers[i + 1].Trees)
                    orphaned.Add(t.Id);
                continue;
            }

            // Region shrunk by one bead — finger nodes must stay at least a bead
            // inside so the slit walls never poke through the perimeter.
            var core = Clipper.InflatePaths(region, -bead, JoinType.Miter, EndType.Polygon, 3.0);
            if (core.Count == 0)
            {
                // Too thin to host fingers on this plane — the columns above have no
                // continuation here, so silently skipping would leave them floating.
                foreach (var t in plan.Layers[i + 1].Trees)
                    orphaned.Add(t.Id);
                continue;
            }

            // Fingers may only ROOT on allowed boundary classes: interior boundaries
            // (holes / inner walls — notch hidden inside the part) and/or the outer
            // perimeter (notch visible outside). After Union normalization, outers
            // have positive area and holes negative.
            // Formbound Buttress with prefer-interior: try holes first; exterior is
            // only used as fallback when a tip cannot reach any interior anchor.
            var anchorInterior = new PathsD();
            var anchorExterior = new PathsD();
            foreach (var path in region)
            {
                bool isOuter = Clipper.Area(path) > 0;
                if (isOuter)
                {
                    if (settings.LightningAnchorExterior) anchorExterior.Add(path);
                }
                else if (settings.LightningAnchorInterior)
                    anchorInterior.Add(path);
            }
            var anchorPaths = new PathsD();
            if (buttress && preferInterior && anchorInterior.Count > 0)
            {
                foreach (var p in anchorInterior) anchorPaths.Add(p);
                // Exterior kept as fallback per-tip below.
            }
            else
            {
                foreach (var p in anchorInterior) anchorPaths.Add(p);
                foreach (var p in anchorExterior) anchorPaths.Add(p);
            }
            if (anchorPaths.Count == 0)
            {
                foreach (var p in anchorExterior) anchorPaths.Add(p);
            }
            if (anchorPaths.Count == 0) continue;

            // Frame remap (identity on constant-frame stacks): lift the plane-local
            // point to world in the source layer's frame, project into the target's.
            Vector2 Remap(int from, int to, Vector2 p)
            {
                if (frames is null || from == to) return p;
                var fa = frames[from];
                var fb = frames[to];
                var w = fa.Origin + fa.U * p.X + fa.V * p.Y;
                var rel = w - fb.Origin;
                return new Vector2(Vector3.Dot(rel, fb.U), Vector3.Dot(rel, fb.V));
            }
            Vector2 Down(Vector2 p) => Remap(i + 1, i, p);

            // ── 1. Inherit the layer-above's trees with retracted tips ─────────
            float stepAbove = MaxStep(i + 1);
            // Sacrificial external fins lean at the physical bead-on-bead limit —
            // half a bead of offset per layer — instead of the shallower
            // surface-quality overhang angle. They peel off the perimeter close
            // under the overhang rather than trailing a sail down to the bed.
            float stepAboveExternal = MathF.Max(stepAbove, 0.5f * bead);
            foreach (var above in plan.Layers[i + 1].Trees)
            {
                var t = above.Clone();
                // Snapshot tips BEFORE re-aim so MaxStep stacking still holds.
                var prevTips = SnapshotTips(t);

                if (frames is not null)
                {
                    t.Anchor = Down(t.Anchor);
                    foreach (var b in t.Branches)
                        for (int k = 0; k < b.Centerline.Count; k++)
                            b.Centerline[k] = Down(b.Centerline[k]);
                    for (int pi = 0; pi < prevTips.Count; pi++)
                        prevTips[pi] = Down(prevTips[pi]);
                }

                // Dangling check FIRST — before natural retraction-death can hide it.
                // A tree that tapers out on this layer is only safe when its anchor
                // wall continues below; if the whole island vanished (newborn patch,
                // angled sweep, notch), everything above is floating — retire the
                // lineage instead of teleporting or silently tapering in mid-air.
                var reAnchor = ClosestOnRegionBoundary(t.External ? region : anchorPaths, t.Anchor);
                if (Vector2.Distance(reAnchor, t.Anchor) > MathF.Max(4f * bead, 3f * stepAbove))
                {
                    orphaned.Add(t.Id);
                    continue;
                }

                RetractLeafTips(t, t.External ? stepAboveExternal : stepAbove);
                if (t.Branches.Count == 0) continue;

                t.Anchor = reAnchor;
                if (!t.External)
                    ClampInside(t, region, core, MaxStep(i));

                // Multi-planar / rotating frames: re-aim trunk along this plane's wall
                // normal and bar along wall tangent so a frozen birth angle does not
                // walk off the overhang as the slice plane tilts. Tips stay within
                // MaxStep of the remapped previous tips (bead-on-bead stack).
                // Planar stacks (null frames) keep remapped geometry + re-root only.
                if (buttress && !t.External && frames is not null)
                    ReAimButtress(t, region, core, bead, barLen, MaxStep(i), prevTips);
                else if (t.Branches.Count > 0 && t.Branches[0].Centerline.Count > 0)
                    t.Branches[0].Centerline[0] = t.Anchor;

                // Round every junction so dual-wall slits never form sub-bead corners
                // (acute elbows → over-extrusion on the perimeter path).
                FilletTreeCorners(t, bead);

                if (t.Branches.Count > 0 && t.Branches[0].Centerline.Count > 0)
                {
                    t.Branches[0].Centerline[0] = t.Anchor;

                    // As the region morphs (ring closing into a cap), a re-anchored
                    // trunk can swing across a hole — unrealizable as a slit and
                    // unstable layer-to-layer. Retire such lineages.
                    bool crossesVoid = false;
                    if (!t.External)
                        foreach (var b in t.Branches)
                        {
                            for (int k = 1; k < b.Centerline.Count && !crossesVoid; k++)
                                crossesVoid = !SegmentInsideRegion(
                                    region, b.Centerline[k - 1], b.Centerline[k], bead);
                            if (crossesVoid) break;
                        }
                    if (crossesVoid)
                    {
                        orphaned.Add(t.Id);
                        continue;
                    }

                    layerPlan.Trees.Add(t);
                }
            }

            // ── 2. New demand: arcs of the layer above too far from this layer's
            //       WALL. Printed material is the perimeter bead itself (infill
            //       replaces shells), so support is measured from the boundary
            //       curve — not the region area. Inward-shrinking tops (domes,
            //       closing vessels) become demand; outward flares are skipped
            //       (nothing below them — physically unsupportable). ─────────────
            float supportRadius = stepAbove + bead * 0.5f;
            float sampleStep = spacing * 0.25f;

            foreach (var path in regions[i + 1])
            {
                // A boundary smaller than a couple of beads is unprintable junk —
                // never worth a finger (grazing-cut specks at the very top).
                if (Math.Abs(Clipper.Area(path)) < 4.0 * bead * bead) continue;

                var samples = SamplePath(path, sampleStep);
                if (samples.Count == 0) continue;
                var rawSamples = frames is not null ? new List<Vector2>(samples) : samples;
                if (frames is not null)
                    for (int k = 0; k < samples.Count; k++)
                        samples[k] = Down(samples[k]);

                // How close a demand sample must be to an existing centerline to count
                // as already covered. Bridge uses the full support radius. Buttress is
                // tighter — and SIDE-AWARE: a T rooted on the opposite wall of a cavity
                // must not suppress demand on this wall (the "one side 95%, other side 0%" case).
                float coverRadius = buttress
                    ? MathF.Max(bead * 0.75f, supportRadius * 0.3f)
                    : supportRadius;
                // Anchors within this distance of the sample's home-wall foot count as
                // "same side". Opposite wall of a typical channel is many beads away.
                float sameSideMax = MathF.Max(6f * bead, barLen);

                // Flag which boundary samples of the layer above lack support here.
                var unsupported = new bool[samples.Count];
                for (int si = 0; si < samples.Count; si++)
                {
                    var pt = samples[si];
                    bool far = Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt) > supportRadius;
                    bool inside = InsideRegion(region, pt);
                    bool covered = buttress
                        ? CoveredBySameSide(layerPlan.Trees, pt, coverRadius, sameSideMax, region)
                        : NearAnyCenterline(layerPlan.Trees, pt, coverRadius);
                    unsupported[si] = far
                        && (inside || settings.LightningExteriorOverhangs)
                        && !covered;
                }

                // Distribute support EVENLY along each contiguous unsupported run.
                foreach (var (start, count) in CircularRuns(unsupported))
                {
                    float runLen = count * sampleStep;
                    if (buttress)
                    {
                        // Pitch along the edge: dense enough that bars can stitch into a
                        // continuous under-bridge. Overlap bars by ~half so a long rail
                        // is not left with gaps between T's (the orange-line case).
                        float barPitch = MathF.Max(bead * 2f, MathF.Min(spacing, barLen * 0.5f));
                        // Adaptive arm length: at least the user barLen, but grow so
                        // neighbouring T's overlap along the run (continuous coverage).
                        float adaptiveBar = MathF.Max(barLen, barPitch * 2.2f);
                        int barCount = Math.Max(1, (int)MathF.Ceiling(runLen / barPitch - 1e-4f));

                        for (int k = 0; k < barCount; k++)
                        {
                            int si = (start + (int)((k + 0.5f) * count / barCount)) % samples.Count;
                            TryAddButtressAt(samples, si, sampleStep, count, start,
                                region, core, anchorPaths, anchorInterior, anchorExterior,
                                preferInterior, settings, bead, adaptiveBar, coverRadius, sameSideMax,
                                layerPlan, ref nextTreeId, solidAt, i + 1, regions[i + 1], rawSamples);
                        }

                        // Residual: every still-uncovered sample on this run gets another
                        // shot. Side-aware so the far wall is never "covered" by near wall.
                        for (int j = 0; j < count; j++)
                        {
                            int si = (start + j) % samples.Count;
                            var pt = samples[si];
                            if (Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt) <= supportRadius)
                                continue;
                            if (!InsideRegion(region, pt) && !settings.LightningExteriorOverhangs)
                                continue;
                            if (CoveredBySameSide(layerPlan.Trees, pt, coverRadius, sameSideMax, region))
                                continue;
                            TryAddButtressAt(samples, si, sampleStep, count, start,
                                region, core, anchorPaths, anchorInterior, anchorExterior,
                                preferInterior, settings, bead, adaptiveBar, coverRadius, sameSideMax,
                                layerPlan, ref nextTreeId, solidAt, i + 1, regions[i + 1], rawSamples);
                        }
                    }
                    else
                    {
                        int tipCount = Math.Max(1, (int)MathF.Round(runLen / spacing));
                        for (int k = 0; k < tipCount; k++)
                        {
                            int si = (start + (int)((k + 0.5f) * count / tipCount)) % samples.Count;
                            var sPt = samples[si];

                            if (!PassesMeshVetoAt(solidAt, i + 1, regions[i + 1], rawSamples, si, bead))
                                continue;

                            bool external = !InsideRegion(region, sPt);
                            var tip = external
                                ? sPt
                                : InsideRegion(core, sPt) ? sPt : ClosestOnRegionBoundary(core, sPt);

                            if (TooCloseToExisting(layerPlan.Trees, tip, spacing * 0.5f)) continue;

                            var anchor = external
                                ? ClosestOnRegionBoundary(region, tip)
                                : ClosestOnRegionBoundary(anchorPaths, tip);

                            if (Vector2.Distance(anchor, tip) < bead) continue;
                            if (!external && !SegmentInsideRegion(region, anchor, tip, bead)) continue;

                            var t = new LightningTree { Id = nextTreeId++, Anchor = anchor, External = external };
                            t.Branches.Add(new LightningBranch([anchor, tip]));
                            layerPlan.Trees.Add(t);
                        }
                    }
                }
            }

            // ── 2c. Opposite-wall sweep (Buttress only): re-walk every contour of the
            //       layer above and force a T wherever a sample is still uncovered on
            //       ITS home wall. Catches the far side of a channel after the near
            //       side already placed long bars that would otherwise look "close".
            if (buttress)
            {
                float coverRadius2 = MathF.Max(bead * 0.75f, supportRadius * 0.3f);
                float sameSideMax2 = MathF.Max(6f * bead, barLen);
                float adaptiveBar2 = MathF.Max(barLen, MathF.Max(bead * 2f, MathF.Min(spacing, barLen * 0.5f)) * 2.2f);

                foreach (var path in regions[i + 1])
                {
                    if (Math.Abs(Clipper.Area(path)) < 4.0 * bead * bead) continue;
                    var samples = SamplePath(path, sampleStep);
                    if (samples.Count == 0) continue;
                    var rawSamples = frames is not null ? new List<Vector2>(samples) : samples;
                    if (frames is not null)
                        for (int k = 0; k < samples.Count; k++)
                            samples[k] = Down(samples[k]);

                    for (int si = 0; si < samples.Count; si++)
                    {
                        var pt = samples[si];
                        if (Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt) <= supportRadius)
                            continue;
                        if (!InsideRegion(region, pt) && !settings.LightningExteriorOverhangs)
                            continue;
                        if (CoveredBySameSide(layerPlan.Trees, pt, coverRadius2, sameSideMax2, region))
                            continue;
                        // Treat whole path as one run for WalkAlongRun clamping.
                        TryAddButtressAt(samples, si, sampleStep, samples.Count, 0,
                            region, core, anchorPaths, anchorInterior, anchorExterior,
                            preferInterior, settings, bead, adaptiveBar2, coverRadius2, sameSideMax2,
                            layerPlan, ref nextTreeId, solidAt, i + 1, regions[i + 1], rawSamples);
                    }
                }
            }

            // ── 2b. Manual demand: brush-painted Bridge marks projected onto the
            //       layer above. The user explicitly asked for support under these
            //       beads — geometric sanity checks only, no spacing thinning, no
            //       mesh veto, and external fins allowed regardless of the setting.
            if (manualDemand is not null && manualDemand.Count > i + 1)
                foreach (var mPt in manualDemand[i + 1])
                {
                    var pt = Down(mPt);
                    if (Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt) <= supportRadius)
                        continue;   // wall below already carries it
                    if (NearAnyCenterline(layerPlan.Trees, pt, spacing * 0.4f))
                        continue;   // a finger is already headed there

                    bool external = !InsideRegion(region, pt);
                    var tip = external
                        ? pt
                        : InsideRegion(core, pt) ? pt : ClosestOnRegionBoundary(core, pt);
                    var anchor = external
                        ? ClosestOnRegionBoundary(region, tip)
                        : ClosestOnRegionBoundary(anchorPaths, tip);
                    if (Vector2.Distance(anchor, tip) < bead) continue;
                    if (!external && !SegmentInsideRegion(region, anchor, tip, bead)) continue;

                    var t = new LightningTree { Id = nextTreeId++, Anchor = anchor, External = external };
                    t.Branches.Add(new LightningBranch([anchor, tip]));
                    layerPlan.Trees.Add(t);
                }

            // ── 3. Straightening: nudge interior nodes toward the root–tip chord,
            //       budgeted by this layer's max step so the layer above still rests
            //       within one step of the new position. ──────────────────────────
            float budget = MaxStep(i);
            foreach (var t in layerPlan.Trees)
            {
                if (t.External) continue;   // fins are short and live outside the core
                foreach (var b in t.Branches)
                    Straighten(b.Centerline, budget, core);
            }
        }

        if (orphaned.Count > 0)
            foreach (var lp in plan.Layers)
                lp.Trees.RemoveAll(t => orphaned.Contains(t.Id));

        return plan;
    }

    // -- Tree operations ---------------------------------------------------------

    private static bool PassesMeshVetoAt(
        Func<int, Vector2, bool>? solidAt,
        int layerAbove,
        PathsD regionAbove,
        List<Vector2> rawSamples,
        int si,
        float bead)
    {
        if (solidAt is null) return true;
        var raw  = rawSamples[si];
        var prev = rawSamples[(si - 1 + rawSamples.Count) % rawSamples.Count];
        var next = rawSamples[(si + 1) % rawSamples.Count];
        var tan  = next - prev;
        var nrm  = tan.LengthSquared() > 1e-9f
            ? Vector2.Normalize(new Vector2(-tan.Y, tan.X))
            : new Vector2(1f, 0f);
        if (!InsideRegion(regionAbove, raw + nrm * (0.6f * bead)))
            nrm = -nrm;
        var probe = raw + nrm * (2.5f * bead);
        if (!InsideRegion(regionAbove, probe))
            probe = raw + nrm * bead;
        if (!InsideRegion(regionAbove, probe))
            probe = raw + nrm * (0.6f * bead);
        return solidAt(layerAbove, probe);
    }

    /// <summary>Try to place one Formbound Buttress T at sample <paramref name="si"/>.
    /// Returns false when mesh veto / topology / proximity rejects it.</summary>
    private static bool TryAddButtressAt(
        List<Vector2> samples, int si, float sampleStep, int runCount, int runStart,
        PathsD region, PathsD core,
        PathsD anchorPaths, PathsD anchorInterior, PathsD anchorExterior,
        bool preferInterior, SliceSettings settings, float bead, float barLen,
        float coverRadius, float sameSideMax,
        LightningLayerPlan layerPlan, ref int nextTreeId,
        Func<int, Vector2, bool>? solidAt, int layerAbove, PathsD regionAbove,
        List<Vector2> rawSamples)
    {
        if (!PassesMeshVetoAt(solidAt, layerAbove, regionAbove, rawSamples, si, bead))
            return false;

        var tree = TryBuildButtressT(
            samples, si, sampleStep, runCount, runStart,
            region, core, anchorPaths, anchorInterior, anchorExterior,
            preferInterior, settings.LightningAnchorInterior,
            settings.LightningAnchorExterior,
            bead, barLen, nextTreeId);
        if (tree is null) return false;

        // Proximity: same-side only. A T on the opposite wall of a channel must not
        // block this placement even if its bar ends near our elbow.
        var elbow = tree.Branches[0].Centerline[^1];
        if (CoveredBySameSide(layerPlan.Trees, elbow, coverRadius, sameSideMax, region))
            return false;
        if (TooCloseToElbowSameSide(layerPlan.Trees, tree.Anchor, elbow, bead * 1.25f, sameSideMax))
            return false;

        nextTreeId++;
        layerPlan.Trees.Add(tree);
        return true;
    }

    /// <summary>
    /// True when <paramref name="pt"/> lies near an existing tree's centerline AND
    /// that tree is rooted on the same wall as <paramref name="pt"/>'s home foot.
    /// Opposite-wall T's never count as coverage (channel far-side support).
    /// </summary>
    private static bool CoveredBySameSide(
        List<LightningTree> trees, Vector2 pt, float coverRadius, float sameSideMax, PathsD region)
    {
        if (trees.Count == 0) return false;
        var home = ClosestOnRegionBoundary(region, pt);
        float coverR2 = coverRadius * coverRadius;
        float sideR2 = sameSideMax * sameSideMax;

        foreach (var t in trees)
        {
            // Same-side gate first (cheap): opposite wall anchors are far from home.
            if (Vector2.DistanceSquared(t.Anchor, home) > sideR2)
                continue;

            foreach (var b in t.Branches)
            {
                var line = b.Centerline;
                for (int i = 1; i < line.Count; i++)
                    if (DistToSegmentSq(pt, line[i - 1], line[i]) < coverR2)
                        return true;
            }
        }
        return false;
    }

    /// <summary>Elbow collision only against trees rooted on the same wall neighborhood.</summary>
    private static bool TooCloseToElbowSameSide(
        List<LightningTree> trees, Vector2 newAnchor, Vector2 elbow, float minDist, float sameSideMax)
    {
        float s2 = minDist * minDist;
        float sideR2 = sameSideMax * sameSideMax;
        foreach (var t in trees)
        {
            if (Vector2.DistanceSquared(t.Anchor, newAnchor) > sideR2) continue;
            if (t.Branches.Count == 0 || t.Branches[0].Centerline.Count < 2) continue;
            var other = t.Branches[0].Centerline[^1];
            if (Vector2.DistanceSquared(other, elbow) < s2) return true;
        }
        return false;
    }

    /// <summary>
    /// Formbound Buttress T-morph: single-bead centerline from a perimeter mouth
    /// into a horizontal support bar that FOLLOWS the unsupported run (bridge edge),
    /// not a free-space chord that can miss the ledge.
    /// <para>
    ///   wall ──► elbow ──┬── bar along run →
    ///                    └── bar along run ←
    /// </para>
    /// Anchor search tries every allowed wall class (interior then exterior) and
    /// picks the shortest valid approach so opposite sides of a cavity each get
    /// their own mouth instead of all anchoring on one wall.
    /// </summary>
    private static LightningTree? TryBuildButtressT(
        List<Vector2> samples, int si, float sampleStep, int runCount, int runStart,
        PathsD region, PathsD core,
        PathsD anchorPaths, PathsD anchorInterior, PathsD anchorExterior,
        bool preferInterior, bool allowInterior, bool allowExterior,
        float bead, float barLen, int id)
    {
        var sPt = samples[si];
        bool external = !InsideRegion(region, sPt);
        var keep = external ? region : core;

        // Elbow sits under the demand sample, kept inside printable core.
        Vector2 elbow = external
            ? sPt
            : InsideRegion(core, sPt) ? sPt : ClosestOnRegionBoundary(core, sPt);

        // Bar follows the UNSUPPORTED RUN (actual bridge edge), not a free tan walk.
        // Walk ±halfBar of arc length along the sample ring, clamped to this run.
        float halfBar = MathF.Max(barLen * 0.5f, bead);
        var left  = WalkAlongRun(samples, si, runStart, runCount, sampleStep, -halfBar, keep, bead, external);
        var right = WalkAlongRun(samples, si, runStart, runCount, sampleStep,  halfBar, keep, bead, external);

        // Home wall under this demand: prefer an anchor on THIS wall so the far
        // side of a channel roots on the far wall (not a long diagonal to the near one).
        var homeWall = ClosestOnRegionBoundary(region, elbow);

        var candidates = new List<Vector2>();
        void AddClosest(PathsD paths)
        {
            if (paths.Count == 0) return;
            candidates.Add(ClosestOnRegionBoundary(paths, elbow));
        }
        if (external)
            AddClosest(region);
        else
        {
            if (preferInterior)
            {
                if (allowInterior) AddClosest(anchorInterior);
                if (allowExterior) AddClosest(anchorExterior);
            }
            else
            {
                AddClosest(anchorPaths);
                if (allowInterior) AddClosest(anchorInterior);
                if (allowExterior) AddClosest(anchorExterior);
            }
        }
        // Always also consider the pure home-wall foot as a candidate.
        candidates.Add(homeWall);

        Vector2 anchor = default;
        bool found = false;
        float bestScore = float.MaxValue;
        foreach (var cand in candidates)
        {
            float dElbow = Vector2.Distance(cand, elbow);
            if (dElbow < bead * 0.5f) continue;
            if (!external && !SegmentInsideRegion(region, cand, elbow, bead)) continue;
            // Prefer anchors near the home wall (same side), then short trunk.
            float dHome = Vector2.Distance(cand, homeWall);
            float score = dHome * 2f + dElbow;
            if (score < bestScore) { bestScore = score; anchor = cand; found = true; }
        }
        if (!found) return null;

        // Single continuous morph as a T: trunk = wall→elbow, leaves = bar ends.
        var tree = new LightningTree { Id = id, Anchor = anchor, External = external };
        tree.Branches.Add(new LightningBranch([anchor, elbow])); // trunk
        if (Vector2.Distance(elbow, left) >= bead * 0.4f)
            tree.Branches.Add(new LightningBranch([elbow, left]) { ParentBranch = 0, ParentNode = 1 });
        if (Vector2.Distance(elbow, right) >= bead * 0.4f)
            tree.Branches.Add(new LightningBranch([elbow, right]) { ParentBranch = 0, ParentNode = 1 });

        // Degenerate: no bar — fall back to plain radial finger.
        if (tree.Branches.Count < 2)
        {
            tree.Branches.Clear();
            tree.Branches.Add(new LightningBranch([anchor, elbow]));
        }
        FilletTreeCorners(tree, bead);
        return tree;
    }

    /// <summary>Leaf tip positions (and trunk tip if no leaves) for MaxStep tracking.</summary>
    private static List<Vector2> SnapshotTips(LightningTree tree)
    {
        var tips = new List<Vector2>();
        for (int bi = 0; bi < tree.Branches.Count; bi++)
        {
            bool isLeaf = true;
            for (int oj = 0; oj < tree.Branches.Count; oj++)
                if (tree.Branches[oj].ParentBranch == bi) { isLeaf = false; break; }
            if (isLeaf && tree.Branches[bi].Centerline.Count > 0)
                tips.Add(tree.Branches[bi].Centerline[^1]);
        }
        if (tips.Count == 0 && tree.Branches.Count > 0 && tree.Branches[0].Centerline.Count > 0)
            tips.Add(tree.Branches[0].Centerline[^1]);
        return tips;
    }

    /// <summary>
    /// Re-aim a Formbound Buttress T into this layer's plane geometry:
    /// trunk along the wall's inward normal, bar along the wall tangent (horizontal
    /// support under the overhang). Multi-planar frame rotation otherwise freezes
    /// the birth angle and walks the stack off the ledge.
    /// Tips may move at most <paramref name="maxStep"/> from <paramref name="prevTips"/>.
    /// </summary>
    private static void ReAimButtress(
        LightningTree tree, PathsD region, PathsD core, float bead, float barLen,
        float maxStep, List<Vector2> prevTips)
    {
        if (tree.Branches.Count == 0) return;
        var trunk = tree.Branches[0].Centerline;
        if (trunk.Count < 2) return;

        var anchor = tree.Anchor;
        if (!TryBoundaryFrame(region, anchor, out var tangent, out var inward))
        {
            trunk[0] = anchor;
            return;
        }

        // Previous elbow / tips in this frame (after Down remap).
        var prevElbow = trunk.Count > 1 ? trunk[^1] : anchor + inward * bead;
        float trunkLen = Vector2.Distance(anchor, prevElbow);
        if (trunkLen < bead * 0.5f) trunkLen = bead;

        // Re-aim elbow along wall normal; keep length, then pull toward previous elbow.
        var elbowTarget = anchor + inward * trunkLen;
        if (!InsideRegion(core, elbowTarget))
            elbowTarget = ClosestOnRegionBoundary(core, elbowTarget);
        var elbow = PullWithin(prevElbow, elbowTarget, maxStep);
        if (!InsideRegion(core, elbow))
            elbow = ClosestOnRegionBoundary(core, elbow);
        if (!SegmentInsideRegion(region, anchor, elbow, bead))
        {
            // Fall back: keep remapped trunk, only re-root.
            trunk[0] = anchor;
            return;
        }

        float halfBar = MathF.Max(barLen * 0.5f, bead);
        var leftTarget  = elbow - tangent * halfBar;
        var rightTarget = elbow + tangent * halfBar;
        if (!InsideRegion(core, leftTarget))  leftTarget  = ClosestOnRegionBoundary(core, leftTarget);
        if (!InsideRegion(core, rightTarget)) rightTarget = ClosestOnRegionBoundary(core, rightTarget);

        // Match previous leaf tips (order by proximity) so stacking stays stable.
        Vector2 prevLeft = prevTips.Count > 0 ? prevTips[0] : leftTarget;
        Vector2 prevRight = prevTips.Count > 1 ? prevTips[1] : (prevTips.Count > 0 ? prevTips[0] : rightTarget);
        if (prevTips.Count >= 2)
        {
            // Assign so each target stays near a previous tip.
            float d0 = Vector2.Distance(prevTips[0], leftTarget) + Vector2.Distance(prevTips[1], rightTarget);
            float d1 = Vector2.Distance(prevTips[0], rightTarget) + Vector2.Distance(prevTips[1], leftTarget);
            if (d1 < d0) (prevLeft, prevRight) = (prevTips[1], prevTips[0]);
            else (prevLeft, prevRight) = (prevTips[0], prevTips[1]);
        }

        var left  = PullWithin(prevLeft, leftTarget, maxStep);
        var right = PullWithin(prevRight, rightTarget, maxStep);

        // Rebuild T: trunk + two bar leaves.
        tree.Branches.Clear();
        tree.Branches.Add(new LightningBranch([anchor, elbow]));
        if (Vector2.Distance(elbow, left) >= bead * 0.4f
            && SegmentInsideRegion(region, elbow, left, bead))
            tree.Branches.Add(new LightningBranch([elbow, left]) { ParentBranch = 0, ParentNode = 1 });
        if (Vector2.Distance(elbow, right) >= bead * 0.4f
            && SegmentInsideRegion(region, elbow, right, bead))
            tree.Branches.Add(new LightningBranch([elbow, right]) { ParentBranch = 0, ParentNode = 1 });
        tree.Anchor = anchor;
    }

    private static Vector2 PullWithin(Vector2 from, Vector2 target, float maxStep)
    {
        var d = target - from;
        float len = d.Length();
        if (len <= maxStep || maxStep <= 0f) return target;
        return from + d * (maxStep / len);
    }

    /// <summary>Tangent (CCW along path) and inward unit normal at the boundary
    /// point nearest <paramref name="p"/>.</summary>
    private static bool TryBoundaryFrame(PathsD region, Vector2 p, out Vector2 tangent, out Vector2 inward)
    {
        tangent = new Vector2(1f, 0f);
        inward = new Vector2(0f, 1f);
        float best = float.MaxValue;
        Vector2 bestA = default, bestB = default;
        bool found = false;
        foreach (var path in region)
        {
            int n = path.Count;
            if (n < 2) continue;
            for (int i = 0; i < n; i++)
            {
                var a = new Vector2((float)path[i].x, (float)path[i].y);
                var b = new Vector2((float)path[(i + 1) % n].x, (float)path[(i + 1) % n].y);
                var ab = b - a;
                float len2 = ab.LengthSquared();
                float t = len2 < 1e-12f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
                var c = a + ab * t;
                float d2 = Vector2.DistanceSquared(p, c);
                if (d2 < best) { best = d2; bestA = a; bestB = b; found = true; }
            }
        }
        if (!found) return false;
        var tan = bestB - bestA;
        if (tan.LengthSquared() < 1e-12f) return false;
        tangent = Vector2.Normalize(tan);
        // Left normal of CCW outer = inward for positive-area outers; for holes
        // (CW / negative area) the same left normal points into the solid too
        // when the hole path is CW. Use a probe to pick the side that is inside.
        var nrm = new Vector2(-tangent.Y, tangent.X);
        var mid = (bestA + bestB) * 0.5f;
        if (!InsideRegion(region, mid + nrm * 0.5f))
            nrm = -nrm;
        inward = nrm;
        return true;
    }

    /// <summary>
    /// Fillet every polyline corner and T-junction to a minimum radius of one
    /// <paramref name="bead"/> so dual-wall slits never form acute corners that
    /// over-extrude when the perimeter path detours through the finger.
    /// </summary>
    internal static void FilletTreeCorners(LightningTree tree, float bead)
    {
        float r = MathF.Max(bead, 0.5f);

        // Soften the T-junction first (trunk meets bar leaves at ~90°).
        if (tree.Branches.Count >= 2)
        {
            var trunk = tree.Branches[0].Centerline;
            if (trunk.Count >= 2)
            {
                var elbow = trunk[^1];
                var trunkDir = elbow - trunk[0];
                if (trunkDir.LengthSquared() > 1e-8f)
                {
                    trunkDir = Vector2.Normalize(trunkDir);
                    float tlen = Vector2.Distance(trunk[0], elbow);
                    // Pull trunk tip back by r so the junction can arc.
                    if (tlen > r * 1.5f)
                        trunk[^1] = trunk[0] + trunkDir * (tlen - r);
                    var trunkEnd = trunk[^1];

                    for (int bi = 1; bi < tree.Branches.Count; bi++)
                    {
                        var leaf = tree.Branches[bi];
                        if (leaf.ParentBranch != 0 || leaf.Centerline.Count < 2) continue;
                        var tip = leaf.Centerline[^1];
                        var dir = tip - elbow;
                        float len = dir.Length();
                        if (len < r * 1.2f) continue;
                        dir /= len;

                        // Arc from trunk approach direction to bar direction, radius r.
                        var rebuilt = new List<Vector2>();
                        // Start at trunk end (already pulled back).
                        rebuilt.Add(trunkEnd);
                        int segs = 6;
                        for (int s = 1; s <= segs; s++)
                        {
                            float t = s / (float)segs;
                            // Slerp-ish direction blend.
                            var d = Vector2.Normalize(Vector2.Lerp(trunkDir, dir, t));
                            if (d.LengthSquared() < 1e-8f) d = dir;
                            // Point on circular-ish blend: offset from elbow by r toward blended normal.
                            var along = Vector2.Lerp(trunkEnd, elbow + dir * r, t);
                            // Push off the sharp corner by radius along the angle bisector.
                            var bis = Vector2.Normalize(trunkDir + dir);
                            if (bis.LengthSquared() > 1e-8f)
                                along = elbow + bis * (r * MathF.Sin(t * MathF.PI * 0.5f))
                                      + dir * (r * t);
                            rebuilt.Add(along);
                        }
                        rebuilt.Add(tip);
                        leaf.Centerline.Clear();
                        leaf.Centerline.AddRange(SimplifyMinSpacing(rebuilt, bead * 0.35f));
                        // Leaf no longer starts at old elbow — parent node still trunk tip.
                        leaf.ParentNode = Math.Max(0, trunk.Count - 1);
                    }
                }
            }
        }

        foreach (var b in tree.Branches)
            FilletPolylineInPlace(b.Centerline, r);
    }

    private static void FilletPolylineInPlace(List<Vector2> line, float radius)
    {
        if (line.Count < 3) return;
        var src = new List<Vector2>(line);
        var dst = new List<Vector2> { src[0] };
        for (int i = 1; i < src.Count - 1; i++)
        {
            var a = src[i - 1];
            var b = src[i];
            var c = src[i + 1];
            var ba = a - b;
            var bc = c - b;
            float la = ba.Length();
            float lc = bc.Length();
            if (la < 1e-6f || lc < 1e-6f) { dst.Add(b); continue; }
            ba /= la; bc /= lc;
            float cos = Math.Clamp(Vector2.Dot(ba, bc), -1f, 1f);
            float ang = MathF.Acos(cos); // interior turning-related angle between -ba and bc...
            // Angle between incoming and outgoing directions (pi - corner angle).
            float turn = MathF.PI - ang;
            if (turn < 0.15f || float.IsNaN(turn)) { dst.Add(b); continue; } // nearly straight
            // Cut distance along each leg for fillet of radius R: R / tan(turn/2)
            float half = turn * 0.5f;
            float cut = radius / MathF.Max(MathF.Tan(half), 1e-3f);
            cut = MathF.Min(cut, MathF.Min(la, lc) * 0.45f);
            if (cut < radius * 0.15f) { dst.Add(b); continue; }

            var p0 = b + ba * cut; // along reverse incoming = toward a
            var p1 = b + bc * cut;
            // Arc from p0 to p1 around center offset along angle bisector.
            var bis = ba + bc;
            if (bis.LengthSquared() < 1e-10f) { dst.Add(b); continue; }
            bis = Vector2.Normalize(bis);
            // Center is along bisector from b; for convex corner from path, center is
            // inside the turn: from b along -bis? For path A→B→C, turn left means
            // center is left of BA. Use cross to pick side.
            float cross = ba.X * bc.Y - ba.Y * bc.X;
            var nIn = new Vector2(-ba.Y, ba.X); // left of incoming reverse... 
            // Incoming direction is -ba (from a to b). Left normal of incoming:
            var inDir = -ba;
            var leftN = new Vector2(-inDir.Y, inDir.X);
            // For a left turn (cross of inDir x outDir > 0), center is left.
            var outDir = bc;
            float crossIO = inDir.X * outDir.Y - inDir.Y * outDir.X;
            var toCenter = crossIO >= 0 ? leftN : -leftN;
            // Distance from corner to center: R / sin(turn/2)
            float dist = radius / MathF.Max(MathF.Sin(half), 1e-3f);
            // Better: center = p0 + leftNormal(inDir)*R (for left turn)
            var center = p0 + toCenter * radius;
            // Verify p1 is roughly on the circle; rebuild arc by angle.
            var v0 = p0 - center;
            var v1 = p1 - center;
            if (v0.LengthSquared() < 1e-8f || v1.LengthSquared() < 1e-8f) { dst.Add(b); continue; }
            float a0 = MathF.Atan2(v0.Y, v0.X);
            float a1 = MathF.Atan2(v1.Y, v1.X);
            float da = a1 - a0;
            // Sweep the short arc matching the turn direction.
            if (crossIO >= 0) { while (da < 0) da += MathF.PI * 2f; while (da > MathF.PI * 2f) da -= MathF.PI * 2f; }
            else { while (da > 0) da -= MathF.PI * 2f; while (da < -MathF.PI * 2f) da += MathF.PI * 2f; }
            int segs = Math.Max(3, (int)MathF.Ceiling(MathF.Abs(da) / (MathF.PI / 6f)));
            for (int s = 0; s <= segs; s++)
            {
                float t = s / (float)segs;
                float angS = a0 + da * t;
                dst.Add(center + new Vector2(MathF.Cos(angS), MathF.Sin(angS)) * radius);
            }
        }
        dst.Add(src[^1]);
        line.Clear();
        line.AddRange(SimplifyMinSpacing(dst, radius * 0.2f));
    }

    private static List<Vector2> SimplifyMinSpacing(List<Vector2> pts, float minDist)
    {
        if (pts.Count == 0) return pts;
        var outp = new List<Vector2> { pts[0] };
        float min2 = minDist * minDist;
        for (int i = 1; i < pts.Count; i++)
        {
            if (Vector2.DistanceSquared(outp[^1], pts[i]) >= min2 || i == pts.Count - 1)
            {
                if (i == pts.Count - 1 && outp.Count > 1
                    && Vector2.DistanceSquared(outp[^1], pts[i]) < min2 * 0.25f)
                    outp[^1] = pts[i];
                else
                    outp.Add(pts[i]);
            }
        }
        return outp;
    }

    /// <summary>Walk ±arc length along the sample ring from <paramref name="si"/>,
    /// staying inside the run window when possible, and project onto <paramref name="keep"/>.</summary>
    private static Vector2 WalkAlongRun(
        List<Vector2> samples, int si, int runStart, int runCount, float sampleStep,
        float signedArc, PathsD keep, float bead, bool external)
    {
        int n = samples.Count;
        if (n == 0) return default;
        int dir = signedArc >= 0f ? 1 : -1;
        float remaining = MathF.Abs(signedArc);
        int idx = si;
        var last = samples[si];

        // Prefer staying inside the unsupported run indices.
        bool InRun(int i)
        {
            for (int j = 0; j < runCount; j++)
                if ((runStart + j) % n == i) return true;
            return false;
        }

        while (remaining > 0.01f)
        {
            int next = (idx + dir + n * 4) % n;
            // Stop at run ends for finite runs (don't wrap into supported arc).
            if (runCount < n && !InRun(next))
                break;
            float seg = Vector2.Distance(samples[idx], samples[next]);
            if (seg < 1e-6f) { idx = next; continue; }
            if (seg >= remaining)
            {
                float t = remaining / seg;
                last = Vector2.Lerp(samples[idx], samples[next], t);
                break;
            }
            remaining -= seg;
            idx = next;
            last = samples[idx];
        }

        if (external) return last;
        if (InsideRegion(keep, last)) return last;
        return ClosestOnRegionBoundary(keep, last);
    }

    /// <summary>True when <paramref name="elbow"/> is within <paramref name="minDist"/>
    /// of another tree's trunk tip (the elbow of an existing T).</summary>
    private static bool TooCloseToElbow(List<LightningTree> trees, Vector2 elbow, float minDist)
    {
        float s2 = minDist * minDist;
        foreach (var t in trees)
        {
            if (t.Branches.Count == 0 || t.Branches[0].Centerline.Count < 2) continue;
            var other = t.Branches[0].Centerline[^1];
            if (Vector2.DistanceSquared(other, elbow) < s2) return true;
        }
        return false;
    }

    /// <summary>Removes up to <paramref name="step"/> of arc length from the tip of
    /// every leaf branch (a branch nothing grows from). Emptied branches are removed
    /// (their children were leaves and got retracted first by tree construction order).</summary>
    internal static void RetractLeafTips(LightningTree tree, float step)
    {
        // Work leaves-first: repeatedly retract branches that no other branch parents.
        bool removed = true;
        var retracted = new HashSet<int>();
        while (removed)
        {
            removed = false;
            for (int bi = tree.Branches.Count - 1; bi >= 0; bi--)
            {
                if (retracted.Contains(bi)) continue;
                bool isLeaf = true;
                for (int oj = 0; oj < tree.Branches.Count; oj++)
                    if (oj != bi && tree.Branches[oj].ParentBranch == bi) { isLeaf = false; break; }
                if (!isLeaf) continue;

                var line = tree.Branches[bi].Centerline;
                float remaining = step;
                while (line.Count >= 2 && remaining > 0f)
                {
                    float segLen = Vector2.Distance(line[^2], line[^1]);
                    if (segLen <= remaining + 1e-4f)
                    {
                        remaining -= segLen;
                        line.RemoveAt(line.Count - 1);
                    }
                    else
                    {
                        line[^1] = Vector2.Lerp(line[^1], line[^2], remaining / segLen);
                        remaining = 0f;
                    }
                }
                retracted.Add(bi);

                if (line.Count < 2)
                {
                    // Branch fully consumed — remove and re-index children/parents.
                    tree.Branches.RemoveAt(bi);
                    retracted = new HashSet<int>(retracted.Where(x => x != bi)
                        .Select(x => x > bi ? x - 1 : x));
                    for (int oj = 0; oj < tree.Branches.Count; oj++)
                    {
                        var o = tree.Branches[oj];
                        if (o.ParentBranch == bi) { o.ParentBranch = -1; o.Centerline.Insert(0, tree.Anchor); }
                        else if (o.ParentBranch > bi) o.ParentBranch--;
                    }
                    removed = true;   // a parent may have become a leaf — loop again
                    break;
                }
            }
        }
    }

    /// <summary>Rescues nodes that fell OUTSIDE the region (the shape changed under
    /// them — no material there) by pulling them to the core boundary; a node that
    /// would need to move farther than <paramref name="maxLateral"/> trims the branch
    /// there. Nodes inside the region are never touched — a retracting tip must be
    /// free to pass through the boundary band on its way to disappearing.</summary>
    private static void ClampInside(LightningTree tree, PathsD region, PathsD core, float maxLateral)
    {
        for (int bi = tree.Branches.Count - 1; bi >= 0; bi--)
        {
            var line = tree.Branches[bi].Centerline;
            // Node 0 of a root branch is the anchor — it legitimately sits ON the
            // region boundary and is re-projected separately.
            int firstNode = tree.Branches[bi].ParentBranch < 0 ? 1 : 0;
            for (int ni = firstNode; ni < line.Count; ni++)
            {
                if (InsideRegion(region, line[ni])) continue;
                var pulled = ClosestOnRegionBoundary(core, line[ni]);
                if (Vector2.Distance(pulled, line[ni]) <= maxLateral + 1e-3f)
                {
                    line[ni] = pulled;
                }
                else
                {
                    line.RemoveRange(ni, line.Count - ni);
                    break;
                }
            }
            if (line.Count < 2)
                tree.Branches.RemoveAt(bi);   // children keep raw junction points; harmless for v2 scale
        }
    }

    private static void Straighten(List<Vector2> line, float budget, PathsD core)
    {
        if (line.Count < 3 || budget <= 0f) return;
        var a = line[0];
        var b = line[^1];
        for (int i = 1; i < line.Count - 1; i++)
        {
            float t = i / (float)(line.Count - 1);
            var target = Vector2.Lerp(a, b, t);
            var delta  = target - line[i];
            float d    = delta.Length();
            var moved  = d <= budget ? target : line[i] + delta * (budget / d);
            if (InsideRegion(core, moved)) line[i] = moved;
        }
    }

    /// <summary>True when <paramref name="p"/> lies within <paramref name="radius"/> of
    /// any planned centerline segment (a finger already supports this spot).</summary>
    private static bool NearAnyCenterline(List<LightningTree> trees, Vector2 p, float radius)
    {
        float r2 = radius * radius;
        foreach (var t in trees)
            foreach (var b in t.Branches)
            {
                var line = b.Centerline;
                for (int i = 1; i < line.Count; i++)
                    if (DistToSegmentSq(p, line[i - 1], line[i]) < r2)
                        return true;
            }
        return false;
    }

    private static float DistToSegmentSq(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-12f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        var c = a + ab * t;
        return Vector2.DistanceSquared(p, c);
    }

    private static bool TooCloseToExisting(List<LightningTree> trees, Vector2 p, float spacing)
    {
        float s2 = spacing * spacing;
        foreach (var t in trees)
            foreach (var b in t.Branches)
                if (Vector2.DistanceSquared(b.Centerline[^1], p) < s2)
                    return true;
        return false;
    }

    private static (LightningTree? Tree, int Branch, int Node, float Dist) NearestCenterlineNode(
        List<LightningTree> trees, Vector2 p)
    {
        LightningTree? best = null; int bb = 0, bn = 0; float bd = float.MaxValue;
        foreach (var t in trees)
            for (int bi = 0; bi < t.Branches.Count; bi++)
            {
                var line = t.Branches[bi].Centerline;
                for (int ni = 0; ni < line.Count; ni++)
                {
                    float d = Vector2.Distance(line[ni], p);
                    if (d < bd) { bd = d; best = t; bb = bi; bn = ni; }
                }
            }
        return (best, bb, bn, bd);
    }

    // -- Region geometry helpers ---------------------------------------------------

    internal static PathsD ToPathsD(List<List<Vector2>> polys, float beadWidth = 0f)
    {
        // Real-world meshes are often double-shelled (every surface duplicated by the
        // CAD export), which slices every contour twice with arbitrary windings. A raw
        // NonZero union of such pairs fills hollow interiors and swallows inner walls
        // (fuselage bug, 2026-07-09), so: (1) drop near-coincident duplicate contours,
        // (2) re-orient the survivors by nesting parity, (3) union.
        // Twin test: a contour whose every vertex lies ON an already-kept contour's
        // curve (within the coincidence tolerance) is the duplicate shell's copy —
        // regardless of how its chain was split, reversed, or re-stitched. Largest
        // first, so a full twin absorbs the split pieces of its counterpart.
        const double twinTol = 0.15;
        var candidates = new List<PathD>();
        foreach (var poly in polys)
        {
            if (poly.Count < 3) continue;
            var path = new PathD(poly.Count);
            foreach (var pt in poly) path.Add(new PointD(pt.X, pt.Y));
            candidates.Add(path);
        }
        candidates.Sort((x, y) => Math.Abs(Clipper.Area(y)).CompareTo(Math.Abs(Clipper.Area(x))));

        var kept = new List<PathD>();
        var keptBounds = new List<RectD>();
        foreach (var path in candidates)
        {
            var b = Clipper.GetBounds(new PathsD { path });
            bool dup = false;
            for (int i = 0; i < kept.Count && !dup; i++)
            {
                if (b.left < keptBounds[i].left - twinTol * 4 || b.right > keptBounds[i].right + twinTol * 4
                    || b.top < keptBounds[i].top - twinTol * 4 || b.bottom > keptBounds[i].bottom + twinTol * 4)
                    continue;
                dup = LiesOnCurve(path, kept[i], twinTol);
            }
            if (dup) continue;
            kept.Add(path);
            keptBounds.Add(b);
        }

        // Orphan holes: a CW contour hosted by NO outer is tangent-band junk — the
        // rim curve of a pocket whose outer wall collapsed at this plane (grazing
        // cut). The parity union below would flip it into a phantom SOLID island,
        // which then gets printed as a wall and grows support fingers under it
        // (Drone V52 bug, 2026-07-09: a 10,000 mm² phantom seeded a 70-layer ladder
        // of bridging under geometry that doesn't exist). Real holes always sit
        // inside an outer; anything fully outside every outer is dropped. Vertices
        // ON an outer's curve count as hosted (twin-tolerance safety).
        // NOTE (2026-07-09): grazing-cut phantom islands (a pocket rim whose outer
        // wall collapsed at this plane) CANNOT be told apart from real geometry
        // here — parity-composed layers (fuselage half-loops) make orientation and
        // containment both unreliable. They are handled cross-layer instead: the
        // planner's persistence veto refuses to grow fingers under solids that
        // vanish within a couple of layers.

        // Parity (EvenOdd) union: winding-agnostic, so corrupted contour orientations
        // don't matter, and doubly-covered areas cancel. That is the physically right
        // reading of messy real-world slices — e.g. a hollow part whose intersection
        // chains split into two half-loops that each enclose the shared cavity: the
        // cavity is exactly their overlap and must be a hole, where a NonZero union
        // would fill it and erase its walls from the toolpath.
        var paths = new PathsD(kept);
        var region = Clipper.Union(paths, FillRule.EvenOdd);

        // Parity punches a small hole wherever tangent-band junk fragments overlap
        // the wall — each one otherwise emits a stub loop (a diamond-lattice artifact
        // in the bead preview). A bead physically fuses any opening smaller than a
        // couple of bead widths shut, so drop sub-(2×bead)² holes. Material is only
        // ever ADDED by this, never removed.
        if (beadWidth > 0f)
            region.RemoveAll(path => Clipper.Area(path) < 0
                && Math.Abs(Clipper.Area(path)) < 4.0 * beadWidth * beadWidth);
        return region;
    }


    /// <summary>True when the whole segment stays inside the region (or within a
    /// bead of its boundary) — a finger centerline that strays farther is crossing a
    /// void (e.g. chord across a ring's hole) and cannot be realized as a slit.</summary>
    internal static bool SegmentInsideRegion(PathsD region, Vector2 a, Vector2 b, float bead)
    {
        float len = Vector2.Distance(a, b);
        int steps = Math.Max(2, (int)(len / MathF.Max(bead, 0.5f)) + 1);
        for (int i = 0; i <= steps; i++)
        {
            var pt = Vector2.Lerp(a, b, i / (float)steps);
            if (InsideRegion(region, pt)) continue;
            if (Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt) > bead * 0.6f)
                return false;
        }
        return true;
    }

    /// <summary>True when every vertex of <paramref name="path"/> lies within
    /// <paramref name="tol"/> of <paramref name="curve"/>'s polyline.</summary>
    internal static bool LiesOnCurve(PathD path, PathD curve, double tol)
    {
        double tol2 = tol * tol;
        foreach (var v in path)
        {
            double best = double.MaxValue;
            for (int i = 0; i < curve.Count; i++)
            {
                var a = curve[i];
                var b = curve[(i + 1) % curve.Count];
                double abx = b.x - a.x, aby = b.y - a.y;
                double len2 = abx * abx + aby * aby;
                double t = len2 < 1e-12 ? 0 : Math.Clamp(((v.x - a.x) * abx + (v.y - a.y) * aby) / len2, 0, 1);
                double dx = v.x - (a.x + abx * t), dy = v.y - (a.y + aby * t);
                double d2 = dx * dx + dy * dy;
                if (d2 < best) best = d2;
                if (best < tol2) break;
            }
            if (best >= tol2) return false;
        }
        return true;
    }

    private static double Sq(double v) => v * v;

    private static PointD PathCentroid(PathD path)
    {
        double x = 0, y = 0;
        foreach (var pt in path) { x += pt.x; y += pt.y; }
        return new PointD(x / path.Count, y / path.Count);
    }

    /// <summary>Even-odd containment across all region paths (outer + holes).</summary>
    internal static bool InsideRegion(PathsD region, Vector2 p)
    {
        var pt = new PointD(p.X, p.Y);
        int containing = 0;
        foreach (var path in region)
            if (Clipper.PointInPolygon(pt, path) == PointInPolygonResult.IsInside)
                containing++;
        return (containing & 1) == 1;
    }

    /// <summary>Closest point on any boundary edge of the region.</summary>
    internal static Vector2 ClosestOnRegionBoundary(PathsD region, Vector2 p)
    {
        var best  = p;
        float bd2 = float.MaxValue;
        foreach (var path in region)
        {
            int cnt = path.Count;
            for (int i = 0; i < cnt; i++)
            {
                var a = path[i];
                var b = path[(i + 1) % cnt];
                float ax = (float)a.x, ay = (float)a.y;
                float dx = (float)b.x - ax, dy = (float)b.y - ay;
                float len2 = dx * dx + dy * dy;
                float t = len2 < 1e-12f ? 0f
                    : Math.Clamp(((p.X - ax) * dx + (p.Y - ay) * dy) / len2, 0f, 1f);
                float cx = ax + t * dx, cy = ay + t * dy;
                float d2 = (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy);
                if (d2 < bd2) { bd2 = d2; best = new Vector2(cx, cy); }
            }
        }
        return best;
    }

    /// <summary>Evenly spaced sample points along one closed boundary path, in order.</summary>
    internal static List<Vector2> SamplePath(PathD path, float step)
    {
        step = MathF.Max(step, 0.5f);
        var samples = new List<Vector2>();
        int cnt = path.Count;
        if (cnt < 3) return samples;
        float carry = 0f;
        for (int i = 0; i < cnt; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % cnt];
            float ax = (float)a.x, ay = (float)a.y;
            float bx = (float)b.x, by = (float)b.y;
            float segLen = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            float d = carry;
            while (d < segLen)
            {
                float t = d / segLen;
                samples.Add(new Vector2(ax + (bx - ax) * t, ay + (by - ay) * t));
                d += step;
            }
            carry = d - segLen;
        }
        return samples;
    }

    /// <summary>Maximal circular runs of consecutive true flags: (startIndex, length).</summary>
    internal static IEnumerable<(int Start, int Count)> CircularRuns(bool[] flags)
    {
        int n = flags.Length;
        if (n == 0) yield break;
        if (flags.All(f => f)) { yield return (0, n); yield break; }

        // Start scanning just after a false so runs never split across the wrap.
        int origin = Array.IndexOf(flags, false);
        int i = 0;
        while (i < n)
        {
            int idx = (origin + i) % n;
            if (!flags[idx]) { i++; continue; }
            int start = idx, len = 0;
            while (i < n && flags[(origin + i) % n]) { len++; i++; }
            yield return (start, len);
        }
    }
}
