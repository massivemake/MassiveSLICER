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

    /// <summary>Lift height (mm) inserted on travel moves. 0 = disabled.</summary>
    public float ZHopMm { get; init; }

    /// <summary>Pre-travel wipe mode. None = disabled.</summary>
    public WipeMode WipeMode { get; init; } = WipeMode.None;

    /// <summary>Total wipe path length in mm.</summary>
    public float WipeLengthMm { get; init; } = 10f;

    /// <summary>Trailing wipe distance (mm) over which extrusion RPM ramps down to zero.</summary>
    public float WipeRampMm { get; init; } = 5f;

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

    // -- Adaptive layer height ----------------------------------------------------

    /// <summary>When true, layer spacing is computed per-Z from mesh surface normals.</summary>
    public bool  AdaptiveLayerHeight { get; init; } = false;

    /// <summary>
    /// Controls the trade-off between surface quality and print speed.
    /// 0 = finest detail (layers approach MinLayerHeight on gentle slopes);
    /// 1 = fastest (layers approach LayerHeight on all but the gentlest slopes).
    /// </summary>
    public float AdaptiveQuality     { get; init; } = 0.5f;

    /// <summary>Minimum layer height used by adaptive slicing (mm). Must be ≤ LayerHeight.</summary>
    public float MinLayerHeight      { get; init; } = 1.0f;

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

    /// <summary>
    /// When true, open contours (panels, single-wall prints) alternate print direction each layer.
    /// Even layers print start→end; odd layers print end→start, eliminating the long return travel.
    /// Has no effect on closed contours.
    /// </summary>
    public bool ZigZagSeam { get; init; } = false;

    // -- Wave gradient ------------------------------------------------------------

    /// <summary>When true, amplitude and wavelength are linearly interpolated per layer between
    /// the Bottom and Top values rather than being held constant.</summary>
    public bool WaveGradient { get; init; } = false;

    /// <summary>Wave amplitude at the bottom (zMin) of the toolpath, in mm.</summary>
    public float WaveAmplitudeBottom { get; init; } = 0f;

    /// <summary>Wave amplitude at the top (zMax) of the toolpath, in mm.</summary>
    public float WaveAmplitudeTop { get; init; } = 3f;

    /// <summary>Wave wavelength at the bottom of the toolpath, in mm.</summary>
    public float WaveWavelengthBottom { get; init; } = 20f;

    /// <summary>Wave wavelength at the top of the toolpath, in mm.</summary>
    public float WaveWavelengthTop { get; init; } = 20f;

    /// <summary>
    /// Shifts the midpoint of the gradient along the height axis.
    /// 0.5 = linear (midpoint at 50 % height). Values closer to 0 compress the gradient
    /// toward the bottom; values closer to 1 compress it toward the top. Range (0, 1).
    /// </summary>
    public float WaveGradientCenter { get; init; } = 0.5f;

    /// <summary>Easing curve applied after the centre-shift bias.</summary>
    public WaveGradientCurveType WaveGradientCurve { get; init; } = WaveGradientCurveType.Linear;

    // -- Infill -------------------------------------------------------------------

    /// <summary>
    /// When non-None, the slicer fills the slice polygon with a continuous infill
    /// pattern instead of emitting contour shells.
    /// </summary>
    public InfillPattern InfillPattern { get; init; } = InfillPattern.None;

    /// <summary>
    /// Centre-to-centre spacing between infill lines in mm.
    /// 0 = use BeadWidth as spacing.
    /// </summary>
    public float InfillSpacingMm { get; init; } = 0f;

    /// <summary>
    /// Base angle of the infill lines in degrees (0 = along X axis).
    /// For Grid and Triangle patterns this is the angle of the first layer;
    /// subsequent layers are rotated by the pattern's step angle.
    /// </summary>
    public float InfillAngleDeg { get; init; } = 0f;

    // -- Overhang orientation -----------------------------------------------------

    /// <summary>
    /// When true, the planar slicer assigns per-move surface normals derived from the
    /// intersected mesh faces. The KRL exporter uses these to tilt the toolhead toward the
    /// surface, improving overhang adhesion. The wave effect passes normals through unchanged —
    /// orientation is driven by mesh geometry, not wave-displaced positions.
    /// </summary>
    public bool  OverhangOrientation { get; init; } = false;

    /// <summary>
    /// Maximum allowed tilt from vertical in degrees. Clamps the per-move normal so that
    /// the tool angle never exceeds this deviation from straight-down. Prevents the robot
    /// from reaching singularity positions on near-horizontal or inverted surfaces.
    /// Range [0, 89]. Defaults to 45°.
    /// </summary>
    public float MaxOverhangTiltDeg  { get; init; } = 45f;

    // -- Orientation smoothing ----------------------------------------------------

    /// <summary>
    /// When true, per-move toolhead normals are smoothed with a box-filter pass after
    /// slicing. Prevents sharp ABC reorientation jumps from over-accelerating the robot.
    /// Only affects orientation (Normal field); XYZ positions are unchanged.
    /// </summary>
    public bool SmoothRotation       { get; init; } = false;

    /// <summary>
    /// Half-width of the orientation smoothing window in moves.
    /// Each move's normal is averaged with ±SmoothRotationRadius neighbours.
    /// Higher values produce smoother orientation curves at the cost of deviating
    /// further from the mesh surface. Range [1, 50]. Defaults to 5.
    /// </summary>
    public int  SmoothRotationRadius { get; init; } = 5;

    /// <summary>
    /// Maximum allowed orientation change in degrees per mm of travel.
    /// A bidirectional slew-rate pass clamps consecutive normal changes so the robot
    /// never needs to rotate faster than this rate, preventing KUKA axis overspeed
    /// at sharp turns. 0 = disabled (no rate limit).
    /// </summary>
    public float SmoothRotationMaxRateDegPerMm { get; init; } = 0f;
}
