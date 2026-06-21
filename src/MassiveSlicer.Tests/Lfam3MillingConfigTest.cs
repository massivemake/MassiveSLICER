using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public sealed class Lfam3MillingConfigTest
{
    [Fact]
    public void Lfam3_json_has_milling_bridge_config()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "assets", "cells", "LFAM3", "lfam3.json");
        Assert.True(File.Exists(path), $"Missing cell file: {path}");

        var cell = CellLoader.Load(path);
        Assert.True(cell.HasMilling);
        Assert.Equal("192.168.0.249", cell.MillIp);
        Assert.Equal(8765, cell.MillBridgePort);
    }
}