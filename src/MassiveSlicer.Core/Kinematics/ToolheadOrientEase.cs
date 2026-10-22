namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Toolhead global orientation along a timeline. Y/X/Z on the panel are KUKA B/C/A.
/// Between two keyframes the angle eases from one saved orientation to the next.
/// Outside that span it eases back to the non-keyed orientation over the key's
/// influence window, then holds that baseline.
/// </summary>
public static class ToolheadOrientEase
{
    /// <summary>KUKA A (Z), B (Y), C (X), degrees.</summary>
    public readonly record struct Degrees(float A, float B, float C);

    public readonly record struct Key(
        int Index, float A, float B, float C, int InfluenceLeft, int InfluenceRight);

    public static Degrees At(double moveIndex, Degrees baseline, IReadOnlyList<Key> keys)
    {
        if (keys is null || keys.Count == 0) return baseline;

        Key? prev = null;
        Key? next = null;
        foreach (var k in keys)
        {
            if (k.Index <= moveIndex && (prev is null || k.Index >= prev.Value.Index))
                prev = k;
            if (k.Index > moveIndex && (next is null || k.Index < next.Value.Index))
                next = k;
        }

        if (prev is { } p && next is { } n && n.Index != p.Index)
        {
            double t = (moveIndex - p.Index) / (n.Index - p.Index);
            return Lerp(Of(p), Of(n), Smooth(t));
        }

        if (prev is { } tail)
        {
            if (Math.Abs(moveIndex - tail.Index) < 1e-6) return Of(tail);
            double span = Math.Max(tail.InfluenceRight, 1);
            double t = (moveIndex - tail.Index) / span;
            if (t >= 1.0) return baseline;
            return Lerp(Of(tail), baseline, Smooth(t));
        }

        if (next is { } head)
        {
            double span = Math.Max(head.InfluenceLeft, 1);
            double t = (head.Index - moveIndex) / span;
            if (t >= 1.0) return baseline;
            return Lerp(Of(head), baseline, Smooth(t));
        }

        return baseline;
    }

    static Degrees Of(Key k) => new(k.A, k.B, k.C);

    static double Smooth(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Shortest step from <paramref name="from"/> to <paramref name="to"/>, in (-180, 180].</summary>
    public static float ShortestDelta(float from, float to)
    {
        float d = to - from;
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    static Degrees Lerp(Degrees a, Degrees b, double t)
    {
        float s = (float)t;
        return new Degrees(
            a.A + ShortestDelta(a.A, b.A) * s,
            a.B + ShortestDelta(a.B, b.B) * s,
            a.C + ShortestDelta(a.C, b.C) * s);
    }
}
