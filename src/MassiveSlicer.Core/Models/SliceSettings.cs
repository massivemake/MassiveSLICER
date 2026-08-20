using System.Numerics;

namespace MassiveSlicer.Core.Models;

/// <summary>Parameters snapshot passed to the planar slicer. All distances are in mm.</summary>
public sealed class SliceSettings
{
    /// <summary>Normal = volumetric shells + infill; Surface = boundary-focused cladding paths.</summary>
    public SlicingMode SlicingMode { get; init; } = SlicingMode.Normal;

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

    /// <summary>Linear speed for wipe extrusion moves in m/s.</summary>
    public float WipeSpeed { get; init; } = 0.12f;

    /// <summary>
    /// Trailing wipe ramp distance (mm). Positive: last N mm of <see cref="WipeLengthMm"/> ramps RPM to zero.
    /// Negative: after the full wipe length, extend an additional |N| mm with ramp-down (squeeze segment).
    /// </summary>
    public float WipeRampMm { get; init; } = 5f;

    /// <summary>
    /// When true, skip wipe insertion before travels shorter than
    /// <c>2 ×</c> the layer height (short gaps do not get a wipe).
    /// </summary>
    public bool WipeSkipShortTravels { get; init; }

    /// <summary>
    /// Brim: outward offset loops around the full first-layer footprint for bed adhesion.
    /// Applied as the LAST toolpath step so first-layer additions (X-bracing, patterns)
    /// are enclosed.
    /// </summary>
    public bool BrimEnabled { get; init; }

    /// <summary>Number of brim offset loops (spaced one bead width apart).</summary>
    public int BrimLoops { get; init; } = 3;

    /// <summary>
    /// Fixed brim print speed (mm/s), independent of print speed and of the Adaptive Speed
    /// window. The brim is bed adhesion, not part shape — it has no reason to follow the
    /// part's speed rule, and following it made the brim the fastest move in the print
    /// (and the one that hit the 99 % RPM export gate). Capped at
    /// <see cref="MaxBrimSpeedMmS"/>. RPM follows the speed, so flow stays correct.
    /// </summary>
    public float BrimSpeedMmS { get; init; } = 60f;

    /// <summary>Upper bound on <see cref="BrimSpeedMmS"/> — a brim never wants to be quick.</summary>
    public const float MaxBrimSpeedMmS = 60f;

    /// <summary>
    /// Absolute extrusion RPM (%) for the brim. 0 = off, i.e. let RPM follow brim speed.
    /// Set it to lay a deliberately fat brim for adhesion despite the slow speed. Capped at
    /// <see cref="MaxBrimRpmPercent"/> so it can never trip the export gate on its own.
    /// </summary>
    public float BrimRpmPercent { get; init; }

    /// <summary>Upper bound on <see cref="BrimRpmPercent"/>, matching the export RPM gate.</summary>
    public const float MaxBrimRpmPercent = 99f;

    /// <summary>Material flow rate (rev/cm³) for RPM ramp scaling.</summary>
    public float FlowRate { get; init; } = 0.463f;

    /// <summary>Stepped speed/RPM ramp after each travel before full extrusion resumes.</summary>
    public bool ResumeRampEnabled { get; init; }

    /// <summary>Ramp start print speed in m/s (e.g. 0.0005 = 0.5 mm/s).</summary>
    public float ResumeRampStartSpeedMps { get; init; } = 0.0005f;

    /// <summary>Ramp start extruder motor speed in percent (e.g. 1 = 1 %).</summary>
    public float ResumeRampStartRpmPercent { get; init; } = 1f;

    /// <summary>Total ramp distance in mm along the extrusion path (e.g. 609.6 ≈ 2 ft).</summary>
    public float ResumeRampDistanceMm { get; init; } = 609.6f;

    /// <summary>Number of discrete speed/RPM steps over <see cref="ResumeRampDistanceMm"/>.</summary>
    public int ResumeRampSteps { get; init; } = 10;

    /// <summary>When true, print speed and RPM scale per layer between min and max rates.</summary>
    public bool LayerSpeedAdaptEnabled { get; init; }

    /// <summary>Layer metric used for speed interpolation.</summary>
    public LayerSpeedBasis LayerSpeedBasis { get; init; } = LayerSpeedBasis.CutLength;

