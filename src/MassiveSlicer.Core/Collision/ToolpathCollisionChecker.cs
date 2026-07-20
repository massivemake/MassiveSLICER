using System.Diagnostics;
using System.Numerics;

namespace MassiveSlicer.Core.Collision;

/// <summary>Result of a whole-toolpath collision sweep.</summary>
public sealed class CollisionResult
{
    public required bool[] Colliding { get; init; }
    public required CollisionHit?[] Hits { get; init; }
    /// <summary>1 = every move checked; K = only every Kth move (time budget).</summary>
    public required int SampleStride { get; init; }
    public required int CheckedMoves { get; init; }
    public required long ElapsedMs { get; init; }
}

/// <summary>
/// Sweeps a solved toolpath (per-move joint angles) through the collision world.
/// Chunked parallel, with a pose-delta gate (straight bead runs collapse to one
/// check) and a time-budget stride fallback for pathological move counts.
/// </summary>
public static class ToolpathCollisionChecker
{
    /// <param name="solutions">Per-move KRL joint angles (unreachable moves may repeat neighbors).</param>
    /// <param name="chainRoots">Per-move FK chain root (rail carriage position folded in).</param>
    /// <param name="tcpWorld">Per-move TCP world position (nozzle-exclusion anchor).</param>
    public static CollisionResult Check(
        CollisionWorld world,
        float[][] solutions,
        Matrix4x4[] chainRoots,
        Vector3[] tcpWorld,
        CancellationToken ct = default)
    {
        int total = solutions.Length;
        var colliding = new bool[total];
        var hits = new CollisionHit?[total];
        var sw = Stopwatch.StartNew();
        var s = world.Settings;

        if (total == 0)
            return new CollisionResult
            {
                Colliding = colliding, Hits = hits,
                SampleStride = 1, CheckedMoves = 0, ElapsedMs = 0,
            };

        // Timing probe over the first moves → stride so the sweep fits the budget.
        int stride = 1;
        {
            int probeN = Math.Min(200, total);
            var scratch = new CollisionWorld.Scratch();
            var probeSw = Stopwatch.StartNew();
            for (int i = 0; i < probeN; i++)
                world.CheckPose(solutions[i], chainRoots[i],
                    i - s.RecentMoveSkip, tcpWorld[i], scratch, out _);
            probeSw.Stop();
            double perMoveMs = probeSw.Elapsed.TotalMilliseconds / probeN;
            // Pose-delta gating typically skips ~70%+; assume 3× headroom.
            double estMs = perMoveMs * total / 3.0;
            if (estMs > s.TimeBudgetMs)
                stride = Math.Max(1, (int)Math.Ceiling(estMs / s.TimeBudgetMs));
        }

        int numChunks = Math.Max(1, Math.Min(Environment.ProcessorCount, total / 256 + 1));
        int chunkSize = (total + numChunks - 1) / numChunks;
        int checkedCount = 0;

        Parallel.For(0, numChunks, () => new CollisionWorld.Scratch(), (chunk, _, scratch) =>
        {
            int start = chunk * chunkSize;
            int end = Math.Min(start + chunkSize, total);
            float[]? prevKrl = null;
            Vector3 prevTcp = default;
            bool prevFlag = false;
            CollisionHit? prevHit = null;
            int localChecked = 0;

            for (int i = start; i < end; i++)
            {
                if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();

                // Stride sampling (budget fallback): carry the last verdict.
                if (stride > 1 && i % stride != 0 && prevKrl is not null)
                {
                    colliding[i] = prevFlag;
                    hits[i] = prevHit;
                    continue;
                }

                // Pose-delta gate: effectively the same pose → same verdict.
                if (prevKrl is not null)
                {
                    float maxDelta = 0f;
                    for (int j = 0; j < 6; j++)
                        maxDelta = MathF.Max(maxDelta, MathF.Abs(solutions[i][j] - prevKrl[j]));
                    if (maxDelta < s.JointDeltaGateDeg
                        && Vector3.DistanceSquared(tcpWorld[i], prevTcp)
                           < s.TcpDeltaGateMm * s.TcpDeltaGateMm)
                    {
                        colliding[i] = prevFlag;
                        hits[i] = prevHit;
                        continue;
                    }
                }

                bool flag = world.CheckPose(solutions[i], chainRoots[i],
                    i - s.RecentMoveSkip, tcpWorld[i], scratch, out var hit);
                localChecked++;
                colliding[i] = flag;
                hits[i] = flag ? hit : null;

                prevKrl = solutions[i];
                prevTcp = tcpWorld[i];
                prevFlag = flag;
                prevHit = hits[i];
            }

            Interlocked.Add(ref checkedCount, localChecked);
            return scratch;
        }, _ => { });

        sw.Stop();
        return new CollisionResult
        {
            Colliding = colliding,
            Hits = hits,
            SampleStride = stride,
            CheckedMoves = checkedCount,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}
