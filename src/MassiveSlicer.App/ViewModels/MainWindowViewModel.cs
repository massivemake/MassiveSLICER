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

        // ── Auto-save on any relevant change ─────────────────────────────────

        Viewport.PropertyChanged += (_, e) =>
        {
            // Cross-panel: mesh → Additive (or LFAM 3 phase tab); toolpath → Toolpath.
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
        // (rendered glTF flange × glTF→KUKA correction), NOT KukaIkSolver.ForwardKinematics.
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
            var tools = Viewport.ActiveCell?.EffectiveTools;
            ToolCellConfig? scannerTool = null;
            if (tools is not null && (uint)robot.SelectedToolIndex < (uint)tools.Count)
                scannerTool = tools[robot.SelectedToolIndex];
            if (scannerTool is null) return null;
            if (Viewport.GetToolWorldPose?.Invoke(scannerTool) is not { } p) return null;
            // OpenTK Matrix4 (row-vector: rows = camera axes in world, Row3 = origin) → System.Numerics.
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

        // Propagate KRL frame dropdown / selected tool → export settings for the active tab.
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
            // Snapshot the camera pose at capture time using the currently selected tool.
            var robot = RightPanel.Settings.Robot;
            var tools = Viewport.ActiveCell?.EffectiveTools;
            ToolCellConfig? scannerTool = null;
            if (tools is not null && (uint)robot.SelectedToolIndex < (uint)tools.Count)
                scannerTool = tools[robot.SelectedToolIndex];

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
                Console.Log($"[scan] No camera pose — tool={scannerTool?.Name ?? "none"}, flange available={Viewport.GetToolWorldPose is not null}");

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
                // Registered: camera frame → world via robot pose at capture time.
                node.LocalTransform = pose;
                Console.Log("[scan] Registered via robot pose (scanner TOOL frame).");
            }
            else
            {
                // No robot loaded — flip the camera frame upright and centre on the bed.
                node.LocalTransform = Matrix4.CreateRotationX(MathF.PI);
                ImportHelper.PlaceOnBed(node, Viewport.ActiveCell);
                Console.Log("[scan] No robot pose available — placed scan on bed centre unregistered.");
            }

            Viewport.AddUserNode(node);
            if (Viewport.IsPrePrintScanRegistrationPhase)
                Viewport.RegisterArmatureScanMesh(node);
            var saved = result.SavedZdfPath is { } p
                ? $", saved {System.IO.Path.GetFileName(p)}{(result.SavedMetadataPath is not null ? " + .json" : "")}"
                : "";
            scan.ScanStatus = $"Added \"{name}\" — {result.ValidPointCount:N0} points{saved}";
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

    private async Task RunAutoBedCalibration()
    {
        var robot  = RightPanel.Settings.Robot;
        var bedCal = robot.BedCalibration;

        if (!robot.IsConnected)
        {
            bedCal.SetStatus("Sync the robot first — C3Bridge must be connected.");
            return;
        }

        try
        {
            var dest = robot.DeployBedScanProgram();
            Console.Log($"[bedcal] Deployed KRL → {dest}");
            var folder = System.IO.Path.GetDirectoryName(dest);
            if (folder is not null)
            {
                try
                {
                    foreach (var alt in System.IO.Directory.EnumerateFiles(folder, "*bed*scan*cal*.src"))
                    {
                        if (!string.Equals(alt, dest, StringComparison.OrdinalIgnoreCase))
                            Console.Log($"[bedcal] Also on controller: {System.IO.Path.GetFileName(alt)} — C3 path uses DEF BED_SCAN_CAL, not the filename.");
                    }
                }
                catch { /* optional */ }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: the program may already be on the controller.
            Console.Log($"[bedcal] KRL deploy skipped: {ex.Message}");
        }

        bedCal.ClearSamples();
        bedCal.SetStatus("Starting BED_SCAN_CAL on the robot (AUTO mode, drives on)…");

        // C3 path must match the KRL DEF name (BED_SCAN_CAL). On LFAM3 the working form is
        // /R1/BED_SCAN_CAL (not /R1/Program/…). Filename on the share can differ.
        string[] programPaths =
        [
            "/R1/BED_SCAN_CAL",
            "BED_SCAN_CAL",
            "/R1/Program/BED_SCAN_CAL",
            "/R1/PROGRAM/BED_SCAN_CAL",
        ];
        const int target = 10;
        int captured = 0;
        robot.PauseStreaming();
        try
        {
            await robot.SetFlagAsync(1, false);
            await robot.SetFlagAsync(2, false);

            // Remote start via C3 Bridge message #10 (select + run). Requires the C3 Bridge
            // Interface Server on the KRC — stock KUKAVARPROXY alone returns ErrorNotImplemented (7).
            C3BridgeClient.ProgramResult sel = default, run = default, start = default;
            string? startedPath = null;
            foreach (var programName in programPaths)
            {
                sel = await robot.SelectProgramAsync(programName);
                Console.Log($"[bedcal] Select {programName}: {C3ErrorName(sel.ErrorCode)} (success={sel.Success}).");
                if (!sel.Success) continue;

                run = await robot.RunProgramAsync(programName);
                Console.Log($"[bedcal] Run {programName}: {C3ErrorName(run.ErrorCode)} (success={run.Success}).");
                if (run.Success) { startedPath = programName; break; }

                // Some C3 builds accept Select but need a separate interpreter Start (cmd 2).
                start = await robot.ProgramControlAsync(2);
                Console.Log($"[bedcal] Start after select {programName}: {C3ErrorName(start.ErrorCode)} (success={start.Success}).");
                if (start.Success) { startedPath = programName; break; }
            }

            if (startedPath is null)
            {
                // This controller does variable read/write (sync + the $FLAG handshake work) but not
                // C3 program select/start (E_FAIL), and the .src deploy is blocked by share creds.
                // Don't give up: fall back to a MANUAL start — the operator runs BED_SCAN_CAL on the
                // pendant and we still drive the capture/clear handshake here (which only needs the
                // working variable read/write). Previously we returned here, so the loop never ran and
                // nothing ever cleared $FLAG[1].
                Console.Log($"[bedcal] C3 auto-start unavailable (select={C3ErrorName(sel.ErrorCode)}, " +
                            $"run={C3ErrorName(run.ErrorCode)}, start={C3ErrorName(start.ErrorCode)}); " +
                            "waiting for a manual pendant start.");
                bedCal.SetStatus("Couldn't auto-start. Run BED_SCAN_CAL on the pendant now (AUTO, drives on) — capture begins at the first stop and the robot is released automatically.");
            }
            else
            {
                Console.Log($"[bedcal] Started {startedPath} via C3 program-run.");
            }

            for (int n = 0; n < target; n++)
            {
                // Wait for the robot to reach a stop ($FLAG[1]) or finish ($FLAG[2]).
                // Allow extra time on the first stop so the operator can start the program on the
                // pendant when C3 auto-start wasn't available (~360 s); ~120 s/stop afterwards.
                bool atStop = false, done = false;
                int pollMax = n == 0 ? 1800 : 600;
                for (int poll = 0; poll < pollMax; poll++)
                {
                    if (await robot.ReadFlagAsync(2)) { done = true; break; }
                    if (await robot.ReadFlagAsync(1)) { atStop = true; break; }
                    await Task.Delay(200);
                }
                if (done) break;
                if (!atStop)
                {
                    bedCal.SetStatus($"Timed out waiting for the robot (captured {captured}/{target}).");
                    break;
                }

                var axes = await robot.ReadAxesAsync();
                robot.E1 = Math.Round(axes[6], 2);
                await bedCal.AddSampleAsync();          // scans + records (E1, world point)
                captured++;
                bedCal.SetStatus($"Captured {captured}/{target} (E1 {axes[6]:F0}°)…");

                await robot.SetFlagAsync(1, false);     // release the robot to the next stop
            }
        }
        catch (Exception ex)
        {
            bedCal.SetStatus($"Auto-cal error: {ex.Message}");
            Console.Log($"[bedcal] ERROR: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            robot.ResumeStreaming();
        }

        if (captured >= 3)
        {
            bedCal.Compute();
            if (bedCal.HasResult) bedCal.Apply();   // auto-apply the sweep result to the cell (no extra click)
            else Console.Log($"[bedcal] Sweep captured {captured} samples but the fit failed — not applied.");
        }
        else
        {
            Console.Log($"[bedcal] Sweep ended with {captured} samples (need >=3) — nothing applied.");
        }
    }

    /// <summary>
    /// Automated 3D scan-tool (hand-eye) calibration: deploy + run SCAN_TOOL_CAL (or fall back to a
    /// manual pendant start), then at each pose read the robot axes, capture a scan of the calibration
    /// card, release the robot ($FLAG[1]), and finally run the hand-eye fit and apply it to the TCP.
    /// Mirrors <see cref="RunAutoBedCalibration"/>; the robot motion lives in the KRL pose sweep.
    /// </summary>
    private async Task RunAutoScanToolCalibration()
    {
        var robot   = RightPanel.Settings.Robot;
        var scanCal = RightPanel.Scan.Calibration;

        if (!robot.IsConnected)
        {
            scanCal.SetStatus("Sync the robot first — C3Bridge must be connected.");
            return;
        }
        if (scanCal.IsAutoRunning)
        {
            Console.Log("[scancal] Auto calibration already running — ignoring re-click.");
            return;
        }
        scanCal.SetAutoRunning(true);
        try   // outer: always clear the sweep-in-progress flag, however we exit
        {

        try
        {
            var dest = robot.DeployScanToolProgram();
            Console.Log($"[scancal] Deployed KRL → {dest}");
        }
        catch (Exception ex)
        {
            Console.Log($"[scancal] KRL deploy skipped: {ex.Message}");   // may already be on the controller
        }

        scanCal.ClearForAuto();
        scanCal.SetStatus("Aim the scanner at the card, then starting SCAN_TOOL_CAL (AUTO, drives on)…");

        string[] programPaths =
        [
            "/R1/SCAN_TOOL_CAL",
            "SCAN_TOOL_CAL",
            "/R1/Program/SCAN_TOOL_CAL",
            "/R1/PROGRAM/SCAN_TOOL_CAL",
        ];
        const int target = 9;   // must match the pose count in SCAN_TOOL_CAL.src
        int captured = 0;
        robot.PauseStreaming();
        try
        {
            await robot.SetFlagAsync(1, false);
            await robot.SetFlagAsync(2, false);

            C3BridgeClient.ProgramResult sel = default, run = default, start = default;
            string? startedPath = null;
            foreach (var programName in programPaths)
            {
                sel = await robot.SelectProgramAsync(programName);
                Console.Log($"[scancal] Select {programName}: {C3ErrorName(sel.ErrorCode)} (success={sel.Success}).");
                if (!sel.Success) continue;

                run = await robot.RunProgramAsync(programName);
                Console.Log($"[scancal] Run {programName}: {C3ErrorName(run.ErrorCode)} (success={run.Success}).");
                if (run.Success) { startedPath = programName; break; }

                start = await robot.ProgramControlAsync(2);
                Console.Log($"[scancal] Start after select {programName}: {C3ErrorName(start.ErrorCode)} (success={start.Success}).");
                if (start.Success) { startedPath = programName; break; }
            }

            if (startedPath is null)
            {
                // As with bed-cal: variable read/write works even when C3 program-start doesn't.
                // Fall back to a manual pendant start and still drive the capture/clear handshake.
                Console.Log($"[scancal] C3 auto-start unavailable (select={C3ErrorName(sel.ErrorCode)}, " +
                            $"run={C3ErrorName(run.ErrorCode)}, start={C3ErrorName(start.ErrorCode)}); " +
                            "waiting for a manual pendant start.");
                scanCal.SetStatus("Couldn't auto-start. Aim the scanner at the card, then run SCAN_TOOL_CAL on the pendant (AUTO, drives on) — scanning begins at the first pose and the robot is released automatically.");
            }
            else
            {
                Console.Log($"[scancal] Started {startedPath} via C3 program-run.");
            }

            for (int n = 0; n < target; n++)
            {
                bool atStop = false, done = false;
                int pollMax = n == 0 ? 1800 : 600;   // ~360 s for a manual first start, then ~120 s/pose
                for (int poll = 0; poll < pollMax; poll++)
                {
                    if (await robot.ReadFlagAsync(2)) { done = true; break; }
                    if (await robot.ReadFlagAsync(1)) { atStop = true; break; }
                    await Task.Delay(200);
                }
                if (done) break;
                if (!atStop)
                {
                    scanCal.SetStatus($"Timed out waiting for the robot (captured {captured}/{target} poses).");
                    break;
                }

                // Drive the viewport FK to the robot's actual pose so the flange frame the hand-eye
                // capture reads is current (streaming is paused during the sweep), then capture.
                var axes = await robot.ReadAxesAsync();
                robot.A1 = Math.Round(axes[0], 2); robot.A2 = Math.Round(axes[1], 2); robot.A3 = Math.Round(axes[2], 2);
                robot.A4 = Math.Round(axes[3], 2); robot.A5 = Math.Round(axes[4], 2); robot.A6 = Math.Round(axes[5], 2);
                robot.E1 = Math.Round(axes[6], 2);
                await Task.Delay(400);   // let the FK settle before reading the flange

                bool ok = await scanCal.CapturePoseAutoAsync();
                if (ok) captured++;
                scanCal.SetStatus($"Captured {captured}/{target} poses (E1 {axes[6]:F0}°)…");

                await robot.SetFlagAsync(1, false);   // release the robot to the next pose
            }
        }
        catch (Exception ex)
        {
            scanCal.SetStatus($"Auto scan-cal error: {ex.Message}");
            Console.Log($"[scancal] ERROR: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            robot.ResumeStreaming();
        }

        if (captured >= 3)
        {
            await scanCal.ComputeCalibrationAsync();
            if (scanCal.HasResult)
            {
                // Calibrated with the uncalibrated working tool (#5); SAVE the computed camera TCP to
                // the calibrated Tool #6 (krlIndex 6), not the working tool used for the sweep.
                const int calibratedToolKrlIndex = 6;
                if (Viewport.ActiveCellPath is { } cellPath)
                {
                    CellLoader.SaveToolTcp(cellPath, calibratedToolKrlIndex,
                        (float)scanCal.ResultX, (float)scanCal.ResultY, (float)scanCal.ResultZ,
                        (float)scanCal.ResultA, (float)scanCal.ResultB, (float)scanCal.ResultC);
                    MassiveSlicer.App.CellSceneCache.Invalidate(cellPath);   // next cell load picks up the new tool-6 TCP
                    scanCal.MarkApplied($"Calibrated ✓ — saved to Tool #{calibratedToolKrlIndex}.");
                    Console.Log($"[scancal] Saved calibrated TCP to Tool #{calibratedToolKrlIndex}: " +
                                $"({scanCal.ResultX:F2}, {scanCal.ResultY:F2}, {scanCal.ResultZ:F2}) mm / " +
                                $"A{scanCal.ResultA:F3} B{scanCal.ResultB:F3} C{scanCal.ResultC:F3} " +
                                $"(rot residual {scanCal.ResidualRot:F3}°, trans {scanCal.ResidualTrans:F3} mm). " +
                                "Reselect/reload tool 6 to use it.");
                }
                else
                {
                    Console.Log("[scancal] Computed result but no active cell path — TCP not saved.");
                }
            }
            else Console.Log($"[scancal] Captured {captured} poses but the hand-eye fit failed — not applied.");
        }
        else
        {
            Console.Log($"[scancal] Ended with {captured} poses (need >=3) — nothing computed.");
        }
        }
        finally
        {
            scanCal.SetAutoRunning(false);
        }
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

        // Inspect BEFORE enqueuing: AddUserNode hands the node to the GL upload thread,
        // which clears PendingMesh once uploaded -- for small meshes that can happen before
        // the inspector runs, making it report 0 verts. Summarize while the mesh is intact.
        var report = GltfImportInspector.InspectLoaded(node, path);
        foreach (var line in report.ToLogLines())
            Console.Log(line);

        Viewport.AddUserNode(node);

        Console.Log($"[import] Added '{node.Name}' to scene.");
        return true;
    }

    /// <summary>
    /// Loads a <c>.mass</c> workspace from <paramref name="path"/> (File → Open).
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
            Console.Log("[view] No active cell — load a cell before saving a view.");
            return;
        }
        CellLoader.SaveCameraView(path, view);
        Viewport.ActiveCell = CellLoader.Load(path);   // refresh in-memory model
        Console.Log($"[view] Saved view to {System.IO.Path.GetFileName(path)} " +
                    $"(azimuth {view.Azimuth:F0}°, elevation {view.Elevation:F0}°, radius {view.Radius:F0} mm). " +
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
    /// source meshes → Additive (slicing settings); toolpaths → Toolpath.
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

        // LFAM 3 phase gating hides Additive outside Print — use the active phase tab.
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
    /// Print → Additive; Scan → Scan; Mill → Subtractive.
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
            if (s.Length == 8) s = s[2..]; // strip alpha → RRGGBB
            return new System.Numerics.Vector3(
                Convert.ToInt32(s[0..2], 16) / 255f,
                Convert.ToInt32(s[2..4], 16) / 255f,
                Convert.ToInt32(s[4..6], 16) / 255f);
        }
        catch { return System.Numerics.Vector3.Zero; }
    }
}
