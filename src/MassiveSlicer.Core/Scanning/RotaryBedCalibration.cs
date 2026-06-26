using System.Numerics;

namespace MassiveSlicer.Core.Scanning;

/// <summary>Result of a rotary-bed (E1) axis calibration from marker samples.</summary>
public sealed class RotaryBedResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>Rotary axis centre in world / ROBROOT (mm) — the 3-D circle centre.</summary>
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public float CenterZ { get; init; }

    /// <summary>Fitted circle radius (mm) — i.e. the marker's distance from the axis.</summary>
    public float Radius { get; init; }

    /// <summary>RMS of the radial fit error (mm). Lower is better; large values flag a slipped marker or arm motion.</summary>
    public float RmsResidualMm { get; init; }

    /// <summary>Max−min sample Z (mm). Diagnostic only now that the axis tilt is solved (see <see cref="AxisTiltDeg"/>).</summary>
    public float ZSpreadMm { get; init; }

    public int SampleCount { get; init; }

    // -- Axis orientation (3-D fit) -------------------------------------------

    /// <summary>Unit rotary-axis direction in world (mm frame), oriented to point up (+Z).</summary>
    public float AxisX { get; init; }
    public float AxisY { get; init; }
    public float AxisZ { get; init; }

    /// <summary>Angle between the fitted axis and world +Z (degrees). 0 = perfectly vertical.</summary>
    public float AxisTiltDeg { get; init; }

    /// <summary>
    /// KUKA ZYX-Euler orientation (degrees) of a base frame whose Z is the rotary axis and whose
    /// X is world-X projected into the table plane — i.e. A/B/C describe the axis tilt only, so a
    /// perfectly vertical axis yields (0, 0, 0). Suitable for BASE_DATA of the rotary workpiece base.
    /// </summary>
    public float BaseA { get; init; }
    public float BaseB { get; init; }
    public float BaseC { get; init; }

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
/// Fits the rotary print-bed (E1) rotation axis from a set of world-space points, each the detected
/// position of a marker fixed to the bed at a different E1 angle. With the robot arm held still the
/// marker sweeps a circle in the plane perpendicular to the bed axis. A full 3-D circle fit recovers
/// the circle centre (bed centre X/Y/Z) <em>and</em> the plane normal (the rotary axis), so real
/// mounting tilt is captured — not assumed away — and a KUKA base orientation (A/B/C) can be derived.
/// </summary>
public static class RotaryBedCalibration
{
    /// <param name="samples">(E1 angle°, world-space marker point mm). The angle drives the rotation
    /// direction fit; the geometric circle fit itself does not require it.</param>
    public static RotaryBedResult Fit(IReadOnlyList<(double AngleDeg, Vector3 World)> samples)
    {
        if (samples.Count < 3)
            return new RotaryBedResult { Success = false, Error = $"Need at least 3 samples (have {samples.Count})." };

        int n = samples.Count;

        // -- Centroid + Z spread -------------------------------------------------
        Vector3 g = Vector3.Zero;
        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var (_, p) in samples)
        {
            g += p;
            if (p.Z < zmin) zmin = p.Z;
            if (p.Z > zmax) zmax = p.Z;
        }
        g /= n;

        // -- Plane fit: smallest-eigenvector of the sample covariance = axis normal ----
        double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
        foreach (var (_, p) in samples)
        {
            var d = p - g;
            cxx += d.X * d.X; cxy += d.X * d.Y; cxz += d.X * d.Z;
            cyy += d.Y * d.Y; cyz += d.Y * d.Z; czz += d.Z * d.Z;
        }
        double[,] cov = { { cxx, cxy, cxz }, { cxy, cyy, cyz }, { cxz, cyz, czz } };
        if (!SmallestEigenvector(cov, out var normal))
            return new RotaryBedResult { Success = false, Error = "Degenerate samples (collinear or coincident)." };
        if (normal.Z < 0) normal = -normal;   // orient up so tilt reads as a small angle from +Z

        // -- In-plane basis, project, 2-D circle fit (Kåsa) ----------------------
        Vector3 seed = MathF.Abs(normal.X) < 0.9f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
        Vector3 u = Vector3.Normalize(seed - Vector3.Dot(seed, normal) * normal);
        Vector3 v = Vector3.Cross(normal, u);

        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0, saq = 0, sbq = 0, sqq = 0;
        var ab = new (double a, double b)[n];
        for (int i = 0; i < n; i++)
        {
            var d = samples[i].World - g;
            double a = Vector3.Dot(d, u), b = Vector3.Dot(d, v), q = a * a + b * b;
            ab[i] = (a, b);
            sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b;
            saq += a * q; sbq += b * q; sqq += q;
        }
        double[,] m = { { saa, sab, sa }, { sab, sbb, sb }, { sa, sb, n } };
        double[]  rhs = { -saq, -sbq, -sqq };
        if (!Solve3(m, rhs, out var sol))
            return new RotaryBedResult { Success = false, Error = "Degenerate samples (marker on-centre — give it a larger radius)." };

        double ca = -sol[0] / 2.0, cb = -sol[1] / 2.0;
        double r2 = ca * ca + cb * cb - sol[2];
        if (r2 <= 0)
            return new RotaryBedResult { Success = false, Error = "Circle fit produced a non-positive radius." };
        double r = Math.Sqrt(r2);

        Vector3 center = g + (float)ca * u + (float)cb * v;

        double sse = 0;
        for (int i = 0; i < n; i++)
        {
            double d = Math.Sqrt((ab[i].a - ca) * (ab[i].a - ca) + (ab[i].b - cb) * (ab[i].b - cb)) - r;
            sse += d * d;
        }

