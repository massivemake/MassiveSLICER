using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class FirstLayerPercentAdjustTest
{
    [Fact]
    public void Parse_empty_and_plus_prefix()
    {
        Assert.Equal(0, FirstLayerPercentAdjust.Parse(null));
        Assert.Equal(0, FirstLayerPercentAdjust.Parse(""));
        Assert.Equal(0, FirstLayerPercentAdjust.Parse("  "));
        Assert.False(FirstLayerPercentAdjust.Has(""));
        Assert.False(FirstLayerPercentAdjust.Has("+0"));
        Assert.Equal(20, FirstLayerPercentAdjust.Parse("+20"));
        Assert.Equal(-15, FirstLayerPercentAdjust.Parse("-15"));
        Assert.True(FirstLayerPercentAdjust.Has("+20"));
    }

    [Fact]
    public void Speed_is_multiplicative_percent()
    {
        Assert.Equal(120, FirstLayerPercentAdjust.SpeedMmS(100, "+20"), 3);
        Assert.Equal(80, FirstLayerPercentAdjust.SpeedMmS(100, "-20"), 3);
        Assert.Equal(100, FirstLayerPercentAdjust.SpeedMmS(100, ""), 3);
    }

    [Fact]
    public void Rpm_is_additive_points()
    {
        Assert.Equal(50, FirstLayerPercentAdjust.RpmPercent(40, "+10"), 3);
        Assert.Equal(30, FirstLayerPercentAdjust.RpmPercent(40, "-10"), 3);
        Assert.Equal(40, FirstLayerPercentAdjust.RpmPercent(40, ""), 3);
        Assert.Equal(100, FirstLayerPercentAdjust.RpmPercent(95, "+20"), 3);
        Assert.Equal(0, FirstLayerPercentAdjust.RpmPercent(5, "-20"), 3);
    }

    [Fact]
    public void Export_applies_percent_adjust_only_on_layer0()
    {
        var tp = new Toolpath();
        var l0 = new ToolpathLayer(0, 3f) { PlaneNormal = System.Numerics.Vector3.UnitZ };
        l0.Moves.Add(new ToolpathMove(
            new System.Numerics.Vector3(0, 0, 3),
            new System.Numerics.Vector3(50, 0, 3),
            MoveKind.Extrude) { Normal = System.Numerics.Vector3.UnitZ });
        var l1 = new ToolpathLayer(1, 6f) { PlaneNormal = System.Numerics.Vector3.UnitZ };
        l1.Moves.Add(new ToolpathMove(
            new System.Numerics.Vector3(0, 0, 6),
            new System.Numerics.Vector3(50, 0, 6),
            MoveKind.Extrude) { Normal = System.Numerics.Vector3.UnitZ });
        tp.Layers.Add(l0);
        tp.Layers.Add(l1);

        float speedMmS = (float)FirstLayerPercentAdjust.SpeedMmS(100, "+20");
        float rpm = (float)FirstLayerPercentAdjust.RpmPercent(40, "+10");
        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName             = "first_layer_pct",
            PrintSpeedMps           = 0.100f,
            ExtrusionRpmPercent     = 40f,
            FirstLayerSpeedMps      = speedMmS / 1000f,
            FirstLayerRpmPercent    = rpm,
            DigitalStartStopEnabled = true,
            HeaderTemplate          = KrlExporter.DefaultUrmHeaderTemplate,
            FooterTemplate          = KrlExporter.DefaultFooterTemplate,
        });

        Assert.Contains("$VEL.CP = 0.120000", krl);
        Assert.Contains("RPM = 50", krl);
        Assert.Contains("$VEL.CP = 0.100000", krl);
        Assert.Contains("RPM = 40", krl);
        Assert.True(krl.IndexOf("$VEL.CP = 0.120000", StringComparison.Ordinal)
                    < krl.IndexOf("$VEL.CP = 0.100000", StringComparison.Ordinal));
    }
}
