using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class NodeBoundsTest
{
    /// <summary>A unit box from (0,0,0) to (sx,sy,sz), offset so its origin is deliberately not
    /// at its centre — the situation every imported file arrives in.</summary>
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

    [Fact]
    public void Measures_a_single_mesh_in_its_own_space()
    {
        var node = new SceneNode { PendingMesh = Box(new Vector3(10f, 20f, 30f), new Vector3(40f, 60f, 90f)) };

        var box = NodeBounds.LocalAabb(node);

        Assert.NotNull(box);
        Assert.Equal(new Vector3(10f, 20f, 30f), box!.Value.Min);
        Assert.Equal(new Vector3(40f, 60f, 90f), box.Value.Max);
        Assert.Equal(new Vector3(25f, 40f, 60f), NodeBounds.LocalCenter(node));
    }

    [Fact]
    public void The_nodes_own_transform_does_not_change_its_own_box()
    {
        // The property that matters: the Move Origin box and the re-centre pivot must not move or
        // resize as the part is dragged and turned, so they are measured before the node's own
        // transform is applied.
        var node = new SceneNode { PendingMesh = Box(Vector3.Zero, new Vector3(100f, 50f, 25f)) };
        var atRest = NodeBounds.LocalAabb(node);

        node.SetPlacement(new NodeTransform(
            position: new Vector3(900f, -300f, 40f),
            rotation: Quaternion.FromAxisAngle(Vector3.UnitZ, 0.9f),
            scale:    new Vector3(2f, 2f, 2f),
            origin:   Vector3.Zero));

        Assert.Equal(atRest, NodeBounds.LocalAabb(node));
    }

    [Fact]
    public void A_childs_own_transform_is_included()
    {
        var root  = new SceneNode { PendingMesh = Box(Vector3.Zero, new Vector3(10f, 10f, 10f)) };
        var child = new SceneNode
        {
            PendingMesh    = Box(Vector3.Zero, new Vector3(10f, 10f, 10f)),
            LocalTransform = Matrix4.CreateTranslation(100f, 0f, 0f),
        };
        root.AddChild(child);

        var box = NodeBounds.LocalAabb(root);

        Assert.NotNull(box);
        Assert.Equal(Vector3.Zero, box!.Value.Min);
        Assert.Equal(new Vector3(110f, 10f, 10f), box.Value.Max);
    }

    [Fact]
    public void An_authoring_overlay_cannot_inflate_the_box()
    {
        // A cut modifier's plane is a real child node. If it counted, snapping the origin to a
        // "corner" would land somewhere out in space rather than on the part.
        var root = new SceneNode { PendingMesh = Box(Vector3.Zero, new Vector3(10f, 10f, 10f)) };
        root.AddChild(new SceneNode
        {
            PendingMesh        = Box(new Vector3(-500f, -500f, -500f), new Vector3(500f, 500f, 500f)),
            IsAuthoringOverlay = true,
        });

        var box = NodeBounds.LocalAabb(root);

        Assert.Equal(new Vector3(10f, 10f, 10f), box!.Value.Max);
    }

    [Fact]
    public void No_geometry_means_no_box()
        => Assert.Null(NodeBounds.LocalAabb(new SceneNode()));

    [Fact]
    public void Offers_exactly_the_twenty_six_common_snap_points()
    {
        var box    = (Min: new Vector3(-10f, -20f, -30f), Max: new Vector3(10f, 20f, 30f));
        var points = NodeBounds.SnapPoints(box).ToList();

        Assert.Equal(26, points.Count);
        Assert.Equal(26, points.Distinct().Count());

        // The centre is deliberately absent — Recenter Origin covers it.
        Assert.DoesNotContain(Vector3.Zero, points);

        int Extremes(Vector3 p) =>
            (p.X == box.Min.X || p.X == box.Max.X ? 1 : 0) +
            (p.Y == box.Min.Y || p.Y == box.Max.Y ? 1 : 0) +
            (p.Z == box.Min.Z || p.Z == box.Max.Z ? 1 : 0);

        Assert.Equal(8,  points.Count(p => Extremes(p) == 3));   // corners
        Assert.Equal(12, points.Count(p => Extremes(p) == 2));   // edge midpoints
        Assert.Equal(6,  points.Count(p => Extremes(p) == 1));   // face centres
    }

    [Fact]
    public void Every_snap_point_lies_on_the_box_surface()
    {
        var box = (Min: new Vector3(5f, 5f, 5f), Max: new Vector3(15f, 25f, 45f));

        foreach (var p in NodeBounds.SnapPoints(box))
        {
            Assert.InRange(p.X, box.Min.X, box.Max.X);
            Assert.InRange(p.Y, box.Min.Y, box.Max.Y);
            Assert.InRange(p.Z, box.Min.Z, box.Max.Z);
            Assert.True(
                p.X == box.Min.X || p.X == box.Max.X ||
                p.Y == box.Min.Y || p.Y == box.Max.Y ||
                p.Z == box.Min.Z || p.Z == box.Max.Z,
                $"{p} is inside the box, not on its surface");
        }
    }
}
