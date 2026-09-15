using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Tests;

public sealed class ToolKinematicsSpecTest
{
    static string Lfam3()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "assets", "cells", "LFAM3", "lfam3.json");
    }

    [Fact]
    public void T12_uses_cutter_position_and_taught_abc()
    {
        var cell = CellLoader.Load(Lfam3());
        var t12 = cell.EffectiveTools.First(t => t.KrlIndex == 12);
        var spec = ToolKinematicsSpec.FromTool(t12);
        Assert.Equal(IkTcpSource.MillCollet, spec.PositionSource);
        Assert.Equal(IkOrientSource.TaughtAbc, spec.OrientSource);
        Assert.Equal(TriadSource.WorldUpAtPath, spec.TriadSource);
        Assert.Equal(0f, spec.HolderYawDeg);
    }

    [Fact]
    public void Extruder_uses_taught_xyz_and_abc()
    {
        var cell = CellLoader.Load(Lfam3());
        var t1 = cell.EffectiveTools.First(t => t.KrlIndex == 1);
        var spec = ToolKinematicsSpec.FromTool(t1);
        Assert.Equal(IkTcpSource.TaughtXyz, spec.PositionSource);
        Assert.Equal(IkOrientSource.TaughtAbc, spec.OrientSource);
        Assert.Equal(TriadSource.ToolFrame, spec.TriadSource);
        Assert.Equal(90f, spec.HolderYawDeg);
    }

    [Fact]
    public void Spindle_no_bit_uses_cutter_if_present_else_taught()
    {
        var cell = CellLoader.Load(Lfam3());
        var t2 = cell.EffectiveTools.First(t => t.KrlIndex == 2);
        var spec = ToolKinematicsSpec.FromTool(t2);
        Assert.Equal(IkTcpSource.SpindleCutter, spec.PositionSource);
    }
}
