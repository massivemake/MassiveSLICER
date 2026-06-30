using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>Motion speeds used to estimate toolpath duration (all mm/s).</summary>
public readonly record struct ToolpathMotionRates(double PrintMmS, double TravelMmS, double WipeMmS);

/// <summary>Per-layer cut length and estimated print time.</summary>
public readonly record struct LayerMetric(int LayerIndex, float Z, double CutLengthMm, double TimeSeconds);

/// <summary>Aggregate toolpath statistics including per-layer min/max spans.</summary>
public sealed class ToolpathStatsResult
{
    public double TotalTimeSeconds { get; init; }
    public double VolumeMm3         { get; init; }
    public LayerMetric? ShortestCutLength { get; init; }
    public LayerMetric? LongestCutLength  { get; init; }
    public LayerMetric? ShortestTime      { get; init; }
    public LayerMetric? LongestTime       { get; init; }
}

public static class ToolpathStatistics
{
    public static ToolpathStatsResult Compute(
        Toolpath toolpath,
        ToolpathMotionRates rates,
        double beadWidthMm,
        double layerHeightMm)
    {
        double totalTime = 0.0, volMm3 = 0.0;
        LayerMetric? shortestLen = null, longestLen = null;
        LayerMetric? shortestTime = null, longestTime = null;

        foreach (var layer in toolpath.Layers)
        {
            double cutLen = 0.0, layerTime = 0.0;
            foreach (var move in layer.Moves)
            {
                double dist = Vector3.Distance(move.From, move.To);
                layerTime += MoveTimeSeconds(move, rates, dist);
                if (ToolpathMoveKinds.IsCutSegment(move.Kind))
                    cutLen += dist;
                if (move.Kind == MoveKind.Extrude)
                    volMm3 += dist * beadWidthMm * layerHeightMm;
            }

            totalTime += layerTime;
            var metric = new LayerMetric(layer.Index, layer.Z, cutLen, layerTime);

            shortestLen  = PickMin(shortestLen, metric, static m => m.CutLengthMm);
            longestLen   = PickMax(longestLen,  metric, static m => m.CutLengthMm);
            shortestTime = PickMin(shortestTime, metric, static m => m.TimeSeconds);
            longestTime  = PickMax(longestTime,  metric, static m => m.TimeSeconds);
        }

        return new ToolpathStatsResult
        {
            TotalTimeSeconds   = totalTime,
            VolumeMm3          = volMm3,
            ShortestCutLength  = shortestLen,
            LongestCutLength   = longestLen,
            ShortestTime       = shortestTime,
            LongestTime        = longestTime,
        };
    }

    public static double MoveTimeSeconds(ToolpathMove move, ToolpathMotionRates rates, double distanceMm)
    {
        if (move.IsWipe)
            return distanceMm / rates.WipeMmS;
        if (move.Kind == MoveKind.Extrude)
        {
            double speed = rates.PrintMmS * Math.Max(move.PrintSpeedScale, 1e-6f);
            if (move.IsResumeRamp)
                speed *= Math.Max(move.ResumeSpeedScale, 1e-6f);
            return distanceMm / speed;
        }
        double travelMmS = move.TravelSpeedMps is { } mps and > 0f
            ? mps * 1000.0
            : rates.TravelMmS;
        return distanceMm / travelMmS;
    }

    public static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s"
            : $"{ts.Minutes}m {ts.Seconds:D2}s";
    }

    public static string FormatCutLength(double mm)
        => mm >= 10_000 ? $"{mm / 1000.0:F2} m" : $"{mm:F0} mm";

    public static string FormatLayerLength(LayerMetric? metric)
        => metric is null ? "--" : $"{FormatCutLength(metric.Value.CutLengthMm)} (L{metric.Value.LayerIndex})";

    public static string FormatLayerTime(LayerMetric? metric)
        => metric is null ? "--" : $"{FormatDuration(metric.Value.TimeSeconds)} (L{metric.Value.LayerIndex})";

    private static LayerMetric? PickMin(LayerMetric? current, LayerMetric candidate, Func<LayerMetric, double> selector)
    {
        if (current is null || selector(candidate) < selector(current.Value))
            return candidate;
        if (Math.Abs(selector(candidate) - selector(current.Value)) < 1e-9 && candidate.LayerIndex < current.Value.LayerIndex)
            return candidate;
        return current;
    }

    private static LayerMetric? PickMax(LayerMetric? current, LayerMetric candidate, Func<LayerMetric, double> selector)
    {
        if (current is null || selector(candidate) > selector(current.Value))
            return candidate;
        if (Math.Abs(selector(candidate) - selector(current.Value)) < 1e-9 && candidate.LayerIndex < current.Value.LayerIndex)
            return candidate;
        return current;
    }
}