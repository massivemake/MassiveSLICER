using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class BedGridSizeSaveTest
{
    [Fact]
    public void SaveBedGridSize_updates_width_and_depth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mslicer-bedgrid-" + Guid.NewGuid().ToString("N"));
        var cellDir = Path.Combine(dir, "assets", "cells", "LFAM1");
        Directory.CreateDirectory(cellDir);
        var path = Path.Combine(cellDir, "lfam1.json");
        File.WriteAllText(path, """
            {
              "name": "LFAM 1",
              "robot": { "modelPath": "robot.glb", "worldPosition": { "x": 0, "y": 0, "z": 0 }, "joints": [] },
              "bed": {
                "origin": { "x": 100, "y": 200, "z": 300 },
                "width": 1200,
                "depth": 800,
                "baseData": { "x": 100, "y": 200, "z": 300 }
              },
              "tools": []
            }
            """);

        Assert.True(CellLoader.SaveBedGridSize(path, 1500, 950, out var error), error);

        var cell = CellLoader.Load(path);
        Assert.Equal(1500f, cell.Bed.Width);
        Assert.Equal(950f, cell.Bed.Depth);
        Assert.Equal(100f, cell.Bed.Origin.X);
    }

    [Fact]
    public void SaveBedGridSize_rejects_non_positive_dimensions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mslicer-bedgrid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cell.json");
        File.WriteAllText(path, """
            {
              "name": "Test",
              "robot": { "modelPath": "robot.glb", "worldPosition": { "x": 0, "y": 0, "z": 0 }, "joints": [] },
              "bed": {
                "origin": { "x": 0, "y": 0, "z": 0 },
                "width": 1000,
                "depth": 1000,
                "baseData": { "x": 0, "y": 0, "z": 0 }
              },
              "tools": []
            }
            """);

        Assert.False(CellLoader.SaveBedGridSize(path, 0, 1000, out var error));
        Assert.NotNull(error);
    }
}