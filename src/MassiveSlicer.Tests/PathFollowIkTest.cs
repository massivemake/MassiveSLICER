using MassiveSlicer.Viewport.Validation;

namespace MassiveSlicer.Tests;

/// <summary>
/// Curtain / SB101: 1.7M "unreachable" from Z 129 when the beads sit on the bed.
/// Cause: every parallel chunk started from the same scrub pose (timeline end).
/// </summary>
public class PathFollowIkTest
{
    // Fake DLS: "TCP" is the move index in seed[0]. Cannot jump more than 300 moves
    // in one solve — same failure mode as 6D from a folded-up pose to the bed.
    static float[]? SolveNear(int i, float[] seed)
    {
        if (Math.Abs(seed[0] - i) > 300) return null;
        return [i, seed[1], seed[2], seed[3], seed[4], seed[5]];
    }

    [Fact]
    public void Window_seeds_walk_from_home_so_late_windows_are_not_jumps()
    {
        const int total = 2000;
        const int stride = 256;
        var home = new float[] { 0, -90, 90, 0, 0, 15 };

        var keys = ToolpathFeasibilityEvaluator.BuildWindowSeeds(
            total, home, SolveNear, stride);

        Assert.Equal((total + stride - 1) / stride, keys.Length);
        for (int k = 0; k < keys.Length; k++)
            Assert.Equal(k * stride, keys[k][0], 3);
    }

    [Fact]
    public void Same_far_seed_on_every_window_cannot_reach_the_bed()
    {
        // Old behaviour: clone the timeline-end pose into every chunk.
        var endPose = new float[] { 1800, -90, 90, 0, 0, 15 };
        Assert.Null(SolveNear(0, endPose));
        Assert.Null(SolveNear(256, endPose));
        Assert.NotNull(SolveNear(1800, endPose));
    }

    [Fact]
    public void Empty_path_returns_no_windows()
    {
        Assert.Empty(ToolpathFeasibilityEvaluator.BuildWindowSeeds(
            0, [0, -90, 90, 0, 0, 15], SolveNear));
    }

    // Curtain / SB101 2026-09-16: Heated Bed Home (A1≈−59, folded wrist) is ~2.4 m
    // from layer-1 beads at ROBROOT XY (1586, 1677). DLS stagnates; window 0 keeps
    // that seed and every later window inherits the miss. Fallback must aim A1 at
    // the target and stretch to  −90/90/0/0/15  before giving up.
    [Fact]
    public void Fallback_seed_aims_A1_at_target_xy()
    {
        var seed = ToolpathFeasibilityEvaluator.PrintIkFallbackSeed(1586.18f, 1677.28f);
        Assert.Equal(6, seed.Length);
        Assert.InRange(seed[0], 46.0f, 47.2f); // atan2(1677.28, 1586.18) ≈ 46.6°
        Assert.Equal(-90f, seed[1]);
        Assert.Equal(90f, seed[2]);
        Assert.Equal(0f, seed[3]);
        Assert.Equal(0f, seed[4]);
        Assert.Equal(15f, seed[5]);
    }

    [Fact]
    public void Failed_home_retries_fallback_so_window_walk_can_start()
    {
        const int total = 512;
        const int stride = 256;
        var foldedHome = new float[] { -58.63f, -81.55f, 41.81f, -157.40f, -38.16f, 174.23f };

        float[]? SolveFrom(int i, float[] seed)
        {
            // Folded home cannot jump to the bed. Aimed A1 (~i as degrees) can.
            if (Math.Abs(seed[0] + 58.63f) < 0.2f) return null;
            if (Math.Abs(seed[1] + 90f) > 1f) return null;
            return [i, seed[1], seed[2], seed[3], seed[4], seed[5]];
        }

        var keys = ToolpathFeasibilityEvaluator.BuildWindowSeeds(
            total, foldedHome,
            (i, s) => ToolpathFeasibilityEvaluator.SolveWithPrintFallback(
                s,
                w => SolveFrom(i, w),
                ToolpathFeasibilityEvaluator.PrintIkFallbackSeeds(i, 1f)),
            stride);

        Assert.Equal(2, keys.Length);
        Assert.Equal(0, keys[0][0], 3);
        Assert.Equal(256, keys[1][0], 3);
    }

    [Fact]
    public void Print_rejects_position_only_when_orientation_is_sideways()
    {
        float[] pos = [0, -90, 90, -118, -18, 187];
        Assert.Null(ToolpathFeasibilityEvaluator.PreferOrientedPrintSolution(
            oriented: null, positionOnly: pos, orientErr: 1.0f, millPath: false));
        Assert.Same(pos, ToolpathFeasibilityEvaluator.PreferOrientedPrintSolution(
            oriented: null, positionOnly: pos, orientErr: 1.0f, millPath: true));
    }

    [Fact]
    public void Print_keeps_oriented_solve_within_threshold()
    {
        float[] sol = [46, -90, 90, 0, 90, 15];
        Assert.Same(sol, ToolpathFeasibilityEvaluator.PreferOrientedPrintSolution(
            sol, positionOnly: [0, -90, 90, -118, -18, 187],
            orientErr: 0.05f, millPath: false));
    }

    [Fact]
    public void Print_fallback_includes_nozzle_down_wrists()
    {
        var seeds = ToolpathFeasibilityEvaluator.PrintIkFallbackSeeds(1586.18f, 1677.28f);
        Assert.Equal(4, seeds.Length);
        Assert.Equal(90f, seeds[2][4]);
        Assert.Equal(-90f, seeds[3][4]);
    }

    [Fact]
    public void Cached_scrub_joints_are_skipped_when_toolhead_orientation_changed()
    {
        Assert.False(ToolpathFeasibilityEvaluator.UseCachedScrubJoints(
            hasCache: true, toolheadOrientationDirty: true));
        Assert.True(ToolpathFeasibilityEvaluator.UseCachedScrubJoints(
            hasCache: true, toolheadOrientationDirty: false));
        Assert.False(ToolpathFeasibilityEvaluator.UseCachedScrubJoints(
            hasCache: false, toolheadOrientationDirty: false));
    }
}
