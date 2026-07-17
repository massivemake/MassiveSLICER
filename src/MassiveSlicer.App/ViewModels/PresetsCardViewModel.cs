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
/// (see PresetsCardViewModel.ApplyToAdditive) pushes these directly onto the real settings
/// object. Persistence is still comp-only (in-memory list, Import/Export use an ad-hoc JSON
/// shape, not the real schema/file format), but Apply itself is real.
/// </summary>
public sealed class PrintPresetSample
{
    public required string Name { get; init; }
    public double BeadWidth { get; init; }
    public double LayerHeight { get; init; }
    public double PrintSpeed { get; init; }
    public string Method { get; init; } = "Planar";
    public string PatternType { get; init; } = "Smooth";
    public string SeamMode { get; init; } = "Normal";
    public bool XBracingEnabled { get; init; }
    public string Material { get; init; } = "ABS";

    /// <summary>
    /// Category tag a preset belongs to — independent of its name, used for the "By Folder"
    /// grouping view. Comp-only: not yet a real user-assignable field (see discussion on
    /// naming/duplication before deciding what this should actually mean).
    /// </summary>
    public string Folder { get; init; } = "Uncategorized";

    public DateTime CreatedUtc { get; init; }

    /// <summary>User-marked favorite — settable directly (see PresetsCardViewModel.ToggleFavorite),
    /// no dedicated command object needed since flipping a bool doesn't need CanExecute logic.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// True only for the ~80 generated sample presets seeded at first launch — never true for
    /// anything the user actually saves or imports. Lets the whole seed batch be cleared in one
    /// action once real presets exist, without touching real user data (see
    /// PresetsCardViewModel.ClearSeedData).
    /// </summary>
    public bool IsSeeded { get; init; }

    /// <summary>
    /// Settable (not init-only) — but deliberately NOT stamped by the "Load" action (see
    /// PresetsCardViewModel.LoadSelected). Named "Printed" not "Used": a preset only counts once
    /// it's actually driven a real print, not merely been selected in the panel. This comp has no
    /// print pipeline to hook that to yet, so it only reflects seeded sample data.
    /// </summary>
    public DateTime? LastPrintedUtc { get; set; }

    /// <summary>
    /// Coarse-rounded settings signature used to detect "this is secretly the same preset as that
    /// one" regardless of name — the mechanism from the naming/duplication discussion. Rounding
    /// stands in for real per-field step tolerances (bead width only ever moves in ~0.5mm steps,
    /// etc.) which still need to be defined properly per field.
    /// </summary>
    public string Fingerprint => string.Join("|",
        Math.Round(BeadWidth, 1), Math.Round(LayerHeight, 1), Math.Round(PrintSpeed, 0),
        Method, PatternType, SeamMode, XBracingEnabled, Material);

    /// <summary>Other presets (by name) sharing this exact fingerprint — recomputed by
    /// PresetsCardViewModel.RecomputeSiblingLinks whenever the preset list changes.</summary>
    public IReadOnlyList<string> SiblingNames { get; set; } = Array.Empty<string>();

    public int SiblingCount => SiblingNames.Count;
    public bool HasSiblings => SiblingCount > 0;
    public string SiblingTooltip => HasSiblings
        ? $"Same settings also saved as: {string.Join(", ", SiblingNames)}"
        : "";

    /// <summary>Small key-value badge shown at the left of the list row.</summary>
    public string SummaryBadge => $"{BeadWidth:0.#}mm";

    public string LastPrintedDisplay => LastPrintedUtc is { } t ? t.ToString("MMM d") : "never";

    /// <summary>
    /// Full field-by-field readout for the "What" info popup — the name alone isn't enough to
    /// know what's actually in a preset before applying it.
    /// </summary>
    public string InfoLines => string.Join("\n",
        $"Folder: {Folder}",
        $"Material: {Material}",
        $"Method: {Method}",
        $"Pattern: {PatternType}",
        $"Seam mode: {SeamMode}",
        $"X-Bracing: {(XBracingEnabled ? "On" : "Off")}",
        $"Bead width: {BeadWidth:0.##} mm",
        $"Layer height: {LayerHeight:0.##} mm",
        $"Print speed: {PrintSpeed:0.#} mm/s",
        $"Created: {CreatedUtc:MMM d, yyyy}",
        $"Last printed: {LastPrintedDisplay}",
        HasSiblings ? $"Also saved as: {string.Join(", ", SiblingNames)}" : "No known duplicates");

