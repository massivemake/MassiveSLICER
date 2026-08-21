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
