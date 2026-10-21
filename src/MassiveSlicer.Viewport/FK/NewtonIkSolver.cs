using MassiveSlicer.Core.Kinematics;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.FK;

/// <summary>
/// Levenberg-Marquardt on the same glTF bone chain the viewport draws.
/// The Jacobian is analytic (revolute axis × lever arm). A miss stays a miss:
/// nothing here calls <see cref="GltfNumericalIkSolver.Solve"/>.
/// </summary>
public sealed class NewtonIkSolver : GltfNumericalIkSolver
{
    const float LoopPosMm = 1f;
    const float LoopRotRad = 0.01f;
    const float OrientWeight = 200f;
    const float MaxStepDeg = 8f;

    public NewtonIkSolver(IReadOnlyList<Matrix4> restPoses, Matrix4 chainRoot,
                          Vector3 robotWorldPos, Matrix4 tcpLocal,
                          IReadOnlyList<JointConfig> jointConfigs,
                          float toolFrameRoll = 0f,
                          float tcpADeg = 0f, float tcpBDeg = 0f, float tcpCDeg = 0f)
        : base(restPoses, chainRoot, robotWorldPos, tcpLocal, jointConfigs,
               toolFrameRoll, tcpADeg, tcpBDeg, tcpCDeg)
    {
    }

    public override float[]? Solve(Vector3 targetRobroot, float[] seed, int maxIterations = 200, float finalTolerance = 10f)
    {
        if (!IsWorkspaceReachable(targetRobroot)) return null;

        var targetScene = targetRobroot + _robotWorldPos;
        var θ = ClampSeed(seed);
        var best = (float[])θ.Clone();
        float bestPos = float.MaxValue;
        float lambda = 1e-2f;
        int rejects = 0;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            var p = ComputeTcpPosScene(θ);
            var ePos = targetScene - p;
            float posErr = ePos.Length;
            if (posErr < bestPos)
            {
                bestPos = posErr;
                best = (float[])θ.Clone();
            }
            if (posErr < LoopPosMm) break;

            if (!TryStep(θ, ePos, rotErr: null, lambda, out var trial))
            {
                lambda = MathF.Min(lambda * 8f, 1e6f);
                if (++rejects >= 8) break;
                continue;
            }

            float trialErr = (targetScene - ComputeTcpPosScene(trial)).Length;
            if (trialErr < posErr)
            {
                θ = trial;
                lambda = MathF.Max(lambda * 0.3f, 1e-8f);
                rejects = 0;
            }
            else
            {
                lambda = MathF.Min(lambda * 8f, 1e6f);
                if (++rejects >= 8) break;
            }
        }

