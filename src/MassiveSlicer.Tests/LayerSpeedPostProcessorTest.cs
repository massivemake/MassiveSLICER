using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

public class LayerSpeedPostProcessorTest
{
    [Fact]
    public void Apply_scales_shortest_layer_to_min_and_longest_to_full_print_speed()
    {
        var toolpath = new Toolpath();
        toolpath.Layers.Add(MakeLayer(0, 100));
        toolpath.Layers.Add(MakeLayer(1, 500));
        toolpath.Layers.Add(MakeLayer(2, 250));

        var settings = new SliceSettings
        {
            LayerSpeedAdaptEnabled = true,
            LayerSpeedBasis        = LayerSpeedBasis.CutLength,
            PrintSpeedMps          = 0.06f,
            LayerSpeedMinMmS       = 10f,
            LayerSpeedMaxMmS       = 60f,
        };

        var result = LayerSpeedPostProcessor.Apply(toolpath, settings);

        Assert.Equal(10f / 60f, ExtrudeScale(result, 0), 3);
        Assert.Equal(1f, ExtrudeScale(result, 1), 3);
        Assert.Equal(0.479f, ExtrudeScale(result, 2), 2);
    }

    [Fact]
    public void Export_layer_speed_before_travel_turns_extruder_off_on_travel_not_during()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f)
        {
            Height      = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
        {
            PrintSpeedScale = 1f,
            Normal          = Vector3.UnitZ,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            PrintSpeedScale = 0.5f,
            Normal          = Vector3.UnitZ,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(200, 0, 10), MoveKind.Travel));
        tp.Layers.Add(layer);

        var settings = new KrlExportSettings
        {
            ProgramName         = "layer_speed_travel",
            PrintSpeedMps       = 0.06f,
            ExtrusionRpmPercent = 60f,
            TravelSetAnout4Zero = true,
        };

        var krl = KrlExporter.Export(tp, settings);

        int layerSpeedIdx  = krl.IndexOf("$ANOUT[4] = 0.3 ; layer speed", StringComparison.Ordinal);
        int secondExtrude  = krl.IndexOf("X 100.00", StringComparison.Ordinal);
        int travelIdx      = krl.IndexOf(";travel", StringComparison.Ordinal);
        int extruderOffIdx = krl.LastIndexOf("$ANOUT[4] = 0.000 ; extruder off", StringComparison.Ordinal);

        Assert.True(layerSpeedIdx >= 0);
        Assert.True(secondExtrude > layerSpeedIdx, "layer speed applies to the following extrude LIN (C_VEL)");
        Assert.True(travelIdx > secondExtrude, "travel follows the extrude segment");
        Assert.True(extruderOffIdx > travelIdx, "travel zeros extruder RPM — not during the preceding extrude");
        Assert.DoesNotContain("layer speed", krl[(travelIdx + 1)..], StringComparison.Ordinal);
    }

    [Fact]
    public void Export_layer_speed_emits_scaled_vel_and_anout()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f)
        {
            Height      = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            PrintSpeedScale = 0.5f,
            Normal          = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var settings = new KrlExportSettings
        {
            ProgramName           = "layer_speed",
            PrintSpeedMps         = 0.06f,
            ExtrusionRpmPercent   = 60f,
            TravelSetAnout4Zero   = false,
            ExtrusionStartWaitSec = 0f,
        };

        var krl = KrlExporter.Export(tp, settings);

        Assert.Contains("$VEL.CP = 0.030000", krl);
        Assert.Contains("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]=0.3 ; RPM on", krl);
    }

    private static float ExtrudeScale(Toolpath tp, int layerIndex)
        => tp.Layers[layerIndex].Moves.First(m => m.Kind == MoveKind.Extrude).PrintSpeedScale;

    private static ToolpathLayer MakeLayer(int index, float extrudeMm)
    {
        var layer = new ToolpathLayer(index, index * 3f);
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(extrudeMm, 0, layer.Z), MoveKind.Extrude));
        return layer;
    }
}