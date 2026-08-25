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
        };

        var krl = KrlExporter.Export(tp, settings);

        int layerSpeedIdx  = krl.IndexOf("$ANOUT[4] = 0.30 ; layer speed", StringComparison.Ordinal);
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
            ExtrusionStartWaitSec = 0f,
        };

        var krl = KrlExporter.Export(tp, settings);

        Assert.Contains("$VEL.CP = 0.030000", krl);
        Assert.Contains("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]=0.30 ; RPM on", krl);
    }

    /// <summary>
    /// Real numbers off the Glider column (Rev 55): a 2-loop brim is 7.75 m while the object's
    /// own first layer is 8.62 m and the body runs 6.48–8.62 m. Counting the brim made layer 0
    /// a 16.37 m outlier — 1.9x anything else — so it took the maximum speed and squeezed the
    /// whole part into the bottom 22 % of the window.
    /// </summary>
    [Fact]
    public void Brim_is_excluded_from_the_layer_metric_so_the_body_gets_the_whole_window()
    {
        var settings = new SliceSettings
        {
            LayerSpeedAdaptEnabled = true,
            LayerSpeedBasis        = LayerSpeedBasis.CutLength,
            PrintSpeedMps          = 0.085f,
            LayerSpeedMinMmS       = 20f,
            LayerSpeedMaxMmS       = 89f,
        };

        var tp = new Toolpath();
        var layer0 = MakeLayer(0, 8620);                                  // the object's first layer
        layer0.Moves.Insert(0, new ToolpathMove(                          // prepended, as BrimPlanner does
            Vector3.Zero, new Vector3(7750, 0, 0), MoveKind.Extrude) { IsBrim = true });
        tp.Layers.Add(layer0);
        tp.Layers.Add(MakeLayer(1, 6482));                                // shortest body layer
        tp.Layers.Add(MakeLayer(2, 8620));                                // longest body layer

        var r = LayerSpeedPostProcessor.Apply(tp, settings);
        float baseMmS = 85f;

        // Layer 0 measures 8620 (not 16370), so it ties layer 2 for longest.
        Assert.Equal(89f, ObjectScale(r, 0) * baseMmS, 1);
        Assert.Equal(20f, ObjectScale(r, 1) * baseMmS, 1);
        Assert.Equal(89f, ObjectScale(r, 2) * baseMmS, 1);

        // Brim is still held OUT of the window — it just runs at the nominal print speed now
        // that the fixed-speed override has gone, rather than tracking layer 0's demand.
        var brim = r.Layers[0].Moves.First(m => m.IsBrim);
        Assert.Equal(baseMmS, brim.PrintSpeedScale * baseMmS, 1);
    }

    [Fact]
    public void Brim_no_longer_drives_the_peak_RPM_of_the_toolpath()
    {
        var settings = new SliceSettings
        {
            LayerSpeedAdaptEnabled = true,
            LayerSpeedBasis        = LayerSpeedBasis.CutLength,
            PrintSpeedMps          = 0.085f,
            LayerSpeedMinMmS       = 20f,
            LayerSpeedMaxMmS       = 130f,        // would put the brim at 110 % RPM if adaptable
        };

        var tp = new Toolpath();
        var layer0 = MakeLayer(0, 4000);
        layer0.Moves.Insert(0, new ToolpathMove(
            Vector3.Zero, new Vector3(20000, 0, 0), MoveKind.Extrude) { IsBrim = true });
        tp.Layers.Add(layer0);
        tp.Layers.Add(MakeLayer(1, 4000));
        var r = LayerSpeedPostProcessor.Apply(tp, settings);

        var krl = new KrlExportSettings
        {
            ProgramName   = "brim_rpm",
            BeadWidthMm   = 7f,
            LayerHeightMm = 3f,
            PrintSpeedMps = 0.085f,
            FlowRate      = 0.5693f,
        };
        var analysis = ToolpathRpm.Analyze(r, krl);

        Assert.False(analysis.HasOverLimit);
        Assert.True(analysis.PeakPercent <= ToolpathRpm.MaxRpmPercent,
            $"peak RPM {analysis.PeakPercent:0.#} % must stay inside the export gate");

        // Brim runs at the nominal print speed, so its RPM is simply the nominal — flow follows
        // speed, so no over- or under-extrusion on the brim either way.
        var brim = r.Layers[0].Moves.First(m => m.IsBrim);
        Assert.Equal(ToolpathRpm.BasePercent(krl), ToolpathRpm.MovePercent(brim, krl), 2);
    }

    private static float ObjectScale(Toolpath tp, int layerIndex)
        => tp.Layers[layerIndex].Moves.First(m => m.Kind == MoveKind.Extrude && !m.IsBrim).PrintSpeedScale;

    private static float ExtrudeScale(Toolpath tp, int layerIndex)
        => tp.Layers[layerIndex].Moves.First(m => m.Kind == MoveKind.Extrude).PrintSpeedScale;

    private static ToolpathLayer MakeLayer(int index, float extrudeMm)
    {
        var layer = new ToolpathLayer(index, index * 3f);
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(extrudeMm, 0, layer.Z), MoveKind.Extrude));
        return layer;
    }
}