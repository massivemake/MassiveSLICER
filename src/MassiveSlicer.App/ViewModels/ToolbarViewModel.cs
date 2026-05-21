using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Manages state and commands for the top toolbar strip.
/// Includes file operations, mode toggling, and view presets.
///</summary>
public sealed class ToolbarViewModel : ViewModelBase
{
    // ── Mode ────────────────────────────────────────────────────────────────

    private AppMode _activeMode = AppMode.Prepare;

    /// <summary>
    /// The current application mode: <see cref="AppMode.Prepare"/> for editing/slicing,
    /// <see cref="AppMode.Preview"/> for KRL animation playback.
    /// </summary>
    public AppMode ActiveMode
    {
        get => _activeMode;
        set => SetField(ref _activeMode, value);
    }

    // ── View ────────────────────────────────────────────────────────────────

    private ViewMode _activeViewMode = ViewMode.Shaded;

    /// <summary>Current shading mode for the 3D viewport.</summary>
    public ViewMode ActiveViewMode
    {
        get => _activeViewMode;
        set => SetField(ref _activeViewMode, value);
    }

    // ── Robot connection ────────────────────────────────────────────────────

    private ConnectionStatus _robotStatus = ConnectionStatus.Disconnected;

    /// <summary>Live connection state of the C3Bridge robot link.</summary>
    public ConnectionStatus RobotStatus
    {
        get => _robotStatus;
        set => SetField(ref _robotStatus, value);
    }

    // ── UI visibility ───────────────────────────────────────────────────────

    private bool _isRightPanelVisible = true;

    /// <summary>Whether the right settings panel is currently shown.</summary>
    public bool IsRightPanelVisible
    {
        get => _isRightPanelVisible;
        set => SetField(ref _isRightPanelVisible, value);
    }

    private bool _isConsoleVisible = false;

    /// <summary>Whether the floating command console overlay is open.</summary>
    public bool IsConsoleVisible
    {
        get => _isConsoleVisible;
        set => SetField(ref _isConsoleVisible, value);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>Opens a file-picker dialog to load a 3D model.</summary>
    public ICommand OpenModelCommand { get; }

    /// <summary>Opens a file-picker dialog to import a KRL .src program.</summary>
    public ICommand ImportKrlCommand { get; }

/// <summary>Opens the application preferences dialog.</summary>
    public ICommand OpenPreferencesCommand { get; }

    /// <summary>Switches to <see cref="AppMode.Prepare"/>.</summary>
    public ICommand SetPrepareModeCommand { get; }

    /// <summary>Switches to <see cref="AppMode.Preview"/>.</summary>
    public ICommand SetPreviewModeCommand { get; }

    /// <summary>Frames all scene objects in the viewport camera.</summary>
    public ICommand FrameAllCommand { get; }

    /// <summary>Snaps the camera to a top-down view (Z-up).</summary>
    public ICommand TopViewCommand { get; }

    /// <summary>Snaps the camera to a front view.</summary>
    public ICommand FrontViewCommand { get; }

    /// <summary>Snaps the camera to a standard isometric view.</summary>
    public ICommand IsometricViewCommand { get; }

    /// <summary>Toggles visibility of the right settings panel.</summary>
    public ICommand ToggleRightPanelCommand { get; }

    /// <summary>Toggles visibility of the command console.</summary>
    public ICommand ToggleConsoleCommand { get; }

    /// <summary>Initiates a live sync from the connected KRC4 controller.</summary>
    public ICommand SyncRobotCommand { get; }

    /// <summary>Initialises all commands.</summary>
    public ToolbarViewModel()
    {
        OpenModelCommand        = new RelayCommand(OpenModel);
        ImportKrlCommand        = new RelayCommand(ImportKrl);
OpenPreferencesCommand  = new RelayCommand(OpenPreferences);
        SetPrepareModeCommand   = new RelayCommand(() => ActiveMode = AppMode.Prepare);
        SetPreviewModeCommand   = new RelayCommand(() => ActiveMode = AppMode.Preview);
        FrameAllCommand         = new RelayCommand(FrameAll);
        TopViewCommand          = new RelayCommand(TopView);
        FrontViewCommand        = new RelayCommand(FrontView);
        IsometricViewCommand    = new RelayCommand(IsometricView);
        ToggleRightPanelCommand = new RelayCommand(() => IsRightPanelVisible = !IsRightPanelVisible);
        ToggleConsoleCommand    = new RelayCommand(() => IsConsoleVisible = !IsConsoleVisible);
        SyncRobotCommand        = new RelayCommand(SyncRobot);
    }

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>Raised when the user triggers the Open Model command.</summary>
    public event EventHandler? ModelLoadRequested;

    /// <summary>Raised when the user triggers the Preferences command.</summary>
    public event EventHandler? PreferencesRequested;

    // ── Private handlers (wired up to real logic incrementally) ──────────────

    private void OpenModel() => ModelLoadRequested?.Invoke(this, EventArgs.Empty);
    private void ImportKrl()       { /* TODO: open file dialog → parse KRL    */ }
    private void OpenPreferences() => PreferencesRequested?.Invoke(this, EventArgs.Empty);
    private void FrameAll()        { /* TODO: notify viewport to frame scene   */ }
    private void TopView()         { /* TODO: notify viewport camera preset    */ }
    private void FrontView()       { /* TODO: notify viewport camera preset    */ }
    private void IsometricView()   { /* TODO: notify viewport camera preset    */ }
    private void SyncRobot()       { /* TODO: trigger C3Bridge sync            */ }
}
