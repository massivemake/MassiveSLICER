namespace MassiveSlicer.Core.Models;

/// <summary>
/// Serialised workspace state: cell, camera, user models, and application settings.
/// Written to <c>%AppData%\MassiveSlicer\workspace.mass</c> by the Save Workspace command.
/// </summary>
public sealed class WorkspaceDocument
{
    public int Version { get; set; } = 2;

    /// <summary>Path to the active cell JSON file.</summary>
    public string? CellPath { get; set; }

    /// <summary>Orbit camera pose at save time.</summary>
    public CameraView? Camera { get; set; }

    /// <summary>Active right-panel tab name (matches <see cref="RightPanelTab"/> enum).</summary>
    public string RightPanelTab { get; set; } = "Additive";

    /// <summary>User-imported models shown in the outliner.</summary>
    public List<WorkspaceModelEntry> Models { get; set; } = [];

    /// <summary>Snapshot of user settings at save time.</summary>
    public AppPreferences Settings { get; set; } = new();

    /// <summary>ERP project/lead this workspace is attached to, or null.</summary>
    public ErpAttachment? Erp { get; set; }

    /// <summary>
    /// Viewport UI session at save time (edit mode, tools, layer isolation, scrub).
    /// Restored on open so the file reopens exactly where the user left off.
    /// Null on workspaces saved before this field existed.
    /// </summary>
    public WorkspaceUiSession? UiSession { get; set; }
}

/// <summary>
/// Transient UI state captured with a workspace so reopen restores edit mode,
/// selected paint/path tools, and the isolated layer window.
/// </summary>
public sealed class WorkspaceUiSession
{
    /// <summary>Body / Toolpath / Speed / RPM / Preview.</summary>
    public string ViewMode { get; set; } = "Body";

    /// <summary>Whether the toolpath Edit toolbar was open.</summary>
    public bool IsPaintEditOpen { get; set; }

    public bool PaintHandActive { get; set; }
    public bool PaintBoxSelectActive { get; set; }
    public bool PaintBridgeActive { get; set; }
    public bool PaintRemoveActive { get; set; }
    public bool PaintLineBridgeActive { get; set; }
    public bool PaintLineRemoveActive { get; set; }

    /// <summary>"Path" or "Point".</summary>
    public string PaintSelectGranularity { get; set; } = "Path";

    /// <summary>"All", "Formbound", or "Perimeter".</summary>
    public string PaintPickFilter { get; set; } = "All";

    public double PaintBrushRadiusMm { get; set; } = 15.0;

    /// <summary>Exclusive upper scrub move index (high handle / top of layer window).</summary>
    public int ToolpathScrubIndex { get; set; }

    /// <summary>Lower scrub move index (low handle / bottom of isolated window).</summary>
    public int ToolpathScrubLowIndex { get; set; }

    /// <summary>1-based layer high (redundant with move index; used if move count shifts).</summary>
    public double ToolpathScrubLayerHigh { get; set; }

    /// <summary>1-based layer low.</summary>
    public double ToolpathScrubLayerLow { get; set; } = 1;

    /// <summary>Timeline scrub session was live (toolpath armed even if not selected).</summary>
    public bool IsScrubSessionActive { get; set; }

    /// <summary>True when the toolpath node itself was the viewport selection.</summary>
    public bool SelectToolpath { get; set; }

    /// <summary>Parent model name for the scrubbed toolpath.</summary>
    public string? ScrubModelName { get; set; }

    /// <summary>Outliner / node name of the scrubbed toolpath.</summary>
    public string? ScrubToolpathName { get; set; }
}

/// <summary>Reference to the ERP Project/Lead (and optional element) a workspace belongs to.</summary>
public sealed class ErpAttachment
{
    /// <summary>"project" or "lead".</summary>
    public string Type { get; set; } = "project";

    /// <summary>ERP record id.</summary>
    public string Id { get; set; } = "";

    /// <summary>Project number, e.g. "25-114".</summary>
    public string Number { get; set; } = "";

    public string Title { get; set; } = "";

    public string? ElementId { get; set; }

    public string? ElementName { get; set; }
}

/// <summary>One outliner root model entry.</summary>
public sealed class WorkspaceModelEntry
{
    /// <summary>Original import path, when the model was loaded from disk.</summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Path to an embedded mesh sidecar (relative to the workspace file directory),
    /// used for exploded/ungrouped geometry with no original file.
    /// </summary>
    public string? EmbeddedMeshPath { get; set; }

    public string Name { get; set; } = "Model";
    public bool Visible { get; set; } = true;
    public bool LayerPreview { get; set; }

    /// <summary>Row-major 4×4 local transform (16 floats: M11–M44).</summary>
    public float[] LocalTransform { get; set; } = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary>Toolpaths generated from this model (child outliner entries).</summary>
    public List<WorkspaceToolpathEntry> Toolpaths { get; set; } = [];
}

/// <summary>One toolpath outliner child saved with its parent model.</summary>
public sealed class WorkspaceToolpathEntry
{
    public string Name { get; set; } = "Toolpath";
    public bool Visible { get; set; } = true;

    /// <summary>Row-major 4×4 local transform (centroid offset if the toolpath was moved).</summary>
    public float[] LocalTransform { get; set; } = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    public float BeadWidth { get; set; } = 6f;
    public float LayerHeight { get; set; } = 3f;

    /// <summary>RGB material colour used for bead rendering.</summary>
    public float[] MaterialColor { get; set; } = [0.1f, 0.45f, 0.9f];

    /// <summary>Displayed (smoothed) toolpath geometry.</summary>
    public WorkspaceToolpathData Data { get; set; } = new();

    /// <summary>Pre-smoothing toolpath for live orientation re-smoothing (optional).</summary>
    public WorkspaceToolpathData? RawData { get; set; }
}

/// <summary>Serialised toolpath layers and moves.</summary>
public sealed class WorkspaceToolpathData
{
    public List<WorkspaceToolpathLayerData> Layers { get; set; } = [];
}

public sealed class WorkspaceToolpathLayerData
{
    public int Index { get; set; }
    public float Z { get; set; }
    public float Height { get; set; }
    public float[] PlaneNormal { get; set; } = [0, 0, 1];
    public List<WorkspaceToolpathMoveData> Moves { get; set; } = [];

    /// <summary>Recorded contour spans (for in-place seam editing). Empty for pre-existing
    /// workspaces saved before seam metadata existed — those need one re-slice to populate.</summary>
    public List<WorkspaceContourSpanData> Contours { get; set; } = [];
}

public sealed class WorkspaceContourSpanData
{
    public int Start { get; set; }
    public int Count { get; set; }
    public bool Closed { get; set; }
    public int EntryTravelIndex { get; set; } = -1;
}

public sealed class WorkspaceToolpathMoveData
{
    public float[] From { get; set; } = [0, 0, 0];
    public float[] To { get; set; } = [0, 0, 0];
    public string Kind { get; set; } = "Extrude";
    public float[] Normal { get; set; } = [0, 0, 0];
    public bool IsLayerChange { get; set; }
    public bool IsLayerStitch { get; set; }
    public bool IsWipe { get; set; }
    public float WipeRpmScale { get; set; } = 1f;
    public bool IsResumeRamp { get; set; }
    public float ResumeSpeedScale { get; set; } = 1f;
    public float ResumeRpmScale { get; set; } = 1f;
    public bool IsZHop { get; set; }
    public float PrintSpeedScale { get; set; } = 1f;
}