    /// <summary>Every value a search should be able to match against, name included.</summary>
    public IEnumerable<string> SearchableTokens()
    {
        yield return Name;
        yield return BeadWidth.ToString("0.###");
        yield return LayerHeight.ToString("0.###");
        yield return PrintSpeed.ToString("0.###");
        yield return Method;
        yield return PatternType;
        yield return SeamMode;
        yield return Folder;
        yield return Material;
        if (XBracingEnabled) yield return "X-Bracing";
        if (XBracingEnabled) yield return "Bracing";
    }
}

public enum PresetSortMode { Name, LastPrinted, DateCreated }

public enum PresetGroupingMode { None, ByMethod, ByFolder }

public sealed class PresetGroupViewModel
{
    public required string GroupName { get; init; }
    public required IReadOnlyList<PrintPresetSample> Items { get; init; }
}

/// <summary>One row in the Save-as-Preset field-group checklist (comp — not wired to real saving).</summary>
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

/// <summary>
/// A live min/max range filter over one numeric field (e.g. bead width). Low/High are plain
/// numeric fields (always show the true bounds, never ambiguous), PLUS draggable handles
/// rendered directly on the track. (A third "converge to one value" field was tried and dropped —
/// parked for separate dev later, not part of this control.)
///
/// Interaction model (see RightPanelView.axaml.cs's OnRangeTrack* handlers): the two handles are
/// NOT independent hit-test elements (that was tried twice and failed both times — two overlaid
/// Sliders let the top one's track swallow every click; two separate Avalonia Thumbs still let
/// whichever is topmost in z-order win EVERY hit-test once the handles are coincident, which is a
/// permanent lock — the "stuck together forever" bug, since the losing handle can then never be
/// grabbed again and the winning handle is itself clamped against the other, so neither can move).
/// Instead, a single parent Canvas captures the pointer and decides which bound (Lower/Upper) a
/// gesture controls: by PROXIMITY when the handles are separated (<see cref="IsLowerNearer"/>),
/// or — when they're exactly coincident, where proximity is a tie — by the DIRECTION of the first
/// subsequent movement (<see cref="DecideActiveLowerBound"/>): moving right grabs Upper, left
/// grabs Lower. A fixed tie-break (e.g. "ties always go to Lower") cannot work here: Lower can
/// never numerically exceed Upper, so if ties always resolved to Lower, a coincident pair could
/// only ever be pulled apart in one direction. Direction-deferred resolution escapes both ways.
/// The chosen bound is locked for the rest of that one gesture (see RangeDragState in the view's
/// code-behind) so a single continuous drag never flips targets mid-motion.
///
/// Dataset bounds recompute as presets are added (see <see cref="RecalculateBounds"/>) so the
/// fields never offer a min/max the loaded data doesn't actually contain, and fall back to real
/// slicer clamp ranges (not 0-0) when the list is empty.
/// </summary>
public sealed class NumericRangeFilterViewModel : ViewModelBase
{
    /// <summary>Fixed pixel width the track renders at — keeps value&lt;-&gt;pixel math simple for a comp.</summary>
    public const double TrackWidthPx = 220.0;

    public required string FieldName { get; init; }
    public required Func<PrintPresetSample, double> Selector { get; init; }
    public double DatasetMin { get; internal set; }
    public double DatasetMax { get; internal set; }

    // Default to +/-infinity-ish bounds (rather than 0) so whichever of LowerValue/UpperValue
    // gets set first during construction never clamps against the other's un-set default —
    // that chicken-and-egg ordering previously threw (DatasetMin > 0 > un-set UpperValue).
    private double _lowerValue = double.MinValue;
    public double LowerValue
    {
        get => _lowerValue;
        set
        {
            if (!SetField(ref _lowerValue, Math.Clamp(value, DatasetMin, Math.Min(_upperValue, DatasetMax)))) return;
            OnRangeChanged();
        }
    }

    private double _upperValue = double.MaxValue;
    public double UpperValue
    {
        get => _upperValue;
        set
        {
            if (!SetField(ref _upperValue, Math.Clamp(value, Math.Max(_lowerValue, DatasetMin), DatasetMax))) return;
            OnRangeChanged();
        }
    }

    public bool IsActive => LowerValue > DatasetMin || UpperValue < DatasetMax;

    /// <summary>Handle X positions (pixels) — a clean 1:1 value<->pixel mapping. The two handles
    /// can render at the identical position without any cosmetic offset hack, since interaction
    /// no longer depends on hit-testing them individually — see the class doc comment.</summary>
    public double LowerThumbX { get; private set; }
    public double UpperThumbX { get; private set; }
    public double FillX { get; private set; }
    public double FillWidth { get; private set; }

