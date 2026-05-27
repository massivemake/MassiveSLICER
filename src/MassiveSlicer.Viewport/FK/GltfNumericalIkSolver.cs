using MassiveSlicer.Core.Kinematics;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.FK;

/// <summary>
/// Position-only and position+orientation IK solver that uses the same GLTF
/// bone-chain FK as the viewport display. Angles are guaranteed consistent with
/// what you see on screen.
///
/// Algorithm: damped least-squares (DLS) with a finite-difference Jacobian.
/// Position-only variant: 3D task space, 3×3 DLS matrix.
/// Orientation-constrained variant: 6D task space (pos + rot), 6×6 DLS matrix.
/// </summary>
public sealed class GltfNumericalIkSolver
{
    private readonly Matrix4[] _restPose;
    private readonly Matrix4   _chainRoot;      // WorldTransform of joint_1's parent (exact scene FK base)
    private readonly Matrix4   _tcpLocal;       // TCP offset as a local transform in GLTF space
    private readonly Vector3   _robotWorldPos;  // ROBROOT origin in scene space (for ROBROOT ↔ scene conversion)
    private readonly float     _toolFrameRoll;  // rotation around flange outward axis (radians), matches SyncTcpReadout

    private readonly JointConfig[] _jcfg;

    /// <param name="restPoses">Per-joint rest-pose local transforms, from <see cref="RobotFkController.RestPoses"/>.</param>
    /// <param name="chainRoot">WorldTransform of joint_1's parent, from <see cref="RobotFkController.ChainRootTransform"/>.</param>
    /// <param name="robotWorldPos">ROBROOT origin in scene space (mm).</param>
    /// <param name="tcpLocal">TCP offset as a GLTF-space local transform (pure translation).</param>
    /// <param name="jointConfigs">Per-joint axis, sign, offset, and limits for A1–A6.</param>
    public GltfNumericalIkSolver(IReadOnlyList<Matrix4> restPoses, Matrix4 chainRoot,
                                  Vector3 robotWorldPos, Matrix4 tcpLocal,
                                  IReadOnlyList<JointConfig> jointConfigs,
                                  float toolFrameRoll = 0f)
    {
        _restPose      = restPoses.ToArray();
        _chainRoot     = chainRoot;
        _robotWorldPos = robotWorldPos;
        _tcpLocal      = tcpLocal;
        _toolFrameRoll = toolFrameRoll;
        _jcfg          = jointConfigs.ToArray();
    }

    // ── FK ────────────────────────────────────────────────────────────────────

    private Matrix4 ComputeJoint6Transform(float[] krl)
    {
        var wt = _chainRoot;
        for (int i = 0; i < 6; i++)
        {
            var   cfg       = _jcfg[i];
            float boneAngle = cfg.KrlSign * krl[i] * MathF.PI / 180f;
            var   rot       = cfg.Axis switch
            {
                RotationAxis.X => Matrix4.CreateRotationX(boneAngle),
                RotationAxis.Z => Matrix4.CreateRotationZ(boneAngle),
                _              => Matrix4.CreateRotationY(boneAngle),
            };
            wt = rot * _restPose[i] * wt;
        }
        return wt;
    }

    /// <summary>Returns the flange (joint_6) position in scene space.</summary>
    public Vector3 ComputeFlangePosScene(float[] krl)
        => ComputeJoint6Transform(krl).Row3.Xyz;

    /// <summary>Returns the TCP position in scene space (flange + TCP offset).</summary>
    public Vector3 ComputeTcpPosScene(float[] krl)
        => (_tcpLocal * ComputeJoint6Transform(krl)).Row3.Xyz;

    /// <summary>
    /// Returns the normalized rotation rows of joint_6 in scene space.
    /// Since <see cref="_tcpLocal"/> is a pure translation, these also describe the TCP frame.
    /// Pass to <see cref="Solve(Vector3, float[], ValueTuple{Vector3, Vector3, Vector3})"/>
    /// as the orientation target.
    /// </summary>
    public (Vector3 r0, Vector3 r1, Vector3 r2) ComputeFlangeRotNorm(float[] krl)
    {
        var   wt = ComputeJoint6Transform(krl);
        float sc = wt.Row0.Xyz.Length;
        return (wt.Row0.Xyz / sc, wt.Row1.Xyz / sc, wt.Row2.Xyz / sc);
    }

