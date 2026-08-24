namespace MassiveSlicer.Core.Models;

/// <summary>
/// Application-wide preferences persisted across sessions.
/// Owned by <c>PreferencesViewModel</c>; shared with subsystems (viewport,
/// input handler) that need to read these values at runtime.
/// </summary>
public sealed class AppPreferences
{
    // ── Keep on bed ───────────────────────────────────────────────────────
    //
    // A part already sitting on the bed stays on it through an edit, instead of being left hanging
    // in the air or buried in the plate and needing a Drop to Plate afterwards.
    //
    // Deliberately "keep", not "always drop": these only hold down a part that was ALREADY resting
    // on the bed when the edit began. A part you have deliberately parked in mid-air is left where
    // you put it. That is the difference between a helpful default and one that keeps undoing your
    // positioning.
    //
    // Separate from the hard floor, which is not optional: a translate drag is always stopped at
    // the bed and anything pushed through it is always set back down, because printing through the
    // plate is never what anyone meant. These toggles govern the softer question of whether a part
    // should FOLLOW the bed down when an edit would otherwise lift it clear.

    /// <summary>Keep a part that was resting on the bed resting on it after a move.</summary>
    /// <remarks>
    /// Off by default, unlike the other two. Dragging a part upward IS the gesture for lifting one
    /// off the plate, so keeping it planted through a move means a resting part cannot be raised at
    /// all — it drops straight back the moment the drag commits. Useful if you never lift parts and
    /// want them pinned; surprising otherwise.
    /// </remarks>
    public bool KeepOnBedWhenMoving { get; set; }

    /// <summary>Keep a part that was resting on the bed resting on it after a rotation.</summary>
    public bool KeepOnBedWhenRotating { get; set; } = true;

    /// <summary>Keep a part that was resting on the bed resting on it after a resize.</summary>
    public bool KeepOnBedWhenScaling { get; set; } = true;

    // ── Navigation ────────────────────────────────────────────────────────

    /// <summary>
    /// When true, the orbit pivot depth adjusts to the geometry under the cursor
    /// rather than using a fixed world-origin pivot.
    /// </summary>
    public bool AutoDepth { get; set; } = true;

    /// <summary>
    /// When true, the orbit pivot is the centre of the current selection
    /// instead of the world origin.
    /// </summary>
    public bool OrbitAroundSelection { get; set; } = true;

    /// <summary>Active mouse-button navigation preset.</summary>
    public NavigationPresetId ActivePreset { get; set; } = NavigationPresetId.Rhino;

    // ── Touchpad gestures (Touchpad preset only; see ViewportView.OnPointerWheelChanged) ──

    /// <summary>Two-finger pan speed multiplier. Higher = the part moves further per swipe.</summary>
    public float TouchpadPanSpeed { get; set; } = 9f;

    /// <summary>Cmd + two-finger rotate speed multiplier.</summary>
    public float TouchpadOrbitSpeed { get; set; } = 2f;

    /// <summary>Shift + two-finger zoom speed multiplier.</summary>
    public float TouchpadZoomSpeed { get; set; } = 1f;

    /// <summary>
    /// When true, the VERTICAL axis of two-finger pan is inverted (swipe up → part moves down).
    /// Horizontal always follows the fingers. Default false = part follows fingers on both axes.
    /// </summary>
    public bool TouchpadInvertPan { get; set; }

    // ── Performance ───────────────────────────────────────────────────────

    /// <summary>Enable multi-sample anti-aliasing in the OpenGL viewport.</summary>
    public bool AntiAliasing { get; set; } = true;

    // ── Appearance ────────────────────────────────────────────────────────

    /// <summary>Active colour theme name (matches a Resources/Themes/*.axaml file).</summary>
    public string ActiveTheme { get; set; } = "MassiveMake";

    /// <summary>Path of the backdrop image loaded at startup, or null for none.</summary>
    public string? DefaultBackdropPath { get; set; }

    /// <summary>Mipmap LOD blur level (0 = sharp, 7 = max).</summary>
    public float DefaultBackdropBlur { get; set; } = 2.5f;

    /// <summary>Backdrop blend over shader background (0 = shader only, 1 = full HDR).</summary>
    public float DefaultBackdropOpacity { get; set; } = 1f;

