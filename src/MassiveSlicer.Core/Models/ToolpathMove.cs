using System.Numerics;

namespace MassiveSlicer.Core.Models;

public enum MoveKind { Extrude, Travel, Mill }

/// <summary>A single move segment in a toolpath -- from one point to another with a deposition intent.</summary>
public sealed record ToolpathMove(Vector3 From, Vector3 To, MoveKind Kind)
{
    public Vector3 Normal        { get; init; } = Vector3.Zero;  // Zero = use layer PlaneNormal (KRL exporter fallback)
    /// <summary>True when this travel move crosses a layer boundary (triggers ;layer change in KRL).</summary>
    public bool    IsLayerChange { get; init; } = false;
    /// <summary>True when this extrude move stitches the end of one layer to the start of the next
    /// (XY gap below the layer-change travel threshold). Post-processing effects should skip these.</summary>
    public bool    IsLayerStitch { get; init; } = false;

    /// <summary>Pre-travel filament wipe extrusion segment.</summary>
    public bool  IsWipe { get; init; }

    /// <summary>RPM scale [0, 1] for wipe ramp-down (1 = full extrusion speed).</summary>
    public float WipeRpmScale { get; init; } = 1f;

    /// <summary>Post-travel resume ramp segment (stepped speed + RPM after travel).</summary>
    public bool IsResumeRamp { get; init; }

    /// <summary>Print speed scale [0, 1] for <see cref="IsResumeRamp"/> segments.</summary>
    public float ResumeSpeedScale { get; init; } = 1f;

    /// <summary>RPM scale [0, 1] for <see cref="IsResumeRamp"/> segments.</summary>
    public float ResumeRpmScale { get; init; } = 1f;

    /// <summary>Vertical or lifted component of a z-hop travel sequence.</summary>
    public bool IsZHop { get; init; }

    /// <summary>Travel inserted when merging separate toolpaths (retraction + connector).</summary>
    public bool IsMergeConnector { get; init; }

    /// <summary>Override travel speed (m/s) for this move during KRL export. Null uses global travel speed.</summary>
    public float? TravelSpeedMps { get; init; }
}
