using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

/// <summary>
/// MassiveLAB's "Open in MassiveSLICER" sends massiveslicer://open?path=Projects/…/part.mass.
/// One link must open the same NAS file on a Windows shop PC and on a Mac.
/// </summary>
public sealed class OpenInMassiveSlicerLinkTest
{
    static readonly string[] WinRoots = [@"Z:\", @"\\192.168.0.191\MassiveFILES"];
    static readonly string[] MacRoots = ["/Volumes/MassiveFILES", "/Volumes/MassiveFILES-1"];
    static bool None(string _) => false;

    [Theory]
    [InlineData("Projects/26-284 Head/mass Files/part.mass")]
    [InlineData(@"Z:\Projects\26-284 Head\mass Files\part.mass")]
    [InlineData("/Volumes/MassiveFILES/Projects/26-284 Head/mass Files/part.mass")]
    [InlineData(@"\\192.168.0.191\MassiveFILES\Projects\26-284 Head\mass Files\part.mass")]
    public void Any_share_form_maps_to_each_os(string linkPath)
    {
        Assert.Equal(@"Z:\Projects\26-284 Head\mass Files\part.mass",
            UnasPaths.ToLocalPath(linkPath, WinRoots, None));
        Assert.Equal("/Volumes/MassiveFILES/Projects/26-284 Head/mass Files/part.mass",
            UnasPaths.ToLocalPath(linkPath, MacRoots, None));
    }

    [Fact]
    public void Picks_the_mount_where_the_file_exists()
    {
        // VPN Mac that mounted the share a second time as MassiveFILES-1.
        string hit = "/Volumes/MassiveFILES-1/Projects/a/b.mass";
        Assert.Equal(hit, UnasPaths.ToLocalPath("Projects/a/b.mass", MacRoots, p => p == hit));

        // Windows PC without Z: mapped falls through to the UNC share.
        string unc = @"\\192.168.0.191\MassiveFILES\Projects\a\b.mass";
        Assert.Equal(unc, UnasPaths.ToLocalPath("Projects/a/b.mass", WinRoots, p => p == unc));
    }

    [Theory]
    [InlineData("Projects/../../etc/passwd.mass")]
    [InlineData("Projects/./a.mass")]
    [InlineData("/Users/derek/Desktop/a.mass")]
    [InlineData(@"C:\Users\derek\Desktop\a.mass")]
    [InlineData("")]
    public void Refuses_paths_off_the_share(string linkPath)
        => Assert.Null(UnasPaths.ToLocalPath(linkPath, MacRoots, None));

    [Fact]
    public void Link_is_mapped_but_a_double_clicked_file_is_left_alone()
    {
        string link = "massiveslicer://open?path=Projects%2FTest%20Project%2Fpart.mass";
        Assert.Equal("/Volumes/MassiveFILES/Projects/Test Project/part.mass",
            ProtocolUri.ResolveLocalWorkspacePath([link], MacRoots, None));

        // A link to a local file is refused; the same file double-clicked still opens.
        Assert.Null(ProtocolUri.ResolveLocalWorkspacePath(
            ["massiveslicer://open?path=%2FUsers%2Fderek%2Fa.mass"], MacRoots, None));
        Assert.Equal("/Users/derek/a.mass",
            ProtocolUri.ResolveLocalWorkspacePath(["/Users/derek/a.mass"], MacRoots, None));
    }

    [Theory]
    [InlineData("/Volumes/MassiveFILES/Projects", "/Volumes/MassiveFILES")]
    [InlineData("/Volumes/MassiveFILES-1/Projects/", "/Volumes/MassiveFILES-1")]
    [InlineData(@"Y:\Projects", @"Y:\")]
    public void Preference_names_the_share_root(string pref, string expectedFirst)
        => Assert.Equal(expectedFirst, UnasPaths.LocalShareRoots(pref)[0]);
}
