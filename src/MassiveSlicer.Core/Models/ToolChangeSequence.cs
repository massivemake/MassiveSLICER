namespace MassiveSlicer.Core.Models;

public enum KrlMoveKind { Lin, Ptp }

public sealed record KrlOutputAnnotation(int Index, bool State);

public sealed record KrlWaitAnnotation(string Type, string? Expr, double? Seconds);

/// <summary>One motion step from a KUKA tool-change .src program.</summary>
public sealed record ToolChangeWaypoint(
    string Name,
    string Kind,
    float X, float Y, float Z, float A, float B, float C,
    float[]? Joint,
    KrlMoveKind Move,
    string? ToolType,
    IReadOnlyList<KrlOutputAnnotation> Outputs,
    IReadOnlyList<KrlWaitAnnotation> Waits);

public sealed record ToolChangeSequenceDef(
    string Id,
    string Label,
    string ToolKey,
    string CellToolName);

public sealed record ToolChangeSequence(
    ToolChangeSequenceDef Definition,
    IReadOnlyList<ToolChangeWaypoint> Waypoints);

/// <summary>Resolved waypoint in robroot-relative mm (for 3D markers and labels).</summary>
public sealed record ResolvedToolChangeWaypoint(
    System.Numerics.Vector3 Position,
    ToolChangeWaypoint Waypoint,
    int Index);

/// <summary>Densified arc-length path for viewport playback.</summary>
public sealed class ToolChangeSequencePath
{
    public required IReadOnlyList<System.Numerics.Vector3> DensePoints { get; init; }
    public required IReadOnlyList<float> CumulativeLength { get; init; }
    public required IReadOnlyList<KrlMoveKind> SegmentMoves { get; init; }
    public required IReadOnlyList<int> WaypointAtDenseIndex { get; init; }
    public required float TotalLength { get; init; }
    public ToolChangeToolEvent? ToolEvent { get; init; }
    public required IReadOnlyList<ResolvedToolChangeWaypoint> ResolvedWaypoints { get; init; }
}

public sealed record ToolChangeToolEvent(
    float Fraction,
    string CellToolName,
    bool Attach);