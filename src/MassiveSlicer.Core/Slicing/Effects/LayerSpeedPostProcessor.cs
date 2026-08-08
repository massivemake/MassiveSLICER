using System.Numerics;
using MassiveSlicer.Core.IO;
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
            float scale = settings.LayerSpeedUseRpmPercent
                ? RpmSpeedScaleForLayer(result.Layers[i], layerValues[i], minValue, maxValue, settings, baseMmS)
                : SpeedScaleForValue(layerValues[i], minValue, maxValue, minMmS, maxMmS, baseMmS);
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

    /// <summary>
    /// Speed scale for one layer when the range is stated as extruder RPM percent.
    ///
    /// The operator names the flow; the layer's own thickness decides the speed that reaches it.
    /// A 1 mm layer under a 3 mm nominal carries a third of the material per millimetre travelled,
    /// so it takes roughly three times the speed to demand the same RPM — which is exactly the
    /// headroom that a single mm/s ceiling leaves unused.
    ///
    /// Capped by <see cref="SliceSettings.LayerSpeedRobotMaxMmS"/>, because the extruder having room
    /// does not mean the arm does.
    /// </summary>
    public static float RpmSpeedScaleForLayer(
        ToolpathLayer layer, double value, double minValue, double maxValue,
        SliceSettings settings, float basePrintMmS)
    {
        double t = maxValue > minValue + 1e-9
            ? (value - minValue) / (maxValue - minValue)
            : 1.0;

        float minPct = MathF.Max(settings.LayerSpeedMinRpmPercent, 0.01f);
        float maxPct = MathF.Max(settings.LayerSpeedMaxRpmPercent, 0.01f);
        if (minPct > maxPct) (minPct, maxPct) = (maxPct, minPct);
        float targetPct = (float)(minPct + (maxPct - minPct) * t);

        // The real thickness of THIS layer, not the nominal — that is the whole point.
        float height = layer.Height > 1e-4f ? layer.Height : settings.LayerHeight;

        float speedMmS = KrlAnout.SpeedMmSForRpmPercent(
            targetPct, settings.BeadWidth, height, settings.FlowRate);

        // Unusable inputs (no flow rate, zero bead) must not silently stop the machine.
        if (speedMmS <= 0f) return 1f;

        float ceiling = settings.LayerSpeedRobotMaxMmS > 0.1f
            ? settings.LayerSpeedRobotMaxMmS
            : MathF.Max(settings.LayerSpeedMaxMmS, 0.1f);
        speedMmS = Math.Clamp(speedMmS, 0.1f, ceiling);

        return speedMmS / MathF.Max(basePrintMmS, 0.1f);
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