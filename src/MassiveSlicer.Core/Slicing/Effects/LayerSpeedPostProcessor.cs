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
        float? brimRpm  = BrimRpmOverride(settings);

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
                if (move.IsBrim)
                {
                    if (move.Kind == MoveKind.Extrude)
                        layer.Moves[mi] = move with
                        {
                            PrintSpeedScale    = brimScale,
                            RpmPercentOverride = brimRpm,
                        };
                    continue;
                }
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
    /// Brim never takes the per-layer adaptive scale — <see cref="Apply"/> gives it its own
    /// fixed speed (and optional absolute RPM) before this is reached.
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

    /// <summary>
    /// Absolute brim RPM (%) to stamp on brim extrude moves, or null to let RPM follow brim
    /// speed as normal. Lets the brim be deliberately over-extruded for bed adhesion despite
    /// running slow — the whole reason it is absolute rather than another scale.
    /// </summary>
    public static float? BrimRpmOverride(SliceSettings settings)
        => settings.BrimRpmPercent > 1e-6f
            ? Math.Clamp(settings.BrimRpmPercent, 1f, SliceSettings.MaxBrimRpmPercent)
            : null;

    private static Toolpath ResetScales(Toolpath toolpath, SliceSettings settings)
    {
        float baseMmS   = settings.PrintSpeedMps * 1000f;
        float brimScale = BrimScale(settings, baseMmS);
        float? brimRpm  = BrimRpmOverride(settings);

        var result = ToolpathClone.Copy(toolpath);
        foreach (var layer in result.Layers)
        {
            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                bool isBrimBead = move.IsBrim && move.Kind == MoveKind.Extrude;
                float  want    = isBrimBead ? brimScale : 1f;
                float? wantRpm = isBrimBead ? brimRpm : null;
                if (Math.Abs(move.PrintSpeedScale - want) < 1e-6f
                    && Nullable.Equals(move.RpmPercentOverride, wantRpm)) continue;
                layer.Moves[mi] = move with { PrintSpeedScale = want, RpmPercentOverride = wantRpm };
            }
        }
        return result;
    }
}