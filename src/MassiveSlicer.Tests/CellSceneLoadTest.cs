using MassiveSlicer.App;
using MassiveSlicer.Core.IO;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

public class CellSceneLoadTest(ITestOutputHelper output)
{
    [Fact]
    public void Lfam2_cell_loads_robot_and_bed_from_publish_layout()
    {
        var cellPath = ResolveCellJson("LFAM2", "lfam2.json");
        if (cellPath is null)
        {
            output.WriteLine("SKIP: lfam2.json not found.");
            return;
        }

        var payload = CellSceneLoader.Load(cellPath, RightPanelTab.Additive, default);
        var meshes  = CountPendingMeshes(payload.RobotBaseNode) + CountEnvironmentMeshes(payload);

        output.WriteLine($"RobotBase={payload.RobotBaseNode is not null} Bed={payload.BedNode is not null} meshes={meshes}");

        Assert.NotNull(payload.RobotBaseNode);
        Assert.NotNull(payload.BedNode);
        Assert.True(meshes > 0);
    }

    [Fact]
    public void Lfam1_cell_loads_robot_and_rail_from_publish_layout()
    {
        var cellPath = ResolveCellJson("LFAM1", "lfam1.json");
        if (cellPath is null)
        {
            output.WriteLine("SKIP: lfam1.json not found.");
            return;
        }

        var cell    = CellLoader.Load(cellPath);
        var payload = CellSceneLoader.Load(cellPath, RightPanelTab.Additive, default);
        var meshes  = CountPendingMeshes(payload.RobotBaseNode) + CountEnvironmentMeshes(payload);

        output.WriteLine($"Name={cell.Name} Rail={payload.BoosterNode is not null} RobotBase={payload.RobotBaseNode is not null} meshes={meshes}");

        Assert.Equal("LFAM 1", cell.Name);
        Assert.NotNull(payload.RobotBaseNode);
        Assert.NotNull(payload.BoosterNode);
        Assert.Contains("LFAM1Robot", cell.Robot.ModelPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LFAM1RobotRail", cell.BoosterFrame!.ModelPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lfam1_bed", cell.Bed.ModelPath!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(payload.BedNode);
        Assert.True(meshes > 0);
    }

    [Fact]
    public void Lfam3_cell_loads_robot_and_rotary_bed_from_publish_layout()
    {
        var cellPath = ResolveCellJson("LFAM3", "lfam3.json");
        if (cellPath is null)
        {
            output.WriteLine("SKIP: lfam3.json not found.");
            return;
        }

        var cell    = CellLoader.Load(cellPath);
        var payload = CellSceneLoader.Load(cellPath, RightPanelTab.Additive, default);
        var meshes  = CountPendingMeshes(payload.RobotBaseNode) + CountEnvironmentMeshes(payload);

        output.WriteLine($"BedHidden={cell.Bed.Hidden} RobotBase={payload.RobotBaseNode is not null} BedNode={payload.BedNode is not null} Env={payload.EnvironmentNodes.Count} meshes={meshes}");

        Assert.NotNull(payload.RobotBaseNode);
        Assert.Null(payload.BedNode);
        Assert.True(payload.EnvironmentNodes.Any(n => n.Name == "RotaryBed"));
        Assert.True(meshes > 0);
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

    private static int CountPendingMeshes(SceneNode? root)
    {
        if (root is null) return 0;
        int count = 0;
        foreach (var n in root.SelfAndDescendants())
            if (n.PendingMesh is not null) count++;
        return count;
    }

    private static int CountEnvironmentMeshes(CellSwapPayload payload)
    {
        int count = 0;
        foreach (var env in payload.EnvironmentNodes)
            count += CountPendingMeshes(env);
        if (payload.BedNode is not null)
            count += CountPendingMeshes(payload.BedNode);
        if (payload.BoosterNode is not null)
            count += CountPendingMeshes(payload.BoosterNode);
        return count;
    }
}