        return bestPos <= finalTolerance ? best : null;
    }

    public override float[]? Solve(Vector3 targetRobroot, float[] seed,
                                   (Vector3 r0, Vector3 r1, Vector3 r2) targetRot,
                                   int maxIterations = 300,
                                   bool requireOrientation = false)
    {
        if (!IsWorkspaceReachable(targetRobroot)) return null;

        var targetScene = targetRobroot + _robotWorldPos;
        var θ = ClampSeed(seed);
        var best = (float[])θ.Clone();
        float bestCost = float.MaxValue;
        float bestPos = float.MaxValue;
        float bestOri = float.MaxValue;
        float lambda = 1e-2f;
        int rejects = 0;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            var (p, c0, c1, c2) = ComputeTcpAndRot(θ);
            var ePos = targetScene - p;
            var eRot = RotErr(targetRot.r0, targetRot.r1, targetRot.r2, c0, c1, c2);
            float posErr = ePos.Length;
            float rotErr = eRot.Length;
            float cost = posErr + OrientWeight * rotErr;
            if (cost < bestCost)
            {
                bestCost = cost;
                bestPos = posErr;
                bestOri = rotErr;
                best = (float[])θ.Clone();
            }
            if (posErr < LoopPosMm && rotErr < LoopRotRad) break;

            if (!TryStep(θ, ePos, eRot, lambda, out var trial))
            {
                lambda = MathF.Min(lambda * 8f, 1e6f);
                if (++rejects >= 8) break;
                continue;
            }

            var (tp, t0, t1, t2) = ComputeTcpAndRot(trial);
            float trialPos = (targetScene - tp).Length;
            float trialRot = RotErr(targetRot.r0, targetRot.r1, targetRot.r2, t0, t1, t2).Length;
            float trialCost = trialPos + OrientWeight * trialRot;
            if (trialCost < cost)
            {
                θ = trial;
                lambda = MathF.Max(lambda * 0.3f, 1e-8f);
                rejects = 0;
            }
            else
            {
                lambda = MathF.Min(lambda * 8f, 1e6f);
                if (++rejects >= 8) break;
            }
        }

        if (bestPos > 10f) return null;
        if (requireOrientation && bestOri > PrintOrientErrMax) return null;
        return best;
    }

    /// <summary>TCP position columns, mm per KRL degree. Tests compare this to a finite difference.</summary>
    internal Vector3[] AnalyticTcpJacobianMmPerDeg(float[] krl)
    {
        var (pivots, axes) = JointPivotsAndAxes(krl);
        var p = ComputeTcpPosScene(krl);
        var J = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            float dBonePerDeg = _jcfg[i].KrlSign * (MathF.PI / 180f);
            J[i] = Vector3.Cross(axes[i], p - pivots[i]) * dBonePerDeg;
        }
        return J;
    }

    bool TryStep(float[] θ, Vector3 ePos, Vector3? rotErr, float lambda, out float[] trial)
    {
        trial = θ;
        var (pivots, axes) = JointPivotsAndAxes(θ);
        var p = ComputeTcpPosScene(θ);

        // Column-major task vectors: joint i lives at i*6. Orientation rows are zero
        // for a position-only step. (JᵀJ)ᵢⱼ = colᵢ · colⱼ, (Jᵀe)ᵢ = colᵢ · e.
        Span<double> cols = stackalloc double[36];
        cols.Clear();
        Span<double> e = stackalloc double[6];
        e.Clear();
        e[0] = ePos.X; e[1] = ePos.Y; e[2] = ePos.Z;
        if (rotErr is { } er)
        {
            e[3] = er.X * OrientWeight;
            e[4] = er.Y * OrientWeight;
            e[5] = er.Z * OrientWeight;
        }

        for (int i = 0; i < 6; i++)
        {
            float dBonePerDeg = _jcfg[i].KrlSign * (MathF.PI / 180f);
            var lin = Vector3.Cross(axes[i], p - pivots[i]) * dBonePerDeg;
            cols[i * 6 + 0] = lin.X;
            cols[i * 6 + 1] = lin.Y;
            cols[i * 6 + 2] = lin.Z;
            if (rotErr is not null)
            {
                var ang = axes[i] * (dBonePerDeg * OrientWeight);
                cols[i * 6 + 3] = ang.X;
                cols[i * 6 + 4] = ang.Y;
                cols[i * 6 + 5] = ang.Z;
            }
        }

        Span<double> JtJ = stackalloc double[36];
        Span<double> Jte = stackalloc double[6];
        for (int i = 0; i < 6; i++)
        {
            double je = 0;
            for (int k = 0; k < 6; k++)
                je += cols[i * 6 + k] * e[k];
            Jte[i] = je;
            for (int j = i; j < 6; j++)
            {
                double dot = 0;
                for (int k = 0; k < 6; k++)
                    dot += cols[i * 6 + k] * cols[j * 6 + k];
                JtJ[i * 6 + j] = dot;
                JtJ[j * 6 + i] = dot;
            }
        }

        for (int i = 0; i < 6; i++)
        {
            double d = JtJ[i * 6 + i];
            if (d < 1e-8) d = 1e-8;
            JtJ[i * 6 + i] = JtJ[i * 6 + i] + lambda * d;
        }

        Span<double> A = stackalloc double[36];
        JtJ.CopyTo(A);
        Span<double> b = stackalloc double[6];
        Jte.CopyTo(b);
        Span<double> x = stackalloc double[6];
        if (!SolveSymmetric6(A, b, x)) return false;

        float maxAbs = 0f;
        for (int i = 0; i < 6; i++)
            maxAbs = MathF.Max(maxAbs, MathF.Abs((float)x[i]));
        float scale = maxAbs > MaxStepDeg ? MaxStepDeg / maxAbs : 1f;

        var next = new float[6];
        for (int i = 0; i < 6; i++)
        {
            float dθ = (float)x[i] * scale;
            if (float.IsNaN(dθ) || float.IsInfinity(dθ)) return false;
            next[i] = Math.Clamp(θ[i] + dθ, _jcfg[i].UsableMinDeg, _jcfg[i].UsableMaxDeg);
        }
        trial = next;
        return true;
    }

    (Vector3[] pivots, Vector3[] axes) JointPivotsAndAxes(float[] krl)
    {
        var pivots = new Vector3[6];
        var axes = new Vector3[6];
        var wt = _chainRoot;
        for (int i = 0; i < 6; i++)
        {
            var cfg = _jcfg[i];
            var mount = _restPose[i] * wt;
            pivots[i] = mount.Row3.Xyz;
            Vector3 axis = cfg.Axis switch
            {
                RotationAxis.X => mount.Row0.Xyz,
                RotationAxis.Z => mount.Row2.Xyz,
                _               => mount.Row1.Xyz,
            };
            float len = axis.Length;
            axes[i] = len > 1e-8f ? axis / len : Vector3.UnitY;

            float bone = cfg.KrlSign * krl[i] * MathF.PI / 180f;
            var rot = cfg.Axis switch
            {
                RotationAxis.X => Matrix4.CreateRotationX(bone),
                RotationAxis.Z => Matrix4.CreateRotationZ(bone),
                _               => Matrix4.CreateRotationY(bone),
            };
            wt = rot * mount;
        }
        return (pivots, axes);
    }

    float[] ClampSeed(float[] seed)
    {
        var θ = new float[6];
        for (int i = 0; i < 6; i++)
            θ[i] = Math.Clamp(seed[i], _jcfg[i].UsableMinDeg, _jcfg[i].UsableMaxDeg);
        return θ;
    }

    static bool SolveSymmetric6(Span<double> A, Span<double> b, Span<double> x)
    {
        const int N = 6;
        for (int col = 0; col < N; col++)
        {
            int piv = col;
            double best = Math.Abs(A[col * N + col]);
            for (int r = col + 1; r < N; r++)
            {
                double v = Math.Abs(A[r * N + col]);
                if (v > best) { best = v; piv = r; }
            }
            if (best < 1e-14) return false;
            if (piv != col)
            {
                for (int c = col; c < N; c++)
                    (A[col * N + c], A[piv * N + c]) = (A[piv * N + c], A[col * N + c]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }
            double inv = 1.0 / A[col * N + col];
            for (int r = col + 1; r < N; r++)
            {
                double f = A[r * N + col] * inv;
                for (int c = col + 1; c < N; c++)
                    A[r * N + c] -= f * A[col * N + c];
                b[r] -= f * b[col];
            }
        }
        for (int r = N - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int c = r + 1; c < N; c++)
                s -= A[r * N + c] * x[c];
            double diag = A[r * N + r];
            if (Math.Abs(diag) < 1e-14) return false;
            x[r] = s / diag;
        }
        return true;
    }
}