    /// <summary>Print speed (mm/s) applied to the shortest/lightest layer.</summary>
    public float LayerSpeedMinMmS { get; init; } = 10f;

    /// <summary>Print speed (mm/s) applied to the longest/busiest layer.</summary>
    public float LayerSpeedMaxMmS { get; init; } = 100f;

    /// <summary>
    /// State the adaptive-speed range as extruder RPM percent instead of robot mm/s. Each layer's
    /// speed is then derived from its own real thickness, so the commanded flow lands on the target
    /// whether the layer is thin or full height — and the target cannot be set past the export gate.
    /// </summary>
    public bool LayerSpeedUseRpmPercent { get; init; }

    /// <summary>Extruder RPM (%) aimed for on the shortest/lightest layer. RPM-percent mode only.</summary>
    public float LayerSpeedMinRpmPercent { get; init; } = 40f;

    /// <summary>Extruder RPM (%) aimed for on the longest/busiest layer. RPM-percent mode only.</summary>
    public float LayerSpeedMaxRpmPercent { get; init; } = 85f;

    /// <summary>
    /// Ceiling on the robot speed the RPM target may ask for (mm/s). 0 = fall back to
    /// <see cref="LayerSpeedMaxMmS"/>. A 1 mm layer can honour a high RPM target only at speeds the
    /// arm may not hold through curves, so the extruder is not the only limit that matters.
    /// </summary>
    public float LayerSpeedRobotMaxMmS { get; init; }

    /// <summary>Z height above the part to approach before each pass, in mm.</summary>
    public float ApproachZ { get; init; } = 50f;

    /// <summary>Tilt around the Y-axis in degrees for the Angled method (leans the plane toward ±X).</summary>
    // ── Live effector (MassiveCODE port): world-space points that locally boost
    //    the pattern amplitude with a smoothstep bell falloff. ─────────────────
    /// <summary>Enabled effector positions (world mm). Empty = no effector.</summary>
    public IReadOnlyList<System.Numerics.Vector3> EffectorPoints { get; set; } = [];
    /// <summary>Effector influence radius (mm).</summary>
    public float EffectorRadiusMm { get; set; } = 400f;
    /// <summary>Amplitude boost at an effector's centre (mm). Amplify mode only.</summary>
    public float EffectorStrengthMm { get; set; } = 30f;
    /// <summary>What the effector does inside its influence bell.</summary>
    public EffectorMode EffectorMode { get; set; } = EffectorMode.Amplify;

    // ── Pattern & texture (MassiveCODE effector port) ─────────────────────
    /// <summary>Decorative wall pattern applied to the toolpath after slicing.</summary>
    public Slicing.Effects.PatternType PatternType { get; init; } = Slicing.Effects.PatternType.Smooth;

    /// <summary>How the pattern wraps the part: evenly by path distance, or by polar angle.</summary>
    public MassiveSlicer.Core.Slicing.Effects.PatternMappingMode PatternMapping { get; init; }
        = MassiveSlicer.Core.Slicing.Effects.PatternMappingMode.ArcLength;
    /// <summary>Pattern relief depth in mm (0 disables).</summary>
    public float PatternAmplitude { get; init; } = 0f;
    /// <summary>Pattern repetitions around the part.</summary>
    public float PatternFrequency { get; init; } = 15f;
    /// <summary>Rotates the pattern with height (degrees per mm).</summary>
    public float PatternTwistDegPerMm { get; init; } = 0f;
    /// <summary>Phase rotation of the pattern around the part (degrees).</summary>
    public float PatternOffsetDeg { get; init; } = 0f;
    /// <summary>Ease-in distance from the bottom (mm).</summary>
    public float PatternFadeInMm { get; init; } = 0f;
    /// <summary>Ease-out distance to the top (mm).</summary>
    public float PatternFadeOutMm { get; init; } = 0f;

    /// <summary>
    /// How far decorative effects (Wave, Pattern) reach into the part. Structure left out of
    /// scope stays straight, but its ends still follow the wall so it stays bonded — see
    /// <c>SkinOnlyBracing</c>.
    /// </summary>
    public PatternScope PatternScope { get; init; } = PatternScope.Everything;

