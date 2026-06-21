using System.Text.Json.Serialization;
using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Core.Models;

/// <summary>A simple three-component float position, used in cell config files.</summary>
public readonly record struct Float3(float X, float Y, float Z)
{
    public static readonly Float3 Zero = new(0f, 0f, 0f);
}

/// <summary>
/// Full definition of a robot cell: robot, print bed, optional booster frame,
/// and C3Bridge connection settings. Serialised as JSON in <c>assets/cells/</c>.
/// </summary>
public sealed record CellConfig
{
    /// <summary>Human-readable cell name (e.g. "LFAM 2").</summary>
    public required string Name { get; init; }

    public required RobotCellConfig Robot { get; init; }
    public required BedCellConfig Bed { get; init; }
    public BoosterFrameCellConfig? BoosterFrame { get; init; }
    public ToolCellConfig? Tool { get; init; }

    /// <summary>Permanent flange-mounted coupler (e.g. Affecto Staubli on LFAM 3).</summary>
    public FlangeAttachmentCellConfig? FlangeAttachment { get; init; }

    /// <summary>Tool-change stands and other static cell geometry.</summary>
    public IReadOnlyList<StandCellConfig> Stands { get; init; } = [];

    /// <summary>Rotary positioner bed (LFAM 3). Null = flat bed only.</summary>
    public RotaryBedCellConfig? RotaryBed { get; init; }

    /// <summary>Named list of available tool configurations.</summary>
    public IReadOnlyList<ToolCellConfig> Tools { get; init; } = [];

    /// <summary>Named KUKA BASE_DATA entries available on this cell (for dropdowns and KRL export).</summary>
    public IReadOnlyList<KrlBaseEntry> KrlBases { get; init; } = [];

    /// <summary>
    /// Returns <see cref="Tools"/> when non-empty, otherwise falls back to the legacy
    /// single <see cref="Tool"/> entry, so old cell files work without changes.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ToolCellConfig> EffectiveTools
        => Tools.Count > 0 ? Tools : (Tool is not null ? [Tool] : []);

    /// <summary>
    /// Name of the tool to activate when switching to the Scan workflow tab.
    /// <see langword="null"/> means this cell has no scanner — the Scan tab is hidden.
    /// </summary>
    public string? ScanToolName { get; init; }

    /// <summary>C3Bridge host IP for live robot connection.</summary>
    public string BridgeIp { get; init; } = "192.168.0.1";
    public int BridgePort { get; init; } = 7000;

    /// <summary>Extruder RevPi lfam-monitor bridge host (LFAM 3: 192.168.0.196).</summary>
    public string? ExtIp { get; init; }

    /// <summary>TCP port for the extruder JSON bridge (default 8765).</summary>
    public int ExtBridgePort { get; init; } = 8765;

    /// <summary>Milling cabinet RevPi lfam-monitor bridge host (LFAM 3: 192.168.0.249).</summary>
    public string? MillIp { get; init; }

    /// <summary>TCP port for the milling JSON bridge (default 8765).</summary>
    public int MillBridgePort { get; init; } = 8765;

    /// <summary>When true the cell has a milling spindle cabinet (LFAM 3).</summary>
    public bool HasMilling { get; init; }

    /// <summary>
    /// Saved default camera view for this cell, applied on load. Null = auto-frame the bed.
    /// Shared via the cell JSON so every user opens to the same saved angle.
    /// </summary>
    public CameraView? View { get; init; }

    /// <summary>
    /// Rotary-bed auto-scan configuration.
    /// <see langword="null"/> means this cell has no rotary bed — the auto-scan UI is hidden.
    /// </summary>
    public BedScanConfig? BedScan { get; init; }
}

/// <summary>A saved orbit-camera pose (spherical, Z-up). Persisted per cell.</summary>
public sealed record CameraView
{
    public float Azimuth   { get; init; }
    public float Elevation { get; init; }
    public float Radius    { get; init; }
    public float TargetX   { get; init; }
    public float TargetY   { get; init; }
    public float TargetZ   { get; init; }
}

