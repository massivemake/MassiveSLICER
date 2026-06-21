namespace MassiveSlicer.Core.Models;

/// <summary>
/// Volumetric vs surface-focused slicing strategy (planar / angled methods).
/// </summary>
public enum SlicingMode
{
    /// <summary>Full solid cross-sections: inset shells and optional infill.</summary>
    Normal,

    /// <summary>Surface/cladding paths on slice intersections (vertical tool unless overhang orientation is on).</summary>
    Surface,
}