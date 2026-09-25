using System.Numerics;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// The Cut Tool's Height is the distance above the bed surface, not world Z — so "127" cuts
/// 127 mm (5") above the bed on every cell, whatever Z the cell's bed sits at.
/// </summary>
public sealed class CutToolHeightFromBedTest
{
    [Theory]
    [InlineData(70f)]      // LFAM 1 bed surface Z
    [InlineData(130f)]     // LFAM 2
    [InlineData(916.31f)]  // LFAM 3
    public void Horizontal_height_is_measured_from_the_bed_surface(float bedZ)
    {
        var s = new CutToolDialogViewModel { BedPoint = new Vector3(1500f, -600f, bedZ) };
        s.SetPose(new Vector3(1600f, -500f, bedZ + 127f), Vector3.UnitZ);
        Assert.Equal(127.0, s.Height, 3);

        s.Height = 50.8;   // 2"
        Assert.Equal(bedZ + 50.8, s.CenterZ, 3);
        Assert.Equal(1600.0, s.CenterX, 3);   // a horizontal height edit only moves the plane up/down
    }

    [Fact]
    public void Tilted_cut_measures_along_its_normal_from_the_bed_centre()
    {
        var bed = new Vector3(0f, 0f, 70f);
        var n = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var s = new CutToolDialogViewModel { BedPoint = bed };
        s.SetPose(bed + n * 100f, n);
        Assert.Equal(100.0, s.Height, 3);
    }
}
