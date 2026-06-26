using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public class CellPathsTest
{
    [Fact]
    public void WriteTargetsFor_includes_repo_mirror_for_publish_copy()
    {
        var publish = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "mslicer-publish", "assets", "cells", "LFAM3", "lfam3.json"));
        var rel = CellPaths.RelativeUnderCells(publish);
        Assert.Equal($"LFAM3{Path.DirectorySeparatorChar}lfam3.json", rel);
    }
}