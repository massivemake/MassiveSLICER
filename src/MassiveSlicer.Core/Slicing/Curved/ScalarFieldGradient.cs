using System.Numerics;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Core.Slicing.Curved;

/// <summary>Gradient and critical-point detection on a per-vertex scalar field.</summary>
public static class ScalarFieldGradient
{
    public sealed record CriticalPoints(
        IReadOnlyList<int> Minima,
        IReadOnlyList<int> Maxima,
        IReadOnlyList<int> Saddles);

    public static CriticalPoints FindCriticalPoints(WeldedMesh mesh, float[] scalarField)
    {
        var minima  = new List<int>();
        var maxima  = new List<int>();
        var saddles = new List<int>();

        for (int v = 0; v < mesh.VertexCount; v++)
        {
            float current = scalarField[v];
            var neighbors = NeighborIndices(mesh, v);
            if (neighbors.Count == 0) continue;

            var diffs = new List<float>();
            foreach (int n in neighbors)
            {
                float nv = scalarField[n];
                if (MathF.Abs(nv - current) > 1e-9f)
                    diffs.Add(current - nv);
            }
            if (diffs.Count == 0) continue;

            int signChanges = CountSignChanges(diffs);
            if (signChanges == 0)
            {
                float firstNeighbor = scalarField[neighbors[0]];
                if (current > firstNeighbor) maxima.Add(v);
                else minima.Add(v);
            }
            else if (signChanges > 2 && signChanges % 2 == 0)
                saddles.Add(v);
        }

        return new CriticalPoints(minima, maxima, saddles);
    }

    public static float FindWeightThroughSaddle(
        WeldedMesh mesh, BoundaryTarget low, BoundaryTarget high, int saddleVertex)
    {
        float lo = 0f, hi = 1f;
        for (int iter = 0; iter < 32; iter++)
        {
            float mid = (lo + hi) * 0.5f;
            var field = InterpolationField.Compute(mid, low, high);
            float val = field[saddleVertex];
            if (MathF.Abs(val) < 1e-4f) return mid;
            if (val > 0f) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5f;
    }

    private static List<int> NeighborIndices(WeldedMesh mesh, int v)
    {
        var list = new List<int>(mesh.Adjacency[v].Count);
        foreach (var (nb, _) in mesh.Adjacency[v]) list.Add(nb);
        return list;
    }

    private static int CountSignChanges(IReadOnlyList<float> values)
    {
        int count = 0;
        float prev = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            float v = values[i];
            if (prev * v < 0f) count++;
            prev = v;
        }
        return count;
    }
}