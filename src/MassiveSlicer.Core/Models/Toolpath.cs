using MassiveSlicer.Core.Slicing.Lightning;

namespace MassiveSlicer.Core.Models;

/// <summary>Complete sliced toolpath -- ordered list of layers from bottom to top.</summary>
public sealed class Toolpath
{
    public List<ToolpathLayer> Layers { get; } = [];

    /// <summary>Formbound planner diagnostics when a Formbound pattern was sliced; null otherwise.</summary>
    public FormboundPlanStats? FormboundStats { get; set; }

    /// <summary>
    /// User-facing slicer warnings (settings/geometry mismatches that changed how the
    /// part was sliced). Post-processors rebuild the toolpath, so callers that need
    /// these must capture them from the slicer's direct output and re-stamp (same
    /// pattern as <see cref="FormboundStats"/>).
    /// </summary>
    public List<string> Warnings { get; } = [];
}
