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

    /// <summary>
    /// Viewport UI session at save time (edit mode, tools, layer isolation, scrub,
    /// 2D slice viewer). Written immediately after Camera so a truncated large save
    /// (common on NAS mid-toolpath serialize) still restores view/edit state.
    /// Null on workspaces saved before this field existed.
    /// </summary>
    public WorkspaceUiSession? UiSession { get; set; }

    /// <summary>Active right-panel tab name (matches <see cref="RightPanelTab"/> enum).</summary>
    public string RightPanelTab { get; set; } = "Additive";

    /// <summary>User-imported models shown in the outliner.</summary>
    public List<WorkspaceModelEntry> Models { get; set; } = [];

    /// <summary>Snapshot of user settings at save time.</summary>
    public AppPreferences Settings { get; set; } = new();

    /// <summary>ERP project/lead this workspace is attached to, or null.</summary>
    public ErpAttachment? Erp { get; set; }
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

    /// <summary>2D Slice Plane Viewer toggle (edit mode only).</summary>
    public bool IsSlicePlaneViewerActive { get; set; }

    /// <summary>
    /// Multi-Planar "Planes" viewport overlay toggle. Nullable so workspaces saved
    /// before this field keep the app default (on) instead of deserializing as false.
    /// </summary>
    public bool? ShowMultiPlanarPlanes { get; set; }

    /// <summary>
    /// X-bracing plane/cylinder helper visibility. Nullable so older workspaces keep
    /// the app default (on). Prefer this when present over Settings alone so the
    /// helper state always round-trips with the .mass file.
    /// Always serialized when non-null (bool? false is not "default" so it is written
    /// even under <c>WhenWritingDefault</c>).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool? XBracingShowHelper { get; set; }

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

    /// <summary>"Square" or "Lasso" region-select mode.</summary>
    public string PaintRegionSelectMode { get; set; } = "Square";

    /// <summary>"Support" or "Remove".</summary>
    public string PaintModificationMode { get; set; } = "Support";

    /// <summary>"Formbound Buttress" or "Formbound Bridge".</summary>
    public string PaintSupportType { get; set; } = "Formbound Buttress";

    /// <summary>Show Support/Remove paint marker spheres in the viewport.</summary>
    public bool ShowPaintMarkers { get; set; } = true;

    /// <summary>Show bead mesh while edit mode is open.</summary>
    public bool PaintShowBeads { get; set; }

    /// <summary>
    /// Applied MODIFICATIONS list (reselectable Support/Remove entries with optional
    /// bridge targets). Null/empty when none or workspaces saved before this field.
    /// </summary>
    public List<WorkspacePaintModification> PaintModifications { get; set; } = [];

    /// <summary>Robot joint pose [A1..A6, E1] (KRL degrees) at save time.</summary>
    public double[]? RobotJoints { get; set; }

    /// <summary>Sim-timeline camera keyframes: [percent, azimuth, elevation, radius, targetX, targetY, targetZ].</summary>
    public List<double[]>? SimCameraKeyframes { get; set; }

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

    /// <summary>
    /// When true, pause realtime re-slice after open (e.g. baked Start/Stop calibration).
    /// Null = leave the current pause state alone (older workspaces).
    /// </summary>
    public bool? RealtimeSlicingPaused { get; set; }
}

/// <summary>
/// One path/point span inside a grouped paint modification (Shift multi-select).
/// </summary>
public sealed class WorkspacePaintModMember
{
    public int LayerIndex { get; set; }
    public float LayerZ { get; set; }
    public int SpanStart { get; set; }
    public int SpanCount { get; set; }
    public bool SpanClosed { get; set; }
    public int SpanEntryTravelIndex { get; set; } = -1;
    /// <summary>Mark centres as [x,y,z] for this member only.</summary>
    public List<float[]> MarkCenters { get; set; } = [];
    /// <summary>World highlight polyline [x,y,z] points.</summary>
    public List<float[]> WorldPoints { get; set; } = [];
}

