using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class SliceLayerAnalyzerHangTest
{
    /// <summary>
    /// Regression: a layer whose cut run STARTS with an IsLayerStitch-flagged extrude
    /// used to spin SynthesizeRuns forever (count=0 → continue without advancing),
    /// hanging the UI thread when edit mode opened. Analyze must terminate.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public void Analyze_LayerStartingWithStitchMove_Terminates()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        // Stitch-flagged extrude first (the killer), then a travel, then a normal run.
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(5, 0, 0), MoveKind.Extrude) { IsLayerStitch = true });
        layer.Moves.Add(new ToolpathMove(new Vector3(5, 0, 0), new Vector3(10, 0, 0), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(10, 0, 0), new Vector3(20, 0, 0), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(20, 0, 0), new Vector3(20, 10, 0), MoveKind.Extrude));
        tp.Layers.Add(layer);

        var stats = SliceLayerAnalyzer.Analyze(tp, 0, beadWidthMm: 6f, layerHeightMm: 3f);

        Assert.True(stats.Islands >= 1);   // the normal run still counts
    }

    [Fact(Timeout = 10_000)]
    public void Analyze_OnlyStitchMoves_Terminates()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(5, 0, 0), MoveKind.Extrude) { IsLayerStitch = true });
        layer.Moves.Add(new ToolpathMove(new Vector3(5, 0, 0), new Vector3(9, 0, 0), MoveKind.Extrude) { IsLayerChange = true });
        tp.Layers.Add(layer);

        var stats = SliceLayerAnalyzer.Analyze(tp, 0, beadWidthMm: 6f, layerHeightMm: 3f);
        Assert.Equal(0, stats.Islands);
    }
}