    private void OnRangeChanged()
    {
        OnPropertyChanged(nameof(IsActive));
        RecomputeFill();
    }

    private void RecomputeFill()
    {
        var span = DatasetMax - DatasetMin;
        double Fraction(double v) => span > 0 ? (v - DatasetMin) / span : 0;
        LowerThumbX = Fraction(LowerValue) * TrackWidthPx;
        UpperThumbX = Fraction(UpperValue) * TrackWidthPx;
        FillX       = LowerThumbX;
        FillWidth   = Math.Max(0, UpperThumbX - LowerThumbX);
        OnPropertyChanged(nameof(LowerThumbX));
        OnPropertyChanged(nameof(UpperThumbX));
        OnPropertyChanged(nameof(FillX));
        OnPropertyChanged(nameof(FillWidth));
    }

    /// <summary>True when the two handles render at (essentially) the identical pixel position —
    /// proximity-based picking is a tie at this point, so callers should defer to drag direction
    /// instead (see <see cref="DecideActiveLowerBound"/>).</summary>
    public bool HandlesCoincident => Math.Abs(LowerThumbX - UpperThumbX) < 0.5;

    /// <summary>Which handle is nearer a given track-relative pixel X. Only meaningful when the
    /// handles are separated — see <see cref="HandlesCoincident"/>.</summary>
    public bool IsLowerNearer(double trackX) => Math.Abs(trackX - LowerThumbX) <= Math.Abs(trackX - UpperThumbX);

    /// <summary>
    /// Decides which bound a drag gesture should control. Pass the press-down X as both
    /// parameters on the initial press (there's no movement yet); pass the original press X and
    /// the pointer's current X on each subsequent move. Returns true for Lower, false for Upper,
    /// or null only while the handles are coincident AND the pointer hasn't moved from the press
    /// point yet (still undecided — ask again on the next move).
    /// </summary>
    public bool? DecideActiveLowerBound(double pressX, double currentX)
    {
        if (!HandlesCoincident) return IsLowerNearer(pressX);
        if (currentX > pressX) return false;
        if (currentX < pressX) return true;
        return null;
    }

    /// <summary>Sets a bound directly from an absolute track-relative pixel X (not a delta) — the
    /// position of the pointer at that pixel IS the value; there's nothing to accumulate, so a
    /// missed or out-of-order event can't cause drift.</summary>
    public void SetFromTrackX(bool isLowerBound, double trackX)
    {
        var frac  = Math.Clamp(trackX / TrackWidthPx, 0, 1);
        var value = DatasetMin + frac * (DatasetMax - DatasetMin);
        if (isLowerBound) LowerValue = value; else UpperValue = value;
    }

    public bool Matches(PrintPresetSample p)
    {
        var v = Selector(p);
        return v >= LowerValue && v <= UpperValue;
    }

    public void Reset()
    {
        LowerValue = DatasetMin;
        UpperValue = DatasetMax;
    }

    /// <summary>
    /// Recomputes the dataset min/max from the live preset list (called whenever a preset is
    /// added) so the filter never offers a bound the data doesn't contain. A filter the user
    /// hasn't touched (still pinned to the old full bounds) tracks the new bounds automatically;
    /// a filter the user has actively narrowed keeps their chosen values untouched.
    /// </summary>
    public void RecalculateBounds(IEnumerable<double> allValues)
    {
        var values = allValues.ToList();
        if (values.Count == 0) return;

        var wasAtMin = _lowerValue <= DatasetMin;
        var wasAtMax = _upperValue >= DatasetMax;

        DatasetMin = values.Min();
        DatasetMax = values.Max();

        if (wasAtMin) _lowerValue = DatasetMin;
        if (wasAtMax) _upperValue = DatasetMax;

        OnRangeChanged();
    }
}

/// <summary>A single-choice filter over a non-numeric field (e.g. method, pattern, seam mode).</summary>
public sealed class ChoiceFilterViewModel : ViewModelBase
{
    public const string AnyOption = "(Any)";

