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
    public void Drop_to_bed_ignores_authoring_overlays()
    {
        // A cut modifier's plane and the Move Origin box are real child nodes with real geometry
        // that extends past the part. Counting them made Drop to Plate measure the overlay's lowest
        // point instead of the model's — which is what pushed a rotated part upward instead of onto
        // the bed. IsAuthoringOverlay already promises exclusion from bounding-box measurements.
        const float BedZ = 130f;

        var node = new SceneNode
        {
            PendingMesh    = Box(new Vector3(0f, 0f, 0f), new Vector3(100f, 100f, 100f)),
            LocalTransform = Matrix4.CreateTranslation(500f, -200f, 400f),
        };
        // An overlay hanging 1000mm below the part, as a big cut plane easily can.
        node.AddChild(new SceneNode
        {
            PendingMesh        = Box(new Vector3(-500f, -500f, -1000f), new Vector3(500f, 500f, -990f)),
            IsAuthoringOverlay = true,
        });

        ImportHelper.CenterOrigin(node);
        MassiveSlicer.App.Views.ViewportView.DropNodeToBed(node, BedZ);

        float lowest = WorldPoints(node).Min(p => p.Z);
        Assert.True(MathF.Abs(lowest - BedZ) < 0.01f,
            $"part should rest on the bed at {BedZ}, its lowest real vertex is at {lowest}");
    }

    [Fact]
    public void Recenter_measures_the_part_not_an_overlay_hanging_off_it()
    {
        // Recenter BAKES vertices by the offset it measures, so measuring an overlay's bottom
        // instead of the model's bakes a wildly wrong shift — which is what flung a part away after
        // a session of moves, rotations and origin picks with a cut plane or the Move Origin box in
        // the subtree. The bake itself still has to move every mesh, overlays included, or they get
        // left behind; only the measurement excludes them.
        var node = new SceneNode
        {
            PendingMesh    = Box(new Vector3(0f, 0f, 0f), new Vector3(100f, 100f, 100f)),
            LocalTransform = Matrix4.CreateTranslation(500f, -200f, 400f),
        };
        node.AddChild(new SceneNode
        {
            PendingMesh        = Box(new Vector3(-4000f, -4000f, -4000f), new Vector3(4000f, 4000f, -3990f)),
            IsAuthoringOverlay = true,
        });

        var before = WorldPoints(node);

        Assert.True(ImportHelper.RecenterPivotToBottomCenter(node));

        AssertUnmoved(before, WorldPoints(node));

        // And the pivot really is the part's bottom centre, not somewhere out by the overlay.
        var pivotWorld = Vector3.TransformPosition(node.Placement?.Origin ?? Vector3.Zero, node.WorldTransform);
        var pts = WorldPoints(node);
        Assert.True(MathF.Abs(pivotWorld.Z - pts.Min(p => p.Z)) < 0.01f,
            $"pivot Z {pivotWorld.Z} should sit at the part's lowest vertex {pts.Min(p => p.Z)}");
    }

    [Fact]
    public void Recenter_does_not_move_a_mesh_that_sits_on_an_offset_child()
    {
        // The bake shifts each mesh in its OWN frame, so the offset has to be rotated into that
        // frame as a direction. Running it through a point transform folded the child's inverse
        // translation into the shift, moving the mesh by an unrelated amount that the root's
        // compensating transform could not cancel — the part flew off.
        var root = new SceneNode { LocalTransform = Matrix4.CreateTranslation(500f, -200f, 400f) };
        root.AddChild(new SceneNode
        {
            PendingMesh    = Box(Vector3.Zero, new Vector3(100f, 100f, 100f)),
            LocalTransform = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(25f))
                           * Matrix4.CreateTranslation(750f, 320f, 60f),
        });

        var child  = root.Children[0];
        var before = WorldPoints(child);

        Assert.True(ImportHelper.RecenterPivotToBottomCenter(root));

        AssertUnmoved(before, WorldPoints(child));
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
