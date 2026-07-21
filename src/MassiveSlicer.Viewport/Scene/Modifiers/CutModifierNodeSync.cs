using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene.Modifiers;

/// <summary>
/// Converts between a <see cref="Core.Models.CutModifier"/>'s plain fields (Offset,
/// RotationDegrees — the persisted source of truth used by Apply/Split) and the world-space
/// transform of its dedicated gizmo <see cref="SceneNode"/> — a real, independent scene object
/// (own geometry, own outliner row, never parented to any mesh) that the existing
/// translate/rotate drag code operates on directly, so moving a modifier never touches any mesh
/// and gets rotation support for free.
///
/// Both orientations build a plain world-space transform, unparented: Horizontal translates to
/// bed center + (PositionX, PositionY, Offset) — X/Y are free, dragged position (no cut math
/// depends on them; they exist purely so the plane can be pushed out of the way to isolate a
/// piece), Offset is the one that actually matters for the cut. Vertical encodes world position
/// (bed center + its rotated normal × Offset) and a Z-axis rotation for RotationDegrees, since
/// rotating a vertical plane pivots around bed center. Neither reads or depends on any mesh.
/// </summary>
public static class CutModifierNodeSync
{
    /// <summary>Horizontal: world-space transform — translated to bed center + (PositionX,
    /// PositionY, Offset). No rotation, so the plane is always flat regardless of anything else
    /// in the scene.</summary>
    public static Matrix4 BuildHorizontalTransform(float positionX, float positionY, float offset, Vector3 bedCenter)
        => Matrix4.CreateTranslation(bedCenter.X + positionX, bedCenter.Y + positionY, bedCenter.Z + offset);

    /// <summary>Horizontal: reads (PositionX, PositionY, Offset) back out of the (possibly
    /// gizmo-dragged) transform — X/Y round-trip as free position, only Z is the real cut value.</summary>
    public static (float PositionX, float PositionY, float Offset) ExtractHorizontal(Matrix4 transform, Vector3 bedCenter)
        => (transform.Row3.X - bedCenter.X, transform.Row3.Y - bedCenter.Y, transform.Row3.Z - bedCenter.Z);

    /// <summary>
    /// Vertical: builds the node's transform directly in world space (no parent) — a Z rotation
    /// by <paramref name="rotationDegrees"/> (its Row0 becomes the plane's normal direction),
    /// translated to <paramref name="bedCenter"/> plus that normal times <paramref name="offset"/>,
    /// plus <paramref name="positionZ"/> as a free height offset (normal has no Z component, so
    /// this is the only thing that ever moves the plane up/down).
    /// </summary>
    public static Matrix4 BuildVerticalTransform(float rotationDegrees, float offset, float positionZ, Vector3 bedCenter)
    {
        var rot = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(rotationDegrees));
        var normal = rot.Row0.Xyz;
        var center = bedCenter + normal * offset;
        rot.Row3 = new Vector4(center.X, center.Y, center.Z + positionZ, 1f);
        return rot;
    }

    /// <summary>Vertical: reads (Offset, RotationDegrees, PositionZ) back out of the (possibly
    /// gizmo-dragged) transform — Row0 is the current normal direction, translation minus bed
    /// center projected onto it is Offset; whatever Z is left over (normal is always flat, so it
    /// never explains any Z) is the free PositionZ.</summary>
    public static (float Offset, float RotationDegrees, float PositionZ) ExtractVertical(Matrix4 transform, Vector3 bedCenter)
    {
        var normal = transform.Row0.Xyz;
        normal = normal.LengthSquared > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitX;
        float rotationDegrees = MathHelper.RadiansToDegrees(MathF.Atan2(normal.Y, normal.X));
        float offset = Vector3.Dot(transform.Row3.Xyz - bedCenter, normal);
        float positionZ = transform.Row3.Z - bedCenter.Z;
        return (offset, rotationDegrees, positionZ);
    }
}
