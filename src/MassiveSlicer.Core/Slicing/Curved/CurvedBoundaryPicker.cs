using System.Numerics;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Core.Slicing.Curved;

/// <summary>Viewport helpers for picking and growing boundary vertex rings.</summary>
public static class CurvedBoundaryPicker
{
    public static int FindNearestVertex(WeldedMesh mesh, Vector3 worldPoint)
    {
        int best = -1;
        float bestSq = float.MaxValue;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float d = (mesh.Vertices[i] - worldPoint).LengthSquared();
            if (d < bestSq) { bestSq = d; best = i; }
        }
        return best;
    }

    public static IReadOnlyList<int> GrowRingFromSeed(WeldedMesh mesh, int seed, float bandMm, bool isLowBand)
    {
        if (seed < 0 || seed >= mesh.VertexCount) return [];
        float zRef = mesh.Vertices[seed].Z;
        var set = new HashSet<int> { seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);

        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (var (nb, _) in mesh.Adjacency[u])
            {
                float z = mesh.Vertices[nb].Z;
                bool inBand = isLowBand ? z <= zRef + bandMm : z >= zRef - bandMm;
                if (inBand && set.Add(nb))
                    queue.Enqueue(nb);
            }
        }
        return set.ToList();
    }
}