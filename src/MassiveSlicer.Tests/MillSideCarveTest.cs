using System.Diagnostics;
using System.Numerics;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public sealed class MillSideCarveTest
{
    static MillSideCarve.Cylinder Up(Vector3 tip, float radius = 20f, float length = 40f, float slab = 1f)
        => new(tip, Vector3.UnitZ, radius, length, slab);

    [Fact]
    public void Upright_shank_points_back_toward_the_spindle()
    {
        var shank = MillSideCarve.ShankDirection(Vector3.UnitZ, 0f, 0f, 0f, 0f, flip: false);
        Assert.True(Vector3.Dot(shank, Vector3.UnitZ) > 0.999f);

        var (_, _, cutter) = KukaOrientation.MillFrame(Vector3.UnitZ);
        Assert.True(Vector3.Dot(cutter, -Vector3.UnitZ) > 0.999f);
    }

    [Fact]
    public void Flip_sends_the_cylinder_along_the_cutter()
    {
        var shank = MillSideCarve.ShankDirection(Vector3.UnitZ, 0f, 0f, 0f, 0f, flip: true);
        Assert.True(Vector3.Dot(shank, -Vector3.UnitZ) > 0.999f);
    }

    [Fact]
    public void Side_hit_is_inside_and_the_cutting_face_is_not()
    {
        var bit = Up(Vector3.Zero, radius: 38f, length: 50f, slab: 1f);

        Assert.False(MillSideCarve.InsideShank(new Vector3(0f, 0f, 0f), bit));
        Assert.False(MillSideCarve.InsideShank(new Vector3(4f, 0f, 1f), bit));
        Assert.True(MillSideCarve.InsideShank(new Vector3(4f, 0f, 8f), bit));
        Assert.True(MillSideCarve.InsideShank(new Vector3(37f, 0f, 20f), bit));
        Assert.False(MillSideCarve.InsideShank(new Vector3(39f, 0f, 20f), bit));
        Assert.False(MillSideCarve.InsideShank(new Vector3(0f, 0f, 51f), bit));
    }

    [Fact]
    public void Grid_keeps_the_tip_plane_and_removes_a_side_hit()
    {
        var bit = Up(Vector3.Zero, radius: 20f, length: 30f, slab: 1f);
        var grid = MillSideCarveGrid.Build([bit], new Vector3(-30f, -30f, -2f), new Vector3(30f, 30f, 34f));

        Assert.False(grid.IsOccupied(new Vector3(0f, 0f, 0.2f)));
        Assert.False(grid.IsOccupied(new Vector3(4f, 0f, 0.4f)));
        Assert.True(grid.IsOccupied(new Vector3(0f, 0f, 12f)));
        Assert.True(grid.IsOccupied(new Vector3(10f, 0f, 12f)));
        Assert.False(grid.IsOccupied(new Vector3(28f, 0f, 12f)));
        Assert.False(grid.IsOccupied(new Vector3(0f, 0f, 33f)));
    }

    [Fact]
    public void Lower_pass_eats_a_bead_up_the_shank_and_keeps_its_own_tip()
    {
        var path = new Toolpath();
        var low = new ToolpathLayer(0, 0f) { Height = 2f };
        low.Moves.Add(new ToolpathMove(new Vector3(-6f, 0f, 0f), new Vector3(6f, 0f, 0f), MoveKind.Mill));
        var high = new ToolpathLayer(1, 8f) { Height = 2f };
        high.Moves.Add(new ToolpathMove(new Vector3(-6f, 0f, 8f), new Vector3(6f, 0f, 8f), MoveKind.Mill));
        var travel = new ToolpathLayer(2, 8f) { Height = 2f };
        travel.Moves.Add(new ToolpathMove(new Vector3(0f, 0f, 30f), new Vector3(40f, 0f, 30f), MoveKind.Travel));
        path.Layers.Add(low);
        path.Layers.Add(high);
        path.Layers.Add(travel);

        var grid = MillSideCarve.BuildForPath(path, Vector3.Zero, null, -1f, radius: 15f, length: 40f,
            nominalLayerHeight: 2f, 0f, 0f, 0f, flip: false);

        Assert.True(grid.OccupiedCount > 0);
        Assert.True(grid.IsOccupied(new Vector3(0f, 0f, 8f)));
        Assert.False(grid.IsOccupied(new Vector3(0f, 0f, 0.2f)));
        Assert.False(grid.IsOccupied(new Vector3(0f, 0f, 30f)));
    }

    [Fact]
    public void Ball_end_scoops_a_round_nose_and_keeps_the_floor_beside_it()
    {
        // Tip is the bottom of the ball. The center sits one radius up the shank.
        var bit = new MillSideCarve.Cylinder(Vector3.Zero, Vector3.UnitZ, 10f, 40f, 1f, Ball: true);

        Assert.True(MillSideCarve.InsideShank(new Vector3(0f, 0f, 0f), bit));
        Assert.True(MillSideCarve.InsideShank(new Vector3(0f, 0f, 5f), bit));
        Assert.True(MillSideCarve.InsideShank(new Vector3(6f, 0f, 2f), bit));
        Assert.True(MillSideCarve.InsideShank(new Vector3(0f, 0f, 25f), bit));
        Assert.False(MillSideCarve.InsideShank(new Vector3(0f, 0f, -1f), bit));
        Assert.False(MillSideCarve.InsideShank(new Vector3(8f, 0f, 0.2f), bit));
        Assert.False(MillSideCarve.InsideShank(new Vector3(11f, 0f, 20f), bit));

        var grid = MillSideCarveGrid.Build(
            [bit], new Vector3(-20f, -20f, -4f), new Vector3(20f, 20f, 44f));
        // Voxels are the shank above the equator. The nose is not a stair-stepped ball.
        Assert.False(grid.IsOccupied(new Vector3(0f, 0f, 5f)));
        Assert.True(grid.IsOccupied(new Vector3(0f, 0f, 25f)));
        Assert.False(grid.IsOccupied(new Vector3(0f, 0f, -2f)));
        Assert.False(grid.HasAnalytic);

        var center = Vector3.UnitZ * bit.Radius;
        grid.AttachNoses([new MillSideCarve.Nose(center, center, bit.Radius)]);
        Assert.True(grid.AnalyticContains(new Vector3(0f, 0f, 5f)));
        Assert.True(grid.AnalyticContains(new Vector3(4f, 0f, 3f)));
        Assert.False(grid.AnalyticContains(new Vector3(0f, 0f, -2f)));
        Assert.False(grid.AnalyticContains(new Vector3(8f, 0f, 0.2f)));
    }

    [Fact]
    public void Ball_capsule_matches_the_sphere_and_a_straight_sweep()
    {
        var center = new Vector3(0f, 0f, 10f);
        Assert.InRange(MillSideCarve.CapsuleDistance(Vector3.Zero, center, center, 10f), -0.02f, 0.02f);
        Assert.True(MillSideCarve.CapsuleDistance(new Vector3(0f, 0f, -1f), center, center, 10f) > 0.5f);
        Assert.True(MillSideCarve.CapsuleDistance(new Vector3(0f, 0f, 5f), center, center, 10f) < -1f);

        var a = new Vector3(0f, 0f, 10f);
        var b = new Vector3(20f, 0f, 10f);
        Assert.True(MillSideCarve.CapsuleDistance(new Vector3(10f, 0f, 10f), a, b, 10f) < -1f);
        Assert.InRange(MillSideCarve.CapsuleDistance(new Vector3(10f, 0f, 0f), a, b, 10f), -0.05f, 0.05f);
        Assert.True(MillSideCarve.CapsuleDistance(new Vector3(10f, 0f, -1f), a, b, 10f) > 0.5f);
    }

    [Fact]
    public void Neighbor_ball_scoops_the_overlap_and_keeps_the_floor_between_passes()
    {
        var grid = MillSideCarveGrid.Build(
            [], new Vector3(-8f, -8f, -4f), new Vector3(28f, 8f, 24f));
        var a = new Vector3(0f, 0f, 10f);
        var b = new Vector3(12f, 0f, 10f);
        grid.AttachNoses(
        [
            new MillSideCarve.Nose(a, a, 10f),
            new MillSideCarve.Nose(b, b, 10f),
        ]);

        Assert.True(grid.AnalyticContains(new Vector3(6f, 0f, 3f)));
        Assert.False(grid.AnalyticContains(new Vector3(6f, 0f, 0f)));
        Assert.False(grid.AnalyticContains(new Vector3(6f, 0f, -1f)));
        Assert.Equal(0, grid.AnalyticFullCells);
    }

    [Fact]
    public void Crowded_cell_keeps_the_sphere_that_actually_covers_the_point()
    {
        var grid = MillSideCarveGrid.Build(
            [], new Vector3(-16f, -16f, -4f), new Vector3(48f, 16f, 28f));
        var noses = new List<MillSideCarve.Nose>(200);
        for (int i = 0; i < 200; i++)
        {
            var c = new Vector3(i * 0.2f, 0f, 10f);
            noses.Add(new MillSideCarve.Nose(c, c, 10f));
        }
        grid.AttachNoses(noses);
        Assert.True(grid.AnalyticContains(new Vector3(0f, 0f, 2f)));
        Assert.False(grid.AnalyticContains(new Vector3(0f, 0f, -1f)));
    }

    [Fact]
    public void Ball_path_rounds_the_tip_and_does_not_fill_the_stepover_disk()
    {
        var path = new Toolpath();
        var low = new ToolpathLayer(0, 0f) { Height = 2f };
        low.Moves.Add(new ToolpathMove(new Vector3(-6f, 0f, 0f), new Vector3(6f, 0f, 0f), MoveKind.Mill));
        path.Layers.Add(low);

        var grid = MillSideCarve.BuildForPath(path, Vector3.Zero, null, -1f, radius: 15f, length: 40f,
            nominalLayerHeight: 2f, 0f, 0f, 0f, flip: false, ball: true);

        Assert.True(grid.HasAnalytic);
        Assert.True(grid.AnalyticContains(new Vector3(0f, 0f, 4f)));
        Assert.False(grid.AnalyticContains(new Vector3(0f, 0f, -2f)));
        Assert.False(grid.AnalyticContains(new Vector3(14f, 0f, 0.3f)));
        Assert.False(grid.IsOccupied(new Vector3(0f, 0f, 4f)));
    }

    [Fact]
    public void Sample_drops_a_colinear_midpoint_and_keeps_a_lean_change()
    {
        var straight = new Vector3(0f, 0f, 1f);
        var poses = new List<MillSideCarve.Cylinder>
        {
            new(new Vector3(0f, 0f, 0f), straight, 10f, 20f, 1f),
            new(new Vector3(3f, 0f, 0f), straight, 10f, 20f, 1f),
            new(new Vector3(12f, 0f, 0f), straight, 10f, 20f, 1f),
            new(Vector3.UnitX * 14f, Vector3.Normalize(new Vector3(0.4f, 0f, 1f)), 10f, 20f, 1f),
        };
        var kept = MillSideCarve.Sample(poses, 8f);
        Assert.Equal(3, kept.Count);
        Assert.Equal(0f, kept[0].Tip.X);
        Assert.Equal(12f, kept[1].Tip.X);
        Assert.Equal(14f, kept[2].Tip.X);
    }

    [Fact]
    public void Dense_tilted_overlap_finishes_quickly()
    {
        var axis = Vector3.Normalize(new Vector3(0.55f, 0.1f, 0.83f));
        var cylinders = new List<MillSideCarve.Cylinder>(20_000);
        for (int i = 0; i < 20_000; i++)
        {
            int x = i % 200;
            int y = i / 200;
            cylinders.Add(new MillSideCarve.Cylinder(new Vector3(x * 1.6f, y * 1.6f, 0f), axis, 28f, 36f, 1f));
        }
        var min = new Vector3(-40f, -40f, -4f);
        var max = new Vector3(360f, 180f, 50f);
        var watch = Stopwatch.StartNew();
        var grid = MillSideCarveGrid.Build(cylinders, min, max);
        watch.Stop();
        Assert.True(grid.OccupiedCount > 1000);
        Assert.True(watch.ElapsedMilliseconds < 12_000, $"carve grid took {watch.ElapsedMilliseconds} ms");
    }
}
