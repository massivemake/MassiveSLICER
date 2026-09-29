using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Viewport.Validation;

namespace MassiveSlicer.Tests;

/// <summary>
/// The nozzle-spin repair must never turn a reachable move unreachable.
/// feature/axis-speed-limits e47cbb4 did: a spin was picked on 3 points
/// (s0, mid, s1), then written across the whole ramp and re-solved one shot.
/// A ramp move that failed was marked unreachable (COW MID REPRINT 2:
/// 0 -> 155 unreachable).
/// </summary>
public class SpinRepairTest
{
    const int Total = 200;
    const int S0 = 100, S1 = 101;
    const float MoveMs = 100f;

    static float[] Pose(float a4 = 0f, float a5 = 30f, float a6 = 0f)
        => [0f, -90f, 90f, a4, a5, a6];

    sealed class Case
    {
        public bool[] Reachable = Enumerable.Repeat(true, Total).ToArray();
        public bool[] Singular = new bool[Total];
        public bool[] SpeedRisk = new bool[Total];
        public float[][] Solutions = Enumerable.Range(0, Total).Select(_ => Pose()).ToArray();
        public float[] Times = Enumerable.Repeat(MoveMs, Total).ToArray();
        public float[] Cos = Enumerable.Repeat(1f, Total + 1).ToArray();
        public float[] Yaw = new float[Total];

        public Case()
        {
            SpeedRisk[S0] = true;
            SpeedRisk[S1] = true;
        }

        public int Run(Func<int, float, float[], float[]?> solve)
            => ToolpathFeasibilityEvaluator.RepairSpansWithSpin(
                Reachable, Singular, SpeedRisk, Solutions, Times, Cos, Yaw, solve);

        public int ReachableCount => Reachable.Count(r => r);
    }

    /// <summary>
    /// +20 solves at s0, mid, s1 but not at one ramp move. -20 solves everywhere.
    /// </summary>
    static float[]? RampTrap(int i, float yaw, float[] _)
        => yaw > 0f && i == 50 ? null : Pose(a6: yaw);

    /// <summary>The selection e47cbb4 shipped: 3 points, A4 only.</summary>
    static float OldThreePointChoice(Func<int, float, float[], float[]?> solve, float[][] solutions)
    {
        foreach (float mag in ToolpathFeasibilityEvaluator.SpinMagnitudesDeg)
            foreach (float sgn in new[] { 1f, -1f })
            {
                float y = mag * sgn;
                bool ok = true;
                foreach (int ti in new[] { S0, (S0 + S1) / 2, S1 })
                {
                    var prev = solutions[ti - 1];
                    var sol = solve(ti, y, prev);
                    if (sol is null || MathF.Abs(sol[4]) < ToolpathFeasibilityEvaluator.SpinMinA5) { ok = false; break; }
                    if (AxisMotionLimits.CheckJointRate(sol[3] - prev[3], MoveMs / 1000f,
                            AxisMotionLimits.Kr120R3900DegPerSec[3], 3).Exceeded) { ok = false; break; }
                }
                if (ok) return y;
            }
        return 0f;
    }

    [Fact]
    public void Old_three_point_test_picks_a_spin_that_breaks_the_ramp()
    {
        var c = new Case();
        float chosen = OldThreePointChoice(RampTrap, c.Solutions);

        Assert.Equal(20f, chosen);
        int rIn = S0 - ToolpathFeasibilityEvaluator.SpinRamp;
        int rOut = S1 + ToolpathFeasibilityEvaluator.SpinRamp;
        float w = ToolpathFeasibilityEvaluator.SpinRampWeight(50, S0, S1, rIn, rOut);
        Assert.Null(RampTrap(50, chosen * w, Pose()));
    }

    [Fact]
    public void Full_range_test_skips_that_spin_and_keeps_every_move_reachable()
    {
        var c = new Case();
        int before = c.ReachableCount;

        int repaired = c.Run(RampTrap);

        Assert.Equal(1, repaired);
        Assert.Equal(before, c.ReachableCount);
        Assert.Equal(-20f, c.Yaw[S0]);
        Assert.True(c.Yaw[50] < 0f);
        Assert.False(c.SpeedRisk[S0]);
        Assert.False(c.SpeedRisk[S1]);
    }

    [Fact]
    public void No_legal_spin_leaves_the_span_as_it_was_and_still_flagged()
    {
        var c = new Case();
        var original = c.Solutions.Select(s => (float[])s.Clone()).ToArray();

        int repaired = c.Run((i, yaw, _) => yaw != 0f && i >= S0 && i <= S1 ? null : Pose(a6: yaw));

        Assert.Equal(0, repaired);
        Assert.Equal(Total, c.ReachableCount);
        Assert.True(c.SpeedRisk[S0]);
        Assert.All(c.Yaw, y => Assert.Equal(0f, y));
        for (int i = 0; i < Total; i++)
            Assert.Equal(original[i], c.Solutions[i]);
    }

    [Fact]
    public void Unreachable_move_the_spin_cannot_fix_stays_unreachable_not_worse()
    {
        var c = new Case();
        c.Reachable[S0] = false;
        int before = c.ReachableCount;

        c.Run((i, yaw, _) => i == S0 ? null : Pose(a6: yaw));

        Assert.Equal(before, c.ReachableCount);
        Assert.False(c.Reachable[S0]);
    }

    [Fact]
    public void Spin_that_winds_A6_over_its_rated_speed_is_rejected()
    {
        // +20 keeps A4 still but walks A6 100x the spin: 20/60 deg of yaw per
        // 100 ms move is 333 deg/s of A6 (limit 221). A4-only testing passed it.
        var c = new Case();
        float[]? Solve(int i, float yaw, float[] _) => Pose(a6: yaw > 0f ? yaw * 100f : yaw);

        Assert.Equal(20f, OldThreePointChoice(Solve, c.Solutions));

        c.Run(Solve);

        Assert.Equal(-20f, c.Yaw[S0]);
    }

    [Fact]
    public void Repaired_span_does_not_trip_speed_on_the_way_back_to_the_path()
    {
        // Every spin solves, but on a branch whose A4 creeps to 20 deg across the
        // ramp. The step back onto the untouched move after the ramp is then
        // 20 deg in 100 ms (200 deg/s, limit 161). Nothing may be written.
        var c = new Case();
        int rIn = S0 - ToolpathFeasibilityEvaluator.SpinRamp;

        int repaired = c.Run((i, yaw, _) => Pose(a4: MathF.Min(20f, (i - rIn) * 0.5f), a6: yaw));

        Assert.Equal(0, repaired);
        Assert.True(c.SpeedRisk[S0]);
        Assert.All(c.Yaw, y => Assert.Equal(0f, y));
    }

    [Fact]
    public void Neighbouring_violation_on_the_ramp_does_not_block_the_repair()
    {
        // A step already over the limit sits on this span's ramp. It belongs to
        // another span; the spin leaves it no worse, so this span still repairs.
        var c = new Case();
        c.Solutions[70] = Pose(a4: 90f);
        c.Solutions[71] = Pose(a4: 90f);

        int repaired = c.Run((i, yaw, walk) => Pose(a4: i == 70 || i == 71 ? 90f : 0f, a6: yaw));

        Assert.Equal(1, repaired);
        Assert.Equal(20f, c.Yaw[S0]);
    }
}
