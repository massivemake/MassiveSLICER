using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public sealed class Lfam3MillingConfigTest
{
    static string Lfam3JsonPath()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "assets", "cells", "LFAM3", "lfam3.json");
    }

    [Fact]
    public void Lfam3_json_has_milling_bridge_config()
    {
        var path = Lfam3JsonPath();
        Assert.True(File.Exists(path), $"Missing cell file: {path}");

        var cell = CellLoader.Load(path);
        Assert.True(cell.HasMilling);
        Assert.Equal("192.168.0.249", cell.MillIp);
        Assert.Equal(8765, cell.MillBridgePort);
    }

    [Fact]
    public void Lfam3_json_has_kuka_tool_12_from_controller()
    {
        var path = Lfam3JsonPath();
        Assert.True(File.Exists(path), $"Missing cell file: {path}");

        var cell = CellLoader.Load(path);
        var t12 = Assert.Single(cell.EffectiveTools, t => t.KrlIndex == 12);
        Assert.Equal("Tool 12", t12.Name);
        Assert.Equal("assets/cells/LFAM3/Toolheads/spindle.glb", t12.ModelPath);
        Assert.Equal(-78.399f, t12.TcpX, 3);
        Assert.Equal(325.229f, t12.TcpY, 3);
        Assert.Equal(637.358f, t12.TcpZ, 3);
        Assert.Equal(103.677f, t12.TcpA, 3);
        Assert.Equal(-43.719f, t12.TcpB, 3);
        Assert.Equal(40.483f, t12.TcpC, 3);
    }
}
