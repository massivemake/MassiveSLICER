namespace MassiveSlicer.Core.Models;

/// <summary>
/// Application-wide preferences persisted across sessions.
/// Owned by <c>PreferencesViewModel</c>; shared with subsystems (viewport,
/// input handler) that need to read these values at runtime.
/// </summary>
public sealed class AppPreferences
{
    // ── Navigation ────────────────────────────────────────────────────────

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

    // ── Performance ───────────────────────────────────────────────────────

    /// <summary>Enable multi-sample anti-aliasing in the OpenGL viewport.</summary>
    public bool AntiAliasing { get; set; } = true;

    // ── Appearance ────────────────────────────────────────────────────────

    /// <summary>Active colour theme name (matches a Resources/Themes/*.axaml file).</summary>
    public string ActiveTheme { get; set; } = "Obsidian";

    /// <summary>Path of the backdrop image loaded at startup, or null for none.</summary>
    public string? DefaultBackdropPath { get; set; }

    /// <summary>Mipmap LOD blur level (0 = sharp, 7 = max).</summary>
    public float DefaultBackdropBlur { get; set; } = 2.5f;

    /// <summary>Whether the ground-plane grid lines are rendered.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>Whether the world-axis indicator is rendered in the viewport.</summary>
    public bool ShowAxes { get; set; } = false;

    /// <summary>Whether the print-bed boundary grid overlay is rendered.</summary>
    public bool ShowBedGrid { get; set; } = true;

    /// <summary>
    /// Per-cell default home position name, keyed by cell name (e.g. "LFAM 2" → "Home").
    /// Restored when the user switches to a cell.
    /// </summary>
    public Dictionary<string, string> DefaultHomePositionNames { get; set; } = [];

    /// <summary>Name of the last selected material preset, or null for none.</summary>
    public string? SelectedMaterialPresetName { get; set; }

    // ── Lighting ──────────────────────────────────────────────────────────

    /// <summary>Horizontal rotation of the key light around Z, in degrees.</summary>
    public float LightAzimuth { get; set; } = 45f;

    /// <summary>Vertical angle of the key light above the XY plane, in degrees.</summary>
    public float LightElevation { get; set; } = 45f;

    /// <summary>Directional light intensity multiplier.</summary>
    public float LightIntensity { get; set; } = 1f;

    // ── Shader / view ─────────────────────────────────────────────────────

    /// <summary>Active viewport shader mode name (matches ShaderMode enum).</summary>
    public string ShaderMode { get; set; } = "Standard";

    /// <summary>Whether mesh edges are drawn over the shaded surface.</summary>
    public bool ShowEdges { get; set; } = false;

    /// <summary>Whether the ground-plane shadow catcher is active.</summary>
    public bool ShadowCatcherEnabled { get; set; } = false;

    // ── Toolpath colors (AARRGGBB hex) ────────────────────────────────────

    public string ToolpathExtrudeColor    { get; set; } = "#FF1A73E6";
    public string ToolpathTravelColor     { get; set; } = "#FFD92E2E";
    public string ToolpathSeamColor       { get; set; } = "#FFFFE600";
    public string ToolpathUnselectedColor { get; set; } = "#FF616161";

    // ── Additive slicing ──────────────────────────────────────────────────

    /// <summary>Layer height in mm.</summary>
    public double LayerHeight { get; set; } = 3.0;

    /// <summary>Bead width in mm.</summary>
    public double BeadWidth { get; set; } = 6.0;

    /// <summary>First-layer height override in mm.</summary>
    public double FirstLayerHeight { get; set; } = 3.0;

    /// <summary>Active slicing algorithm name (matches SliceMethod enum).</summary>
    public string SliceMethod { get; set; } = "Planar";

    /// <summary>Pass rotation angle in degrees.</summary>
    public double PassAngle { get; set; } = 0.0;

    /// <summary>Tilt around Y-axis in degrees.</summary>
    public double TiltAngle { get; set; } = 0.0;

    /// <summary>Tilt around X-axis in degrees.</summary>
    public double TiltAngleX { get; set; } = 0.0;

    /// <summary>Deposition feed rate in m/s.</summary>
    public double FeedRate { get; set; } = 0.1;

    /// <summary>Travel (PTP) speed in mm/min.</summary>
    public double TravelSpeed { get; set; } = 120.0;

    /// <summary>Acceleration as a percentage of robot maximum (1–100).</summary>
    public int Acceleration { get; set; } = 100;

    /// <summary>Approach Z height above part in mm.</summary>
    public double ApproachZ { get; set; } = 50.0;

    /// <summary>KUKA TOOL_DATA index (1–16).</summary>
    public int ToolDataIndex { get; set; } = 1;

    /// <summary>KUKA BASE_DATA index (1–32).</summary>
    public int BaseDataIndex { get; set; } = 1;

    /// <summary>Toolhead A angle in degrees.</summary>
    public double ToolheadA { get; set; } = 0.0;

    /// <summary>Toolhead B angle in degrees.</summary>
    public double ToolheadB { get; set; } = 0.0;

    /// <summary>Toolhead C angle in degrees.</summary>
    public double ToolheadC { get; set; } = 0.0;
}
