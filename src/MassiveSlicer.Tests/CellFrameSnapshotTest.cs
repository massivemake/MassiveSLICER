using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Viewport.FK;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using NVec3 = System.Numerics.Vector3;

namespace MassiveSlicer.Tests;

public sealed class CellFrameSnapshotTest
{
    [Fact]
    public void Dump_reads_robroot_flange_and_glb_tcp_not_tool()
    {
        var wrapper = new SceneNode
        {
            Name = "LFAM 3_Robot",
            LocalTransform = Matrix4.CreateTranslation(100f, 200f, 300f),
        };
        for (int i = 1; i <= 5; i++)
            wrapper.AddChild(new SceneNode { Name = $"joint_{i}" });

        var flange = new SceneNode
        {
            Name = "joint_6",
            LocalTransform = Matrix4.CreateTranslation(10f, 20f, 30f),
        };
        var glbTcp = new SceneNode
        {
            Name = "tcp",
            LocalTransform = Matrix4.CreateTranslation(0f, 5f, 0f),
        };
        flange.AddChild(glbTcp);
        wrapper.AddChild(flange);

        var fk = RobotFkController.TryBuild(wrapper, []);
        Assert.NotNull(fk);

        var robroot = new NVec3(100f, 200f, 300f);
        var snap = CellFrameDump.FromFk(wrapper, fk!, robroot, spec: null, cutterWorldMm: null, baseOriginMm: null);

        Assert.Equal(CellFrameKind.Robroot, snap.Robroot.Kind);
        Assert.Equal(robroot, snap.Robroot.OriginMm);

        var flangeMm = new NVec3(flange.WorldTransform.Row3.X, flange.WorldTransform.Row3.Y, flange.WorldTransform.Row3.Z);
        Assert.Equal(CellFrameKind.Flange, snap.Flange.Kind);
        Assert.Equal(flangeMm, snap.Flange.OriginMm);

        Assert.True(snap.GlbTcp.HasValue);
        var g = snap.GlbTcp.Value;
        var tcpMm = new NVec3(glbTcp.WorldTransform.Row3.X, glbTcp.WorldTransform.Row3.Y, glbTcp.WorldTransform.Row3.Z);
        Assert.Equal(CellFrameKind.GlbTcp, g.Kind);
        Assert.Equal(tcpMm, g.OriginMm);
        Assert.NotEqual(snap.Flange.OriginMm, g.OriginMm);

        Assert.Null(snap.Tool);
    }
}
