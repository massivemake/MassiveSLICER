using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class GuidePlaneAlignTest
{
    [Fact]
    public void Flat_face_at_mid_height_is_zero_tilt()
    {
        var fit = GuidePlaneAlign.FromFace(0, 0, 50, 0, 0, 1, -40, -40, 0, 40, 40, 100);
        Assert.Equal(50, fit.HeightPct, 1);
        Assert.Equal(0, fit.AngleX, 1);
        Assert.Equal(0, fit.AngleY, 1);
    }

    [Fact]
    public void Face_leaning_along_x_sets_y_tilt()
    {
        float ty = 20f * MathF.PI / 180f;
        var fit = GuidePlaneAlign.FromFace(
            0, 0, 50, MathF.Sin(ty), 0, MathF.Cos(ty), -80, -80, 0, 80, 80, 100);
        Assert.Equal(50, fit.HeightPct, 1);
        Assert.Equal(0, fit.AngleX, 1);
        Assert.Equal(20, fit.AngleY, 1);
    }

    [Fact]
    public void Face_leaning_along_y_sets_x_tilt()
    {
        float tx = 15f * MathF.PI / 180f;
        var fit = GuidePlaneAlign.FromFace(
            0, 0, 40, 0, -MathF.Sin(tx), MathF.Cos(tx), -80, -80, 0, 80, 80, 100);
        Assert.Equal(40, fit.HeightPct, 1);
        Assert.Equal(15, fit.AngleX, 1);
        Assert.Equal(0, fit.AngleY, 1);
    }
}