    /// <summary>Whether the ground-plane grid lines are rendered.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>Whether the world-axis indicator is rendered in the viewport.</summary>
    public bool ShowAxes { get; set; } = false;

    /// <summary>Whether the print-bed boundary grid overlay is rendered.</summary>
    public bool ShowBedGrid { get; set; } = true;

    /// <summary>
    /// Per-cell default home position name, keyed by cell name (e.g. "LFAM 2" → "Home").
    /// Restored when the user switches to a cell.
    /// </summary>
    public Dictionary<string, string> DefaultHomePositionNames { get; set; } = [];

    /// <summary>
    /// Preferred cell to open on a cold start (no workspace on the command line).
    /// Matched against discovered cell display names (case-insensitive contains).
    /// Stored in local prefs.json only — per machine. Null/empty falls back to LFAM 2.
    /// </summary>
    public string? DefaultCellName { get; set; }

    /// <summary>Name of the last selected material preset, or null for none.</summary>
    public string? SelectedMaterialPresetName { get; set; }

    // ── Lighting ──────────────────────────────────────────────────────────

    /// <summary>Horizontal rotation of the key light around Z, in degrees.</summary>
    public float LightAzimuth { get; set; } = 45f;

    /// <summary>Vertical angle of the key light above the XY plane, in degrees.</summary>
    public float LightElevation { get; set; } = 45f;

    /// <summary>Directional light intensity multiplier.</summary>
    public float LightIntensity { get; set; } = 1f;

    // ── Shader / view ─────────────────────────────────────────────────────

    /// <summary>Active viewport shader mode name (matches ShaderMode enum).</summary>
    public string ShaderMode { get; set; } = "Standard";

    /// <summary>Per-view-mode display profiles (JSON: Body/Toolpath/Speed/RPM/Preview).</summary>
    public string? ViewModeProfiles { get; set; }

    /// <summary>Whether mesh edges are drawn over the shaded surface.</summary>
    public bool ShowEdges { get; set; } = false;

    /// <summary>Whether the ground-plane shadow catcher is active.</summary>
    public bool ShadowCatcherEnabled { get; set; } = true;

    /// <summary>Contact shadow spread multiplier (1 = default tuned size).</summary>
    public float ContactShadowSize { get; set; } = 1f;

    /// <summary>Contact shadow darkness multiplier (1 = default tuned strength).</summary>
    public float ContactShadowDarkness { get; set; } = 1f;

    public float ContactShadowBlur { get; set; } = 1f;

    /// <summary>Blender-style viewport cavity accentuation.</summary>
    public bool CavityEnabled { get; set; }

    /// <summary>Cavity sampling mode: Screen, World, or Both.</summary>
    public string CavityMode { get; set; } = "Both";

    public float CavityScreenRidge  { get; set; } = 1f;
    public float CavityScreenValley { get; set; } = 1f;
    public float CavityWorldRidge   { get; set; } = 1f;
    public float CavityWorldValley  { get; set; } = 1f;
    public float CavityWorldDistance { get; set; } = 5f;

    // ── Toolpath colors (AARRGGBB hex) ────────────────────────────────────

    public string ToolpathExtrudeColor    { get; set; } = "#FFFFFFFF";
    public string ToolpathBeadColor       { get; set; } = "#FFF2F2F2";
    public string ToolpathTravelColor     { get; set; } = "#FFD92E2E";
    public string ToolpathWipeColor       { get; set; } = "#FFFF8800";
    public string ToolpathRetractionColor { get; set; } = "#FF9C27B0";
    public string ToolpathSeamColor       { get; set; } = "#FFFFE600";
    public string ToolpathUnselectedColor { get; set; } = "#FF616161";

    // ── Additive slicing ──────────────────────────────────────────────────

    /// <summary>Layer height in mm.</summary>
    public double LayerHeight { get; set; } = 3.0;

    /// <summary>Bead width in mm.</summary>
    public double BeadWidth { get; set; } = 6.0;

    /// <summary>First-layer height override in mm.</summary>
    public double FirstLayerHeight { get; set; } = 3.0;

    /// <summary>When true, planar slicing adapts layer spacing to local surface slope.</summary>
    public bool AdaptiveLayerHeight { get; set; }

    /// <summary>0 = finest adaptive detail, 1 = fastest adaptive print.</summary>
    public double AdaptiveQuality { get; set; } = 0.5;

