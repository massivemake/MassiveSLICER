namespace MassiveSlicer.Core.Models;

/// <summary>One Multi-Planar guide plane: tilt (deg) anchored at a height along the part.</summary>
public sealed record MultiPlanarPlane(float HeightPct, float AngleDeg);
