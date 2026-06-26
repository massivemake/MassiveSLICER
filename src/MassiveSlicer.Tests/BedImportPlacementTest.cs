using MassiveSlicer.App;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;
using MeshData = MassiveSlicer.Viewport.Scene.MeshData;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class BedImportPlacementTest
{
    [Fact]
    public void Lfam3_ImportSurfaceCenter_Is_RotaryBed_Origin()
    {
        var path = Path.Combine("assets", "cells", "LFAM3", "lfam3.json");
        var cell = CellLoader.Load(path);
        var center = cell.Bed.ImportSurfaceCenter(cell.Robot.WorldPosition);

        Assert.True(cell.Bed.IsRotaryPrintBed);
        Assert.Equal(cell.Bed.Origin.X, center.X, 2);
        Assert.Equal(cell.Bed.Origin.Y, center.Y, 2);
        Assert.Equal(cell.Bed.Origin.Z, center.Z, 2);
        Assert.NotNull(cell.Bed.ImportSurfaceRadiusMm);
    }

    [Fact]
    public void Lfam2_ImportSurfaceCenter_Is_PrintBed_Grid_Centre()
    {
        var path = Path.Combine("assets", "cells", "LFAM2", "lfam2.json");
        var cell = CellLoader.Load(path);
        var corner = cell.Bed.VisualGridCorner(cell.Robot.WorldPosition);
        var expected = new Float3(
            corner.X + cell.Bed.Width / 2f,
            corner.Y + cell.Bed.Depth / 2f,
            corner.Z);
        var center = cell.Bed.ImportSurfaceCenter(cell.Robot.WorldPosition);

        Assert.False(cell.Bed.IsRotaryPrintBed);
        Assert.Equal(expected.X, center.X, 1);
        Assert.Equal(expected.Y, center.Y, 1);
        Assert.Equal(expected.Z, center.Z, 1);
        Assert.Null(cell.Bed.ImportSurfaceRadiusMm);
    }

    [Fact]
    public void PlaceOnBed_Lfam3_Centres_On_Rotary_Surface_And_Scales_To_Diameter()
    {
        var path = Path.Combine("assets", "cells", "LFAM3", "lfam3.json");
        var cell = CellLoader.Load(path);
        var bed = cell.Bed;
        float r = bed.ImportSurfaceRadiusMm!.Value * 0.96f;

        var mesh = BoxMesh(2000, 2000, 100, 0, 0, 50);
        var node = new SceneNode { Name = "big-part", PendingMesh = mesh };

        ImportHelper.PlaceOnBed(node, cell);

        var (min, max) = ImportHelper.ComputeSubtreeAabb(node);
        var c = (min + max) * 0.5f;
        Assert.Equal(bed.Origin.X, c.X, 0);
        Assert.Equal(bed.Origin.Y, c.Y, 0);
        Assert.Equal(bed.Origin.Z, min.Z, 0);
        Assert.True(MathF.Max(max.X - c.X, max.Y - c.Y) <= r + 0.5f);
    }

    [Fact]
    public void PlaceOnBed_Lfam2_Sits_On_Print_Bed_Grid()
    {
        var path = Path.Combine("assets", "cells", "LFAM2", "lfam2.json");
        var cell = CellLoader.Load(path);
        var target = cell.Bed.ImportSurfaceCenter(cell.Robot.WorldPosition);

        var mesh = BoxMesh(200, 200, 80, 0, 0, 40);
        var node = new SceneNode { Name = "part", PendingMesh = mesh };

        ImportHelper.PlaceOnBed(node, cell);

        var (min, max) = ImportHelper.ComputeSubtreeAabb(node);
        var c = (min + max) * 0.5f;
        Assert.Equal(target.X, c.X, 0);
        Assert.Equal(target.Y, c.Y, 0);
        Assert.Equal(target.Z, min.Z, 0);
    }

    private static MeshData BoxMesh(float sx, float sy, float sz, float cx, float cy, float cz)
    {
        float hx = sx * 0.5f, hy = sy * 0.5f, hz = sz * 0.5f;
        var verts = new[]
        {
            new Vector3(cx - hx, cy - hy, cz - hz), new Vector3(cx + hx, cy - hy, cz - hz),
            new Vector3(cx + hx, cy + hy, cz - hz), new Vector3(cx - hx, cy + hy, cz - hz),
            new Vector3(cx - hx, cy - hy, cz + hz), new Vector3(cx + hx, cy - hy, cz + hz),
            new Vector3(cx + hx, cy + hy, cz + hz), new Vector3(cx - hx, cy + hy, cz + hz),
        };
        uint[] idx =
        [
            0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6,
            0, 4, 5, 0, 5, 1,
            2, 6, 7, 2, 7, 3,
            0, 3, 7, 0, 7, 4,
            1, 5, 6, 1, 6, 2,
        ];
        return new MeshData(verts, verts.Select(_ => Vector3.UnitZ).ToArray(), idx, "box");
    }
}