    /// <summary>Minimum layer height used by adaptive slicing (mm).</summary>
    public double MinLayerHeight { get; set; } = 1.0;

    /// <summary>Skip bead-width/2 contour inset; print centerline on surface.</summary>
    public bool DisableContourOffset { get; set; }

    /// <summary>Seam mode display: Normal or Zig-zag.</summary>
    public string SeamMode { get; set; } = "Normal";

    /// <summary>
    /// Zig-zag: when multiple open faces exist on one layer, keep them and Travel
    /// (start/stop) between faces. When false, only the longest face is printed.
    /// </summary>
    public bool ZigZagAllowSameLayerTravel { get; set; } = true;

    /// <summary>When true, tool tilts toward mesh surface on overhangs.</summary>
    public bool OverhangOrientation { get; set; }

    /// <summary>Maximum tool tilt from vertical for overhang orientation (degrees).</summary>
    public double MaxOverhangTiltDeg { get; set; } = 45.0;

    /// <summary>Smooth per-move toolhead normals after slicing.</summary>
    public bool SmoothRotation { get; set; }

    public int SmoothRotationRadius { get; set; } = 5;
    public double SmoothRotationMaxRateDegPerMm { get; set; }

    /// <summary>Infill pattern display: None, Rectilinear, Grid, Triangle, Ghost Mesh Grid.</summary>
    public string InfillPattern { get; set; } = "None";

    public double InfillSpacingMm { get; set; }
    public double InfillAngleDeg { get; set; }

    /// <summary>Lightning Bridge: max unsupported overhang angle (deg).</summary>
    public double LightningOverhangDeg { get; set; } = 30.0;

    /// <summary>Lightning Bridge: finger root spacing (mm). 0 = auto.</summary>
    public double LightningBranchSpacingMm { get; set; }

    /// <summary>Lightning Bridge: tip support-pad loop radius (mm). 0 = off.</summary>
    public double LightningTipLoopRadiusMm { get; set; }

    /// <summary>Lightning Bridge: root fingers on interior boundaries (hidden notches).</summary>
    public bool LightningAnchorInterior { get; set; } = true;

    /// <summary>Lightning Bridge: root fingers on the outer perimeter.</summary>
    public bool LightningAnchorExterior { get; set; } = true;

    /// <summary>Lightning Bridge: sacrificial external fins under outward overhangs.</summary>
    public bool LightningExteriorOverhangs { get; set; }

    /// <summary>Formbound Buttress: horizontal support bar length (mm), single bead.</summary>
    public double LightningButtressBarMm { get; set; } = 40.0;

    /// <summary>Formbound Buttress: prefer interior mouths over exterior face cuts.</summary>
    public bool LightningPreferInteriorMouths { get; set; } = true;

    /// <summary>
    /// Formbound Bridge/Buttress: only grow support under edit-mode Support
    /// selections (skip automatic overhang detection).
    /// </summary>
    public bool LightningTargetSupportSelections { get; set; }

    /// <summary>Multi-Planar guide planes as [heightPct, angleDeg] pairs.</summary>
    public List<double[]> MultiPlanarPlanes { get; set; } =
        [[0, 0], [50, 15], [100, 30]];

    /// <summary>Multi-Planar: tilt about X instead of Y.</summary>
    public bool MultiPlanarAxisX { get; set; }

    /// <summary>Brim: outward offset loops around the first layer for bed adhesion.</summary>
    public bool BrimEnabled { get; set; }

    /// <summary>Number of brim offset loops.</summary>
    public int BrimLoops { get; set; } = 3;

    /// <summary>Fixed brim print speed (mm/s), independent of print speed and Adaptive Speed.</summary>
    public double BrimSpeedMmS { get; set; } = 60.0;

    /// <summary>Absolute brim extrusion RPM (%). 0 = let RPM follow brim speed.</summary>
    public double BrimRpmPercent { get; set; }

    /// <summary>X-Bracing Wall: cut dual-wall X notches for structural back-support.</summary>
    public bool XBracingEnabled { get; set; }

    /// <summary>Brace depth into the wall at the TOP of the part (mm).</summary>
    public double XBracingDepthMm { get; set; } = 50.0;

    /// <summary>X-bracing depth taper ease at the bottom (Linear / Ease-In / Ease-Out / Smooth).</summary>
    public string XBracingDepthEaseBottom { get; set; } = "Linear";

