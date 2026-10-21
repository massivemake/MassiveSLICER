using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Non-planar geodesic slicer. Computes a surface-distance field from the build plate
/// via Dijkstra on the welded mesh graph, then extracts curved 3D iso-distance contours
/// as toolpath layers.
/// </summary>
public static class GeodesicSlicer
{
    /// <param name="progress">Optional 0..1 progress callback: mesh weld ≈ 0.05,
    /// distance field ≈ 0.25, then contour extraction 0.25 → 1.</param>
    public static Toolpath Slice(IReadOnlyList<Vector3[]> meshes, SliceSettings settings,
                                 Action<float>? progress = null)
    {
        var mesh = MeshGraph.Build(meshes);
        if (mesh.VertexCount == 0 || mesh.Triangles.Length == 0) return new Toolpath();
        progress?.Invoke(0.05f);

        // Seed the bottom of every connected piece. A stray fragment below the part
        // used to become the only seed, and the real shell was left unreachable.
        var geodDist = DistanceFromLocalBottoms(mesh, settings.LayerHeight * 0.1f);
        progress?.Invoke(0.25f);

        float maxDist = 0f;
        foreach (var d in geodDist)
            if (d < float.MaxValue / 2f && d > maxDist) maxDist = d;
        if (maxDist < settings.FirstLayerHeight) return new Toolpath();

        var parameters = new List<float>();
        float layerD = settings.FirstLayerHeight;
        while (layerD <= maxDist + 1e-4f)
        {
            parameters.Add(layerD);
            layerD += settings.LayerHeight;
        }

        return MeshGraph.SliceScalarLayers(
            mesh,
            _ => geodDist,
            parameters,
            settings,
            targetAtParameter: t => t,
            progress: progress is null ? null : f => progress(0.25f + f * 0.75f));
    }

    static float[] DistanceFromLocalBottoms(WeldedMesh mesh, float band)
    {
        int n = mesh.VertexCount;
        var comp = new int[n];
        Array.Fill(comp, -1);
        var zMin = new List<float>();
        var stack = new Stack<int>();

        for (int i = 0; i < n; i++)
        {
            if (comp[i] >= 0) continue;
            int id = zMin.Count;
            zMin.Add(float.MaxValue);
            stack.Push(i);
            comp[i] = id;
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                float z = mesh.Vertices[u].Z;
                if (z < zMin[id]) zMin[id] = z;
                foreach (var (v, _) in mesh.Adjacency[u])
                {
                    if (comp[v] >= 0) continue;
                    comp[v] = id;
                    stack.Push(v);
                }
            }
        }

        var seeds = new List<int>();
        for (int i = 0; i < n; i++)
            if (mesh.Vertices[i].Z <= zMin[comp[i]] + band)
                seeds.Add(i);
        return MeshGraph.DijkstraFromSeeds(mesh, seeds);
    }
}