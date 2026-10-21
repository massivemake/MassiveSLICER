using System.Numerics;

namespace MassiveSlicer.Core.Slicing;

/// <summary>One signed-distance query. Distance is negative inside a watertight solid.
/// <see cref="Outward"/> is a unit vector pointing out of the solid.</summary>
public readonly record struct SdfHit(float Distance, Vector3 Outward);

/// <summary>
/// Euclidean signed distance to a triangle mesh. The gradient is the angle-weighted
/// pseudonormal of the closest feature (Bærentzen and Aanæs), which stays stable on
/// edges and corners. A ray through the solid flips an inside-out mesh so the
/// interior reads negative.
/// </summary>
public sealed class MeshSignedDistance
{
    const int LeafSize = 4;

    readonly Vector3[] _verts;
    readonly int[][] _tris;
    readonly Vector3[] _faceN;
    readonly Vector3[] _vertN;
    readonly Dictionary<(int, int), Vector3> _edgeN;
    readonly Node[] _nodes;
    readonly int[] _order;
    readonly int _root;
    readonly bool _flip;

    public (Vector3 Min, Vector3 Max) Bounds { get; }
    public bool HasTriangles => _tris.Length > 0;

    public MeshSignedDistance(IReadOnlyList<Vector3[]> meshes)
    {
        var welded = MeshGraph.Build(meshes);
        _verts = welded.Vertices;
        _tris = welded.Triangles;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < _verts.Length; i++)
        {
            min = Vector3.Min(min, _verts[i]);
            max = Vector3.Max(max, _verts[i]);
        }
        Bounds = _verts.Length == 0 ? (Vector3.Zero, Vector3.Zero) : (min, max);

        _faceN = new Vector3[_tris.Length];
        _vertN = new Vector3[_verts.Length];
        var edgeSum = new Dictionary<(int, int), Vector3>();

        for (int t = 0; t < _tris.Length; t++)
        {
            int i0 = _tris[t][0], i1 = _tris[t][1], i2 = _tris[t][2];
            var a = _verts[i0]; var b = _verts[i1]; var c = _verts[i2];
            var fn = Vector3.Cross(b - a, c - a);
            float len = fn.Length();
            if (len < 1e-12f) continue;
            fn /= len;
            _faceN[t] = fn;

            _vertN[i0] += fn * CornerAngle(b - a, c - a);
            _vertN[i1] += fn * CornerAngle(a - b, c - b);
            _vertN[i2] += fn * CornerAngle(a - c, b - c);

            AddEdge(edgeSum, i0, i1, fn);
            AddEdge(edgeSum, i1, i2, fn);
            AddEdge(edgeSum, i2, i0, fn);
        }

        for (int i = 0; i < _vertN.Length; i++)
        {
            float len = _vertN[i].Length();
            if (len > 1e-8f) _vertN[i] /= len;
        }

        _edgeN = new Dictionary<(int, int), Vector3>(edgeSum.Count);
        foreach (var (key, sum) in edgeSum)
        {
            float len = sum.Length();
            if (len > 1e-8f) _edgeN[key] = sum / len;
        }

        _order = new int[_tris.Length];
        for (int i = 0; i < _order.Length; i++) _order[i] = i;
        var nodes = new List<Node>(Math.Max(1, _tris.Length * 2));
        _root = _tris.Length == 0 ? -1 : Build(nodes, 0, _tris.Length);
        _nodes = nodes.ToArray();

