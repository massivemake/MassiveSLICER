using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// A saved print preset. Field names mirror <see cref="AdditiveSettingsViewModel"/> — Apply
/// (see PresetsCardViewModel.ApplyPresetToAdditive) pushes these directly onto the real settings
/// object. Every settings field is nullable: a preset only carries whichever field-groups were
/// checked in the Save-as-Preset dialog at save time (see PresetsCardViewModel.SaveFieldGroups),
/// so an unchecked group's fields are simply absent rather than holding a misleading default —
/// Apply only touches the fields a preset actually carries.
/// </summary>
public sealed class PrintPresetSample
{
    public required string Name { get; init; }
    public string Folder { get; init; } = "Uncategorized";
    public DateTime CreatedUtc { get; init; }
    public bool IsFavorite { get; set; }
    public bool IsSeeded { get; init; }

    /// <summary>Always captured/applied — not gated by a field-group checkbox (the material
    /// preset library is its own separate concept; see SaveNewPreset/ApplyPresetToAdditive).</summary>
    public string Material { get; init; } = "";

    /// <summary>
    /// Settable (not init-only) — but deliberately NOT stamped by the "Load" action (see
    /// PresetsCardViewModel.LoadSelected). Named "Printed" not "Used": a preset only counts once
    /// it's actually driven a real print, not merely been selected in the panel. This comp has no
    /// print pipeline to hook that to yet, so it only reflects seeded sample data.
    /// </summary>
    public DateTime? LastPrintedUtc { get; set; }

    // -- Geometry & layers ---------------------------------------------------
    public double? BeadWidth { get; init; }
    public double? LayerHeight { get; init; }
    public double? TiltAngle { get; init; }
    public double? TiltAngleX { get; init; }
    public bool? MultiPlanarAxisX { get; init; }
    public List<PresetPlaneRow>? MultiPlanarPlanes { get; init; }

    // -- Slicing mode & method ------------------------------------------------
    public string? Method { get; init; }
    public string? SeamMode { get; init; }
    public string? SlicingMode { get; init; }
    public double? OrientationFollowPercent { get; init; }
    public double? OrientationMaxTiltDeg { get; init; }
    public bool? FirstLayerZeroTilt { get; init; }
    public double? LayerLeanPercent { get; init; }
    public double? LayerLeanMaxTiltDeg { get; init; }
    public string? CurvedBoundarySourceDisplay { get; init; }
    public double? CurvedAutoDetectBandMm { get; init; }
    public bool? CurvedEnableRegionSplit { get; init; }

    // -- Live effector ---------------------------------------------------------
    public bool? EffectorEnabled { get; init; }
    public string? EffectorMode { get; init; }
    public double? EffectorRange { get; init; }
    public double? EffectorStrength { get; init; }

    // -- Pattern & texture -----------------------------------------------------
    public string? PatternType { get; init; }
    public string? PatternMapping { get; init; }
    public double? PatternWavelengthMm { get; init; }
    public double? PatternAmplitude { get; init; }
    public double? PatternFrequency { get; init; }
    public double? PatternTwist { get; init; }
    public double? PatternOffset { get; init; }
    public double? PatternFadeIn { get; init; }
    public double? PatternFadeOut { get; init; }

    // -- X-Bracing wall (world-position CylinderX/Y deliberately excluded — per-model, not portable) --
    public bool? XBracingEnabled { get; init; }
    public string? XBracingProjectionType { get; init; }
    public bool? XBracingShowHelper { get; init; }
    public double? XBracingPlaneTiltY { get; init; }
    public double? XBracingPlaneTiltX { get; init; }
    public double? XBracingCylinderDiameterMm { get; init; }
    public bool? XBracingCylinderFlipDirection { get; init; }
    public double? XBracingDepthMm { get; init; }
    public double? XBracingDepthBottomMm { get; init; }
    public string? XBracingDepthEaseBottom { get; init; }
    public string? XBracingDepthEaseTop { get; init; }
    public double? XBracingSpanMm { get; init; }
    public double? XBracingAngleDeg { get; init; }
    public bool? XBracingExtendEdges { get; init; }

    // -- Wave effect -------------------------------------------------------------
    public string? WaveEffect { get; init; }
    public double? WaveAmplitude { get; init; }
    public string? WaveFrequencyMode { get; init; }
    public double? WaveWavelength { get; init; }
    public int? WaveCycles { get; init; }
    public double? WaveShape { get; init; }
    public double? WaveStagger { get; init; }
    public int? WavePhaseMethodIndex { get; init; }
    public bool? WaveGradient { get; init; }
    public double? WaveAmplitudeBottom { get; init; }
    public double? WaveAmplitudeTop { get; init; }
    public double? WaveWavelengthBottom { get; init; }
    public double? WaveWavelengthTop { get; init; }
    public double? WaveGradientCenter { get; init; }
    public string? WaveGradientCurve { get; init; }

    // -- Infill ------------------------------------------------------------------
    public string? InfillPattern { get; init; }
    public double? InfillSpacingMm { get; init; }
    public double? InfillAngleDeg { get; init; }
    public double? LightningOverhangDeg { get; init; }
    public double? LightningBranchSpacingMm { get; init; }
    public double? LightningTipLoopRadiusMm { get; init; }
    public bool? LightningAffectInterior { get; init; }
    public bool? LightningAffectExterior { get; init; }
    public bool? LightningTargetSupportSelections { get; init; }
    public double? LightningButtressBarMm { get; init; }

    // -- Overhang & orientation ----------------------------------------------
    public bool? OverhangOrientation { get; init; }
    public double? MaxOverhangTiltDeg { get; init; }
    public bool? ZigZagAllowSameLayerTravel { get; init; }
    public bool? DisableContourOffset { get; init; }

    // -- Toolhead orientation --------------------------------------------------
    public double? ToolheadA { get; init; }
    public double? ToolheadB { get; init; }
    public double? ToolheadC { get; init; }

    // -- Motion & KUKA frame ---------------------------------------------------
    public double? PrintSpeed { get; init; }
    public double? TravelSpeed { get; init; }
    public double? ApoCvel { get; init; }
    public bool? E1MotionEnabled { get; init; }
    public double? E1YPlusMm { get; init; }
    public double? E1YMinusMm { get; init; }
    public bool? SmoothRotation { get; init; }
    public int? SmoothRotationRadius { get; init; }
    public double? SmoothRotationMaxRateDegPerMm { get; init; }
    public double? OrientationLookAheadMm { get; init; }
    public double? OrientationSigmaMm { get; init; }

    // -- Temperatures --------------------------------------------------------
    public double? Temperature1 { get; init; }
    public double? Temperature2 { get; init; }
    public double? Temperature3 { get; init; }

    // -- KRL export tuning -----------------------------------------------------
    public string? TemperatureOffset { get; init; }
    public string? ExtrusionSpeedOffset { get; init; }
    public bool? DigitalStartStopEnabled { get; init; }
    public double? ExtrusionStartWaitSec { get; init; }
    public double? ExtrusionResumeWaitSec { get; init; }

    // -- Movement (z-hop / wipe / resume) --------------------------------------
    public double? ZHopMm { get; init; }
    public string? WipeModeDisplay { get; init; }
    public double? WipeLengthMm { get; init; }
    public double? WipeRampMm { get; init; }
    public double? WipeSpeed { get; init; }
    public bool? WipeSkipShortTravels { get; init; }
    public bool? ResumeRampEnabled { get; init; }
    public double? ResumeRampStartSpeed { get; init; }
    public double? ResumeRampStartRpmPercent { get; init; }
    public double? ResumeRampDistanceMm { get; init; }
    public int? ResumeRampSteps { get; init; }

    // -- Adaptive layer speed --------------------------------------------------
    public bool? LayerSpeedAdaptEnabled { get; init; }
    public string? LayerSpeedBasisDisplay { get; init; }
    public double? LayerSpeedMinMmS { get; init; }
    public double? LayerSpeedMaxMmS { get; init; }

    // -- KRL post-process --------------------------------------------------------
    public string? KrlHeaderText { get; init; }
    public string? KrlFooterText { get; init; }

    // -- Adaptive layer height -----------------------------------------------
    public bool? AdaptiveLayerHeight { get; init; }
    public double? MinLayerHeight { get; init; }
    public double? AdaptiveQuality { get; init; }

    // -- Stock from Maps -------------------------------------------------------
    public bool? UseDisplacedStock { get; init; }
    public double? StockAllowanceMm { get; init; }

    // -- Brim --------------------------------------------------------------------
    public bool? BrimEnabled { get; init; }
    public int? BrimLoops { get; init; }

    /// <summary>
    /// Coarse-rounded settings signature used to detect "this is secretly the same preset as that
    /// one" regardless of name. Only the handful of fields every preset is most likely to carry
    /// (bead width/layer height/print speed/method/pattern/seam/bracing/material) — a preset
    /// missing one of these (unchecked group) just contributes a stable placeholder to the string.
    /// </summary>
    public string Fingerprint => string.Join("|",
        Math.Round(BeadWidth ?? 0, 1), Math.Round(LayerHeight ?? 0, 1), Math.Round(PrintSpeed ?? 0, 0),
        Method ?? "", PatternType ?? "", SeamMode ?? "", XBracingEnabled ?? false, Material);

    /// <summary>Other presets (by name) sharing this exact fingerprint — recomputed by
    /// PresetsCardViewModel.RecomputeSiblingLinks whenever the preset list changes.</summary>
    public IReadOnlyList<string> SiblingNames { get; set; } = Array.Empty<string>();

    public int SiblingCount => SiblingNames.Count;
    public bool HasSiblings => SiblingCount > 0;
    public string SiblingTooltip => HasSiblings
        ? $"Same settings also saved as: {string.Join(", ", SiblingNames)}"
        : "";

    /// <summary>Small key-value badge shown at the left of the list row.</summary>
    public string SummaryBadge => BeadWidth is { } bw ? $"{bw:0.#}mm" : "—";

    public string LastPrintedDisplay => LastPrintedUtc is { } t ? t.ToString("MMM d") : "never";

