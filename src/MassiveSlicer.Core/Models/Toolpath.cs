using MassiveSlicer.Core.Slicing.Lightning;

namespace MassiveSlicer.Core.Models;

/// <summary>Complete sliced toolpath -- ordered list of layers from bottom to top.</summary>
public sealed class Toolpath
{
    public List<ToolpathLayer> Layers { get; } = [];

    /// <summary>Formbound planner diagnostics when a Formbound pattern was sliced; null otherwise.</summary>
    public FormboundPlanStats? FormboundStats { get; set; }
}
