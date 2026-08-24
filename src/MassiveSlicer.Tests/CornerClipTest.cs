using Avalonia;
using Avalonia.Media;
using MassiveSlicer.App.Behaviors;

namespace MassiveSlicer.Tests;

public sealed class CornerClipTest
{
    [Fact]
    public void CreateClip_uses_rounded_rect_not_a_square()
    {
        var clip = CornerClip.CreateClip(new Size(200, 80), new CornerRadius(5));
        Assert.NotNull(clip);
        Assert.Equal(5, clip!.RadiusX);
        Assert.Equal(5, clip.RadiusY);
        Assert.Equal(200, clip.Rect.Width);
        Assert.Equal(80, clip.Rect.Height);
    }

    [Fact]
    public void CreateClip_caps_radius_to_half_the_short_side()
    {
        var clip = CornerClip.CreateClip(new Size(8, 40), new CornerRadius(5));
        Assert.NotNull(clip);
        Assert.Equal(4, clip!.RadiusX);
    }

    [Fact]
    public void CreateClip_skips_zero_size()
    {
        Assert.Null(CornerClip.CreateClip(default, new CornerRadius(5)));
    }
}