/// <summary>Contents of the per-cell <c>home_positions.json</c> sidecar file.</summary>
public sealed class CellPositionData
{
    /// <summary>Name of the position that should be pre-selected when the cell loads.</summary>
    public string? Default { get; set; }

    /// <summary>All named positions for this cell (built-in + user-saved).</summary>
    public List<HomePositionConfig> Positions { get; set; } = [];
}

/// <summary>A named robot home/start position (A1-A6 joint angles in KRL degrees).</summary>
public sealed record HomePositionConfig
{
    /// <summary>Display name shown in the home position selector.</summary>
    public required string Name { get; init; }

    /// <summary>Joint angles A1-A6 in KRL degrees.</summary>
    public float[] Angles { get; init; } = [0f, -90f, 90f, 0f, 15f, 0f];
}

public sealed record RobotCellConfig
{
    /// <summary>Path to the robot GLB asset, relative to the working directory.</summary>
    public required string ModelPath { get; init; }

    /// <summary>World position of ROBROOT (A1 axis mounting surface) in mm, Z-up.</summary>
    public Float3 WorldPosition { get; init; } = Float3.Zero;

    /// <summary>Per-joint axis, sign, KRL offset, and soft limits for A1-A6.</summary>
    public IReadOnlyList<JointConfig> Joints { get; init; } = [];

    /// <summary>Fallback home position in KRL degrees for A1-A6. Used when <see cref="HomePositions"/> is empty.</summary>
    public float[] HomePosition { get; init; } = [0f, -90f, 90f, 0f, 15f, 0f];

    /// <summary>Named home/start positions available for this cell. Shown in the TOOLPATH panel dropdown.</summary>
    public IReadOnlyList<HomePositionConfig> HomePositions { get; init; } = [];

    /// <summary>Name of the position pre-selected when the cell loads. Null means use the first entry.</summary>
    public string? DefaultHomePosition { get; init; }

    /// <summary>
    /// Extra rotation (degrees, CCW positive) applied around the flange outward axis
    /// for the visual flange-frame indicator only. Does not affect TCP position or
    /// IK solving. Use when the physical flange reference mark is rotated relative
    /// to the GLTF joint_6 frame (e.g. LFAM 3 = 15deg).
    /// </summary>
    public float FlangeDisplayRoll { get; init; } = 0f;

    /// <summary>Default toolhead ABC orientation loaded when this cell is selected (degrees, KUKA ZYX Euler).</summary>
    public double DefaultToolheadA { get; init; } = 0.0;
    public double DefaultToolheadB { get; init; } = 0.0;
    public double DefaultToolheadC { get; init; } = 0.0;
}

public sealed record BedCellConfig
{
    /// <summary>Optional path to a bed mesh STL, relative to the working directory.</summary>
    public string? ModelPath { get; init; }

    /// <summary>
    /// World position used to place the bed mesh STL in the scene, in mm, Z-up.
    /// For corner-origin beds (e.g. LFAM 2) this is also the back-left corner of the
    /// print-area grid. For centre-origin beds (e.g. LFAM 3) set <see cref="GridOrigin"/>
    /// separately so the grid is placed correctly.
    /// </summary>
    public required Float3 Origin { get; init; }

    /// <summary>Print area extent along +X in mm.</summary>
    public float Width { get; init; } = 3000f;

    /// <summary>Print area extent along +Y in mm.</summary>
    public float Depth { get; init; } = 3000f;

    /// <summary>
    /// Back-left corner of the print-area grid in world-space mm, Z-up.
    /// When null the renderer falls back to <see cref="Origin"/>.
    /// Set this when the bed mesh origin is the centre of the print area rather
    /// than its back-left corner (e.g. a hexagonal bed centred on the BASE frame).
    /// </summary>
    public Float3? GridOrigin { get; init; }

    /// <summary>
    /// Optional world-space placement for the bed mesh STL and print-area grid only.
    /// Prefer <see cref="VisualOffset"/> (BASE-frame mm from the locked origin).
    /// </summary>
    public Float3? VisualOrigin { get; init; }

