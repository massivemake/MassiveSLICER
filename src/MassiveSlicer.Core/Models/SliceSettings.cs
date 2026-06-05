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
    public float PrintSpeedMps { get; init; } = 0.1f;

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

    /// <summary>
    /// Minimum XY displacement (mm) between the end of one layer and the start of the next
    /// that triggers a full layer-change travel (stop extrusion, move at travel speed, restart).
    /// Below this threshold the transition is a short extrude stitch — the robot keeps printing
    /// through the seam without stopping. Set to 0 to always travel between layers.
    /// </summary>
    public float LayerChangeMinTravelMm { get; init; } = 2.0f;

    // -- Wave effect --------------------------------------------------------------

    /// <summary>Which wave post-processing effect to apply after slicing. None = disabled.</summary>
    public WaveEffectType WaveEffect { get; init; } = WaveEffectType.None;

    /// <summary>Peak displacement in mm — how far the path swings left/right of the original line.</summary>
    public float WaveAmplitude { get; init; } = 3f;

    /// <summary>Length in mm of one complete oscillation cycle (used when WaveCycles == 0).</summary>
    public float WaveWavelength { get; init; } = 20f;

    /// <summary>
    /// Fixed number of complete wave cycles per contour. When &gt; 0, overrides WaveWavelength —
    /// the effective wavelength scales with the contour perimeter so every layer contains exactly
    /// this many cycles. Useful for radially symmetric parts (vases, columns) where a consistent
    /// visual wave density is more important than a consistent physical wavelength.
    /// 0 = use WaveWavelength (default).
    /// </summary>
    public int WaveCycles { get; init; } = 0;

    /// <summary>
    /// Wave shape: 1.0 = full amplitude variation, lower values clip peaks toward a
    /// square/trapezoidal waveform. Range [0.01, 1.0].
    /// </summary>
    public float WaveShape { get; init; } = 1f;

    /// <summary>
    /// Phase offset added per layer, expressed as a fraction of one wavelength [0, 1].
    /// 0 = all layers identical. 0.5 = each layer shifts by half a cycle, so consecutive
    /// layers alternate between peaks and valleys (useful for structural interlocking).
    /// Values wrap modulo 1 so 1.0 is identical to 0.0.
    /// </summary>
    public float WaveStagger { get; init; } = 0f;
}