    /// <summary>X-bracing depth taper ease at the top (Linear / Ease-In / Ease-Out / Smooth).</summary>
    public string XBracingDepthEaseTop { get; set; } = "Linear";

    /// <summary>Brace depth at the BOTTOM of the part (mm); 0 = constant (same as top).</summary>
    public double XBracingDepthBottomMm { get; set; }

    /// <summary>Horizontal span of one X cell (mm).</summary>
    public double XBracingSpanMm { get; set; } = 120.0;

    /// <summary>Brace angle from vertical (deg) — lower is more printable.</summary>
    public double XBracingAngleDeg { get; set; } = 30.0;

    /// <summary>Partial X cells on left/right wall ends (not top/bottom).</summary>
    public bool XBracingExtendEdges { get; set; } = true;

    /// <summary>
    /// Show the brace plane / cylinder helper in the viewport.
    /// Must always be written to .mass JSON: SaveOptions use
    /// <c>WhenWritingDefault</c>, and <c>default(bool)</c> is false — so an omitted
    /// field would leave the initializer <c>true</c> on load and "off" would never stick.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool XBracingShowHelper { get; set; } = true;

    /// <summary>X-bracing direction plane tilt about Y (deg).</summary>
    public double XBracingPlaneTiltY { get; set; }

    /// <summary>X-bracing direction plane tilt about X (deg).</summary>
    public double XBracingPlaneTiltX { get; set; }

    /// <summary>X-bracing projection: Planar or Cylinder.</summary>
    public string XBracingProjectionType { get; set; } = "Planar";

    /// <summary>Cylinder projection diameter (mm).</summary>
    public double XBracingCylinderDiameterMm { get; set; } = 200.0;

    /// <summary>Cylinder axis X on the bed (mm).</summary>
    public double XBracingCylinderX { get; set; }

    /// <summary>Cylinder axis Y on the bed (mm).</summary>
    public double XBracingCylinderY { get; set; }

    /// <summary>When true, cylinder braces radiate outward; default is pull toward axis.</summary>
    public bool XBracingCylinderFlipDirection { get; set; }

    /// <summary>Wave effect display: None, Sine, Sawtooth, Triangle.</summary>
    public string WaveEffect { get; set; } = "None";

    public double WaveAmplitude { get; set; } = 3.0;
    public string WaveFrequencyMode { get; set; } = "Wavelength";
    public double WaveWavelength { get; set; } = 20.0;
    public int WaveCycles { get; set; }
    public double WaveShape { get; set; } = 1.0;
    public double WaveStagger { get; set; }
    public int WavePhaseMethodIndex { get; set; }   // 0 = Method A (seam), 1 = Method B (inherit)
    public bool WaveGradient { get; set; }
    public double WaveAmplitudeBottom { get; set; }
    public double WaveAmplitudeTop { get; set; } = 3.0;
    public double WaveWavelengthBottom { get; set; } = 20.0;
    public double WaveWavelengthTop { get; set; } = 20.0;
    public double WaveGradientCenter { get; set; } = 0.5;
    public string WaveGradientCurve { get; set; } = "Linear";

    /// <summary>Pattern &amp; Texture card: selected pattern tile (matches Core PatternType).</summary>
    public string PatternType { get; set; } = "Smooth";

    /// <summary>Pattern distribution: "Wavelength (mm)", "Even (path length)", or "Radial (angle)".</summary>
    public string PatternMapping { get; set; } = "Wavelength (mm)";
    /// <summary>How far Wave/Pattern reach: Everything / Walls only / Visible skin (raycast).
    /// An unrecognised value falls back to Everything, so workspaces saved with a retired
    /// option still load.</summary>
    public string PatternScope { get; set; } = "Everything";

    public double PatternWavelengthMm { get; set; } = 60.0;
    public double PatternAmplitude { get; set; }
    public double PatternFrequency { get; set; } = 15.0;
    public double PatternTwist { get; set; }
    public double PatternOffset { get; set; }
    public double PatternFadeIn { get; set; }
    public double PatternFadeOut { get; set; }

    /// <summary>KRL export: ±°C adjustment applied to all zones ("" = no change).</summary>
    public string TemperatureOffset { get; set; } = "";

