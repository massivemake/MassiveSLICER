using System.Numerics;

namespace MassiveSlicer.Core.Models;

/// <summary>How a detected opening is machined.</summary>
public enum DrillCutKind
{
    /// <summary>A straight round hole. Plunge, or bore a circle when it is wider than the tool.</summary>
    Round,
    /// <summary>A large flat-bottomed diameter down to a shoulder, then a smaller through hole.</summary>
    Counterbore,
    /// <summary>A cone at the mouth, then a smaller through hole.</summary>
    Countersink,
    /// <summary>A square, polygon, slot, or other outline. The tool follows that shape.</summary>
    Profile,
}

/// <summary>
/// One drill the user chose. Position is the mouth. Axis points out of the surface.
/// Depth is the plunge length into the material, already including any breakthrough
/// the caller wants. Diameter is the finished size at the mouth. A counterbore or
/// countersink also carries the through-hole diameter and how deep the large end goes.
/// A profile carries the rim the tool follows.
/// </summary>
public readonly record struct DrillPoint(
    Vector3 Position,
    Vector3 Axis,
    float DepthMm,
    float DiameterMm,
    DrillCutKind Cut = DrillCutKind.Round,
    float MinorDiameterMm = 0f,
    float ShoulderDepthMm = 0f,
    IReadOnlyList<Vector3>? Profile = null);
