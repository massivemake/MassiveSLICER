using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class CellWaypointTest
{
    [Fact]
    public void Lfam3_Loads_ScannerDownBed_Waypoint()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "assets", "cells", "LFAM3", "lfam3.json");
        Assert.True(File.Exists(path), $"Missing cell file: {path}");

        var cell = CellLoader.Load(path);
        var wp = CellLoader.FindWaypoint(cell, "scanner-down-bed");
        Assert.NotNull(wp);
        Assert.Equal(6, wp!.Tool);
        Assert.Equal(0, wp.Base);
        Assert.True(wp.PreferJoints);
        Assert.Equal(2093.6f, wp.TcpX, 1);
        Assert.Equal(131.6f, wp.TcpZ, 1);
        Assert.NotNull(wp.Joints);
        Assert.Equal(7, wp.Joints!.Length);
        Assert.Equal(197.0f, wp.Joints[5], 1);
        Assert.Contains("scan-cal", wp.Tags);
        Assert.Contains("bed-cal", wp.Tags);

        Assert.NotNull(CellLoader.FindWaypointByTag(cell, "bed-cal"));
        Assert.NotNull(CellLoader.FindWaypointByTag(cell, "scan-cal"));
        Assert.Equal("scanner-down-bed", CellLoader.FindWaypointByTag(cell, "bed-cal")!.Name);
    }

    [Fact]
    public void SaveWaypoint_Inserts_And_Replaces()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mslicer-wp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "test.json");
        var seed = new CellConfig
        {
            Name = "Test",
            Robot = new RobotCellConfig { ModelPath = "r.glb" },
            Bed = new BedCellConfig { Origin = Float3.Zero, BaseData = Float3.Zero },
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(seed));

        var wp1 = new CellWaypointConfig
        {
            Name = "pose-a",
            TcpX = 100,
            Tool = 6,
            Base = 0,
        };
        Assert.True(CellLoader.SaveWaypoint(path, wp1, out var err1), err1);

        var loaded = CellLoader.Load(path);
        Assert.Single(loaded.Waypoints);
        Assert.Equal("pose-a", loaded.Waypoints[0].Name);

        var wp2 = wp1 with { TcpX = 200, Description = "updated" };
        Assert.True(CellLoader.SaveWaypoint(path, wp2, out var err2), err2);

        loaded = CellLoader.Load(path);
        Assert.Single(loaded.Waypoints);
        Assert.Equal(200f, loaded.Waypoints[0].TcpX);
        Assert.Equal("updated", loaded.Waypoints[0].Description);
    }
}