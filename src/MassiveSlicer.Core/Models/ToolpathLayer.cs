namespace MassiveSlicer.Core.Models;

/// <summary>One horizontal layer of a sliced toolpath.</summary>
public sealed class ToolpathLayer
{
    /// <summary>Zero-based layer index (0 = first/bottom layer).</summary>
    public int Index { get; }

    /// <summary>Z height of this layer in mm.</summary>
    public float Z { get; }

    /// <summary>All move segments in this layer, in order of execution.</summary>
    public List<ToolpathMove> Moves { get; } = [];

    public ToolpathLayer(int index, float z)
    {
        Index = index;
        Z     = z;
    }
}
