using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Core.Slicing.Curved;

/// <summary>
/// Non-planar curved slicing (interpolation / sweep) between LOW and HIGH surface boundaries.
/// Port of compas_slicer InterpolationSlicer with region-split support for branching geometry.
/// </summary>
public static class CurvedSlicer
{
    public static Toolpath Slice(IReadOnlyList<Vector3[]> meshes, SliceSettings settings)
    {
        var mesh = MeshGraph.Build(meshes);
        if (mesh.VertexCount == 0 || mesh.Triangles.Length == 0) return new Toolpath();

        var (lowVerts, highVerts) = ResolveBoundaries(mesh, settings);
        if (lowVerts.Count == 0 || highVerts.Count == 0)
            throw new InvalidOperationException(
                "Curved slicing requires LOW and HIGH boundary vertices. Use auto-detect, viewport pick, or JSON import.");

        var lowTarget  = new BoundaryTarget(mesh, lowVerts);
        var highTarget = new BoundaryTarget(mesh, highVerts);

        IReadOnlyList<MeshRegionSplitter.MeshPart> parts;
        if (settings.CurvedEnableRegionSplit)
        {
            var field05 = InterpolationField.Compute(0.5f, lowTarget, highTarget);
            var critical = ScalarFieldGradient.FindCriticalPoints(mesh, field05);
            parts = MeshRegionSplitter.Split(mesh, lowTarget, highTarget, critical.Saddles);
            parts = SplitMeshGraph.OrderForPrint(parts);
        }
        else
        {
            parts = [new MeshRegionSplitter.MeshPart(mesh, lowVerts, highVerts)];
        }

        var merged = new Toolpath();
        int globalLayerIdx = 0;

        foreach (var part in parts)
        {
            var partLow  = new BoundaryTarget(part.Mesh, part.LowVertices);
            var partHigh = new BoundaryTarget(part.Mesh, part.HighVertices);

            int n = InterpolationSchedule.FindNoOfIsocurves(partLow, partHigh, settings.LayerHeight);
            var tParams = InterpolationSchedule.GetInterpolationParameters(n);

            var partTp = MeshGraph.SliceScalarLayers(
                part.Mesh,
                t => InterpolationField.Compute(t, partLow, partHigh),
                tParams,
                settings);

            foreach (var layer in partTp.Layers)
            {
                var mergedLayer = new ToolpathLayer(globalLayerIdx++, layer.Z);
                mergedLayer.Moves.AddRange(layer.Moves);
                merged.Layers.Add(mergedLayer);
            }
        }

        return merged;
    }

    private static (IReadOnlyList<int> low, IReadOnlyList<int> high) ResolveBoundaries(
        WeldedMesh mesh, SliceSettings settings)
    {
        if (settings.CurvedBoundaryLowVertices.Count > 0 && settings.CurvedBoundaryHighVertices.Count > 0)
            return (settings.CurvedBoundaryLowVertices, settings.CurvedBoundaryHighVertices);

        if (settings.CurvedBoundarySource != CurvedBoundarySource.JsonImport)
            return BoundaryAutoDetect.Detect(mesh, settings.CurvedAutoDetectBandMm);

        return ([], []);
    }
}