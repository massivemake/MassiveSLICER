using MassiveSlicer.Core.Collision;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.FK;
using NMatrix = System.Numerics.Matrix4x4;
using NVec3 = System.Numerics.Vector3;
using TkMatrix4 = OpenTK.Mathematics.Matrix4;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace MassiveSlicer.Viewport.Validation;

/// <summary>
/// Robot feasibility pass over a toolpath: per-move IK reachability, wrist-singularity
/// detection with TCP-yaw auto-repair, trapezoidal move timing, and the digital-twin
/// collision sweep (environment + self + deposited material).
/// </summary>
/// <remarks>
/// Extracted from the viewport's live validation task so the same verdict can be produced
/// for throwaway candidate geometry (Auto Orient) without touching the scene, the outliner
/// or any view state. Everything here is pure with respect to the caller's UI: the only
/// mutation is baking the repaired <see cref="ToolpathMove.TcpYawDeg"/> back onto the moves
/// (so KRL export writes the rotated orientations) and the transient
/// <see cref="Collision.CollisionWorld.Beads"/> grid, which is cleared before returning.
///
/// E1 (rail) planning is NOT done here — every move's <see cref="ToolpathMove.E1Mm"/> is
/// expected to be baked already by the caller (NaN = rail parked at home).
/// </remarks>
public static class ToolpathFeasibilityEvaluator
{
    /// <summary>
    /// Immutable snapshot of everything the sweep needs. Captured on the UI thread by the
    /// caller so the evaluation itself can run entirely on a background thread.
    /// </summary>
    /// <param name="Solver">IK solver, already pointed at the live scene kinematics.</param>
    /// <param name="Toolpath">Toolpath to validate. Moves must already carry baked E1.</param>
    /// <param name="Cache">Flat scrub cache (entry 0 = first From, then each To).</param>
    /// <param name="WorldTransform">Node world transform for toolpath → world mapping.</param>
    /// <param name="Origin">Toolpath origin subtracted before the world transform.</param>
    /// <param name="SeedKrl">Six-axis KRL seed for the path-follow walk — named home,
    /// not the live scrub pose. Parallel windows start from sequential keypoints along
    /// the path so a folded-up sim pose cannot paint the bed unreachable.</param>
    /// <param name="E1Motion">Whether rail motion is planned (targets follow the carriage).</param>
    /// <param name="Rail">Rail geometry; required when <paramref name="E1Motion"/> is set.</param>
    /// <param name="HomeWorld">Robot home (ROBROOT) world position used for rail math.</param>
    /// <param name="HomeE1">Rail position when a move carries no planned E1.</param>
    /// <param name="World">Collision world, or null to skip the collision sweep.</param>
    /// <param name="Robroot">Live ROBROOT world position (rail parked at home).</param>
    /// <param name="Joints">Cell's per-axis joint limits, or null to skip envelope filtering
    /// (a solution the raw IK solver returns can still be outside the physical joint range —
    /// this is the check that catches that instead of treating any non-null solve as reachable).</param>
    public sealed record Input(
        GltfNumericalIkSolver Solver,
        Toolpath Toolpath,
        (NVec3 pos, NVec3 normal)[] Cache,
        TkMatrix4 WorldTransform,
        NVec3 Origin,
        float OffsetADeg, float OffsetBDeg, float OffsetCDeg,
        float[] SeedKrl,
        bool E1Motion,
        RobotRailCellConfig? Rail,
        NVec3 HomeWorld,
        float HomeE1,
        float PrintMmS, float TravelMmS, float WipeMmS, float ApoCvelFrac,
        CollisionWorld? World,
        NMatrix ChainRootColl,
        NMatrix WorldTransformColl,
        NVec3 OriginColl,
        float BeadWidthColl,
        TkVector3 Robroot,
        IReadOnlyList<JointConfig>? Joints = null);

    /// <summary>
    /// Sequential keypoint stride. Adjacent print moves are 1–6 mm; a 256-move jump is
    /// still close enough for position-first DLS, and 1.8M-move paths stay parallel.
    /// </summary>
    public const int IkWindowStride = 256;

    /// <summary>
    /// Walks from <paramref name="homeSeed"/> along the path, solving only window starts.
    /// Each window then parallel-fills from its own key instead of every chunk jumping
    /// from the same pose (the scrub-at-end false-unreachable case).
    /// </summary>
    public static float[][] BuildWindowSeeds(
        int total,
        float[] homeSeed,
        Func<int, float[], float[]?> solveAt,
        int stride = IkWindowStride,
        CancellationToken ct = default)
    {
        if (total <= 0) return [];
        if (stride < 1) stride = 1;
        int n = (total + stride - 1) / stride;
        var keys = new float[n][];
        var seed = (float[])homeSeed.Clone();
        for (int k = 0; k < n; k++)
        {
            ct.ThrowIfCancellationRequested();
            var sol = solveAt(k * stride, seed);
            if (sol is not null) seed = sol;
            keys[k] = (float[])seed.Clone();
        }
        return keys;
    }

