using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Lightning;

/// <summary>
/// Realizes a layer's lightning plan as toolpath moves.
///
/// Every finger tree's centerlines are inflated by half a bead
/// (<c>JoinType.Round / EndType.Round</c>) into a closed "slit" polygon whose
/// boundary is exactly the hairpin out-and-back wall pair with a rounded tip;
/// the root end is extended past the region boundary so
/// <c>Difference(region, slits)</c> notches the perimeter. The resulting
/// polygons' boundaries ARE the perimeter-with-finger-detours paths — each is
/// emitted as one plain closed extrude loop, so continuity comes for free.
/// Optional tip discs are unioned onto the slits, making the boundary loop
/// around each tip ("support pad").
/// </summary>
public static class LightningGenerator
{
    public static void EmitLightning(
        List<List<Vector2>> fillPolys,
        LightningLayerPlan? plan,
        float z,
        ToolpathLayer layer,
        float beadWidth,
        float tipLoopRadius,
        Func<Vector2, Vector3>? project = null)
    {
        var region = LightningPlanner.ToPathsD(fillPolys);
        if (region.Count == 0) return;

        int outerCount = CountOuters(region);
        var result = region;

        if (plan is not null && plan.Trees.Count > 0)
        {
            foreach (var tree in plan.Trees)
            {
                // A guard dropped this lineage at a lower layer — its support column
                // is gone, so printing the inherited finger here would leave it in
                // mid-air. (Emission runs bottom-up.)
                if (plan.DroppedTrees.Contains(tree.Id)) continue;

                var slit = BuildTreeSlit(tree, region, beadWidth, tipLoopRadius);
                if (slit.Count == 0) continue;   // newborn stub, too small this layer — not a drop

                PathsD candidate;
                if (tree.External)
                {
                    // Sacrificial fin OUTSIDE the part: add the bump instead of
                    // notching. The boundary detours outward around it — still one loop.
                    candidate = Clipper.Union(result, slit, FillRule.NonZero);
                    candidate = Clipper.SimplifyPaths(candidate, 0.05, false);
                    // Guard: a fin bridging to another island would merge outers
                    // (changes topology unexpectedly) — drop that lineage.
                    if (CountOuters(candidate) < outerCount) { plan.DroppedTrees.Add(tree.Id); continue; }
                }
                else
                {
                    candidate = Clipper.Difference(result, slit, FillRule.NonZero);
                    candidate = Clipper.SimplifyPaths(candidate, 0.05, false);
                    // Guard: a slit that cuts clean across a neck would split the region
                    // into extra islands (adding travels) — drop that lineage.
                    if (CountOuters(candidate) > outerCount) { plan.DroppedTrees.Add(tree.Id); continue; }
                }

                // Guard: degenerate output (region consumed).
                if (candidate.Count == 0) { plan.DroppedTrees.Add(tree.Id); continue; }

                result = candidate;
            }

            // Converging fingers (inherited lineages drifting together — common on
            // angled sweeps) can leave a sliver of region thinner than a bead between
            // their slits: two walls printed almost on top of each other = gross
            // over-extrusion. Morphologically CLOSE the cut area (dilate + erode) so
            // near-touching slits merge into one clean notch. Gaps bounded by the
            // part's real exterior only dilate from one side and survive untouched.
            if (!ReferenceEquals(result, region))
            {
                var cut = Clipper.Difference(region, result, FillRule.NonZero);
                if (cut.Count > 0)
                {
                    double closeR = beadWidth * 0.55;
                    var closed = Clipper.InflatePaths(cut, closeR, JoinType.Round, EndType.Polygon);
                    closed     = Clipper.InflatePaths(closed, -closeR, JoinType.Round, EndType.Polygon);
                    // Subtract from RESULT (not region): the notches are already cut
                    // (idempotent) and unioned external fins must survive the merge.
                    var merged = Clipper.Difference(result, closed, FillRule.NonZero);
                    merged     = Clipper.SimplifyPaths(merged, 0.05, false);
                    // The rounded dilate/erode leaves sub-millimetre lens fragments along
                    // the old sliver midline; anything smaller than one bead² is
                    // unprintable anyway — drop it before judging topology.
                    merged.RemoveAll(path => Math.Abs(Clipper.Area(path)) < beadWidth * beadWidth);
                    // Keep the per-tree guards' promises: adopt the merge only when it
                    // doesn't change the island topology.
                    if (merged.Count > 0 && CountOuters(merged) == CountOuters(result))
                        result = merged;
                }
            }
        }

        EmitLoops(result, z, layer, project, plan, beadWidth, tipLoopRadius);
    }

