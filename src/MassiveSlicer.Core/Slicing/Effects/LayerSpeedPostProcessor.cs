using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Assigns per-move <see cref="ToolpathMove.PrintSpeedScale"/> from layer cut length or time.
/// Longest/busiest layers use <see cref="SliceSettings.LayerSpeedMaxMmS"/>;
/// shortest layers use <see cref="SliceSettings.LayerSpeedMinMmS"/>.
/// RPM scales with the same factor as robot speed for KRL export.
/// </summary>
public static class LayerSpeedPostProcessor
{
    public static Toolpath Apply(Toolpath toolpath, SliceSettings settings)
    {
        if (!settings.LayerSpeedAdaptEnabled || toolpath.Layers.Count == 0)
            return ResetScales(toolpath);

        float baseMmS = settings.PrintSpeedMps * 1000f;
        float minMmS  = Math.Max(settings.LayerSpeedMinMmS, 0.1f);
        float maxMmS  = settings.LayerSpeedMaxMmS > 0f ? settings.LayerSpeedMaxMmS : baseMmS;
        if (minMmS > maxMmS)
            (minMmS, maxMmS) = (maxMmS, minMmS);

        var rates = new ToolpathMotionRates(
            baseMmS,
            settings.TravelSpeed * 1000f,
            settings.WipeSpeed * 1000f);

        var layerValues = new double[toolpath.Layers.Count];
        for (int i = 0; i < toolpath.Layers.Count; i++)
            layerValues[i] = LayerMetricValue(toolpath.Layers[i], settings.LayerSpeedBasis, rates);

        double minValue = layerValues.Min();
        double maxValue = layerValues.Max();

        var result = ToolpathClone.Copy(toolpath);
        for (int i = 0; i < result.Layers.Count; i++)
        {
            float scale = SpeedScaleForValue(layerValues[i], minValue, maxValue, minMmS, maxMmS, baseMmS);
            var layer = result.Layers[i];
            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                if (!IsAdaptable(move)) continue;
                layer.Moves[mi] = move with { PrintSpeedScale = scale };
            }
        }

        return result;
    }

    public static float SpeedScaleForValue(
        double value, double minValue, double maxValue, float minMmS, float maxMmS, float basePrintMmS)
    {
        double t = maxValue > minValue + 1e-9
            ? (value - minValue) / (maxValue - minValue)
            : 1.0;
        float speedMmS = (float)(minMmS + (maxMmS - minMmS) * t);
        return speedMmS / Math.Max(basePrintMmS, 0.1f);
    }

    private static double LayerMetricValue(ToolpathLayer layer, LayerSpeedBasis basis, ToolpathMotionRates rates)
    {
        if (basis == LayerSpeedBasis.CutLength)
        {
            double cutLen = 0.0;
            foreach (var move in layer.Moves)
            {
                if (!ToolpathMoveKinds.IsCutSegment(move.Kind)) continue;
                cutLen += Vector3.Distance(move.From, move.To);
            }
            return cutLen;
        }

        double layerTime = 0.0;
        foreach (var move in layer.Moves)
        {
            double dist = Vector3.Distance(move.From, move.To);
            layerTime += ToolpathStatistics.MoveTimeSeconds(move, rates, dist);
        }
        return layerTime;
    }

    private static bool IsAdaptable(ToolpathMove move)
        => move.Kind == MoveKind.Extrude && !move.IsWipe && !move.IsLayerStitch;

    private static Toolpath ResetScales(Toolpath toolpath)
    {
        var result = ToolpathClone.Copy(toolpath);
        foreach (var layer in result.Layers)
        {
            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                if (Math.Abs(move.PrintSpeedScale - 1f) < 1e-6f) continue;
                layer.Moves[mi] = move with { PrintSpeedScale = 1f };
            }
        }
        return result;
    }
}