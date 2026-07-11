using System.Numerics;

namespace MassiveSlicer.Core.Models;

public enum PaintMarkKind
{
    /// <summary>Beads under this mark need support: the Formbound planner grows
    /// fingers beneath them (manual demand, mesh-oracle veto bypassed — the user
    /// explicitly asked).</summary>
    Bridge,

    /// <summary>Beads inside this mark are removed from the toolpath (artifact
    /// cleanup / manual deletions); the gap is spliced with a travel.</summary>
    Remove,
}

/// <summary>
/// Role of a Bridge mark when steering a paint-driven Formbound column.
/// </summary>
public enum PaintBridgeRole
{
    /// <summary>Generic / legacy Bridge demand (treated as support-bar samples).</summary>
    None = 0,

    /// <summary>
    /// Samples along the Support selection — open a full-width T bar at this height.
    /// </summary>
    SupportBar = 1,

    /// <summary>
    /// Single foot at the mid of the bridge Target — one perimeter mouth; column
    /// rises from here. Never opens additional perimeter breaks.
    /// </summary>
    ColumnFoot = 2,
}

/// <summary>One painted brush dab in WORLD space. World-space (not move indices)
/// so marks survive re-slices and setting changes; they persist with the
/// workspace.</summary>
public sealed record PaintMark(
    Vector3 Center,
    float Radius,
    PaintMarkKind Kind,
    PaintBridgeRole BridgeRole = PaintBridgeRole.None);

/// <summary>
/// Plane-local manual demand for one slicing layer: support-bar samples (T width)
/// and optional column-foot (single mouth / aim).
/// </summary>
public sealed class ManualDemandLayer
{
    public List<Vector2> SupportBar { get; } = [];
    public List<Vector2> ColumnFoot { get; } = [];
    public bool HasAny => SupportBar.Count > 0 || ColumnFoot.Count > 0;
}
