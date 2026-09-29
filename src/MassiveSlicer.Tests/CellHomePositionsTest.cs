using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// Named homes were saved 3–4 times then wiped: SavePositionData wrote the
/// running bin/Release copy with File.WriteAllText (silent on failure), and a
/// cell reload / PreserveNewest copy of source lfam3.json replaced the dropdown.
/// </summary>
public class CellHomePositionsTest
{
    [Fact]
    public void Four_saved_homes_survive_cell_json_being_replaced()
    {
        var src = ResolveSourceLfam3();
        Assert.True(File.Exists(src), $"missing {src}");
        var dir = Path.Combine(Path.GetTempPath(), $"mslicer-homes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var cellPath = Path.Combine(dir, "lfam3.json");
        File.Copy(src, cellPath);

        try
        {
            var data = CellLoader.LoadPositionData(cellPath);
            for (int i = 1; i <= 4; i++)
            {
                var name = $"Home Target {i}";
                var angles = new float[] { i, -90, 90, 0, 0, 15, 0 };
                int idx = data.Positions.FindIndex(p => p.Name == name);
                var cfg = new HomePositionConfig { Name = name, Angles = angles };
                if (idx >= 0) data.Positions[idx] = cfg;
                else data.Positions.Add(cfg);
            }

            CellLoader.SavePositionData(cellPath, data);

            // PreserveNewest / git checkout of source cell JSON (builtins only).
            File.Copy(src, cellPath, overwrite: true);

            var reloaded = CellLoader.LoadPositionData(cellPath);
            for (int i = 1; i <= 4; i++)
            {
                var name = $"Home Target {i}";
                var hit = reloaded.Positions.FirstOrDefault(p => p.Name == name);
                Assert.NotNull(hit);
                Assert.Equal(i, hit!.Angles[0]);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void UpdateFromCell_keeps_homes_the_user_just_saved()
    {
        var add = new AdditiveSettingsViewModel();
        var cell = new CellConfig
        {
            Name = "LFAM 3",
            Robot = new RobotCellConfig
            {
                ModelPath = "x",
                HomePositions =
                [
                    new HomePositionConfig { Name = "LFAM 3 Start", Angles = [0, -90, 90, 0, 0, 15, 0] },
                ],
                DefaultHomePosition = "LFAM 3 Start",
            },
            Bed = new BedCellConfig { Origin = Float3.Zero, BaseData = Float3.Zero },
        };

        add.UpdateFromCell(cell, "LFAM 3 Start", cell.Robot.HomePositions);
        add.AddHomePosition("Home Target 1", [1, -90, 90, 0, 0, 15, 0]);
        add.AddHomePosition("Home Target 2", [2, -90, 90, 0, 0, 15, 0]);
        add.AddHomePosition("Home Target 3", [3, -90, 90, 0, 0, 15, 0]);
        add.AddHomePosition("Home Target 4", [4, -90, 90, 0, 0, 15, 0]);

        // Cell swap reloads builtins from disk that never got the new names.
        add.UpdateFromCell(cell, "LFAM 3 Start", cell.Robot.HomePositions);

        Assert.Contains("Home Target 1", add.AvailableHomePositionNames);
        Assert.Contains("Home Target 2", add.AvailableHomePositionNames);
        Assert.Contains("Home Target 3", add.AvailableHomePositionNames);
        Assert.Contains("Home Target 4", add.AvailableHomePositionNames);
        Assert.Contains("LFAM 3 Start", add.AvailableHomePositionNames);
    }

    static string ResolveSourceLfam3()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "assets", "cells", "LFAM3", "lfam3.json");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrEmpty(parent)) break;
            dir = parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "assets", "cells", "LFAM3", "lfam3.json"));
    }
}
