using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>Deep copy helpers for <see cref="Toolpath"/> instances.</summary>
public static class ToolpathClone
{
    public static Toolpath Copy(Toolpath source)
    {
        var copy = new Toolpath();
        foreach (var layer in source.Layers)
        {
            var layerCopy = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height      = layer.Height,
                PlaneNormal = layer.PlaneNormal,
            };
            foreach (var move in layer.Moves)
            {
                layerCopy.Moves.Add(new ToolpathMove(move.From, move.To, move.Kind)
                {
                    Normal            = move.Normal,
                    IsLayerChange     = move.IsLayerChange,
                    IsLayerStitch     = move.IsLayerStitch,
                    IsWipe            = move.IsWipe,
                    WipeRpmScale      = move.WipeRpmScale,
                    IsResumeRamp      = move.IsResumeRamp,
                    ResumeSpeedScale  = move.ResumeSpeedScale,
                    ResumeRpmScale    = move.ResumeRpmScale,
                    IsZHop            = move.IsZHop,
                    IsMergeConnector  = move.IsMergeConnector,
                    TravelSpeedMps    = move.TravelSpeedMps,
                });
            }
            copy.Layers.Add(layerCopy);
        }
        return copy;
    }
}