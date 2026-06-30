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
    public static Toolpath Slice(IReadOnlyList<Vector3[]> meshes, SliceSettings settings)
    {
        var mesh = MeshGraph.Build(meshes);
        if (mesh.VertexCount == 0 || mesh.Triangles.Length == 0) return new Toolpath();

        float zMin = float.MaxValue;
        foreach (var v in mesh.Vertices) if (v.Z < zMin) zMin = v.Z;

        var geodDist = MeshGraph.DijkstraFromZThreshold(mesh, zMin + settings.LayerHeight * 0.1f);

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
            targetAtParameter: t => t);
    }
}