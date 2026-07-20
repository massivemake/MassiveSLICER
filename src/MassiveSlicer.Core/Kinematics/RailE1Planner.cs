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
        return ClampToAllowance(ideal, homeE1Mm, yPlusMm, yMinusMm, rail.MinMm, rail.MaxMm);
    }

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
        float lo = MathF.Max(rail.MinMm, homeE1Mm - MathF.Max(0f, yMinusMm));
        float hi = MathF.Min(rail.MaxMm, homeE1Mm + MathF.Max(0f, yPlusMm));
        if (lo > hi) (lo, hi) = (hi, lo);

        gridCount = Math.Clamp(gridCount, 3, 21);
        var set = new HashSet<float>();
        set.Add(ClampToAllowance(homeE1Mm, homeE1Mm, yPlusMm, yMinusMm, rail.MinMm, rail.MaxMm));
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

        float prev = homeE1Mm;
        for (int i = 0; i < n; i++)
        {
            float pick = PickBestE1(
                worldPoints[i], robotHomeWorld, rail, homeE1Mm, yPlusMm, yMinusMm,
                prev, preferredHorizReachMm, inWorkspace, gridCount);
            // Blend toward pick so rail doesn't step-jump every bead
            float blended = SmoothToward(prev, pick, smoothBlend);
            e1[i] = ClampToAllowance(blended, homeE1Mm, yPlusMm, yMinusMm, rail.MinMm, rail.MaxMm);
            prev = e1[i];
        }

        // Forward-backward smooth pass to reduce residual chatter
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 1; i < n; i++)
                e1[i] = ClampToAllowance(
                    0.65f * e1[i] + 0.35f * e1[i - 1],
                    homeE1Mm, yPlusMm, yMinusMm, rail.MinMm, rail.MaxMm);
            for (int i = n - 2; i >= 0; i--)
                e1[i] = ClampToAllowance(
                    0.65f * e1[i] + 0.35f * e1[i + 1],
                    homeE1Mm, yPlusMm, yMinusMm, rail.MinMm, rail.MaxMm);
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
}
