using System.Numerics;

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

    /// <summary>Deposition print speed in m/s.</summary>
    public float FeedRate { get; init; } = 0.1f;

    /// <summary>Travel move speed in m/s.</summary>
    public float TravelSpeed { get; init; } = 0.5f;

    /// <summary>Z height above the part to approach before each pass, in mm.</summary>
    public float ApproachZ { get; init; } = 50f;

    /// <summary>Tilt around the Y-axis in degrees for the Angled method (leans the plane toward ±X).</summary>
    public float TiltAngle { get; init; } = 0f;

    /// <summary>Tilt around the X-axis in degrees for the Angled method (leans the plane toward ±Y).</summary>
    public float TiltAngleX { get; init; } = 0f;

    /// <summary>
    /// XY direction used to project-align seams across layers.
    /// The contour vertex with the highest dot-product against this direction becomes the seam start.
    /// Defaults to (0, 1) -- back of model (max Y).
    /// </summary>
    public Vector2 SeamDirection { get; init; } = new(0f, 1f);

    /// <summary>
    /// Maximum perpendicular deviation (mm) for Douglas-Peucker simplification applied after
    /// the Clipper2 inset. Removes the redundant collinear vertices Clipper adds on straight
    /// segments. 0 disables simplification. Defaults to 0.1 mm.
    /// </summary>
    public float SimplificationTolerance { get; init; } = 0.1f;

    /// <summary>
    /// When true, the bead-width/2 contour inset step is skipped. The raw intersection
    /// contour becomes the print centerline, adding extra material on the outside. Useful
    /// for parts that will be finish-milled after printing.
    /// </summary>
    public bool DisableContourOffset { get; init; } = false;
}
