using MassiveSlicer.App;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// A GLB root carries the Y-up metres → Z-up millimetres transform. Ungroup promotes
/// the children to their own objects, and those children have to keep that size.
/// </summary>
public class UngroupGlbScaleTest
{
    [Fact]
    public void Ungroup_keeps_a_glb_part_at_its_millimetre_size()
    {
        var root = new SceneNode { Name = "file.glb", LocalTransform = GltfLoader.GltfToScene };
        var part = new SceneNode { Name = "part", LocalTransform = Matrix4.Identity };
        root.AddChild(part);

        // 1 metre along the file's X is 1000 mm in the scene.
        var local = new Vector3(1f, 0f, 0f);
        var before = Vector3.TransformPosition(local, part.WorldTransform);
        Assert.True(before.Length > 100f, $"imported part should already be millimetres, got {before}");

        part.DetachKeepingWorld();

        var after = Vector3.TransformPosition(local, part.WorldTransform);
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
        Assert.Equal(before.Z, after.Z, 3);
        Assert.Null(part.Parent);
    }

    [Fact]
    public void Ungroup_keeps_a_nested_part_and_the_rotary_bed()
    {
        var pivot = new SceneNode
        {
            Name = "rotary",
            LocalTransform = Matrix4.CreateRotationZ(0.4f) * Matrix4.CreateTranslation(100f, 200f, 5f),
        };
        var root = new SceneNode { Name = "file.glb", LocalTransform = GltfLoader.GltfToScene };
        var child = new SceneNode { Name = "assembly", LocalTransform = Matrix4.CreateTranslation(0.25f, 0f, 0f) };
        var grand = new SceneNode { Name = "part", LocalTransform = Matrix4.Identity };
        child.AddChild(grand);
        root.AddChild(child);
        pivot.AddChild(root);

        var local = new Vector3(0.1f, 0.2f, 0.3f);
        var before = Vector3.TransformPosition(local, grand.WorldTransform);

        child.DetachKeepingWorld();
        // Same conversion PendingRotaryNodes applies when the part is parented to the turntable.
        child.LocalTransform = child.LocalTransform * pivot.WorldTransform.Inverted();
        pivot.AddChild(child);

        var after = Vector3.TransformPosition(local, grand.WorldTransform);
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
        Assert.Equal(before.Z, after.Z, 3);
    }

    [Fact]
    public void Ungroup_recenters_each_part_off_the_group_origin()
    {
        var root = new SceneNode { Name = "file.glb", LocalTransform = GltfLoader.GltfToScene };
        var part = new SceneNode
        {
            Name = "lug",
            LocalTransform = Matrix4.Identity,
            PendingMesh = Box(new Vector3(1f, 0f, 0f), new Vector3(1.2f, 0.2f, 0.2f)),
        };
        root.AddChild(part);

        var corner = new Vector3(1f, 0f, 0f);
        var before = Vector3.TransformPosition(corner, part.WorldTransform);

        part.DetachKeepingWorld();
        ImportHelper.RecenterPivot(part);

        var after = Vector3.TransformPosition(corner, part.WorldTransform);
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
        Assert.Equal(before.Z, after.Z, 3);

        Assert.NotNull(part.Placement);
        var origin = part.Placement!.Value.Origin;
        Assert.Equal(1.1f, origin.X, 3);
        Assert.Equal(0.1f, origin.Y, 3);
        Assert.Equal(0.1f, origin.Z, 3);

        var pivot = Vector3.TransformPosition(origin, part.WorldTransform);
        Assert.True(pivot.Length > 100f, $"pivot should sit on the part in millimetres, got {pivot}");
        Assert.NotEqual(0f, pivot.X);
    }

    [Fact]
    public void Recenter_moves_an_existing_group_pivot_without_moving_the_mesh()
    {
        var part = new SceneNode
        {
            Name = "lug",
            PendingMesh = Box(new Vector3(100f, 0f, 0f), new Vector3(120f, 20f, 20f)),
        };
        part.EnsurePlacement(Vector3.Zero);

        var corner = new Vector3(100f, 0f, 0f);
        var before = Vector3.TransformPosition(corner, part.WorldTransform);

        ImportHelper.RecenterPivot(part);

        var after = Vector3.TransformPosition(corner, part.WorldTransform);
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
        Assert.Equal(before.Z, after.Z, 3);
        Assert.Equal(110f, part.Placement!.Value.Origin.X, 3);
        Assert.Equal(10f, part.Placement.Value.Origin.Y, 3);
        Assert.Equal(10f, part.Placement.Value.Origin.Z, 3);
    }

    private static MeshData Box(Vector3 min, Vector3 max)
    {
        var p = new[]
        {
            new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
            new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
            new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
            new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
        };
        return new MeshData(p, new Vector3[p.Length], indices: null, name: "box");
    }
}
