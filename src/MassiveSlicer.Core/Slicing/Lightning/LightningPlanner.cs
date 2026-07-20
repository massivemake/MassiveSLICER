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
        IReadOnlyList<ManualDemandLayer>? manualDemand = null)
    {
        int n = fillPolysPerLayer.Count;
        var plan = new LightningPlan(n);
        if (n == 0) return plan;

        if (settings.LightningTargetSupportSelections)
        {
            int bridgeMarks = 0;
            foreach (var m in settings.PaintMarks)
                if (m.Kind == PaintMarkKind.Bridge) bridgeMarks++;
            bool anyManual = false;
            if (manualDemand is not null)
                for (int mi = 0; mi < manualDemand.Count && !anyManual; mi++)
                    anyManual = manualDemand[mi].HasAny;
            System.Console.WriteLine(
                $"[formbound] Target Support Selections ON — auto demand OFF; " +
                $"bridgeMarks={bridgeMarks} manualDemandLayers={(anyManual ? "yes" : "none")}");
            if (bridgeMarks == 0 && !anyManual)
            {
                plan.UncoveredLog.Add(
                    "[formbound] Target Support Selections: no Support paint marks — " +
                    "select paths in Edit → Support/Apply, then reslice.");
                System.Console.WriteLine(plan.UncoveredLog[^1]);
            }
        }

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
        int demandFlags = 0;
        int meshVetoes = 0;
        int uncoveredTotal = 0;
        PfNoAnchor = PfBarReach = PfNoFrame = PfCovered = PfElbow = 0;
        int inheritSkips = 0;
        int inheritReseeds = 0;
        int auditExtensions = 0;
        // Painted Bridge marks mean the user is steering support — do NOT also
        // birth automatic geometric buttresses (those look like random start/stop
        // columns away from the selection).
        bool hasManualPaint = false;
        if (manualDemand is not null)
            for (int mi = 0; mi < manualDemand.Count && !hasManualPaint; mi++)
                if (manualDemand[mi].HasAny) hasManualPaint = true;
        // The severity-aware Bridge machinery (severity-scaled cover radius, the
        // residual worst-first pass, and the free-edge exterior re-birth pass) is
        // only engaged for the Target-Support-Selections feature or when the user
        // has painted Support marks. On the default automatic path it changed
        // finger density enough to notch perimeters and trail exterior fins to the
        // bed, so keep the classic fixed-radius behavior there.
        bool severityAware = hasManualPaint || settings.LightningTargetSupportSelections;
        // Exactly one paint-driven perimeter mouth for the whole stack.
        int? paintColumnId = null;
        // Fixed seam pin (bridge target / column mouth XY). Copied onto every layer
        // after the walk so EmitLoops starts the perimeter at the same place as the
        // buttress opens, layer after layer.
        Vector2? paintSeamPin = null;
        // Stack-level bridge aims (native layer frames). Column births under the
        // SupportBar mid, then slides along Lerp(foot, bar, t) so intermediate
        // layers interpolate Anchor → Target instead of staying under the bar.
        Vector2? paintBarMid = null;
        int paintBarLayer = -1;
        Vector2? paintFootMid = null;
        int paintFootLayer = -1;
        if (manualDemand is not null)
        {
            for (int mi = 0; mi < manualDemand.Count; mi++)
            {
                var dem0 = manualDemand[mi];
                if (dem0.SupportBar.Count > 0)
                {
                    var br = OrderDemandRun(dem0.SupportBar, p => p);
                    if (br.Count > 0)
                    {
                        paintBarMid = br[MidIndexAlongRun(br)];
                        paintBarLayer = mi;
                    }
                }
                if (dem0.ColumnFoot.Count > 0)
                {
                    var fr = OrderDemandRun(dem0.ColumnFoot, p => p);
                    if (fr.Count > 0)
                    {
                        paintFootMid = fr[MidIndexAlongRun(fr)];
                        paintFootLayer = mi;
                        paintSeamPin = paintFootMid; // prefer target for seam
                    }
                }
            }
            if (paintBarMid is { } bm0 && paintFootMid is { } fm0)
            {
                System.Console.WriteLine(
                    $"[formbound] paint-bridge aims bar=L{paintBarLayer + 1}({bm0.X:0.#},{bm0.Y:0.#}) " +
                    $"foot=L{paintFootLayer + 1}({fm0.X:0.#},{fm0.Y:0.#})");
            }
            else if (paintBarMid is { } bm1)
            {
                System.Console.WriteLine(
                    $"[formbound] paint-bridge aims bar=L{paintBarLayer + 1}({bm1.X:0.#},{bm1.Y:0.#}) (no foot)");
            }
            else if (paintFootMid is { } fm1)
            {
                System.Console.WriteLine(
                    $"[formbound] paint-bridge aims foot=L{paintFootLayer + 1}({fm1.X:0.#},{fm1.Y:0.#}) (no bar)");
            }
        }
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
                if (Environment.GetEnvironmentVariable("MSL_ORPHAN_DEBUG") == "1"
                    && plan.Layers[i + 1].Trees.Count > 0)
                    System.Console.WriteLine(
                        $"[orphan] layer={i} EMPTY-REGION kills {plan.Layers[i + 1].Trees.Count} tree(s)");
                continue;
            }

            // Region shrunk by one bead — finger nodes must stay at least a bead
            // inside so the slit walls never poke through the perimeter.
            // Multi-planar wedges are often thinner than a full bead: a hard empty-core
            // orphan wiped every tree (liveSlots=0 with treesBorn>0). Fall back to a
            // shallower inset, then the region itself, before giving up.
            var envelope = BuildEnvelope(region);
            var core = Clipper.InflatePaths(region, -bead, JoinType.Miter, EndType.Polygon, 3.0);
            if (core.Count == 0)
                core = Clipper.InflatePaths(region, -bead * 0.35, JoinType.Miter, EndType.Polygon, 3.0);
            if (core.Count == 0)
                core = region;
            if (core.Count == 0)
            {
                // Truly empty region (should be rare — region.Count was > 0 above).
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
            //
            // Target Support Selections: mouths must still root on the wall even when
            // Affect Interior/Exterior are both off (those gates only auto-demand).
            bool targetSelOnly = settings.LightningTargetSupportSelections;
            var anchorInterior = new PathsD();
            var anchorExterior = new PathsD();
            foreach (var path in region)
            {
                bool isOuter = Clipper.Area(path) > 0;
                if (isOuter)
                {
                    if (settings.LightningAnchorExterior || targetSelOnly)
                        anchorExterior.Add(path);
                }
                else if (settings.LightningAnchorInterior || targetSelOnly)
                    anchorInterior.Add(path);
            }
            var anchorPaths = new PathsD();
            if (buttress && preferInterior && anchorInterior.Count > 0 && !targetSelOnly)
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
            if (anchorPaths.Count == 0 && targetSelOnly)
            {
                // Last resort: any contour so paint-driven mouths can still land.
                foreach (var path in region)
                    anchorPaths.Add(path);
            }
            if (anchorPaths.Count == 0)
            {
                // No legal mouth on this plane — columns above have nowhere to land.
                if (Environment.GetEnvironmentVariable("MSL_ORPHAN_DEBUG") == "1"
                    && plan.Layers[i + 1].Trees.Count > 0)
                    System.Console.WriteLine(
                        $"[orphan] layer={i} NO-ANCHOR kills {plan.Layers[i + 1].Trees.Count} tree(s)");
                foreach (var t in plan.Layers[i + 1].Trees)
                    orphaned.Add(t.Id);
                continue;
            }

            // Frame remap (identity on constant-frame stacks): lift the plane-local
            // point to world in the source layer's frame, project into the target's.
            // Used for INHERITED tree geometry (stack continuity under MaxStep).
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

            // World-vertical projection of an upper plane-local point onto THIS
            // plane. Plane-normal remapping cancels the tilt's world stair-step:
            // on the down-tilt side an upper layer edge can overhang in GRAVITY
            // while looking perfectly stacked in-plane, so demand never fired
            // there (one wing grew fins, its mirror stayed bare). Support must
            // also exist under the world landing point.
            Vector2 DownGravity(Vector2 p)
            {
                if (frames is null) return p;
                var fa = frames[i + 1];
                var fb = frames[i];
                var w = fa.Origin + fa.U * p.X + fa.V * p.Y;
                var n = Vector3.Normalize(Vector3.Cross(fb.U, fb.V));
                if (MathF.Abs(n.Z) < 0.2f) return Remap(i + 1, i, p);
                float t = Vector3.Dot(n, w - fb.Origin) / n.Z;
                var g = w - new Vector3(0f, 0f, t);
                var rel = g - fb.Origin;
                return new Vector2(Vector3.Dot(rel, fb.U), Vector3.Dot(rel, fb.V));
            }
            bool GravityFar(PathsD? fp, Vector2 ptG) => fp is not null
                && (InsideRegion(fp, ptG)
                    || Vector2.Distance(ClosestOnRegionBoundary(fp, ptG), ptG) < bead * 0.6f);

            // ── 1. Inherit the layer-above's trees with retracted tips ─────────
            float stepAbove = MaxStep(i + 1);
            // Sacrificial external fins lean at the physical bead-on-bead limit —
            // half a bead of offset per layer — instead of the shallower
            // surface-quality overhang angle. They peel off the perimeter close
            // under the overhang rather than trailing a sail down to the bed.
            float stepAboveExternal = MathF.Max(stepAbove, 0.5f * bead);
            // Multi-planar: ALWAYS snap re-root and continue the same lineage.
            // Skip+short-reseed left mid-wall gaps; snap + RetractButtress grows ≤ MaxStep.
            foreach (var above in plan.Layers[i + 1].Trees)
            {
                // Target Support Selections: never inherit automatic / non-paint
                // trees — those were the "buttress all over the surface" cascade.
                // Only Manual / PaintColumn lineages (and optional connectors) continue.
                if (targetSelOnly
                    && !above.Manual
                    && !above.PaintColumn
                    && !above.Connector)
                    continue;
                // Selection mode: skip island-connector umbrellas (not user support).
                if (targetSelOnly && above.Connector && !above.Manual && !above.PaintColumn)
                    continue;

                var t = above.Clone();

                if (frames is not null)
                {
                    t.Anchor = Down(t.Anchor);
                    foreach (var b in t.Branches)
                        for (int k = 0; k < b.Centerline.Count; k++)
                            b.Centerline[k] = Down(b.Centerline[k]);
                }

                // Re-root onto this layer's wall.
                // Planar: large jump → island gone → orphan the WHOLE column (all layers).
                // Multi-planar: UV drift often exceeds a few beads even with a live wall —
                // always snap and continue (never skip this layer / never birth a reseed).
                var reAnchor = ClosestOnRegionBoundary(
                    t.External || t.Cavity ? region : anchorPaths, t.Anchor);
                float reDist = Vector2.Distance(reAnchor, t.Anchor);
                float reTol = frames is not null
                    ? MathF.Max(14f * bead, 10f * stepAbove)
                    : MathF.Max(4f * bead, 3f * stepAbove);
                // Paint columns must track a moving aim XY across layers — never
                // orphan for a large re-root (same as multi-planar snap).
                bool isPaintCol = t.PaintColumn
                    || (paintColumnId is int pcid && t.Id == pcid);
                if (reDist > reTol)
                {
                    if (frames is null && !isPaintCol)
                    {
                        if (Environment.GetEnvironmentVariable("MSL_ORPHAN_DEBUG") == "1")
                            System.Console.WriteLine(
                                $"[orphan] layer={i} REROOT-JUMP tree={t.Id} dist={reDist:0.#} tol={reTol:0.#}");
                        orphaned.Add(t.Id);
                        continue;
                    }
                    inheritSkips++; // forced snap (column continues)
                }
                if (isPaintCol)
                {
                    t.PaintColumn = true;
                    paintColumnId = t.Id;
                }

                t.Anchor = reAnchor;
                if (t.Branches.Count > 0 && t.Branches[0].Centerline.Count > 0)
                    t.Branches[0].Centerline[0] = reAnchor;

                // Top-down retract = bottom-up growth at ≤ MaxStep per layer
                // (layerH·tan(overhang), capped). Buttress must retract TRUNK + bars
                // so the T builds out from the wall gradually — leaf-only retract
                // left full-depth trunks ("straight out to a T").
                float step = t.External ? stepAboveExternal : stepAbove;
                bool holdConnector = t.Connector && CountOuterPaths(region) > 1;
                if (!holdConnector)
                {
                    if (buttress && !t.External)
                        RetractButtress(t, step, bead);
                    else
                        RetractLeafTips(t, step);
                }
                if (t.Branches.Count == 0) continue;

                // Tips AFTER retract — re-aim must not grow past these (MaxStep).
                var prevTips = SnapshotTips(t);

                if (!t.External && !t.Cavity)
                    ClampInside(t, region, core, MaxStep(i));

                // Multi-planar: re-orient wall normal / tangent WITHOUT lengthening
                // past retracted tips. Never force full barLen trunk depth.
                if (buttress && !t.External && !t.Cavity && frames is not null)
                {
                    if (!ReAimButtress(t, region, core, bead, barLen, MaxStep(i), prevTips))
                    {
                        // Same lineage: MaxStep stub from the snapped wall (not a new tree id).
                        if (!TryRebuildShortButtress(t, region, core, bead, barLen, MaxStep(i)))
                            continue;
                        inheritReseeds++;
                    }
                }
                else if (t.Branches.Count > 0 && t.Branches[0].Centerline.Count > 0)
                    t.Branches[0].Centerline[0] = t.Anchor;

                // Round every junction so dual-wall slots never form sub-bead corners
                // (acute elbows → over-extrusion on the perimeter path).
                FilletTreeCorners(t, bead);

                if (t.Branches.Count > 0 && t.Branches[0].Centerline.Count > 0)
                {
                    t.Branches[0].Centerline[0] = t.Anchor;

                    // Void-crossing trunk: planar → orphan column; multi-planar → rebuild
                    // same lineage (keep continuous column; do not leave a mid-wall hole).
                    bool crossesVoid = false;
                    if (!t.External && !t.Cavity)
                        foreach (var b in t.Branches)
                        {
                            for (int k = 1; k < b.Centerline.Count && !crossesVoid; k++)
                                crossesVoid = !SegmentInsideRegion(
                                    region, b.Centerline[k - 1], b.Centerline[k], bead);
                            if (crossesVoid) break;
                        }
                    else if (t.Cavity)
                        foreach (var b in t.Branches)
                        {
                            for (int k = 1; k < b.Centerline.Count && !crossesVoid; k++)
                                crossesVoid = !SegmentInsideVoid(
                                    region, b.Centerline[k - 1], b.Centerline[k], bead * 1.5f);
                            if (crossesVoid) break;
                        }
                    if (crossesVoid)
                    {
                        // Cavity trunks: the hole drifts between (tilted) layers and
                        // the inherited line can clip the wall band. RE-SEAT the
                        // trunk from the snapped anchor — shorten toward a MaxStep
                        // stub until it sits inside the void again — instead of
                        // retiring the whole column mid-air (826 of 859 orphans on
                        // the V85 angled stack were this exact kill; the column's
                        // support vanished below and the marked line stayed afloat).
                        if (t.Cavity && (t.Manual || t.PaintColumn)
                            && TryReseatCavityTrunk(t, region, bead, MaxStep(i)))
                        {
                            FilletTreeCorners(t, bead);
                            inheritReseeds++;
                        }
                        else if (frames is not null && buttress
                            && TryRebuildShortButtress(t, region, core, bead, barLen, MaxStep(i)))
                        {
                            FilletTreeCorners(t, bead);
                            inheritReseeds++;
                        }
                        else if (frames is not null)
                        {
                            // Last resort: keep upper prints; rare when snap has no wall bite.
                            continue;
                        }
                        else
                        {
                            if (Environment.GetEnvironmentVariable("MSL_ORPHAN_DEBUG") == "1")
                                System.Console.WriteLine(
                                    $"[orphan] layer={i} VOID-CROSS tree={t.Id} ext={t.External} cav={t.Cavity} " +
                                    $"anchor=({t.Anchor.X:0},{t.Anchor.Y:0})");
                            orphaned.Add(t.Id);
                            continue;
                        }
                    }

                    layerPlan.Trees.Add(t);
                }
            }

            // ── 2. New demand ─────────────────────────────────────────────────
            // Planar: upper boundary farther from this wall than supportRadius
            //   (classic silhouette, same UV frame).
            // Multi-planar: upper fill remapped into this frame, minus this region
            //   inflated by supportRadius. The leftover footprint is real
            //   unsupported solid (ledges / closing roofs). World-XY edge distance
            //   falsely flags every tilted-cylinder ellipse shift; pure Z-project
            //   zeros demand on continuous shells. Clipper difference is the fix.
            //
            // When the user has painted Bridge marks, skip automatic geometric
            // births entirely — only inherit + manual demand (section 2b).
            float supportRadius = stepAbove + bead * 0.5f;
            // Demand detection honors the user's Max-overhang angle. supportRadius
            // (half-bead slack) is the PRINTABILITY limit — a bead can ride on half
            // the bead below — but on long flares that sag accumulates; with wide
            // beads it silently raised the support threshold to ~60° and the fins
            // stopped short of the steep band the user asked to support. Only a
            // small noise slack on top of the angle-derived step stays.
            float demandRadius = MathF.Min(supportRadius, stepAbove + bead * 0.1f);
            // Slightly denser than legacy spacing/4 so short free-edge lobes register,
            // without exploding sample count (SamplePath floors at 0.5 mm).
            // Denser sampling helps the Target-Support-Selections feature resolve
            // fine painted runs, but on the default automatic path it lengthens
            // "unsupported" runs and shifts mouth/tip placement enough that merged
            // slits notch the perimeter (fast-reversing multi-planar stacks). Keep
            // the classic pitch unless the feature is engaged.
            float sampleStep = severityAware
                ? MathF.Min(spacing * 0.25f, MathF.Max(1.25f, bead * 0.45f))
                : spacing * 0.25f;

            // Manual marks are ADDITIVE by default: they pin support where the user
            // demands it, while automatic geometric demand covers the rest.
            // "Target Support Selections" turns that off — only edit-mode Support
            // paint marks (Bridge) drive Formbound; skip all auto overhang detection.
            if (settings.LightningTargetSupportSelections)
                goto ManualDemandOnly;

            // Multi-planar: precompute "upper solid not covered by lower wall band".
            PathsD? multiDemandFootprint = null;
            PathsD? multiExtendFootprint = null;
            PathsD? gravityDemandFootprint = null;
            PathsD? gravityExtendFootprint = null;
            if (frames is not null)
            {
                var upperRemapped = new PathsD();
                var upperGravity = new PathsD();
                foreach (var up in regions[i + 1])
                {
                    if (up.Count < 3) continue;
                    var rp = new PathD(up.Count);
                    var rg = new PathD(up.Count);
                    foreach (var pt in up)
                    {
                        var d = Down(new Vector2((float)pt.x, (float)pt.y));
                        rp.Add(new PointD(d.X, d.Y));
                        var dg = DownGravity(new Vector2((float)pt.x, (float)pt.y));
                        rg.Add(new PointD(dg.X, dg.Y));
                    }
                    if (rp.Count >= 3) upperRemapped.Add(rp);
                    if (rg.Count >= 3) upperGravity.Add(rg);
                }
                if (upperRemapped.Count > 0)
                {
                    var lowerBand = Clipper.InflatePaths(region, supportRadius,
                        JoinType.Miter, EndType.Polygon, 3.0);
                    multiDemandFootprint = Clipper.Difference(upperRemapped, lowerBand, FillRule.NonZero);
                    // Drop speckles smaller than a bead².
                    multiDemandFootprint.RemoveAll(p => Math.Abs(Clipper.Area(p)) < bead * bead);
                    if (multiDemandFootprint.Count == 0)
                        multiDemandFootprint = null;
                    var extendBand = Clipper.InflatePaths(region, demandRadius,
                        JoinType.Miter, EndType.Polygon, 3.0);
                    multiExtendFootprint = Clipper.Difference(upperRemapped, extendBand, FillRule.NonZero);
                    multiExtendFootprint.RemoveAll(p => Math.Abs(Clipper.Area(p)) < bead * bead);
                    if (multiExtendFootprint.Count == 0)
                        multiExtendFootprint = null;
                    if (upperGravity.Count > 0)
                    {
                        gravityDemandFootprint = Clipper.Difference(upperGravity, lowerBand, FillRule.NonZero);
                        gravityDemandFootprint.RemoveAll(p => Math.Abs(Clipper.Area(p)) < bead * bead);
                        if (gravityDemandFootprint.Count == 0) gravityDemandFootprint = null;
                        gravityExtendFootprint = Clipper.Difference(upperGravity, extendBand, FillRule.NonZero);
                        gravityExtendFootprint.RemoveAll(p => Math.Abs(Clipper.Area(p)) < bead * bead);
                        if (gravityExtendFootprint.Count == 0) gravityExtendFootprint = null;
                    }
                }
            }

            foreach (var path in regions[i + 1])
            {
                // A boundary smaller than a couple of beads is unprintable junk —
                // never worth a finger (grazing-cut specks at the very top).
                if (Math.Abs(Clipper.Area(path)) < 4.0 * bead * bead) continue;

                var rawSamples = SamplePath(path, sampleStep);
                if (rawSamples.Count == 0) continue;
                // Placement frame: UV-remap upper → this plane (elbow lives in region).
                var samples = new List<Vector2>(rawSamples.Count);
                if (frames is not null)
                {
                    for (int k = 0; k < rawSamples.Count; k++)
                        samples.Add(Down(rawSamples[k]));
                }
                else
                {
                    samples.AddRange(rawSamples);
                }
                List<Vector2>? samplesG = null;
                if (frames is not null && gravityDemandFootprint is not null)
                {
                    samplesG = new List<Vector2>(rawSamples.Count);
                    for (int k = 0; k < rawSamples.Count; k++)
                        samplesG.Add(DownGravity(rawSamples[k]));
                }

                float coverRadius = buttress
                    ? MathF.Max(bead * 0.75f, supportRadius * 0.3f)
                    : supportRadius;
                float sameSideMax = MathF.Max(6f * bead, barLen);

                var unsupported = new bool[samples.Count];
                var severities = new float[samples.Count];
                for (int si = 0; si < samples.Count; si++)
                {
                    var pt = samples[si];
                    bool far;
                    if (multiDemandFootprint is not null)
                    {
                        far = InsideRegion(multiDemandFootprint, pt)
                              || Vector2.Distance(ClosestOnRegionBoundary(multiDemandFootprint, pt), pt)
                                 < bead * 0.6f;
                    }
                    else
                    {
                        far = Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt) > supportRadius;
                    }
                    if (!far && samplesG is not null
                        && GravityFar(gravityDemandFootprint, samplesG[si]))
                    {
                        far = true;
                        pt = samples[si] = samplesG[si];
                    }
                    var space = ClassifyPoint(region, envelope, pt);
                    float severity = far
                        ? DemandSeverity(pt, region, multiDemandFootprint, supportRadius, bead)
                        : 0f;
                    severities[si] = severity;
                    // Affect Interior (inner + cavity) vs Affect Exterior (outer flares).
                    bool domainOk = space == DemandSpace.Exterior
                        ? settings.LightningExteriorOverhangs
                        : settings.LightningAnchorInterior;
                    // Bridge: severity-aware cover so mid-wall fingers do not suppress
                    // free-edge demand. Buttress keeps same-side cover.
                    bool covered = buttress
                        ? CoveredBySameSide(layerPlan.Trees, pt, coverRadius, sameSideMax, region)
                        : NearAnyCenterline(layerPlan.Trees, pt,
                            severityAware
                                ? CoverRadiusForSeverity(severity, supportRadius, bead)
                                : coverRadius);
                    unsupported[si] = far && domainOk && !covered;
                    if (unsupported[si]) demandFlags++;
                }

                foreach (var (start, count) in CircularRuns(unsupported))
                {
                    float runLen = count * sampleStep;
                    if (buttress)
                    {
                        float barPitch = MathF.Max(bead * 2f, MathF.Min(spacing, barLen * 0.5f));
                        float adaptiveBar = MathF.Max(barLen, barPitch * 2.2f);
                        int barCount = Math.Max(1, (int)MathF.Ceiling(runLen / barPitch - 1e-4f));

                        for (int k = 0; k < barCount; k++)
                        {
                            int si = (start + (int)((k + 0.5f) * count / barCount)) % samples.Count;
                            if (!TryAddButtressAt(samples, si, sampleStep, count, start,
                                region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
                                preferInterior, settings, bead, adaptiveBar, coverRadius, sameSideMax,
                                layerPlan, ref nextTreeId, solidAt, i + 1, regions[i + 1], rawSamples))
                            {
                                if (solidAt is not null) meshVetoes++;
                            }
                        }

                        for (int j = 0; j < count; j++)
                        {
                            int si = (start + j) % samples.Count;
                            if (!unsupported[si]) continue;
                            var pt = samples[si];
                            if (ClassifyPoint(region, envelope, pt) == DemandSpace.Exterior && !settings.LightningExteriorOverhangs)
                                continue;
                            if (CoveredBySameSide(layerPlan.Trees, pt, coverRadius, sameSideMax, region))
                                continue;
                            TryAddButtressAt(samples, si, sampleStep, count, start,
                                region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
                                preferInterior, settings, bead, adaptiveBar, coverRadius, sameSideMax,
                                layerPlan, ref nextTreeId, solidAt, i + 1, regions[i + 1], rawSamples);
                        }
                    }
                    else
                    {
                        // Separate tip budgets: Interior and Exterior domains do not
                        // steal finger slots from each other along a mixed run.
                        var rankedInt = new List<(int Si, float Sev)>(count);
                        var rankedExt = new List<(int Si, float Sev)>(count);
                        int intSamples = 0, extSamples = 0;
                        for (int j = 0; j < count; j++)
                        {
                            int si = (start + j) % samples.Count;
                            if (!unsupported[si]) continue;
                            var sp = ClassifyPoint(region, envelope, samples[si]);
                            if (sp == DemandSpace.Exterior)
                            {
                                extSamples++;
                                rankedExt.Add((si, severities[si]));
                            }
                            else
                            {
                                intSamples++;
                                rankedInt.Add((si, severities[si]));
                            }
                        }
                        rankedInt.Sort((a, b) => b.Sev.CompareTo(a.Sev));
                        rankedExt.Sort((a, b) => b.Sev.CompareTo(a.Sev));

                        int tipBudgetInt = rankedInt.Count == 0 ? 0
                            : Math.Max(1, (int)MathF.Round(intSamples * sampleStep / spacing));
                        int tipBudgetExt = rankedExt.Count == 0 ? 0
                            : Math.Max(1, (int)MathF.Round(extSamples * sampleStep / spacing));

                        meshVetoes += PlaceBridgeDomainTips(
                            rankedInt, tipBudgetInt, samples, rawSamples,
                            region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
                            settings, bead, supportRadius, spacing, exteriorDomain: false,
                            layerPlan, ref nextTreeId, solidAt, i + 1, regions[i + 1],
                            maxStep: stepAbove);
                        meshVetoes += PlaceBridgeDomainTips(
                            rankedExt, tipBudgetExt, samples, rawSamples,
                            region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
                            settings, bead, supportRadius, spacing, exteriorDomain: true,
                            layerPlan, ref nextTreeId, solidAt, i + 1, regions[i + 1],
                            maxStep: stepAboveExternal);
                    }
                }
            }

            // Residual: one pass for still-open severe free-edge holes only.
            // Feature-gated — the default automatic Bridge path must not densify.
            if (!buttress && severityAware)
            {
                float severeMin = MathF.Max(bead * 0.5f, supportRadius * 0.65f);
                var residual = CollectBridgeDemand(
                    regions[i + 1], region, envelope, core,
                    multiDemandFootprint, gravityDemandFootprint,
                    frames is not null, Down, DownGravity,
                    settings, bead, supportRadius, sampleStep,
                    layerPlan.Trees, solidAt, i + 1);
                residual.RemoveAll(s => s.Severity < severeMin);
                if (residual.Count > 0)
                {
                    residual.Sort((a, b) => b.Severity.CompareTo(a.Severity));
                    // At most a few extras — do not densify into phantom ladders.
                    int extraBudget = Math.Max(1, 1 + residual.Count / 8);
                    if (residual.Count > extraBudget)
                        residual.RemoveRange(extraBudget, residual.Count - extraBudget);
                    meshVetoes += PlaceBridgeTipsSeverityFirst(
                        residual, region, envelope, core, anchorPaths,
                        settings, bead, supportRadius, spacing,
                        layerPlan, ref nextTreeId, solidAt, i + 1, regions[i + 1],
                        maxStep: stepAbove);
                }
            }

            // ── Free-edge exterior pass (Formbound Bridge + Affect Exterior) ──
            // Outer perimeter of the UPPER layer often steps out only a few mm per
            // layer. The main pass uses supportRadius (= maxStep + bead/2), which with
            // wide beads is ~5 mm — so a 3–4 mm free-edge step never registers as
            // "far" and the worst free edge stays bare. Use the angle-based
            // demandRadius (respects Max overhang °) and denser tip spacing so free
            // edges get sacrificial exterior fins under Affect Exterior.
            if (!buttress && settings.LightningExteriorOverhangs)
            {
                float freeStep = MathF.Max(1f, bead * 0.4f);
                float freeTipSep = MathF.Max(bead * 1.25f, spacing * 0.45f);
                float freeRadius = MathF.Max(demandRadius, stepAbove + bead * 0.05f);
                var freeMouth = anchorExterior.Count > 0 ? anchorExterior
                    : (anchorPaths.Count > 0 ? anchorPaths : region);

                foreach (var path in regions[i + 1])
                {
                    if (path.Count < 3) continue;
                    // Outer perimeter only (positive area after normalization).
                    if (Clipper.Area(path) <= 0) continue;

                    var raw = SamplePath(path, freeStep);
                    if (raw.Count == 0) continue;
                    var pts = new List<Vector2>(raw.Count);
                    if (frames is not null)
                    {
                        for (int k = 0; k < raw.Count; k++)
                            pts.Add(Down(raw[k]));
                    }
                    else
                        pts.AddRange(raw);

                    // Rank free-edge candidates by how far they sit outside lower material.
                    var freeRanked = new List<(Vector2 Pt, float Sev, int Si)>(pts.Count);
                    for (int si = 0; si < pts.Count; si++)
                    {
                        var pt = pts[si];
                        float dWall = Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt);
                        bool outside = !InsideRegion(region, pt);
                        // Require a genuine outward step past the angle-based radius,
                        // whether the sample reads inside or (numerically) just outside
                        // the wall. On a straight wall the upper perimeter coincides
                        // with this wall (dWall≈0) and read "outside" — the old
                        // !outside-only skip let those fabricate a fin every layer,
                        // trailing sacrificial material all the way to the bed.
                        if (dWall <= freeRadius) continue;
                        float sev = outside ? dWall + bead : MathF.Max(0f, dWall - freeRadius);
                        if (NearAnyCenterline(layerPlan.Trees, pt,
                                CoverRadiusForSeverity(sev + freeRadius, supportRadius, bead)))
                            continue;
                        freeRanked.Add((pt, sev, si));
                    }
                    if (freeRanked.Count == 0) continue;
                    freeRanked.Sort((a, b) => b.Sev.CompareTo(a.Sev));

                    // Own free-edge tip budget (independent of interior budget).
                    float freeArc = freeRanked.Count * freeStep;
                    int freeBudget = Math.Max(1, (int)MathF.Ceiling(freeArc / freeTipSep));
                    int freePlaced = 0;
                    float freeMouthCatch = MathF.Max(freeTipSep * 1.15f, bead * 2.5f);
                    foreach (var cand in freeRanked)
                    {
                        if (freePlaced >= freeBudget) break;
                        var tip = cand.Pt;
                        int si = cand.Si;
                        if (TooCloseToExisting(layerPlan.Trees, tip, freeTipSep)) continue;
                        if (!PassesMeshVetoAt(solidAt, i + 1, regions[i + 1], raw, si, bead))
                        {
                            meshVetoes++;
                            continue;
                        }

                                        // Prefer MaxStep-extend / mouth-deepen of an inherited exterior
                        // fin over a brand-new full-length hairpin (full re-birth
                        // mid-stack creates floating rectangular free-edge islands).
                        if (TryExtendExistingBridge(
                                layerPlan.Trees, tip, region, core, bead,
                                stepAboveExternal, freeMouthCatch, wantExternal: true)
                            || TryDeepenNearbyBridgeMouth(
                                layerPlan.Trees, tip, region, core, bead,
                                stepAboveExternal, freeMouthCatch, wantExternal: true))
                        {
                            freePlaced++;
                            continue;
                        }

                        // Fin roots on lower outer wall, tip at free-edge demand.
                        var anchor = ClosestOnRegionBoundary(freeMouth, tip);
                        if (Vector2.Distance(anchor, tip) < bead * 0.5f)
                        {
                            // Demand almost on the wall — push tip outward along free edge normal.
                            var prev = pts[(si - 1 + pts.Count) % pts.Count];
                            var next = pts[(si + 1) % pts.Count];
                            var tan = next - prev;
                            var nrm = tan.LengthSquared() > 1e-8f
                                ? Vector2.Normalize(new Vector2(-tan.Y, tan.X))
                                : new Vector2(1f, 0f);
                            // Prefer outward (away from lower material).
                            if (InsideRegion(region, tip + nrm * bead))
                                nrm = -nrm;
                            tip += nrm * MathF.Max(bead, freeRadius);
                            anchor = ClosestOnRegionBoundary(freeMouth, tip);
                        }
                        if (Vector2.Distance(anchor, tip) < bead * 0.4f) continue;

                        var t = new LightningTree
                        {
                            Id = nextTreeId++,
                            Anchor = anchor,
                            External = true,
                            Cavity = false,
                        };
                        t.Branches.Add(new LightningBranch([anchor, tip]));
                        layerPlan.Trees.Add(t);
                        freePlaced++;
                    }
                }
            }

            // ── 2c. Opposite-wall sweep (Buttress only) ───────────────────────
            if (buttress)
            {
                float coverRadius2 = MathF.Max(bead * 0.75f, supportRadius * 0.3f);
                float sameSideMax2 = MathF.Max(6f * bead, barLen);
                float adaptiveBar2 = MathF.Max(barLen, MathF.Max(bead * 2f, MathF.Min(spacing, barLen * 0.5f)) * 2.2f);

                foreach (var path in regions[i + 1])
                {
                    if (Math.Abs(Clipper.Area(path)) < 4.0 * bead * bead) continue;
                    var rawSamples = SamplePath(path, sampleStep);
                    if (rawSamples.Count == 0) continue;
                    var samples = new List<Vector2>(rawSamples.Count);
                    if (frames is not null)
                    {
                        for (int k = 0; k < rawSamples.Count; k++)
                            samples.Add(Down(rawSamples[k]));
                    }
                    else
                        samples.AddRange(rawSamples);
                    List<Vector2>? samplesG = null;
                    if (frames is not null && gravityDemandFootprint is not null)
                    {
                        samplesG = new List<Vector2>(rawSamples.Count);
                        for (int k = 0; k < rawSamples.Count; k++)
                            samplesG.Add(DownGravity(rawSamples[k]));
                    }

                    for (int si = 0; si < samples.Count; si++)
                    {
                        var pt = samples[si];
                        bool stillOpen;
                        if (multiDemandFootprint is not null)
                            stillOpen = InsideRegion(multiDemandFootprint, pt)
                                || Vector2.Distance(ClosestOnRegionBoundary(multiDemandFootprint, pt), pt) < bead * 0.6f;
                        else
                            stillOpen = Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt) > supportRadius;
                        if (!stillOpen && samplesG is not null
                            && GravityFar(gravityDemandFootprint, samplesG[si]))
                        {
                            stillOpen = true;
                            pt = samples[si] = samplesG[si];
                        }
                        if (!stillOpen) continue;
                        if (ClassifyPoint(region, envelope, pt) == DemandSpace.Exterior && !settings.LightningExteriorOverhangs)
                            continue;
                        if (CoveredBySameSide(layerPlan.Trees, pt, coverRadius2, sameSideMax2, region))
                            continue;
                        TryAddButtressAt(samples, si, sampleStep, samples.Count, 0,
                            region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
                            preferInterior, settings, bead, adaptiveBar2, coverRadius2, sameSideMax2,
                            layerPlan, ref nextTreeId, solidAt, i + 1, regions[i + 1], rawSamples);
                    }
                }
            }

            // ── 2e. Coverage audit (Formbound Buttress) — 100% of demand samples ─
            // Prefer EXTENDING existing same-side trees (grow ≤ MaxStep) before
            // birthing new T's. Bridge is excluded (over-notches multi-planar walls).
            if (buttress)
            {
                float coverRa = MathF.Max(bead * 0.75f, supportRadius * 0.3f);
                float sameSideRa = MathF.Max(6f * bead, barLen);
                float adaptiveBarA = MathF.Max(barLen,
                    MathF.Max(bead * 2f, MathF.Min(spacing, barLen * 0.5f)) * 2.2f);
                const int maxAuditPasses = 6;
                for (int pass = 0; pass < maxAuditPasses; pass++)
                {
                    int placed = 0;
                    foreach (var path in regions[i + 1])
                    {
                        if (Math.Abs(Clipper.Area(path)) < 4.0 * bead * bead) continue;
                        var rawSamples = SamplePath(path, sampleStep);
                        if (rawSamples.Count == 0) continue;
                        var samples = new List<Vector2>(rawSamples.Count);
                        if (frames is not null)
                        {
                            for (int k = 0; k < rawSamples.Count; k++)
                                samples.Add(Down(rawSamples[k]));
                        }
                        else
                            samples.AddRange(rawSamples);
                        List<Vector2>? samplesG = null;
                        if (frames is not null && gravityDemandFootprint is not null)
                        {
                            samplesG = new List<Vector2>(rawSamples.Count);
                            for (int k = 0; k < rawSamples.Count; k++)
                                samplesG.Add(DownGravity(rawSamples[k]));
                        }

                        for (int si = 0; si < samples.Count; si++)
                        {
                            var pt = samples[si];
                            // The audit hunts at the user's Max-overhang angle
                            // (demandRadius) — tighter than the birth passes' half-
                            // bead printability threshold — so existing fins EXTEND
                            // (or wall-foot stubs seed) under the marginal 30-60°
                            // flare band instead of stopping short of it.
                            var auditFootprint = multiExtendFootprint ?? multiDemandFootprint;
                            bool far = frames is not null
                                ? (auditFootprint is not null
                                   && (InsideRegion(auditFootprint, pt)
                                       || Vector2.Distance(
                                           ClosestOnRegionBoundary(auditFootprint, pt), pt)
                                          < bead * 0.6f))
                                : Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt)
                                  > demandRadius;
                            if (!far && samplesG is not null
                                && GravityFar(gravityExtendFootprint ?? gravityDemandFootprint, samplesG[si]))
                            {
                                far = true;
                                pt = samples[si] = samplesG[si];
                            }
                            if (!far) continue;
                            if (ClassifyPoint(region, envelope, pt) == DemandSpace.Exterior && !settings.LightningExteriorOverhangs)
                                continue;
                            if (CoveredBySameSide(layerPlan.Trees, pt, coverRa, sameSideRa, region))
                                continue;

                            // 1) Extend an existing same-side tree toward this sample.
                            if (TryExtendExistingButtress(layerPlan.Trees, pt, region, core,
                                    bead, barLen, MaxStep(i), coverRa, sameSideRa))
                            {
                                placed++;
                                auditExtensions++;
                                continue;
                            }

                            // 2) Birth a new T only when no same-side tree can grow here.
                            if (TryAddButtressAt(samples, si, sampleStep, samples.Count, 0,
                                region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
                                preferInterior, settings, bead, adaptiveBarA, coverRa, sameSideRa,
                                layerPlan, ref nextTreeId, solidAt, i + 1, regions[i + 1], rawSamples))
                            {
                                placed++;
                                continue;
                            }
                            // 3) Last resort: short MaxStep stub at wall foot.
                            var wall = ClosestOnRegionBoundary(
                                anchorPaths.Count > 0 ? anchorPaths : region, pt);
                            if (NearAnyCenterline(layerPlan.Trees, wall, bead * 1.5f)
                                || CoveredBySameSide(layerPlan.Trees, pt, coverRa, sameSideRa, region))
                                continue;
                            var seed = new LightningTree
                            {
                                Id = nextTreeId,
                                Anchor = wall,
                                External = !InsideRegion(region, pt),
                            };
                            if (!TryRebuildShortButtress(seed, region, core, bead, barLen, MaxStep(i)))
                                continue;
                            nextTreeId++;
                            layerPlan.Trees.Add(seed);
                            placed++;
                        }
                    }
                    if (placed == 0) break;
                }

                // Final uncovered tally.
                int layerUncovered = 0;
                foreach (var path in regions[i + 1])
                {
                    if (Math.Abs(Clipper.Area(path)) < 4.0 * bead * bead) continue;
                    var rawSamples = SamplePath(path, sampleStep);
                    if (rawSamples.Count == 0) continue;
                    var samples = new List<Vector2>(rawSamples.Count);
                    if (frames is not null)
                    {
                        for (int k = 0; k < rawSamples.Count; k++)
                            samples.Add(Down(rawSamples[k]));
                    }
                    else
                        samples.AddRange(rawSamples);
                    List<Vector2>? samplesG = null;
                    if (frames is not null && gravityDemandFootprint is not null)
                    {
                        samplesG = new List<Vector2>(rawSamples.Count);
                        for (int k = 0; k < rawSamples.Count; k++)
                            samplesG.Add(DownGravity(rawSamples[k]));
                    }
                    for (int si = 0; si < samples.Count; si++)
                    {
                        var pt = samples[si];
                        bool far = multiDemandFootprint is not null
                            ? (InsideRegion(multiDemandFootprint, pt)
                               || Vector2.Distance(
                                   ClosestOnRegionBoundary(multiDemandFootprint, pt), pt)
                                  < bead * 0.6f)
                            : Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt)
                              > supportRadius;
                        if (!far && samplesG is not null
                            && GravityFar(gravityDemandFootprint, samplesG[si]))
                        {
                            far = true;
                            pt = samples[si] = samplesG[si];
                        }
                        if (!far) continue;
                        if (ClassifyPoint(region, envelope, pt) == DemandSpace.Exterior && !settings.LightningExteriorOverhangs)
                            continue;
                        // Phantom demand the mesh oracle rejected (grazing-cut parity
                        // ledges) is CORRECTLY unsupported — not a coverage failure.
                        if (!PassesMeshVetoAt(solidAt, i + 1, regions[i + 1], rawSamples, si, bead))
                            continue;
                        if (!CoveredBySameSide(layerPlan.Trees, pt, coverRa, sameSideRa, region))
                        {
                            layerUncovered++;
                            if (uncoveredTotal + layerUncovered <= 12)
                            {
                                var line = $"[formbound] UNCOVERED layer {i + 1} at ({pt.X:0.#},{pt.Y:0.#})";
                                plan.UncoveredLog.Add(line);   // app console via FormboundStats
                                System.Console.WriteLine(line);
                            }
                        }
                    }
                }
                uncoveredTotal += layerUncovered;
            }

            ManualDemandOnly:
            // ── 2b. Paint-driven column (ONE perimeter mouth for the whole stack) ──
            // ColumnFoot (bridge target) = the ONLY wall break / mouth / seam pin —
            // stacked vertically on every layer. SupportBar only opens the T width
            // at the support height; it must NEVER relocate the mouth to the bar.
            //
            // Target Support Selections (aggressive): densify the painted run and
            // birth/extend T columns so the *entire* selected line is covered —
            // not a single mid-point T with sparse bars.
            {
                bool aggressive = settings.LightningTargetSupportSelections;
                var dem = (manualDemand is not null && manualDemand.Count > i + 1)
                    ? manualDemand[i + 1] : null;
                // Raw projected samples + sides (before order/densify).
                var barRaw = dem is not null
                    ? dem.SupportBar.Select(Down).ToList() : new List<Vector2>();
                var barSidesRaw = dem is not null
                    ? new List<PaintSupportSide>(dem.SupportBarSides) : new List<PaintSupportSide>();
                while (barSidesRaw.Count < barRaw.Count)
                    barSidesRaw.Add(PaintSupportSide.Inside);
                var footRaw = dem is not null
                    ? dem.ColumnFoot.Select(Down).ToList() : new List<Vector2>();
                var footSidesRaw = dem is not null
                    ? new List<PaintSupportSide>(dem.ColumnFootSides) : new List<PaintSupportSide>();
                while (footSidesRaw.Count < footRaw.Count)
                    footSidesRaw.Add(PaintSupportSide.Inside);

                var barRun = dem is not null
                    ? OrderDemandRun(dem.SupportBar, Down) : new List<Vector2>();
                var footRun = dem is not null
                    ? OrderDemandRun(dem.ColumnFoot, Down) : new List<Vector2>();

                // Dense samples along the selected line (sub-bead pitch).
                if (aggressive && barRun.Count >= 2)
                    barRun = DenseResampleRun(barRun, MathF.Max(1.5f, bead * 0.4f));
                if (aggressive && footRun.Count >= 2)
                    footRun = DenseResampleRun(footRun, MathF.Max(1.5f, bead * 0.4f));

                // Map densified samples back to Inside/Outside from original paint.
                var barSides = TransferDemandSides(barRaw, barSidesRaw, barRun);
                var footSides = TransferDemandSides(footRaw, footSidesRaw, footRun);
                PaintSupportSide SideAtBar(int si) =>
                    si >= 0 && si < barSides.Count ? barSides[si] : PaintSupportSide.Inside;

                // Global foot in THIS layer's frame — preferred mouth + seam for all Z.
                Vector2? globalFoot = null;
                if (paintFootMid is { } gfm && paintFootLayer >= 0)
                    globalFoot = Remap(paintFootLayer, i, gfm);
                if (footRun.Count > 0)
                {
                    var fMid = footRun[MidIndexAlongRun(footRun)];
                    paintSeamPin = fMid;
                    globalFoot = fMid;
                }
                else if (globalFoot is { } gfPin)
                    paintSeamPin = gfPin;

                // Mouth aim: FOOT only. Bar is not an aim (that put the blue break
                // under the support selection instead of the yellow seam stack).
                Vector2? mouthAim = globalFoot;
                if (mouthAim is null && footRun.Count > 0)
                    mouthAim = footRun[MidIndexAlongRun(footRun)];
                // Support-only paint (no target yet): fall back to bar mid.
                if (mouthAim is null && barRun.Count > 0)
                    mouthAim = barRun[MidIndexAlongRun(barRun)];
                if (mouthAim is null && paintBarMid is { } gbm && paintBarLayer >= 0)
                    mouthAim = Remap(paintBarLayer, i, gbm);

                // ── Corbel ledges (classic paint only) ───────────────────────
                // NEVER when Target Support Selections: corbel pads are inflated
                // free-space loops that the generator unions into floating islands
                // ("support starting in mid-air"). Selection mode uses steppable
                // wall→tip columns only (MaxStep per layer, inherited downward).
                if (barRun.Count > 0 && !aggressive)
                {
                    float corbelReach = bead * 3f;
                    float stepC = MaxStep(i);
                    float pairDist = bead * 6f;
                    var cor = new List<(Vector2 Pt, Vector2 Anchor, Vector2 Dir, float Reach)>();
                    foreach (var mpt in barRun)
                    {
                        var anchorC = ClosestOnRegionBoundary(region, mpt);
                        float dWall = Vector2.Distance(anchorC, mpt);
                        if (dWall <= 1e-3f || dWall > corbelReach) continue;
                        if (InsideRegion(region, mpt)) continue; // already on material
                        cor.Add((mpt, anchorC, (mpt - anchorC) / dWall, dWall + bead * 0.55f));
                    }
                    for (int ci2 = 0; ci2 < cor.Count; ci2++)
                    {
                        var (pt, anchorC, dirC, reachFull) = cor[ci2];
                        bool paired = ci2 + 1 < cor.Count
                            && Vector2.Distance(pt, cor[ci2 + 1].Pt) <= pairDist;
                        int layersDown = Math.Min(40,
                            (int)MathF.Ceiling(reachFull / MathF.Max(stepC, 0.1f)));
                        for (int k = 0; k <= layersDown && i - k >= 0; k++)
                        {
                            float ext = reachFull - k * stepC;
                            if (ext <= 0f) break;
                            var line = new List<Vector2>
                            {
                                anchorC - dirC * (bead * 0.6f),
                                anchorC + dirC * ext,
                            };
                            if (paired)
                            {
                                var (pt2, anchor2, dir2, reach2) = cor[ci2 + 1];
                                float ext2 = MathF.Max(0f, reach2 - k * stepC);
                                line.Add(anchor2 + dir2 * ext2);
                                line.Add(anchor2 - dir2 * (bead * 0.6f));
                            }
                            var pathC = new PathD(line.Count);
                            foreach (var v0 in line)
                            {
                                var v = (frames is not null && k > 0) ? Remap(i, i - k, v0) : v0;
                                pathC.Add(new PointD(v.X, v.Y));
                            }
                            var padC = Clipper.InflatePaths(new PathsD { pathC }, bead * 0.6,
                                JoinType.Round, EndType.Round, 2.0);
                            var target = plan.Layers[i - k];
                            target.CorbelPads ??= new PathsD();
                            target.CorbelPads.AddRange(padC);
                        }
                    }
                }

                float barLenRun = 0f;
                for (int j = 1; j < barRun.Count; j++)
                    barLenRun += Vector2.Distance(barRun[j - 1], barRun[j]);
                // Aggressive multi-T: each column uses the user Horizontal-bar length
                // (or a modest default) — NOT the full painted run length. Using
                // barLenRun as halfBar made one T claim the whole path, then
                // TryExtend always "succeeded" by a hair and blocked every new birth
                // (2 trees covering ~25% of 700+ samples).
                // Classic single-column mode still opens one T spanning the run.
                float mBar = aggressive
                    ? MathF.Max(barLen > 0f ? barLen : spacing * 2f, bead * 5f)
                    : (barRun.Count > 0
                        ? MathF.Max(barLen, MathF.Max(bead * 5f, barLenRun * 1.05f))
                        : MathF.Max(barLen, bead * 5f));
                float mSide = aggressive
                    ? MathF.Max(12f * bead, mBar * 1.5f)
                    : MathF.Max(
                        MathF.Max(24f * bead, barLen * 2f),
                        barLenRun + 16f * bead);
                float mCover = aggressive
                    ? MathF.Max(bead * 1.25f, MathF.Min(bead * 2.5f, spacing * 0.55f))
                    : MathF.Max(bead * 2f, MathF.Max(barLenRun, bead * 4f) * 0.55f);
                float mStep = bead * 2.5f;
                float layerH = MathF.Max(i < layerHeights.Count ? layerHeights[i] : bead, 0.1f);
                // How far a tip may stick out and still have a MaxStep-legal column
                // underneath (layers 0..i below the current planning layer).
                float stepI = MaxStep(i);
                float maxPrintableReach = MathF.Max(bead * 2f, (i + 1) * stepI + bead);
                float maxAnchorDist = aggressive
                    ? MathF.Max(maxPrintableReach, bead * 4f)
                    : MathF.Max(2f * layerH, 24f * bead);
                // NEVER grow faster than MaxStep — that prints air under the bar.
                float growStep = stepI;

                Vector2? barFace = null;
                if (barRun.Count > 0)
                    barFace = barRun[MidIndexAlongRun(barRun)];
                else if (paintBarMid is { } gbm2 && paintBarLayer >= 0)
                    barFace = Remap(paintBarLayer, i, gbm2);

                LightningTree? paintTree = null;
                if (paintColumnId is int pid)
                    paintTree = layerPlan.Trees.FirstOrDefault(t => t.Id == pid);
                if (paintTree is null)
                    paintTree = layerPlan.Trees.FirstOrDefault(t => t.PaintColumn);

                // Trees already present on this layer came from inheritance + MaxStep
                // retract. Re-birthing a full-depth T under the same demand creates a
                // mid-air island (full bar with no steppable column below). Snapshot
                // ids NOW — before any new births this layer.
                var inheritedTreeIds = new HashSet<int>();
                foreach (var t0 in layerPlan.Trees)
                    inheritedTreeIds.Add(t0.Id);
                bool hasInheritedPaintColumn = layerPlan.Trees.Any(t =>
                    inheritedTreeIds.Contains(t.Id) && (t.PaintColumn || t.Manual));

                // ── Aggressive full-line support (Target Support Selections) ──
                // TOP of a paint column: birth full wall-rooted T's along the run.
                // LOWER layers (already have inherited paint trees): ONLY MaxStep
                // extend — never invent a new full-depth bar in free space.
                // Bottom-up: stub → longer trunk → opening bar → full T under selection.
                void AggressiveCoverBarRun()
                {
                    if (!buttress || barRun.Count == 0) return;
                    var barSamples = barRun;
                    // Birth allowed only when this layer is founding the column
                    // (no inherited paint/manual trees). Otherwise extend-only.
                    bool allowFullBirth = !hasInheritedPaintColumn;
                    // Birth pitch ≈ half a T-bar so neighboring columns tile the run.
                    float birthPitch = MathF.Max(bead * 2f,
                        MathF.Min(mBar * 0.85f, MathF.Max(spacing * 0.75f, bead * 3f)));
                    float acc = birthPitch; // force first sample
                    int births = 0;
                    int extensions = 0;
                    int birthFails = 0;

                    // Local sample window around a birth so halfBar does not expand
                    // to the entire 700-pt painted path (that blocked multi-T birth).
                    void LocalRunWindow(int si, out int runStart, out int runCount)
                    {
                        float want = MathF.Max(mBar, bead * 6f);
                        runStart = si;
                        float left = 0f;
                        while (runStart > 0 && left < want * 0.55f)
                        {
                            left += Vector2.Distance(barSamples[runStart - 1], barSamples[runStart]);
                            runStart--;
                        }
                        int runEnd = si;
                        float right = 0f;
                        while (runEnd + 1 < barSamples.Count && right < want * 0.55f)
                        {
                            right += Vector2.Distance(barSamples[runEnd], barSamples[runEnd + 1]);
                            runEnd++;
                        }
                        runCount = Math.Max(1, runEnd - runStart + 1);
                    }

                    void TagNewPaintTrees(int treesBefore, PaintSupportSide side)
                    {
                        bool outside = PaintSupportSideUtil.IsOutside(side);
                        for (int ti = treesBefore; ti < layerPlan.Trees.Count; ti++)
                        {
                            var nt = layerPlan.Trees[ti];
                            nt.Manual = true;
                            nt.PaintColumn = true;
                            // Inside = dual-wall notch into the solid; Outside =
                            // sacrificial exterior fin (union, not difference).
                            nt.External = outside;
                            nt.Cavity = false;
                            // Soft validate interior trunks only.
                            if (!outside
                                && nt.Branches.Count > 0
                                && nt.Branches[0].Centerline.Count >= 2)
                            {
                                var a = nt.Anchor;
                                var e = nt.Branches[0].Centerline[^1];
                                if (!SegmentInsideRegion(region, a, e, bead * 1.25f))
                                {
                                    if (!TryRebuildShortButtress(nt, region, core, bead, mBar, growStep))
                                    {
                                        layerPlan.Trees.RemoveAt(ti);
                                        ti--;
                                        continue;
                                    }
                                }
                            }
                            births++;
                        }
                    }

                    for (int si = 0; si < barSamples.Count; si++)
                    {
                        var pt = barSamples[si];
                        float stepAlong = si == 0 ? birthPitch
                            : Vector2.Distance(barSamples[si - 1], pt);
                        acc += stepAlong;
                        bool birthSlot = acc >= birthPitch - 1e-4f || si == 0
                                         || si == barSamples.Count - 1;
                        if (birthSlot) acc = 0f;

                        if (CoveredBySameSide(layerPlan.Trees, pt, mCover, mSide, region))
                        {
                            // Already covered — optional MaxStep grow.
                            if (TryExtendExistingButtress(
                                    layerPlan.Trees, pt, region, core,
                                    bead, mBar, growStep, mCover, mSide))
                                extensions++;
                            continue;
                        }

                        // Grow existing columns first, but ONLY skip birth when the
                        // sample is actually covered after the extend. A tiny MaxStep
                        // nudge must not block a new T for an uncovered far sample.
                        if (TryExtendExistingButtress(
                                layerPlan.Trees, pt, region, core,
                                bead, mBar, growStep, mCover, mSide))
                        {
                            extensions++;
                            if (CoveredBySameSide(layerPlan.Trees, pt, mCover, mSide, region))
                                continue;
                        }

                        // Lower layers: no new full T — inheritance is the foundation.
                        if (!allowFullBirth || !birthSlot)
                            continue;

                        int treesBefore = layerPlan.Trees.Count;
                        LocalRunWindow(si, out int r0, out int rN);
                        Vector2? face = barFace ?? pt;
                        var side = SideAtBar(si);
                        bool added = TryAddButtressAt(barSamples, si, mStep, rN, r0,
                            region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
                            preferInterior, settings, bead, mBar, mCover, mSide,
                            layerPlan, ref nextTreeId, null, i + 1, regions[i + 1], barSamples,
                            maxAnchorDist, face, side);
                        TagNewPaintTrees(treesBefore, side);
                        if (!added && layerPlan.Trees.Count == treesBefore)
                            birthFails++;
                        if (layerPlan.Trees.Count > treesBefore)
                        {
                            paintColumnId ??= layerPlan.Trees[^1].Id;
                            paintSeamPin ??= layerPlan.Trees[^1].Anchor;
                            for (int sj = Math.Max(0, si - 4);
                                 sj < Math.Min(barSamples.Count, si + 5); sj++)
                            {
                                if (TryExtendExistingButtress(
                                        layerPlan.Trees, barSamples[sj], region, core,
                                        bead, mBar, growStep, mCover, mSide))
                                    extensions++;
                            }
                        }
                    }

                    // Final sweep: extend only on lower layers; birth only when founding.
                    int reseed = 0;
                    foreach (var pt in barSamples)
                    {
                        if (CoveredBySameSide(layerPlan.Trees, pt, mCover * 1.15f, mSide, region))
                            continue;
                        if (TryExtendExistingButtress(
                                layerPlan.Trees, pt, region, core,
                                bead, mBar, growStep, mCover * 1.25f, mSide))
                        {
                            extensions++;
                            if (CoveredBySameSide(layerPlan.Trees, pt, mCover * 1.15f, mSide, region))
                            { reseed++; continue; }
                        }
                        if (!allowFullBirth)
                            continue;
                        int treesBefore = layerPlan.Trees.Count;
                        int bSi = 0;
                        float bestD = float.MaxValue;
                        for (int k = 0; k < barSamples.Count; k++)
                        {
                            float d = Vector2.DistanceSquared(barSamples[k], pt);
                            if (d < bestD) { bestD = d; bSi = k; }
                        }
                        LocalRunWindow(bSi, out int r0b, out int rNb);
                        var sideB = SideAtBar(bSi);
                        bool added = TryAddButtressAt(barSamples, bSi, mStep, rNb, r0b,
                            region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
                            preferInterior, settings, bead, mBar, mCover * 0.85f, mSide,
                            layerPlan, ref nextTreeId, null, i + 1, regions[i + 1], barSamples,
                            maxAnchorDist, pt, sideB);
                        int beforeCount = births;
                        TagNewPaintTrees(treesBefore, sideB);
                        if (births > beforeCount) reseed++;
                        else if (!added) birthFails++;
                    }

                    int covered = 0;
                    foreach (var pt in barSamples)
                        if (CoveredBySameSide(layerPlan.Trees, pt, mCover * 1.2f, mSide, region))
                            covered++;
                    var msg = $"[formbound] target-selections layer={i + 1} " +
                              $"barPts={barSamples.Count} covered={covered}/{barSamples.Count} " +
                              $"trees={layerPlan.Trees.Count(t => t.Manual)} " +
                              $"births={births} fail={birthFails} ext={extensions} " +
                              $"mode={(allowFullBirth ? "FOUND" : "INHERIT-ONLY")} " +
                              $"mBar={mBar:0.#} pitch={birthPitch:0.#} reseeds={reseed}";
                    plan.UncoveredLog.Add(msg);
                    System.Console.WriteLine(msg);
                }

                if (aggressive && buttress && dem is not null && dem.HasAny)
                {
                    // Prefer classic single-column mouth at foot when a bridge target
                    // exists, then aggressively cover the full support bar.
                    // Dominant side for this layer's paint (outside wins if any sample is).
                    bool layerOutside = barSides.Any(PaintSupportSideUtil.IsOutside)
                        || footSides.Any(PaintSupportSideUtil.IsOutside);
                    var layerSide = layerOutside ? PaintSupportSide.Outside : PaintSupportSide.Inside;

                    if (paintTree is not null && mouthAim is { } aimKeep)
                    {
                        paintColumnId = paintTree.Id;
                        paintTree.PaintColumn = true;
                        paintTree.External = layerOutside;
                        paintTree.Cavity = false;
                        SnapPaintMouthToAim(paintTree, aimKeep, region, core, bead, mBar, growStep);
                    }
                    else if (!hasInheritedPaintColumn
                             && paintColumnId is null
                             && (globalFoot is not null || footRun.Count > 0))
                    {
                        // First foundation only — never re-birth a full foot column
                        // on lower layers that already inherited paint trees.
                        List<Vector2> birthRun = globalFoot is { } gfB
                            ? [gfB]
                            : footRun;
                        int bMid = MidIndexAlongRun(birthRun);
                        int treesBefore = layerPlan.Trees.Count;
                        var barSamples = barRun.Count > 0 ? barRun : birthRun;
                        TryAddButtressAt(birthRun, bMid, mStep, birthRun.Count, 0,
                            region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
                            preferInterior, settings, bead, mBar, mCover, mSide,
                            layerPlan, ref nextTreeId, null, i + 1, regions[i + 1], barSamples,
                            maxAnchorDist, barFace ?? mouthAim, layerSide);
                        for (int ti = treesBefore; ti < layerPlan.Trees.Count; ti++)
                        {
                            layerPlan.Trees[ti].Manual = true;
                            layerPlan.Trees[ti].PaintColumn = true;
                            layerPlan.Trees[ti].External = layerOutside;
                            layerPlan.Trees[ti].Cavity = false;
                        }
                        if (layerPlan.Trees.Count > treesBefore)
                        {
                            paintColumnId = layerPlan.Trees[^1].Id;
                            paintSeamPin ??= globalFoot ?? layerPlan.Trees[^1].Anchor;
                            if (mouthAim is { } aim0)
                                SnapPaintMouthToAim(layerPlan.Trees[^1], aim0, region, core,
                                    bead, mBar, growStep);
                        }
                    }

                    AggressiveCoverBarRun();
                }
                else if (buttress && paintTree is not null)
                {
                    paintColumnId = paintTree.Id;
                    paintTree.PaintColumn = true;
                    paintTree.External = false;
                    paintTree.Cavity = false;
                    // Snap mouth fully onto the target/seam every layer (vertical stack).
                    if (mouthAim is { } aim)
                    {
                        if (SnapPaintMouthToAim(paintTree, aim, region, core, bead, mBar, stepI))
                            auditExtensions++;
                    }
                    // Support layer only: grow T bars toward the bar run — mouth stays put.
                    if (barRun.Count > 0)
                    {
                        foreach (var pt in barRun)
                        {
                            if (TryExtendExistingButtress(
                                    layerPlan.Trees, pt, region, core,
                                    bead, mBar, stepI, mCover, mSide))
                                auditExtensions++;
                        }
                        // Re-snap mouth after extend in case growth drifted the root.
                        if (mouthAim is { } aim2)
                            SnapPaintMouthToAim(paintTree, aim2, region, core, bead, mBar, stepI);
                    }
                    // No second perimeter birth — ever.
                }
                else if (buttress && paintColumnId is null
                         && dem is not null && dem.HasAny)
                {
                    // First (and only) perimeter mouth: at the TARGET (foot), never
                    // at the support bar. Bar run only supplies T width + face.
                    List<Vector2> birthRun;
                    if (globalFoot is { } gfBirth)
                        birthRun = [gfBirth];
                    else if (footRun.Count > 0)
                        birthRun = footRun;
                    else
                        birthRun = barRun; // support-only paint
                    if (birthRun.Count > 0)
                    {
                        int bMid = MidIndexAlongRun(birthRun);
                        // Face toward support so trunk/T open under the selection.
                        Vector2? face = barFace ?? mouthAim;
                        int treesBefore = layerPlan.Trees.Count;
                        // Use barRun as the walk samples when present so halfBar
                        // covers the full support path; mouth sample is still foot.
                        var barSamples = barRun.Count > 0 ? barRun : birthRun;
                        TryAddButtressAt(birthRun, bMid, mStep, birthRun.Count, 0,
                            region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
                            preferInterior, settings, bead, mBar, mCover, mSide,
                            layerPlan, ref nextTreeId, null, i + 1, regions[i + 1], barSamples,
                            maxAnchorDist, face);
                        for (int ti = treesBefore; ti < layerPlan.Trees.Count; ti++)
                            layerPlan.Trees[ti].Manual = true;
                        if (layerPlan.Trees.Count > treesBefore)
                        {
                            var born = layerPlan.Trees[^1];
                            born.PaintColumn = true;
                            // Wall-path paint is often classified Exterior (marks sit on
                            // the perimeter). Force interior so the mouth notches the wall.
                            born.External = false;
                            born.Cavity = false;
                            paintColumnId = born.Id;
                            paintSeamPin ??= globalFoot ?? born.Anchor;
                            // Force mouth onto the target immediately after birth.
                            if (mouthAim is { } aim0)
                                SnapPaintMouthToAim(born, aim0, region, core, bead, mBar, stepI);
                            // Open full T toward support bar samples when present.
                            if (barRun.Count > 0)
                            {
                                foreach (var pt in barRun)
                                    TryExtendExistingButtress(
                                        layerPlan.Trees, pt, region, core,
                                        bead, mBar, stepI, mCover, mSide);
                                if (mouthAim is { } aim1)
                                    SnapPaintMouthToAim(born, aim1, region, core, bead, mBar, stepI);
                            }
                            var mouth = born.Anchor;
                            var ok = $"[formbound] paint-column BORN id={born.Id} layer={i} " +
                                     $"barPts={barRun.Count} footPts={footRun.Count} " +
                                     $"mouth=({mouth.X:0.#},{mouth.Y:0.#}) " +
                                     $"aim={(mouthAim is { } aa ? $"({aa.X:0.#},{aa.Y:0.#})" : "none")} " +
                                     $"seamPin=({(paintSeamPin?.X ?? 0):0.#},{(paintSeamPin?.Y ?? 0):0.#})";
                            plan.UncoveredLog.Add(ok);
                            System.Console.WriteLine(ok);
                        }
                        else
                        {
                            var fail = $"[formbound] paint-column BIRTH FAILED layer={i} " +
                                       $"barPts={barRun.Count} footPts={footRun.Count} " +
                                       $"noAnchor={PfNoAnchor} barReach={PfBarReach} " +
                                       $"noFrame={PfNoFrame} covered={PfCovered}";
                            plan.UncoveredLog.Add(fail);
                            System.Console.WriteLine(fail);
                        }
                    }
                }
                else if (!buttress && dem is not null && dem.HasAny)
                {
                    // Formbound Bridge: one finger per dense sample when aggressive;
                    // classic mode keeps a single paint-column mouth.
                    var run = OrderDemandRun(
                        dem.ColumnFoot.Count > 0 ? dem.ColumnFoot : dem.SupportBar, Down);
                    if (aggressive && run.Count >= 2)
                        run = DenseResampleRun(run, MathF.Max(1.5f, bead * 0.45f));
                    if (run.Count > 0)
                    {
                        int[] indices = aggressive
                            ? Enumerable.Range(0, run.Count).ToArray()
                            : (paintColumnId is null
                                && !layerPlan.Trees.Any(t => t.PaintColumn)
                                    ? new[] { MidIndexAlongRun(run) }
                                    : Array.Empty<int>());
                        float tipSep = aggressive ? MathF.Max(bead * 1.1f, spacing * 0.4f)
                            : spacing * 0.4f;
                        foreach (int midSi in indices)
                        {
                            var pt = run[midSi];
                            if (NearAnyCenterline(layerPlan.Trees, pt, tipSep))
                                continue;
                            if (Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt)
                                <= supportRadius * 0.35f && !aggressive)
                                continue;
                            var mSpace = ClassifyPoint(region, envelope, pt);
                            bool external = mSpace == DemandSpace.Exterior;
                            bool cavity   = mSpace == DemandSpace.Cavity;
                            var tip = mSpace == DemandSpace.Interior
                                ? (InsideRegion(core, pt) ? pt : ClosestOnRegionBoundary(core, pt))
                                : pt;
                            var anchor = mSpace == DemandSpace.Interior
                                ? ClosestOnRegionBoundary(anchorPaths, tip)
                                : ClosestOnRegionBoundary(region, tip);
                            if (Vector2.Distance(anchor, tip) < bead * 0.5f && aggressive)
                            {
                                // On-wall paint: push tip slightly off-wall so a fin can form.
                                var nrm = tip - anchor;
                                float nl = nrm.Length();
                                if (nl > 1e-4f) tip = anchor + nrm / nl * MathF.Max(bead, nl);
                            }
                            if (Vector2.Distance(anchor, tip) >= bead * 0.45f
                                && (mSpace != DemandSpace.Interior
                                    || SegmentInsideRegion(region, anchor, tip, bead)
                                    || aggressive)
                                && (!cavity || SegmentInsideVoid(region, anchor, tip, bead)
                                    || aggressive))
                            {
                                var t = new LightningTree
                                {
                                    Id = nextTreeId++, Anchor = anchor,
                                    External = external && !aggressive, Cavity = cavity,
                                    PaintColumn = true, Manual = true,
                                };
                                t.Branches.Add(new LightningBranch([anchor, tip]));
                                layerPlan.Trees.Add(t);
                                paintColumnId ??= t.Id;
                            }
                        }
                    }
                }
            }

            // ── 2d. Island connectors: a layer whose region splits into several
            //       outers must still print as ONE continuous line — no travel ever
            //       starts an island. Every island gets an umbilical tube from the
            //       nearest other component (a cavity tree whose tip bites INTO the
            //       island, so the generator's Union merges the loops). The
            //       connector holds full length on every disconnected layer; below
            //       the island the same lineage retracts into a normal support
            //       column, so bottom-up the umbilical's landing is always there.
            // Target Support Selections: never invent island tubes (surface-wide
            // formbound that is not part of the user's Support selection).
            if (settings.LightningTargetSupportSelections)
                goto SkipIslandConnectors;
            var outerPaths = new List<PathD>();
            foreach (var pth in region)
                if (Clipper.Area(pth) > 0) outerPaths.Add(pth);
            if (outerPaths.Count > 1)
            {
                int dominant = 0;
                double dominantArea = 0;
                for (int oi = 0; oi < outerPaths.Count; oi++)
                {
                    double a = Clipper.Area(outerPaths[oi]);
                    if (a > dominantArea) { dominantArea = a; dominant = oi; }
                }
                for (int oi = 0; oi < outerPaths.Count; oi++)
                {
                    if (oi == dominant) continue;
                    var island = outerPaths[oi];

                    // Already bridged? A cavity/external tree whose anchor lives on
                    // ANOTHER component and whose centerline reaches this island.
                    bool bridged = false;
                    foreach (var t in layerPlan.Trees)
                    {
                        if (!t.Cavity && !t.External) continue;
                        if (PointNearPath(t.Anchor, island, bead * 1.5f)) continue; // rooted on the island itself
                        foreach (var b in t.Branches)
                        {
                            foreach (var node in b.Centerline)
                                if (PointNearPath(node, island, bead * 1.5f)) { bridged = true; break; }
                            if (bridged) break;
                        }
                        if (bridged) { t.Connector = true; break; }
                    }
                    if (bridged) continue;

                    // Closest pair between this island and any other component.
                    Vector2 from = default, to = default;
                    float bestD = float.MaxValue;
                    for (int oj = 0; oj < outerPaths.Count; oj++)
                    {
                        if (oj == oi) continue;
                        FindClosestPathPair(outerPaths[oj], island, ref from, ref to, ref bestD);
                    }
                    if (bestD == float.MaxValue) continue;

                    // Tip bites one bead INTO the island so the Union merges loops.
                    var dir = to - from;
                    if (dir.LengthSquared() < 1e-6f) continue;
                    dir = Vector2.Normalize(dir);
                    var tip = to + dir * bead;
                    if (!SegmentInsideVoid(region, from, to, bead * 1.5f)) continue;

                    var conn = new LightningTree
                    {
                        Id = nextTreeId++, Anchor = from,
                        Cavity = true, Connector = true,
                    };
                    conn.Branches.Add(new LightningBranch([from, tip]));
                    layerPlan.Trees.Add(conn);
                }
            }

            SkipIslandConnectors:
            // ── 3. Straightening: nudge interior nodes toward the root–tip chord,
            //       budgeted by this layer's max step so the layer above still rests
            //       within one step of the new position. ──────────────────────────
            float budget = MaxStep(i);
            foreach (var t in layerPlan.Trees)
            {
                if (t.External || t.Cavity) continue;   // fins/cavity tubes live outside the core
                foreach (var b in t.Branches)
                    Straighten(b.Centerline, budget, core);
            }
        }

        if (orphaned.Count > 0)
            foreach (var lp in plan.Layers)
                lp.Trees.RemoveAll(t => orphaned.Contains(t.Id));

        // Propagate the fixed paint-bridge seam pin onto every layer. Prefer the
        // ColumnFoot target; if we only ever saw a birth anchor, use that. When a
        // layer has a live PaintColumn, EmitLoops can also fall back to its Anchor.
        if (paintSeamPin is null && paintColumnId is int pinId)
        {
            for (int li = plan.Layers.Length - 1; li >= 0 && paintSeamPin is null; li--)
            {
                var t = plan.Layers[li].Trees.FirstOrDefault(x => x.Id == pinId || x.PaintColumn);
                if (t is not null) paintSeamPin = t.Anchor;
            }
        }
        if (paintSeamPin is { } pinXY)
        {
            foreach (var lp in plan.Layers)
                lp.SeamPinXY = pinXY;
            plan.UncoveredLog.Add(
                $"[formbound] seam-pin=({pinXY.X:0.#},{pinXY.Y:0.#}) locked for {plan.Layers.Length} layers");
            System.Console.WriteLine(
                $"[formbound] seam-pin=({pinXY.X:0.#},{pinXY.Y:0.#}) locked for {plan.Layers.Length} layers");
        }

        int live = 0;
        foreach (var lp in plan.Layers) live += lp.Trees.Count;

        plan.DemandFlags = demandFlags;
        plan.TreesBorn = nextTreeId;
        plan.MeshVetoes = meshVetoes;
        plan.OrphanedLineages = orphaned.Count;
        plan.UncoveredSamples = uncoveredTotal;
        plan.InheritSkips = inheritSkips;
        plan.InheritReseeds = inheritReseeds;
        plan.AuditExtensions = auditExtensions;
        plan.LiveSlots = live;
        plan.BarMm = barLen;
        plan.SpacingMm = spacing;
        plan.MultiPlanar = frames is not null;

        if (uncoveredTotal > 0)
            plan.UncoveredLog.Add(
                $"[formbound] place-fails: noAnchor={PfNoAnchor} barReach={PfBarReach} " +
                $"noFrame={PfNoFrame} covered={PfCovered} elbowCrowd={PfElbow}");

        // Stdout for headless/tests; App surfaces the same line via Toolpath.FormboundStats.
        System.Console.WriteLine(plan.ToStats().ToLogLine());
        foreach (var line in plan.UncoveredLog)
            if (line.Contains("place-fails")) System.Console.WriteLine(line);

        return plan;
    }

    // -- Tree operations ---------------------------------------------------------

    /// <summary>
    /// Lock the paint-column mouth to the wall nearest <paramref name="aim"/>
    /// (ColumnFoot / seam) and rebuild a short inward trunk. Used every layer so
    /// the perimeter break stacks with the yellow seam, not under the support bar.
    /// </summary>
    private static bool SnapPaintMouthToAim(
        LightningTree tree, Vector2 aim,
        PathsD region, PathsD core,
        float bead, float barLen, float maxStep)
    {
        tree.External = false;
        tree.Cavity = false;
        if (region.Count == 0) return false;

        var prev = tree.Anchor;
        var newAnchor = ClosestOnRegionBoundary(region, aim);
        tree.Anchor = newAnchor;

        if (!TryBoundaryFrame(region, newAnchor, out var tangent, out var inward))
        {
            if (tree.Branches.Count > 0 && tree.Branches[0].Centerline.Count > 0)
                tree.Branches[0].Centerline[0] = newAnchor;
            return Vector2.DistanceSquared(prev, newAnchor) > 1e-6f;
        }

        // Preserve prior trunk depth (within MaxStep), rebuild bars if any.
        float prevDepth = MathF.Max(bead * 1.5f, maxStep * 2f);
        float prevBarL = 0f, prevBarR = 0f;
        Vector2 prevElbow = newAnchor + inward * prevDepth;
        if (tree.Branches.Count > 0 && tree.Branches[0].Centerline.Count >= 2)
        {
            prevElbow = tree.Branches[0].Centerline[^1];
            prevDepth = Vector2.Distance(tree.Branches[0].Centerline[0], prevElbow);
            prevDepth = MathF.Max(bead * 0.75f, prevDepth);
            for (int bi = 1; bi < tree.Branches.Count; bi++)
            {
                var br = tree.Branches[bi];
                if (br.ParentBranch != 0 || br.Centerline.Count < 2) continue;
                float bl = Vector2.Distance(br.Centerline[0], br.Centerline[^1]);
                float side = Vector2.Dot(br.Centerline[^1] - prevElbow, tangent);
                if (side < 0) prevBarL = MathF.Max(prevBarL, bl);
                else prevBarR = MathF.Max(prevBarR, bl);
            }
        }

        float wantDepth = MathF.Min(prevDepth, MathF.Max(barLen * 4f, bead * 12f));
        wantDepth = MathF.Max(bead * 0.75f, wantDepth);
        var elbow = newAnchor + inward * wantDepth;
        if (!InsideRegion(core, elbow))
            elbow = ClosestOnRegionBoundary(core, elbow);
        if (!SegmentInsideRegion(region, newAnchor, elbow, bead))
        {
            elbow = newAnchor + inward * MathF.Max(bead * 0.75f, maxStep);
            if (!InsideRegion(core, elbow))
                elbow = ClosestOnRegionBoundary(core, elbow);
            if (!SegmentInsideRegion(region, newAnchor, elbow, bead))
            {
                tree.Branches.Clear();
                tree.Branches.Add(new LightningBranch([newAnchor, newAnchor + inward * bead * 0.5f]));
                return true;
            }
        }

        float halfBar = MathF.Max(barLen * 0.5f, bead * 2f);
        tree.Branches.Clear();
        tree.Branches.Add(new LightningBranch([newAnchor, elbow]));
        if (prevBarL >= bead * 0.5f || prevBarR >= bead * 0.5f)
        {
            float reachL = MaxBarReach(elbow, -tangent,
                MathF.Min(halfBar, MathF.Max(prevBarL, bead)), region, bead);
            float reachR = MaxBarReach(elbow,  tangent,
                MathF.Min(halfBar, MathF.Max(prevBarR, bead)), region, bead);
            if (reachL >= bead * 0.5f)
                tree.Branches.Add(new LightningBranch([elbow, elbow - tangent * reachL])
                    { ParentBranch = 0, ParentNode = 1 });
            if (reachR >= bead * 0.5f)
                tree.Branches.Add(new LightningBranch([elbow, elbow + tangent * reachR])
                    { ParentBranch = 0, ParentNode = 1 });
        }
        return true;
    }

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

    /// <summary>
    /// Grow an existing same-side buttress toward an uncovered demand sample by at
    /// most <paramref name="maxStep"/>. Prefer this over birthing a new short tree
    /// so columns stay continuous and T bars fill along the rim gradually.
    /// </summary>
    private static bool TryExtendExistingButtress(
        List<LightningTree> trees, Vector2 pt, PathsD region, PathsD core,
        float bead, float barLen, float maxStep, float coverRadius, float sameSideMax)
    {
        if (trees.Count == 0 || maxStep <= 0f) return false;
        var home = ClosestOnRegionBoundary(region, pt);
        float sideR2 = sameSideMax * sameSideMax;

        LightningTree? best = null;
        float bestScore = float.MaxValue;
        foreach (var t in trees)
        {
            if (t.External || t.Branches.Count == 0) continue;
            if (Vector2.DistanceSquared(t.Anchor, home) > sideR2) continue;
            float d = MinDistToTree(t, pt);
            // Prefer closer geometry; slight bias toward trees already near the wall foot.
            float score = d + 0.15f * Vector2.Distance(t.Anchor, home);
            if (score < bestScore) { bestScore = score; best = t; }
        }
        if (best is null) return false;
        // Too far to close in one MaxStep growth budget — let birth handle it.
        // Cap the "barLen" leg so a multi-metre painted run (when barLen was
        // mistakenly set to full path length) cannot force extend-always-true.
        float extendCap = MathF.Max(coverRadius + maxStep * 4f,
            MathF.Min(MathF.Max(barLen * 0.6f, bead * 4f), bead * 24f));
        if (bestScore > extendCap)
            return false;

        var trunk = best.Branches[0].Centerline;
        if (trunk.Count < 2) return false;
        var anchor = best.Anchor;
        trunk[0] = anchor;
        var elbow = trunk[^1];
        if (!TryBoundaryFrame(region, anchor, out var tangent, out var inward))
            return false;

        // Deepen trunk toward the sample (projected on inward normal), ≤ maxStep.
        float curDepth = Vector2.Distance(anchor, elbow);
        float wantDepth = MathF.Max(curDepth, Vector2.Dot(pt - anchor, inward));
        float maxTrunk = MathF.Max(barLen * 12f, bead * 50f);
        wantDepth = MathF.Min(wantDepth, curDepth + maxStep);
        wantDepth = MathF.Min(wantDepth, maxTrunk);
        wantDepth = MathF.Max(wantDepth, bead * 0.75f);
        var elbowTarget = anchor + inward * wantDepth;
        if (!InsideRegion(core, elbowTarget))
            elbowTarget = ClosestOnRegionBoundary(core, elbowTarget);
        var newElbow = PullWithin(elbow, elbowTarget, maxStep);
        if (!InsideRegion(core, newElbow))
            newElbow = ClosestOnRegionBoundary(core, newElbow);
        if (!SegmentInsideRegion(region, anchor, newElbow, bead))
            return false;

        // Preserve / grow bars toward the sample's lateral offset.
        float halfBar = MathF.Max(barLen * 0.5f, bead * 2f);
        float prevBarL = 0f, prevBarR = 0f;
        for (int bi = 1; bi < best.Branches.Count; bi++)
        {
            var br = best.Branches[bi];
            if (br.ParentBranch != 0 || br.Centerline.Count < 2) continue;
            float bl = Vector2.Distance(br.Centerline[0], br.Centerline[^1]);
            float side = Vector2.Dot(br.Centerline[^1] - elbow, tangent);
            if (side < 0) prevBarL = MathF.Max(prevBarL, bl);
            else prevBarR = MathF.Max(prevBarR, bl);
        }
        float sidePt = Vector2.Dot(pt - newElbow, tangent);
        if (sidePt < 0)
            prevBarL = MathF.Max(prevBarL, MathF.Min(halfBar, prevBarL + maxStep + MathF.Abs(sidePt) * 0.25f));
        else
            prevBarR = MathF.Max(prevBarR, MathF.Min(halfBar, prevBarR + maxStep + MathF.Abs(sidePt) * 0.25f));
        // If neither bar existed yet, open a tiny seed toward the sample.
        if (prevBarL < bead * 0.5f && prevBarR < bead * 0.5f)
        {
            float seed = MathF.Min(halfBar, MathF.Max(maxStep, bead));
            if (sidePt < 0) prevBarL = seed; else prevBarR = seed;
        }
        // Grow each side by at most maxStep from previous length.
        float wantL = MathF.Min(halfBar, (prevBarL > 0 ? prevBarL : 0) + maxStep);
        float wantR = MathF.Min(halfBar, (prevBarR > 0 ? prevBarR : 0) + maxStep);
        // Keep prior lengths when sample is on the other side.
        if (prevBarL > 0) wantL = MathF.Max(wantL, MathF.Min(halfBar, prevBarL));
        if (prevBarR > 0) wantR = MathF.Max(wantR, MathF.Min(halfBar, prevBarR));

        float reachL = MaxBarReach(newElbow, -tangent, wantL, region, bead);
        float reachR = MaxBarReach(newElbow,  tangent, wantR, region, bead);

        best.Branches.Clear();
        best.Branches.Add(new LightningBranch([anchor, newElbow]));
        if (reachL >= bead * 0.5f)
            best.Branches.Add(new LightningBranch([newElbow, newElbow - tangent * reachL])
                { ParentBranch = 0, ParentNode = 1 });
        if (reachR >= bead * 0.5f)
            best.Branches.Add(new LightningBranch([newElbow, newElbow + tangent * reachR])
                { ParentBranch = 0, ParentNode = 1 });
        FilletTreeCorners(best, bead);
        return CoveredBySameSide(trees, pt, coverRadius, sameSideMax, region)
            || MinDistToTree(best, pt) < bestScore - 1e-3f;
    }

    private static float MinDistToTree(LightningTree t, Vector2 pt)
    {
        float best = float.MaxValue;
        foreach (var b in t.Branches)
        {
            var line = b.Centerline;
            for (int i = 1; i < line.Count; i++)
            {
                float d = MathF.Sqrt(DistToSegmentSq(pt, line[i - 1], line[i]));
                if (d < best) best = d;
            }
            if (line.Count == 1)
                best = MathF.Min(best, Vector2.Distance(pt, line[0]));
        }
        return best;
    }

    /// <summary>
    /// Grow an existing Bridge finger toward demand by ≤ <paramref name="maxStep"/>.
    /// Prefer this over re-birthing a full-length dual-wall hairpin on every layer of a
    /// continuous overhang — full re-birth mid-stack creates floating rectangular
    /// free-edge islands with no MaxStep support column underneath.
    /// Returns true when a matching column was found (even if already at target length),
    /// so callers skip a full-depth re-seed on that mouth.
    /// </summary>
    private static bool TryExtendExistingBridge(
        List<LightningTree> trees, Vector2 demandPt,
        PathsD region, PathsD core,
        float bead, float maxStep, float mouthCatchR,
        bool wantExternal)
    {
        if (trees.Count == 0 || maxStep <= 1e-6f) return false;
        var home = ClosestOnRegionBoundary(region, demandPt);

        // Only claim RESIDUAL demand of an inherited column (tip already near demand
        // after RetractLeafTips). Distant demand is a new founding → full birth.
        // Matching solely by mouth claimed neighbour columns and starved coverage.
        float residualR = maxStep * 2.5f + bead * 0.5f;

        LightningTree? best = null;
        float bestScore = float.MaxValue;
        foreach (var t in trees)
        {
            if (t.External != wantExternal) continue;
            if (t.Cavity) continue;
            if (t.Branches.Count == 0 || t.Branches[0].Centerline.Count < 2) continue;
            float dMouth = Vector2.Distance(t.Anchor, home);
            float dGeom = MinDistToTree(t, demandPt);
            var tip0 = t.Branches[0].Centerline[^1];
            float dTip = Vector2.Distance(tip0, demandPt);
            // Residual of this column after retract: tip/geometry still near demand,
            // and mouth still on the same local wall run.
            if (dGeom > residualR && dTip > residualR) continue;
            if (dMouth > mouthCatchR * 1.35f) continue;
            float score = MathF.Min(dGeom, dTip);
            if (score < bestScore) { bestScore = score; best = t; }
        }
        if (best is null) return false;

        var line = best.Branches[0].Centerline;
        var anchor = best.Anchor;
        line[0] = anchor;
        var tip = line[^1];
        float curLen = Vector2.Distance(anchor, tip);

        Vector2 targetTip = demandPt;
        if (!wantExternal)
        {
            if (!InsideRegion(core, targetTip))
                targetTip = ClosestOnRegionBoundary(core, targetTip);
        }

        // Prefer restore along the existing trunk (undo retract) before aiming at a
        // new sample — keeps the free-edge stack within MaxStep of the layer above.
        var trunkDir = tip - anchor;
        float trunkLen = trunkDir.Length();
        Vector2 aim;
        if (trunkLen > bead * 0.2f)
        {
            trunkDir /= trunkLen;
            var toDemand = targetTip - anchor;
            float td = toDemand.Length();
            var demandDir = td > 1e-4f ? toDemand / td : trunkDir;
            // Blend: mostly keep trunk direction, slight pull toward demand.
            aim = Vector2.Normalize(trunkDir * 0.75f + demandDir * 0.25f);
        }
        else
        {
            var toDemand = targetTip - anchor;
            float td = toDemand.Length();
            if (td < bead * 0.25f) return true;
            aim = toDemand / td;
        }

        // Deepen by at most maxStep; never shorten (RetractLeafTips already did).
        float targetLen = Vector2.Distance(anchor, targetTip);
        float newLen = MathF.Min(targetLen, curLen + maxStep);
        newLen = MathF.Max(newLen, curLen);
        var newTip = anchor + aim * newLen;

        // Hard MaxStep ball around the post-retract tip (retraction invariant).
        float tipMove = Vector2.Distance(newTip, tip);
        if (tipMove > maxStep + 1e-3f)
            newTip = tip + (newTip - tip) * (maxStep / tipMove);

        if (!wantExternal)
        {
            if (!InsideRegion(core, newTip))
                newTip = ClosestOnRegionBoundary(core, newTip);
            if (!SegmentInsideRegion(region, anchor, newTip, bead))
                return true; // claim column, keep prior tip
        }

        best.Branches[0].Centerline.Clear();
        best.Branches[0].Centerline.Add(anchor);
        best.Branches[0].Centerline.Add(newTip);
        for (int bi = best.Branches.Count - 1; bi >= 1; bi--)
            best.Branches.RemoveAt(bi);

        return true;
    }

    /// <summary>
    /// Same-mouth MaxStep deepen when residual tip-match failed (column fully
    /// retracted past residualR, or demand jumped). Still refuses a full-depth
    /// re-seed: grows at most one MaxStep toward demand so the free-edge stack
    /// stays printable bottom-up.
    /// </summary>
    private static bool TryDeepenNearbyBridgeMouth(
        List<LightningTree> trees, Vector2 demandPt,
        PathsD region, PathsD core,
        float bead, float maxStep, float mouthCatchR,
        bool wantExternal)
    {
        if (trees.Count == 0 || maxStep <= 1e-6f) return false;
        var home = ClosestOnRegionBoundary(region, demandPt);

        LightningTree? best = null;
        float bestMouth = float.MaxValue;
        foreach (var t in trees)
        {
            if (t.External != wantExternal || t.Cavity) continue;
            if (t.Branches.Count == 0 || t.Branches[0].Centerline.Count < 2) continue;
            float dMouth = Vector2.Distance(t.Anchor, home);
            if (dMouth > mouthCatchR) continue;
            if (dMouth < bestMouth) { bestMouth = dMouth; best = t; }
        }
        if (best is null) return false;

        var anchor = best.Anchor;
        var tip = best.Branches[0].Centerline[^1];
        float curLen = Vector2.Distance(anchor, tip);

        Vector2 targetTip = demandPt;
        if (!wantExternal && !InsideRegion(core, targetTip))
            targetTip = ClosestOnRegionBoundary(core, targetTip);

        var aim = targetTip - anchor;
        float aimLen = aim.Length();
        if (aimLen < bead * 0.25f) return true;
        aim /= aimLen;

        // Prefer keep trunk direction for stack continuity.
        var trunk = tip - anchor;
        float tLen = trunk.Length();
        if (tLen > bead * 0.2f)
        {
            trunk /= tLen;
            aim = Vector2.Normalize(trunk * 0.7f + aim * 0.3f);
        }

        float targetLen = Vector2.Distance(anchor, targetTip);
        float newLen = MathF.Min(targetLen, curLen + maxStep);
        newLen = MathF.Max(newLen, curLen);
        // Brand-new residual after full retract: allow at least a MaxStep stub.
        if (curLen < bead * 0.5f)
            newLen = MathF.Min(targetLen, MathF.Max(newLen, maxStep));

        var newTip = anchor + aim * newLen;
        float tipMove = Vector2.Distance(newTip, tip);
        if (curLen >= bead * 0.5f && tipMove > maxStep + 1e-3f)
            newTip = tip + (newTip - tip) * (maxStep / tipMove);

        if (!wantExternal)
        {
            if (!InsideRegion(core, newTip))
                newTip = ClosestOnRegionBoundary(core, newTip);
            if (!SegmentInsideRegion(region, anchor, newTip, bead))
                return true;
        }

        best.Branches[0].Centerline.Clear();
        best.Branches[0].Centerline.Add(anchor);
        best.Branches[0].Centerline.Add(newTip);
        for (int bi = best.Branches.Count - 1; bi >= 1; bi--)
            best.Branches.RemoveAt(bi);
        return true;
    }

    /// <summary>Try to place one Formbound Buttress T at sample <paramref name="si"/>.
    /// Returns false when mesh veto / topology / proximity rejects it.</summary>
    /// <param name="maxAnchorDist">
    /// Cap on wall-mouth distance from the demand mid (e.g. 2× layer height for
    /// painted Bridge). <see cref="float.MaxValue"/> = no extra cap.
    /// </param>
    /// <param name="faceToward">
    /// Optional aim point (support selection). Wall mouth is chosen so the trunk
    /// faces this direction from the mid sample.
    /// </param>
    private static bool TryAddButtressAt(
        List<Vector2> samples, int si, float sampleStep, int runCount, int runStart,
        PathsD region, PathsD envelope, PathsD core,
        PathsD anchorPaths, PathsD anchorInterior, PathsD anchorExterior,
        bool preferInterior, SliceSettings settings, float bead, float barLen,
        float coverRadius, float sameSideMax,
        LightningLayerPlan layerPlan, ref int nextTreeId,
        Func<int, Vector2, bool>? solidAt, int layerAbove, PathsD regionAbove,
        List<Vector2> rawSamples,
        float maxAnchorDist = float.MaxValue,
        Vector2? faceToward = null,
        PaintSupportSide supportSide = PaintSupportSide.Inside)
    {
        if (!PassesMeshVetoAt(solidAt, layerAbove, regionAbove, rawSamples, si, bead))
            return false;

        var tree = TryBuildButtressT(
            samples, si, sampleStep, runCount, runStart,
            region, envelope, core, anchorPaths, anchorInterior, anchorExterior,
            preferInterior, settings.LightningAnchorInterior,
            settings.LightningAnchorExterior,
            bead, barLen, nextTreeId, maxAnchorDist, faceToward, supportSide);
        if (tree is null) return false;

        // Proximity: same-side only. A T on the opposite wall of a channel must not
        // block this placement even if its bar ends near our elbow.
        var elbow = tree.Branches[0].Centerline[^1];
        if (CoveredBySameSide(layerPlan.Trees, elbow, coverRadius, sameSideMax, region))
        { PfCovered++; return false; }
        // Crowding may never exceed the COVERAGE radius: an elbow rejected as
        // "too close" must by definition already be covered, or samples in the
        // annulus between the two radii deadlock — unplaceable yet uncovered
        // (the exact hole the Drone deck ledge fell into). Coverage beats tidiness.
        if (TooCloseToElbowSameSide(layerPlan.Trees, tree.Anchor, elbow,
                MathF.Min(bead * 1.25f, coverRadius), sameSideMax))
        { PfElbow++; return false; }

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
    /// For painted Bridge demand: mouth is at the mid sample; wall break is the
    /// closest valid perimeter point within <paramref name="maxAnchorDist"/> that
    /// faces <paramref name="faceToward"/> (support selection).
    /// </summary>
    private static LightningTree? TryBuildButtressT(
        List<Vector2> samples, int si, float sampleStep, int runCount, int runStart,
        PathsD region, PathsD envelope, PathsD core,
        PathsD anchorPaths, PathsD anchorInterior, PathsD anchorExterior,
        bool preferInterior, bool allowInterior, bool allowExterior,
        float bead, float barLen, int id,
        float maxAnchorDist = float.MaxValue,
        Vector2? faceToward = null,
        PaintSupportSide supportSide = PaintSupportSide.Inside)
    {
        // Birth = FULL support geometry at the TOP of the overhang column.
        // Lower layers inherit and RetractButtress by MaxStep so bottom-up print
        // grows wall→stub→longer trunk→opening bar→full T within the overhang angle.
        var sPt = samples[si];
        // Interior = notch in material (elbow clamped to core); Cavity = over a
        // modeled internal void (tube UNIONED into the hole — elbow AT the demand,
        // structural growth); Exterior = sacrificial fin outside the part.
        var space = ClassifyPoint(region, envelope, sPt);
        bool external = space == DemandSpace.Exterior;
        bool cavity   = space == DemandSpace.Cavity;
        bool offMaterial = space != DemandSpace.Interior;
        var keep = offMaterial ? region : core;

        var homeWall = ClosestOnRegionBoundary(region, sPt);
        float maxTrunk = MathF.Max(barLen * 12f, bead * 50f);
        if (maxAnchorDist < float.MaxValue * 0.5f)
            maxTrunk = MathF.Min(maxTrunk, MathF.Max(maxAnchorDist, bead * 2f));

        // Elbow under demand sample (or projected into core) — mid of target/support line.
        Vector2 elbow = offMaterial
            ? sPt
            : InsideRegion(core, sPt) ? sPt : ClosestOnRegionBoundary(core, sPt);

        // Paint marks sit ON the perimeter path. Elbow must sit a few beads INSIDE
        // the solid so a short wall→elbow trunk is valid; otherwise every candidate
        // is rejected as dElbow < bead/2 and the paint column never births.
        bool paintBirth = maxAnchorDist < float.MaxValue * 0.5f;
        bool forceOutside = paintBirth && PaintSupportSideUtil.IsOutside(supportSide);
        // Per-selection Outside: sacrificial exterior fin (unioned outside the wall).
        if (forceOutside
            && TryBoundaryFrame(region, homeWall, out _, out var paintOutward))
        {
            // Frame "inward" points into solid; exterior fin grows the opposite way.
            var outward = -paintOutward;
            float wantDepth = MathF.Min(maxTrunk,
                MathF.Max(bead * 2.5f, Vector2.Distance(homeWall, sPt)));
            if (maxAnchorDist < float.MaxValue * 0.5f)
                wantDepth = MathF.Min(wantDepth, MathF.Max(bead * 2f, maxAnchorDist));
            elbow = homeWall + outward * wantDepth;
            external = true;
            cavity = false;
            offMaterial = true;
            keep = region;
        }
        // Target Support Inside: NEVER leave the elbow in free air — grow wall→core.
        else if (paintBirth && offMaterial
            && TryBoundaryFrame(region, homeWall, out _, out var paintInward))
        {
            float wantDepth = MathF.Min(maxTrunk,
                MathF.Max(bead * 2.5f, Vector2.Distance(homeWall, sPt)));
            // Cap initial trunk so a single layer cannot claim more reach than the
            // caller allowed (maxAnchorDist / maxPrintableReach).
            if (maxAnchorDist < float.MaxValue * 0.5f)
                wantDepth = MathF.Min(wantDepth, MathF.Max(bead * 2f, maxAnchorDist));
            var inset = homeWall + paintInward * wantDepth;
            if (InsideRegion(core, inset))
                elbow = inset;
            else if (InsideRegion(region, inset))
                elbow = ClosestOnRegionBoundary(core, inset);
            else
                elbow = ClosestOnRegionBoundary(core, homeWall + paintInward * bead * 2.5f);
            external = false;
            cavity = false;
            offMaterial = false;
            keep = core;
        }
        else if (!offMaterial && paintBirth
            && Vector2.Distance(homeWall, elbow) < bead * 2.5f
            && TryBoundaryFrame(region, homeWall, out _, out var inwardN))
        {
            var inset = homeWall + inwardN * MathF.Max(bead * 2.5f, bead * 2f);
            if (InsideRegion(core, inset) || InsideRegion(region, inset))
                elbow = InsideRegion(core, inset) ? inset : ClosestOnRegionBoundary(core, inset);
            else
                elbow = ClosestOnRegionBoundary(core, homeWall + inwardN * bead);
        }

        // Bar follows the UNSUPPORTED RUN (ledge edge). Prefer covering the full
        // painted/unsupported run length when the caller passed barLen ≥ run length
        // (manual Bridge demand does this so a selected path segment gets a full T).
        float halfBar = MathF.Max(barLen * 0.5f, bead * 2f);
        // From this sample, also allow walking the remaining run distance so a
        // mid-run birth opens a T that reaches both ends of the selected segment.
        // Paint multi-T births pass a LOCAL run window — walk that window only.
        // Never let halfBar explode past barLen for paint (that made one T claim
        // an entire 1m+ selection and blocked further births).
        if (runCount > 1 && samples.Count > 0)
        {
            float toStart = 0f, toEnd = 0f;
            for (int j = si - 1; j >= runStart; j--)
                toStart += Vector2.Distance(samples[j + 1], samples[j]);
            int runEnd = Math.Min(samples.Count - 1, runStart + runCount - 1);
            for (int j = si; j < runEnd; j++)
                toEnd += Vector2.Distance(samples[j], samples[j + 1]);
            float walk = MathF.Max(toStart, toEnd) + bead;
            if (paintBirth)
                walk = MathF.Min(walk, halfBar * 1.15f);
            halfBar = MathF.Max(halfBar, walk);
        }
        var left  = WalkAlongRun(samples, si, runStart, runCount, sampleStep, -halfBar, keep, bead, offMaterial);
        var right = WalkAlongRun(samples, si, runStart, runCount, sampleStep,  halfBar, keep, bead, offMaterial);

        if (Vector2.Distance(elbow, left) < bead || Vector2.Distance(elbow, right) < bead)
        {
            if (TryBoundaryFrame(region, homeWall, out var tan, out _))
            {
                if (Vector2.Distance(elbow, left) < bead)
                    left = elbow - tan * MaxBarReach(elbow, -tan, halfBar, region, bead);
                if (Vector2.Distance(elbow, right) < bead)
                    right = elbow + tan * MaxBarReach(elbow, tan, halfBar, region, bead);
            }
        }
        if (!offMaterial)
        {
            left  = ClampBarEnd(elbow, left, core, region, bead);
            right = ClampBarEnd(elbow, right, core, region, bead);
        }

        // Preferred trunk direction: wall → elbow should face the support selection.
        // faceToward is the support-side end of the painted/bridge run.
        Vector2 faceDir = default;
        bool hasFace = false;
        if (faceToward is { } ft)
        {
            faceDir = ft - elbow;
            float fl2 = faceDir.LengthSquared();
            if (fl2 > 1e-8f)
            {
                faceDir /= MathF.Sqrt(fl2);
                hasFace = true;
            }
        }

        var candidates = new List<Vector2>();
        void AddClosest(PathsD paths)
        {
            if (paths.Count == 0) return;
            candidates.Add(ClosestOnRegionBoundary(paths, elbow));
        }
        void AddNearby(PathsD paths)
        {
            if (paths.Count == 0 || maxAnchorDist >= float.MaxValue * 0.5f) return;
            CollectBoundaryCandidatesNear(paths, elbow, maxAnchorDist, bead, candidates);
        }
        if (offMaterial)
        {
            AddClosest(region);
            AddNearby(region);
        }
        else
        {
            if (preferInterior)
            {
                if (allowInterior) { AddClosest(anchorInterior); AddNearby(anchorInterior); }
                if (allowExterior) { AddClosest(anchorExterior); AddNearby(anchorExterior); }
            }
            else
            {
                AddClosest(anchorPaths); AddNearby(anchorPaths);
                if (allowInterior) { AddClosest(anchorInterior); AddNearby(anchorInterior); }
                if (allowExterior) { AddClosest(anchorExterior); AddNearby(anchorExterior); }
            }
        }
        candidates.Add(homeWall);
        AddNearby(region);

        Vector2 anchor = default;
        bool found = false;
        float bestScore = float.MaxValue;
        // Paint columns: allow longer wall reach — marks are on the path, wall is local.
        float maxDist = paintBirth
            ? MathF.Max(maxAnchorDist, MathF.Max(maxTrunk, bead * 20f))
            : (maxAnchorDist < float.MaxValue * 0.5f ? maxAnchorDist : maxTrunk);
        float minTrunk = paintBirth ? bead * 0.2f : bead * 0.5f;

        void ScoreCandidates(bool enforceFace)
        {
            foreach (var cand in candidates)
            {
                float dElbow = Vector2.Distance(cand, elbow);
                if (dElbow < minTrunk) continue;
                if (dElbow > maxDist) continue;
                if (!offMaterial && !SegmentInsideRegion(region, cand, elbow, bead)) continue;
                if (cavity && !SegmentInsideVoid(region, cand, elbow, bead)) continue;

                var trunkDir = elbow - cand;
                float tl2 = trunkDir.LengthSquared();
                if (tl2 < 1e-10f) continue;
                trunkDir /= MathF.Sqrt(tl2);

                float faceAlign = 0f;
                if (enforceFace && hasFace)
                {
                    faceAlign = Vector2.Dot(trunkDir, faceDir);
                    // Soft facing for paint — hard reject only clear opposite walls.
                    if (faceAlign < -0.5f) continue;
                }

                float score = dElbow - faceAlign * maxDist * 0.35f;
                if (score < bestScore) { bestScore = score; anchor = cand; found = true; }
            }
        }

        // Prefer facing when requested; if that rejects every wall, retry without face
        // so a paint column still births under the selection.
        ScoreCandidates(enforceFace: hasFace);
        if (!found && hasFace)
            ScoreCandidates(enforceFace: false);
        if (!found) { PfNoAnchor++; return null; }

        // T at birth (full). RetractButtress shortens trunk+bars layer-by-layer going down.
        var tree = new LightningTree { Id = id, Anchor = anchor, External = external, Cavity = cavity };
        tree.Branches.Add(new LightningBranch([anchor, elbow]));
        bool hasBar = false;
        if (Vector2.Distance(elbow, left) >= bead * 0.75f)
        {
            tree.Branches.Add(new LightningBranch([elbow, left]) { ParentBranch = 0, ParentNode = 1 });
            hasBar = true;
        }
        if (Vector2.Distance(elbow, right) >= bead * 0.75f)
        {
            tree.Branches.Add(new LightningBranch([elbow, right]) { ParentBranch = 0, ParentNode = 1 });
            hasBar = true;
        }
        if (!hasBar)
        {
            // Region too thin for any bar (wing-tip slivers, narrow flare edges —
            // multi-planar tilt makes this hit one side of a symmetric part): a
            // trunk-only stub still covers demand within coverRadius of the elbow,
            // so birth it rather than leaving the tip unsupported. Counters kept
            // for diagnostics only.
            if (!TryBoundaryFrame(region, anchor, out var tan2, out _))
            {
                PfNoFrame++;
            }
            else
            {
                float reach = MaxBarReach(elbow, tan2, halfBar, region, bead);
                if (reach < bead * 0.75f)
                {
                    PfBarReach++;
                }
                else
                {
                    tree.Branches.Add(new LightningBranch([elbow, elbow + tan2 * reach])
                        { ParentBranch = 0, ParentNode = 1 });
                    float reachL = MaxBarReach(elbow, -tan2, halfBar, region, bead);
                    if (reachL >= bead * 0.75f)
                        tree.Branches.Add(new LightningBranch([elbow, elbow - tan2 * reachL])
                            { ParentBranch = 0, ParentNode = 1 });
                }
            }
        }
        FilletTreeCorners(tree, bead);
        return tree;
    }

    /// <summary>How far we can go from <paramref name="origin"/> along <paramref name="dir"/>
    /// while the segment stays inside the region (binary search).</summary>
    private static float MaxBarReach(Vector2 origin, Vector2 dir, float want, PathsD region, float bead)
    {
        float len = dir.Length();
        if (len < 1e-8f || want < 1e-3f) return 0f;
        dir /= len;
        if (!SegmentInsideRegion(region, origin, origin + dir * MathF.Min(want, bead * 0.5f), bead))
            return 0f;
        float lo = 0f, hi = want;
        for (int k = 0; k < 14; k++)
        {
            float mid = 0.5f * (lo + hi);
            if (SegmentInsideRegion(region, origin, origin + dir * mid, bead)) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Pull a bar tip back toward the elbow until the segment is inside solid,
    /// without snapping to the wall (which collapses the T into a stub finger).</summary>
    private static Vector2 ClampBarEnd(
        Vector2 elbow, Vector2 tip, PathsD core, PathsD region, float bead)
    {
        if (InsideRegion(core, tip) && SegmentInsideRegion(region, elbow, tip, bead))
            return tip;
        var dir = tip - elbow;
        float want = dir.Length();
        if (want < 1e-6f) return elbow;
        float reach = MaxBarReach(elbow, dir, want, region, bead);
        if (reach < bead * 0.5f) return elbow;
        var end = elbow + dir * (reach / want);
        if (!InsideRegion(core, end))
            end = ClosestOnRegionBoundary(core, end);
        // If closest-on-core collapsed toward the wall past the elbow depth, keep reach tip.
        if (Vector2.Distance(elbow, end) < bead * 0.5f)
            end = elbow + Vector2.Normalize(dir) * reach;
        return end;
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
    /// Returns false when no valid wall-attached T can be placed — caller should
    /// orphan the lineage (do not keep a remapped diagonal trunk).
    /// </summary>
    private static bool ReAimButtress(
        LightningTree tree, PathsD region, PathsD core, float bead, float barLen,
        float maxStep, List<Vector2> prevTips)
    {
        if (tree.Branches.Count == 0) return false;
        var trunk = tree.Branches[0].Centerline;
        if (trunk.Count < 2) return false;

        var anchor = tree.Anchor;
        if (!TryBoundaryFrame(region, anchor, out var tangent, out var inward))
            return false;

        // Preserve RETRACTED trunk length (growth budget). Never force full barLen depth.
        var prevElbow = trunk.Count > 1 ? trunk[^1] : anchor + inward * bead;
        float trunkLen = Vector2.Distance(anchor, prevElbow);
        if (trunkLen < bead * 0.35f) return false; // fully retracted this layer
        float maxTrunk = MathF.Max(barLen * 12f, bead * 50f);
        if (trunkLen > maxTrunk) trunkLen = maxTrunk;

        // Elbow: same depth as retracted tip, re-aimed along wall normal, ≤ maxStep from prev.
        var elbowTarget = anchor + inward * trunkLen;
        if (!InsideRegion(core, elbowTarget))
            elbowTarget = ClosestOnRegionBoundary(core, elbowTarget);
        var elbow = PullWithin(prevElbow, elbowTarget, maxStep);
        if (!InsideRegion(core, elbow))
            elbow = ClosestOnRegionBoundary(core, elbow);
        if (!SegmentInsideRegion(region, anchor, elbow, bead))
            return false;

        // Bar targets: match previous bar lengths (after retract), along wall tangent.
        float halfBar = MathF.Max(barLen * 0.5f, bead * 2f);
        float prevBarL = 0f, prevBarR = 0f;
        for (int bi = 1; bi < tree.Branches.Count; bi++)
        {
            var br = tree.Branches[bi];
            if (br.ParentBranch != 0 || br.Centerline.Count < 2) continue;
            float bl = Vector2.Distance(br.Centerline[0], br.Centerline[^1]);
            // Classify by which side of tangent the tip lies.
            float side = Vector2.Dot(br.Centerline[^1] - prevElbow, tangent);
            if (side < 0) prevBarL = MathF.Max(prevBarL, bl);
            else prevBarR = MathF.Max(prevBarR, bl);
        }
        if (prevBarL < bead * 0.5f && prevBarR < bead * 0.5f)
        {
            // No bars yet (early growth): only open a tiny bar ≤ maxStep so it grows
            // over subsequent layers rather than jumping to full halfBar.
            prevBarL = prevBarR = MathF.Min(halfBar, MathF.Max(maxStep, bead));
        }
        float reachL = MaxBarReach(elbow, -tangent, MathF.Min(halfBar, prevBarL + maxStep), region, bead);
        float reachR = MaxBarReach(elbow,  tangent, MathF.Min(halfBar, prevBarR + maxStep), region, bead);
        var leftTarget  = elbow - tangent * reachL;
        var rightTarget = elbow + tangent * reachR;

        Vector2 prevLeft = prevTips.Count > 0 ? prevTips[0] : leftTarget;
        Vector2 prevRight = prevTips.Count > 1 ? prevTips[1] : (prevTips.Count > 0 ? prevTips[0] : rightTarget);
        if (prevTips.Count >= 2)
        {
            float d0 = Vector2.Distance(prevTips[0], leftTarget) + Vector2.Distance(prevTips[1], rightTarget);
            float d1 = Vector2.Distance(prevTips[0], rightTarget) + Vector2.Distance(prevTips[1], leftTarget);
            if (d1 < d0) (prevLeft, prevRight) = (prevTips[1], prevTips[0]);
            else (prevLeft, prevRight) = (prevTips[0], prevTips[1]);
        }

        var left  = PullWithin(prevLeft, leftTarget, maxStep);
        var right = PullWithin(prevRight, rightTarget, maxStep);
        left  = ClampBarEnd(elbow, left, core, region, bead);
        right = ClampBarEnd(elbow, right, core, region, bead);

        tree.Branches.Clear();
        tree.Branches.Add(new LightningBranch([anchor, elbow]));
        bool hasBar = false;
        if (Vector2.Distance(elbow, left) >= bead * 0.5f)
        {
            tree.Branches.Add(new LightningBranch([elbow, left]) { ParentBranch = 0, ParentNode = 1 });
            hasBar = true;
        }
        if (Vector2.Distance(elbow, right) >= bead * 0.5f)
        {
            tree.Branches.Add(new LightningBranch([elbow, right]) { ParentBranch = 0, ParentNode = 1 });
            hasBar = true;
        }
        // Early trunk-only growth is OK (bars open later as length allows).
        if (!hasBar && trunkLen >= bead * 1.5f)
        {
            // Prefer opening a minimal bar once trunk is deep enough for a T morph.
            float r = MaxBarReach(elbow, tangent, MathF.Min(halfBar, maxStep * 2f), region, bead);
            if (r >= bead * 0.5f)
                tree.Branches.Add(new LightningBranch([elbow, elbow + tangent * r])
                    { ParentBranch = 0, ParentNode = 1 });
        }
        tree.Anchor = anchor;
        return tree.Branches.Count >= 1;
    }

    private static Vector2 PullWithin(Vector2 from, Vector2 target, float maxStep)
    {
        var d = target - from;
        float len = d.Length();
        if (len <= maxStep || maxStep <= 0f) return target;
        return from + d * (maxStep / len);
    }

    /// <summary>One-layer recovery stub: only MaxStep deep from the wall (gradual growth).</summary>
    private static bool TryRebuildShortButtress(
        LightningTree tree, PathsD region, PathsD core, float bead, float barLen, float maxStep)
    {
        var anchor = tree.Anchor;
        if (!TryBoundaryFrame(region, anchor, out var tangent, out var inward))
            return false;
        // At most one overhang step out — never a full-depth T.
        float trunkLen = MathF.Max(bead * 0.75f, MathF.Min(maxStep * 2f, bead * 3f));
        var elbow = anchor + inward * trunkLen;
        if (!InsideRegion(core, elbow))
            elbow = ClosestOnRegionBoundary(core, elbow);
        if (!SegmentInsideRegion(region, anchor, elbow, bead))
            return false;
        tree.Branches.Clear();
        tree.Branches.Add(new LightningBranch([anchor, elbow]));
        // Optional tiny bar seed (grows on later layers via re-aim).
        float r = MaxBarReach(elbow, tangent, MathF.Min(barLen * 0.5f, maxStep * 2f), region, bead);
        if (r >= bead * 0.5f)
            tree.Branches.Add(new LightningBranch([elbow, elbow + tangent * r])
                { ParentBranch = 0, ParentNode = 1 });
        return true;
    }

    /// <summary>
    /// Top-down retract for Formbound Buttress = bottom-up growth within MaxStep.
    /// Shortens bar tips toward the elbow, then moves the elbow toward the wall so
    /// the trunk also recedes — otherwise only bars shrink and every layer shows a
    /// full-depth T that cannot print at the overhang angle.
    /// </summary>
    internal static void RetractButtress(LightningTree tree, float step, float bead)
    {
        if (tree.Branches.Count == 0 || step <= 0f) return;
        var trunk = tree.Branches[0].Centerline;
        if (trunk.Count < 2) { tree.Branches.Clear(); return; }

        var anchor = tree.Anchor;
        // Ensure trunk[0] is anchor.
        trunk[0] = anchor;
        var elbow = trunk[^1];

        // 1) Retract each bar tip toward its root (elbow).
        for (int bi = tree.Branches.Count - 1; bi >= 1; bi--)
        {
            var br = tree.Branches[bi];
            if (br.ParentBranch != 0 || br.Centerline.Count < 2)
            {
                tree.Branches.RemoveAt(bi);
                continue;
            }
            // Normalize to [elbow, tip].
            var tip = br.Centerline[^1];
            float barLen = Vector2.Distance(elbow, tip);
            if (barLen <= step + bead * 0.25f)
            {
                tree.Branches.RemoveAt(bi); // bar fully retracted this layer
                continue;
            }
            var newTip = Vector2.Lerp(tip, elbow, step / barLen);
            br.Centerline.Clear();
            br.Centerline.Add(elbow);
            br.Centerline.Add(newTip);
        }

        // 2) Retract elbow toward wall (shorten trunk). The tree may only dissolve
        //    when NOTHING remains above it: while bars still exist they are the
        //    support column for printed geometry — clearing the whole T here left
        //    everything above floating (cavity columns died with 25 mm of live bar).
        //    Instead the elbow pins at a wall stub and the bars retract out first.
        float trunkLen = Vector2.Distance(anchor, elbow);
        bool barsRemain = tree.Branches.Count > 1;
        if (trunkLen <= step + bead * 0.35f && !barsRemain)
        {
            // Trunk gone and no bars — the column is fully consumed.
            tree.Branches.Clear();
            return;
        }
        float newLen = MathF.Max(trunkLen - step, bead * 0.35f);
        var newElbow = trunkLen < 1e-4f
            ? anchor
            : Vector2.Lerp(anchor, elbow, newLen / trunkLen);
        trunk.Clear();
        trunk.Add(anchor);
        trunk.Add(newElbow);

        // 3) Re-parent remaining bars to the new elbow; drop if too short.
        for (int bi = tree.Branches.Count - 1; bi >= 1; bi--)
        {
            var br = tree.Branches[bi];
            if (br.Centerline.Count < 2) { tree.Branches.RemoveAt(bi); continue; }
            var tip = br.Centerline[^1];
            if (Vector2.Distance(newElbow, tip) < bead * 0.4f)
            {
                tree.Branches.RemoveAt(bi);
                continue;
            }
            br.Centerline.Clear();
            br.Centerline.Add(newElbow);
            br.Centerline.Add(tip);
            br.ParentBranch = 0;
            br.ParentNode = 1;
        }
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

    /// <summary>
    /// Assign each densified sample the Inside/Outside of the nearest original paint
    /// sample (so order/densify preserve per-selection wall side).
    /// </summary>
    private static List<PaintSupportSide> TransferDemandSides(
        List<Vector2> srcPts, List<PaintSupportSide> srcSides, List<Vector2> dstPts)
    {
        var result = new List<PaintSupportSide>(dstPts.Count);
        if (dstPts.Count == 0) return result;
        if (srcPts.Count == 0)
        {
            for (int i = 0; i < dstPts.Count; i++)
                result.Add(PaintSupportSide.Inside);
            return result;
        }
        foreach (var p in dstPts)
        {
            int bi = 0;
            float bd = float.MaxValue;
            for (int i = 0; i < srcPts.Count; i++)
            {
                float d = Vector2.DistanceSquared(srcPts[i], p);
                if (d < bd) { bd = d; bi = i; }
            }
            result.Add(bi < srcSides.Count ? srcSides[bi] : PaintSupportSide.Inside);
        }
        return result;
    }

    /// <summary>
    /// Evenly sample a polyline at <paramref name="pitch"/> mm so long selected
    /// lines get dense Formbound demand (Target Support Selections).
    /// </summary>
    private static List<Vector2> DenseResampleRun(List<Vector2> run, float pitch)
    {
        if (run.Count == 0) return run;
        if (run.Count == 1 || pitch < 1e-3f) return new List<Vector2>(run);
        var result = new List<Vector2> { run[0] };
        float acc = 0f;
        for (int i = 1; i < run.Count; i++)
        {
            var a = run[i - 1];
            var b = run[i];
            float seg = Vector2.Distance(a, b);
            if (seg < 1e-6f) continue;
            float d = 0f;
            while (acc + (seg - d) >= pitch - 1e-5f)
            {
                float need = pitch - acc;
                d += need;
                float u = Math.Clamp(d / seg, 0f, 1f);
                result.Add(a + (b - a) * u);
                acc = 0f;
            }
            acc += seg - d;
        }
        var last = run[^1];
        if (Vector2.DistanceSquared(result[^1], last) > 1e-4f)
            result.Add(last);
        return result;
    }

    /// <summary>Index of the sample nearest the geometric midpoint of the run.</summary>
    private static int MidIndexAlongRun(List<Vector2> run)
    {
        if (run.Count <= 1) return 0;
        float total = 0f;
        for (int j = 1; j < run.Count; j++)
            total += Vector2.Distance(run[j - 1], run[j]);
        if (total < 1e-6f) return run.Count / 2;
        float half = total * 0.5f;
        float acc = 0f;
        for (int j = 1; j < run.Count; j++)
        {
            float seg = Vector2.Distance(run[j - 1], run[j]);
            if (acc + seg >= half) return j;
            acc += seg;
        }
        return run.Count / 2;
    }

    /// <summary>
    /// Order projected manual-demand points into a continuous polyline via nearest-
    /// neighbour chaining, starting from the <b>first</b> planted mark so the
    /// support-selection end of a bridge ribbon stays at index 0 (facing aim).
    /// </summary>
    private static List<Vector2> OrderDemandRun(
        IReadOnlyList<Vector2> raw, Func<Vector2, Vector2> map)
    {
        var pts = new List<Vector2>(raw.Count);
        foreach (var p in raw) pts.Add(map(p));
        if (pts.Count <= 2) return pts;

        // Start at first planted mark (support selection / ribbon start), not min-X.
        int start = 0;
        var ordered = new List<Vector2>(pts.Count);
        var used = new bool[pts.Count];
        int cur = start;
        for (int n = 0; n < pts.Count; n++)
        {
            ordered.Add(pts[cur]);
            used[cur] = true;
            int next = -1;
            float best = float.MaxValue;
            for (int j = 0; j < pts.Count; j++)
            {
                if (used[j]) continue;
                float d = Vector2.DistanceSquared(pts[cur], pts[j]);
                if (d < best) { best = d; next = j; }
            }
            if (next < 0) break;
            cur = next;
        }
        return ordered;
    }

    /// <summary>
    /// Boundary samples on <paramref name="paths"/> within <paramref name="maxDist"/>
    /// of <paramref name="near"/> — used to pick the closest facing wall break for
    /// painted Bridge mouths (2× layer height search).
    /// </summary>
    private static void CollectBoundaryCandidatesNear(
        PathsD paths, Vector2 near, float maxDist, float bead, List<Vector2> into)
    {
        float max2 = maxDist * maxDist;
        float step = MathF.Max(bead * 0.5f, maxDist / 12f);
        foreach (var path in paths)
        {
            int cnt = path.Count;
            if (cnt < 2) continue;
            for (int i = 0; i < cnt; i++)
            {
                var a = new Vector2((float)path[i].x, (float)path[i].y);
                var b = new Vector2((float)path[(i + 1) % cnt].x, (float)path[(i + 1) % cnt].y);
                var ab = b - a;
                float len = ab.Length();
                if (len < 1e-6f)
                {
                    if (Vector2.DistanceSquared(near, a) <= max2) into.Add(a);
                    continue;
                }
                // Closest point on this segment.
                float t = Math.Clamp(Vector2.Dot(near - a, ab) / (len * len), 0f, 1f);
                var c = a + ab * t;
                if (Vector2.DistanceSquared(near, c) <= max2) into.Add(c);
                // Extra samples along the segment for better facing choice.
                int nSamp = Math.Max(1, (int)MathF.Ceiling(len / step));
                for (int s = 0; s <= nSamp; s++)
                {
                    float u = s / (float)nSamp;
                    var p = a + ab * u;
                    if (Vector2.DistanceSquared(near, p) <= max2) into.Add(p);
                }
            }
        }
    }

    /// <summary>Walk ±arc length along the sample ring from <paramref name="si"/>,
    /// staying inside the run window when possible, and project onto <paramref name="keep"/>.</summary>
    private static Vector2 WalkAlongRun(
        List<Vector2> samples, int si, int runStart, int runCount, float sampleStep,
        float signedArc, PathsD keep, float bead, bool external)
    {
        int n = samples.Count;
        if (n == 0) return default;
        // Single sample (e.g. paint mouth at ColumnFoot only): no arc to walk.
        if (n == 1)
        {
            var only = samples[0];
            if (external) return only;
            if (InsideRegion(keep, only)) return only;
            return ClosestOnRegionBoundary(keep, only);
        }
        int dir = signedArc >= 0f ? 1 : -1;
        float remaining = MathF.Abs(signedArc);
        int idx = si;
        var last = samples[si];
        int guard = n * 4 + 8; // never wrap forever on degenerate collinear runs

        // Prefer staying inside the unsupported run indices.
        bool InRun(int i)
        {
            for (int j = 0; j < runCount; j++)
                if ((runStart + j) % n == i) return true;
            return false;
        }

        while (remaining > 0.01f && guard-- > 0)
        {
            int next = (idx + dir + n * 4) % n;
            // Stop at run ends for finite runs (don't wrap into supported arc).
            if (runCount < n && !InRun(next))
                break;
            if (next == idx) break;
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

    // ── Formbound Bridge severity placement ─────────────────────────────────

    /// <summary>One boundary sample that still needs a Formbound Bridge finger.</summary>
    private readonly record struct BridgeDemandSample(
        Vector2 Pt, float Severity, int RawSi, List<Vector2> RawSamples, List<Vector2> Samples);

    /// <summary>
    /// How far past the support threshold this sample hangs (mm). Higher = worse overhang.
    /// Multi-planar: same metric via distance to the lower wall region boundary.
    /// </summary>
    internal static float DemandSeverity(
        Vector2 pt, PathsD region, PathsD? multiDemandFootprint, float supportRadius, float bead)
    {
        float dWall = Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt);
        float baseSev = MathF.Max(0f, dWall - supportRadius);
        if (multiDemandFootprint is null) return baseSev;

        // Inside the unsupported footprint: prefer deeper into the air-side pocket.
        if (InsideRegion(multiDemandFootprint, pt))
            return MathF.Max(baseSev, bead * 0.5f + baseSev);

        float dFp = Vector2.Distance(ClosestOnRegionBoundary(multiDemandFootprint, pt), pt);
        if (dFp < bead * 0.6f)
            return MathF.Max(baseSev, MathF.Max(0.01f, bead * 0.6f - dFp));
        return baseSev;
    }

    /// <summary>
    /// Cover radius used when deciding "already supported". Mild demand keeps a wide
    /// radius; severe free-edge demand only counts a finger as covering when it is
    /// close (so mid-wall fingers do not suppress the free edge).
    /// </summary>
    internal static float CoverRadiusForSeverity(
        float severity, float supportRadius, float bead)
    {
        float mild = MathF.Max(supportRadius, bead * 0.5f);
        // Severe free edge: only a nearby tip counts as cover (not half a spacing away).
        // Always strictly tighter than mild so mid-wall fingers cannot suppress free edges.
        float severe = MathF.Min(mild * 0.4f, MathF.Max(bead * 0.75f, mild * 0.25f));
        float span = MathF.Max(supportRadius, bead * 0.5f);
        float t = Math.Clamp(severity / span, 0f, 1f);
        t = t * t * (3f - 2f * t); // smoothstep
        return mild + (severe - mild) * t;
    }

    /// <summary>
    /// Place Bridge tips for one domain (interior or exterior) with an isolated tip budget.
    /// Returns mesh-veto skip count.
    /// </summary>
    private static int PlaceBridgeDomainTips(
        List<(int Si, float Sev)> ranked,
        int tipBudget,
        List<Vector2> samples,
        List<Vector2> rawSamples,
        PathsD region, PathsD envelope, PathsD core,
        PathsD anchorPaths, PathsD anchorInterior, PathsD anchorExterior,
        SliceSettings settings, float bead, float supportRadius, float spacing,
        bool exteriorDomain,
        LightningLayerPlan layerPlan, ref int nextTreeId,
        Func<int, Vector2, bool>? solidAt, int upperLayerIdx,
        PathsD regionAbove,
        float maxStep)
    {
        if (tipBudget <= 0 || ranked.Count == 0) return 0;
        int meshVetoes = 0;
        int placed = 0;
        float tipSpacing = spacing * 0.5f;
        // Tight mouth catch: only claim the same column, not a neighbour a spacing away.
        float mouthCatch = MathF.Max(spacing * 0.55f, bead * 2.25f);

        foreach (var (si, sev) in ranked)
        {
            if (placed >= tipBudget) break;
            var sPt = samples[si];
            float coverR = CoverRadiusForSeverity(sev, supportRadius, bead);
            if (NearAnyCenterline(layerPlan.Trees, sPt, coverR))
                continue;

            if (!PassesMeshVetoAt(solidAt, upperLayerIdx, regionAbove, rawSamples, si, bead))
            {
                meshVetoes++;
                continue;
            }

            var tipSpace = ClassifyPoint(region, envelope, sPt);
            bool external = tipSpace == DemandSpace.Exterior;
            bool cavity   = tipSpace == DemandSpace.Cavity;
            if (exteriorDomain != external) continue;

            var tip = tipSpace == DemandSpace.Interior
                ? (InsideRegion(core, sPt) ? sPt : ClosestOnRegionBoundary(core, sPt))
                : sPt;

            if (TooCloseToExisting(layerPlan.Trees, tip, tipSpacing))
                continue;

            // MaxStep-extend / deepen inherited columns before birthing a full-depth
            // hairpin. Same-mouth columns must never re-seed full-length dual-wall
            // islands mid-stack (those float without a MaxStep foundation).
            if (!cavity && (TryExtendExistingBridge(
                    layerPlan.Trees, tip, region, core, bead, maxStep, mouthCatch, external)
                || TryDeepenNearbyBridgeMouth(
                    layerPlan.Trees, tip, region, core, bead, maxStep, mouthCatch, external)))
            {
                placed++;
                continue;
            }

            // Domain-matched mouths: interior demand prefers interior anchors;
            // exterior demand prefers outer perimeter (falls back to any allowed).
            PathsD mouthPaths;
            if (external)
            {
                mouthPaths = anchorExterior.Count > 0 ? anchorExterior
                    : (anchorPaths.Count > 0 ? anchorPaths : region);
            }
            else
            {
                mouthPaths = anchorInterior.Count > 0 ? anchorInterior
                    : (anchorPaths.Count > 0 ? anchorPaths : region);
            }

            var anchor = tipSpace == DemandSpace.Interior || tipSpace == DemandSpace.Cavity
                ? ClosestOnRegionBoundary(mouthPaths, tip)
                : ClosestOnRegionBoundary(mouthPaths.Count > 0 ? mouthPaths : region, tip);

            if (Vector2.Distance(anchor, tip) < bead) continue;
            if (tipSpace == DemandSpace.Interior
                && !SegmentInsideRegion(region, anchor, tip, bead)) continue;
            if (cavity && !SegmentInsideVoid(region, anchor, tip, bead)) continue;

            var t = new LightningTree
            {
                Id = nextTreeId++, Anchor = anchor,
                External = external, Cavity = cavity,
            };
            t.Branches.Add(new LightningBranch([anchor, tip]));
            layerPlan.Trees.Add(t);
            placed++;
        }

        return meshVetoes;
    }

    /// <summary>
    /// Place Bridge tips worst-first (residual path). Returns mesh-veto skip count.
    /// </summary>
    private static int PlaceBridgeTipsSeverityFirst(
        List<BridgeDemandSample> demand,
        PathsD region, PathsD envelope, PathsD core, PathsD anchorPaths,
        SliceSettings settings, float bead, float supportRadius, float spacing,
        LightningLayerPlan layerPlan, ref int nextTreeId,
        Func<int, Vector2, bool>? solidAt, int upperLayerIdx,
        PathsD regionAbove,
        float maxStep)
    {
        if (demand.Count == 0) return 0;
        demand.Sort((a, b) => b.Severity.CompareTo(a.Severity));

        int meshVetoes = 0;
        float tipSpacing = MathF.Max(bead * 1.25f, spacing * 0.85f);
        float mouthCatch = MathF.Max(spacing * 0.55f, bead * 2.25f);

        foreach (var s in demand)
        {
            var pt = s.Pt;
            float coverR = CoverRadiusForSeverity(s.Severity, supportRadius, bead);
            if (NearAnyCenterline(layerPlan.Trees, pt, coverR))
                continue;

            if (!PassesMeshVetoAt(solidAt, upperLayerIdx, regionAbove, s.RawSamples, s.RawSi, bead))
            {
                meshVetoes++;
                continue;
            }

            var tipSpace = ClassifyPoint(region, envelope, pt);
            bool external = tipSpace == DemandSpace.Exterior;
            bool cavity   = tipSpace == DemandSpace.Cavity;
            // Respect domain checkboxes.
            if (external && !settings.LightningExteriorOverhangs) continue;
            if (!external && !settings.LightningAnchorInterior) continue;

            var tip = tipSpace == DemandSpace.Interior
                ? (InsideRegion(core, pt) ? pt : ClosestOnRegionBoundary(core, pt))
                : pt;

            if (TooCloseToExisting(layerPlan.Trees, tip, tipSpacing))
                continue;

            if (!cavity && (TryExtendExistingBridge(
                    layerPlan.Trees, tip, region, core, bead, maxStep, mouthCatch, external)
                || TryDeepenNearbyBridgeMouth(
                    layerPlan.Trees, tip, region, core, bead, maxStep, mouthCatch, external)))
                continue;

            var anchor = tipSpace == DemandSpace.Interior
                ? ClosestOnRegionBoundary(anchorPaths, tip)
                : ClosestOnRegionBoundary(region, tip);

            if (Vector2.Distance(anchor, tip) < bead) continue;
            if (tipSpace == DemandSpace.Interior
                && !SegmentInsideRegion(region, anchor, tip, bead)) continue;
            if (cavity && !SegmentInsideVoid(region, anchor, tip, bead)) continue;

            var t = new LightningTree
            {
                Id = nextTreeId++, Anchor = anchor,
                External = external, Cavity = cavity,
            };
            t.Branches.Add(new LightningBranch([anchor, tip]));
            layerPlan.Trees.Add(t);
        }

        return meshVetoes;
    }

    /// <summary>Re-scan upper boundaries for residual Bridge demand (severity-aware cover).</summary>
    private static List<BridgeDemandSample> CollectBridgeDemand(
        PathsD upperRegions,
        PathsD region, PathsD envelope, PathsD core,
        PathsD? multiDemandFootprint, PathsD? gravityDemandFootprint,
        bool hasFrames,
        Func<Vector2, Vector2> down, Func<Vector2, Vector2> downGravity,
        SliceSettings settings, float bead, float supportRadius, float sampleStep,
        List<LightningTree> trees,
        Func<int, Vector2, bool>? solidAt, int upperLayerIdx)
    {
        var list = new List<BridgeDemandSample>(128);
        foreach (var path in upperRegions)
        {
            if (Math.Abs(Clipper.Area(path)) < 4.0 * bead * bead) continue;
            var rawSamples = SamplePath(path, sampleStep);
            if (rawSamples.Count == 0) continue;
            var samples = new List<Vector2>(rawSamples.Count);
            if (hasFrames)
            {
                for (int k = 0; k < rawSamples.Count; k++)
                    samples.Add(down(rawSamples[k]));
            }
            else
                samples.AddRange(rawSamples);

            List<Vector2>? samplesG = null;
            if (hasFrames && gravityDemandFootprint is not null)
            {
                samplesG = new List<Vector2>(rawSamples.Count);
                for (int k = 0; k < rawSamples.Count; k++)
                    samplesG.Add(downGravity(rawSamples[k]));
            }

            for (int si = 0; si < samples.Count; si++)
            {
                var pt = samples[si];
                bool far = multiDemandFootprint is not null
                    ? (InsideRegion(multiDemandFootprint, pt)
                       || Vector2.Distance(ClosestOnRegionBoundary(multiDemandFootprint, pt), pt)
                          < bead * 0.6f)
                    : Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt) > supportRadius;
                if (!far && samplesG is not null && gravityDemandFootprint is not null)
                {
                    var ptG = samplesG[si];
                    bool gFar = InsideRegion(gravityDemandFootprint, ptG)
                        || Vector2.Distance(ClosestOnRegionBoundary(gravityDemandFootprint, ptG), ptG)
                           < bead * 0.6f;
                    if (gFar)
                    {
                        far = true;
                        pt = samples[si] = ptG;
                    }
                }
                if (!far) continue;
                var space = ClassifyPoint(region, envelope, pt);
                if (space == DemandSpace.Exterior && !settings.LightningExteriorOverhangs)
                    continue;
                if (space != DemandSpace.Exterior && !settings.LightningAnchorInterior)
                    continue;
                float severity = DemandSeverity(pt, region, multiDemandFootprint, supportRadius, bead);
                float coverR = CoverRadiusForSeverity(severity, supportRadius, bead);
                if (NearAnyCenterline(trees, pt, coverR))
                    continue;
                if (!PassesMeshVetoAt(solidAt, upperLayerIdx, upperRegions, rawSamples, si, bead))
                    continue;
                list.Add(new BridgeDemandSample(pt, severity, si, rawSamples, samples));
            }
        }
        return list;
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

    /// <summary>Where a demand point lives relative to the layer's material.
    /// Interior = in material; Cavity = over a region HOLE (a modeled internal
    /// void — still inside the part's outer envelope, so support is mandatory);
    /// Exterior = outside every outer (a true outward flare — sacrificial-fin
    /// territory).</summary>
    internal enum DemandSpace { Interior, Cavity, Exterior }

    /// <summary>The part's filled silhouette for one layer: every boundary path
    /// treated as a solid (holes fill in), NonZero-unioned. Inside the envelope but
    /// not in material = a truly ENCLOSED void. Per-path outer tests are unreliable
    /// on parity-composed regions (half-loop chains) and on concave shapes — they
    /// classified exterior air as cavity and put support tubes OUTSIDE the mesh.</summary>
    internal static PathsD BuildEnvelope(PathsD region)
    {
        var filled = new PathsD(region.Count);
        foreach (var path in region)
        {
            if (path.Count < 3) continue;
            var p2 = new PathD(path);
            if (Clipper.Area(p2) < 0) p2.Reverse();
            filled.Add(p2);
        }
        return Clipper.Union(filled, FillRule.NonZero);
    }

    internal static DemandSpace ClassifyPoint(PathsD region, PathsD envelope, Vector2 p)
    {
        if (InsideRegion(region, p)) return DemandSpace.Interior;
        var pt = new PointD(p.X, p.Y);
        foreach (var path in envelope)
            if (Clipper.PointInPolygon(pt, path) == PointInPolygonResult.IsInside)
                return DemandSpace.Cavity;
        return DemandSpace.Exterior;
    }

    // Placement-failure diagnostics, reset per Build, appended to the stats line.
    internal static int PfNoAnchor, PfBarReach, PfNoFrame, PfCovered, PfElbow;

    internal static int CountOuterPaths(PathsD region)
    {
        int n = 0;
        foreach (var path in region)
            if (Clipper.Area(path) > 0) n++;
        return n;
    }

    /// <summary>True when <paramref name="p"/> lies within <paramref name="tol"/> of
    /// the path's boundary curve.</summary>
    private static bool PointNearPath(Vector2 p, PathD path, float tol)
    {
        float t2 = tol * tol;
        for (int i = 0; i < path.Count; i++)
        {
            var a = new Vector2((float)path[i].x, (float)path[i].y);
            var b = new Vector2((float)path[(i + 1) % path.Count].x, (float)path[(i + 1) % path.Count].y);
            if (DistToSegmentSq(p, a, b) < t2) return true;
        }
        return false;
    }

    /// <summary>Closest vertex pair between two boundary paths (coarse but stable:
    /// vertices only, so the same pair wins on consecutive layers).</summary>
    private static void FindClosestPathPair(
        PathD fromPath, PathD toPath, ref Vector2 from, ref Vector2 to, ref float bestD)
    {
        int strideA = Math.Max(1, fromPath.Count / 256);
        int strideB = Math.Max(1, toPath.Count / 256);
        for (int a = 0; a < fromPath.Count; a += strideA)
        {
            var pa = new Vector2((float)fromPath[a].x, (float)fromPath[a].y);
            for (int b = 0; b < toPath.Count; b += strideB)
            {
                var pb = new Vector2((float)toPath[b].x, (float)toPath[b].y);
                float d = Vector2.Distance(pa, pb);
                if (d < bestD) { bestD = d; from = pa; to = pb; }
            }
        }
    }

    /// <summary>True when the segment runs through VOID (a cavity), only touching
    /// material within one bead of the walls it departs from / arrives at. A cavity
    /// tube crossing deep material would bulge the union through a wall.</summary>
    /// <summary>
    /// Re-seat a cavity trunk whose inherited line clips the shifted wall band:
    /// keep the anchor (already snapped to this layer's boundary), aim along the
    /// old trunk direction and shorten until the segment sits inside the void.
    /// Bars are dropped (they regrow at the overhang rate). False = even a
    /// MaxStep stub cannot sit in the void — caller may orphan.
    /// </summary>
    private static bool TryReseatCavityTrunk(
        LightningTree t, PathsD region, float bead, float maxStep)
    {
        if (t.Branches.Count == 0 || t.Branches[0].Centerline.Count < 2) return false;
        var line = t.Branches[0].Centerline;
        var anchor = t.Anchor;
        var tip = line[^1];
        var dir = tip - anchor;
        float len = dir.Length();
        if (len < 1e-3f) return false;
        dir /= len;
        for (float L = len; L >= maxStep * 0.85f; L *= 0.6f)
        {
            var cand = anchor + dir * L;
            if (!SegmentInsideVoid(region, anchor, cand, bead * 1.5f)) continue;
            if (t.Branches.Count > 1)
                t.Branches.RemoveRange(1, t.Branches.Count - 1);
            line.Clear();
            line.Add(anchor);
            line.Add(cand);
            return true;
        }
        return false;
    }

    internal static bool SegmentInsideVoid(PathsD region, Vector2 a, Vector2 b, float bead)
    {
        float len = Vector2.Distance(a, b);
        int n = Math.Max(2, (int)(len / MathF.Max(bead * 0.5f, 0.5f)));
        for (int k = 0; k <= n; k++)
        {
            var p = Vector2.Lerp(a, b, k / (float)n);
            if (!InsideRegion(region, p)) continue;                 // in void — fine
            if (Vector2.Distance(ClosestOnRegionBoundary(region, p), p) > bead)
                return false;                                       // deep in material
        }
        return true;
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