        _flip = false;
        _flip = NeedsFlip();
    }

    public SdfHit Sample(Vector3 p)
    {
        if (_root < 0) return new SdfHit(0f, Vector3.UnitZ);

        var best = new Best { Sq = float.PositiveInfinity, Tri = -1 };
        Walk(_root, p, ref best);
        if (best.Tri < 0) return new SdfHit(0f, Vector3.UnitZ);

        var pseudo = Pseudonormal(best);
        float plen = pseudo.Length();
        if (plen < 1e-8f) pseudo = _faceN[best.Tri];
        else pseudo /= MathF.Max(plen, 1e-8f);

        float dist = MathF.Sqrt(MathF.Max(best.Sq, 0f));
        if (Vector3.Dot(p - best.Point, pseudo) < 0f) dist = -dist;
        if (_flip)
        {
            dist = -dist;
            pseudo = -pseudo;
        }
        return new SdfHit(dist, pseudo);
    }

    bool NeedsFlip()
    {
        // Cross the solid along +X through the middle of the bounds. An even hit
        // count is a closed shell; the midpoint of the first span is inside.
        // Pseudonormal sign is independent of that parity, so a positive reading
        // there means the faces point inward.
        if (_tris.Length == 0) return false;
        var origin = new Vector3(
            Bounds.Min.X - 1f,
            (Bounds.Min.Y + Bounds.Max.Y) * 0.5f,
            (Bounds.Min.Z + Bounds.Max.Z) * 0.5f);
        var hits = new List<float>();
        var dir = Vector3.UnitX;
        for (int t = 0; t < _tris.Length; t++)
        {
            var tri = _tris[t];
            if (RayTri(origin, dir, _verts[tri[0]], _verts[tri[1]], _verts[tri[2]], out float hit))
                hits.Add(hit);
        }
        if (hits.Count < 2) return false;
        hits.Sort();
        var uniq = new List<float>(hits.Count);
        foreach (float t in hits)
            if (uniq.Count == 0 || t - uniq[^1] > 1e-3f) uniq.Add(t);
        if (uniq.Count < 2 || (uniq.Count & 1) != 0) return false;

        var inside = origin + dir * ((uniq[0] + uniq[1]) * 0.5f);
        return Sample(inside).Distance > 1e-3f;
    }

    static bool RayTri(Vector3 orig, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0f;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var pvec = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, pvec);
        if (MathF.Abs(det) < 1e-8f) return false;
        float inv = 1f / det;
        var tvec = orig - v0;
        float u = Vector3.Dot(tvec, pvec) * inv;
        if (u < 0f || u > 1f) return false;
        var qvec = Vector3.Cross(tvec, e1);
        float v = Vector3.Dot(dir, qvec) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, qvec) * inv;
        return t > 1e-4f;
    }

    Vector3 Pseudonormal(Best best)
    {
        if (best.Kind == 1)
        {
            int v = best.Ia;
            return (uint)v < (uint)_vertN.Length ? _vertN[v] : _faceN[best.Tri];
        }
        if (best.Kind == 2)
        {
            var key = best.Ia < best.Ib ? (best.Ia, best.Ib) : (best.Ib, best.Ia);
            return _edgeN.TryGetValue(key, out var n) ? n : _faceN[best.Tri];
        }
        return _faceN[best.Tri];
    }

    int Build(List<Node> nodes, int start, int count)
    {
        int index = nodes.Count;
        nodes.Add(default);

        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        for (int i = 0; i < count; i++)
        {
            var tri = _tris[_order[start + i]];
            for (int k = 0; k < 3; k++)
            {
                var v = _verts[tri[k]];
                bmin = Vector3.Min(bmin, v);
                bmax = Vector3.Max(bmax, v);
            }
        }

        if (count <= LeafSize)
        {
            nodes[index] = new Node { Min = bmin, Max = bmax, Left = -1, Right = -1, Start = start, Count = count };
            return index;
        }

        var ext = bmax - bmin;
        int axis = ext.Y > ext.X && ext.Y >= ext.Z ? 1 : ext.Z > ext.X ? 2 : 0;
        _order.AsSpan(start, count).Sort((a, b) => Centroid(a, axis).CompareTo(Centroid(b, axis)));
        int mid = count / 2;
        int left = Build(nodes, start, mid);
        int right = Build(nodes, start + mid, count - mid);
        nodes[index] = new Node { Min = bmin, Max = bmax, Left = left, Right = right, Start = 0, Count = 0 };
        return index;
    }

    float Centroid(int tri, int axis)
    {
        var t = _tris[tri];
        float s = Component(_verts[t[0]], axis) + Component(_verts[t[1]], axis) + Component(_verts[t[2]], axis);
        return s / 3f;
    }

    static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    void Walk(int node, Vector3 p, ref Best best)
    {
        var n = _nodes[node];
        if (BoxDistSq(p, n.Min, n.Max) > best.Sq) return;

        if (n.Left < 0)
        {
            for (int i = 0; i < n.Count; i++)
                Consider(_order[n.Start + i], p, ref best);
            return;
        }

        float dl = BoxDistSq(p, _nodes[n.Left].Min, _nodes[n.Left].Max);
        float dr = BoxDistSq(p, _nodes[n.Right].Min, _nodes[n.Right].Max);
        if (dl < dr)
        {
            Walk(n.Left, p, ref best);
            Walk(n.Right, p, ref best);
        }
        else
        {
            Walk(n.Right, p, ref best);
            Walk(n.Left, p, ref best);
        }
    }

    void Consider(int tri, Vector3 p, ref Best best)
    {
        if (_faceN[tri].LengthSquared() < 1e-16f) return;
        var t = _tris[tri];
        var hit = Closest(_verts[t[0]], _verts[t[1]], _verts[t[2]], t[0], t[1], t[2], p);
        float d2 = (p - hit.Point).LengthSquared();
        if (d2 >= best.Sq) return;
        best.Sq = d2;
        best.Tri = tri;
        best.Point = hit.Point;
        best.Kind = hit.Kind;
        best.Ia = hit.Ia;
        best.Ib = hit.Ib;
    }

    /// <summary>Closest point. Kind 0 = face, 1 = vertex (Ia), 2 = edge (Ia, Ib).</summary>
    static (Vector3 Point, int Kind, int Ia, int Ib) Closest(
        Vector3 a, Vector3 b, Vector3 c, int ia, int ib, int ic, Vector3 p)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return (a, 1, ia, ia);

        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return (b, 1, ib, ib);

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float v = d1 / (d1 - d3);
            return (a + v * ab, 2, ia, ib);
        }

        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return (c, 1, ic, ic);

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float w = d2 / (d2 - d6);
            return (a + w * ac, 2, ia, ic);
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return (b + w * (c - b), 2, ib, ic);
        }

        float denom = 1f / (va + vb + vc);
        float vFace = vb * denom;
        float wFace = vc * denom;
        return (a + ab * vFace + ac * wFace, 0, 0, 0);
    }

    static float BoxDistSq(Vector3 p, Vector3 min, Vector3 max)
    {
        float d = 0f;
        if (p.X < min.X) d += (min.X - p.X) * (min.X - p.X);
        else if (p.X > max.X) d += (p.X - max.X) * (p.X - max.X);
        if (p.Y < min.Y) d += (min.Y - p.Y) * (min.Y - p.Y);
        else if (p.Y > max.Y) d += (p.Y - max.Y) * (p.Y - max.Y);
        if (p.Z < min.Z) d += (min.Z - p.Z) * (min.Z - p.Z);
        else if (p.Z > max.Z) d += (p.Z - max.Z) * (p.Z - max.Z);
        return d;
    }

    static float CornerAngle(Vector3 u, Vector3 v)
    {
        float lu = u.Length();
        float lv = v.Length();
        if (lu < 1e-12f || lv < 1e-12f) return 0f;
        return MathF.Acos(Math.Clamp(Vector3.Dot(u, v) / (lu * lv), -1f, 1f));
    }

    static void AddEdge(Dictionary<(int, int), Vector3> edges, int a, int b, Vector3 faceN)
    {
        var key = a < b ? (a, b) : (b, a);
        edges.TryGetValue(key, out var sum);
        edges[key] = sum + faceN;
    }

    struct Node
    {
        public Vector3 Min, Max;
        public int Left, Right, Start, Count;
    }

    struct Best
    {
        public float Sq;
        public int Tri;
        public Vector3 Point;
        public int Kind, Ia, Ib;
    }
}
