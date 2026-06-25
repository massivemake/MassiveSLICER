using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public class RotaryOrientationOffsetTest
{
    // The bed orientation offset must persist to the cell JSON and round-trip, defaulting to 0.
    [Fact]
    public void SaveRotaryOrientation_persists_and_round_trips()
    {
        var dir     = Path.Combine(Path.GetTempPath(), "mslicer-orient-" + Guid.NewGuid().ToString("N"));
        var cellDir = Path.Combine(dir, "assets", "cells", "LFAM3");
        Directory.CreateDirectory(cellDir);
        var path = Path.Combine(cellDir, "lfam3.json");
        File.WriteAllText(path, """
            {
              "name": "LFAM 3",
              "robot": { "modelPath": "robot.glb", "worldPosition": { "x": 0, "y": 0, "z": 1000 }, "joints": [] },
              "bed": { "origin": { "x": 2002, "y": 1, "z": 336 }, "width": 1800, "depth": 1800,
                       "baseData": { "x": 2002, "y": 1, "z": -664 } },
              "rotaryBed": {
                "bottomPath": "rotary_bed_bottom.glb",
                "topPath": "rotary_bed_top.glb",
                "basePos": [ 2002, 1, -664 ], "baseAbc": [ 0, 0, -90 ], "e1Sign": -1
              },
              "tools": []
            }
            """);

        Assert.Equal(0f, CellLoader.Load(path).RotaryBed!.OrientationOffsetDeg);   // default

        Assert.True(CellLoader.SaveRotaryOrientation(path, -0.93f, out var err), err);

        var after = CellLoader.Load(path);
        Assert.Equal(-0.93f, after.RotaryBed!.OrientationOffsetDeg, 3);
        // unrelated fields preserved
        Assert.Equal(-1f, after.RotaryBed!.E1Sign);
        Assert.Equal(2002, after.Bed.Origin.X);
    }
}
