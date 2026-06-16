using Avalonia;
using Avalonia.Threading;
using MassiveSlicer.App;
using MassiveSlicer.Core.C3Bridge;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Scanning;
using MassiveSlicer.Viewport;
using MassiveSlicer.Viewport.Loading;
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

    private bool _startupSyncDone;

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
        RightPanel.Scan.PropertyChanged          += (_, e) =>
        {
            // Skip transient capture-progress properties.
            if (e.PropertyName is nameof(ScanSettingsViewModel.IsScanning)
                                or nameof(ScanSettingsViewModel.ScanStatus))
                return;
            PersistSettings();
        };

        // Wire toolbar commands to cross-panel actions.
        Toolbar.FrameAllRequested  += (_, _) => Viewport.OnFrameAllRequested?.Invoke();
        Viewport.OnSaveViewRequested = SaveCurrentView;

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

        // Swap the displayed end-effector to match the active workflow tab:
        // the Scan tab shows the scanner, Additive shows the extruder.
        RightPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RightPanelViewModel.ActiveTab)) return;
            var toolName = RightPanel.ActiveTab switch
            {
                RightPanelTab.Scan     => Viewport.ActiveCell?.ScanToolName,
                RightPanelTab.Additive => "HV Extruder",
                _                      => null,
            };
            if (toolName is null) return;
            int idx = robot.ToolNames.IndexOf(toolName);
            if (idx >= 0) robot.SelectedToolIndex = idx;
        };
        robot.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RobotPanelViewModel.ConnectionStatus))
                Toolbar.RobotStatus = robot.ConnectionStatus;
        };

        // Propagate KRL frame dropdown selection → scan settings indices.
        robot.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RobotPanelViewModel.KrlToolIndex))
                RightPanel.Scan.ToolDataIndex = robot.KrlToolIndex;
            if (e.PropertyName == nameof(RobotPanelViewModel.KrlBaseIndex))
                RightPanel.Scan.BaseDataIndex = robot.KrlBaseIndex;
        };

        // When sync connects, apply live TCP values from $config.dat and re-swap
        // the active tool so the viewport TCP gizmo reflects the real robot data.
        robot.OnDatRead = snapshot =>
        {
            Viewport.LiveDat = snapshot;

            // Log what we read.
            Console.Log($"[dat] Read {snapshot.Tools.Count} TOOL_DATA entries from $config.dat");
            foreach (var t in snapshot.Tools)
                Console.Log($"[dat]  TOOL[{t.Index}]  X={t.X:F2}  Y={t.Y:F2}  Z={t.Z:F2}  A={t.A:F2}  B={t.B:F2}  C={t.C:F2}");

            // Re-trigger the selected tool so PendingToolSwap picks up the live TCP values.
            var current = robot.SelectedToolIndex;
            robot.SelectedToolIndex = -1;
            robot.SelectedToolIndex = current;
        };

        // After each cell swap: populate KRL dropdowns, select tool for active tab,
        // and on first load trigger auto-sync with the robot controller.
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

            if (!_startupSyncDone)
            {
                _startupSyncDone = true;
                robot.ConnectCommand.Execute(null);
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
                var last = start.ErrorCode != 0 ? start : run.ErrorCode != 0 ? run : sel;
                var hint = last.ErrorCode == 7
                    ? "ErrorNotImplemented — port 7000 is KUKAVARPROXY only; install/start the C3 Bridge Interface Server on the KRC."
                    : last.ErrorCode == 0
                        ? "ErrorGeneral — program won't compile, wrong path, or robot not in AUTO with drives on. On the pendant open BED_SCAN_CAL (DEF name), compile, then retry."
                        : "Check console for select/run/start codes.";
                bedCal.SetStatus($"Could not start BED_SCAN_CAL — select={C3ErrorName(sel.ErrorCode)}, run={C3ErrorName(run.ErrorCode)}, start={C3ErrorName(start.ErrorCode)}. {hint}");
                return;
            }
            Console.Log($"[bedcal] Started {startedPath} via C3 program-run.");

            for (int n = 0; n < target; n++)
            {
                // Wait for the robot to reach a stop ($FLAG[1]) or finish ($FLAG[2]); ~120 s/stop.
                bool atStop = false, done = false;
                for (int poll = 0; poll < 600; poll++)
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
            bedCal.Compute();
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
        add.PassAngle     = p.PassAngle;
        add.TiltAngle     = p.TiltAngle;
        add.TiltAngleX    = p.TiltAngleX;
        add.PrintSpeed      = p.PrintSpeed;
        add.TravelSpeed   = p.TravelSpeed;
        add.Acceleration  = p.Acceleration;
        add.ApproachZ     = p.ApproachZ;
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
        p.PassAngle        = add.PassAngle;
        p.TiltAngle        = add.TiltAngle;
        p.TiltAngleX       = add.TiltAngleX;
        p.PrintSpeed         = add.PrintSpeed;
        p.TravelSpeed      = add.TravelSpeed;
        p.Acceleration     = add.Acceleration;
        p.ApproachZ        = add.ApproachZ;
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
