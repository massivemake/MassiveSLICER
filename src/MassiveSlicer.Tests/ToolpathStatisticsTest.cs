using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public class ToolpathStatisticsTest
{
    private static readonly ToolpathMotionRates Rates = new(PrintMmS: 100.0, TravelMmS: 200.0, WipeMmS: 120.0);

    [Fact]
    public void Compute_finds_longest_and_shortest_layer_cut_length_and_time()
    {
        var toolpath = new Toolpath();
        toolpath.Layers.Add(MakeLayer(0, 0f, extrudeMm: 100, travelMm: 50));
        toolpath.Layers.Add(MakeLayer(1, 3f, extrudeMm: 400, travelMm: 20));
        toolpath.Layers.Add(MakeLayer(2, 6f, extrudeMm: 250, travelMm: 10));

        var stats = ToolpathStatistics.Compute(toolpath, Rates, beadWidthMm: 6, layerHeightMm: 3);

        Assert.NotNull(stats.ShortestCutLength);
        Assert.Equal(0, stats.ShortestCutLength.Value.LayerIndex);
        Assert.Equal(100, stats.ShortestCutLength.Value.CutLengthMm, 3);
        Assert.NotNull(stats.LongestCutLength);
        Assert.Equal(1, stats.LongestCutLength.Value.LayerIndex);
        Assert.Equal(400, stats.LongestCutLength.Value.CutLengthMm, 3);

        // L0: 100/100 + 50/200 = 1.25s
        // L1: 400/100 + 20/200 = 4.10s
        // L2: 250/100 + 10/200 = 2.55s
        Assert.NotNull(stats.ShortestTime);
        Assert.Equal(0, stats.ShortestTime.Value.LayerIndex);
        Assert.Equal(1.25, stats.ShortestTime.Value.TimeSeconds, 3);
        Assert.NotNull(stats.LongestTime);
        Assert.Equal(1, stats.LongestTime.Value.LayerIndex);
        Assert.Equal(4.10, stats.LongestTime.Value.TimeSeconds, 3);
        Assert.Equal(7.90, stats.TotalTimeSeconds, 3);
    }

    [Fact]
    public void Compute_counts_mill_moves_in_cut_length()
    {
        var layer = new ToolpathLayer(0, 0f);
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 0), new Vector3(80, 0, 0), MoveKind.Mill));
        layer.Moves.Add(new ToolpathMove(new Vector3(80, 0, 0), new Vector3(120, 0, 0), MoveKind.Travel));

        var toolpath = new Toolpath();
        toolpath.Layers.Add(layer);

        var stats = ToolpathStatistics.Compute(toolpath, Rates, beadWidthMm: 6, layerHeightMm: 3);

        Assert.NotNull(stats.LongestCutLength);
        Assert.Equal(80, stats.LongestCutLength.Value.CutLengthMm, 3);
        Assert.NotNull(stats.LongestTime);
        Assert.Equal(80 / 200.0 + 40 / 200.0, stats.LongestTime.Value.TimeSeconds, 3);
    }

    [Fact]
    public void FormatLayerLength_and_time_include_layer_index()
    {
        var metric = new LayerMetric(7, 21f, 1234.0, 65.0);

        Assert.Equal("1234 mm (L7)", ToolpathStatistics.FormatLayerLength(metric));
        Assert.Equal("1m 05s (L7)", ToolpathStatistics.FormatLayerTime(metric));
    }

    private static ToolpathLayer MakeLayer(int index, float z, double extrudeMm, double travelMm)
    {
        var layer = new ToolpathLayer(index, z);
        var p0 = new Vector3(0, 0, z);
        var p1 = new Vector3((float)extrudeMm, 0, z);
        var p2 = new Vector3((float)(extrudeMm + travelMm), 0, z);
        layer.Moves.Add(new ToolpathMove(p0, p1, MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(p1, p2, MoveKind.Travel));
        return layer;
    }
}