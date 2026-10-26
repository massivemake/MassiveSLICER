using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Tests;

public sealed class JointTimelineTest
{
    [Fact]
    public void Envelope_keeps_a_spike_inside_its_bucket()
    {
        var values = new float[1000];
        values[500] = 100f;

        var spans = JointTimeline.Envelope(values, 10);

        Assert.Equal(100f, spans[5].High);
        Assert.True(spans[5].HasValue);
        Assert.Equal(0f, spans[0].High);
    }

    [Fact]
    public void Last_sample_lands_in_the_last_bucket()
    {
        var values = new float[10];
        values[9] = 7f;

        var spans = JointTimeline.Envelope(values, 4);

        Assert.Equal(7f, spans[^1].High);
        Assert.True(spans[^1].HasValue);
    }

    [Fact]
    public void NaN_samples_are_ignored()
    {
        var spans = JointTimeline.Envelope([1f, float.NaN, 3f], 1);

        Assert.Equal(new JointSpan(1f, 3f, true), spans[0]);
    }

    [Fact]
    public void All_NaN_bucket_has_no_value()
    {
        var spans = JointTimeline.Envelope([float.NaN, float.NaN], 2);

        Assert.False(spans[0].HasValue);
        Assert.False(spans[1].HasValue);
    }

    [Fact]
    public void Fraction_is_zero_at_the_stop_and_past_one_beyond_it()
    {
        Assert.Equal(0d, JointTimeline.Fraction(-185f, -185f, 185f), 4);
        Assert.Equal(1d, JointTimeline.Fraction(185f, -185f, 185f), 4);
        Assert.Equal(0.5d, JointTimeline.Fraction(0f, -185f, 185f), 4);
        Assert.True(JointTimeline.Fraction(186f, -185f, 185f) > 1f);
        Assert.Equal(0.5d, JointTimeline.Fraction(10f, 5f, 5f), 4);
    }

    [Fact]
    public void EnvelopeAxis_reads_one_joint_and_skips_a_short_row()
    {
        float[][] solutions =
        [
            [1f, 10f],
            [2f, 20f],
            null!,
            [4f],
        ];

        var a1 = JointTimeline.EnvelopeAxis(solutions, 0, 1);
        var a2 = JointTimeline.EnvelopeAxis(solutions, 1, 1);

        Assert.Equal(new JointSpan(1f, 4f, true), a1[0]);
        Assert.Equal(new JointSpan(10f, 20f, true), a2[0]);
    }
}
