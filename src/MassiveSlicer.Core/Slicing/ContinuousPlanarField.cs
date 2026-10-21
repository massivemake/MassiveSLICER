using System.Numerics;
using Clipper2Lib;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// One curve per island on a planar layer. The wall is printed once. Infill
/// lines cover the closed area and each new line is the closest unused one,
/// welded when the gap is within 3× bead width, the same nearest-entry rule as
/// toolpath optimize. A bead already printed is not printed again, and no bead
/// crosses another. Separate islands are never unioned.
/// </summary>
public static class ContinuousPlanarField
{
    /// <summary>
    /// One open polyline per printed piece. Every bead is used once.
    /// <paramref name="preferStart"/> picks the wall vertex nearest the previous layer.
    /// </summary>
    public static List<List<Vector2>> Build(IReadOnlyList<List<Vector2>> contours, float spacing,
                                            Vector2? preferStart = null)
    {
        spacing = MathF.Max(spacing, 0.2f);
        var (outers, holes) = Classify(contours, spacing);
        var ordered = preferStart is Vector2 near
            ? outers.OrderBy(path => NearestDist(path, near)).ToList()
            : outers;
        var islands = new List<List<Vector2>>(ordered.Count);
        foreach (var outer in ordered)
        {
            var mine = new List<PathD>();
            foreach (var hole in holes)
                if (Clipper.PointInPolygon(Centroid(hole), outer) != PointInPolygonResult.IsOutside)
                    mine.Add(hole);
            foreach (var piece in BuildIsland(outer, mine, spacing, preferStart))
                if (piece.Count >= 2) islands.Add(piece);
        }
        return islands;
    }

    static List<List<Vector2>> BuildIsland(PathD outer, List<PathD> holes, float spacing, Vector2? preferStart)
    {
        var wall = From(outer);
        if (wall.Count < 3) return [];

        var region = new PathsD { outer };
        foreach (var hole in holes) region.Add(hole);
        var core = Clipper.InflatePaths(region, -spacing, JoinType.Miter, EndType.Polygon, 2.0);
        var unused = ScanSegments(core, spacing);
        var loops = new List<List<Vector2>>();
        foreach (var hole in holes)
        {
            var loop = From(hole);
            if (loop.Count >= 3) loops.Add(loop);
        }

        // The climb from the previous layer starts on the nearest wall vertex.
        int at = 0;
        if (preferStart is Vector2 prefer)
        {
            float best = float.MaxValue;
            for (int i = 0; i < wall.Count; i++)
            {
                float d = Vector2.Distance(wall[i], prefer);
                if (d < best) { best = d; at = i; }
            }
        }

        var blocked = new List<(Vector2 A, Vector2 B)>();
        for (int i = 0; i < wall.Count; i++)
            blocked.Add((wall[i], wall[(i + 1) % wall.Count]));

        var pieces = new List<List<Vector2>>();
        var line = new List<Vector2>();
        for (int k = 0; k <= wall.Count; k++)
            Append(line, [wall[(at + k) % wall.Count]]);
        // Same rule as ToolpathOptimizer: the next line is the closest unused
        // entry, and a gap within 3× bead width is an extruded stitch.
        Grow(line, unused, loops, blocked, outer, holes, spacing);
        pieces.Add(line);

        var cursor = line[^1];
        while (unused.Count > 0 || loops.Count > 0)
        {
            int segI = -1, loopI = -1, loopAt = 0;
            float best = float.MaxValue;
            for (int i = 0; i < unused.Count; i++)
            {
                var seg = unused[i];
                float d = MathF.Min(
                    Vector2.DistanceSquared(cursor, seg[0]),
                    Vector2.DistanceSquared(cursor, seg[^1]));
                if (d < best) { best = d; segI = i; loopI = -1; }
            }
            for (int i = 0; i < loops.Count; i++)
            {
                var loop = loops[i];
                for (int v = 0; v < loop.Count; v++)
                {
                    float d = Vector2.DistanceSquared(cursor, loop[v]);
                    if (d < best) { best = d; loopI = i; segI = -1; loopAt = v; }
                }
            }

            var extra = new List<Vector2>();
            if (loopI >= 0)
            {
                var loop = loops[loopI];
                loops.RemoveAt(loopI);
                if (LoopCuts(blocked, loop)) continue;
                for (int k = 0; k <= loop.Count; k++)
                    Append(extra, [loop[(loopAt + k) % loop.Count]]);
                Remember(blocked, extra);
            }
            else
            {
                var seg = unused[segI];
                unused.RemoveAt(segI);
                if (seg.Count < 2 || PathCuts(blocked, seg) || PathHitsSelf(seg)) continue;
                if (Vector2.DistanceSquared(cursor, seg[^1]) < Vector2.DistanceSquared(cursor, seg[0]))
                    seg.Reverse();
                Append(extra, seg);
                Remember(blocked, seg);
            }
            Grow(extra, unused, loops, blocked, outer, holes, spacing);
            if (extra.Count < 2) continue;
            pieces.Add(extra);
            cursor = extra[^1];
        }
        return pieces;
    }

