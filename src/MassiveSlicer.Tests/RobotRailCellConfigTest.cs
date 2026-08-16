using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class RobotRailCellConfigTest
{
    [Fact]
    public void Lfam1_rail_limits_match_controller()
    {
        var path = ResolveCellJson("LFAM1", "lfam1.json");
        if (path is null) return;

        var cell = MassiveSlicer.Core.IO.CellLoader.Load(path);
        var rail = cell.RobotRail;

        Assert.NotNull(rail);
        Assert.Equal("Y", rail!.Axis, ignoreCase: true);
        Assert.Equal(-4641f, rail.MinMm);
        Assert.Equal(150f, rail.MaxMm);

        // Verified against LFAM 1 on 2026-08-15: live RAM via C3Bridge (192.168.0.151:7000)
        // and KRC\R1\Mada\$machine.dat agree, and both match the smartPAD Software Limit
        // Switch screen. A2 min is -129, NOT -125 — this test asserted -125 while claiming to
        // match the controller, so it was holding the wrong number in place.
        var j = cell.Robot.Joints;
        Assert.True(j.Count >= 6);
        Assert.Equal(-70f, j[0].MinDeg);
        Assert.Equal(70f, j[0].MaxDeg);
        Assert.Equal(-129f, j[1].MinDeg);
        Assert.Equal(0f, j[1].MaxDeg);
        Assert.Equal(-120f, j[2].MinDeg);
        Assert.Equal(168f, j[2].MaxDeg);
        Assert.Equal(-350f, j[3].MinDeg);
        Assert.Equal(350f, j[3].MaxDeg);
        Assert.Equal(-125f, j[4].MinDeg);
        Assert.Equal(125f, j[4].MaxDeg);
        Assert.Equal(-350f, j[5].MinDeg);
        Assert.Equal(350f, j[5].MaxDeg);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1617.87, 1617.87)]
    [InlineData(150, -150)]
    public void SceneOffset_applies_e1Sign_on_Y(double e1, float expectedY)
    {
        var rail = new RobotRailCellConfig { Axis = "Y", E1Sign = -1f };
        var off  = rail.SceneOffsetMm(e1);
        Assert.Equal(expectedY, off.Y, 2);
        Assert.Equal(0f, off.X);
        Assert.Equal(0f, off.Z);
    }

    private static string? ResolveCellJson(string folder, string file)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "assets", "cells", folder, file),
            Path.Combine(FindRepoRoot() ?? "", "assets", "cells", folder, file),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "assets", "cells")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
            if (string.IsNullOrEmpty(dir)) break;
        }

        return null;
    }
}