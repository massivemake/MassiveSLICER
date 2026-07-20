using System.Text.Json;
using System.Text.Json.Serialization;

namespace MassiveSlicer.Core.IO;

/// <summary>One row of <c>AdditiveSettingsViewModel.MultiPlanarPlanes</c> — a plain DTO (no
/// back-reference to an owner), used only for preset persistence.</summary>
public sealed class PresetPlaneRow
{
    public double HeightPct { get; set; }
    public double AngleDeg { get; set; }
}

/// <summary>
/// One saved print preset — a named snapshot of some/all of the Additive slicing settings.
/// Field names mirror <c>AdditiveSettingsViewModel</c>. Every settings field is nullable: a
/// preset only carries the field-groups that were checked in the Save-as-Preset dialog at save
/// time (see <c>PresetsCardViewModel.SaveFieldGroups</c>), so an unchecked group's fields are
/// simply absent (null) rather than holding a misleading default value. Applying a preset only
/// touches the fields it actually carries.
/// </summary>
public sealed class PrintPresetRecord
{
    public required string Name { get; set; }
    public string Folder { get; set; } = "Uncategorized";
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastPrintedUtc { get; set; }
    public bool IsFavorite { get; set; }

    /// <summary>Always captured/applied — not gated by a field-group checkbox (see PresetsCardViewModel).</summary>
    public string Material { get; set; } = "";

    // -- Geometry & layers ---------------------------------------------------
    public double? BeadWidth { get; set; }
    public double? LayerHeight { get; set; }
    public double? TiltAngle { get; set; }
    public double? TiltAngleX { get; set; }
    public bool? MultiPlanarAxisX { get; set; }
    public List<PresetPlaneRow>? MultiPlanarPlanes { get; set; }

    // -- Slicing mode & method ------------------------------------------------
    public string? Method { get; set; }
    public string? SeamMode { get; set; }
    public string? SlicingMode { get; set; }
    public double? OrientationFollowPercent { get; set; }
    public double? OrientationMaxTiltDeg { get; set; }
    public bool? FirstLayerZeroTilt { get; set; }
    public double? LayerLeanPercent { get; set; }
    public double? LayerLeanMaxTiltDeg { get; set; }
    public string? CurvedBoundarySourceDisplay { get; set; }
    public double? CurvedAutoDetectBandMm { get; set; }
    public bool? CurvedEnableRegionSplit { get; set; }

    // -- Live effector ---------------------------------------------------------
    public bool? EffectorEnabled { get; set; }
    public string? EffectorMode { get; set; }
    public double? EffectorRange { get; set; }
    public double? EffectorStrength { get; set; }

    // -- Pattern & texture -----------------------------------------------------
    public string? PatternType { get; set; }
    public string? PatternMapping { get; set; }
    public double? PatternWavelengthMm { get; set; }
    public double? PatternAmplitude { get; set; }
    public double? PatternFrequency { get; set; }
    public double? PatternTwist { get; set; }
    public double? PatternOffset { get; set; }
    public double? PatternFadeIn { get; set; }
    public double? PatternFadeOut { get; set; }

    // -- X-Bracing wall (world-position CylinderX/Y deliberately excluded — per-model, not portable) --
    public bool? XBracingEnabled { get; set; }
    public string? XBracingProjectionType { get; set; }
    public bool? XBracingShowHelper { get; set; }
    public double? XBracingPlaneTiltY { get; set; }
    public double? XBracingPlaneTiltX { get; set; }
    public double? XBracingCylinderDiameterMm { get; set; }
    public bool? XBracingCylinderFlipDirection { get; set; }
    public double? XBracingDepthMm { get; set; }
    public double? XBracingDepthBottomMm { get; set; }
    public string? XBracingDepthEaseBottom { get; set; }
    public string? XBracingDepthEaseTop { get; set; }
    public double? XBracingSpanMm { get; set; }
    public double? XBracingAngleDeg { get; set; }
    public bool? XBracingExtendEdges { get; set; }

    // -- Wave effect -------------------------------------------------------------
    public string? WaveEffect { get; set; }
    public double? WaveAmplitude { get; set; }
    public string? WaveFrequencyMode { get; set; }
    public double? WaveWavelength { get; set; }
    public int? WaveCycles { get; set; }
    public double? WaveShape { get; set; }
    public double? WaveStagger { get; set; }
    public int? WavePhaseMethodIndex { get; set; }
    public bool? WaveGradient { get; set; }
    public double? WaveAmplitudeBottom { get; set; }
    public double? WaveAmplitudeTop { get; set; }
    public double? WaveWavelengthBottom { get; set; }
    public double? WaveWavelengthTop { get; set; }
    public double? WaveGradientCenter { get; set; }
    public string? WaveGradientCurve { get; set; }

