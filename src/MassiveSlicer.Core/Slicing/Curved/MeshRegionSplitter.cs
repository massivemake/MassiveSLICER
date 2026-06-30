using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Core.Slicing.Curved;

/// <summary>
/// Partitions a mesh into connected triangle regions separated by saddle iso-contour cuts.
/// Simplified port of compas_slicer region_split (topology partition without face splitting).
/// </summary>
public static class MeshRegionSplitter
{
    public sealed record MeshPart(
        WeldedMesh Mesh,
        IReadOnlyList<int> LowVertices,
        IReadOnlyList<int> HighVertices);

    public static IReadOnlyList<MeshPart> Split(
        WeldedMesh mesh,
        BoundaryTarget low,
        BoundaryTarget high,
        IReadOnlyList<int> saddles)
    {
        if (saddles.Count == 0)
            return [new MeshPart(mesh, low.SeedVertices, high.SeedVertices)];

        var blockedEdges = new HashSet<(int, int)>();
        foreach (int saddle in saddles)
        {
            float t = ScalarFieldGradient.FindWeightThroughSaddle(mesh, low, high, saddle);
            var field = InterpolationField.Compute(t, low, high);
            CollectZeroCrossingEdges(mesh, field, blockedEdges);
        }

        var triComponents = PartitionTriangles(mesh, blockedEdges);
        if (triComponents.Count <= 1)
            return [new MeshPart(mesh, low.SeedVertices, high.SeedVertices)];

        var parts = new List<MeshPart>(triComponents.Count);
        var lowSet  = new HashSet<int>(low.SeedVertices);
        var highSet = new HashSet<int>(high.SeedVertices);

        foreach (var triIndices in triComponents)
        {
            var partMesh = ExtractSubmesh(mesh, triIndices, out var oldToNew);
            var partLow  = RemapSeeds(lowSet, oldToNew);
            var partHigh = RemapSeeds(highSet, oldToNew);
            if (partLow.Count == 0 || partHigh.Count == 0) continue;
            parts.Add(new MeshPart(partMesh, partLow, partHigh));
        }

        return parts.Count > 0
            ? parts
            : [new MeshPart(mesh, low.SeedVertices, high.SeedVertices)];
    }

    private static void CollectZeroCrossingEdges(WeldedMesh mesh, float[] scalar, HashSet<(int, int)> blocked)
    {
        foreach (var tri in mesh.Triangles)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = tri[k], b = tri[(k + 1) % 3];
                float da = scalar[a], db = scalar[b];
                if (da * db <= 0f && (da != 0f || db != 0f))
                {
                    var edge = a < b ? (a, b) : (b, a);
                    blocked.Add(edge);
                }
            }
        }
    }

    private static List<List<int>> PartitionTriangles(WeldedMesh mesh, HashSet<(int, int)> blockedEdges)
    {
        var triAdj = BuildTriangleAdjacency(mesh, blockedEdges);
        var visited = new bool[mesh.Triangles.Length];
        var components = new List<List<int>>();

        for (int start = 0; start < mesh.Triangles.Length; start++)
        {
            if (visited[start]) continue;
            var component = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start);
            visited[start] = true;

            while (queue.Count > 0)
            {
                int ti = queue.Dequeue();
                component.Add(ti);
                foreach (int nb in triAdj[ti])
                {
                    if (!visited[nb]) { visited[nb] = true; queue.Enqueue(nb); }
                }
            }
            components.Add(component);
        }
        return components;
    }

    private static List<int>[] BuildTriangleAdjacency(WeldedMesh mesh, HashSet<(int, int)> blocked)
    {
        var edgeToTris = new Dictionary<(int, int), List<int>>();
        for (int ti = 0; ti < mesh.Triangles.Length; ti++)
        {
            var tri = mesh.Triangles[ti];
            for (int k = 0; k < 3; k++)
            {
                int a = tri[k], b = tri[(k + 1) % 3];
                var edge = a < b ? (a, b) : (b, a);
                if (!edgeToTris.TryGetValue(edge, out var list))
                {
                    list = [];
                    edgeToTris[edge] = list;
                }
                list.Add(ti);
            }
        }

        var adj = new List<int>[mesh.Triangles.Length];
        for (int i = 0; i < adj.Length; i++) adj[i] = [];

        foreach (var (edge, tris) in edgeToTris)
        {
            if (blocked.Contains(edge) || tris.Count < 2) continue;
            for (int i = 0; i < tris.Count; i++)
                for (int j = i + 1; j < tris.Count; j++)
                {
                    adj[tris[i]].Add(tris[j]);
                    adj[tris[j]].Add(tris[i]);
                }
        }
        return adj;
    }

    private static WeldedMesh ExtractSubmesh(WeldedMesh mesh, List<int> triIndices, out Dictionary<int, int> oldToNew)
    {
        oldToNew = new Dictionary<int, int>();
        var verts = new List<System.Numerics.Vector3>();
        var tris  = new List<int[]>();

        foreach (int ti in triIndices)
        {
            var tri = mesh.Triangles[ti];
            int i0 = MapVertex(tri[0], mesh, verts, oldToNew);
            int i1 = MapVertex(tri[1], mesh, verts, oldToNew);
            int i2 = MapVertex(tri[2], mesh, verts, oldToNew);
            if (i0 != i1 && i1 != i2 && i0 != i2)
                tris.Add([i0, i1, i2]);
        }

        var va = verts.ToArray();
        return new WeldedMesh(va, tris.ToArray(), MeshGraph.BuildAdjacency(va, tris.ToArray()));
    }

    private static int MapVertex(int old, WeldedMesh mesh, List<System.Numerics.Vector3> verts, Dictionary<int, int> oldToNew)
    {
        if (!oldToNew.TryGetValue(old, out int nid))
        {
            nid = verts.Count;
            oldToNew[old] = nid;
            verts.Add(mesh.Vertices[old]);
        }
        return nid;
    }

    private static IReadOnlyList<int> RemapSeeds(HashSet<int> seeds, Dictionary<int, int> oldToNew)
    {
        var list = new List<int>();
        foreach (int s in seeds)
            if (oldToNew.TryGetValue(s, out int n)) list.Add(n);
        return list;
    }
}