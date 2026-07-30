using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Bounding boxes for a node's geometry, measured in the node's <em>own</em> space — before its
/// <see cref="SceneNode.LocalTransform"/> is applied.
/// </summary>
/// <remarks>
/// This is the space <see cref="NodeTransform.Origin"/> lives in, which is what makes it the right
/// frame for both the one-time re-centre at import and the Move Origin bounding box. Measuring in
/// world space instead would give a box that grows and shrinks as the part is rotated, and snap
/// points that drift off the surface.
/// </remarks>
public static class NodeBounds
{
    /// <summary>
    /// The axis-aligned box enclosing every mesh in <paramref name="node"/>'s subtree, in the
    /// node's own space. <c>null</c> when the subtree holds no geometry.
    /// </summary>
    /// <remarks>
    /// Walks actual vertex positions rather than combining each descendant's own box, since
    /// re-enclosing a rotated child's box would pad the result and push snap points off the mesh.
    /// Authoring overlays (a modifier's gizmo plane) are skipped so a modifier can never inflate
    /// the box the user snaps to.
    /// </remarks>
    public static (Vector3 Min, Vector3 Max)? LocalAabb(SceneNode node)
    {
        var nodeWorld = node.WorldTransform;
        // A collapsed transform (zero scale on an axis) has no inverse; fall back to identity so a
        // degenerate node measures as if it were unparented rather than throwing.
        var invNodeWorld = Matrix4.Identity;
        if (MathF.Abs(nodeWorld.Determinant) > 1e-12f)
            Matrix4.Invert(nodeWorld, out invNodeWorld);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;

        foreach (var n in node.SelfAndDescendants())
        {
            if (n.IsAuthoringOverlay) continue;
            var mesh = n.Mesh?.PickingData ?? n.PendingMesh;
            if (mesh is null || mesh.Positions.Length == 0) continue;

            // n.WorldTransform * inverse(node.WorldTransform) is exactly the chain from n's mesh
            // space up to node's own space, with node's own transform cancelled out.
            var toNodeLocal = n.WorldTransform * invNodeWorld;

            foreach (var p in mesh.Positions)
            {
                var q = Vector3.TransformPosition(p, toNodeLocal);
                min = Vector3.ComponentMin(min, q);
                max = Vector3.ComponentMax(max, q);
                any = true;
            }
        }

        return any ? (min, max) : null;
    }

    /// <summary>
    /// Centre of <see cref="LocalAabb"/> — the pivot a part gets at import and whenever Recenter
    /// Origin is pressed. <c>null</c> when there is no geometry to measure.
    /// </summary>
    public static Vector3? LocalCenter(SceneNode node)
        => LocalAabb(node) is { } b ? (b.Min + b.Max) * 0.5f : null;

    /// <summary>
    /// The 26 points the Move Origin box offers: 8 corners, 6 face centres, 12 edge midpoints.
    /// The box centre is deliberately absent — Recenter Origin already covers it.
    /// </summary>
    /// <remarks>
    /// Because the box is axis-aligned in the node's own space, the object's own X/Y/Z axes already
    /// lie along the box's edges at every one of these points. Two arrows therefore run along any
    /// face's plane and the third points out of it with no reorientation needed, which also avoids
    /// having to invent a "perpendicular" at a corner where three faces meet.
    /// </remarks>
    public static IEnumerable<Vector3> SnapPoints((Vector3 Min, Vector3 Max) box)
    {
        var mid = (box.Min + box.Max) * 0.5f;
        var xs  = new[] { box.Min.X, mid.X, box.Max.X };
        var ys  = new[] { box.Min.Y, mid.Y, box.Max.Y };
        var zs  = new[] { box.Min.Z, mid.Z, box.Max.Z };

        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        for (int k = 0; k < 3; k++)
        {
            // Skip the one point where all three are the middle: that is the box centre.
            if (i == 1 && j == 1 && k == 1) continue;
            yield return new Vector3(xs[i], ys[j], zs[k]);
        }
    }
}