        // -- Base orientation: Z = axis, X = world-X projected into the plane ----
        Vector3 bx = new Vector3(1, 0, 0) - Vector3.Dot(new Vector3(1, 0, 0), normal) * normal;
        if (bx.LengthSquared() < 1e-9f) bx = new Vector3(0, 1, 0) - Vector3.Dot(new Vector3(0, 1, 0), normal) * normal;
        bx = Vector3.Normalize(bx);
        Vector3 by = Vector3.Cross(normal, bx);
        var (baseA, baseB, baseC) = FrameToKukaAbc(bx, by, normal);

        float tilt = (float)(Math.Acos(Math.Clamp(normal.Z, -1f, 1f)) * 180.0 / Math.PI);

        var rot = FitRotation(samples, center.X, center.Y);

        return new RotaryBedResult
        {
            Success             = true,
            CenterX             = center.X,
            CenterY             = center.Y,
            CenterZ             = center.Z,
            Radius              = (float)r,
            RmsResidualMm       = (float)Math.Sqrt(sse / n),
            ZSpreadMm           = (float)(zmax - zmin),
            SampleCount         = n,
            AxisX               = normal.X,
            AxisY               = normal.Y,
            AxisZ               = normal.Z,
            AxisTiltDeg         = tilt,
            BaseA               = baseA,
            BaseB               = baseB,
            BaseC               = baseC,
            RotationResolved    = rot.Resolved,
            RotationSign        = rot.Sign,
            MeasuredDegPerE1    = rot.DegPerE1,
            RotationResidualDeg = rot.ResidualDeg,
        };
    }

    /// <summary>
    /// KUKA ZYX-Euler (A about Z, B about Y, C about X; degrees) for a frame whose axes (expressed in
    /// the reference frame) are the columns x, y, z. R = Rz(A)·Ry(B)·Rx(C). Same extraction the KRL
    /// exporter uses for tool poses, factored for a general frame.
    /// </summary>
    public static (float A, float B, float C) FrameToKukaAbc(Vector3 x, Vector3 y, Vector3 z)
    {
        const float R2D = 180f / MathF.PI;
        float bRad = MathF.Atan2(-x.Z, MathF.Sqrt(x.X * x.X + x.Y * x.Y));
        float aRad, cRad;
        if (MathF.Abs(MathF.Abs(bRad) - MathF.PI / 2f) < 0.05f)   // gimbal lock near B = ±90°
        {
            aRad = MathF.Atan2(-y.X, y.Y);
            cRad = 0f;
        }
        else
        {
            aRad = MathF.Atan2(x.Y, x.X);
            cRad = MathF.Atan2(y.Z, z.Z);
        }
        return (aRad * R2D, bRad * R2D, cRad * R2D);
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
            xs[i] = ord[i].AngleDeg * Math.PI / 180.0;
            ys[i] = unwrapped;
        }

        int    mm = ord.Count;
        double sx = xs.Sum(), sy = ys.Sum();
        double sxx = 0, sxy = 0;
        for (int i = 0; i < mm; i++) { sxx += xs[i] * xs[i]; sxy += xs[i] * ys[i]; }
        double denom = mm * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) return (false, 0f, 0f, 0f);

        double slope = (mm * sxy - sx * sy) / denom;
        double bb    = (sy - slope * sx) / mm;

        double sse = 0;
        for (int i = 0; i < mm; i++) { double e = ys[i] - (slope * xs[i] + bb); sse += e * e; }
        double residualDeg = Math.Sqrt(sse / mm) * 180.0 / Math.PI;

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

    /// <summary>Unit eigenvector of the smallest eigenvalue of a symmetric 3×3 matrix (Jacobi).</summary>
    private static bool SmallestEigenvector(double[,] a, out Vector3 vec)
    {
        vec = Vector3.UnitZ;
        // Jacobi eigenvalue iteration on a symmetric 3×3.
        double[,] m = (double[,])a.Clone();
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            // largest off-diagonal
            int p = 0, q = 1; double max = Math.Abs(m[0, 1]);
            if (Math.Abs(m[0, 2]) > max) { max = Math.Abs(m[0, 2]); p = 0; q = 2; }
            if (Math.Abs(m[1, 2]) > max) { max = Math.Abs(m[1, 2]); p = 1; q = 2; }
            if (max < 1e-12) break;

            double app = m[p, p], aqq = m[q, q], apq = m[p, q];
            double phi = 0.5 * Math.Atan2(2 * apq, aqq - app);
            double c = Math.Cos(phi), s = Math.Sin(phi);
            // rotate m
            for (int k = 0; k < 3; k++)
            {
                double mkp = m[k, p], mkq = m[k, q];
                m[k, p] = c * mkp - s * mkq;
                m[k, q] = s * mkp + c * mkq;
            }
            for (int k = 0; k < 3; k++)
            {
                double mpk = m[p, k], mqk = m[q, k];
                m[p, k] = c * mpk - s * mqk;
                m[q, k] = s * mpk + c * mqk;
            }
            // accumulate eigenvectors
            for (int k = 0; k < 3; k++)
            {
                double vkp = v[k, p], vkq = v[k, q];
                v[k, p] = c * vkp - s * vkq;
                v[k, q] = s * vkp + c * vkq;
            }
        }
        // smallest eigenvalue is on the diagonal
        int idx = 0; double best = m[0, 0];
        if (m[1, 1] < best) { best = m[1, 1]; idx = 1; }
        if (m[2, 2] < best) { best = m[2, 2]; idx = 2; }
        var e = new Vector3((float)v[0, idx], (float)v[1, idx], (float)v[2, idx]);
        if (e.LengthSquared() < 1e-12f) return false;
        vec = Vector3.Normalize(e);
        return true;
    }
}
