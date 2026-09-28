using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Plans linear-rail (KUKA E1, mm) positions within a home-centered Y+/Y− allowance.
/// Prefers carriage poses that put the TCP in the arm workspace (when a reachability
/// predicate is provided), sampling both + and − rail directions — not just path tracking.
/// </summary>
public static class RailE1Planner
{
    /// <summary>
    /// Ideal E1 (mm) so the robot base is closest to <paramref name="worldPos"/> along the rail axis.
    /// Clamped to [home − yMinus, home + yPlus] and the rail soft limits.
    /// </summary>
    public static float IdealE1(
        Vector3 worldPos,
        Vector3 robotHomeWorld,
        RobotRailCellConfig rail,
        float homeE1Mm,
        float yPlusMm,
        float yMinusMm)
    {
        float targetAlong = Along(worldPos, rail.Axis);
        float homeAlong   = Along(robotHomeWorld, rail.Axis);
        float sign = rail.E1Sign == 0f ? 1f : rail.E1Sign;
        float ideal = sign * (targetAlong - homeAlong);
        var (rMin, rMax) = JointLimitEnvelope.Inset(rail.MinMm, rail.MaxMm);
        return ClampToAllowance(ideal, homeE1Mm, yPlusMm, yMinusMm, rMin, rMax);
    }

    static (float Min, float Max) RailEnvelope(RobotRailCellConfig rail)
        => JointLimitEnvelope.Inset(rail.MinMm, rail.MaxMm);

    public static float ClampToAllowance(
        float e1,
        float homeE1Mm,
        float yPlusMm,
        float yMinusMm,
        float railMinMm,
        float railMaxMm)
    {
        float lo = MathF.Max(railMinMm, homeE1Mm - MathF.Max(0f, yMinusMm));
        float hi = MathF.Min(railMaxMm, homeE1Mm + MathF.Max(0f, yPlusMm));
        if (lo > hi) (lo, hi) = (hi, lo);
        return Math.Clamp(e1, lo, hi);
    }

    /// <summary>
    /// E1 sample set covering home, geometric track, and a grid across the full
    /// Y− … Y+ allowance (so + and − directions are both considered).
    /// </summary>
    public static float[] BuildCandidates(
        Vector3 worldPos,
        Vector3 robotHomeWorld,
        RobotRailCellConfig rail,
        float homeE1Mm,
        float yPlusMm,
        float yMinusMm,
        int gridCount = 9)
    {
        var (rMin, rMax) = RailEnvelope(rail);
        float lo = MathF.Max(rMin, homeE1Mm - MathF.Max(0f, yMinusMm));
        float hi = MathF.Min(rMax, homeE1Mm + MathF.Max(0f, yPlusMm));
        if (lo > hi) (lo, hi) = (hi, lo);

        gridCount = Math.Clamp(gridCount, 3, 21);
        var set = new HashSet<float>();
        set.Add(ClampToAllowance(homeE1Mm, homeE1Mm, yPlusMm, yMinusMm, rMin, rMax));
        set.Add(IdealE1(worldPos, robotHomeWorld, rail, homeE1Mm, yPlusMm, yMinusMm));

        for (int i = 0; i < gridCount; i++)
        {
            float t = gridCount == 1 ? 0.5f : i / (float)(gridCount - 1);
            set.Add(lo + (hi - lo) * t);
        }

        // Explicit endpoints (positive / negative allowance extremes)
        set.Add(lo);
        set.Add(hi);

        var list = set.ToList();
        list.Sort();
        return list.ToArray();
    }

