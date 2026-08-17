using System.Numerics;

namespace MassiveSlicer.Core.Models;

/// <summary>
/// Where the planar-facing / planar-clearing cutter points (T12 +Z) before tilt.
/// Raster / projection is along the opposite direction (from air into the work).
/// </summary>
public enum MillPlanarAxisKind
{
    /// <summary>Shop default: bit points world −Z (face the XY plane from above).</summary>
    WorldNegZ,
    WorldPosZ,
    WorldPosX,
    WorldNegX,
    WorldPosY,
    WorldNegY,
    /// <summary>Average painted-area / selected-face outward normal.</summary>
    PaintedFace,
    /// <summary>Viewport camera: tool comes from the eye toward the target.</summary>
    Camera,
    /// <summary>User XYZ for T12 +Z.</summary>
    Custom,
}

/// <summary>
/// Resolves a planar mill tool axis (T12 +Z) and the matching approach / surface normal.
/// Mill ABC uses <c>tool Z = −surfaceNormal</c>, so <see cref="ApproachFromToolAxis"/> is
/// the vector <see cref="Slicing.SurfaceFollowMillGenerator"/> rasters along.
/// </summary>
public static class MillPlanarOrientation
{
    const float Deg2Rad = MathF.PI / 180f;

    public static Vector3 BaseToolAxis(MillPlanarAxisKind kind, Vector3 capturedOrCustom)
    {
        var preset = kind switch
        {
            MillPlanarAxisKind.WorldNegZ => -Vector3.UnitZ,
            MillPlanarAxisKind.WorldPosZ => Vector3.UnitZ,
            MillPlanarAxisKind.WorldPosX => Vector3.UnitX,
            MillPlanarAxisKind.WorldNegX => -Vector3.UnitX,
            MillPlanarAxisKind.WorldPosY => Vector3.UnitY,
            MillPlanarAxisKind.WorldNegY => -Vector3.UnitY,
            _ => capturedOrCustom,
        };
        if (preset.LengthSquared() < 1e-12f)
            return -Vector3.UnitZ;
        return Vector3.Normalize(preset);
    }

    /// <summary>Tilt the tool axis <paramref name="tiltDeg"/> toward <paramref name="azimuthDeg"/> (0 = +X of the tool frame).</summary>
    public static Vector3 ApplyTilt(Vector3 toolAxis, float tiltDeg, float azimuthDeg)
    {
        var z = toolAxis.LengthSquared() > 1e-12f ? Vector3.Normalize(toolAxis) : -Vector3.UnitZ;
        if (MathF.Abs(tiltDeg) < 1e-4f)
            return z;

        var hint = MathF.Abs(z.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        var x = Vector3.Cross(hint, z);
        if (x.LengthSquared() < 1e-12f)
            x = Vector3.Cross(Vector3.UnitY, z);
        x = Vector3.Normalize(x);
        var y = Vector3.Cross(z, x);

        float az = azimuthDeg * Deg2Rad;
        var tiltAxis = x * MathF.Cos(az) + y * MathF.Sin(az);
        return Rotate(z, tiltAxis, tiltDeg * Deg2Rad);
    }

    public static Vector3 ResolveToolAxis(
        MillPlanarAxisKind kind,
        Vector3 capturedOrCustom,
        float tiltDeg,
        float azimuthDeg)
        => ApplyTilt(BaseToolAxis(kind, capturedOrCustom), tiltDeg, azimuthDeg);

    /// <summary>Direction the cutter comes from (air → work). Raster +Z in this frame.</summary>
    public static Vector3 ApproachFromToolAxis(Vector3 toolAxis)
    {
        var z = toolAxis.LengthSquared() > 1e-12f ? Vector3.Normalize(toolAxis) : -Vector3.UnitZ;
        return -z;
    }

    /// <summary>Outward surface the bit faces. <c>AbcFromMillNormal</c> maps this to T12 +Z.</summary>
    public static Vector3 SurfaceNormalFromToolAxis(Vector3 toolAxis)
        => ApproachFromToolAxis(toolAxis);

    /// <summary>Average outward normal of triangles referenced by <paramref name="indices"/>.</summary>
    public static Vector3 AverageSurfaceNormal(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<int> indices)
    {
        var sum = Vector3.Zero;
        int n = 0;
        if (indices.Count >= 3)
        {
            for (int t = 0; t + 2 < indices.Count; t += 3)
            {
                int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                if ((uint)i0 >= (uint)positions.Count
                    || (uint)i1 >= (uint)positions.Count
                    || (uint)i2 >= (uint)positions.Count)
                    continue;
                var fn = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
                if (fn.LengthSquared() < 1e-14f) continue;
                // Prefer authored normals when they agree with the winding.
                var vn = SafeNormalAt(normals, i0) + SafeNormalAt(normals, i1) + SafeNormalAt(normals, i2);
                if (vn.LengthSquared() > 1e-10f && Vector3.Dot(vn, fn) < 0f)
                    fn = -fn;
                sum += Vector3.Normalize(fn);
                n++;
            }
        }
        if (n == 0 && normals is { Count: > 0 })
        {
            foreach (var v in normals)
            {
                if (v.LengthSquared() < 1e-12f) continue;
                sum += Vector3.Normalize(v);
                n++;
            }
        }
        return n == 0 ? Vector3.UnitZ : Vector3.Normalize(sum);
    }

    static Vector3 SafeNormalAt(IReadOnlyList<Vector3> normals, int i)
    {
        if (normals is null || (uint)i >= (uint)normals.Count) return Vector3.Zero;
        return normals[i];
    }

    static Vector3 Rotate(Vector3 v, Vector3 axis, float radians)
    {
        var a = axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.UnitX;
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return v * c + Vector3.Cross(a, v) * s + a * Vector3.Dot(a, v) * (1f - c);
    }
}
