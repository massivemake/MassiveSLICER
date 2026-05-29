namespace MassiveSlicer.Core.Models;

/// <summary>
/// Application-wide preferences persisted across sessions.
/// Owned by <c>PreferencesViewModel</c>; shared with subsystems (viewport,
/// input handler) that need to read these values at runtime.
/// </summary>
public sealed class AppPreferences
{
    // -- Navigation --------------------------------------------------------

    /// <summary>
    /// When true, the orbit pivot depth adjusts to the geometry under the cursor
    /// rather than using a fixed world-origin pivot.
    /// </summary>
    public bool AutoDepth { get; set; } = true;

    /// <summary>
    /// When true, the orbit pivot is the centre of the current selection
    /// instead of the world origin.
    /// </summary>
    public bool OrbitAroundSelection { get; set; } = true;

    /// <summary>Active mouse-button navigation preset.</summary>
    public NavigationPresetId ActivePreset { get; set; } = NavigationPresetId.Rhino;

    // -- Performance -------------------------------------------------------

    /// <summary>Enable multi-sample anti-aliasing in the OpenGL viewport.</summary>
    public bool AntiAliasing { get; set; } = true;

    // -- Appearance --------------------------------------------------------

    /// <summary>Path of the backdrop image loaded at startup, or null for none.</summary>
    public string? DefaultBackdropPath { get; set; }

    /// <summary>Mipmap LOD blur level saved alongside the default backdrop (0 = sharp, 7 = max).</summary>
    public float DefaultBackdropBlur { get; set; } = 2.5f;

    /// <summary>Whether the ground-plane grid lines are rendered.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>Whether the world-axis indicator is rendered in the viewport.</summary>
    public bool ShowAxes { get; set; } = false;

    /// <summary>Whether the print-bed boundary grid overlay is rendered.</summary>
    public bool ShowBedGrid { get; set; } = true;

    /// <summary>
    /// Per-cell default home position name, keyed by cell name (e.g. "LFAM 2" -> "Home").
    /// Restored when the user switches to a cell.
    /// </summary>
    public Dictionary<string, string> DefaultHomePositionNames { get; set; } = [];

    /// <summary>Name of the last selected material preset, or null for none.</summary>
    public string? SelectedMaterialPresetName { get; set; }
}
