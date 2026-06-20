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
        Assert.Equal("0.5", KrlAnout.RpmPercentToAnoutText(50f));
    }

    [Fact]
    public void RpmIdleAnout_is_one_percent()
    {
        Assert.Equal(0.001f, KrlAnout.RpmIdleAnout, precision: 5);
        Assert.Equal("0.001", KrlAnout.RpmIdleAnoutText);
    }

    [Fact]
    public void RpmToAnout_calibration_point_petg_flow_rate()
    {
        // W=6, H=3, v=100 mm/s, FlowRate=0.463 → 50% RPM → $ANOUT[4]=0.5
        float anout = KrlAnout.RpmToAnout(6f, 3f, 0.1f, 0.463f);
        Assert.Equal(0.5f, anout, precision: 3);
    }
}