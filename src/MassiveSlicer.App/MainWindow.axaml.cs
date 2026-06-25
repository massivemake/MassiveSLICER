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

        // -- Local control bridge (external tooling reads console / sends commands) --
        if (_controlBridge is null)
        {
            try
            {
                _controlBridge = new MassiveSlicer.App.Console.LocalControlBridge(vm);
                int port = _controlBridge.Start();
                vm.Console.Log(port > 0
                    ? $"[bridge] control API on http://127.0.0.1:{port}  — GET /status, GET /console?n=N, POST /command"
                    : "[bridge] control API failed to start (ports busy).");
            }
            catch (Exception ex) { vm.Console.LogError($"[bridge] {ex.Message}"); }
        }

        // -- Right panel column toggle -----------------------------------------
        vm.Toolbar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ToolbarViewModel.IsRightPanelVisible)) return;
            bool visible = vm.Toolbar.IsRightPanelVisible;
            WorkAreaGrid.ColumnDefinitions[3].Width = visible ? new GridLength(4)   : new GridLength(0);
            WorkAreaGrid.ColumnDefinitions[4].Width = visible ? new GridLength(300)  : new GridLength(0);
        };

        // Apply persisted panel visibility before first layout pass.
        bool rightVisible = vm.Toolbar.IsRightPanelVisible;
        WorkAreaGrid.ColumnDefinitions[3].Width = rightVisible ? new GridLength(4)   : new GridLength(0);
        WorkAreaGrid.ColumnDefinitions[4].Width = rightVisible ? new GridLength(300)  : new GridLength(0);

        // -- Cell selector -----------------------------------------------------
        vm.LeftPanel.OnCellSelected = SwitchCell;
        vm.Viewport.OnDevCellReloadRequested = SwitchCell;
        vm.Viewport.OnDevLog = msg => vm.Console.Log(msg);

        var cellsRoot = MassiveSlicer.Core.IO.CellPaths.PreferredCellsDirectory();
        if (cellsRoot is not null)
            vm.Console.Log($"[cell] using cells directory: {cellsRoot}");

        var cells = CellLoader.FindAll()
            .Select(path =>
            {
                string full = Path.GetFullPath(path);
                string name;
                try   { name = CellLoader.Load(full).Name; }
                catch { name = Path.GetFileNameWithoutExtension(full); }
                return (name, full);
            })
            .ToList();

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

        // Default to LFAM 2 — empty outliner, robot not synced until user connects.
        var lfam2Idx = cells.FindIndex(c =>
            c.name.Contains("LFAM 2", StringComparison.OrdinalIgnoreCase) ||
            c.name.Contains("LFAM2",  StringComparison.OrdinalIgnoreCase));
        if (cells.Count > 0)
            vm.LeftPanel.SelectedCellIndex = lfam2Idx >= 0 ? lfam2Idx : 0;

        // -- Model loading -----------------------------------------------------
        vm.Toolbar.ModelLoadRequested += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title          = "Open 3D Model",
                AllowMultiple  = false,
                FileTypeFilter = [
                    new("3D Files") { Patterns = ["*.glb", "*.gltf", "*.stl", "*.obj", "*.3mf"] },
                    new("GL Transmission Format") { Patterns = ["*.glb", "*.gltf"] },
                    new("STL Files") { Patterns = ["*.stl"] },
                    new("OBJ Files") { Patterns = ["*.obj"] },
                    new("3MF Files") { Patterns = ["*.3mf"] },
                    new("All Files") { Patterns = ["*.*"] },
                ],
            });

            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path is null) return;

            await LoadAndAddNodeAsync(path, vm);
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
            var win = new Views.PreferencesWindow { DataContext = vm.Preferences };
            await win.ShowDialog(this);
        };

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
    }

    // -- Cell switching --------------------------------------------------------

    private void SwitchCell(string path)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        path = Path.GetFullPath(path);

        _cellLoadCts?.Cancel();
        _cellLoadCts?.Dispose();
        _cellLoadCts = new CancellationTokenSource();
        var ct  = _cellLoadCts.Token;
        var gen = Interlocked.Increment(ref _cellLoadGeneration);
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

                    vm.Viewport.PendingCellSwap.Enqueue(payload);
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

        vm.SaveWorkspace(path);
    }
}