/// <summary>
/// One applied paint modification for workspace save/restore.
/// Layer spans are re-bound by index/Z after the toolpath reloads.
/// </summary>
public sealed class WorkspacePaintModification
{
    public Guid Id { get; set; }
    /// <summary>"Bridge" or "Remove" (<see cref="PaintMarkKind"/> name).</summary>
    public string Kind { get; set; } = "Bridge";

    public int LayerIndex { get; set; }
    public float LayerZ { get; set; }
    public int SpanStart { get; set; }
    public int SpanCount { get; set; }
    public bool SpanClosed { get; set; }
    public int SpanEntryTravelIndex { get; set; } = -1;

    /// <summary>Mark centres as [x,y,z].</summary>
    public List<float[]> MarkCenters { get; set; } = [];

    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool IsExpanded { get; set; }

    /// <summary>"Formbound Buttress", "Formbound Bridge", "Tree Support",
    /// or "Structural Support".</summary>
    public string SupportType { get; set; } = "Formbound Buttress";

    /// <summary>Structural Support cards: index into the saved StructuralSupports
    /// list this card edits. -1 = not a structural card.</summary>
    public int StructuralIndex { get; set; } = -1;

    /// <summary>"Inside" or "Outside" — Formbound wall side for this selection.</summary>
    public string SupportSide { get; set; } = "Inside";

    /// <summary>World highlight polyline [x,y,z] points.</summary>
    public List<float[]> WorldPoints { get; set; } = [];

    /// <summary>
    /// Group members (Shift multi-select). Empty = legacy single-span mod.
    /// When present, primary Layer/Span fields still mirror the first member.
    /// </summary>
    public List<WorkspacePaintModMember> Members { get; set; } = [];

    // ── Optional bridge target ───────────────────────────────────────────────
    public int? TargetLayerIndex { get; set; }
    public float? TargetLayerZ { get; set; }
    public int? TargetSpanStart { get; set; }
    public int? TargetSpanCount { get; set; }
    public bool TargetSpanClosed { get; set; }
    public int TargetSpanEntryTravelIndex { get; set; } = -1;
    public List<float[]> TargetMarkCenters { get; set; } = [];
    public List<float[]> TargetWorldPoints { get; set; } = [];
    public List<float[]> ScaffoldMarkCenters { get; set; } = [];
    public int ScaffoldLayerCount { get; set; }
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

    /// <summary>Always serialized even when false — false is bool's TYPE default, so
    /// WhenWritingDefault would otherwise drop it and silently reset to true (this class's
    /// declared default) on load. See WorkspaceUiSession.XBracingShowHelper for the same pattern.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool Visible { get; set; } = true;

    public bool LayerPreview { get; set; }

    /// <summary>Row-major 4×4 local transform (16 floats: M11–M44).</summary>
    public float[] LocalTransform { get; set; } = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary>
    /// The part's pivot in its own mesh space — where the gizmo sits and what rotation and scale
    /// work about. Three floats; <c>null</c> in files saved before pivots were a thing.
    /// </summary>
    /// <remarks>
    /// The matrix above cannot carry this. A pivot move is deliberately composed to leave the final
    /// matrix identical — that is what lets the handle be repositioned without the geometry
    /// stirring — so the pivot is invisible to anything reading the matrix alone. Saving only the
    /// matrix therefore lost every Move Origin and Recenter the user had done, and reopening a file
    /// left its parts with no placement at all: back to the exporter's own origin, the detached
    /// gizmo, and world-axis rotation this whole transform rework exists to get rid of.
    /// <para>
    /// Everything else about the placement — position, rotation, scale — is recoverable from the
    /// matrix once the pivot is known (<c>NodeTransform.FromMatrix</c>), so only this and
    /// <see cref="ImportScale"/> need storing.
    /// </para>
    /// </remarks>
    public float[]? PivotOrigin { get; set; }

    /// <summary>
    /// The scale the part had when it first adopted a placement — the 100% the scale tool's percent
    /// mode measures against. Three floats; <c>null</c> in older files.
    /// </summary>
    /// <remarks>
    /// Not always 1: a metres-as-millimetres import is corrected ×1000 and a part dropped into a
    /// rotary cell is scaled to fit the platter, both before the placement is taken. Without this,
    /// reopening a file quietly redefines 100% as "whatever size it was saved at".
    /// </remarks>
    public float[]? ImportScale { get; set; }

