using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene.Modifiers;

/// <summary>
/// Converts between a <see cref="Core.Models.CutModifier"/>'s plain fields (Offset,
/// RotationDegrees — the persisted source of truth used by Apply/Split) and the transform of
/// its dedicated gizmo <see cref="SceneNode"/> (a real, independent object the existing
/// translate/rotate drag code operates on directly, so moving a modifier never touches the
/// mesh it's attached to and gets rotation support for free).
///
/// Horizontal is parented to the owning mesh — its local transform is a pure translation along
/// local Z by Offset, so the existing parent-aware drag math naturally accounts for wherever
/// the mesh currently sits. Vertical has no parent — its transform directly encodes world
/// position (bed center + its rotated normal × Offset) and a Z-axis rotation for
/// RotationDegrees, since rotating a vertical plane pivots around bed center, not the mesh.
/// </summary>
public static class CutModifierNodeSync
{
    /// <summary>Horizontal: local transform relative to the parent mesh — translation only.</summary>
    public static Matrix4 BuildHorizontalLocalTransform(float offset)
        => Matrix4.CreateTranslation(0f, 0f, offset);

    /// <summary>Horizontal: reads Offset back out of the (possibly gizmo-dragged) local transform.</summary>
    public static float ExtractHorizontalOffset(Matrix4 localTransform) => localTransform.Row3.Z;

    /// <summary>
    /// Vertical: builds the node's transform directly in world space (no parent) — a Z rotation
    /// by <paramref name="rotationDegrees"/> (its Row0 becomes the plane's normal direction),
    /// translated to <paramref name="bedCenter"/> plus that normal times <paramref name="offset"/>.
    /// </summary>
    public static Matrix4 BuildVerticalTransform(float rotationDegrees, float offset, Vector3 bedCenter)
    {
        var rot = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(rotationDegrees));
        var normal = rot.Row0.Xyz;
        rot.Row3 = new Vector4(bedCenter + normal * offset, 1f);
        return rot;
    }

    /// <summary>Vertical: reads (Offset, RotationDegrees) back out of the (possibly
    /// gizmo-dragged) transform — Row0 is the current normal direction, translation minus bed
    /// center projected onto it is Offset.</summary>
    public static (float Offset, float RotationDegrees) ExtractVertical(Matrix4 transform, Vector3 bedCenter)
    {
        var normal = transform.Row0.Xyz;
        normal = normal.LengthSquared > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitX;
        float rotationDegrees = MathHelper.RadiansToDegrees(MathF.Atan2(normal.Y, normal.X));
        float offset = Vector3.Dot(transform.Row3.Xyz - bedCenter, normal);
        return (offset, rotationDegrees);
    }
}
