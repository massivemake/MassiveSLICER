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
}