using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Modifiers;

/// <summary>
/// Splits a <see cref="Toolpath"/> at a horizontal (Z) plane for the Cut modifier.
/// Whole layers are bucketed to one side or the other — a layer is never split mid-run,
/// so layer <see cref="ToolpathLayer.Index"/>/<see cref="ToolpathLayer.Z"/> are preserved
/// unchanged on both sides. The source toolpath is never mutated.
/// </summary>
public static class HorizontalCutSplitter
{
    public sealed record SplitResult(Toolpath Below, Toolpath Above);

    /// <param name="source">The toolpath to split. Never modified.</param>
    /// <param name="cutZ">Plane height (mm, WORLD space — toolpath moves are baked in world
    /// space at slice time, unlike MeshData, which is local; confirmed against PlanarSlicer's
    /// actual input and SceneRenderer's centroid-based node placement). Layers with Z below this
    /// go to <see cref="SplitResult.Below"/>; Z at or above go to <see cref="SplitResult.Above"/>.</param>
    public static SplitResult Split(Toolpath source, float cutZ)
    {
        var below = new Toolpath { FormboundStats = source.FormboundStats };
        var above = new Toolpath { FormboundStats = source.FormboundStats };

        foreach (var layer in source.Layers)
        {
            var target = layer.Z < cutZ ? below : above;
            target.Layers.Add(CloneLayer(layer));
        }

        return new SplitResult(below, above);
    }

    private static ToolpathLayer CloneLayer(ToolpathLayer layer)
    {
        var copy = new ToolpathLayer(layer.Index, layer.Z)
        {
            Height       = layer.Height,
            PlaneNormal  = layer.PlaneNormal,
            ThermalTempC = layer.ThermalTempC,
        };
        copy.Moves.AddRange(layer.Moves);
        copy.Contours.AddRange(layer.Contours);
        return copy;
    }
}
