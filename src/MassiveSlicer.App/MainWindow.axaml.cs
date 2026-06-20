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
    private static readonly Vector4 BedLimeGreen = new(0.35f, 1.0f, 0.05f, 1f);
    private const float BedAluminumLimeTint = 0.45f;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // -- Right panel column toggle -----------------------------------------
        vm.Toolbar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ToolbarViewModel.IsRightPanelVisible)) return;
            bool visible = vm.Toolbar.IsRightPanelVisible;
            WorkAreaGrid.ColumnDefinitions[3].Width = visible ? new GridLength(4)   : new GridLength(0);
            WorkAreaGrid.ColumnDefinitions[4].Width = visible ? new GridLength(300)  : new GridLength(0);
        };

        // -- Console overlay toggle --------------------------------------------
        vm.Toolbar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ToolbarViewModel.IsConsoleVisible)) return;
            ConsoleOverlay.IsVisible = vm.Toolbar.IsConsoleVisible;
        };
        ConsoleOverlay.IsVisible = vm.Toolbar.IsConsoleVisible;

        // -- Cell selector -----------------------------------------------------
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

        if (cells.Count == 0)
            System.Console.Error.WriteLine("No cell files found in assets/cells/.");

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

            vm.Console.Log($"[krl] Import selected: {System.IO.Path.GetFileName(path)} (parser not yet implemented).");
        };
    }

    // -- Cell switching --------------------------------------------------------

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

        var environment = CellEnvironmentBuilder.Build(cell);

        SceneNode? bedNode = null;
        if (!cell.Bed.Hidden && cell.Bed.ModelPath is { } bedPath && File.Exists(bedPath))
        {
            try
            {
                var bedExt  = Path.GetExtension(bedPath).ToLowerInvariant();
                var bed     = (bedExt is ".glb" or ".gltf")
                    ? GltfLoader.Load(bedPath)
                    : StlLoader.Load(bedPath, $"{cell.Name}_Bed");
                var o       = cell.Bed.VisualMeshOrigin(cell.Robot.WorldPosition);
                var wrapper = new SceneNode
                {
                    Name           = bed.Name + "_Root",
                    LocalTransform = Matrix4.CreateTranslation(o.X, o.Y, o.Z),
                    Selectable     = false,
                };
                wrapper.AddChild(bed);
                ApplyBedMaterialTint(wrapper);
                bedNode = wrapper;
            }
            catch { /* non-critical */ }
        }

        // Pick the tool that matches the default active tab so the viewport shows
        // the right end-effector immediately without waiting for OnCellSwapCompleted.
        bool cellHasScan = cell.ScanToolName is not null;
        var defaultTabToolName = vm.RightPanel.ActiveTab switch
        {
            RightPanelTab.Scan when cellHasScan => cell.ScanToolName,
            RightPanelTab.Additive              => "HV Extruder",
            _                                   => cellHasScan ? cell.ScanToolName : "HV Extruder",
        };
        var firstTool = (defaultTabToolName is not null
                            ? cell.EffectiveTools.FirstOrDefault(t => t.Name == defaultTabToolName)
                            : null)
                     ?? (cell.EffectiveTools.Count > 0 ? cell.EffectiveTools[0] : null);

        SceneNode? toolHolder = null;
        if (environment.MultiTools is null && firstTool is not null)
        {
            try { toolHolder = BuildToolHolder(firstTool); }
            catch { /* non-critical */ }
        }

        SceneNode? flangeAttachment = null;
        if (cell.FlangeAttachment is { } fa)
            flangeAttachment = CellEnvironmentBuilder.BuildFlangeAttachment(fa);

        var bedCfg = cell.Bed;
        var rp     = cell.Robot.WorldPosition;
        var marker = bedCfg.BaseMarkerWorld(rp);
        var grid   = bedCfg.VisualGridCorner(rp);
        var off    = bedCfg.VisualOffset is { } vo ? $"{vo.X:F1}, {vo.Y:F1}" : "none";
        vm.Console.Log(
            $"[bed] {cell.Name}: visualOffset=({off})  BASE marker=({marker.X:F1}, {marker.Y:F1})  visual grid=({grid.X:F1}, {grid.Y:F1})");

        vm.Viewport.PendingCellSwap.Enqueue(new CellSwapPayload(
            cell, path, robotBaseNode, boosterNode, bedNode, toolHolder, firstTool,
            environment.EnvironmentNodes, environment.RotaryBedPivot, environment.MultiTools,
            flangeAttachment));
        vm.Viewport.NotifyRenderNeeded();
    }

    private async Task LoadAndAddNodeAsync(string filePath, MainWindowViewModel vm)
    {
        bool place = true;

        SceneNode? node = ImportHelper.LoadAndPlace(filePath, place ? vm.Viewport.ActiveCell : null);
        if (node is null)
        {
            System.Console.Error.WriteLine($"Failed to load model: {filePath}");
            return;
        }
        vm.Viewport.AddUserNode(node);
    }

    /// <summary>Tints only bed aluminum (e.g. GLB "Silver") with a subtle anodized lime cast; base stays white.</summary>
    private static void ApplyBedMaterialTint(SceneNode root)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;
            if (!IsBedAluminumMesh(mesh)) continue;

            n.PendingMesh = new MeshData(
                mesh.Positions, mesh.Normals, mesh.Indices, mesh.Name,
                TintToward(mesh.BaseColor, BedLimeGreen, BedAluminumLimeTint),
                metallic: 0.85f, roughness: 0.38f);
        }
    }

    private static bool IsBedAluminumMesh(MeshData mesh)
    {
        if (mesh.Name.Contains("BaseKuka", StringComparison.OrdinalIgnoreCase))
            return false;

        if (mesh.Name.Contains("Silver", StringComparison.OrdinalIgnoreCase)
         || mesh.Name.Contains("Aluminum", StringComparison.OrdinalIgnoreCase)
         || mesh.Name.Contains("Aluminium", StringComparison.OrdinalIgnoreCase))
            return true;

        // Warm white dielectric bed plate (LFAM2 BaseKuka fallback when unnamed).
        var c = mesh.BaseColor;
        if (c.X > 0.9f && c.Y > 0.9f && c.Z > 0.85f && mesh.Metallic < 0.25f)
            return false;

        return mesh.Metallic >= 0.35f;
    }

    private static Vector4 TintToward(Vector4 from, Vector4 toward, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new Vector4(
            from.X + (toward.X - from.X) * amount,
            from.Y + (toward.Y - from.Y) * amount,
            from.Z + (toward.Z - from.Z) * amount,
            from.W);
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
