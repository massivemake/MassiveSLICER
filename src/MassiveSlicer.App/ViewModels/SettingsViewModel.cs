using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// The settings panel reached via the gear icon on the right panel tab bar.
/// Contains four sub-tab ViewModels: VIEW, UV, ROBOT, and PROPS.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private SettingsTab _activeTab = SettingsTab.View;

    /// <summary>Which settings sub-tab is currently displayed.</summary>
    public SettingsTab ActiveTab
    {
        get => _activeTab;
        set => SetField(ref _activeTab, value);
    }

    /// <summary>Viewport appearance settings (theme, lights, shader, environment).</summary>
    public ViewSettingsViewModel View { get; } = new();

    /// <summary>UV channel viewer and texture slot management.</summary>
    public UvSettingsViewModel Uv { get; } = new();

    /// <summary>Robot joint control, TCP readout, and tool library.</summary>
    public RobotPanelViewModel Robot { get; } = new();

    /// <summary>Object/bed/robot-cell property inspector.</summary>
    public PropsViewModel Props { get; } = new();
}
