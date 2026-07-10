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

/// <summary>One painted brush dab in WORLD space. World-space (not move indices)
/// so marks survive re-slices and setting changes; they persist with the
/// workspace.</summary>
public sealed record PaintMark(Vector3 Center, float Radius, PaintMarkKind Kind);
