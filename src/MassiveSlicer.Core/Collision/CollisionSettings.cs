namespace MassiveSlicer.Core.Collision;

/// <summary>Thresholds and toggles for the per-move digital-twin collision check.</summary>
public sealed record CollisionSettings
{
    public bool CheckEnvironment { get; init; } = true;
    public bool CheckSelf { get; init; } = true;
    public bool CheckMaterial { get; init; } = true;

    /// <summary>Robot-vs-environment clearance (mm) — below this flags a collision.</summary>
    public float ClearanceMm { get; init; } = 10f;

    /// <summary>Self-collision clearance (mm). 0 = flag contact/penetration only.</summary>
    public float SelfClearanceMm { get; init; } = 0f;

    /// <summary>Robot-vs-deposited-material clearance (mm) — tight; the toolhead
    /// legitimately flies close over the part.</summary>
    public float MaterialClearanceMm { get; init; } = 2f;

    /// <summary>Beads within this radius of the current TCP are skipped (the bead
    /// under the nozzle and the previous layer directly below it).</summary>
    public float NozzleExclusionRadiusMm { get; init; } = 40f;

    /// <summary>The most recent N moves' beads are invisible to the material check.</summary>
    public int RecentMoveSkip { get; init; } = 30;

    /// <summary>Material at or below the nozzle plane is NEVER an obstacle — the
    /// extruder always hovers just above what it printed, by construction. Only
    /// beads whose top rises above <c>nozzleZ + this</c> (a tall column beside a
    /// lower region) can collide with the tool/arm body.</summary>
    public float MaterialZToleranceMm { get; init; } = 2f;

    /// <summary>Soft budget for the whole per-toolpath sweep; the checker degrades
    /// to sampling every Kth move when the estimate exceeds this.</summary>
    public int TimeBudgetMs { get; init; } = 2000;

    /// <summary>Pose-delta gate: skip the full check when every joint moved less
    /// than this since the last checked move…</summary>
    public float JointDeltaGateDeg { get; init; } = 0.5f;

    /// <summary>…and the TCP moved less than this (mm).</summary>
    public float TcpDeltaGateMm { get; init; } = 5f;
}

/// <summary>Which pair produced a collision flag.</summary>
public enum CollisionClass { Environment, Self, Material }

public readonly record struct CollisionHit(CollisionClass Class, int Link, string Other);
