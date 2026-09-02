using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Tests;

public sealed class CellFrameTest
{
    [Fact]
    public void Kinds_are_distinct_and_named()
    {
        Assert.NotEqual(CellFrameKind.Tool, CellFrameKind.Cutter);
        Assert.NotEqual(CellFrameKind.Flange, CellFrameKind.GlbTcp);
        Assert.Equal("TOOL_DATA", CellFrameKind.Tool.DumpName());
        Assert.Equal("CUTTER", CellFrameKind.Cutter.DumpName());
        Assert.Equal("FLANGE", CellFrameKind.Flange.DumpName());
        Assert.Equal("GLB_tcp", CellFrameKind.GlbTcp.DumpName());
        Assert.Equal("BASE", CellFrameKind.Base.DumpName());
        Assert.Equal("ROBROOT", CellFrameKind.Robroot.DumpName());
    }

    [Fact]
    public void Frame_stores_origin_mm_and_optional_abc()
    {
        var f = new CellFrame(
            CellFrameKind.Tool,
            OriginMm: new System.Numerics.Vector3(-78.4f, 325.2f, 637.4f),
            AbcDeg: new System.Numerics.Vector3(103.7f, -43.7f, 40.5f));
        Assert.Equal(CellFrameKind.Tool, f.Kind);
        Assert.True(f.HasOrientation);
    }
}