    public float TiltAngle { get; init; } = 0f;

    /// <summary>Tilt around the X-axis in degrees for the Angled method (leans the plane toward ±Y).</summary>
    public float TiltAngleX { get; init; } = 0f;

    /// <summary>
    /// XY direction used to project-align seams across layers.
    /// The contour vertex with the highest dot-product against this direction becomes the seam start.
    /// Defaults to (0, 1) -- back of model (max Y).
    /// </summary>
    public Vector2 SeamDirection { get; init; } = new(0f, 1f);

    /// <summary>User-placed seam guides. When non-empty, seams align to the nearest guide per contour.</summary>
    public IReadOnlyList<SeamGuidePoint> SeamGuidePoints { get; init; } = [];

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

    /// <summary>
    /// Smallest triangle (mm²) allowed to dictate a layer's thickness. 0 = derive it from the
    /// bead footprint; negative = off, every triangle votes as before.
    ///
    /// Layer thickness is the minimum demand of any single triangle crossing that Z, unweighted,
    /// so one sliver outvotes the whole cross-section. Measured on a real part: 277 of 281
    /// constrained layers were decided by a triangle under a tenth of the average area it beat,
    /// with the deciding triangles running 0.95–8 mm² against 2,500–5,000 faces crossing.
    ///
    /// The reasoning behind the default: a surface feature smaller than a single bead cannot be
    /// reproduced by the machine anyway, so it has no business setting layer thickness. Faces
    /// below the threshold still constrain when NOTHING clears it — a fully slivered cross-section
    /// falls back to the old behaviour rather than silently going to full thickness.
    /// </summary>
    public float AdaptiveMinFaceAreaMm2 { get; init; } = 0f;

    /// <summary>
    /// Largest thickness change allowed between adjacent layers (mm). 0 = off.
    ///
    /// Both height rules choose each layer independently — adaptive asks what the stairstep
    /// tolerance allows at this Z, support-driven asks how thin the bead must be to land on the
    /// layer below — and neither looks at what its neighbours got. Nothing stops 4.00 → 2.61 →
    /// 4.00 on consecutive layers.
    ///
    /// That matters because extruder RPM tracks real thickness (see
    /// <see cref="Slicing.Effects.LayerHeightFlowPostProcessor"/>) while robot speed usually does
    /// not, so a thickness cliff becomes an RPM cliff. Measured on a 392-layer column at flat
    /// 85 mm/s: one boundary moved RPM 25.5 points in a single layer. With a known extruder
    /// transport lag of order seconds, a step that size cannot land where it is commanded — it
    /// shows on the part as bands of visibly different thickness.
    ///
    /// <para><b>It can only ever make a layer THINNER.</b> A rise is limited by thinning the
    /// layer above; a drop by thinning the layers below so the ladder walks down into a thin
    /// region instead of falling into it. Both directions thin, and thinner always satisfies
    /// both the stairstep tolerance and the overlap target — those are upper bounds. So the
    /// layer that genuinely needs to be thin still gets its thickness; nothing is traded away
    /// to buy the smoothing.</para>
    /// </summary>
    public float MaxLayerHeightChangeMm { get; init; } = 0f;

    // -- Support-driven layer height (3b) ------------------------------------------

    /// <summary>
    /// Thin a layer when the boundary steps sideways far enough that the bead would not sit on
    /// the one below. <b>Off by default</b> — it changes slice output, so it is opt-in.
    ///
    /// This is the only rule that chooses thickness from MEASURED overlap rather than from
    /// triangle normals. <see cref="AdaptiveLayerHeight"/> answers a surface-finish question
    /// (stairstepping); this answers an adhesion question. They compose: the thinner wins.
    /// </summary>
    public bool SupportDrivenLayerHeight { get; init; } = false;

    /// <summary>
    /// How much of each bead must sit on the one below, as a percentage. 60 means the bead may
    /// hang off by 40 % of its width, i.e. a sideways step of 0.4 x bead width.
    ///
    /// Jeff's minimum is 50 %; 60 is his deliberate overcorrection so an under-extruding bead
    /// still lands safe — over-extrusion is already safe. Measured cost of 50 -> 60 on a real
    /// part: layers thinned 20 -> 99, extra layers +1 -> +10 of 377.
    /// </summary>
    public float SupportOverlapTargetPercent { get; init; } = 60f;

