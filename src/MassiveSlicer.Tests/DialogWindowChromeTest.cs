using MassiveSlicer.App.Views;

namespace MassiveSlicer.Tests;

public sealed class DialogWindowChromeTest
{
    [Fact]
    public void PhysicalRoundRect_matches_10px_card_at_1x()
    {
        var (w, h, ellipse) = DialogWindowChrome.PhysicalRoundRect(420, 760, 10, 1);
        Assert.Equal(420, w);
        Assert.Equal(760, h);
        Assert.Equal(20, ellipse);
    }

    [Fact]
    public void PhysicalRoundRect_scales_for_dpi()
    {
        var (w, h, ellipse) = DialogWindowChrome.PhysicalRoundRect(420, 760, 10, 1.5);
        Assert.Equal(630, w);
        Assert.Equal(1140, h);
        Assert.Equal(30, ellipse);
    }

    [Fact]
    public void PhysicalRoundRect_never_returns_zero_ellipse()
    {
        var (_, _, ellipse) = DialogWindowChrome.PhysicalRoundRect(100, 80, 0, 1);
        Assert.True(ellipse >= 2);
    }
}