    /// <summary>
    /// Converts KUKA ABC Euler ZYX angles (degrees) to the normalized joint_6
    /// scene-space rotation rows expected by
    /// <see cref="Solve(Vector3, float[], ValueTuple{Vector3, Vector3, Vector3})"/>.
    /// Inverts the SyncTcpReadout toolFrameRoll mapping so the orientation
    /// constraint targets the specified tool-frame orientation exactly.
    /// </summary>
    public (Vector3 r0, Vector3 r1, Vector3 r2) TargetRotFromKukaAbc(float aDeg, float bDeg, float cDeg)
    {
        float a = aDeg * MathF.PI / 180f;
        float b = bDeg * MathF.PI / 180f;
        float c = cDeg * MathF.PI / 180f;

        float ca = MathF.Cos(a), sa = MathF.Sin(a);
        float cb = MathF.Cos(b), sb = MathF.Sin(b);
        float cc = MathF.Cos(c), sc = MathF.Sin(c);

        // KUKA ZYX Euler R = Rz(A)*Ry(B)*Rx(C), column-vector convention.
        // Columns = tool X, Y, Z axes expressed in ROBROOT frame.
        var kukaX = new Vector3(ca * cb,                  sa * cb,                  -sb);
        var kukaY = new Vector3(ca * sb * sc - sa * cc,   sa * sb * sc + ca * cc,    cb * sc);
        var kukaZ = new Vector3(ca * sb * cc + sa * sc,   sa * sb * cc - ca * sc,    cb * cc);

        // Invert the toolFrameRoll applied in SyncTcpReadout:
        //   kukaX = cr*r0 + sr*r2,  kukaY = sr*r0 − cr*r2,  kukaZ = r1
        // → r0 = cr*kukaX + sr*kukaY,  r1 = kukaZ,  r2 = sr*kukaX − cr*kukaY
        float cr = MathF.Cos(_toolFrameRoll);
        float sr = MathF.Sin(_toolFrameRoll);
        return (cr * kukaX + sr * kukaY, kukaZ, sr * kukaX - cr * kukaY);
    }

    /// <summary>
    /// Computes the orientation target for a tool that approaches along the slicing-plane
    /// normal, i.e. with the tool coming in from above along <c>-normal</c>.
    ///
    /// Derivation: the solver's <c>kukaX</c> direction is the tool approach vector.
    /// Setting <c>kukaX = -normal</c> and solving KUKA ZYX Euler gives:
    /// <code>
    ///   B = asin(normal.Z)
    ///   A = atan2(-normal.Y, -normal.X)   (0 when cos(B) ≈ 0 — gimbal lock)
    ///   C = 0                              (no tool spin)
    /// </code>
    /// Then delegates to <see cref="TargetRotFromKukaAbc"/> so the result is
    /// consistent with the tool-drag solver.
    ///
    /// <b>Note:</b> this leaves the tool spin around the approach axis undefined (C=0).
    /// Prefer <see cref="TargetRotFromPathFrame"/> when the toolpath tangent is available,
    /// as it fully determines the tool frame.
    /// </summary>
    public (Vector3 r0, Vector3 r1, Vector3 r2) TargetRotFromPlaneNormal(Vector3 normal)
    {
        float b    = MathF.Asin(Math.Clamp(normal.Z, -1f, 1f));
        float cosB = MathF.Cos(b);
        float a    = MathF.Abs(cosB) > 1e-6f ? MathF.Atan2(-normal.Y, -normal.X) : 0f;

        return TargetRotFromKukaAbc(
            a * (180f / MathF.PI),
            b * (180f / MathF.PI),
            0f);
    }

