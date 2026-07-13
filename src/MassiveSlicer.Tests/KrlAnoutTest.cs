using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public sealed class KrlAnoutTest
{
    [Fact]
    public void TempToAnout_220C_matches_on_cell_literal()
    {
        Assert.Equal(0.2272f, KrlAnout.TempToAnout(220f), precision: 5);
        Assert.Equal("0.2272", KrlAnout.TempToAnoutText(220f));
    }

    [Fact]
    public void TempToAnout_230C_matches_lfam_formula()
    {
        Assert.Equal(0.2592f, KrlAnout.TempToAnout(230f), precision: 5);
        Assert.Equal("0.2592", KrlAnout.TempToAnoutText(230f));
    }

    [Fact]
    public void TempToAnout_240C_matches_lfam_formula()
    {
        Assert.Equal(0.2912f, KrlAnout.TempToAnout(240f), precision: 5);
        Assert.Equal("0.2912", KrlAnout.TempToAnoutText(240f));
    }

    [Fact]
    public void RpmPercentToAnout_50_percent()
    {
        Assert.Equal(0.5f, KrlAnout.RpmPercentToAnout(50f), precision: 4);
        Assert.Equal("0.50", KrlAnout.RpmPercentToAnoutText(50f));
    }

    [Fact]
    public void RpmIdleAnout_is_one_percent()
    {
        // Idle uses a separate 0.001 scale (not whole-percent extrusion steps).
        Assert.Equal(0.001f, KrlAnout.RpmIdleAnout, precision: 5);
        Assert.Equal("0.001", KrlAnout.RpmIdleAnoutText);
    }

    [Fact]
    public void RoundAnout4UpToPercent_ceilings_to_whole_percent_prefer_overextrusion()
    {
        Assert.Equal(0.03f, KrlAnout.RoundAnout4UpToPercent(0.0203f), precision: 4);
        Assert.Equal(0.04f, KrlAnout.RoundAnout4UpToPercent(0.0303f), precision: 4);
        Assert.Equal(0.03f, KrlAnout.RoundAnout4UpToPercent(0.03f), precision: 4);
        Assert.Equal(0.01f, KrlAnout.RoundAnout4UpToPercent(0.001f), precision: 4);
        Assert.Equal(0f, KrlAnout.RoundAnout4UpToPercent(0f), precision: 4);
        Assert.Equal(1f, KrlAnout.RoundAnout4UpToPercent(1.0f), precision: 4);
        Assert.Equal("0.03", KrlAnout.FormatAnout4(0.0203f));
        Assert.Equal("0.04", KrlAnout.FormatAnout4(0.0303f));
    }

    [Fact]
    public void RpmPercentToAnout_ceilings_fractional_percent()
    {
        // 2.03 % → 0.0203 raw → ceiling to 0.03
        Assert.Equal(0.03f, KrlAnout.RpmPercentToAnout(2.03f), precision: 4);
        Assert.Equal("0.03", KrlAnout.RpmPercentToAnoutText(2.03f));
        // 3.03 % → 0.04
        Assert.Equal(0.04f, KrlAnout.RpmPercentToAnout(3.03f), precision: 4);
        Assert.Equal("0.04", KrlAnout.RpmPercentToAnoutText(3.03f));
    }

    [Fact]
    public void RpmToAnout_calibration_point_petg_flow_rate()
    {
        // W=6, H=3, v=100 mm/s, FlowRate=0.463 → ~50.004% → ceiling to 51% → 0.51
        float anout = KrlAnout.RpmToAnout(6f, 3f, 0.1f, 0.463f);
        Assert.Equal(0.51f, anout, precision: 3);
        Assert.Equal("0.51", KrlAnout.FormatAnout4(anout));
    }

    [Fact]
    public void RpmToAnout_calibration_point_asa_gf_flow_rate()
    {
        // W=6, H=3, v=45 mm/s, FlowRate=0.4115 → 20% RPM → $ANOUT[4]=0.2
        float anout = KrlAnout.RpmToAnout(6f, 3f, 0.045f, 0.4115f);
        Assert.Equal(0.2f, anout, precision: 3);
    }
}