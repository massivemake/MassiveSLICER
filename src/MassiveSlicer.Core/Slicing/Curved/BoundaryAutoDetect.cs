using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Core.Slicing.Curved;

/// <summary>Auto-detect LOW/HIGH boundary vertex rings from Z extrema bands.</summary>
public static class BoundaryAutoDetect
{
    public static (IReadOnlyList<int> low, IReadOnlyList<int> high) Detect(WeldedMesh mesh, float bandMm)
    {
        float zMin = float.MaxValue, zMax = float.MinValue;
        foreach (var v in mesh.Vertices)
        {
            if (v.Z < zMin) zMin = v.Z;
            if (v.Z > zMax) zMax = v.Z;
        }

        float lowCut  = zMin + bandMm;
        float highCut = zMax - bandMm;

        var lowCandidates  = new List<int>();
        var highCandidates = new List<int>();
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float z = mesh.Vertices[i].Z;
            if (z <= lowCut)  lowCandidates.Add(i);
            if (z >= highCut) highCandidates.Add(i);
        }

        return (
            LargestCluster(mesh, lowCandidates),
            LargestCluster(mesh, highCandidates));
    }

    private static IReadOnlyList<int> LargestCluster(WeldedMesh mesh, List<int> candidates)
    {
        if (candidates.Count == 0) return [];
        var set = new HashSet<int>(candidates);
        var visited = new HashSet<int>();
        List<int>? best = null;

        foreach (int s in candidates)
        {
            if (visited.Contains(s)) continue;
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
                    if (set.Contains(nb) && visited.Add(nb))
                        queue.Enqueue(nb);
                }
            }

            if (best is null || cluster.Count > best.Count)
                best = cluster;
        }

        return best ?? [];
    }
}