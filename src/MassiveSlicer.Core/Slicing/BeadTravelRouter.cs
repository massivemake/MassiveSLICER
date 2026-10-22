using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// A travel whose straight line leaves the printed beads is redrawn along those
/// beads. G0 jumps across an opening stay travels, and they follow the part
/// instead of cutting the opening.
/// </summary>
public static class BeadTravelRouter
{
    public static void Reroute(Toolpath toolpath, float beadWidth)
    {
        float near = MathF.Max(beadWidth * 1.6f, 0.4f);
        float snap = MathF.Max(beadWidth * 4f, 1f);
        foreach (var layer in toolpath.Layers)
        {
            var segs = Segments(layer);
            if (segs.Count == 0) continue;
            var routed = new List<ToolpathMove>(layer.Moves.Count);
            foreach (var move in layer.Moves)
            {
                if (move.Kind != MoveKind.Travel || move.IsZHop || Vertical(move))
                {
                    routed.Add(move);
                    continue;
                }
                var a = new Vector2(move.From.X, move.From.Y);
                var b = new Vector2(move.To.X, move.To.Y);
                if (!LeavesBeads(a, b, segs, near) || !TryRoute(a, b, segs, snap, out var path))
                {
                    routed.Add(move);
                    continue;
                }
                var cur = move.From;
                bool first = true;
                for (int i = 1; i < path.Count; i++)
                {
                    var nxt = new Vector3(path[i].X, path[i].Y, move.From.Z);
                    if (Vector3.DistanceSquared(cur, nxt) < 1e-4f) continue;
                    routed.Add(new ToolpathMove(cur, nxt, MoveKind.Travel)
                    {
                        IsLayerChange = first && move.IsLayerChange,
                    });
                    first = false;
                    cur = nxt;
                }
                if (Vector3.DistanceSquared(cur, move.To) > 1e-4f)
                    routed.Add(new ToolpathMove(cur, move.To, MoveKind.Travel));
            }
            layer.Moves.Clear();
            layer.Moves.AddRange(routed);
        }
    }

    public static bool LeavesBeads(Vector2 a, Vector2 b, IReadOnlyList<(Vector2 A, Vector2 B)> segs, float near)
    {
        for (int s = 1; s <= 4; s++)
        {
            var p = a + (b - a) * (s / 5f);
            float best = float.MaxValue;
            foreach (var (c, d) in segs)
            {
                float dist = Dist(p, c, d);
                if (dist < best) best = dist;
                if (best <= near) break;
            }
            if (best > near) return true;
        }
        return false;
    }

    public static bool TryRoute(Vector2 a, Vector2 b, IReadOnlyList<(Vector2 A, Vector2 B)> segs, float snap,
                                 out List<Vector2> path)
    {
        path = [];
        var nodes = new List<Vector2>();
        var adj = new List<List<(int To, float W)>>();
        var weld = new Dictionary<long, int>();

        int Node(Vector2 p)
        {
            int ix = (int)MathF.Round(p.X * 20f);
            int iy = (int)MathF.Round(p.Y * 20f);
            long key = ((long)ix << 32) | (uint)iy;
            if (weld.TryGetValue(key, out int id)) return id;
            id = nodes.Count;
            nodes.Add(p);
            adj.Add([]);
            weld[key] = id;
            return id;
        }

        foreach (var (c, d) in segs)
        {
            if (Vector2.DistanceSquared(c, d) < 1e-8f) continue;
            int ia = Node(c);
            int ib = Node(d);
            if (ia == ib) continue;
            float w = Vector2.Distance(nodes[ia], nodes[ib]);
            adj[ia].Add((ib, w));
            adj[ib].Add((ia, w));
        }
        if (nodes.Count == 0) return false;

        int Nearest(Vector2 p)
        {
            int best = -1;
            float bestD = snap * snap;
            for (int i = 0; i < nodes.Count; i++)
            {
                float d = Vector2.DistanceSquared(nodes[i], p);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        int start = Nearest(a);
        int goal = Nearest(b);
        if (start < 0 || goal < 0 || start == goal) return false;

        var dist = new float[nodes.Count];
        Array.Fill(dist, float.PositiveInfinity);
        var prev = new int[nodes.Count];
        Array.Fill(prev, -1);
        dist[start] = 0;
        var used = new bool[nodes.Count];
        for (int n = 0; n < nodes.Count; n++)
        {
            int u = -1;
            float best = float.PositiveInfinity;
            for (int i = 0; i < nodes.Count; i++)
                if (!used[i] && dist[i] < best) { best = dist[i]; u = i; }
            if (u < 0 || u == goal) break;
            used[u] = true;
            foreach (var (v, w) in adj[u])
                if (dist[u] + w < dist[v]) { dist[v] = dist[u] + w; prev[v] = u; }
        }
        if (float.IsPositiveInfinity(dist[goal])) return false;

        var rev = new List<Vector2>();
        for (int u = goal; u >= 0; u = prev[u])
        {
            rev.Add(nodes[u]);
            if (u == start) break;
        }
        rev.Reverse();
        if (rev.Count < 2) return false;
        path = rev;
        return true;
    }

    public static List<(Vector2 A, Vector2 B)> Segments(ToolpathLayer layer)
    {
        var segs = new List<(Vector2, Vector2)>();
        foreach (var move in layer.Moves)
        {
            if (move.Kind != MoveKind.Extrude || move.IsWipe) continue;
            var a = new Vector2(move.From.X, move.From.Y);
            var b = new Vector2(move.To.X, move.To.Y);
            if (Vector2.DistanceSquared(a, b) > 1e-8f)
                segs.Add((a, b));
        }
        return segs;
    }

    static bool Vertical(ToolpathMove move)
        => MathF.Abs(move.From.X - move.To.X) < 1e-3f && MathF.Abs(move.From.Y - move.To.Y) < 1e-3f;

    static float Dist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-8f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }
}