    /// <summary>Inflates one tree's centerlines (plus optional tip discs) into slit polygons.</summary>
    private static PathsD BuildTreeSlit(LightningTree tree, PathsD region, float beadWidth, float tipLoopRadius)
    {
        float halfBead = beadWidth * 0.5f;
        var openLines = new PathsD();

        foreach (var branch in tree.Branches)
        {
            var line = branch.Centerline;
            if (line.Count < 2) continue;
            if (ArcLength(line) < beadWidth * 0.1f) continue;    // degenerate only —
            // short stubs MUST print: they are the first layers of a growing finger,
            // and skipping them would leave the next layer's stub unsupported.

            var path = new PathD(line.Count + 1);

            // Extend the root of trunk branches one bead past the boundary so the
            // slit definitely crosses the perimeter and notches it.
            if (branch.ParentBranch < 0)
            {
                var dir = line[0] - line[1];
                float dl = dir.Length();
                if (dl > 1e-4f)
                {
                    dir /= dl;
                    var ext = line[0] + dir * beadWidth;
                    path.Add(new PointD(ext.X, ext.Y));
                }
            }
            foreach (var pt in line) path.Add(new PointD(pt.X, pt.Y));
            openLines.Add(path);
        }
        if (openLines.Count == 0) return new PathsD();

        var slit = Clipper.InflatePaths(openLines, halfBead, JoinType.Round, EndType.Round);

        // Tip support pads: a disc at every leaf tip makes the boundary loop around it.
        if (tipLoopRadius > 0f)
        {
            var discs = new PathsD();
            foreach (var branch in tree.Branches)
            {
                bool isLeaf = true;
                for (int oj = 0; oj < tree.Branches.Count; oj++)
                    if (tree.Branches[oj].ParentBranch >= 0
                        && ReferenceEquals(tree.Branches[tree.Branches[oj].ParentBranch], branch))
                    { isLeaf = false; break; }
                if (!isLeaf || branch.Centerline.Count < 2) continue;
                discs.Add(Disc(branch.Centerline[^1], tipLoopRadius, 24));
            }
            if (discs.Count > 0)
                slit = Clipper.Union(Clipper.Union(slit, FillRule.NonZero), discs, FillRule.NonZero);
        }

        return slit;
    }

    /// <summary>Emits every result polygon as a closed extrude loop; separate polygons
    /// (islands, holes) are connected by travels exactly like shell printing.</summary>
    private static void EmitLoops(PathsD polys, float z, ToolpathLayer layer, Func<Vector2, Vector3>? project,
        LightningLayerPlan? plan = null, float beadWidth = 0f, float tipLoopRadius = 0f)
    {
        Vector3 P(Vector2 p) => project?.Invoke(p) ?? new Vector3(p.X, p.Y, z);

        // Segments hugging a finger centerline (or a tip pad) are tagged so the
        // viewport can show/hide the fingers as their own display layer.
        float tagRadius = beadWidth * 0.8f;
        bool IsLightningSeg(Vector2 a2, Vector2 b2)
        {
            if (plan is null || plan.Trees.Count == 0) return false;
            var mid = (a2 + b2) * 0.5f;
            foreach (var tree in plan.Trees)
            {
                if (plan.DroppedTrees.Contains(tree.Id)) continue;
                foreach (var branch in tree.Branches)
                {
                    var line = branch.Centerline;
                    for (int i = 1; i < line.Count; i++)
                        if (DistToSegmentSq(mid, line[i - 1], line[i]) < tagRadius * tagRadius)
                            return true;
                    if (tipLoopRadius > 0f && line.Count > 0)
                    {
                        float rr = tipLoopRadius + beadWidth * 0.8f;
                        if (Vector2.DistanceSquared(mid, line[^1]) < rr * rr)
                            return true;
                    }
                }
            }
            return false;
        }

        // Largest outer first, then by area descending — mirrors shell ordering.
        var ordered = polys
            .Where(p => p.Count >= 3)
            .OrderByDescending(p => Math.Abs(Clipper.Area(p)))
            .ToList();
        if (ordered.Count == 0) return;

        Vector3? runningEnd = layer.Moves.Count > 0 ? layer.Moves[^1].To : null;

        foreach (var path in ordered)
        {
            // Start each loop at the vertex nearest the running end position.
            int start = 0;
            if (runningEnd is { } re)
            {
                float bd2 = float.MaxValue;
                for (int i = 0; i < path.Count; i++)
                {
                    var w = P(new Vector2((float)path[i].x, (float)path[i].y));
                    float d2 = (w.X - re.X) * (w.X - re.X) + (w.Y - re.Y) * (w.Y - re.Y);
                    if (d2 < bd2) { bd2 = d2; start = i; }
                }
            }

            var first2 = new Vector2((float)path[start].x, (float)path[start].y);
            var first  = P(first2);
            if (runningEnd is { } prev && Vector3.Distance(prev, first) > 0.01f)
                layer.Moves.Add(new ToolpathMove(prev, first, MoveKind.Travel));

            var cur  = first;
            var cur2 = first2;   // plane-local twin of cur — tagging must use plane space
            for (int k = 1; k <= path.Count; k++)
            {
                var idx  = (start + k) % path.Count;
                var nxt2 = new Vector2((float)path[idx].x, (float)path[idx].y);
                var nxt  = P(nxt2);
                if (Vector3.DistanceSquared(cur, nxt) < 1e-6f) continue;
                layer.Moves.Add(new ToolpathMove(cur, nxt, MoveKind.Extrude)
                    { IsLightning = IsLightningSeg(cur2, nxt2) });
                cur = nxt; cur2 = nxt2;
            }
            if (Vector3.DistanceSquared(cur, first) > 1e-6f)
                layer.Moves.Add(new ToolpathMove(cur, first, MoveKind.Extrude)
                    { IsLightning = IsLightningSeg(cur2, first2) });

            runningEnd = first;
        }
    }

    private static int CountOuters(PathsD polys)
    {
        int outers = 0;
        foreach (var p in polys)
            if (Clipper.Area(p) > 0) outers++;
        return outers;
    }

    private static float DistToSegmentSq(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-12f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    private static float ArcLength(List<Vector2> line)
    {
        float len = 0f;
        for (int i = 1; i < line.Count; i++)
            len += Vector2.Distance(line[i - 1], line[i]);
        return len;
    }

    private static PathD Disc(Vector2 c, float r, int segments)
    {
        var path = new PathD(segments);
        for (int i = 0; i < segments; i++)
        {
            float a = i / (float)segments * 2f * MathF.PI;
            path.Add(new PointD(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a)));
        }
        return path;
    }
}