    /// <summary>
    /// BASE-frame XY offset (mm) from the locked origin to the visual bed back-left corner.
    /// Set from synced TCP at the physical bed corner (e.g. X=-127.8, Y=-103.6).
    /// Does not affect KRL export or <see cref="BaseData"/>.
    /// </summary>
    public Float3? VisualOffset { get; init; }

    /// <summary>
    /// World-space BASE 0,0,0 corner on the bed surface (locked origin marker).
    /// XY from ROBROOT + <see cref="BaseData"/>; Z from <see cref="Origin"/> surface height.
    /// </summary>
    public Float3 BaseMarkerWorld(Float3 robrootWorld) => new(
        robrootWorld.X + BaseData.X,
        robrootWorld.Y + BaseData.Y,
        Origin.Z);

    /// <summary>Whether the visual bed/grid is shifted away from the locked BASE marker.</summary>
    [JsonIgnore]
    public bool HasVisualShift => VisualOffset is not null || VisualOrigin is not null;

    /// <summary>Back-left grid corner for rendering; does not affect the locked BASE marker.</summary>
    public Float3 VisualGridCorner(Float3 robrootWorld)
    {
        var locked = BaseMarkerWorld(robrootWorld);
        if (VisualOffset is { } d)
            return new Float3(locked.X + d.X, locked.Y + d.Y, locked.Z);
        if (VisualOrigin is { } v)
            return v;
        return GridOrigin ?? Origin;
    }

    /// <summary>World-space translation for the bed mesh STL wrapper.</summary>
    public Float3 VisualMeshOrigin(Float3 robrootWorld)
        => HasVisualShift && GridOrigin is null ? VisualGridCorner(robrootWorld) : Origin;

    /// <summary>
    /// BASE_DATA position relative to ROBROOT in mm. Used for IK solving and KRL export.
    /// NOT a world position -- add ROBROOT world position to get world coords.
    /// </summary>
    public required Float3 BaseData { get; init; }

    /// <summary>
    /// When set, the bed is a circular rotary turntable of this diameter (mm) centred on
    /// <see cref="Origin"/>: the print-area overlay renders as a polar grid (circle + rings +
    /// spokes) instead of a rectangle. Null = rectangular bed.
    /// </summary>
    public float? Diameter { get; init; }

    /// <summary>
    /// Sign applied to E1 when rotating scene geometry about <see cref="Origin"/>:
    /// +1 = CCW about world +Z, −1 = CW. Set by rotary-bed rotation calibration.
    /// Null defaults to −1 (the original hard-coded direction).
    /// </summary>
    public float? RotationSign { get; init; }

    /// <summary>When true the flat bed mesh is omitted (rotary bed replaces it).</summary>
    public bool Hidden { get; init; }
}

public sealed record BoosterFrameCellConfig
{
    /// <summary>Path to the booster frame STL, relative to the working directory.</summary>
    public required string ModelPath { get; init; }

    /// <summary>World position of the frame origin in mm, Z-up.</summary>
    public Float3 WorldPosition { get; init; } = Float3.Zero;
}

/// <summary>
/// Tool (end-effector) attached to the robot flange.
/// TCP position and orientation are in KUKA TOOL_DATA convention:
/// position in mm relative to the flange, orientation as Euler ZYX degrees (A=RotZ, B=RotY, C=RotX).
/// </summary>
public sealed record ToolCellConfig
{
    /// <summary>Display name shown in the tool selector.</summary>
    public string Name { get; init; } = "";

    /// <summary>Path to the tool GLB asset, relative to the working directory.</summary>
    public required string ModelPath { get; init; }

    // -- KUKA TOOL_DATA --------------------------------------------------------
    public float TcpX { get; init; } = 0f;
    public float TcpY { get; init; } = 0f;
    public float TcpZ { get; init; } = 0f;
    /// <summary>Euler ZYX A -- rotation around Z (degrees).</summary>
    public float TcpA { get; init; } = 0f;
    /// <summary>Euler ZYX B -- rotation around Y (degrees).</summary>
    public float TcpB { get; init; } = 0f;
    /// <summary>Euler ZYX C -- rotation around X (degrees).</summary>
    public float TcpC { get; init; } = 0f;

