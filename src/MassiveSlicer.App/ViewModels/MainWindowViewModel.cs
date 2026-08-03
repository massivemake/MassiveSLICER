using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Threading;
using MassiveSlicer.App;
using MassiveSlicer.App.Console;
using MassiveSlicer.App.Undo;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.C3Bridge;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Scanning;
using MassiveSlicer.Viewport;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.ViewModels.Base;
using OpenTK.Mathematics;

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

    /// <summary>
    /// Full LFAM3 calibration wizard (scan-cal → bed-cal). Bound from left panel
    /// <c>CALIBRATE LFAM3</c> button.
    /// </summary>
    public ICommand StartLfam3CalibrationCommand { get; }

    /// <summary>Teach current live pose as the scan/bed-cal waypoint (<c>scanner-down-bed</c>).</summary>
    public ICommand MarkScanPositionCommand { get; }

    /// <summary>Teach current live joints as the default <c>Home</c> position.</summary>
    public ICommand MarkHomePositionCommand { get; }

    /// <summary>PTP to the saved cell Home via MassiveDRIVE (MS_CMD=93).</summary>
    public ICommand GoHomeCommand { get; }

    /// <summary>Shared application preferences instance, loaded from disk at startup.</summary>
    public AppPreferences AppPreferences { get; } = PreferencesLoader.Load();

    /// <summary>ViewModel backing the Preferences window.</summary>
    public PreferencesViewModel Preferences { get; }

    /// <summary>Global undo/redo stack for transforms and settings.</summary>
    public UndoRedoService UndoRedo { get; } = new();

    private (WorkspaceDocument Doc, string Path)? _pendingWorkspaceRestore;
    private int _cellLoadRequestId;
    private int _workspaceRestoreGeneration;
    private bool _cellSceneReady;
    private bool _applyingUndoRedo;
    private bool _suppressWorkspaceDirty;
    private string _lastCommittedPrefsJson = "";
    private CancellationTokenSource? _settingsUndoDebounce;
    private string _lastProgressLogMessage = string.Empty;

    /// <summary>Marks the open workspace as having unsaved changes (yellow status dot).</summary>
    public void MarkWorkspaceDirty()
    {
        if (_suppressWorkspaceDirty || _applyingUndoRedo) return;
        StatusBar.IsWorkspaceDirty = true;
    }

    /// <summary>Clears the unsaved flag after a successful save or open (green status dot).</summary>
    public void ClearWorkspaceDirty()
    {
        StatusBar.IsWorkspaceDirty = false;
    }

    private static readonly JsonSerializerOptions PrefsJsonOptions = new() { WriteIndented = false };

    /// <summary>Initialises the ViewModel and wires child ViewModels.</summary>
    public MainWindowViewModel()
    {
        StartLfam3CalibrationCommand = new RelayCommand(() => StartLfam3CalibrationWizard(null));
        MarkScanPositionCommand = new RelayCommand(
            () => _ = MarkScanPositionAsync().ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is { } ex)
                    Console.LogError($"[scan-pose] Unhandled: {ex.GetBaseException().Message}");
            }, TaskScheduler.FromCurrentSynchronizationContext()));
        MarkHomePositionCommand = new RelayCommand(
            () => _ = MarkHomePositionAsync("Home").ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is { } ex)
                    Console.LogError($"[home] Unhandled: {ex.GetBaseException().Message}");
            }, TaskScheduler.FromCurrentSynchronizationContext()));
        GoHomeCommand = new RelayCommand(
            () => _ = GoToSavedHomeAsync().ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is { } ex)
                    Console.LogError($"[home] Unhandled: {ex.GetBaseException().Message}");
            }, TaskScheduler.FromCurrentSynchronizationContext()));

        // One-shot, off-to-the-side check for whether origin/main has moved past this
        // build's baseline. Never awaited here — must never add even a millisecond to
        // startup. Silently does nothing if it can't reach git or the network.
        BuildFreshnessChecker.CheckAsync(StatusBar);

        // Persist/restore collapsible-panel (Expander) open state across sessions via prefs.json.
        MassiveSlicer.App.Behaviors.PersistExpander.Store = AppPreferences;

        Preferences = new PreferencesViewModel(AppPreferences, () =>
        {
            // Preferences edits (theme, navigation, touchpad) must NOT re-push slicing
            // settings: those assignments fire PropertyChanged, which the realtime-slice
            // watchlist turns into a re-slice, and they would also overwrite slicing values
            // the user changed in the Additive panel this session.
            SyncViewportFromPrefs(includeSlicingSettings: false);
            OnSettingsChanged();
        });

        Toolbar.AttachUndoRedo(UndoRedo);
        Viewport.UndoRedo = UndoRedo;
        // Any undoable edit (transform, settings, paint, delete…) marks the scene dirty.
        UndoRedo.StateChanged += () =>
        {
            if (_suppressWorkspaceDirty) return;
            // Clear stack on New/Open also fires StateChanged — only mark dirty when
            // there is something on the stack (a real edit).
            if (UndoRedo.CanUndo || UndoRedo.CanRedo)
                MarkWorkspaceDirty();
        };
        Viewport.MarkWorkspaceDirty = MarkWorkspaceDirty;
        Viewport.OnPaintEditModeChanged = open => RightPanel.ApplyPaintEditMode(open);

        // Give the viewport direct access to the robot panel so the render loop
        // can read joint angles for FK without a cross-tree binding.
        Viewport.Robot = RightPanel.Settings.Robot;
        Viewport.LiveIo.AttachRobot(RightPanel.Settings.Robot);
        Viewport.Erp.Initialize(AppPreferences,
            () => PreferencesLoader.Save(AppPreferences),
            msg => Console.Log(msg));
        Viewport.RobotSmb.Initialize(AppPreferences,
            () => PreferencesLoader.Save(AppPreferences),
            msg => Console.Log(msg));
        Viewport.Erp.GetDefaultElementName = () =>
            AppPreferences.LastWorkspacePath is { Length: > 0 } wp
                ? System.IO.Path.GetFileNameWithoutExtension(wp)
                : null;
        Viewport.Erp.FindWorkspaceFiles = FindErpWorkspaceFiles;

        Viewport.Erp.OpenWorkspaceFile  = p => OpenWorkspace(p);

        Viewport.Erp.BuildSlicePayloadAsync = BuildErpSlicePayloadAsync;
        Toolbar.SetRecentWorkspaces(AppPreferences.RecentWorkspaces);
        Toolbar.OpenRecentRequested += (_, recentPath) =>
        {
            if (System.IO.File.Exists(recentPath))
            {
                OpenWorkspace(recentPath);
            }
            else
            {
                Console.LogError($"[workspace] '{recentPath}' no longer exists — removed from Open Recent.");
                AppPreferences.RecentWorkspaces.RemoveAll(r =>
                    string.Equals(r, recentPath, StringComparison.OrdinalIgnoreCase));
                PreferencesLoader.Save(AppPreferences);
                Toolbar.SetRecentWorkspaces(AppPreferences.RecentWorkspaces);
            }
        };
        Viewport.Erp.WorkspaceLinked = () =>
        {
            if (!TrySaveCurrentWorkspace())
                Console.Log("[workspace] linked to ERP but no project folder found — use Save As.");
        };

        // Give the viewport direct access to additive + subtractive settings for the slice/mill commands.
        Viewport.AdditiveSettings = RightPanel.Additive;
        Viewport.SubtractiveSettings = RightPanel.Subtractive;

        // Mill OPERATION → SELECT AREA tools arm mesh-face picking on the workpiece only
        // (user imports/scans — never robot, bed, or cell environment).
        RightPanel.Subtractive.ApplyAreaSelectTool = tool =>
        {
            Viewport.MillAreaSelectTool = tool;
            Viewport.ActiveTool = TransformTool.Select;
            switch (tool)
            {
                case Core.Models.MillAreaSelectTool.WholeModel:
                    Viewport.SelectionMode = SelectionMode.Object;
                    Viewport.ClearMillAreaSelection();
                    break;
                case Core.Models.MillAreaSelectTool.Face:
                    Viewport.SelectionMode = SelectionMode.Face;
                    break;
                case Core.Models.MillAreaSelectTool.Box:
                    Viewport.SelectionMode = SelectionMode.Face;
                    Viewport.PaintRegionSelectMode = "Square";
                    break;
                case Core.Models.MillAreaSelectTool.Lasso:
                    Viewport.SelectionMode = SelectionMode.Face;
                    Viewport.PaintRegionSelectMode = "Lasso";
                    break;
                case Core.Models.MillAreaSelectTool.Brush:
                    Viewport.SelectionMode = SelectionMode.Face;
                    break;
            }
            RightPanel.Subtractive.AreaSelectStatus = Viewport.MillAreaStatusText;
        };
        RightPanel.Subtractive.ClearAreaSelection = () =>
        {
            Viewport.MillAreaSelectTool = Core.Models.MillAreaSelectTool.WholeModel;
            Viewport.SelectionMode = SelectionMode.Object;
            Viewport.ActiveTool = TransformTool.Select;
            Viewport.ClearMillAreaSelection();
            RightPanel.Subtractive.AreaSelectStatus = Viewport.MillAreaStatusText;
        };
        Viewport.OnMillAreaSelectionChanged = () =>
            RightPanel.Subtractive.AreaSelectStatus = Viewport.MillAreaStatusText;
        Viewport.LogMill = msg => Console.Log(msg);

        // Modifiers panel reads the current selection back from the viewport, and the
        // viewport reads back which modifier is selected, to draw its plane preview.
        RightPanel.Modifiers.Viewport = Viewport;
        Viewport.ModifiersPanel = RightPanel.Modifiers;

        // X-bracing cylinder is placed on the print-bed centre (ImportSurfaceCenter),
        // not the robot / world origin.
        RightPanel.Additive.ResolvePrintBedCenterXY = () =>
        {
            var cell = Viewport.ActiveCell;
            if (cell?.Bed is null) return null;
            var c = cell.Bed.ImportSurfaceCenter(cell.Robot.WorldPosition);
            return (c.X, c.Y);
        };

        // Load persisted material presets and restore the last selection.
        foreach (var preset in MaterialPresetsLoader.Load())
            RightPanel.Additive.MaterialPresets.Add(preset);

        RightPanel.Additive.KrlPostProcess.LoadFrom(KrlPostProcessLoader.Load());

        if (AppPreferences.SelectedMaterialPresetName is { } savedPreset)
        {
            int idx = RightPanel.Additive.MaterialPresets
                .Select((p, i) => (p, i))
                .FirstOrDefault(t => t.p.Name == savedPreset, (null!, -1)).i;
            if (idx >= 0) RightPanel.Additive.SelectedPresetIndex = idx;
        }

        RightPanel.Additive.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AdditiveSettingsViewModel.LayerHeight)
                              or nameof(AdditiveSettingsViewModel.BeadWidth)
                              or nameof(AdditiveSettingsViewModel.SelectedPresetIndex))
                Viewport.NotifyWorkflowParamsChanged();

            if (e.PropertyName != nameof(AdditiveSettingsViewModel.SelectedPresetIndex)) return;
            var idx = RightPanel.Additive.SelectedPresetIndex;
            AppPreferences.SelectedMaterialPresetName = idx >= 0 && idx < RightPanel.Additive.MaterialPresets.Count
                ? RightPanel.Additive.MaterialPresets[idx].Name
                : null;
            ScheduleSettingsUndo();
            PreferencesLoader.Save(AppPreferences);
        };

        // Share the viewport's authoritative outliner list with the left panel.
        LeftPanel.OutlinerItems = Viewport.OutlinerItems;

        // Restore all persisted settings before subscribing so saves don't fire
        // during initialisation.
        SyncViewportFromPrefs();
        PersistSettings();
        _lastCommittedPrefsJson = CapturePrefsJson();

        // â”€â”€ Auto-save on any relevant change â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        Viewport.PropertyChanged += (_, e) =>
        {
            // Cross-panel: mesh â†’ Additive (or LFAM 3 phase tab); toolpath â†’ Toolpath.
            if (e.PropertyName is nameof(ViewportViewModel.IsToolpathSelected)
                                or nameof(ViewportViewModel.HasMeshSelected))
                SyncRightPanelToViewportSelection();

            if (e.PropertyName is nameof(ViewportViewModel.ShowLfam3ToolPicker)
                                or nameof(ViewportViewModel.IsPrintStepActive)
                                or nameof(ViewportViewModel.IsVerifyScanStepActive)
                                or nameof(ViewportViewModel.IsMillStepActive)
                                or nameof(ViewportViewModel.IsPrePrintScanStepActive)
                                or nameof(ViewportViewModel.HasPrePrintScanStep))
                SyncLfam3WorkflowSidebar();

            if (e.PropertyName is nameof(ViewportViewModel.IsSlicing))
            {
                StatusBar.IsProgressActive = Viewport.IsSlicing;
                if (Viewport.IsSlicing)
                {
                    StatusBar.OperationFeedback = string.Empty;
                    LogProgressDetail("Starting slice…");
                    ShowBusy("Slicing", "Starting slice…");
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(Viewport.SliceStatusMessage))
                    {
                        StatusBar.OperationFeedback = Viewport.SliceStatusMessage;
                        LogProgressDetail(Viewport.SliceStatusMessage);
                    }
                    _lastProgressLogMessage = string.Empty;
                    HideBusy();
                }
            }

            if (e.PropertyName is nameof(ViewportViewModel.SliceStatusMessage))
            {
                if (Viewport.IsSlicing)
                {
                    LogProgressDetail(Viewport.SliceStatusMessage);
                    UpdateBusy(Viewport.SliceStatusMessage);
                }
                else if (!string.IsNullOrWhiteSpace(Viewport.SliceStatusMessage))
                    StatusBar.OperationFeedback = Viewport.SliceStatusMessage;
            }

            if (e.PropertyName is nameof(ViewportViewModel.SliceProgressPercent) && Viewport.IsSlicing)
                UpdateBusyProgress(Viewport.SliceProgressPercent);

            if (e.PropertyName is nameof(ViewportViewModel.HasSelection)
                                or nameof(ViewportViewModel.HasMeshSelected)
                                or nameof(ViewportViewModel.IsSlicing)
                                or nameof(ViewportViewModel.IsToolpathSelected)
                                or nameof(ViewportViewModel.IsLayFlatMode)
                                or nameof(ViewportViewModel.ToolpathScrubIndex)
                                or nameof(ViewportViewModel.ToolpathScrubMax)
                                or nameof(ViewportViewModel.ToolpathScrubText))
                return;

            OnSettingsChanged();
        };

        RightPanel.Settings.View.PropertyChanged += (_, _) => OnSettingsChanged();
        RightPanel.Additive.PropertyChanged      += (_, e) =>
        {
            if (e.PropertyName is nameof(AdditiveSettingsViewModel.SelectedPresetIndex))
                return;
            OnSettingsChanged();
        };
        RightPanel.Scan.PropertyChanged          += (_, e) =>
        {
            // Skip transient capture-progress properties.
            if (e.PropertyName is nameof(ScanSettingsViewModel.IsScanning)
                                or nameof(ScanSettingsViewModel.ScanStatus)
                                or nameof(ScanSettingsViewModel.QuickPositionStatus))
                return;
            OnSettingsChanged();
        };

        // Wire toolbar commands to cross-panel actions.
        Toolbar.FrameAllRequested       += (_, _) => Viewport.OnFrameAllRequested?.Invoke();
        Toolbar.NewWorkspaceRequested   += (_, _) => NewWorkspace();
        Viewport.OnSaveViewRequested    = SaveCurrentView;
        Viewport.OnToolheadSelected     = () =>
        {
            if (RightPanel.ShowAdditiveTabButton)
                RightPanel.ActiveTab = RightPanelTab.Additive;
            RightPanel.FlashToolheadOrientation();
        };

        Console.Attach(this, new ConsoleCommandContext
        {
            Main = this,
            Log = Console.Log,
            LogError = Console.LogError,
            RequestOpenWorkspacePicker = () => Toolbar.OpenWorkspaceCommand.Execute(null),
            RequestSaveWorkspaceAs = () => Toolbar.SaveWorkspaceAsCommand.Execute(null),
            RequestOpenModelPicker = () => Toolbar.OpenModelCommand.Execute(null),
            RequestImportKrlPicker = () => Toolbar.ImportKrlCommand.Execute(null),
            RequestPreferencesDialog = () => Toolbar.OpenPreferencesCommand.Execute(null),
        });

        // Wire the robot connect button to the robot panel and mirror status to toolbar.
        var robot = RightPanel.Settings.Robot;
        Toolbar.SyncRobotRequested += (_, _) => robot.ConnectCommand.Execute(null);

        RightPanel.Scan.OnTestScanRequested = RunTestScan;
        RightPanel.Scan.OnMoveQuickPositionRequested = name => _ = MoveQuickPositionAsync(name);
        RightPanel.Scan.OnSaveQuickPositionRequested = name => _ = SaveQuickPositionAsync(name);

        // Wire hand-eye calibration: provide the live flange pose and apply result to TCP fields.
        // CRITICAL: calibration must use the SAME flange frame the viewport applies scans in
        // (rendered glTF flange Ã— glTFâ†’KUKA correction), NOT KukaIkSolver.ForwardKinematics.
        // The analytic FK flange and the rendered flange are different frames; feeding the
        // analytic one makes calibration learn the camera in a frame registration never uses,
        // so scans land rotated/translated wrong despite tiny calibration residuals.
        var calib = RightPanel.Scan.Calibration;
        calib.GetFlangeInBase = () =>
            Viewport.GetFlangeInBaseForCalibration?.Invoke() ?? System.Numerics.Matrix4x4.Identity;
        calib.OnApplyCalibration = (x, y, z, a, b, c) =>
        {
            var r = RightPanel.Settings.Robot;
            r.EditTcpX = x;
            r.EditTcpY = y;
            r.EditTcpZ = z;
            r.EditTcpA = a;
            r.EditTcpB = b;
            r.EditTcpC = c;
        };
        calib.OnAutoCalibrateRequested = async () => { await RunAutoScanToolCalibrationAsync(); };
        calib.Log = Console.Log;

        // Wire rotary-bed (E1) calibration: capture the board centroid in world via the
        // (fixed) scanner camera pose, read live E1, and persist the fitted centre to the cell.
        var bedCal = robot.BedCalibration;
        bedCal.GetCameraToWorld = () =>
        {
            if (ResolveCalibratedScannerTool() is not { } scannerTool) return null;
            if (Viewport.GetToolWorldPose?.Invoke(scannerTool) is not { } p) return null;
            // OpenTK Matrix4 (row-vector: rows = camera axes in world, Row3 = origin) â†’ System.Numerics.
            return new System.Numerics.Matrix4x4(
                p.M11, p.M12, p.M13, p.M14,
                p.M21, p.M22, p.M23, p.M24,
                p.M31, p.M32, p.M33, p.M34,
                p.M41, p.M42, p.M43, p.M44);
        };
        bedCal.GetCurrentE1 = () => robot.E1;
        bedCal.OnApplyCenter = (x, y, z, sign) =>
        {
            // Route through the live bed-edit path: updates the scene immediately AND persists.
            robot.ApplyBedCalibration(x, y, z, sign);
            Console.Log($"[bedcal] Applied bed centre ({x:F1}, {y:F1}, {z:F1}), rotation {(sign < 0 ? "CW" : "CCW")}.");
            // Also push the calibrated rotary base back to the controller (live), so coordinated
            // motion matches the model â€” not just the app's cell. Fire-and-forget with logging.
            _ = WriteRotaryBaseToControllerAsync(x, y, z);
        };
        bedCal.OnAutoCalibrateRequested = async () => { await RunAutoBedCalibrationAsync(); };
        bedCal.Log = Console.Log;

        // Swap the displayed end-effector to match the active sidebar tab (non-LFAM 3).
        // LFAM 3 workflow phase buttons own tool selection (print / scan / mill).
        RightPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RightPanelViewModel.ActiveTab)) return;
            if (!Viewport.ShowLfam3ToolPicker)
            {
                var toolName = RightPanel.ActiveTab switch
                {
                    RightPanelTab.Scan     => Viewport.ActiveCell?.ScanToolName,
                    RightPanelTab.Additive => "HV Extruder",
                    _                      => null,
                };
                if (toolName is not null)
                {
                    int idx = robot.ToolNames.IndexOf(toolName);
                    if (idx >= 0) robot.SelectedToolIndex = idx;
                }
            }
            SyncKrlFrameIndicesToActiveTab();
        };
        robot.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RobotPanelViewModel.ConnectionStatus))
            {
                Toolbar.RobotStatus = robot.ConnectionStatus;
                Viewport.NotifyRobotSyncChanged();
            }
        };

        // Propagate KRL frame dropdown / selected tool â†’ export settings for the active tab.
        robot.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RobotPanelViewModel.KrlToolIndex))
                SyncKrlFrameIndicesToActiveTab();
            if (e.PropertyName == nameof(RobotPanelViewModel.KrlBaseIndex))
            {
                if (RightPanel.ActiveTab == RightPanelTab.Additive)
                    RightPanel.Additive.BaseDataIndex = robot.KrlBaseIndex;
                else if (RightPanel.ActiveTab == RightPanelTab.Scan)
                    RightPanel.Scan.BaseDataIndex = robot.KrlBaseIndex;
            }
        };

        // After each cell swap: populate KRL dropdowns and select tool for active tab.
        Viewport.OnCellSwapCompleted = generation =>
        {
            _cellSceneReady = true;
            var cell = Viewport.ActiveCell;
            if (cell is not null)
                robot.SetKrlFrameOptions(
                    cell.EffectiveTools,
                    cell.KrlBases,
                    RightPanel.Scan.ToolDataIndex,
                    RightPanel.Scan.BaseDataIndex);

            // Show/hide the Scan tab based on whether this cell has a scanner.
            RightPanel.HasScanTab = cell?.ScanToolName is not null;

            // Pick the material preset's per-extruder flow rate for this cell's extruder.
            UpdateActiveExtruderType();

            if (!Viewport.ShowLfam3ToolPicker)
            {
                var toolName = RightPanel.ActiveTab switch
                {
                    RightPanelTab.Scan     => cell?.ScanToolName,
                    RightPanelTab.Additive => "HV Extruder",
                    _                      => null,
                };
                if (toolName is not null)
                {
                    int idx = robot.ToolNames.IndexOf(toolName);
                    if (idx >= 0) robot.SelectedToolIndex = idx;
                }
            }

            SyncLfam3WorkflowSidebar();
            SyncKrlFrameIndicesToActiveTab();
            RightPanel.Scan.RefreshQuickPositions(cell);
            Viewport.FlattenScansToBedGroup();

            TryApplyPendingWorkspaceRestore(generation);
        };
    }

    async Task MoveQuickPositionAsync(string waypointName)
    {
        var scan = RightPanel.Scan;
        bool ok = await GoToWaypointAsync(waypointName);
        var label = scan.QuickPositions.FirstOrDefault(p =>
            p.WaypointName.Equals(waypointName, StringComparison.OrdinalIgnoreCase))?.Label
            ?? waypointName;
        scan.QuickPositionStatus = ok
            ? $"At {label}."
            : "Move timed out — check MASSIVE_SERVER / CELL.";
    }

    async Task SaveQuickPositionAsync(string displayName)
    {
        var scan = RightPanel.Scan;
        displayName = (displayName ?? string.Empty).Trim();
        if (displayName.Length == 0)
        {
            scan.QuickPositionStatus = "Enter a name for the position.";
            return;
        }

        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected)
        {
            scan.QuickPositionStatus = "Robot not connected — Sync first.";
            return;
        }

        string slug = ScanSettingsViewModel.SlugifyQuickPositionName(displayName);
        scan.QuickPositionStatus = $"Saving {displayName}…";
        bool ok = await SaveWaypointFromRobotAsync(slug, displayName, [ScanSettingsViewModel.QuickPositionTag]);
        if (!ok)
        {
            scan.QuickPositionStatus = $"Couldn't save {displayName} — see console.";
            return;
        }

        scan.RefreshQuickPositions(Viewport.ActiveCell);
        scan.QuickPositionStatus = $"Saved {displayName}.";
    }

    /// <summary>
    /// Captures a frame from the Zivid camera on a worker thread, meshes the
    /// organized point cloud, and adds the result to the viewport and outliner.
    /// The scan is placed on the bed centre; camera-to-robot registration comes
    /// later with hand-eye calibration.
    /// </summary>
    private async void RunTestScan()
    {
        var scan = RightPanel.Scan;
        if (scan.IsScanning) return;

        scan.IsScanning = true;
        scan.ScanStatus = "Starting capture...";
        try
        {
            var robot = RightPanel.Settings.Robot;
            EnsureCalibratedScannerToolSelected("[scan]");
            ToolCellConfig? scannerTool = ResolveCalibratedScannerTool();

            Matrix4? cameraPose = scannerTool is not null
                ? Viewport.GetToolWorldPose?.Invoke(scannerTool)
                : null;

            if (cameraPose is { } dbgPose)
            {
                Console.Log($"[scan] Tool: {scannerTool?.Name ?? "?"}  TCP: ({scannerTool?.TcpX:F1}, {scannerTool?.TcpY:F1}, {scannerTool?.TcpZ:F1})");
                Console.Log($"[scan] Camera origin  : ({dbgPose.Row3.X:F1}, {dbgPose.Row3.Y:F1}, {dbgPose.Row3.Z:F1}) mm");
                Console.Log($"[scan] Camera Z-axis  : ({dbgPose.Row2.X:F3}, {dbgPose.Row2.Y:F3}, {dbgPose.Row2.Z:F3})");
            }
            else
                Console.Log($"[scan] No camera pose â€” tool={scannerTool?.Name ?? "none"}, flange available={Viewport.GetToolWorldPose is not null}");

            var outDir = scan.OutputDirectory;
            var meta = new ScanMetadata
            {
                A1 = (float)robot.A1, A2 = (float)robot.A2, A3 = (float)robot.A3,
                A4 = (float)robot.A4, A5 = (float)robot.A5, A6 = (float)robot.A6,
                E1 = (float)robot.E1,
                TcpX = (float)robot.EditTcpX, TcpY = (float)robot.EditTcpY, TcpZ = (float)robot.EditTcpZ,
                TcpA = (float)robot.EditTcpA, TcpB = (float)robot.EditTcpB, TcpC = (float)robot.EditTcpC,
                CameraWorldX = cameraPose?.Row3.X ?? 0f,
                CameraWorldY = cameraPose?.Row3.Y ?? 0f,
                CameraWorldZ = cameraPose?.Row3.Z ?? 0f,
            };
            var result = await Task.Run(() => ZividScanService.Capture(
                outDir, meta,
                msg => Dispatcher.UIThread.Post(() => scan.ScanStatus = msg)));

            scan.ScanStatus = $"Meshing {result.ValidPointCount:N0} points...";
            var name = $"Scan {DateTime.Now:HH-mm-ss}";
            var node = await Task.Run(() => PointCloudMesher.Build(
                result.PointsXYZ, result.Width, result.Height, name));

            if (node is null)
            {
                scan.ScanStatus = "Scan contained no meshable points.";
                return;
            }

            node.CullFaces = false;
            if (cameraPose is { } pose)
            {
                // Registered: camera frame â†’ world via robot pose at capture time.
                node.LocalTransform = pose;
                Console.Log("[scan] Registered via robot pose (scanner TOOL frame).");
            }
            else
            {
                // No robot loaded â€” flip the camera frame upright and centre on the bed.
                node.LocalTransform = Matrix4.CreateRotationX(MathF.PI);
                ImportHelper.PlaceOnBed(node, Viewport.ActiveCell);
                Console.Log("[scan] No robot pose available â€” placed scan on bed centre unregistered.");
            }

            // Stash the registered scan's capture-time WORLD points + E1 for the rotary diagnostic
            // export (offline calibration solve). Do it now â€” node.LocalTransform is still the clean
            // cameraâ†’world pose, before AddScanNode reparents it under the E1 pivot.
            if (cameraPose is not null && node.PendingMesh is { } stashMesh)
                Viewport.StashScanDiag(name, (float)robot.E1, stashMesh.Positions, node.LocalTransform);

            // On a rotary cell the scan nests under the turntable group and tracks E1 (so multiple
            // scans at different E1 angles stay registered to one another); else attaches to the root.
            Viewport.AddScanNode(node);
            if (Viewport.IsPrePrintScanRegistrationPhase)
                Viewport.RegisterArmatureScanMesh(node);
            var saved = result.SavedZdfPath is { } p
                ? $", saved {System.IO.Path.GetFileName(p)}{(result.SavedMetadataPath is not null ? " + .json" : "")}"
                : "";
            scan.ScanStatus = $"Added \"{name}\" â€” {result.ValidPointCount:N0} points{saved}";
            Console.Log($"[scan] {scan.ScanStatus}");
        }
        catch (Exception ex)
        {
            scan.ScanStatus = $"Scan failed: {ex.Message}";
            Console.Log($"[scan] ERROR: {ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                Console.Log($"[scan]   inner: {inner.GetType().Name}: {inner.Message}");
        }
        finally
        {
            scan.IsScanning = false;
        }
    }

    /// <summary>Maps a C3 Bridge protocol error code to its name (ulsu-tech C3BI enum).</summary>
    private static string C3ErrorName(int code) => code switch
    {
        0  => "General (E_FAIL)",
        1  => "Success",
        2  => "Access denied",
        3  => "Invalid argument",
        4  => "Memory",
        5  => "Pointer",
        6  => "Unexpected",
        7  => "Not implemented",
        8  => "No interface",
        9  => "Protocol (bad message)",
        10 => "Answer too long",
        _  => $"code {code}",
    };

    /// <summary>
    /// Pushes the calibrated rotary base (centre + fitted axis orientation A/B/C) to the controller's
    /// <c>BASE_DATA[rotary]</c> frame so coordinated motion matches the model â€” not just the app's cell.
    /// Position is the bed centre relative to ROBROOT (BASE_DATA is $WORLD/$ROBROOT-relative). No-op
    /// (logged) when the robot isn't connected or the cell has no rotary base. Fire-and-forget.
    /// </summary>
    private async System.Threading.Tasks.Task WriteRotaryBaseToControllerAsync(float cx, float cy, float cz)
    {
        var robot  = RightPanel.Settings.Robot;
        var bedCal = robot.BedCalibration;

        if (!robot.IsConnected)
        {
            Console.Log("[bedcal] Robot not connected â€” BASE_DATA not written (calibration saved to the cell only).");
            return;
        }
        if (Viewport.ActiveCell is not { } cell)
        {
            Console.Log("[bedcal] No active cell â€” BASE_DATA not written.");
            return;
        }

        KrlBaseEntry? rotary = null;
        foreach (var bse in cell.KrlBases)
            if (bse.Name.IndexOf("Rotary", System.StringComparison.OrdinalIgnoreCase) >= 0) { rotary = bse; break; }
        if (rotary is null)
        {
            Console.Log("[bedcal] Cell has no 'Base Rotary' entry â€” BASE_DATA not written.");
            return;
        }

        // BASE_DATA is $WORLD/$ROBROOT-relative; our centre is in world/ROBROOT mm, so subtract robroot.
        // X/Y follow the calibrated axis centre; Z keeps the modeled rotary base height (the axis-centre
        // fit doesn't measure table height â€” writing the fit's Z would drop the base).
        var rw = cell.Robot.WorldPosition;
        double bz = cell.RotaryBed is { } rbZ && rbZ.BasePos.Length > 2 ? rbZ.BasePos[2] : cz - rw.Z;
        double bx = cx - rw.X, by = cy - rw.Y;
        double a = bedCal.BaseA, b = bedCal.BaseB, c = bedCal.BaseC;

        try
        {
            var echo = await robot.WriteBaseDataAsync(rotary.Index, bx, by, bz, a, b, c);
            Console.Log($"[bedcal] Wrote BASE_DATA[{rotary.Index}] ('{rotary.Name}') = " +
                        $"(X {bx:F2}, Y {by:F2}, Z {bz:F2}, A {a:F3}, B {b:F3}, C {c:F3}) â†’ controller. " +
                        $"Echo: {echo?.Trim()}");
        }
        catch (System.Exception ex)
        {
            Console.Log($"[bedcal] BASE_DATA[{rotary.Index}] write FAILED: {ex.Message} (calibration is still saved to the cell).");
        }
    }

    /// <summary>
    /// Calibration motion runs through MassiveDRIVE + <c>LFAM3_RSI_BulkPTP</c> (not CELL/MS_CMD 1–5).
    /// Requires DRIVE reachable, path executor idle, RSI stream live.
    /// </summary>
    async Task<bool> EnsureMassiveDriveReadyForCalibrationAsync(string logPrefix, Action<string>? setStatus = null)
    {
        var cell = ActiveCellConfig();
        var url = cell?.MassiveDriveUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            string msg = "massiveDriveUrl not set on cell — cannot run cal via MassiveDRIVE.";
            setStatus?.Invoke(msg);
            Console.LogError($"{logPrefix} {msg}");
            return false;
        }

        try
        {
            using var client = new MassiveDriveClient(url, TimeSpan.FromSeconds(6));
            var status = await client.QueryPathStatusAsync();
            if (!status.Reachable)
            {
                string msg = $"MassiveDRIVE unreachable ({status.Detail}) — start massivedrive on 233.";
                setStatus?.Invoke(msg);
                Console.LogError($"{logPrefix} {msg}");
                return false;
            }
            if (status.PathActive)
            {
                string msg =
                    "MassiveDRIVE path is ACTIVE — stop it first (UI Stop or console `drive-stop`), then retry. "
                    + status.Summary;
                setStatus?.Invoke(msg);
                Console.LogError($"{logPrefix} {msg}");
                return false;
            }
            Console.Log($"{logPrefix} MassiveDRIVE ready ({status.Summary}). Pendant: LFAM3_RSI_BulkPTP + AUT + drives ON.");
            return true;
        }
        catch (Exception ex)
        {
            string msg = $"MassiveDRIVE check failed: {ex.Message}";
            setStatus?.Invoke(msg);
            Console.LogError($"{logPrefix} {msg}");
            return false;
        }
    }

    string? MassiveDriveUrlOrNull() => ActiveCellConfig()?.MassiveDriveUrl?.Trim();

    /// <summary>
    /// Joint angles via MassiveDRIVE first (<c>/api/robot</c> axes), then C3 <c>$AXIS_ACT</c>.
    /// </summary>
    async Task<double[]?> ReadAxesForCalAsync(RobotPanelViewModel robot, string logPrefix)
    {
        var url = MassiveDriveUrlOrNull();
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                using var client = new MassiveDriveClient(url, TimeSpan.FromSeconds(8));
                var axes = await client.ReadAxesAsync();
                if (axes is { Length: >= 6 })
                {
                    ApplyAxesToRobotPanel(robot, axes);
                    return axes;
                }
            }
            catch (Exception ex)
            {
                Console.Log($"{logPrefix} Drive axes read failed ({ex.Message}) — trying C3…");
            }
        }

        if (robot.IsConnected)
        {
            try
            {
                var axes = await robot.ReadAxesAsync();
                if (axes is { Length: >= 6 })
                {
                    ApplyAxesToRobotPanel(robot, axes);
                    return axes;
                }
            }
            catch (Exception ex)
            {
                Console.LogError($"{logPrefix} C3 axes read failed: {ex.Message}");
            }
        }

        return null;
    }

    static void ApplyAxesToRobotPanel(RobotPanelViewModel robot, double[] axes)
    {
        robot.A1 = Math.Round(axes[0], 2);
        robot.A2 = Math.Round(axes[1], 2);
        robot.A3 = Math.Round(axes[2], 2);
        robot.A4 = Math.Round(axes[3], 2);
        robot.A5 = Math.Round(axes[4], 2);
        robot.A6 = Math.Round(axes[5], 2);
        if (axes.Length > 6)
            robot.E1 = Math.Round(axes[6], 2);
    }

    /// <summary>
    /// Wait until MassiveDRIVE reports capture-ready (phase waiting_capture + motion_settled)
    /// and joint feedback is stable (esp. E1 for bed cal). Prevents Zivid frames mid-move.
    /// </summary>
    async Task<bool> WaitForDriveCaptureReadyAsync(
        MassiveDriveClient client,
        RobotPanelViewModel robot,
        string logPrefix,
        string? expectedToken,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        double? lastE1 = null;
        int stableReads = 0;
        bool loggedWait = false;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var stDoc = await client.SequenceRunStatusAsync();
                if (!stDoc.RootElement.TryGetProperty("run", out var run))
                {
                    await Task.Delay(100);
                    continue;
                }

                string phase = run.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "" : "";
                if (!string.Equals(phase, "waiting_capture", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!string.IsNullOrEmpty(expectedToken)
                    && run.TryGetProperty("capture_token", out var tokEl))
                {
                    string tok = tokEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(tok) && !string.Equals(tok, expectedToken, StringComparison.Ordinal))
                        return false;
                }

                bool settled = true;
                if (run.TryGetProperty("motion_settled", out var ms))
                {
                    settled = ms.ValueKind == System.Text.Json.JsonValueKind.True
                        || (ms.ValueKind == System.Text.Json.JsonValueKind.Number && ms.GetDouble() != 0);
                }
                if (!settled)
                {
                    if (!loggedWait)
                    {
                        Console.Log($"{logPrefix} Waiting for Drive motion_settled before capture…");
                        loggedWait = true;
                    }
                    stableReads = 0;
                    await Task.Delay(100);
                    continue;
                }

                var axes = await client.ReadAxesAsync();
                if (axes is not { Length: >= 6 })
                {
                    await Task.Delay(100);
                    continue;
                }
                ApplyAxesToRobotPanel(robot, axes);
                double e1 = axes.Length > 6 ? axes[6] : 0;

                if (lastE1 is double le && Math.Abs(e1 - le) <= 0.12)
                    stableReads++;
                else
                    stableReads = 1;
                lastE1 = e1;

                if (stableReads >= 3)
                {
                    await Task.Delay(200);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.Log($"{logPrefix} settle poll: {ex.Message}");
            }

            await Task.Delay(120);
        }

        Console.LogError($"{logPrefix} Timed out waiting for settled capture window.");
        return false;
    }

    /// <summary>Cartesian pose (x,y,z,a,b,c[,e1]) from MassiveDRIVE, else C3 <c>$POS_ACT</c>.</summary>
    async Task<(double X, double Y, double Z, double A, double B, double C, double E1)?> ReadPoseForCalAsync(
        RobotPanelViewModel robot, string logPrefix)
    {
        var url = MassiveDriveUrlOrNull();
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                using var client = new MassiveDriveClient(url, TimeSpan.FromSeconds(8));
                using var doc = await client.RobotStatusAsync(force: true);
                var root = doc.RootElement;
                var robotEl = root.TryGetProperty("robot", out var r) ? r : root;
                if (robotEl.TryGetProperty("pose", out var pose) && pose.ValueKind == JsonValueKind.Object)
                {
                    double G(string k) =>
                        pose.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number
                            ? p.GetDouble() : double.NaN;
                    double x = G("x"), y = G("y"), z = G("z"), a = G("a"), b = G("b"), c = G("c");
                    if (!double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(z))
                    {
                        double e1 = 0;
                        if (robotEl.TryGetProperty("axes", out var axes)
                            && axes.ValueKind == JsonValueKind.Object
                            && axes.TryGetProperty("e1", out var e1p)
                            && e1p.ValueKind == JsonValueKind.Number)
                            e1 = e1p.GetDouble();
                        robot.TcpX = Math.Round(x, 1);
                        robot.TcpY = Math.Round(y, 1);
                        robot.TcpZ = Math.Round(z, 1);
                        return (x, y, z, a, b, c, e1);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Log($"{logPrefix} Drive pose read failed ({ex.Message}) — trying C3…");
            }
        }

        if (robot.IsConnected)
        {
            try
            {
                var posStr = await robot.ReadVarAsync("$POS_ACT");
                var (x, y, z, a, b, c) = KrlVarParser.ParsePosAct(posStr);
                double e1 = 0;
                try
                {
                    var axes = await robot.ReadAxesAsync();
                    if (axes.Length > 6) e1 = axes[6];
                }
                catch { /* optional */ }
                return (x, y, z, a, b, c, e1);
            }
            catch (Exception ex)
            {
                Console.LogError($"{logPrefix} C3 pose read failed: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Cartesian bulk LIN via MassiveDRIVE (<c>MS_CMD=99</c>) — the proven working motion path.
    /// Prefer this over joint PTP (<c>MS_CMD=93</c>) until joint moves are reliable.
    /// </summary>
    async Task<bool> DriveMovePoseAsync(
        double x, double y, double z, double a, double b, double c,
        double? e1, double speedMmS, int tool, int baseIdx, string logPrefix)
    {
        var url = MassiveDriveUrlOrNull();
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.LogError($"{logPrefix} No massiveDriveUrl — cannot bulk move.");
            return false;
        }
        try
        {
            using var client = new MassiveDriveClient(url, TimeSpan.FromMinutes(3));
            // Tight ori tolerance so pure ABC reorients actually complete (default 1° was too loose)
            using var doc = await client.MoveBulkPoseAsync(
                x, y, z, a, b, c, e1, speedMmS, tool, baseIdx,
                waitS: 120, tolMm: 3, tolDeg: 0.6);
            bool ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                string err = doc.RootElement.TryGetProperty("error", out var e)
                    ? e.GetString() ?? doc.RootElement.GetRawText()
                    : doc.RootElement.GetRawText();
                Console.LogError($"{logPrefix} Drive bulk pose failed: {err}");
                return false;
            }
            // Log actual end pose when present
            if (doc.RootElement.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Object)
            {
                double G(string k) => to.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : double.NaN;
                Console.Log($"{logPrefix} bulk at ({G("x"):F1},{G("y"):F1},{G("z"):F1}) " +
                            $"ABC=({G("a"):F1},{G("b"):F1},{G("c"):F1})");
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.LogError($"{logPrefix} Drive bulk pose error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Relative bulk jump (dx/dy/dz mm) via MassiveDRIVE <c>MS_CMD=99</c>.</summary>
    async Task<bool> DriveBulkDeltaAsync(
        double dx, double dy, double dz, double speedMmS, string logPrefix)
    {
        var url = MassiveDriveUrlOrNull();
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.LogError($"{logPrefix} No massiveDriveUrl.");
            return false;
        }
        try
        {
            using var client = new MassiveDriveClient(url, TimeSpan.FromMinutes(3));
            using var doc = await client.MoveBulkDeltaAsync(dx, dy, dz, speedMmS);
            bool ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                string err = doc.RootElement.TryGetProperty("error", out var e)
                    ? e.GetString() ?? doc.RootElement.GetRawText()
                    : doc.RootElement.GetRawText();
                Console.LogError($"{logPrefix} Drive bulk delta failed: {err}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.LogError($"{logPrefix} Drive bulk delta error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Joint PTP via MassiveDRIVE <c>POST /api/motion/axes</c> → BulkPTP <c>MS_CMD=93</c>
    /// (<c>SPTP MS_AXIS</c>). Required for scanner hand-eye wrist nutation (legacy path).
    /// </summary>
    async Task<bool> DriveMoveAxesAsync(
        double a1, double a2, double a3, double a4, double a5, double a6, double e1,
        int velPct, int tool, int baseIdx, string logPrefix)
    {
        var url = MassiveDriveUrlOrNull();
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.LogError($"{logPrefix} No massiveDriveUrl — cannot joint-move.");
            return false;
        }
        try
        {
            using var client = new MassiveDriveClient(url, TimeSpan.FromMinutes(3));
            using var doc = await client.MoveAxesAsync(
                a1, a2, a3, a4, a5, a6, e1, velPct, tool, baseIdx,
                tolDeg: 0.6, waitS: 90);
            bool ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                string err = doc.RootElement.TryGetProperty("error", out var e)
                    ? e.GetString() ?? doc.RootElement.GetRawText()
                    : doc.RootElement.GetRawText();
                Console.LogError($"{logPrefix} Drive joint move failed: {err}");
                return false;
            }
            if (doc.RootElement.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Object)
            {
                double G(string k, double fb) =>
                    to.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number
                        ? p.GetDouble() : fb;
                ApplyAxesToRobotPanel(RightPanel.Settings.Robot, new[]
                {
                    G("a1", a1), G("a2", a2), G("a3", a3),
                    G("a4", a4), G("a5", a5), G("a6", a6),
                    G("e1", e1),
                });
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.LogError($"{logPrefix} Drive joint move error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Automated rotary bed calibration.
    /// <b>Motion master = MassiveDRIVE Movements</b> (sequence "Bed Calibration":
    /// fixed scanner TCP + taught E1 per waypoint). SLICER does not need local E1
    /// schedules — it follows Drive and captures when waypoint notes request bed
    /// (<c>bed</c> / <c>bedscan</c>). Same handshake as scan-cal (<c>waiting_capture</c>
    /// + capture-ack). After the movement, fits centre from board samples and
    /// estimates rotation phase from surface clouds.
    /// </summary>
    /// <returns><c>true</c> when a bed centre fit was applied (or already had a usable result).</returns>
    private async Task<bool> RunAutoBedCalibrationAsync()
    {
        var robot   = RightPanel.Settings.Robot;
        var bedCal  = robot.BedCalibration;
        var scanCal = RightPanel.Scan.Calibration;
        var scan    = RightPanel.Scan;

        if (bedCal.IsAutoRunning)
        {
            Console.LogError("[bedcal] Auto calibration already running — wait for it to finish.");
            return false;
        }
        if (scanCal.IsAutoRunning)
        {
            Console.LogError("[bedcal] scan-cal is still running — wait for `=== Done ===` before bed-cal.");
            return false;
        }
        if (scan.IsScanning)
        {
            Console.LogError("[bedcal] A Zivid capture is in progress — wait for it to finish.");
            return false;
        }
        if (!await EnsureMassiveDriveReadyForCalibrationAsync("[bedcal]", bedCal.SetStatus))
            return false;

        if (!robot.IsConnected)
            Console.Log("[bedcal] C3 not synced — using MassiveDRIVE for motion + joint feedback.");

        bedCal.SetAutoRunning(true);
        if (robot.IsConnected)
            robot.PauseStreaming();
        int captured = 0;
        bool applied = false;
        var url = MassiveDriveUrlOrNull();
        var phaseClouds = new List<(double E1, float[] World, float YOffsetMm)>();
        try
        {
            Console.Log("[bedcal] === AUTO BED CAL (MassiveDRIVE Movements master) ===");
            Console.Log("[bedcal] Coordinates/E1 live on MassiveDRIVE — not in this SLICER install.");
            Console.Log("[bedcal] Capture trigger: waypoint notes containing 'bed' (or bedscan / bed-cal).");
            Console.Log("[bedcal] Pendant: LFAM3_RSI_BulkPTP, AUT, drives ON, path idle. Board off-centre on bed.");

            if (string.IsNullOrWhiteSpace(url))
            {
                bedCal.SetStatus("massiveDriveUrl not set on cell — cannot follow Drive movements.");
                Console.LogError("[bedcal] No massiveDriveUrl.");
                return false;
            }

            Viewport.ClearScanDiag();
            bedCal.ClearSamples();
            EnsureCalibratedScannerToolSelected("[bedcal]");

            using var client = new MassiveDriveClient(url, TimeSpan.FromMinutes(5));
            string? sequenceId = null;
            string sequenceName = MassiveDriveWaypointNotes.BedCalibrationSequenceName;

            // Prefer attaching to a movement already started on the Drive UI
            using (var st0 = await client.SequenceRunStatusAsync())
            {
                if (st0.RootElement.TryGetProperty("run", out var run0)
                    && run0.TryGetProperty("active", out var act0) && act0.GetBoolean())
                {
                    sequenceId = run0.TryGetProperty("sequence_id", out var sid) ? sid.GetString() : null;
                    sequenceName = run0.TryGetProperty("name", out var nm) ? nm.GetString() ?? sequenceName : sequenceName;
                    Console.Log($"[bedcal] Attaching to active Drive movement \"{sequenceName}\" ({sequenceId}) — DRIVE is master.");
                    bedCal.SetStatus($"Following active Drive movement: {sequenceName}…");
                }
            }

            if (sequenceId is null)
            {
                using var listDoc = await client.ListSequencesAsync();
                if (!listDoc.RootElement.TryGetProperty("sequences", out var seqs)
                    || seqs.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    bedCal.SetStatus("MassiveDRIVE returned no Movements — teach Bed Calibration on Drive first.");
                    Console.LogError("[bedcal] GET /api/sequences missing sequences[] — teach on MassiveDRIVE.");
                    return false;
                }

                foreach (var s in seqs.EnumerateArray())
                {
                    string name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Equals(MassiveDriveWaypointNotes.BedCalibrationSequenceName,
                            StringComparison.OrdinalIgnoreCase)
                        || name.Contains("bed cal", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Bed Scan", StringComparison.OrdinalIgnoreCase))
                    {
                        sequenceId = s.TryGetProperty("sequence_id", out var id) ? id.GetString() : null;
                        sequenceName = name;
                        break;
                    }
                }

                // Prefer not to steal Scanner Calibration if name match failed
                if (sequenceId is null)
                {
                    foreach (var s in seqs.EnumerateArray())
                    {
                        string name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.Contains("scanner", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (name.Contains("bed", StringComparison.OrdinalIgnoreCase))
                        {
                            sequenceId = s.TryGetProperty("sequence_id", out var id) ? id.GetString() : null;
                            sequenceName = name;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(sequenceId))
                {
                    bedCal.SetStatus("No Bed Calibration movement on MassiveDRIVE — create it there first.");
                    Console.LogError("[bedcal] No bed movement found. Teach waypoints (with e1) + notes=bed on MassiveDRIVE.");
                    return false;
                }

                Console.Log($"[bedcal] Starting Drive movement \"{sequenceName}\" ({sequenceId}) as master…");
                bedCal.SetStatus($"Starting Drive movement: {sequenceName}…");
                using var startDoc = await client.StartSequenceAsync(sequenceId, async: true);
                bool startedOk = startDoc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                bool startedFlag = startDoc.RootElement.TryGetProperty("started", out var stEl) && stEl.GetBoolean();
                if (!startedOk && !startedFlag)
                {
                    string err = startDoc.RootElement.TryGetProperty("error", out var e)
                        ? e.GetString() ?? startDoc.RootElement.GetRawText()
                        : startDoc.RootElement.GetRawText();
                    bedCal.SetStatus($"Could not start Drive movement: {err}");
                    Console.LogError($"[bedcal] Start sequence failed: {err}");
                    return false;
                }
            }

            bedCal.SetStatus($"Following Drive movement \"{sequenceName}\" — capturing on notes=bed…");
            Console.Log("[bedcal] Listening for waiting_capture (notes=bed). Board sample + surface at each stop.");

            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(40);
            string lastPhase = "";
            while (DateTime.UtcNow < deadline)
            {
                using var stDoc = await client.SequenceRunStatusAsync();
                if (!stDoc.RootElement.TryGetProperty("run", out var run))
                {
                    await Task.Delay(200);
                    continue;
                }

                string phase = run.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "" : "";
                bool active = run.TryGetProperty("active", out var act) && act.GetBoolean();
                int stepIdx = run.TryGetProperty("step_index", out var si) && si.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? si.GetInt32() : -1;
                int stepsTotal = run.TryGetProperty("steps_total", out var stt) && stt.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? stt.GetInt32() : 0;
                string wpName = run.TryGetProperty("waypoint_name", out var wn) ? wn.GetString() ?? "" : "";
                string notes = run.TryGetProperty("notes", out var no) ? no.GetString() ?? "" : "";

                if (!string.Equals(phase, lastPhase, StringComparison.Ordinal))
                {
                    Console.Log($"[bedcal] Drive phase={phase} step {stepIdx + 1}/{stepsTotal} {wpName}" +
                                (string.IsNullOrEmpty(notes) ? "" : $" notes={notes}"));
                    lastPhase = phase;
                    bedCal.SetStatus($"Drive: {phase} · step {stepIdx + 1}/{stepsTotal} · {wpName}" +
                                     (string.IsNullOrEmpty(notes) ? "" : $" · {notes}"));
                }

                // Only after Drive finished motion (never during moving/settling)
                bool captureWindow =
                    string.Equals(phase, "waiting_capture", StringComparison.OrdinalIgnoreCase);

                // Only bed notes — never treat hand-eye 'scan' as a bed sample
                if (captureWindow && MassiveDriveWaypointNotes.RequestsBed(notes))
                {
                    string token = run.TryGetProperty("capture_token", out var tok)
                        ? tok.GetString() ?? "" : "";
                    string key = string.IsNullOrEmpty(token)
                        ? $"{stepIdx}:{wpName}:{notes}"
                        : token;
                    if (!seenTokens.Contains(key))
                    {
                        bool ready = await WaitForDriveCaptureReadyAsync(
                            client, robot, "[bedcal]",
                            string.IsNullOrEmpty(token) ? null : token,
                            TimeSpan.FromSeconds(60));
                        if (ready && seenTokens.Add(key))
                        {
                        Console.Log($"[bedcal] Settled @ {wpName} (notes={notes}, E1={robot.E1:F1}°) — board + surface…");

                        int before = bedCal.SampleCount;
                        await bedCal.AddSampleAsync();
                        if (bedCal.SampleCount > before)
                        {
                            captured++;
                            Console.Log($"[bedcal] Board sample {captured} at {wpName} (E1={robot.E1:F1}°).");
                        }
                        else
                        {
                            Console.Log($"[bedcal] Board not detected at {wpName}: {bedCal.Status}");
                        }

                        // Surface cloud for rotation-phase estimate (same as legacy E1 sweep)
                        try
                        {
                            if (bedCal.GetCameraToWorld?.Invoke() is { } camW)
                            {
                                double e1 = robot.E1;
                                var sres = await Task.Run(() => ZividScanService.Capture(null, null, null));
                                var (world, valid) = ScanPointCloudTransform.ToWorld(sres.PointsXYZ, camW);
                                phaseClouds.Add((e1, world, 0f));
                                Viewport.StashScanDiagWorld($"bedcal_drive_E1_{e1:F0}", (float)e1, world);
                                Console.Log($"[bedcal] Surface scan @ E1={e1:F1}° ({valid:N0} pts).");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Log($"[bedcal] Surface scan skipped @ {wpName}: {ex.Message}");
                        }

                        try
                        {
                            await client.SequenceCaptureAckAsync(string.IsNullOrEmpty(token) ? null : token);
                        }
                        catch (Exception ackEx)
                        {
                            Console.Log($"[bedcal] capture-ack: {ackEx.Message}");
                        }
                        }
                    }
                }
                else if (captureWindow && MassiveDriveWaypointNotes.RequestsScan(notes)
                         && !MassiveDriveWaypointNotes.RequestsBed(notes))
                {
                    // Drive is waiting for scan-cal; do not steal the dwell — wait only
                    await Task.Delay(150);
                    continue;
                }

                if (!active && phase is "done" or "error" or "stopped" or "idle")
                {
                    if (phase is "error" or "stopped")
                    {
                        string err = run.TryGetProperty("error", out var er) ? er.GetString() ?? phase : phase;
                        Console.LogError($"[bedcal] Drive movement ended: {err}");
                    }
                    else
                        Console.Log($"[bedcal] Drive movement finished (phase={phase}).");
                    break;
                }

                await Task.Delay(150);
            }

            if (captured >= 3)
            {
                Console.Log($"[bedcal] Fitting centre from {captured} board samples, {phaseClouds.Count} surface scans…");
                bedCal.SetStatus($"Fitting centre from {captured} samples…");
                bedCal.Compute();
                if (bedCal.HasResult)
                {
                    Console.Log($"[bedcal] Centre ({bedCal.CenterX:F1}, {bedCal.CenterY:F1}, {bedCal.CenterZ:F1}) mm, " +
                                $"R {bedCal.Radius:F0} mm, residual {bedCal.Residual:F2} mm, " +
                                $"rotation {(bedCal.RotationSign < 0 ? "CW" : "CCW")}.");
                    bedCal.Apply();
                    applied = true;
                    Console.Log("[bedcal] Centre + rotation sign applied. Estimating rotation phase…");
                    if (phaseClouds.Count >= 2)
                        await EstimateAndApplyBedPhaseAsync(phaseClouds, bedCal);
                    else
                        Console.Log("[bedcal] Too few surface scans for rotation-phase estimate.");
                }
                else
                {
                    Console.Log($"[bedcal] Captured {captured} samples but the fit failed — not applied.");
                }
            }
            else
            {
                Console.Log($"[bedcal] Ended with {captured} samples (need >=3) — nothing applied.");
                bedCal.SetStatus($"Bed-cal ended with {captured} samples (need >=3). Check notes=bed + board on bed.");
            }

            if (Viewport.ScanDiagCount > 0)
                Console.Log($"[bedcal] {Viewport.ScanDiagCount} surface scans stashed — run `diag-scans` to export.");
            return applied;
        }
        catch (Exception ex)
        {
            bedCal.SetStatus($"Auto-cal error: {ex.Message}");
            Console.LogError($"[bedcal] ERROR: {ex.GetType().Name}: {ex.Message}");
            try
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    using var client = new MassiveDriveClient(url, TimeSpan.FromSeconds(8));
                    await client.SequenceRunStopAsync();
                }
            }
            catch { /* best-effort stop */ }
            return false;
        }
        finally
        {
            if (robot.IsConnected)
                robot.ResumeStreaming();
            bedCal.SetAutoRunning(false);
            Console.Log("[bedcal] === Done (MassiveDRIVE Movements master) ===");
        }
    }

    /// <summary>
    /// Estimates the rotary bed's constant orientation phase (model vs reality) from the surface scans
    /// captured during the sweep â€” by un-rotating each to E1=0 about the fitted centre and fitting the
    /// hole-lattice angle (<see cref="RotaryPhaseEstimator"/>) â€” and applies it as the bed orientation
    /// offset. Assumes the model bed's holes are world-axis-aligned (true for LFAM 3, measured +0.01Â°);
    /// the scan grid's deviation from world is the offset. Bounded to Â±5Â° (~1in) as a sanity gate.
    /// </summary>
    private async Task EstimateAndApplyBedPhaseAsync(List<(double E1, float[] World, float YOffsetMm)> clouds, RotaryBedCalibrationViewModel bedCal)
    {
        // Only the primary (Y0) vantage — offset Y positions skew registration and pollute the lattice fit.
        var phaseClouds = clouds.Where(c => Math.Abs(c.YOffsetMm) < 1f).ToList();
        if (phaseClouds.Count < clouds.Count)
            Console.Log($"[bedcal] Rotation phase: using {phaseClouds.Count}/{clouds.Count} surface scans (Y0 vantage only).");

        double cx = bedCal.CenterX, cy = bedCal.CenterY;
        double sign = bedCal.RotationSign != 0 ? bedCal.RotationSign : -1;

        // Dominant top-plane Z (the dense flat bed top) via a coarse pooled histogram.
        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var (_, w, _) in phaseClouds)
            for (int i = 2; i < w.Length; i += 3)
                if (!float.IsNaN(w[i])) { if (w[i] < zmin) zmin = w[i]; if (w[i] > zmax) zmax = w[i]; }
        if (phaseClouds.Count < 2 || zmax <= zmin)
        {
            Console.Log("[bedcal] Rotation phase: not enough Y0 surface scans — orientation offset unchanged.");
            return;
        }
        const int bins = 200; var hist = new int[bins]; double bw = (zmax - zmin) / bins;
        foreach (var (_, w, _) in phaseClouds)
            for (int i = 2; i < w.Length; i += 3)
                if (!float.IsNaN(w[i])) { int b = (int)((w[i] - zmin) / bw); hist[Math.Clamp(b, 0, bins - 1)]++; }
        int pk = 0; for (int b = 1; b < bins; b++) if (hist[b] > hist[pk]) pk = b;
        double ztop = zmin + (pk + 0.5) * bw;

        // Un-rotate each cloud to E1=0 about the centre, keep the top band, project to plan.
        var plan = new List<(double, double)>();
        const double r2d = Math.PI / 180.0;
        foreach (var (e1, w, _) in phaseClouds)
        {
            double ang = sign * e1 * r2d, ca = Math.Cos(-ang), sa = Math.Sin(-ang);
            for (int i = 0; i + 2 < w.Length; i += 3)
            {
                float x = w[i], y = w[i + 1], z = w[i + 2];
                if (float.IsNaN(x) || z < ztop - 30 || z > ztop + 8) continue;
                double dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > 880 * 880) continue;
                plan.Add((ca * dx - sa * dy, sa * dx + ca * dy));
            }
        }

        var scanAngle = RotaryPhaseEstimator.HoleLatticeAngleDeg(plan, out int hc);
        if (scanAngle is null || double.IsNaN(scanAngle.Value))
        {
            Console.Log($"[bedcal] Rotation phase: hole pattern not detected ({hc} holes, {plan.Count} pts) â€” orientation offset unchanged.");
            return;
        }
        // Model holes are world-aligned, so the scan grid's deviation is the misalignment. Apply the
        // measured angle directly (+offset was wrong on LFAM 3; negative values are valid).
        double measured = scanAngle.Value;
        double phase = measured;
        if (Math.Abs(phase) > 5.0)
        {
            Console.Log($"[bedcal] Rotation phase {phase:F2}Â° exceeds the Â±5Â° (~1in) sanity bound â€” NOT applied (holes {hc}). Re-scan with more bed coverage.");
            return;
        }
        Console.Log($"[bedcal] Rotation phase: bed grid measured {measured:+0.000;-0.000}Â° from model â†’ applying offset {phase:+0.000;-0.000}Â° ({hc} holes, {plan.Count} pts).");
        Console.Log($"[bedcal] {SetBedOrientationOffset((float)phase)}");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Automated 3D scan-tool (hand-eye) calibration.
    /// <b>Motion master = MassiveDRIVE Movements</b> (taught waypoints; sequence
    /// "Scanner Calibration"). SLICER does not need local cal coordinates — it
    /// loads the sequence from Drive and captures when waypoint notes request a
    /// scan (<c>scan</c> / <c>capture</c> / <c>slicer:scan</c>).
    /// </summary>
    /// <returns><c>true</c> when hand-eye result was computed and saved.</returns>
    private async Task<bool> RunAutoScanToolCalibrationAsync()
    {
        var robot   = RightPanel.Settings.Robot;
        var scanCal = RightPanel.Scan.Calibration;

        if (scanCal.IsAutoRunning)
        {
            Console.LogError("[scancal] Auto calibration already running — wait for it to finish (or restart the app if stuck).");
            return false;
        }
        if (robot.BedCalibration.IsAutoRunning)
        {
            Console.LogError("[scancal] bed-cal is running — wait for it to finish before scan-cal.");
            return false;
        }

        if (!await EnsureMassiveDriveReadyForCalibrationAsync("[scancal]", scanCal.SetStatus))
            return false;

        if (!robot.IsConnected)
            Console.Log("[scancal] C3 not synced — flange FK uses Drive axes; `sync` improves live feedback.");

        scanCal.SetAutoRunning(true);
        if (robot.IsConnected)
            robot.PauseStreaming();
        int captured = 0;
        bool applied = false;
        var url = MassiveDriveUrlOrNull();
        try
        {
            Console.Log("[scancal] === AUTO-CALIBRATE SCAN TOOL (MassiveDRIVE Movements master) ===");
            Console.Log("[scancal] Coordinates live on MassiveDRIVE — not in this SLICER install.");
            Console.Log("[scancal] Capture trigger: waypoint notes containing 'scan' (or capture / slicer:scan).");
            Console.Log("[scancal] Pendant: LFAM3_RSI_BulkPTP, AUT, drives ON, path idle.");

            if (string.IsNullOrWhiteSpace(url))
            {
                scanCal.SetStatus("massiveDriveUrl not set on cell — cannot follow Drive movements.");
                Console.LogError("[scancal] No massiveDriveUrl.");
                return false;
            }

            int calTool = ScanToolCalSweep.CalToolIndex;
            int resultTool = ScanToolCalSweep.ResultToolIndex;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (robot.SelectToolByKrlIndex(calTool))
                    Console.Log($"[scancal] UI tool → #{calTool} (uncalibrated scan tool for hand-eye).");
            });

            scanCal.ClearForAuto();
            scanCal.SetStatus("MassiveDRIVE: loading Scanner Calibration movement…");

            using var client = new MassiveDriveClient(url, TimeSpan.FromMinutes(5));
            string? sequenceId = null;
            string sequenceName = MassiveDriveWaypointNotes.ScannerCalibrationSequenceName;

            // Prefer attaching to a movement already started on the Drive UI
            using (var st0 = await client.SequenceRunStatusAsync())
            {
                if (st0.RootElement.TryGetProperty("run", out var run0)
                    && run0.TryGetProperty("active", out var act0) && act0.GetBoolean())
                {
                    sequenceId = run0.TryGetProperty("sequence_id", out var sid) ? sid.GetString() : null;
                    sequenceName = run0.TryGetProperty("name", out var nm) ? nm.GetString() ?? sequenceName : sequenceName;
                    Console.Log($"[scancal] Attaching to active Drive movement \"{sequenceName}\" ({sequenceId}) — DRIVE is master.");
                    scanCal.SetStatus($"Following active Drive movement: {sequenceName}…");
                }
            }

            if (sequenceId is null)
            {
                using var listDoc = await client.ListSequencesAsync();
                if (!listDoc.RootElement.TryGetProperty("sequences", out var seqs)
                    || seqs.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    scanCal.SetStatus("MassiveDRIVE returned no Movements — teach Scanner Calibration on Drive first.");
                    Console.LogError("[scancal] GET /api/sequences missing sequences[] — teach on MassiveDRIVE.");
                    return false;
                }

                foreach (var s in seqs.EnumerateArray())
                {
                    string name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Equals(MassiveDriveWaypointNotes.ScannerCalibrationSequenceName,
                            StringComparison.OrdinalIgnoreCase)
                        || name.Contains("scanner cal", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("scan cal", StringComparison.OrdinalIgnoreCase))
                    {
                        sequenceId = s.TryGetProperty("sequence_id", out var id) ? id.GetString() : null;
                        sequenceName = name;
                        break;
                    }
                }

                if (sequenceId is null && seqs.GetArrayLength() > 0)
                {
                    var s0 = seqs[0];
                    sequenceId = s0.TryGetProperty("sequence_id", out var id) ? id.GetString() : null;
                    sequenceName = s0.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                    Console.Log($"[scancal] No named Scanner Calibration — using first Drive movement \"{sequenceName}\".");
                }

                if (string.IsNullOrEmpty(sequenceId))
                {
                    scanCal.SetStatus("No Movements on MassiveDRIVE — create Scanner Calibration there first.");
                    Console.LogError("[scancal] Empty sequences list. Teach waypoints + movement on MassiveDRIVE (notes=scan on capture poses).");
                    return false;
                }

                Console.Log($"[scancal] Starting Drive movement \"{sequenceName}\" ({sequenceId}) as master…");
                scanCal.SetStatus($"Starting Drive movement: {sequenceName}…");
                using var startDoc = await client.StartSequenceAsync(sequenceId, async: true);
                bool startedOk = startDoc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                bool startedFlag = startDoc.RootElement.TryGetProperty("started", out var stEl) && stEl.GetBoolean();
                if (!startedOk && !startedFlag)
                {
                    string err = startDoc.RootElement.TryGetProperty("error", out var e)
                        ? e.GetString() ?? startDoc.RootElement.GetRawText()
                        : startDoc.RootElement.GetRawText();
                    scanCal.SetStatus($"Could not start Drive movement: {err}");
                    Console.LogError($"[scancal] Start sequence failed: {err}");
                    return false;
                }
            }

            scanCal.SetStatus($"Following Drive movement \"{sequenceName}\" — capturing on notes=scan…");
            Console.Log("[scancal] Listening for waiting_capture (notes=scan). No local pose list used.");

            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(40);
            string lastPhase = "";
            while (DateTime.UtcNow < deadline)
            {
                using var stDoc = await client.SequenceRunStatusAsync();
                if (!stDoc.RootElement.TryGetProperty("run", out var run))
                {
                    await Task.Delay(200);
                    continue;
                }

                string phase = run.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "" : "";
                bool active = run.TryGetProperty("active", out var act) && act.GetBoolean();
                int stepIdx = run.TryGetProperty("step_index", out var si) && si.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? si.GetInt32() : -1;
                int stepsTotal = run.TryGetProperty("steps_total", out var stt) && stt.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? stt.GetInt32() : 0;
                string wpName = run.TryGetProperty("waypoint_name", out var wn) ? wn.GetString() ?? "" : "";
                string notes = run.TryGetProperty("notes", out var no) ? no.GetString() ?? "" : "";

                if (!string.Equals(phase, lastPhase, StringComparison.Ordinal))
                {
                    Console.Log($"[scancal] Drive phase={phase} step {stepIdx + 1}/{stepsTotal} {wpName}" +
                                (string.IsNullOrEmpty(notes) ? "" : $" notes={notes}"));
                    lastPhase = phase;
                    scanCal.SetStatus($"Drive: {phase} · step {stepIdx + 1}/{stepsTotal} · {wpName}" +
                                      (string.IsNullOrEmpty(notes) ? "" : $" · {notes}"));
                }

                // Only after Drive finished motion (never during moving/settling)
                bool scanCaptureWindow =
                    string.Equals(phase, "waiting_capture", StringComparison.OrdinalIgnoreCase)
                    && MassiveDriveWaypointNotes.RequestsScan(notes);

                if (scanCaptureWindow)
                {
                    string token = run.TryGetProperty("capture_token", out var tok)
                        ? tok.GetString() ?? "" : "";
                    string key = string.IsNullOrEmpty(token)
                        ? $"{stepIdx}:{wpName}:{notes}"
                        : token;
                    if (!seenTokens.Contains(key))
                    {
                        bool ready = await WaitForDriveCaptureReadyAsync(
                            client, robot, "[scancal]",
                            string.IsNullOrEmpty(token) ? null : token,
                            TimeSpan.FromSeconds(60));
                        if (ready && seenTokens.Add(key))
                        {
                        Console.Log($"[scancal] Settled @ {wpName} (notes={notes}) — Zivid hand-eye pose…");
                        bool inFrame = await scanCal.CapturePoseAutoAsync();
                        if (inFrame)
                        {
                            captured++;
                            Console.Log($"[scancal] Captured pose {captured} at {wpName}.");
                        }
                        else
                        {
                            Console.Log($"[scancal] Board not in frame at {wpName}: {scanCal.LastCaptureStatus}");
                        }

                        try
                        {
                            await client.SequenceCaptureAckAsync(string.IsNullOrEmpty(token) ? null : token);
                        }
                        catch (Exception ackEx)
                        {
                            Console.Log($"[scancal] capture-ack: {ackEx.Message}");
                        }
                        }
                    }
                }

                if (!active && phase is "done" or "error" or "stopped" or "idle")
                {
                    if (phase is "error" or "stopped")
                    {
                        string err = run.TryGetProperty("error", out var er) ? er.GetString() ?? phase : phase;
                        Console.LogError($"[scancal] Drive movement ended: {err}");
                    }
                    else
                        Console.Log($"[scancal] Drive movement finished (phase={phase}).");
                    break;
                }

                await Task.Delay(150);
            }

            if (captured >= 3)
            {
                scanCal.SetStatus($"Computing hand-eye from {captured} Drive-triggered poses…");
                Console.Log($"[scancal] Hand-eye fit ({captured} poses) → save to tool #{resultTool}…");
                await scanCal.ComputeCalibrationAsync();
                if (scanCal.HasResult)
                {
                    await ApplyScanCalibrationResultAsync(robot, scanCal, resultTool);
                    applied = true;
                }
                else
                {
                    Console.Log($"[scancal] Captured {captured} poses but the hand-eye fit failed — not applied.");
                }
            }
            else
            {
                Console.Log($"[scancal] Ended with {captured} poses (need >=3) — nothing computed.");
                scanCal.SetStatus($"Scan-cal ended with {captured} poses (need >=3). Check notes=scan on Drive waypoints + board in frame.");
            }
            return applied;
        }
        catch (Exception ex)
        {
            scanCal.SetStatus($"Auto scan-cal error: {ex.Message}");
            Console.LogError($"[scancal] ERROR: {ex.GetType().Name}: {ex.Message}");
            try
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    using var client = new MassiveDriveClient(url, TimeSpan.FromSeconds(8));
                    await client.SequenceRunStopAsync();
                }
            }
            catch { /* best-effort stop */ }
            return false;
        }
        finally
        {
            if (robot.IsConnected)
                robot.ResumeStreaming();
            scanCal.SetAutoRunning(false);
            Console.Log("[scancal] === Done (MassiveDRIVE Movements master) ===");
        }
    }

    /// <summary>Legacy CELL tool select (unused by Drive scan-cal).</summary>
    async Task<bool> ScanCalActivateToolAsync(
        RobotPanelViewModel robot, ScanCalibrationViewModel scanCal, int tool, int baseIdx)
    {
        scanCal.SetStatus($"Activating tool #{tool} on controller…");
        await robot.InitCommandServerAsync();

        bool frameOk = await robot.SetFrameAsync(tool, baseIdx, timeoutMs: 30000);
        if (!frameOk)
        {
            scanCal.SetStatus($"Couldn't activate tool #{tool} — is CELL selected, AUTO, drives on?");
            Console.LogError($"[scancal] SetFrame tool #{tool} base #{baseIdx} timed out — check CELL / AUTO / drives.");
            return false;
        }

        string actTool = (await robot.ReadVarAsync("$ACT_TOOL")).Trim();
        Console.Log($"[scancal] Controller $ACT_TOOL={actTool} (expect {tool}).");

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (robot.SelectToolByKrlIndex(tool))
                Console.Log($"[scancal] UI tool → #{tool} (uncalibrated scan tool for hand-eye sweep).");
            else
                Console.Log($"[scancal] WARNING: tool #{tool} not in cell tool list — FK may not match controller.");
        });

        return true;
    }

    /// <summary>
    /// Persists hand-eye TCP to tool #6 in the active cell JSON (tcpX/Y/Z, tcpA/B/C), applies it
    /// live in the viewport, and reloads the cell scene.
    /// </summary>
    async Task ApplyScanCalibrationResultAsync(
        RobotPanelViewModel robot, ScanCalibrationViewModel scanCal, int resultToolKrlIndex)
    {
        double x = scanCal.ResultX, y = scanCal.ResultY, z = scanCal.ResultZ;
        double a = scanCal.ResultA, b = scanCal.ResultB, c = scanCal.ResultC;

        if (Viewport.ActiveCellPath is not { } cellPath)
        {
            scanCal.SetStatus("Calibration computed but no active cell — load LFAM 3 first.");
            Console.LogError("[scancal] No ActiveCellPath — TCP not written to JSON. Run `cell LFAM 3` then re-calibrate.");
            return;
        }

        if (!CellLoader.TrySaveToolTcp(cellPath, resultToolKrlIndex,
                (float)x, (float)y, (float)z, (float)a, (float)b, (float)c, out var saveErr,
                mirrorSensorOrigin: true))
        {
            scanCal.SetStatus($"Couldn't save tool #{resultToolKrlIndex} TCP: {saveErr}");
            Console.LogError($"[scancal] JSON save FAILED ({System.IO.Path.GetFileName(cellPath)}): {saveErr}");
            return;
        }

        Console.Log($"[scancal] Saved tool #{resultToolKrlIndex} TCP to {cellPath}: " +
                    $"tcpX={x:F2} tcpY={y:F2} tcpZ={z:F2} tcpA={a:F3} tcpB={b:F3} tcpC={c:F3} " +
                    $"(rot residual {scanCal.ResidualRot:F3}°, trans {scanCal.ResidualTrans:F3} mm).");

        MassiveSlicer.App.CellSceneCache.Invalidate(cellPath);

        bool wentLive = false;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (robot.SelectToolByKrlIndex(resultToolKrlIndex))
            {
                robot.ApplyTcpOffset(x, y, z, a, b, c);
                wentLive = true;
            }
        });

        Viewport.ActiveCell = CellLoader.Load(cellPath);
        Viewport.OnDevCellReloadRequested?.Invoke(cellPath);

        string howApplied = wentLive
            ? $"Calibrated ✓ — tool #{resultToolKrlIndex} TCP saved to JSON and live."
            : $"Calibrated ✓ — tool #{resultToolKrlIndex} TCP saved to JSON (select Scanner / tool {resultToolKrlIndex} to view).";
        scanCal.MarkApplied(howApplied);
        Console.Log($"[scancal] {(wentLive ? "Live viewport" : "JSON only")} — reloaded {System.IO.Path.GetFileName(cellPath)}.");

        await Task.CompletedTask;
    }

    /// <summary>Scanner tool #6 — hand-eye result lives here after scan-cal.</summary>
    ToolCellConfig? ResolveCalibratedScannerTool()
    {
        var tools = Viewport.ActiveCell?.EffectiveTools;
        if (tools is null) return null;
        int krl = ScanToolCalSweep.ResultToolIndex;
        return tools.FirstOrDefault(t => t.KrlIndex == krl)
            ?? tools.FirstOrDefault(t => string.Equals(t.Name, "Scanner", StringComparison.OrdinalIgnoreCase));
    }

    void EnsureCalibratedScannerToolSelected(string logPrefix)
    {
        var robot = RightPanel.Settings.Robot;
        int krl = ScanToolCalSweep.ResultToolIndex;
        if (robot.KrlToolIndex == krl) return;
        if (robot.SelectToolByKrlIndex(krl))
            Console.Log($"{logPrefix} Selected tool #{krl} (Scanner) for registration.");
        else
            Console.LogError($"{logPrefix} Tool #{krl} (Scanner) not in cell — run scan-cal and `cell LFAM 3` first.");
    }

    System.Numerics.Matrix4x4? GetScannerCameraToWorld()
    {
        EnsureCalibratedScannerToolSelected("[scan]");
        if (ResolveCalibratedScannerTool() is not { } scannerTool
            || Viewport.GetToolWorldPose?.Invoke(scannerTool) is not { } p)
            return null;
        return new System.Numerics.Matrix4x4(
            p.M11, p.M12, p.M13, p.M14,
            p.M21, p.M22, p.M23, p.M24,
            p.M31, p.M32, p.M33, p.M34,
            p.M41, p.M42, p.M43, p.M44);
    }

    /// <summary>
    /// Captures a Zivid frame, meshes for viewport (optional), stashes world points for diag export.
    /// Callable from console / bridge.
    /// </summary>
    public async Task RunConsoleScanAsync(bool addToViewport = true, bool saveToDisk = false)
    {
        var scan = RightPanel.Scan;
        if (scan.IsScanning)
        {
            Console.LogError("[scan] Capture already in progress.");
            return;
        }

        scan.IsScanning = true;
        scan.ScanStatus = "Starting capture...";
        try
        {
            var robot = RightPanel.Settings.Robot;
            var camW = GetScannerCameraToWorld();
            if (camW is null)
                Console.Log("[scan] No scanner camera pose â€” cloud will be unregistered.");

            var outDir = saveToDisk ? scan.OutputDirectory : null;
            var meta = new ScanMetadata
            {
                A1 = (float)robot.A1, A2 = (float)robot.A2, A3 = (float)robot.A3,
                A4 = (float)robot.A4, A5 = (float)robot.A5, A6 = (float)robot.A6,
                E1 = (float)robot.E1,
                TcpX = (float)robot.EditTcpX, TcpY = (float)robot.EditTcpY, TcpZ = (float)robot.EditTcpZ,
                TcpA = (float)robot.EditTcpA, TcpB = (float)robot.EditTcpB, TcpC = (float)robot.EditTcpC,
            };

            var result = await Task.Run(() => ZividScanService.Capture(
                outDir, meta,
                msg => Dispatcher.UIThread.Post(() => scan.ScanStatus = msg)));

            if (camW is { } cw)
            {
                var (world, valid) = ScanPointCloudTransform.ToWorld(result.PointsXYZ, cw);
                var name = $"scan_{DateTime.Now:HH-mm-ss}";
                Viewport.StashScanDiagWorld(name, (float)robot.E1, world);
                Console.Log($"[scan] {valid:N0} world points stashed (E1 {robot.E1:F1}Â°) â€” run `diag-scans` to export.");
            }

            if (!addToViewport)
            {
                scan.ScanStatus = $"Captured {result.ValidPointCount:N0} points (CPU only, no viewport mesh).";
                Console.Log($"[scan] {scan.ScanStatus}");
                return;
            }

            scan.ScanStatus = $"Meshing {result.ValidPointCount:N0} points...";
            var nodeName = $"Scan {DateTime.Now:HH-mm-ss}";
            var node = await Task.Run(() => PointCloudMesher.Build(
                result.PointsXYZ, result.Width, result.Height, nodeName));
            if (node is null)
            {
                scan.ScanStatus = "Scan contained no meshable points.";
                return;
            }

            node.CullFaces = false;
            OpenTK.Mathematics.Matrix4? otPose = ResolveCalibratedScannerTool() is { } st
                ? Viewport.GetToolWorldPose?.Invoke(st)
                : null;

            if (otPose is { } pose)
            {
                node.LocalTransform = pose;
                Console.Log("[scan] Registered via robot pose (scanner TOOL frame).");
            }
            else
            {
                node.LocalTransform = Matrix4.CreateRotationX(MathF.PI);
                ImportHelper.PlaceOnBed(node, Viewport.ActiveCell);
            }

            Viewport.AddScanNode(node);
            scan.ScanStatus = $"Added \"{nodeName}\" â€” {result.ValidPointCount:N0} points";
            Console.Log($"[scan] {scan.ScanStatus}");
        }
        catch (Exception ex)
        {
            scan.ScanStatus = $"Scan failed: {ex.Message}";
            Console.LogError($"[scan] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            scan.IsScanning = false;
        }
    }

    /// <summary>Bridge/console entry point for Auto Bed Calibration (fire-and-forget).</summary>
    public void StartBedCalibration()
    {
        Console.Log("[bedcal] Starting auto bed calibration from console…");
        _ = RunAutoBedCalibrationAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is { } ex)
                Console.LogError($"[bedcal] Unhandled: {ex.GetBaseException().Message}");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Bridge/console entry point for Auto 3D Scan (hand-eye) Calibration (fire-and-forget).</summary>
    public void StartScanCalibration()
    {
        Console.Log("[scancal] Starting auto scan-tool calibration from console…");
        _ = RunAutoScanToolCalibrationAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is { } ex)
                Console.LogError($"[scancal] Unhandled: {ex.GetBaseException().Message}");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Full LFAM3 calibration wizard: MassiveDRIVE idle check → scan-cal (hand-eye) → bed-cal.
    /// Console: <c>calibrate</c>, <c>calibrate scan</c>, <c>calibrate bed</c>.
    /// </summary>
    public void StartLfam3CalibrationWizard(string? mode = null)
    {
        string m = (mode ?? "full").Trim().ToLowerInvariant();
        if (m is "scan" or "scancal" or "hand-eye" or "handeye")
        {
            StartScanCalibration();
            return;
        }
        if (m is "bed" or "bedcal" or "rotary")
        {
            StartBedCalibration();
            return;
        }

        Console.Log("[cal] === LFAM3 calibration wizard (scan-cal → bed-cal via MassiveDRIVE) ===");
        Console.Log("[cal] Pendant: LFAM3_RSI_BulkPTP selected, AUT, drives ON. Path executor idle.");
        _ = RunLfam3CalibrationWizardAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is { } ex)
                Console.LogError($"[cal] Unhandled: {ex.GetBaseException().Message}");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Query MassiveDRIVE path busy state (console <c>drive-status</c>).</summary>
    public async Task ReportMassiveDriveStatusAsync()
    {
        var cell = ActiveCellConfig();
        var url = cell?.MassiveDriveUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Log("[drive] No massiveDriveUrl on active cell.");
            return;
        }
        try
        {
            using var client = new MassiveDriveClient(url, TimeSpan.FromSeconds(5));
            var status = await client.QueryPathStatusAsync();
            Console.Log($"[drive] {url} — {status.Summary}" +
                        (status.SafeForCalibration ? " (safe for CELL cal)" : status.PathActive ? " (block cal)" : " (check connectivity)"));
        }
        catch (Exception ex)
        {
            Console.LogError($"[drive] status failed: {ex.Message}");
        }
    }

    /// <summary>Stop MassiveDRIVE path executor (console <c>drive-stop</c>).</summary>
    public async Task StopMassiveDrivePathAsync(string reason = "slicer-cal")
    {
        var cell = ActiveCellConfig();
        var url = cell?.MassiveDriveUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.LogError("[drive] No massiveDriveUrl on active cell.");
            return;
        }
        try
        {
            using var client = new MassiveDriveClient(url, TimeSpan.FromSeconds(8));
            using var doc = await client.StopAsync(reason);
            Console.Log($"[drive] Stop requested ({reason}): {doc.RootElement.GetRawText()}");
        }
        catch (Exception ex)
        {
            Console.LogError($"[drive] stop failed: {ex.Message}");
        }
    }

    async Task RunLfam3CalibrationWizardAsync()
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected)
        {
            Console.LogError("[cal] Robot not connected — run `sync` first.");
            return;
        }
        if (!await EnsureMassiveDriveReadyForCalibrationAsync("[cal]"))
            return;

        Console.Log("[cal] Phase 1/2: scan-tool hand-eye…");
        bool scanOk = await RunAutoScanToolCalibrationAsync();
        Console.Log(scanOk
            ? "[cal] Phase 1/2: scan-cal OK."
            : "[cal] Phase 1/2: scan-cal did not apply a result — continuing to bed-cal anyway (existing tool #6 TCP).");

        // Small pause so streaming can resume between phases
        await Task.Delay(500);

        if (!await EnsureMassiveDriveReadyForCalibrationAsync("[cal]"))
            return;

        Console.Log("[cal] Phase 2/2: rotary bed…");
        bool bedOk = await RunAutoBedCalibrationAsync();
        Console.Log(bedOk
            ? "[cal] Phase 2/2: bed-cal OK."
            : "[cal] Phase 2/2: bed-cal did not apply a centre.");

        Console.Log($"[cal] === Wizard done — scan={(scanOk ? "ok" : "skip/fail")}, bed={(bedOk ? "ok" : "fail")} ===");
        Console.Log("[cal] Keep LFAM3_RSI_BulkPTP running for MassiveDRIVE paths.");
    }

    /// <summary>Moves E1 (deg on rotary cells, mm on rail cells) while holding A1â€“A6.</summary>
    public async Task MoveE1Async(double e1Value, int vel = 20)
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[e1] Robot not connected â€” Sync first."); return; }
        string unit = robot.IsRobotRail ? "mm" : "Â°";
        robot.PauseStreaming();
        try
        {
            var axes = await robot.ReadAxesAsync();
            Console.Log($"[e1] PTP E1 â†’ {e1Value:F1}{unit} (holding A1â€“A6) @ {vel}% â€¦");
            await robot.InitCommandServerAsync();
            bool ok = await robot.SendAxesAsync(
                axes[0], axes[1], axes[2], axes[3], axes[4], axes[5], e1Value, vel,
                robot.KrlToolIndex, robot.KrlBaseIndex);
            if (ok)
            {
                robot.E1 = Math.Round(e1Value, 2);
                Console.Log($"[e1] At E1={e1Value:F1}{unit}.");
            }
            else
                Console.LogError("[e1] Move timed out.");
        }
        catch (Exception ex) { Console.LogError($"[e1] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>Seeds a RobotSmb prefs entry per discovered cell (host prefilled from the
    /// cell's bridge IP) so Preferences → Connections always lists every cell.</summary>
    public void EnsureRobotSmbEntries(IEnumerable<(string Name, string BridgeIp)> cells)
    {
        bool added = false;
        foreach (var (name, ip) in cells)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (AppPreferences.RobotSmb.Any(c => string.Equals(c.CellName, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            AppPreferences.RobotSmb.Add(new RobotSmbConfig { CellName = name, Host = ip });
            added = true;
        }
        if (added) PreferencesLoader.Save(AppPreferences);
    }

    /// <summary>Captures the full app window as PNG bytes. Wired from <c>MainWindow</c> on load.</summary>
    internal Func<Task<byte[]?>>? CaptureAppScreenshot { get; set; }

    /// <summary>Captures only the GL viewport (no UI chrome) as PNG bytes — used for the
    /// ERP element preview. Wired from <c>MainWindow</c> on load.</summary>
    internal Func<Task<byte[]?>>? CaptureViewportPng { get; set; }

    /// <summary>
    /// Builds the ERP slice registration payload: renders a viewport preview PNG into
    /// <c>slicer/</c> beside the saved .mass, and references heavy files by UNAS
    /// share-relative path (the ERP resolves them via its UNAS API — no bytes are
    /// uploaded to the web server). Returns null when the workspace was never saved.
    /// </summary>
    internal async Task<(MassiveSlicer.App.Erp.ErpSliceStats Stats, IReadOnlyList<MassiveSlicer.App.Erp.ErpSliceFile> Files)?> BuildErpSlicePayloadAsync()
    {
        if (AppPreferences.LastWorkspacePath is not { Length: > 0 } massPath
            || !System.IO.File.Exists(massPath))
            return null;

        var files = new List<MassiveSlicer.App.Erp.ErpSliceFile>();

        // The .src for this revision goes to the project's 3D Print Files/Rev N/ on
        // the UNAS; the preview render lands beside it so each rev folder is complete.
        var src = await ExportSrcToPrintFilesAsync();
        if (src is { } s2)
        {
            if (ToUnasShareRelative(s2.Path) is { } krlRel)
                files.Add(new MassiveSlicer.App.Erp.ErpSliceFile("krl", krlRel, new System.IO.FileInfo(s2.Path).Length));
        }
        else
        {
            Console.Log("[erp] no active toolpath — registering without a .src file.");
        }

        try
        {
            if (CaptureViewportPng is { } capture && await capture() is { Length: > 0 } png)
            {
                string dir = src?.RevFolder
                    ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(massPath)!, "slicer");
                System.IO.Directory.CreateDirectory(dir);
                string previewPath = System.IO.Path.Combine(
                    dir, System.IO.Path.GetFileNameWithoutExtension(massPath) + " preview.png");
                await System.IO.File.WriteAllBytesAsync(previewPath, png);
                if (ToUnasShareRelative(previewPath) is { } previewRel)
                    files.Add(new MassiveSlicer.App.Erp.ErpSliceFile("preview", previewRel, png.Length));
            }
        }
        catch (Exception ex)
        {
            Console.Log($"[erp] preview render failed: {ex.Message}");
        }

        if (ToUnasShareRelative(massPath) is { } massRel)
            files.Add(new MassiveSlicer.App.Erp.ErpSliceFile("workspace", massRel, new System.IO.FileInfo(massPath).Length));

        var add = RightPanel.Additive;
        var stats = new MassiveSlicer.App.Erp.ErpSliceStats(
            PrintTime:     Viewport.StatsTime is { Length: > 0 } t ? t : null,
            Weight:        Viewport.StatsWeight is { Length: > 0 } w ? w : null,
            Material:      add.SelectedPreset?.Name,
            LayerHeightMm: add.LayerHeight,
            BeadWidthMm:   add.BeadWidth,
            PrintTimeSec:  Viewport.StatsTimeSeconds > 0 ? Viewport.StatsTimeSeconds : null,
            WeightKg:      Viewport.StatsWeightKg > 0 ? Viewport.StatsWeightKg : null);
        return (stats, files);
    }

    /// <summary>
    /// After a successful Export-to-Robot SMB upload: mirrors the .src into the
    /// project's <c>slicer/</c> folder on the UNAS (so the ERP can reach the same
    /// file) and registers a slice rev flagged <c>sentToRobot</c> so the ERP knows
    /// the program is on the printer and ready to run.
    /// </summary>
    internal async Task NotifyErpSentToRobotAsync(string srcPath, string fileName, string cellName, string host)
    {
        var files = new List<MassiveSlicer.App.Erp.ErpSliceFile>();
        string robotPath = $@"\\{host}\{fileName}";

        // srcPath is the NAS copy under 3D Print Files/Rev N/ when the workspace is
        // saved on the share; reference it directly.
        if (ToUnasShareRelative(srcPath) is { } krlRel && System.IO.File.Exists(srcPath))
            files.Add(new MassiveSlicer.App.Erp.ErpSliceFile("krl", krlRel, new System.IO.FileInfo(srcPath).Length));

        if (AppPreferences.LastWorkspacePath is { Length: > 0 } massPath && System.IO.File.Exists(massPath))
        {
            if (ToUnasShareRelative(massPath) is { } massRel)
                files.Add(new MassiveSlicer.App.Erp.ErpSliceFile("workspace", massRel, new System.IO.FileInfo(massPath).Length));
        }

        var add = RightPanel.Additive;
        var stats = new MassiveSlicer.App.Erp.ErpSliceStats(
            PrintTime:     Viewport.StatsTime is { Length: > 0 } t ? t : null,
            Weight:        Viewport.StatsWeight is { Length: > 0 } w ? w : null,
            Material:      add.SelectedPreset?.Name,
            LayerHeightMm: add.LayerHeight,
            BeadWidthMm:   add.BeadWidth,
            PrintTimeSec:  Viewport.StatsTimeSeconds > 0 ? Viewport.StatsTimeSeconds : null,
            WeightKg:      Viewport.StatsWeightKg > 0 ? Viewport.StatsWeightKg : null);

        await Viewport.Erp.NotifySentToRobotAsync(stats, files, new Dictionary<string, object?>
        {
            ["cell"]     = cellName,
            ["host"]     = host,
            ["file"]     = fileName,
            ["robotPath"] = robotPath,
            ["at"]       = DateTime.UtcNow.ToString("o"),
        });
    }

    /// <summary>
    /// Writes the active toolpath's .src into the project's
    /// <c>3D Print Files/Rev N/</c> folder beside the saved .mass — one folder per
    /// registered/sent revision, filename identical to the source geometry (the KRL
    /// program name derives from it). Returns null when the workspace was never
    /// saved or no toolpath is active.
    /// </summary>
    internal async Task<(string Path, string RevFolder)?> ExportSrcToPrintFilesAsync()
    {
        if (AppPreferences.LastWorkspacePath is not { Length: > 0 } massPath
            || !System.IO.File.Exists(massPath)
            || Viewport.ExportKrlToDirectory is not { } export)
            return null;

        string baseDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(massPath)!, "3D Print Files");
        string revDir = NextRevisionDir(baseDir);
        System.IO.Directory.CreateDirectory(revDir);
        int rev = int.TryParse(System.IO.Path.GetFileName(revDir)[4..].Trim(), out int n2) ? n2 : 1;

        string? path = null;
        try
        {
            path = await export(revDir, rev);
        }
        catch (Exception ex)
        {
            Console.Log($"[krl] .src export to {revDir} failed: {ex.Message}");
        }

        if (path is null)
        {
            try { System.IO.Directory.Delete(revDir); } catch { /* non-empty or gone */ }
            return null;
        }
        Console.Log($"[krl] Saved {System.IO.Path.GetFileName(path)} to {revDir}");
        return (path, revDir);
    }

    /// <summary>Next unused "Rev N" subfolder (Rev 1, Rev 2, …) under <paramref name="baseDir"/>.</summary>
    internal static string NextRevisionDir(string baseDir)
    {
        int next = 1;
        if (System.IO.Directory.Exists(baseDir))
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(baseDir))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (name.StartsWith("Rev ", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(name[4..].Trim(), out int n) && n >= next)
                    next = n + 1;
            }
        }
        return System.IO.Path.Combine(baseDir, $"Rev {next}");
    }

    /// <summary>
    /// <c>/Volumes/&lt;share&gt;/rest…</c> → <c>rest…</c> — the path the ERP's UNAS API
    /// resolves against the same share. Null for local (non-mounted) paths.
    /// </summary>
    internal static string? ToUnasShareRelative(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 2 && parts[0] == "Volumes"
            ? string.Join('/', parts.Skip(2))
            : null;
    }

    /// <summary>Saves a full-window PNG under <c>%LOCALAPPDATA%/MassiveSlicer/screenshots/</c> and returns the path.</summary>
    public async Task<string> SaveViewportScreenshotAsync()
    {
        if (CaptureAppScreenshot is not { } capture)
            return "App screenshot not available.";
        var png = await capture();
        if (png is null || png.Length == 0)
            return "App screenshot failed (no frame).";
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MassiveSlicer", "screenshots");
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"app_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        await System.IO.File.WriteAllBytesAsync(path, png);
        Console.Log($"[screenshot] {path} ({png.Length:N0} bytes)");
        return path;
    }

    /// <summary>
    /// Exports the rotary scans stashed this session (world points + capture E1) plus the rotary
    /// rotation centre/sign to a <c>diag/</c> folder under the scan output directory, for offline
    /// calibration analysis. Returns a summary line.
    /// </summary>
    public string ExportScanDiagnostics()
    {
        if (Viewport.ScanDiagCount == 0)
            return "No scans stashed this session â€” run `scan`, auto bed-cal, or registered scans first.";

        var robot = RightPanel.Settings.Robot;
        float sign = (float)robot.BedRotationSign;
        OpenTK.Mathematics.Vector3 center = OpenTK.Mathematics.Vector3.Zero;
        if (Viewport.ActiveCell is { RotaryBed: { } rb } cell && rb.BasePos.Length >= 3)
        {
            var rw = cell.Robot.WorldPosition;
            center = new OpenTK.Mathematics.Vector3(rw.X + rb.BasePos[0], rw.Y + rb.BasePos[1], rw.Z + rb.BasePos[2]);
        }

        var diagDir = System.IO.Path.Combine(RightPanel.Scan.OutputDirectory, "diag");
        var summary = Viewport.ExportScanDiag(diagDir, center, sign);
        Console.Log($"[diag] Exported {summary} (centre {center.X:F1}, {center.Y:F1}, {center.Z:F1}; sign {sign:F0}).");
        return summary;
    }

    /// <summary>
    /// Sets the rotary bed's constant orientation offset (degrees about its vertical axis) in the
    /// active cell, then reloads so the bed mesh rotates to match. Persists to the cell JSON.
    /// </summary>
    public string SetBedOrientationOffset(float deg)
    {
        if (Viewport.ActiveCellPath is not { } path)
            return "No active cell.";
        if (!CellLoader.SaveRotaryOrientation(path, deg, out var err))
            return $"Failed: {err}";
        MassiveSlicer.App.CellSceneCache.Invalidate(path);
        Viewport.OnDevCellReloadRequested?.Invoke(path);
        return $"Bed orientation offset = {deg:F3}Â° â€” reloading cell.";
    }

    // â”€â”€ Motion commands (handled by the controller's CELL.SRC loop via the MS_* globals) â”€â”€

    /// <summary>Sends a Cartesian move (PTP or LIN) to the controller's motion loop and logs the result.</summary>
    public async Task MoveServerPoseAsync(bool linear, double x, double y, double z, double a, double b, double c, int vel, int tool = -1, int baseIndex = -1)
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[srv] Robot not connected â€” Sync first."); return; }
        int useTool = tool >= 0 ? tool : robot.KrlToolIndex;
        int useBase = baseIndex >= 0 ? baseIndex : robot.KrlBaseIndex;
        robot.PauseStreaming();
        try
        {
            await robot.InitCommandServerAsync();
            Console.Log($"[srv] {(linear ? "LIN" : "PTP")} â†’ ({x:F1}, {y:F1}, {z:F1}) A{a:F1} B{b:F1} C{c:F1} @ {vel}% tool #{useTool} base #{useBase} â€¦");
            bool ok = await robot.SendPoseAsync(linear, x, y, z, a, b, c, vel, useTool, useBase);
            Console.Log(ok ? "[srv] Move acknowledged." : "[srv] Move timed out â€” is MASSIVE_SERVER running on the controller?");
        }
        catch (Exception ex) { Console.LogError($"[srv] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>Sends the robot to its HOME position via MASSIVE_SERVER.</summary>
    public async Task MoveServerHomeAsync(int vel)
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[srv] Robot not connected â€” Sync first."); return; }
        robot.PauseStreaming();
        try
        {
            await robot.InitCommandServerAsync();
            Console.Log($"[srv] HOME @ {vel}% â€¦");
            bool ok = await robot.GoHomeAsync(vel);
            Console.Log(ok ? "[srv] At HOME." : "[srv] Home timed out â€” is MASSIVE_SERVER running?");
        }
        catch (Exception ex) { Console.LogError($"[srv] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>Triggers <c>Scanner_Pick</c> via the <c>CELL</c> dispatcher (<c>bRunScanPick</c> BOOL).</summary>
    public async Task TriggerScanPickAsync()
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[pick] Robot not connected â€” Sync first."); return; }
        robot.PauseStreaming();
        try
        {
            var echo = await robot.TriggerScanPickAsync();
            Console.Log($"[pick] bRunScanPick=TRUE (echo {echo.Trim()}) â€” watch CELL run Scanner_Pick.");
        }
        catch (Exception ex) { Console.LogError($"[pick] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>Reads one or more KRL variables over C3Bridge and logs the raw values.</summary>
    public async Task ReadKrlVarsAsync(string names)
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[var] Robot not connected â€” Sync first."); return; }
        robot.PauseStreaming();
        try
        {
            foreach (var name in names.Split((char[])[' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var value = await robot.ReadVarAsync(name);
                Console.Log($"[var] {name} = {value.Trim()}");
            }
        }
        catch (Exception ex) { Console.LogError($"[var] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>Applies tool/base on the controller (MS_CMD=5) so pos/move-pose share the same frame.</summary>
    public async Task SetServerFrameAsync(int tool, int baseIndex)
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[frame] Robot not connected â€” Sync first."); return; }
        robot.PauseStreaming();
        try
        {
            await robot.InitCommandServerAsync();
            bool ok = await robot.SetFrameAsync(tool, baseIndex);
            Console.Log(ok ? $"[frame] controller tool #{tool}, base #{baseIndex}." : "[frame] timed out â€” reload cell.src?");
        }
        catch (Exception ex) { Console.LogError($"[frame] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>Logs $AXIS_ACT (A1â€“A6, E1) with LFAM3 soft-limit hints and a move-joints line.</summary>
    public async Task LogCurrentJointsAsync()
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[joints] Robot not connected â€” Sync first."); return; }
        robot.PauseStreaming();
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var axisStr = await robot.ReadVarAsync("$AXIS_ACT");
            var (j, e1) = MassiveSlicer.Core.C3Bridge.KrlVarParser.ParseAxisActWithE1(axisStr);
            int actTool = robot.KrlToolIndex, actBase = robot.KrlBaseIndex;
            var toolStr = await robot.ReadVarAsync("$ACT_TOOL");
            var baseStr = await robot.ReadVarAsync("$ACT_BASE");
            if (int.TryParse(toolStr.Trim(), System.Globalization.NumberStyles.Integer, inv, out var t)) actTool = t;
            if (int.TryParse(baseStr.Trim(), System.Globalization.NumberStyles.Integer, inv, out var bIdx)) actBase = bIdx;

            static string Lim(int i, double v) => i switch
            {
                1 => v is < -185 or > 185 ? " !A1" : "",
                2 => v is < -140 or > -5 ? " !A2" : "",
                3 => v is < -120 or > 168 ? " !A3" : "",
                4 => v is < -350 or > 350 ? " !A4" : "",
                5 => v is < -125 or > 125 ? " !A5" : "",
                6 => v is < -350 or > 350 ? " !A6" : "",
                _ => ""
            };

            Console.Log(string.Format(inv,
                "[joints] A1={0:F2} A2={1:F2} A3={2:F2} A4={3:F2} A5={4:F2} A6={5:F2} E1={6:F2}  (ctrl tool #{7}, base #{8})",
                j[0], j[1], j[2], j[3], j[4], j[5], e1, actTool, actBase));
            for (int i = 0; i < 6; i++)
            {
                var flag = Lim(i + 1, j[i]);
                if (flag.Length > 0)
                    Console.Log($"[joints] near/outside soft limit:{flag} on A{i + 1}={j[i]:F2}");
            }
            Console.Log(string.Format(inv,
                "move-joints {0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} {6:F2} 20 {7} {8}",
                j[0], j[1], j[2], j[3], j[4], j[5], e1, actTool, actBase));
            Console.Log("[joints] Use move-joints when move-pose hits +A6 / workspace â€” tweak A2/A3/A5 to lower TCP without pinning ABC.");
        }
        catch (Exception ex) { Console.LogError($"[joints] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>Relative Cartesian jog in the active controller frame (LFAM3: +X fwd, +Y right, +Z up).</summary>
    public async Task MoveRelativeAsync(double dxMm, double dyMm, double dzMm, int vel = 20)
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[srv] Robot not connected â€” Sync first."); return; }
        robot.PauseStreaming();
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var posStr = await robot.ReadVarAsync("$POS_ACT");
            var (x, y, z, a, b, c) = MassiveSlicer.Core.C3Bridge.KrlVarParser.ParsePosAct(posStr);
            int actTool = robot.KrlToolIndex, actBase = robot.KrlBaseIndex;
            var toolStr = await robot.ReadVarAsync("$ACT_TOOL");
            var baseStr = await robot.ReadVarAsync("$ACT_BASE");
            if (int.TryParse(toolStr.Trim(), System.Globalization.NumberStyles.Integer, inv, out var t)) actTool = t;
            if (int.TryParse(baseStr.Trim(), System.Globalization.NumberStyles.Integer, inv, out var bIdx)) actBase = bIdx;

            double nx = x + dxMm, ny = y + dyMm, nz = z + dzMm;
            Console.Log(string.Format(inv,
                "[srv] Relative Î”X={0:F1} Î”Y={1:F1} Î”Z={2:F1} mm â†’ ({3:F1}, {4:F1}, {5:F1})",
                dxMm, dyMm, dzMm, nx, ny, nz));
            await robot.InitCommandServerAsync();
            bool ok = await robot.SendPoseAsync(false, nx, ny, nz, a, b, c, vel, actTool, actBase);
            Console.Log(ok ? "[srv] Move acknowledged." : "[srv] Move timed out â€” try joints if soft limit (+A6).");
        }
        catch (Exception ex) { Console.LogError($"[srv] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>PTP to a joint target via MS_CMD=3 (MS_AXIS).</summary>
    public async Task MoveServerJointsAsync(double a1, double a2, double a3, double a4, double a5, double a6, double e1,
        int vel, int tool = -1, int baseIndex = -1)
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[srv] Robot not connected â€” Sync first."); return; }
        int useTool = tool >= 0 ? tool : robot.KrlToolIndex;
        int useBase = baseIndex >= 0 ? baseIndex : robot.KrlBaseIndex;
        robot.PauseStreaming();
        try
        {
            await robot.InitCommandServerAsync();
            Console.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[srv] PTP joints A1={0:F1} A2={1:F1} A3={2:F1} A4={3:F1} A5={4:F1} A6={5:F1} E1={6:F1} @ {7}% tool #{8} base #{9} â€¦",
                a1, a2, a3, a4, a5, a6, e1, vel, useTool, useBase));
            bool ok = await robot.SendAxesAsync(a1, a2, a3, a4, a5, a6, e1, vel, useTool, useBase);
            Console.Log(ok ? "[srv] Joint move acknowledged." : "[srv] Joint move timed out.");
        }
        catch (Exception ex) { Console.LogError($"[srv] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>Logs $POS_ACT plus a move-pose line using the controller's actual $ACT_TOOL/$ACT_BASE.</summary>
    public async Task LogCurrentPoseAsync()
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[pos] Robot not connected â€” Sync first."); return; }
        robot.PauseStreaming();
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var posStr = await robot.ReadVarAsync("$POS_ACT");
            var (x, y, z, a, b, c) = MassiveSlicer.Core.C3Bridge.KrlVarParser.ParsePosAct(posStr);
            int actTool = robot.KrlToolIndex, actBase = robot.KrlBaseIndex;
            var toolStr = await robot.ReadVarAsync("$ACT_TOOL");
            var baseStr = await robot.ReadVarAsync("$ACT_BASE");
            if (int.TryParse(toolStr.Trim(), System.Globalization.NumberStyles.Integer, inv, out var t)) actTool = t;
            if (int.TryParse(baseStr.Trim(), System.Globalization.NumberStyles.Integer, inv, out var bIdx)) actBase = bIdx;
            Console.Log(string.Format(inv,
                "[pos] X={0:F1} Y={1:F1} Z={2:F1} A={3:F3} B={4:F3} C={5:F3}  (ctrl tool #{6}, base #{7})",
                x, y, z, a, b, c, actTool, actBase));
            Console.Log(string.Format(inv,
                "move-pose {0:F1} {1:F1} {2:F1} {3:F3} {4:F3} {5:F3} 20 {6} {7}",
                x, y, z, a, b, c, actTool, actBase));
        }
        catch (Exception ex) { Console.LogError($"[pos] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    CellConfig? ActiveCellConfig()
        => Viewport.ActiveCellPath is { } path ? CellLoader.Load(path) : Viewport.ActiveCell;

    /// <summary>Returns a named waypoint from the active cell config, or null.</summary>
    public CellWaypointConfig? GetActiveWaypoint(string name)
    {
        if (ActiveCellConfig() is not { } cell || string.IsNullOrWhiteSpace(name))
            return null;
        return CellLoader.FindWaypoint(cell, name);
    }

    /// <summary>Lists saved waypoints for the active cell.</summary>
    public void LogWaypoints()
    {
        if (ActiveCellConfig() is not { } cell)
        {
            Console.LogError("[waypoint] No active cell.");
            return;
        }

        if (cell.Waypoints.Count == 0)
        {
            Console.Log("[waypoint] No waypoints saved for this cell â€” use `waypoint save <name>` at the teach pose.");
            return;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Console.Log($"[waypoint] {cell.Waypoints.Count} saved for {cell.Name}:");
        foreach (var wp in cell.Waypoints)
        {
            var tags = wp.Tags.Count > 0 ? $" [{string.Join(", ", wp.Tags)}]" : "";
            var mode = wp.PreferJoints ? "joints" : "pose";
            Console.Log($"  {wp.Name}{tags} â€” {wp.Description ?? "(no description)"} ({mode}, tool #{wp.Tool} base #{wp.Base})");
            Console.Log(string.Format(inv,
                "    TCP ({0:F1}, {1:F1}, {2:F1}) A={3:F3} B={4:F3} C={5:F3}",
                wp.TcpX, wp.TcpY, wp.TcpZ, wp.TcpA, wp.TcpB, wp.TcpC));
            if (wp.Joints is { Length: >= 6 } j)
            {
                var e1 = j.Length >= 7 ? j[6] : 0;
                Console.Log(string.Format(inv,
                    "    joints A1={0:F2}..A6={5:F2} E1={6:F2}",
                    j[0], j[1], j[2], j[3], j[4], j[5], e1));
            }
        }
        Console.Log("[waypoint] Recall: `waypoint go <name>`");
    }

    /// <summary>Moves the robot to a saved cell waypoint (joint or Cartesian per <see cref="CellWaypointConfig.PreferJoints"/>).</summary>
    public async Task<bool> GoToWaypointAsync(string name, int velOverride = -1)
    {
        if (Viewport.ActiveCellPath is not { } path)
        {
            Console.LogError("[waypoint] No active cell.");
            return false;
        }

        var cell = CellLoader.Load(path);
        if (CellLoader.FindWaypoint(cell, name) is not { } wp)
        {
            Console.LogError($"[waypoint] '{name}' not found â€” run `waypoint list`.");
            return false;
        }

        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected)
        {
            Console.LogError("[waypoint] Robot not connected â€” Sync first.");
            return false;
        }

        robot.PauseStreaming();
        try
        {
            bool ok = await ExecuteWaypointMoveAsync(robot, wp, "[waypoint]", velOverride);
            Console.Log(ok
                ? $"[waypoint] At {wp.Name}."
                : "[waypoint] Move timed out â€” check MASSIVE_SERVER / CELL.");
            return ok;
        }
        catch (Exception ex)
        {
            Console.LogError($"[waypoint] {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>
    /// Joint-angle tolerance (deg) for treating current pose as already at the cal waypoint.
    /// If within this band we <b>do not move</b> — operator often has scanner already aimed at bed.
    /// </summary>
    const double CalWaypointSkipTolDeg = 4.0;

    /// <summary>
    /// Soft cap on cal approach moves so we never race to a taught waypoint.
    /// </summary>
    const int CalApproachVelPctMax = 12;

    /// <summary>
    /// Optionally approaches the cell waypoint tagged for cal (<c>bed-cal</c>, <c>scan-cal</c>).
    /// If the arm is already near the taught joints (or no joints taught), keeps the current pose.
    /// </summary>
    async Task<bool> GoToCalWaypointAsync(string tag, Action<string> setStatus, string logPrefix)
    {
        if (ActiveCellConfig() is not { } cell)
        {
            Console.Log($"{logPrefix} No active cell — starting from current pose.");
            return true;
        }

        if (CellLoader.FindWaypointByTag(cell, tag) is not { } wp)
        {
            Console.Log($"{logPrefix} No waypoint tagged '{tag}' — keeping current pose (scanner already set up).");
            return true;
        }

        var robot = RightPanel.Settings.Robot;
        try
        {
            // Prefer staying put when already near the taught TCP (bulk Cartesian distance).
            var curPose = await ReadPoseForCalAsync(robot, logPrefix);
            if (curPose is { } cp)
            {
                double d = Math.Sqrt(
                    Math.Pow(cp.X - wp.TcpX, 2) +
                    Math.Pow(cp.Y - wp.TcpY, 2) +
                    Math.Pow(cp.Z - wp.TcpZ, 2));
                const double skipMm = 25.0;
                if (d <= skipMm)
                {
                    setStatus($"Already near {wp.Name} ({d:F0} mm) — keeping current scanner pose.");
                    Console.Log($"{logPrefix} Skip pre-cal bulk: within {skipMm:F0} mm of '{wp.Name}' (d={d:F1} mm). Holding your setup.");
                    return true;
                }
                Console.Log($"{logPrefix} {d:F0} mm from '{wp.Name}' — slow bulk approach.");
            }
            else
            {
                Console.Log($"{logPrefix} No pose feedback — slow bulk approach to '{wp.Name}'.");
            }

            setStatus($"Slow bulk approach to {wp.Name}…");
            int vel = Math.Min(wp.VelocityPct > 0 ? wp.VelocityPct : CalApproachVelPctMax, CalApproachVelPctMax);
            bool ok = await ExecuteWaypointMoveAsync(robot, wp, logPrefix, vel);
            if (!ok)
            {
                setStatus($"Couldn't reach {wp.Name} — check LFAM3_RSI_BulkPTP + MassiveDRIVE, or jog closer and retry.");
                Console.Log($"{logPrefix} Pre-cal bulk to {wp.Name} failed — abort (won't invent another pose).");
                return false;
            }

            await ReadPoseForCalAsync(robot, logPrefix);
            setStatus($"At {wp.Name} — starting calibration…");
            Console.Log($"{logPrefix} At {wp.Name} — proceeding with calibration.");
            return true;
        }
        catch (Exception ex)
        {
            setStatus($"Pre-cal move failed: {ex.Message}");
            Console.Log($"{logPrefix} Pre-cal move error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    static bool JointsNear(IReadOnlyList<double> cur, IReadOnlyList<float> target, double tolDeg)
        => MaxJointDeltaDeg(cur, target) <= tolDeg;

    static double MaxJointDeltaDeg(IReadOnlyList<double> cur, IReadOnlyList<float> target)
    {
        int n = Math.Min(6, Math.Min(cur.Count, target.Count));
        double max = 0;
        for (int i = 0; i < n; i++)
        {
            double d = Math.Abs(cur[i] - target[i]);
            // wrap A4/A6-style large angles into shortest arc
            if (d > 180) d = 360 - d;
            if (d > max) max = d;
        }
        return max;
    }

    async Task<bool> ExecuteWaypointMoveAsync(
        RobotPanelViewModel robot, CellWaypointConfig wp, string logPrefix, int velOverride = -1)
    {
        int vel = velOverride >= 0 ? velOverride : wp.VelocityPct;
        if (vel < 1) vel = CalApproachVelPctMax;
        vel = Math.Min(vel, CalApproachVelPctMax);
        // Always bulk LIN (MS_CMD=99) — joint PTP is unreliable on this cell.
        double speed = Math.Clamp(vel * 1.5, 10, 30);
        double? e1 = wp.Joints is { Length: >= 7 } j ? j[6] : null;
        Console.Log($"{logPrefix} → {wp.Name} bulk LIN @ {speed:F0} mm/s " +
                    $"(TCP {wp.TcpX:F0},{wp.TcpY:F0},{wp.TcpZ:F0})");
        return await DriveMovePoseAsync(
            wp.TcpX, wp.TcpY, wp.TcpZ, wp.TcpA, wp.TcpB, wp.TcpC,
            e1: e1, speedMmS: speed, tool: wp.Tool, baseIdx: wp.Base, logPrefix: logPrefix);
    }

    /// <summary>
    /// Parks TCP at bed-cal home + optional Y offset.
    /// Y+0 keeps current / park joints — does not re-drive the waypoint.
    /// Non-zero Y is a deliberate side-step (only if cell configures multi-vantage).
    /// </summary>
    async Task<bool> BedCalMoveToVantageAsync(
        RobotPanelViewModel robot, CellWaypointConfig wp, float yOffsetMm, int vel, int tool, int baseIdx)
    {
        if (Math.Abs(yOffsetMm) < 0.5f)
        {
            Console.Log("[bedcal] Vantage Y+0 — holding current arm pose (scanner stays on bed).");
            return true;
        }

        // Side-step: keep A1–A3/A5-ish by bulk Cartesian only when operator opted in via cell config.
        double y = wp.TcpY + yOffsetMm;
        int slow = Math.Min(vel > 0 ? vel : CalApproachVelPctMax, CalApproachVelPctMax);
        double speed = Math.Clamp(slow * 1.0, 8, 25);
        Console.Log($"[bedcal] CAUTION: multi-vantage Y offset {yOffsetMm:F0} mm → ({wp.TcpX:F1}, {y:F1}, {wp.TcpZ:F1}) @ {speed:F0} mm/s");
        bool ok = await DriveMovePoseAsync(
            wp.TcpX, y, wp.TcpZ, wp.TcpA, wp.TcpB, wp.TcpC,
            e1: null, speedMmS: speed, tool: tool, baseIdx: baseIdx, logPrefix: "[bedcal]");
        if (ok)
        {
            await Task.Delay(500);
            await ReadAxesForCalAsync(robot, "[bedcal]");
        }
        return ok;
    }

    /// <summary>
    /// Wrist nutation in <b>joint space</b> (A4/A5/A6) via MassiveDRIVE MS_CMD=93 SPTP MS_AXIS.
    /// Halves tilt when calibration card is out of frame.
    /// </summary>
    async Task<(int Captured, int Skipped)> ScanCalRunWristSweepAsync(
        RobotPanelViewModel robot,
        ScanCalibrationViewModel scanCal,
        double[] home,
        int vel,
        int tool,
        int baseIdx,
        IReadOnlyList<ScanToolCalSweep.WristDelta> startingDeltas,
        string? cellPath)
    {
        int captured = 0, skipped = 0;
        var deltas = startingDeltas.Select(d => d).ToList();
        int target = deltas.Count;
        var learned = new bool[target];

        for (int n = 0; n < target; n++)
        {
            var delta = deltas[n];
            double scale = 1.0;

            while (true)
            {
                double a4 = home[3] + delta.A4 * scale;
                double a5 = home[4] + delta.A5 * scale;
                double a6 = home[5] + delta.A6 * scale;

                scanCal.SetStatus(
                    $"Pose {n + 1}/{target}: scale {scale:P0} — joint wrist (card must be in frame)…");
                Console.Log($"[scancal] Pose {n + 1}/{target} scale={scale:F3} joints → " +
                            $"A4={a4:F1} A5={a5:F1} A6={a6:F1}");

                bool moved = await DriveMoveAxesAsync(
                    home[0], home[1], home[2], a4, a5, a6, home.Length > 6 ? home[6] : 0,
                    vel, tool, baseIdx, "[scancal]");
                if (!moved)
                {
                    Console.Log($"[scancal] Pose {n + 1}: joint SPTP failed — skipping.");
                    skipped++;
                    break;
                }

                await Task.Delay(500);
                await ReadAxesForCalAsync(robot, "[scancal]");
                await Task.Delay(400);

                bool inFrame = await scanCal.CapturePoseAutoAsync();
                if (inFrame)
                {
                    captured++;
                    if (scale < 0.999)
                    {
                        deltas[n] = new ScanToolCalSweep.WristDelta(
                            delta.A4 * scale, delta.A5 * scale, delta.A6 * scale);
                        learned[n] = true;
                        Console.Log($"[scancal] Pose {n + 1}: learned ΔA4={deltas[n].A4:F2} ΔA5={deltas[n].A5:F2} ΔA6={deltas[n].A6:F2}° " +
                                    $"(scale {scale:F3}).");
                    }
                    scanCal.SetStatus($"Pose {n + 1}/{target}: card in frame — captured ({captured} good)…");
                    Console.Log($"[scancal] Pose {n + 1}: card in frame — pose {captured} accepted.");
                    break;
                }

                if (scale <= ScanToolCalSweep.MinScale)
                {
                    skipped++;
                    Console.Log($"[scancal] Pose {n + 1}: card still out of frame at min scale ({scanCal.LastCaptureStatus}) — skipped.");
                    scanCal.SetStatus($"Pose {n + 1}/{target}: skipped (card never fully in frame).");
                    break;
                }

                scale *= 0.5;
                Console.Log($"[scancal] Pose {n + 1}: card out of frame ({scanCal.LastCaptureStatus}); re-aiming at scale {scale:F3}.");
                scanCal.SetStatus($"Pose {n + 1}/{target}: card out of frame — gentler wrist…");
            }
        }

        if (skipped > 0)
            Console.Log($"[scancal] Sweep: {captured} poses in frame, {skipped} skipped.");

        if (learned.Any(l => l) && cellPath is not null)
        {
            if (CellLoader.TrySaveScanCalWristDeltas(cellPath, deltas, out var saveErr))
            {
                int nLearned = learned.Count(l => l);
                Console.Log($"[scancal] Saved {nLearned} learned wrist delta(s) to {cellPath}.");
                MassiveSlicer.App.CellSceneCache.Invalidate(cellPath);
                Viewport.ActiveCell = CellLoader.Load(cellPath);
                Viewport.OnDevCellReloadRequested?.Invoke(cellPath);
            }
            else
                Console.LogError($"[scancal] Couldn't save learned wrist deltas: {saveErr}");
        }

        return (captured, skipped);
    }

    /// <summary>Full E1 sweep via bulk LIN (same XYZABC, vary MS_POSE.E1).</summary>
    async Task<(int VantageCaptured, int CapturedTotal)> BedCalRunE1SweepAsync(
        RobotPanelViewModel robot,
        RotaryBedCalibrationViewModel bedCal,
        IReadOnlyList<double> e1Angles,
        (double X, double Y, double Z, double A, double B, double C) parkPose,
        List<(double E1, float[] World, float YOffsetMm)> phaseClouds,
        int vantageIndex,
        float yOffsetMm,
        int vel,
        int tool,
        int baseIdx,
        int capturedTotal,
        int totalStops)
    {
        int vantageCaptured = 0;
        string yTag = $"Y{yOffsetMm:F0}";
        double speed = Math.Clamp(vel * 1.5, 12, 40);

        for (int n = 0; n < e1Angles.Count; n++)
        {
            double e1 = e1Angles[n];
            int stopNum = vantageIndex * e1Angles.Count + n + 1;
            bedCal.SetStatus($"[{yTag}] E1 {e1:F0}° ({stopNum}/{totalStops})…");
            Console.Log($"[bedcal] [{yTag}] {n + 1}/{e1Angles.Count} — bulk E1={e1:F1}°");

            bool moved = await DriveMovePoseAsync(
                parkPose.X, parkPose.Y, parkPose.Z, parkPose.A, parkPose.B, parkPose.C,
                e1, speed, tool, baseIdx, "[bedcal]");
            if (!moved)
            {
                Console.Log($"[bedcal] [{yTag}] E1 bulk move failed at {e1:F0}°.");
                break;
            }

            await Task.Delay(500);
            await ReadPoseForCalAsync(robot, "[bedcal]");

            int before = bedCal.SampleCount;
            await bedCal.AddSampleAsync();
            if (bedCal.SampleCount > before)
            {
                capturedTotal++;
                vantageCaptured++;
                Console.Log($"[bedcal] [{yTag}] board sample {capturedTotal} @ E1 {e1:F0}°.");
            }
            else
            {
                Console.Log($"[bedcal] [{yTag}] board not detected @ E1 {e1:F0}° — continuing.");
            }

            try
            {
                if (bedCal.GetCameraToWorld?.Invoke() is { } camW)
                {
                    var sres = await Task.Run(() => ZividScanService.Capture(null, null, null));
                    var (world, valid) = ScanPointCloudTransform.ToWorld(sres.PointsXYZ, camW);
                    phaseClouds.Add((e1, world, yOffsetMm));
                    Viewport.StashScanDiagWorld($"bedcal_{yTag}_E1_{e1:F0}", (float)e1, world);
                    Console.Log($"[bedcal] [{yTag}] surface scan ({valid:N0} pts).");
                }
            }
            catch (Exception ex)
            {
                Console.Log($"[bedcal] [{yTag}] surface skipped @ E1 {e1:F0}°: {ex.Message}");
            }
        }

        return (vantageCaptured, capturedTotal);
    }

    async Task SyncRobotAxesFromControllerAsync(RobotPanelViewModel robot)
    {
        // Prefer MassiveDRIVE joints; C3 only if Drive has no axes / offline
        var axes = await ReadAxesForCalAsync(robot, "[sync-axes]");
        if (axes is null)
            return;
    }

    /// <summary>Captures the live robot pose and saves it as a named waypoint in the active cell JSON.</summary>
    public async Task<bool> SaveWaypointFromRobotAsync(string name, string? description = null, IReadOnlyList<string>? tags = null)
    {
        if (Viewport.ActiveCellPath is not { } path)
        {
            Console.LogError("[waypoint] No active cell.");
            return false;
        }

        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Console.LogError("[waypoint] usage: waypoint save <name>");
            return false;
        }

        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected)
        {
            Console.LogError("[waypoint] Robot not connected — Sync first.");
            return false;
        }

        robot.PauseStreaming();
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var posStr = await robot.ReadVarAsync("$POS_ACT");
            var (x, y, z, a, b, c) = MassiveSlicer.Core.C3Bridge.KrlVarParser.ParsePosAct(posStr);
            var axisStr = await robot.ReadVarAsync("$AXIS_ACT");
            var (j, e1) = MassiveSlicer.Core.C3Bridge.KrlVarParser.ParseAxisActWithE1(axisStr);
            int actTool = robot.KrlToolIndex, actBase = robot.KrlBaseIndex;
            var toolStr = await robot.ReadVarAsync("$ACT_TOOL");
            var baseStr = await robot.ReadVarAsync("$ACT_BASE");
            if (int.TryParse(toolStr.Trim(), System.Globalization.NumberStyles.Integer, inv, out var t)) actTool = t;
            if (int.TryParse(baseStr.Trim(), System.Globalization.NumberStyles.Integer, inv, out var bIdx)) actBase = bIdx;

            // Merge tags with any existing waypoint of the same name (keep extras).
            var existing = CellLoader.FindWaypoint(CellLoader.Load(path), name);
            var tagList = new List<string>();
            if (existing?.Tags is { Count: > 0 } oldTags)
                tagList.AddRange(oldTags);
            if (tags is not null)
            {
                foreach (var tag in tags)
                {
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    if (!tagList.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                        tagList.Add(tag.Trim());
                }
            }

            var wp = new CellWaypointConfig
            {
                Name = name,
                Description = description ?? existing?.Description,
                Tags = tagList,
                TcpX = (float)x, TcpY = (float)y, TcpZ = (float)z,
                TcpA = (float)a, TcpB = (float)b, TcpC = (float)c,
                Joints = [(float)j[0], (float)j[1], (float)j[2], (float)j[3], (float)j[4], (float)j[5], (float)e1],
                Tool = actTool,
                Base = actBase,
                VelocityPct = Math.Min(
                    existing is { VelocityPct: > 0 } ex ? ex.VelocityPct : 12,
                    20),
                PreferJoints = true,
            };

            if (!CellLoader.SaveWaypoint(path, wp, out var err))
            {
                Console.LogError($"[waypoint] Save failed: {err}");
                return false;
            }

            CellSceneCache.Invalidate(path);
            Viewport.ActiveCell = CellLoader.Load(path);
            Console.Log($"[waypoint] Saved '{name}' (tool #{actTool}, base #{actBase}, preferJoints=true).");
            Console.Log(string.Format(inv,
                "  TCP ({0:F1}, {1:F1}, {2:F1}) A={3:F3} B={4:F3} C={5:F3}",
                x, y, z, a, b, c));
            Console.Log(string.Format(inv,
                "  joints A1={0:F2} A2={1:F2} A3={2:F2} A4={3:F2} A5={4:F2} A6={5:F2} E1={6:F2}",
                j[0], j[1], j[2], j[3], j[4], j[5], e1));
            return true;
        }
        catch (Exception ex)
        {
            Console.LogError($"[waypoint] {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>
    /// Teach current live pose as the scan/bed calibration waypoint
    /// (<c>scanner-down-bed</c>, tags <c>scan-cal</c> + <c>bed-cal</c>).
    /// </summary>
    public async Task<bool> MarkScanPositionAsync()
    {
        Console.Log("[scan-pose] Marking current pose as scan position (scanner-down-bed)…");
        bool ok = await SaveWaypointFromRobotAsync(
            "scanner-down-bed",
            "Scanner aimed down on bed — taught live for scan-cal + bed-cal",
            ["scan-cal", "bed-cal"]);
        if (ok)
            Console.Log("[scan-pose] Done. Auto-cal will prefer this pose (skip move if already near).");
        return ok;
    }

    /// <summary>
    /// Teach current live joints as a named home (default <c>Home</c>) and a recall waypoint.
    /// Mirrors KUKA XHOME intent: joint PTP home, stored in the cell for MassiveDRIVE go-home.
    /// </summary>
    public async Task<bool> MarkHomePositionAsync(string name = "Home")
    {
        name = string.IsNullOrWhiteSpace(name) ? "Home" : name.Trim();
        if (Viewport.ActiveCellPath is not { } path)
        {
            Console.LogError("[home] No active cell.");
            return false;
        }

        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected)
        {
            Console.LogError("[home] Robot not connected — Sync first.");
            return false;
        }

        robot.PauseStreaming();
        try
        {
            var axes = await robot.ReadAxesAsync();
            if (axes is not { Length: >= 6 })
            {
                Console.LogError("[home] Could not read $AXIS_ACT.");
                return false;
            }

            var angles = new float[]
            {
                (float)axes[0], (float)axes[1], (float)axes[2],
                (float)axes[3], (float)axes[4], (float)axes[5],
            };

            var data = CellLoader.LoadPositionData(path);
            int idx = data.Positions.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var cfg = new HomePositionConfig { Name = name, Angles = angles };
            if (idx >= 0) data.Positions[idx] = cfg;
            else data.Positions.Add(cfg);
            data.Default = name;
            CellLoader.SavePositionData(path, data);

            // Viewport / additive dropdown
            Viewport.AdditiveSettings?.AddHomePosition(name, angles);
            if (Viewport.AdditiveSettings is not null)
                Viewport.AdditiveSettings.SelectedHomePositionName = name;
            robot.SetNextPositionName(data.Positions.Count + 1);

            // Also a joint waypoint for MassiveDRIVE go (includes E1 when available)
            await SaveWaypointFromRobotAsync(
                "home",
                $"Home taught live ({name}) — PTP via MassiveDRIVE",
                ["home", "xhome"]);

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Console.Log($"[home] Saved '{name}' as default home + waypoint 'home'.");
            Console.Log(string.Format(inv,
                "  A1={0:F2} A2={1:F2} A3={2:F2} A4={3:F2} A5={4:F2} A6={5:F2} E1={6:F2}",
                axes[0], axes[1], axes[2], axes[3], axes[4], axes[5],
                axes.Length > 6 ? axes[6] : 0));
            Console.Log("[home] Go with: home go  ·  UI GO HOME  ·  waypoint go home");
            return true;
        }
        catch (Exception ex)
        {
            Console.LogError($"[home] {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>
    /// Go home via MassiveDRIVE: prefer controller <c>SPTP XHOME</c> (<c>/api/motion/home</c>),
    /// else bulk LIN to taught waypoint <c>home</c> TCP.
    /// </summary>
    public async Task GoToSavedHomeAsync(int velPct = 20)
    {
        var robot = RightPanel.Settings.Robot;
        if (!await EnsureMassiveDriveReadyForCalibrationAsync("[home]"))
            return;

        velPct = Math.Clamp(velPct, 1, 30);
        var url = MassiveDriveUrlOrNull();
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.LogError("[home] No massiveDriveUrl.");
            return;
        }

        if (robot.IsConnected)
            robot.PauseStreaming();
        try
        {
            // Primary: controller XHOME (same as Drive UI Home button)
            Console.Log("[home] MassiveDRIVE SPTP XHOME…");
            using (var client = new MassiveDriveClient(url, TimeSpan.FromMinutes(3)))
            {
                try
                {
                    using var doc = await client.GoHomeAsync();
                    if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
                    {
                        Console.Log("[home] At controller XHOME.");
                        await ReadPoseForCalAsync(robot, "[home]");
                        return;
                    }
                    var raw = doc.RootElement.GetRawText();
                    Console.Log($"[home] XHOME API: {raw[..Math.Min(200, raw.Length)]}");
                }
                catch (Exception ex)
                {
                    Console.Log($"[home] XHOME API failed ({ex.Message}) — trying bulk to waypoint…");
                }
            }

            var cell = ActiveCellConfig();
            CellWaypointConfig? wp = cell is not null
                ? CellLoader.FindWaypoint(cell, "home") ?? CellLoader.FindWaypointByTag(cell, "home")
                : null;
            if (wp is not null)
            {
                double speed = Math.Clamp(velPct * 1.5, 15, 40);
                Console.Log($"[home] Bulk LIN → waypoint '{wp.Name}' @ {speed:F0} mm/s…");
                bool ok = await DriveMovePoseAsync(
                    wp.TcpX, wp.TcpY, wp.TcpZ, wp.TcpA, wp.TcpB, wp.TcpC,
                    wp.Joints is { Length: >= 7 } j ? j[6] : null,
                    speed, wp.Tool, wp.Base, "[home]");
                if (ok)
                {
                    await ReadPoseForCalAsync(robot, "[home]");
                    Console.Log("[home] At taught home waypoint.");
                }
                return;
            }

            Console.LogError("[home] No XHOME response and no 'home' waypoint — use Drive UI Home or MARK HOME.");
        }
        finally
        {
            if (robot.IsConnected)
                robot.ResumeStreaming();
        }
    }

    /// <summary>Resets the viewport robot to the selected home preset (no real-robot move).</summary>
    public string ApplyViewportHome()
    {
        var home = Viewport.AdditiveSettings?.SelectedHomeAngles
                   ?? [0f, -90f, 90f, 0f, 15f, 0f];
        RightPanel.Settings.Robot.ApplyViewportJoints(home);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Format(inv,
            "[viewport-home] A1={0:F2} A2={1:F2} A3={2:F2} A4={3:F2} A5={4:F2} A6={5:F2}",
            home[0], home[1], home[2], home[3], home[4], home[5]);
    }

    /// <summary>Switches to a cell whose display name matches <paramref name="name"/>
    /// (e.g. "LFAM 3" / "lfam3"). For the console / control bridge. Call on the UI thread.</summary>
    public string SwitchCellByName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return "[cell] usage: cell <name> [--home]";

        bool resetHome = false;
        var parts = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1
            && (parts[^1].Equals("--home", StringComparison.OrdinalIgnoreCase)
                || parts[^1].Equals("home", StringComparison.OrdinalIgnoreCase)))
        {
            resetHome = true;
            name = string.Join(' ', parts[..^1]);
        }

        var names = LeftPanel.CellNames;
        int idx = -1;
        for (int i = 0; i < names.Count; i++)
            if (names[i].Contains(name, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        if (idx < 0) // retry ignoring spaces, so "lfam3" matches "LFAM 3"
        {
            string norm = name.Replace(" ", "");
            for (int i = 0; i < names.Count; i++)
                if (names[i].Replace(" ", "").Contains(norm, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        }
        if (idx < 0)
            return $"[cell] no cell matching '{name}'. Available: {string.Join(", ", names)}";
        if (LeftPanel.SelectedCellIndex == idx)
        {
            if (resetHome) return ApplyViewportHome();
            return $"[cell] already on {names[idx]}. (append --home to reset viewport pose)";
        }

        LeftPanel.SelectedCellIndex = idx; // fires the async cell load
        return $"[cell] switching to {names[idx]}…";
    }

    /// <summary>Syncs (connects) the robot over C3Bridge if not already connected.</summary>
    public string SyncRobot()
    {
        var r = RightPanel.Settings.Robot;
        if (r.IsConnected) return "[sync] already synced.";
        r.ConnectCommand.Execute(null); // ToggleConnect â†’ connect (async)
        return "[sync] connectingâ€¦";
    }

    /// <summary>Desyncs (disconnects) the robot if connected.</summary>
    public string DesyncRobot()
    {
        var r = RightPanel.Settings.Robot;
        if (!r.IsConnected) return "[sync] already desynced.";
        r.Desync();
        return "[sync] desynced.";
    }

    /// <summary>Clears user models and starts a fresh unsaved workspace.</summary>
    public void NewWorkspace()
    {
        _suppressWorkspaceDirty = true;
        try
        {
            Viewport.ClearUserScene();
            Viewport.Erp.ClearAttachment();
            UndoRedo.Clear();
            AppPreferences.LastWorkspacePath = null;
            PreferencesLoader.Save(AppPreferences);
            StatusBar.FileStatus = "Untitled";
            ClearWorkspaceDirty();
        }
        finally
        {
            _suppressWorkspaceDirty = false;
        }

        // Reset to a fresh cell: rebuild the active cell scene (robot/bed back to home pose).
        // Falls back to the first discovered cell if none is active.
        if (Viewport.ActiveCellPath is { Length: > 0 } cellPath)
            Viewport.OnDevCellReloadRequested?.Invoke(cellPath);
        else if (LeftPanel.DiscoveredCellPaths.Count > 0)
            LeftPanel.OnCellSelected?.Invoke(LeftPanel.DiscoveredCellPaths[0]);

        Console.Log("[workspace] New workspace — scene cleared, cell reloaded.");
    }

    /// <summary>Imports a model file into the scene and logs material diagnostics.</summary>
    /// <summary>
    /// Imports a KUKA KRL (.src) program's Cartesian motion as a scrubbable toolpath in the outliner.
    /// Positions are placed in world space using the active cell's robroot + base offset (inverse of
    /// the exporter). Logs success/failure to the console.
    /// </summary>
    public bool ImportKrlToolpath(string path)
    {
        try
        {
            path = System.IO.Path.GetFullPath(path);
            if (!System.IO.File.Exists(path))
            {
                Console.LogError($"[krl] File not found: {path}");
                return false;
            }

            var text = System.IO.File.ReadAllText(path);
            var off  = System.Numerics.Vector3.Zero;
            if (Viewport.ActiveCell is { } cell)
                off = new System.Numerics.Vector3(
                    cell.Robot.WorldPosition.X + cell.Bed.BaseData.X,
                    cell.Robot.WorldPosition.Y + cell.Bed.BaseData.Y,
                    cell.Robot.WorldPosition.Z + cell.Bed.BaseData.Z);
            else
                Console.Log("[krl] No active cell â€” placing the toolpath in raw KRL base coordinates.");

            var tp = KrlToolpathParser.Parse(text, off, out int moves);
            if (moves == 0)
            {
                Console.LogError($"[krl] No Cartesian LIN/PTP moves found in {System.IO.Path.GetFileName(path)} â€” " +
                                 "nothing to display (joint-only programs like calibration sweeps aren't toolpaths).");
                return false;
            }

            var name = $"KRL: {System.IO.Path.GetFileNameWithoutExtension(path)}";
            Viewport.AddImportedToolpath(tp, name);
            Console.Log($"[krl] Imported {moves} moves from {System.IO.Path.GetFileName(path)} â†’ \"{name}\". " +
                        "Select it in the outliner to scrub the toolpath.");
            return true;
        }
        catch (Exception ex)
        {
            Console.LogError($"[krl] Failed to import {System.IO.Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    public bool ReloadOutlinerModel(MassiveSlicer.Viewport.Scene.SceneNode node)
    {
        var path = OutlinerModelOps.ResolveSourceFilePath(node);
        if (path is null)
        {
            Console.LogError($"[import] No source file to reload for '{node.Name}'.");
            return false;
        }

        if (!Viewport.ReloadModel(node, path))
        {
            Console.LogError($"[import] Failed to reload '{node.Name}' from '{path}'.");
            return false;
        }

        Console.Log($"[import] Reloaded '{node.Name}' from '{System.IO.Path.GetFileName(path)}'.");
        return true;
    }

    public bool ReplaceOutlinerModel(MassiveSlicer.Viewport.Scene.SceneNode node, string path)
    {
        if (!Viewport.ReloadModel(node, path))
        {
            Console.LogError($"[import] Failed to replace '{node.Name}' from '{path}'.");
            return false;
        }

        Console.Log($"[import] Replaced '{node.Name}' from '{System.IO.Path.GetFileName(path)}'.");
        return true;
    }

    public bool ImportModelFromPath(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!ImportHelper.IsSupported(path))
        {
            Console.LogError($"[import] Unsupported file type: {path}");
            return false;
        }

        if (!System.IO.File.Exists(path))
        {
            Console.LogError($"[import] File not found: {path}");
            return false;
        }

        // STEP tessellation is heavy (cascadio/OCCT subprocess); keep it off the UI thread.
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".stp" or ".step")
        {
            _ = ImportStepModelAsync(path);
            return true;
        }

        return FinishImport(path, loadSync: true);
    }

    async Task ImportStepModelAsync(string path)
    {
        var fileName = System.IO.Path.GetFileName(path);
        ShowBusy("Importing STEP", $"Tessellating {fileName}… (first run may install converter)");
        Console.Log($"[import] STEP load started: {fileName}");

        SceneNode? node = null;
        string? error = null;
        try
        {
            var cell = Viewport.ActiveCell;
            node = await Task.Run(() =>
            {
                try
                {
                    return ImportHelper.LoadAndPlace(path, cell, msg =>
                        Dispatcher.UIThread.Post(() => Console.Log(msg)));
                }
                catch (Exception ex)
                {
                    error = ex.ToString();
                    return null;
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }
        finally
        {
            HideBusy();
        }

        if (node is null)
        {
            Console.LogError($"[import] Failed to load '{path}'{(error is null ? "" : $": {error}")}.");
            return;
        }

        FinishImportWithNode(path, node);
    }

    bool FinishImport(string path, bool loadSync)
    {
        SceneNode? node;
        try
        {
            node = ImportHelper.LoadAndPlace(path, Viewport.ActiveCell, Console.Log);
        }
        catch (Exception ex)
        {
            Console.LogError($"[import] Failed to load '{path}': {ex.Message}");
            return false;
        }

        if (node is null)
        {
            Console.LogError($"[import] Failed to load '{path}'.");
            return false;
        }

        FinishImportWithNode(path, node);
        return true;
    }

    void FinishImportWithNode(string path, SceneNode node)
    {
        MarkWorkspaceDirty();
        // Inspect BEFORE enqueuing: AddImportNode hands the node to the GL upload thread,
        // which clears PendingMesh once uploaded -- for small meshes that can happen before
        // the inspector runs, making it report 0 verts. Summarize while the mesh is intact.
        var report = GltfImportInspector.InspectLoaded(node, path);
        foreach (var line in report.ToLogLines())
            Console.Log(line);

        Viewport.AddImportNode(node);
        StatusBar.FileStatus = System.IO.Path.GetFileName(path);

        // Guide the workflow: importing opens MODEL and SLICE.
        RightPanel.StepModelExpanded = true;
        RightPanel.StepSliceExpanded = true;

        Console.Log($"[import] Added '{node.Name}' to scene.");
    }

    /// <summary>Called when a cell load begins so workspace restore waits for the bed scene.</summary>
    internal void NotifyCellLoadStarted() => _cellSceneReady = false;

    /// <summary>
    /// Loads a <c>.mass</c> workspace from <paramref name="path"/> (File â†’ Open).
    /// Models restore only after the workspace cell scene (bed/robot) is ready.
    /// </summary>
    // -- Busy overlay (project open / slicing) ------------------------------------

    private bool   _busyOverlayVisible;
    private string _busyTitle  = "";
    private string _busyDetail = "";

    public bool BusyOverlayVisible
    {
        get => _busyOverlayVisible;
        set => SetField(ref _busyOverlayVisible, value);
    }

    public string BusyTitle
    {
        get => _busyTitle;
        set => SetField(ref _busyTitle, value);
    }

    public string BusyDetail
    {
        get => _busyDetail;
        set => SetField(ref _busyDetail, value);
    }

    private double _busyProgress;

    /// <summary>Overlay progress 0–100 (determinate).</summary>
    public double BusyProgress
    {
        get => _busyProgress;
        set => SetField(ref _busyProgress, Math.Clamp(value, 0.0, 100.0));
    }

    internal void ShowBusy(string title, string detail = "")
    {
        BusyTitle    = title;
        BusyDetail   = detail;
        BusyProgress = 0;
        BusyOverlayVisible = true;
    }

    internal void UpdateBusy(string detail) => BusyDetail = detail;

    internal void UpdateBusyProgress(double percent) => BusyProgress = percent;

    internal void HideBusy() => BusyOverlayVisible = false;

    public void OpenWorkspace(string path) => _ = OpenWorkspaceAsync(path);

    private async Task OpenWorkspaceAsync(string path)
    {
        path = PathNormalization.Normalize(path);
        ShowBusy("Opening Project", $"Reading {Path.GetFileName(path)}…");

        WorkspaceDocument? doc = null;
        try
        {
            // Parsing large workspaces (multi-million-move toolpaths) takes tens of
            // seconds — run it off the UI thread so the overlay stays responsive.
            // File read/parse owns 0–70% of the bar (byte-accurate).
            doc = await Task.Run(() => WorkspaceLoader.Load(path,
                f => Dispatcher.UIThread.Post(() => UpdateBusyProgress(f * 70.0))));
        }
        catch (Exception ex)
        {
            Console.Log($"[workspace] Load failed: {ex.Message}");
        }

        if (doc is null)
        {
            HideBusy();
            Console.Log($"[workspace] Failed to load '{path}'.");
            if (System.IO.File.Exists(path + ".bak"))
                Console.Log($"[workspace] A previous save exists at {System.IO.Path.GetFileName(path)}.bak — open it to recover.");
            return;
        }

        UpdateBusy("Preparing robot cell…");
        UpdateBusyProgress(75);
        _pendingWorkspaceRestore = (doc, path);
        _workspaceRestoreGeneration = 0;
        QueueWorkspaceRestoreAfterCellReady(doc);
    }

    private void QueueWorkspaceRestoreAfterCellReady(WorkspaceDocument doc)
    {
        string? resolved = WorkspaceCellPath.Resolve(doc.CellPath, LeftPanel.DiscoveredCellPaths);
        if (resolved is null)
        {
            if (doc.CellPath is { Length: > 0 } saved)
                Console.Log($"[workspace] Saved cell '{saved}' not found — using the active cell.");
            TryApplyPendingWorkspaceRestore(0);
            return;
        }

        bool cellReady = _cellSceneReady
                      && WorkspaceCellPath.Matches(doc.CellPath, Viewport.ActiveCellPath, LeftPanel.DiscoveredCellPaths);

        if (cellReady)
        {
            Console.Log($"[workspace] Cell {Path.GetFileNameWithoutExtension(resolved)} already active — restoring.");
            TryApplyPendingWorkspaceRestore(Viewport.AcceptedCellSwapGeneration);
            return;
        }

        int gen = Interlocked.Increment(ref _cellLoadRequestId);
        _workspaceRestoreGeneration = gen;
        Viewport.WorkspaceCellLoadGeneration = gen;
        Viewport.AcceptedCellSwapGeneration = gen - 1;

        Console.Log($"[workspace] Waiting for cell {Path.GetFileNameWithoutExtension(resolved)} before restore…");

        int idx = LeftPanel.FindCellIndex(resolved);
        if (idx >= 0)
        {
            if (idx != LeftPanel.SelectedCellIndex)
                LeftPanel.SelectedCellIndex = idx;
            else
                LeftPanel.OnCellSelected?.Invoke(resolved);
            return;
        }

        LeftPanel.OnCellSelected?.Invoke(resolved);
    }

    private void TryApplyPendingWorkspaceRestore(int generation)
    {
        if (_pendingWorkspaceRestore is not { } pending)
            return;

        if (_workspaceRestoreGeneration > 0 && generation != _workspaceRestoreGeneration)
            return;

        if (!WorkspaceCellPath.Matches(
                pending.Doc.CellPath,
                Viewport.ActiveCellPath,
                LeftPanel.DiscoveredCellPaths))
            return;

        string cellName = Viewport.ActiveCell?.Name ?? Path.GetFileNameWithoutExtension(Viewport.ActiveCellPath ?? "cell");
        _pendingWorkspaceRestore = null;
        _workspaceRestoreGeneration = 0;
        Viewport.WorkspaceCellLoadGeneration = null;

        UpdateBusy($"Restoring {pending.Doc.Models.Count} model(s) and toolpaths…");
        UpdateBusyProgress(85);
        // Defer one frame so the overlay text above repaints before the UI thread
        // is occupied by the (potentially long) restore.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                ApplyWorkspaceState(pending.Doc, pending.Path);
                Console.Log($"[workspace] Restored on {cellName}.");
                UpdateBusyProgress(100);
            }
            finally
            {
                HideBusy();
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Saves to <see cref="AppPreferences.LastWorkspacePath"/> when set.
    /// Returns <c>false</c> when no file is open yet (caller should run Save As).
    /// </summary>
    public bool TrySaveCurrentWorkspace()
    {
        // ERP-linked workspaces know their home: the attached project's
        // 06-Production Documents folder on the UNAS — no save dialog needed.
        if (ResolveErpProjectDocsFolder() is { } docs)
        {
            string fileName = AppPreferences.LastWorkspacePath is { Length: > 0 } lp
                ? System.IO.Path.GetFileName(lp)
                : ErpDefaultWorkspaceFileName();
            string target = System.IO.Path.Combine(docs, fileName);
            if (!string.Equals(target, AppPreferences.LastWorkspacePath, StringComparison.Ordinal))
                Console.Log($"[workspace] ERP-linked — saving to {target}");
            _ = SaveWorkspaceAsync(target, guardStalePath: true);
            return true;
        }

        if (AppPreferences.LastWorkspacePath is not { Length: > 0 } path)
            return false;

        _ = SaveWorkspaceAsync(path, guardStalePath: true);
        return true;
    }

    /// <summary>Front-inserts into File → Open Recent (deduped, capped at 10).</summary>
    private void RecordRecentWorkspace(string path)
    {
        var list = AppPreferences.RecentWorkspaces;
        list.RemoveAll(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > 10)
            list.RemoveRange(10, list.Count - 10);
        Toolbar.SetRecentWorkspaces(list);
    }

    /// <summary>The attached project/lead's "06-Production Documents" folder, found by
    /// matching the attachment number against folder names under the UNAS projects
    /// root (e.g. "26-173 - studio JEFRE llc - …"). Null when unattached, the share
    /// is offline, or no folder starts with the number.</summary>
    internal string? ResolveErpProjectDocsFolder()
    {
        var att = Viewport.Erp.Attachment;
        if (att is null || string.IsNullOrWhiteSpace(att.Number)) return null;

        try
        {
            string root = AppPreferences.UnasProjectsRoot;
            if (!System.IO.Directory.Exists(root)) return null;
            var match = System.IO.Directory.EnumerateDirectories(root).FirstOrDefault(d =>
                System.IO.Path.GetFileName(d).StartsWith(att.Number, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Console.Log($"[workspace] no project folder starting with '{att.Number}' under {root}.");
                return null;
            }
            string docs = System.IO.Path.Combine(match, "06-Production Documents");
            System.IO.Directory.CreateDirectory(docs);
            return docs;
        }
        catch (Exception ex)
        {
            Console.Log($"[workspace] project folder lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Lists saved .mass workspaces in the attached project's documents folder,
    /// newest first — the ERP dock offers them so an existing element's work can be
    /// loaded and iterated (each new send registers the next rev).</summary>
    internal IReadOnlyList<(string Path, string Name, string Detail)> FindErpWorkspaceFiles()
    {
        try
        {
            if (ResolveErpProjectDocsFolder() is not { } docs) return [];
            return System.IO.Directory.EnumerateFiles(docs, "*.mass", System.IO.SearchOption.TopDirectoryOnly)
                .Select(f => new System.IO.FileInfo(f))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Take(12)
                .Select(fi => (
                    fi.FullName,
                    System.IO.Path.GetFileNameWithoutExtension(fi.Name),
                    $"{fi.LastWriteTime:MMM d, HH:mm} · {fi.Length / (1024.0 * 1024.0):0.#} MB"))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Log($"[erp] workspace scan failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>"yyyy_MMdd - <first model>.mass" (their file convention) for the first
    /// save of a fresh ERP-linked workspace.</summary>
    private string ErpDefaultWorkspaceFileName()
    {
        var model = Viewport.EnumerateUserModelItems().FirstOrDefault();
        return RobotKrlPaths.SuggestedFileName(model?.Node.Name) + ".mass";
    }

    private bool _workspaceSaveInProgress;

    /// <summary>
    /// Saves all outliner models, camera, cell, and settings to <paramref name="path"/>.
    /// Toolpath serialization runs off the UI thread so large slices do not freeze the app.
    /// <paramref name="guardStalePath"/> is set by implicit Save (which targets the
    /// remembered LastWorkspacePath): it refuses to overwrite a file that has models when
    /// the current scene is empty. Explicit Save As paths skip the guard.
    /// </summary>
    public async Task SaveWorkspaceAsync(string path, bool guardStalePath = false)
    {
        if (_workspaceSaveInProgress)
        {
            Console.Log("[workspace] Save already in progress.");
            return;
        }

        if (!path.EndsWith(".mass", StringComparison.OrdinalIgnoreCase))
            path += ".mass";

        _workspaceSaveInProgress = true;
        try
        {
            PersistSettings();
            Console.Log("[workspace] Saving…");

            var capture = WorkspaceService.Capture(Viewport, RightPanel, AppPreferences, path);
            int toolpathCount = capture.ToolpathEntries.Count;
            int modelCount    = capture.Document.Models.Count;

            // A stale LastWorkspacePath plus an empty scene must never clobber a real
            // workspace file (models are unrecoverable without NAS snapshots).
            if (guardStalePath && modelCount == 0 &&
                await Task.Run(() => WorkspaceService.FileHasModels(path)))
            {
                Console.LogError(
                    $"[workspace] Refusing to save: the scene is empty but {System.IO.Path.GetFileName(path)} " +
                    "contains models. Use Save As to overwrite it intentionally.");
                return;
            }

            await Task.Run(() => WorkspaceService.FinalizeAndSave(capture, path));

            AppPreferences.LastWorkspacePath = path;
            RecordRecentWorkspace(path);
            Viewport.Erp.RefreshWorkspaceCandidates();
            PreferencesLoader.Save(AppPreferences);
            StatusBar.FileStatus = Path.GetFileName(path);
            ClearWorkspaceDirty();
            Console.Log(toolpathCount > 0
                ? $"[workspace] Saved {modelCount} model(s) and {toolpathCount} toolpath(s) to {path}"
                : $"[workspace] Saved {modelCount} model(s) and settings to {path}");
            if (RightPanel.Additive.SelectedPreset is { } mp)
                Console.Log($"[workspace] Material preset '{mp.Name}' saved with the workspace.");
        }
        catch (Exception ex)
        {
            Console.LogError($"[workspace] Save failed: {ex.Message}");
        }
        finally
        {
            _workspaceSaveInProgress = false;
        }
    }

    /// <summary>Fire-and-forget wrapper for callers that cannot await.</summary>
    public void SaveWorkspace(string path) => _ = SaveWorkspaceAsync(path);

    /// <summary>
    /// Suggested filename stem for the Save As dialog (last save or default).
    /// Extension-less: the dialog's DefaultExtension adds ".mass" — including it
    /// here produced doubled extensions (".mass.mass") on macOS.
    /// </summary>
    internal string SuggestedWorkspaceFileName =>
        AppPreferences.LastWorkspacePath is { } last
            ? System.IO.Path.GetFileNameWithoutExtension(last)
            : "workspace";

    private void ApplyWorkspaceState(WorkspaceDocument doc, string workspacePath)
    {
        _applyingUndoRedo = true;
        _suppressWorkspaceDirty = true;
        try
        {
            ApplyWorkspaceStateCore(doc, workspacePath);
            ClearWorkspaceDirty();
        }
        finally
        {
            _applyingUndoRedo = false;
            _suppressWorkspaceDirty = false;
            PersistSettings();
            _lastCommittedPrefsJson = CapturePrefsJson();
        }
    }

    private void ApplyWorkspaceStateCore(WorkspaceDocument doc, string workspacePath)
    {
        // Pause realtime slice *before* prefs hit AdditiveSettings PropertyChanged, so
        // baked calibration toolpaths are not immediately re-sliced away on open.
        if (ShouldPauseRealtimeSlicingOnOpen(doc))
        {
            Viewport.RealtimeSlicingPaused = true;
            Console.Log("[workspace] Real-time slicing paused (baked toolpath / workspace flag).");
        }

        CopyPreferences(doc.Settings);
        SyncViewportFromPrefs();
        // UiSession can override Settings for helper visibility (saved after prefs snapshot
        // quirks; older mass files leave this null and keep the Settings value).
        if (doc.UiSession?.XBracingShowHelper is bool showXHelper)
            RightPanel.Additive.XBracingShowHelper = showXHelper;
        RestoreMaterialPresetSelection(doc.Settings.SelectedMaterialPresetName,
            keepCurrentWhenMissing: true);

        if (Enum.TryParse<RightPanelTab>(doc.RightPanelTab, out var tab))
            RightPanel.ActiveTab = tab;

        Viewport.Erp.RestoreAttachment(doc.Erp);

        int restoredCount = WorkspaceService.RestoreModels(doc, Viewport, workspacePath);
        Viewport.FlattenScansToBedGroup();

        if (doc.Camera is { } camera)
            Viewport.ApplyCameraState?.Invoke(camera);

        // Robot pose (incl. E1) saved with the workspace — reapply so the scene
        // comes back exactly as it was. A restored scrub session may later re-pose
        // A1–A6 from IK, which is correct; E1 sticks.
        if (doc.UiSession?.RobotJoints is { Length: >= 7 } rj && Viewport.Robot is { } robotVm)
        {
            robotVm.A1 = rj[0]; robotVm.A2 = rj[1]; robotVm.A3 = rj[2];
            robotVm.A4 = rj[3]; robotVm.A5 = rj[4]; robotVm.A6 = rj[5];
            robotVm.E1 = rj[6];
        }

        Viewport.RestoreSimCameraKeyframes(doc.UiSession?.SimCameraKeyframes);

        // Queue UI session restore (edit mode / tool / layer isolation). Applied once
        // pending toolpath uploads finish so the scrub range and nodes exist.
        Viewport.PendingUiSession = doc.UiSession;
        if (doc.UiSession is not null && Viewport.PendingToolpath.IsEmpty)
            Viewport.RequestApplyPendingUiSession?.Invoke();

        AppPreferences.LastWorkspacePath = workspacePath;
        RecordRecentWorkspace(workspacePath);
        StatusBar.FileStatus = Path.GetFileName(workspacePath);
        SyncKrlFrameIndicesToActiveTab();
        PreferencesLoader.Save(AppPreferences);
        Console.Log(restoredCount == doc.Models.Count
            ? $"[workspace] Restored {restoredCount} model(s) from {workspacePath}"
            : $"[workspace] Restored {restoredCount} of {doc.Models.Count} model(s) from {workspacePath} (some meshes missing).");
    }

    /// <summary>
    /// True when the workspace asks for pause, or any saved toolpath is marked BAKED
    /// (Start/Stop calibration matrix — re-slice would destroy per-cell settings).
    /// </summary>
    private static bool ShouldPauseRealtimeSlicingOnOpen(WorkspaceDocument doc)
    {
        if (doc.UiSession?.RealtimeSlicingPaused == true)
            return true;

        foreach (var model in doc.Models)
        {
            foreach (var tp in model.Toolpaths)
            {
                if (!string.IsNullOrEmpty(tp.Name)
                    && tp.Name.Contains("BAKED", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private void OnSettingsChanged()
    {
        if (_applyingUndoRedo) return;
        PersistSettings();
        ScheduleSettingsUndo();
    }

    private void ScheduleSettingsUndo()
    {
        if (_applyingUndoRedo) return;

        _settingsUndoDebounce?.Cancel();
        _settingsUndoDebounce = new CancellationTokenSource();
        var token = _settingsUndoDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                Dispatcher.UIThread.Post(CommitSettingsUndo);
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    private void CommitSettingsUndo()
    {
        if (_applyingUndoRedo) return;

        PersistSettings();
        var after = CapturePrefsJson();
        if (!string.Equals(_lastCommittedPrefsJson, after, StringComparison.Ordinal))
        {
            var before = _lastCommittedPrefsJson;
            UndoRedo.Push(new SettingsUndoAction(before, after, ApplyPrefsFromJson, "Settings"));
            _lastCommittedPrefsJson = after;
        }
    }

    private string CapturePrefsJson()
        => JsonSerializer.Serialize(AppPreferences, PrefsJsonOptions);

    private void ApplyPrefsFromJson(string json)
    {
        var copy = JsonSerializer.Deserialize<AppPreferences>(json, PrefsJsonOptions);
        if (copy is null) return;

        _applyingUndoRedo = true;
        try
        {
            CopyPreferences(copy);
            SyncViewportFromPrefs();
            RestoreMaterialPresetSelection(copy.SelectedMaterialPresetName);
            PersistSettings();
            _lastCommittedPrefsJson = json;
        }
        finally
        {
            _applyingUndoRedo = false;
        }
    }

    private void CopyPreferences(AppPreferences src)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(src);
        var copy = System.Text.Json.JsonSerializer.Deserialize<AppPreferences>(json);
        if (copy is null) return;

        // Preserve navigation preset wiring not stored in workspace snapshots.
        copy.ActivePreset = src.ActivePreset;
        AppPreferences.SelectedMaterialPresetName = copy.SelectedMaterialPresetName;

        // Overwrite scalar/collection fields onto the live instance.
        var live = AppPreferences;
        live.AutoDepth              = copy.AutoDepth;
        live.OrbitAroundSelection   = copy.OrbitAroundSelection;
        live.AntiAliasing           = copy.AntiAliasing;
        live.ActiveTheme            = copy.ActiveTheme;
        live.DefaultBackdropPath    = copy.DefaultBackdropPath;
        live.DefaultBackdropBlur     = copy.DefaultBackdropBlur;
        live.DefaultBackdropOpacity  = copy.DefaultBackdropOpacity;
        live.ShowGrid               = copy.ShowGrid;
        live.ShowAxes               = copy.ShowAxes;
        live.ShowBedGrid            = copy.ShowBedGrid;
        live.DefaultHomePositionNames = copy.DefaultHomePositionNames;
        live.SelectedMaterialPresetName = copy.SelectedMaterialPresetName;
        live.LightAzimuth           = copy.LightAzimuth;
        live.LightElevation         = copy.LightElevation;
        live.LightIntensity         = copy.LightIntensity;
        live.ShaderMode             = copy.ShaderMode;
        live.ShowEdges              = copy.ShowEdges;
        live.ShadowCatcherEnabled      = copy.ShadowCatcherEnabled;
        live.ContactShadowSize         = copy.ContactShadowSize;
        live.ContactShadowDarkness     = copy.ContactShadowDarkness;
        live.ContactShadowBlur         = copy.ContactShadowBlur;
        live.CavityEnabled             = copy.CavityEnabled;
        live.CavityMode                = copy.CavityMode;
        live.CavityScreenRidge         = copy.CavityScreenRidge;
        live.CavityScreenValley        = copy.CavityScreenValley;
        live.CavityWorldRidge          = copy.CavityWorldRidge;
        live.CavityWorldValley         = copy.CavityWorldValley;
        live.CavityWorldDistance       = copy.CavityWorldDistance;
        live.ToolpathExtrudeColor    = copy.ToolpathExtrudeColor;
        live.ToolpathTravelColor     = copy.ToolpathTravelColor;
        live.ToolpathSeamColor       = copy.ToolpathSeamColor;
        live.ToolpathUnselectedColor = copy.ToolpathUnselectedColor;
        live.ToolpathWipeColor       = copy.ToolpathWipeColor;
        live.ToolpathRetractionColor = copy.ToolpathRetractionColor;
        live.ZHopMm                  = copy.ZHopMm;
        live.WipeModeDisplay         = MigrateWipeModeDisplay(copy.WipeModeDisplay);
        live.WipeLengthMm            = copy.WipeLengthMm;
        live.WipeRampMm              = copy.WipeRampMm;
        live.WipeSpeed               = copy.WipeSpeed;
        live.WipeSkipShortTravels    = copy.WipeSkipShortTravels;
        live.ExtrusionStartWaitSec   = copy.ExtrusionStartWaitSec;
        live.ExtrusionResumeWaitSec  = copy.ExtrusionResumeWaitSec;
        live.SsPreTravelWaitSec      = copy.SsPreTravelWaitSec;
        live.SsResumePrimePercent    = copy.SsResumePrimePercent;
        live.DigitalStartStopEnabled = copy.DigitalStartStopEnabled;
        live.ResumeRampEnabled         = copy.ResumeRampEnabled;
        live.ResumeRampStartSpeed      = copy.ResumeRampStartSpeed;
        live.ResumeRampStartRpmPercent = copy.ResumeRampStartRpmPercent;
        live.ResumeRampDistanceMm      = copy.ResumeRampDistanceMm;
        live.ResumeRampSteps           = copy.ResumeRampSteps;
        live.LayerSpeedAdaptEnabled    = copy.LayerSpeedAdaptEnabled;
        live.LayerSpeedBasisDisplay    = copy.LayerSpeedBasisDisplay;
        live.LayerSpeedMinMmS          = copy.LayerSpeedMinMmS;
        live.LayerSpeedMaxMmS          = copy.LayerSpeedMaxMmS;
        live.SeamGuidePoints         = copy.SeamGuidePoints;
        live.PaintMarks              = copy.PaintMarks;
        live.StructuralSupports      = copy.StructuralSupports;
        live.CurvedBoundarySource       = copy.CurvedBoundarySource;
        live.CurvedAutoDetectBandMm     = copy.CurvedAutoDetectBandMm;
        live.CurvedEnableRegionSplit    = copy.CurvedEnableRegionSplit;
        live.CurvedBoundaryLowVertices  = [.. copy.CurvedBoundaryLowVertices];
        live.CurvedBoundaryHighVertices = [.. copy.CurvedBoundaryHighVertices];
        live.LayerHeight            = copy.LayerHeight;
        live.BeadWidth              = copy.BeadWidth;
        live.FirstLayerHeight       = copy.FirstLayerHeight;
        live.AdaptiveLayerHeight    = copy.AdaptiveLayerHeight;
        live.AdaptiveQuality        = copy.AdaptiveQuality;
        live.MinLayerHeight         = copy.MinLayerHeight;
        live.DisableContourOffset   = copy.DisableContourOffset;
        live.SeamMode               = copy.SeamMode;
        live.ZigZagAllowSameLayerTravel = copy.ZigZagAllowSameLayerTravel;
        live.OverhangOrientation    = copy.OverhangOrientation;
        live.MaxOverhangTiltDeg     = copy.MaxOverhangTiltDeg;
        live.SmoothRotation                = copy.SmoothRotation;
        live.SmoothRotationRadius          = copy.SmoothRotationRadius;
        live.SmoothRotationMaxRateDegPerMm = copy.SmoothRotationMaxRateDegPerMm;
        live.InfillPattern          = NormalizeInfillPatternLabel(copy.InfillPattern);
        live.InfillSpacingMm        = copy.InfillSpacingMm;
        live.InfillAngleDeg         = copy.InfillAngleDeg;
        live.LightningOverhangDeg     = copy.LightningOverhangDeg;
        live.LightningBranchSpacingMm = copy.LightningBranchSpacingMm;
        live.LightningTipLoopRadiusMm = copy.LightningTipLoopRadiusMm;
        live.LightningAnchorInterior  = copy.LightningAnchorInterior;
        live.LightningExteriorOverhangs = copy.LightningExteriorOverhangs;
        live.LightningAnchorExterior  = copy.LightningExteriorOverhangs;
        live.LightningButtressBarMm         = copy.LightningButtressBarMm;
        live.LightningPreferInteriorMouths  = copy.LightningPreferInteriorMouths;
        live.LightningTargetSupportSelections = copy.LightningTargetSupportSelections;
        live.MultiPlanarPlanes = copy.MultiPlanarPlanes.Select(a => (double[])a.Clone()).ToList();
        live.MultiPlanarAxisX  = copy.MultiPlanarAxisX;
        live.BrimEnabled            = copy.BrimEnabled;
        live.BrimLoops              = copy.BrimLoops;
        live.XBracingEnabled        = copy.XBracingEnabled;
        live.XBracingDepthMm        = copy.XBracingDepthMm;
        live.XBracingDepthBottomMm  = copy.XBracingDepthBottomMm;
        live.XBracingDepthEaseBottom = copy.XBracingDepthEaseBottom;
        live.XBracingDepthEaseTop   = copy.XBracingDepthEaseTop;
        live.XBracingSpanMm         = copy.XBracingSpanMm;
        live.XBracingAngleDeg       = copy.XBracingAngleDeg;
        live.XBracingExtendEdges    = copy.XBracingExtendEdges;
        live.XBracingShowHelper     = copy.XBracingShowHelper;
        live.XBracingPlaneTiltY     = copy.XBracingPlaneTiltY;
        live.XBracingPlaneTiltX     = copy.XBracingPlaneTiltX;
        live.XBracingProjectionType = copy.XBracingProjectionType;
        live.XBracingCylinderDiameterMm = copy.XBracingCylinderDiameterMm;
        live.XBracingCylinderX      = copy.XBracingCylinderX;
        live.XBracingCylinderY      = copy.XBracingCylinderY;
        live.XBracingCylinderFlipDirection = copy.XBracingCylinderFlipDirection;
        live.WaveEffect             = copy.WaveEffect;
        live.WaveAmplitude          = copy.WaveAmplitude;
        live.WaveFrequencyMode      = copy.WaveFrequencyMode;
        live.WaveWavelength         = copy.WaveWavelength;
        live.WaveCycles             = copy.WaveCycles;
        live.WaveShape              = copy.WaveShape;
        live.WaveStagger            = copy.WaveStagger;
        live.WavePhaseMethodIndex   = copy.WavePhaseMethodIndex;
        live.WaveGradient           = copy.WaveGradient;
        live.WaveAmplitudeBottom    = copy.WaveAmplitudeBottom;
        live.WaveAmplitudeTop       = copy.WaveAmplitudeTop;
        live.WaveWavelengthBottom   = copy.WaveWavelengthBottom;
        live.WaveWavelengthTop      = copy.WaveWavelengthTop;
        live.WaveGradientCenter     = copy.WaveGradientCenter;
        live.WaveGradientCurve      = copy.WaveGradientCurve;
        live.TemperatureOffset      = copy.TemperatureOffset;
        live.ExtrusionSpeedOffset   = copy.ExtrusionSpeedOffset;
        live.PatternType            = copy.PatternType;
        live.PatternMapping         = copy.PatternMapping;
        live.PatternWavelengthMm    = copy.PatternWavelengthMm;
        live.PatternAmplitude       = copy.PatternAmplitude;
        live.PatternFrequency       = copy.PatternFrequency;
        live.PatternTwist           = copy.PatternTwist;
        live.PatternOffset          = copy.PatternOffset;
        live.PatternFadeIn          = copy.PatternFadeIn;
        live.PatternFadeOut         = copy.PatternFadeOut;
        live.SliceMethod            = copy.SliceMethod;
        live.SlicingMode            = copy.SlicingMode;
        live.PassAngle              = copy.PassAngle;
        live.TiltAngle              = copy.TiltAngle;
        live.TiltAngleX             = copy.TiltAngleX;
        live.PrintSpeed             = copy.PrintSpeed;
        live.FirstLayerAdjustmentsEnabled = copy.FirstLayerAdjustmentsEnabled;
        live.FirstLayerSpeed        = copy.FirstLayerSpeed;
        live.FirstLayerRpm          = copy.FirstLayerRpm;
        live.TravelSpeed            = copy.TravelSpeed;
        live.Acceleration           = copy.Acceleration;
        live.ApproachZ              = copy.ApproachZ;
        live.ToolDataIndex          = copy.ToolDataIndex;
        live.BaseDataIndex          = copy.BaseDataIndex;
        live.ToolheadA              = copy.ToolheadA;
        live.ToolheadB              = copy.ToolheadB;
        live.E1MotionEnabled        = copy.E1MotionEnabled;
        live.E1YPlusMm              = copy.E1YPlusMm;
        live.E1YMinusMm             = copy.E1YMinusMm;
        live.ToolheadC              = copy.ToolheadC;
        live.OrientationFollowPercent = copy.OrientationFollowPercent;
        live.OrientationMaxTiltDeg    = copy.OrientationMaxTiltDeg;
        live.FirstLayerZeroTilt       = copy.FirstLayerZeroTilt;
        live.LayerLeanPercent         = copy.LayerLeanPercent;
        live.LayerLeanMaxTiltDeg      = copy.LayerLeanMaxTiltDeg;
        live.OrientationLookAheadMm   = copy.OrientationLookAheadMm;
        live.OrientationSigmaMm       = copy.OrientationSigmaMm;
        live.ApoCvel                = copy.ApoCvel;
        live.ScanCameraIp           = copy.ScanCameraIp;
        live.ScanOutputDirectory    = copy.ScanOutputDirectory;
        live.ScanToolDataIndex      = copy.ScanToolDataIndex;
        live.ScanBaseDataIndex      = copy.ScanBaseDataIndex;
    }

    private void RestoreMaterialPresetSelection(
        string? presetName, bool keepCurrentWhenMissing = false)
    {
        if (string.IsNullOrEmpty(presetName))
        {
            // Older .mass files never recorded the preset — clearing here would also
            // wipe the global selection (the change handler persists prefs). Keep it.
            if (!keepCurrentWhenMissing)
                RightPanel.Additive.SelectedPresetIndex = -1;
            return;
        }

        int idx = RightPanel.Additive.MaterialPresets
            .Select((p, i) => (p, i))
            .FirstOrDefault(t => t.p.Name == presetName, (null!, -1)).i;
        if (idx >= 0)
            RightPanel.Additive.SelectedPresetIndex = idx;
        else
            Console.Log($"[workspace] Material preset '{presetName}' from the file "
                + "is not in the local preset library — selection left unchanged.");
    }

    /// <summary>
    /// Saves the current camera view into the active cell JSON (shared via the file, so every
    /// user opens to it) and refreshes the in-memory cell. Logs the result to the console.
    /// </summary>
    private void SaveCurrentView()
    {
        var view = Viewport.GetCameraState?.Invoke();
        var path = Viewport.ActiveCellPath;
        if (view is null || path is null)
        {
            Console.Log("[view] No active cell â€” load a cell before saving a view.");
            return;
        }
        CellLoader.SaveCameraView(path, view);
        Viewport.ActiveCell = CellLoader.Load(path);   // refresh in-memory model
        Console.Log($"[view] Saved view to {System.IO.Path.GetFileName(path)} " +
                    $"(azimuth {view.Azimuth:F0}Â°, elevation {view.Elevation:F0}Â°, radius {view.Radius:F0} mm). " +
                    "Restored on next load for all users.");
    }

    /// <summary>
    /// Canonical FILL PATTERN labels for the ComboBox / workspace round-trip.
    /// Maps legacy names and rejects unknown values so the selection sticks on reopen.
    /// </summary>
    private static string NormalizeInfillPatternLabel(string? raw) => raw switch
    {
        "Rectilinear" => "Rectilinear",
        "Grid" => "Grid",
        "Triangle" => "Triangle",
        "Ghost Mesh Grid" => "Ghost Mesh Grid",
        "Formbound Bridge" or "Lightning Bridge" => "Formbound Bridge",
        "Formbound Buttress" => "Formbound Buttress",
        "None" or null or "" => "None",
        _ => "None",
    };

    /// <summary>
    /// Copies all persisted settings from <see cref="AppPreferences"/> back into
    /// the live ViewModels. Called at startup and after the Preferences dialog applies.
    /// </summary>
    public void SyncViewportFromPrefs(bool includeSlicingSettings = true)
    {
        var p    = AppPreferences;
        var vp   = Viewport;
        var view = RightPanel.Settings.View;
        var add  = RightPanel.Additive;

        // Viewport visibility & navigation
        vp.ShowGrid            = p.ShowGrid;
        vp.ShowContactShadows       = p.ShadowCatcherEnabled;
        vp.ContactShadowSize        = p.ContactShadowSize;
        vp.ContactShadowDarkness    = p.ContactShadowDarkness;
        vp.ContactShadowBlur        = p.ContactShadowBlur;
        vp.CavityEnabled            = p.CavityEnabled;
        vp.CavityModeOption         = p.CavityMode;
        vp.CavityScreenRidge        = p.CavityScreenRidge;
        vp.CavityScreenValley       = p.CavityScreenValley;
        vp.CavityWorldRidge         = p.CavityWorldRidge;
        vp.CavityWorldValley        = p.CavityWorldValley;
        vp.CavityWorldDistance      = p.CavityWorldDistance;
        vp.ShowAxes     = p.ShowAxes;
        vp.ShowBedGrid  = p.ShowBedGrid;
        vp.ActivePreset = p.ActivePreset;
        vp.TouchpadPanSpeed   = p.TouchpadPanSpeed;
        vp.TouchpadOrbitSpeed = p.TouchpadOrbitSpeed;
        vp.TouchpadZoomSpeed  = p.TouchpadZoomSpeed;
        vp.TouchpadInvertPan  = p.TouchpadInvertPan;

        // Toolpath colors
        vp.BeadColor               = HexToVec3(p.ToolpathBeadColor);
        vp.ToolpathExtrudeColor    = HexToVec3(p.ToolpathExtrudeColor);
        vp.ToolpathTravelColor     = HexToVec3(p.ToolpathTravelColor);
        vp.ToolpathSeamColor       = HexToVec3(p.ToolpathSeamColor);
        vp.ToolpathUnselectedColor = HexToVec3(p.ToolpathUnselectedColor);
        vp.ToolpathWipeColor       = HexToVec3(p.ToolpathWipeColor);
        vp.ToolpathRetractionColor = HexToVec3(p.ToolpathRetractionColor);

        // Lighting
        vp.LightAzimuth   = p.LightAzimuth;
        vp.LightElevation = p.LightElevation;
        vp.LightIntensity = p.LightIntensity;

        // Shader mode
        if (Enum.TryParse<ShaderMode>(p.ShaderMode, out var sm))
            vp.ActiveShaderMode = sm;
        vp.LoadViewProfiles(p.ViewModeProfiles);

        // Backdrop
        if (p.DefaultBackdropPath is { } backdropPath)
        {
            var match = vp.AvailableBackdrops.FirstOrDefault(b =>
                AssetPaths.BackdropPathsEqual(b.Path, backdropPath));
            if (match is not null) vp.ActiveBackdrop = match;
        }
        vp.BackdropBlur     = p.DefaultBackdropBlur;
        vp.BackdropOpacity  = p.DefaultBackdropOpacity;

        // Theme: update swatch selection and apply visually
        if (Enum.TryParse<AppTheme>(p.ActiveTheme, out var theme))
        {
            view.ActiveTheme = theme;
            (Application.Current as MassiveSlicer.App.App)?.ApplyTheme(theme);
        }
        view.ShowEdges            = p.ShowEdges;
        view.ShadowCatcherEnabled = vp.ShowContactShadows;

        if (includeSlicingSettings) SyncSlicingSettingsFromPrefs();
        // Scan settings
        var scan = RightPanel.Scan;
        scan.CameraIp        = p.ScanCameraIp;
        scan.OutputDirectory = p.ScanOutputDirectory;
        scan.ToolDataIndex   = p.ScanToolDataIndex;
        scan.BaseDataIndex   = p.ScanBaseDataIndex;
    }

    /// <summary>
    /// Pushes persisted SLICING settings into the Additive panel. Separate from
    /// <see cref="SyncViewportFromPrefs"/> because these assignments raise PropertyChanged on
    /// AdditiveSettingsViewModel, which the realtime-slice watchlist reacts to — editing an
    /// unrelated preference (theme, touchpad invert) must not kick off a re-slice, and must not
    /// clobber slicing settings the user changed in the panel this session.
    /// </summary>
    private void SyncSlicingSettingsFromPrefs()
    {
        var p   = AppPreferences;
        var add = RightPanel.Additive;

        // Additive slicing settings
        add.LayerHeight      = p.LayerHeight;
        add.BeadWidth        = p.BeadWidth;
        add.FirstLayerHeight = p.FirstLayerHeight;
        add.AdaptiveLayerHeight = p.AdaptiveLayerHeight;
        add.AdaptiveQuality     = p.AdaptiveQuality;
        add.MinLayerHeight      = p.MinLayerHeight;
        add.DisableContourOffset = p.DisableContourOffset;
        add.SeamMode = add.SeamModeOptions.Contains(p.SeamMode) ? p.SeamMode : "Normal";
        add.ZigZagAllowSameLayerTravel = p.ZigZagAllowSameLayerTravel;
        add.OverhangOrientation = p.OverhangOrientation;
        add.MaxOverhangTiltDeg  = p.MaxOverhangTiltDeg;
        add.SmoothRotation                = p.SmoothRotation;
        add.SmoothRotationRadius          = p.SmoothRotationRadius;
        add.SmoothRotationMaxRateDegPerMm = p.SmoothRotationMaxRateDegPerMm;
        add.InfillPattern    = NormalizeInfillPatternLabel(p.InfillPattern);
        add.InfillSpacingMm  = p.InfillSpacingMm;
        add.InfillAngleDeg   = p.InfillAngleDeg;
        add.LightningOverhangDeg     = p.LightningOverhangDeg;
        add.LightningBranchSpacingMm = p.LightningBranchSpacingMm;
        add.LightningTipLoopRadiusMm = p.LightningTipLoopRadiusMm;
        // Affect Interior / Affect Exterior (UI checkboxes).
        add.LightningAffectInterior = p.LightningAnchorInterior;
        add.LightningAffectExterior = p.LightningExteriorOverhangs;
        add.LightningButtressBarMm         = p.LightningButtressBarMm;
        add.LightningPreferInteriorMouths  = p.LightningPreferInteriorMouths;
        add.LightningTargetSupportSelections = p.LightningTargetSupportSelections;
        add.MultiPlanarPlanes.Clear();
        foreach (var pair in p.MultiPlanarPlanes.Where(a => a is { Length: >= 2 }))
            add.MultiPlanarPlanes.Add(new MultiPlanarPlaneRow(pair[0], pair[1]));
        if (add.MultiPlanarPlanes.Count < 2)
        {
            add.MultiPlanarPlanes.Add(new MultiPlanarPlaneRow(0, 0));
            add.MultiPlanarPlanes.Add(new MultiPlanarPlaneRow(100, 30));
        }
        add.MultiPlanarAxisX = p.MultiPlanarAxisX;
        add.BumpMultiPlanarStamp();
        add.BrimEnabled         = p.BrimEnabled;
        add.BrimLoops           = p.BrimLoops;
        add.XBracingEnabled     = p.XBracingEnabled;
        add.XBracingDepthMm     = p.XBracingDepthMm;
        add.XBracingDepthBottomMm = p.XBracingDepthBottomMm;
        add.XBracingDepthEaseBottom = add.XBracingDepthEaseOptions.Contains(p.XBracingDepthEaseBottom)
            ? p.XBracingDepthEaseBottom : "Linear";
        add.XBracingDepthEaseTop = add.XBracingDepthEaseOptions.Contains(p.XBracingDepthEaseTop)
            ? p.XBracingDepthEaseTop : "Linear";
        add.XBracingSpanMm      = p.XBracingSpanMm;
        add.XBracingAngleDeg    = p.XBracingAngleDeg;
        add.XBracingExtendEdges = p.XBracingExtendEdges;
        add.XBracingShowHelper = p.XBracingShowHelper;
        add.XBracingPlaneTiltY  = p.XBracingPlaneTiltY;
        add.XBracingPlaneTiltX  = p.XBracingPlaneTiltX;
        add.XBracingProjectionType = p.XBracingProjectionType is "Cylinder" ? "Cylinder" : "Planar";
        add.XBracingCylinderDiameterMm = p.XBracingCylinderDiameterMm;
        add.XBracingCylinderX   = p.XBracingCylinderX;
        add.XBracingCylinderY   = p.XBracingCylinderY;
        add.XBracingCylinderFlipDirection = p.XBracingCylinderFlipDirection;
        add.WaveEffect = p.WaveEffect switch
        {
            "Sine"     => "Sine",
            "Sawtooth" => "Sawtooth",
            "Triangle" => "Triangle",
            _          => "None",
        };
        add.WaveAmplitude        = p.WaveAmplitude;
        add.WaveFrequencyMode    = p.WaveFrequencyMode is "Cycles" ? "Cycles" : "Wavelength";
        add.WaveWavelength       = p.WaveWavelength;
        add.WaveCycles           = p.WaveCycles;
        add.WaveShape            = p.WaveShape;
        add.WaveStagger          = p.WaveStagger;
        add.WavePhaseMethodIndex = p.WavePhaseMethodIndex;
        add.WaveGradient         = p.WaveGradient;
        add.WaveAmplitudeBottom  = p.WaveAmplitudeBottom;
        add.WaveAmplitudeTop     = p.WaveAmplitudeTop;
        add.WaveWavelengthBottom = p.WaveWavelengthBottom;
        add.WaveWavelengthTop    = p.WaveWavelengthTop;
        add.WaveGradientCenter     = p.WaveGradientCenter;
        add.WaveGradientCurve    = p.WaveGradientCurve switch
        {
            "Smooth"   => "Smooth",
            "Ease In"  => "Ease In",
            "Ease Out" => "Ease Out",
            _          => "Linear",
        };
        add.TemperatureOffset    = p.TemperatureOffset;
        add.ExtrusionSpeedOffset = p.ExtrusionSpeedOffset;
        add.PatternType         = p.PatternType;
        add.PatternMapping      = add.PatternMappingOptions.Contains(p.PatternMapping)
            ? p.PatternMapping : "Wavelength (mm)";
        add.PatternWavelengthMm = p.PatternWavelengthMm;
        add.PatternAmplitude    = p.PatternAmplitude;
        add.PatternFrequency    = p.PatternFrequency;
        add.PatternTwist        = p.PatternTwist;
        add.PatternOffset       = p.PatternOffset;
        add.PatternFadeIn       = p.PatternFadeIn;
        add.PatternFadeOut      = p.PatternFadeOut;
        if (Enum.TryParse<SliceMethod>(p.SliceMethod, out var method))
            add.Method = method;
        add.SlicingMode   = p.SlicingMode is "Surface" ? "Surface" : "Normal";
        add.PassAngle     = p.PassAngle;
        add.TiltAngle     = p.TiltAngle;
        add.TiltAngleX    = p.TiltAngleX;
        add.PrintSpeed      = p.PrintSpeed;
        add.FirstLayerAdjustmentsEnabled = p.FirstLayerAdjustmentsEnabled;
        add.FirstLayerSpeed = p.FirstLayerSpeed;
        add.FirstLayerRpm   = p.FirstLayerRpm;
        add.TravelSpeed   = p.TravelSpeed;
        add.Acceleration  = p.Acceleration;
        add.ApproachZ     = p.ApproachZ;
        add.ZHopMm                  = p.ZHopMm;
        add.WipeModeDisplay         = MigrateWipeModeDisplay(p.WipeModeDisplay);
        add.WipeLengthMm            = p.WipeLengthMm;
        add.WipeRampMm              = p.WipeRampMm;
        add.WipeSpeed               = p.WipeSpeed;
        add.WipeSkipShortTravels    = p.WipeSkipShortTravels;
        add.ExtrusionStartWaitSec   = p.ExtrusionStartWaitSec;
        add.ExtrusionResumeWaitSec  = p.ExtrusionResumeWaitSec;
        add.SsPreTravelWaitSec      = p.SsPreTravelWaitSec;
        add.SsResumePrimePercent    = p.SsResumePrimePercent;
        add.DigitalStartStopEnabled = p.DigitalStartStopEnabled;
        // Ensure post-process header is Caracol URM (not LFAM $ANOUT MAT) when flag is on.
        // SetField no-ops if value already matched, so always re-apply templates after load.
        add.ApplyUrmPostProcessTemplates(p.DigitalStartStopEnabled);
        add.ResumeRampEnabled         = p.ResumeRampEnabled;
        add.ResumeRampStartSpeed      = p.ResumeRampStartSpeed;
        add.ResumeRampStartRpmPercent = p.ResumeRampStartRpmPercent;
        add.ResumeRampDistanceMm      = p.ResumeRampDistanceMm;
        add.ResumeRampSteps           = p.ResumeRampSteps;
        add.LayerSpeedAdaptEnabled    = p.LayerSpeedAdaptEnabled;
        add.LayerSpeedBasisDisplay    = p.LayerSpeedBasisDisplay;
        add.LayerSpeedMinMmS          = p.LayerSpeedMinMmS;
        add.LayerSpeedMaxMmS          = p.LayerSpeedMaxMmS;
        add.SetSeamGuides(p.SeamGuidePoints
            .Where(a => a is { Length: >= 3 })
            .Select(a => new SeamGuidePoint(a[0], a[1], a[2])));
        add.StructuralSupports.Clear();
        add.StructuralSupports.AddRange(p.StructuralSupports
            .Where(a => a is { Length: >= 12 })
            .Select(a => new StructuralSupportSpec
            {
                Shape = a[0] >= 1f ? SupportShapeKind.Circle : SupportShapeKind.Rectangle,
                AnchorX = a[1], AnchorY = a[2], AnchorLayer = (int)a[3],
                LayersUp = (int)a[4], LayersDown = (int)a[5],
                CenterX = a[6], CenterY = a[7],
                WidthMm = a[8], DepthMm = a[9], RotationDeg = a[10],
                Enabled = a[11] >= 0.5f,
            }));
        add.SelectedSupportIndex = add.StructuralSupports.Count > 0 ? 0 : -1;
        add.SetPaintMarks(p.PaintMarks
            .Where(a => a is { Length: >= 5 })
            .Select(a => new PaintMark(
                new System.Numerics.Vector3(a[0], a[1], a[2]), a[3],
                a[4] >= 1f ? PaintMarkKind.Remove : PaintMarkKind.Bridge,
                a.Length >= 6 ? (PaintBridgeRole)(int)a[5] : PaintBridgeRole.None,
                a.Length >= 7 ? (PaintSupportStyle)(int)a[6] : PaintSupportStyle.FormboundButtress,
                a.Length >= 8 ? (PaintSupportSide)(int)a[7] : PaintSupportSide.Inside)));
        add.CurvedBoundarySourceDisplay = p.CurvedBoundarySource switch
        {
            "Viewport Pick" => "Viewport Pick",
            "JSON Import"   => "JSON Import",
            _               => "Auto",
        };
        add.CurvedAutoDetectBandMm    = p.CurvedAutoDetectBandMm;
        add.CurvedEnableRegionSplit   = p.CurvedEnableRegionSplit;
        add.SetCurvedBoundaries(p.CurvedBoundaryLowVertices, p.CurvedBoundaryHighVertices);
        add.ToolDataIndex = p.ToolDataIndex;
        add.BaseDataIndex = p.BaseDataIndex;
        add.ToolheadA     = p.ToolheadA;
        add.ToolheadB     = p.ToolheadB;
        add.ToolheadC     = p.ToolheadC;
        add.E1MotionEnabled = p.E1MotionEnabled;
        add.E1YPlusMm     = p.E1YPlusMm;
        add.E1YMinusMm    = p.E1YMinusMm;
        add.OrientationFollowPercent = p.OrientationFollowPercent;
        add.OrientationMaxTiltDeg    = p.OrientationMaxTiltDeg;
        add.FirstLayerZeroTilt       = p.FirstLayerZeroTilt;
        add.LayerLeanPercent         = p.LayerLeanPercent;
        add.LayerLeanMaxTiltDeg      = p.LayerLeanMaxTiltDeg;
        add.OrientationLookAheadMm   = p.OrientationLookAheadMm;
        add.OrientationSigmaMm       = p.OrientationSigmaMm;
        add.ApoCvel                = p.ApoCvel;

    }

    /// <summary>
    /// Keeps the right sidebar tab aligned with the viewport selection:
    /// source meshes â†’ Additive (slicing settings); toolpaths â†’ Toolpath.
    /// </summary>
    void SyncRightPanelToViewportSelection()
    {
        // Selecting a toolpath no longer swaps to the legacy Toolpath tab —
        // the numbered workflow steps (Additive) stay up for all selections.
        if (Viewport.IsToolpathSelected)
        {
            if (RightPanel.ShowAdditiveTabButton)
                RightPanel.ActiveTab = RightPanelTab.Additive;
            return;
        }

        if (!Viewport.HasMeshSelected)
        {
            // Nothing selected: leave the toolpath-options view and show the workflow
            // steps again (don't disturb Scan/Subtractive/Settings if those are active).
            if (RightPanel.ActiveTab == RightPanelTab.Toolpath && RightPanel.ShowAdditiveTabButton)
                RightPanel.ActiveTab = RightPanelTab.Additive;
            return;
        }

        if (RightPanel.ShowAdditiveTabButton)
        {
            RightPanel.ActiveTab = RightPanelTab.Additive;
            return;
        }

        // LFAM 3 phase gating hides Additive outside Print â€” use the active phase tab.
        if (!Viewport.ShowLfam3ToolPicker) return;

        if (Viewport.IsMillStepActive)
            RightPanel.ActiveTab = RightPanelTab.Subtractive;
        else if (Viewport.IsScannerToolActive && RightPanel.HasScanTab)
            RightPanel.ActiveTab = RightPanelTab.Scan;
        else
            RightPanel.ActiveTab = RightPanelTab.Additive;
    }

    /// <summary>
    /// LFAM 3: sidebar tabs per workflow phase (+ Toolpath on all phases).
    /// Print â†’ Additive; Scan â†’ Scan; Mill â†’ Subtractive.
    /// </summary>
    void SyncLfam3WorkflowSidebar()
    {
        if (!Viewport.ShowLfam3ToolPicker)
        {
            RightPanel.SetLfam3WorkflowTabGating(active: false, showAdditive: true, showScan: true, showSubtractive: true);
            return;
        }

        bool showScan        = Viewport.IsScannerToolActive;
        bool showAdditive    = Viewport.IsPrintStepActive;
        bool showSubtractive = Viewport.IsMillStepActive;
        if (!showScan && !showAdditive && !showSubtractive)
            showAdditive = true;

        RightPanel.SetLfam3WorkflowTabGating(active: true, showAdditive, showScan, showSubtractive);
    }

    /// <summary>
    /// Mirrors the robot panel's KRL TOOL/BASE indices into the export settings
    /// for whichever workflow tab is active (additive extruder vs scan camera).
    /// </summary>
    private void SyncKrlFrameIndicesToActiveTab()
    {
        var robot = RightPanel.Settings.Robot;
        switch (RightPanel.ActiveTab)
        {
            case RightPanelTab.Additive:
                RightPanel.Additive.ToolDataIndex = robot.KrlToolIndex;
                RightPanel.Additive.BaseDataIndex = robot.KrlBaseIndex;
                break;
            case RightPanelTab.Scan:
                RightPanel.Scan.ToolDataIndex = robot.KrlToolIndex;
                RightPanel.Scan.BaseDataIndex = robot.KrlBaseIndex;
                break;
        }
        UpdateActiveExtruderType();
    }

    /// <summary>Sets the additive HF/HV flag from the active cell's selected KRL tool, so the
    /// material preset uses the matching per-extruder flow rate (HF and HV deposit differently).</summary>
    private void UpdateActiveExtruderType()
    {
        var cell = Viewport.ActiveCell;
        int krlTool = RightPanel.Settings.Robot.KrlToolIndex;
        var tool = cell?.EffectiveTools?.FirstOrDefault(t => t.KrlIndex == krlTool);
        RightPanel.Additive.ActiveExtruderIsHf =
            tool?.Name?.Contains("HF", StringComparison.OrdinalIgnoreCase) ?? false;
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
        p.ShowGrid              = vp.ShowGrid;
        p.ShadowCatcherEnabled      = vp.ShowContactShadows;
        p.ContactShadowSize         = vp.ContactShadowSize;
        p.ContactShadowDarkness     = vp.ContactShadowDarkness;
        p.ContactShadowBlur         = vp.ContactShadowBlur;
        p.CavityEnabled             = vp.CavityEnabled;
        p.CavityMode                = vp.CavityModeOption;
        p.CavityScreenRidge         = vp.CavityScreenRidge;
        p.CavityScreenValley        = vp.CavityScreenValley;
        p.CavityWorldRidge          = vp.CavityWorldRidge;
        p.CavityWorldValley         = vp.CavityWorldValley;
        p.CavityWorldDistance       = vp.CavityWorldDistance;
        p.ShowAxes     = vp.ShowAxes;
        p.ShowBedGrid  = vp.ShowBedGrid;
        p.ActivePreset = vp.ActivePreset;

        // Toolpath bead colour (live viewport control)
        p.ToolpathBeadColor = Vec3ToHex(vp.BeadColor);

        // Lighting
        p.LightAzimuth   = vp.LightAzimuth;
        p.LightElevation = vp.LightElevation;
        p.LightIntensity = vp.LightIntensity;

        // Shader mode & backdrop
        p.ShaderMode          = vp.ActiveShaderMode.ToString();
        p.ViewModeProfiles    = vp.SerializeViewProfiles();
        p.DefaultBackdropPath = vp.ActiveBackdropPath;
        p.DefaultBackdropBlur    = vp.BackdropBlur;
        p.DefaultBackdropOpacity = vp.BackdropOpacity;

        // View settings panel
        p.ActiveTheme = view.ActiveTheme.ToString();
        p.ShowEdges   = view.ShowEdges;

        // Additive slicing settings
        // Material preset: capture from the live VM, not the change-event mirror —
        // the .mass snapshot must always match what the MATERIAL dropdown shows.
        p.SelectedMaterialPresetName = add.SelectedPreset?.Name;
        p.LayerHeight      = add.LayerHeight;
        p.BeadWidth        = add.BeadWidth;
        p.FirstLayerHeight = add.FirstLayerHeight;
        p.AdaptiveLayerHeight = add.AdaptiveLayerHeight;
        p.AdaptiveQuality     = add.AdaptiveQuality;
        p.MinLayerHeight      = add.MinLayerHeight;
        p.DisableContourOffset = add.DisableContourOffset;
        p.SeamMode            = add.SeamMode;
        p.ZigZagAllowSameLayerTravel = add.ZigZagAllowSameLayerTravel;
        p.OverhangOrientation = add.OverhangOrientation;
        p.MaxOverhangTiltDeg  = add.MaxOverhangTiltDeg;
        p.SmoothRotation                = add.SmoothRotation;
        p.SmoothRotationRadius          = add.SmoothRotationRadius;
        p.SmoothRotationMaxRateDegPerMm = add.SmoothRotationMaxRateDegPerMm;
        p.InfillPattern    = NormalizeInfillPatternLabel(add.InfillPattern);
        p.InfillSpacingMm  = add.InfillSpacingMm;
        p.InfillAngleDeg   = add.InfillAngleDeg;
        p.LightningOverhangDeg     = add.LightningOverhangDeg;
        p.LightningBranchSpacingMm = add.LightningBranchSpacingMm;
        p.LightningTipLoopRadiusMm = add.LightningTipLoopRadiusMm;
        p.LightningAnchorInterior  = add.LightningAffectInterior;
        p.LightningAnchorExterior  = add.LightningAffectExterior;
        p.LightningExteriorOverhangs = add.LightningAffectExterior;
        p.LightningButtressBarMm         = add.LightningButtressBarMm;
        p.LightningPreferInteriorMouths  = add.LightningPreferInteriorMouths;
        p.LightningTargetSupportSelections = add.LightningTargetSupportSelections;
        p.MultiPlanarPlanes = add.MultiPlanarPlanes
            .Select(r => new[] { r.HeightPct, r.AngleDeg }).ToList();
        p.MultiPlanarAxisX = add.MultiPlanarAxisX;
        p.BrimEnabled          = add.BrimEnabled;
        p.BrimLoops            = add.BrimLoops;
        p.XBracingEnabled      = add.XBracingEnabled;
        p.XBracingDepthMm      = add.XBracingDepthMm;
        p.XBracingDepthBottomMm = add.XBracingDepthBottomMm;
        p.XBracingDepthEaseBottom = add.XBracingDepthEaseBottom;
        p.XBracingDepthEaseTop = add.XBracingDepthEaseTop;
        p.XBracingSpanMm       = add.XBracingSpanMm;
        p.XBracingAngleDeg     = add.XBracingAngleDeg;
        p.XBracingExtendEdges  = add.XBracingExtendEdges;
        p.XBracingShowHelper   = add.XBracingShowHelper;
        p.XBracingPlaneTiltY   = add.XBracingPlaneTiltY;
        p.XBracingPlaneTiltX   = add.XBracingPlaneTiltX;
        p.XBracingProjectionType = add.XBracingProjectionType;
        p.XBracingCylinderDiameterMm = add.XBracingCylinderDiameterMm;
        p.XBracingCylinderX    = add.XBracingCylinderX;
        p.XBracingCylinderY    = add.XBracingCylinderY;
        p.XBracingCylinderFlipDirection = add.XBracingCylinderFlipDirection;
        p.WaveEffect           = add.WaveEffect;
        p.WaveAmplitude        = add.WaveAmplitude;
        p.WaveFrequencyMode    = add.WaveFrequencyMode;
        p.WaveWavelength       = add.WaveWavelength;
        p.WaveCycles           = add.WaveCycles;
        p.WaveShape            = add.WaveShape;
        p.WaveStagger          = add.WaveStagger;
        p.WavePhaseMethodIndex = add.WavePhaseMethodIndex;
        p.WaveGradient         = add.WaveGradient;
        p.WaveAmplitudeBottom  = add.WaveAmplitudeBottom;
        p.WaveAmplitudeTop     = add.WaveAmplitudeTop;
        p.WaveWavelengthBottom = add.WaveWavelengthBottom;
        p.WaveWavelengthTop    = add.WaveWavelengthTop;
        p.WaveGradientCenter   = add.WaveGradientCenter;
        p.WaveGradientCurve    = add.WaveGradientCurve;
        p.TemperatureOffset    = add.TemperatureOffset;
        p.ExtrusionSpeedOffset = add.ExtrusionSpeedOffset;
        p.PatternType          = add.PatternType;
        p.PatternMapping       = add.PatternMapping;
        p.PatternWavelengthMm  = add.PatternWavelengthMm;
        p.PatternAmplitude     = add.PatternAmplitude;
        p.PatternFrequency     = add.PatternFrequency;
        p.PatternTwist         = add.PatternTwist;
        p.PatternOffset        = add.PatternOffset;
        p.PatternFadeIn        = add.PatternFadeIn;
        p.PatternFadeOut       = add.PatternFadeOut;
        p.SliceMethod      = add.Method.ToString();
        p.SlicingMode      = add.SlicingMode;
        p.PassAngle        = add.PassAngle;
        p.TiltAngle        = add.TiltAngle;
        p.TiltAngleX       = add.TiltAngleX;
        p.PrintSpeed         = add.PrintSpeed;
        p.FirstLayerAdjustmentsEnabled = add.FirstLayerAdjustmentsEnabled;
        p.FirstLayerSpeed    = add.FirstLayerSpeed;
        p.FirstLayerRpm      = add.FirstLayerRpm;
        p.TravelSpeed      = add.TravelSpeed;
        p.Acceleration     = add.Acceleration;
        p.ApproachZ        = add.ApproachZ;
        p.ZHopMm                  = add.ZHopMm;
        p.WipeModeDisplay         = add.WipeModeDisplay;
        p.WipeLengthMm            = add.WipeLengthMm;
        p.WipeRampMm              = add.WipeRampMm;
        p.WipeSpeed               = add.WipeSpeed;
        p.WipeSkipShortTravels    = add.WipeSkipShortTravels;
        p.ExtrusionStartWaitSec   = add.ExtrusionStartWaitSec;
        p.ExtrusionResumeWaitSec  = add.ExtrusionResumeWaitSec;
        p.SsPreTravelWaitSec      = add.SsPreTravelWaitSec;
        p.SsResumePrimePercent    = add.SsResumePrimePercent;
        p.DigitalStartStopEnabled = add.DigitalStartStopEnabled;
        p.ResumeRampEnabled         = add.ResumeRampEnabled;
        p.ResumeRampStartSpeed      = add.ResumeRampStartSpeed;
        p.ResumeRampStartRpmPercent = add.ResumeRampStartRpmPercent;
        p.ResumeRampDistanceMm      = add.ResumeRampDistanceMm;
        p.ResumeRampSteps           = add.ResumeRampSteps;
        p.LayerSpeedAdaptEnabled    = add.LayerSpeedAdaptEnabled;
        p.LayerSpeedBasisDisplay    = add.LayerSpeedBasisDisplay;
        p.LayerSpeedMinMmS          = add.LayerSpeedMinMmS;
        p.LayerSpeedMaxMmS          = add.LayerSpeedMaxMmS;
        p.SeamGuidePoints = add.SeamGuides
            .Select(g => new[] { (float)g.X, (float)g.Y, (float)g.Z })
            .ToList();
        p.PaintMarks = add.PaintMarks
            .Select(m => new[] {
                m.Center.X, m.Center.Y, m.Center.Z, m.Radius,
                (float)m.Kind, (float)m.BridgeRole, (float)m.SupportStyle,
                (float)m.SupportSide })
            .ToList();
        p.StructuralSupports = add.StructuralSupports
            .Select(s => new[] {
                s.Shape == SupportShapeKind.Circle ? 1f : 0f,
                s.AnchorX, s.AnchorY, s.AnchorLayer,
                s.LayersUp, s.LayersDown,
                s.CenterX, s.CenterY, s.WidthMm, s.DepthMm, s.RotationDeg,
                s.Enabled ? 1f : 0f })
            .ToList();
        p.CurvedBoundarySource       = add.CurvedBoundarySourceDisplay;
        p.CurvedAutoDetectBandMm     = add.CurvedAutoDetectBandMm;
        p.CurvedEnableRegionSplit    = add.CurvedEnableRegionSplit;
        p.CurvedBoundaryLowVertices  = add.BuildCurvedLowBoundaryList().ToList();
        p.CurvedBoundaryHighVertices = add.BuildCurvedHighBoundaryList().ToList();
        p.ToolDataIndex    = add.ToolDataIndex;
        p.BaseDataIndex    = add.BaseDataIndex;
        p.ToolheadA        = add.ToolheadA;
        p.ToolheadB        = add.ToolheadB;
        p.ToolheadC        = add.ToolheadC;
        p.E1MotionEnabled  = add.E1MotionEnabled;
        p.E1YPlusMm        = add.E1YPlusMm;
        p.E1YMinusMm       = add.E1YMinusMm;
        p.OrientationFollowPercent = add.OrientationFollowPercent;
        p.OrientationMaxTiltDeg    = add.OrientationMaxTiltDeg;
        p.FirstLayerZeroTilt       = add.FirstLayerZeroTilt;
        p.LayerLeanPercent         = add.LayerLeanPercent;
        p.LayerLeanMaxTiltDeg      = add.LayerLeanMaxTiltDeg;
        p.OrientationLookAheadMm   = add.OrientationLookAheadMm;
        p.OrientationSigmaMm       = add.OrientationSigmaMm;
        p.ApoCvel                = add.ApoCvel;

        // Scan settings
        var scan = RightPanel.Scan;
        p.ScanCameraIp        = scan.CameraIp;
        p.ScanOutputDirectory = scan.OutputDirectory;
        p.ScanToolDataIndex   = scan.ToolDataIndex;
        p.ScanBaseDataIndex   = scan.BaseDataIndex;

        PreferencesLoader.Save(p);
    }

    private static string MigrateWipeModeDisplay(string? mode) => mode switch
    {
        "Natural" or "Normal" => "Same-Direction",
        _                     => mode ?? "Off",
    };

    private static string Vec3ToHex(System.Numerics.Vector3 c) =>
        $"#FF{(int)Math.Clamp(c.X * 255f, 0f, 255f):X2}{(int)Math.Clamp(c.Y * 255f, 0f, 255f):X2}{(int)Math.Clamp(c.Z * 255f, 0f, 255f):X2}";

    private static System.Numerics.Vector3 HexToVec3(string hex)
    {
        try
        {
            var s = hex.TrimStart('#');
            if (s.Length == 8) s = s[2..]; // strip alpha â†’ RRGGBB
            return new System.Numerics.Vector3(
                Convert.ToInt32(s[0..2], 16) / 255f,
                Convert.ToInt32(s[2..4], 16) / 255f,
                Convert.ToInt32(s[4..6], 16) / 255f);
        }
        catch { return System.Numerics.Vector3.Zero; }
    }

    private void LogProgressDetail(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message == _lastProgressLogMessage)
            return;

        _lastProgressLogMessage = message;
        Console.Log($"[progress] {message}");
    }
}
