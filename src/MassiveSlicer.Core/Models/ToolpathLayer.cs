using System.Numerics;

namespace MassiveSlicer.Core.Models;

/// <summary>One layer of a sliced toolpath.</summary>
public sealed class ToolpathLayer
{
    /// <summary>Zero-based layer index (0 = first/bottom layer).</summary>
    public int Index { get; }

    /// <summary>Representative Z height of this layer in mm.</summary>
    public float Z { get; }

    /// <summary>Thickness of this layer in mm (Z of this layer minus Z of the previous layer).</summary>
    public float Height { get; set; }

    /// <summary>
    /// Unit normal of the slicing plane in scene/world space.
    /// Points away from the part surface (upward for horizontal planar slicing).
    /// The tool approaches along <c>-PlaneNormal</c> so IK can maintain the
    /// correct orientation for the slice method.
    /// Defaults to <c>(0, 0, 1)</c> for horizontal planar slicing.
    /// </summary>
    public Vector3 PlaneNormal { get; init; } = Vector3.UnitZ;

    /// <summary>All move segments in this layer, in order of execution.</summary>
    public List<ToolpathMove> Moves { get; } = [];

    public ToolpathLayer(int index, float z)
    {
        Index = index;
        Z     = z;
    }
}