    /// <summary>
    /// How long a continuous under-target stretch may be before the layer is thinned (mm).
    /// 0 = derive it as 2 x bead width. A bead spans a short gap, so a speck of unsupported
    /// path is not worth thinning a whole layer for.
    ///
    /// Deliberately an absolute length, NOT a percentage of the layer: 1 % of a 1500 mm layer
    /// and 1 % of a 300 mm layer are not the same physical defect. Measured on a real part, the
    /// stretches a 2x-bead tolerance excuses are 99-of-105 one-offs that never stack, while every
    /// vertically stacked region (up to 6 layers deep) had stretches of 29 mm or more — so this
    /// cannot hide the runs that actually fail.
    /// </summary>
    public float SupportBridgeToleranceMm { get; init; } = 0f;

    /// <summary>
    /// True when any enabled rule can make a layer's REAL thickness differ from
    /// <see cref="LayerHeight"/> — so extrusion flow must follow the measured thickness rather
    /// than the nominal one.
    ///
    /// This exists so the flow correction keys off ONE named concept instead of a growing list of
    /// feature flags. <see cref="SupportDrivenLayerHeight"/> was added without joining that list
    /// and silently handed every layer it thinned a full nominal layer's worth of material —
    /// measured at 1.5x over-extrusion on 37 % of layers. Any future rule that moves a slice
    /// plane belongs in this property, not in a new condition somewhere downstream.
    /// </summary>
    public bool VariesLayerThickness
        => AdaptiveLayerHeight || SupportDrivenLayerHeight || MaxLayerHeightChangeMm > 1e-4f;

    /// <summary>Sideways step (mm) at which a bead is considered under target.</summary>
    public float SupportTargetOffsetMm
        => BeadWidth * (1f - Math.Clamp(SupportOverlapTargetPercent, 0f, 100f) / 100f);

    /// <summary><see cref="SupportBridgeToleranceMm"/> with 0 resolved to 2 x bead width.</summary>
    public float ResolvedBridgeToleranceMm
        => SupportBridgeToleranceMm > 1e-4f ? SupportBridgeToleranceMm : 2f * BeadWidth;

    /// <summary>
    /// <see cref="AdaptiveMinFaceAreaMm2"/> with 0 resolved to the default: one bead's side
    /// footprint, bead width × the thinnest layer the slicer may choose. Returns 0 when the gate
    /// is off, which the planner reads as "every triangle votes".
    ///
    /// On a real part the deciding triangles measured 0.95–8 mm² against a 12 mm² footprint, while
    /// the two legitimate ones were 261 and 304 mm² — so the default separates them with room to
    /// spare rather than splitting a close call.
    /// </summary>
    public float ResolvedMinFaceAreaMm2
    {
        get
        {
            if (AdaptiveMinFaceAreaMm2 < 0f) return 0f;                  // explicitly off
            if (AdaptiveMinFaceAreaMm2 > 0f) return AdaptiveMinFaceAreaMm2;

            float thinnest = MinLayerHeight > 1e-4f
                ? MathF.Min(MinLayerHeight, LayerHeight)
                : LayerHeight;
            float area = BeadWidth * thinnest;
            return area > 1e-4f ? area : 0f;
        }
    }

    // -- X-Bracing wall (structural notches) ------------------------------------

    /// <summary>
    /// When true, cut dual-wall X-bracing notches into the perimeter (Formbound-style
    /// slits) for back-support on thin walls. Independent of infill pattern.
    /// </summary>
    public bool XBracingEnabled { get; init; }

    /// <summary>How far each brace goes into the wall from the perimeter (mm).
    /// This is the depth at the TOP of the part; see <see cref="XBracingDepthBottomMm"/>
    /// to taper it over height.</summary>
    public float XBracingDepthMm { get; init; } = 50f;

