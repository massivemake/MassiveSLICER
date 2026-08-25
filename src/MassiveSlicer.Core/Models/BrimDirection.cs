namespace MassiveSlicer.Core.Models;

/// <summary>
/// Which side of the first-layer path the brim loops sit on.
///
/// <para>Offsetting the footprint outward produces ONE boundary that runs down BOTH sides of
/// the path — measured on a real open wall it comes back 2.01x the path length, because it
/// travels out along one side, round the end, and back along the other. So "inside" is not a
/// second calculation: it is the stretch of that same boundary lying on the other side.</para>
///
/// <para>Side is taken relative to the path's direction of travel, so it stays on one side for
/// the whole run. A rule based on concavity would switch sides wherever the curvature flips —
/// on an S-shaped wall that hands you a brim that crosses the wall halfway along.</para>
///
/// <para>The same rule covers closed shapes: for a wall loop one side IS the bore and the other
/// IS the outside, so tubes and columns fall out of it without a special case.</para>
/// </summary>
public enum BrimDirection
{
    /// <summary>The outer side only — the longer side of the offset boundary. The default.</summary>
    Outside,

    /// <summary>The inner side only: the concave side of an open wall, or the bore of a closed one.</summary>
    Inside,

    /// <summary>Both sides — the whole offset boundary.</summary>
    Both,
}
