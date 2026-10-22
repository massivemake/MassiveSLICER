using System.Numerics;

namespace MassiveSlicer.Core.Models;

/// <summary>Falloff volume. Sphere is distance to the handle. Cylinder is radial distance to its axis.</summary>
public enum TcpAdjustShape { Sphere, Cylinder }

/// <summary>Which frame the handle aims. Tool is the cutter. Flange is the robot wrist axis.</summary>
public enum TcpAdjustFrame { Tool, Flange }

/// <summary>Legacy names for the TCP unit axes. A saved <c>Aim</c> of X, Y, or Z still loads.</summary>
public enum TcpAdjustAxis { X, Y, Z }

/// <summary>
/// One TCP attractor or deflector. <see cref="Axis"/> is the bar direction
/// (cylinder +Z). <see cref="AimDir"/> is a direction in the selected TCP.
/// Zero means +Z, which keeps the old lean. On a mill tool that direction
/// points down the bar. On the robot flange it points along the bar.
/// A deflector does not use it: inside the volume it turns the chosen frame
/// back toward the way out. Influence falls to zero at <see cref="RadiusMm"/>.
/// <see cref="HeightMm"/> is the full length of a cylinder along its axis.
/// Zero keeps the old cylinder, whose half-height equals <see cref="RadiusMm"/>.
/// A sphere ignores it and keeps one influence radius.
/// <see cref="Detract"/> aims that TCP direction away from the bar.
/// Unchecked, it aims toward the bar. A deflector ignores it.
/// </summary>
public readonly record struct TcpAttractor(
    Vector3 Position,
    float RadiusMm,
    Vector3 Axis,
    TcpAdjustShape Shape = TcpAdjustShape.Sphere,
    TcpAdjustFrame Frame = TcpAdjustFrame.Tool,
    bool Deflect = false,
    Vector3 AimDir = default,
    float HeightMm = 0f,
    bool Detract = false)
{
    /// <summary>Unit direction in the TCP frame. A zero vector is +Z.</summary>
    public Vector3 AimUnit =>
        AimDir.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(AimDir);

    /// <summary>X, Y, or Z from an older pose. Anything else is +Z.</summary>
    public static Vector3 UnitFromAimName(string? name)
    {
        if (!Enum.TryParse<TcpAdjustAxis>(name, true, out var axis)) return Vector3.UnitZ;
        return axis switch
        {
            TcpAdjustAxis.X => Vector3.UnitX,
            TcpAdjustAxis.Y => Vector3.UnitY,
            _ => Vector3.UnitZ,
        };
    }

    /// <summary>Axis letter when the vector sits on +X, +Y, or +Z. Otherwise empty.</summary>
    public static string LegacyAimName(Vector3 unit)
    {
        if (unit.X > 0.999f && MathF.Abs(unit.Y) < 0.02f && MathF.Abs(unit.Z) < 0.02f)
            return nameof(TcpAdjustAxis.X);
        if (unit.Y > 0.999f && MathF.Abs(unit.X) < 0.02f && MathF.Abs(unit.Z) < 0.02f)
            return nameof(TcpAdjustAxis.Y);
        if (unit.Z > 0.999f && MathF.Abs(unit.X) < 0.02f && MathF.Abs(unit.Y) < 0.02f)
            return nameof(TcpAdjustAxis.Z);
        return "";
    }
}

/// <summary>Workspace snapshot of a <see cref="TcpAttractor"/>. Plain floats so .mass JSON stays stable.</summary>
public sealed class TcpAttractorPose
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float RadiusMm { get; set; } = 300f;
    public float AxisX { get; set; }
    public float AxisY { get; set; }
    public float AxisZ { get; set; } = 1f;
    public string Shape { get; set; } = nameof(TcpAdjustShape.Sphere);
    public string Frame { get; set; } = nameof(TcpAdjustFrame.Tool);
    public string Aim { get; set; } = nameof(TcpAdjustAxis.Z);
    public float AimX { get; set; }
    public float AimY { get; set; }
    public float AimZ { get; set; }
    public bool Deflect { get; set; }
    /// <summary>Full cylinder height in mm. Zero means half-height equals <see cref="RadiusMm"/>.</summary>
    public float HeightMm { get; set; }
    /// <summary>Outliner name. Empty on an older file, which still loads as TCP 1, TCP 2, and so on.</summary>
    public string? Name { get; set; }
    /// <summary>True aims the TCP direction away from the bar. False aims toward it.</summary>
    public bool Detract { get; set; }

    public TcpAttractor ToAttractor()
    {
        var axis = new Vector3(AxisX, AxisY, AxisZ);
        if (axis.LengthSquared() < 1e-8f) axis = Vector3.UnitZ;
        else axis = Vector3.Normalize(axis);
        if (!Enum.TryParse<TcpAdjustShape>(Shape, out var shape)) shape = TcpAdjustShape.Sphere;
        if (!Enum.TryParse<TcpAdjustFrame>(Frame, out var frame)) frame = TcpAdjustFrame.Tool;
        var typed = new Vector3(AimX, AimY, AimZ);
        var aim = typed.LengthSquared() > 1e-8f
            ? Vector3.Normalize(typed)
            : TcpAttractor.UnitFromAimName(Aim);
        return new TcpAttractor(new Vector3(X, Y, Z), RadiusMm, axis, shape, frame, Deflect, aim, HeightMm, Detract);
    }

    public static TcpAttractorPose From(TcpAttractor a) => new()
    {
        X = a.Position.X,
        Y = a.Position.Y,
        Z = a.Position.Z,
        RadiusMm = a.RadiusMm,
        AxisX = a.Axis.X,
        AxisY = a.Axis.Y,
        AxisZ = a.Axis.Z,
        Shape = a.Shape.ToString(),
        Frame = a.Frame.ToString(),
        AimX = a.AimUnit.X,
        AimY = a.AimUnit.Y,
        AimZ = a.AimUnit.Z,
        Aim = TcpAttractor.LegacyAimName(a.AimUnit),
        Deflect = a.Deflect,
        HeightMm = a.HeightMm,
        Detract = a.Detract,
    };
}
