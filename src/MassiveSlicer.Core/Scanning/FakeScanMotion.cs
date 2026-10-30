namespace MassiveSlicer.Core.Scanning;

/// <summary>
/// Joint stops for a simulated move onto a scan pose. The last stop is the
/// target. Intermediate stops are where a fake scan can collect a frame.
/// </summary>
public static class FakeScanMotion
{
    public static float WrapDegrees(float delta)
    {
        while (delta > 180f) delta -= 360f;
        while (delta < -180f) delta += 360f;
        return delta;
    }

    /// <summary>
    /// Poses from just after <paramref name="start"/> through <paramref name="target"/>.
    /// A move under 1° is the target alone. Otherwise a stop about every
    /// <paramref name="stepDegrees"/>, capped at <paramref name="maxStops"/>.
    /// </summary>
    public static IReadOnlyList<float[]> CaptureStops(
        float[] start, float[] target, float stepDegrees = 30f, int maxStops = 8)
    {
        int n = Math.Min(start.Length, target.Length);
        if (n < 6) return [];
        float step = MathF.Max(5f, stepDegrees);
        int cap = Math.Max(1, maxStops);
        float max = 0f;
        var delta = new float[n];
        for (int i = 0; i < n; i++)
        {
            delta[i] = WrapDegrees(target[i] - start[i]);
            max = MathF.Max(max, MathF.Abs(delta[i]));
        }

        int stops = max < 1f ? 1 : Math.Clamp((int)MathF.Ceiling(max / step), 1, cap);
        var list = new List<float[]>(stops);
        for (int s = 1; s <= stops; s++)
        {
            float t = s / (float)stops;
            var pose = new float[n];
            bool last = s == stops;
            for (int i = 0; i < n; i++)
                pose[i] = last ? target[i] : start[i] + delta[i] * t;
            list.Add(pose);
        }
        return list;
    }
}