    /// <summary>
    /// Pick the best E1 for one world point: prefer workspace-reachable samples, then
    /// mid-reach horizontal distance, then proximity to <paramref name="prevE1"/> (smoothness).
    /// </summary>
    /// <param name="inWorkspace">
    /// Optional: (target in ROBROOT at that E1) → true if arm envelope allows it.
    /// When null, only geometric mid-reach + smoothness is used.
    /// </param>
    public static float PickBestE1(
        Vector3 worldPos,
        Vector3 robotHomeWorld,
        RobotRailCellConfig rail,
        float homeE1Mm,
        float yPlusMm,
        float yMinusMm,
        float prevE1,
        float preferredHorizReachMm,
        Func<Vector3 /*targetRobroot*/, bool>? inWorkspace,
        int gridCount = 9)
    {
        var candidates = BuildCandidates(
            worldPos, robotHomeWorld, rail, homeE1Mm, yPlusMm, yMinusMm, gridCount);

        float bestE1 = float.IsNaN(prevE1) ? homeE1Mm : prevE1;
        float bestScore = float.MaxValue;
        bool anyReachable = false;

        foreach (float e1 in candidates)
        {
            var baseW = BaseWorld(robotHomeWorld, rail, e1);
            var rel = worldPos - baseW; // ROBROOT-frame TCP if base is at e1

            bool reachable = inWorkspace?.Invoke(rel) ?? true;
            if (inWorkspace is not null && !reachable)
            {
                // Still consider as last resort with huge penalty
            }
            else
                anyReachable = true;

            float dxy = MathF.Sqrt(rel.X * rel.X + rel.Y * rel.Y);
            // Prefer mid-reach; strong penalty when outside envelope
            float reachTerm = MathF.Abs(dxy - preferredHorizReachMm);
            float smoothTerm = 0.15f * MathF.Abs(e1 - (float.IsNaN(prevE1) ? homeE1Mm : prevE1));
            float failTerm = reachable ? 0f : 1_000_000f;
            // Slight preference for geometric track (base under TCP along rail)
            float trackTerm = 0.05f * MathF.Abs(e1 - IdealE1(
                worldPos, robotHomeWorld, rail, homeE1Mm, yPlusMm, yMinusMm));

            float score = failTerm + reachTerm + smoothTerm + trackTerm;
            if (score < bestScore)
            {
                bestScore = score;
                bestE1 = e1;
            }
        }

        // If nothing was reachable, still return best-scored (least-bad) sample.
        _ = anyReachable;
        return bestE1;
    }

