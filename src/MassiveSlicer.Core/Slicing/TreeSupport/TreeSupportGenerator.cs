using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Lightning;

namespace MassiveSlicer.Core.Slicing.TreeSupport;

/// <summary>
/// Emits freestanding tree dual-wall rectangle outlines only.
/// Does <b>not</b> weld into part paths or neighbouring trees — gaps use
/// <see cref="MoveKind.Travel"/> (extruder off) then resume extrusion on the next loop.
/// </summary>
public static class TreeSupportGenerator
{
    /// <param name="minWorldZ">
    /// Hard floor for extruded points (typically mesh/bed bottom). Points below
    /// this are clamped up so the extruder never dives through the bed.
    /// </param>
    public static void Emit(
        TreeSupportLayerPlan? plan,
        float z,
        ToolpathLayer layer,
        float beadWidth,
        List<List<Vector2>>? partFillPolys = null,
        Func<Vector2, Vector3>? project = null,
        float? minWorldZ = null)
    {
        if (plan is null || plan.Branches.Count == 0 || beadWidth < 0.05f) return;

        float half = beadWidth * 0.5f;
        float maxOutlineSpan = beadWidth * 20f;

        PathsD? part = null;
        if (partFillPolys is { Count: > 0 })
        {
            part = LightningPlanner.ToPathsD(partFillPolys, beadWidth);
            if (part.Count == 0) part = null;
        }

        // Inflate each planned outline independently — no union/bridge with part or peers.
        var loops = new List<List<Vector2>>();
        int planned = 0, emitted = 0;
        foreach (var branch in plan.Branches)
        {
            if (branch.Count < 3) continue;
            planned++;

            int n = branch.Count;
            if (n > 1 && Vector2.DistanceSquared(branch[0], branch[^1]) < 1e-6f)
                n--;
            if (n < 3) continue;

            float minU = float.MaxValue, maxU = float.MinValue;
            float minV = float.MaxValue, maxV = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (branch[i].X < minU) minU = branch[i].X;
                if (branch[i].X > maxU) maxU = branch[i].X;
                if (branch[i].Y < minV) minV = branch[i].Y;
                if (branch[i].Y > maxV) maxV = branch[i].Y;
            }
            if (maxU - minU > maxOutlineSpan || maxV - minV > maxOutlineSpan)
            {
                System.Console.WriteLine(
                    $"[tree-support] emit L{layer.Index}: skip oversized outline " +
                    $"{maxU - minU:0.#}×{maxV - minV:0.#} mm");
                continue;
            }

            var centerline = new PathD(n);
            for (int i = 0; i < n; i++)
                centerline.Add(new PointD(branch[i].X, branch[i].Y));

            // Freestanding: push fully outside if midpoint is inside the part.
            if (part is not null && OutlineMidInside(centerline, part))
            {
                var pushed = PushClosedOutside(centerline, part, beadWidth * 2.25f);
                if (pushed.Count >= 3) centerline = pushed;
            }

            var ring = Clipper.InflatePaths(
                new PathsD { centerline }, half, JoinType.Miter, EndType.Polygon, 2.0);
            if (ring.Count == 0) continue;

            // Keep each tree ring as its own island (no Union across peers).
            foreach (var poly in ring)
            {
                if (poly.Count < 3) continue;
                if (Clipper.Area(poly) < 0) continue; // skip holes
                var pts = new List<Vector2>(poly.Count);
                foreach (var q in poly)
                    pts.Add(new Vector2((float)q.x, (float)q.y));
                if (pts.Count >= 3)
                {
                    loops.Add(pts);
                    emitted++;
                }
            }
        }

        if (loops.Count == 0)
        {
            if (planned > 0)
                System.Console.WriteLine(
                    $"[tree-support] emit L{layer.Index}: planned={planned} but 0 rings");
            return;
        }

        float zFloor = minWorldZ ?? float.NegativeInfinity;
        Vector3 P(Vector2 p)
        {
            var w = project?.Invoke(p) ?? new Vector3(p.X, p.Y, z);
            if (w.Z < zFloor) w = w with { Z = zFloor };
            return w;
        }

        // Nearest-neighbour order; always Travel between islands / previous path end.
        Vector3? cursor = layer.Moves.Count > 0 ? layer.Moves[^1].To : null;
        var remaining = new List<int>(loops.Count);
        for (int i = 0; i < loops.Count; i++) remaining.Add(i);

