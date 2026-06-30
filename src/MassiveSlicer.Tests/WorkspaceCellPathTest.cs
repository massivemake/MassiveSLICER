using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public sealed class WorkspaceCellPathTest
{
    [Fact]
    public void NormalizeForSave_stores_relative_path_under_assets_cells()
    {
        var saved = WorkspaceCellPath.NormalizeForSave(
            @"C:\MassiveSlicer\build\assets\cells\lfam2.json");

        Assert.Equal("lfam2.json", saved);
    }

    [Fact]
    public void Resolve_finds_cell_by_filename_when_absolute_path_differs()
    {
        var discovered = new[]
        {
            @"D:\publish\assets\cells\lfam3.json",
        };

        var resolved = WorkspaceCellPath.Resolve(
            @"C:\old\install\assets\cells\lfam3.json",
            discovered);

        Assert.Equal(Path.GetFullPath(discovered[0]), resolved);
    }

    [Fact]
    public void Resolve_prefers_discovered_install_over_network_absolute_path()
    {
        var discovered = new[]
        {
            @"C:\MassiveSlicer\build\assets\cells\LFAM1\lfam1.json",
        };

        var resolved = WorkspaceCellPath.Resolve(
            @"\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2\assets\cells\LFAM1\lfam1.json",
            discovered);

        Assert.Equal(Path.GetFullPath(discovered[0]), resolved);
    }

    [Fact]
    public void Matches_accepts_same_cell_by_filename_when_paths_differ()
    {
        var discovered = new[] { @"C:\build\assets\cells\LFAM1\lfam1.json" };
        Assert.True(WorkspaceCellPath.Matches(
            @"\\server\repo\assets\cells\LFAM1\lfam1.json",
            @"C:\build\assets\cells\LFAM1\lfam1.json",
            discovered));
    }

    [Fact]
    public void Resolve_finds_relative_saved_path_against_discovered_cells_dir()
    {
        var cellsDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "assets", "cells");
        Directory.CreateDirectory(cellsDir);
        var cellFile = Path.Combine(cellsDir, "lfam2.json");
        File.WriteAllText(cellFile, "{}");

        try
        {
            var resolved = WorkspaceCellPath.Resolve("lfam2.json", [cellFile]);
            Assert.Equal(Path.GetFullPath(cellFile), resolved);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(cellsDir)!, recursive: true);
        }
    }
}