    /// <summary>Weld the closest legal line onto <paramref name="line"/> until none remain in reach.</summary>
    static void Grow(List<Vector2> line, List<List<Vector2>> unused, List<List<Vector2>> loops,
                     List<(Vector2 A, Vector2 B)> blocked,
                     PathD outer, List<PathD> holes, float spacing)
    {
        while (line.Count > 0 && TryPick(line[^1], unused, loops, blocked, outer, holes, spacing,
                   out int segIndex, out bool reverse, out int loopIndex, out int loopAt))
        {
            if (loopIndex >= 0)
            {
                var loop = loops[loopIndex];
                loops.RemoveAt(loopIndex);
                var spun = new List<Vector2>(loop.Count + 1);
                for (int k = 0; k <= loop.Count; k++)
                    spun.Add(loop[(loopAt + k) % loop.Count]);
                if (Vector2.Distance(line[^1], spun[0]) > 1e-3f)
                    blocked.Add((line[^1], spun[0]));
                Append(line, spun);
                Remember(blocked, spun);
            }
            else
            {
                var seg = unused[segIndex];
                unused.RemoveAt(segIndex);
                if (reverse) seg.Reverse();
                if (Vector2.Distance(line[^1], seg[0]) > 1e-3f)
                    blocked.Add((line[^1], seg[0]));
                Append(line, seg);
                Remember(blocked, seg);
            }
        }
    }

    static bool TryPick(Vector2 cursor, List<List<Vector2>> unused, List<List<Vector2>> loops,
                        List<(Vector2 A, Vector2 B)> blocked,
                        PathD outer, List<PathD> holes, float spacing,
                        out int segIndex, out bool reverse, out int loopIndex, out int loopAt)
    {
        int bestSeg = -1, bestLoop = -1, bestAt = 0;
        bool bestRev = false;
        float best = float.MaxValue;
        float limit = spacing * 3f;
        for (int i = 0; i < unused.Count; i++)
        {
            var seg = unused[i];
            if (seg.Count < 2) continue;
            ConsiderSeg(i, seg, false);
            ConsiderSeg(i, seg, true);
        }
        for (int i = 0; i < loops.Count; i++)
        {
            var loop = loops[i];
            for (int v = 0; v < loop.Count; v++)
                ConsiderLoop(i, v, loop[v]);
        }
        segIndex = bestSeg;
        reverse = bestRev;
        loopIndex = bestLoop;
        loopAt = bestAt;
        return bestSeg >= 0 || bestLoop >= 0;

        void ConsiderSeg(int i, List<Vector2> seg, bool rev)
        {
            var entry = rev ? seg[^1] : seg[0];
            float d = Vector2.Distance(cursor, entry);
            if (d >= best || d > limit) return;
            if (!LegalLink(cursor, entry, unused, loops, blocked, outer, holes, i)) return;
            if (rev)
            {
                var flipped = new List<Vector2>(seg);
                flipped.Reverse();
                if (PathCuts(blocked, flipped) || LinkHitsChain(cursor, flipped)) return;
            }
            else if (PathCuts(blocked, seg) || LinkHitsChain(cursor, seg))
                return;
            best = d;
            bestSeg = i;
            bestRev = rev;
            bestLoop = -1;
        }

        void ConsiderLoop(int i, int v, Vector2 entry)
        {
            float d = Vector2.Distance(cursor, entry);
            if (d >= best || d > limit) return;
            if (!LegalLink(cursor, entry, unused, loops, blocked, outer, holes, -1)) return;
            best = d;
            bestSeg = -1;
            bestRev = false;
            bestLoop = i;
            bestAt = v;
        }
    }

