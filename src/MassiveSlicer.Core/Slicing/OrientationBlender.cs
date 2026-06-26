using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Blends per-move stacking normals between world +Z (vertical tool) and the
/// surface-following direction assigned by the slicer.
/// </summary>
public static class OrientationBlender
{
    /// <summary>
    /// Rotates <paramref name="surfaceNormal"/> toward <see cref="Vector3.UnitZ"/>
    /// by <c>(1 - strength)</c> of the total tilt angle.
    /// <paramref name="strength"/> 0 = vertical, 1 = full surface follow.
    /// </summary>
    public static Vector3 BlendNormal(Vector3 surfaceNormal, float strength)
    {
        strength = Math.Clamp(strength, 0f, 1f);
        if (surfaceNormal.LengthSquared() < 1e-8f)
            return Vector3.UnitZ;
        if (strength >= 1f - 1e-6f)
            return Vector3.Normalize(surfaceNormal);
        if (strength <= 1e-6f)
            return Vector3.UnitZ;

        var n = Vector3.Normalize(surfaceNormal);
        float dot = Math.Clamp(Vector3.Dot(n, Vector3.UnitZ), -1f, 1f);
        float tilt = MathF.Acos(dot);
        float blendedTilt = tilt * strength;

        if (blendedTilt < 1e-6f)
            return Vector3.UnitZ;

        var axis = Vector3.Cross(Vector3.UnitZ, n);
        if (axis.LengthSquared() < 1e-8f)
            return Vector3.UnitZ;

        axis = Vector3.Normalize(axis);
        return Vector3.Normalize(Vector3.Transform(
            Vector3.UnitZ,
            Quaternion.CreateFromAxisAngle(axis, blendedTilt)));
    }

    /// <summary>Rewrites extrude/mill move normals in place. Zero normals are left unchanged.</summary>
    public static void ApplyInPlace(Toolpath toolpath, float strength)
    {
        if (strength >= 1f - 1e-6f) return;

        foreach (var layer in toolpath.Layers)
        {
            for (int i = 0; i < layer.Moves.Count; i++)
            {
                var move = layer.Moves[i];
                if (!ToolpathMoveKinds.IsCutSegment(move.Kind) || move.Normal.LengthSquared() < 1e-8f)
                    continue;
                layer.Moves[i] = move with { Normal = BlendNormal(move.Normal, strength) };
            }
        }
    }
}