    /// <summary>KRL export: ±% adjustment to extrusion speed ("" = no change).</summary>
    public string ExtrusionSpeedOffset { get; set; } = "";

    /// <summary>
    /// Calibration-only: forces the exported screw speed (%) regardless of bead geometry.
    /// 0 = off (normal computed flow). Written ONLY by the purge-and-weigh calibration
    /// workspace. It previously abused <see cref="ExtrusionSpeedOffset"/> for this, which is
    /// a field operators use on real jobs — a calibration run could leave a large number in
    /// it and silently inflate the flow of every part sliced afterwards. Export raises a
    /// warning whenever this is non-zero so it can never apply unnoticed.
    /// </summary>
    public double ExtrusionRpmOverridePercent { get; set; }


    /// <summary>Active slicing algorithm name (matches SliceMethod enum).</summary>
    public string SliceMethod { get; set; } = "Planar";

    /// <summary>Normal or Surface slicing strategy for planar/angled methods.</summary>
    public string SlicingMode { get; set; } = "Normal";

    /// <summary>Pass rotation angle in degrees.</summary>
    public double PassAngle { get; set; } = 0.0;

    /// <summary>Tilt around Y-axis in degrees.</summary>
    public double TiltAngle { get; set; } = 0.0;

    /// <summary>Tilt around X-axis in degrees.</summary>
    public double TiltAngleX { get; set; } = 0.0;

    /// <summary>Deposition print speed in mm/s.</summary>
    public double PrintSpeed { get; set; } = 100.0;

    /// <summary>Master toggle for first-layer speed/RPM adjustments. When false the first layer
    /// prints at the normal speed/RPM and the override fields are ignored on export.</summary>
    public bool FirstLayerAdjustmentsEnabled { get; set; }

    /// <summary>First-layer print speed override (mm/s). 0 = use the calculated (normal) speed.</summary>
    public double FirstLayerSpeed { get; set; }

    /// <summary>First-layer extrusion RPM override (%). 0 = use the calculated RPM.</summary>
    public double FirstLayerRpm { get; set; }

    /// <summary>Travel (PTP) speed in mm/s.</summary>
    public double TravelSpeed { get; set; } = 120.0;

    /// <summary>Acceleration as a percentage of robot maximum (1–100).</summary>
    public int Acceleration { get; set; } = 100;

    /// <summary>Approach Z height above part in mm.</summary>
    public double ApproachZ { get; set; } = 50.0;

    /// <summary>Vertical z-hop on travel moves in mm.</summary>
    public double ZHopMm { get; set; }

    /// <summary>Wipe mode display: Off, Retrace, Same-Direction.</summary>
    public string WipeModeDisplay { get; set; } = "Same-Direction";

    public double WipeLengthMm { get; set; } = 35.0;
    public double WipeRampMm { get; set; } = 5.0;
    public double WipeSpeed { get; set; } = 600.0;
    /// <summary>Skip wipe when the following travel is shorter than 2× layer height.</summary>
    public bool WipeSkipShortTravels { get; set; }
    public double ExtrusionStartWaitSec { get; set; }
    public double ExtrusionResumeWaitSec { get; set; }

    /// <summary>Digital S&S: dwell after screw-stop before the travel move (seconds).</summary>
    public double SsPreTravelWaitSec { get; set; } = 0.5;

    /// <summary>Digital S&S: screw speed during the resume wait, % of segment RPM (5-100).</summary>
    public double SsResumePrimePercent { get; set; } = 100.0;

    /// <summary>
    /// When true, KRL export pulses digital Start/Stop around travels:
    /// <c>$OUT[9]</c> (URM / MIO) on before stop, <c>$OUT[7]</c> (print enable / screw) off for travel,
    /// then screw on + wait + URM off on resume. When false, <c>$OUT[9]</c> stays on for the whole job
    /// (legacy MassiveSLICER behaviour).
    /// </summary>
    public bool DigitalStartStopEnabled { get; set; } = true;

    /// <summary>
    /// Robot Mode MAT (T1/T2/T3/RPM). Null = not in file (pre-split) — load migrates
    /// from <see cref="DigitalStartStopEnabled"/>. New prefs set this to false so
    /// Travel Moves can default on without also turning on Caracol MAT.
    /// </summary>
    public bool? RobotModeEnabled { get; set; }

