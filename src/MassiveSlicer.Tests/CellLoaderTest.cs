using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class CellLoaderTest
{
    [Fact]
    public void Lfam2_Loads_VisualOffset_And_Separates_Marker_From_Grid()
    {
        // bin/Release/net8.0 -> Tests -> src -> repo root
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var path     = Path.Combine(repoRoot, "assets", "cells", "LFAM2", "lfam2.json");
        Assert.True(File.Exists(path), $"Missing cell file: {path}");

        var cell = CellLoader.Load(path);
        var bed  = cell.Bed;

        Assert.NotNull(bed.VisualOffset);
        Assert.Equal(-127.8f, bed.VisualOffset!.Value.X, 2);
        Assert.Equal(-103.6f, bed.VisualOffset!.Value.Y, 2);

        var rp     = cell.Robot.WorldPosition;
        var marker = bed.BaseMarkerWorld(rp);
        var grid   = bed.VisualGridCorner(rp);

        Assert.Equal(1433.829f, marker.X, 2);
        Assert.Equal(-1377.359f, marker.Y, 2);
        Assert.Equal(1306.029f, grid.X, 2);
        Assert.Equal(-1480.959f, grid.Y, 2);
        Assert.True(bed.HasVisualShift);
    }

    [Fact]
    public void Lfam1_bed_and_tool_match_controller_config_dat()
    {
        var path = ResolveCellJson("LFAM1", "lfam1.json");
        if (path is null) return;

        var cell = CellLoader.Load(path);
        var bed  = cell.Bed;
        var tool = cell.EffectiveTools.Single(t => t.KrlIndex == 1);

        Assert.Null(bed.VisualOffset);
        Assert.Equal(1496.36047f, bed.BaseData.X, 3);
        Assert.Equal(-577.892273f, bed.BaseData.Y, 3);
        Assert.Equal(278f, bed.BaseData.Z, 2);
        Assert.True(bed.Origin.Z > 0f);

        Assert.Equal(901.2f, tool.TcpX, 2);
        Assert.Equal(-165f, tool.TcpY, 2);
        Assert.Equal(249.99f, tool.TcpZ, 2);

        Assert.Contains(cell.KrlBases, b => b.Name == "massiveb1" && b.Index == 1);
        Assert.Contains("CREHF_Extruder", tool.ModelPath, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(cell.RobotRail);
        Assert.Equal(-4650f, cell.RobotRail!.MinMm);
        Assert.Equal(150f, cell.RobotRail.MaxMm);
    }

    private static string? ResolveCellJson(string folder, string file)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "assets", "cells", folder, file),
            Path.Combine(FindRepoRoot() ?? "", "assets", "cells", folder, file),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "assets", "cells")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
            if (string.IsNullOrEmpty(dir)) break;
        }

        return null;
    }
}