    /// <summary>
    /// Stretched print seed with A1 facing ROBROOT XY. Named home (folded Heated
    /// Bed Home, etc.) is too far for DLS to reach layer-1 beads; this is the retry.
    /// </summary>
    public static float[] PrintIkFallbackSeed(float robrootX, float robrootY)
    {
        float a1 = MathF.Atan2(robrootY, robrootX) * (180f / MathF.PI);
        return [a1, -90f, 90f, 0f, 0f, 15f];
    }

    /// <summary>Aimed A1, opposite shoulder, then A5 ±90 (nozzle-down wrists).</summary>
    public static float[][] PrintIkFallbackSeeds(float robrootX, float robrootY)
    {
        var aimed = PrintIkFallbackSeed(robrootX, robrootY);
        float flip = aimed[0] + 180f;
        if (flip > 180f) flip -= 360f;
        else if (flip < -180f) flip += 360f;
        var other = (float[])aimed.Clone();
        other[0] = flip;
        var a5p = (float[])aimed.Clone();
        a5p[4] = 90f;
        var a5n = (float[])aimed.Clone();
        a5n[4] = -90f;
        return [aimed, other, a5p, a5n];
    }

    /// <summary>
    /// Cached scrub joints were solved at a previous Toolhead Y/X/Z.
    /// Replaying them makes the sliders look dead until the operator scrubs.
    /// </summary>
    public static bool UseCachedScrubJoints(bool hasCache, bool toolheadOrientationDirty)
        => hasCache && !toolheadOrientationDirty;

    /// <summary>
    /// Mill may keep a position-only hit. Print may not — that is the sideways
    /// HV on a planar path when Heated Bed Home is already on the bead.
    /// </summary>
    public static float[]? PreferOrientedPrintSolution(
        float[]? oriented, float[]? positionOnly, float orientErr, bool millPath)
    {
        if (millPath)
            return oriented ?? positionOnly;
        if (oriented is not null && orientErr <= GltfNumericalIkSolver.PrintOrientErrMax)
            return oriented;
        return null;
    }

    /// <summary>Try <paramref name="seed"/> first, then each fallback. Null only if all fail.</summary>
    public static float[]? SolveWithPrintFallback(
        float[] seed,
        Func<float[], float[]?> solve,
        IEnumerable<float[]> extras)
    {
        var first = solve(seed);
        if (first is not null) return first;
        foreach (var extra in extras)
        {
            var sol = solve(extra);
            if (sol is not null) return sol;
        }
        return null;
    }

    /// <summary>Per-move verdicts, all arrays indexed by flat move index.</summary>
    /// <param name="Reachable">False where IK failed to converge.</param>
    /// <param name="Solutions">Six-axis solutions, gap-filled and ±360°-unwrapped.</param>
    /// <param name="Singularity">True where |A5| &lt; 5° after repair.</param>
    /// <param name="Collision">Null when the sweep was skipped or failed.</param>
    /// <param name="CollisionStride">Sampling stride the collision sweep actually used.</param>
    public sealed record Result(
        bool[] Reachable,
        float[][] Solutions,
        bool[] Singularity,
        float[] MoveTimesMs,
        float[] PeakVelocities,
        float[] E1PerMove,
        bool[]? Collision,
        int CollisionStride,
        CollisionHit? FirstCollisionHit,
        int UnrepairableLimits = 0,
        bool[]? AxisLimit = null);

