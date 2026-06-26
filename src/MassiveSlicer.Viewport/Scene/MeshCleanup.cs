using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Repairs common triangle-mesh problems (degenerate faces, duplicate shells,
/// welded vertices, inconsistent normals) before slicing or export.
/// </summary>
public static class MeshCleanup
{
    public sealed class Result
    {
        public required MeshData Mesh { get; init; }
        public int RemovedDegenerateTriangles { get; init; }
        public int RemovedDuplicateTriangles { get; init; }
        public int MergedVertices { get; init; }
        public int RemovedColinearVertices { get; init; }
        public int InsertedGapVertices { get; init; }
    }

    private const float ColinearEps = 1e-8f;
    private const float AreaEps     = 1e-12f;

    private sealed class MutableMesh
    {
        public List<Vector3> Positions = [];
        public List<(int A, int B, int C)> Tris = [];
    }

    public static Result Clean(MeshData source, MeshCleanupOptions options)
    {
        var mesh = ToMutable(source);
        int removedDegenerate = 0, removedDuplicate = 0, mergedVerts = 0, removedColinear = 0, insertedGaps = 0;

        if (options.MergeVertices)
        {
            int before = mesh.Positions.Count;
            WeldVertices(mesh, options.MergeEpsilon);
            mergedVerts = Math.Max(0, before - mesh.Positions.Count);
        }

        if (options.RemoveOnePointPolygons || options.RemoveTwoPointPolygons
            || options.FixDuplicatePointsInPolygons || options.FixFaceNormalVectors)
        {
            int before = mesh.Tris.Count;
            RemoveDegenerateTriangles(mesh, options);
            removedDegenerate += before - mesh.Tris.Count;
        }

        if (options.RemoveColinearVertices)
        {
            int n = RemoveColinearVerticesOnEdges(mesh, options.MergeEpsilon);
            removedColinear += n;
            if (n > 0)
            {
                int before = mesh.Tris.Count;
                RemoveDegenerateTriangles(mesh, options);
                removedDegenerate += before - mesh.Tris.Count;
            }
        }

        if (options.FixGaps)
        {
            insertedGaps = InsertGapVertices(mesh, options.MergeEpsilon);
            if (insertedGaps > 0)
            {
                int before = mesh.Tris.Count;
                RemoveDegenerateTriangles(mesh, options);
                removedDegenerate += before - mesh.Tris.Count;
            }
        }

        if (options.UnifyPolygons || options.ForceUnify)
        {
            int before = mesh.Tris.Count;
            RemoveDuplicateTriangles(mesh, options.ForceUnify, options.MergeEpsilon);
            removedDuplicate += before - mesh.Tris.Count;
        }

        if (options.FixFaceNormalVectors)
        {
            int before = mesh.Tris.Count;
            RemoveZeroAreaTriangles(mesh);
            removedDegenerate += before - mesh.Tris.Count;
        }

        if (options.RemoveFloatingVertices)
            CompactVertices(mesh);

        var normals = options.FixFaceNormalVectors
            ? ComputeVertexNormals(mesh)
            : ReuseOrDefaultNormals(source, mesh);

        return new Result
        {
            Mesh = ToMeshData(source, mesh, normals),
            RemovedDegenerateTriangles = removedDegenerate,
            RemovedDuplicateTriangles  = removedDuplicate,
            MergedVertices             = mergedVerts,
            RemovedColinearVertices    = removedColinear,
            InsertedGapVertices        = insertedGaps,
        };
    }

    private static MutableMesh ToMutable(MeshData mesh)
    {
        var m = new MutableMesh
        {
            Positions = [.. mesh.Positions],
        };

        int triCount = mesh.Indices is { } idx ? idx.Length / 3 : mesh.Positions.Length / 3;
        if (mesh.Indices is { } indices)
        {
            for (int t = 0; t < triCount; t++)
                m.Tris.Add(((int)indices[t * 3], (int)indices[t * 3 + 1], (int)indices[t * 3 + 2]));
        }
        else
        {
            for (int t = 0; t < triCount; t++)
                m.Tris.Add((t * 3, t * 3 + 1, t * 3 + 2));
        }

        return m;
    }

    private static MeshData ToMeshData(MeshData source, MutableMesh mesh, Vector3[] normals)
    {
        var positions = mesh.Positions.ToArray();
        var indices   = new uint[mesh.Tris.Count * 3];
        for (int t = 0; t < mesh.Tris.Count; t++)
        {
            var (a, b, c) = mesh.Tris[t];
            indices[t * 3]     = (uint)a;
            indices[t * 3 + 1] = (uint)b;
            indices[t * 3 + 2] = (uint)c;
        }

        return new MeshData(positions, normals, indices, source.Name,
            source.BaseColor, source.Metallic, source.Roughness);
    }

    private static Vector3[] ReuseOrDefaultNormals(MeshData source, MutableMesh mesh)
    {
        if (source.Normals.Length == mesh.Positions.Count)
            return [.. source.Normals];

        return ComputeVertexNormals(mesh);
    }

    private static void WeldVertices(MutableMesh mesh, float epsilon)
    {
        float scale = 1f / Math.Max(epsilon, 1e-12f);
        var map = new Dictionary<long, int>();
        var remap = new int[mesh.Positions.Count];

        for (int i = 0; i < mesh.Positions.Count; i++)
        {
            long key = Quantize(mesh.Positions[i], scale);
            if (!map.TryGetValue(key, out int canonical))
                map[key] = canonical = i;
            remap[i] = canonical;
        }

        for (int t = 0; t < mesh.Tris.Count; t++)
        {
            var (a, b, c) = mesh.Tris[t];
            mesh.Tris[t] = (remap[a], remap[b], remap[c]);
        }

        var used = new bool[mesh.Positions.Count];
        foreach (var (a, b, c) in mesh.Tris)
        {
            used[a] = used[b] = used[c] = true;
        }

        var compact = new int[mesh.Positions.Count];
        var newPos  = new List<Vector3>();
        for (int i = 0; i < mesh.Positions.Count; i++)
        {
            if (!used[i]) { compact[i] = -1; continue; }
            compact[i] = newPos.Count;
            newPos.Add(mesh.Positions[i]);
        }

        mesh.Positions = newPos;
        for (int t = 0; t < mesh.Tris.Count; t++)
        {
            var (a, b, c) = mesh.Tris[t];
            mesh.Tris[t] = (compact[a], compact[b], compact[c]);
        }
    }

    private static void RemoveDegenerateTriangles(MutableMesh mesh, MeshCleanupOptions options)
    {
        mesh.Tris.RemoveAll(tri =>
        {
            var (a, b, c) = tri;
            int unique = CountUnique(a, b, c);

            if (options.RemoveOnePointPolygons && unique <= 1) return true;
            if (options.RemoveTwoPointPolygons && unique == 2) return true;

            if (options.FixDuplicatePointsInPolygons && HasDuplicatePositions(mesh, a, b, c, options.MergeEpsilon))
                return true;

            if (options.FixFaceNormalVectors && TriangleArea(mesh, a, b, c) <= AreaEps)
                return true;

            return false;
        });
    }

    private static void RemoveZeroAreaTriangles(MutableMesh mesh)
    {
        mesh.Tris.RemoveAll(tri =>
        {
            var (a, b, c) = tri;
            return TriangleArea(mesh, a, b, c) <= AreaEps;
        });
    }

    private static int RemoveColinearVerticesOnEdges(MutableMesh mesh, float epsilon)
    {
        int removed = 0;
        bool changed = true;

        while (changed)
        {
            changed = false;
            var edgeTris = BuildEdgeTriangleMap(mesh);

            for (int v = 0; v < mesh.Positions.Count; v++)
            {
                var neighbors = CollectNeighbors(mesh, v);
                if (neighbors.Count != 2) continue;

                var neighborList = neighbors.ToList();
                int n0 = neighborList[0], n1 = neighborList[1];
                if (!IsColinear(mesh.Positions[n0], mesh.Positions[v], mesh.Positions[n1], epsilon))
                    continue;

                // Only collapse valence-2 vertices on open or flat strips.
                if (!CanCollapseValence2(mesh, v, edgeTris)) continue;

                for (int t = 0; t < mesh.Tris.Count; t++)
                {
                    var (a, b, c) = mesh.Tris[t];
                    mesh.Tris[t] = (
                        a == v ? n0 : a,
                        b == v ? n0 : b,
                        c == v ? n0 : c);
                }

                removed++;
                changed = true;
                break;
            }
        }

        return removed;
    }

    private static bool CanCollapseValence2(MutableMesh mesh, int v, Dictionary<(int, int), List<int>> edgeTris)
    {
        int triCount = 0;
        foreach (var tri in mesh.Tris)
        {
            if (tri.A == v || tri.B == v || tri.C == v) triCount++;
        }
        return triCount <= 2;
    }

    private static HashSet<int> CollectNeighbors(MutableMesh mesh, int v)
    {
        var set = new HashSet<int>();
        foreach (var (a, b, c) in mesh.Tris)
        {
            if (a == v) { set.Add(b); set.Add(c); }
            if (b == v) { set.Add(a); set.Add(c); }
            if (c == v) { set.Add(a); set.Add(b); }
        }
        set.Remove(v);
        return set;
    }

    private static int InsertGapVertices(MutableMesh mesh, float epsilon)
    {
        int inserted = 0;
        var onEdges = FindVerticesOnEdges(mesh, epsilon);

        foreach (var (va, vb, vc) in onEdges)
        {
            bool split = false;
            for (int t = 0; t < mesh.Tris.Count; t++)
            {
                var (a, b, c) = mesh.Tris[t];
                if (SharesEdge(a, b, c, va, vb) && !UsesVertex(a, b, c, vc))
                {
                    var (opposite, e0, e1) = OppositeOnEdge(a, b, c, va, vb);
                    mesh.Tris[t] = (e0, vc, opposite);
                    mesh.Tris.Add((vc, e1, opposite));
                    split = true;
                }
            }
            if (split) inserted++;
        }

        return inserted;
    }

    private static List<(int Va, int Vb, int Vc)> FindVerticesOnEdges(MutableMesh mesh, float epsilon)
    {
        var results = new List<(int, int, int)>();
        var edges = new HashSet<(int, int)>();

        foreach (var (a, b, c) in mesh.Tris)
        {
            AddEdge(edges, a, b);
            AddEdge(edges, b, c);
            AddEdge(edges, c, a);
        }

        foreach (var (v0, v1) in edges)
        {
            var p0 = mesh.Positions[v0];
            var p1 = mesh.Positions[v1];
            for (int vc = 0; vc < mesh.Positions.Count; vc++)
            {
                if (vc == v0 || vc == v1) continue;
                if (!IsBetweenOnSegment(mesh.Positions[vc], p0, p1, epsilon)) continue;
                results.Add((v0, v1, vc));
            }
        }

        return results;
    }

    private static void RemoveDuplicateTriangles(MutableMesh mesh, bool force, float epsilon)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        mesh.Tris.RemoveAll(tri =>
        {
            string key = force
                ? PositionKey(mesh, tri, epsilon)
                : IndexKey(tri);
            return !seen.Add(key);
        });
    }

    private static void CompactVertices(MutableMesh mesh)
    {
        var used = new bool[mesh.Positions.Count];
        foreach (var (a, b, c) in mesh.Tris)
            used[a] = used[b] = used[c] = true;

        var remap = new int[mesh.Positions.Count];
        var compact = new List<Vector3>();
        for (int i = 0; i < mesh.Positions.Count; i++)
        {
            if (!used[i]) { remap[i] = -1; continue; }
            remap[i] = compact.Count;
            compact.Add(mesh.Positions[i]);
        }

        mesh.Positions = compact;
        for (int t = 0; t < mesh.Tris.Count; t++)
        {
            var (a, b, c) = mesh.Tris[t];
            mesh.Tris[t] = (remap[a], remap[b], remap[c]);
        }
    }

    private static Vector3[] ComputeVertexNormals(MutableMesh mesh)
    {
        var accum = new Vector3[mesh.Positions.Count];
        foreach (var (a, b, c) in mesh.Tris)
        {
            var n = FaceNormal(mesh.Positions[a], mesh.Positions[b], mesh.Positions[c]);
            if (n.LengthSquared < AreaEps) continue;
            accum[a] += n;
            accum[b] += n;
            accum[c] += n;
        }

        var normals = new Vector3[mesh.Positions.Count];
        for (int i = 0; i < normals.Length; i++)
            normals[i] = accum[i].LengthSquared > AreaEps ? Vector3.Normalize(accum[i]) : Vector3.UnitZ;
        return normals;
    }

    private static Dictionary<(int, int), List<int>> BuildEdgeTriangleMap(MutableMesh mesh)
    {
        var map = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t < mesh.Tris.Count; t++)
        {
            var (a, b, c) = mesh.Tris[t];
            AddEdgeTri(map, a, b, t);
            AddEdgeTri(map, b, c, t);
            AddEdgeTri(map, c, a, t);
        }
        return map;
    }

    private static void AddEdgeTri(Dictionary<(int, int), List<int>> map, int v0, int v1, int tri)
    {
        var key = v0 < v1 ? (v0, v1) : (v1, v0);
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(tri);
    }

    private static int CountUnique(int a, int b, int c)
    {
        if (a == b && b == c) return 1;
        if (a == b || b == c || a == c) return 2;
        return 3;
    }

    private static bool HasDuplicatePositions(MutableMesh mesh, int a, int b, int c, float epsilon)
    {
        var pa = mesh.Positions[a];
        var pb = mesh.Positions[b];
        var pc = mesh.Positions[c];
        float e2 = epsilon * epsilon;
        return (pa - pb).LengthSquared <= e2
            || (pb - pc).LengthSquared <= e2
            || (pc - pa).LengthSquared <= e2;
    }

    private static float TriangleArea(MutableMesh mesh, int a, int b, int c)
    {
        var ab = mesh.Positions[b] - mesh.Positions[a];
        var ac = mesh.Positions[c] - mesh.Positions[a];
        return Vector3.Cross(ab, ac).Length * 0.5f;
    }

    private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        return Vector3.Cross(ab, ac);
    }

    private static bool IsColinear(Vector3 a, Vector3 b, Vector3 c, float epsilon)
    {
        var ab = b - a;
        var ac = c - a;
        float denom = Math.Max(ab.LengthSquared * ac.LengthSquared, ColinearEps);
        return Vector3.Cross(ab, ac).LengthSquared <= (epsilon * epsilon) * denom;
    }

    private static bool IsBetweenOnSegment(Vector3 p, Vector3 a, Vector3 b, float epsilon)
    {
        if (!IsColinear(a, p, b, epsilon)) return false;
        var ab = b - a;
        float len2 = ab.LengthSquared;
        if (len2 <= ColinearEps) return false;
        float t = Vector3.Dot(p - a, ab) / len2;
        return t > epsilon && t < 1f - epsilon;
    }

    private static void AddEdge(HashSet<(int, int)> edges, int v0, int v1)
    {
        edges.Add(v0 < v1 ? (v0, v1) : (v1, v0));
    }

    private static bool SharesEdge(int a, int b, int c, int e0, int e1)
    {
        return UsesVertex(a, b, c, e0) && UsesVertex(a, b, c, e1);
    }

    private static bool UsesVertex(int a, int b, int c, int v)
        => a == v || b == v || c == v;

    private static (int Opposite, int E0, int E1) OppositeOnEdge(int a, int b, int c, int e0, int e1)
    {
        if (a != e0 && a != e1) return (a, e0, e1);
        if (b != e0 && b != e1) return (b, e0, e1);
        return (c, e0, e1);
    }

    private static string IndexKey((int A, int B, int C) tri)
    {
        int[] idx = [tri.A, tri.B, tri.C];
        Array.Sort(idx);
        return $"{idx[0]}:{idx[1]}:{idx[2]}";
    }

    private static string PositionKey(MutableMesh mesh, (int A, int B, int C) tri, float epsilon)
    {
        float scale = 1f / Math.Max(epsilon, 1e-12f);
        long[] keys =
        [
            Quantize(mesh.Positions[tri.A], scale),
            Quantize(mesh.Positions[tri.B], scale),
            Quantize(mesh.Positions[tri.C], scale),
        ];
        Array.Sort(keys);
        return $"{keys[0]}:{keys[1]}:{keys[2]}";
    }

    private static long Quantize(Vector3 v, float scale)
    {
        long x = (long)Math.Round(v.X * scale);
        long y = (long)Math.Round(v.Y * scale);
        long z = (long)Math.Round(v.Z * scale);
        return (x << 42) | (y << 21) | (z & 0x1FFFFF);
    }
}