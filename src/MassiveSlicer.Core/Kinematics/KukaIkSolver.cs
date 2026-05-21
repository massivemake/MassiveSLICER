using System.Numerics;

namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Analytical OPW inverse / forward kinematics for the KR120 R3900-2 K.
///
/// OPW parameters (from $machine.dat at 192.168.0.152):
///   a1=730  a2=-115  b=0  c1=550  c2=1350  c3=1820  c4=215  (all mm)
///
/// KRL to math angle convention:
///   math = sign * (krl_rad - offset)
///   offsets = [0, -pi/2, pi/2, 0, 0, pi],  signs = [1, 1, 1, 1, 1, 1]
///
/// Uses System.Numerics.Matrix4x4 / Vector3 (row-vector convention, same as OpenTK).
/// Tool Z-axis (column 2 of standard R) = row 2 of the row-major matrix = M31, M32, M33.
/// </summary>
public static class KukaIkSolver
{
    private const float _A1 =  730f;
    private const float _A2 = -115f;
    private const float _B  =    0f;
    private const float _C1 =  550f;
    private const float _C2 = 1350f;
    private const float _C3 = 1820f;
    private const float _C4 =  215f;

    private static readonly float[] Offsets = [0f, -MathF.PI / 2f, MathF.PI / 2f, 0f, 0f, MathF.PI];
    private static readonly int[]   Signs   = [1, 1, 1, 1, 1, 1];

    private static readonly (float Min, float Max)[] Limits =
    [
        (-60f,  60f), (-120f, 70f), (-120f, 168f),
        (-350f, 350f), (-125f, 125f), (-350f, 350f),
    ];

    // DH table (matches kinematics.js _DH_ALPHA / _DH_A / _DH_D)
    private static readonly float[] DhAlpha = [-MathF.PI / 2f, 0f, -MathF.PI / 2f, MathF.PI / 2f, -MathF.PI / 2f, 0f];
    private static readonly float[] DhA     = [_A1, _C2, 115f, 0f, 0f, 0f];
    // d4 is negative: after the alpha chain the local Z at joint 4 points in -scene_Z,
    // so a positive C3 would translate the wrist downward. Negating corrects this.
    private static readonly float[] DhD     = [_C1, 0f, 0f, -_C3, 0f, _C4];

    // ── IK solution record ────────────────────────────────────────────────────

    public sealed record IkSolution(float[] Krl, bool InLimits, bool Unreachable, bool Singular);

    // ── ABC <-> Matrix conversion ─────────────────────────────────────────────

    /// <summary>
    /// Converts KUKA ABC orientation (degrees, ZYX Euler) to a row-major rotation matrix.
    /// R = Rz(A)*Ry(B)*Rx(C) in column-vector convention; stored as R^T in row-vector form.
    /// </summary>
    public static Matrix4x4 AbcToMatrix(float aDeg, float bDeg, float cDeg)
    {
        float a = aDeg * MathF.PI / 180f;
        float b = bDeg * MathF.PI / 180f;
        float c = cDeg * MathF.PI / 180f;
        // Row-vector order: Rx(C) applied first, then Ry(B), then Rz(A).
        return Matrix4x4.CreateRotationX(c) * Matrix4x4.CreateRotationY(b) * Matrix4x4.CreateRotationZ(a);
    }

    /// <summary>
    /// Extracts KUKA ABC angles (degrees) from a row-major rotation matrix.
    /// Inverse of <see cref="AbcToMatrix"/>.
    /// </summary>
    public static (float A, float B, float C) MatrixToAbc(Matrix4x4 m)
    {
        // For M = R^T where R = Rz(A)*Ry(B)*Rx(C):
        //   M13 = -sinB,  M11 = cosA*cosB,  M12 = sinA*cosB
        //   M23 = cosB*sinC,  M33 = cosB*cosC
        float sinB = Math.Clamp(-m.M13, -1f, 1f);
        float bRad = MathF.Asin(sinB);
        float cosB = MathF.Cos(bRad);
        float aRad, cRad;
        if (MathF.Abs(cosB) > 1e-6f)
        {
            aRad = MathF.Atan2(m.M12, m.M11);
            cRad = MathF.Atan2(m.M23, m.M33);
        }
        else
        {
            aRad = 0f;
            cRad = MathF.Atan2(-m.M21, m.M22);
        }
        return (aRad * 180f / MathF.PI, bRad * 180f / MathF.PI, cRad * 180f / MathF.PI);
    }

    // ── Forward kinematics ────────────────────────────────────────────────────

    /// <summary>
    /// OPW DH forward kinematics. Returns the row-major transform from flange frame
    /// to ROBROOT frame. Translation = flange position in mm (M41, M42, M43).
    /// </summary>
    public static Matrix4x4 ForwardKinematics(float[] krlDeg)
    {
        var math = KrlToMath(krlDeg);
        // Build combined transform = (T1*T2*...*T6)^T in row-vector convention.
        // Accumulating T = DhStep(i) * T iterates from joint 1 outward,
        // giving T6^T * ... * T1^T = (T1*...*T6)^T.
        var T = Matrix4x4.Identity;
        for (int i = 0; i < 6; i++)
            T = DhStep(DhA[i], DhD[i], DhAlpha[i], math[i]) * T;
        return T;
    }

