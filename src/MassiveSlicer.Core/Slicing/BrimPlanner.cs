using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Bed-adhesion brim: offset loops alongside the FULL first-layer footprint.
///
/// Runs as the LAST toolpath step (after paint removals, X-bracing, patterns) so any
/// effect that adds or bulges first-layer geometry is enclosed — the footprint is
/// derived from the actual layer-0 extrude segments, not the mesh outline.
///
/// Loops print farthest → nearest, so the last one fuses against the part's first bead;
/// loop k's centreline sits (k − ½) bead widths from the footprint edge (adjacent beads
/// touching).
///
/// <para><b>Both sides come from one offset.</b> Growing the footprint produces a single
/// boundary that runs down BOTH sides of the path — on a real open wall it measures 2.01x the
/// path length, out along one side, round the end, back along the other. So
/// <see cref="BrimDirection"/> does not offset twice; it selects which stretches of that one
/// boundary to keep. On a closed wall loop the two sides are the outside and the bore, so
/// tubes and columns fall out of the same rule with no special case.</para>
///
/// <para><b>Side is measured against the path's direction of travel</b>, which keeps a run on
/// one side for its whole length. Choosing by concavity instead would switch sides wherever
/// curvature flips, and on an S-shaped wall that produces a brim that crosses the wall
/// halfway along.</para>
/// </summary>
public static class BrimPlanner
{
    /// <summary>A stretch of one offset ring that stays on a single side of the path.</summary>
    private readonly record struct Run(List<PointD> Points, bool WholeRing, int Side, double Length);

