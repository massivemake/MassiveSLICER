using System.Numerics;

namespace MassiveSlicer.Core.Scanning;

/// <summary>
/// Analysis output from a completed rotary-bed auto-scan sequence.
/// All positions are in the bed frame (de-rotated, origin at rotation axis).
/// </summary>
public sealed class BedScanResult
{
    /// <summary>Unit normal of the fitted bed plane. Ideally (0, 0, 1).</summary>
    public required Vector3 PlaneNormal    { get; init; }

    /// <summary>Centroid of all valid bed-frame points.</summary>
    public required Vector3 PlaneCentroid  { get; init; }

    /// <summary>Angle between the fitted plane normal and (0,0,1), in degrees. 0 = perfectly level.</summary>
    public required float   TiltDeg        { get; init; }

    /// <summary>XY unit vector pointing in the direction the bed tilts toward (toward the low side).</summary>
    public required Vector2 TiltDirection  { get; init; }

    /// <summary>
    /// Fitted rotation-axis XY offset from the nominal bed origin, in mm.
    /// Null if the circle fit failed (insufficient rim points or degenerate geometry).
    /// </summary>
    public Vector2? RotationAxisOffset     { get; init; }

    /// <summary>Fitted bed radius in mm. Null when circle fit failed.</summary>
    public float?   BedRadiusMm           { get; init; }

    /// <summary>
    /// Z residuals (mm) relative to the fitted plane, on a regular XY grid.
    /// Row-major: index = row * HeightMapCols + col. NaN = no data in that cell.
    /// </summary>
    public required float[] HeightMap      { get; init; }
    public required int     HeightMapCols  { get; init; }
    public required int     HeightMapRows  { get; init; }

    /// <summary>World-space XY origin of the height map grid (bottom-left corner), in mm.</summary>
    public required float   HeightMapOriginX { get; init; }
    public required float   HeightMapOriginY { get; init; }

    /// <summary>Cell size of the height map grid in mm.</summary>
    public required float   HeightMapCellMm  { get; init; }
}

/// <summary>
/// Analyses the output of a <see cref="BedScanSequencer"/> run:
///   1. De-rotates each step's point cloud into a shared bed frame.
///   2. Fits a plane (SVD via Jacobi iteration) — gives tilt/levelling info.
///   3. Fits a circle to rim points — refines the rotation-axis XY position.
///   4. Builds a Z-residual height map on a regular grid.
///
/// All geometry is in the project's Z-up right-hand convention.
/// </summary>
public static class BedScanAnalyzer
{
    /// <param name="steps">Capture results from <see cref="BedScanSequencer.RunAsync"/>.</param>
    /// <param name="cameraToWorld">
    ///   4×4 matrix that transforms a point from the Zivid camera frame to world frame.
    ///   Computed by the caller via FK at the scan pose + scanner tool TCP.
    /// </param>
    /// <param name="bedOrigin">
    ///   World-space position of the rotary bed's rotation axis (from <c>lfam3.json bed.origin</c>).
    /// </param>
    /// <param name="heightMapCellMm">Cell size for the output height map grid.</param>
    public static BedScanResult Analyze(
        IReadOnlyList<BedScanStep> steps,
        Matrix4x4                 cameraToWorld,
        Vector3                   bedOrigin,
        float                     heightMapCellMm = 10f)
    {
        var pts = DerotateAll(steps, cameraToWorld, bedOrigin);

        if (pts.Count < 10)
            throw new InvalidOperationException(
                $"BedScanAnalyzer: only {pts.Count} valid points after de-rotation — scan data too sparse.");

        var (normal, centroid) = FitPlane(pts);

        // Ensure normal points toward +Z (away from the floor)
        if (normal.Z < 0) normal = -normal;

        float tiltDeg = MathF.Acos(Math.Clamp(Vector3.Dot(normal, Vector3.UnitZ), -1f, 1f))
                        * (180f / MathF.PI);

        var tiltXY  = new Vector2(normal.X, normal.Y);
        if (tiltXY.LengthSquared() > 1e-8f) tiltXY = Vector2.Normalize(tiltXY);

        Vector2? axisOffset = null;
        float?   bedRadius  = null;

        var rimPts = ExtractRimPoints(pts, rimFraction: 0.08f);
        if (rimPts.Count >= 8)
        {
            try
            {
                var (cx, cy, r) = FitCircle(rimPts);
                if (r > 0f)
                {
                    axisOffset = new Vector2(cx, cy); // offset from nominal origin
                    bedRadius  = r;
                }
            }
            catch { /* degenerate — leave null */ }
        }

        var (hmap, cols, rows, ox, oy) = BuildHeightMap(pts, normal, centroid, heightMapCellMm);

        return new BedScanResult
        {
            PlaneNormal      = normal,
            PlaneCentroid    = centroid,
            TiltDeg          = tiltDeg,
            TiltDirection    = tiltXY,
            RotationAxisOffset = axisOffset,
            BedRadiusMm      = bedRadius,
            HeightMap        = hmap,
            HeightMapCols    = cols,
            HeightMapRows    = rows,
            HeightMapOriginX = ox,
            HeightMapOriginY = oy,
            HeightMapCellMm  = heightMapCellMm,
        };
    }

