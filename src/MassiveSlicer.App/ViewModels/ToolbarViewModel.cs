using System.Windows.Input;
using MassiveSlicer.App.Undo;
using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Manages state and commands for the top toolbar strip.
/// Includes file operations, mode toggling, and view presets.
///</summary>
public sealed class ToolbarViewModel : ViewModelBase
{
    // -- Mode ----------------------------------------------------------------

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

    // -- View ----------------------------------------------------------------

    private ViewMode _activeViewMode = ViewMode.Shaded;

    /// <summary>Current shading mode for the 3D viewport.</summary>
    public ViewMode ActiveViewMode
    {
        get => _activeViewMode;
        set => SetField(ref _activeViewMode, value);
    }

    // -- Robot connection ----------------------------------------------------

    private ConnectionStatus _robotStatus = ConnectionStatus.Disconnected;

    /// <summary>Live connection state of the C3Bridge robot link.</summary>
    public ConnectionStatus RobotStatus
    {
        get => _robotStatus;
        set => SetField(ref _robotStatus, value);
    }

    // -- UI visibility -------------------------------------------------------

    private bool _isRightPanelVisible = true;

    /// <summary>Whether the right settings panel is currently shown.</summary>
    public bool IsRightPanelVisible
    {
        get => _isRightPanelVisible;
        set => SetField(ref _isRightPanelVisible, value);
    }

    public const double DefaultConsoleHeight = 250;
    public const double MinConsoleHeight     = 120;
    public const double MaxConsoleHeight     = 600;

    private bool _isConsoleVisible = false;

    /// <summary>Whether the bottom-left console panel is expanded.</summary>
    public bool IsConsoleVisible
    {
        get => _isConsoleVisible;
        set
        {
            if (!SetField(ref _isConsoleVisible, value)) return;
            OnPropertyChanged(nameof(DockedConsoleHeight));
        }
    }

    private double _consolePanelHeight = DefaultConsoleHeight;

    /// <summary>Height of the resizable console body (history + input), in pixels.</summary>
    public double ConsolePanelHeight
    {
        get => _consolePanelHeight;
        set
        {
            var clamped = Math.Clamp(value, MinConsoleHeight, MaxConsoleHeight);
            if (!SetField(ref _consolePanelHeight, clamped)) return;
            OnPropertyChanged(nameof(DockedConsoleHeight));
        }
    }

    /// <summary>Effective console height — zero when collapsed.</summary>
    public double DockedConsoleHeight => IsConsoleVisible ? ConsolePanelHeight : 0;

    // -- Commands -------------------------------------------------------------

    /// <summary>Opens a file-picker dialog to load a 3D model.</summary>
    public ICommand OpenModelCommand { get; }

    /// <summary>Opens a saved <c>.mass</c> workspace file.</summary>
    public ICommand OpenWorkspaceCommand { get; }

    /// <summary>Saves to the current workspace file, or prompts for a path if none is open.</summary>
    public ICommand SaveWorkspaceCommand { get; }

    /// <summary>Prompts for a new path and saves the workspace there.</summary>
    public ICommand SaveWorkspaceAsCommand { get; }

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

    /// <summary>Undoes the last transform or settings change.</summary>
    public ICommand UndoCommand { get; }

    /// <summary>Redoes the last undone change.</summary>
    public ICommand RedoCommand { get; }

    /// <summary>Initiates a live sync from the connected KRC4 controller.</summary>
    public ICommand SyncRobotCommand { get; }

    private UndoRedoService? _undoRedo;

