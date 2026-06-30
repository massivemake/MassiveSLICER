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

    /// <summary>Linear rail (LFAM 1 E1). Null = no rail translation in the viewport.</summary>
    public RobotRailCellConfig? RobotRail { get; init; }

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
    /// Path to the KUKA <c>ROBOTER/KRC/R1</c> folder (contains <c>Program/</c> with tool-change KRL).
    /// LFAM 3 live share: <c>\\192.168.0.153\krc\ROBOTER\KRC\R1</c>.
    /// </summary>
    public string? KrcRoot { get; init; }

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

    /// <summary>
    /// Named robot poses for calibration and setup workflows (3D scan cal, bed cal, etc.).
    /// Stored in the cell JSON; recalled via <c>waypoint go &lt;name&gt;</c> or programmatically.
    /// </summary>
    public IReadOnlyList<CellWaypointConfig> Waypoints { get; init; } = [];
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

    /// <summary>LFAM 3-style circular turntable (imports centre on <see cref="Origin"/>).</summary>
    [JsonIgnore]
    public bool IsRotaryPrintBed => Diameter is > 0;

    /// <summary>
    /// World-space centre of the surface where user imports should land.
    /// Rotary: turntable axis centre (<see cref="Origin"/>). Rectangular: print-area centre.
    /// </summary>
    public Float3 ImportSurfaceCenter(Float3 robrootWorld)
    {
        if (IsRotaryPrintBed)
            return Origin;

        var corner = VisualGridCorner(robrootWorld);
        return new Float3(
            corner.X + Width / 2f,
            corner.Y + Depth / 2f,
            corner.Z);
    }

    /// <summary>Half-diameter usable for imports on a rotary bed; null on rectangular cells.</summary>
    [JsonIgnore]
    public float? ImportSurfaceRadiusMm => Diameter is > 0 ? Diameter * 0.5f : null;
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

/// <summary>LFAM 1 KL4000S linear rail driven by KUKA E1 (mm, not degrees).</summary>
public sealed record RobotRailCellConfig
{
    /// <summary>World axis the carriage travels along: X, Y, or Z.</summary>
    public string Axis { get; init; } = "Y";

    /// <summary>Minimum E1 position in mm (KUKA soft limit).</summary>
    public float MinMm { get; init; } = -4650f;

    /// <summary>Maximum E1 position in mm (KUKA soft limit).</summary>
    public float MaxMm { get; init; } = 150f;

    /// <summary>Sign mapping KUKA E1 mm to scene translation along <see cref="Axis"/>.</summary>
    public float E1Sign { get; init; } = 1f;

    /// <summary>Scene-space translation (mm) for a live E1 reading.</summary>
    public Float3 SceneOffsetMm(double e1Mm)
    {
        float d = (float)(E1Sign * e1Mm);
        return Axis.ToUpperInvariant() switch
        {
            "X" => new Float3(d, 0f, 0f),
            "Y" => new Float3(0f, d, 0f),
            "Z" => new Float3(0f, 0f, d),
            _   => new Float3(0f, d, 0f),
        };
    }
}

/// <summary>LFAM 3 rotary positioner: fixed bottom + E1-driven top section.</summary>
public sealed record RotaryBedCellConfig
{
    public required string BottomPath { get; init; }
    public required string TopPath { get; init; }
    public float[] BasePos { get; init; } = [0f, 0f, 0f];
    public float[] BaseAbc { get; init; } = [0f, 0f, 0f];
    public float E1Sign { get; init; } = 1f;

    /// <summary>LFAM 3 validated factory default when omitted from cell JSON.</summary>
    public const float DefaultOrientationOffsetDeg = -0.97f;

    /// <summary>
    /// Constant rotation (degrees) of the whole bed assembly about its vertical axis through the
    /// centre, applied on top of <see cref="BaseAbc"/>. Corrects a fixed phase between the idealised
    /// model orientation and how the table is physically mounted (measured from registered scans).
    /// Future scans auto-compensate (they're placed by world pose); the mesh rotates to match them.
    /// </summary>
    public float OrientationOffsetDeg { get; init; } = DefaultOrientationOffsetDeg;
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

    /// <summary>
    /// TCP Y offsets (mm, active base) for multi-vantage auto bed cal relative to the
    /// <c>bed-cal</c> waypoint. Each entry runs a full E1 sweep before the next offset.
    /// Default: centre (0) then −300 mm Y (outer ring).
    /// </summary>
    public float[] BedCalVantageOffsetsY { get; init; } = [0f, -300f];

    /// <summary>
    /// Wrist nutation deltas (deg, A4/A5/A6) for each auto scan-cal pose. When omitted,
    /// <see cref="Scanning.ScanToolCalSweep"/> factory defaults are used. After a run where
    /// a pose needed a gentler angle to keep the calibration card in frame, the successful
    /// deltas are written here so the next calibration starts closer.
    /// </summary>
    public ScanCalWristDeltaConfig[]? ScanCalWristDeltas { get; init; }
}

/// <summary>One wrist-only delta for scan-tool hand-eye calibration (degrees).</summary>
public sealed record ScanCalWristDeltaConfig
{
    public float A4 { get; init; }
    public float A5 { get; init; }
    public float A6 { get; init; }
}

/// <summary>
/// A reusable named robot pose for a cell. Captures both Cartesian TCP and joint angles so
/// calibration workflows can prefer joint-space moves when Cartesian paths hit soft limits.
/// </summary>
public sealed record CellWaypointConfig
{
    /// <summary>Stable id for console / code lookup (e.g. <c>scanner-down-bed</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable note shown in <c>waypoint list</c>.</summary>
    public string? Description { get; init; }

    /// <summary>Workflow tags — e.g. <c>scan-cal</c>, <c>bed-cal</c>.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public float TcpX { get; init; }
    public float TcpY { get; init; }
    public float TcpZ { get; init; }
    public float TcpA { get; init; }
    public float TcpB { get; init; }
    public float TcpC { get; init; }

    /// <summary>A1–A6 in KRL degrees; optional 7th element is E1.</summary>
    public float[]? Joints { get; init; }

    public int Tool { get; init; } = 6;
    public int Base { get; init; } = 0;
    public int VelocityPct { get; init; } = 20;

    /// <summary>When true, recall uses MS_AXIS (joint PTP) instead of Cartesian MS_POSE.</summary>
    public bool PreferJoints { get; init; }
}
