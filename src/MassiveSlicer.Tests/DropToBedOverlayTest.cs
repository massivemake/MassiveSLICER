using MassiveSlicer.App;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

/// <summary>
/// Drop to Plate measures the part, not the authoring overlays hanging off it.
/// </summary>
/// <remarks>
/// Carried over from RecenterKeepsGeometryTest, whose other cases all exercised the vertex-baking
/// Recenter that has since been deleted — Recenter is a pivot move now, covered by
/// <see cref="NodeBoundsTest"/> and the live bridge checks. This one is about a different rule and
/// is still load-bearing.
/// </remarks>
public class DropToBedOverlayTest
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
}
