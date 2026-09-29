using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

/// <summary>
/// LFAM1 KR120 R3900 A4 rated speed is $VEL_AXIS_MA[4]=5980 rpm over
/// $RAT_MOT_AX[4]=10387/55. KRC4 IPO is 12 ms. Rev142 stopped 2026-09-29
/// when one IPO commanded 2.56 deg of A4 (8.42 rad motor &gt; 8.31).
/// </summary>
public class AxisMotionLimitsTest
{
    [Fact]
    public void Rev142_A4_step_exceeds_rated_speed()
    {
        var v = AxisMotionLimits.CheckJointRate(
            deltaDeg: 2.56f,
            dtSec: 0.012f,
            maxDegPerSec: AxisMotionLimits.Kr120R3900DegPerSec[3]);

        Assert.True(v.Exceeded);
        Assert.Equal(3, v.Axis);
        Assert.True(v.RateDegPerSec > 200f);
    }

    [Fact]
    public void Same_bead_is_legal_when_the_wrist_stays_off_zero()
    {
        float dt = 6.14f / 80f;
        float a4 = AxisMotionLimits.Kr120R3900DegPerSec[3];
        var stuck = AxisMotionLimits.CheckWrist(1f, dt, a5Deg: 1f, a4);
        var clear = AxisMotionLimits.CheckWrist(1f, dt, a5Deg: 40f, a4);

        Assert.True(stuck.Exceeded);
        Assert.False(clear.Exceeded);
        Assert.True(AxisMotionLimits.NeedsNewArm(
            AxisMotionLimits.Scan(
                [[0f, -90f, 90f, 0f, 1f, 0f], [0f, -90f, 90f, 1f, 1f, 0f]],
                [dt],
                [1f])));
    }

    [Fact]
    public void Over_limit_step_is_not_shipped_as_a_slower_bead()
    {
        var scan = AxisMotionLimits.Scan(
            [[0f, -90f, 90f, 90f, 30f, 0f], [0f, -90f, 90f, 92.56f, 28f, 0f]],
            [0.012f],
            [0.1f]);

        Assert.True(AxisMotionLimits.NeedsNewArm(scan));
        var move = new ToolpathMove(default, new System.Numerics.Vector3(6, 0, 0), MoveKind.Extrude);
        Assert.Equal(1f, move.PrintSpeedScale);
    }

    [Fact]
    public void Legal_joint_step_is_not_a_speed_limit()
    {
        var v = AxisMotionLimits.CheckJointRate(
            0.2f, 0.012f, AxisMotionLimits.Kr120R3900DegPerSec[3]);

        Assert.False(v.Exceeded);
        Assert.Equal(1f, v.RequiredScale);
    }

    [Fact]
    public void Wrist_at_one_degree_cannot_hold_bead_orientation_at_a_legal_speed()
    {
        // Bead just before the stop: 1 deg of A over 6.14 mm at 80 mm/s.
        // At A5 = 1 deg the wrist bound stays over A4 at print speed.
        // A wrist that stays off zero can hold the same bead.
        var v = AxisMotionLimits.CheckWrist(
            orientDeg: 1f,
            dtSec: 6.14f / 80f,
            a5Deg: 1f,
            a4MaxDegPerSec: AxisMotionLimits.Kr120R3900DegPerSec[3]);

        Assert.True(v.Exceeded);
        Assert.False(v.Repairable);
        Assert.True(v.RequiredScale < AxisMotionLimits.MinRepairScale);
    }

    [Fact]
    public void Wrist_span_the_joint_step_misses_is_still_refused()
    {
        // Joints barely move, so a joint-rate check alone would pass.
        // A5 is 1 deg and the bead still asks for 1 deg of orientation.
        float[] start = [0f, -90f, 90f, 0f, 1f, 0f];
        float[] end = [0f, -90f, 90f, 1f, 1f, 0f];
        var scan = AxisMotionLimits.Scan([start, end], [6.14f / 80f], [1f]);

        Assert.Equal(1, scan.UnrepairableCount);
        Assert.Equal(0, scan.FirstIndex);
        Assert.Equal(0f, scan.Scale[0]);
    }

    [Fact]
    public void Joint_past_software_limit_is_not_cleared_by_slowing()
    {
        var v = AxisMotionLimits.CheckPosition(-130f, -129f, 0f);

        Assert.True(v.Exceeded);
        Assert.False(v.Repairable);
        Assert.Equal(0f, v.RequiredScale);
    }
}
