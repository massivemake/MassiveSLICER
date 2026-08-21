namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Usable joint / rail envelope: stay <see cref="MarginFraction"/> of travel
/// inside each software end-switch so simulation, IK, and KRL never aim at
/// the raw <c>$SOFTN_END</c> / <c>$SOFTP_END</c> stops.
/// </summary>
public static class JointLimitEnvelope
{
    /// <summary>Keep this fraction of each axis travel clear of both software stops.</summary>
    public const float MarginFraction = 0.05f;

    public static (float Min, float Max) Inset(float min, float max, float margin = MarginFraction)
    {
        if (float.IsNaN(min) || float.IsNaN(max))
            return (min, max);
        if (max < min)
            (min, max) = (max, min);
        float span = max - min;
        if (span <= 1e-6f)
            return (min, max);
        float pad = span * Math.Clamp(margin, 0f, 0.45f);
        return (min + pad, max - pad);
    }

    public static (double Min, double Max) Inset(double min, double max, double margin = MarginFraction)
    {
        var (lo, hi) = Inset((float)min, (float)max, (float)margin);
        return (lo, hi);
    }

    public static float UsableMin(float min, float max, float margin = MarginFraction)
        => Inset(min, max, margin).Min;

    public static float UsableMax(float min, float max, float margin = MarginFraction)
        => Inset(min, max, margin).Max;

    public static double UsableMin(double min, double max, double margin = MarginFraction)
        => Inset(min, max, margin).Min;

    public static double UsableMax(double min, double max, double margin = MarginFraction)
        => Inset(min, max, margin).Max;

    public static float Clamp(float value, float min, float max, float margin = MarginFraction)
    {
        var (lo, hi) = Inset(min, max, margin);
        return Math.Clamp(value, lo, hi);
    }

    public static double Clamp(double value, double min, double max, double margin = MarginFraction)
    {
        var (lo, hi) = Inset(min, max, margin);
        return Math.Clamp(value, lo, hi);
    }

    public static bool Contains(float value, float min, float max, float margin = MarginFraction)
    {
        var (lo, hi) = Inset(min, max, margin);
        return value >= lo && value <= hi;
    }

    public static bool Contains(double value, double min, double max, double margin = MarginFraction)
        => Contains((float)value, (float)min, (float)max, (float)margin);

    /// <summary>True when every A1–A6 angle sits inside the 5% envelope of <paramref name="joints"/>.</summary>
    public static bool JointsInside(ReadOnlySpan<float> krlDeg, IReadOnlyList<JointConfig> joints)
    {
        int n = Math.Min(krlDeg.Length, joints.Count);
        for (int i = 0; i < n; i++)
        {
            if (!Contains(krlDeg[i], joints[i].MinDeg, joints[i].MaxDeg))
                return false;
        }
        return true;
    }
}