    /// <summary>
    /// Same as <see cref="TargetRotFromPlaneNormal"/> but adds the given KUKA Euler
    /// angle offsets (degrees) on top of the plane-normal-derived A, B and zero C.
    ///
    /// Because the offset is added directly to the Euler angles before the GLTF/toolFrameRoll
    /// mapping, <c>(0, 0, 0)</c> is a true identity offset — identical to calling
    /// <see cref="TargetRotFromPlaneNormal"/> with no offset.  This avoids the world-axis
    /// snap that occurs when composing a separate rotation matrix whose "identity" does
    /// not coincide with the plane-normal orientation.
    /// </summary>
    public (Vector3 r0, Vector3 r1, Vector3 r2) TargetRotFromPlaneNormalWithOffset(
        Vector3 normal, float offsetADeg, float offsetBDeg, float offsetCDeg)
    {
        float b    = MathF.Asin(Math.Clamp(normal.Z, -1f, 1f));
        float cosB = MathF.Cos(b);
        float a    = MathF.Abs(cosB) > 1e-6f ? MathF.Atan2(-normal.Y, -normal.X) : 0f;

        return TargetRotFromKukaAbc(
            a * (180f / MathF.PI) + offsetADeg,
            b * (180f / MathF.PI) + offsetBDeg,
            offsetCDeg);
    }

