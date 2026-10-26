namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Toolhead global orientation along a timeline. Y/X/Z on the panel are KUKA B/C/A.
/// Each key falls off to the baseline inside its own influence window, and the
/// tail is the baseline again. A move that no window covers stays on the baseline.
/// Where two windows overlap, those keys blend, and that blend still fades to the
/// baseline by the combined weight so the tails meet it. A move on a key uses
/// that key only.
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

    /// <param name="pathEnd">Kept so callers compile. The falloff no longer runs out to the path end.</param>
    public static Degrees At(double moveIndex, Degrees baseline, IReadOnlyList<Key> keys, int pathEnd = -1)
    {
        _ = pathEnd;
        if (keys is null || keys.Count == 0) return baseline;

        Key? onKey = null;
        double onDist = 0.5;
        foreach (var k in keys)
        {
            double ad = Math.Abs(moveIndex - k.Index);
            if (ad >= 0.5) continue;
            if (onKey is null || ad < onDist || (ad == onDist && k.Index >= onKey.Value.Index))
            {
                onKey = k;
                onDist = ad;
            }
        }
        if (onKey is { } hit) return Of(hit);

        double wsum = 0;
        Degrees acc = baseline;
        bool any = false;
        foreach (var k in keys)
        {
            double w = WindowWeight(moveIndex, k);
            if (w <= 1e-9) continue;
            if (!any)
            {
                acc = Of(k);
                wsum = w;
                any = true;
                continue;
            }
            double next = wsum + w;
            acc = Lerp(acc, Of(k), w / next);
            wsum = next;
        }
        if (!any || wsum <= 1e-9) return baseline;
        return Lerp(baseline, acc, Math.Min(wsum, 1.0));
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
