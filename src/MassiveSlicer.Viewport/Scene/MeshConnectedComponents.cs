using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Splits triangle meshes into connected components. Two triangles belong to the
/// same component when they share an edge (matched by quantised vertex positions).
/// Does not split individual triangles or shared-vertex islands within one shell.
/// </summary>
public static class MeshConnectedComponents
{
    private const float QuantizeScale = 10000f;

    /// <summary>Returns <c>true</c> when the mesh contains more than one disconnected shell.</summary>
    public static bool HasMultipleComponents(MeshData mesh) => CountComponents(mesh) > 1;

    /// <summary>Returns the number of connected triangle components in <paramref name="mesh"/>.</summary>
    public static int CountComponents(MeshData mesh)
    {
        int triCount = TriangleCount(mesh);
        if (triCount <= 1) return triCount;

        var triVerts = BuildTriangleVertices(mesh, triCount, out _);
        var parent   = new int[triCount];
        for (int i = 0; i < triCount; i++) parent[i] = i;

        var edgeOwner = new Dictionary<(int, int), int>();
        for (int t = 0; t < triCount; t++)
        {
            int a = triVerts[t * 3], b = triVerts[t * 3 + 1], c = triVerts[t * 3 + 2];
            UniteEdge(a, b, t, edgeOwner, parent);
            UniteEdge(b, c, t, edgeOwner, parent);
            UniteEdge(c, a, t, edgeOwner, parent);
        }

        int roots = 0;
        var seen = new HashSet<int>();
        for (int t = 0; t < triCount; t++)
        {
            int root = Find(parent, t);
            if (seen.Add(root)) roots++;
        }
        return roots;
    }

    /// <summary>
    /// Splits <paramref name="mesh"/> into one <see cref="MeshData"/> per connected shell.
    /// Returns a single-element list when the mesh is already one component.
    /// </summary>
    public static IReadOnlyList<MeshData> Split(MeshData mesh)
    {
        int triCount = TriangleCount(mesh);
        if (triCount == 0) return [];

        var triVerts = BuildTriangleVertices(mesh, triCount, out _);
        var parent   = new int[triCount];
        for (int i = 0; i < triCount; i++) parent[i] = i;

        var edgeOwner = new Dictionary<(int, int), int>();
        for (int t = 0; t < triCount; t++)
        {
            int a = triVerts[t * 3], b = triVerts[t * 3 + 1], c = triVerts[t * 3 + 2];
            UniteEdge(a, b, t, edgeOwner, parent);
            UniteEdge(b, c, t, edgeOwner, parent);
            UniteEdge(c, a, t, edgeOwner, parent);
        }

        var groups = new Dictionary<int, List<int>>();
        foreach (int t in Enumerable.Range(0, triCount))
        {
            int root = Find(parent, t);
            if (!groups.TryGetValue(root, out var list))
                groups[root] = list = [];
            list.Add(t);
        }

        if (groups.Count <= 1) return [mesh];

        var results = new List<MeshData>(groups.Count);
        int part = 1;
        foreach (var tris in groups.Values)
        {
            var positions = new Vector3[tris.Count * 3];
            var normals   = new Vector3[tris.Count * 3];
            int outIdx = 0;

            if (mesh.Indices is { } indices)
            {
                foreach (int t in tris)
                {
                    for (int v = 0; v < 3; v++)
                    {
                        int vi = (int)indices[t * 3 + v];
                        positions[outIdx] = mesh.Positions[vi];
                        normals[outIdx]   = mesh.Normals[vi];
                        outIdx++;
                    }
                }
            }
            else
            {
                foreach (int t in tris)
                {
                    for (int v = 0; v < 3; v++)
                    {
                        int vi = t * 3 + v;
                        positions[outIdx] = mesh.Positions[vi];
                        normals[outIdx]   = mesh.Normals[vi];
                        outIdx++;
                    }
                }
            }

            var name = $"{mesh.Name} {part}";
            results.Add(new MeshData(positions, normals, null, name,
                mesh.BaseColor, mesh.Metallic, mesh.Roughness));
            part++;
        }

        return results;
    }

    private static int TriangleCount(MeshData mesh)
        => mesh.Indices is { } idx ? idx.Length / 3 : mesh.Positions.Length / 3;

    private static int[] BuildTriangleVertices(MeshData mesh, int triCount, out int canonicalCount)
    {
        var vertMap  = new Dictionary<long, int>();
        var triVerts = new int[triCount * 3];
        canonicalCount = 0;

        if (mesh.Indices is { } indices)
        {
            for (int t = 0; t < triCount; t++)
            {
                for (int v = 0; v < 3; v++)
                {
                    int vi = (int)indices[t * 3 + v];
                    triVerts[t * 3 + v] = CanonicalVertex(mesh.Positions[vi], vertMap, ref canonicalCount);
                }
            }
        }
        else
        {
            for (int t = 0; t < triCount; t++)
            {
                for (int v = 0; v < 3; v++)
                {
                    int vi = t * 3 + v;
                    triVerts[t * 3 + v] = CanonicalVertex(mesh.Positions[vi], vertMap, ref canonicalCount);
                }
            }
        }

        return triVerts;
    }

    private static int CanonicalVertex(Vector3 p, Dictionary<long, int> vertMap, ref int nextId)
    {
        long key = Quantize(p);
        if (!vertMap.TryGetValue(key, out int id))
            vertMap[key] = id = nextId++;
        return id;
    }

    private static long Quantize(Vector3 v)
    {
        long x = (long)Math.Round(v.X * QuantizeScale);
        long y = (long)Math.Round(v.Y * QuantizeScale);
        long z = (long)Math.Round(v.Z * QuantizeScale);
        return (x << 42) | (y << 21) | (z & 0x1FFFFF);
    }

    private static void UniteEdge(int v0, int v1, int tri,
        Dictionary<(int, int), int> edgeOwner, int[] parent)
    {
        var key = v0 < v1 ? (v0, v1) : (v1, v0);
        if (edgeOwner.TryGetValue(key, out int other))
            Union(parent, tri, other);
        else
            edgeOwner[key] = tri;
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }
        return x;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a);
        int rb = Find(parent, b);
        if (ra != rb) parent[rb] = ra;
    }
}