    /// <param name="meshContours">
    /// Layer 0's MESH cross-section (closed contours only). This defines the footprint TOPOLOGY.
    /// Deriving it from the toolpath instead drags in every gap between wall passes and every
    /// infill void: measured on a real capital, the toolpath footprint had 300 interior holes
    /// where the mesh had 0, and brim offset into all of them - 50 m of bead scattered through
    /// the part. Null falls back to the toolpath, for callers that have no mesh.
    /// </param>
    public static void Apply(
        Toolpath toolpath,
        SliceSettings settings,
        IReadOnlyList<IReadOnlyList<Vector2>>? meshContours = null)
    {
        if (!settings.BrimEnabled || settings.BrimLoops <= 0) return;
        if (toolpath.Layers.Count == 0) return;
        var layer0 = toolpath.Layers[0];
        if (layer0.Moves.Count == 0) return;

        float bead = MathF.Max(settings.BeadWidth, 0.5f);

        // 1) Footprint: dilate every layer-0 extrude segment by half a bead and union.
        //    The same segments are kept as centrelines — they are what "which side" is measured
        //    against, and they must be captured BEFORE the brim is prepended.
        var segs = new PathsD();
        var centre = new List<(Vector2 A, Vector2 B)>();
        float z = float.NaN;
        foreach (var m in layer0.Moves)
        {
            if (m.Kind != MoveKind.Extrude) continue;
            segs.Add(new PathD { new PointD(m.From.X, m.From.Y), new PointD(m.To.X, m.To.Y) });
            centre.Add((new Vector2(m.From.X, m.From.Y), new Vector2(m.To.X, m.To.Y)));
            if (float.IsNaN(z)) z = m.To.Z;
        }
        if (segs.Count == 0) return;
        var toolpathRegion = Clipper.Union(
            Clipper.InflatePaths(segs, bead * 0.5, JoinType.Round, EndType.Round),
            FillRule.NonZero);
        if (toolpathRegion.Count == 0) return;

        // The mesh decides the topology; the toolpath only widens the outer extent.
        //
        // Union the two so a pattern bulge or X-bracing detour poking outside the silhouette is still
        // enclosed, then FILL every hole the mesh does not itself have. That one step removes the
        // slicer's own internal structure - wall gaps, infill voids, seams, connecting paths - none of
        // which are part of the shape and none of which want a brim offset into them.
        var meshRegion = MeshRegion(meshContours);
        var footprint = toolpathRegion;
        if (meshRegion.Count > 0)
        {
            var combined = Clipper.Union(toolpathRegion, meshRegion, FillRule.NonZero);
            // Keep only positive (outer) rings: unioning them fills every hole at once.
            var outers = new PathsD();
            foreach (var r in combined) if (Clipper.Area(r) > 0) outers.Add(r);
            var filled = Clipper.Union(outers, FillRule.NonZero);
            // Put back only the mesh's OWN holes - a real bore keeps its inside brim.
            var meshHoles = new PathsD();
            foreach (var r in meshRegion) if (Clipper.Area(r) < 0) meshHoles.Add(Reversed(r));
            footprint = meshHoles.Count > 0
                ? Clipper.Difference(filled, meshHoles, FillRule.NonZero)
                : filled;
        }
        if (footprint.Count == 0) return;

        var index = new SegmentGrid(centre);

        // 2) Offset rings, farthest first. Round offset joins tessellate corners into many
        //    sub-mm points; simplify each ring (same as the wall contours) so the robot isn't
        //    fed points below its interpolation step — otherwise it stalls at every point while
        //    the screw keeps pumping (over-extrusion + jitter).
        double tol = Math.Max(settings.SimplificationTolerance, 0.3f);
        var runs = new List<Run>();
        for (int k = settings.BrimLoops; k >= 1; k--)
        {
            var rings = Clipper.InflatePaths(
                footprint, bead * (k - 0.5), JoinType.Round, EndType.Polygon);
            rings = Clipper.SimplifyPaths(rings, tol);
            foreach (var ring in rings)
            {
                if (ring.Count < 3) continue;
                SplitBySide(ring, index, runs);
            }
        }
        if (runs.Count == 0) return;

        // 3) Which side is "outside"? Not the winding — layer-0 move direction is not
        //    guaranteed, so a fixed left/right would flip meaning between parts. The outer side
        //    is always the LONGER one: on a closed loop the outer perimeter exceeds the bore,
        //    and on an open wall the convex side exceeds the concave. Measured, not assumed.
        double lenPos = runs.Where(r => r.Side > 0).Sum(r => r.Length);
        double lenNeg = runs.Where(r => r.Side < 0).Sum(r => r.Length);
        int outsideSide = lenPos >= lenNeg ? 1 : -1;

        bool wantOutside = settings.BrimDirection is BrimDirection.Outside or BrimDirection.Both;
        bool wantInside  = settings.BrimDirection is BrimDirection.Inside  or BrimDirection.Both;

        // Grouped by side, NOT interleaved per offset. Interleaving would hand the head back
        // and forth across the part once per loop, and every crossing is a within-layer travel
        // — a dead stop the extruder keeps pumping through. Grouped, the head crosses once.
        // Within each side the farthest-first order from the k loop above is preserved, so the
        // last loop printed on a side is the one against the part.
        var keep = new List<Run>(runs.Count);
        if (wantOutside) keep.AddRange(runs.Where(r => r.Side == outsideSide));
        if (wantInside)  keep.AddRange(runs.Where(r => r.Side != outsideSide));
        if (keep.Count == 0) return;

        // 4) Emit. A run covering a whole ring closes; a partial run stays open, because
        //    closing it would draw a chord straight across the part.
        var brim = new List<ToolpathMove>();
        Vector3? cur = null;
        foreach (var run in keep)
        {
            var pts = new List<Vector3>(run.Points.Count + 1);
            foreach (var p in run.Points) pts.Add(new Vector3((float)p.x, (float)p.y, z));
            if (run.WholeRing && pts.Count > 2) pts.Add(pts[0]);
            if (pts.Count < 2) continue;
            if (cur is { } c)
                brim.Add(new ToolpathMove(c, pts[0], MoveKind.Travel) { IsBrim = true });
            for (int i = 1; i < pts.Count; i++)
                brim.Add(new ToolpathMove(pts[i - 1], pts[i], MoveKind.Extrude) { IsBrim = true });
            cur = pts[^1];
        }
        if (brim.Count == 0) return;
        if (cur is { } last)
            brim.Add(new ToolpathMove(last, layer0.Moves[0].From, MoveKind.Travel) { IsBrim = true });

        layer0.Moves.InsertRange(0, brim);
    }
    /// <summary>Closed mesh contours as a Clipper region. Open contours enclose nothing.</summary>
    private static PathsD MeshRegion(IReadOnlyList<IReadOnlyList<Vector2>>? contours)
    {
        var paths = new PathsD();
        if (contours is null) return paths;
        foreach (var c in contours)
        {
            if (c.Count < 3) continue;
            var path = new PathD(c.Count);
            foreach (var v in c) path.Add(new PointD(v.X, v.Y));
            paths.Add(path);
        }
        return paths.Count == 0 ? paths : Clipper.Union(paths, FillRule.NonZero);
    }

