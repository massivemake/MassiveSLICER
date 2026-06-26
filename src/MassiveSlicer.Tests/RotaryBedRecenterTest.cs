using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public class RotaryBedRecenterTest
{
    // When the bed is recentred (Auto Bed Calibration apply), the rotary turntable mesh must
    // stay centred on the circular origin grid. The cell invariant is robroot + basePos ==
    // bed.origin, so the apply path writes basePos = (newCentre - robroot) alongside the new
    // origin. This guards that math end-to-end through the CellLoader round-trip.
    [Fact]
    public void Recentring_keeps_rotary_basePos_aligned_with_bed_origin()
    {
        var dir     = Path.Combine(Path.GetTempPath(), "mslicer-rotrecenter-" + Guid.NewGuid().ToString("N"));
        var cellDir = Path.Combine(dir, "assets", "cells", "LFAM3");
        Directory.CreateDirectory(cellDir);
        var path = Path.Combine(cellDir, "lfam3.json");

        // robroot.z = 1000; origin.z 900.08 -> basePos.z -99.92, matching the real LFAM3 cell.
        File.WriteAllText(path, """
            {
              "name": "LFAM 3",
              "robot": { "modelPath": "robot.glb", "worldPosition": { "x": 0, "y": 0, "z": 1000 }, "joints": [] },
              "bed": {
                "origin": { "x": 2007.07, "y": 10.98, "z": 900.08 },
                "gridOrigin": { "x": 1107.07, "y": -889.02, "z": 900.08 },
                "width": 1800, "depth": 1800,
                "baseData": { "x": 2007.07, "y": 10.98, "z": -99.92 },
                "diameter": 1828.8, "rotationSign": -1
              },
              "rotaryBed": {
                "bottomPath": "rotary_bed_bottom.glb",
                "topPath": "rotary_bed_top.glb",
                "basePos": [ 2007.07, 10.98, -99.92 ],
                "baseAbc": [ 0, 0, -90 ],
                "e1Sign": 1
              },
              "tools": []
            }
            """);

        var start = CellLoader.Load(path);
        var rw0   = start.Robot.WorldPosition;
        // Precondition: the cell ships aligned (robroot + basePos == origin).
        Assert.Equal(start.Bed.Origin.X, rw0.X + start.RotaryBed!.BasePos[0], 3);
        Assert.Equal(start.Bed.Origin.Y, rw0.Y + start.RotaryBed!.BasePos[1], 3);
        Assert.Equal(start.Bed.Origin.Z, rw0.Z + start.RotaryBed!.BasePos[2], 3);

        // --- simulate the apply path: a calibration finds a slightly shifted centre ---
        float nx = 2050.5f, ny = -25.25f, nz = 905.0f;
        CellLoader.SaveBedCenter(path, nx, ny, nz, null, null);

        var mid = CellLoader.Load(path);
        var rw  = mid.Robot.WorldPosition;
        float[] basePos = [ nx - rw.X, ny - rw.Y, nz - rw.Z ];
        Assert.True(CellLoader.SaveRotaryBedTransform(path, basePos, mid.RotaryBed!.BaseAbc, out var err), err);

        // --- the turntable centre must equal the new grid origin, baseAbc preserved ---
        var after = CellLoader.Load(path);
        Assert.Equal(nx, after.Bed.Origin.X, 3);
        Assert.Equal(ny, after.Bed.Origin.Y, 3);
        Assert.Equal(nz, after.Bed.Origin.Z, 3);

        Assert.Equal(after.Bed.Origin.X, rw.X + after.RotaryBed!.BasePos[0], 3);
        Assert.Equal(after.Bed.Origin.Y, rw.Y + after.RotaryBed!.BasePos[1], 3);
        Assert.Equal(after.Bed.Origin.Z, rw.Z + after.RotaryBed!.BasePos[2], 3);

        Assert.Equal(-90f, after.RotaryBed!.BaseAbc[2], 3);   // tilt preserved
    }
}
