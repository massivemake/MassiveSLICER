using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// A node's complete placement state, captured for undo: both the composed matrix and the separated
/// <see cref="NodeTransform"/> when it has one.
/// </summary>
/// <remarks>
/// Capturing only the matrix is enough for an ordinary move, rotate or scale, because those never
/// change what the pivot means. It is <em>not</em> enough either side of a pivot bake: that rewrites
/// every vertex coordinate, so a pivot recorded in the model's own coordinates would afterwards point
/// at a different physical spot on the part. Restoring the matrix alone would leave the pivot stale.
/// </remarks>
public readonly record struct NodePose(Matrix4 LocalTransform, NodeTransform? Placement)
{
    public static NodePose Of(SceneNode node) => new(node.LocalTransform, node.Placement);

    /// <summary>Restores this pose onto <paramref name="node"/>, pivot included.</summary>
    public void ApplyTo(SceneNode node)
    {
        if (Placement is { } p) node.SetPlacement(p);
        else                    node.LocalTransform = LocalTransform;
    }
}
