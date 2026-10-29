using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Rigid point-to-point ICP (translation + rotation) for aligning scan clouds in world mm.
/// Row-vector convention: <c>p' = p * T</c>.
/// </summary>
public static class ScanPointCloudAligner
{
    private const int MaxIterations     = 24;
    private const float MaxPairDistMm   = 12f;
    private const float ConvergenceMm   = 0.05f;
    private const int MaxSamplePoints   = 4000;

    /// <summary>
    /// Returns a row-vector transform that aligns <paramref name="moving"/> to
    /// <paramref name="reference"/> (identity when clouds already coincide).
    /// </summary>
    public static Matrix4 AlignToReference(IReadOnlyList<Vector3> reference, IReadOnlyList<Vector3> moving)
        => AlignToReference(reference, moving, rotationOnly: false);

    /// <summary>
    /// Rotation-only variant: optimizes overlap without translating the moving cloud's centroid.
    /// Used when merging already-registered rotary scans that should stay on the bed.
    /// </summary>
    public static Matrix4 AlignToReferenceRotationOnly(IReadOnlyList<Vector3> reference, IReadOnlyList<Vector3> moving)
        => AlignToReference(reference, moving, rotationOnly: true);

    static Matrix4 AlignToReference(IReadOnlyList<Vector3> reference, IReadOnlyList<Vector3> moving, bool rotationOnly)
    {
        if (reference.Count < 3 || moving.Count < 3)
            return Matrix4.Identity;

        var refSample = Subsample(reference, MaxSamplePoints);
        var movSample = Subsample(moving, MaxSamplePoints);

        var accum = Matrix4.Identity;
        var transformed = movSample.ToArray();

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            if (!TryBuildCorrespondences(refSample, transformed, out var src, out var dst))
                break;

            var step = rotationOnly
                ? KabschRowVectorRotationOnly(src, dst)
                : KabschRowVector(src, dst);
            for (int i = 0; i < transformed.Length; i++)
                transformed[i] = TransformPoint(transformed[i], step);

            // Row-vector: p * (accum * step) applies the new step last, matching the
            // sample loop above. step * accum applies the passes backwards and spins
            // a cloud that needed more than one iteration.
            accum = accum * step;

            float maxShift = 0f;
            for (int i = 0; i < src.Length; i++)
            {
                var d = (TransformPoint(src[i], step) - src[i]).Length;
                if (d > maxShift) maxShift = d;
            }
            if (maxShift < ConvergenceMm)
                break;
        }

