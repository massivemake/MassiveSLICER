using System.Numerics;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Mesh ∩ plane Z = z. Used by AdaOne cutout / contouring / morph (polyline + axial stepdown).
/// </summary>
public static class MeshWaterline
{
    public readonly record struct Segment(Vector3 A, Vector3 B);

    /// <summary>Closed loops on the plane, largest first (XY area).</summary>
    public static List<List<Vector3>> SliceClosedLoops(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<int> indices,
        float z,
        float snapMm = 0.05f)
    {
        var segs = Intersect(positions, indices, z);
        var loops = Stitch(segs, snapMm);
        loops.Sort((a, b) => PolygonArea(b).CompareTo(PolygonArea(a)));
        return loops;
    }

    /// <summary>Largest closed loop, or an XY AABB rectangle of the mesh when the slice is empty.</summary>
    public static List<Vector3> OuterLoopOrBounds(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<int> indices,
        float z)
    {
        var loops = SliceClosedLoops(positions, indices, z);
        if (loops.Count > 0 && loops[0].Count >= 3)
            return loops[0];
        return BoundsRectangle(positions, z);
    }

    public static List<Vector3> BoundsRectangle(IReadOnlyList<Vector3> positions, float z)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in positions)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        if (minX > maxX) return [];
        return
        [
            new(minX, minY, z),
            new(maxX, minY, z),
            new(maxX, maxY, z),
            new(minX, maxY, z),
        ];
    }

    public static float PolygonArea(IReadOnlyList<Vector3> loop)
    {
        double a = 0;
        int n = loop.Count;
        for (int i = 0; i < n; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % n];
            a += (double)p.X * q.Y - (double)q.X * p.Y;
        }
        return (float)Math.Abs(a) * 0.5f;
    }

    public static List<Segment> Intersect(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<int> indices,
        float z)
    {
        var segs = new List<Segment>();
        int n = indices.Count / 3 * 3;
        for (int i = 0; i < n; i += 3)
        {
            int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
            if ((uint)ia >= (uint)positions.Count || (uint)ib >= (uint)positions.Count
                || (uint)ic >= (uint)positions.Count)
                continue;
            var a = positions[ia];
            var b = positions[ib];
            var c = positions[ic];
            int h = 0;
            Vector3 h0 = default, h1 = default;
            if (TryEdge(a, b, z, out var ab)) { if (h == 0) h0 = ab; else if (h == 1) h1 = ab; h++; }
            if (TryEdge(b, c, z, out var bc)) { if (h == 0) h0 = bc; else if (h == 1) h1 = bc; h++; }
            if (TryEdge(c, a, z, out var ca)) { if (h == 0) h0 = ca; else if (h == 1) h1 = ca; h++; }
            if (h >= 2)
                segs.Add(new Segment(h0, h1));
        }
        return segs;
    }

    static bool TryEdge(Vector3 a, Vector3 b, float z, out Vector3 p)
    {
        float da = a.Z - z;
        float db = b.Z - z;
        if (da == 0 && db == 0) { p = a; return false; } // coplanar edge — skip (faces above/below produce the loop)
        if (da * db > 0) { p = default; return false; }
        if (da == 0) { p = a; return true; }
        if (db == 0) { p = b; return true; }
        float t = da / (da - db);
        p = a + (b - a) * t;
        return true;
    }

    static List<List<Vector3>> Stitch(List<Segment> segs, float snapMm)
    {
        float inv = 1f / MathF.Max(1e-4f, snapMm);
        (int, int, int) Key(Vector3 p) =>
            ((int)MathF.Round(p.X * inv), (int)MathF.Round(p.Y * inv), (int)MathF.Round(p.Z * inv));

        var adj = new Dictionary<(int, int, int), List<Vector3>>();
        void Add((int, int, int) k, Vector3 to)
        {
            if (!adj.TryGetValue(k, out var list))
                adj[k] = list = [];
            list.Add(to);
        }

        foreach (var s in segs)
        {
            if (Vector3.DistanceSquared(s.A, s.B) < 1e-8f) continue;
            Add(Key(s.A), s.B);
            Add(Key(s.B), s.A);
        }

        var used = new HashSet<(int, int, int)>();
        var loops = new List<List<Vector3>>();
        foreach (var start in adj.Keys)
        {
            if (!used.Add(start)) continue;
            var loop = new List<Vector3>();
            var cur = start;
            Vector3? prev = null;
            for (int guard = 0; guard < adj.Count + 2; guard++)
            {
                if (!adj.TryGetValue(cur, out var nbrs) || nbrs.Count == 0) break;
                Vector3 next = default;
                bool found = false;
                foreach (var n in nbrs)
                {
                    if (prev is { } p && Vector3.DistanceSquared(n, p) < 1e-8f) continue;
                    next = n;
                    found = true;
                    break;
                }
                if (!found) next = nbrs[0];
                loop.Add(next);
                var nk = Key(next);
                used.Add(nk);
                prev = new Vector3(cur.Item1 / inv, cur.Item2 / inv, cur.Item3 / inv);
                cur = nk;
                if (cur.Equals(start) && loop.Count >= 3) break;
            }
            if (loop.Count >= 3)
                loops.Add(loop);
        }
        return loops;
    }

    /// <summary>Resample a closed loop to <paramref name="count"/> points (for morph).</summary>
    public static List<Vector3> ResampleClosed(IReadOnlyList<Vector3> loop, int count)
    {
        if (loop.Count < 2 || count < 2) return [.. loop];
        float total = 0;
        var lens = new float[loop.Count];
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            float d = Vector3.Distance(a, b);
            lens[i] = d;
            total += d;
        }
        if (total < 1e-6f) return [.. loop];
        var outPts = new List<Vector3>(count);
        for (int k = 0; k < count; k++)
        {
            float t = total * k / count;
            float acc = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                float d = lens[i];
                if (acc + d >= t || i == loop.Count - 1)
                {
                    float u = d < 1e-8f ? 0 : (t - acc) / d;
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Count];
                    outPts.Add(Vector3.Lerp(a, b, Math.Clamp(u, 0, 1)));
                    break;
                }
                acc += d;
            }
        }
        return outPts;
    }
}
