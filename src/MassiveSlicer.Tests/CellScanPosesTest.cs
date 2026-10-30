using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// Scan poses live in *.scanposes.json. A cell JSON overwrite must not drop them,
/// and saving them must not rewrite the cell.
/// </summary>
public class CellScanPosesTest
{
    [Fact]
    public void Missing_sidecar_is_the_overview_pose()
    {
        var dir = TempDir();
        var cellPath = Path.Combine(dir, "lfam3.json");
        File.WriteAllText(cellPath, "{\"name\":\"cell\"}");
        try
        {
            var loaded = CellLoader.LoadScanPoses(cellPath);
            var overview = Assert.Single(loaded.Poses);
            Assert.Equal(ScanPoses.OverviewName, overview.Name);
            Assert.Equal(ScanPoses.OverviewAngles, overview.Angles);
            Assert.Equal(ScanPoses.OverviewName, loaded.Selected);
            Assert.False(File.Exists(CellLoader.ScanPosesSidecarPath(cellPath)));
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void Saved_poses_survive_cell_json_being_replaced_and_do_not_change_it()
    {
        var dir = TempDir();
        var cellPath = Path.Combine(dir, "lfam3.json");
        File.WriteAllText(cellPath, "{\"name\":\"original\"}");
        var before = File.ReadAllBytes(cellPath);
        try
        {
            var file = CellLoader.LoadScanPoses(cellPath);
            file.Poses.Add(new ScanPoseConfig { Name = "Side", Angles = [1f, 2f, 3f, 4f, 5f, 6f, 7f] });
            file.Poses.Add(new ScanPoseConfig { Name = "High", Angles = [8f, -1f, 2f, 3f, 4f, 5f] });
            file.Selected = "High";
            // Replacing Overview stores the operator's joints, not the built-in.
            int overview = file.Poses.FindIndex(p => p.Name == ScanPoses.OverviewName);
            file.Poses[overview] = new ScanPoseConfig
            {
                Name = ScanPoses.OverviewName,
                Angles = [9f, -8f, 7f, 6f, 5f, 4f, 3f],
            };

            CellLoader.SaveScanPoses(cellPath, file);

            Assert.Equal(before, File.ReadAllBytes(cellPath));
            Assert.False(File.Exists(Path.Combine(dir, "lfam3.homes.json")));

            File.WriteAllText(cellPath, "{\"name\":\"replaced\"}");

            var reloaded = CellLoader.LoadScanPoses(cellPath);
            Assert.Equal("High", reloaded.Selected);
            Assert.Equal(9f, Pose(reloaded, ScanPoses.OverviewName).Angles[0]);
            Assert.Equal(7f, Pose(reloaded, "Side").Angles[6]);
            Assert.Equal(6, Pose(reloaded, "High").Angles.Length);
            Assert.Equal(8f, Pose(reloaded, "High").Angles[0]);
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void Broken_sidecar_falls_back_to_overview()
    {
        var dir = TempDir();
        var cellPath = Path.Combine(dir, "lfam3.json");
        File.WriteAllText(cellPath, "{}");
        File.WriteAllText(CellLoader.ScanPosesSidecarPath(cellPath), "{ not json");
        try
        {
            var loaded = CellLoader.LoadScanPoses(cellPath);
            Assert.Equal(ScanPoses.OverviewName, Assert.Single(loaded.Poses).Name);
        }
        finally
        {
            Delete(dir);
        }
    }

    [Fact]
    public void Go_to_pose_only_reports_the_simulated_joints()
    {
        var scan = new ScanSettingsViewModel();
        var applied = new List<float[]>();
        scan.OnApplyScanPose = angles => applied.Add(angles);
        scan.ReloadScanPoses(null);

        Assert.Contains(ScanPoses.OverviewName, scan.AvailableScanPoseNames);
        scan.SelectedScanPoseName = ScanPoses.OverviewName;
        Assert.Empty(applied);

        scan.GoToScanPoseCommand.Execute(null);

        var once = Assert.Single(applied);
        Assert.Equal(ScanPoses.OverviewAngles, once);
        Assert.Contains("live arm was not moved", scan.ScanPoseStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_stores_simulated_joints_and_refuses_without_a_cell()
    {
        var scan = new ScanSettingsViewModel();
        var applied = new List<float[]>();
        scan.OnApplyScanPose = angles => applied.Add(angles);
        scan.CaptureSimJoints = () => [4f, -3f, 2f, -1f, 5f, 6f, 12f];
        scan.NewScanPoseName = "Booth";
        scan.SaveScanPoseCommand.Execute(null);
        Assert.Contains("No cell loaded", scan.ScanPoseStatus, StringComparison.Ordinal);
        Assert.Empty(applied);

        var dir = TempDir();
        var cellPath = Path.Combine(dir, "lfam3.json");
        File.WriteAllText(cellPath, "{\"name\":\"cell\"}");
        var before = File.ReadAllBytes(cellPath);
        try
        {
            scan.ActiveCellPath = () => cellPath;
            scan.SaveScanPoseCommand.Execute(null);

            Assert.Equal(before, File.ReadAllBytes(cellPath));
            Assert.Contains("Booth", scan.AvailableScanPoseNames);
            var saved = Pose(CellLoader.LoadScanPoses(cellPath), "Booth");
            Assert.Equal(12f, saved.Angles[6]);
            Assert.Empty(applied);

            scan.GoToNamedScanPose("Booth");
            Assert.Equal(4f, Assert.Single(applied)[0]);
            Assert.Contains("live arm was not moved", scan.ScanPoseStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Delete(dir);
        }
    }

    static ScanPoseConfig Pose(ScanPoseFile file, string name)
        => file.Poses.First(p => p.Name == name);

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mslicer-scanpose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }
}
