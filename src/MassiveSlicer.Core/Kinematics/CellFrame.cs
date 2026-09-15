using System.Numerics;

namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Named kinematic frames on a cell. Do not say "TCP" without saying which one —
/// TOOL_DATA (taught pad), CUTTER (mill nose), FLANGE (joint_6), and GLB_tcp
/// (robot bone) are different origins.
/// </summary>
public enum CellFrameKind
{
    World,
    Robroot,
    Flange,
    GlbTcp,
    Tool,
    Cutter,
    Base,
}

public readonly record struct CellFrame(
    CellFrameKind Kind,
    Vector3 OriginMm,
    Vector3? AbcDeg = null)
{
    public bool HasOrientation => AbcDeg is { } a
        && (MathF.Abs(a.X) + MathF.Abs(a.Y) + MathF.Abs(a.Z) > 1e-3f);
}

public static class CellFrameKindNames
{
    public static string DumpName(this CellFrameKind kind) => kind switch
    {
        CellFrameKind.World   => "WORLD",
        CellFrameKind.Robroot => "ROBROOT",
        CellFrameKind.Flange  => "FLANGE",
        CellFrameKind.GlbTcp  => "GLB_tcp",
        CellFrameKind.Tool    => "TOOL_DATA",
        CellFrameKind.Cutter  => "CUTTER",
        CellFrameKind.Base    => "BASE",
        _ => kind.ToString(),
    };
}
