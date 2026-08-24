using System.Collections.ObjectModel;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Tool-library dialog: browse / create / edit mill bits and their cutting-data presets.
/// </summary>
public sealed class MillBitLibraryViewModel : ViewModelBase
{
    private MillBitTool? _selectedTool;
    private MillBitCuttingPreset? _selectedPreset;
    private string _searchText = "";
    private string? _typeFilter = "All types";
    private int _editorTab; // 0 = Cutter, 1 = Holder, 2 = Cutting data

    public MillBitLibraryViewModel(IEnumerable<MillBitTool> seed)
    {
        // Commands first — SelectedTool/SelectedPreset setters call RaiseCanExecuteChanged.
        CreateToolCommand = new RelayCommand(CreateTool);
        DeleteToolCommand = new RelayCommand(DeleteSelected, () => SelectedTool is not null);
        DuplicateToolCommand = new RelayCommand(DuplicateSelected, () => SelectedTool is not null);
        AddPresetCommand = new RelayCommand(AddPreset, () => SelectedTool is not null);
        DeletePresetCommand = new RelayCommand(DeletePreset, () =>
            SelectedTool is not null && SelectedPreset is not null && SelectedTool.CuttingPresets.Count > 1);
        AddHolderSegmentCommand = new RelayCommand(AddHolderSegment, () => SelectedTool is not null);
        RemoveHolderSegmentCommand = new RelayCommand<object>(RemoveHolderSegment, _ => SelectedTool is not null);

        foreach (var t in seed.Select(CloneTool))
        {
            t.CuttingPresets ??= [];
            if (t.CuttingPresets.Count == 0)
                t.CuttingPresets.Add(new MillBitCuttingPreset());
            t.HolderSegments ??= [];
            Tools.Add(t);
        }

        SelectedTool = Tools.FirstOrDefault(t => t.IsDefaultSpindleBit)
                       ?? Tools.FirstOrDefault(t => t.Id == MillBitTool.DefaultSpindleBitId)
                       ?? Tools.FirstOrDefault();
    }

    public ObservableCollection<MillBitTool> Tools { get; } = [];

    public static IReadOnlyList<string> TypeFilterOptions { get; } =
        ["All types", "Ball end mill", "Flat end mill", "Bull nose", "Drill", "Other"];

    public static IReadOnlyList<MillBitType> TypeOptions { get; } =
        [MillBitType.BallEndMill, MillBitType.FlatEndMill, MillBitType.BullNose, MillBitType.Drill, MillBitType.Other];

