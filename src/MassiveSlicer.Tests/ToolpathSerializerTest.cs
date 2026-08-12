using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class ToolpathSerializerTest
{
    [Fact]
    public void RoundTrip_preserves_layers_and_moves()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f)
        {
            Height      = 3f,
            PlaneNormal = new Vector3(0, 0, 1),
        };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            Normal        = new Vector3(0, 0, 1),
            IsLayerChange = false,
            IsLayerStitch = true,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(100, 100, 10), MoveKind.Travel)
        {
            IsLayerChange = true,
        });
        tp.Layers.Add(layer);

        var restored = ToolpathSerializer.FromData(ToolpathSerializer.ToData(tp));

        Assert.Single(restored.Layers);
        var rl = restored.Layers[0];
        Assert.Equal(0, rl.Index);
        Assert.Equal(10f, rl.Z, precision: 3);
        Assert.Equal(3f, rl.Height, precision: 3);
        Assert.Equal(Vector3.UnitZ, rl.PlaneNormal);
        Assert.Equal(2, rl.Moves.Count);
        Assert.Equal(MoveKind.Extrude, rl.Moves[0].Kind);
        Assert.True(rl.Moves[0].IsLayerStitch);
        Assert.Equal(MoveKind.Travel, rl.Moves[1].Kind);
        Assert.True(rl.Moves[1].IsLayerChange);
    }

    /// <summary>
    /// HeightScale is the adaptive-layer-height / Multi-Planar flow correction. It used to be
    /// dropped on save, so reopening a workspace and exporting without re-slicing sent every
    /// thin layer out at full nominal flow.
    /// </summary>
    [Fact]
    public void RoundTrip_preserves_HeightScale_and_the_other_per_move_fields()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 1f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            HeightScale     = 0.3333f,      // a 1 mm slice of a 3 mm nominal layer
            PrintSpeedScale = 0.5f,
            IsLightning     = true,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(100, 100, 10), MoveKind.Travel)
        {
            IsMergeConnector = true,
            TravelSpeedMps   = 0.075f,
        });
        // IsBrim has to survive too: reprocessing a reloaded workspace re-runs
        // LayerSpeedPostProcessor, which would otherwise put the brim back into the metric.
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
        {
            IsBrim = true,
        });
        tp.Layers.Add(layer);

        var restored = ToolpathSerializer.FromData(ToolpathSerializer.ToData(tp));
        var m0 = restored.Layers[0].Moves[0];
        var m1 = restored.Layers[0].Moves[1];
        var m2 = restored.Layers[0].Moves[2];

        Assert.Equal(0.3333f, m0.HeightScale, precision: 4);
        Assert.Equal(0.5f, m0.PrintSpeedScale, precision: 4);
        Assert.True(m0.IsLightning);
        Assert.False(m0.IsBrim);
        Assert.True(m1.IsMergeConnector);
        Assert.Equal(0.075f, m1.TravelSpeedMps!.Value, precision: 4);
        Assert.True(m2.IsBrim);
    }

    /// <summary>The number that actually reaches the machine has to survive the round trip.</summary>
    [Fact]
    public void RoundTrip_preserves_the_exported_RPM_of_a_thin_layer()
    {
        var krl = new KrlExportSettings
        {
            ProgramName   = "T",
            BeadWidthMm   = 7f,
            LayerHeightMm = 3f,
            PrintSpeedMps = 0.085f,
            FlowRate      = 0.5693f,
        };

        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 1f, PlaneNormal = Vector3.UnitZ };
        var thin = new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            HeightScale = 1f / 3f,          // 1 mm of a 3 mm nominal layer
        };
        layer.Moves.Add(thin);
        tp.Layers.Add(layer);

        float before = ToolpathRpm.MovePercent(thin, krl);
        var restored = ToolpathSerializer.FromData(ToolpathSerializer.ToData(tp));
        float after = ToolpathRpm.MovePercent(restored.Layers[0].Moves[0], krl);

        // Nominal at 85 mm/s is ~60.97 %; a third of the layer height must be a third of that.
        Assert.Equal(ToolpathRpm.BasePercent(krl) / 3f, before, precision: 2);
        Assert.Equal(before, after, precision: 3);
    }

    /// <summary>
    /// Files written before HeightScale existed have no such property. They must load as
    /// nominal flow (1), i.e. exactly the behaviour they had when they were saved.
    /// </summary>
    [Fact]
    public void Legacy_move_without_HeightScale_loads_as_nominal_flow()
    {
        var data = new WorkspaceToolpathData();
        var layerDto = new WorkspaceToolpathLayerData { Index = 0, Z = 10f, Height = 3f };
        layerDto.Moves.Add(new WorkspaceToolpathMoveData
        {
            From = [0, 0, 10], To = [100, 0, 10], Kind = "Extrude",
        });
        // A malformed 0 must not export dry either — 0 is never written by the save path.
        layerDto.Moves.Add(new WorkspaceToolpathMoveData
        {
            From = [100, 0, 10], To = [200, 0, 10], Kind = "Extrude", HeightScale = 0f,
        });
        data.Layers.Add(layerDto);

        var restored = ToolpathSerializer.FromData(data);

        Assert.Equal(1f, restored.Layers[0].Moves[0].HeightScale, precision: 4);
        Assert.Equal(1f, restored.Layers[0].Moves[1].HeightScale, precision: 4);
    }
}