    /// <summary>
    /// Plan E1 for a sequence of world positions (one per move endpoint). Smooths along the path.
    /// </summary>
    public static float[] PlanPath(
        IReadOnlyList<Vector3> worldPoints,
        Vector3 robotHomeWorld,
        RobotRailCellConfig rail,
        float homeE1Mm,
        float yPlusMm,
        float yMinusMm,
        float preferredHorizReachMm,
        Func<Vector3, bool>? inWorkspace,
        int gridCount = 9,
        float smoothBlend = 0.4f)
    {
        int n = worldPoints.Count;
        var e1 = new float[n];
        if (n == 0) return e1;
        var (rMin, rMax) = RailEnvelope(rail);

        float prev = homeE1Mm;
        for (int i = 0; i < n; i++)
        {
            float pick = PickBestE1(
                worldPoints[i], robotHomeWorld, rail, homeE1Mm, yPlusMm, yMinusMm,
                prev, preferredHorizReachMm, inWorkspace, gridCount);
            // Blend toward pick so rail doesn't step-jump every bead
            float blended = SmoothToward(prev, pick, smoothBlend);
            e1[i] = ClampToAllowance(blended, homeE1Mm, yPlusMm, yMinusMm, rMin, rMax);
            prev = e1[i];
        }

        // Forward-backward smooth pass to reduce residual chatter
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 1; i < n; i++)
                e1[i] = ClampToAllowance(
                    0.65f * e1[i] + 0.35f * e1[i - 1],
                    homeE1Mm, yPlusMm, yMinusMm, rMin, rMax);
            for (int i = n - 2; i >= 0; i--)
                e1[i] = ClampToAllowance(
                    0.65f * e1[i] + 0.35f * e1[i + 1],
                    homeE1Mm, yPlusMm, yMinusMm, rMin, rMax);
        }

        return e1;
    }

    public static float SmoothToward(float lastE1, float ideal, float blend = 0.25f)
    {
        blend = Math.Clamp(blend, 0.05f, 1f);
        if (float.IsNaN(lastE1)) return ideal;
        return lastE1 * (1f - blend) + ideal * blend;
    }

    public static float Along(Vector3 p, string axis)
        => axis.ToUpperInvariant() switch
        {
            "X" => p.X,
            "Z" => p.Z,
            _   => p.Y,
        };

    /// <summary>World-space robot base origin for a given E1 (mm).</summary>
    public static Vector3 BaseWorld(Vector3 robotHomeWorld, RobotRailCellConfig rail, float e1Mm)
    {
        var off = rail.SceneOffsetMm(e1Mm);
        return new Vector3(
            robotHomeWorld.X + off.X,
            robotHomeWorld.Y + off.Y,
            robotHomeWorld.Z + off.Z);
    }

    /// <summary>
    /// One layer of rail motion. A hold when one E1 covers every point. Otherwise
    /// holds joined by constant-speed glides (linear in path length, not point index).
    /// <paramref name="poseOk"/> is reach and singularity at that carriage position.
    /// Allowance samples are search only — they are not written into the plan.
    /// </summary>
    public readonly record struct LayerRailPlan(float[] E1Mm, int GlideCount);

    public static LayerRailPlan PlanLayer(
        IReadOnlyList<Vector3> worldPoints,
        Vector3 robotHomeWorld,
        RobotRailCellConfig rail,
        float homeE1Mm,
        float yPlusMm,
        float yMinusMm,
        float prevE1Mm,
        Func<Vector3, float, bool> poseOk)
    {
        int n = worldPoints.Count;
        var e1 = new float[n];
        if (n == 0) return new LayerRailPlan(e1, 0);
        ArgumentNullException.ThrowIfNull(poseOk);

        var bounds = Allowance(rail, homeE1Mm, yPlusMm, yMinusMm);
        float prev = float.IsNaN(prevE1Mm) ? homeE1Mm : prevE1Mm;
        prev = ClampToAllowance(prev, homeE1Mm, yPlusMm, yMinusMm, bounds.RailMin, bounds.RailMax);
        var s = PathLength(worldPoints);

        if (CoversAll(worldPoints, prev, poseOk))
        {
            Array.Fill(e1, prev);
            return new LayerRailPlan(e1, 0);
        }

        if (TryBestSingleCover(worldPoints, s, robotHomeWorld, rail, homeE1Mm, yPlusMm, yMinusMm, bounds, prev, poseOk, e1, out int singleGlides))
            return new LayerRailPlan(e1, singleGlides);

        float current = prev;
        int i = 0;
        int glides = 0;
        int guard = 0;
        while (i < n && guard++ < n + 2)
        {
            int fail = i;
            while (fail < n && poseOk(worldPoints[fail], current)) fail++;
            if (fail == n)
            {
                Fill(e1, i, n - 1, current);
                break;
            }

            if (TryPlaceGlide(
                    worldPoints, s, robotHomeWorld, rail, homeE1Mm, yPlusMm, yMinusMm, bounds,
                    i, fail, current, poseOk, e1, out int arrival, out float dest))
            {
                current = dest;
                glides++;
                i = arrival;
                if (i < n && !poseOk(worldPoints[i], current))
                    i = Math.Min(n, arrival + 1);
                continue;
            }

            float step = ClosestFeasible(worldPoints[fail], current, robotHomeWorld, rail, homeE1Mm, yPlusMm, yMinusMm, bounds, poseOk);
            Fill(e1, i, fail - 1, current);
            e1[fail] = step;
            current = step;
            glides++;
            i = fail + 1;
        }

        return new LayerRailPlan(e1, glides);
    }

    /// <summary>
    /// If the next layer starts at a different E1, pull that change back into the
    /// tail of <paramref name="previousE1"/> as one path-length ramp. Refuses when
    /// any sample on that ramp would be unreachable or singular.
    /// </summary>
    public static bool TryPullGlideBack(
        float[] previousE1,
        IReadOnlyList<Vector3> previousPoints,
        float targetE1,
        Func<Vector3, float, bool> poseOk)
    {
        ArgumentNullException.ThrowIfNull(poseOk);
        int n = previousPoints.Count;
        if (n < 2 || previousE1.Length != n) return false;
        if (MathF.Abs(previousE1[^1] - targetE1) < 0.5f) return false;

        var s = PathLength(previousPoints);
        int end = n - 1;
        for (int start = 0; start < end; start++)
        {
            if (!RampOk(previousPoints, s, start, end, previousE1[start], targetE1, poseOk))
                continue;
            WriteRamp(previousE1, s, start, end, previousE1[start], targetE1);
            return true;
        }
        return false;
    }

    readonly record struct AllowanceBounds(float Lo, float Hi, float RailMin, float RailMax);

    static AllowanceBounds Allowance(RobotRailCellConfig rail, float homeE1Mm, float yPlusMm, float yMinusMm)
    {
        var (rMin, rMax) = RailEnvelope(rail);
        float lo = MathF.Max(rMin, homeE1Mm - MathF.Max(0f, yMinusMm));
        float hi = MathF.Min(rMax, homeE1Mm + MathF.Max(0f, yPlusMm));
        if (lo > hi) (lo, hi) = (hi, lo);
        return new AllowanceBounds(lo, hi, rMin, rMax);
    }

    static float[] PathLength(IReadOnlyList<Vector3> pts)
    {
        var s = new float[pts.Count];
        for (int i = 1; i < pts.Count; i++)
            s[i] = s[i - 1] + Vector3.Distance(pts[i], pts[i - 1]);
        return s;
    }

    static bool CoversAll(IReadOnlyList<Vector3> pts, float e1, Func<Vector3, float, bool> poseOk)
    {
        for (int i = 0; i < pts.Count; i++)
            if (!poseOk(pts[i], e1)) return false;
        return true;
    }

    static void Fill(float[] e1, int from, int to, float value)
    {
        for (int i = Math.Max(0, from); i <= to && i < e1.Length; i++)
            e1[i] = value;
    }

    static bool TryBestSingleCover(
        IReadOnlyList<Vector3> pts, float[] s,
        Vector3 home, RobotRailCellConfig rail,
        float homeE1, float yPlus, float yMinus, AllowanceBounds bounds,
        float prev, Func<Vector3, float, bool> poseOk,
        float[] e1, out int glides)
    {
        glides = 0;
        float? cover = BestSingleCover(pts, home, rail, homeE1, yPlus, yMinus, bounds, prev, poseOk);
        if (cover is not float dest) return false;

        int n = pts.Count;
        int fail = 0;
        while (fail < n && poseOk(pts[fail], prev)) fail++;

        // Slowest in-layer ramp that still arrives before prev stops reaching.
        if (fail > 0 && fail < n)
        {
            int end = n - 1;
            int start = -1;
            for (int candidateEnd = end; candidateEnd >= fail && start < 0; candidateEnd--)
            {
                for (int a = 0; a < fail; a++)
                {
                    if (!RampOk(pts, s, a, candidateEnd, prev, dest, poseOk)) continue;
                    start = a;
                    end = candidateEnd;
                    break;
                }
            }
            if (start >= 0)
            {
                Fill(e1, 0, start - 1, prev);
                WriteRamp(e1, s, start, end, prev, dest);
                Fill(e1, end + 1, n - 1, dest);
                glides = 1;
                return true;
            }
        }

        Array.Fill(e1, dest);
        return true;
    }

    static float? BestSingleCover(
        IReadOnlyList<Vector3> pts, Vector3 home, RobotRailCellConfig rail,
        float homeE1, float yPlus, float yMinus, AllowanceBounds bounds,
        float prev, Func<Vector3, float, bool> poseOk)
    {
        float best = float.NaN;
        float bestDist = float.MaxValue;
        foreach (float sample in Samples(bounds.Lo, bounds.Hi, 25f))
        {
            float e = ClampToAllowance(sample, homeE1, yPlus, yMinus, bounds.RailMin, bounds.RailMax);
            if (!CoversAll(pts, e, poseOk)) continue;
            float dist = MathF.Abs(e - prev);
            if (dist < bestDist) { bestDist = dist; best = e; }
        }
        if (float.IsNaN(best)) return null;
        return WalkToward(best, prev, bounds, homeE1, yPlus, yMinus, e => CoversAll(pts, e, poseOk));
    }

    static bool TryPlaceGlide(
        IReadOnlyList<Vector3> pts, float[] s,
        Vector3 home, RobotRailCellConfig rail,
        float homeE1, float yPlus, float yMinus, AllowanceBounds bounds,
        int segmentStart, int fail, float current,
        Func<Vector3, float, bool> poseOk,
        float[] e1, out int arrival, out float dest)
    {
        arrival = fail;
        dest = current;
        int n = pts.Count;
        int furthest = fail;
        float furthestGap = -1f;
        int look = Math.Max(1, (n - fail) / 24);
        for (int i = fail; i < n; i += look)
        {
            float feas = ClosestFeasible(pts[i], current, home, rail, homeE1, yPlus, yMinus, bounds, poseOk);
            float gap = MathF.Abs(feas - current);
            if (gap > furthestGap) { furthestGap = gap; furthest = i; }
        }
        {
            float feas = ClosestFeasible(pts[^1], current, home, rail, homeE1, yPlus, yMinus, bounds, poseOk);
            if (MathF.Abs(feas - current) > furthestGap) furthest = n - 1;
        }

        int stride = n <= 64 ? 1 : Math.Max(1, (furthest - fail) / 16);
        int bestEnd = -1;
        int bestStart = -1;
        float bestDest = current;
        float bestTravel = float.MaxValue;

        for (int target = furthest; target >= fail; target -= stride)
        {
            foreach (float raw in TargetCandidates(pts[target], current, home, rail, homeE1, yPlus, yMinus, bounds, poseOk))
            {
                for (int end = target; end >= fail; end -= stride)
                {
                    if (!poseOk(pts[end], raw)) continue;
                    int start = EarliestStart(pts, s, segmentStart, fail, end, current, raw, poseOk);
                    if (start < 0) continue;
                    float travel = MathF.Abs(raw - current);
                    bool better = end > bestEnd || (end == bestEnd && travel < bestTravel);
                    if (!better) continue;
                    bestEnd = end;
                    bestStart = start;
                    bestDest = raw;
                    bestTravel = travel;
                    if (stride == 1 && end == furthest) goto placed;
                }
            }
            if (target == fail) break;
        }

        if (bestStart < 0) return false;

        placed:
        // Refine to the latest arrival and earliest departure on this destination.
        int refinedEnd = bestEnd;
        for (int end = Math.Min(n - 1, bestEnd + stride); end > bestEnd; end--)
        {
            int start = EarliestStart(pts, s, segmentStart, fail, end, current, bestDest, poseOk);
            if (start < 0) continue;
            refinedEnd = end;
            bestStart = start;
            break;
        }
        int refinedStart = bestStart;
        for (int start = segmentStart; start < bestStart; start++)
        {
            if (!RampOk(pts, s, start, refinedEnd, current, bestDest, poseOk)) continue;
            refinedStart = start;
            break;
        }

        Fill(e1, segmentStart, refinedStart - 1, current);
        WriteRamp(e1, s, refinedStart, refinedEnd, current, bestDest);
        arrival = refinedEnd;
        dest = bestDest;
        return true;
    }

    static int EarliestStart(
        IReadOnlyList<Vector3> pts, float[] s,
        int segmentStart, int fail, int end,
        float fromE, float toE, Func<Vector3, float, bool> poseOk)
    {
        int last = Math.Min(fail, end);
        int span = last - segmentStart;
        int step = span <= 24 ? 1 : Math.Max(1, span / 8);
        int found = -1;
        for (int start = segmentStart; start <= last; start += step)
        {
            if (start == end)
                return poseOk(pts[end], toE) ? start : -1;
            if (!RampOk(pts, s, start, end, fromE, toE, poseOk)) continue;
            found = start;
            break;
        }
        if (found < 0) return -1;
        int refineFrom = Math.Max(segmentStart, found - step + 1);
        for (int start = refineFrom; start < found; start++)
        {
            if (start == end) break;
            if (RampOk(pts, s, start, end, fromE, toE, poseOk))
                return start;
        }
        return found;
    }

    static IEnumerable<float> TargetCandidates(
        Vector3 point, float current, Vector3 home, RobotRailCellConfig rail,
        float homeE1, float yPlus, float yMinus, AllowanceBounds bounds,
        Func<Vector3, float, bool> poseOk)
    {
        var yielded = new HashSet<int>();
        var list = new List<float>(4);
        void Add(float raw)
        {
            float e = ClampToAllowance(raw, homeE1, yPlus, yMinus, bounds.RailMin, bounds.RailMax);
            if (!poseOk(point, e)) return;
            int key = (int)MathF.Round(e);
            if (!yielded.Add(key)) return;
            list.Add(e);
        }

        Add(ClosestFeasible(point, current, home, rail, homeE1, yPlus, yMinus, bounds, poseOk));
        Add(IdealE1(point, home, rail, homeE1, yPlus, yMinus));
        Add((bounds.Lo + bounds.Hi) * 0.5f);
        return list;
    }

    static float ClosestFeasible(
        Vector3 point, float current, Vector3 home, RobotRailCellConfig rail,
        float homeE1, float yPlus, float yMinus, AllowanceBounds bounds,
        Func<Vector3, float, bool> poseOk)
    {
        float best = float.NaN;
        float bestDist = float.MaxValue;
        foreach (float sample in Samples(bounds.Lo, bounds.Hi, 10f))
        {
            float e = ClampToAllowance(sample, homeE1, yPlus, yMinus, bounds.RailMin, bounds.RailMax);
            if (!poseOk(point, e)) continue;
            float dist = MathF.Abs(e - current);
            if (dist < bestDist) { bestDist = dist; best = e; }
        }
        if (float.IsNaN(best))
            return IdealE1(point, home, rail, homeE1, yPlus, yMinus);
        return WalkToward(best, current, bounds, homeE1, yPlus, yMinus, e => poseOk(point, e));
    }

    static float WalkToward(
        float from, float toward, AllowanceBounds bounds,
        float homeE1, float yPlus, float yMinus, Func<float, bool> ok)
    {
        float dir = MathF.Sign(toward - from);
        if (dir == 0f) return from;
        float refined = from;
        for (float step = 1f; step <= MathF.Abs(toward - from); step += 1f)
        {
            float e = ClampToAllowance(from + dir * step, homeE1, yPlus, yMinus, bounds.RailMin, bounds.RailMax);
            if (!ok(e)) break;
            refined = e;
            if (MathF.Abs(e - toward) <= 1f) break;
        }
        return refined;
    }

    static IEnumerable<float> Samples(float lo, float hi, float step)
    {
        if (hi - lo < 1f)
        {
            yield return lo;
            yield break;
        }
        step = MathF.Max(1f, step);
        for (float e = lo; e <= hi + 0.1f; e += step)
            yield return MathF.Min(e, hi);
    }

    const float RampSampleMm = 20f;

    static bool RampOk(
        IReadOnlyList<Vector3> pts, float[] s,
        int start, int end, float fromE, float toE,
        Func<Vector3, float, bool> poseOk)
    {
        if (start < 0 || end >= pts.Count || start > end) return false;
        if (!poseOk(pts[end], toE)) return false;
        float s0 = s[start];
        float span = s[end] - s0;
        if (span < 1f)
            return poseOk(pts[start], fromE);
        for (float d = 0f; d <= span + 0.01f; d += RampSampleMm)
        {
            float u = MathF.Min(d, span);
            float e = fromE + (toE - fromE) * (u / span);
            if (!poseOk(PointAt(pts, s, s0 + u), e)) return false;
        }
        return true;
    }

    static void WriteRamp(float[] e1, float[] s, int start, int end, float fromE, float toE)
    {
        float s0 = s[start];
        float span = s[end] - s0;
        for (int i = start; i <= end; i++)
        {
            float u = span < 1f ? (i == end ? 1f : 0f) : (s[i] - s0) / span;
            e1[i] = fromE + (toE - fromE) * Math.Clamp(u, 0f, 1f);
        }
    }

    static Vector3 PointAt(IReadOnlyList<Vector3> pts, float[] s, float dist)
    {
        if (dist <= 0f) return pts[0];
        if (dist >= s[^1]) return pts[^1];
        int i = 1;
        while (i < pts.Count - 1 && s[i] < dist) i++;
        float seg = s[i] - s[i - 1];
        float t = seg < 1e-3f ? 1f : (dist - s[i - 1]) / seg;
        return Vector3.Lerp(pts[i - 1], pts[i], Math.Clamp(t, 0f, 1f));
    }
}
