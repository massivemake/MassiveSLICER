namespace MassiveSlicer.Core.Models;

/// <summary>Parameters snapshot passed to the planar slicer. All distances are in mm.</summary>
public sealed class SliceSettings
{
    /// <summary>Height of each deposited layer in mm.</summary>
    public float LayerHeight { get; init; } = 3f;

    /// <summary>Height override for the very first layer in mm.</summary>
    public float FirstLayerHeight { get; init; } = 3f;

    /// <summary>Width of the deposited bead in mm.</summary>
    public float BeadWidth { get; init; } = 6f;

    /// <summary>Deposition feed rate in m/s.</summary>
    public float FeedRate { get; init; } = 0.1f;

    /// <summary>Point-to-point travel speed in m/min.</summary>
    public float PtpSpeed { get; init; } = 1f;

    /// <summary>Z height above the part to approach before each pass, in mm.</summary>
    public float ApproachZ { get; init; } = 50f;
}
