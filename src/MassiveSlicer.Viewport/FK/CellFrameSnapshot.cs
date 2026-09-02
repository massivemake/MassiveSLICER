using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using NVec3 = System.Numerics.Vector3;

namespace MassiveSlicer.Viewport.FK;

/// <summary>
/// Named-frame dump from the live FK chain. TOOL_DATA is never the GLB <c>tcp</c> bone.
/// </summary>
public sealed record CellFrameSnapshot(
    CellFrame World,
    CellFrame Robroot,
    CellFrame Flange,
    CellFrame? GlbTcp,
    CellFrame? Tool,
    CellFrame? Cutter,
    CellFrame? Base);

public static class CellFrameDump
{
    public static CellFrameSnapshot FromFk(
        SceneNode robotWrapper,
        RobotFkController fk,
        NVec3 robrootMm,
        ToolKinematicsSpec? spec,
        NVec3? cutterWorldMm,
        NVec3? baseOriginMm)
    {
        _ = robotWrapper;
        _ = spec; // taught XYZ mapping is Task 5 (ToolFrameMaps); do not treat GLB tcp as TOOL.

        return new CellFrameSnapshot(
            World: new CellFrame(CellFrameKind.World, NVec3.Zero),
            Robroot: new CellFrame(CellFrameKind.Robroot, robrootMm),
            Flange: new CellFrame(CellFrameKind.Flange, OriginMm(fk.FlangeNode)),
            GlbTcp: fk.TcpNode is { } tcp
                ? new CellFrame(CellFrameKind.GlbTcp, OriginMm(tcp))
                : null,
            Tool: null,
            Cutter: cutterWorldMm is { } c
                ? new CellFrame(CellFrameKind.Cutter, c)
                : null,
            Base: baseOriginMm is { } b
                ? new CellFrame(CellFrameKind.Base, b)
                : null);
    }

    static NVec3 OriginMm(SceneNode? node)
    {
        if (node is null) return NVec3.Zero;
        var t = node.WorldTransform.Row3;
        return new NVec3(t.X, t.Y, t.Z);
    }
}
