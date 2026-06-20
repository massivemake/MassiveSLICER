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
}
