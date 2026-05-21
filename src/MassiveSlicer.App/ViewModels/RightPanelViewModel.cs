using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Manages the right settings panel. Owns the three primary tab ViewModels
/// and tracks which tab (Additive, Subtractive, Settings) is active.
/// </summary>
public sealed class RightPanelViewModel : ViewModelBase
{
    private RightPanelTab _activeTab = RightPanelTab.Additive;

    /// <summary>The currently selected primary tab.</summary>
    public RightPanelTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (!SetField(ref _activeTab, value)) return;
            OnPropertyChanged(nameof(IsAdditiveTab));
            OnPropertyChanged(nameof(IsSubtractiveTab));
            OnPropertyChanged(nameof(IsSettingsTab));
        }
    }

    public bool IsAdditiveTab     => ActiveTab == RightPanelTab.Additive;
    public bool IsSubtractiveTab  => ActiveTab == RightPanelTab.Subtractive;
    public bool IsSettingsTab     => ActiveTab == RightPanelTab.Settings;

    public ICommand ShowAdditiveCommand     { get; }
    public ICommand ShowSubtractiveCommand  { get; }
    public ICommand ShowSettingsCommand     { get; }

    public RightPanelViewModel()
    {
        ShowAdditiveCommand    = new RelayCommand(() => ActiveTab = RightPanelTab.Additive);
        ShowSubtractiveCommand = new RelayCommand(() => ActiveTab = RightPanelTab.Subtractive);
        ShowSettingsCommand    = new RelayCommand(() => ActiveTab = RightPanelTab.Settings);
    }

    /// <summary>Settings for additive (print) slicing operations.</summary>
    public AdditiveSettingsViewModel Additive { get; } = new();

    /// <summary>Settings for subtractive (milling) slicing operations.</summary>
    public SubtractiveSettingsViewModel Subtractive { get; } = new();

    /// <summary>
    /// Settings sub-panel with VIEW / UV / ROBOT / PROPS sub-tabs.
    /// Activated via the gear icon.
    /// </summary>
    public SettingsViewModel Settings { get; } = new();
}
