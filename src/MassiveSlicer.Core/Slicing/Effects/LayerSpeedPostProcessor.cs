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
        // Brim speed is deliberately independent of Adaptive Speed, so it still applies when
        // the feature is off.
        if (!settings.LayerSpeedAdaptEnabled || toolpath.Layers.Count == 0)
            return ResetScales(toolpath, settings);

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

        float brimScale = BrimScale(settings, baseMmS);

        var result = ToolpathClone.Copy(toolpath);
        for (int i = 0; i < result.Layers.Count; i++)
        {
            float scale = SpeedScaleForValue(layerValues[i], minValue, maxValue, minMmS, maxMmS, baseMmS);
            var layer = result.Layers[i];
            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                if (move.IsBrim)
                {
                    if (move.Kind == MoveKind.Extrude)
                        layer.Moves[mi] = move with { PrintSpeedScale = brimScale };
                    continue;
                }
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
                if (move.IsBrim) continue;
                cutLen += Vector3.Distance(move.From, move.To);
            }
            return cutLen;
        }

        double layerTime = 0.0;
        foreach (var move in layer.Moves)
        {
            if (move.IsBrim) continue;
            double dist = Vector3.Distance(move.From, move.To);
            layerTime += ToolpathStatistics.MoveTimeSeconds(move, rates, dist);
        }
        return layerTime;
    }

    /// <summary>
    /// Brim never takes the per-layer adaptive scale — <see cref="Apply"/> assigns it its own
    /// (the gentler of nominal print speed and the adaptive minimum) before this is reached.
    /// Left adaptable, the brim is the longest "layer" in the part, so it took the maximum
    /// speed — and being full nominal thickness, nothing reduced its flow. That made it the
    /// move that hit the 99 % RPM export gate, which in turn capped how high the maximum
    /// could be set and left the rest of the part crawling near the minimum.
    /// </summary>
    private static bool IsAdaptable(ToolpathMove move)
        => move.Kind == MoveKind.Extrude && !move.IsWipe && !move.IsLayerStitch && !move.IsBrim;

    /// <summary>
    /// Scale that puts a brim extrude move at <see cref="SliceSettings.BrimSpeedMmS"/>,
    /// clamped to <see cref="SliceSettings.MaxBrimSpeedMmS"/>. Independent of print speed and
    /// of the Adaptive Speed window by design — the brim is bed adhesion, not part shape.
    /// </summary>
    public static float BrimScale(SliceSettings settings, float basePrintMmS)
    {
        // Floor matches the UI's own clamp: a brim below 1 mm/s is never wanted, and an
        // unset 0 from an older preset must not divide the speed away to nothing.
        float brimMmS = Math.Clamp(settings.BrimSpeedMmS, 1f, SliceSettings.MaxBrimSpeedMmS);
        return brimMmS / Math.Max(basePrintMmS, 0.1f);
    }

    private static Toolpath ResetScales(Toolpath toolpath, SliceSettings settings)
    {
        float baseMmS   = settings.PrintSpeedMps * 1000f;
        float brimScale = BrimScale(settings, baseMmS);

        var result = ToolpathClone.Copy(toolpath);
        foreach (var layer in result.Layers)
        {
            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                float want = move.IsBrim && move.Kind == MoveKind.Extrude ? brimScale : 1f;
                if (Math.Abs(move.PrintSpeedScale - want) < 1e-6f) continue;
                layer.Moves[mi] = move with { PrintSpeedScale = want };
            }
        }
        return result;
    }
}