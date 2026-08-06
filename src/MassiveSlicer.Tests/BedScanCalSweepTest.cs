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
    public void VantageOffsetsY_Defaults_To_Centre_Only()
    {
        // Safe default: no side-step when scanner is already aimed at bed.
        var v = BedScanCalSweep.VantageOffsetsY(null);
        Assert.Single(v);
        Assert.Equal(0f, v[0]);
    }
}