    /// <summary>Toolpaths generated from this model (child outliner entries).</summary>
    public List<WorkspaceToolpathEntry> Toolpaths { get; set; } = [];

    /// <summary>Non-destructive modifiers (Cut planes, future braces/supports). Applied when the workspace loads.</summary>
    public List<WorkspaceCutModifier> Modifiers { get; set; } = [];

    /// <summary>Name of the Applied-Pieces group this entry belonged to at save time (see
    /// ViewportViewModel.CreateAppliedPiecesGroup), or null for a model that isn't a Cut
    /// modifier's output piece. Entries sharing the same name are re-grouped together on load.</summary>
    public string? PiecesGroupName { get; set; }

    /// <summary>
    /// True when this entry is a Zivid / bed scan (outliner under the rotary group), not a
    /// print CAD import. Scans must be restored with <c>AddScanNode</c> so they stay selectable
    /// and track E1; omitting this used to drop every scan on Save Workspace.
    /// </summary>
    public bool IsScan { get; set; }

    /// <summary>
    /// Optional absolute or workspace-relative path to a captured <c>.zdf</c> (and sidecar
    /// <c>.json</c>) for re-meshing if the embedded STL is missing.
    /// </summary>
    public string? ScanZdfPath { get; set; }
}

/// <summary>Serialized Cut modifier (plane position, orientation, size bounds).</summary>
public sealed class WorkspaceCutModifier
{
    public string Name { get; set; } = "Cut";

    // Enabled/PreviewVisible/Cut/Infinite all default to true — false is bool's TYPE default,
    // so WhenWritingDefault would otherwise drop a false value entirely and silently reset it
    // to true (this class's declared default) on load. Same pattern as WorkspaceModelEntry.Visible
    // and WorkspaceUiSession.XBracingShowHelper.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool Enabled { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool PreviewVisible { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool Cut { get; set; } = true;

    /// <summary>"Horizontal" or "Vertical".</summary>
    public string Orientation { get; set; } = "Horizontal";

    public float RotationDegrees { get; set; }
    public float Offset { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public float PositionTangent { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool Infinite { get; set; } = true;
    public float SizeX { get; set; } = 1500f;
    public float SizeY { get; set; } = 1500f;
}

/// <summary>One toolpath outliner child saved with its parent model.</summary>
public sealed class WorkspaceToolpathEntry
{
    public string Name { get; set; } = "Toolpath";

    /// <summary>Always serialized even when false — see WorkspaceModelEntry.Visible.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
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
    /// <summary>Optional per-travel resume wait (seconds). Null/0 omitted when serializing default.</summary>
    public float? ResumeWaitSec { get; set; }

    /// <summary>
    /// Local layer thickness relative to nominal (1 = nominal) — the adaptive-layer-height and
    /// Multi-Planar wedge flow correction. MUST round-trip: without it a reopened workspace
    /// exports every thin layer at full nominal flow (measured up to 2.9x over-extrusion).
    /// Absent in files saved before this field existed, which load as 1 — the old behaviour.
    /// </summary>
    public float HeightScale { get; set; } = 1f;

    /// <summary>Lightning Bridge support finger — drives its own display layer.</summary>
    public bool IsLightning { get; set; }

    /// <summary>Absolute RPM (%) for this move; null = derive from speed. Used by the brim.</summary>
    public float? RpmPercentOverride { get; set; }

    /// <summary>
    /// Bed-adhesion brim. MUST round-trip: reprocessing a reloaded workspace re-runs
    /// LayerSpeedPostProcessor, which would otherwise put the brim back into the layer-speed
    /// metric and re-cap the whole part's speed against the 99 % RPM gate.
    /// </summary>
    public bool IsBrim { get; set; }

    /// <summary>Travel inserted when merging separate toolpaths.</summary>
    public bool IsMergeConnector { get; set; }

    /// <summary>Per-move travel speed override (m/s). Null = use the global travel speed.</summary>
    public float? TravelSpeedMps { get; set; }
}