    public static IReadOnlyList<SpindleDirection> DirectionOptions { get; } =
        [SpindleDirection.Clockwise, SpindleDirection.CounterClockwise];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value ?? "")) return;
            OnPropertyChanged(nameof(FilteredTools));
        }
    }

    public string? TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (!SetField(ref _typeFilter, value)) return;
            OnPropertyChanged(nameof(FilteredTools));
        }
    }

    public IEnumerable<MillBitTool> FilteredTools
    {
        get
        {
            IEnumerable<MillBitTool> q = Tools;
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var s = SearchText.Trim();
                q = q.Where(t => t.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                              || t.TypeDisplayName.Contains(s, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(TypeFilter) && TypeFilter != "All types")
                q = q.Where(t => t.TypeDisplayName == TypeFilter);
            return q.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }
    }

    public MillBitTool? SelectedTool
    {
        get => _selectedTool;
        set
        {
            if (!SetField(ref _selectedTool, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(EditorTitle));
            SelectedPreset = value?.CuttingPresets.FirstOrDefault();
            DeleteToolCommand.RaiseCanExecuteChanged();
            DuplicateToolCommand.RaiseCanExecuteChanged();
            AddPresetCommand.RaiseCanExecuteChanged();
            DeletePresetCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(SelectedToolType));
        }
    }

    public bool HasSelection => SelectedTool is not null;

    public string EditorTitle => SelectedTool?.Name ?? "Select a tool";

    /// <summary>Two-way bind for type combo without boxing issues.</summary>
    public MillBitType SelectedToolType
    {
        get => SelectedTool?.Type ?? MillBitType.BallEndMill;
        set
        {
            if (SelectedTool is null || SelectedTool.Type == value) return;
            SelectedTool.Type = value;
            if (value == MillBitType.BallEndMill)
                SelectedTool.CornerRadiusMm = SelectedTool.DiameterMm * 0.5;
            OnPropertyChanged(nameof(SelectedToolType));
            OnPropertyChanged(nameof(FilteredTools));
            TouchSelected();
        }
    }

    public MillBitCuttingPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetField(ref _selectedPreset, value)) return;
            DeletePresetCommand.RaiseCanExecuteChanged();
        }
    }

    public int EditorTab
    {
        get => _editorTab;
        set
        {
            if (!SetField(ref _editorTab, value)) return;
            OnPropertyChanged(nameof(IsCutterTab));
            OnPropertyChanged(nameof(IsHolderTab));
            OnPropertyChanged(nameof(IsCuttingTab));
        }
    }

    public bool IsCutterTab  => EditorTab == 0;
    public bool IsHolderTab  => EditorTab == 1;
    public bool IsCuttingTab => EditorTab == 2;

    public RelayCommand CreateToolCommand { get; }
    public RelayCommand DeleteToolCommand { get; }
    public RelayCommand DuplicateToolCommand { get; }
    public RelayCommand AddPresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand AddHolderSegmentCommand { get; }
    public RelayCommand<object> RemoveHolderSegmentCommand { get; }

    public RelayCommand ShowCutterTabCommand  => _showCutter  ??= new RelayCommand(() => EditorTab = 0);
    public RelayCommand ShowHolderTabCommand  => _showHolder  ??= new RelayCommand(() => EditorTab = 1);
    public RelayCommand ShowCuttingTabCommand => _showCutting ??= new RelayCommand(() => EditorTab = 2);
    RelayCommand? _showCutter, _showHolder, _showCutting;

    void AddHolderSegment()
    {
        if (SelectedTool is null) return;
        SelectedTool.HolderSegments.Add(new MillBitHolderSegment());
        TouchSelected();
        OnPropertyChanged(nameof(SelectedTool));
    }

    void RemoveHolderSegment(object? seg)
    {
        if (SelectedTool is null || seg is not MillBitHolderSegment s) return;
        SelectedTool.HolderSegments.Remove(s);
        TouchSelected();
        OnPropertyChanged(nameof(SelectedTool));
    }

    void CreateTool()
    {
        var t = new MillBitTool
        {
            Name = "New bit",
            Identifier = "New bit",
            Type = MillBitType.BallEndMill,
            DiameterMm = 6,
            ShaftDiameterMm = 6,
            CornerRadiusMm = 3,
            FluteCount = 2,
            HolderSegments = [new MillBitHolderSegment()],
            CuttingPresets = [new MillBitCuttingPreset { Name = "Default" }],
            ShowSpindleCylinder = true,
            CylinderLengthMm = 50,
        };
        Tools.Add(t);
        SelectedTool = t;
        OnPropertyChanged(nameof(FilteredTools));
    }

    void DeleteSelected()
    {
        if (SelectedTool is null) return;
        var idx = Tools.IndexOf(SelectedTool);
        Tools.Remove(SelectedTool);
        SelectedTool = Tools.Count == 0 ? null
            : Tools[Math.Clamp(idx, 0, Tools.Count - 1)];
        OnPropertyChanged(nameof(FilteredTools));
    }

    void DuplicateSelected()
    {
        if (SelectedTool is null) return;
        var copy = CloneTool(SelectedTool);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.ErpId = null;
        copy.Name = SelectedTool.Name + " (copy)";
        Tools.Add(copy);
        SelectedTool = copy;
        OnPropertyChanged(nameof(FilteredTools));
    }

    void AddPreset()
    {
        if (SelectedTool is null) return;
        var p = new MillBitCuttingPreset { Name = $"Preset {SelectedTool.CuttingPresets.Count + 1}" };
        SelectedTool.CuttingPresets.Add(p);
        SelectedPreset = p;
        TouchSelected();
    }

    void DeletePreset()
    {
        if (SelectedTool is null || SelectedPreset is null) return;
        if (SelectedTool.CuttingPresets.Count <= 1) return;
        SelectedTool.CuttingPresets.Remove(SelectedPreset);
        SelectedPreset = SelectedTool.CuttingPresets.FirstOrDefault();
        TouchSelected();
    }

    public void TouchSelected()
    {
        if (SelectedTool is null) return;
        SelectedTool.LastModifiedUtc = DateTime.UtcNow;
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(FilteredTools));
    }

    /// <summary>Snapshot for save (deep clone).</summary>
    public List<MillBitTool> Snapshot() => Tools.Select(CloneTool).ToList();

    public static MillBitTool CloneTool(MillBitTool src)
    {
        var presets = (src.CuttingPresets ?? [])
            .Select(p => new MillBitCuttingPreset
            {
                Name = p.Name,
                SpindleRpm = p.SpindleRpm,
                SurfaceSpeedMPerMin = p.SurfaceSpeedMPerMin,
                CuttingFeedMmS = p.CuttingFeedMmS,
                FeedPerToothMm = p.FeedPerToothMm,
                PlungeFeedMmMin = p.PlungeFeedMmMin,
                StepoverMm = p.StepoverMm,
                StepdownMm = p.StepdownMm,
                FinishAllowanceMm = p.FinishAllowanceMm,
                RapidZMm = p.RapidZMm,
                SpindleDirection = p.SpindleDirection,
            }).ToList();
        if (presets.Count == 0)
            presets.Add(new MillBitCuttingPreset());

        var holders = (src.HolderSegments ?? [])
            .Select(h => new MillBitHolderSegment
            {
                HeightMm = h.HeightMm,
                TopDiameterMm = h.TopDiameterMm,
                BottomDiameterMm = h.BottomDiameterMm,
            }).ToList();

        return new MillBitTool
        {
            Id = src.Id,
            ErpId = src.ErpId,
            Name = src.Name,
            Identifier = src.Identifier,
            ToolNumber = src.ToolNumber,
            Type = src.Type,
            DiameterMm = src.DiameterMm,
            ShaftDiameterMm = src.ShaftDiameterMm,
            CornerRadiusMm = src.CornerRadiusMm,
            TotalLengthMm = src.TotalLengthMm,
            FluteLengthMm = src.FluteLengthMm,
            ShoulderLengthMm = src.ShoulderLengthMm,
            LengthBelowHolderMm = src.LengthBelowHolderMm,
            FluteCount = src.FluteCount,
            MaxDepthMm = src.MaxDepthMm,
            IsDefaultSpindleBit = src.IsDefaultSpindleBit,
            ShowSpindleCylinder = src.ShowSpindleCylinder,
            CylinderLengthMm = src.CylinderLengthMm,
            CylinderFlip = src.CylinderFlip,
            LastModifiedUtc = src.LastModifiedUtc,
            HolderSegments = holders,
            CuttingPresets = presets,
        };
    }
}