    public required string FieldName { get; init; }
    public required Func<PrintPresetSample, string> Selector { get; init; }
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
    /// values become choosable without ever invalidating the currently-selected option.</summary>
    public void RefreshOptions(IEnumerable<string> allValues)
    {
        foreach (var v in allValues.Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            if (!Options.Contains(v)) Options.Add(v);
    }
}

/// <summary>
/// Drives the "0 PRESETS" step-card: search/sort/filter/grouping over an in-memory preset list,
/// select + Apply (real — pushes onto the given <see cref="AdditiveSettingsViewModel"/>),
/// favorites, import/export-to-file, fingerprint-based sibling badges, and a Save-as-Preset
/// dialog with the field-group checklist. Persistence is still comp-only (in-memory list, no
/// file-backed schema yet) — Apply itself is real.
/// </summary>
public sealed class PresetsCardViewModel : ViewModelBase
{
    public ObservableCollection<PrintPresetSample> AllPresets { get; }
    public ObservableCollection<PrintPresetSample> FilteredPresets { get; } = new();
    public ObservableCollection<PresetGroupViewModel> GroupedPresets { get; } = new();
    public ObservableCollection<PresetFieldGroupOption> SaveFieldGroups { get; }
    public ObservableCollection<NumericRangeFilterViewModel> NumericFilters { get; } = new();
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

    private readonly AdditiveSettingsViewModel _additive;

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

        SaveFieldGroups = new ObservableCollection<PresetFieldGroupOption>(
            new[]
            {
                "Geometry & layers", "Slicing mode & method", "Live effector", "Pattern & texture",
                "X-Bracing wall", "Wave effect", "Infill", "Overhang & orientation",
                "Toolhead orientation", "Motion & KUKA frame", "Temperatures",
                "KRL export tuning", "Movement (z-hop / wipe / resume)", "Adaptive layer speed",
                "KRL post-process",
            }.Select(name => new PresetFieldGroupOption { Name = name }));

        NumericFilters.Add(MakeNumericFilter("Bead width (mm)", p => p.BeadWidth, fallbackMin: 1, fallbackMax: 100));
        NumericFilters.Add(MakeNumericFilter("Layer height (mm)", p => p.LayerHeight, fallbackMin: 0.5, fallbackMax: 100));
        NumericFilters.Add(MakeNumericFilter("Print speed (mm/s)", p => p.PrintSpeed, fallbackMin: 1, fallbackMax: 2000));
        foreach (var f in NumericFilters) f.PropertyChanged += (_, _) => Refresh();

        ChoiceFilters.Add(MakeChoiceFilter("Material", p => p.Material));
        ChoiceFilters.Add(MakeChoiceFilter("Method", p => p.Method));
        ChoiceFilters.Add(MakeChoiceFilter("Pattern", p => p.PatternType));
        ChoiceFilters.Add(MakeChoiceFilter("Seam mode", p => p.SeamMode));
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

        RecomputeSiblingLinks();
        Refresh();
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

    private NumericRangeFilterViewModel MakeNumericFilter(
        string fieldName, Func<PrintPresetSample, double> selector, double fallbackMin, double fallbackMax)
    {
        // Empty dataset (e.g. a brand-new library with nothing saved yet) falls back to the real
        // clamp range of the matching AdditiveSettingsViewModel property, not a degenerate 0-0 —
        // there's nothing to search for either way once presets exist, so this only matters for
        // that one edge case.
        var min = AllPresets.Count == 0 ? fallbackMin : AllPresets.Min(selector);
        var max = AllPresets.Count == 0 ? fallbackMax : AllPresets.Max(selector);
        var filter = new NumericRangeFilterViewModel
        {
            FieldName  = fieldName,
            Selector   = selector,
            DatasetMin = min,
            DatasetMax = max,
        };
        filter.LowerValue = min;
        filter.UpperValue = max;
        return filter;
    }

