using System.Numerics;

namespace MassiveSlicer.Core.Models;

public enum MoveKind { Extrude, Travel }

/// <summary>A single move segment in a toolpath -- from one point to another with a deposition intent.</summary>
public sealed record ToolpathMove(Vector3 From, Vector3 To, MoveKind Kind)
{
    public Vector3 Normal        { get; init; } = Vector3.Zero;  // Zero = use layer PlaneNormal (KRL exporter fallback)
    /// <summary>True when this travel move crosses a layer boundary (triggers ;layer change in KRL).</summary>
    public bool    IsLayerChange { get; init; } = false;
    /// <summary>True when this extrude move stitches the end of one layer to the start of the next
    /// (XY gap below the layer-change travel threshold). Post-processing effects should skip these.</summary>
    public bool    IsLayerStitch { get; init; } = false;
}
