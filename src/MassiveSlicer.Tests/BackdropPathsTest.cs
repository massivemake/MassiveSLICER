using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public class BackdropPathsTest
{
    [Fact]
    public void EnumerateBackdropHdrPaths_finds_repo_images()
    {
        var paths = AssetPaths.EnumerateBackdropHdrPaths();
        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.EndsWith(".hdr", p, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains("assets/Images", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BackdropPathsEqual_matches_relative_and_resolved()
    {
        var relative = "assets/Images/AmbienceExposure4k.hdr";
        if (!AssetPaths.Exists(relative))
            return;

        var resolved = AssetPaths.Resolve(relative);
        Assert.True(AssetPaths.BackdropPathsEqual(relative, resolved));
        Assert.True(AssetPaths.BackdropPathsEqual(relative, Path.GetFileName(relative)));
    }
}