    /// <summary>
    /// Runs the full feasibility pass. Returns null when the toolpath is empty or the
    /// work was cancelled — callers should treat null as "no verdict", not as "feasible".
    /// </summary>
    public static Result? Evaluate(Input input, CancellationToken ct)
    {
        var solver   = input.Solver;
        var toolpath = input.Toolpath;
        var cache    = input.Cache;
        var wt       = input.WorldTransform;
        var origin   = input.Origin;
        float offA   = input.OffsetADeg;
        float offB   = input.OffsetBDeg;
        float offC   = input.OffsetCDeg;
        var seed     = input.SeedKrl;
        bool e1Motion = input.E1Motion;
        float homeE1 = input.HomeE1;
        var homeWorld = input.HomeWorld;
        var robroot  = input.Robroot;
        var cellJoints = input.Joints;
        bool millPath = ToolpathHasMillMoves(toolpath);

        int total = 0;
        foreach (var layer in toolpath.Layers) total += layer.Moves.Count;
        if (total == 0 || cache.Length == 0) return null;

        var e1PerMove = new float[total];
        var targets   = new TkVector3[total];
        var normals   = new TkVector3[total];
        int mi        = 0;
        var lastNormN = NVec3.UnitZ; // last valid extrude normal; held through transitions
        var railCfg   = input.Rail;
        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                var (pos, _) = cache[Math.Min(mi + 1, cache.Length - 1)];
                float lx = pos.X - origin.X, ly = pos.Y - origin.Y, lz = pos.Z - origin.Z;
                var world = new TkVector3(
                    lx * wt.M11 + ly * wt.M21 + lz * wt.M31 + wt.M41,
                    lx * wt.M12 + ly * wt.M22 + lz * wt.M32 + wt.M42,
                    lx * wt.M13 + ly * wt.M23 + lz * wt.M33 + wt.M43);

                float e1 = !float.IsNaN(move.E1Mm) ? move.E1Mm : homeE1;
                e1PerMove[mi] = e1;

                // Target in ROBROOT of the carriage at planned E1 (pure translation rail).
                if (e1Motion && railCfg is { } rail)
                {
                    var baseW = RailE1Planner.BaseWorld(homeWorld, rail, e1);
                    targets[mi] = new TkVector3(
                        world.X - baseW.X, world.Y - baseW.Y, world.Z - baseW.Z);
                }
                else
                    targets[mi] = world - robroot;

                // Travel and layer-stitch moves carry no orientation — hold the last
                // extrude normal to prevent a sudden IK jump at layer transitions.
                // Per-move normal (overhang orientation) takes priority; falls back to UnitZ.
                NVec3 effNorm;
                if (move.Kind == MoveKind.Travel || move.IsLayerStitch)
                    effNorm = lastNormN;
                else
                {
                    effNorm   = move.Normal.LengthSquared() > 1e-6f ? move.Normal : NVec3.UnitZ;
                    lastNormN = effNorm;
                }
                float nx = effNorm.X, ny = effNorm.Y, nz = effNorm.Z;
                normals[mi] = TkVector3.Normalize(new TkVector3(
                    nx * wt.M11 + ny * wt.M21 + nz * wt.M31,
                    nx * wt.M12 + ny * wt.M22 + nz * wt.M32,
                    nx * wt.M13 + ny * wt.M23 + nz * wt.M33));
                mi++;
            }
        }

        if (ct.IsCancellationRequested) return null;

        var targetRots = new (TkVector3 r0, TkVector3 r1, TkVector3 r2)[total];
        for (int i = 0; i < total; i++)
        {
            targetRots[i] = millPath
                ? solver.TargetRotFromMillNormal(normals[i])
                : solver.TargetRotFromGlobalOrientation(normals[i], offA, offB, offC);
        }

        if (ct.IsCancellationRequested) return null;

        // Path-follow IK: sequential keypoints from named home, then parallel fill.
        // Each window seeds from its predecessor on the path so a folded-up scrub pose
        // cannot make layer 1 look unreachable. Position-first DLS, then 6D refine —
        // 6D from a far seed stalls because orientation weight dominates.
        var result      = new bool[total];
        var ikSolutions = new float[]?[total]; // null = unreachable

        float[]? SolveReach(TkVector3 target, float[] walkSeed,
            (TkVector3 r0, TkVector3 r1, TkVector3 r2) rot, int maxIterations)
        {
            var pos = solver.Solve(target, walkSeed, maxIterations: maxIterations);
            if (pos is null || (cellJoints is not null && !JointLimitEnvelope.JointsInside(pos, cellJoints)))
                return null;
            var sol = solver.Solve(target, pos, rot, maxIterations: maxIterations,
                requireOrientation: !millPath);
            if (sol is not null && (cellJoints is not null && !JointLimitEnvelope.JointsInside(sol, cellJoints)))
                sol = null;
            float orientErr = sol is not null ? solver.OrientationError(sol, rot) : float.MaxValue;
            return PreferOrientedPrintSolution(sol, pos, orientErr, millPath);
        }

        try
        {
            var windowSeeds = BuildWindowSeeds(
                total, seed,
                (i, s) => millPath
                    ? SolveReach(targets[i], s, targetRots[i], 80)
                    : SolveWithPrintFallback(
                        s,
                        w => SolveReach(targets[i], w, targetRots[i], 80),
                        PrintIkFallbackSeeds(targets[i].X, targets[i].Y)),
                IkWindowStride, ct);
            int stride = IkWindowStride;

            Parallel.For(0, windowSeeds.Length,
                new ParallelOptions { CancellationToken = ct },
                wi =>
                {
                    int start     = wi * stride;
                    int end       = Math.Min(start + stride, total);
                    var chunkSeed = windowSeeds[wi];

                    for (int i = start; i < end; i++)
                    {
                        if (ct.IsCancellationRequested) return;
                        var sol = SolveReach(targets[i], chunkSeed, targetRots[i], 40);
                        result[i]      = sol is not null;
                        ikSolutions[i] = sol;
                        if (sol is not null) chunkSeed = sol;
                    }
                });
        }
        catch (OperationCanceledException) { return null; }

        // Fill unreachable gaps with nearest valid solution so playback stays smooth.
        var solutions = new float[total][];
        var lastValid = seed;
        for (int i = 0; i < total; i++)
        {
            if (ikSolutions[i] is not null) lastValid = ikSolutions[i]!;
            solutions[i] = (float[])lastValid.Clone();
        }

        // Unwrap joint angles to prevent ±360° configuration discontinuities at
        // chunk boundaries and travel→extrude transitions.  Each axis is adjusted
        // by the nearest multiple of 360° so consecutive solutions stay continuous.
        for (int i = 1; i < total; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                float diff = solutions[i][j] - solutions[i - 1][j];
                if      (diff >  180f) solutions[i][j] -= 360f;
                else if (diff < -180f) solutions[i][j] += 360f;
            }
        }

        // Velocity profile: time (ms) per move accounting for C_VEL corner blending.
        var (moveTimes, peakVelocities, junctionSpeeds, cosAngles) = BuildMoveProfile(
            toolpath, input.PrintMmS, input.TravelMmS, input.WipeMmS, input.ApoCvelFrac);

        // Singularity detection: flag moves where |A5| < 5° (wrist singularity).
        var singularity = new bool[total];
        var speedRisk = new bool[total];
        for (int i = 0; i < total; i++)
        {
            singularity[i] = MathF.Abs(solutions[i][4]) < 5f;
            if (i == 0) continue;

            // Skip per-joint rate check when KUKA's C_VEL blending is actively managing
            // this corner. The IK solver returns endpoint poses; at a sharp direction change
            // the robot never snaps between those poses — it blends continuously across the
            // corner arc. The joint-rate computed from consecutive endpoint solutions over
            // the short segment duration is therefore an overestimate. The real axis speed
            // limit that matters is on straight extrudes (as in Rev142) where there is no
            // blending to distribute the joint motion.
            //
            // A corner is "blend-managed" when cosA < 0 (>90° direction change) or when
            // the junction factor is well below 1 (blend speed much less than cruise speed).
            // The threshold 0.3 corresponds to ~73° — a sharp corner by any measure.
            // Straight segments (cosA≈1) still get the full joint rate check.
            float cornerCosA = i < cosAngles.Length ? cosAngles[i] : 1f;
            bool blendManaged = cornerCosA < 0.3f;

            if (!blendManaged)
            {
                float dt = MathF.Max(moveTimes[i] / 1000f, 1e-4f);
                for (int j = 0; j < 6; j++)
                {
                    var rate = AxisMotionLimits.CheckJointRate(
                        solutions[i][j] - solutions[i - 1][j],
                        dt,
                        AxisMotionLimits.Kr120R3900DegPerSec[j],
                        j);
                    if (rate.Exceeded)
                    {
                        speedRisk[i] = true;
                        break;
                    }
                }
            }
        }

        // -- TCP auto-rotate repair -------------------------------------------
        // Print-neutral nozzle spin for spans that are unreachable, singular, or
        // over an axis speed. A spin is only written if the whole range it
        // rewrites (ramp included) stays reachable and legal; otherwise the span
        // keeps its original solve and is reported downstream as a limit
        // violation. See RepairSpansWithSpin.
        {
            bool anyBad = false;
            for (int i = 0; i < total && !anyBad; i++)
                anyBad = !result[i] || singularity[i] || speedRisk[i];

            if (anyBad)
            {
                var flatMoves = new ToolpathMove[total];
                {
                    int fi = 0;
                    foreach (var layer in toolpath.Layers)
                        foreach (var mv in layer.Moves)
                        { if (fi < total) flatMoves[fi] = mv; fi++; }
                }

                var yawByMove = new float[total];

                // Same solve the main pass trusted: position-first, then oriented,
                // joint envelope, and the print fallback seeds when the walk seed fails.
                float[]? SpinSolveAt(int i, float yawDeg, float[] walkSeed)
                {
                    var rot = millPath
                        ? solver.TargetRotFromMillNormal(normals[i], yawDeg)
                        : solver.TargetRotFromGlobalOrientation(
                            normals[i], offA, offB, offC + yawDeg);
                    var fast = SolveReach(targets[i], walkSeed, rot, 40);
                    if (fast is not null) return fast;
                    if (millPath) return SolveReach(targets[i], walkSeed, rot, 80);
                    return SolveWithPrintFallback(
                        walkSeed,
                        w => SolveReach(targets[i], w, rot, 80),
                        PrintIkFallbackSeeds(targets[i].X, targets[i].Y));
                }

                try
                {
                    RepairSpansWithSpin(result, singularity, speedRisk, solutions,
                        moveTimes, cosAngles, yawByMove, SpinSolveAt, ct);
                }
                catch (OperationCanceledException) { return null; }

                // Bake the repair into the toolpath so KRL export writes the
                // rotated orientations.
                for (int i = 0; i < total; i++)
                    flatMoves[i].TcpYawDeg = yawByMove[i];
            }
        }

        // ── Digital-twin collision sweep (environment + self + material) ────
        bool[]? collision = null;
        CollisionHit? firstCollHit = null;
        int collStride = 1;
        var collisionWorld = input.World;
        if (collisionWorld is not null)
        {
            try
            {
                collisionWorld.Beads = collisionWorld.Settings.CheckMaterial
                    ? new BeadObstacleGrid(toolpath, input.BeadWidthColl,
                                           input.WorldTransformColl, input.OriginColl)
                    : null;

                var chainRoots = new NMatrix[total];
                var tcpWorlds = new NVec3[total];
                var railColl = input.Rail;
                for (int i = 0; i < total; i++)
                {
                    if (e1Motion && railColl is { } rc)
                    {
                        var bw = RailE1Planner.BaseWorld(homeWorld, rc, e1PerMove[i]);
                        var bh = RailE1Planner.BaseWorld(homeWorld, rc, homeE1);
                        chainRoots[i] = input.ChainRootColl *
                            NMatrix.CreateTranslation(
                                bw.X - bh.X, bw.Y - bh.Y, bw.Z - bh.Z);
                        tcpWorlds[i] = new NVec3(
                            targets[i].X + bw.X, targets[i].Y + bw.Y, targets[i].Z + bw.Z);
                    }
                    else
                    {
                        chainRoots[i] = input.ChainRootColl;
                        tcpWorlds[i] = new NVec3(
                            targets[i].X + robroot.X, targets[i].Y + robroot.Y, targets[i].Z + robroot.Z);
                    }
                }

                var solved = new float[total][];
                for (int i = 0; i < total; i++) solved[i] = solutions[i] ?? seed;

                var collResult = ToolpathCollisionChecker.Check(
                    collisionWorld, solved, chainRoots, tcpWorlds, ct);
                collision = collResult.Colliding;
                collStride = collResult.SampleStride;
                for (int i = 0; i < total; i++)
                    if (collision[i])
                        firstCollHit ??= collResult.Hits[i];
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[collision] sweep failed: {ex.Message}");
                collision = null;
            }
            finally
            {
                collisionWorld.Beads = null;   // free the per-toolpath grid
            }
        }

        var (limitCount, limitFlags) = ApplyAxisSpeedRepairs(
            toolpath, solutions, moveTimes, cosAngles, seed, normals, offA, offB, offC, cellJoints);

        return new Result(
            Reachable: result,
            Solutions: solutions,
            Singularity: singularity,
            MoveTimesMs: moveTimes,
            PeakVelocities: peakVelocities,
            E1PerMove: e1PerMove,
            Collision: collision,
            CollisionStride: collStride,
            FirstCollisionHit: firstCollHit,
            UnrepairableLimits: limitCount,
            AxisLimit: limitFlags);
    }

    /// <summary>Moves over which a repair spin ramps in and out.</summary>
    public const int SpinRamp = 60;

    /// <summary>Wrist margin a repaired span must hold (deg of |A5|).</summary>
    public const float SpinMinA5 = 6f;

    /// <summary>|A5| below this is a wrist singularity.</summary>
    public const float SingularA5 = 5f;

    /// <summary>At or below this cos, a corner is blended by C_VEL and its joint step is not rated.</summary>
    public const float BlendCornerCos = 0.3f;

    /// <summary>Nozzle spins tried, smallest first. Each is tried +/-.</summary>
    public static readonly float[] SpinMagnitudesDeg = [20f, 40f, 60f, 90f, 120f, 150f, 180f];

    /// <summary>
    /// Ramp weight of the spin at move <paramref name="i"/>: 1 across the span,
    /// linear to 0 at <paramref name="rIn"/> and <paramref name="rOut"/>.
    /// </summary>
    public static float SpinRampWeight(int i, int s0, int s1, int rIn, int rOut)
        => i < s0 ? (i - rIn) / (float)Math.Max(1, s0 - rIn)
         : i > s1 ? (rOut - i) / (float)Math.Max(1, rOut - s1)
         : 1f;

    /// <summary>
    /// Print-neutral nozzle-spin repair. For each bad span (unreachable,
    /// singular, or over an axis speed) try spins smallest first. A spin is
    /// accepted only if every move it rewrites — the span and its ramp — solves,
    /// holds the wrist clear, and keeps every axis under its rated speed at the
    /// planned move times, including the step back onto the untouched path.
    /// If no spin passes, nothing is written: the span keeps its original solve
    /// and flags. A repair can only turn moves reachable, never unreachable.
    /// </summary>
    /// <param name="solveAt">Solve move i at a nozzle spin (deg) from a walk seed.
    /// Null means unreachable.</param>
    /// <returns>Number of spans repaired.</returns>
    public static int RepairSpansWithSpin(
        bool[] reachable,
        bool[] singularity,
        bool[] speedRisk,
        float[][] solutions,
        float[] moveTimesMs,
        float[] cosAngles,
        float[] yawByMove,
        Func<int, float, float[], float[]?> solveAt,
        CancellationToken ct = default)
    {
        int total = solutions.Length;
        int reachableBefore = 0;
        foreach (var r in reachable) if (r) reachableBefore++;

        bool Bad(int i) => !reachable[i] || singularity[i] || speedRisk[i];

        int repaired = 0;
        int s0 = 0;
        while (s0 < total)
        {
            ct.ThrowIfCancellationRequested();
            if (!Bad(s0)) { s0++; continue; }
            int s1 = s0;
            while (s1 + 1 < total && Bad(s1 + 1)) s1++;

            int rIn  = Math.Max(0, s0 - SpinRamp);
            int rOut = Math.Min(total - 1, s1 + SpinRamp);
            int len  = rOut - rIn + 1;
            var trialSol = new float[len][];
            var trialYaw = new float[len];
            bool accepted = false;

            foreach (float mag in SpinMagnitudesDeg)
            {
                foreach (float sgn in new[] { 1f, -1f })
                {
                    ct.ThrowIfCancellationRequested();
                    if (TrySpin(mag * sgn))
                    {
                        for (int k = 0; k < len; k++)
                        {
                            int i = rIn + k;
                            solutions[i]   = trialSol[k];
                            yawByMove[i]   = trialYaw[k];
                            reachable[i]   = true;
                            singularity[i] = MathF.Abs(trialSol[k][4]) < SingularA5;
                            speedRisk[i]   = false;
                        }
                        repaired++;
                        accepted = true;
                        break;
                    }
                }
                if (accepted) break;
            }
            // No spin passed: leave rIn..rOut exactly as solved. The span stays
            // flagged and the limit scan blocks export.
            s0 = s1 + 1;

            bool TrySpin(float spin)
            {
                float[]? walk = rIn > 0 ? solutions[rIn - 1] : solutions[rIn];
                for (int k = 0; k < len; k++)
                {
                    int i = rIn + k;
                    float y = spin * SpinRampWeight(i, s0, s1, rIn, rOut);
                    // An earlier span's ramp may already spin this move; keep the larger.
                    if (MathF.Abs(yawByMove[i]) > MathF.Abs(y)) y = yawByMove[i];

                    var sol = solveAt(i, y, walk);
                    if (sol is null) return false;
                    sol = (float[])sol.Clone();
                    // Same ±360 continuity the main pass applies.
                    for (int j = 0; j < Math.Min(6, sol.Length); j++)
                    {
                        float d = sol[j] - walk[j];
                        if (d > 180f) sol[j] -= 360f;
                        else if (d < -180f) sol[j] += 360f;
                    }

                    bool inSpan = i >= s0 && i <= s1;
                    float a5 = MathF.Abs(sol[4]);
                    if (inSpan ? a5 < SpinMinA5 : a5 < SingularA5) return false;

                    // Inside the span every step must be legal. On the ramp a step
                    // may only stay over if it was already over before the spin —
                    // that violation belongs to a neighbouring span, and the final
                    // limit scan still reports it.
                    if (i > 0 && StepOverLimit(i, walk, sol)
                        && (inSpan || !StepOverLimit(i, solutions[i - 1], solutions[i])))
                        return false;

                    trialSol[k] = sol;
                    trialYaw[k] = y;
                    walk = sol;
                }
                // The move after the ramp now starts from a new pose.
                if (rOut + 1 < total
                    && StepOverLimit(rOut + 1, walk, solutions[rOut + 1])
                    && !StepOverLimit(rOut + 1, solutions[rOut], solutions[rOut + 1]))
                    return false;
                return true;
            }
        }

        int reachableAfter = 0;
        foreach (var r in reachable) if (r) reachableAfter++;
        // Accepting a spin only ever sets reachable = true, so this cannot fire.
        // It is here so a future edit that breaks that is caught in tests.
        System.Diagnostics.Debug.Assert(reachableAfter >= reachableBefore,
            $"Spin repair lost reach: {reachableBefore} -> {reachableAfter}.");
        return repaired;

        bool StepOverLimit(int i, float[] from, float[] to)
        {
            if (i < cosAngles.Length && cosAngles[i] < BlendCornerCos) return false;
            float dt = MathF.Max(moveTimesMs[i] / 1000f, 1e-4f);
            int axes = Math.Min(6, Math.Min(from.Length, to.Length));
            for (int j = 0; j < axes; j++)
            {
                if (AxisMotionLimits.CheckJointRate(
                        to[j] - from[j], dt, AxisMotionLimits.Kr120R3900DegPerSec[j], j).Exceeded)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// After the wrist replan, flag any span that is still over a speed or
    /// software limit at print speed. Do not write a slower bead.
    /// </summary>
    static (int Unrepairable, bool[] Flags) ApplyAxisSpeedRepairs(
        Toolpath toolpath,
        float[][] solutions,
        float[] moveTimesMs,
        float[] cosAngles,
        float[] seed,
        TkVector3[] normals,
        float offA,
        float offB,
        float offC,
        IReadOnlyList<JointConfig>? joints)
    {
        int total = solutions.Length;
        var flags = new bool[total];
        if (total == 0 || moveTimesMs.Length < total) return (0, flags);

        var poses = new float[total + 1][];
        poses[0] = seed;
        var dt = new float[total];
        var orient = new float[total];
        (float A, float B, float C) prevAbc = default;
        bool havePrev = false;
        int fi = 0;
        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (fi >= total) break;
                poses[fi + 1] = solutions[fi] ?? seed;
                // For blend-managed corners (sharp direction changes that KUKA smooths
                // via C_VEL), set dt to a large value so Scan() skips the joint-rate
                // check for that step. The real constraint is still checked on the
                // straight segments where blending does not apply.
                bool blendManaged = fi < cosAngles.Length && cosAngles[fi] < 0.3f;
                dt[fi] = blendManaged
                    ? float.MaxValue / 2f
                    : MathF.Max(moveTimesMs[fi] / 1000f, 1e-4f);
                var n = fi < normals.Length ? normals[fi] : TkVector3.UnitZ;
                if (n.LengthSquared < 1e-8f) n = TkVector3.UnitZ;
                var abc = KukaOrientation.AbcFromNormal(
                    new NVec3(n.X, n.Y, n.Z), offA, offB, offC + move.TcpYawDeg);
                orient[fi] = havePrev ? AbcDeltaDeg(prevAbc, abc) : 0f;
                prevAbc = abc;
                havePrev = true;
                fi++;
            }
        }

        float[]? min = null, max = null;
        if (joints is { Count: > 0 })
        {
            min = new float[joints.Count];
            max = new float[joints.Count];
            for (int j = 0; j < joints.Count; j++)
            {
                min[j] = joints[j].MinDeg;
                max[j] = joints[j].MaxDeg;
            }
        }

        var scan = AxisMotionLimits.Scan(poses, dt, orient, minDeg: min, maxDeg: max);
        int bad = 0;
        fi = 0;
        foreach (var layer in toolpath.Layers)
        {
            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                if (fi >= scan.Scale.Length) break;
                if (scan.Scale[fi] < 0.999f)
                {
                    flags[fi] = true;
                    bad++;
                }
                fi++;
            }
        }
        return (bad, flags);
    }

    static float AbcDeltaDeg((float A, float B, float C) a, (float A, float B, float C) b)
    {
        float da = NormDeg(b.A - a.A);
        float db = NormDeg(b.B - a.B);
        float dc = NormDeg(b.C - a.C);
        return MathF.Sqrt(da * da + db * db + dc * dc);
    }

    static float NormDeg(float d)
    {
        d %= 360f;
        if (d > 180f) d -= 360f;
        else if (d < -180f) d += 360f;
        return MathF.Abs(d);
    }

    /// <summary>Mill T12 uses a different target-rotation convention (cutter along tool +Z into
    /// the work) than the extruder — mirrors ViewportView.axaml.cs's own copy of this check.</summary>
    static bool ToolpathHasMillMoves(Toolpath? tp)
    {
        if (tp is null) return false;
        foreach (var layer in tp.Layers)
            foreach (var m in layer.Moves)
                if (m.Kind == MoveKind.Mill) return true;
        return false;
    }

    /// <summary>
    /// Computes per-move timing (ms) and peak velocity (mm/s) for the toolpath using a
    /// two-pass trapezoidal velocity profile with KUKA C_VEL corner-speed limits.
    /// <para>
    /// Corner speed at each junction = <c>apoCvelFraction × min(v_in, v_out)</c> scaled by
    /// the cosine of the direction change — straight runs carry full speed, sharp turns
    /// slow to <paramref name="apoCvelFraction"/> × programmed speed (default 0.5, matching
    /// <c>$APO.CVEL=50</c>). A two-pass forward/backward sweep propagates acceleration
    /// constraints so short segments between close corners also show realistic slowdowns.
    /// </para>
    /// </summary>
    public static (float[] timesMs, float[] peakVelocities, float[] junctionSpeeds, float[] cosAngles) BuildMoveProfile(
        Toolpath tp, float printMmS, float travelMmS, float wipeMmS,
        float apoCvelFraction = 0.5f, float accelMmS2 = 2000f)
    {
        var moves = new List<ToolpathMove>(tp.Layers.Sum(l => l.Moves.Count));
        foreach (var layer in tp.Layers) moves.AddRange(layer.Moves);

        int n = moves.Count;
        if (n == 0) return ([], [], [], []);

        var vProg = new float[n];
        var dist  = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (moves[i].IsWipe)
                vProg[i] = wipeMmS;
            else if (moves[i].Kind == MoveKind.Extrude)
            {
                float speed = printMmS * Math.Max(moves[i].PrintSpeedScale, 1e-6f);
                if (moves[i].IsResumeRamp)
                    speed *= Math.Max(moves[i].ResumeSpeedScale, 1e-6f);
                vProg[i] = speed;
            }
            else
                vProg[i] = travelMmS;
            dist[i]  = NVec3.Distance(moves[i].From, moves[i].To);
        }

        // Junction speeds: the robot must not exceed this speed at waypoint i.
        // At each junction the factor blends linearly between apoCvel (sharp reversal)
        // and 1.0 (perfectly straight) based on the cosine of the direction change.
        var jV    = new float[n + 1]; // jV[0]=0 (start at rest), jV[n]=0 (end at rest)
        var cosA  = new float[n + 1]; // cosA[i] = cos(direction change at waypoint i); 1=straight
        for (int i = 1; i < n; i++)
        {
            var d1 = moves[i - 1].To - moves[i - 1].From;
            var d2 = moves[i].To     - moves[i].From;
            float l1 = d1.Length(), l2 = d2.Length();
            cosA[i] = l1 > 1e-6f && l2 > 1e-6f
                ? NVec3.Dot(d1 / l1, d2 / l2)
                : 1f;
            float factor = apoCvelFraction + (1f - apoCvelFraction) * 0.5f * (cosA[i] + 1f);
            jV[i] = factor * MathF.Min(vProg[i - 1], vProg[i]);
        }

        // Forward pass: max speed reachable by accelerating from entry junction speed.
        var vFwd = new float[n];
        for (int i = 0; i < n; i++)
            vFwd[i] = MathF.Min(vProg[i], MathF.Sqrt(jV[i] * jV[i] + 2f * accelMmS2 * dist[i]));

        // Backward pass: cap so the robot can decelerate to the exit junction speed.
        var vPeak = (float[])vFwd.Clone();
        for (int i = n - 1; i >= 0; i--)
        {
            float vReachable = MathF.Sqrt(jV[i + 1] * jV[i + 1] + 2f * accelMmS2 * dist[i]);
            vPeak[i] = MathF.Min(vFwd[i], MathF.Min(vProg[i], vReachable));
        }

        // Compute time per move using a trapezoidal (or triangular) velocity profile.
        var timesMs = new float[n];
        for (int i = 0; i < n; i++)
        {
            float d    = dist[i];
            float v0   = jV[i];
            float v1   = jV[i + 1];
            float vTop = vPeak[i];

            if (d < 1e-6f)  { timesMs[i] = 1f;    continue; }
            if (vTop < 1e-6f) { timesMs[i] = 1000f; continue; }

            float dAccel  = (vTop * vTop - v0 * v0) / (2f * accelMmS2);
            float dDecel  = (vTop * vTop - v1 * v1) / (2f * accelMmS2);
            float dCruise = d - dAccel - dDecel;

            float t;
            if (dCruise >= 0f)
            {
                t = (vTop - v0) / accelMmS2 + dCruise / vTop + (vTop - v1) / accelMmS2;
            }
            else
            {
                // Triangle: didn't reach vTop — solve for actual peak.
                float vActual = MathF.Sqrt((2f * accelMmS2 * d + v0 * v0 + v1 * v1) * 0.5f);
                vActual = MathF.Max(vActual, MathF.Max(v0, v1));
                t       = (vActual - v0) / accelMmS2 + (vActual - v1) / accelMmS2;
            }
            timesMs[i] = MathF.Max(t * 1000f, 0.1f);
        }

        return (timesMs, vPeak, jV, cosA);
    }
}
