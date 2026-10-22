using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class JointLimitEnvelopeTest
{
    [Fact]
    public void Inset_lfam1_A1_is_five_percent_of_travel()
    {
        var (lo, hi) = JointLimitEnvelope.Inset(-70f, 70f);
        Assert.Equal(-63f, lo, 3);
        Assert.Equal(63f, hi, 3);
    }

    [Fact]
    public void Inset_lfam1_A2_asymmetric()
    {
        var (lo, hi) = JointLimitEnvelope.Inset(-125f, 0f);
        Assert.Equal(-118.75f, lo, 3);
        Assert.Equal(-6.25f, hi, 3);
    }

    [Fact]
    public void Clamp_stays_inside_envelope_not_raw_stop()
    {
        Assert.Equal(63f, JointLimitEnvelope.Clamp(70f, -70f, 70f), 3);
        Assert.Equal(-63f, JointLimitEnvelope.Clamp(-70f, -70f, 70f), 3);
    }

    [Fact]
    public void A5_turn_outside_the_stops_folds_back_to_the_same_wrist_pose()
    {
        // LFAM 3 A5 software stops are ±125°. Validation stores the continuous
        // twin (248° is −112° plus one turn). Clamping that twin to +112.5°
        // is the other wrist stop and lifts the cutter off the part.
        const float min = -125f, max = 125f;
        Assert.Equal(-111.68f, JointLimitEnvelope.FoldIntoSoftLimits(248.32f, min, max), 2);
        Assert.Equal(-14.22f, JointLimitEnvelope.FoldIntoSoftLimits(345.78f, min, max), 2);
        Assert.Equal(112.5f, JointLimitEnvelope.FoldIntoSoftLimits(112.5f, min, max), 2);
        Assert.Equal(-112.5f, JointLimitEnvelope.FoldIntoSoftLimits(-112.5f, min, max), 2);
        // No congruent turn sits inside ±125°. Stay on the envelope stop.
        Assert.Equal(112.5f, JointLimitEnvelope.FoldIntoSoftLimits(200f, min, max), 2);
    }

    [Fact]
    public void JointConfig_Clamp_uses_envelope()
    {
        var j = new JointConfig { MinDeg = -70, MaxDeg = 70 };
        Assert.Equal(63f, j.Clamp(90f), 3);
        Assert.True(JointLimitEnvelope.Contains(0f, j.MinDeg, j.MaxDeg));
        Assert.False(JointLimitEnvelope.Contains(69f, j.MinDeg, j.MaxDeg));
    }

    [Fact]
    public void Rail_E1_inset_matches_lfam1_machine_dat()
    {
        var rail = new RobotRailCellConfig { MinMm = -4641f, MaxMm = 150f };
        var (lo, hi) = JointLimitEnvelope.Inset(rail.MinMm, rail.MaxMm);
        float span = 4641f + 150f;
        Assert.Equal(-4641f + 0.05f * span, lo, 2);
        Assert.Equal(150f - 0.05f * span, hi, 2);
    }
}
