using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport;
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

        // Restore all persisted settings before subscribing so saves don't fire
        // during initialisation.
        SyncViewportFromPrefs();

        // ── Auto-save on any relevant change ─────────────────────────────────

        Viewport.PropertyChanged += (_, e) =>
        {
            // Cross-panel side-effect: switch to toolpath tab on selection.
            if (e.PropertyName == nameof(ViewportViewModel.IsToolpathSelected)
                && Viewport.IsToolpathSelected)
                RightPanel.ActiveTab = RightPanelTab.Toolpath;

            // Skip transient / non-persistent properties to avoid unnecessary disk writes.
            if (e.PropertyName is nameof(ViewportViewModel.HasSelection)
                                or nameof(ViewportViewModel.IsSlicing)
                                or nameof(ViewportViewModel.IsToolpathSelected)
                                or nameof(ViewportViewModel.IsLayFlatMode)
                                or nameof(ViewportViewModel.ToolpathScrubIndex)
                                or nameof(ViewportViewModel.ToolpathScrubMax)
                                or nameof(ViewportViewModel.ToolpathScrubText))
                return;

            PersistSettings();
        };

        RightPanel.Settings.View.PropertyChanged += (_, _) => PersistSettings();
        RightPanel.Additive.PropertyChanged      += (_, _) => PersistSettings();

        // Wire toolbar commands to cross-panel actions.
        Toolbar.FrameAllRequested  += (_, _) => Viewport.OnFrameAllRequested?.Invoke();

        // Wire the robot connect button to the robot panel and mirror status to toolbar.
        var robot = RightPanel.Settings.Robot;
        Toolbar.SyncRobotRequested += (_, _) => robot.ConnectCommand.Execute(null);
        robot.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RobotPanelViewModel.ConnectionStatus))
                Toolbar.RobotStatus = robot.ConnectionStatus;
        };
    }

    /// <summary>
    /// Copies all persisted settings from <see cref="AppPreferences"/> back into
    /// the live ViewModels. Called at startup and after the Preferences dialog applies.
    /// </summary>
    public void SyncViewportFromPrefs()
    {
        var p    = AppPreferences;
        var vp   = Viewport;
        var view = RightPanel.Settings.View;
        var add  = RightPanel.Additive;

        // Viewport visibility & navigation
        vp.ShowGrid     = p.ShowGrid;
        vp.ShowAxes     = p.ShowAxes;
        vp.ShowBedGrid  = p.ShowBedGrid;
        vp.ActivePreset = p.ActivePreset;

        // Lighting
        vp.LightAzimuth   = p.LightAzimuth;
        vp.LightElevation = p.LightElevation;
        vp.LightIntensity = p.LightIntensity;

        // Shader mode
        if (Enum.TryParse<ShaderMode>(p.ShaderMode, out var sm))
            vp.ActiveShaderMode = sm;

        // Backdrop
        if (p.DefaultBackdropPath is { } backdropPath)
        {
            var match = vp.AvailableBackdrops.FirstOrDefault(b => b.Path == backdropPath);
            if (match is not null) vp.ActiveBackdrop = match;
        }
        vp.BackdropBlur = p.DefaultBackdropBlur;

        // View settings panel
        if (Enum.TryParse<AppTheme>(p.AppTheme, out var theme))
            view.ActiveTheme = theme;
        view.ShowEdges            = p.ShowEdges;
        view.ShadowCatcherEnabled = p.ShadowCatcherEnabled;

        // Additive slicing settings
        add.LayerHeight      = p.LayerHeight;
        add.BeadWidth        = p.BeadWidth;
        add.FirstLayerHeight = p.FirstLayerHeight;
        if (Enum.TryParse<SliceMethod>(p.SliceMethod, out var method))
            add.Method = method;
        add.PassAngle    = p.PassAngle;
        add.TiltAngle    = p.TiltAngle;
        add.TiltAngleX   = p.TiltAngleX;
        add.FeedRate     = p.FeedRate;
        add.PtpSpeed     = p.PtpSpeed;
        add.Acceleration = p.Acceleration;
        add.ApproachZ    = p.ApproachZ;
        add.ToolDataIndex = p.ToolDataIndex;
        add.BaseDataIndex = p.BaseDataIndex;
        add.ToolheadA    = p.ToolheadA;
        add.ToolheadB    = p.ToolheadB;
        add.ToolheadC    = p.ToolheadC;
    }

    /// <summary>
    /// Snapshots all auto-save eligible ViewModel state into <see cref="AppPreferences"/>
    /// and flushes to disk. Called on every relevant PropertyChanged event.
    /// </summary>
    private void PersistSettings()
    {
        var p    = AppPreferences;
        var vp   = Viewport;
        var view = RightPanel.Settings.View;
        var add  = RightPanel.Additive;

        // Viewport visibility & navigation
        p.ShowGrid     = vp.ShowGrid;
        p.ShowAxes     = vp.ShowAxes;
        p.ShowBedGrid  = vp.ShowBedGrid;
        p.ActivePreset = vp.ActivePreset;

        // Lighting
        p.LightAzimuth   = vp.LightAzimuth;
        p.LightElevation = vp.LightElevation;
        p.LightIntensity = vp.LightIntensity;

        // Shader mode & backdrop
        p.ShaderMode          = vp.ActiveShaderMode.ToString();
        p.DefaultBackdropPath = vp.ActiveBackdropPath;
        p.DefaultBackdropBlur = vp.BackdropBlur;

        // View settings panel
        p.AppTheme             = view.ActiveTheme.ToString();
        p.ShowEdges            = view.ShowEdges;
        p.ShadowCatcherEnabled = view.ShadowCatcherEnabled;

        // Additive slicing settings
        p.LayerHeight      = add.LayerHeight;
        p.BeadWidth        = add.BeadWidth;
        p.FirstLayerHeight = add.FirstLayerHeight;
        p.SliceMethod      = add.Method.ToString();
        p.PassAngle        = add.PassAngle;
        p.TiltAngle        = add.TiltAngle;
        p.TiltAngleX       = add.TiltAngleX;
        p.FeedRate         = add.FeedRate;
        p.PtpSpeed         = add.PtpSpeed;
        p.Acceleration     = add.Acceleration;
        p.ApproachZ        = add.ApproachZ;
        p.ToolDataIndex    = add.ToolDataIndex;
        p.BaseDataIndex    = add.BaseDataIndex;
        p.ToolheadA        = add.ToolheadA;
        p.ToolheadB        = add.ToolheadB;
        p.ToolheadC        = add.ToolheadC;

        PreferencesLoader.Save(p);
    }
}
