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

    /// <summary>Named list of available tool configurations.</summary>
    public IReadOnlyList<ToolCellConfig> Tools { get; init; } = [];

    /// <summary>
    /// Returns <see cref="Tools"/> when non-empty, otherwise falls back to the legacy
    /// single <see cref="Tool"/> entry, so old cell files work without changes.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ToolCellConfig> EffectiveTools
        => Tools.Count > 0 ? Tools : (Tool is not null ? [Tool] : []);

    /// <summary>C3Bridge host IP for live robot connection.</summary>
    public string BridgeIp { get; init; } = "192.168.0.1";
    public int BridgePort { get; init; } = 7000;
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

    /// <summary>
    /// Extra rotation (degrees, CCW positive) applied around the flange outward axis
    /// for the visual flange-frame indicator only. Does not affect TCP position or
    /// IK solving. Use when the physical flange reference mark is rotated relative
    /// to the GLTF joint_6 frame (e.g. LFAM 3 = 15deg).
    /// </summary>
    public float FlangeDisplayRoll { get; init; } = 0f;
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
    /// BASE_DATA position relative to ROBROOT in mm. Used for IK solving and KRL export.
    /// NOT a world position -- add ROBROOT world position to get world coords.
    /// </summary>
    public required Float3 BaseData { get; init; }
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

}