    /// <summary>Extruder cooling air: $OUT[5] on in header, off in footer.</summary>
    public bool ExtruderAirEnabled { get; set; }

    public bool ResumeRampEnabled { get; set; }
    public double ResumeRampStartSpeed { get; set; } = 0.5;
    public double ResumeRampStartRpmPercent { get; set; } = 1.0;
    public double ResumeRampDistanceMm { get; set; } = 609.6;
    public int ResumeRampSteps { get; set; } = 10;

    public bool LayerSpeedAdaptEnabled { get; set; }
    public string LayerSpeedBasisDisplay { get; set; } = "Cut length";
    public double LayerSpeedMinMmS { get; set; } = 10.0;
    public double LayerSpeedMaxMmS { get; set; } = 100.0;

    /// <summary>Seam guide points as [x, y, z] world coordinates.</summary>
    public List<float[]> SeamGuidePoints { get; set; } = [];

    /// <summary>Toolpath paint marks as [x, y, z, radius, kind, bridgeRole?] where
    /// kind is (float)PaintMarkKind and optional bridgeRole is (float)PaintBridgeRole
    /// (SupportBar / ColumnFoot). Bridge marks grow Formbound under painted beads;
    /// Remove marks delete them.</summary>
    public List<float[]> PaintMarks { get; set; } = [];

    /// <summary>Structural Support specs, encoded as float[12]:
    /// [shape, anchorX, anchorY, anchorLayer, layersUp, layersDown,
    ///  centerX, centerY, width, depth, rotationDeg, enabled].</summary>
    public List<float[]> StructuralSupports { get; set; } = [];

    /// <summary>Structural Support names, index-parallel to <see cref="StructuralSupports"/>
    /// (names can't ride in the float array). Shorter/missing = fall back to "Support N".</summary>
    public List<string> StructuralSupportNames { get; set; } = [];

    /// <summary>Curved slicing boundary source: Auto, Viewport Pick, JSON Import.</summary>
    public string CurvedBoundarySource { get; set; } = "Auto";

    public double CurvedAutoDetectBandMm { get; set; } = 2.0;
    public bool CurvedEnableRegionSplit { get; set; } = true;
    public List<int> CurvedBoundaryLowVertices { get; set; } = [];
    public List<int> CurvedBoundaryHighVertices { get; set; } = [];

    /// <summary>KUKA TOOL_DATA index (1–16).</summary>
    public int ToolDataIndex { get; set; } = 1;

    /// <summary>KUKA BASE_DATA index (1–32).</summary>
    public int BaseDataIndex { get; set; } = 1;

    /// <summary>Toolhead A angle in degrees.</summary>
    public double ToolheadA { get; set; } = 0.0;

    /// <summary>Toolhead B angle in degrees.</summary>
    public double ToolheadB { get; set; } = 0.0;

    /// <summary>Toolhead C angle in degrees.</summary>
    public double ToolheadC { get; set; } = 0.0;

    /// <summary>LFAM rail: allow E1 to track the path within Y+/Y− of home.</summary>
    public bool E1MotionEnabled { get; set; }

    /// <summary>Max positive E1 travel from home (mm) when motion is enabled.</summary>
    public double E1YPlusMm { get; set; } = 500.0;

    /// <summary>Max negative E1 travel from home (mm) when motion is enabled.</summary>
    public double E1YMinusMm { get; set; } = 500.0;

    /// <summary>Surface follow strength percent (0 = vertical, 100 = full follow).</summary>
    public double OrientationFollowPercent { get; set; } = 100.0;

    /// <summary>Hard cap on TCP tilt from vertical in degrees, applied after the
    /// surface-follow blend (90 = uncapped).</summary>
    public double OrientationMaxTiltDeg { get; set; } = 90.0;

    /// <summary>Force the first layer's tool orientation to vertical (flat-bed adhesion).</summary>
    public bool FirstLayerZeroTilt { get; set; }

    /// <summary>Layer lean strength percent (planar; 0 = off).</summary>
    public double LayerLeanPercent { get; set; }

    /// <summary>Layer lean max tilt from vertical (degrees).</summary>
    public double LayerLeanMaxTiltDeg { get; set; } = 0.0;

    /// <summary>Forward-biased Gaussian look-ahead for KRL ABC smoothing (mm). 0 = off.</summary>
    public double OrientationLookAheadMm { get; set; }

