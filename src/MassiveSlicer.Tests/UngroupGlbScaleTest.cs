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
}