    /// <summary>
    /// Full field-by-field readout for the "What" info popup — the name alone isn't enough to
    /// know what's actually in a preset before applying it. Only lists fields this preset
    /// actually carries (a partial preset, saved with some groups unchecked, shows only what
    /// it has rather than padding out with misleading defaults).
    /// </summary>
    /// <summary>
    /// Full field-by-field readout for the "What" info popup, organized by the same field-groups
    /// as the Save-as-Preset checklist — a group only appears here if this preset actually
    /// captured at least one of its fields, so a partial preset (some groups unchecked at save
    /// time) visibly shows only what it really carries, not a padded-out full list.
    /// </summary>
    public string InfoLines
    {
        get
        {
            var lines = new List<string> { $"Folder: {Folder}" };
            if (!string.IsNullOrEmpty(Material)) lines.Add($"Material: {Material}");

            void D(List<string> s, string label, double? v, string unit = "") { if (v is { } x) s.Add($"{label}: {x:0.##}{unit}"); }
            void I(List<string> s, string label, int? v) { if (v is { } x) s.Add($"{label}: {x}"); }
            void B(List<string> s, string label, bool? v) { if (v is { } x) s.Add($"{label}: {(x ? "On" : "Off")}"); }
            void S(List<string> s, string label, string? v) { if (!string.IsNullOrEmpty(v)) s.Add($"{label}: {v}"); }

            void Section(string title, Action<List<string>> body)
            {
                var section = new List<string>();
                body(section);
                if (section.Count == 0) return;
                lines.Add("");
                lines.Add($"— {title} —");
                lines.AddRange(section);
            }

            Section("Geometry & layers", s =>
            {
                D(s, "Bead width", BeadWidth, " mm");
                D(s, "Layer height", LayerHeight, " mm");
                D(s, "Tilt angle", TiltAngle, "°");
                D(s, "Tilt angle X", TiltAngleX, "°");
                B(s, "Multi-Planar axis X", MultiPlanarAxisX);
                if (MultiPlanarPlanes is { Count: > 0 } planes)
                    s.Add($"Multi-Planar planes: {planes.Count} ({string.Join(", ", planes.Select(p => $"{p.HeightPct:0.#}%@{p.AngleDeg:0.#}°"))})");
            });

            Section("Slicing mode & method", s =>
            {
                S(s, "Method", Method);
                S(s, "Seam mode", SeamMode);
                S(s, "Slicing mode", SlicingMode);
                D(s, "Orientation follow", OrientationFollowPercent, "%");
                D(s, "Orientation max tilt", OrientationMaxTiltDeg, "°");
                B(s, "First layer zero tilt", FirstLayerZeroTilt);
                D(s, "Layer lean", LayerLeanPercent, "%");
                D(s, "Layer lean max tilt", LayerLeanMaxTiltDeg, "°");
                S(s, "Curved boundary source", CurvedBoundarySourceDisplay);
                D(s, "Curved auto-detect band", CurvedAutoDetectBandMm, " mm");
                B(s, "Curved region split", CurvedEnableRegionSplit);
            });

            Section("Live effector", s =>
            {
                B(s, "Effector enabled", EffectorEnabled);
                S(s, "Effector mode", EffectorMode);
                D(s, "Effector range", EffectorRange, " mm");
                D(s, "Effector strength", EffectorStrength);
            });

            Section("Pattern & texture", s =>
            {
                S(s, "Pattern", PatternType);
                S(s, "Pattern mapping", PatternMapping);
                D(s, "Pattern wavelength", PatternWavelengthMm, " mm");
                D(s, "Pattern amplitude", PatternAmplitude);
                D(s, "Pattern frequency", PatternFrequency);
                D(s, "Pattern twist", PatternTwist);
                D(s, "Pattern offset", PatternOffset);
                D(s, "Pattern fade in", PatternFadeIn);
                D(s, "Pattern fade out", PatternFadeOut);
            });

            Section("X-Bracing wall", s =>
            {
                B(s, "X-Bracing", XBracingEnabled);
                S(s, "Projection type", XBracingProjectionType);
                B(s, "Show helper", XBracingShowHelper);
                D(s, "Plane tilt Y", XBracingPlaneTiltY, "°");
                D(s, "Plane tilt X", XBracingPlaneTiltX, "°");
                D(s, "Cylinder diameter", XBracingCylinderDiameterMm, " mm");
                B(s, "Cylinder flip direction", XBracingCylinderFlipDirection);
                D(s, "Depth", XBracingDepthMm, " mm");
                D(s, "Depth (bottom)", XBracingDepthBottomMm, " mm");
                S(s, "Depth ease (bottom)", XBracingDepthEaseBottom);
                S(s, "Depth ease (top)", XBracingDepthEaseTop);
                D(s, "Span", XBracingSpanMm, " mm");
                D(s, "Angle", XBracingAngleDeg, "°");
                B(s, "Extend edges", XBracingExtendEdges);
            });

            Section("Wave effect", s =>
            {
                S(s, "Wave effect", WaveEffect);
                D(s, "Amplitude", WaveAmplitude);
                S(s, "Frequency mode", WaveFrequencyMode);
                D(s, "Wavelength", WaveWavelength);
                I(s, "Cycles", WaveCycles);
                D(s, "Shape", WaveShape);
                D(s, "Stagger", WaveStagger);
                I(s, "Phase method", WavePhaseMethodIndex);
                B(s, "Gradient", WaveGradient);
                D(s, "Amplitude (bottom)", WaveAmplitudeBottom);
                D(s, "Amplitude (top)", WaveAmplitudeTop);
                D(s, "Wavelength (bottom)", WaveWavelengthBottom);
                D(s, "Wavelength (top)", WaveWavelengthTop);
                D(s, "Gradient center", WaveGradientCenter);
                S(s, "Gradient curve", WaveGradientCurve);
            });

            Section("Infill", s =>
            {
                S(s, "Infill pattern", InfillPattern);
                D(s, "Infill spacing", InfillSpacingMm, " mm");
                D(s, "Infill angle", InfillAngleDeg, "°");
                D(s, "Lightning overhang", LightningOverhangDeg, "°");
                D(s, "Lightning branch spacing", LightningBranchSpacingMm, " mm");
                D(s, "Lightning tip loop radius", LightningTipLoopRadiusMm, " mm");
                B(s, "Lightning affect interior", LightningAffectInterior);
                B(s, "Lightning affect exterior", LightningAffectExterior);
                B(s, "Lightning target supports", LightningTargetSupportSelections);
                D(s, "Lightning buttress bar", LightningButtressBarMm, " mm");
            });

            Section("Overhang & orientation", s =>
            {
                B(s, "Overhang orientation", OverhangOrientation);
                D(s, "Max overhang tilt", MaxOverhangTiltDeg, "°");
                B(s, "Zig-zag same-layer travel", ZigZagAllowSameLayerTravel);
                B(s, "Disable contour offset", DisableContourOffset);
            });

            Section("Toolhead orientation", s =>
            {
                D(s, "Toolhead A", ToolheadA, "°");
                D(s, "Toolhead B", ToolheadB, "°");
                D(s, "Toolhead C", ToolheadC, "°");
            });

            Section("Motion & KUKA frame", s =>
            {
                D(s, "Print speed", PrintSpeed, " mm/s");
                D(s, "Travel speed", TravelSpeed, " mm/s");
                D(s, "APO CVEL", ApoCvel);
                B(s, "E1 motion enabled", E1MotionEnabled);
                D(s, "E1 Y+", E1YPlusMm, " mm");
                D(s, "E1 Y-", E1YMinusMm, " mm");
                B(s, "Smooth rotation", SmoothRotation);
                I(s, "Smooth rotation radius", SmoothRotationRadius);
                D(s, "Smooth rotation max rate", SmoothRotationMaxRateDegPerMm, "°/mm");
                D(s, "Orientation look-ahead", OrientationLookAheadMm, " mm");
                D(s, "Orientation sigma", OrientationSigmaMm, " mm");
            });

            Section("Temperatures", s =>
            {
                D(s, "Temperature 1", Temperature1, "°C");
                D(s, "Temperature 2", Temperature2, "°C");
                D(s, "Temperature 3", Temperature3, "°C");
            });

            Section("KRL export tuning", s =>
            {
                S(s, "Temperature offset", TemperatureOffset);
                S(s, "Extrusion speed offset", ExtrusionSpeedOffset);
                B(s, "Digital start/stop", DigitalStartStopEnabled);
                D(s, "Extrusion start wait", ExtrusionStartWaitSec, " s");
                D(s, "Extrusion resume wait", ExtrusionResumeWaitSec, " s");
            });

            Section("Movement (z-hop / wipe / resume)", s =>
            {
                D(s, "Z-hop", ZHopMm, " mm");
                S(s, "Wipe mode", WipeModeDisplay);
                D(s, "Wipe length", WipeLengthMm, " mm");
                D(s, "Wipe ramp", WipeRampMm, " mm");
                D(s, "Wipe speed", WipeSpeed, " mm/s");
                B(s, "Wipe skip short travels", WipeSkipShortTravels);
                B(s, "Resume ramp enabled", ResumeRampEnabled);
                D(s, "Resume ramp start speed", ResumeRampStartSpeed);
                D(s, "Resume ramp start RPM", ResumeRampStartRpmPercent, "%");
                D(s, "Resume ramp distance", ResumeRampDistanceMm, " mm");
                I(s, "Resume ramp steps", ResumeRampSteps);
            });

            Section("Adaptive layer speed", s =>
            {
                B(s, "Layer speed adapt", LayerSpeedAdaptEnabled);
                S(s, "Layer speed basis", LayerSpeedBasisDisplay);
                D(s, "Layer speed min", LayerSpeedMinMmS, " mm/s");
                D(s, "Layer speed max", LayerSpeedMaxMmS, " mm/s");
            });

            Section("KRL post-process", s =>
            {
                if (KrlHeaderText is { Length: > 0 } h) s.Add($"Header: custom ({h.Length} chars)");
                if (KrlFooterText is { Length: > 0 } f) s.Add($"Footer: custom ({f.Length} chars)");
            });

            Section("Adaptive layer height", s =>
            {
                B(s, "Adaptive layer height", AdaptiveLayerHeight);
                D(s, "Min layer height", MinLayerHeight, " mm");
                D(s, "Adaptive quality", AdaptiveQuality);
            });

            Section("Stock from Maps", s =>
            {
                B(s, "Use displaced stock", UseDisplacedStock);
                D(s, "Stock allowance", StockAllowanceMm, " mm");
            });

            Section("Brim", s =>
            {
                B(s, "Brim enabled", BrimEnabled);
                I(s, "Brim loops", BrimLoops);
            });

            lines.Add("");
            lines.Add($"Created: {CreatedUtc:MMM d, yyyy}");
            lines.Add($"Last printed: {LastPrintedDisplay}");
            lines.Add(HasSiblings ? $"Also saved as: {string.Join(", ", SiblingNames)}" : "No known duplicates");
            return string.Join("\n", lines);
        }
    }

    /// <summary>Every value a search should be able to match against, name included. Deliberately
    /// limited to the original core fields — the expanded settings surface is not wired into
    /// search/sort/filter yet (that scope is still to be decided).</summary>
    public IEnumerable<string> SearchableTokens()
    {
        yield return Name;
        if (BeadWidth is { } bw) yield return bw.ToString("0.###");
        if (LayerHeight is { } lh) yield return lh.ToString("0.###");
        if (PrintSpeed is { } ps) yield return ps.ToString("0.###");
        if (Method is not null) yield return Method;
        if (PatternType is not null) yield return PatternType;
        if (SeamMode is not null) yield return SeamMode;
        yield return Folder;
        yield return Material;
        if (XBracingEnabled is true) yield return "X-Bracing";
        if (XBracingEnabled is true) yield return "Bracing";
    }
}

public enum PresetSortMode { Name, LastPrinted, DateCreated }

public enum PresetGroupingMode { None, ByMethod, ByFolder }

public sealed class PresetGroupViewModel
{
    public required string GroupName { get; init; }
    public required IReadOnlyList<PrintPresetSample> Items { get; init; }
}

/// <summary>
/// One row in the Save-as-Preset field-group checklist. Real, not cosmetic: SaveNewPreset only
/// captures a group's fields from the live AdditiveSettingsViewModel when its checkbox is on,
/// and an unchecked group's fields are simply absent (null) from the saved preset.
/// </summary>
public sealed class PresetFieldGroupOption : ViewModelBase
{
    private bool _isIncluded = true;

    public required string Name { get; init; }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetField(ref _isIncluded, value);
    }
}

/// <summary>A single-choice filter over a non-numeric field (e.g. method, pattern, seam mode).</summary>
public sealed class ChoiceFilterViewModel : ViewModelBase
{
    public const string AnyOption = "(Any)";

    public required string FieldName { get; init; }
    public required Func<PrintPresetSample, string> Selector { get; init; }

    /// <summary>Set only for numeric fields (bead width/layer height/print speed) so their Options
    /// sort by value instead of alphabetically — plain string order would put "10" before "2".
    /// Left null for genuinely textual fields (Material/Method/...), which keep alphabetical.</summary>
    public Func<string, double>? NumericSortKey { get; init; }

    public ObservableCollection<string> Options { get; } = new();

    private string _selectedOption = AnyOption;
    public string SelectedOption
    {
        get => _selectedOption;
        set => SetField(ref _selectedOption, value);
    }

    public bool Matches(PrintPresetSample p) => SelectedOption == AnyOption || Selector(p) == SelectedOption;

    public void Reset() => SelectedOption = AnyOption;

    /// <summary>Adds any newly-appeared distinct values (never removes) so a saved/imported preset's
    /// values become choosable without ever invalidating the currently-selected option. A preset
    /// that doesn't carry this field at all (blank selector result) contributes nothing — there's
    /// no real value there to pick.</summary>
    public void RefreshOptions(IEnumerable<string> allValues)
    {
        var candidates = allValues.Where(v => v.Length > 0).Distinct();
        var ordered = NumericSortKey is { } key
            ? candidates.OrderBy(key)
            : candidates.OrderBy(v => v, StringComparer.OrdinalIgnoreCase);
        foreach (var v in ordered)
            if (!Options.Contains(v)) Options.Add(v);
    }
}

/// <summary>
/// Drives the "0 PRESETS" step-card: search/sort/filter/grouping over an in-memory preset list,
/// select + Apply (real — pushes onto the given <see cref="AdditiveSettingsViewModel"/>),
/// favorites, import/export-to-file, fingerprint-based sibling badges, and a Save-as-Preset
/// dialog with the field-group checklist. Persistence is real (see PrintPresetsLoader).
/// </summary>
public sealed class PresetsCardViewModel : ViewModelBase
{
    public ObservableCollection<PrintPresetSample> AllPresets { get; }
    public ObservableCollection<PrintPresetSample> FilteredPresets { get; } = new();
    public ObservableCollection<PresetGroupViewModel> GroupedPresets { get; } = new();
    public ObservableCollection<PresetFieldGroupOption> SaveFieldGroups { get; }
    public ObservableCollection<ChoiceFilterViewModel> ChoiceFilters { get; } = new();

    /// <summary>Committed search terms (each removable, all AND'd together — "zigzag" + "7200"
    /// narrows to presets matching both, not either). The live, not-yet-committed text in
    /// <see cref="SearchText"/> also applies on top of these until Enter pins it as its own tag.</summary>
    public ObservableCollection<string> SearchTags { get; } = new();