    /// <summary>
    /// Computes nozzle-tip world position from KRL joint angles, the ROBROOT world position,
    /// and the tool TCP offset vector in flange-local frame (mm).
    /// </summary>
    public static Vector3 ComputeTcpWorldPos(float[] krlDeg, Vector3 robrootWorld, Vector3 tcpOffset)
    {
        var fk      = ForwardKinematics(krlDeg);
        var flangeP = new Vector3(fk.M41, fk.M42, fk.M43);
        var offWorld = Vector3.TransformNormal(tcpOffset, fk);  // rotate to ROBROOT frame
        return robrootWorld + flangeP + offWorld;
    }

    // ── Inverse kinematics ────────────────────────────────────────────────────

    /// <summary>
    /// Returns all 8 OPW IK solutions for a given flange target.
    /// <paramref name="flangePos"/> is the OPW flange reference point in ROBROOT frame (mm).
    /// </summary>
    public static IkSolution[] SolveAll(Vector3 flangePos, Matrix4x4 rotMat)
    {
        float px = flangePos.X, py = flangePos.Y, pz = flangePos.Z;

        // Tool axes (column 2, 0, 1 of standard R = rows 2, 0, 1 of R^T = M3x, M1x, M2x):
        float zx = rotMat.M31, zy = rotMat.M32, zz = rotMat.M33;  // Z-axis
        float xx = rotMat.M11, xy = rotMat.M12, xz = rotMat.M13;  // X-axis
        float yx = rotMat.M21, yy = rotMat.M22, yz = rotMat.M23;  // Y-axis

        // Wrist centre: step back C4 along tool Z from flange, then subtract C1
        float wx = px - _C4 * zx;
        float wy = py - _C4 * zy;
        float wz = pz - _C4 * zz - _C1;

        float nx1    = MathF.Sqrt(MathF.Max(0f, wx * wx + wy * wy - _B * _B)) - _A1;
        float kappa2 = _A2 * _A2 + _C3 * _C3;
        float c2sq   = _C2 * _C2;
        float s1sq   = nx1 * nx1 + wz * wz;
        float s2sq   = (nx1 + 2 * _A1) * (nx1 + 2 * _A1) + wz * wz;
        float tmp10  = MathF.Atan2(_A2, _C3);
        float tmp9   = 2f * _C2 * MathF.Sqrt(kappa2);

        var solutions = new List<IkSolution>(8);

        for (int sh = 0; sh < 2; sh++)
        {
            float tmp1 = MathF.Atan2(wy, wx);
            float tmp2 = MathF.Atan2(_B, nx1 + _A1);
            float j0   = sh != 0 ? tmp1 + tmp2 - MathF.PI : tmp1 - tmp2;
            float s0   = MathF.Sin(j0), c0 = MathF.Cos(j0);

            for (int el = 0; el < 2; el++)
            {
                float j1, j2;
                bool  bad = false;

                if (sh == 0)
                {
                    float s1 = MathF.Sqrt(s1sq);
                    if (s1 < 1e-6f)
                    {
                        bad = true; j1 = j2 = 0f;
                    }
                    else
                    {
                        float t5 = Math.Clamp((s1sq + c2sq - kappa2) / (2f * s1 * _C2), -1f, 1f);
                        float t7 = Math.Clamp((s1sq - c2sq - kappa2) / tmp9,              -1f, 1f);
                        j1 = el != 0 ?  MathF.Acos(t5) + MathF.Atan2(nx1, wz)
                                     : -MathF.Acos(t5) + MathF.Atan2(nx1, wz);
                        j2 = el != 0 ? -MathF.Acos(t7) - tmp10
                                     :  MathF.Acos(t7) - tmp10;
                    }
                }
                else
                {
                    float s2 = MathF.Sqrt(s2sq);
                    if (s2 < 1e-6f)
                    {
                        bad = true; j1 = j2 = 0f;
                    }
                    else
                    {
                        float t6 = Math.Clamp((s2sq + c2sq - kappa2) / (2f * s2 * _C2), -1f, 1f);
                        float t8 = Math.Clamp((s2sq - c2sq - kappa2) / tmp9,              -1f, 1f);
                        j1 = el != 0 ? -MathF.Acos(t6) - MathF.Atan2(nx1 + 2 * _A1, wz)
                                     :  MathF.Acos(t6) - MathF.Atan2(nx1 + 2 * _A1, wz);
                        j2 = el != 0 ?  MathF.Acos(t8) - tmp10
                                     : -MathF.Acos(t8) - tmp10;
                    }
                }

                float s23 = MathF.Sin(j1 + j2), c23 = MathF.Cos(j1 + j2);
                float mj4 = zx * s23 * c0 + zy * s23 * s0 + zz * c23;

                for (int wf = 0; wf < 2; wf++)
                {
                    const float Sing = 0.0124f;
                    float j3, j4, j5;

                    j4 = MathF.Atan2(MathF.Sqrt(MathF.Max(0f, 1f - mj4 * mj4)), mj4);
                    if (wf != 0) j4 = -j4;

                    if (MathF.Abs(j4) < Sing)
                    {
                        // Wrist singularity: A4 and A6 are co-axial; lock A4 = 0
                        j3 = 0f;
                        float col0x = c0 * zz;
                        float col0y = s0 * zz;
                        float col0z = (-s0) * zy - c0 * zx;
                        j5 = MathF.Atan2(
                            (-s0) * xx + c0 * xy,
                            col0x * xx + col0y * xy + col0z * xz
                        );
                    }
                    else
                    {
                        j3 = MathF.Atan2(
                            zy * c0 - zx * s0,
                            zx * c23 * c0 + zy * c23 * s0 - zz * s23
                        );
                        j5 = MathF.Atan2(
                             yx * s23 * c0 + yy * s23 * s0 + yz * c23,
                            -xx * s23 * c0 - xy * s23 * s0 - xz * c23
                        );
                    }

                    if (wf != 0) { j3 += MathF.PI; j5 -= MathF.PI; }

                    var  krl   = MathToKrl([j0, j1, j2, j3, j4, j5]);
                    bool inLim = !bad;
                    if (inLim)
                    {
                        for (int i = 0; i < 6; i++)
                            if (krl[i] < Limits[i].Min || krl[i] > Limits[i].Max)
                            { inLim = false; break; }
                    }

                    solutions.Add(new IkSolution(krl, inLim, bad, MathF.Abs(j4) < Sing));
                }
            }
        }

        return solutions.ToArray();
    }