    /// <summary>
    /// Brace depth at the BOTTOM of the part (mm). When &gt; 0 the depth interpolates
    /// with height from this at the part's lowest slice to
    /// <see cref="XBracingDepthMm"/> at the top (e.g. a deeper base than tip on a
    /// cylinder). 0 (default) = constant depth equal to <see cref="XBracingDepthMm"/>.
    /// Curve shape uses <see cref="XBracingDepthEaseBottom"/> / <see cref="XBracingDepthEaseTop"/>.
    /// </summary>
    public float XBracingDepthBottomMm { get; init; }

    /// <summary>
    /// Easing of the depth taper at the BOTTOM of the part: Linear, Ease-In, Ease-Out, Smooth.
    /// Controls the start slope of the height→depth curve (how quickly depth leaves the bottom value).
    /// </summary>
    public string XBracingDepthEaseBottom { get; init; } = "Linear";

    /// <summary>
    /// Easing of the depth taper at the TOP of the part: Linear, Ease-In, Ease-Out, Smooth.
    /// Controls the end slope of the height→depth curve (how depth settles to the top value).
    /// </summary>
    public string XBracingDepthEaseTop { get; init; } = "Linear";

    /// <summary>Horizontal span of one full X cell along the wall (mm).</summary>
    public float XBracingSpanMm { get; init; } = 120f;

    /// <summary>
    /// Brace angle from vertical (deg). Smaller = more printable (shallower overhang
    /// when built bottom-up). Typical 25–40°.
    /// </summary>
    public float XBracingAngleDeg { get; init; } = 30f;

    /// <summary>
    /// When true, place partial X cells at the left/right ends of the wall so braces
    /// reach the vertical edges. Top and bottom of the part are never extended.
    /// </summary>
    public bool XBracingExtendEdges { get; init; } = true;

    /// <summary>
    /// Brace-direction plane tilt about Y (deg). Same convention as angled slice:
    /// normal = (sin Y, −sin X · cos Y, cos X · cos Y). Hairpins grow along the
    /// XY projection of this normal (perpendicular to the plane).
    /// </summary>
    public float XBracingPlaneTiltY { get; init; }

    /// <summary>Brace-direction plane tilt about X (deg). See <see cref="XBracingPlaneTiltY"/>.</summary>
    public float XBracingPlaneTiltX { get; init; }

    /// <summary>
    /// How brace direction is projected onto the surface:
    /// <c>Planar</c> (default) or <c>Cylinder</c> (radial from a vertical cylinder).
    /// </summary>
    public string XBracingProjectionType { get; init; } = "Planar";

    /// <summary>Cylinder projection diameter (mm). Height is taken from the part AABB.</summary>
    public float XBracingCylinderDiameterMm { get; init; } = 200f;

    /// <summary>Cylinder axis X on the bed (mm, world).</summary>
    public float XBracingCylinderX { get; init; }

    /// <summary>Cylinder axis Y on the bed (mm, world).</summary>
    public float XBracingCylinderY { get; init; }

    /// <summary>
    /// When false (default), cylinder braces pull toward the axis.
    /// When true, braces radiate outward from the axis.
    /// </summary>
    public bool XBracingCylinderFlipDirection { get; init; }

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
    /// Wave phase method.
    /// "A" (seam-anchored, original): each layer's phase counts from its contour start.
    ///     Layer-to-layer alignment can drift as the part's cross-section morphs.
    /// "B" (phase inheritance): each layer continues the phase of the layer below plus
    ///     stagger — constant layer-to-layer alignment everywhere, shape change absorbed
    ///     as a tiny bounded wavelength flex. Ignored when WaveCycles &gt; 0.
    /// </summary>
    public string WavePhaseMethod { get; init; } = "A";

    /// <summary>
    /// When true, open contours (panels, single-wall prints) alternate print direction each layer.
    /// Even layers print start→end; odd layers print end→start, eliminating the long return travel.
    /// Has no effect on closed contours.
    /// </summary>
    public bool ZigZagSeam { get; init; } = false;

    /// <summary>
    /// When <see cref="ZigZagSeam"/> is on and a layer has multiple open faces (islands),
    /// keep all of them and insert Travel moves (start/stop) between faces.
    /// When false, only the longest open face on each layer is printed.
    /// Default true — Multi-Planar organic panels often need multi-island travel.
    /// </summary>
    public bool ZigZagAllowSameLayerTravel { get; init; } = true;

