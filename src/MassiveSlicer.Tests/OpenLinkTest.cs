using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

/// <summary>
/// Lab's "Open in MassiveSLICER" for a model: path=&lt;stl&gt;&amp;saveTo=&lt;new .mass&gt;&amp;project&amp;element.
/// Spec: docs/ERP-OpenInSlicer-Replit-Prompt.md (Part 2).
/// </summary>
public sealed class OpenLinkTest
{
    static readonly string[] MacRoots = ["/Volumes/MassiveFILES"];
    static readonly string[] WinRoots = [@"Z:\"];
    static bool None(string _) => false;

    const string StlLink =
        "massiveslicer://open?path=Projects%2F26-284%20Head%2F03-Design%2Fpart.stl" +
        "&saveTo=Projects%2F26-284%20Head%2F06-Production%20Documents%2Fpart.mass&project=42&element=99";

    [Fact]
    public void Model_link_carries_save_target_and_lab_ids()
    {
        var link = OpenLink.Parse(StlLink)!;
        Assert.True(link.IsModel);
        Assert.Equal("Projects/26-284 Head/03-Design/part.stl", link.Path);
        Assert.Equal("Projects/26-284 Head/06-Production Documents/part.mass", link.SaveTo);
        Assert.Equal("42", link.Project);
        Assert.Equal("99", link.Element);
    }

    [Fact]
    public void Model_link_maps_both_paths_per_os()
    {
        var mac = OpenLink.Parse(StlLink)!.ToLocal(MacRoots, None)!;
        Assert.Equal("/Volumes/MassiveFILES/Projects/26-284 Head/03-Design/part.stl", mac.Path);
        Assert.Equal("/Volumes/MassiveFILES/Projects/26-284 Head/06-Production Documents/part.mass", mac.SaveTo);

        var win = OpenLink.Parse(StlLink)!.ToLocal(WinRoots, None)!;
        Assert.Equal(@"Z:\Projects\26-284 Head\06-Production Documents\part.mass", win.SaveTo);
        Assert.Equal("42", win.Project);
    }

    [Fact]
    public void Url_search_params_encoding_from_lab_parses()
    {
        // JS URLSearchParams writes spaces as '+'; the spec's example converts them, but accept both.
        var link = OpenLink.Parse(
            "massiveslicer://open?path=Projects%2Fa+b%2Fm.stl&saveTo=Projects%2Fa+b%2Fm.mass")!;
        Assert.Equal("Projects/a b/m.stl", link.Path);
        Assert.Equal("Projects/a b/m.mass", link.SaveTo);
    }

    [Theory]
    [InlineData(".obj")]
    [InlineData(".3mf")]
    [InlineData(".glb")]
    [InlineData(".STEP")]
    public void Other_model_types_work_the_same(string ext)
        => Assert.NotNull(OpenLink.Parse($"massiveslicer://open?path=Projects%2Fa{ext}&saveTo=Projects%2Fa.mass"));

    [Theory]
    // Model without saveTo — spec says required.
    [InlineData("massiveslicer://open?path=Projects%2Fa.stl")]
    // saveTo must be a .mass.
    [InlineData("massiveslicer://open?path=Projects%2Fa.stl&saveTo=Projects%2Fa.stl")]
    // Two paths is ambiguous.
    [InlineData("massiveslicer://open?path=Projects%2Fa.stl&path=Projects%2Fb.stl&saveTo=Projects%2Fa.mass")]
    [InlineData("massiveslicer://open?path=Projects%2Fa.stl&saveTo=Projects%2Fa.mass&saveTo=Projects%2Fb.mass")]
    // Not a model or project.
    [InlineData("massiveslicer://open?path=Projects%2Fa.exe&saveTo=Projects%2Fa.mass")]
    [InlineData("massiveslicer://open?path=Projects%2Fprint.gcode.3mf&saveTo=Projects%2Fa.mass")]
    [InlineData("massiveslicer://open")]
    [InlineData("https://example.com/?path=Projects%2Fa.mass")]
    public void Malformed_links_are_refused(string raw) => Assert.Null(OpenLink.Parse(raw));

    [Theory]
    [InlineData("massiveslicer://open?path=Projects%2Fa.stl&saveTo=%2FUsers%2Fderek%2Fa.mass")]
    [InlineData("massiveslicer://open?path=%2FUsers%2Fderek%2Fa.stl&saveTo=Projects%2Fa.mass")]
    [InlineData("massiveslicer://open?path=Projects%2Fa.stl&saveTo=Projects%2F..%2F..%2Fa.mass")]
    public void Either_path_off_the_share_is_refused(string raw)
        => Assert.Null(OpenLink.Parse(raw)!.ToLocal(MacRoots, None));

    [Fact]
    public void Mass_links_still_open_as_before_and_ignore_unknown_params()
    {
        var link = OpenLink.Parse("massiveslicer://open?path=Projects%2Fa.mass&utm=lab")!;
        Assert.False(link.IsModel);
        Assert.Null(link.SaveTo);
        Assert.Equal("/Volumes/MassiveFILES/Projects/a.mass", link.ToLocal(MacRoots, None)!.Path);
    }

    [Fact]
    public void Args_take_a_link_or_a_double_clicked_mass()
    {
        Assert.True(OpenLink.FromArgs([StlLink], MacRoots, None)!.IsModel);
        Assert.Equal("/Users/derek/a.mass", OpenLink.FromArgs(["/Users/derek/a.mass"], MacRoots, None)!.Path);
        // A double-clicked STL is not a launch request (use Import).
        Assert.Null(OpenLink.FromArgs(["/Users/derek/a.stl"], MacRoots, None));
    }
}
