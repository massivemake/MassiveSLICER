using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Root ViewModel for <c>MainWindow</c>. Owns all top-level child ViewModels
/// and mediates cross-panel communication (e.g., a model load in the toolbar
/// updates both the viewport and the properties panel).
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    /// <summary>Gets the ViewModel for the top toolbar strip.</summary>
    public ToolbarViewModel Toolbar { get; } = new();

    /// <summary>Gets the ViewModel for the left workspace/outliner panel.</summary>
    public LeftPanelViewModel LeftPanel { get; } = new();

    /// <summary>Gets the ViewModel for the 3D viewport canvas.</summary>
    public ViewportViewModel Viewport { get; } = new();

    /// <summary>Gets the ViewModel for the right settings panel.</summary>
    public RightPanelViewModel RightPanel { get; } = new();

    /// <summary>Gets the ViewModel for the bottom status bar.</summary>
    public StatusBarViewModel StatusBar { get; } = new();

    /// <summary>Gets the ViewModel for the floating command console.</summary>
    public ConsoleViewModel Console { get; } = new();

    /// <summary>Shared application preferences instance, loaded from disk at startup.</summary>
    public AppPreferences AppPreferences { get; } = PreferencesLoader.Load();

    /// <summary>ViewModel backing the Preferences window.</summary>
    public PreferencesViewModel Preferences { get; }

    /// <summary>Initialises the ViewModel and wires child ViewModels.</summary>
    public MainWindowViewModel()
    {
        Preferences = new PreferencesViewModel(AppPreferences, SyncViewportFromPrefs);
        SyncViewportFromPrefs();

        // Give the viewport direct access to the robot panel so the render loop
        // can read joint angles for FK without a cross-tree binding.
        Viewport.Robot = RightPanel.Settings.Robot;

        // Give the viewport direct access to additive settings for the slice command.
        Viewport.AdditiveSettings = RightPanel.Additive;

        // Load persisted material presets and restore the last selection.
        foreach (var preset in MaterialPresetsLoader.Load())
            RightPanel.Additive.MaterialPresets.Add(preset);

        if (AppPreferences.SelectedMaterialPresetName is { } savedPreset)
        {
            int idx = RightPanel.Additive.MaterialPresets
                .Select((p, i) => (p, i))
                .FirstOrDefault(t => t.p.Name == savedPreset, (null!, -1)).i;
            if (idx >= 0) RightPanel.Additive.SelectedPresetIndex = idx;
        }

        RightPanel.Additive.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AdditiveSettingsViewModel.SelectedPresetIndex)) return;
            var idx = RightPanel.Additive.SelectedPresetIndex;
            AppPreferences.SelectedMaterialPresetName = idx >= 0 && idx < RightPanel.Additive.MaterialPresets.Count
                ? RightPanel.Additive.MaterialPresets[idx].Name
                : null;
            PreferencesLoader.Save(AppPreferences);
        };

        // Share the viewport's authoritative outliner list with the left panel.
        LeftPanel.OutlinerItems = Viewport.OutlinerItems;

        // Persist the default backdrop and blur whenever the user sets one.
        Viewport.OnSetDefaultBackdrop = path =>
        {
            AppPreferences.DefaultBackdropPath = path;
            AppPreferences.DefaultBackdropBlur = Viewport.BackdropBlur;
            PreferencesLoader.Save(AppPreferences);
        };

        // Persist visibility toggles and handle cross-panel side-effects.
        Viewport.PropertyChanged += (_, e) =>
        {
            bool dirty = false;
            switch (e.PropertyName)
            {
                case nameof(ViewportViewModel.ShowGrid):
                    AppPreferences.ShowGrid = Viewport.ShowGrid; dirty = true; break;
                case nameof(ViewportViewModel.ShowAxes):
                    AppPreferences.ShowAxes = Viewport.ShowAxes; dirty = true; break;
                case nameof(ViewportViewModel.ShowBedGrid):
                    AppPreferences.ShowBedGrid = Viewport.ShowBedGrid; dirty = true; break;
                case nameof(ViewportViewModel.IsToolpathSelected):
                    if (Viewport.IsToolpathSelected)
                        RightPanel.ActiveTab = RightPanelTab.Toolpath;
                    break;
            }
            if (dirty) PreferencesLoader.Save(AppPreferences);
        };

        // Wire the robot connect button to the robot panel and mirror status to toolbar.
        var robot = RightPanel.Settings.Robot;
        Toolbar.SyncRobotRequested += (_, _) => robot.ConnectCommand.Execute(null);
        robot.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RobotPanelViewModel.ConnectionStatus))
                Toolbar.RobotStatus = robot.ConnectionStatus;
        };

        // Restore default backdrop and blur from prefs.
        if (AppPreferences.DefaultBackdropPath is { } saved)
        {
            var match = Viewport.AvailableBackdrops.FirstOrDefault(b => b.Path == saved);
            if (match is not null)
                Viewport.ActiveBackdrop = match;
        }
        Viewport.BackdropBlur = AppPreferences.DefaultBackdropBlur;
    }

    /// <summary>
    /// Propagates <see cref="AppPreferences"/> values that affect the live viewport
    /// to <see cref="Viewport"/>. Call after Apply() on the Preferences window.
    /// </summary>
    public void SyncViewportFromPrefs()
    {
        Viewport.ShowGrid    = AppPreferences.ShowGrid;
        Viewport.ShowAxes    = AppPreferences.ShowAxes;
        Viewport.ShowBedGrid = AppPreferences.ShowBedGrid;
        Viewport.ActivePreset = AppPreferences.ActivePreset;
    }
}
