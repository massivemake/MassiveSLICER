namespace MassiveSlicer.Core.Models;

/// <summary>Complete sliced toolpath -- ordered list of layers from bottom to top.</summary>
public sealed class Toolpath
{
    public List<ToolpathLayer> Layers { get; } = [];
}
