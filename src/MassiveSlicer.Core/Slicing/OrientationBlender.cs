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
    /// by <c>(1 - strength)</c> of the total tilt angle, then clamps the remaining
    /// tilt to <paramref name="maxTiltDeg"/>.
    /// <paramref name="strength"/> 0 = vertical, 1 = full surface follow.
    /// <paramref name="maxTiltDeg"/> hard cap on tilt from vertical in degrees (90 = uncapped).
    /// </summary>
    public static Vector3 BlendNormal(Vector3 surfaceNormal, float strength, float maxTiltDeg = 90f)
    {
        strength = Math.Clamp(strength, 0f, 1f);
        float maxTilt = Math.Clamp(maxTiltDeg, 0f, 90f) * MathF.PI / 180f;
        if (surfaceNormal.LengthSquared() < 1e-8f)
            return Vector3.UnitZ;
        if (strength <= 1e-6f)
            return Vector3.UnitZ;

        var n = Vector3.Normalize(surfaceNormal);
        float dot = Math.Clamp(Vector3.Dot(n, Vector3.UnitZ), -1f, 1f);
        float tilt = MathF.Acos(dot);
        float blendedTilt = MathF.Min(tilt * strength, maxTilt);

        if (blendedTilt < 1e-6f)
            return Vector3.UnitZ;
        if (MathF.Abs(blendedTilt - tilt) < 1e-6f)
            return n;

        var axis = Vector3.Cross(Vector3.UnitZ, n);
        if (axis.LengthSquared() < 1e-8f)
            return Vector3.UnitZ;

        axis = Vector3.Normalize(axis);
        return Vector3.Normalize(Vector3.Transform(
            Vector3.UnitZ,
            Quaternion.CreateFromAxisAngle(axis, blendedTilt)));
    }

    /// <summary>Rewrites extrude/mill move normals in place. Zero normals are left unchanged.
    /// <paramref name="firstLayerZeroTilt"/> forces the first layer's normals to world +Z
    /// (vertical tool) regardless of the blend — flat-bed adhesion for Geodesic/Curved.</summary>
    public static void ApplyInPlace(Toolpath toolpath, float strength, float maxTiltDeg = 90f,
                                    bool firstLayerZeroTilt = false)
    {
        bool fullFollow = strength >= 1f - 1e-6f;
        bool uncapped   = maxTiltDeg >= 90f - 1e-4f;
        if (fullFollow && uncapped && !firstLayerZeroTilt) return;

        bool isFirst = true;
        foreach (var layer in toolpath.Layers)
        {
            bool zeroTiltLayer = firstLayerZeroTilt && isFirst;
            isFirst = false;
            for (int i = 0; i < layer.Moves.Count; i++)
            {
                var move = layer.Moves[i];
                if (!ToolpathMoveKinds.IsCutSegment(move.Kind) || move.Normal.LengthSquared() < 1e-8f)
                    continue;
                layer.Moves[i] = move with
                {
                    Normal = zeroTiltLayer ? Vector3.UnitZ
                                           : BlendNormal(move.Normal, strength, maxTiltDeg)
                };
            }
        }
    }
}