    // ── De-rotation ─────────────────────────────────────────────────────────────

    private static List<Vector3> DerotateAll(
        IReadOnlyList<BedScanStep> steps,
        Matrix4x4                 camToWorld,
        Vector3                   bedOrigin)
    {
        var all = new List<Vector3>(steps.Sum(s => s.Capture.ValidPointCount));

        foreach (var step in steps)
        {
            // R_z(-θ) de-rotates the bed back to its reference position.
            float theta = (float)(step.E1Deg * Math.PI / 180.0);
            float cosT  = MathF.Cos(-theta);   // cos(θ)
            float sinT  = MathF.Sin(-theta);   // -sin(θ)

            var pts = step.Capture.PointsXYZ;
            int n   = pts.Length / 3;

            for (int i = 0; i < n; i++)
            {
                float cx = pts[i * 3];
                if (float.IsNaN(cx)) continue;
                float cy = pts[i * 3 + 1];
                float cz = pts[i * 3 + 2];

                // Camera frame → world frame (includes translation)
                var w = Vector3.Transform(new Vector3(cx, cy, cz), camToWorld);

                // Translate to rotation axis, then de-rotate around Z
                float dx = w.X - bedOrigin.X;
                float dy = w.Y - bedOrigin.Y;
                float dz = w.Z - bedOrigin.Z;

                all.Add(new Vector3(
                    cosT * dx - sinT * dy,
                    sinT * dx + cosT * dy,
                    dz));
            }
        }

        return all;
    }

    // ── Plane fit (3×3 covariance matrix, Jacobi eigenvector) ───────────────────

    private static (Vector3 normal, Vector3 centroid) FitPlane(List<Vector3> pts)
    {
        var c = Vector3.Zero;
        foreach (var p in pts) c += p;
        c /= pts.Count;

        double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
        foreach (var p in pts)
        {
            double dx = p.X - c.X, dy = p.Y - c.Y, dz = p.Z - c.Z;
            cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                             cyy += dy * dy; cyz += dy * dz;
                                              czz += dz * dz;
        }
        double n = pts.Count;
        cxx /= n; cxy /= n; cxz /= n; cyy /= n; cyz /= n; czz /= n;

        var normal = SmallestEigenvector(cxx, cxy, cxz, cyy, cyz, czz);
        return (normal, c);
    }

    // Jacobi eigenvalue algorithm for a 3×3 symmetric matrix.
    // Returns the unit eigenvector for the smallest eigenvalue (= plane normal).
    private static Vector3 SmallestEigenvector(
        double a00, double a01, double a02,
        double a11, double a12, double a22)
    {
        double[,] a = { { a00, a01, a02 }, { a01, a11, a12 }, { a02, a12, a22 } };
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };  // eigenvector columns

        for (int iter = 0; iter < 100; iter++)
        {
            // Largest off-diagonal magnitude
            int p = 0, q = 1;
            double maxOff = Math.Abs(a[0, 1]);
            if (Math.Abs(a[0, 2]) > maxOff) { maxOff = Math.Abs(a[0, 2]); p = 0; q = 2; }
            if (Math.Abs(a[1, 2]) > maxOff) { p = 1; q = 2; maxOff = Math.Abs(a[1, 2]); }
            if (maxOff < 1e-12) break;

            // Rotation angle: tan(2θ) = 2*a[p,q] / (a[q,q] - a[p,p])
            double theta = 0.5 * Math.Atan2(2.0 * a[p, q], a[q, q] - a[p, p]);
            double c = Math.Cos(theta), s = Math.Sin(theta);

            // Jacobi rotation matrix J (identity with (p,q) block replaced)
            double[,] J = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            J[p, p] =  c; J[p, q] = s;
            J[q, p] = -s; J[q, q] = c;

            a = Mul3(Mul3(Tr3(J), a), J);
            v = Mul3(v, J);
        }

        // Column of v corresponding to smallest diagonal entry of a
        int minIdx = 0;
        for (int i = 1; i < 3; i++)
            if (a[i, i] < a[minIdx, minIdx]) minIdx = i;

