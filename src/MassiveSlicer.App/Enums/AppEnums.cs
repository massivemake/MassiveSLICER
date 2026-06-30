namespace MassiveSlicer;

/// <summary>Top-level application mode toggled by the Prepare / Preview buttons.</summary>
public enum AppMode
{
    /// <summary>Model editing, slicing, and configuration.</summary>
    Prepare,
    /// <summary>KRL animation playback on the timeline.</summary>
    Preview
}

/// <summary>Viewport shading display mode.</summary>
public enum ViewMode
{
    /// <summary>Full PBR shading.</summary>
    Shaded,
    /// <summary>Unlit mesh wireframe.</summary>
    Wireframe,
    /// <summary>Vertex-normal visualisation (colour-coded by direction).</summary>
    Normals,
    /// <summary>Displacement-map preview applied to geometry.</summary>
    Displacement
}

/// <summary>Measurement units used for all displayed values.</summary>
public enum DisplayUnit
{
    /// <summary>Millimetres (internal storage unit).</summary>
    Millimetres,
    /// <summary>Centimetres.</summary>
    Centimetres,
    /// <summary>Metres.</summary>
    Metres,
    /// <summary>Inches.</summary>
    Inches
}

/// <summary>Live connection state of the C3Bridge robot link.</summary>
public enum ConnectionStatus
{
    /// <summary>No connection established.</summary>
    Disconnected,
    /// <summary>Actively streaming joint data from the KRC4.</summary>
    Syncing,
    /// <summary>Connected and idle.</summary>
    Ready,
    /// <summary>Connection or protocol error.</summary>
    Error
}

/// <summary>Tab options for the left workspace panel.</summary>
public enum LeftPanelTab
{
    /// <summary>Scene tree showing all loaded objects.</summary>
    Outliner,
    /// <summary>Viewport appearance: backdrop, blur, shader mode.</summary>
    Viewport,
    /// <summary>Robot joint control, TCP readout, and tool library.</summary>
    Robot
}

/// <summary>Primary tab options for the right settings panel.</summary>
public enum RightPanelTab
{
    /// <summary>Additive (wire-arc / extrusion) slicing parameters.</summary>
    Additive,
    /// <summary>Zivid 3D scanner capture settings.</summary>
    Scan,
    /// <summary>Subtractive (milling) slicing and post-processor settings.</summary>
    Subtractive,
    /// <summary>View, UV, robot, and object settings -- opened via the gear icon.</summary>
    Settings,
    /// <summary>Toolpath export and visibility options. Shown when a toolpath is selected.</summary>
    Toolpath
}

/// <summary>Sub-tab options inside the Settings panel.</summary>
public enum SettingsTab
{
    /// <summary>Viewport appearance (theme, lights, shader mode).</summary>
    View,
    /// <summary>UV channel viewer and texture map slots.</summary>
    Uv,
    /// <summary>Robot joint control, TCP readout, tool library.</summary>
    Robot,
    /// <summary>Geometry and object property inspector.</summary>
    Props
}

/// <summary>Selection component granularity in the viewport.</summary>
public enum SelectionMode
{
    /// <summary>Select individual vertices.</summary>
    Vertex,
    /// <summary>Select mesh edges.</summary>
    Edge,
    /// <summary>Select mesh faces (triangles).</summary>
    Face,
    /// <summary>Select whole scene objects.</summary>
    Object
}

/// <summary>Active transform gizmo in the viewport.</summary>
public enum TransformTool
{
    /// <summary>Marquee / click selection only.</summary>
    Select,
    /// <summary>Translation gizmo.</summary>
    Move,
    /// <summary>Rotation gizmo.</summary>
    Rotate,
    /// <summary>Uniform and per-axis scale gizmo.</summary>
    Scale
}

/// <summary>Additive slicing algorithm.</summary>
public enum SliceMethod
{
    /// <summary>Horizontal Z-plane layers.</summary>
    Planar,
    /// <summary>Planes tilted at a fixed angle (reduces stair-stepping on curved faces).</summary>
    Angled,
    /// <summary>Surface-following conformal toolpaths.</summary>
    Geodesic,
    /// <summary>Non-planar layers interpolating between LOW/HIGH surface boundaries (ETH sweep).</summary>
    Curved
}

/// <summary>Application colour theme.</summary>
public enum AppTheme
{
    /// <summary>Dark grey with lime-green accent (default).</summary>
    Obsidian,
    /// <summary>Cool dark blue with cyan accent.</summary>
    Arctic,
    /// <summary>Warm charcoal with red accent.</summary>
    Crimson,
    /// <summary>Deep teal blue with turquoise accent.</summary>
    Nautical,
    /// <summary>Warm brown with orange accent.</summary>
    Solar,
    /// <summary>Deep space void with violet backgrounds and nova magenta accent.</summary>
    Cosmic,
    /// <summary>Ultra-dark navy with electric blue accent.</summary>
    Midnight,
    /// <summary>Old-growth dark greens with emerald accent.</summary>
    Forest,
    /// <summary>Light neutral grey with vivid blue accent.</summary>
    Chalk,
    /// <summary>Warm cream and parchment tones with terracotta accent.</summary>
    Warm,
}

// ShaderMode enum lives in MassiveSlicer.Viewport.ShaderMode (Viewport project).
