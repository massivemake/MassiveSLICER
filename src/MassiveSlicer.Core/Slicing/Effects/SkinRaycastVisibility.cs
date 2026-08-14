using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Classifies a layer's extrusion into "outer skin" and "interior" by horizontal visibility:
/// sweep rays across the layer from every compass direction, and whatever a ray reaches first is
/// skin. Rays never travel in Z, so this is a purely per-layer 2D question.
///
/// <para>
/// This exists because the obvious approach — nesting depth, "is this contour inside another
/// one" — is a point-in-polygon test and needs closed contours to mean anything. Parts that slice
/// into open chains (scanned, organic or non-watertight meshes) come back with every contour at
/// depth 0, so everything reads as outermost and nothing can be excluded. Measured on a real
/// part: 6,676,002 wall moves, every one of them at depth 0, zero interior. A depth-based scope
/// was tried first and removed for exactly that reason; do not reach for it again without
/// checking whether the target geometry actually closes. Visibility needs no closure, no nesting
/// and no watertight mesh, so it separates skin from structure where depth cannot.
/// </para>
///
/// <para>
/// A ray stops at the first thing it meets, but the skin is not a single point: a pattern pushes
/// the bead in and out, and the geometry behind the first hit within a bead width is the same
/// surface. So everything within <c>penetration</c> of the first hit in a lane counts as hit too.
/// On the part this was built for the two classes separate by a huge margin — the nearest interior
/// point sits 86mm behind the skin, median 356mm — so the exact penetration is not delicate.
/// </para>
/// </summary>
internal static class SkinRaycastVisibility
{
    /// <summary>
    /// Compass directions swept. Doubling this to 180 moved the result by 0.1% on a real layer,
    /// so this is past the point of diminishing returns rather than an arbitrary pick.
    /// </summary>
    private const int Directions = 72;

    /// <summary>
    /// A hit needs company. A ray that happens to line up with a slot can reach a long way inside
    /// and light up a few isolated points deep in the part; genuine skin is always part of a
    /// continuous run. A hit with fewer than this many hit neighbours nearby is discarded.
    /// </summary>
    private const int MinNeighbours = 2;

    /// <summary>
    /// How many of the <see cref="Directions"/> sweeps must reach a point before it counts as skin.
    ///
    /// <para>
    /// This is the real defence against a ray threading a slot. A point on the actual surface is
    /// the first thing hit across a wide arc of directions — roughly half of them for anything
    /// convex, and still tens of them down inside a concave notch. A point only reachable through
    /// a narrow gap is front-of-lane for one or two directions and no more. Counting directions
    /// separates those two populations by an order of magnitude, where a single hit cannot: two
    /// lucky points landing side by side survive a neighbour test but not this one.
    /// </para>
    /// </summary>
    private const int MinDirections = 3;

    /// <summary>
    /// Per-move interior mask, index-parallel to <paramref name="moves"/>. True = interior
    /// (leave straight). Travel, stitch and empty moves are false and should be ignored.
    /// </summary>
    internal static bool[] BuildInteriorMask(
        IReadOnlyList<ToolpathMove> moves, float beadWidth, float penetrationMm)
    {
        var interior = new bool[moves.Count];
        float lane = MathF.Max(beadWidth, 0.5f);
        float pen  = MathF.Max(penetrationMm, 0.1f);

        // -- 1. Sub-sample every extrusion. A long move must occlude along its whole length, not
        //       just at a midpoint, or a flat wall would block a single lane and let rays through
        //       the rest of itself.
        var sx = new List<float>(moves.Count * 2);
        var sy = new List<float>(moves.Count * 2);
        var owner = new List<int>(moves.Count * 2);

        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) continue;

