using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class ToolpathRpmTest
{
    /// <summary>Toolpath with one extrude move per supplied print-speed scale.</summary>
    private static Toolpath Path(params float[] speedScales)
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        float x = 0f;
        foreach (float s in speedScales)
        {
            layer.Moves.Add(new ToolpathMove(
                new Vector3(x, 0, 10), new Vector3(x + 100f, 0, 10), MoveKind.Extrude)
            {
                Normal = Vector3.UnitZ,
                PrintSpeedScale = s,
            });
            x += 100f;
        }
        tp.Layers.Add(layer);
        return tp;
    }

    private static KrlExportSettings Settings(float rpmPercent) => new()
    {
        ProgramName         = "rpm_test",
        ExtrusionRpmPercent = rpmPercent,
    };

    [Fact]
    public void Under_the_limit_is_clean_and_exports()
    {
        var a = ToolpathRpm.Analyze(Path(1f, 1f), Settings(80f));

        Assert.False(a.HasOverLimit);
        Assert.Equal(0, a.OverCount);
        Assert.Equal(80f, a.PeakPercent, 3);
        Assert.Empty(a.Spans);
        Assert.NotEmpty(KrlExporter.Export(Path(1f, 1f), Settings(80f)));
    }

    [Fact]
    public void Ninety_nine_is_allowed_and_anything_above_is_not()
    {
        Assert.False(ToolpathRpm.IsOverLimit(99f));      // exactly at the limit
        Assert.False(ToolpathRpm.IsOverLimit(98.4f));    // steps up to 99
        Assert.True(ToolpathRpm.IsOverLimit(99.01f));    // steps up to 100
        Assert.True(ToolpathRpm.IsOverLimit(130f));
    }

    [Fact]
    public void Over_limit_moves_are_flagged_individually()
    {
        // 60 % nominal: the 1.0x moves sit at 60 %, the 1.8x move demands 108 %.
        var a = ToolpathRpm.Analyze(Path(1f, 1.8f, 1f), Settings(60f));

        Assert.True(a.HasOverLimit);
        Assert.Equal(1, a.OverCount);
        Assert.Equal([false, true, false], a.OverLimit);
        Assert.Equal(108f, a.PeakPercent, 2);

        var span = Assert.Single(a.Spans);
        Assert.Equal(1, span.FirstMoveIndex);
        Assert.Equal(1, span.LastMoveIndex);
        Assert.Equal(0, span.LayerIndex);
    }

    [Fact]
    public void Consecutive_over_limit_moves_group_into_one_span()
    {
        var a = ToolpathRpm.Analyze(Path(1f, 1.8f, 1.9f, 1f, 2f), Settings(60f));

        Assert.Equal(3, a.OverCount);
        Assert.Equal(2, a.Spans.Count);
        Assert.Equal(1, a.Spans[0].FirstMoveIndex);
        Assert.Equal(2, a.Spans[0].LastMoveIndex);
        Assert.Equal(2, a.Spans[0].MoveCount);
        Assert.Equal(4, a.Spans[1].FirstMoveIndex);
    }

    [Fact]
    public void Export_refuses_a_toolpath_that_exceeds_the_limit()
    {
        var ex = Assert.Throws<RpmLimitExceededException>(
            () => KrlExporter.Export(Path(1f, 1.8f), Settings(60f)));

        Assert.Equal(1, ex.Analysis.OverCount);
        Assert.Contains("99", ex.Message);
    }

    /// <summary>
    /// The guarantee: what the viewport reads out of ToolpathRpm is what the exporter
    /// writes into $ANOUT[4]. Nothing between the two may adjust the number.
    /// </summary>
    [Fact]
    public void Viewport_rpm_matches_every_value_written_to_the_src()
    {
        var scales   = new[] { 0.4f, 0.75f, 1f, 1.2f };
        var settings = Settings(70f);
        var tp       = Path(scales);

        var analysis = ToolpathRpm.Analyze(tp, settings);
        var krl      = KrlExporter.Export(tp, settings);

        // Every extrusion $ANOUT[4] the exporter emitted (the 0.001 idle line aside).
        var written = Regex.Matches(krl, @"\$ANOUT\[4\]\s*=\s*([0-9]*\.?[0-9]+)")
            .Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Where(v => v > 0.01f)          // drop idle (0.001) and extruder-off (0)
            .Distinct()
            .ToList();

        var predicted = analysis.PerMovePercent
            .Where(p => !float.IsNaN(p) && p > 0f)
            .Select(p => p / 100f)
            .Distinct()
            .ToList();

        Assert.NotEmpty(written);
        foreach (float w in written)
            Assert.Contains(predicted, p => Math.Abs(p - w) < 1e-4f);
    }

    [Fact]
    public void First_layer_override_is_checked_against_its_own_rpm()
    {
        var tp = new Toolpath();
        foreach (int i in new[] { 0, 1 })
        {
            var layer = new ToolpathLayer(i, 10f + i * 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
            layer.Moves.Add(new ToolpathMove(
                new Vector3(0, i, 10 + i * 3), new Vector3(100, i, 10 + i * 3), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
            tp.Layers.Add(layer);
        }

        // Normal layers are fine at 50 %; only the first-layer override is over.
        var a = ToolpathRpm.Analyze(tp, Settings(50f) with { FirstLayerRpmPercent = 120f });

        Assert.Equal(1, a.OverCount);
        Assert.True(a.OverLimit[0]);
        Assert.False(a.OverLimit[1]);
        Assert.Equal(0, Assert.Single(a.Spans).LayerIndex);
    }

    [Fact]
    public void Travel_moves_carry_no_rpm_reading()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude)
        { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var a = ToolpathRpm.Analyze(tp, Settings(200f));

        Assert.True(float.IsNaN(a.PerMovePercent[0]));   // travel spins at idle, not a print RPM
        Assert.False(a.OverLimit[0]);
        Assert.True(a.OverLimit[1]);
        Assert.Equal(1, a.OverCount);
    }

    [Fact]
    public void Wedge_layer_thickness_can_push_a_move_over_on_its_own()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        { Normal = Vector3.UnitZ, HeightScale = 1f });
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(200, 0, 10), MoveKind.Extrude)
        { Normal = Vector3.UnitZ, HeightScale = 1.5f });   // thick end of the wedge
        tp.Layers.Add(layer);

        var a = ToolpathRpm.Analyze(tp, Settings(80f));

        Assert.False(a.OverLimit[0]);   // 80 %
        Assert.True(a.OverLimit[1]);    // 120 %
    }

    [Fact]
    public void Milling_export_is_untouched_by_the_extruder_limit()
    {
        // Spindle RPM is a different machine value; the extruder limit must not block it.
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Mill)
        { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName         = "mill_test",
            IsMilling           = true,
            SpindleRpm          = 12000f,
            ExtrusionRpmPercent = 500f,   // nonsense for a mill program — must not matter
        });

        Assert.NotEmpty(krl);
    }

    [Fact]
    public void A_header_template_cannot_smuggle_in_an_over_limit_rpm()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KrlExporter.Export(
            Path(1f),
            Settings(50f) with
            {
                HeaderTemplate = "DEF {{PROGRAM_NAME}}()\n$ANOUT[4] = 1.0 ; sneaky\nBAS(#INITMOV,0)\n",
            }));

        Assert.Contains("$ANOUT[4]", ex.Message);
    }
}
