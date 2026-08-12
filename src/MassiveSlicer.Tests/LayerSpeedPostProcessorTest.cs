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

        // The brim is on its own fixed speed, default 60, regardless of the 20-89 window.
        var brim = r.Layers[0].Moves.First(m => m.IsBrim);
        Assert.Equal(SliceSettings.MaxBrimSpeedMmS, brim.PrintSpeedScale * baseMmS, 1);
    }

    /// <summary>
    /// Brim speed is a fixed field: it must not move when print speed or the Adaptive Speed
    /// window changes, and it is capped at <see cref="SliceSettings.MaxBrimSpeedMmS"/>.
    /// </summary>
    [Theory]
    [InlineData(85f, 20f, 89f, 45f, 45f)]    // asked 45 -> 45, regardless of the window
    [InlineData(85f, 95f, 120f, 45f, 45f)]   // window entirely above it -> still 45
    [InlineData(30f, 20f, 89f, 45f, 45f)]    // print speed below it -> still 45
    [InlineData(85f, 60f, 99f, 200f, 60f)]   // over the cap -> clamped to 60
    [InlineData(85f, 60f, 99f, 0f, 1f)]      // zero/unset -> clamped to the 1 mm/s floor
    public void Brim_speed_is_fixed_and_capped(
        float baseMmS, float minMmS, float maxMmS, float asked, float expectedMmS)
    {
        var settings = new SliceSettings
        {
            LayerSpeedAdaptEnabled = true,
            LayerSpeedBasis        = LayerSpeedBasis.CutLength,
            PrintSpeedMps          = baseMmS / 1000f,
            LayerSpeedMinMmS       = minMmS,
            LayerSpeedMaxMmS       = maxMmS,
            BrimSpeedMmS           = asked,
        };

        var tp = new Toolpath();
        var layer0 = MakeLayer(0, 8000);
        layer0.Moves.Insert(0, new ToolpathMove(
            Vector3.Zero, new Vector3(7750, 0, 0), MoveKind.Extrude) { IsBrim = true });
        tp.Layers.Add(layer0);
        tp.Layers.Add(MakeLayer(1, 6000));

        var brim = LayerSpeedPostProcessor.Apply(tp, settings)
                                         .Layers[0].Moves.First(m => m.IsBrim);
        Assert.Equal(expectedMmS, brim.PrintSpeedScale * baseMmS, 1);
    }

    /// <summary>
    /// Brim RPM is an ABSOLUTE demand. The whole point is to lay a fat brim for adhesion while
    /// running slow, so it must not be multiplied by the speed scale (which would pull it back
    /// toward the speed-derived value) nor by HeightScale.
    /// </summary>
    [Theory]
    [InlineData(0f,   null)]   // off -> RPM follows speed
    [InlineData(55f,  55f)]    // honoured exactly, not scaled by the 60/85 speed ratio
    [InlineData(200f, 99f)]    // clamped to the export gate
    public void Brim_rpm_override_is_absolute(float asked, float? expectedPct)
    {
        const float baseMmS = 85f;
        var settings = new SliceSettings
        {
            LayerSpeedAdaptEnabled = true,
            LayerSpeedBasis        = LayerSpeedBasis.CutLength,
            PrintSpeedMps          = baseMmS / 1000f,
            LayerSpeedMinMmS       = 20f,
            LayerSpeedMaxMmS       = 89f,
            BrimSpeedMmS           = 30f,
            BrimRpmPercent         = asked,
        };
        var krl = new KrlExportSettings
        {
            ProgramName = "brim_rpm_abs", BeadWidthMm = 6f, LayerHeightMm = 3f,
            PrintSpeedMps = baseMmS / 1000f, FlowRate = 0.5512f,
        };

        var tp = new Toolpath();
        var layer0 = MakeLayer(0, 8000);
        layer0.Moves.Insert(0, new ToolpathMove(
            Vector3.Zero, new Vector3(7750, 0, 0), MoveKind.Extrude)
            { IsBrim = true, HeightScale = 0.5f });   // even a thinned layer must not scale it
        tp.Layers.Add(layer0);
        tp.Layers.Add(MakeLayer(1, 6000));

        var r = LayerSpeedPostProcessor.Apply(tp, settings);
        var brim = r.Layers[0].Moves.First(m => m.IsBrim);

        // Speed is unaffected by the RPM override either way.
        Assert.Equal(30f, brim.PrintSpeedScale * baseMmS, 1);

        if (expectedPct is null)
        {
            Assert.Null(brim.RpmPercentOverride);
            // Falls back to speed-derived flow, halved again by HeightScale.
            Assert.Equal(ToolpathRpm.BasePercent(krl) * (30f / baseMmS) * 0.5f,
                         ToolpathRpm.MovePercent(brim, krl), 2);
        }
        else
        {
            Assert.Equal(expectedPct.Value, brim.RpmPercentOverride!.Value, 2);
            Assert.Equal(expectedPct.Value, ToolpathRpm.MovePercent(brim, krl), 2);
        }
    }

    /// <summary>
    /// The brim RPM override has to reach the WRITTEN PROGRAM, not just ToolpathRpm.MovePercent.
    /// The exporter has its own scale-based path, so an override honoured only by MovePercent
    /// showed the right number in the viewport and the gate while the .src still carried the
    /// speed-derived value — 7.14 % where 30 % was asked for.
    /// </summary>
    [Fact]
    public void Brim_rpm_override_reaches_the_exported_program()
    {
        const float baseMmS = 60f;
        var slice = new SliceSettings
        {
            LayerSpeedAdaptEnabled = true,
            LayerSpeedBasis        = LayerSpeedBasis.CutLength,
            PrintSpeedMps          = baseMmS / 1000f,
            LayerSpeedMinMmS       = 60f,
            LayerSpeedMaxMmS       = 99f,
            BrimSpeedMmS           = 12f,
            BrimRpmPercent         = 30f,
        };

        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(500, 0, 3), MoveKind.Extrude)
            { IsBrim = true, Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(500, 0, 3), new Vector3(900, 0, 3), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var processed = LayerSpeedPostProcessor.Apply(tp, slice);
        var krl = new KrlExportSettings
        {
            ProgramName             = "brim_rpm_export",
            BeadWidthMm             = 6f,
            LayerHeightMm           = 3f,
            PrintSpeedMps           = baseMmS / 1000f,
            FlowRate                = 0.5512f,
            DigitalStartStopEnabled = true,
            ExtrusionStartWaitSec   = 0f,
        };

        string src = KrlExporter.Export(processed, krl);

        // The brim's own commanded speed and RPM, as the machine will read them.
        Assert.Contains("$VEL.CP = 0.012000", src);
        Assert.Contains("RPM = 30", src);
        // And the speed-derived value it used to emit must be gone.
        Assert.DoesNotContain("RPM = 7.1", src);

        // Belt and braces: the gate and the program agree on the same number.
        var brim = processed.Layers[0].Moves.First(m => m.IsBrim);
        Assert.Equal(30f, ToolpathRpm.MovePercent(brim, krl), 2);
    }

    /// <summary>Brim speed applies even with Adaptive Speed switched off.</summary>
    [Fact]
    public void Brim_speed_applies_when_adaptive_speed_is_disabled()
    {
        const float baseMmS = 85f;
        var settings = new SliceSettings
        {
            LayerSpeedAdaptEnabled = false,
            PrintSpeedMps          = baseMmS / 1000f,
            BrimSpeedMmS           = 40f,
        };

        var tp = new Toolpath();
        var layer0 = MakeLayer(0, 8000);
        layer0.Moves.Insert(0, new ToolpathMove(
            Vector3.Zero, new Vector3(7750, 0, 0), MoveKind.Extrude) { IsBrim = true });
        tp.Layers.Add(layer0);

        var r = LayerSpeedPostProcessor.Apply(tp, settings);
        Assert.Equal(40f, r.Layers[0].Moves.First(m => m.IsBrim).PrintSpeedScale * baseMmS, 1);
        // The part itself is untouched: full print speed.
        Assert.Equal(1f, ObjectScale(r, 0), 4);
    }

    /// <summary>
    /// The brim used to be the move that hit the 99 % RPM export gate: it took the maximum
    /// speed and, being full nominal thickness, nothing reduced its flow. That capped how high
    /// the maximum could be set at all.
    /// </summary>
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

        // The brim runs at its own fixed 60 mm/s, so its RPM is the nominal scaled to that —
        // flow follows speed automatically, no over- or under-extrusion on the brim.
        var brim = r.Layers[0].Moves.First(m => m.IsBrim);
        Assert.Equal(ToolpathRpm.BasePercent(krl) * (SliceSettings.MaxBrimSpeedMmS / 85f),
                     ToolpathRpm.MovePercent(brim, krl), 2);
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