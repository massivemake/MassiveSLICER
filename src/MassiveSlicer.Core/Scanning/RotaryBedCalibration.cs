using System.Numerics;

namespace MassiveSlicer.Core.Scanning;

/// <summary>Result of a rotary-bed (E1) axis calibration from marker samples.</summary>
public sealed class RotaryBedResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>Rotary axis location in world / ROBROOT (mm). X/Y = circle centre, Z = mean sample height.</summary>
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public float CenterZ { get; init; }

    /// <summary>Fitted circle radius (mm) — i.e. the marker's distance from the axis.</summary>
    public float Radius { get; init; }

    /// <summary>RMS of the radial fit error (mm). Lower is better; large values flag a slipped marker or arm motion.</summary>
    public float RmsResidualMm { get; init; }

    /// <summary>Max−min sample Z (mm). A near-vertical axis on a flat bed keeps this small; large values flag axis tilt.</summary>
    public float ZSpreadMm { get; init; }

    public int SampleCount { get; init; }

    // -- Rotation direction ---------------------------------------------------

    /// <summary>True when the E1 spread was large enough to resolve the rotation direction.</summary>
    public bool RotationResolved { get; init; }

    /// <summary>Sign applied to E1 when rotating scene geometry about the centre: +1 = CCW about world +Z, −1 = CW. 0 when unresolved.</summary>
    public float RotationSign { get; init; }

    /// <summary>Measured scene-degrees per E1-degree (≈ ±1 for a direct turntable). Diagnostic — large deviation flags a gearing/units issue.</summary>
    public float MeasuredDegPerE1 { get; init; }

    /// <summary>RMS of the angle-vs-E1 fit (degrees). Low = clean 1:1 rotation; high = slipped marker or wrong centre.</summary>
    public float RotationResidualDeg { get; init; }
}

/// <summary>
/// Fits the rotary print-bed (E1) rotation axis from a set of world-space points,
/// each the detected position of a marker fixed to the bed at a different E1 angle.
/// With the robot arm held still, the marker sweeps a circle about the (near-vertical)
/// bed axis: the circle centre gives the bed centre X/Y and the mean height gives Z.
/// Assumes a near-vertical axis (true for this turntable); <see cref="RotaryBedResult.ZSpreadMm"/>
/// surfaces any tilt so a bad assumption is visible rather than silent.
/// </summary>
public static class RotaryBedCalibration
{
    /// <param name="samples">(E1 angle°, world-space marker point mm). The angle is recorded for
    /// diagnostics/outlier review; the geometric fit itself does not require it.</param>
    public static RotaryBedResult Fit(IReadOnlyList<(double AngleDeg, Vector3 World)> samples)
    {
        if (samples.Count < 3)
            return new RotaryBedResult { Success = false, Error = $"Need at least 3 samples (have {samples.Count})." };

        // Algebraic (Kåsa) circle fit in the XY plane: x²+y² + D·x + E·y + F = 0.
        // Normal equations minimise Σ(D·xi + E·yi + F + (xi²+yi²))².
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        double sxz = 0, syz = 0, szz = 0;   // z := xi²+yi²
        double sz = 0, zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var (_, p) in samples)
        {
            double x = p.X, y = p.Y, q = x * x + y * y;
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            sxz += x * q; syz += y * q; szz += q;
            sz += p.Z;
            if (p.Z < zmin) zmin = p.Z;
            if (p.Z > zmax) zmax = p.Z;
        }
        int n = samples.Count;

        // [sxx sxy sx][D]   [-sxz]
        // [sxy syy sy][E] = [-syz]
        // [sx  sy  n ][F]   [-szz]
        double[,] a = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        double[]  b = { -sxz, -syz, -szz };
        if (!Solve3(a, b, out var sol))
            return new RotaryBedResult { Success = false, Error = "Degenerate samples (collinear or marker on-centre — give it a larger radius)." };

        double cx = -sol[0] / 2.0, cy = -sol[1] / 2.0;
        double r2 = cx * cx + cy * cy - sol[2];
        if (r2 <= 0)
            return new RotaryBedResult { Success = false, Error = "Circle fit produced a non-positive radius." };
        double r = Math.Sqrt(r2);