    /// <summary>Flips a ring's winding.</summary>
    private static PathD Reversed(PathD ring)
    {
        var flipped = new PathD(ring.Count);
        for (int i = ring.Count - 1; i >= 0; i--) flipped.Add(ring[i]);
        return flipped;
    }


    /// <summary>
    /// Walks a closed ring and cuts it into maximal stretches that stay on one side of the
    /// path. A ring entirely on one side yields a single whole-ring run (the closed-shape
    /// case); a ring straddling both yields one run per side.
    /// </summary>
    private static void SplitBySide(PathD ring, SegmentGrid index, List<Run> into)
    {
        int n = ring.Count;
        var sides = new int[n];
        for (int i = 0; i < n; i++) sides[i] = index.SideOf(ring[i]);

        // Points sitting exactly on the centreline are undecidable; inherit a decided neighbour
        // rather than starting a spurious run.
        for (int i = 0; i < n; i++)
        {
            if (sides[i] != 0) continue;
            for (int step = 1; step < n; step++)
            {
                int a = sides[(i - step + n) % n], b = sides[(i + step) % n];
                if (a != 0) { sides[i] = a; break; }
                if (b != 0) { sides[i] = b; break; }
            }
            if (sides[i] == 0) sides[i] = 1;
        }

        bool uniform = true;
        for (int i = 1; i < n && uniform; i++) if (sides[i] != sides[0]) uniform = false;
        if (uniform)
        {
            into.Add(new Run([.. ring], WholeRing: true, sides[0], Perimeter(ring, closed: true)));
            return;
        }

        // Start at a side change so runs are not split across the wrap-around seam.
        int start = 0;
        for (int i = 0; i < n; i++)
            if (sides[i] != sides[(i - 1 + n) % n]) { start = i; break; }

        var cur = new List<PointD>();
        int curSide = sides[start];
        for (int c = 0; c < n; c++)
        {
            int i = (start + c) % n;
            if (sides[i] != curSide && cur.Count > 0)
            {
                if (cur.Count >= 2)
                    into.Add(new Run(cur, WholeRing: false, curSide, Perimeter(cur, closed: false)));
                cur = [];
                curSide = sides[i];
            }
            cur.Add(ring[i]);
        }
        if (cur.Count >= 2)
            into.Add(new Run(cur, WholeRing: false, curSide, Perimeter(cur, closed: false)));
    }

    private static double Perimeter(IReadOnlyList<PointD> pts, bool closed)
    {
        double d = 0;
        for (int i = 1; i < pts.Count; i++) d += Dist(pts[i - 1], pts[i]);
        if (closed && pts.Count > 2) d += Dist(pts[^1], pts[0]);
        return d;
    }

