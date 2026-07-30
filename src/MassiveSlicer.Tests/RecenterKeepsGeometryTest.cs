using MassiveSlicer.App;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

/// <summary>
/// Recenter moves the pivot, not the part. Jeff reported the object jumping when the button was
/// pressed; the cause was compensating for the baked vertex shift in the wrong frame, which only
/// cancels out while the part is unrotated and unscaled — so the pre-existing coverage passed on a
/// fresh import and missed it entirely.
/// </summary>
public class RecenterKeepsGeometryTest
{
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

    private static Vector3[] WorldPoints(SceneNode node)
    {
        var mesh = node.PendingMesh!;
        var m    = node.WorldTransform;
        return mesh.Positions.Select(p => Vector3.TransformPosition(p, m)).ToArray();
    }

    private static void AssertUnmoved(Vector3[] before, Vector3[] after)
    {
        Assert.Equal(before.Length, after.Length);
        // Compare as sets by index — the bake rewrites coordinates but preserves vertex order.
        for (int i = 0; i < before.Length; i++)
            Assert.True((before[i] - after[i]).Length < 0.01f,
                $"vertex {i} moved from {before[i]} to {after[i]}");
    }

    [Fact]
    public void Recenter_does_not_move_an_unrotated_part()
    {
        var node = new SceneNode
        {
            PendingMesh    = Box(new Vector3(10f, 20f, 30f), new Vector3(40f, 60f, 90f)),
            LocalTransform = Matrix4.CreateTranslation(500f, -200f, 15f),
        };
        var before = WorldPoints(node);

        Assert.True(ImportHelper.RecenterPivotToBottomCenter(node));

        AssertUnmoved(before, WorldPoints(node));
    }

    [Fact]
    public void Recenter_does_not_move_a_rotated_part()
    {
        // The case the old code got wrong.
        var node = new SceneNode
        {
            PendingMesh = Box(new Vector3(10f, 20f, 30f), new Vector3(40f, 60f, 90f)),
            LocalTransform = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(37f))
                           * Matrix4.CreateTranslation(500f, -200f, 15f),
        };
        var before = WorldPoints(node);

        Assert.True(ImportHelper.RecenterPivotToBottomCenter(node));

        AssertUnmoved(before, WorldPoints(node));
    }

    [Fact]
    public void Recenter_does_not_move_a_rotated_and_unevenly_scaled_part()
    {
        var node = new SceneNode
        {
            PendingMesh = Box(new Vector3(10f, 20f, 30f), new Vector3(40f, 60f, 90f)),
            LocalTransform = Matrix4.CreateScale(2.5f, 0.4f, 1f)
                           * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(-22f))
                           * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(37f))
                           * Matrix4.CreateTranslation(500f, -200f, 15f),
        };
        var before = WorldPoints(node);

        Assert.True(ImportHelper.RecenterPivotToBottomCenter(node));

        AssertUnmoved(before, WorldPoints(node));
    }

    [Fact]
    public void Recenter_leaves_the_pivot_on_the_parts_bottom_centre()
    {
        // The pivot is stored in the model's own coordinates, and the bake rewrites those — so the
        // pivot has to be re-seated or it ends up pointing at a different spot on the part.
        var node = new SceneNode
        {
            PendingMesh = Box(new Vector3(10f, 20f, 30f), new Vector3(40f, 60f, 90f)),
            LocalTransform = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(37f))
                           * Matrix4.CreateTranslation(500f, -200f, 15f),
        };
        ImportHelper.CenterOrigin(node);                       // as a fresh import would be
        var pivotBefore = Vector3.TransformPosition(node.Placement!.Value.Origin, node.WorldTransform);

        Assert.True(ImportHelper.RecenterPivotToBottomCenter(node));

        var t = node.Placement!.Value;
        // Bottom-centre is the model's own zero after the bake.
        Assert.True(t.Origin.Length < 0.01f, $"pivot should sit at the model's zero, got {t.Origin}");

        // And it really is the bottom centre of the part in the world: same XY as the box centre,
        // and the lowest Z of any vertex.
        var pivotWorld = Vector3.TransformPosition(t.Origin, node.WorldTransform);
        var pts        = WorldPoints(node);
        Assert.True(pivotWorld.Z <= pts.Min(p => p.Z) + 0.01f,
            $"pivot Z {pivotWorld.Z} should be at or below the lowest vertex {pts.Min(p => p.Z)}");
        Assert.NotEqual(pivotBefore.Z, pivotWorld.Z, 1);   // it did move down from the box centre
    }
}
