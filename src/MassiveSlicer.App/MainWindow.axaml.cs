using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.ViewModels;
using OpenTK.Mathematics;

namespace MassiveSlicer.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // ── Right panel column toggle ─────────────────────────────────────────
        vm.Toolbar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ToolbarViewModel.IsRightPanelVisible)) return;
            bool visible = vm.Toolbar.IsRightPanelVisible;
            WorkAreaGrid.ColumnDefinitions[3].Width = visible ? new GridLength(4)   : new GridLength(0);
            WorkAreaGrid.ColumnDefinitions[4].Width = visible ? new GridLength(300)  : new GridLength(0);
        };

        // ── Console overlay toggle ────────────────────────────────────────────
        vm.Toolbar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ToolbarViewModel.IsConsoleVisible)) return;
            ConsoleOverlay.IsVisible = vm.Toolbar.IsConsoleVisible;
        };
        ConsoleOverlay.IsVisible = vm.Toolbar.IsConsoleVisible;

        // ── Cell selector ─────────────────────────────────────────────────────
        vm.LeftPanel.OnCellSelected = SwitchCell;

        var cells = CellLoader.FindAll()
            .Select(path =>
            {
                string name;
                try   { name = CellLoader.Load(path).Name; }
                catch { name = Path.GetFileNameWithoutExtension(path); }
                return (name, path);
            })
            .ToList();

        // TODO Phase 3: replace MessageBox with Avalonia dialog
        if (cells.Count == 0)
            System.Console.Error.WriteLine("No cell files found in assets/cells/.");

        vm.LeftPanel.SetCells(cells);

        // ── Model loading ─────────────────────────────────────────────────────
        vm.Toolbar.ModelLoadRequested += async (_, _) =>
        {
            // TODO Phase 3: replace with StorageProvider file picker
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title          = "Open 3D Model",
                AllowMultiple  = false,
                FileTypeFilter = [
                    new("3D Files") { Patterns = ["*.glb", "*.gltf", "*.stl"] },
                    new("GL Transmission Format") { Patterns = ["*.glb", "*.gltf"] },
                    new("STL Files") { Patterns = ["*.stl"] },
                    new("All Files") { Patterns = ["*.*"] },
                ],
            });

            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path is null) return;

            await LoadAndAddNodeAsync(path, vm);
        };

        // ── Preferences ───────────────────────────────────────────────────────
        vm.Toolbar.PreferencesRequested += async (_, _) =>
        {
            var win = new Views.PreferencesWindow { DataContext = vm.Preferences };
            await win.ShowDialog(this);
        };
    }

    // ── Cell switching ────────────────────────────────────────────────────────

    private void SwitchCell(string path)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        CellConfig cell;
        try   { cell = CellLoader.Load(path); }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Failed to load cell '{path}': {ex.Message}");
            return;
        }

        SceneNode? robotBaseNode = null;
        if (File.Exists(cell.Robot.ModelPath))
        {
            try
            {
                var robot = GltfLoader.Load(cell.Robot.ModelPath);
                var p     = cell.Robot.WorldPosition;
                robotBaseNode = new SceneNode
                {
                    Name           = $"{cell.Name}_Robot",
                    LocalTransform = Matrix4.CreateTranslation(p.X, p.Y, p.Z),
                    Selectable     = false,
                };
                robotBaseNode.AddChild(robot);
            }
            catch (Exception ex) { System.Console.Error.WriteLine($"Failed to load robot model: {ex.Message}"); }
        }

        SceneNode? boosterNode = null;
        if (cell.BoosterFrame is { } frame && File.Exists(frame.ModelPath))
        {
            try
            {
                var ext  = Path.GetExtension(frame.ModelPath).ToLowerInvariant();
                var node = (ext is ".glb" or ".gltf")
                    ? GltfLoader.Load(frame.ModelPath)
                    : StlLoader.Load(frame.ModelPath, $"{cell.Name}_BoosterFrame");
                var p    = frame.WorldPosition;
                if (p.X != 0f || p.Y != 0f || p.Z != 0f)
                {
                    boosterNode = new SceneNode
                    {
                        Name           = node.Name + "_Root",
                        LocalTransform = Matrix4.CreateTranslation(p.X, p.Y, p.Z),
                        Selectable     = false,
                    };
                    boosterNode.AddChild(node);
                }
                else
                {
                    node.Selectable = false;
                    boosterNode     = node;
                }
            }
            catch { /* non-critical */ }
        }

        SceneNode? bedNode = null;
        if (cell.Bed.ModelPath is { } bedPath && File.Exists(bedPath))
        {
            try
            {
                var bedExt  = Path.GetExtension(bedPath).ToLowerInvariant();
                var bed     = (bedExt is ".glb" or ".gltf")
                    ? GltfLoader.Load(bedPath)
                    : StlLoader.Load(bedPath, $"{cell.Name}_Bed");
                var o       = cell.Bed.Origin;
                var wrapper = new SceneNode
                {
                    Name           = bed.Name + "_Root",
                    LocalTransform = Matrix4.CreateTranslation(o.X, o.Y, o.Z),
                    Selectable     = false,
                };
                wrapper.AddChild(bed);
                bedNode = wrapper;
            }
            catch { /* non-critical */ }
        }

        var firstTool  = cell.EffectiveTools.Count > 0 ? cell.EffectiveTools[0] : null;
        SceneNode? toolHolder = null;
        if (firstTool is not null && File.Exists(firstTool.ModelPath))
        {
            try { toolHolder = BuildToolHolder(firstTool); }
            catch { /* non-critical */ }
        }

        vm.Viewport.PendingCellSwap.Enqueue(new CellSwapPayload(
            cell, robotBaseNode, boosterNode, bedNode, toolHolder, firstTool));
        vm.Viewport.NotifyRenderNeeded();
    }

    private async Task LoadAndAddNodeAsync(string filePath, MainWindowViewModel vm)
    {
        // TODO Phase 3: replace with proper Avalonia dialog
        bool place = true;

        SceneNode? node = ImportHelper.LoadAndPlace(filePath, place ? vm.Viewport.ActiveCell : null);
        if (node is null)
        {
            System.Console.Error.WriteLine($"Failed to load model: {filePath}");
            return;
        }
        vm.Viewport.AddUserNode(node);
    }

    private static SceneNode BuildToolHolder(ToolCellConfig tool)
    {
        bool isGlb = tool.ModelPath.EndsWith(".glb",  StringComparison.OrdinalIgnoreCase)
                  || tool.ModelPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

        if (isGlb)
        {
            var toolRoot = GltfLoader.Load(tool.ModelPath);
            var children = toolRoot.Children.ToList();
            foreach (var child in children)
                toolRoot.RemoveChild(child);

            var holder = new SceneNode
            {
                Name           = "Tool",
                LocalTransform = Matrix4.CreateRotationY(MathF.PI / 2f),
                Selectable     = false,
            };
            foreach (var child in children)
                holder.AddChild(child);
            return holder;
        }
        else
        {
            var stlNode = StlLoader.Load(tool.ModelPath, "Tool");
            var holder  = new SceneNode
            {
                Name           = "Tool",
                LocalTransform = Matrix4.CreateScale(1f / 1000f)
                               * Matrix4.CreateRotationX(-MathF.PI / 2f)
                               * Matrix4.CreateRotationY(MathF.PI / 2f),
                Selectable     = false,
            };
            holder.AddChild(stlNode);
            return holder;
        }
    }
}