    /// <summary>Spiral/vase mode: closed contours ramp continuously in Z (no stepped seam).</summary>
    public bool Spiralize { get; init; }

    /// <summary>Cycle size in mm for <see cref="MassiveSlicer.Core.Slicing.Effects.PatternMappingMode.Wavelength"/> mapping.</summary>
    public float PatternWavelengthMm { get; init; } = 60f;

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

    // -- Lightning Bridge infill ----------------------------------------------------

    /// <summary>Max unsupported overhang angle for lightning finger growth (degrees).
    /// Per-layer lateral step = min(layerHeight · tan(angle), 0.5 · BeadWidth).</summary>
    public float LightningOverhangDeg { get; init; } = 30f;

    /// <summary>Spacing between lightning finger roots along unsupported arcs (mm).
    /// 0 = auto (4 × BeadWidth).</summary>
    public float LightningBranchSpacingMm { get; init; } = 0f;

    /// <summary>Radius of the support pad loop at each finger tip (mm). 0 = plain
    /// rounded tip (one bead wide).</summary>
    public float LightningTipLoopRadiusMm { get; init; } = 0f;

    /// <summary>Allow fingers to anchor on interior boundaries (holes / inner walls —
    /// notches stay hidden inside the part).</summary>
    public bool LightningAnchorInterior { get; init; } = true;

    /// <summary>Allow fingers to anchor on the outer perimeter (notches visible on
    /// the outside surface).</summary>
    public bool LightningAnchorExterior { get; init; } = true;

    /// <summary>Grow sacrificial external support fins under OUTWARD overhangs
    /// (material added outside the part — cut away after printing).</summary>
    public bool LightningExteriorOverhangs { get; init; } = false;

    /// <summary>Formbound Buttress: length of the single-bead horizontal support bar
    /// (mm) under an overhang. The approach from the wall mouth morphs into this bar.
    /// Default 40 mm.</summary>
    public float LightningButtressBarMm { get; init; } = 40f;

    /// <summary>Formbound Buttress: prefer mouths on interior boundaries (holes /
    /// inner walls) before scarifying the outer face.</summary>
    public bool LightningPreferInteriorMouths { get; init; } = true;

    /// <summary>
    /// When true, Formbound Bridge/Buttress only grows under edit-mode Support
    /// selections (Bridge paint marks). Automatic geometric overhang detection is
    /// skipped so support is limited to what the user selected.
    /// </summary>
    public bool LightningTargetSupportSelections { get; init; }

    // ── Toolpath paint marks (brush tool in the preview view) ─────────────────

    /// <summary>World-space brush dabs painted on the toolpath. Bridge marks inject
    /// manual Formbound demand (fingers grow under the painted beads); Remove marks
    /// delete the painted beads and splice the gap with a travel. Persist with the
    /// workspace and survive re-slices.</summary>
    public IReadOnlyList<PaintMark> PaintMarks { get; init; } = [];

    /// <summary>Structural Support modifiers — 2×4 pockets / cylinder wraps spliced
    /// into the wall path at a fixed anchor so the neck stacks layer over layer.</summary>
    public IReadOnlyList<StructuralSupportSpec> StructuralSupports { get; init; } = [];

    // ── Multi-Planar slicing (a stack of guide planes) ───────────────────────

    /// <summary>Guide planes as (height % of the part, tilt °): the slicing plane's
    /// tilt interpolates linearly between adjacent guides as the print climbs.
    /// Constant tilt below the first and above the last guide. Minimum two planes.</summary>
    public IReadOnlyList<MultiPlanarPlane> MultiPlanarPlanes { get; init; } =
        [new(0f, 0f), new(50f, 15f), new(100f, 30f)];

    /// <summary>False = tilt about Y (planes lean along X, like <see cref="TiltAngle"/>);
    /// true = tilt about X (planes lean along Y).</summary>
    public bool MultiPlanarAxisX { get; init; } = false;

    // ── Thermomechanical simulation (analytical interlayer cooling) ──────────

    /// <summary>Melt temperature at deposition (°C) — hottest extruder zone.</summary>
    public float ThermalDepositTempC { get; init; } = 230f;

