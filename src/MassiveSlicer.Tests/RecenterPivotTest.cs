using MassiveSlicer.App;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class RecenterPivotTest
{
    [Fact]
    public void RecenterPivotToBottomCenter_ParentedUnderBed_PreservesWorldBounds()
    {
        var bed = new SceneNode { LocalTransform = Matrix4.CreateTranslation(1433.83f, -1377.36f, 130f) };
        var root = new SceneNode { Name = "Part", LocalTransform = Matrix4.CreateTranslation(120f, -80f, 25f) };
        var meshNode = new SceneNode { Name = "Mesh" };
        meshNode.PendingMesh = BoxMesh(-40f, -30f, 10f, 40f, 30f, 90f);
        root.AddChild(meshNode);
        bed.AddChild(root);

        var (wMinBefore, wMaxBefore) = WorldAabb(root);

        Assert.True(ImportHelper.RecenterPivotToBottomCenter(root));

        var (wMinAfter, wMaxAfter) = WorldAabb(root);

        Assert.Equal(wMinBefore.X, wMinAfter.X, 0.05f);
        Assert.Equal(wMinBefore.Y, wMinAfter.Y, 0.05f);
        Assert.Equal(wMinBefore.Z, wMinAfter.Z, 0.05f);
        Assert.Equal(wMaxBefore.X, wMaxAfter.X, 0.05f);
        Assert.Equal(wMaxBefore.Y, wMaxAfter.Y, 0.05f);
        Assert.Equal(wMaxBefore.Z, wMaxAfter.Z, 0.05f);
    }

    [Fact]
    public void RecenterPivotToBottomCenter_returns_false_when_no_editable_mesh()
    {
        var root = new SceneNode { Name = "Empty" };
        Assert.False(ImportHelper.RecenterPivotToBottomCenter(root));
    }

    [Fact]
    public void RecenterPivotToBottomCenter_moves_origin_without_shifting_world_bounds()
    {
        var root = new SceneNode { Name = "Part", LocalTransform = Matrix4.CreateTranslation(100f, 200f, 50f) };
        var meshNode = new SceneNode { Name = "Mesh" };
        meshNode.PendingMesh = BoxMesh(-40f, -30f, 10f, 40f, 30f, 90f);
        root.AddChild(meshNode);

        var (wMinBefore, wMaxBefore) = WorldAabb(root);

        Assert.True(ImportHelper.RecenterPivotToBottomCenter(root));

        var (wMinAfter, wMaxAfter) = WorldAabb(root);

        Assert.Equal(wMinBefore.X, wMinAfter.X, 0.05f);
        Assert.Equal(wMinBefore.Y, wMinAfter.Y, 0.05f);
        Assert.Equal(wMinBefore.Z, wMinAfter.Z, 0.05f);
        Assert.Equal(wMaxBefore.X, wMaxAfter.X, 0.05f);
        Assert.Equal(wMaxBefore.Y, wMaxAfter.Y, 0.05f);
        Assert.Equal(wMaxBefore.Z, wMaxAfter.Z, 0.05f);

        var pivot = root.WorldTransform.Row3.Xyz;
        var bcWorld = new Vector3(
            (wMinAfter.X + wMaxAfter.X) * 0.5f,
            (wMinAfter.Y + wMaxAfter.Y) * 0.5f,
            wMinAfter.Z);
        Assert.Equal(bcWorld.X, pivot.X, 0.05f);
        Assert.Equal(bcWorld.Y, pivot.Y, 0.05f);
        Assert.Equal(bcWorld.Z, pivot.Z, 0.05f);
    }

    private static (Vector3 Min, Vector3 Max) WorldAabb(SceneNode root)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;
            var w = n.WorldTransform;
            foreach (var p in mesh.Positions)
            {
                var pt = new Vector3(
                    p.X * w.M11 + p.Y * w.M21 + p.Z * w.M31 + w.M41,
                    p.X * w.M12 + p.Y * w.M22 + p.Z * w.M32 + w.M42,
                    p.X * w.M13 + p.Y * w.M23 + p.Z * w.M33 + w.M43);
                min = Vector3.ComponentMin(min, pt);
                max = Vector3.ComponentMax(max, pt);
            }
        }
        return (min, max);
    }

    private static MeshData BoxMesh(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var positions = new[]
        {
            new Vector3(x0, y0, z0), new Vector3(x1, y0, z0),
            new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
            new Vector3(x0, y0, z1), new Vector3(x1, y0, z1),
            new Vector3(x1, y1, z1), new Vector3(x0, y1, z1),
        };
        uint[] indices =
        [
            0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6,
            0, 4, 5, 0, 5, 1,
            1, 5, 6, 1, 6, 2,
            2, 6, 7, 2, 7, 3,
            3, 7, 4, 3, 4, 0,
        ];
        return new MeshData(positions, positions, indices, "box");
    }
}