            float len = Vector2.Distance(new Vector2(m.From.X, m.From.Y), new Vector2(m.To.X, m.To.Y));
            int steps = Math.Clamp((int)MathF.Ceiling(len / lane), 1, 512);
            for (int s = 0; s < steps; s++)
            {
                float t = (s + 0.5f) / steps;                 // centres, never the shared endpoints
                sx.Add(m.From.X + (m.To.X - m.From.X) * t);
                sy.Add(m.From.Y + (m.To.Y - m.From.Y) * t);
                owner.Add(i);
            }
        }

        int n = sx.Count;
        if (n == 0) return interior;

        // -- 2. Sweep. For each direction, u is the lane across the beam and v is depth along it;
        //       the largest v in a lane is the first surface the beam meets.
        var seenFrom = new int[n];       // how many directions reached this sample
        var depth    = new float[n];
        var laneOf   = new int[n];

        for (int d = 0; d < Directions; d++)
        {
            float th = d * MathF.PI * 2f / Directions;
            float ct = MathF.Cos(th), st = MathF.Sin(th);

            int lo = int.MaxValue, hi = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                depth[i]  = sx[i] * ct + sy[i] * st;
                int l     = (int)MathF.Floor((-sx[i] * st + sy[i] * ct) / lane);
                laneOf[i] = l;
                if (l < lo) lo = l;
                if (l > hi) hi = l;
            }

            int span = hi - lo + 1;
            if (span <= 0 || span > 8_000_000) continue;      // degenerate layer, skip this pass

            var front = new float[span];
            Array.Fill(front, float.NegativeInfinity);
            for (int i = 0; i < n; i++)
            {
                int l = laneOf[i] - lo;
                if (depth[i] > front[l]) front[l] = depth[i];
            }
            for (int i = 0; i < n; i++)
                if (depth[i] >= front[laneOf[i] - lo] - pen) seenFrom[i]++;
        }

        // -- 3. Skin is what enough directions agree on, then a neighbour pass for the rest.
        var visible = new bool[n];
        for (int i = 0; i < n; i++) visible[i] = seenFrom[i] >= MinDirections;
        DropIsolated(sx, sy, visible, lane * 3f);

        // -- 4. A move is interior unless most of it is visible. Majority rather than "any"
        //       keeps an infill line that merely touches the wall from being textured end to end.
        var seen = new int[moves.Count];
        var hits = new int[moves.Count];
        for (int i = 0; i < n; i++)
        {
            seen[owner[i]]++;
            if (visible[i]) hits[owner[i]]++;
        }
        for (int i = 0; i < moves.Count; i++)
            if (seen[i] > 0 && hits[i] * 2 < seen[i]) interior[i] = true;

        return interior;
    }

    /// <summary>
    /// Forces every contiguous extrusion run to a single verdict, by majority.
    /// <para>
    /// <see cref="WaveEffect"/> defers whole contours rather than individual moves — it has to,
    /// because a wave's phase is continuous along a contour and cannot be started and stopped
    /// partway without a step in the bead. Feeding it a per-move mask would let one loop come
    /// back half waved, so the verdict is settled per run before it gets there.
    /// </para>
    /// </summary>
    internal static void HomogenizeByContour(IReadOnlyList<ToolpathMove> moves, bool[] interior)
    {
        int i = 0;
        while (i < moves.Count)
        {
            if (moves[i].Kind != MoveKind.Extrude || moves[i].IsLayerStitch) { i++; continue; }

            int start = i, n = 0, inside = 0;
            while (i < moves.Count && moves[i].Kind == MoveKind.Extrude && !moves[i].IsLayerStitch)
            {
                n++;
                if (interior[i]) inside++;
                i++;
            }
            bool verdict = inside * 2 >= n;
            for (int k = start; k < start + n; k++) interior[k] = verdict;
        }
    }

    /// <summary>
    /// Clears hits that have almost no hit neighbours within <paramref name="radius"/>, using a
    /// uniform grid so this stays linear.
    /// </summary>
    private static void DropIsolated(List<float> sx, List<float> sy, bool[] visible, float radius)
    {
        int n = visible.Length;
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (!visible[i]) continue;
            var key = ((int)MathF.Floor(sx[i] / radius), (int)MathF.Floor(sy[i] / radius));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(i);
        }

        float r2 = radius * radius;
        var drop = new List<int>();
        foreach (var (key, list) in grid)
        {
            foreach (int i in list)
            {
                int near = 0;
                for (int gx = key.Item1 - 1; gx <= key.Item1 + 1 && near < MinNeighbours; gx++)
                for (int gy = key.Item2 - 1; gy <= key.Item2 + 1 && near < MinNeighbours; gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var other)) continue;
                    foreach (int j in other)
                    {
                        if (j == i) continue;
                        float dx = sx[i] - sx[j], dy = sy[i] - sy[j];
                        if (dx * dx + dy * dy <= r2 && ++near >= MinNeighbours) break;
                    }
                }
                if (near < MinNeighbours) drop.Add(i);
            }
        }
        foreach (int i in drop) visible[i] = false;
    }
}
