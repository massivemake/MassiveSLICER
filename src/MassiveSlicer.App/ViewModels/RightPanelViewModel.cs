using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Manages the right settings panel. Owns the primary tab ViewModels
/// and tracks which tab (Additive, Scan, Subtractive, Settings) is active.
/// </summary>
public sealed class RightPanelViewModel : ViewModelBase
{
    private RightPanelTab _activeTab = RightPanelTab.Additive;
    private bool _hasScanTab = true;
    private bool _lfam3TabGating;
    private bool _lfam3ShowAdditiveTab = true;
    private bool _lfam3ShowScanTab = true;
    private bool _lfam3ShowSubtractiveTab;

    /// <summary>
    /// Whether the Scan tab is available for the current cell.
    /// Set to <see langword="false"/> for cells without a scanner — hides the tab button
    /// and redirects to Additive if the Scan tab was active.
    /// </summary>
    public bool HasScanTab
    {
        get => _hasScanTab;
        set
        {
            if (!SetField(ref _hasScanTab, value)) return;
            NotifyTabBarVisibility();
            if (!value && _activeTab == RightPanelTab.Scan)
                ActiveTab = RightPanelTab.Additive;
        }
    }

    /// <summary>The currently selected primary tab.</summary>
    public RightPanelTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (!SetField(ref _activeTab, value)) return;
            OnPropertyChanged(nameof(IsAdditiveTab));
            OnPropertyChanged(nameof(IsScanTab));
            OnPropertyChanged(nameof(IsSubtractiveTab));
            OnPropertyChanged(nameof(IsSettingsTab));
            OnPropertyChanged(nameof(IsToolpathTab));
        }
    }

    public bool IsAdditiveTab     => ActiveTab == RightPanelTab.Additive;
    public bool IsScanTab         => ActiveTab == RightPanelTab.Scan;
    public bool IsSubtractiveTab  => ActiveTab == RightPanelTab.Subtractive;
    public bool IsSettingsTab     => ActiveTab == RightPanelTab.Settings;
    public bool IsToolpathTab     => ActiveTab == RightPanelTab.Toolpath;

    /// <summary>LFAM 3 workflow limits the tab bar to the active phase.</summary>
    public bool IsLfam3TabGatingActive => _lfam3TabGating;

    public bool ShowAdditiveTabButton =>
        !_lfam3TabGating || _lfam3ShowAdditiveTab;

    public bool ShowScanTabButton =>
        HasScanTab && (!_lfam3TabGating || _lfam3ShowScanTab);

    public bool ShowSubtractiveTabButton =>
        !_lfam3TabGating || _lfam3ShowSubtractiveTab;

    public bool ShowToolpathTabButton => true;

    public ICommand ShowAdditiveCommand     { get; }
    public ICommand ShowScanCommand         { get; }
    public ICommand ShowSubtractiveCommand  { get; }
    public ICommand ShowSettingsCommand     { get; }
    public ICommand ShowToolpathCommand     { get; }

    public RightPanelViewModel()
    {
        ShowAdditiveCommand    = new RelayCommand(() => ActiveTab = RightPanelTab.Additive);
        ShowScanCommand        = new RelayCommand(() => ActiveTab = RightPanelTab.Scan);
        ShowSubtractiveCommand = new RelayCommand(() => ActiveTab = RightPanelTab.Subtractive);
        ShowSettingsCommand    = new RelayCommand(() => ActiveTab = RightPanelTab.Settings);
        ShowToolpathCommand    = new RelayCommand(() => ActiveTab = RightPanelTab.Toolpath);
    }

    private bool _stepModelExpanded;
    /// <summary>Step 1 MODEL card expansion (auto-opened on import).</summary>
    public bool StepModelExpanded
    {
        get => _stepModelExpanded;
        set => SetField(ref _stepModelExpanded, value);
    }

    private bool _stepSliceExpanded = true;
    /// <summary>Step 2 SLICE card expansion (auto-opened on import).</summary>
    public bool StepSliceExpanded
    {
        get => _stepSliceExpanded;
        set => SetField(ref _stepSliceExpanded, value);
    }

    private bool _stepToolpathExpanded;
    /// <summary>Step 4 TOOLPATH card expansion (auto-opened when the toolhead is selected).</summary>
    public bool StepToolpathExpanded
    {
        get => _stepToolpathExpanded;
        set => SetField(ref _stepToolpathExpanded, value);
    }

    private bool _globalSectionExpanded;
    /// <summary>GLOBAL section inside step 4 (home position + toolhead orientation).</summary>
    public bool GlobalSectionExpanded
    {
        get => _globalSectionExpanded;
        set => SetField(ref _globalSectionExpanded, value);
    }

    private bool _highlightToolheadOrientation;
    /// <summary>Drives the attention flash around TOOLHEAD GLOBAL ORIENTATION.</summary>
    public bool HighlightToolheadOrientation
    {
        get => _highlightToolheadOrientation;
        private set => SetField(ref _highlightToolheadOrientation, value);
    }

    private Avalonia.Threading.DispatcherTimer? _flashTimer;

    /// <summary>Opens step 4 + GLOBAL and flashes a green border around the toolhead
    /// orientation settings, fading out after a moment (user guidance).</summary>
    public void FlashToolheadOrientation()
    {
        StepToolpathExpanded  = true;
        GlobalSectionExpanded = true;
        HighlightToolheadOrientation = true;
        _flashTimer?.Stop();
        _flashTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5),
        };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer!.Stop();
            HighlightToolheadOrientation = false;
        };
        _flashTimer.Start();
    }

    /// <summary>Settings for additive (print) slicing operations.</summary>
    public AdditiveSettingsViewModel Additive { get; } = new();

    /// <summary>Settings for the Zivid 3D scanner workflow.</summary>
    public ScanSettingsViewModel Scan { get; } = new();

    /// <summary>Settings for subtractive (milling) slicing operations.</summary>
    public SubtractiveSettingsViewModel Subtractive { get; } = new();

    /// <summary>
    /// Settings sub-panel with VIEW / UV / ROBOT / PROPS sub-tabs.
    /// Activated via the gear icon.
    /// </summary>
    public SettingsViewModel Settings { get; } = new();

    /// <summary>
    /// LFAM 3 workflow: show only the sidebar tab(s) for the active phase and select it.
    /// </summary>
    public void SetLfam3WorkflowTabGating(bool active, bool showAdditive, bool showScan, bool showSubtractive)
    {
        if (_lfam3TabGating == active
            && _lfam3ShowAdditiveTab == showAdditive
            && _lfam3ShowScanTab == showScan
            && _lfam3ShowSubtractiveTab == showSubtractive)
            return;

        _lfam3TabGating = active;
        _lfam3ShowAdditiveTab = showAdditive;
        _lfam3ShowScanTab = showScan;
        _lfam3ShowSubtractiveTab = showSubtractive;
        NotifyTabBarVisibility();

        if (!active) return;

        // Keep TOOLPATH open when the user is reviewing path options across phase changes.
        if (_activeTab == RightPanelTab.Toolpath) return;

        if (showSubtractive)
            ActiveTab = RightPanelTab.Subtractive;
        else if (showScan && HasScanTab)
            ActiveTab = RightPanelTab.Scan;
        else if (showAdditive)
            ActiveTab = RightPanelTab.Additive;
    }

    void NotifyTabBarVisibility()
    {
        OnPropertyChanged(nameof(IsLfam3TabGatingActive));
        OnPropertyChanged(nameof(ShowAdditiveTabButton));
        OnPropertyChanged(nameof(ShowScanTabButton));
        OnPropertyChanged(nameof(ShowSubtractiveTabButton));
        OnPropertyChanged(nameof(ShowToolpathTabButton));
    }
}