    /// <summary>
    /// Builds a fully-determined tool orientation from the toolpath geometry:
    /// <code>
    ///   kukaX = -normalize(normal)       — approach direction (tool → workpiece)
    ///   kukaZ = normalize(tangent ⊥ X)  — travel direction (re-orthogonalised)
    ///   kukaY = kukaZ × kukaX           — side vector (right-hand frame)
    /// </code>
    /// This eliminates the undefined spin-around-approach-axis present in
    /// <see cref="TargetRotFromPlaneNormal"/>.
    ///
    /// When <paramref name="offset"/> is provided it is applied as a post-multiply
    /// in the tool's own frame: <c>R_final = R_path × R_offset</c>.  With all-zero
    /// offset the behaviour is a pure path-frame solve.
    /// </summary>
    public (Vector3 r0, Vector3 r1, Vector3 r2) TargetRotFromPathFrame(
        Vector3 normal, Vector3 tangent,
        (Vector3 r0, Vector3 r1, Vector3 r2)? offset = null)
    {
        // ── Step 1: build the path-derived KUKA tool frame ───────────────────
        var kukaX = -Vector3.Normalize(normal);       // approach = -planeNormal

        // Re-orthogonalise tangent against kukaX so kukaZ ⊥ kukaX.
        var t = tangent - Vector3.Dot(tangent, kukaX) * kukaX;
        Vector3 kukaY, kukaZ;
        if (t.LengthSquared > 1e-6f)
        {
            kukaZ = Vector3.Normalize(t);
            kukaY = Vector3.Cross(kukaZ, kukaX);      // already unit, perpendicular pair
        }
        else
        {
            // Tangent degenerate (parallel to normal) — fall back to an arbitrary spin.
            var arb = MathF.Abs(kukaX.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            kukaY = Vector3.Normalize(Vector3.Cross(arb, kukaX));
            kukaZ = Vector3.Cross(kukaX, kukaY);
        }

        // ── Step 2: map to scene-space rows via toolFrameRoll ────────────────
        float cr = MathF.Cos(_toolFrameRoll);
        float sr = MathF.Sin(_toolFrameRoll);
        var r0 = cr * kukaX + sr * kukaY;
        var r1 = kukaZ;
        var r2 = sr * kukaX - cr * kukaY;

        if (offset is null) return (r0, r1, r2);

        // ── Step 3: apply local post-rotation R_final = R_path * R_offset ────
        // Column-vector: R_final = R_path * R_offset
        // Row-vector equivalent: M_final = M_offset * M_path
        var (or0, or1, or2) = offset.Value;
        var Mp = new Matrix3(r0,  r1,  r2);
        var Mo = new Matrix3(or0, or1, or2);
        var Mf = Mo * Mp;
        return (Mf.Row0, Mf.Row1, Mf.Row2);
    }

    // ── IK ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Position-only IK. Solves for a TCP target in ROBROOT frame (mm).
    /// Returns 6 KRL angles or <c>null</c> if convergence fails within 10 mm.
    /// </summary>
    public float[]? Solve(Vector3 targetRobroot, float[] seed)
    {
        const float Eps    = 1.0f;
        const float Lambda = 10f;
        const float Tol    = 1.0f;
        const int   MaxIt  = 200;

        var targetScene = targetRobroot + _robotWorldPos;
        var θ           = (float[])seed.Clone();

        for (int iter = 0; iter < MaxIt; iter++)
        {
            var p0    = ComputeTcpPosScene(θ);
            var error = targetScene - p0;
            if (error.LengthSquared < Tol * Tol) break;

            var J = new Vector3[6];
            for (int j = 0; j < 6; j++)
            {
                θ[j] += Eps;
                J[j]  = (ComputeTcpPosScene(θ) - p0) / Eps;
                θ[j] -= Eps;
            }

            float m00 = Lambda, m01 = 0f, m02 = 0f,
                                m11 = Lambda, m12 = 0f,
                                              m22 = Lambda;
            for (int j = 0; j < 6; j++)
            {
                m00 += J[j].X * J[j].X;
                m01 += J[j].X * J[j].Y;
                m02 += J[j].X * J[j].Z;
                m11 += J[j].Y * J[j].Y;
                m12 += J[j].Y * J[j].Z;
                m22 += J[j].Z * J[j].Z;
            }

            var x = Solve3x3(m00, m01, m02,
                              m01, m11, m12,
                              m02, m12, m22, error);

            for (int j = 0; j < 6; j++)
                θ[j] = Math.Clamp(θ[j] + Vector3.Dot(J[j], x), _jcfg[j].MinDeg, _jcfg[j].MaxDeg);
        }

        return (ComputeTcpPosScene(θ) - targetScene).Length <= 10f ? θ : null;
    }

    /// <summary>
    /// Position + orientation constrained IK. Solves for a TCP target in ROBROOT
    /// frame (mm) while holding the flange orientation fixed.
    /// <paramref name="targetRot"/> is the desired normalized rotation rows in scene
    /// space — obtain it by calling <see cref="ComputeFlangeRotNorm"/> at the seed
    /// angles before solving.
    /// Returns 6 KRL angles or <c>null</c> if position convergence fails within 10 mm.
    /// </summary>
    public float[]? Solve(Vector3 targetRobroot, float[] seed,
                          (Vector3 r0, Vector3 r1, Vector3 r2) targetRot)
    {
        const float Eps    = 1.0f;
        const float Lambda = 10f;
        const float PosTol = 1.0f;    // mm
        const float RotTol = 0.01f;   // rad (~0.57°)
        const int   MaxIt  = 300;
        // Orientation weight: scales radians to mm-equivalent so the 6D error
        // is balanced. Larger = stronger orientation constraint.
        const float OW = 200f;

        var targetScene    = targetRobroot + _robotWorldPos;
        var θ              = (float[])seed.Clone();
        var (tR0, tR1, tR2) = targetRot;

        for (int iter = 0; iter < MaxIt; iter++)
        {
            var p0               = ComputeTcpPosScene(θ);
            var ePos             = targetScene - p0;
            var (cR0, cR1, cR2) = ComputeFlangeRotNorm(θ);
            var eRot             = RotErr(tR0, tR1, tR2, cR0, cR1, cR2);

            if (ePos.LengthSquared < PosTol * PosTol && eRot.LengthSquared < RotTol * RotTol) break;

            // Jacobian: Jp = position (mm/deg), Jr = orientation (OW*rad/deg)
            var Jp = new Vector3[6];
            var Jr = new Vector3[6];
            for (int j = 0; j < 6; j++)
            {
                θ[j] += Eps;
                Jp[j] = (ComputeTcpPosScene(θ) - p0) / Eps;
                var (pR0, pR1, pR2) = ComputeFlangeRotNorm(θ);
                Jr[j] = OW * RotErr(pR0, pR1, pR2, cR0, cR1, cR2) / Eps;
                θ[j] -= Eps;
            }

            // 6D error vector: [pos (mm); orientation (OW*rad)]
            float[] e6 = [ePos.X, ePos.Y, ePos.Z, OW * eRot.X, OW * eRot.Y, OW * eRot.Z];

            // Build 6×7 augmented matrix [M | e] where M = J*J^T + λI
            var aug = new float[6, 7];
            for (int r = 0; r < 6; r++)
            {
                aug[r, r] = Lambda;
                aug[r, 6] = e6[r];
            }
            for (int j = 0; j < 6; j++)
            {
                float[] col = [Jp[j].X, Jp[j].Y, Jp[j].Z, Jr[j].X, Jr[j].Y, Jr[j].Z];
                for (int r = 0; r < 6; r++)
                    for (int c = 0; c < 6; c++)
                        aug[r, c] += col[r] * col[c];
            }

            SolveAugmented(aug, 6);

            // Δθ = J^T * x  (x extracted as aug[r,6] / aug[r,r] after elimination)
            for (int j = 0; j < 6; j++)
            {
                float[] col = [Jp[j].X, Jp[j].Y, Jp[j].Z, Jr[j].X, Jr[j].Y, Jr[j].Z];
                float   dθ  = 0f;
                for (int r = 0; r < 6; r++)
                    dθ += col[r] * (aug[r, 6] / aug[r, r]);
                θ[j] = Math.Clamp(θ[j] + dθ, _jcfg[j].MinDeg, _jcfg[j].MaxDeg);
            }
        }

        return (ComputeTcpPosScene(θ) - targetScene).Length <= 10f ? θ : null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Orientation error as a rotation vector (axis × sin(angle/2) approximation).
    /// Formula: ω = ½ Σ_k (cR_k × tR_k) — drives current frame toward target frame.
    /// </summary>
    private static Vector3 RotErr(
        Vector3 tR0, Vector3 tR1, Vector3 tR2,
        Vector3 cR0, Vector3 cR1, Vector3 cR2)
        => 0.5f * (Vector3.Cross(cR0, tR0) + Vector3.Cross(cR1, tR1) + Vector3.Cross(cR2, tR2));

    /// <summary>
    /// In-place Gauss-Jordan elimination with partial pivoting on an n×(n+1)
    /// augmented matrix. After the call, x[i] = m[i, n] / m[i, i].
    /// </summary>
    private static void SolveAugmented(float[,] m, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int best = col;
            for (int r = col + 1; r < n; r++)
                if (MathF.Abs(m[r, col]) > MathF.Abs(m[best, col])) best = r;

            if (MathF.Abs(m[best, col]) < 1e-12f) continue;

            if (best != col)
                for (int c = 0; c <= n; c++) (m[col, c], m[best, c]) = (m[best, c], m[col, c]);

            float inv = 1f / m[col, col];
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                float f = m[r, col] * inv;
                for (int c = col; c <= n; c++) m[r, c] -= f * m[col, c];
            }
        }
    }

    private static Vector3 Solve3x3(
        float a00, float a01, float a02,
        float a10, float a11, float a12,
        float a20, float a21, float a22,
        Vector3 b)
    {
        float[,] m =
        {
            { a00, a01, a02, b.X },
            { a10, a11, a12, b.Y },
            { a20, a21, a22, b.Z },
        };

        for (int col = 0; col < 3; col++)
        {
            int best = col;
            for (int r = col + 1; r < 3; r++)
                if (MathF.Abs(m[r, col]) > MathF.Abs(m[best, col])) best = r;

            if (MathF.Abs(m[best, col]) < 1e-12f) return Vector3.Zero;

            if (best != col)
                for (int c = 0; c < 4; c++) (m[col, c], m[best, c]) = (m[best, c], m[col, c]);

            float inv = 1f / m[col, col];
            for (int r = 0; r < 3; r++)
            {
                if (r == col) continue;
                float f = m[r, col] * inv;
                for (int c = col; c < 4; c++) m[r, c] -= f * m[col, c];
            }
        }

        return new Vector3(m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2]);
    }
}