        return Vector3.Normalize(new Vector3(
            (float)v[0, minIdx], (float)v[1, minIdx], (float)v[2, minIdx]));
    }

    // ── Circle fit (algebraic least squares) ─────────────────────────────────────

    // Returns the top rimFraction of points by XY radius.
    private static List<Vector2> ExtractRimPoints(List<Vector3> pts, float rimFraction)
    {
        var radii = new float[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            radii[i] = MathF.Sqrt(pts[i].X * pts[i].X + pts[i].Y * pts[i].Y);

        var sorted = (float[])radii.Clone();
        Array.Sort(sorted);
        float threshold = sorted[(int)(sorted.Length * (1f - rimFraction))];

        var rim = new List<Vector2>();
        for (int i = 0; i < pts.Count; i++)
            if (radii[i] >= threshold)
                rim.Add(new Vector2(pts[i].X, pts[i].Y));
        return rim;
    }

    // Algebraic circle fit via least squares.
    // Linearisation: x²+y² = 2a*x + 2b*y + c  where c = r²-a²-b²
    // Solves normal equations A^T A [a,b,c] = A^T d.
    private static (float cx, float cy, float r) FitCircle(List<Vector2> pts)
    {
        double s11 = 0, s12 = 0, s13 = 0, s22 = 0, s23 = 0;
        int    n   = pts.Count;
        double r1  = 0, r2 = 0, r3 = 0;

        foreach (var p in pts)
        {
            double x = p.X, y = p.Y, d = x * x + y * y;
            double tx = 2 * x, ty = 2 * y;
            s11 += tx * tx; s12 += tx * ty; s13 += tx;
                             s22 += ty * ty; s23 += ty;
            r1  += tx * d;  r2  += ty * d;  r3  += d;
        }

        double[,] A = {
            { s11, s12, s13 },
            { s12, s22, s23 },
            { s13, s23, n   }
        };

        var sol = Solve3(A, [r1, r2, r3]);
        double cx = sol[0], cy = sol[1], c = sol[2];
        double rSq = c + cx * cx + cy * cy;
        if (rSq <= 0) throw new InvalidOperationException("Circle fit: negative r².");
        return ((float)cx, (float)cy, (float)Math.Sqrt(rSq));
    }

    // ── Height map (Z residuals on XY grid) ──────────────────────────────────────

    private static (float[] map, int cols, int rows, float ox, float oy) BuildHeightMap(
        List<Vector3> pts, Vector3 normal, Vector3 centroid, float cellMm)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }

        int cols = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / cellMm) + 1);
        int rows = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / cellMm) + 1);

        var sum = new double[cols * rows];
        var cnt = new int[cols * rows];

        foreach (var p in pts)
        {
            int col = Math.Clamp((int)((p.X - minX) / cellMm), 0, cols - 1);
            int row = Math.Clamp((int)((p.Y - minY) / cellMm), 0, rows - 1);
            int idx = row * cols + col;
            sum[idx] += Vector3.Dot(p - centroid, normal);  // signed distance from plane
            cnt[idx]++;
        }

        var map = new float[cols * rows];
        for (int i = 0; i < map.Length; i++)
            map[i] = cnt[i] > 0 ? (float)(sum[i] / cnt[i]) : float.NaN;

        return (map, cols, rows, minX, minY);
    }

    // ── 3×3 matrix helpers ───────────────────────────────────────────────────────

    private static double[,] Mul3(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    r[i, j] += a[i, k] * b[k, j];
        return r;
    }

    private static double[,] Tr3(double[,] a)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i, j] = a[j, i];
        return r;
    }

    // Gaussian elimination with partial pivoting for a 3×3 system Ax = b.
    private static double[] Solve3(double[,] A, double[] b)
    {
        var M = new double[3, 4];
        for (int i = 0; i < 3; i++)
        {
            M[i, 0] = A[i, 0]; M[i, 1] = A[i, 1];
            M[i, 2] = A[i, 2]; M[i, 3] = b[i];
        }

        for (int col = 0; col < 3; col++)
        {
            int maxRow = col;
            for (int row = col + 1; row < 3; row++)
                if (Math.Abs(M[row, col]) > Math.Abs(M[maxRow, col])) maxRow = row;
            for (int k = 0; k <= 3; k++) (M[col, k], M[maxRow, k]) = (M[maxRow, k], M[col, k]);

            double diag = M[col, col];
            if (Math.Abs(diag) < 1e-12)
                throw new InvalidOperationException("Circle fit: singular normal equations.");
            for (int k = col; k <= 3; k++) M[col, k] /= diag;

            for (int row = 0; row < 3; row++)
            {
                if (row == col) continue;
                double f = M[row, col];
                for (int k = col; k <= 3; k++) M[row, k] -= f * M[col, k];
            }
        }

        return [M[0, 3], M[1, 3], M[2, 3]];
    }
}
