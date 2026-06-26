using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Core.Slicing.Curved;

/// <summary>
/// Geodesic distance field from a boundary vertex set (one or more connected clusters).
/// Port of compas_slicer CompoundTarget with min union across clusters.
/// </summary>
public sealed class BoundaryTarget
{
    private readonly WeldedMesh _mesh;
    private readonly float[] _distances;
    private readonly IReadOnlyList<int> _seedVertices;
    private readonly IReadOnlyList<IReadOnlyList<int>> _clusters;

    public BoundaryTarget(WeldedMesh mesh, IReadOnlyList<int> seedVertices)
    {
        _mesh = mesh;
        _seedVertices = seedVertices;
        _clusters = ClusterSeeds(mesh, seedVertices);
        if (_clusters.Count == 0)
            throw new InvalidOperationException("Boundary target has no seed vertices.");

        _distances = ComputeUnionDistances(mesh, _clusters);
    }

    public IReadOnlyList<int> SeedVertices => _seedVertices;
    public IReadOnlyList<IReadOnlyList<int>> Clusters => _clusters;
    public float[] Distances => _distances;

    public float GetDistance(int vertexIndex) => _distances[vertexIndex];

    public float GetMaxDistance()
    {
        float max = 0f;
        foreach (var d in _distances)
            if (d < float.MaxValue / 2f && d > max) max = d;
        return max;
    }

    public float GetAvgDistanceFromOther(BoundaryTarget other)
    {
        if (other._seedVertices.Count == 0) return 0f;
        float sum = 0f;
        foreach (int v in other._seedVertices)
            sum += _distances[v];
        return sum / other._seedVertices.Count;
    }

    private static IReadOnlyList<IReadOnlyList<int>> ClusterSeeds(WeldedMesh mesh, IReadOnlyList<int> seeds)
    {
        var seedSet = new HashSet<int>(seeds);
        var visited = new HashSet<int>();
        var clusters = new List<IReadOnlyList<int>>();

        foreach (int s in seeds)
        {
            if (!seedSet.Contains(s) || visited.Contains(s)) continue;
            var cluster = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(s);
            visited.Add(s);

            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                cluster.Add(u);
                foreach (var (nb, _) in mesh.Adjacency[u])
                {
                    if (seedSet.Contains(nb) && visited.Add(nb))
                        queue.Enqueue(nb);
                }
            }
            clusters.Add(cluster);
        }
        return clusters;
    }

    private static float[] ComputeUnionDistances(WeldedMesh mesh, IReadOnlyList<IReadOnlyList<int>> clusters)
    {
        var result = new float[mesh.VertexCount];
        Array.Fill(result, float.MaxValue);

        foreach (var cluster in clusters)
        {
            var d = MeshGraph.DijkstraFromSeeds(mesh, cluster);
            for (int i = 0; i < mesh.VertexCount; i++)
                if (d[i] < result[i]) result[i] = d[i];
        }
        return result;
    }
}