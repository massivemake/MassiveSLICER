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
    public bool IsZHop { get; set; }
}