    public ICommand RemoveSearchTagCommand { get; }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value)) return;
            Refresh();
        }
    }

    /// <summary>Enter in the search box calls this — pins the current text as a tag and clears the box.</summary>
    public void CommitSearchTag()
    {
        var trimmed = SearchText.Trim();
        if (trimmed.Length == 0) return;
        if (!SearchTags.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            SearchTags.Add(trimmed);
        SearchText = "";
        Refresh();
    }

    private void RemoveSearchTag(string? tag)
    {
        if (tag is null) return;
        SearchTags.Remove(tag);
        Refresh();
    }

    private PresetSortMode _sortMode = PresetSortMode.Name;
    public PresetSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (!SetField(ref _sortMode, value)) return;
            OnPropertyChanged(nameof(IsSortName));
            OnPropertyChanged(nameof(IsSortLastPrinted));
            OnPropertyChanged(nameof(IsSortDateCreated));
            Refresh();
        }
    }

    public bool IsSortName        => SortMode == PresetSortMode.Name;
    public bool IsSortLastPrinted => SortMode == PresetSortMode.LastPrinted;
    public bool IsSortDateCreated => SortMode == PresetSortMode.DateCreated;

    private PresetGroupingMode _groupingMode = PresetGroupingMode.None;
    public PresetGroupingMode GroupingMode
    {
        get => _groupingMode;
        set
        {
            if (!SetField(ref _groupingMode, value)) return;
            OnPropertyChanged(nameof(IsGroupedView));
            OnPropertyChanged(nameof(IsGroupByNone));
            OnPropertyChanged(nameof(IsGroupByMethod));
            OnPropertyChanged(nameof(IsGroupByFolder));
            Refresh();
        }
    }

    public bool IsGroupedView    => GroupingMode != PresetGroupingMode.None;
    public bool IsGroupByNone    => GroupingMode == PresetGroupingMode.None;
    public bool IsGroupByMethod  => GroupingMode == PresetGroupingMode.ByMethod;
    public bool IsGroupByFolder  => GroupingMode == PresetGroupingMode.ByFolder;

    private bool _favoritesOnly;
    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (!SetField(ref _favoritesOnly, value)) return;
            Refresh();
        }
    }

    private bool _isFilterDrawerOpen;
    public bool IsFilterDrawerOpen
    {
        get => _isFilterDrawerOpen;
        set => SetField(ref _isFilterDrawerOpen, value);
    }

    private bool _isSaveDialogOpen;
    public bool IsSaveDialogOpen
    {
        get => _isSaveDialogOpen;
        set => SetField(ref _isSaveDialogOpen, value);
    }

    private string _saveNameText = "";
    public string SaveNameText
    {
        get => _saveNameText;
        set => SetField(ref _saveNameText, value);
    }

    /// <summary>The preset a right-click "Rename" targeted — cleared once the rename dialog
    /// closes (committed or cancelled). Not exposed as a bindable property; only RenameNameText
    /// and IsRenameDialogOpen drive the view.</summary>
    private PrintPresetSample? _renameTargetPreset;

    private bool _isRenameDialogOpen;
    public bool IsRenameDialogOpen
    {
        get => _isRenameDialogOpen;
        set => SetField(ref _isRenameDialogOpen, value);
    }

    private string _renameNameText = "";
    public string RenameNameText
    {
        get => _renameNameText;
        set => SetField(ref _renameNameText, value);
    }

    private PrintPresetSample? _selectedPreset;
    public PrintPresetSample? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetField(ref _selectedPreset, value)) return;
            OnPropertyChanged(nameof(CanLoadSelected));
        }
    }

    public bool CanLoadSelected => SelectedPreset is not null;

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public ICommand ToggleFilterDrawerCommand   { get; }
    public ICommand ToggleSaveDialogCommand     { get; }
    public ICommand SetGroupByNoneCommand       { get; }
    public ICommand SetGroupByMethodCommand     { get; }
    public ICommand SetGroupByFolderCommand     { get; }
    public ICommand SetSortNameCommand          { get; }
    public ICommand SetSortLastPrintedCommand   { get; }
    public ICommand SetSortDateCreatedCommand   { get; }
    public ICommand SaveNewPresetCommand        { get; }
    public ICommand SelectAllFieldGroupsCommand { get; }
    public ICommand ClearFieldGroupsCommand     { get; }
    public ICommand ResetFiltersCommand         { get; }
    public ICommand LoadSelectedCommand         { get; }
    public ICommand ToggleFavoriteCommand       { get; }
    public ICommand ClearSeedDataCommand        { get; }
    public ICommand DeletePresetCommand         { get; }
    public ICommand UpdatePresetCommand         { get; }
    public ICommand BeginRenameCommand          { get; }
    public ICommand CommitRenameCommand         { get; }
    public ICommand CancelRenameCommand         { get; }

    private readonly AdditiveSettingsViewModel _additive;

    /// <summary>Field-group names — same order they render in the Save-as-Preset checklist.
    /// See SaveNewPreset/ApplyPresetToAdditive for exactly which properties each group gates.</summary>
    private static readonly string[] FieldGroupNames =
    {
        "Geometry & layers", "Slicing mode & method", "Live effector", "Pattern & texture",
        "X-Bracing wall", "Wave effect", "Infill", "Overhang & orientation",
        "Toolhead orientation", "Motion & KUKA frame", "Temperatures",
        "KRL export tuning", "Movement (z-hop / wipe / resume)", "Adaptive layer speed",
        "KRL post-process", "Adaptive layer height", "Stock from Maps", "Brim",
    };

    private const string MasterDefaultsPresetName = "Master Defaults";

    /// <summary>
    /// Off by default — real presets (Thom's "HHN Nasty Wall" etc.) replaced the ~80 generated
    /// sample presets once real testing started. Flip this back to true to bring the sample data
    /// back for UI testing; the generator itself (<see cref="SeedSampleData"/>) is untouched.
    /// </summary>
    private const bool SeedTestDataOnStartup = false;

    public PresetsCardViewModel(AdditiveSettingsViewModel additive)
    {
        _additive = additive;

        AllPresets = new ObservableCollection<PrintPresetSample>();
        if (SeedTestDataOnStartup) SeedSampleData();
        LoadPersistedPresets();
        EnsureMasterDefaultsPreset();

        SaveFieldGroups = new ObservableCollection<PresetFieldGroupOption>(
            FieldGroupNames.Select(name => new PresetFieldGroupOption { Name = name }));

        // Numeric fields are dropdowns over whatever values have actually been saved, same as the
        // text fields below — not a free-form range, so there's nothing to type/drag that could
        // land on a value no preset actually uses.
        ChoiceFilters.Add(MakeNumericChoiceFilter("Bead width (mm)", p => p.BeadWidth));
        ChoiceFilters.Add(MakeNumericChoiceFilter("Layer height (mm)", p => p.LayerHeight));
        ChoiceFilters.Add(MakeNumericChoiceFilter("Print speed (mm/s)", p => p.PrintSpeed));

        ChoiceFilters.Add(MakeChoiceFilter("Material", p => p.Material));
        ChoiceFilters.Add(MakeChoiceFilter("Method", p => p.Method ?? ""));
        ChoiceFilters.Add(MakeChoiceFilter("Pattern", p => p.PatternType ?? ""));
        ChoiceFilters.Add(MakeChoiceFilter("Seam mode", p => p.SeamMode ?? ""));
        ChoiceFilters.Add(MakeChoiceFilter("Folder", p => p.Folder));
        foreach (var f in ChoiceFilters) f.PropertyChanged += (_, _) => Refresh();

        ToggleFilterDrawerCommand   = new RelayCommand(() => IsFilterDrawerOpen = !IsFilterDrawerOpen);
        ToggleSaveDialogCommand     = new RelayCommand(() => IsSaveDialogOpen = !IsSaveDialogOpen);
        SetGroupByNoneCommand       = new RelayCommand(() => GroupingMode = PresetGroupingMode.None);
        SetGroupByMethodCommand     = new RelayCommand(() => GroupingMode = PresetGroupingMode.ByMethod);
        SetGroupByFolderCommand     = new RelayCommand(() => GroupingMode = PresetGroupingMode.ByFolder);
        SetSortNameCommand          = new RelayCommand(() => SortMode = PresetSortMode.Name);
        SetSortLastPrintedCommand   = new RelayCommand(() => SortMode = PresetSortMode.LastPrinted);
        SetSortDateCreatedCommand   = new RelayCommand(() => SortMode = PresetSortMode.DateCreated);
        SaveNewPresetCommand        = new RelayCommand(SaveNewPreset);
        SelectAllFieldGroupsCommand = new RelayCommand(() => SetAllFieldGroupsIncluded(true));
        ClearFieldGroupsCommand     = new RelayCommand(() => SetAllFieldGroupsIncluded(false));
        ResetFiltersCommand         = new RelayCommand(ResetFilters);
        LoadSelectedCommand         = new RelayCommand(LoadSelected);
        RemoveSearchTagCommand      = new RelayCommand<string>(RemoveSearchTag);
        ToggleFavoriteCommand       = new RelayCommand<PrintPresetSample>(ToggleFavorite);
        ClearSeedDataCommand        = new RelayCommand(ClearSeedData);
        // Delete/Update/Rename are reachable from each row's "⋮" button/Flyout (see
        // RightPanelView), not a right-click ContextMenu — a ContextMenu's Popup proved unreliable
        // to open reliably on a virtualized ListBox row (sometimes wouldn't open at all, tried
        // first with a right-click gesture). A Button.Flyout doesn't have that problem (same
        // mechanism the row's ⓘ info popup already uses), so CommandParameter="{Binding}" here is
        // trustworthy.
        DeletePresetCommand         = new RelayCommand<PrintPresetSample>(DeletePreset);
        UpdatePresetCommand         = new RelayCommand<PrintPresetSample>(UpdatePreset);
        BeginRenameCommand          = new RelayCommand<PrintPresetSample>(BeginRename);
        CommitRenameCommand         = new RelayCommand(CommitRename);
        CancelRenameCommand         = new RelayCommand(CancelRename);

        RecomputeSiblingLinks();
        Refresh();
    }

    private bool IsGroupIncluded(string groupName) =>
        SaveFieldGroups.FirstOrDefault(g => g.Name == groupName)?.IsIncluded ?? true;

    /// <summary>
    /// One-time seed: a "Master Defaults" preset capturing every field from a brand-new, untouched
    /// AdditiveSettingsViewModel — i.e. the slicer's real out-of-the-box baseline, not whatever
    /// happens to be dialed in on the live panel. Created once if not already present (by name);
    /// after that it's a normal saved preset the user can edit/delete like any other.
    /// </summary>
    private void EnsureMasterDefaultsPreset()
    {
        if (AllPresets.Any(p => p.Name == MasterDefaultsPresetName)) return;

        var d = new AdditiveSettingsViewModel();
        var preset = new PrintPresetSample
        {
            Name           = MasterDefaultsPresetName,
            Folder         = "Uncategorized",
            CreatedUtc     = DateTime.UtcNow,
            Material       = d.SelectedPreset?.Name ?? "",

            BeadWidth = d.BeadWidth, LayerHeight = d.LayerHeight, TiltAngle = d.TiltAngle,
            TiltAngleX = d.TiltAngleX, MultiPlanarAxisX = d.MultiPlanarAxisX,
            MultiPlanarPlanes = d.MultiPlanarPlanes.Select(r => new PresetPlaneRow { HeightPct = r.HeightPct, AngleDeg = r.AngleDeg }).ToList(),

            Method = FormatSliceMethod(d.Method), SeamMode = d.SeamMode, SlicingMode = d.SlicingMode,
            OrientationFollowPercent = d.OrientationFollowPercent, OrientationMaxTiltDeg = d.OrientationMaxTiltDeg,
            FirstLayerZeroTilt = d.FirstLayerZeroTilt, LayerLeanPercent = d.LayerLeanPercent,
            LayerLeanMaxTiltDeg = d.LayerLeanMaxTiltDeg, CurvedBoundarySourceDisplay = d.CurvedBoundarySourceDisplay,
            CurvedAutoDetectBandMm = d.CurvedAutoDetectBandMm, CurvedEnableRegionSplit = d.CurvedEnableRegionSplit,

            EffectorEnabled = d.EffectorEnabled, EffectorMode = d.EffectorMode,
            EffectorRange = d.EffectorRange, EffectorStrength = d.EffectorStrength,

            PatternType = d.PatternType, PatternMapping = d.PatternMapping, PatternWavelengthMm = d.PatternWavelengthMm,
            PatternAmplitude = d.PatternAmplitude, PatternFrequency = d.PatternFrequency, PatternTwist = d.PatternTwist,
            PatternOffset = d.PatternOffset, PatternFadeIn = d.PatternFadeIn, PatternFadeOut = d.PatternFadeOut,

            XBracingEnabled = d.XBracingEnabled, XBracingProjectionType = d.XBracingProjectionType,
            XBracingShowHelper = d.XBracingShowHelper, XBracingPlaneTiltY = d.XBracingPlaneTiltY,
            XBracingPlaneTiltX = d.XBracingPlaneTiltX, XBracingCylinderDiameterMm = d.XBracingCylinderDiameterMm,
            XBracingCylinderFlipDirection = d.XBracingCylinderFlipDirection, XBracingDepthMm = d.XBracingDepthMm,
            XBracingDepthBottomMm = d.XBracingDepthBottomMm, XBracingDepthEaseBottom = d.XBracingDepthEaseBottom,
            XBracingDepthEaseTop = d.XBracingDepthEaseTop, XBracingSpanMm = d.XBracingSpanMm,
            XBracingAngleDeg = d.XBracingAngleDeg, XBracingExtendEdges = d.XBracingExtendEdges,

            WaveEffect = d.WaveEffect, WaveAmplitude = d.WaveAmplitude, WaveFrequencyMode = d.WaveFrequencyMode,
            WaveWavelength = d.WaveWavelength, WaveCycles = d.WaveCycles, WaveShape = d.WaveShape,
            WaveStagger = d.WaveStagger, WavePhaseMethodIndex = d.WavePhaseMethodIndex, WaveGradient = d.WaveGradient,
            WaveAmplitudeBottom = d.WaveAmplitudeBottom, WaveAmplitudeTop = d.WaveAmplitudeTop,
            WaveWavelengthBottom = d.WaveWavelengthBottom, WaveWavelengthTop = d.WaveWavelengthTop,
            WaveGradientCenter = d.WaveGradientCenter, WaveGradientCurve = d.WaveGradientCurve,

            InfillPattern = d.InfillPattern, InfillSpacingMm = d.InfillSpacingMm, InfillAngleDeg = d.InfillAngleDeg,
            LightningOverhangDeg = d.LightningOverhangDeg, LightningBranchSpacingMm = d.LightningBranchSpacingMm,
            LightningTipLoopRadiusMm = d.LightningTipLoopRadiusMm, LightningAffectInterior = d.LightningAffectInterior,
            LightningAffectExterior = d.LightningAffectExterior, LightningTargetSupportSelections = d.LightningTargetSupportSelections,
            LightningButtressBarMm = d.LightningButtressBarMm,

            OverhangOrientation = d.OverhangOrientation, MaxOverhangTiltDeg = d.MaxOverhangTiltDeg,
            ZigZagAllowSameLayerTravel = d.ZigZagAllowSameLayerTravel, DisableContourOffset = d.DisableContourOffset,

            ToolheadA = d.ToolheadA, ToolheadB = d.ToolheadB, ToolheadC = d.ToolheadC,

            PrintSpeed = d.PrintSpeed, TravelSpeed = d.TravelSpeed, ApoCvel = d.ApoCvel,
            E1MotionEnabled = d.E1MotionEnabled, E1YPlusMm = d.E1YPlusMm, E1YMinusMm = d.E1YMinusMm,
            SmoothRotation = d.SmoothRotation, SmoothRotationRadius = d.SmoothRotationRadius,
            SmoothRotationMaxRateDegPerMm = d.SmoothRotationMaxRateDegPerMm,
            OrientationLookAheadMm = d.OrientationLookAheadMm, OrientationSigmaMm = d.OrientationSigmaMm,

            Temperature1 = d.Temperature1, Temperature2 = d.Temperature2, Temperature3 = d.Temperature3,

            TemperatureOffset = d.TemperatureOffset, ExtrusionSpeedOffset = d.ExtrusionSpeedOffset,
            DigitalStartStopEnabled = d.DigitalStartStopEnabled, ExtrusionStartWaitSec = d.ExtrusionStartWaitSec,
            ExtrusionResumeWaitSec = d.ExtrusionResumeWaitSec,

            ZHopMm = d.ZHopMm, WipeModeDisplay = d.WipeModeDisplay, WipeLengthMm = d.WipeLengthMm,
            WipeRampMm = d.WipeRampMm, WipeSpeed = d.WipeSpeed, WipeSkipShortTravels = d.WipeSkipShortTravels,
            ResumeRampEnabled = d.ResumeRampEnabled, ResumeRampStartSpeed = d.ResumeRampStartSpeed,
            ResumeRampStartRpmPercent = d.ResumeRampStartRpmPercent, ResumeRampDistanceMm = d.ResumeRampDistanceMm,
            ResumeRampSteps = d.ResumeRampSteps,

            LayerSpeedAdaptEnabled = d.LayerSpeedAdaptEnabled, LayerSpeedBasisDisplay = d.LayerSpeedBasisDisplay,
            LayerSpeedMinMmS = d.LayerSpeedMinMmS, LayerSpeedMaxMmS = d.LayerSpeedMaxMmS,

            KrlHeaderText = d.KrlPostProcess.HeaderText, KrlFooterText = d.KrlPostProcess.FooterText,

            AdaptiveLayerHeight = d.AdaptiveLayerHeight, MinLayerHeight = d.MinLayerHeight, AdaptiveQuality = d.AdaptiveQuality,

            UseDisplacedStock = d.UseDisplacedStock, StockAllowanceMm = d.StockAllowanceMm,

            BrimEnabled = d.BrimEnabled, BrimLoops = d.BrimLoops,
        };

        AllPresets.Add(preset);
        PersistUserPresets();
    }

    /// <summary>
    /// ~80 sample presets: a handful of "blueprints" (real settings combos) each saved under
    /// several different names/folders — deliberately demonstrating the fingerprint/sibling-badge
    /// idea from the naming/duplication discussion — plus a broad procedural spread of one-off
    /// combos (most presets in the wild won't turn out to duplicate anything, which is the point).
    /// </summary>
    private void SeedSampleData()
    {
        var now = DateTime.UtcNow;

        var blueprints = new (double Bead, double Layer, double Speed, string Method, string Pattern, string Seam, bool Bracing, string Material, string[] Variants, string[] Folders)[]
        {
            (6.5, 3.0, 120, "Planar", "Smooth", "Normal", false, "ABS",
                new[] { "HHN 6.5 Bead - Fast Wall", "AV5 Standard Wall", "HEC Base Print", "MDX Default Wall" },
                new[] { "HHN", "AV5", "HEC", "MDX" }),

            (6.5, 1.5, 80, "Planar", "Smooth", "Zig-zag", false, "ABS",
                new[] { "HHN 6.5 Bead - Fine Detail", "AV5 Fine Detail Pass" },
                new[] { "HHN", "AV5" }),

            (6.5, 3.0, 90, "Planar", "Smooth", "Zig-zag", true, "PETG",
                new[] { "HHN 6.5 Bead - Braced Wall", "MDX 6.5mm Zig-zag Braced", "HEC Braced Standard" },
                new[] { "HHN", "MDX", "HEC" }),

            (8.0, 4.0, 100, "Angled", "Wave", "Normal", false, "PETG",
                new[] { "Curtain Wall - Angled", "R&D Wave Panel" },
                new[] { "Production", "R&D" }),

            (8.0, 4.0, 95, "Angled", "Wave", "Zig-zag", true, "PETG",
                new[] { "Curtain Wall - Zig-zag Seam", "AV5 Braced Curtain" },
                new[] { "Production", "AV5" }),

            (9.0, 4.5, 85, "Multi-Planar", "Smooth", "Normal", true, "PP",
                new[] { "Braced Column - Heavy Wall", "HEC Heavy Column", "MDX Braced Column Standard" },
                new[] { "Production", "HEC", "MDX" }),

            (7.0, 3.0, 100, "Planar", "Smooth", "Zig-zag", false, "Nylon",
                new[] { "MDX Zig-zag Test" },
                new[] { "Experiments" }),

            (4.0, 2.0, 60, "Geodesic", "Spiral (vase)", "Spiral (vase)", false, "PLA",
                new[] { "Geodesic Test - Small Vase" },
                new[] { "Experiments" }),

            (5.5, 2.5, 70, "Curved", "Ghost Mesh Grid", "Normal", false, "TPU",
                new[] { "Curved Sweep - Vessel" },
                new[] { "Experiments" }),

            (10.0, 5.0, 150, "Planar", "Rectilinear", "Normal", false, "PP",
                new[] { "Relief Blank - Rough", "HEC Rough Fill Blank" },
                new[] { "Production", "HEC" }),

            (7.0, 3.5, 110, "Multi-Planar", "Smooth", "Normal", false, "ABS",
                new[] { "Multi-Planar Column" },
                new[] { "Production" }),
        };

        var idx = 0;
        foreach (var bp in blueprints)
        {
            for (var v = 0; v < bp.Variants.Length; v++)
            {
                AllPresets.Add(new PrintPresetSample
                {
                    Name            = bp.Variants[v],
                    BeadWidth       = bp.Bead,
                    LayerHeight     = bp.Layer,
                    PrintSpeed      = bp.Speed,
                    Method          = bp.Method,
                    PatternType     = bp.Pattern,
                    SeamMode        = bp.Seam,
                    XBracingEnabled = bp.Bracing,
                    Material        = bp.Material,
                    Folder          = bp.Folders[v],
                    CreatedUtc      = now.AddDays(-(45 - idx)),
                    LastPrintedUtc  = idx % 3 == 0 ? now.AddDays(-(idx % 20)) : null,
                    IsSeeded        = true,
                });
                idx++;
            }
        }

        var extraMethods   = new[] { "Planar", "Angled", "Multi-Planar", "Geodesic", "Curved" };
        var extraPatterns  = new[] { "Smooth", "Rectilinear", "Wave", "Ghost Mesh Grid", "Formbound Bridge", "Triangle", "Bumps" };
        var extraSeams     = new[] { "Normal", "Zig-zag" };
        var extraFolders   = new[] { "HHN", "Production", "Experiments", "AV5", "HEC", "MDX", "R&D" };
        var extraMaterials = new[] { "ABS", "PETG", "PP", "Nylon", "PLA", "TPU", "PC" };

        for (var i = 0; i < 58; i++)
        {
            var bead     = Math.Round(4.0 + i % 13 * 0.5, 1);
            var layer    = Math.Round(1.5 + i % 8 * 0.5, 1);
            var speed    = 60 + i % 10 * 10;
            var method   = extraMethods[i % extraMethods.Length];
            var pattern  = extraPatterns[i / 3 % extraPatterns.Length];
            var seam     = extraSeams[i % extraSeams.Length];
            var brace    = i % 5 == 0;
            var folder   = extraFolders[i / 2 % extraFolders.Length];
            var material = extraMaterials[i % extraMaterials.Length];
            var name     = $"{folder} {bead:0.#}mm {(brace ? "Braced " : "")}{pattern} Run {i + 1}";

            AllPresets.Add(new PrintPresetSample
            {
                Name            = name,
                BeadWidth       = bead,
                LayerHeight     = layer,
                PrintSpeed      = speed,
                Method          = method,
                PatternType     = pattern,
                SeamMode        = seam,
                XBracingEnabled = brace,
                Material        = material,
                Folder          = folder,
                CreatedUtc      = now.AddDays(-(60 - i)),
                LastPrintedUtc  = i % 4 == 0 ? now.AddDays(-(i % 15)) : null,
                IsSeeded        = true,
            });
        }
    }

    private ChoiceFilterViewModel MakeChoiceFilter(string fieldName, Func<PrintPresetSample, string> selector)
    {
        var filter = new ChoiceFilterViewModel { FieldName = fieldName, Selector = selector };
        filter.Options.Add(ChoiceFilterViewModel.AnyOption);
        filter.RefreshOptions(AllPresets.Select(selector));
        return filter;
    }

    /// <summary>A choice filter over a numeric field — same dropdown-of-saved-values UI as
    /// MakeChoiceFilter, but sorted by value (NumericSortKey) instead of alphabetically, so bead
    /// widths like 4/6/8/10 don't end up ordered "10, 4, 6, 8". Presets missing the field entirely
    /// are excluded from the option list (nothing to pick), same as they'd fail to Match below.</summary>
    private ChoiceFilterViewModel MakeNumericChoiceFilter(string fieldName, Func<PrintPresetSample, double?> selector)
    {
        string Format(PrintPresetSample p) => selector(p) is { } v ? v.ToString("0.##") : "";
        var filter = new ChoiceFilterViewModel
        {
            FieldName      = fieldName,
            Selector       = Format,
            NumericSortKey = s => double.TryParse(s, out var d) ? d : double.MaxValue,
        };
        filter.Options.Add(ChoiceFilterViewModel.AnyOption);
        filter.RefreshOptions(AllPresets.Select(Format).Where(s => s.Length > 0));
        return filter;
    }

    /// <summary>Only flips checkbox state — never touches the underlying preset values.</summary>
    private void SetAllFieldGroupsIncluded(bool included)
    {
        foreach (var g in SaveFieldGroups) g.IsIncluded = included;
    }

    private void ResetFilters()
    {
        foreach (var f in ChoiceFilters) f.Reset();
    }

    /// <summary>
    /// Applies a preset's fields directly onto the real <see cref="AdditiveSettingsViewModel"/> —
    /// this is genuinely real now, not a comp stub. Deliberately does NOT stamp LastPrintedUtc:
    /// a preset only counts as "used" once it's actually driven a real print (export/send-to-
    /// robot), not merely applied to the panel; that hook doesn't exist yet, so LastPrintedUtc
    /// for now only reflects the seeded sample data.
    /// </summary>
    private void LoadSelected()
    {
        if (SelectedPreset is null) return;
        ApplyPresetToAdditive(SelectedPreset);
        StatusMessage = $"Applied \"{SelectedPreset.Name}\" to the Additive panel";
    }

    /// <summary>
    /// Only touches a field if the preset actually carries it (non-null) — a preset saved with
    /// some groups unchecked leaves the rest of the panel exactly as it was.
    /// </summary>
    private void ApplyPresetToAdditive(PrintPresetSample p)
    {
        if (p.BeadWidth is { } beadWidth) _additive.BeadWidth = beadWidth;
        if (p.LayerHeight is { } layerHeight) _additive.LayerHeight = layerHeight;
        if (p.TiltAngle is { } tiltAngle) _additive.TiltAngle = tiltAngle;
        if (p.TiltAngleX is { } tiltAngleX) _additive.TiltAngleX = tiltAngleX;
        if (p.MultiPlanarAxisX is { } multiPlanarAxisX) _additive.MultiPlanarAxisX = multiPlanarAxisX;
        if (p.MultiPlanarPlanes is { } planes)
        {
            _additive.MultiPlanarPlanes.Clear();
            foreach (var row in planes)
                _additive.MultiPlanarPlanes.Add(new MultiPlanarPlaneRow(row.HeightPct, row.AngleDeg));
        }

        if (p.Method is { } method) _additive.Method = ParseSliceMethod(method);
        if (p.SeamMode is { } seamMode) _additive.SeamMode = seamMode;
        if (p.SlicingMode is { } slicingMode) _additive.SlicingMode = slicingMode;
        if (p.OrientationFollowPercent is { } orientationFollowPercent) _additive.OrientationFollowPercent = orientationFollowPercent;
        if (p.OrientationMaxTiltDeg is { } orientationMaxTiltDeg) _additive.OrientationMaxTiltDeg = orientationMaxTiltDeg;
        if (p.FirstLayerZeroTilt is { } firstLayerZeroTilt) _additive.FirstLayerZeroTilt = firstLayerZeroTilt;
        if (p.LayerLeanPercent is { } layerLeanPercent) _additive.LayerLeanPercent = layerLeanPercent;
        if (p.LayerLeanMaxTiltDeg is { } layerLeanMaxTiltDeg) _additive.LayerLeanMaxTiltDeg = layerLeanMaxTiltDeg;
        if (p.CurvedBoundarySourceDisplay is { } curvedBoundarySourceDisplay) _additive.CurvedBoundarySourceDisplay = curvedBoundarySourceDisplay;
        if (p.CurvedAutoDetectBandMm is { } curvedAutoDetectBandMm) _additive.CurvedAutoDetectBandMm = curvedAutoDetectBandMm;
        if (p.CurvedEnableRegionSplit is { } curvedEnableRegionSplit) _additive.CurvedEnableRegionSplit = curvedEnableRegionSplit;

        if (p.EffectorEnabled is { } effectorEnabled) _additive.EffectorEnabled = effectorEnabled;
        if (p.EffectorMode is { } effectorMode) _additive.EffectorMode = effectorMode;
        if (p.EffectorRange is { } effectorRange) _additive.EffectorRange = effectorRange;
        if (p.EffectorStrength is { } effectorStrength) _additive.EffectorStrength = effectorStrength;

        if (p.PatternType is { } patternType) _additive.PatternType = patternType;
        if (p.PatternMapping is { } patternMapping) _additive.PatternMapping = patternMapping;
        if (p.PatternWavelengthMm is { } patternWavelengthMm) _additive.PatternWavelengthMm = patternWavelengthMm;
        if (p.PatternAmplitude is { } patternAmplitude) _additive.PatternAmplitude = patternAmplitude;
        if (p.PatternFrequency is { } patternFrequency) _additive.PatternFrequency = patternFrequency;
        if (p.PatternTwist is { } patternTwist) _additive.PatternTwist = patternTwist;
        if (p.PatternOffset is { } patternOffset) _additive.PatternOffset = patternOffset;
        if (p.PatternFadeIn is { } patternFadeIn) _additive.PatternFadeIn = patternFadeIn;
        if (p.PatternFadeOut is { } patternFadeOut) _additive.PatternFadeOut = patternFadeOut;

        if (p.XBracingEnabled is { } xBracingEnabled) _additive.XBracingEnabled = xBracingEnabled;
        if (p.XBracingProjectionType is { } xBracingProjectionType) _additive.XBracingProjectionType = xBracingProjectionType;
        if (p.XBracingShowHelper is { } xBracingShowHelper) _additive.XBracingShowHelper = xBracingShowHelper;
        if (p.XBracingPlaneTiltY is { } xBracingPlaneTiltY) _additive.XBracingPlaneTiltY = xBracingPlaneTiltY;
        if (p.XBracingPlaneTiltX is { } xBracingPlaneTiltX) _additive.XBracingPlaneTiltX = xBracingPlaneTiltX;
        if (p.XBracingCylinderDiameterMm is { } xBracingCylinderDiameterMm) _additive.XBracingCylinderDiameterMm = xBracingCylinderDiameterMm;
        if (p.XBracingCylinderFlipDirection is { } xBracingCylinderFlipDirection) _additive.XBracingCylinderFlipDirection = xBracingCylinderFlipDirection;
        if (p.XBracingDepthMm is { } xBracingDepthMm) _additive.XBracingDepthMm = xBracingDepthMm;
        if (p.XBracingDepthBottomMm is { } xBracingDepthBottomMm) _additive.XBracingDepthBottomMm = xBracingDepthBottomMm;
        if (p.XBracingDepthEaseBottom is { } xBracingDepthEaseBottom) _additive.XBracingDepthEaseBottom = xBracingDepthEaseBottom;
        if (p.XBracingDepthEaseTop is { } xBracingDepthEaseTop) _additive.XBracingDepthEaseTop = xBracingDepthEaseTop;
        if (p.XBracingSpanMm is { } xBracingSpanMm) _additive.XBracingSpanMm = xBracingSpanMm;
        if (p.XBracingAngleDeg is { } xBracingAngleDeg) _additive.XBracingAngleDeg = xBracingAngleDeg;
        if (p.XBracingExtendEdges is { } xBracingExtendEdges) _additive.XBracingExtendEdges = xBracingExtendEdges;

        if (p.WaveEffect is { } waveEffect) _additive.WaveEffect = waveEffect;
        if (p.WaveAmplitude is { } waveAmplitude) _additive.WaveAmplitude = waveAmplitude;
        if (p.WaveFrequencyMode is { } waveFrequencyMode) _additive.WaveFrequencyMode = waveFrequencyMode;
        if (p.WaveWavelength is { } waveWavelength) _additive.WaveWavelength = waveWavelength;
        if (p.WaveCycles is { } waveCycles) _additive.WaveCycles = waveCycles;
        if (p.WaveShape is { } waveShape) _additive.WaveShape = waveShape;
        if (p.WaveStagger is { } waveStagger) _additive.WaveStagger = waveStagger;
        if (p.WavePhaseMethodIndex is { } wavePhaseMethodIndex) _additive.WavePhaseMethodIndex = wavePhaseMethodIndex;
        if (p.WaveGradient is { } waveGradient) _additive.WaveGradient = waveGradient;
        if (p.WaveAmplitudeBottom is { } waveAmplitudeBottom) _additive.WaveAmplitudeBottom = waveAmplitudeBottom;
        if (p.WaveAmplitudeTop is { } waveAmplitudeTop) _additive.WaveAmplitudeTop = waveAmplitudeTop;
        if (p.WaveWavelengthBottom is { } waveWavelengthBottom) _additive.WaveWavelengthBottom = waveWavelengthBottom;
        if (p.WaveWavelengthTop is { } waveWavelengthTop) _additive.WaveWavelengthTop = waveWavelengthTop;
        if (p.WaveGradientCenter is { } waveGradientCenter) _additive.WaveGradientCenter = waveGradientCenter;
        if (p.WaveGradientCurve is { } waveGradientCurve) _additive.WaveGradientCurve = waveGradientCurve;

        if (p.InfillPattern is { } infillPattern) _additive.InfillPattern = infillPattern;
        if (p.InfillSpacingMm is { } infillSpacingMm) _additive.InfillSpacingMm = infillSpacingMm;
        if (p.InfillAngleDeg is { } infillAngleDeg) _additive.InfillAngleDeg = infillAngleDeg;
        if (p.LightningOverhangDeg is { } lightningOverhangDeg) _additive.LightningOverhangDeg = lightningOverhangDeg;
        if (p.LightningBranchSpacingMm is { } lightningBranchSpacingMm) _additive.LightningBranchSpacingMm = lightningBranchSpacingMm;
        if (p.LightningTipLoopRadiusMm is { } lightningTipLoopRadiusMm) _additive.LightningTipLoopRadiusMm = lightningTipLoopRadiusMm;
        if (p.LightningAffectInterior is { } lightningAffectInterior) _additive.LightningAffectInterior = lightningAffectInterior;
        if (p.LightningAffectExterior is { } lightningAffectExterior) _additive.LightningAffectExterior = lightningAffectExterior;
        if (p.LightningTargetSupportSelections is { } lightningTargetSupportSelections) _additive.LightningTargetSupportSelections = lightningTargetSupportSelections;
        if (p.LightningButtressBarMm is { } lightningButtressBarMm) _additive.LightningButtressBarMm = lightningButtressBarMm;

        if (p.OverhangOrientation is { } overhangOrientation) _additive.OverhangOrientation = overhangOrientation;
        if (p.MaxOverhangTiltDeg is { } maxOverhangTiltDeg) _additive.MaxOverhangTiltDeg = maxOverhangTiltDeg;
        if (p.ZigZagAllowSameLayerTravel is { } zigZagAllowSameLayerTravel) _additive.ZigZagAllowSameLayerTravel = zigZagAllowSameLayerTravel;
        if (p.DisableContourOffset is { } disableContourOffset) _additive.DisableContourOffset = disableContourOffset;

        if (p.ToolheadA is { } toolheadA) _additive.ToolheadA = toolheadA;
        if (p.ToolheadB is { } toolheadB) _additive.ToolheadB = toolheadB;
        if (p.ToolheadC is { } toolheadC) _additive.ToolheadC = toolheadC;

        if (p.PrintSpeed is { } printSpeed) _additive.PrintSpeed = printSpeed;
        if (p.TravelSpeed is { } travelSpeed) _additive.TravelSpeed = travelSpeed;
        if (p.ApoCvel is { } apoCvel) _additive.ApoCvel = apoCvel;
        if (p.E1MotionEnabled is { } e1MotionEnabled) _additive.E1MotionEnabled = e1MotionEnabled;
        if (p.E1YPlusMm is { } e1YPlusMm) _additive.E1YPlusMm = e1YPlusMm;
        if (p.E1YMinusMm is { } e1YMinusMm) _additive.E1YMinusMm = e1YMinusMm;
        if (p.SmoothRotation is { } smoothRotation) _additive.SmoothRotation = smoothRotation;
        if (p.SmoothRotationRadius is { } smoothRotationRadius) _additive.SmoothRotationRadius = smoothRotationRadius;
        if (p.SmoothRotationMaxRateDegPerMm is { } smoothRotationMaxRateDegPerMm) _additive.SmoothRotationMaxRateDegPerMm = smoothRotationMaxRateDegPerMm;
        if (p.OrientationLookAheadMm is { } orientationLookAheadMm) _additive.OrientationLookAheadMm = orientationLookAheadMm;
        if (p.OrientationSigmaMm is { } orientationSigmaMm) _additive.OrientationSigmaMm = orientationSigmaMm;

        if (p.Temperature1 is { } temperature1) _additive.Temperature1 = temperature1;
        if (p.Temperature2 is { } temperature2) _additive.Temperature2 = temperature2;
        if (p.Temperature3 is { } temperature3) _additive.Temperature3 = temperature3;

        if (p.TemperatureOffset is { } temperatureOffset) _additive.TemperatureOffset = temperatureOffset;
        if (p.ExtrusionSpeedOffset is { } extrusionSpeedOffset) _additive.ExtrusionSpeedOffset = extrusionSpeedOffset;
        if (p.DigitalStartStopEnabled is { } digitalStartStopEnabled) _additive.DigitalStartStopEnabled = digitalStartStopEnabled;
        if (p.ExtrusionStartWaitSec is { } extrusionStartWaitSec) _additive.ExtrusionStartWaitSec = extrusionStartWaitSec;
        if (p.ExtrusionResumeWaitSec is { } extrusionResumeWaitSec) _additive.ExtrusionResumeWaitSec = extrusionResumeWaitSec;

        if (p.ZHopMm is { } zHopMm) _additive.ZHopMm = zHopMm;
        if (p.WipeModeDisplay is { } wipeModeDisplay) _additive.WipeModeDisplay = wipeModeDisplay;
        if (p.WipeLengthMm is { } wipeLengthMm) _additive.WipeLengthMm = wipeLengthMm;
        if (p.WipeRampMm is { } wipeRampMm) _additive.WipeRampMm = wipeRampMm;
        if (p.WipeSpeed is { } wipeSpeed) _additive.WipeSpeed = wipeSpeed;
        if (p.WipeSkipShortTravels is { } wipeSkipShortTravels) _additive.WipeSkipShortTravels = wipeSkipShortTravels;
        if (p.ResumeRampEnabled is { } resumeRampEnabled) _additive.ResumeRampEnabled = resumeRampEnabled;
        if (p.ResumeRampStartSpeed is { } resumeRampStartSpeed) _additive.ResumeRampStartSpeed = resumeRampStartSpeed;
        if (p.ResumeRampStartRpmPercent is { } resumeRampStartRpmPercent) _additive.ResumeRampStartRpmPercent = resumeRampStartRpmPercent;
        if (p.ResumeRampDistanceMm is { } resumeRampDistanceMm) _additive.ResumeRampDistanceMm = resumeRampDistanceMm;
        if (p.ResumeRampSteps is { } resumeRampSteps) _additive.ResumeRampSteps = resumeRampSteps;

        if (p.LayerSpeedAdaptEnabled is { } layerSpeedAdaptEnabled) _additive.LayerSpeedAdaptEnabled = layerSpeedAdaptEnabled;
        if (p.LayerSpeedBasisDisplay is { } layerSpeedBasisDisplay) _additive.LayerSpeedBasisDisplay = layerSpeedBasisDisplay;
        if (p.LayerSpeedMinMmS is { } layerSpeedMinMmS) _additive.LayerSpeedMinMmS = layerSpeedMinMmS;
        if (p.LayerSpeedMaxMmS is { } layerSpeedMaxMmS) _additive.LayerSpeedMaxMmS = layerSpeedMaxMmS;

        if (p.KrlHeaderText is { } krlHeaderText) _additive.KrlPostProcess.HeaderText = krlHeaderText;
        if (p.KrlFooterText is { } krlFooterText) _additive.KrlPostProcess.FooterText = krlFooterText;

        if (p.AdaptiveLayerHeight is { } adaptiveLayerHeight) _additive.AdaptiveLayerHeight = adaptiveLayerHeight;
        if (p.MinLayerHeight is { } minLayerHeight) _additive.MinLayerHeight = minLayerHeight;
        if (p.AdaptiveQuality is { } adaptiveQuality) _additive.AdaptiveQuality = adaptiveQuality;

        if (p.UseDisplacedStock is { } useDisplacedStock) _additive.UseDisplacedStock = useDisplacedStock;
        if (p.StockAllowanceMm is { } stockAllowanceMm) _additive.StockAllowanceMm = stockAllowanceMm;

        if (p.BrimEnabled is { } brimEnabled) _additive.BrimEnabled = brimEnabled;
        if (p.BrimLoops is { } brimLoops) _additive.BrimLoops = brimLoops;

        if (!string.IsNullOrEmpty(p.Material))
        {
            var idx = _additive.MaterialPresets.ToList().FindIndex(mp => mp.Name == p.Material);
            if (idx >= 0) _additive.SelectedPresetIndex = idx;
        }
    }

    /// <summary>Maps this comp's simplified method labels ("Multi-Planar") onto the real
    /// <see cref="SliceMethod"/> enum. Unrecognized values fall back to Planar rather than throw —
    /// a preset with a typo'd/legacy method shouldn't block applying everything else.</summary>
    private static SliceMethod ParseSliceMethod(string method) => method switch
    {
        "Angled"       => SliceMethod.Angled,
        "Geodesic"     => SliceMethod.Geodesic,
        "Curved"       => SliceMethod.Curved,
        "Multi-Planar" => SliceMethod.MultiPlanar,
        _              => SliceMethod.Planar,
    };

    /// <summary>Reverse of <see cref="ParseSliceMethod"/> — the real enum's spelling back to
    /// this comp's display labels, used when saving the panel's CURRENT method into a preset.</summary>
    private static string FormatSliceMethod(SliceMethod method) => method switch
    {
        SliceMethod.Angled     => "Angled",
        SliceMethod.Geodesic   => "Geodesic",
        SliceMethod.Curved     => "Curved",
        SliceMethod.MultiPlanar=> "Multi-Planar",
        _                      => "Planar",
    };

    private void ToggleFavorite(PrintPresetSample? p)
    {
        if (p is null) return;
        p.IsFavorite = !p.IsFavorite;
        PersistUserPresets();
        Refresh();
    }

    /// <summary>Deletes one preset — works on both a real (Saved/Imported) preset and a seeded
    /// sample; the row's own confirm-flyout (see RightPanelView) is the only guard, there's no
    /// undo. PersistUserPresets no-ops for a seeded preset (never written to disk anyway) but is
    /// still safe/cheap to call unconditionally here.</summary>
    private void DeletePreset(PrintPresetSample? p)
    {
        if (p is null) return;
        if (!AllPresets.Remove(p)) return;

        if (ReferenceEquals(SelectedPreset, p)) SelectedPreset = null;

        StatusMessage = $"Deleted \"{p.Name}\"";
        RefreshFilterBoundsFromData();
        RecomputeSiblingLinks();
        PersistUserPresets();
        Refresh();
    }

    /// <summary>Removes every seeded sample preset in one action (see PrintPresetSample.IsSeeded) —
    /// never touches anything the user actually saved or imported.</summary>
    private void ClearSeedData()
    {
        var removed = AllPresets.Where(p => p.IsSeeded).ToList();
        foreach (var p in removed) AllPresets.Remove(p);

        if (SelectedPreset is { IsSeeded: true }) SelectedPreset = null;

        StatusMessage = $"Cleared {removed.Count} seed preset(s)";
        RefreshFilterBoundsFromData();
        RecomputeSiblingLinks();
        Refresh();
    }

    /// <summary>
    /// Captures the panel's ACTUAL current values — not placeholders — gated group-by-group by
    /// the Save-as-Preset checklist (see SaveFieldGroups): an unchecked group's fields are simply
    /// left null rather than populated with a value the user didn't ask to save. Shared by
    /// SaveNewPreset (new entry) and UpdatePreset (overwrites an existing entry's settings while
    /// keeping its Name/Folder/CreatedUtc/IsFavorite/LastPrintedUtc).
    /// </summary>
    private PrintPresetSample CapturePresetFromAdditive(
        string name, string folder, DateTime createdUtc, bool isFavorite, DateTime? lastPrintedUtc)
    {
        var a = _additive;

        var geometry  = IsGroupIncluded("Geometry & layers");
        var slicing   = IsGroupIncluded("Slicing mode & method");
        var effector  = IsGroupIncluded("Live effector");
        var pattern   = IsGroupIncluded("Pattern & texture");
        var xbracing  = IsGroupIncluded("X-Bracing wall");
        var wave      = IsGroupIncluded("Wave effect");
        var infill    = IsGroupIncluded("Infill");
        var overhang  = IsGroupIncluded("Overhang & orientation");
        var toolhead  = IsGroupIncluded("Toolhead orientation");
        var motion    = IsGroupIncluded("Motion & KUKA frame");
        var temps     = IsGroupIncluded("Temperatures");
        var krlTuning = IsGroupIncluded("KRL export tuning");
        var movement  = IsGroupIncluded("Movement (z-hop / wipe / resume)");
        var adaptSpd  = IsGroupIncluded("Adaptive layer speed");
        var krlPost   = IsGroupIncluded("KRL post-process");
        var adaptH    = IsGroupIncluded("Adaptive layer height");
        var stockMaps = IsGroupIncluded("Stock from Maps");
        var brim      = IsGroupIncluded("Brim");

        return new PrintPresetSample
        {
            Name           = name,
            Folder         = folder,
            CreatedUtc     = createdUtc,
            IsFavorite     = isFavorite,
            LastPrintedUtc = lastPrintedUtc,
            Material       = a.SelectedPreset?.Name ?? "",

            BeadWidth = geometry ? a.BeadWidth : null,
            LayerHeight = geometry ? a.LayerHeight : null,
            TiltAngle = geometry ? a.TiltAngle : null,
            TiltAngleX = geometry ? a.TiltAngleX : null,
            MultiPlanarAxisX = geometry ? a.MultiPlanarAxisX : null,
            MultiPlanarPlanes = geometry
                ? a.MultiPlanarPlanes.Select(r => new PresetPlaneRow { HeightPct = r.HeightPct, AngleDeg = r.AngleDeg }).ToList()
                : null,

            Method = slicing ? FormatSliceMethod(a.Method) : null,
            SeamMode = slicing ? a.SeamMode : null,
            SlicingMode = slicing ? a.SlicingMode : null,
            OrientationFollowPercent = slicing ? a.OrientationFollowPercent : null,
            OrientationMaxTiltDeg = slicing ? a.OrientationMaxTiltDeg : null,
            FirstLayerZeroTilt = slicing ? a.FirstLayerZeroTilt : null,
            LayerLeanPercent = slicing ? a.LayerLeanPercent : null,
            LayerLeanMaxTiltDeg = slicing ? a.LayerLeanMaxTiltDeg : null,
            CurvedBoundarySourceDisplay = slicing ? a.CurvedBoundarySourceDisplay : null,
            CurvedAutoDetectBandMm = slicing ? a.CurvedAutoDetectBandMm : null,
            CurvedEnableRegionSplit = slicing ? a.CurvedEnableRegionSplit : null,

            EffectorEnabled = effector ? a.EffectorEnabled : null,
            EffectorMode = effector ? a.EffectorMode : null,
            EffectorRange = effector ? a.EffectorRange : null,
            EffectorStrength = effector ? a.EffectorStrength : null,

            PatternType = pattern ? a.PatternType : null,
            PatternMapping = pattern ? a.PatternMapping : null,
            PatternWavelengthMm = pattern ? a.PatternWavelengthMm : null,
            PatternAmplitude = pattern ? a.PatternAmplitude : null,
            PatternFrequency = pattern ? a.PatternFrequency : null,
            PatternTwist = pattern ? a.PatternTwist : null,
            PatternOffset = pattern ? a.PatternOffset : null,
            PatternFadeIn = pattern ? a.PatternFadeIn : null,
            PatternFadeOut = pattern ? a.PatternFadeOut : null,

            XBracingEnabled = xbracing ? a.XBracingEnabled : null,
            XBracingProjectionType = xbracing ? a.XBracingProjectionType : null,
            XBracingShowHelper = xbracing ? a.XBracingShowHelper : null,
            XBracingPlaneTiltY = xbracing ? a.XBracingPlaneTiltY : null,
            XBracingPlaneTiltX = xbracing ? a.XBracingPlaneTiltX : null,
            XBracingCylinderDiameterMm = xbracing ? a.XBracingCylinderDiameterMm : null,
            XBracingCylinderFlipDirection = xbracing ? a.XBracingCylinderFlipDirection : null,
            XBracingDepthMm = xbracing ? a.XBracingDepthMm : null,
            XBracingDepthBottomMm = xbracing ? a.XBracingDepthBottomMm : null,
            XBracingDepthEaseBottom = xbracing ? a.XBracingDepthEaseBottom : null,
            XBracingDepthEaseTop = xbracing ? a.XBracingDepthEaseTop : null,
            XBracingSpanMm = xbracing ? a.XBracingSpanMm : null,
            XBracingAngleDeg = xbracing ? a.XBracingAngleDeg : null,
            XBracingExtendEdges = xbracing ? a.XBracingExtendEdges : null,

            WaveEffect = wave ? a.WaveEffect : null,
            WaveAmplitude = wave ? a.WaveAmplitude : null,
            WaveFrequencyMode = wave ? a.WaveFrequencyMode : null,
            WaveWavelength = wave ? a.WaveWavelength : null,
            WaveCycles = wave ? a.WaveCycles : null,
            WaveShape = wave ? a.WaveShape : null,
            WaveStagger = wave ? a.WaveStagger : null,
            WavePhaseMethodIndex = wave ? a.WavePhaseMethodIndex : null,
            WaveGradient = wave ? a.WaveGradient : null,
            WaveAmplitudeBottom = wave ? a.WaveAmplitudeBottom : null,
            WaveAmplitudeTop = wave ? a.WaveAmplitudeTop : null,
            WaveWavelengthBottom = wave ? a.WaveWavelengthBottom : null,
            WaveWavelengthTop = wave ? a.WaveWavelengthTop : null,
            WaveGradientCenter = wave ? a.WaveGradientCenter : null,
            WaveGradientCurve = wave ? a.WaveGradientCurve : null,

            InfillPattern = infill ? a.InfillPattern : null,
            InfillSpacingMm = infill ? a.InfillSpacingMm : null,
            InfillAngleDeg = infill ? a.InfillAngleDeg : null,
            LightningOverhangDeg = infill ? a.LightningOverhangDeg : null,
            LightningBranchSpacingMm = infill ? a.LightningBranchSpacingMm : null,
            LightningTipLoopRadiusMm = infill ? a.LightningTipLoopRadiusMm : null,
            LightningAffectInterior = infill ? a.LightningAffectInterior : null,
            LightningAffectExterior = infill ? a.LightningAffectExterior : null,
            LightningTargetSupportSelections = infill ? a.LightningTargetSupportSelections : null,
            LightningButtressBarMm = infill ? a.LightningButtressBarMm : null,

            OverhangOrientation = overhang ? a.OverhangOrientation : null,
            MaxOverhangTiltDeg = overhang ? a.MaxOverhangTiltDeg : null,
            ZigZagAllowSameLayerTravel = overhang ? a.ZigZagAllowSameLayerTravel : null,
            DisableContourOffset = overhang ? a.DisableContourOffset : null,

            ToolheadA = toolhead ? a.ToolheadA : null,
            ToolheadB = toolhead ? a.ToolheadB : null,
            ToolheadC = toolhead ? a.ToolheadC : null,

            PrintSpeed = motion ? a.PrintSpeed : null,
            TravelSpeed = motion ? a.TravelSpeed : null,
            ApoCvel = motion ? a.ApoCvel : null,
            E1MotionEnabled = motion ? a.E1MotionEnabled : null,
            E1YPlusMm = motion ? a.E1YPlusMm : null,
            E1YMinusMm = motion ? a.E1YMinusMm : null,
            SmoothRotation = motion ? a.SmoothRotation : null,
            SmoothRotationRadius = motion ? a.SmoothRotationRadius : null,
            SmoothRotationMaxRateDegPerMm = motion ? a.SmoothRotationMaxRateDegPerMm : null,
            OrientationLookAheadMm = motion ? a.OrientationLookAheadMm : null,
            OrientationSigmaMm = motion ? a.OrientationSigmaMm : null,

            Temperature1 = temps ? a.Temperature1 : null,
            Temperature2 = temps ? a.Temperature2 : null,
            Temperature3 = temps ? a.Temperature3 : null,

            TemperatureOffset = krlTuning ? a.TemperatureOffset : null,
            ExtrusionSpeedOffset = krlTuning ? a.ExtrusionSpeedOffset : null,
            DigitalStartStopEnabled = krlTuning ? a.DigitalStartStopEnabled : null,
            ExtrusionStartWaitSec = krlTuning ? a.ExtrusionStartWaitSec : null,
            ExtrusionResumeWaitSec = krlTuning ? a.ExtrusionResumeWaitSec : null,

            ZHopMm = movement ? a.ZHopMm : null,
            WipeModeDisplay = movement ? a.WipeModeDisplay : null,
            WipeLengthMm = movement ? a.WipeLengthMm : null,
            WipeRampMm = movement ? a.WipeRampMm : null,
            WipeSpeed = movement ? a.WipeSpeed : null,
            WipeSkipShortTravels = movement ? a.WipeSkipShortTravels : null,
            ResumeRampEnabled = movement ? a.ResumeRampEnabled : null,
            ResumeRampStartSpeed = movement ? a.ResumeRampStartSpeed : null,
            ResumeRampStartRpmPercent = movement ? a.ResumeRampStartRpmPercent : null,
            ResumeRampDistanceMm = movement ? a.ResumeRampDistanceMm : null,
            ResumeRampSteps = movement ? a.ResumeRampSteps : null,

            LayerSpeedAdaptEnabled = adaptSpd ? a.LayerSpeedAdaptEnabled : null,
            LayerSpeedBasisDisplay = adaptSpd ? a.LayerSpeedBasisDisplay : null,
            LayerSpeedMinMmS = adaptSpd ? a.LayerSpeedMinMmS : null,
            LayerSpeedMaxMmS = adaptSpd ? a.LayerSpeedMaxMmS : null,

            KrlHeaderText = krlPost ? a.KrlPostProcess.HeaderText : null,
            KrlFooterText = krlPost ? a.KrlPostProcess.FooterText : null,

            AdaptiveLayerHeight = adaptH ? a.AdaptiveLayerHeight : null,
            MinLayerHeight = adaptH ? a.MinLayerHeight : null,
            AdaptiveQuality = adaptH ? a.AdaptiveQuality : null,

            UseDisplacedStock = stockMaps ? a.UseDisplacedStock : null,
            StockAllowanceMm = stockMaps ? a.StockAllowanceMm : null,

            BrimEnabled = brim ? a.BrimEnabled : null,
            BrimLoops = brim ? a.BrimLoops : null,
        };
    }

    private void SaveNewPreset()
    {
        var name = string.IsNullOrWhiteSpace(SaveNameText) ? "Untitled Preset" : SaveNameText.Trim();
        var preset = CapturePresetFromAdditive(name, "Uncategorized", DateTime.UtcNow, isFavorite: false, lastPrintedUtc: null);
        AllPresets.Add(preset);

        SaveNameText     = "";
        IsSaveDialogOpen = false;
        RefreshFilterBoundsFromData();
        RecomputeSiblingLinks();
        PersistUserPresets();
        Refresh();
    }

    /// <summary>Overwrites an existing preset's settings with the panel's current values (same
    /// field-group gating as Save-as-Preset) — Name/Folder/CreatedUtc/IsFavorite/LastPrintedUtc
    /// carry over unchanged. Promotes a seeded sample to a real, persisted preset (see
    /// PrintPresetSample.IsSeeded / FromRecord), same as Rename/EnsureMasterDefaultsPreset's
    /// "edit it like any other" model.</summary>
    private void UpdatePreset(PrintPresetSample? p)
    {
        if (p is null) return;
        var updated = CapturePresetFromAdditive(p.Name, p.Folder, p.CreatedUtc, p.IsFavorite, p.LastPrintedUtc);
        if (!ReplacePreset(p, updated)) return;
        StatusMessage = $"Updated \"{updated.Name}\" from the current panel settings";
    }

    /// <summary>Swaps one preset object for another at the same index — the only way to change an
    /// otherwise-init-only PrintPresetSample (see Rename/UpdatePreset). Carries the selection over
    /// if the replaced preset was selected.</summary>
    private bool ReplacePreset(PrintPresetSample oldPreset, PrintPresetSample newPreset)
    {
        var index = AllPresets.IndexOf(oldPreset);
        if (index < 0) return false;

        AllPresets[index] = newPreset;
        if (ReferenceEquals(SelectedPreset, oldPreset)) SelectedPreset = newPreset;

        RefreshFilterBoundsFromData();
        RecomputeSiblingLinks();
        PersistUserPresets();
        Refresh();
        return true;
    }

    private void BeginRename(PrintPresetSample? p)
    {
        if (p is null) return;
        _renameTargetPreset = p;
        RenameNameText = p.Name;
        IsRenameDialogOpen = true;
    }

    private void CommitRename()
    {
        var target = _renameTargetPreset;
        var newName = RenameNameText.Trim();
        IsRenameDialogOpen = false;
        _renameTargetPreset = null;
        if (target is null || newName.Length == 0 || newName == target.Name) return;

        var record = ToRecord(target);
        record.Name = newName;
        if (!ReplacePreset(target, FromRecord(record))) return;
        StatusMessage = $"Renamed \"{target.Name}\" to \"{newName}\"";
    }

    private void CancelRename()
    {
        IsRenameDialogOpen = false;
        _renameTargetPreset = null;
    }

    /// <summary>Converts one PrintPresetSample to the persisted record shape.</summary>
    private static PrintPresetRecord ToRecord(PrintPresetSample p) => new()
    {
        Name = p.Name, Folder = p.Folder, CreatedUtc = p.CreatedUtc, LastPrintedUtc = p.LastPrintedUtc,
        IsFavorite = p.IsFavorite, Material = p.Material,

        BeadWidth = p.BeadWidth, LayerHeight = p.LayerHeight, TiltAngle = p.TiltAngle, TiltAngleX = p.TiltAngleX,
        MultiPlanarAxisX = p.MultiPlanarAxisX,
        MultiPlanarPlanes = p.MultiPlanarPlanes?.Select(r => new Core.IO.PresetPlaneRow { HeightPct = r.HeightPct, AngleDeg = r.AngleDeg }).ToList(),

        Method = p.Method, SeamMode = p.SeamMode, SlicingMode = p.SlicingMode,
        OrientationFollowPercent = p.OrientationFollowPercent, OrientationMaxTiltDeg = p.OrientationMaxTiltDeg,
        FirstLayerZeroTilt = p.FirstLayerZeroTilt, LayerLeanPercent = p.LayerLeanPercent,
        LayerLeanMaxTiltDeg = p.LayerLeanMaxTiltDeg, CurvedBoundarySourceDisplay = p.CurvedBoundarySourceDisplay,
        CurvedAutoDetectBandMm = p.CurvedAutoDetectBandMm, CurvedEnableRegionSplit = p.CurvedEnableRegionSplit,

        EffectorEnabled = p.EffectorEnabled, EffectorMode = p.EffectorMode,
        EffectorRange = p.EffectorRange, EffectorStrength = p.EffectorStrength,

        PatternType = p.PatternType, PatternMapping = p.PatternMapping, PatternWavelengthMm = p.PatternWavelengthMm,
        PatternAmplitude = p.PatternAmplitude, PatternFrequency = p.PatternFrequency, PatternTwist = p.PatternTwist,
        PatternOffset = p.PatternOffset, PatternFadeIn = p.PatternFadeIn, PatternFadeOut = p.PatternFadeOut,

        XBracingEnabled = p.XBracingEnabled, XBracingProjectionType = p.XBracingProjectionType,
        XBracingShowHelper = p.XBracingShowHelper, XBracingPlaneTiltY = p.XBracingPlaneTiltY,
        XBracingPlaneTiltX = p.XBracingPlaneTiltX, XBracingCylinderDiameterMm = p.XBracingCylinderDiameterMm,
        XBracingCylinderFlipDirection = p.XBracingCylinderFlipDirection, XBracingDepthMm = p.XBracingDepthMm,
        XBracingDepthBottomMm = p.XBracingDepthBottomMm, XBracingDepthEaseBottom = p.XBracingDepthEaseBottom,
        XBracingDepthEaseTop = p.XBracingDepthEaseTop, XBracingSpanMm = p.XBracingSpanMm,
        XBracingAngleDeg = p.XBracingAngleDeg, XBracingExtendEdges = p.XBracingExtendEdges,

        WaveEffect = p.WaveEffect, WaveAmplitude = p.WaveAmplitude, WaveFrequencyMode = p.WaveFrequencyMode,
        WaveWavelength = p.WaveWavelength, WaveCycles = p.WaveCycles, WaveShape = p.WaveShape,
        WaveStagger = p.WaveStagger, WavePhaseMethodIndex = p.WavePhaseMethodIndex, WaveGradient = p.WaveGradient,
        WaveAmplitudeBottom = p.WaveAmplitudeBottom, WaveAmplitudeTop = p.WaveAmplitudeTop,
        WaveWavelengthBottom = p.WaveWavelengthBottom, WaveWavelengthTop = p.WaveWavelengthTop,
        WaveGradientCenter = p.WaveGradientCenter, WaveGradientCurve = p.WaveGradientCurve,

        InfillPattern = p.InfillPattern, InfillSpacingMm = p.InfillSpacingMm, InfillAngleDeg = p.InfillAngleDeg,
        LightningOverhangDeg = p.LightningOverhangDeg, LightningBranchSpacingMm = p.LightningBranchSpacingMm,
        LightningTipLoopRadiusMm = p.LightningTipLoopRadiusMm, LightningAffectInterior = p.LightningAffectInterior,
        LightningAffectExterior = p.LightningAffectExterior, LightningTargetSupportSelections = p.LightningTargetSupportSelections,
        LightningButtressBarMm = p.LightningButtressBarMm,

        OverhangOrientation = p.OverhangOrientation, MaxOverhangTiltDeg = p.MaxOverhangTiltDeg,
        ZigZagAllowSameLayerTravel = p.ZigZagAllowSameLayerTravel, DisableContourOffset = p.DisableContourOffset,

        ToolheadA = p.ToolheadA, ToolheadB = p.ToolheadB, ToolheadC = p.ToolheadC,

        PrintSpeed = p.PrintSpeed, TravelSpeed = p.TravelSpeed, ApoCvel = p.ApoCvel,
        E1MotionEnabled = p.E1MotionEnabled, E1YPlusMm = p.E1YPlusMm, E1YMinusMm = p.E1YMinusMm,
        SmoothRotation = p.SmoothRotation, SmoothRotationRadius = p.SmoothRotationRadius,
        SmoothRotationMaxRateDegPerMm = p.SmoothRotationMaxRateDegPerMm,
        OrientationLookAheadMm = p.OrientationLookAheadMm, OrientationSigmaMm = p.OrientationSigmaMm,

        Temperature1 = p.Temperature1, Temperature2 = p.Temperature2, Temperature3 = p.Temperature3,

        TemperatureOffset = p.TemperatureOffset, ExtrusionSpeedOffset = p.ExtrusionSpeedOffset,
        DigitalStartStopEnabled = p.DigitalStartStopEnabled, ExtrusionStartWaitSec = p.ExtrusionStartWaitSec,
        ExtrusionResumeWaitSec = p.ExtrusionResumeWaitSec,

        ZHopMm = p.ZHopMm, WipeModeDisplay = p.WipeModeDisplay, WipeLengthMm = p.WipeLengthMm,
        WipeRampMm = p.WipeRampMm, WipeSpeed = p.WipeSpeed, WipeSkipShortTravels = p.WipeSkipShortTravels,
        ResumeRampEnabled = p.ResumeRampEnabled, ResumeRampStartSpeed = p.ResumeRampStartSpeed,
        ResumeRampStartRpmPercent = p.ResumeRampStartRpmPercent, ResumeRampDistanceMm = p.ResumeRampDistanceMm,
        ResumeRampSteps = p.ResumeRampSteps,

        LayerSpeedAdaptEnabled = p.LayerSpeedAdaptEnabled, LayerSpeedBasisDisplay = p.LayerSpeedBasisDisplay,
        LayerSpeedMinMmS = p.LayerSpeedMinMmS, LayerSpeedMaxMmS = p.LayerSpeedMaxMmS,

        KrlHeaderText = p.KrlHeaderText, KrlFooterText = p.KrlFooterText,

        AdaptiveLayerHeight = p.AdaptiveLayerHeight, MinLayerHeight = p.MinLayerHeight, AdaptiveQuality = p.AdaptiveQuality,

        UseDisplacedStock = p.UseDisplacedStock, StockAllowanceMm = p.StockAllowanceMm,

        BrimEnabled = p.BrimEnabled, BrimLoops = p.BrimLoops,
    };

    private static PrintPresetSample FromRecord(PrintPresetRecord r) => new()
    {
        Name = r.Name, Folder = r.Folder, CreatedUtc = r.CreatedUtc, LastPrintedUtc = r.LastPrintedUtc,
        IsFavorite = r.IsFavorite, IsSeeded = false, Material = r.Material,

        BeadWidth = r.BeadWidth, LayerHeight = r.LayerHeight, TiltAngle = r.TiltAngle, TiltAngleX = r.TiltAngleX,
        MultiPlanarAxisX = r.MultiPlanarAxisX,
        MultiPlanarPlanes = r.MultiPlanarPlanes?.Select(row => new PresetPlaneRow { HeightPct = row.HeightPct, AngleDeg = row.AngleDeg }).ToList(),

        Method = r.Method, SeamMode = r.SeamMode, SlicingMode = r.SlicingMode,
        OrientationFollowPercent = r.OrientationFollowPercent, OrientationMaxTiltDeg = r.OrientationMaxTiltDeg,
        FirstLayerZeroTilt = r.FirstLayerZeroTilt, LayerLeanPercent = r.LayerLeanPercent,
        LayerLeanMaxTiltDeg = r.LayerLeanMaxTiltDeg, CurvedBoundarySourceDisplay = r.CurvedBoundarySourceDisplay,
        CurvedAutoDetectBandMm = r.CurvedAutoDetectBandMm, CurvedEnableRegionSplit = r.CurvedEnableRegionSplit,

        EffectorEnabled = r.EffectorEnabled, EffectorMode = r.EffectorMode,
        EffectorRange = r.EffectorRange, EffectorStrength = r.EffectorStrength,

        PatternType = r.PatternType, PatternMapping = r.PatternMapping, PatternWavelengthMm = r.PatternWavelengthMm,
        PatternAmplitude = r.PatternAmplitude, PatternFrequency = r.PatternFrequency, PatternTwist = r.PatternTwist,
        PatternOffset = r.PatternOffset, PatternFadeIn = r.PatternFadeIn, PatternFadeOut = r.PatternFadeOut,

        XBracingEnabled = r.XBracingEnabled, XBracingProjectionType = r.XBracingProjectionType,
        XBracingShowHelper = r.XBracingShowHelper, XBracingPlaneTiltY = r.XBracingPlaneTiltY,
        XBracingPlaneTiltX = r.XBracingPlaneTiltX, XBracingCylinderDiameterMm = r.XBracingCylinderDiameterMm,
        XBracingCylinderFlipDirection = r.XBracingCylinderFlipDirection, XBracingDepthMm = r.XBracingDepthMm,
        XBracingDepthBottomMm = r.XBracingDepthBottomMm, XBracingDepthEaseBottom = r.XBracingDepthEaseBottom,
        XBracingDepthEaseTop = r.XBracingDepthEaseTop, XBracingSpanMm = r.XBracingSpanMm,
        XBracingAngleDeg = r.XBracingAngleDeg, XBracingExtendEdges = r.XBracingExtendEdges,

        WaveEffect = r.WaveEffect, WaveAmplitude = r.WaveAmplitude, WaveFrequencyMode = r.WaveFrequencyMode,
        WaveWavelength = r.WaveWavelength, WaveCycles = r.WaveCycles, WaveShape = r.WaveShape,
        WaveStagger = r.WaveStagger, WavePhaseMethodIndex = r.WavePhaseMethodIndex, WaveGradient = r.WaveGradient,
        WaveAmplitudeBottom = r.WaveAmplitudeBottom, WaveAmplitudeTop = r.WaveAmplitudeTop,
        WaveWavelengthBottom = r.WaveWavelengthBottom, WaveWavelengthTop = r.WaveWavelengthTop,
        WaveGradientCenter = r.WaveGradientCenter, WaveGradientCurve = r.WaveGradientCurve,

        InfillPattern = r.InfillPattern, InfillSpacingMm = r.InfillSpacingMm, InfillAngleDeg = r.InfillAngleDeg,
        LightningOverhangDeg = r.LightningOverhangDeg, LightningBranchSpacingMm = r.LightningBranchSpacingMm,
        LightningTipLoopRadiusMm = r.LightningTipLoopRadiusMm, LightningAffectInterior = r.LightningAffectInterior,
        LightningAffectExterior = r.LightningAffectExterior, LightningTargetSupportSelections = r.LightningTargetSupportSelections,
        LightningButtressBarMm = r.LightningButtressBarMm,

        OverhangOrientation = r.OverhangOrientation, MaxOverhangTiltDeg = r.MaxOverhangTiltDeg,
        ZigZagAllowSameLayerTravel = r.ZigZagAllowSameLayerTravel, DisableContourOffset = r.DisableContourOffset,

        ToolheadA = r.ToolheadA, ToolheadB = r.ToolheadB, ToolheadC = r.ToolheadC,

        PrintSpeed = r.PrintSpeed, TravelSpeed = r.TravelSpeed, ApoCvel = r.ApoCvel,
        E1MotionEnabled = r.E1MotionEnabled, E1YPlusMm = r.E1YPlusMm, E1YMinusMm = r.E1YMinusMm,
        SmoothRotation = r.SmoothRotation, SmoothRotationRadius = r.SmoothRotationRadius,
        SmoothRotationMaxRateDegPerMm = r.SmoothRotationMaxRateDegPerMm,
        OrientationLookAheadMm = r.OrientationLookAheadMm, OrientationSigmaMm = r.OrientationSigmaMm,

        Temperature1 = r.Temperature1, Temperature2 = r.Temperature2, Temperature3 = r.Temperature3,

        TemperatureOffset = r.TemperatureOffset, ExtrusionSpeedOffset = r.ExtrusionSpeedOffset,
        DigitalStartStopEnabled = r.DigitalStartStopEnabled, ExtrusionStartWaitSec = r.ExtrusionStartWaitSec,
        ExtrusionResumeWaitSec = r.ExtrusionResumeWaitSec,

        ZHopMm = r.ZHopMm, WipeModeDisplay = r.WipeModeDisplay, WipeLengthMm = r.WipeLengthMm,
        WipeRampMm = r.WipeRampMm, WipeSpeed = r.WipeSpeed, WipeSkipShortTravels = r.WipeSkipShortTravels,
        ResumeRampEnabled = r.ResumeRampEnabled, ResumeRampStartSpeed = r.ResumeRampStartSpeed,
        ResumeRampStartRpmPercent = r.ResumeRampStartRpmPercent, ResumeRampDistanceMm = r.ResumeRampDistanceMm,
        ResumeRampSteps = r.ResumeRampSteps,

        LayerSpeedAdaptEnabled = r.LayerSpeedAdaptEnabled, LayerSpeedBasisDisplay = r.LayerSpeedBasisDisplay,
        LayerSpeedMinMmS = r.LayerSpeedMinMmS, LayerSpeedMaxMmS = r.LayerSpeedMaxMmS,

        KrlHeaderText = r.KrlHeaderText, KrlFooterText = r.KrlFooterText,

        AdaptiveLayerHeight = r.AdaptiveLayerHeight, MinLayerHeight = r.MinLayerHeight, AdaptiveQuality = r.AdaptiveQuality,

        UseDisplacedStock = r.UseDisplacedStock, StockAllowanceMm = r.StockAllowanceMm,

        BrimEnabled = r.BrimEnabled, BrimLoops = r.BrimLoops,
    };

    /// <summary>
    /// Loads presets saved on a previous run from <c>%AppData%\MassiveSlicer\presets.json</c> —
    /// this is what makes Save actually survive closing the app, instead of living only until
    /// the app closes. Called once at startup, after the seed data.
    /// </summary>
    private void LoadPersistedPresets()
    {
        foreach (var record in PrintPresetsLoader.Load())
            AllPresets.Add(FromRecord(record));
    }

    /// <summary>
    /// Writes every NON-seeded preset to disk — call after anything that adds/edits a real
    /// (Save'd or Imported) preset. Seed/sample presets are never written here; only what the
    /// user actually made lives on disk (see PrintPresetSample.IsSeeded).
    /// </summary>
    private void PersistUserPresets()
        => PrintPresetsLoader.Save(AllPresets.Where(p => !p.IsSeeded).Select(ToRecord));

    /// <summary>
    /// Recomputes every filter's dataset bounds/options from the live preset list — call after
    /// AllPresets changes so filters never offer a min/max/option the data doesn't actually have
    /// (an untouched filter tracks the new bounds; an actively-narrowed one keeps the user's choice).
    /// </summary>
    private void RefreshFilterBoundsFromData()
    {
        foreach (var f in ChoiceFilters) f.RefreshOptions(AllPresets.Select(f.Selector));
    }

    /// <summary>Recomputes each preset's sibling list from the current fingerprints — call after AllPresets changes.</summary>
    private void RecomputeSiblingLinks()
    {
        var byFingerprint = AllPresets.GroupBy(p => p.Fingerprint).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var p in AllPresets)
            p.SiblingNames = byFingerprint[p.Fingerprint].Where(o => !ReferenceEquals(o, p)).Select(o => o.Name).ToList();
    }

    /// <summary>Shares one preset as a standalone file — the same shape as the persisted-library
    /// record, so a file exported here can also be dropped straight into presets.json by hand if needed.</summary>
    public string ExportSelectedToJson()
    {
        if (SelectedPreset is null) return "";
        return System.Text.Json.JsonSerializer.Serialize(ToRecord(SelectedPreset),
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
    }

    /// <summary>
    /// Imports a shared preset file and immediately selects + applies it — the point of sharing
    /// a preset is to use it, not to have to go find it in the list afterward. Tolerant of
    /// missing fields (defaults fill in) so a hand-edited or partial file doesn't just fail.
    /// Deserializes through the same PrintPresetRecord shape a persisted/exported file uses, so a
    /// full-featured shared preset round-trips every field it carries, not just the original core set.
    /// </summary>
    public void ImportPresetFromJson(string json)
    {
        var record = System.Text.Json.JsonSerializer.Deserialize<PrintPresetRecord>(json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        if (record is null) throw new InvalidOperationException("Empty or invalid preset file");
        if (string.IsNullOrWhiteSpace(record.Name)) record.Name = "Imported Preset";
        record.CreatedUtc = DateTime.UtcNow;
        record.LastPrintedUtc = null;
        if (record.Folder == "Uncategorized" || string.IsNullOrWhiteSpace(record.Folder)) record.Folder = "Imported";

        var imported = FromRecord(record);
        AllPresets.Add(imported);

        RefreshFilterBoundsFromData();
        RecomputeSiblingLinks();
        PersistUserPresets();

        SelectedPreset = imported;
        ApplyPresetToAdditive(imported);
        StatusMessage = $"Imported and applied \"{imported.Name}\"";

        Refresh();
    }

    /// <summary>
    /// Normalizes for search: lowercase + strip everything but letters/digits. This makes matching
    /// liberal about PUNCTUATION/FORMATTING ("zigzag" finds "Zig-zag", "6.5" finds "6.5mm") without
    /// being liberal about actual CHARACTERS — no fuzzy/typo tolerance, so "HHN" still can't match
    /// "HCN". Those are different kinds of "liberal" and only the first one is safe here: fuzzy
    /// matching on short project-code-like names is exactly how you'd load the wrong project's
    /// preset by accident.
    /// </summary>
    private static string NormalizeForSearch(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    private void Refresh()
    {
        IEnumerable<PrintPresetSample> query = AllPresets;

        // Every committed tag AND's together, plus whatever's still being typed (not yet pinned
        // with Enter) — "zigzag" then "7200" narrows to presets matching BOTH, not either.
        var terms = SearchTags.ToList();
        if (!string.IsNullOrWhiteSpace(SearchText)) terms.Add(SearchText.Trim());

        foreach (var term in terms)
        {
            var needle = NormalizeForSearch(term);
            query = query.Where(p => p.SearchableTokens().Any(
                t => NormalizeForSearch(t).Contains(needle, StringComparison.Ordinal)));
        }

        foreach (var f in ChoiceFilters) query = query.Where(f.Matches);
        if (FavoritesOnly) query = query.Where(p => p.IsFavorite);

        query = SortMode switch
        {
            PresetSortMode.LastPrinted => query.OrderByDescending(p => p.LastPrintedUtc ?? DateTime.MinValue),
            PresetSortMode.DateCreated => query.OrderByDescending(p => p.CreatedUtc),
            _                          => query.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
        };

        var results = query.ToList();

        FilteredPresets.Clear();
        foreach (var p in results) FilteredPresets.Add(p);

        GroupedPresets.Clear();
        if (GroupingMode == PresetGroupingMode.None) return;

        var groups = GroupingMode == PresetGroupingMode.ByMethod
            ? results.GroupBy(p => p.Method ?? "Unknown")
            : results.GroupBy(p => p.Folder);

        foreach (var g in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            GroupedPresets.Add(new PresetGroupViewModel { GroupName = g.Key, Items = g.ToList() });
    }
}
