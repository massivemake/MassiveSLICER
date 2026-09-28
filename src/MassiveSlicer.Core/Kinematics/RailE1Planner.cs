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

        e1 = PlanCorridor(worldPoints, s, prev, bounds, homeE1Mm, yPlusMm, yMinusMm, poseOk);
        // A glide is one run of rail motion in one direction; a hold or a reversal ends it.
        int glides = 0, dir = 0;
        for (int i = 1; i < n; i++)
        {
            float d = e1[i] - e1[i - 1];
            int nd = MathF.Abs(d) <= 0.01f ? 0 : MathF.Sign(d);
            if (nd != 0 && nd != dir) glides++;
            dir = nd;
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

    /// <summary>Path spacing (mm) of the E1 band samples a corridor plan is built on.</summary>
    const float BandKeyMm = 25f;

    /// <summary>How far (mm) each side of its first passing E1 a band is measured.</summary>
    const float BandReachMm = 400f;

    /// <summary>
    /// Rail for a layer one E1 cannot hold. At samples along the path, find the band of E1
    /// that passes; the rail is then the shortest path through those bands (a taut string).
    /// It moves only where a band forces it, in straight constant-speed runs, and turns back
    /// only where a later point needs it. Points between samples are checked afterwards and
    /// become samples themselves if they fail.
    /// </summary>
    /// <remarks>
    /// Replaces a greedy search that glided to whichever destination stayed passable longest
    /// â€” usually the carriage straight across from a far point â€” and swung the rail up to
    /// 2 m per layer on a 4.1 m part where a few hundred mm was enough.
    /// </remarks>
    static float[] PlanCorridor(
        IReadOnlyList<Vector3> pts, float[] s, float start,
        AllowanceBounds bounds, float homeE1, float yPlus, float yMinus,
        Func<Vector3, float, bool> poseOk)
    {
        int n = pts.Count;
        var keys = new SortedSet<int> { 0, n - 1 };
        float next = BandKeyMm;
        for (int i = 1; i < n; i++)
            if (s[i] >= next) { keys.Add(i); next = s[i] + BandKeyMm; }

        var e1 = new float[n];
        for (int round = 0; round < 4; round++)
        {
            var idx = keys.ToArray();
            var lo = new float[idx.Length];
            var hi = new float[idx.Length];
            var has = new bool[idx.Length];
            float reference = start;
            for (int k = 0; k < idx.Length; k++)
            {
                if (!Band(pts[idx[k]], reference, bounds, homeE1, yPlus, yMinus, poseOk, out lo[k], out hi[k]))
                    continue;
                has[k] = true;
                reference = Math.Clamp(reference, lo[k], hi[k]);
            }

            var verts = TautString(idx, s, lo, hi, has, start);
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                while (v + 1 < verts.Count - 1 && verts[v + 1].S <= s[i]) v++;
                var (s0, e0) = verts[v];
                var (s1, e1v) = verts[Math.Min(v + 1, verts.Count - 1)];
                float t = s1 - s0 < 1e-3f ? 1f : Math.Clamp((s[i] - s0) / (s1 - s0), 0f, 1f);
                e1[i] = e0 + (e1v - e0) * t;
            }

            bool added = false;
            for (int i = 0; i < n; i++)
                if (!keys.Contains(i) && !poseOk(pts[i], e1[i])) { keys.Add(i); added = true; }
            if (!added) break;
        }
        SmoothSteps(pts, s, e1, poseOk);
        return e1;
    }

    /// <summary>Longest stretch of path (mm) each side of a step a smoothing ramp may use.</summary>
    const float StepRampReachMm = 600f;

    /// <summary>
    /// Where the rail moves faster than the tool between two points, spread the change over a
    /// longer straight ramp — widened a band sample at a time until every point on it passes.
    /// A band edge can be a failed IK start guess rather than a real limit, and the string
    /// then jumps across it (115 mm inside a 21 mm bead on Cow Mid). A step that no ramp
    /// within <see cref="StepRampReachMm"/> can replace is left for validation to flag.
    /// </summary>
    static void SmoothSteps(IReadOnlyList<Vector3> pts, float[] s, float[] e1, Func<Vector3, float, bool> poseOk)
    {
        int n = pts.Count;
        for (int i = 1; i < n; i++)
        {
            float ds = s[i] - s[i - 1];
            if (MathF.Abs(e1[i] - e1[i - 1]) <= MathF.Max(ds, 1f)) continue;
            for (float reach = BandKeyMm; reach <= StepRampReachMm; reach += BandKeyMm)
            {
                int a = i - 1, b = i;
                while (a > 0 && s[i - 1] - s[a - 1] <= reach) a--;
                while (b < n - 1 && s[b + 1] - s[i] <= reach) b++;
                float span = s[b] - s[a];
                if (span < 1f || MathF.Abs(e1[b] - e1[a]) > span) continue;   // still faster than the tool
                bool ok = true;
                for (int k = a + 1; k < b && ok; k++)
                    ok = poseOk(pts[k], e1[a] + (e1[b] - e1[a]) * (s[k] - s[a]) / span);
                if (!ok) continue;
                for (int k = a + 1; k < b; k++)
                    e1[k] = e1[a] + (e1[b] - e1[a]) * (s[k] - s[a]) / span;
                break;
            }
        }
    }

    /// <summary>
    /// E1 band that passes at <paramref name="point"/>: the passing E1 nearest
    /// <paramref name="reference"/>, widened each way up to <see cref="BandReachMm"/> and
    /// bisected to 1 mm at its edges. False when no E1 in the allowance passes.
    /// </summary>
    static bool Band(
        Vector3 point, float reference, AllowanceBounds bounds,
        float homeE1, float yPlus, float yMinus,
        Func<Vector3, float, bool> poseOk, out float lo, out float hi)
    {
        Func<float, bool> ok = e => poseOk(point, e);
        lo = hi = reference;
        if (NearestPassing(reference, bounds, homeE1, yPlus, yMinus, ok) is not float c) return false;
        float Clamp(float e) => ClampToAllowance(e, homeE1, yPlus, yMinus, bounds.RailMin, bounds.RailMax);
        float Edge(float dir)
        {
            float good = c;
            for (float d = SearchStepMm; d <= BandReachMm + 0.1f; d += SearchStepMm)
            {
                float e = Clamp(c + dir * d);
                if (MathF.Abs(e - good) < 0.01f) return good;   // allowance end
                if (ok(e)) { good = e; continue; }
                float bad = e;
                while (MathF.Abs(bad - good) > 1f)
                {
                    float mid = 0.5f * (good + bad);
                    if (ok(mid)) good = mid; else bad = mid;
                }
                return good;
            }
            return good;
        }
        lo = Edge(-1f);
        hi = Edge(+1f);
        return true;
    }

    /// <summary>
    /// Shortest path from (0, <paramref name="start"/>) through the bands [lo, hi] at path
    /// distances s[idx[k]] â€” funnel string-pulling in one dimension. The free end runs as
    /// flat as the last bands allow. Samples with no band are skipped.
    /// </summary>
    static List<(float S, float E)> TautString(
        int[] idx, float[] s, float[] lo, float[] hi, bool[] has, float start)
    {
        int m = idx.Length;
        float sa = s[idx[0]];
        float ea = has[0] ? Math.Clamp(start, lo[0], hi[0]) : start;
        var verts = new List<(float S, float E)> { (sa, ea) };
        float su = float.PositiveInfinity, sl = float.NegativeInfinity;
        int iu = -1, il = -1;
        for (int j = 1; j < m; j++)
        {
            if (!has[j]) continue;
            float ds = s[idx[j]] - sa;
            if (ds < 1e-3f) continue;
            float mh = (hi[j] - ea) / ds, ml = (lo[j] - ea) / ds;
            if (mh < sl)
            {
                sa = s[idx[il]]; ea = lo[il]; verts.Add((sa, ea));
                j = il; su = float.PositiveInfinity; sl = float.NegativeInfinity; iu = il = -1;
                continue;
            }
            if (ml > su)
            {
                sa = s[idx[iu]]; ea = hi[iu]; verts.Add((sa, ea));
                j = iu; su = float.PositiveInfinity; sl = float.NegativeInfinity; iu = il = -1;
                continue;
            }
            if (mh < su) { su = mh; iu = j; }
            if (ml > sl) { sl = ml; il = j; }
        }
        float slope = Math.Clamp(0f, sl, su);
        float sEnd = s[idx[m - 1]];
        if (sEnd > sa) verts.Add((sEnd, ea + slope * (sEnd - sa)));
        return verts;
    }

    /// <summary>
    /// Nearest passing E1 to <paramref name="center"/> on a <see cref="SearchStepMm"/> grid,
    /// searched outward so it stops at the first hit. Scanning the whole allowance and
    /// keeping the closest gives the same answer at ~100× the IK solves.
    /// </summary>
    static float? NearestPassing(
        float center, AllowanceBounds bounds,
        float homeE1, float yPlus, float yMinus, Func<float, bool> ok)
    {
        float Clamp(float e) => ClampToAllowance(e, homeE1, yPlus, yMinus, bounds.RailMin, bounds.RailMax);
        float c = Clamp(center);
        if (ok(c)) return c;
        bool loDone = false, hiDone = false;
        for (int k = 1; !(loDone && hiDone); k++)
        {
            if (!loDone)
            {
                float e = c - k * SearchStepMm;
                if (e <= bounds.Lo) { e = Clamp(bounds.Lo); loDone = true; }
                if (e < c && ok(e)) return e;
            }
            if (!hiDone)
            {
                float e = c + k * SearchStepMm;
                if (e >= bounds.Hi) { e = Clamp(bounds.Hi); hiDone = true; }
                if (e > c && ok(e)) return e;
            }
        }
        return null;
    }

    /// <summary>
    /// Coarse grid for the rail search. <paramref name="poseOk"/> is a full IK solve in the
    /// app, so the grid only brackets; <see cref="Band"/> bisects its edges to 1 mm.
    /// </summary>
    const float SearchStepMm = 40f;

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