    // -- Infill ------------------------------------------------------------------
    public string? InfillPattern { get; set; }
    public double? InfillSpacingMm { get; set; }
    public double? InfillAngleDeg { get; set; }
    public double? LightningOverhangDeg { get; set; }
    public double? LightningBranchSpacingMm { get; set; }
    public double? LightningTipLoopRadiusMm { get; set; }
    public bool? LightningAffectInterior { get; set; }
    public bool? LightningAffectExterior { get; set; }
    public bool? LightningTargetSupportSelections { get; set; }
    public double? LightningButtressBarMm { get; set; }

    // -- Overhang & orientation ----------------------------------------------
    public bool? OverhangOrientation { get; set; }
    public double? MaxOverhangTiltDeg { get; set; }
    public bool? ZigZagAllowSameLayerTravel { get; set; }
    public bool? DisableContourOffset { get; set; }

    // -- Toolhead orientation --------------------------------------------------
    public double? ToolheadA { get; set; }
    public double? ToolheadB { get; set; }
    public double? ToolheadC { get; set; }

    // -- Motion & KUKA frame ---------------------------------------------------
    public double? PrintSpeed { get; set; }
    public double? TravelSpeed { get; set; }
    public double? ApoCvel { get; set; }
    public bool? E1MotionEnabled { get; set; }
    public double? E1YPlusMm { get; set; }
    public double? E1YMinusMm { get; set; }
    public bool? SmoothRotation { get; set; }
    public int? SmoothRotationRadius { get; set; }
    public double? SmoothRotationMaxRateDegPerMm { get; set; }
    public double? OrientationLookAheadMm { get; set; }
    public double? OrientationSigmaMm { get; set; }

    // -- Temperatures --------------------------------------------------------
    public double? Temperature1 { get; set; }
    public double? Temperature2 { get; set; }
    public double? Temperature3 { get; set; }

    // -- KRL export tuning -----------------------------------------------------
    public string? TemperatureOffset { get; set; }
    public string? ExtrusionSpeedOffset { get; set; }
    public bool? DigitalStartStopEnabled { get; set; }
    public double? ExtrusionStartWaitSec { get; set; }
    public double? ExtrusionResumeWaitSec { get; set; }

    // -- Movement (z-hop / wipe / resume) --------------------------------------
    public double? ZHopMm { get; set; }
    public string? WipeModeDisplay { get; set; }
    public double? WipeLengthMm { get; set; }
    public double? WipeRampMm { get; set; }
    public double? WipeSpeed { get; set; }
    public bool? WipeSkipShortTravels { get; set; }
    public bool? ResumeRampEnabled { get; set; }
    public double? ResumeRampStartSpeed { get; set; }
    public double? ResumeRampStartRpmPercent { get; set; }
    public double? ResumeRampDistanceMm { get; set; }
    public int? ResumeRampSteps { get; set; }

    // -- Adaptive layer speed --------------------------------------------------
    public bool? LayerSpeedAdaptEnabled { get; set; }
    public string? LayerSpeedBasisDisplay { get; set; }
    public double? LayerSpeedMinMmS { get; set; }
    public double? LayerSpeedMaxMmS { get; set; }

    // -- KRL post-process --------------------------------------------------------
    public bool? TravelSetAnout4Zero { get; set; }
    public string? KrlHeaderText { get; set; }
    public string? KrlFooterText { get; set; }

    // -- Adaptive layer height -----------------------------------------------
    public bool? AdaptiveLayerHeight { get; set; }
    public double? MinLayerHeight { get; set; }
    public double? AdaptiveQuality { get; set; }

    // -- Stock from Maps -------------------------------------------------------
    public bool? UseDisplacedStock { get; set; }
    public double? StockAllowanceMm { get; set; }

    // -- Brim --------------------------------------------------------------------
    public bool? BrimEnabled { get; set; }
    public int? BrimLoops { get; set; }
}

/// <summary>
/// Persists the user's saved/imported print presets as JSON in the user's AppData folder.
/// Path: <c>%AppData%\MassiveSlicer\presets.json</c>. Local-only for now (matches
/// <see cref="PreferencesLoader"/>'s convention) — a shared/synced library is a later,
/// separate step (see project notes on one-file-per-preset + git for that).
/// Seeded/sample presets are never written here — only presets the user actually saved or
/// imported.
/// </summary>
public static class PrintPresetsLoader
{
    private static readonly string PresetsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MassiveSlicer");

    private static readonly string PresetsPath = Path.Combine(PresetsDir, "presets.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        Converters                  = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads saved presets from disk. Returns an empty list if the file doesn't exist or can't be parsed.</summary>
    public static List<PrintPresetRecord> Load()
    {
        if (!File.Exists(PresetsPath)) return [];
        try
        {
            var json = File.ReadAllText(PresetsPath);
            return JsonSerializer.Deserialize<List<PrintPresetRecord>>(json, Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Serializes the given presets to disk, creating the directory if needed.</summary>
    public static void Save(IEnumerable<PrintPresetRecord> presets)
    {
        try
        {
            Directory.CreateDirectory(PresetsDir);
            File.WriteAllText(PresetsPath, JsonSerializer.Serialize(presets.ToList(), Options));
        }
        catch { /* non-fatal -- same as PreferencesLoader; don't crash the app over a disk write */ }
    }
}
