using MassiveSlicer.Core.Models;

namespace MassiveSlicer.App;

/// <summary>UI-thread snapshot of workspace content before background toolpath serialization.</summary>
internal sealed class WorkspaceCaptureState
{
    public required WorkspaceDocument Document { get; init; }
    public List<(WorkspaceToolpathEntry Entry, Toolpath RawToolpath)> ToolpathEntries { get; } = [];
}