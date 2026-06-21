using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class CellDevTransformSaveTest
{
    [Fact]
    public void SaveBedDevTransform_shifts_origin_and_grid_together_on_LFAM3()
    {
        var dir  = Path.Combine(Path.GetTempPath(), "mslicer-devsave-" + Guid.NewGuid().ToString("N"));
        var cellDir = Path.Combine(dir, "assets", "cells", "LFAM3");
        Directory.CreateDirectory(cellDir);
        var path = Path.Combine(cellDir, "lfam3.json");
        File.WriteAllText(path, """
            {
              "name": "LFAM 3",
              "robot": { "modelPath": "robot.glb", "worldPosition": { "x": 0, "y": 0, "z": 1000 }, "joints": [] },
              "bed": {
                "origin": { "x": 100, "y": 200, "z": 300 },
                "gridOrigin": { "x": 0, "y": 0, "z": 300 },
                "width": 1800,
                "depth": 1800,
                "baseData": { "x": 100, "y": 200, "z": -700 }
              },
              "tools": []
            }
            """);

        Directory.SetCurrentDirectory(dir);
        Assert.True(CellLoader.SaveBedDevTransform(path, 150, 250, 350, out var error), error);

        var cell = CellLoader.Load(path);
        Assert.Equal(150f, cell.Bed.Origin.X);
        Assert.Equal(250f, cell.Bed.Origin.Y);
        Assert.Equal(350f, cell.Bed.Origin.Z);
        Assert.Equal(50f, cell.Bed.GridOrigin!.Value.X);
        Assert.Equal(50f, cell.Bed.GridOrigin!.Value.Y);
        Assert.Equal(350f, cell.Bed.GridOrigin!.Value.Z);
        Assert.Equal(100f, cell.Bed.BaseData.X);
    }
}