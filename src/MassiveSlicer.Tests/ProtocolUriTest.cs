using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public sealed class ProtocolUriTest
{
    [Theory]
    [InlineData(@"massiveslicer://open?path=Z%3A%5CProjects%5C26-284%20Head%5Cmass%20Files%5Cpart.mass",
        @"Z:\Projects\26-284 Head\mass Files\part.mass")]
    [InlineData(@"massiveslicer:open?path=Z%3A%5CProjects%5Ca%5Cb.mass",
        @"Z:\Projects\a\b.mass")]
    [InlineData(@"""Z:\Projects\26-284\file.mass""",
        @"Z:\Projects\26-284\file.mass")]
    public void Resolves_protocol_and_quoted_mass_paths(string input, string expected)
        => Assert.Equal(expected, ProtocolUri.ResolveWorkspacePath(input));

    [Fact]
    public void Ignores_non_mass_and_empty()
    {
        Assert.Null(ProtocolUri.ResolveWorkspacePath(@"Z:\Research\LFAM\MassiveSLICER"));
        Assert.Null(ProtocolUri.ResolveWorkspacePath("massiveslicer://open?path=C%3A%5CWindows"));
        Assert.Null(ProtocolUri.ResolveWorkspacePath(""));
        Assert.Null(ProtocolUri.ResolveWorkspacePath(Array.Empty<string>()));
    }

    [Fact]
    public void Picks_first_mass_arg()
    {
        Assert.Equal(
            @"Z:\Projects\a.mass",
            ProtocolUri.ResolveWorkspacePath(new[] { "--foo", @"Z:\Projects\a.mass", @"Z:\Projects\b.mass" }));
    }
}
