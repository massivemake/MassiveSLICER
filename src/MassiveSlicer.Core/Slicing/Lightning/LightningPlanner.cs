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
    /// <param name="fillPolysPerLayer">Closed fill polygons per layer, bottom-up,
    /// plane-local 2D (outer CCW / holes CW as produced by the slicers).</param>
    /// <param name="layerHeights">Height of each layer (adaptive-aware).</param>
    public static LightningPlan Build(
        IReadOnlyList<List<List<Vector2>>> fillPolysPerLayer,
        IReadOnlyList<float> layerHeights,
        SliceSettings settings)
    {
        int n = fillPolysPerLayer.Count;
        var plan = new LightningPlan(n);
        if (n == 0) return plan;

        float bead    = MathF.Max(settings.BeadWidth, 0.1f);
        float tanA    = MathF.Tan(Math.Clamp(settings.LightningOverhangDeg, 5f, 80f) * MathF.PI / 180f);
        float spacing = settings.LightningBranchSpacingMm > 0f
            ? settings.LightningBranchSpacingMm
            : 4f * bead;

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
            var anchorPaths = new PathsD();
            foreach (var path in region)
            {
                bool isOuter = Clipper.Area(path) > 0;
                if (isOuter ? settings.LightningAnchorExterior : settings.LightningAnchorInterior)
                    anchorPaths.Add(path);
            }
            if (anchorPaths.Count == 0) continue;   // nowhere allowed to root

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
                var samples = SamplePath(path, sampleStep);
                if (samples.Count == 0) continue;

                // Flag which boundary samples of the layer above lack support here.
                var unsupported = new bool[samples.Count];
                for (int si = 0; si < samples.Count; si++)
                {
                    var pt = samples[si];
                    bool far = Vector2.Distance(ClosestOnRegionBoundary(region, pt), pt) > supportRadius;
                    bool inside = InsideRegion(region, pt);
                    // Inward-shrinking arcs are always demand; outward flares only when
                    // sacrificial external fins are enabled.
                    unsupported[si] = far
                        && (inside || settings.LightningExteriorOverhangs)
                        && !NearAnyCenterline(layerPlan.Trees, pt, supportRadius);
                }

                // Distribute tips EVENLY along each contiguous unsupported run —
                // greedy first-come dedupe leaves worst-case 2×spacing holes at the
                // run wrap-around, which shows up as one missing finger in a ring.
                foreach (var (start, count) in CircularRuns(unsupported))
                {
                    float runLen = count * sampleStep;
                    int tipCount = Math.Max(1, (int)MathF.Round(runLen / spacing));
                    for (int k = 0; k < tipCount; k++)
                    {
                        int si = (start + (int)((k + 0.5f) * count / tipCount)) % samples.Count;
                        var sPt = samples[si];

                        bool external = !InsideRegion(region, sPt);

                        // Interior demand: tip goes right under the unsupported arc, kept
                        // ≥ one bead inside so the slit can't breach the far wall.
                        // External demand: the fin tip is the overhanging point itself.
                        var tip = external
                            ? sPt
                            : InsideRegion(core, sPt) ? sPt : ClosestOnRegionBoundary(core, sPt);

                        if (TooCloseToExisting(layerPlan.Trees, tip, spacing * 0.5f)) continue;

                        var anchor = ClosestOnRegionBoundary(external ? region : anchorPaths, tip);
                        if (Vector2.Distance(anchor, tip) < bead) continue;   // wall covers it
                        // Reject fingers whose centerline would cross a void (a chord
                        // over a ring's hole) — the slit can't realize them.
                        if (!external && !SegmentInsideRegion(region, anchor, tip, bead)) continue;

                // Merge: root on the nearest existing centerline when that is closer
                // than the boundary (child branch), else start a new tree.
                // DISABLED for now: a trunk cannot retract while any child lives, so
                // chained branches outlive their support depth and reach the bed.
                // Re-enabling needs depth-aware retraction (retract the longest
                // root-to-leaf arc, not each leaf independently).
                const bool enableTreeMerging = false;
                var (tree, branch, node, distToTree) = enableTreeMerging
                    ? NearestCenterlineNode(layerPlan.Trees, tip)
                    : (null, 0, 0, float.MaxValue);
                if (tree is not null && distToTree < Vector2.Distance(anchor, tip)
                    && distToTree >= bead)
                {
                    var junction = tree.Branches[branch].Centerline[node];
                    tree.Branches.Add(new LightningBranch([junction, tip])
                    {
                        ParentBranch = branch,
                        ParentNode   = node,
                    });
                }
                else
                {
                    var t = new LightningTree { Id = nextTreeId++, Anchor = anchor, External = external };
                    t.Branches.Add(new LightningBranch([anchor, tip]));
                    layerPlan.Trees.Add(t);
                }
                    }
                }
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
