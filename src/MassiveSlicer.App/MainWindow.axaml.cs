using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cellLoadCts;
    private int _cellLoadGeneration;
    private MassiveSlicer.App.Console.LocalControlBridge? _controlBridge;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        vm.CaptureAppScreenshot = () => CaptureAppScreenshotAsync();
        vm.CaptureViewportPng   = () => Viewport.CaptureScreenshotAsync();

        // Route GL host diagnostics to the in-app console (readable via the control bridge).
        MassiveSlicer.App.Views.GlHostControl.Diag = msg =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is MainWindowViewModel m) m.Console.Log(msg);
            });

        // -- Local control bridge (external tooling reads console / sends commands) --
        if (_controlBridge is null)
        {
            try
            {
                _controlBridge = new MassiveSlicer.App.Console.LocalControlBridge(vm);
                int port = _controlBridge.Start();
                vm.Console.Log(port > 0
                    ? $"[bridge] control API on http://127.0.0.1:{port}  — GET /status, GET /console?n=N, GET /screenshot, GET|POST /materials, POST /command"
                    : "[bridge] control API failed to start (ports busy).");
            }
            catch (Exception ex) { vm.Console.LogError($"[bridge] {ex.Message}"); }
        }

        // -- Plasticity live bridge (collapsible section in the N-key HUD) ------
        vm.Viewport.Plasticity.Attach(vm.Viewport, msg => vm.Console.Log(msg));
        vm.Viewport.MassiveBrain.Attach(vm.Viewport, msg => vm.Console.Log(msg));
        vm.Viewport.MassiveBrain.Enabled = true;   // sync server on by default (localhost:4547)

        // -- Right panel toggle (floating card) --------------------------------
        vm.Toolbar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ToolbarViewModel.IsRightPanelVisible)) return;
            RightPanelHost.IsVisible = vm.Toolbar.IsRightPanelVisible;
        };

        // Apply persisted panel visibility before first layout pass.
        RightPanelHost.IsVisible = vm.Toolbar.IsRightPanelVisible;


        // -- Cell selector -----------------------------------------------------
        vm.LeftPanel.OnCellSelected = SwitchCell;
        vm.Viewport.OnDevCellReloadRequested = SwitchCell;
        vm.Viewport.OnDevLog = msg => vm.Console.Log(msg);

        var cellsRoot = MassiveSlicer.Core.IO.CellPaths.PreferredCellsDirectory();
        if (cellsRoot is not null)
        {
            vm.Console.Log($"[cell] using cells directory: {cellsRoot}");
            if (MassiveSlicer.Core.IO.CellPaths.IsNasCellsDirectory(cellsRoot))
                vm.Console.LogError(
                    "[cell] WARNING: cell geometry is coming from the shared NAS share, not this build. " +
                    "Bed and robot positions may differ from what is committed. " +
                    "Unset MASSIVE_SLICER_CELLS_NAS to use this build's own cells.");
        }

        var smbSeeds = new List<(string Name, string BridgeIp)>();
        var cells = CellLoader.FindAll()
            .Select(path =>
            {
                string full = Path.GetFullPath(path);
                string name;
                try
                {
                    var cfg = CellLoader.Load(full);
                    name = cfg.Name;
                    smbSeeds.Add((cfg.Name, cfg.BridgeIp));
                }
                catch { name = Path.GetFileNameWithoutExtension(full); }
                return (name, full);
            })
            .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        vm.EnsureRobotSmbEntries(smbSeeds);

        if (cells.Count == 0)
        {
            vm.Console.LogError("[cell] No cell files found — robot and bed will not appear. Check assets/cells beside the .exe.");
            System.Console.Error.WriteLine("No cell files found in assets/cells/.");
        }
        else
        {
            vm.Console.Log($"[cell] discovered {cells.Count} cell(s).");
        }

        vm.LeftPanel.SetCells(cells);

        // When opening a .mass at startup, let OpenWorkspace load the saved cell first.
        // Otherwise prefer AppPreferences.DefaultCellName (machine-local prefs.json),
        // then LFAM 2, then the first discovered cell.
        if (App.StartupWorkspacePath is null && cells.Count > 0)
            vm.LeftPanel.SelectedCellIndex = ResolveDefaultCellIndex(cells, vm.AppPreferences.DefaultCellName);

        // -- Model loading -----------------------------------------------------
        vm.Toolbar.ModelLoadRequested += async (_, _) => await ShowModelImportPickerAsync(vm);

        // Left-panel import dropzone: click opens the picker, dropped files import directly.
        LeftPanelControl.ImportClickRequested += async () => await ShowModelImportPickerAsync(vm);
        LeftPanelControl.ImportFilesDropped += paths =>
        {
            foreach (var p in paths)
            {
                if (!vm.ImportModelFromPath(p))
                    vm.Console.LogError($"[import] Failed to import '{p}'.");
            }
        };

        vm.Viewport.OnModelReloadRequested = node => vm.ReloadOutlinerModel(node);

        vm.Viewport.OnModelReplaceRequested = async node =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title          = "Replace Model",
                AllowMultiple  = false,
                FileTypeFilter = [
                    new("3D Files") { Patterns = ["*.glb", "*.gltf", "*.stl", "*.obj", "*.3mf", "*.stp", "*.step"] },
                    new("GL Transmission Format") { Patterns = ["*.glb", "*.gltf"] },
                    new("STL Files") { Patterns = ["*.stl"] },
                    new("STEP Files") { Patterns = ["*.stp", "*.step"] },
                    new("OBJ Files") { Patterns = ["*.obj"] },
                    new("3MF Files") { Patterns = ["*.3mf"] },
                    new("All Files") { Patterns = ["*.*"] },
                ],
            });

            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path is null) return;

            vm.ReplaceOutlinerModel(node, path);
        };

        // -- Relief heightmap picker (Subtractive tab) -------------------------
        vm.RightPanel.Subtractive.BrowseHeightmapRequested += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title          = "Open Relief Heightmap",
                AllowMultiple  = false,
                FileTypeFilter = [
                    new("Image Files") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tga"] },
                    new("All Files") { Patterns = ["*.*"] },
                ],
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path is null) return;

            vm.RightPanel.Subtractive.HeightmapPath = path;
        };

        // -- Workspace open / save (File menu) ---------------------------------
        vm.Toolbar.OpenWorkspaceRequested += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title          = "Open Workspace",
                AllowMultiple  = false,
                FileTypeFilter = [
                    new("MassiveSlicer Workspace") { Patterns = ["*.mass"] },
                    new("All Files") { Patterns = ["*.*"] },
                ],
            });
            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (path is null) return;

            vm.OpenWorkspace(path);
        };

        vm.Toolbar.SaveWorkspaceRequested += (_, _) =>
        {
            if (!vm.TrySaveCurrentWorkspace())
                _ = SaveWorkspaceAsAsync(vm);
        };

        vm.Toolbar.SaveWorkspaceAsRequested += async (_, _) =>
        {
            await SaveWorkspaceAsAsync(vm);
        };

        // -- Preferences -------------------------------------------------------
        vm.Toolbar.PreferencesRequested += async (_, _) =>
        {
            vm.Preferences.Erp = vm.Viewport.Erp;
            vm.Preferences.RefreshSmbRows();
            var win = new Views.PreferencesWindow { DataContext = vm.Preferences };
            await win.ShowDialog(this);
        };

        // ERP dock cog / hints open Preferences directly on the Connections section.
        vm.Viewport.Erp.OpenPreferencesRequested = () => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            vm.Preferences.Erp = vm.Viewport.Erp;
            vm.Preferences.RefreshSmbRows();
            var win = new Views.PreferencesWindow { DataContext = vm.Preferences };
            win.ShowConnections();
            await win.ShowDialog(this);
        });

        // -- Import KRL (File menu) --------------------------------------------
        vm.Toolbar.ImportKrlRequested += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title          = "Import KRL",
                AllowMultiple  = false,
                FileTypeFilter = [
                    new("KUKA Robot Language") { Patterns = ["*.src", "*.SRC"] },
                    new("All Files") { Patterns = ["*.*"] },
                ],
            });
            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (path is null) return;

            vm.ImportKrlToolpath(path);
        };

        if (App.StartupWorkspacePath is { } startupWorkspace)
            vm.OpenWorkspace(startupWorkspace);
    }

    /// <summary>
    /// Picks the cold-start cell index from machine-local <paramref name="preferredName"/>,
    /// then LFAM 2, then index 0. Matching is case-insensitive contains on the display name.
    /// </summary>
    internal static int ResolveDefaultCellIndex(
        IReadOnlyList<(string name, string full)> cells,
        string? preferredName)
    {
        if (cells.Count == 0) return -1;

        static int Find(IReadOnlyList<(string name, string full)> list, params string[] needles)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i].name;
                foreach (var needle in needles)
                {
                    if (n.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            // Prefer exact-ish match on the preferred string, then a space-stripped form (LFAM3).
            var compact = preferredName.Replace(" ", "", StringComparison.Ordinal);
            int pref = Find(cells, preferredName.Trim());
            if (pref < 0 && compact.Length > 0 && !compact.Equals(preferredName.Trim(), StringComparison.OrdinalIgnoreCase))
                pref = Find(cells, compact);
            if (pref >= 0) return pref;
        }

        int lfam2 = Find(cells, "LFAM 2", "LFAM2");
        return lfam2 >= 0 ? lfam2 : 0;
    }

    // -- Cell switching --------------------------------------------------------

    private void SwitchCell(string path)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        path = Path.GetFullPath(path);
        vm.NotifyCellLoadStarted();

        _cellLoadCts?.Cancel();
        _cellLoadCts?.Dispose();
        _cellLoadCts = new CancellationTokenSource();
        var ct  = _cellLoadCts.Token;
        // Workspace opens assign a generation from MainWindowViewModel; keep the stale-load
        // guard (_cellLoadGeneration) in sync or the payload is dropped before enqueue.
        int gen;
        if (vm.Viewport.WorkspaceCellLoadGeneration is int workspaceGen)
        {
            gen = workspaceGen;
            Volatile.Write(ref _cellLoadGeneration, workspaceGen);
            vm.Viewport.WorkspaceCellLoadGeneration = null;
        }
        else
        {
            gen = Interlocked.Increment(ref _cellLoadGeneration);
        }
        var defaultTab = vm.RightPanel.ActiveTab;
        var cacheKey   = CellSceneCache.CacheKey(path);
        bool cacheHit  = CellSceneCache.TryGet(cacheKey, out _);

        vm.Console.Log(cacheHit
            ? $"[cell] switching to {Path.GetFileNameWithoutExtension(path)} (cached)…"
            : $"[cell] loading {Path.GetFileNameWithoutExtension(path)}…");

        Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                ct.ThrowIfCancellationRequested();
                var payload = CellSceneLoader.Load(path, defaultTab, ct);
                var elapsed = sw.ElapsedMilliseconds;
                if (ct.IsCancellationRequested || gen != Volatile.Read(ref _cellLoadGeneration))
                    return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested || gen != Volatile.Read(ref _cellLoadGeneration))
                        return;

                    var toolCount = payload.MultiTools?.Tools.Count ?? 0;
                    vm.Console.Log(
                        $"[cell] {payload.Config.Name}: robot={(payload.RobotBaseNode is not null)} " +
                        $"bed={(payload.BedNode is not null)} env={payload.EnvironmentNodes.Count} tools={toolCount} " +
                        $"rotary={(payload.RotaryBedPivot is not null)} — CPU ready in {elapsed}ms" +
                        (cacheHit ? " (geometry cache)" : "") +
                        " (GPU upload continues in viewport…)");

                    if (payload.RobotBaseNode is null)
                        vm.Console.LogError("[cell] Robot model did not load — check console for missing .glb paths.");
                    if (payload.BedNode is null && payload.RotaryBedPivot is null && !payload.Config.Bed.Hidden)
                        vm.Console.LogError("[cell] Bed model did not load.");
                    if (payload.Config.Bed.Hidden && payload.RotaryBedPivot is null)
                        vm.Console.Log("[cell] LFAM 3 uses a hidden flat bed; rotary bed mesh was not built.");

                    var bedCfg = payload.Config.Bed;
                    var rp     = payload.Config.Robot.WorldPosition;
                    var marker = bedCfg.BaseMarkerWorld(rp);
                    var grid   = bedCfg.VisualGridCorner(rp);
                    var off    = bedCfg.VisualOffset is { } vo ? $"{vo.X:F1}, {vo.Y:F1}" : "none";
                    vm.Console.Log(
                        $"[bed] {payload.Config.Name}: visualOffset=({off})  BASE marker=({marker.X:F1}, {marker.Y:F1})  visual grid=({grid.X:F1}, {grid.Y:F1})");

                    vm.Viewport.PendingCellSwap.Enqueue(payload with { Generation = gen });
                    vm.Viewport.NotifyRenderNeeded();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    vm.Console.LogError($"[cell] Failed to load '{Path.GetFileName(path)}': {ex.Message}");
                    System.Console.Error.WriteLine($"Failed to load cell '{path}': {ex.Message}");
                }
            }
        }, ct);
    }

    private async Task ShowModelImportPickerAsync(MainWindowViewModel vm)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title          = "Open 3D Model",
            AllowMultiple  = false,
            FileTypeFilter = [
                new("3D Files") { Patterns = ["*.glb", "*.gltf", "*.stl", "*.obj", "*.3mf", "*.stp", "*.step"] },
                new("GL Transmission Format") { Patterns = ["*.glb", "*.gltf"] },
                new("STL Files") { Patterns = ["*.stl"] },
                new("STEP Files") { Patterns = ["*.stp", "*.step"] },
                new("OBJ Files") { Patterns = ["*.obj"] },
                new("3MF Files") { Patterns = ["*.3mf"] },
                new("All Files") { Patterns = ["*.*"] },
            ],
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        await LoadAndAddNodeAsync(path, vm);
    }

    private Task LoadAndAddNodeAsync(string filePath, MainWindowViewModel vm)
    {
        if (!vm.ImportModelFromPath(filePath))
            System.Console.Error.WriteLine($"Failed to load model: {filePath}");
        return Task.CompletedTask;
    }

    private async Task SaveWorkspaceAsAsync(MainWindowViewModel vm)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Workspace As",
            DefaultExtension  = "mass",
            SuggestedFileName = vm.SuggestedWorkspaceFileName,
            FileTypeChoices   = [new("MassiveSlicer Workspace") { Patterns = ["*.mass"] }],
        });
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (path is null) return;

        await vm.SaveWorkspaceAsync(SavePathUtil.Normalize(path, "mass"));
    }

    /// <summary>
    /// Captures the full application window (toolbar, panels, console, viewport) as PNG.
    /// Refreshes the GL viewport first so the 3D frame matches what is on screen.
    /// </summary>
    internal async Task<byte[]?> CaptureAppScreenshotAsync()
    {
        var viewportPng = await Viewport.CaptureScreenshotAsync();
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            UpdateLayout();
            return AppScreenshotCapture.CapturePng(this, viewportPng, Viewport.ViewportSurface,
                [LeftPanelHost, RightPanelHost, ToolbarHost]);
        });
    }
}
