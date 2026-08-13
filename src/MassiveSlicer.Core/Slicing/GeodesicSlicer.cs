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

        float zMin = float.MaxValue;
        foreach (var v in mesh.Vertices) if (v.Z < zMin) zMin = v.Z;

        var geodDist = MeshGraph.DijkstraFromZThreshold(mesh, zMin + settings.LayerHeight * 0.1f);
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
}