    static bool LegalLink(Vector2 cursor, Vector2 entry, List<List<Vector2>> unused, List<List<Vector2>> loops,
                          List<(Vector2 A, Vector2 B)> blocked, PathD outer, List<PathD> holes, int skipSeg)
    {
        if (!InMaterial(cursor, entry, outer, holes)) return false;
        if (Cuts(blocked, cursor, entry)) return false;
        if (HitsOthers(cursor, entry, unused, skipSeg)) return false;
        return !HitsLoops(cursor, entry, loops);
    }

    static bool LoopCuts(List<(Vector2 A, Vector2 B)> blocked, List<Vector2> loop)
    {
        int n = loop.Count;
        for (int i = 0; i < n; i++)
            if (Cuts(blocked, loop[i], loop[(i + 1) % n])) return true;
        return false;
    }

    static bool HitsLoops(Vector2 a, Vector2 b, List<List<Vector2>> loops)
    {
        foreach (var loop in loops)
        {
            int n = loop.Count;
            for (int k = 0; k < n; k++)
            {
                var c = loop[k];
                var d = loop[(k + 1) % n];
                if (BboxMiss(a, b, c, d)) continue;
                if (ProperCross(a, b, c, d) || OverlapsBead(a, b, c, d)) return true;
            }
        }
        return false;
    }

    static bool HitsOthers(Vector2 a, Vector2 b, List<List<Vector2>> unused, int skip)
    {
        for (int i = 0; i < unused.Count; i++)
        {
            if (i == skip) continue;
            var seg = unused[i];
            for (int k = 1; k < seg.Count; k++)
            {
                if (BboxMiss(a, b, seg[k - 1], seg[k])) continue;
                if (ProperCross(a, b, seg[k - 1], seg[k]) || OverlapsBead(a, b, seg[k - 1], seg[k]))
                    return true;
            }
        }
        return false;
    }

    static bool BboxMiss(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        if (MathF.Max(c.X, d.X) < MathF.Min(a.X, b.X) || MathF.Min(c.X, d.X) > MathF.Max(a.X, b.X)) return true;
        if (MathF.Max(c.Y, d.Y) < MathF.Min(a.Y, b.Y) || MathF.Min(c.Y, d.Y) > MathF.Max(a.Y, b.Y)) return true;
        return false;
    }