    /// <summary>Glass-transition (bonding-relevant) temperature of the material (°C).</summary>
    public float ThermalGlassTransitionC { get; init; } = 105f;

    /// <summary>Ambient / build-environment temperature (°C).</summary>
    public float ThermalAmbientTempC { get; init; } = 30f;

    /// <summary>Material density (g/cm³) for the thermal mass.</summary>
    public float ThermalDensityGmCc { get; init; } = 1.05f;

    /// <summary>Bonding limit = Tg + this margin (°C). Default 10.</summary>
    public float ThermalBondMarginC { get; init; } = 10f;

    /// <summary>Sag limit = Tg + this margin (°C). Default 45.</summary>
    public float ThermalSagMarginC { get; init; } = 45f;

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

    // -- Curved (interpolation / sweep) slicing -----------------------------------

    /// <summary>Vertex indices on the welded mesh forming the LOW (start) boundary ring.</summary>
    public IReadOnlyList<int> CurvedBoundaryLowVertices { get; init; } = [];

    /// <summary>Vertex indices on the welded mesh forming the HIGH (end) boundary ring.</summary>
    public IReadOnlyList<int> CurvedBoundaryHighVertices { get; init; } = [];

    /// <summary>How LOW/HIGH boundaries are supplied when vertex lists are empty.</summary>
    public CurvedBoundarySource CurvedBoundarySource { get; init; } = CurvedBoundarySource.AutoDetect;

    /// <summary>Z-band tolerance (mm) for auto-detect boundary rings.</summary>
    public float CurvedAutoDetectBandMm { get; init; } = 2f;

    /// <summary>When true, split mesh at saddle points before slicing (Y-shapes / branching).</summary>
    public bool CurvedEnableRegionSplit { get; init; } = true;

    /// <summary>
    /// Blends move normals between world +Z (0) and full surface/stacking follow (1).
    /// Lower values keep the toolhead more vertical for body clearance on curved paths.
    /// </summary>
    public float OrientationFollowStrength { get; init; } = 1f;

    /// <summary>Hard cap on TCP tilt from vertical in degrees, applied after the
    /// surface-follow blend (90 = uncapped). Guards flange clearance on steep shells.</summary>
    public float OrientationMaxTiltDeg { get; init; } = 90f;

    /// <summary>Force the first layer's tool orientation to vertical (world +Z) regardless
    /// of the surface-follow blend — flat-bed adhesion for Geodesic/Curved slicing.</summary>
    public bool FirstLayerZeroTilt { get; init; } = false;

    // -- Layer lean ("poor man's non-planar" for planar slicing) -------------------

    /// <summary>0..1: how strongly planar moves lean toward the nearest deposited
    /// material on the previous layer. 0 = off (vertical tool).</summary>
    public float LayerLeanStrength { get; init; } = 0f;

    /// <summary>Hard cap on layer-lean tilt from vertical, in degrees.</summary>
    public float LayerLeanMaxTiltDeg { get; init; } = 0f;
}

/// <summary>Live effector behaviour inside the influence radius.</summary>
/// <summary>What decorative effects (Wave, Pattern) are allowed to displace.</summary>
public enum PatternScope
{
    /// <summary>Every extrusion, including infill and bracing. Original behaviour.</summary>
    Everything,
    /// <summary>Perimeters only — slicer infill, X-bracing, Formbound fill and supports
    /// stay straight. Interior walls (cavity boundaries, modelled ribs) are still textured.</summary>
    WallsOnly,
    /// <summary>
    /// Whatever a horizontal ray can reach. Rays sweep each layer from every compass direction
    /// and the first thing hit is skin; everything shadowed behind it stays straight.
    /// <para>
    /// Needs no closed contours, which is why this rather than a nesting-depth test: scanned and
    /// organic parts slice into open chains, and "is this contour inside another one" has no
    /// answer for those — measured on one, all 6,676,002 wall moves came back at depth 0.
    /// </para>
    /// </summary>
    VisibleSkin,
}

public enum EffectorMode
{
    /// <summary>Boost the local pattern amplitude (smoothstep bell × strength).</summary>
    Amplify,
    /// <summary>Suppress the pattern toward zero — smooths the lines back to the
    /// plain wall inside the influence area (full erase at the centre).</summary>
    Erase,
}
