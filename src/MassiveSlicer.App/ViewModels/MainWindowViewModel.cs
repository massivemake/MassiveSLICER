using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using MassiveSlicer.App;
using MassiveSlicer.App.Console;
using MassiveSlicer.App.Undo;
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

    /// <summary>Shared application preferences instance, loaded from disk at startup.</summary>
    public AppPreferences AppPreferences { get; } = PreferencesLoader.Load();

    /// <summary>ViewModel backing the Preferences window.</summary>
    public PreferencesViewModel Preferences { get; }

    /// <summary>Global undo/redo stack for transforms and settings.</summary>
    public UndoRedoService UndoRedo { get; } = new();

    private (WorkspaceDocument Doc, string Path)? _pendingWorkspaceRestore;
    private bool _applyingUndoRedo;
    private string _lastCommittedPrefsJson = "";
    private CancellationTokenSource? _settingsUndoDebounce;

    private static readonly JsonSerializerOptions PrefsJsonOptions = new() { WriteIndented = false };

    /// <summary>Initialises the ViewModel and wires child ViewModels.</summary>
    public MainWindowViewModel()
    {
        // Persist/restore collapsible-panel (Expander) open state across sessions via prefs.json.
        MassiveSlicer.App.Behaviors.PersistExpander.Store = AppPreferences;

        Preferences = new PreferencesViewModel(AppPreferences, () =>
        {
            SyncViewportFromPrefs();
            OnSettingsChanged();
        });

        Toolbar.AttachUndoRedo(UndoRedo);
        Viewport.UndoRedo = UndoRedo;

        // Give the viewport direct access to the robot panel so the render loop
        // can read joint angles for FK without a cross-tree binding.
        Viewport.Robot = RightPanel.Settings.Robot;
        Viewport.LiveIo.AttachRobot(RightPanel.Settings.Robot);

        // Give the viewport direct access to additive + subtractive settings for the slice/mill commands.
        Viewport.AdditiveSettings = RightPanel.Additive;
        Viewport.SubtractiveSettings = RightPanel.Subtractive;

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

            // Skip transient / non-persistent properties to avoid unnecessary disk writes.
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
                                or nameof(ScanSettingsViewModel.ScanStatus))
                return;
            OnSettingsChanged();
        };

        // Wire toolbar commands to cross-panel actions.
        Toolbar.FrameAllRequested       += (_, _) => Viewport.OnFrameAllRequested?.Invoke();
        Viewport.OnSaveViewRequested    = SaveCurrentView;

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
        calib.OnAutoCalibrateRequested = RunAutoScanToolCalibration;
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
        bedCal.OnAutoCalibrateRequested = RunAutoBedCalibration;
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
        Viewport.OnCellSwapCompleted = () =>
        {
            var cell = Viewport.ActiveCell;
            if (cell is not null)
                robot.SetKrlFrameOptions(
                    cell.EffectiveTools,
                    cell.KrlBases,
                    RightPanel.Scan.ToolDataIndex,
                    RightPanel.Scan.BaseDataIndex);

            // Show/hide the Scan tab based on whether this cell has a scanner.
            RightPanel.HasScanTab = cell?.ScanToolName is not null;

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

            if (_pendingWorkspaceRestore is { } pending)
            {
                _pendingWorkspaceRestore = null;
                ApplyWorkspaceState(pending.Doc, pending.Path);
            }
        };
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

    private async Task RunAutoBedCalibration()
    {
        var robot   = RightPanel.Settings.Robot;
        var bedCal  = robot.BedCalibration;
        var scanCal = RightPanel.Scan.Calibration;
        var scan    = RightPanel.Scan;

        if (!robot.IsConnected)
        {
            bedCal.SetStatus("Sync the robot first — C3Bridge must be connected.");
            Console.LogError("[bedcal] Robot not connected — run `sync` first.");
            return;
        }
        if (bedCal.IsAutoRunning)
        {
            Console.LogError("[bedcal] Auto calibration already running — wait for it to finish.");
            return;
        }
        if (scanCal.IsAutoRunning)
        {
            Console.LogError("[bedcal] scan-cal is still running — wait for `=== Done ===` before bed-cal.");
            return;
        }
        if (scan.IsScanning)
        {
            Console.LogError("[bedcal] A Zivid capture is in progress — wait for it to finish.");
            return;
        }

        bedCal.SetAutoRunning(true);
        robot.PauseStreaming();
        int captured = 0;
        try
        {
            Console.Log("[bedcal] === Auto bed calibration (CELL MS_AXIS E1 sweep) ===");

            bedCal.SetStatus("Moving to bed-cal waypoint…");
            Console.Log("[bedcal] Step 1: pre-cal waypoint (scanner-down-bed)…");
            if (!await GoToCalWaypointAsync("bed-cal", bedCal.SetStatus, "[bedcal]"))
                return;

            Viewport.ClearScanDiag();
            bedCal.ClearSamples();

            var cell = ActiveCellConfig();
            var wp = cell is not null ? CellLoader.FindWaypointByTag(cell, "bed-cal") : null;
            if (wp?.Joints is not { Length: >= 6 })
            {
                bedCal.SetStatus("No bed-cal waypoint with joints — teach scanner-down-bed first.");
                Console.LogError("[bedcal] Missing waypoint tagged bed-cal (need joints for E1 sweep).");
                return;
            }

            int tool = wp.Tool, baseIdx = wp.Base, vel = wp.VelocityPct;
            var e1Angles = BedScanCalSweep.E1AnglesForCell(cell?.BedScan);
            var yVantages = BedScanCalSweep.VantageOffsetsY(cell?.BedScan);
            // Surface scans for rotation-phase fit; YOffsetMm tags the vantage (phase uses Y0 only).
            var phaseClouds = new List<(double E1, float[] World, float YOffsetMm)>();
            int totalStops = yVantages.Count * e1Angles.Count;

            Console.Log("[bedcal] Step 2: activate scanner tool on controller…");
            EnsureCalibratedScannerToolSelected("[bedcal]");
            await robot.InitCommandServerAsync();
            if (!await robot.SetFrameAsync(tool, baseIdx, timeoutMs: 30000))
            {
                bedCal.SetStatus($"Couldn't activate tool #{tool} — is CELL selected, AUTO, drives on?");
                Console.LogError($"[bedcal] SetFrame tool #{tool} base #{baseIdx} timed out.");
                return;
            }

            bedCal.SetStatus($"Step 3: {yVantages.Count} Y × {e1Angles.Count} E1 — keep CELL selected…");
            Console.Log($"[bedcal] Step 3: multi-vantage E1 sweep — Y [{string.Join(", ", yVantages)}] mm, " +
                        $"{e1Angles.Count} E1/step, tool #{tool} base #{baseIdx}.");

            bool aborted = false;
            for (int v = 0; v < yVantages.Count && !aborted; v++)
            {
                float yOff = yVantages[v];
                bedCal.SetStatus($"Vantage {v + 1}/{yVantages.Count}: Y offset {yOff:F0} mm…");
                Console.Log($"[bedcal] === Vantage {v + 1}/{yVantages.Count} (Y {(yOff >= 0 ? "+" : "")}{yOff:F0} mm) ===");

                if (!await BedCalMoveToVantageAsync(robot, wp, yOff, vel, tool, baseIdx))
                {
                    bedCal.SetStatus($"Couldn't reach Y offset {yOff:F0} mm (captured {captured}).");
                    Console.LogError($"[bedcal] Vantage Y{yOff:F0} move failed.");
                    aborted = true;
                    break;
                }

                var park = await robot.ReadAxesAsync();
                (int vantageCaptured, captured) = await BedCalRunE1SweepAsync(
                    robot, bedCal, e1Angles, park, phaseClouds, v, yOff, vel, tool, baseIdx,
                    captured, totalStops);
                Console.Log($"[bedcal] Vantage Y{yOff:F0}: {vantageCaptured} board samples this ring.");

                Console.Log("[bedcal] E1 → 0° before next vantage…");
                await robot.SendAxesAsync(park[0], park[1], park[2], park[3], park[4], park[5], 0, vel, tool, baseIdx);
                await SyncRobotAxesFromControllerAsync(robot);
            }

            Console.Log("[bedcal] Returning to bed-cal waypoint…");
            await ExecuteWaypointMoveAsync(robot, wp, "[bedcal]", vel);
            await SyncRobotAxesFromControllerAsync(robot);

            if (captured >= 3)
            {
                Console.Log($"[bedcal] Step 4: fit from {captured} board samples, {phaseClouds.Count} surface scans…");
                bedCal.Compute();
                if (bedCal.HasResult)
                {
                    Console.Log($"[bedcal] Centre ({bedCal.CenterX:F1}, {bedCal.CenterY:F1}, {bedCal.CenterZ:F1}) mm, " +
                                $"R {bedCal.Radius:F0} mm, residual {bedCal.Residual:F2} mm, " +
                                $"rotation {(bedCal.RotationSign < 0 ? "CW" : "CCW")}.");
                    bedCal.Apply();
                    Console.Log("[bedcal] Centre + rotation sign applied. Estimating rotation phase…");
                    if (phaseClouds.Count >= 2)
                        await EstimateAndApplyBedPhaseAsync(phaseClouds, bedCal);
                    else
                        Console.Log("[bedcal] Too few surface scans for rotation-phase estimate.");
                }
                else
                {
                    Console.Log($"[bedcal] Sweep captured {captured} samples but the fit failed — not applied.");
                }
            }
            else
            {
                Console.Log($"[bedcal] Sweep ended with {captured} samples (need >=3) — nothing applied.");
                bedCal.SetStatus($"Bed-cal ended with {captured} samples (need >=3).");
            }

            if (Viewport.ScanDiagCount > 0)
                Console.Log($"[bedcal] {Viewport.ScanDiagCount} surface scans stashed — run `diag-scans` to export.");
        }
        catch (Exception ex)
        {
            bedCal.SetStatus($"Auto-cal error: {ex.Message}");
            Console.LogError($"[bedcal] ERROR: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            robot.ResumeStreaming();
            bedCal.SetAutoRunning(false);
            Console.Log("[bedcal] === Done ===");
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
    /// Automated 3D scan-tool (hand-eye) calibration via CELL <c>MS_AXIS</c> wrist nutation — no
    /// <c>SCAN_TOOL_CAL</c> program. At each pose the calibration card must be fully in frame; if not,
    /// wrist tilt is halved and the pose is retried until in frame or <see cref="ScanToolCalSweep.MinScale"/>.
    /// </summary>
    private async Task RunAutoScanToolCalibration()
    {
        var robot   = RightPanel.Settings.Robot;
        var scanCal = RightPanel.Scan.Calibration;

        if (!robot.IsConnected)
        {
            scanCal.SetStatus("Sync the robot first — C3Bridge must be connected.");
            Console.LogError("[scancal] Robot not connected — run `sync` first.");
            return;
        }
        if (scanCal.IsAutoRunning)
        {
            Console.LogError("[scancal] Auto calibration already running — wait for it to finish (or restart the app if stuck).");
            return;
        }
        if (robot.BedCalibration.IsAutoRunning)
        {
            Console.LogError("[scancal] bed-cal is running — wait for it to finish before scan-cal.");
            return;
        }

        scanCal.SetAutoRunning(true);
        robot.PauseStreaming();
        int captured = 0;
        try
        {
            Console.Log("[scancal] === Auto scan-tool calibration (CELL MS_AXIS, no SCAN_TOOL_CAL) ===");

            scanCal.SetStatus("Moving to scan-cal waypoint…");
            Console.Log("[scancal] Step 1/4: pre-cal waypoint (scanner-down-bed)…");
            if (!await GoToCalWaypointAsync("scan-cal", scanCal.SetStatus, "[scancal]"))
                return;

            scanCal.ClearForAuto();

            var cell = ActiveCellConfig();
            var wp = cell is not null ? CellLoader.FindWaypointByTag(cell, "scan-cal") : null;
            int calTool = ScanToolCalSweep.CalToolIndex;
            int resultTool = ScanToolCalSweep.ResultToolIndex;
            int baseIdx = wp?.Base ?? robot.KrlBaseIndex;
            int vel = wp?.VelocityPct > 0
                ? Math.Min(wp.VelocityPct, ScanToolCalSweep.DefaultVelPct)
                : ScanToolCalSweep.DefaultVelPct;

            Console.Log("[scancal] Step 2/4: activate tool #5 on controller (sweep uses uncalibrated scan tool)…");
            if (!await ScanCalActivateToolAsync(robot, scanCal, calTool, baseIdx))
                return;

            var home = await robot.ReadAxesAsync();
            Console.Log($"[scancal] Home joints: A1={home[0]:F1}…A6={home[5]:F1} A4={home[3]:F1} A5={home[4]:F1} A6={home[5]:F1} E1={home[6]:F1}.");

            var wristDeltas = ScanToolCalSweep.PoseDeltasForCell(cell?.BedScan);
            bool usingLearned = cell?.BedScan?.ScanCalWristDeltas is { Length: ScanToolCalSweep.PoseCount };
            scanCal.SetStatus($"Step 3/4: {wristDeltas.Count} wrist poses — CELL selected, card visible…");
            Console.Log($"[scancal] Step 3/4: wrist nutation — tool #{calTool} base #{baseIdx} @ {vel}% " +
                        $"({(usingLearned ? "learned" : "default")} deltas from cell).");

            string? cellPath = Viewport.ActiveCellPath;
            (captured, _) = await ScanCalRunWristSweepAsync(
                robot, scanCal, home, vel, calTool, baseIdx, wristDeltas, cellPath);

            Console.Log("[scancal] Returning to home pose…");
            await robot.SendAxesAsync(
                home[0], home[1], home[2], home[3], home[4], home[5], home[6], vel, calTool, baseIdx);
            await SyncRobotAxesFromControllerAsync(robot);

            if (wp is not null)
            {
                Console.Log("[scancal] Returning to scan-cal waypoint…");
                await ExecuteWaypointMoveAsync(robot, wp, "[scancal]", vel);
                await SyncRobotAxesFromControllerAsync(robot);
            }

            if (captured >= 3)
            {
                scanCal.SetStatus($"Step 4/4: computing hand-eye from {captured} poses…");
                Console.Log($"[scancal] Step 4/4: hand-eye fit ({captured} poses) → save to tool #{resultTool}…");
                await scanCal.ComputeCalibrationAsync();
                if (scanCal.HasResult)
                    await ApplyScanCalibrationResultAsync(robot, scanCal, resultTool);
                else
                {
                    Console.Log($"[scancal] Captured {captured} poses but the hand-eye fit failed — not applied.");
                }
            }
            else
            {
                Console.Log($"[scancal] Ended with {captured} poses (need >=3) — nothing computed.");
                scanCal.SetStatus($"Scan-cal ended with {captured} poses (need >=3 in frame).");
            }
        }
        catch (Exception ex)
        {
            scanCal.SetStatus($"Auto scan-cal error: {ex.Message}");
            Console.LogError($"[scancal] ERROR: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            robot.ResumeStreaming();
            scanCal.SetAutoRunning(false);
            Console.Log("[scancal] === Done ===");
        }
    }

    /// <summary>MS_CMD=5 tool #{5} + UI selection — sweep runs on uncalibrated scan tool; result goes to #6.</summary>
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
        _ = RunAutoBedCalibration().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is { } ex)
                Console.LogError($"[bedcal] Unhandled: {ex.GetBaseException().Message}");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Bridge/console entry point for Auto 3D Scan (hand-eye) Calibration (fire-and-forget).</summary>
    public void StartScanCalibration()
    {
        Console.Log("[scancal] Starting auto scan-tool calibration from console…");
        _ = RunAutoScanToolCalibration().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is { } ex)
                Console.LogError($"[scancal] Unhandled: {ex.GetBaseException().Message}");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Rotates E1 to <paramref name="e1Deg"/> while holding A1â€“A6 at current values.</summary>
    public async Task MoveE1Async(double e1Deg, int vel = 20)
    {
        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected) { Console.LogError("[e1] Robot not connected â€” Sync first."); return; }
        robot.PauseStreaming();
        try
        {
            var axes = await robot.ReadAxesAsync();
            Console.Log($"[e1] PTP E1 â†’ {e1Deg:F1}Â° (holding A1â€“A6) @ {vel}% â€¦");
            await robot.InitCommandServerAsync();
            bool ok = await robot.SendAxesAsync(
                axes[0], axes[1], axes[2], axes[3], axes[4], axes[5], e1Deg, vel,
                robot.KrlToolIndex, robot.KrlBaseIndex);
            if (ok)
            {
                robot.E1 = Math.Round(e1Deg, 2);
                Console.Log($"[e1] At E1={e1Deg:F1}Â°.");
            }
            else
                Console.LogError("[e1] Move timed out.");
        }
        catch (Exception ex) { Console.LogError($"[e1] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>Captures the full app window as PNG bytes. Wired from <c>MainWindow</c> on load.</summary>
    internal Func<Task<byte[]?>>? CaptureAppScreenshot { get; set; }

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
            foreach (var name in names.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
    public async Task GoToWaypointAsync(string name, int velOverride = -1)
    {
        if (Viewport.ActiveCellPath is not { } path)
        {
            Console.LogError("[waypoint] No active cell.");
            return;
        }

        var cell = CellLoader.Load(path);
        if (CellLoader.FindWaypoint(cell, name) is not { } wp)
        {
            Console.LogError($"[waypoint] '{name}' not found â€” run `waypoint list`.");
            return;
        }

        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected)
        {
            Console.LogError("[waypoint] Robot not connected â€” Sync first.");
            return;
        }

        robot.PauseStreaming();
        try
        {
            bool ok = await ExecuteWaypointMoveAsync(robot, wp, "[waypoint]", velOverride);
            Console.Log(ok
                ? $"[waypoint] At {wp.Name}."
                : "[waypoint] Move timed out â€” check MASSIVE_SERVER / CELL.");
        }
        catch (Exception ex) { Console.LogError($"[waypoint] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>
    /// Moves to the cell waypoint tagged for a calibration workflow (<c>bed-cal</c>, <c>scan-cal</c>).
    /// Returns false when the move was required but failed; true when skipped (no tag) or successful.
    /// </summary>
    async Task<bool> GoToCalWaypointAsync(string tag, Action<string> setStatus, string logPrefix)
    {
        if (ActiveCellConfig() is not { } cell)
        {
            Console.Log($"{logPrefix} No active cell â€” skipping pre-cal waypoint.");
            return true;
        }

        if (CellLoader.FindWaypointByTag(cell, tag) is not { } wp)
        {
            Console.Log($"{logPrefix} No waypoint tagged '{tag}' â€” skipping pre-cal move.");
            return true;
        }

        var robot = RightPanel.Settings.Robot;
        setStatus($"Moving to {wp.Name}â€¦");
        Console.Log($"{logPrefix} Pre-cal â†’ {wp.Name} (tag {tag}, tool #{wp.Tool}, base #{wp.Base})");

        try
        {
            bool ok = await ExecuteWaypointMoveAsync(robot, wp, logPrefix);
            if (!ok)
            {
                setStatus($"Couldn't reach {wp.Name} â€” check MASSIVE_SERVER / CELL.");
                Console.Log($"{logPrefix} Pre-cal move to {wp.Name} timed out.");
                return false;
            }

            await SyncRobotAxesFromControllerAsync(robot);
            setStatus($"At {wp.Name} â€” starting calibrationâ€¦");
            Console.Log($"{logPrefix} At {wp.Name} â€” proceeding with calibration.");
            return true;
        }
        catch (Exception ex)
        {
            setStatus($"Pre-cal move failed: {ex.Message}");
            Console.Log($"{logPrefix} Pre-cal move error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    async Task<bool> ExecuteWaypointMoveAsync(
        RobotPanelViewModel robot, CellWaypointConfig wp, string logPrefix, int velOverride = -1)
    {
        int vel = velOverride >= 0 ? velOverride : wp.VelocityPct;
        await robot.InitCommandServerAsync();
        Console.Log($"{logPrefix} â†’ {wp.Name} @ {vel}% ({(wp.PreferJoints ? "joints" : "pose")})");

        if (wp.PreferJoints && wp.Joints is { Length: >= 6 } j)
        {
            double e1 = j.Length >= 7 ? j[6] : 0;
            return await robot.SendAxesAsync(j[0], j[1], j[2], j[3], j[4], j[5], e1, vel, wp.Tool, wp.Base);
        }

        return await robot.SendPoseAsync(false, wp.TcpX, wp.TcpY, wp.TcpZ, wp.TcpA, wp.TcpB, wp.TcpC, vel, wp.Tool, wp.Base);
    }

    /// <summary>Parks TCP at bed-cal waypoint + <paramref name="yOffsetMm"/> on Y (active base frame).</summary>
    async Task<bool> BedCalMoveToVantageAsync(
        RobotPanelViewModel robot, CellWaypointConfig wp, float yOffsetMm, int vel, int tool, int baseIdx)
    {
        if (Math.Abs(yOffsetMm) < 0.5f)
        {
            Console.Log("[bedcal] Vantage Y+0 — using bed-cal waypoint joints.");
            return await ExecuteWaypointMoveAsync(robot, wp, "[bedcal]", vel);
        }

        double y = wp.TcpY + yOffsetMm;
        Console.Log($"[bedcal] MS_POSE Y offset {yOffsetMm:F0} mm → ({wp.TcpX:F1}, {y:F1}, {wp.TcpZ:F1})");
        bool ok = await robot.SendPoseAsync(
            false, wp.TcpX, y, wp.TcpZ, wp.TcpA, wp.TcpB, wp.TcpC, vel, tool, baseIdx, timeoutMs: 120000);
        if (ok)
        {
            await Task.Delay(500);
            await SyncRobotAxesFromControllerAsync(robot);
        }
        return ok;
    }

    /// <summary>
    /// Nine wrist nutation poses about <paramref name="home"/>; halves tilt when the calibration card
    /// is out of frame until in frame or <see cref="ScanToolCalSweep.MinScale"/>. Successful gentler
    /// angles are persisted to <c>bedScan.scanCalWristDeltas</c> so the next run starts closer.
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
                    $"Pose {n + 1}/{target}: scale {scale:P0} — moving wrist (card must be in frame)…");
                Console.Log($"[scancal] Pose {n + 1}/{target} scale={scale:F3} → A4={a4:F1} A5={a5:F1} A6={a6:F1}");

                bool moved = await robot.SendAxesAsync(
                    home[0], home[1], home[2], a4, a5, a6, home[6], vel, tool, baseIdx, timeoutMs: 120000);
                if (!moved)
                {
                    Console.Log($"[scancal] Pose {n + 1}: wrist move timed out — skipping.");
                    skipped++;
                    break;
                }

                await Task.Delay(500);
                await SyncRobotAxesFromControllerAsync(robot);
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
                                    $"(was scale {scale:F3} of ΔA4={delta.A4:F1} ΔA5={delta.A5:F1} ΔA6={delta.A6:F1}).");
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
                scanCal.SetStatus($"Pose {n + 1}/{target}: card out of frame — gentler wrist angle…");
            }
        }

        if (skipped > 0)
            Console.Log($"[scancal] Sweep: {captured} poses in frame, {skipped} skipped.");

        if (learned.Any(l => l) && cellPath is not null)
        {
            if (CellLoader.TrySaveScanCalWristDeltas(cellPath, deltas, out var saveErr))
            {
                int nLearned = learned.Count(l => l);
                Console.Log($"[scancal] Saved {nLearned} learned wrist delta(s) to {cellPath} — next scan-cal will start gentler.");
                MassiveSlicer.App.CellSceneCache.Invalidate(cellPath);
                Viewport.ActiveCell = CellLoader.Load(cellPath);
                Viewport.OnDevCellReloadRequested?.Invoke(cellPath);
            }
            else
                Console.LogError($"[scancal] Couldn't save learned wrist deltas: {saveErr}");
        }

        return (captured, skipped);
    }

    /// <summary>Full E1 sweep at the current parked arm pose; merges captures into bed-cal + phase clouds.</summary>
    async Task<(int VantageCaptured, int CapturedTotal)> BedCalRunE1SweepAsync(
        RobotPanelViewModel robot,
        RotaryBedCalibrationViewModel bedCal,
        IReadOnlyList<double> e1Angles,
        double[] parkJoints,
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

        for (int n = 0; n < e1Angles.Count; n++)
        {
            double e1 = e1Angles[n];
            int stopNum = vantageIndex * e1Angles.Count + n + 1;
            bedCal.SetStatus($"[{yTag}] E1 {e1:F0}° ({stopNum}/{totalStops})…");
            Console.Log($"[bedcal] [{yTag}] {n + 1}/{e1Angles.Count} — MS_AXIS E1={e1:F1}°");

            bool moved = await robot.SendAxesAsync(
                parkJoints[0], parkJoints[1], parkJoints[2], parkJoints[3], parkJoints[4], parkJoints[5],
                e1, vel, tool, baseIdx, timeoutMs: 120000);
            if (!moved)
            {
                Console.Log($"[bedcal] [{yTag}] E1 move timed out at {e1:F0}°.");
                break;
            }

            await Task.Delay(500);
            await SyncRobotAxesFromControllerAsync(robot);

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

    static async Task SyncRobotAxesFromControllerAsync(RobotPanelViewModel robot)
    {
        var axes = await robot.ReadAxesAsync();
        robot.A1 = Math.Round(axes[0], 2);
        robot.A2 = Math.Round(axes[1], 2);
        robot.A3 = Math.Round(axes[2], 2);
        robot.A4 = Math.Round(axes[3], 2);
        robot.A5 = Math.Round(axes[4], 2);
        robot.A6 = Math.Round(axes[5], 2);
        robot.E1 = Math.Round(axes[6], 2);
    }

    /// <summary>Captures the live robot pose and saves it as a named waypoint in the active cell JSON.</summary>
    public async Task SaveWaypointFromRobotAsync(string name, string? description = null, IReadOnlyList<string>? tags = null)
    {
        if (Viewport.ActiveCellPath is not { } path)
        {
            Console.LogError("[waypoint] No active cell.");
            return;
        }

        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Console.LogError("[waypoint] usage: waypoint save <name>");
            return;
        }

        var robot = RightPanel.Settings.Robot;
        if (!robot.IsConnected)
        {
            Console.LogError("[waypoint] Robot not connected â€” Sync first.");
            return;
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

            var wp = new CellWaypointConfig
            {
                Name = name,
                Description = description,
                Tags = tags ?? [],
                TcpX = (float)x, TcpY = (float)y, TcpZ = (float)z,
                TcpA = (float)a, TcpB = (float)b, TcpC = (float)c,
                Joints = [(float)j[0], (float)j[1], (float)j[2], (float)j[3], (float)j[4], (float)j[5], (float)e1],
                Tool = actTool,
                Base = actBase,
                VelocityPct = 20,
                PreferJoints = true,
            };

            if (!CellLoader.SaveWaypoint(path, wp, out var err))
            {
                Console.LogError($"[waypoint] Save failed: {err}");
                return;
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
        }
        catch (Exception ex) { Console.LogError($"[waypoint] {ex.GetType().Name}: {ex.Message}"); }
        finally { robot.ResumeStreaming(); }
    }

    /// <summary>Switches to a cell whose display name matches <paramref name="name"/>
    /// (e.g. "LFAM 3" / "lfam3"). For the console / control bridge. Call on the UI thread.</summary>
    public string SwitchCellByName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return "[cell] usage: cell <name>";

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
            return $"[cell] already on {names[idx]}.";

        LeftPanel.SelectedCellIndex = idx; // fires the async cell load
        return $"[cell] switching to {names[idx]}â€¦";
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
        Viewport.ClearUserScene();
        UndoRedo.Clear();
        AppPreferences.LastWorkspacePath = null;
        PreferencesLoader.Save(AppPreferences);
        Console.Log("[workspace] New workspace.");
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

        SceneNode? node;
        try
        {
            node = ImportHelper.LoadAndPlace(path, Viewport.ActiveCell);
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

        // Inspect BEFORE enqueuing: AddImportNode hands the node to the GL upload thread,
        // which clears PendingMesh once uploaded -- for small meshes that can happen before
        // the inspector runs, making it report 0 verts. Summarize while the mesh is intact.
        var report = GltfImportInspector.InspectLoaded(node, path);
        foreach (var line in report.ToLogLines())
            Console.Log(line);

        Viewport.AddImportNode(node);

        Console.Log($"[import] Added '{node.Name}' to scene.");
        return true;
    }

    /// <summary>
    /// Loads a <c>.mass</c> workspace from <paramref name="path"/> (File â†’ Open).
    /// </summary>
    public void OpenWorkspace(string path)
    {
        var doc = WorkspaceLoader.Load(path);
        if (doc is null)
        {
            Console.Log($"[workspace] Failed to load '{path}'.");
            return;
        }

        if (doc.CellPath is { } cellPath)
        {
            int idx = LeftPanel.FindCellIndex(cellPath);
            if (idx >= 0 && idx != LeftPanel.SelectedCellIndex)
            {
                _pendingWorkspaceRestore = (doc, path);
                LeftPanel.SelectedCellIndex = idx;
                return;
            }
        }

        ApplyWorkspaceState(doc, path);
    }

    /// <summary>
    /// Saves to <see cref="AppPreferences.LastWorkspacePath"/> when set.
    /// Returns <c>false</c> when no file is open yet (caller should run Save As).
    /// </summary>
    public bool TrySaveCurrentWorkspace()
    {
        if (AppPreferences.LastWorkspacePath is not { Length: > 0 } path)
            return false;

        SaveWorkspace(path);
        return true;
    }

    /// <summary>
    /// Saves all outliner models, camera, cell, and settings to <paramref name="path"/>.
    /// </summary>
    public void SaveWorkspace(string path)
    {
        if (!path.EndsWith(".mass", StringComparison.OrdinalIgnoreCase))
            path += ".mass";

        PersistSettings();
        var doc = WorkspaceService.Build(Viewport, RightPanel, AppPreferences, path);
        WorkspaceLoader.Save(doc, path);
        AppPreferences.LastWorkspacePath = path;
        PreferencesLoader.Save(AppPreferences);
        Console.Log($"[workspace] Saved {doc.Models.Count} model(s) and settings to {path}");
    }

    /// <summary>Suggested filename for the Save As dialog (last save or default).</summary>
    internal string SuggestedWorkspaceFileName =>
        AppPreferences.LastWorkspacePath is { } last
            ? System.IO.Path.GetFileName(last)
            : "workspace.mass";

    private void ApplyWorkspaceState(WorkspaceDocument doc, string workspacePath)
    {
        _applyingUndoRedo = true;
        try
        {
            ApplyWorkspaceStateCore(doc, workspacePath);
        }
        finally
        {
            _applyingUndoRedo = false;
            PersistSettings();
            _lastCommittedPrefsJson = CapturePrefsJson();
        }
    }

    private void ApplyWorkspaceStateCore(WorkspaceDocument doc, string workspacePath)
    {
        CopyPreferences(doc.Settings);
        SyncViewportFromPrefs();
        RestoreMaterialPresetSelection(doc.Settings.SelectedMaterialPresetName);

        if (Enum.TryParse<RightPanelTab>(doc.RightPanelTab, out var tab))
            RightPanel.ActiveTab = tab;

        WorkspaceService.RestoreModels(doc, Viewport, workspacePath);

        if (doc.Camera is { } camera)
            Viewport.ApplyCameraState?.Invoke(camera);

        AppPreferences.LastWorkspacePath = workspacePath;
        SyncKrlFrameIndicesToActiveTab();
        PreferencesLoader.Save(AppPreferences);
        Console.Log($"[workspace] Restored {doc.Models.Count} model(s) from {workspacePath}");
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
        live.DefaultBackdropBlur    = copy.DefaultBackdropBlur;
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
        live.ShadowCatcherEnabled     = copy.ShadowCatcherEnabled;
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
        live.ExtrusionStartWaitSec   = copy.ExtrusionStartWaitSec;
        live.ExtrusionResumeWaitSec  = copy.ExtrusionResumeWaitSec;
        live.ResumeRampEnabled         = copy.ResumeRampEnabled;
        live.ResumeRampStartSpeed      = copy.ResumeRampStartSpeed;
        live.ResumeRampStartRpmPercent = copy.ResumeRampStartRpmPercent;
        live.ResumeRampDistanceMm      = copy.ResumeRampDistanceMm;
        live.ResumeRampSteps           = copy.ResumeRampSteps;
        live.SeamGuidePoints         = copy.SeamGuidePoints;
        live.LayerHeight            = copy.LayerHeight;
        live.BeadWidth              = copy.BeadWidth;
        live.FirstLayerHeight       = copy.FirstLayerHeight;
        live.SliceMethod            = copy.SliceMethod;
        live.SlicingMode            = copy.SlicingMode;
        live.PassAngle              = copy.PassAngle;
        live.TiltAngle              = copy.TiltAngle;
        live.TiltAngleX             = copy.TiltAngleX;
        live.PrintSpeed             = copy.PrintSpeed;
        live.TravelSpeed            = copy.TravelSpeed;
        live.Acceleration           = copy.Acceleration;
        live.ApproachZ              = copy.ApproachZ;
        live.ToolDataIndex          = copy.ToolDataIndex;
        live.BaseDataIndex          = copy.BaseDataIndex;
        live.ToolheadA              = copy.ToolheadA;
        live.ToolheadB              = copy.ToolheadB;
        live.ToolheadC              = copy.ToolheadC;
        live.ApoCvel                = copy.ApoCvel;
        live.ScanCameraIp           = copy.ScanCameraIp;
        live.ScanOutputDirectory    = copy.ScanOutputDirectory;
        live.ScanToolDataIndex      = copy.ScanToolDataIndex;
        live.ScanBaseDataIndex      = copy.ScanBaseDataIndex;
    }

    private void RestoreMaterialPresetSelection(string? presetName)
    {
        if (presetName is null)
        {
            RightPanel.Additive.SelectedPresetIndex = -1;
            return;
        }

        int idx = RightPanel.Additive.MaterialPresets
            .Select((p, i) => (p, i))
            .FirstOrDefault(t => t.p.Name == presetName, (null!, -1)).i;
        if (idx >= 0)
            RightPanel.Additive.SelectedPresetIndex = idx;
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

        // Toolpath colors
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

        // Backdrop
        if (p.DefaultBackdropPath is { } backdropPath)
        {
            var match = vp.AvailableBackdrops.FirstOrDefault(b => b.Path == backdropPath);
            if (match is not null) vp.ActiveBackdrop = match;
        }
        vp.BackdropBlur = p.DefaultBackdropBlur;

        // Theme: update swatch selection and apply visually
        if (Enum.TryParse<AppTheme>(p.ActiveTheme, out var theme))
        {
            view.ActiveTheme = theme;
            (Application.Current as MassiveSlicer.App.App)?.ApplyTheme(theme);
        }
        view.ShowEdges            = p.ShowEdges;
        view.ShadowCatcherEnabled = p.ShadowCatcherEnabled;

        // Additive slicing settings
        add.LayerHeight      = p.LayerHeight;
        add.BeadWidth        = p.BeadWidth;
        add.FirstLayerHeight = p.FirstLayerHeight;
        if (Enum.TryParse<SliceMethod>(p.SliceMethod, out var method))
            add.Method = method;
        add.SlicingMode   = p.SlicingMode is "Surface" ? "Surface" : "Normal";
        add.PassAngle     = p.PassAngle;
        add.TiltAngle     = p.TiltAngle;
        add.TiltAngleX    = p.TiltAngleX;
        add.PrintSpeed      = p.PrintSpeed;
        add.TravelSpeed   = p.TravelSpeed;
        add.Acceleration  = p.Acceleration;
        add.ApproachZ     = p.ApproachZ;
        add.ZHopMm                  = p.ZHopMm;
        add.WipeModeDisplay         = MigrateWipeModeDisplay(p.WipeModeDisplay);
        add.WipeLengthMm            = p.WipeLengthMm;
        add.WipeRampMm              = p.WipeRampMm;
        add.WipeSpeed               = p.WipeSpeed;
        add.ExtrusionStartWaitSec   = p.ExtrusionStartWaitSec;
        add.ExtrusionResumeWaitSec  = p.ExtrusionResumeWaitSec;
        add.ResumeRampEnabled         = p.ResumeRampEnabled;
        add.ResumeRampStartSpeed      = p.ResumeRampStartSpeed;
        add.ResumeRampStartRpmPercent = p.ResumeRampStartRpmPercent;
        add.ResumeRampDistanceMm      = p.ResumeRampDistanceMm;
        add.ResumeRampSteps           = p.ResumeRampSteps;
        add.SetSeamGuides(p.SeamGuidePoints
            .Where(a => a is { Length: >= 3 })
            .Select(a => new SeamGuidePoint(a[0], a[1], a[2])));
        add.ToolDataIndex = p.ToolDataIndex;
        add.BaseDataIndex = p.BaseDataIndex;
        add.ToolheadA     = p.ToolheadA;
        add.ToolheadB     = p.ToolheadB;
        add.ToolheadC     = p.ToolheadC;
        add.ApoCvel                = p.ApoCvel;

        // Scan settings
        var scan = RightPanel.Scan;
        scan.CameraIp        = p.ScanCameraIp;
        scan.OutputDirectory = p.ScanOutputDirectory;
        scan.ToolDataIndex   = p.ScanToolDataIndex;
        scan.BaseDataIndex   = p.ScanBaseDataIndex;
    }

    /// <summary>
    /// Keeps the right sidebar tab aligned with the viewport selection:
    /// source meshes â†’ Additive (slicing settings); toolpaths â†’ Toolpath.
    /// </summary>
    void SyncRightPanelToViewportSelection()
    {
        if (Viewport.IsToolpathSelected)
        {
            RightPanel.ActiveTab = RightPanelTab.Toolpath;
            return;
        }

        if (!Viewport.HasMeshSelected) return;

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
        p.ActiveTheme          = view.ActiveTheme.ToString();
        p.ShowEdges            = view.ShowEdges;
        p.ShadowCatcherEnabled = view.ShadowCatcherEnabled;

        // Additive slicing settings
        p.LayerHeight      = add.LayerHeight;
        p.BeadWidth        = add.BeadWidth;
        p.FirstLayerHeight = add.FirstLayerHeight;
        p.SliceMethod      = add.Method.ToString();
        p.SlicingMode      = add.SlicingMode;
        p.PassAngle        = add.PassAngle;
        p.TiltAngle        = add.TiltAngle;
        p.TiltAngleX       = add.TiltAngleX;
        p.PrintSpeed         = add.PrintSpeed;
        p.TravelSpeed      = add.TravelSpeed;
        p.Acceleration     = add.Acceleration;
        p.ApproachZ        = add.ApproachZ;
        p.ZHopMm                  = add.ZHopMm;
        p.WipeModeDisplay         = add.WipeModeDisplay;
        p.WipeLengthMm            = add.WipeLengthMm;
        p.WipeRampMm              = add.WipeRampMm;
        p.WipeSpeed               = add.WipeSpeed;
        p.ExtrusionStartWaitSec   = add.ExtrusionStartWaitSec;
        p.ExtrusionResumeWaitSec  = add.ExtrusionResumeWaitSec;
        p.ResumeRampEnabled         = add.ResumeRampEnabled;
        p.ResumeRampStartSpeed      = add.ResumeRampStartSpeed;
        p.ResumeRampStartRpmPercent = add.ResumeRampStartRpmPercent;
        p.ResumeRampDistanceMm      = add.ResumeRampDistanceMm;
        p.ResumeRampSteps           = add.ResumeRampSteps;
        p.SeamGuidePoints = add.SeamGuides
            .Select(g => new[] { (float)g.X, (float)g.Y, (float)g.Z })
            .ToList();
        p.ToolDataIndex    = add.ToolDataIndex;
        p.BaseDataIndex    = add.BaseDataIndex;
        p.ToolheadA        = add.ToolheadA;
        p.ToolheadB        = add.ToolheadB;
        p.ToolheadC        = add.ToolheadC;
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
}
