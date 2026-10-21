using System.Numerics;

namespace MassiveSlicer.Core.Models;

/// <summary>
/// One spherical TCP attractor. <see cref="Axis"/> is the extruder body direction
/// (cylinder +Z). The nozzle points along −Axis. At <see cref="Position"/> the
/// tool matches that angle; influence falls to zero at <see cref="RadiusMm"/>.
/// </summary>
public readonly record struct TcpAttractor(Vector3 Position, float RadiusMm, Vector3 Axis);

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

    public TcpAttractor ToAttractor()
    {
        var axis = new Vector3(AxisX, AxisY, AxisZ);
        if (axis.LengthSquared() < 1e-8f) axis = Vector3.UnitZ;
        else axis = Vector3.Normalize(axis);
        return new TcpAttractor(new Vector3(X, Y, Z), RadiusMm, axis);
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
    };
}
