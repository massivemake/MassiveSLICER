using MassiveSlicer.App;
using Xunit;

namespace MassiveSlicer.Tests;

public class RobotKrlPathsTest
{
    [Fact]
    public void SanitizeStem_KeepsReadableDateNameRevFormat()
    {
        var s = RobotKrlPaths.SanitizeStem("2026_0710 - Drone Print V90 Rev08");
        Assert.Equal("2026_0710 - Drone Print V90 Rev08", s);
    }

    [Fact]
    public void SanitizeStem_StripsPointLoaderHostileChars_KeepsLayout()
    {
        var s = RobotKrlPaths.SanitizeStem(
            "2026_0710 - Drone Print (V90) Rev08 — Final #1 @site!");
        Assert.Equal("2026_0710 - Drone Print V90 Rev08 - Final 1 site", s);
        Assert.DoesNotContain("(", s);
        Assert.DoesNotContain("#", s);
        Assert.DoesNotContain("@", s);
        Assert.DoesNotContain("—", s);
    }

    [Fact]
    public void SuggestedFileName_PreservesExistingDatePrefix()
    {
        var s = RobotKrlPaths.SuggestedFileName("2026_0710 - Drone Print V90");
        Assert.Equal("2026_0710 - Drone Print V90", s);
        Assert.DoesNotContain("2026_0710 - 2026_", s); // no double date
    }

    [Fact]
    public void SuggestedSrcFileName_AddsRevAndExtension()
    {
        var s = RobotKrlPaths.SuggestedSrcFileName("2026_0710 - Drone Print V90", rev: 8);
        Assert.Equal("2026_0710 - Drone Print V90 Rev08.src", s);
    }

    [Fact]
    public void SuggestedFileName_AddsDateWhenMissing()
    {
        var s = RobotKrlPaths.SuggestedFileName("Drone Print V90");
        Assert.Matches(@"^\d{4}_\d{4} - Drone Print V90$", s);
    }
}
