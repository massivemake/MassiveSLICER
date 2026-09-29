namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Controller limits the feasibility solve must honor before a program can
/// reach the bed. Speeds are LFAM1 KR120 R3900 rated values from
/// <c>$VEL_AXIS_MA</c> / <c>$RAT_MOT_AX</c>, not a job setting.
/// A step the wrist can make if the nozzle slows stays repairable.
/// A step that still exceeds the limit at <see cref="MinRepairScale"/>, or a
/// joint outside the software stops, is not — slowing will not clear it.
/// </summary>
public static class AxisMotionLimits
{
    /// <summary>Stay inside the rated speed. The pad trips at the rated speed.</summary>
    public const float SpeedMargin = 0.85f;

    /// <summary>
    /// Slowest repair we will write into a program. Below this the span is a
    /// wrist the arm cannot pass, not a speed the screw can follow.
    /// </summary>
    public const float MinRepairScale = 0.25f;

    /// <summary>A1–A6 deg/s. A4 is index 3 (190 deg/s).</summary>
    public static readonly float[] Kr120R3900DegPerSec =
    [
        104.93f, // 4494 rpm, 1798/7
        101.01f, // 4501 rpm, 1872/7
        110.05f, // 4607 rpm, 754/3
        190.04f, // 5980 rpm, 10387/55
        180.05f, // 5703 rpm, 91834/483
        260.15f, // 5688 rpm, 6485103/49400
    ];

    public readonly record struct Verdict(
        bool Exceeded,
        bool Repairable,
        float RequiredScale,
        int Axis,
        float RateDegPerSec,
        float LimitDegPerSec);

    public static Verdict CheckJointRate(float deltaDeg, float dtSec, float maxDegPerSec, int axis = 3)
    {
        float limit = maxDegPerSec * SpeedMargin;
        float rate = dtSec > 1e-6f ? MathF.Abs(deltaDeg) / dtSec : float.PositiveInfinity;
        if (rate <= limit || limit <= 0f)
            return new Verdict(false, true, 1f, axis, rate, limit);

        float scale = limit / rate;
        return new Verdict(true, scale >= MinRepairScale, scale, axis, rate, limit);
    }

    /// <summary>
    /// Lower bound on A4 when the nozzle orientation is changing and A5 is
    /// near 0. KUKA will not flip the wrist mid-LIN, so the rate is at least
    /// orientation_rate / sin(|A5|).
    /// </summary>
    public static Verdict CheckWrist(float orientDeg, float dtSec, float a5Deg, float a4MaxDegPerSec)
    {
        float orientRate = dtSec > 1e-6f ? MathF.Abs(orientDeg) / dtSec : float.PositiveInfinity;
        float s = MathF.Sin(MathF.Abs(a5Deg) * MathF.PI / 180f);
        float bound = s < 1e-4f ? float.PositiveInfinity : orientRate / s;
        return CheckJointRate(bound * MathF.Max(dtSec, 1e-6f), MathF.Max(dtSec, 1e-6f), a4MaxDegPerSec, axis: 3);
    }

    public static Verdict CheckPosition(float deg, float minDeg, float maxDeg)
    {
        if (deg >= minDeg && deg <= maxDeg)
            return new Verdict(false, true, 1f, -1, 0f, 0f);
        return new Verdict(true, false, 0f, -1, deg, 0f);
    }

    public readonly record struct ScanResult(float[] Scale, int UnrepairableCount, int FirstIndex);

    /// <summary>
    /// One step per move. <paramref name="joints"/> is the pose at the start of
    /// move 0, then the pose at the end of each move. A repairable step returns
    /// a scale in (0, 1). An unrepairable step returns scale 0.
    /// </summary>
    public static ScanResult Scan(
        IReadOnlyList<float[]> joints,
        IReadOnlyList<float> dtSec,
        IReadOnlyList<float> orientDeg,
        float[]? maxDegPerSec = null,
        float[]? minDeg = null,
        float[]? maxDeg = null)
    {
        var speeds = maxDegPerSec ?? Kr120R3900DegPerSec;
        int n = dtSec.Count;
        var scale = new float[n];
        int bad = 0;
        int first = -1;
        float a4 = speeds.Length > 3 ? speeds[3] : Kr120R3900DegPerSec[3];

        for (int i = 0; i < n; i++)
        {
            scale[i] = 1f;
            if (i + 1 >= joints.Count) continue;
            var a = joints[i];
            var b = joints[i + 1];
            float worst = 1f;
            bool refuse = false;
            int axes = Math.Min(a.Length, Math.Min(b.Length, speeds.Length));
            for (int j = 0; j < axes; j++)
            {
                var v = CheckJointRate(b[j] - a[j], dtSec[i], speeds[j], j);
                if (!v.Exceeded) continue;
                if (!v.Repairable) refuse = true;
                worst = MathF.Min(worst, v.RequiredScale);
            }

            if (i < orientDeg.Count && b.Length > 4)
            {
                var w = CheckWrist(orientDeg[i], dtSec[i], b[4], a4);
                if (w.Exceeded)
                {
                    if (!w.Repairable) refuse = true;
                    worst = MathF.Min(worst, w.RequiredScale);
                }
            }

            if (minDeg is not null && maxDeg is not null)
            {
                int lim = Math.Min(b.Length, Math.Min(minDeg.Length, maxDeg.Length));
                for (int j = 0; j < lim; j++)
                {
                    if (CheckPosition(b[j], minDeg[j], maxDeg[j]).Exceeded)
                        refuse = true;
                }
            }

            if (refuse)
            {
                bad++;
                if (first < 0) first = i;
                scale[i] = 0f;
            }
            else
                scale[i] = worst;
        }

        return new ScanResult(scale, bad, first);
    }

    /// <summary>
    /// True when the current arm cannot hold this bead at print speed.
    /// The repair is a different wrist, not a slower move.
    /// </summary>
    public static bool NeedsNewArm(in ScanResult scan)
    {
        for (int i = 0; i < scan.Scale.Length; i++)
            if (scan.Scale[i] < 0.999f) return true;
        return scan.UnrepairableCount > 0;
    }
}