    /// <summary>
    /// Rotation of the KUKA TOOL frame around the flange outward axis (degrees, CCW positive).
    /// Corrects for the physical mounting orientation of the tool on the flange.
    /// 0 = KUKA-X aligns with GLTF flange X; adjust per printer.
    /// </summary>
    public float ToolFrameRoll { get; init; } = 0f;

    /// <summary>KUKA TOOL_DATA[] index for this tool (1–16). 0 means unmapped.</summary>
    public int KrlIndex { get; init; } = 0;

    // -- Sensor origin (optional, e.g. camera optical centre) -----------------
    // When set, a second axis gizmo is drawn in the viewport and scan
    // registration uses this position instead of the main TCP focal point.
    public float? SensorOriginX { get; init; }
    public float? SensorOriginY { get; init; }
    public float? SensorOriginZ { get; init; }
    public float? SensorOriginA { get; init; }
    public float? SensorOriginB { get; init; }
    public float? SensorOriginC { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSensorOrigin => SensorOriginX.HasValue;

    /// <summary>Parked tool pose in ROBROOT mm + KUKA ABC (tool-change dock).</summary>
    public ToolDockCellConfig? Dock { get; init; }

    /// <summary>Shown mounted on the flange when the cell first loads.</summary>
    public bool Default { get; init; }
}

/// <summary>Dock pose for a parked tool head (ROBROOT frame, mm + KUKA ZYX degrees).</summary>
public sealed record ToolDockCellConfig
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float A { get; init; }
    public float B { get; init; }
    public float C { get; init; }
}

/// <summary>Permanent coupler mesh parented to joint_6.</summary>
public sealed record FlangeAttachmentCellConfig
{
    public string Name { get; init; } = "";
    public required string ModelPath { get; init; }
}

/// <summary>Static cell prop (tool stand, enclosure, etc.).</summary>
public sealed record StandCellConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ModelPath { get; init; }

    /// <summary>Seed placement in scene metres (MassiveCONNECT convention).</summary>
    public float[] Position { get; init; } = [0f, 0f, 0f];

    /// <summary>Seed rotation in radians (XYZ order).</summary>
    public float[] Rotation { get; init; } = [0f, 0f, 0f];
}

/// <summary>LFAM 3 rotary positioner: fixed bottom + E1-driven top section.</summary>
public sealed record RotaryBedCellConfig
{
    public required string BottomPath { get; init; }
    public required string TopPath { get; init; }
    public float[] BasePos { get; init; } = [0f, 0f, 0f];
    public float[] BaseAbc { get; init; } = [0f, 0f, 0f];
    public float E1Sign { get; init; } = 1f;
}

/// <summary>A named KUKA BASE_DATA entry exposed for dropdowns and KRL export.</summary>
public sealed record KrlBaseEntry
{
    public required string Name  { get; init; }
    public required int    Index { get; init; }
}

/// <summary>
/// Configuration for the LFAM3 rotary-bed auto-scan feature.
/// The robot parks at <see cref="ScanPose"/>, then BedScan.src steps the rotary
/// table through <see cref="ScanSteps"/> equally-spaced positions while Zivid
/// fires one capture per step.
/// </summary>
public sealed record BedScanConfig
{
    /// <summary>
    /// Robot joint pose (A1–A6, KRL degrees) for the overhead scan position.
    /// Calibrate on the pendant, then update lfam3.json.
    /// </summary>
    public float[] ScanPose { get; init; } = [0f, -90f, 90f, 0f, 15f, 0f];

    /// <summary>
    /// Which external axis drives the rotary table (1 = E1).
    /// Used by the PC scan-registration de-rotation: p_bed = R_z(-θ) × (M_cam × p_cam − bedOrigin).
    /// </summary>
    public int RotaryAxisIndex { get; init; } = 1;

    /// <summary>
    /// Number of rotation steps. Step angle = 360 / ScanSteps.
    /// Default 8 gives 45° per step.
    /// </summary>
    public int ScanSteps { get; init; } = 8;
}
