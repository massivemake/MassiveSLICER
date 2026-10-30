using MassiveSlicer.Core.Scanning;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

public class MagicScanPlannerTest
{
    [Fact]
    public void Detect_ignores_the_table_and_boxes_the_part()
    {
        var xyz = new List<float>();
        for (int i = 0; i < 40; i++)
        {
            xyz.Add(i);
            xyz.Add(0);
            xyz.Add(5);
        }
        for (int i = 0; i < 40; i++)
        {
            xyz.Add(100 + i);
            xyz.Add(200);
            xyz.Add(80 + (i % 5));
        }

        var box = MagicScanPlanner.Detect(xyz.ToArray(), bedZ: 0f, clearanceMm: 20f, minPoints: 30);
        Assert.NotNull(box);
        Assert.Equal(40, box!.Value.PointCount);
        Assert.InRange(box.Value.MinX, 99f, 101f);
        Assert.InRange(box.Value.MaxZ, 80f, 85f);
        Assert.False(MagicScanPlanner.NeedsSecondOverhead(box.Value, 500f));
    }

    [Fact]
    public void A_wide_part_asks_for_a_second_overhead()
    {
        var box = new MagicScanBox(0, 0, 30, 900, 200, 80, 100);
        Assert.True(MagicScanPlanner.NeedsSecondOverhead(box, 500f));
    }

    [Fact]
    public void Fine_path_stays_above_the_part_and_covers_a_small_box_in_one_station()
    {
        var box = new MagicScanBox(0, 0, 40, 100, 80, 90, 50);
        var stations = MagicScanPlanner.PlanFine(box, standoffMm: 600f, footprintMm: 400f, overlap: 0.3f);
        var only = Assert.Single(stations);
        Assert.Equal(50f, only.X, 1);
        Assert.Equal(40f, only.Y, 1);
        Assert.Equal(690f, only.Z, 1);
    }

    [Fact]
    public void Rough_pass_sits_higher_than_the_fine_standoff()
    {
        var box = new MagicScanBox(0, 0, 20, 800, 700, 120, 40);
        float raised = MagicScanPlanner.RaisedStandoffMm(box, fineStandoffMm: 600f);
        Assert.Equal(1000f, raised, 1f);
        Assert.True(raised > 600f);

        var attempts = MagicScanPlanner.StandoffAttempts(raised, 600f);
        Assert.Equal(raised, attempts[0], 1f);
        Assert.True(attempts[^1] < attempts[0]);
        Assert.InRange(attempts[^1], 450f, 600f);

        Assert.Equal(600f, MagicScanPlanner.CloserStandoffMm(raised, 600f), 1f);
        Assert.True(MagicScanPlanner.CloserStandoffMm(500f, 600f) < 500f);
    }

    [Fact]
    public void Rough_grid_covers_a_wide_part_from_above_and_not_from_one_center_shot()
    {
        var box = new MagicScanBox(0, 0, 20, 800, 700, 120, 40);
        var stations = MagicScanPlanner.PlanCoarse(box, standoffMm: 1000f);
        Assert.Equal(4, stations.Count);
        Assert.All(stations, s => Assert.Equal(1120f, s.Z, 1f));
        Assert.Contains(stations, s => MathF.Abs(s.X - 200f) < 2f && MathF.Abs(s.Y - 175f) < 2f);
        Assert.Contains(stations, s => MathF.Abs(s.X - 600f) < 2f && MathF.Abs(s.Y - 525f) < 2f);
        Assert.DoesNotContain(stations, s => MathF.Abs(s.X - 400f) < 2f && MathF.Abs(s.Y - 350f) < 2f);
    }

