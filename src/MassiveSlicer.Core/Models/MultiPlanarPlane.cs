namespace MassiveSlicer.Core.Models;

/// <summary>One Multi-Planar guide plane. Both tilts apply at once.
/// <paramref name="AngleY"/> leans along X. <paramref name="AngleX"/> leans along Y.</summary>
public sealed record MultiPlanarPlane(float HeightPct, float AngleY, float AngleX = 0);
