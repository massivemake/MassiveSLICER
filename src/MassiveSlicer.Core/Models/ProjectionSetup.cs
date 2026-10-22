using System.Numerics;

namespace MassiveSlicer.Core.Models;

/// <summary>Helper shape a projection finish is cast from.</summary>
public enum ProjectionShape
{
    Cylinder,
    Sphere,
    Cube,
}

/// <summary>How a cylinder projection walks the part.</summary>
public enum ProjectionSliceDirection
{
    /// <summary>Rings around the cylinder, then step up.</summary>
    Horizontal,
    /// <summary>Passes up and down the cylinder, then step around.</summary>
    Vertical,
}

/// <summary>
/// Drive surface for projection milling. The shape stands on <see cref="Center"/>
/// (the base, on the bed) and extends <see cref="HeightMm"/> along +Z.
/// </summary>
public sealed record ProjectionSetup
{
    public ProjectionShape Shape { get; init; } = ProjectionShape.Cylinder;
    public Vector3 Center { get; init; }
    /// <summary>World direction of the helper's local X. With <see cref="AxisZ"/> it carries rotation.</summary>
    public Vector3 AxisX { get; init; } = Vector3.UnitX;
    /// <summary>World direction of the helper's axis. Cylinder and cube stand along this.</summary>
    public Vector3 AxisZ { get; init; } = Vector3.UnitZ;
    public float BaseDiameterMm { get; init; } = 400f;
    public float TopDiameterMm { get; init; } = 400f;
    public float HeightMm { get; init; } = 300f;
    /// <summary>Rays travel toward the shape interior. Off, they leave the surface.</summary>
    public bool Inward { get; init; }
    /// <summary>0 = tool follows the shape angle. 1 = tool follows the part surface normal.</summary>
    public float NormalInfluence { get; init; } = 0.35f;
    /// <summary>Spin the rotary (E1) so the arm stays on one side of the cylinder.</summary>
    public ProjectionSliceDirection SliceDirection { get; init; } = ProjectionSliceDirection.Horizontal;
    public bool UseE1 { get; init; }
    /// <summary>World centre of the turntable axis.</summary>
    public Vector3 RotaryCenter { get; init; }
    /// <summary>Scene rotation = this sign times E1 degrees. Matches the bed pivot.</summary>
    public float RotarySign { get; init; } = -1f;
}
