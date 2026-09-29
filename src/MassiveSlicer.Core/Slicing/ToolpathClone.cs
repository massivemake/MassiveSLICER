using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>Deep copy helpers for <see cref="Toolpath"/> instances.</summary>
public static class ToolpathClone
{
    public static Toolpath Copy(Toolpath source)
    {
        var copy = new Toolpath { FormboundStats = source.FormboundStats };
        foreach (var layer in source.Layers)
        {
            var layerCopy = CloneLayer(layer, z: layer.Z);
            copy.Layers.Add(layerCopy);
        }
        return copy;
    }

    /// <summary>
    /// Shifts every move (and layer Z) by <paramref name="delta"/> in place.
    /// Cell-swap uses this so a path from LFAM 3 lands on LFAM 1's print bed.
    /// </summary>
    public static void OffsetInPlace(Toolpath tp, Vector3 delta)
    {
        if (tp.Layers.Count == 0) return;
        if (delta == Vector3.Zero) return;
        for (int i = 0; i < tp.Layers.Count; i++)
            tp.Layers[i] = CloneLayer(tp.Layers[i], z: tp.Layers[i].Z + delta.Z, delta);
    }

    static ToolpathLayer CloneLayer(ToolpathLayer layer, float z, Vector3 delta = default)
    {
        var layerCopy = new ToolpathLayer(layer.Index, z)
        {
            Height       = layer.Height,
            PlaneNormal  = layer.PlaneNormal,
            ThermalTempC = layer.ThermalTempC,
        };
        layerCopy.Contours.AddRange(layer.Contours);
        foreach (var move in layer.Moves)
        {
            layerCopy.Moves.Add(new ToolpathMove(move.From + delta, move.To + delta, move.Kind)
            {
                Normal            = move.Normal,
                IsLayerChange     = move.IsLayerChange,
                IsLayerStitch     = move.IsLayerStitch,
                IsWipe            = move.IsWipe,
                IsPreTravelStart  = move.IsPreTravelStart,
                IsPostTravelEnd   = move.IsPostTravelEnd,
                WipeRpmScale      = move.WipeRpmScale,
                IsResumeRamp      = move.IsResumeRamp,
                ResumeSpeedScale  = move.ResumeSpeedScale,
                ResumeRpmScale    = move.ResumeRpmScale,
                IsZHop            = move.IsZHop,
                IsMergeConnector  = move.IsMergeConnector,
                TravelSpeedMps    = move.TravelSpeedMps,
                ResumeWaitSec     = move.ResumeWaitSec,
                PrintSpeedScale   = move.PrintSpeedScale,
                IsLightning       = move.IsLightning,
                HeightScale       = move.HeightScale,
                IsBrim            = move.IsBrim,
                IsWall            = move.IsWall,
                RpmPercentOverride = move.RpmPercentOverride,
                TcpYawDeg         = move.TcpYawDeg,
                E1Mm              = move.E1Mm,
            });
        }
        return layerCopy;
    }
}