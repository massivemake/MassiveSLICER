namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// One slice of a joint along the program. Low and high are the extremes inside
/// that slice, so a brief trip to a stop is still visible after downsampling.
/// </summary>
public readonly record struct JointSpan(float Low, float High, bool HasValue);

/// <summary>
/// Downsamples a per-move joint trace for the timeline graph. The reachability
/// check already stores one sample per move; this only buckets those samples.
/// </summary>
public static class JointTimeline
{
    /// <summary>Columns drawn across the timeline. Each keeps the min and max inside it.</summary>
    public const int Buckets = 512;

    /// <summary>0 at <paramref name="min"/>, 1 at <paramref name="max"/>. Past either stop stays outside 0..1.</summary>
    public static float Fraction(float value, float min, float max)
    {
        float span = max - min;
        if (span == 0f || float.IsNaN(value)) return 0.5f;
        return (value - min) / span;
    }

    /// <summary>Bucket a flat series. NaN samples are skipped. An empty bucket has <see cref="JointSpan.HasValue"/> false.</summary>
    public static JointSpan[] Envelope(ReadOnlySpan<float> values, int buckets)
    {
        if (buckets < 1) buckets = 1;
        var spans = new JointSpan[buckets];
        int n = values.Length;
        if (n == 0) return spans;
        for (int i = 0; i < n; i++)
        {
            float v = values[i];
            if (float.IsNaN(v)) continue;
            Accumulate(spans, Bucket(i, n, buckets), v);
        }
        return spans;
    }

    /// <summary>Bucket one column of a per-move solution array. A short or null row is skipped.</summary>
    public static JointSpan[] EnvelopeAxis(float[][] solutions, int axis, int buckets)
    {
        if (buckets < 1) buckets = 1;
        var spans = new JointSpan[buckets];
        int n = solutions.Length;
        if (n == 0) return spans;
        for (int i = 0; i < n; i++)
        {
            var row = solutions[i];
            if (row is null || (uint)axis >= (uint)row.Length) continue;
            float v = row[axis];
            if (float.IsNaN(v)) continue;
            Accumulate(spans, Bucket(i, n, buckets), v);
        }
        return spans;
    }

    static int Bucket(int index, int count, int buckets)
    {
        int b = (int)((long)index * buckets / count);
        return b >= buckets ? buckets - 1 : b;
    }

    static void Accumulate(JointSpan[] spans, int bucket, float value)
    {
        ref var s = ref spans[bucket];
        if (!s.HasValue) spans[bucket] = new JointSpan(value, value, true);
        else if (value < s.Low) spans[bucket] = new JointSpan(value, s.High, true);
        else if (value > s.High) spans[bucket] = new JointSpan(s.Low, value, true);
    }
}
