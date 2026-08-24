namespace MassiveSlicer.Core.Models;

/// <summary>
/// Which side of the first-layer footprint the brim loops sit on.
///
/// <para>Both families come out of the same offset pass: growing the footprint by
/// (k − ½) bead widths moves the outer boundary OUT by that much and pushes every hole
/// boundary the same distance INTO its void. So an inward loop is not a second
/// calculation — it is the ring the outward pass already produced and used to throw away.</para>
/// </summary>
public enum BrimDirection
{
    /// <summary>Loops outside the footprint only. Interior holes get nothing. The original behaviour.</summary>
    Outward,

    /// <summary>Loops inside interior holes only. The outer boundary gets nothing.</summary>
    Inward,

    /// <summary>Both — outside the footprint and inside every interior hole.</summary>
    Both,
}