    /// <summary>
    /// Solves IK for a nozzle-tip target position in ROBROOT frame.
    /// <paramref name="tcpOffset"/> is the tool TCP offset in flange-local frame (mm),
    /// or <see langword="null"/> to target the flange directly.
    /// Returns 6 KRL angles (degrees) closest to <paramref name="seed"/>,
    /// or <see langword="null"/> if no solution can be computed.
    /// </summary>
    public static float[]? Solve(
        Vector3 nozzlePos, float aDeg, float bDeg, float cDeg,
        float[] seed, Vector3? tcpOffset = null)
    {
        var rotMat    = AbcToMatrix(aDeg, bDeg, cDeg);
        var flangePos = nozzlePos;
        if (tcpOffset.HasValue)
            flangePos -= Vector3.TransformNormal(tcpOffset.Value, rotMat);

        var solutions = SolveAll(flangePos, rotMat);

        var valid = Filter(solutions, s => s.InLimits && !s.Unreachable);
        var pool  = valid.Length > 0 ? valid : Filter(solutions, s => !s.Unreachable);
        var cands = pool.Length  > 0 ? pool  : solutions;

        IkSolution? best    = null;
        float       bestDst = float.MaxValue;
        foreach (var sol in cands)
        {
            float d = 0f;
            for (int i = 0; i < 6; i++) { float diff = sol.Krl[i] - seed[i]; d += diff * diff; }
            if (d < bestDst) { bestDst = d; best = sol; }
        }

        if (best is null) return null;

        var angles = new float[6];
        for (int i = 0; i < 6; i++)
            angles[i] = Math.Clamp(best.Krl[i], Limits[i].Min, Limits[i].Max);
        return angles;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static IkSolution[] Filter(IkSolution[] all, Func<IkSolution, bool> pred)
    {
        int n = 0;
        foreach (var s in all) if (pred(s)) n++;
        var r = new IkSolution[n];
        int j = 0;
        foreach (var s in all) if (pred(s)) r[j++] = s;
        return r;
    }

    private static float[] KrlToMath(float[] krl)
    {
        var r = new float[6];
        for (int i = 0; i < 6; i++)
            r[i] = Signs[i] * (krl[i] * MathF.PI / 180f - Offsets[i]);
        return r;
    }

    private static float[] MathToKrl(float[] math)
    {
        var r = new float[6];
        for (int i = 0; i < 6; i++)
        {
            float k = (Signs[i] * math[i] + Offsets[i]) * 180f / MathF.PI;
            while (k >  180f) k -= 360f;
            while (k < -180f) k += 360f;
            r[i] = k;
        }
        return r;
    }

    // DH step matrix in row-vector convention (= standard DH matrix transposed).
    // Translation row (M41, M42, M43): (a*cos, a*sin, d).
    private static Matrix4x4 DhStep(float a, float d, float alpha, float theta)
    {
        float c  = MathF.Cos(theta), s  = MathF.Sin(theta);
        float ca = MathF.Cos(alpha), sa = MathF.Sin(alpha);
        return new Matrix4x4(
             c,       s,    0f,  0f,
            -s * ca,  c * ca, sa,  0f,
             s * sa, -c * sa, ca,  0f,
             a * c,   a * s,  d,   1f
        );
    }
}