    /// <summary>Initialises all commands.</summary>
    public ToolbarViewModel()
    {
        OpenModelCommand        = new RelayCommand(OpenModel);
        OpenWorkspaceCommand    = new RelayCommand(OpenWorkspace);
        SaveWorkspaceCommand    = new RelayCommand(SaveWorkspace);
        SaveWorkspaceAsCommand  = new RelayCommand(SaveWorkspaceAs);
        ImportKrlCommand        = new RelayCommand(ImportKrl);
OpenPreferencesCommand  = new RelayCommand(OpenPreferences);
        SetPrepareModeCommand   = new RelayCommand(() => ActiveMode = AppMode.Prepare);
        SetPreviewModeCommand   = new RelayCommand(() => ActiveMode = AppMode.Preview);
        FrameAllCommand         = new RelayCommand(FrameAll);
        TopViewCommand          = new RelayCommand(TopView);
        FrontViewCommand        = new RelayCommand(FrontView);
        IsometricViewCommand    = new RelayCommand(IsometricView);
        ToggleRightPanelCommand = new RelayCommand(() => IsRightPanelVisible = !IsRightPanelVisible);
        ToggleConsoleCommand    = new RelayCommand(ToggleConsole);
        UndoCommand             = new RelayCommand(Undo, () => _undoRedo?.CanUndo ?? false);
        RedoCommand             = new RelayCommand(Redo, () => _undoRedo?.CanRedo ?? false);
        SyncRobotCommand        = new RelayCommand(SyncRobot);
    }

    /// <summary>Wires the shared undo/redo stack and refreshes command availability.</summary>
    public void AttachUndoRedo(UndoRedoService undoRedo)
    {
        _undoRedo = undoRedo;
        undoRedo.StateChanged += () =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            ((RelayCommand)UndoCommand).RaiseCanExecuteChanged();
            ((RelayCommand)RedoCommand).RaiseCanExecuteChanged();
        };
    }

    public bool CanUndo => _undoRedo?.CanUndo ?? false;
    public bool CanRedo => _undoRedo?.CanRedo ?? false;

    // -- Events ---------------------------------------------------------------

    /// <summary>Raised when the user triggers the Open Model command.</summary>
    public event EventHandler? ModelLoadRequested;

    /// <summary>Raised when the user triggers Open Workspace.</summary>
    public event EventHandler? OpenWorkspaceRequested;

    /// <summary>Raised when the user triggers Save (current file).</summary>
    public event EventHandler? SaveWorkspaceRequested;

    /// <summary>Raised when the user triggers Save As.</summary>
    public event EventHandler? SaveWorkspaceAsRequested;

    /// <summary>Raised when the user triggers the Preferences command.</summary>
    public event EventHandler? PreferencesRequested;

    /// <summary>Raised when the user clicks the robot sync/connect button.</summary>
    public event EventHandler? SyncRobotRequested;

    /// <summary>Raised when the user clicks Frame All to fit all scene objects in view.</summary>
    public event EventHandler? FrameAllRequested;

    /// <summary>Raised when the user triggers Import KRL.</summary>
    public event EventHandler? ImportKrlRequested;

    // ── Private handlers (wired up to real logic incrementally) ──────────────

    private void OpenModel() => ModelLoadRequested?.Invoke(this, EventArgs.Empty);
    private void OpenWorkspace() => OpenWorkspaceRequested?.Invoke(this, EventArgs.Empty);
    private void SaveWorkspace()   => SaveWorkspaceRequested?.Invoke(this, EventArgs.Empty);
    private void SaveWorkspaceAs() => SaveWorkspaceAsRequested?.Invoke(this, EventArgs.Empty);
    private void ImportKrl() => ImportKrlRequested?.Invoke(this, EventArgs.Empty);
    private void Undo()      => _undoRedo?.Undo();
    private void Redo()      => _undoRedo?.Redo();
    private void OpenPreferences() => PreferencesRequested?.Invoke(this, EventArgs.Empty);
    private void FrameAll()        => FrameAllRequested?.Invoke(this, EventArgs.Empty);
    private void TopView()         { /* TODO: notify viewport camera preset    */ }
    private void FrontView()       { /* TODO: notify viewport camera preset    */ }
    private void IsometricView()   { /* TODO: notify viewport camera preset    */ }
    private void SyncRobot() => SyncRobotRequested?.Invoke(this, EventArgs.Empty);

    private void ToggleConsole()
    {
        if (IsConsoleVisible)
        {
            IsConsoleVisible = false;
            return;
        }

        if (_consolePanelHeight < MinConsoleHeight)
            ConsolePanelHeight = DefaultConsoleHeight;
        IsConsoleVisible = true;
    }
}
