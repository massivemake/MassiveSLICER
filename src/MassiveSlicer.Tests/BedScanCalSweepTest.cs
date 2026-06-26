using MassiveSlicer.Core.Scanning;

namespace MassiveSlicer.Tests;

public class BedScanCalSweepTest
{
    [Fact]
    public void DefaultE1Angles_Matches_Legacy_NineByForty()
    {
        var angles = BedScanCalSweep.DefaultE1Angles();
        Assert.Equal(9, angles.Count);
        Assert.Equal(-180, angles[0]);
        Assert.Equal(140, angles[8]);
    }

    [Fact]
    public void VantageOffsetsY_Defaults_To_Centre_And_Minus300()
    {
        var v = BedScanCalSweep.VantageOffsetsY(null);
        Assert.Equal(2, v.Count);
        Assert.Equal(0f, v[0]);
        Assert.Equal(-300f, v[1]);
    }
}