    private ChoiceFilterViewModel MakeChoiceFilter(string fieldName, Func<PrintPresetSample, string> selector)
    {
        var filter = new ChoiceFilterViewModel { FieldName = fieldName, Selector = selector };
        filter.Options.Add(ChoiceFilterViewModel.AnyOption);
        foreach (var v in AllPresets.Select(selector).Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            filter.Options.Add(v);
        return filter;
    }

    /// <summary>Only flips checkbox state — never touches the underlying preset values.</summary>
    private void SetAllFieldGroupsIncluded(bool included)
    {
        foreach (var g in SaveFieldGroups) g.IsIncluded = included;
    }

    private void ResetFilters()
    {
        foreach (var f in NumericFilters) f.Reset();
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

    private void ApplyPresetToAdditive(PrintPresetSample p)
    {
        _additive.BeadWidth       = p.BeadWidth;
        _additive.LayerHeight     = p.LayerHeight;
        _additive.PrintSpeed      = p.PrintSpeed;
        _additive.SeamMode        = p.SeamMode;
        _additive.XBracingEnabled = p.XBracingEnabled;
        _additive.PatternType     = p.PatternType;
        _additive.Method          = ParseSliceMethod(p.Method);
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
    /// Captures the panel's ACTUAL current values — not placeholders. (An earlier version of
    /// this hardcoded defaults regardless of what was really dialed in, which is why applying a
    /// saved preset looked like it "did nothing": the preset never held real values to begin
    /// with. Fixed by reading straight from <see cref="_additive"/>.)
    /// </summary>
    private void SaveNewPreset()
    {
        var name = string.IsNullOrWhiteSpace(SaveNameText) ? "Untitled Preset" : SaveNameText.Trim();
        AllPresets.Add(new PrintPresetSample
        {
            Name           = name,
            BeadWidth      = _additive.BeadWidth,
            LayerHeight    = _additive.LayerHeight,
            PrintSpeed     = _additive.PrintSpeed,
            Method         = FormatSliceMethod(_additive.Method),
            PatternType    = _additive.PatternType,
            SeamMode       = _additive.SeamMode,
            XBracingEnabled= _additive.XBracingEnabled,
            Folder         = "Uncategorized",
            CreatedUtc     = DateTime.UtcNow,
            LastPrintedUtc = null,
        });

        SaveNameText     = "";
        IsSaveDialogOpen = false;
        RefreshFilterBoundsFromData();
        RecomputeSiblingLinks();
        PersistUserPresets();
        Refresh();
    }

    /// <summary>Converts one PrintPresetSample to the persisted record shape.</summary>
    private static PrintPresetRecord ToRecord(PrintPresetSample p) => new()
    {
        Name            = p.Name,
        BeadWidth       = p.BeadWidth,
        LayerHeight     = p.LayerHeight,
        PrintSpeed      = p.PrintSpeed,
        Method          = p.Method,
        PatternType     = p.PatternType,
        SeamMode        = p.SeamMode,
        XBracingEnabled = p.XBracingEnabled,
        Material        = p.Material,
        Folder          = p.Folder,
        CreatedUtc      = p.CreatedUtc,
        LastPrintedUtc  = p.LastPrintedUtc,
        IsFavorite      = p.IsFavorite,
    };

    private static PrintPresetSample FromRecord(PrintPresetRecord r) => new()
    {
        Name            = r.Name,
        BeadWidth       = r.BeadWidth,
        LayerHeight     = r.LayerHeight,
        PrintSpeed      = r.PrintSpeed,
        Method          = r.Method,
        PatternType     = r.PatternType,
        SeamMode        = r.SeamMode,
        XBracingEnabled = r.XBracingEnabled,
        Material        = r.Material,
        Folder          = r.Folder,
        CreatedUtc      = r.CreatedUtc,
        LastPrintedUtc  = r.LastPrintedUtc,
        IsFavorite      = r.IsFavorite,
        IsSeeded        = false,
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
        foreach (var f in NumericFilters) f.RecalculateBounds(AllPresets.Select(f.Selector));
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
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Imports a shared preset file and immediately selects + applies it — the point of sharing
    /// a preset is to use it, not to have to go find it in the list afterward. Tolerant of
    /// missing fields (defaults fill in) so a hand-edited or partial file doesn't just fail.
    /// </summary>
    public void ImportPresetFromJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        string Str(string key, string fallback) =>
            root.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() ?? fallback : fallback;
        double Num(string key, double fallback) =>
            root.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : fallback;
        bool Bool(string key, bool fallback) =>
            root.TryGetProperty(key, out var v) &&
            (v.ValueKind == System.Text.Json.JsonValueKind.True || v.ValueKind == System.Text.Json.JsonValueKind.False)
                ? v.GetBoolean() : fallback;

        var imported = new PrintPresetSample
        {
            Name            = Str("name", "Imported Preset"),
            BeadWidth       = Num("beadWidth", 6.0),
            LayerHeight     = Num("layerHeight", 3.0),
            PrintSpeed      = Num("printSpeed", 100),
            Method          = Str("method", "Planar"),
            PatternType     = Str("patternType", "Smooth"),
            SeamMode        = Str("seamMode", "Normal"),
            XBracingEnabled = Bool("xBracingEnabled", false),
            Material        = Str("material", "ABS"),
            Folder          = Str("folder", "Imported"),
            CreatedUtc      = DateTime.UtcNow,
            LastPrintedUtc  = null,
        };
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

        foreach (var f in NumericFilters) query = query.Where(f.Matches);
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
            ? results.GroupBy(p => p.Method)
            : results.GroupBy(p => p.Folder);

        foreach (var g in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            GroupedPresets.Add(new PresetGroupViewModel { GroupName = g.Key, Items = g.ToList() });
    }
}
