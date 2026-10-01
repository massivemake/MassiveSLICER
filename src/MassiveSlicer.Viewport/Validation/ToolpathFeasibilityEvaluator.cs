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

    /// <summary>
    /// One pose, the way validation decides it: position-first DLS from
    /// <paramref name="seed"/>, then a 6D refine that must match the tool orientation (print)
    /// and stay inside the joint envelope. Null = not reachable. Public so the E1 rail
    /// planner asks exactly this question instead of keeping its own copy.
    /// </summary>
    public static float[]? SolvePose(
        GltfNumericalIkSolver solver, TkVector3 target, float[] seed,
        (TkVector3 r0, TkVector3 r1, TkVector3 r2) rot,
        IReadOnlyList<JointConfig>? joints, bool millPath, int maxIterations)
    {
        var pos = solver.Solve(target, seed, maxIterations: maxIterations);
        if (pos is null || (joints is not null && !JointLimitEnvelope.JointsInside(pos, joints)))
            return null;
        var sol = solver.Solve(target, pos, rot, maxIterations: maxIterations,
            requireOrientation: !millPath);
        if (sol is not null && (joints is not null && !JointLimitEnvelope.JointsInside(sol, joints)))
            sol = null;
        float orientErr = sol is not null ? solver.OrientationError(sol, rot) : float.MaxValue;
        return PreferOrientedPrintSolution(sol, pos, orientErr, millPath);
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
        CollisionHit? FirstCollisionHit);

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
            => SolvePose(solver, target, walkSeed, rot, cellJoints, millPath, maxIterations);

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
        var (moveTimes, peakVelocities) = BuildMoveProfile(
            toolpath, input.PrintMmS, input.TravelMmS, input.WipeMmS, input.ApoCvelFrac);

        // Singularity detection: flag moves where |A5| < 5° (wrist singularity).
        var singularity = new bool[total];
        for (int i = 0; i < total; i++)
            singularity[i] = MathF.Abs(solutions[i][4]) < 5f;

        // -- TCP auto-rotate repair -------------------------------------------
        // The nozzle is rotationally symmetric, so spinning it about its own axis
        // (KUKA C offset) is print-neutral — but it swings the flange/wrist into a
        // different configuration. For each flagged span, search for the smallest
        // spin that clears the wrist singularity, ramp it in/out smoothly over
        // neighbouring moves, and re-solve IK for the affected range.
        {
            bool anyBad = false;
            for (int i = 0; i < total && !anyBad; i++)
                anyBad = !result[i] || singularity[i];

            if (anyBad)
            {
                var flatMoves = new ToolpathMove[total];
                {
                    int fi = 0;
                    foreach (var layer in toolpath.Layers)
                        foreach (var mv in layer.Moves)
                        { if (fi < total) flatMoves[fi] = mv; fi++; }
                }

                const int   Ramp  = 60;   // moves over which yaw ramps in/out
                const float MinA5 = 6f;   // deg of wrist margin required
                var yawByMove = new float[total];
                bool Bad(int i) => !result[i] || singularity[i];

                int s0 = 0;
                while (s0 < total)
                {
                    if (ct.IsCancellationRequested) return null;
                    if (!Bad(s0)) { s0++; continue; }
                    int s1 = s0;
                    while (s1 + 1 < total && Bad(s1 + 1)) s1++;

                    // Smallest nozzle spin that clears the span's start/middle/end.
                    float chosen = 0f;
                    foreach (float mag in new[] { 20f, 40f, 60f, 90f, 120f, 150f, 180f })
                    {
                        foreach (float sgn in new[] { 1f, -1f })
                        {
                            float y = mag * sgn;
                            bool ok = true;
                            foreach (int ti in new[] { s0, (s0 + s1) / 2, s1 })
                            {
                                var rot = millPath
                                    ? solver.TargetRotFromMillNormal(normals[ti], y)
                                    : solver.TargetRotFromGlobalOrientation(
                                        normals[ti], offA, offB, offC + y);
                                var sol = solver.Solve(targets[ti],
                                    solutions[Math.Max(0, ti - 1)], rot, maxIterations: 60);
                                if (sol is null || MathF.Abs(sol[4]) < MinA5) { ok = false; break; }
                            }
                            if (ok) { chosen = y; break; }
                        }
                        if (chosen != 0f) break;
                    }

                    if (chosen != 0f)
                    {
                        int rIn  = Math.Max(0, s0 - Ramp);
                        int rOut = Math.Min(total - 1, s1 + Ramp);
                        for (int i = rIn; i <= rOut; i++)
                        {
                            float w = i < s0 ? (i - rIn)  / (float)Math.Max(1, s0 - rIn)
                                    : i > s1 ? (rOut - i) / (float)Math.Max(1, rOut - s1)
                                    : 1f;
                            float y = chosen * w;
                            if (MathF.Abs(y) > MathF.Abs(yawByMove[i])) yawByMove[i] = y;
                        }

                        // Re-solve the affected range with the yawed orientation.
                        var chunkSeed = solutions[Math.Max(0, rIn - 1)];
                        for (int i = rIn; i <= rOut; i++)
                        {
                            var rot = millPath
                                ? solver.TargetRotFromMillNormal(normals[i], yawByMove[i])
                                : solver.TargetRotFromGlobalOrientation(
                                    normals[i], offA, offB, offC + yawByMove[i]);
                            var sol = solver.Solve(targets[i], chunkSeed, rot, maxIterations: 40);
                            bool inEnv = sol is not null &&
                                (cellJoints is null || JointLimitEnvelope.JointsInside(sol, cellJoints));
                            result[i] = inEnv;
                            if (inEnv) { solutions[i] = sol!; chunkSeed = sol!; }
                            singularity[i] = MathF.Abs(solutions[i][4]) < 5f;
                        }
                    }
                    s0 = s1 + 1;
                }

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

        return new Result(
            Reachable: result,
            Solutions: solutions,
            Singularity: singularity,
            MoveTimesMs: moveTimes,
            PeakVelocities: peakVelocities,
            E1PerMove: e1PerMove,
            Collision: collision,
            CollisionStride: collStride,
            FirstCollisionHit: firstCollHit);
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
    public static (float[] timesMs, float[] peakVelocities) BuildMoveProfile(
        Toolpath tp, float printMmS, float travelMmS, float wipeMmS,
        float apoCvelFraction = 0.5f, float accelMmS2 = 2000f)
    {
        var moves = new List<ToolpathMove>(tp.Layers.Sum(l => l.Moves.Count));
        foreach (var layer in tp.Layers) moves.AddRange(layer.Moves);

        int n = moves.Count;
        if (n == 0) return ([], []);

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
        var jV = new float[n + 1]; // jV[0]=0 (start at rest), jV[n]=0 (end at rest)
        for (int i = 1; i < n; i++)
        {
            var d1 = moves[i - 1].To - moves[i - 1].From;
            var d2 = moves[i].To     - moves[i].From;
            float l1 = d1.Length(), l2 = d2.Length();
            float cosA = l1 > 1e-6f && l2 > 1e-6f
                ? NVec3.Dot(d1 / l1, d2 / l2)
                : 1f;
            float factor = apoCvelFraction + (1f - apoCvelFraction) * 0.5f * (cosA + 1f);
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

        return (timesMs, vPeak);
    }
}
