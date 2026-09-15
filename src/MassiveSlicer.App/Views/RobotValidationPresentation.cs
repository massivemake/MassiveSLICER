namespace MassiveSlicer.App.Views;

/// <summary>
/// Pure presentation rules for a completed robot-validation pass.
/// Kept out of the viewport code-behind so a later clean pass cannot leave a
/// prior failure banner / export gate in place, and so the rules can be tested
/// without Avalonia.
/// </summary>
public static class RobotValidationPresentation
{
    public readonly record struct Counts(
        int Unreachable,
        int Singular,
        int Collisions,
        int TotalMoves,
        int CollisionStride,
        float ZLo,
        float ZHi);

    /// <summary>
    /// True when the latest completed pass has any operator-facing issue
    /// (unreachable, residual singularity after repair, or predicted collision).
    /// </summary>
    public static bool HasIssues(in Counts c)
        => c.Unreachable + c.Singular + c.Collisions > 0;

    /// <summary>
    /// Export / Send confirm is the unreachable + residual-singularity gate.
    /// Collision-only paths still raise the red slice-status banner.
    /// </summary>
    public static bool BlocksExport(int unreachable, int singular)
        => unreachable + singular > 0;

    public static bool BlocksExport(in Counts c)
        => BlocksExport(c.Unreachable, c.Singular);

    /// <summary>
    /// A fully clean pass must replace any prior robot-validation error UI.
    /// </summary>
    public static bool ShouldClearErrorUi(in Counts c) => !HasIssues(c);

    public static bool IsRobotValidationError(string? message)
        => !string.IsNullOrWhiteSpace(message)
           && message.Contains("⚠ Robot validation:", StringComparison.Ordinal);

    public static string ReachabilityLabel(in Counts c)
    {
        string label = c.Unreachable == 0
            ? $"All {c.TotalMoves} reachable"
            : $"{c.Unreachable} / {c.TotalMoves} unreachable";
        if (c.Collisions > 0)
            label += c.CollisionStride > 1
                ? $" · {c.Collisions:N0} collision (sampled 1/{c.CollisionStride})"
                : $" · {c.Collisions:N0} collision";
        return label;
    }

    public static string ErrorSliceStatus(in Counts c, string collisionDetail = "")
    {
        string collPart = c.Collisions > 0
            ? $" and {c.Collisions:N0} predicted collision moves{collisionDetail}"
            : "";
        string zPart = c.ZLo <= c.ZHi ? $" between Z {c.ZLo:0} and {c.ZHi:0} mm" : "";
        return $"⚠ Robot validation: {c.Singular:N0} singularity-risk, {c.Unreachable:N0} unreachable{collPart}{zPart}" +
               " — the robot may fault or crash mid-print.";
    }

    /// <summary>
    /// Non-error footer text for a clean pass. Replaces a prior red banner and
    /// updates the status bar so operators see the latest verdict, not the last fault.
    /// </summary>
    public static string CleanSliceStatus(in Counts c)
        => $"Robot validation: {ReachabilityLabel(c)}.";

    public static int FirstIssueIndex(bool[] reachable, bool[] singularity, bool[]? collision)
    {
        int n = reachable.Length;
        for (int i = 0; i < n; i++)
        {
            if (!reachable[i]) return i;
            if (i < singularity.Length && singularity[i]) return i;
            if (collision is not null && i < collision.Length && collision[i]) return i;
        }
        return -1;
    }

    /// <summary>
    /// Cancelled or superseded passes must not publish counts, banners, or export gates.
    /// </summary>
    public static bool ShouldPublishCompletedPass(bool cancelled, bool isCurrentRun)
        => !cancelled && isCurrentRun;
}