    private static double Dist(PointD a, PointD b)
    {
        double dx = a.x - b.x, dy = a.y - b.y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Uniform-grid index over the layer-0 centrelines, answering "which side of the nearest
    /// segment is this point on". A first layer can carry tens of thousands of segments and
    /// each ring hundreds of points, so scanning every segment per point is not viable.
    /// </summary>
    private sealed class SegmentGrid
    {
        private readonly List<(Vector2 A, Vector2 B)> _segs;
        private readonly Dictionary<(int, int), List<int>> _cells = new();
        private readonly double _cell;
        private readonly double _minX, _minY;

        public SegmentGrid(List<(Vector2 A, Vector2 B)> segs)
        {
            _segs = segs;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var (a, b) in segs)
            {
                minX = Math.Min(minX, Math.Min(a.X, b.X)); maxX = Math.Max(maxX, Math.Max(a.X, b.X));
                minY = Math.Min(minY, Math.Min(a.Y, b.Y)); maxY = Math.Max(maxY, Math.Max(a.Y, b.Y));
            }
            _minX = minX; _minY = minY;
            double span = Math.Max(Math.Max(maxX - minX, maxY - minY), 1.0);
            // ~64 cells across the part: enough to cut the scan down without a huge table.
            _cell = span / 64.0;
            for (int i = 0; i < segs.Count; i++)
            {
                var (a, b) = segs[i];
                foreach (var key in CellsAlong(a, b)) Add(key, i);
            }
        }

        private void Add((int, int) key, int i)
        {
            if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
            list.Add(i);
        }

        private (int, int) KeyOf(double x, double y)
            => ((int)Math.Floor((x - _minX) / _cell), (int)Math.Floor((y - _minY) / _cell));

        private IEnumerable<(int, int)> CellsAlong(Vector2 a, Vector2 b)
        {
            // Walk the segment in cell-sized steps; enough for bucketing purposes.
            double len = Vector2.Distance(a, b);
            int steps = (int)Math.Ceiling(len / _cell) + 1;
            var seen = new HashSet<(int, int)>();
            for (int s = 0; s <= steps; s++)
            {
                float t = steps == 0 ? 0f : (float)s / steps;
                var p = Vector2.Lerp(a, b, t);
                if (seen.Add(KeyOf(p.X, p.Y))) yield return KeyOf(p.X, p.Y);
            }
        }

        /// <summary>+1 / −1 for the side of the nearest centreline; 0 when exactly on it.</summary>
        public int SideOf(PointD p)
        {
            var (cx, cy) = KeyOf(p.x, p.y);
            int best = -1;
            double bestD = double.MaxValue;
            // Grow the search ring until something is found — a point far outside the part sees
            // no nearby cell, and must still be classified.
            for (int r = 1; r <= 64 && best < 0; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r && r > 1) continue;
                    if (!_cells.TryGetValue((cx + dx, cy + dy), out var list)) continue;
                    foreach (int i in list)
                    {
                        double d = DistToSeg(p, _segs[i]);
                        if (d < bestD) { bestD = d; best = i; }
                    }
                }
            }
            if (best < 0)
            {
                // Fall back to a full scan rather than guessing.
                for (int i = 0; i < _segs.Count; i++)
                {
                    double d = DistToSeg(p, _segs[i]);
                    if (d < bestD) { bestD = d; best = i; }
                }
            }
            if (best < 0) return 0;
            var (a, b) = _segs[best];
            double cross = (b.X - a.X) * (p.y - a.Y) - (b.Y - a.Y) * (p.x - a.X);
            return cross > 1e-9 ? 1 : cross < -1e-9 ? -1 : 0;
        }

        private static double DistToSeg(PointD p, (Vector2 A, Vector2 B) s)
        {
            double vx = s.B.X - s.A.X, vy = s.B.Y - s.A.Y;
            double wx = p.x - s.A.X, wy = p.y - s.A.Y;
            double vv = vx * vx + vy * vy;
            double t = vv <= 1e-12 ? 0 : Math.Clamp((wx * vx + wy * vy) / vv, 0, 1);
            double dx = wx - t * vx, dy = wy - t * vy;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