        double sse = 0;
        foreach (var (_, p) in samples)
        {
            double d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) - r;
            sse += d * d;
        }

        var rot = FitRotation(samples, cx, cy);

        return new RotaryBedResult
        {
            Success             = true,
            CenterX             = (float)cx,
            CenterY             = (float)cy,
            CenterZ             = (float)(sz / n),
            Radius              = (float)r,
            RmsResidualMm       = (float)Math.Sqrt(sse / n),
            ZSpreadMm           = (float)(zmax - zmin),
            SampleCount         = n,
            RotationResolved    = rot.Resolved,
            RotationSign        = rot.Sign,
            MeasuredDegPerE1    = rot.DegPerE1,
            RotationResidualDeg = rot.ResidualDeg,
        };
    }

    /// <summary>
    /// Determines how scene rotation about the centre tracks E1: fits the marker's world angle
    /// φ = atan2(y−cy, x−cx) against the E1 axis angle. The slope's sign is the rotation
    /// direction; its magnitude (≈1) confirms a direct 1:1 turntable. Needs &gt;5° of E1 spread.
    /// </summary>
    private static (bool Resolved, float Sign, float DegPerE1, float ResidualDeg) FitRotation(
        IReadOnlyList<(double AngleDeg, Vector3 World)> samples, double cx, double cy)
    {
        var ord = samples.OrderBy(s => s.AngleDeg).ToList();
        if (ord.Count < 2 || ord[^1].AngleDeg - ord[0].AngleDeg < 5.0)
            return (false, 0f, 0f, 0f);

        // Unwrap φ over the E1-sorted sequence so it accumulates monotonically.
        var xs = new double[ord.Count];
        var ys = new double[ord.Count];
        double unwrapped = 0, prev = 0;
        for (int i = 0; i < ord.Count; i++)
        {
            double phi = Math.Atan2(ord[i].World.Y - cy, ord[i].World.X - cx);
            if (i == 0) unwrapped = phi;
            else
            {
                double d = phi - prev;
                while (d >  Math.PI) d -= 2 * Math.PI;
                while (d < -Math.PI) d += 2 * Math.PI;
                unwrapped += d;
            }
            prev = phi;
            xs[i] = ord[i].AngleDeg * Math.PI / 180.0;   // E1 in rad
            ys[i] = unwrapped;
        }

        int    m  = ord.Count;
        double sx = xs.Sum(), sy = ys.Sum();
        double sxx = 0, sxy = 0;
        for (int i = 0; i < m; i++) { sxx += xs[i] * xs[i]; sxy += xs[i] * ys[i]; }
        double denom = m * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) return (false, 0f, 0f, 0f);

        double slope = (m * sxy - sx * sy) / denom;     // rad/rad = deg/deg
        double b     = (sy - slope * sx) / m;

        double sse = 0;
        for (int i = 0; i < m; i++) { double e = ys[i] - (slope * xs[i] + b); sse += e * e; }
        double residualDeg = Math.Sqrt(sse / m) * 180.0 / Math.PI;

        return (true, slope >= 0 ? 1f : -1f, (float)slope, (float)residualDeg);
    }

    // Gaussian elimination with partial pivoting for a 3×3 system. Returns false if singular.
    private static bool Solve3(double[,] a, double[] b, out double[] x)
    {
        x = new double[3];
        for (int col = 0; col < 3; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 3; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-9) return false;
            if (piv != col)
            {
                for (int c = 0; c < 3; c++) (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }
            for (int r = col + 1; r < 3; r++)
            {
                double f = a[r, col] / a[col, col];
                for (int c = col; c < 3; c++) a[r, c] -= f * a[col, c];
                b[r] -= f * b[col];
            }
        }
        for (int r = 2; r >= 0; r--)
        {
            double s = b[r];
            for (int c = r + 1; c < 3; c++) s -= a[r, c] * x[c];
            x[r] = s / a[r, r];
        }
        return true;
    }
}
