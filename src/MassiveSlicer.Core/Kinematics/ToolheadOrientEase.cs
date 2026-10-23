namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Toolhead global orientation along a timeline. Y/X/Z on the panel are KUKA B/C/A.
/// Each key keeps its own angle inside its influence window. Another key does not
/// rewrite that window. The blank gap between two windows eases from one saved
/// angle to the next. Where the windows overlap, those two angles blend.
/// Before the first key the angle eases from the non-keyed orientation at the
/// start of the path up to that key. After the last key it eases from that key
/// back to the non-keyed orientation at the end of the path.
/// A half-turn from a negative angle to a positive one, within 181° across 0,
/// passes through 0. A shorter step across ±180, such as 170° to −170°, still
/// takes that short step.
/// </summary>
public static class ToolheadOrientEase
{
    /// <summary>KUKA A (Z), B (Y), C (X), degrees.</summary>
    public readonly record struct Degrees(float A, float B, float C);

    public readonly record struct Key(
        int Index, float A, float B, float C, int InfluenceLeft, int InfluenceRight);

    /// <param name="pathEnd">Last move index. The ease after the final key runs to here.</param>
    public static Degrees At(double moveIndex, Degrees baseline, IReadOnlyList<Key> keys, int pathEnd = -1)
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
            return Between(moveIndex, p, n);

        if (prev is { } tail)
        {
            if (Math.Abs(moveIndex - tail.Index) < 0.5) return Of(tail);
            double end = pathEnd > tail.Index
                ? pathEnd
                : tail.Index + Math.Max(tail.InfluenceRight, 1);
            double span = end - tail.Index;
            if (span <= 1e-6) return Of(tail);
            double t = (moveIndex - tail.Index) / span;
            if (t >= 1.0) return baseline;
            return Lerp(Of(tail), baseline, Smooth(t));
        }

        if (next is { } head)
        {
            if (head.Index <= 0) return Of(head);
            double t = moveIndex / head.Index;
            return Lerp(baseline, Of(head), Smooth(t));
        }

        return baseline;
    }

    /// <summary>
    /// The key's own angle inside its window. The other key's angle stays on the other
    /// window. The open gap between the windows eases from one to the next.
    /// </summary>
    static Degrees Between(double moveIndex, Key p, Key n)
    {
        if (Math.Abs(moveIndex - p.Index) < 0.5) return Of(p);
        if (Math.Abs(moveIndex - n.Index) < 0.5) return Of(n);

        double aEnd = p.Index + Math.Max(p.InfluenceRight, 1);
        double bStart = n.Index - Math.Max(n.InfluenceLeft, 1);
        bool inA = moveIndex >= p.Index && moveIndex <= aEnd;
        bool inB = moveIndex >= bStart && moveIndex <= n.Index;
        if (inA && inB)
        {
            double wa = WindowWeight(moveIndex, p);
            double wb = WindowWeight(moveIndex, n);
            double sum = wa + wb;
            if (sum <= 1e-9) return Of(p);
            return Lerp(Of(p), Of(n), wb / sum);
        }
        if (inA) return Of(p);
        if (inB) return Of(n);

        double span = bStart - aEnd;
        double t = span > 1e-6
            ? (moveIndex - aEnd) / span
            : (moveIndex - p.Index) / (n.Index - p.Index);
        return Lerp(Of(p), Of(n), Smooth(t));
    }

    static double WindowWeight(double moveIndex, Key k)
    {
        double d = moveIndex - k.Index;
        double span = d < 0 ? Math.Max(k.InfluenceLeft, 1) : Math.Max(k.InfluenceRight, 1);
        double t = Math.Abs(d) / span;
        if (t >= 1.0) return 0;
        double x = 1.0 - t;
        return x * x * (3.0 - 2.0 * x);
    }

    static Degrees Of(Key k) => new(k.A, k.B, k.C);

    static double Smooth(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>
    /// Step from <paramref name="from"/> to <paramref name="to"/>.
    /// Opposite signs within 181° across 0 return that through-zero step, so a
    /// half-turn such as −90° to +90° does not wrap onto ±180. Other steps stay
    /// in (−180, 180], which keeps a short seam step such as 170° to −170°.
    /// </summary>
    public static float ShortestDelta(float from, float to)
    {
        float direct = to - from;
        bool opposite = (from < 0f && to > 0f) || (from > 0f && to < 0f);
        if (opposite)
        {
            float throughZero = MathF.Abs(from) + MathF.Abs(to);
            if (throughZero <= 181f)
                return MathF.CopySign(throughZero, direct == 0f ? 1f : direct);
        }
        while (direct > 180f) direct -= 360f;
        while (direct < -180f) direct += 360f;
        return direct;
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
