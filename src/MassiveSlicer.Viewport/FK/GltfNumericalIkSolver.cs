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

    // Workspace bounds measured directly from FK, not derived from abstract L1/L2.
    // Sampling A2/A3 over their full joint ranges gives the exact TCP reach envelope
    // for this specific GLTF model and TCP offset — no assumption about which FK
    // iteration corresponds to the wrist center.
    private readonly float _minReachFromShoulder;
    private readonly float _maxReachFromShoulder;
    private readonly float _shoulderZ;       // ROBROOT Z of the A2 (shoulder) pivot
    private readonly float _shoulderHorizR;  // horizontal offset of shoulder from A1 axis

    /// <param name="restPoses">Per-joint rest-pose local transforms, from <see cref="RobotFkController.RestPoses"/>.</param>
    /// <param name="chainRoot">WorldTransform of joint_1's parent, from <see cref="RobotFkController.ChainRootTransform"/>.</param>
    /// <param name="robotWorldPos">ROBROOT origin in scene space (mm).</param>
    /// <param name="tcpLocal">TCP offset as a GLTF-space local transform (pure translation).</param>
    /// <param name="jointConfigs">Per-joint axis, sign, offset, and limits for A1-A6.</param>
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

        // FK iteration 1 (i=0 only) → A2 pivot (shoulder) in scene space.
        var home         = new float[6];
        var shoulderScene = ComputePartialFkPos(home, 1);
        var shoulder      = shoulderScene - robotWorldPos;
        _shoulderZ      = shoulder.Z;
        _shoulderHorizR = MathF.Sqrt(shoulder.X * shoulder.X + shoulder.Y * shoulder.Y);

        // Sample A2 × A3 over full joint ranges to get the real TCP reach envelope.
        // 25×25 = 625 FK evaluations at construction — negligible cost.
        const int Steps = 24;
        float minD = float.MaxValue, maxD = 0f;
        var θ = new float[6];
        for (int i = 0; i <= Steps; i++)
        for (int j = 0; j <= Steps; j++)
        {
            θ[1] = _jcfg[1].MinDeg + (_jcfg[1].MaxDeg - _jcfg[1].MinDeg) * i / Steps;
            θ[2] = _jcfg[2].MinDeg + (_jcfg[2].MaxDeg - _jcfg[2].MinDeg) * j / Steps;
            float d = (ComputeTcpPosScene(θ) - shoulderScene).Length;
            if (d < minD) minD = d;
            if (d > maxD) maxD = d;
        }
        _minReachFromShoulder = minD;
        _maxReachFromShoulder = maxD;
    }

    /// <summary>Returns the scene-space position after applying the first <paramref name="joints"/> FK steps at the given KRL angles.</summary>
    private Vector3 ComputePartialFkPos(float[] krl, int joints)
    {
        var wt = _chainRoot;
        for (int i = 0; i < joints; i++)
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
        return wt.Row3.Xyz;
    }

    /// <summary>
    /// Workspace reachability test using sampled TCP reach from the shoulder pivot.
    ///
    /// The shoulder (A2 axis) sweeps a circle of radius <c>_shoulderHorizR</c> around
    /// the A1 axis.  With A1 free to rotate, the shoulder-to-target 3D distance ranges:
    ///   r_min = √((dxy − shoulderHorizR)² + dz²)   — A1 aligned toward target
    ///   r_max = √((dxy + shoulderHorizR)² + dz²)   — A1 aligned away from target
    ///
    /// A target is reachable iff there exists some A1 angle where the shoulder-to-target
    /// distance falls within the sampled TCP reach band [_minReach, _maxReach]:
    ///   r_min ≤ _maxReachFromShoulder   (arm can extend far enough)
    ///   r_max ≥ _minReachFromShoulder   (arm can fold close enough)
    /// </summary>
    private bool IsWorkspaceReachable(Vector3 targetRobroot)
    {
        float dxy        = MathF.Sqrt(targetRobroot.X * targetRobroot.X + targetRobroot.Y * targetRobroot.Y);
        float dz         = targetRobroot.Z - _shoulderZ;
        float dHorizNear = dxy - _shoulderHorizR;
        float dHorizFar  = dxy + _shoulderHorizR;

        float rMin = MathF.Sqrt(dHorizNear * dHorizNear + dz * dz);
        float rMax = MathF.Sqrt(dHorizFar  * dHorizFar  + dz * dz);

        return rMin <= _maxReachFromShoulder && rMax >= _minReachFromShoulder;
    }

    // -- FK --------------------------------------------------------------------

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

    /// <summary>Returns TCP position and normalized rotation rows in one FK pass.</summary>
    private (Vector3 pos, Vector3 r0, Vector3 r1, Vector3 r2) ComputeTcpAndRot(float[] krl)
    {
        var   wt  = ComputeJoint6Transform(krl);
        var   tcp = (_tcpLocal * wt).Row3.Xyz;
        float sc  = wt.Row0.Xyz.Length;
        return (tcp, wt.Row0.Xyz / sc, wt.Row1.Xyz / sc, wt.Row2.Xyz / sc);
    }

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
        // -> r0 = cr*kukaX + sr*kukaY,  r1 = kukaZ,  r2 = sr*kukaX − cr*kukaY
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
    ///   A = atan2(-normal.Y, -normal.X)   (0 when cos(B) ≈ 0 -- gimbal lock)
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
    /// mapping, <c>(0, 0, 0)</c> is a true identity offset -- identical to calling
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
    ///   kukaX = -normalize(normal)       -- approach direction (tool -> workpiece)
    ///   kukaZ = normalize(tangent ⊥ X)  -- travel direction (re-orthogonalised)
    ///   kukaY = kukaZ × kukaX           -- side vector (right-hand frame)
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
        // -- Step 1: build the path-derived KUKA tool frame -------------------
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
            // Tangent degenerate (parallel to normal) -- fall back to an arbitrary spin.
            var arb = MathF.Abs(kukaX.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            kukaY = Vector3.Normalize(Vector3.Cross(arb, kukaX));
            kukaZ = Vector3.Cross(kukaX, kukaY);
        }

        // -- Step 2: map to scene-space rows via toolFrameRoll ----------------
        float cr = MathF.Cos(_toolFrameRoll);
        float sr = MathF.Sin(_toolFrameRoll);
        var r0 = cr * kukaX + sr * kukaY;
        var r1 = kukaZ;
        var r2 = sr * kukaX - cr * kukaY;

        if (offset is null) return (r0, r1, r2);

        // -- Step 3: apply local post-rotation R_final = R_path * R_offset ----
        // Column-vector: R_final = R_path * R_offset
        // Row-vector equivalent: M_final = M_offset * M_path
        var (or0, or1, or2) = offset.Value;
        var Mp = new Matrix3(r0,  r1,  r2);
        var Mo = new Matrix3(or0, or1, or2);
        var Mf = Mo * Mp;
        return (Mf.Row0, Mf.Row1, Mf.Row2);
    }

    /// <summary>
    /// Convenience overload of <see cref="TargetRotFromPathFrame"/> that applies a KUKA ZYX
    /// Euler offset (degrees) as a post-rotation in the path frame's local space.
    /// Zero offset is a true identity -- identical to calling <see cref="TargetRotFromPathFrame"/>
    /// with no offset parameter.
    /// </summary>
    public (Vector3 r0, Vector3 r1, Vector3 r2) TargetRotFromPathFrameWithOffset(
        Vector3 normal, Vector3 tangent,
        float offsetADeg, float offsetBDeg, float offsetCDeg)
    {
        if (offsetADeg == 0f && offsetBDeg == 0f && offsetCDeg == 0f)
            return TargetRotFromPathFrame(normal, tangent);

        float a = offsetADeg * MathF.PI / 180f;
        float b = offsetBDeg * MathF.PI / 180f;
        float c = offsetCDeg * MathF.PI / 180f;
        float ca = MathF.Cos(a), sa = MathF.Sin(a);
        float cb = MathF.Cos(b), sb = MathF.Sin(b);
        float cc = MathF.Cos(c), sc = MathF.Sin(c);

        // Pure KUKA ZYX rotation matrix (no toolFrameRoll) expressed as row vectors.
        // Row i = column i of R_kuka, so identity for (0,0,0).
        var or0 = new Vector3(ca * cb,                  sa * cb,                  -sb);
        var or1 = new Vector3(ca * sb * sc - sa * cc,   sa * sb * sc + ca * cc,    cb * sc);
        var or2 = new Vector3(ca * sb * cc + sa * sc,   sa * sb * cc - ca * sc,    cb * cc);

        return TargetRotFromPathFrame(normal, tangent, (or0, or1, or2));
    }

    /// <summary>
    /// Builds the IK target rotation matching KrlExporter.KukaAbc:
    ///   1. Base perpendicular frame from normal (Rodrigues of (0,0,-1)→-normal).
    ///   2. Local KUKA ZYX offset (offA=A=Rz, offB=B=Ry, offC=C=Rx) applied in that frame.
    ///   3. Mapped to scene-space IK rows via toolFrameRoll.
    /// Zero offset → nozzle perpendicular to surface. Non-zero → physically tilts/rolls.
    /// </summary>
    public (Vector3 r0, Vector3 r1, Vector3 r2) TargetRotFromGlobalOrientation(
        Vector3 normal, float offADeg, float offBDeg, float offCDeg)
    {
        normal = normal.Normalized();

        // Step 1: base perpendicular frame via Rodrigues (0,0,-1) → xBase = -normal
        var xDef  = new Vector3(0f, 0f, -1f);
        var xBase = -normal;
        float cosT = Math.Clamp(Vector3.Dot(xDef, xBase), -1f, 1f);
        Vector3 xB, yB, zB;
        if (MathF.Abs(cosT - 1f) < 1e-6f)
        {
            xB = xDef; yB = new Vector3(0f, 1f, 0f); zB = new Vector3(1f, 0f, 0f);
        }
        else if (MathF.Abs(cosT + 1f) < 1e-6f)
        {
            xB = -xDef; yB = new Vector3(0f, 1f, 0f); zB = new Vector3(-1f, 0f, 0f);
        }
        else
        {
            var   axis = Vector3.Cross(xDef, xBase).Normalized();
            float sinT = MathF.Sqrt(1f - cosT * cosT);
            xB = xBase;
            yB = Rod(new Vector3(0f, 1f, 0f), axis, sinT, cosT);
            zB = Rod(new Vector3(1f, 0f, 0f), axis, sinT, cosT);
        }

        // Step 2: local KUKA ZYX offset in the base frame
        float ca = MathF.Cos(offADeg * MathF.PI / 180f), sa = MathF.Sin(offADeg * MathF.PI / 180f);
        float cb = MathF.Cos(offBDeg * MathF.PI / 180f), sb = MathF.Sin(offBDeg * MathF.PI / 180f);
        float cc = MathF.Cos(offCDeg * MathF.PI / 180f), sc = MathF.Sin(offCDeg * MathF.PI / 180f);

        var xF = xB * (ca * cb)                + yB * (sa * cb)                + zB * (-sb);
        var yF = xB * (ca * sb * sc - sa * cc)  + yB * (sa * sb * sc + ca * cc)  + zB * (cb * sc);
        var zF = xB * (ca * sb * cc + sa * sc)  + yB * (sa * sb * cc - ca * sc)  + zB * (cb * cc);

        // Step 3: map to scene-space IK rows via toolFrameRoll
        float cr = MathF.Cos(_toolFrameRoll), sr = MathF.Sin(_toolFrameRoll);
        return (cr * xF + sr * yF, zF, sr * xF - cr * yF);
    }

    private static Vector3 Rod(Vector3 v, Vector3 axis, float sinT, float cosT)
        => v * cosT + Vector3.Cross(axis, v) * sinT + axis * Vector3.Dot(axis, v) * (1f - cosT);

    // -- IK --------------------------------------------------------------------

    /// <summary>
    /// Position-only IK. Solves for a TCP target in ROBROOT frame (mm).
    /// Returns 6 KRL angles or <c>null</c> if convergence fails within 10 mm.
    ///
    /// Two early-exit conditions keep this fast:
    /// • Convergence : exits as soon as error &lt; 1 mm (~10–20 iterations for reachable points).
    /// • Stagnation  : exits after 5 consecutive iterations that improve by &lt; 0.1 mm absolute.
    ///   Using an absolute threshold (not %) is important: close-to-base targets require the arm
    ///   to fold slowly — each iteration may improve by only 0.2–0.5 mm, which looks stagnant
    ///   under a relative threshold but is real progress. Truly unreachable targets plateau at
    ///   ~0 mm/iter regardless of distance, so the absolute threshold catches them immediately.
    /// <paramref name="maxIterations"/> is a safety cap; stagnation detection typically exits
    /// well before it. 40 is sufficient for reachability checks.
    /// </summary>
    public float[]? Solve(Vector3 targetRobroot, float[] seed, int maxIterations = 200, float finalTolerance = 10f)
    {
        // Analytical workspace test: rejects targets that are geometrically unreachable
        // (both too far AND too close) before spending iterations on the DLS loop.
        if (!IsWorkspaceReachable(targetRobroot)) return null;

        const float Eps            = 1.0f;
        const float Lambda         = 10f;
        const float Tol            = 1.0f;
        const float StagnantMinMm  = 0.1f; // must improve by >0.1 mm absolute per iter
        const int   StagnantMax    = 5;    // consecutive stagnant iterations before giving up

        var targetScene = targetRobroot + _robotWorldPos;
        var θ           = (float[])seed.Clone();
        float bestErr   = float.MaxValue;
        int   stagnant  = 0;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            var   p0  = ComputeTcpPosScene(θ);
            float err = (targetScene - p0).Length;

            if (err < Tol) break;

            // Stagnation: track best-ever error; reset counter only on meaningful improvement.
            float improve = bestErr - err;
            if (err < bestErr) bestErr = err;
            if (improve >= StagnantMinMm)
                stagnant = 0;
            else if (++stagnant >= StagnantMax)
                break;

            var error = targetScene - p0;
            var J     = new Vector3[6];
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

        return (ComputeTcpPosScene(θ) - targetScene).Length <= finalTolerance ? θ : null;
    }

    /// <summary>
    /// Position + orientation constrained IK. Solves for a TCP target in ROBROOT
    /// frame (mm) while holding the flange orientation fixed.
    /// <paramref name="targetRot"/> is the desired normalized rotation rows in scene
    /// space -- obtain it by calling <see cref="ComputeFlangeRotNorm"/> at the seed
    /// angles before solving.
    /// Returns 6 KRL angles or <c>null</c> if position convergence fails within 10 mm.
    /// </summary>
    public float[]? Solve(Vector3 targetRobroot, float[] seed,
                          (Vector3 r0, Vector3 r1, Vector3 r2) targetRot,
                          int maxIterations = 300)
    {
        if (!IsWorkspaceReachable(targetRobroot)) return null;

        const float Eps           = 1.0f;
        const float Lambda        = 10f;
        const float PosTol        = 1.0f;   // mm
        const float RotTol        = 0.01f;  // rad (~0.57 deg)
        const float StagnantMinMm = 0.1f;
        const int   StagnantMax   = 5;
        const float OW            = 200f;

        var targetScene     = targetRobroot + _robotWorldPos;
        var θ               = (float[])seed.Clone();
        var (tR0, tR1, tR2) = targetRot;
        float bestErr       = float.MaxValue;
        int   stagnant      = 0;

        // All working storage is stack-allocated once outside the iteration loop so
        // repeated Solve calls from Parallel validation never pressure the GC.
        // aug: 6×7 augmented matrix stored row-major (stride 7).
        // cv:  shared column buffer reused across j-loops.
        // Jp/Jr: Jacobian columns (position mm/deg, orientation OW*rad/deg).
        Span<float>   aug = stackalloc float[42]; // 6 rows × 7 cols
        Span<float>   cv  = stackalloc float[6];
        Span<Vector3> Jp  = stackalloc Vector3[6];
        Span<Vector3> Jr  = stackalloc Vector3[6];

        for (int iter = 0; iter < maxIterations; iter++)
        {
            var (p0, cR0, cR1, cR2) = ComputeTcpAndRot(θ);
            var ePos                 = targetScene - p0;
            var eRot                 = RotErr(tR0, tR1, tR2, cR0, cR1, cR2);

            if (ePos.LengthSquared < PosTol * PosTol && eRot.LengthSquared < RotTol * RotTol) break;

            float err     = ePos.Length + OW * eRot.Length;
            float improve = bestErr - err;
            if (err < bestErr) bestErr = err;
            if (improve >= StagnantMinMm)
                stagnant = 0;
            else if (++stagnant >= StagnantMax)
                break;

            for (int j = 0; j < 6; j++)
            {
                θ[j] += Eps;
                var (pp, pR0, pR1, pR2) = ComputeTcpAndRot(θ);
                Jp[j] = (pp - p0) / Eps;
                Jr[j] = OW * RotErr(pR0, pR1, pR2, cR0, cR1, cR2) / Eps;
                θ[j] -= Eps;
            }

            // Initialise augmented matrix: diagonal = λI, last column = 6D error.
            aug.Clear();
            for (int r = 0; r < 6; r++) aug[r * 7 + r] = Lambda;
            aug[ 6] = ePos.X;         aug[13] = ePos.Y;         aug[20] = ePos.Z;
            aug[27] = OW * eRot.X;   aug[34] = OW * eRot.Y;   aug[41] = OW * eRot.Z;

            // Accumulate J·Jᵀ into aug (symmetric outer-product update).
            for (int j = 0; j < 6; j++)
            {
                cv[0] = Jp[j].X; cv[1] = Jp[j].Y; cv[2] = Jp[j].Z;
                cv[3] = Jr[j].X; cv[4] = Jr[j].Y; cv[5] = Jr[j].Z;
                for (int r = 0; r < 6; r++)
                {
                    float cr = cv[r];
                    int   ri = r * 7;
                    for (int c = 0; c < 6; c++)
                        aug[ri + c] += cr * cv[c];
                }
            }

            SolveAugmented6(aug);

            // Δθⱼ = Jⱼᵀ · x  where x[r] = aug[r*7+6] / aug[r*7+r] after elimination.
            for (int j = 0; j < 6; j++)
            {
                cv[0] = Jp[j].X; cv[1] = Jp[j].Y; cv[2] = Jp[j].Z;
                cv[3] = Jr[j].X; cv[4] = Jr[j].Y; cv[5] = Jr[j].Z;
                float dθ = 0f;
                for (int r = 0; r < 6; r++)
                    dθ += cv[r] * (aug[r * 7 + 6] / aug[r * 7 + r]);
                θ[j] = Math.Clamp(θ[j] + dθ, _jcfg[j].MinDeg, _jcfg[j].MaxDeg);
            }
        }

        return (ComputeTcpPosScene(θ) - targetScene).Length <= 10f ? θ : null;
    }

    // -- Helpers ---------------------------------------------------------------

    /// <summary>
    /// Orientation error as a rotation vector (axis × sin(angle/2) approximation).
    /// Formula: ω = ½ Σ_k (cR_k × tR_k) -- drives current frame toward target frame.
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

    /// <summary>
    /// In-place Gauss-Jordan elimination on a 6×7 augmented matrix stored row-major
    /// in <paramref name="m"/> with stride 7. After the call, x[i] = m[i*7+6] / m[i*7+i].
    /// </summary>
    private static void SolveAugmented6(Span<float> m)
    {
        const int N = 6, Stride = 7;
        for (int col = 0; col < N; col++)
        {
            int best = col;
            for (int r = col + 1; r < N; r++)
                if (MathF.Abs(m[r * Stride + col]) > MathF.Abs(m[best * Stride + col])) best = r;

            if (MathF.Abs(m[best * Stride + col]) < 1e-12f) continue;

            if (best != col)
                for (int c = 0; c <= N; c++)
                    (m[col * Stride + c], m[best * Stride + c]) = (m[best * Stride + c], m[col * Stride + c]);

            float inv = 1f / m[col * Stride + col];
            for (int r = 0; r < N; r++)
            {
                if (r == col) continue;
                float f = m[r * Stride + col] * inv;
                for (int c = col; c <= N; c++)
                    m[r * Stride + c] -= f * m[col * Stride + c];
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