    static List<List<Vector2>> ScanSegments(PathsD region, float spacing)
    {
        var polys = new List<List<Vector2>>();
        foreach (var path in region)
        {
            if (Math.Abs(Clipper.Area(path)) < 1e-3) continue;
            polys.Add(From(path));
        }
        if (polys.Count == 0) return [];

        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var poly in polys)
            foreach (var p in poly)
            {
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        if (maxY - minY < spacing * 0.5f) return [];

        var segs = new List<List<Vector2>>();
        for (float y = minY + spacing * 0.5f; y < maxY - spacing * 0.15f; y += spacing)
        {
            var xs = Crossings(polys, y);
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                if (xs[i + 1] - xs[i] <= spacing * 0.25f) continue;
                segs.Add([new Vector2(xs[i], y), new Vector2(xs[i + 1], y)]);
            }
        }
        return segs;
    }

    static float NearestDist(PathD path, Vector2 p)
    {
        float best = float.MaxValue;
        foreach (var q in path)
        {
            float dx = (float)q.x - p.X;
            float dy = (float)q.y - p.Y;
            float d = dx * dx + dy * dy;
            if (d < best) best = d;
        }
        return best;
    }

    static void Remember(List<(Vector2 A, Vector2 B)> blocked, List<Vector2> path)
    {
        for (int i = 1; i < path.Count; i++)
            blocked.Add((path[i - 1], path[i]));
    }

    static bool PathCuts(List<(Vector2 A, Vector2 B)> blocked, List<Vector2> path)
    {
        for (int i = 1; i < path.Count; i++)
            if (Cuts(blocked, path[i - 1], path[i])) return true;
        return false;
    }

    static bool Cuts(List<(Vector2 A, Vector2 B)> blocked, Vector2 a, Vector2 b)
    {
        foreach (var (c, d) in blocked)
            if (ProperCross(a, b, c, d) || OverlapsBead(a, b, c, d)) return true;
        return false;
    }

    static bool LinkHitsChain(Vector2 a, List<Vector2> path)
    {
        if (path.Count < 2) return false;
        var b = path[0];
        for (int i = 1; i < path.Count; i++)
            if (ProperCross(a, b, path[i - 1], path[i]) || OverlapsBead(a, b, path[i - 1], path[i]))
                return true;
        return false;
    }

    static bool PathHitsSelf(List<Vector2> path)
    {
        for (int i = 1; i < path.Count; i++)
        for (int j = i + 2; j < path.Count; j++)
        {
            if (ProperCross(path[i - 1], path[i], path[j - 1], path[j])) return true;
            if (OverlapsBead(path[i - 1], path[i], path[j - 1], path[j])) return true;
        }
        return false;
    }

    /// <summary>True when the open segments cross at a point that is not a shared endpoint.
    /// A bead that rides another bead is <see cref="OverlapsBead"/>, not a crossing.</summary>
    public static bool ProperCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        if (Near(a, c) || Near(a, d) || Near(b, c) || Near(b, d)) return false;
        int o1 = Orient(a, b, c);
        int o2 = Orient(a, b, d);
        int o3 = Orient(c, d, a);
        int o4 = Orient(c, d, b);
        if (o1 == 0 && o2 == 0) return false;
        return o1 != o2 && o3 != o4 && o1 != 0 && o2 != 0 && o3 != 0 && o4 != 0;
    }

    static int Orient(Vector2 a, Vector2 b, Vector2 c)
    {
        float v = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(v) <= 1e-4f) return 0;
        return v > 0f ? 1 : -1;
    }

    static bool Near(Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, b) < 1e-4f;

    /// <summary>
    /// True when the two segments cover the same bead for a positive length,
    /// including a reverse pass. Touching at an endpoint is not an overlap.
    /// </summary>
    public static bool OverlapsBead(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        if (Near(a, b) || Near(c, d)) return false;
        if (Orient(a, b, c) != 0 || Orient(a, b, d) != 0) return false;
        bool useX = MathF.Abs(b.X - a.X) >= MathF.Abs(b.Y - a.Y);
        float a0 = useX ? a.X : a.Y;
        float a1 = useX ? b.X : b.Y;
        float c0 = useX ? c.X : c.Y;
        float c1 = useX ? d.X : d.Y;
        float overlap = MathF.Min(MathF.Max(a0, a1), MathF.Max(c0, c1))
            - MathF.Max(MathF.Min(a0, a1), MathF.Min(c0, c1));
        return overlap > 0.05f;
    }

    /// <summary>
    /// Joins infill chains whose connector stays inside the island. Chains that
    /// would have to cross a hole or leave the mesh stay separate.
    /// </summary>
    static List<List<Vector2>> MergeInside(List<List<Vector2>> chains, PathsD region)
    {
        var pool = new List<List<Vector2>>();
        foreach (var chain in chains)
            if (chain.Count >= 2) pool.Add([.. chain]);

        var merged = new List<List<Vector2>>();
        while (pool.Count > 0)
        {
            var line = pool[0];
            pool.RemoveAt(0);
            bool grew = true;
            while (grew && pool.Count > 0)
            {
                grew = false;
                int best = -1;
                bool reverse = false;
                float bestD = float.MaxValue;
                for (int i = 0; i < pool.Count; i++)
                {
                    var candidate = pool[i];
                    float d0 = Vector2.Distance(line[^1], candidate[0]);
                    float d1 = Vector2.Distance(line[^1], candidate[^1]);
                    if (d0 < bestD && InsideSegment(line[^1], candidate[0], region))
                    { bestD = d0; best = i; reverse = false; }
                    if (d1 < bestD && InsideSegment(line[^1], candidate[^1], region))
                    { bestD = d1; best = i; reverse = true; }
                }
                if (best < 0) break;
                var next = pool[best];
                pool.RemoveAt(best);
                if (reverse) next.Reverse();
                Append(line, next);
                grew = true;
            }
            merged.Add(line);
        }
        return merged;
    }

    static bool InsideSegment(Vector2 a, Vector2 b, PathsD region)
    {
        for (int i = 1; i <= 3; i++)
        {
            float t = i / 4f;
            if (!Inside(a + (b - a) * t, region)) return false;
        }
        return true;
    }

    static bool Inside(Vector2 p, PathsD region)
    {
        // Inside the outer path is not enough: the hole sits inside that outline
        // and is not material. A connector through the opening was being kept.
        var q = new PointD(p.X, p.Y);
        bool inOuter = false;
        foreach (var path in region)
        {
            var hit = Clipper.PointInPolygon(q, path);
            if (hit == PointInPolygonResult.IsOutside) continue;
            if (Clipper.Area(path) < 0)
            {
                if (hit == PointInPolygonResult.IsInside) return false;
                continue;
            }
            inOuter = true;
        }
        return inOuter;
    }

    /// <summary>
    /// A travel from <paramref name="from"/> to <paramref name="to"/>. When the
    /// straight segment leaves the solid, the path walks the outer wall instead
    /// of cutting through an opening.
    /// </summary>
    public static List<Vector2> TravelAround(Vector2 from, Vector2 to, IReadOnlyList<List<Vector2>> loops)
    {
        if (loops.Count == 0 || SegmentInMaterial(from, to, loops))
            return [from, to];

        var outer = loops.OrderByDescending(AbsArea).First();
        int n = outer.Count;
        if (n < 3) return [from, to];
        int ia = 0, ib = 0;
        float da = float.MaxValue, db = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            float d0 = Vector2.Distance(outer[i], from);
            float d1 = Vector2.Distance(outer[i], to);
            if (d0 < da) { da = d0; ia = i; }
            if (d1 < db) { db = d1; ib = i; }
        }

        int forward = (ib - ia + n) % n;
        int backward = (ia - ib + n) % n;
        var route = new List<Vector2> { from };
        if (forward <= backward)
        {
            for (int i = 0; i <= forward; i++)
                AddIfFar(route, outer[(ia + i) % n]);
        }
        else
        {
            for (int i = 0; i <= backward; i++)
                AddIfFar(route, outer[(ia - i + n) % n]);
        }
        AddIfFar(route, to);
        return route;
    }

    static void AddIfFar(List<Vector2> line, Vector2 p)
    {
        if (line.Count == 0 || Vector2.Distance(line[^1], p) > 0.05f)
            line.Add(p);
    }

    static bool SegmentInMaterial(Vector2 a, Vector2 b, IReadOnlyList<List<Vector2>> loops)
    {
        for (int i = 1; i <= 4; i++)
            if (!PointInMaterial(a + (b - a) * (i / 5f), loops)) return false;
        return true;
    }

    static bool PointInMaterial(Vector2 p, IReadOnlyList<List<Vector2>> loops)
    {
        int hits = 0;
        foreach (var loop in loops)
        {
            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % n];
                if ((a.Y > p.Y) == (b.Y > p.Y)) continue;
                float x = a.X + (p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                if (x > p.X) hits++;
            }
        }
        return hits % 2 == 1;
    }

    static float AbsArea(List<Vector2> loop)
    {
        float a = 0f;
        for (int i = 0; i < loop.Count; i++)
        {
            var c = loop[i];
            var d = loop[(i + 1) % loop.Count];
            a += c.X * d.Y - d.X * c.Y;
        }
        return MathF.Abs(a);
    }

    /// <summary>
    /// Rotates a closed polyline so it starts and ends on the vertex nearest
    /// <paramref name="target"/>. The climb to the next layer then stays on the
    /// wall instead of cutting across a hole.
    /// </summary>
    public static void SeatClosed(List<Vector2> line, Vector2 target)
    {
        if (line.Count < 4) return;
        bool closed = Vector2.Distance(line[0], line[^1]) < 1e-2f;
        int n = closed ? line.Count - 1 : line.Count;
        if (n < 3) return;
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            float d = Vector2.Distance(line[i], target);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best == 0 && closed) return;
        var spun = new List<Vector2>(n + 1);
        for (int i = 0; i < n; i++)
            spun.Add(line[(best + i) % n]);
        spun.Add(spun[0]);
        line.Clear();
        line.AddRange(spun);
    }

    static bool PickEntry(List<Vector2> wall, List<Vector2> chain,
                          List<(Vector2 A, Vector2 B)> blocked,
                          PathD outer, List<PathD> holes, int? lockedAt,
                          out int at, out List<Vector2> directed)
    {
        at = lockedAt ?? 0;
        directed = chain;
        float best = float.MaxValue;
        bool found = false;
        var flipped = new List<Vector2>(chain);
        flipped.Reverse();
        foreach (var path in new[] { chain, flipped })
        {
            if (PathCuts(blocked, path)) continue;
            for (int i = 0; i < wall.Count; i++)
            {
                if (lockedAt is int only && i != only) continue;
                float d = Vector2.Distance(wall[i], path[0]);
                if (d >= best) continue;
                if (!InMaterial(wall[i], path[0], outer, holes)) continue;
                if (Cuts(blocked, wall[i], path[0])) continue;
                if (LinkHitsChain(wall[i], path)) continue;
                best = d;
                at = i;
                directed = path;
                found = true;
            }
        }
        return found;
    }

    static bool ClosestPair(List<Vector2> wall, List<Vector2> hole, PathD outer, List<PathD> holes,
                            out int atWall, out int atHole)
    {
        atWall = 0;
        atHole = 0;
        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < wall.Count; i++)
        for (int j = 0; j < hole.Count; j++)
        {
            float d = Vector2.Distance(wall[i], hole[j]);
            if (d >= best || !InMaterial(wall[i], hole[j], outer, holes)) continue;
            best = d;
            atWall = i;
            atHole = j;
            found = true;
        }
        return found;
    }

    static bool InMaterial(Vector2 a, Vector2 b, PathD outer, List<PathD> holes)
    {
        for (int i = 1; i <= 4; i++)
        {
            var p = a + (b - a) * (i / 5f);
            var q = new PointD(p.X, p.Y);
            if (Clipper.PointInPolygon(q, outer) == PointInPolygonResult.IsOutside) return false;
            foreach (var hole in holes)
                if (Clipper.PointInPolygon(q, hole) == PointInPolygonResult.IsInside) return false;
        }
        return true;
    }

    static void Append(List<Vector2> line, List<Vector2> more)
    {
        foreach (var p in more)
        {
            if (line.Count == 0 || Vector2.Distance(line[^1], p) > 1e-3f)
                line.Add(p);
        }
    }

    static (List<PathD> Outers, List<PathD> Holes) Classify(IReadOnlyList<List<Vector2>> contours, float spacing)
    {
        var paths = new List<PathD>();
        foreach (var contour in contours)
        {
            if (contour.Count < 3) continue;
            var path = ToPath(contour);
            if (Math.Abs(Clipper.Area(path)) < spacing * spacing * 0.25) continue;
            if (Clipper.Area(path) < 0) path.Reverse();
            paths.Add(path);
        }

        var outers = new List<PathD>();
        var holes = new List<PathD>();
        foreach (var path in paths.OrderByDescending(p => Math.Abs(Clipper.Area(p))))
        {
            bool inside = false;
            foreach (var outer in outers)
            {
                if (Clipper.PointInPolygon(Centroid(path), outer) != PointInPolygonResult.IsOutside)
                {
                    inside = true;
                    break;
                }
            }
            if (inside)
            {
                if (Clipper.Area(path) > 0) path.Reverse();
                holes.Add(path);
            }
            else
            {
                if (Clipper.Area(path) < 0) path.Reverse();
                outers.Add(path);
            }
        }
        return (outers, holes);
    }

    static List<List<Vector2>> Serpentine(PathsD region, float spacing)
    {
        var polys = new List<List<Vector2>>();
        foreach (var path in region)
        {
            if (Math.Abs(Clipper.Area(path)) < 1e-3) continue;
            polys.Add(From(path));
        }
        if (polys.Count == 0) return [];

        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var poly in polys)
            foreach (var p in poly)
            {
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        if (maxY - minY < spacing * 0.5f) return [];

        var chains = new List<List<Vector2>>();
        var current = new List<Vector2>();
        bool rightward = true;
        float turn = spacing * 1.25f;

        for (float y = minY + spacing * 0.5f; y < maxY - spacing * 0.15f; y += spacing)
        {
            var xs = Crossings(polys, y);
            if (xs.Count < 2) continue;
            var spans = new List<(float x0, float x1)>();
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] - xs[i] > spacing * 0.25f)
                    spans.Add((xs[i], xs[i + 1]));
            if (!rightward) spans.Reverse();

            foreach (var (x0, x1) in spans)
            {
                var start = new Vector2(rightward ? x0 : x1, y);
                var end = new Vector2(rightward ? x1 : x0, y);
                bool crossesVoid = current.Count > 0 && !Inside((current[^1] + start) * 0.5f, region);
                if (current.Count > 0 && (Vector2.Distance(current[^1], start) > turn || crossesVoid))
                {
                    if (current.Count >= 2) chains.Add(current);
                    current = [];
                }
                if (current.Count == 0 || Vector2.Distance(current[^1], start) > 1e-3f)
                    current.Add(start);
                current.Add(end);
                rightward = !rightward;
            }
        }
        if (current.Count >= 2) chains.Add(current);
        return chains;
    }

    static List<float> Crossings(List<List<Vector2>> polys, float y)
    {
        var xs = new List<float>();
        foreach (var poly in polys)
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % n];
                if ((a.Y > y) == (b.Y > y)) continue;
                float dy = b.Y - a.Y;
                if (MathF.Abs(dy) < 1e-8f) continue;
                xs.Add(a.X + (y - a.Y) / dy * (b.X - a.X));
            }
        }
        xs.Sort();
        return xs;
    }

    static int Nearest(List<Vector2> loop, Vector2 p)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < loop.Count; i++)
        {
            float d = Vector2.Distance(loop[i], p);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    static PointD Centroid(PathD path)
    {
        double x = 0, y = 0;
        foreach (var p in path) { x += p.x; y += p.y; }
        double n = Math.Max(1, path.Count);
        return new PointD(x / n, y / n);
    }

    static PathD ToPath(List<Vector2> contour)
    {
        var path = new PathD(contour.Count);
        foreach (var p in contour) path.Add(new PointD(p.X, p.Y));
        return path;
    }

    static List<Vector2> From(PathD path)
    {
        var list = new List<Vector2>(path.Count);
        foreach (var p in path) list.Add(new Vector2((float)p.x, (float)p.y));
        if (list.Count > 1 && Vector2.Distance(list[0], list[^1]) < 1e-3f)
            list.RemoveAt(list.Count - 1);
        return list;
    }
}