    [Fact]
    public void Rough_grid_of_a_part_larger_than_one_frame_still_reaches_the_edges()
    {
        var box = new MagicScanBox(0, 0, 0, 2200, 2200, 100, 80);
        var stations = MagicScanPlanner.PlanCoarse(box, standoffMm: 1000f);
        Assert.Equal(9, stations.Count);
        Assert.All(stations, s => Assert.True(s.Z >= box.Top + 1000f - 0.1f));
        Assert.Contains(stations, s => s.X < 50f);
        Assert.Contains(stations, s => s.X > 2150f);
    }

    [Fact]
    public void Triangle_bounds_follow_the_model()
    {
        float[] triangles =
        [
            10, 20, 30,
            110, 20, 40,
            10, 90, 50,
        ];
        var box = MagicScanPlanner.BoundsOfTriangles(triangles);
        Assert.NotNull(box);
        Assert.Equal(10f, box!.Value.MinX, 0.1f);
        Assert.Equal(110f, box.Value.MaxX, 0.1f);
        Assert.Equal(20f, box.Value.MinY, 0.1f);
        Assert.Equal(90f, box.Value.MaxY, 0.1f);
        Assert.Equal(30f, box.Value.MinZ, 0.1f);
        Assert.Equal(50f, box.Value.MaxZ, 0.1f);
    }

    [Fact]
    public void Fine_path_covers_a_large_top_without_entering_the_part()
    {
        var box = new MagicScanBox(-400, -200, 10, 400, 200, 120, 80);
        var stations = MagicScanPlanner.PlanFine(box, 500f, 300f, 0.25f);
        Assert.InRange(stations.Count, 2, MagicScanPlanner.MaxStations);
        Assert.All(stations, s => Assert.True(s.Z >= box.Top + 500f - 0.1f));
        Assert.Contains(stations, s => s.X < -100f);
        Assert.Contains(stations, s => s.X > 100f);
    }

    [Fact]
    public void Magic_scan_moves_the_viewport_only_when_the_robot_is_not_synced()
    {
        var scan = new ScanSettingsViewModel();
        int applied = 0;
        scan.OnApplyScanPose = _ => applied++;

        scan.MagicScanCommand.Execute(null);
        Assert.Equal(0, applied);
        Assert.True(scan.ShowMagicMove);
        Assert.Contains("simulated arm", scan.MagicScanStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not synced", scan.MagicScanStatus, StringComparison.OrdinalIgnoreCase);

        scan.MoveToOverheadCommand.Execute(null);
        Assert.Equal(1, applied);
        Assert.Contains("live arm was not moved", scan.MagicScanStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Continue_merges_and_localizes_without_another_arm_move()
    {
        var scan = new ScanSettingsViewModel();
        int applied = 0;
        int merges = 0;
        int aligns = 0;
        scan.OnApplyScanPose = _ => applied++;
        scan.TryMergeScans = () => { merges++; return true; };
        scan.AlignLocalizedModel = () => aligns++;
        scan.MagicScanMode = "Localization";
        scan.SetDetectedBoxForTest(new MagicScanBox(0, 0, 20, 100, 80, 90, 40));
        scan.PlanFineScanCommand.Execute(null);
        Assert.NotEmpty(scan.MagicScanStations);

        scan.ContinueMagicCommand.Execute(null);
        Assert.Equal(0, applied);
        Assert.Equal(1, merges);
        Assert.Equal(1, aligns);
        Assert.Contains("not sent to the arm", scan.MagicScanStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Part_recreation_does_not_align_a_model()
    {
        var scan = new ScanSettingsViewModel();
        int aligns = 0;
        scan.TryMergeScans = () => true;
        scan.AlignLocalizedModel = () => aligns++;
        scan.MagicScanMode = "Part recreation";
        scan.SetDetectedBoxForTest(new MagicScanBox(0, 0, 20, 80, 80, 40, 40));
        scan.PlanFineScanCommand.Execute(null);
        scan.ContinueMagicCommand.Execute(null);
        Assert.Equal(0, aligns);
        Assert.Contains("recreation", scan.MagicScanStatus, StringComparison.OrdinalIgnoreCase);
    }
}
