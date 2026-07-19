using MassiveSlicer.Core.Models;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene.Modifiers;

/// <summary>
/// Resolves a <see cref="CutModifier"/>'s settings into the plane point/normal that
/// <see cref="PlanarMeshSplitter"/> needs, and performs the mesh-side split. Toolpath
/// splitting is handled separately (see <c>HorizontalCutSplitter</c>/<c>VerticalCutSplitter</c> in Core).
/// </summary>
public static class CutModifierGeometry
{
    /// <summary>
    /// Plane normal for a modifier's orientation: Z for horizontal; for vertical, the direction
    /// <see cref="CutModifier.RotationDegrees"/> points to in the XY plane (0° = +X, 90° = +Y,
    /// any angle between for a manually-dialed cut).
    /// </summary>
    public static Vector3 Normal(CutModifier modifier)
    {
        if (modifier.Orientation == CutOrientation.Horizontal) return Vector3.UnitZ;

        float rad = MathHelper.DegreesToRadians(modifier.RotationDegrees);
        return new Vector3(MathF.Cos(rad), MathF.Sin(rad), 0f);
    }

    /// <summary>
    /// A point on the modifier's plane. Horizontal is measured from the mesh's local Z origin.
    /// Vertical is measured from <paramref name="bedCenter"/> outward along the rotated normal —
    /// rotation pivots around bed center, not the mesh's own origin, so a manually-dialed angle
    /// stays reproducible for whatever later references this exact cut line.
    /// </summary>
    public static Vector3 PlanePoint(CutModifier modifier, Vector3 bedCenter)
    {
        var normal = Normal(modifier);
        return modifier.Orientation == CutOrientation.Horizontal
            ? normal * modifier.Offset
            : bedCenter + normal * modifier.Offset;
    }

    /// <summary>
    /// Splits <paramref name="mesh"/> at this modifier's plane. The positive-normal side
    /// (above for horizontal, the direction it's rotated to face for vertical) is
    /// <see cref="PlanarMeshSplitter.SplitResult.Positive"/>.
    /// </summary>
    public static PlanarMeshSplitter.SplitResult Split(CutModifier modifier, MeshData mesh, Vector3 bedCenter)
        => PlanarMeshSplitter.Split(mesh, PlanePoint(modifier, bedCenter), Normal(modifier));
}
