using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// How a mounted tool maps flange → IK TCP and which overlay to draw.
/// Derived from existing <see cref="ToolCellConfig"/> — no JSON schema change.
/// T12 mill position is the collet, not pad TOOL_DATA XYZ.
/// </summary>
public enum IkTcpSource
{
    TaughtXyz,
    MillCollet,
    SpindleCutter,
    Flange,
}

public enum IkOrientSource
{
    Flange,
    TaughtAbc,
}

public enum TriadSource
{
    ToolFrame,
    WorldUpAtPath,
    Flange,
}

public sealed record ToolKinematicsSpec(
    int KrlIndex,
    string Name,
    IkTcpSource PositionSource,
    IkOrientSource OrientSource,
    TriadSource TriadSource,
    float HolderYawDeg,
    float ToolFrameRollDeg,
    float TcpX, float TcpY, float TcpZ,
    float TcpA, float TcpB, float TcpC)
{
    public static ToolKinematicsSpec FromTool(ToolCellConfig t)
    {
        bool t12 = t.KrlIndex == 12
            || t.Name.Contains("Tool 12", StringComparison.OrdinalIgnoreCase);
        bool spindle = !t12
            && (t.KrlIndex is 2 or 3 or 7 or 8 or 9 or 10
                || t.Name.Contains("Spindle", StringComparison.OrdinalIgnoreCase));
        bool noTool = t.KrlIndex == 4
            || t.Name.Equals("No Tool", StringComparison.OrdinalIgnoreCase);

        var pos = t12 ? IkTcpSource.MillCollet
                : noTool ? IkTcpSource.Flange
                : spindle ? IkTcpSource.SpindleCutter
                : IkTcpSource.TaughtXyz;

        return new ToolKinematicsSpec(
            t.KrlIndex, t.Name, pos,
            IkOrientSource.TaughtAbc,
            t12 ? TriadSource.WorldUpAtPath : TriadSource.ToolFrame,
            HolderYawDeg: t12 ? 0f : 90f,
            t.ToolFrameRoll, t.TcpX, t.TcpY, t.TcpZ, t.TcpA, t.TcpB, t.TcpC);
    }
}