        return accum;
    }

    private static bool TryBuildCorrespondences(
        IReadOnlyList<Vector3> reference,
        IReadOnlyList<Vector3> moving,
        out Vector3[] src,
        out Vector3[] dst)
    {
        var srcList = new List<Vector3>(moving.Count);
        var dstList = new List<Vector3>(moving.Count);
        float maxPairSq = MaxPairDistMm * MaxPairDistMm;

        foreach (var p in moving)
        {
            var nearest = FindNearest(reference, p, out float distSq);
            if (nearest is null || distSq > maxPairSq) continue;
            srcList.Add(p);
            dstList.Add(nearest.Value);
        }

        if (srcList.Count < 3)
        {
            src = [];
            dst = [];
            return false;
        }

        src = srcList.ToArray();
        dst = dstList.ToArray();
        return true;
    }

    private static Vector3? FindNearest(IReadOnlyList<Vector3> points, Vector3 query, out float distSq)
    {
        distSq = float.MaxValue;
        Vector3? best = null;
        foreach (var p in points)
        {
            float d = (p - query).LengthSquared;
            if (d >= distSq) continue;
            distSq = d;
            best   = p;
        }
        return best;
    }

    /// <summary>Kabsch: returns T such that <c>src[i] * T ≈ dst[i]</c> (row-vector).</summary>
    private static Matrix4 KabschRowVector(ReadOnlySpan<Vector3> src, ReadOnlySpan<Vector3> dst)
    {
        var srcCentroid = Centroid(src);
        var dstCentroid = Centroid(dst);

        float s00 = 0, s01 = 0, s02 = 0;
        float s10 = 0, s11 = 0, s12 = 0;
        float s20 = 0, s21 = 0, s22 = 0;

        for (int i = 0; i < src.Length; i++)
        {
            var a = src[i] - srcCentroid;
            var b = dst[i] - dstCentroid;
            s00 += a.X * b.X; s01 += a.X * b.Y; s02 += a.X * b.Z;
            s10 += a.Y * b.X; s11 += a.Y * b.Y; s12 += a.Y * b.Z;
            s20 += a.Z * b.X; s21 += a.Z * b.Y; s22 += a.Z * b.Z;
        }

        // H_ij = Σ a_i b_j. Its polar factor Q (H = Q P) is the row-vector
        // rotation, because a * Q ≈ b.
        var h = new Matrix3(
            s00, s01, s02,
            s10, s11, s12,
            s20, s21, s22);

        var r = PolarRotation(h);

        var rot4 = new Matrix4(
            r.M11, r.M12, r.M13, 0,
            r.M21, r.M22, r.M23, 0,
            r.M31, r.M32, r.M33, 0,
            0, 0, 0, 1);
        var rotatedSrcCentroid = TransformPoint(srcCentroid, rot4);
        var t = dstCentroid - rotatedSrcCentroid;

        return new Matrix4(
            r.M11, r.M12, r.M13, 0,
            r.M21, r.M22, r.M23, 0,
            r.M31, r.M32, r.M33, 0,
            t.X,   t.Y,   t.Z,   1);
    }

    /// <summary>Rotation about the moving cloud centroid — no net translation.</summary>
    private static Matrix4 KabschRowVectorRotationOnly(ReadOnlySpan<Vector3> src, ReadOnlySpan<Vector3> dst)
    {
        var srcCentroid = Centroid(src);
        var dstCentroid = Centroid(dst);

        float s00 = 0, s01 = 0, s02 = 0;
        float s10 = 0, s11 = 0, s12 = 0;
        float s20 = 0, s21 = 0, s22 = 0;

        for (int i = 0; i < src.Length; i++)
        {
            var a = src[i] - srcCentroid;
            var b = dst[i] - dstCentroid;
            s00 += a.X * b.X; s01 += a.X * b.Y; s02 += a.X * b.Z;
            s10 += a.Y * b.X; s11 += a.Y * b.Y; s12 += a.Y * b.Z;
            s20 += a.Z * b.X; s21 += a.Z * b.Y; s22 += a.Z * b.Z;
        }

        var h = new Matrix3(
            s00, s01, s02,
            s10, s11, s12,
            s20, s21, s22);

        var r = PolarRotation(h);
        var rot4 = new Matrix4(
            r.M11, r.M12, r.M13, 0,
            r.M21, r.M22, r.M23, 0,
            r.M31, r.M32, r.M33, 0,
            0, 0, 0, 1);

        return RotationAboutPoint(srcCentroid, rot4);
    }

    private static Matrix4 RotationAboutPoint(Vector3 pivot, Matrix4 rotation)
    {
        var toOrigin   = Matrix4.CreateTranslation(-pivot.X, -pivot.Y, -pivot.Z);
        var fromOrigin = Matrix4.CreateTranslation(pivot.X, pivot.Y, pivot.Z);
        return toOrigin * rotation * fromOrigin;
    }

    /// <summary>
    /// Orthogonal factor of a 3×3 covariance. Gram-Schmidt on the rows leaves the
    /// stretch of a long scan in the rotation, which throws the cloud off the bed.
    /// </summary>
    private static Matrix3 PolarRotation(Matrix3 h)
    {
        double[,] x =
        {
            { h.M11, h.M12, h.M13 },
            { h.M21, h.M22, h.M23 },
            { h.M31, h.M32, h.M33 },
        };

        double scale = 0;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                scale += x[r, c] * x[r, c];
        scale = Math.Sqrt(scale);
        if (scale < 1e-8)
            return Matrix3.Identity;

        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                x[r, c] /= scale;

        // Newton-Schulz: X ← ½ X (3I − XᵀX), scaled so singular values sit in (0, √3).
        for (int iter = 0; iter < 16; iter++)
        {
            double[,] xt = Transpose3(x);
            double[,] xtx = Mul3(xt, x);
            xtx[0, 0] = 3 - xtx[0, 0];
            xtx[1, 1] = 3 - xtx[1, 1];
            xtx[2, 2] = 3 - xtx[2, 2];
            xtx[0, 1] = -xtx[0, 1];
            xtx[0, 2] = -xtx[0, 2];
            xtx[1, 0] = -xtx[1, 0];
            xtx[1, 2] = -xtx[1, 2];
            xtx[2, 0] = -xtx[2, 0];
            xtx[2, 1] = -xtx[2, 1];
            x = Mul3(x, xtx);
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    x[r, c] *= 0.5;
        }

        var r0 = new Vector3((float)x[0, 0], (float)x[0, 1], (float)x[0, 2]);
        var r1 = new Vector3((float)x[1, 0], (float)x[1, 1], (float)x[1, 2]);
        var r2 = new Vector3((float)x[2, 0], (float)x[2, 1], (float)x[2, 2]);
        if (r0.LengthSquared < 1e-10f || r1.LengthSquared < 1e-10f)
            return Matrix3.Identity;
        r0.Normalize();
        r1 -= r0 * Vector3.Dot(r1, r0);
        if (r1.LengthSquared < 1e-10f)
            return Matrix3.Identity;
        r1.Normalize();
        var crossed = Vector3.Cross(r0, r1);
        // A reflection (det −1) would mirror the scan. Force a proper rotation.
        if (Vector3.Dot(crossed, r2) < 0f)
            crossed = -crossed;
        r2 = crossed.Normalized();
        return new Matrix3(
            r0.X, r0.Y, r0.Z,
            r1.X, r1.Y, r1.Z,
            r2.X, r2.Y, r2.Z);
    }

    private static double[,] Transpose3(double[,] m)
    {
        return new[,]
        {
            { m[0, 0], m[1, 0], m[2, 0] },
            { m[0, 1], m[1, 1], m[2, 1] },
            { m[0, 2], m[1, 2], m[2, 2] },
        };
    }

    private static double[,] Mul3(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return r;
    }

    private static Vector3 Centroid(ReadOnlySpan<Vector3> pts)
    {
        if (pts.Length == 0) return Vector3.Zero;
        var sum = Vector3.Zero;
        foreach (var p in pts)
            sum += p;
        return sum / pts.Length;
    }

    private static Vector3[] Subsample(IReadOnlyList<Vector3> points, int maxCount)
    {
        if (points.Count <= maxCount)
            return points.ToArray();

        int step = Math.Max(1, points.Count / maxCount);
        var sample = new List<Vector3>(maxCount);
        for (int i = 0; i < points.Count; i += step)
            sample.Add(points[i]);
        return sample.ToArray();
    }

    public static Vector3 TransformPoint(Vector3 p, Matrix4 m)
        => new(
            p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);

    internal static Vector3 TransformNormal(Vector3 n, Matrix4 m)
    {
        var x = n.X * m.M11 + n.Y * m.M21 + n.Z * m.M31;
        var y = n.X * m.M12 + n.Y * m.M22 + n.Z * m.M32;
        var z = n.X * m.M13 + n.Y * m.M23 + n.Z * m.M33;
        var len = MathF.Sqrt(x * x + y * y + z * z);
        return len > 1e-6f ? new Vector3(x / len, y / len, z / len) : n;
    }
}