    /// <summary>Width of the KRL orientation transition ramp (mm).</summary>
    public double OrientationSigmaMm { get; set; } = 30.0;

    /// <summary>KUKA $APO.CVEL value (0–100). Written to the SRC header and used by the simulation velocity profile.</summary>
    public double ApoCvel { get; set; }

    // ── Scan (Zivid) ──────────────────────────────────────────────────────

    /// <summary>IP address of the Zivid camera on the cell network.</summary>
    public string ScanCameraIp { get; set; } = "192.168.0.150";

    /// <summary>Directory where captured scans (.zdf / .ply) are written.</summary>
    public string ScanOutputDirectory { get; set; } = "scans";

    /// <summary>KUKA TOOL_DATA index holding the calibrated scanner TCP (1–16).</summary>
    public int ScanToolDataIndex { get; set; } = 6;

    /// <summary>KUKA BASE_DATA index used while scanning (1–32).</summary>
    public int ScanBaseDataIndex { get; set; } = 1;

    /// <summary>
    /// MILL right-sidebar snapshot (operation, Box/Face area tool, bit, feeds, planar axis).
    /// Null on files saved before this field — do not reset the live mill panel.
    /// </summary>
    public MillSidebarSettings? Mill { get; set; }

    /// <summary>Path to the last workspace saved via Save As (.mass). Restored on next launch.</summary>
    public string? LastWorkspacePath { get; set; }

    /// <summary>Most-recently opened/saved workspaces, newest first (File → Open Recent).</summary>
    public List<string> RecentWorkspaces { get; set; } = [];

    // -- ERP connection (MassiveSLICER ↔ ERP API, /api/slicer/v1) -----------

    /// <summary>Base URL of the ERP slicer API. Defaults to the published production
    /// ERP (same host the MCP integration targets); ErpClient tolerates the URL with
    /// or without the /api/slicer/v1 suffix.</summary>
    public string ErpBaseUrl { get; set; } = "https://lab.massivemake.com/api/slicer/v1";

    /// <summary>Bearer token from ERP Settings → Slicer Access, or from
    /// <c>POST /api/slicer/v1/login</c>. Local prefs only — never in .mass files.</summary>
    public string? ErpApiToken { get; set; }

    /// <summary>Lab login email. Used with <see cref="ErpPassword"/> to fetch
    /// <see cref="ErpApiToken"/> automatically. Local prefs only.</summary>
    public string? ErpEmail { get; set; }

    /// <summary>Lab login password. Local prefs only — scrubbed from .mass files.</summary>
    public string? ErpPassword { get; set; }

    /// <summary>Per-cell SMB credentials for direct Export-to-Robot uploads.
    /// Passwords are scrubbed from workspace (.mass) settings snapshots.</summary>
    public List<RobotSmbConfig> RobotSmb { get; set; } = [];

    /// <summary>Root of the project folders on the UNAS share. ERP-linked workspaces
    /// save into "&lt;root&gt;/&lt;number&gt; - …/06-Production Documents" automatically.</summary>
    public string UnasProjectsRoot { get; set; } = "/Volumes/MassiveFILES/Projects";

    // ── UI layout state ───────────────────────────────────────────────────

    /// <summary>
    /// Open/closed state of collapsible panels, keyed by a stable panel id (e.g. "bed-settings").
    /// Restored on next launch so expanders stay how the user left them. Written by the
    /// <c>PersistExpander</c> attached behaviour.
    /// </summary>
    public Dictionary<string, bool> ExpandedPanels { get; set; } = [];
}

/// <summary>
/// SMB connection details for one robot cell's controller share (KRC "D drive").
/// Keyed by the cell's display name. The password lives in local prefs.json only —
/// WorkspaceService scrubs it from .mass settings snapshots.
/// </summary>
public sealed class RobotSmbConfig
{
    public string CellName { get; set; } = "";

    /// <summary>Controller IP (defaults to the cell's bridge IP when first opened).</summary>
    public string Host { get; set; } = "";

    /// <summary>SMB share name on the controller, e.g. "d" or "D$".</summary>
    public string Share { get; set; } = "d";

    /// <summary>Folder inside the share to drop programs into (blank = share root).</summary>
    public string Folder { get; set; } = "";

    public string Username { get; set; } = "";
    public string? Password { get; set; }
}