        int moveBefore = layer.Moves.Count;
        while (remaining.Count > 0)
        {
            int bestIdx = 0;
            int bestLoop = remaining[0];
            int bestStart = 0;
            float bestDist = float.MaxValue;
            var from = cursor ?? P(loops[bestLoop][0]);

            for (int ri = 0; ri < remaining.Count; ri++)
            {
                var lp = loops[remaining[ri]];
                for (int k = 0; k < lp.Count; k++)
                {
                    float d = Vector3.Distance(from, P(lp[k]));
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestIdx = ri;
                        bestLoop = remaining[ri];
                        bestStart = k;
                    }
                }
            }

            remaining.RemoveAt(bestIdx);
            var pts = loops[bestLoop];
            if (bestStart > 0)
            {
                var rot = new List<Vector2>(pts.Count);
                for (int k = 0; k < pts.Count; k++)
                    rot.Add(pts[(bestStart + k) % pts.Count]);
                pts = rot;
            }

            var start = P(pts[0]);
            if (cursor is { } c && Vector3.Distance(c, start) > beadWidth * 0.15f)
            {
                // Always travel — never extrude-bridge into shells or other trees.
                layer.Moves.Add(new ToolpathMove(c, start, MoveKind.Travel)
                {
                    Normal = Vector3.UnitZ,
                });
            }

            for (int i = 0; i < pts.Count; i++)
            {
                var a = P(pts[i]);
                var b = P(pts[(i + 1) % pts.Count]);
                if (Vector3.Distance(a, b) < 1e-4f) continue;
                layer.Moves.Add(new ToolpathMove(a, b, MoveKind.Extrude)
                {
                    IsLightning = true,
                    Normal = Vector3.UnitZ,
                });
            }
            cursor = P(pts[0]);
        }

        int added = layer.Moves.Count - moveBefore;
        if (layer.Index == 0 || (layer.Index & 15) == 0 || added == 0)
            System.Console.WriteLine(
                $"[tree-support] emit L{layer.Index}: planned={planned} rings={emitted} " +
                $"moves+={added} (freestanding + travel only)");
    }

    private static bool OutlineMidInside(PathD centerline, PathsD part)
    {
        if (centerline.Count == 0 || part.Count == 0) return false;
        double sx = 0, sy = 0;
        foreach (var p in centerline) { sx += p.x; sy += p.y; }
        var mid = new PointD(sx / centerline.Count, sy / centerline.Count);
        foreach (var poly in part)
        {
            if (Clipper.Area(poly) <= 0) continue;
            if (Clipper.PointInPolygon(mid, poly) != PointInPolygonResult.IsOutside)
                return true;
        }
        return false;
    }

    private static PathD PushClosedOutside(PathD centerline, PathsD part, float pushMm)
    {
        double sx = 0, sy = 0;
        foreach (var p in centerline) { sx += p.x; sy += p.y; }
        var mid = new Vector2((float)(sx / centerline.Count), (float)(sy / centerline.Count));
        var wall = ClosestOnBoundary(part, mid);
        var outDir = mid - wall;
        float ol = outDir.Length();
        Vector2 offset = ol > 1e-4f ? outDir / ol * pushMm : new Vector2(pushMm, 0f);

        var shifted = new PathD(centerline.Count);
        foreach (var p in centerline)
            shifted.Add(new PointD(p.x + offset.X, p.y + offset.Y));
        return shifted;
    }

    private static Vector2 ClosestOnBoundary(PathsD region, Vector2 pt)
    {
        float best = float.MaxValue;
        Vector2 bestP = pt;
        foreach (var path in region)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var a = new Vector2((float)path[i].x, (float)path[i].y);
                var b = new Vector2((float)path[(i + 1) % path.Count].x,
                    (float)path[(i + 1) % path.Count].y);
                var q = ClosestOnSegment(pt, a, b);
                float d = Vector2.DistanceSquared(pt, q);
                if (d < best) { best = d; bestP = q; }
            }
        }
        return bestP;
    }

    private static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Vector2.Dot(p - a, ab);
        float den = ab.LengthSquared();
        if (den < 1e-12f) return a;
        t = Math.Clamp(t / den, 0f, 1f);
        return a + ab * t;
    }
}
