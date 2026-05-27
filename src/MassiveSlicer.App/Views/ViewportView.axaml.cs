using System.Collections.Concurrent;
using System.IO;
#pragma warning disable CA1416  // Windows-only app
using Avalonia;
using OpenTK.Mathematics;
using NVec3 = System.Numerics.Vector3;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Threading;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Viewport;
using MassiveSlicer.Viewport.FK;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Rendering;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.ViewModels;
using TkMatrix4 = OpenTK.Mathematics.Matrix4;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace MassiveSlicer.App.Views;

public partial class ViewportView : UserControl
{
    private readonly SceneRenderer _renderer = new();
    private RobotFkController?     _fkController;
    private GltfNumericalIkSolver? _ikSolver;
    private SceneNode?             _currentToolNode;
    private Matrix4                _toolCorrectionMatrix = Matrix4.Identity;

    // Camera drag tracking
    private Point    _lastMousePos;
    private bool     _isOrbiting;
    private bool     _isPanning;
    private AvaBtn?  _orbitButton;
    private AvaBtn?  _panButton;

    // Selection / gizmo drag tracking
    private Point    _leftDownPos;
    private bool     _leftDragged;
    private GizmoAxis _gizmoDragAxis = GizmoAxis.None;
    private bool     _toolIsDragging;
    private Vector3  _ikDragTcpOffset;
    private (Vector3, Vector3, Vector3) _ikDragTargetRot;
    private Vector3  _gizmoDragAxisDir;
    private Vector3  _gizmoDragPlaneNormal;
    private Vector3  _gizmoDragPlanePoint;
    private Vector3  _gizmoDragStartHit;
    private Matrix4  _gizmoDragInitialLocal;
    private float    _gizmoDragStartAngle;

    // Keyboard-initiated transform state (Blender-style G/R/S)
    private bool      _kbTransformActive;
    private GizmoMode _kbTransformOp;
    private GizmoAxis _kbTransformAxis = GizmoAxis.None;
    private Point     _kbTransformStartPos;
    private Matrix4   _kbTransformInitialLocal;

    // Pointer capture
    private IPointer? _capturedPointer;

    // Cached VM reference — set on the UI thread in WireGlCanvas, read from GL thread in OnRender.
    // Avoids accessing the Avalonia DataContext property (UI-thread-only) from the GL thread.
    private ViewportViewModel? _vm;

    // Toolpath-to-node map — populated on GL thread, read on UI thread (ConcurrentDictionary is safe)
    private readonly ConcurrentDictionary<SceneNode, Toolpath> _toolpathByNode = new();

    // Cancellation for in-flight scrub-IK tasks — replaced on each scrub step so only
    // the most recent index drives the robot.
    private CancellationTokenSource? _scrubIkCts;

    // Robot cell state
    private Vector3 _robrootWorldPos;
    private Vector3 _tcpOffsetLocal;
    private float   _toolFrameRoll;
    private float   _flangeDisplayRoll;

    private Matrix3 _gltfToKukaLocal = Matrix3.Identity;
    private Matrix4 _toolMeshMatrix  = Matrix4.Identity;

    // Simple button enum — avoids dependency on WPF MouseButton.
    private enum AvaBtn { Left, Right, Middle }

    public ViewportView()
    {
        InitializeComponent();

        PointerPressed      += OnPointerPressed;
        PointerMoved        += OnPointerMoved;
        PointerReleased     += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        KeyDown             += OnKeyDown;

        Focusable = true;

        // Wire GL canvas events once the control is attached.
        AttachedToVisualTree += (_, _) => WireGlCanvas();

        // Drag & drop
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent,  OnDragEnter);
        AddHandler(DragDrop.DropEvent,      OnDrop);
    }

    // ── GL lifecycle ──────────────────────────────────────────────────────────

    private void WireGlCanvas()
    {
        GlCanvas.GlRender += OnRender;

        if (DataContext is not ViewportViewModel vm) return;
        _vm = vm;

        {
            vm.PropertyChanged   += (_, _) => GlCanvas.RequestNextFrameRendering();
            vm.PropertyChanged   += (_, pe) =>
            {
                if (pe.PropertyName == nameof(ViewportViewModel.IsLayFlatMode))
                    Cursor = vm.IsLayFlatMode ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
            };
            vm.RenderNeeded       += (_, _) => GlCanvas.RequestNextFrameRendering();
            vm.OnSliceRequested       = () => RunSliceAsync(vm);
            vm.OnFocusRequested       = FocusSelected;
            vm.OnDropToPlateRequested = DropToPlate;
            vm.OnScrubIkRequested     = ScrubIk;

            // OverlayView is declared in the XAML Grid above GlCanvas. Because
            // there is no native HWND, normal Avalonia z-order works — no
            // OverlayLayer needed. Just wire the DataContext.
            OverlayView.DataContext = vm;
        }

        if (vm.Robot is { } robot)
        {
            robot.PropertyChanged += (_, _) => GlCanvas.RequestNextFrameRendering();
            robot.OnToolSelected   = OnToolSwapRequested;
        }

        if (vm.AdditiveSettings is { } additive)
        {
            additive.PropertyChanged += (_, pe) =>
            {
                if (pe.PropertyName is nameof(AdditiveSettingsViewModel.TiltAngle)
                                    or nameof(AdditiveSettingsViewModel.TiltAngleX)
                                    or nameof(AdditiveSettingsViewModel.Method))
                    GlCanvas.RequestNextFrameRendering();
            };
        }
    }

    private void OnRender(TimeSpan delta, int w, int h)
    {
        _renderer.Initialise();

        if (_vm is { } vm)
        {
            _renderer.ShowGrid    = vm.ShowGrid;
            _renderer.ShowAxes    = vm.ShowAxes;
            _renderer.ShowBedGrid = vm.ShowBedGrid;
            _renderer.GizmoEnabled   = vm.GizmoEnabled;
            _renderer.GizmoMode      = vm.ActiveGizmoModeInternal;
            _renderer.ShaderMode     = vm.ActiveShaderMode;
            _renderer.LightAzimuth   = vm.LightAzimuth;
            _renderer.LightElevation = vm.LightElevation;
            _renderer.LightIntensity = vm.LightIntensity;

            if (_renderer.BackdropPath != vm.ActiveBackdropPath)
                _renderer.SetBackdrop(vm.ActiveBackdropPath);
            _renderer.BackdropBlur = vm.BackdropBlur;

            while (vm.PendingCellSwap.TryDequeue(out var swap))
                ApplyCellSwap(swap, vm);

            while (vm.PendingRemoveNodes.TryDequeue(out var removing))
            {
                _toolpathByNode.TryRemove(removing, out _);
                _renderer.RemoveToolpathIfExists(removing);
                foreach (var n in removing.SelfAndDescendants())
                    n.Mesh?.Dispose();
                _renderer.SceneRoot.RemoveChild(removing);
                if (_renderer.SelectedNode is not null &&
                    removing.SelfAndDescendants().Any(n => n == _renderer.SelectedNode))
                    _renderer.Select(null);
            }

            while (vm.PendingNodes.TryDequeue(out var incoming))
            {
                _renderer.SceneRoot.AddChild(incoming);
                foreach (var n in incoming.SelfAndDescendants())
                {
                    if (n.PendingMesh is null) continue;
                    n.Mesh        = new MeshRenderer(n.PendingMesh);
                    n.PendingMesh = null;
                }

                if (_fkController is null)
                    _fkController = RobotFkController.TryBuild(incoming,
                        vm.ActiveCell?.Robot.Joints ?? []);
            }

            while (vm.PendingToolNodes.TryDequeue(out var toolNode))
            {
                if (_fkController?.FlangeNode is not { } flange)
                {
                    vm.PendingToolNodes.Enqueue(toolNode);
                    continue;
                }
                _toolCorrectionMatrix   = toolNode.LocalTransform;
                RebuildFrameMatrices();
                toolNode.LocalTransform = _toolMeshMatrix * flange.WorldTransform;
                toolNode.Selectable     = true;
                _renderer.SceneRoot.AddChild(toolNode);
                UploadPendingMeshes(toolNode);
                _currentToolNode = toolNode;
            }

            while (vm.PendingToolSwap.TryDequeue(out var swap))
            {
                if (_fkController?.FlangeNode is not { } flange) continue;

                if (_currentToolNode is not null)
                {
                    foreach (var n in _currentToolNode.SelfAndDescendants())
                        n.Mesh?.Dispose();
                    _renderer.SceneRoot.RemoveChild(_currentToolNode);
                    _currentToolNode = null;
                }

                _toolCorrectionMatrix    = swap.Node.LocalTransform;
                var t = swap.Config;
                _tcpOffsetLocal  = new Vector3(t.TcpX, t.TcpY, t.TcpZ);
                _toolFrameRoll   = t.ToolFrameRoll * MathF.PI / 180f;
                RebuildFrameMatrices();
                swap.Node.LocalTransform = _toolMeshMatrix * flange.WorldTransform;
                swap.Node.Selectable     = true;
                _renderer.SceneRoot.AddChild(swap.Node);
                UploadPendingMeshes(swap.Node);
                _currentToolNode = swap.Node;

                RebuildIkSolver(vm);
            }

            while (vm.PendingToolpath.TryDequeue(out var entry))
            {
                _toolpathByNode[entry.Node] = entry.Toolpath;
                _renderer.AddToolpath(entry.Toolpath, entry.Node);
                _renderer.Select(entry.Node);
                Dispatcher.UIThread.Post(UpdateFocusOverlay);
            }

            UpdateAnglePlanePreview(vm);

            if (_fkController is not null && vm.Robot is not null)
            {
                _fkController.Apply(
                    (float)vm.Robot.A1, (float)vm.Robot.A2, (float)vm.Robot.A3,
                    (float)vm.Robot.A4, (float)vm.Robot.A5, (float)vm.Robot.A6);

                if (_currentToolNode is not null && _fkController.FlangeNode is { } flange && !_toolIsDragging)
                    _currentToolNode.LocalTransform = _toolMeshMatrix * flange.WorldTransform;
            }

            if (_fkController is not null && vm.Robot is not null)
                SyncTcpReadout(vm);
        }

        _renderer.Render(w, h);
    }

    // ── TCP readout ───────────────────────────────────────────────────────────

    private void SyncTcpReadout(ViewportViewModel vm)
    {
        if (_fkController?.FlangeNode is not { } flange) return;

        var fw  = flange.WorldTransform;
        var pos = fw.Row3.Xyz;
        float sc = fw.Row0.Xyz.Length;

        var gltfRot = new Matrix3(fw.Row0.Xyz / sc, fw.Row1.Xyz / sc, fw.Row2.Xyz / sc);
        var kukaRot = _gltfToKukaLocal * gltfRot;
        var kukaX   = kukaRot.Row0;
        var kukaY   = kukaRot.Row1;
        var kukaZ   = kukaRot.Row2;

        var tcp = pos
                + _tcpOffsetLocal.X * kukaX
                + _tcpOffsetLocal.Y * kukaY
                + _tcpOffsetLocal.Z * kukaZ;

        _renderer.TcpFrameMatrix = new Matrix4(
            kukaX.X, kukaX.Y, kukaX.Z, 0,
            kukaY.X, kukaY.Y, kukaY.Z, 0,
            kukaZ.X, kukaZ.Y, kukaZ.Z, 0,
            tcp.X,   tcp.Y,   tcp.Z,   1f);

        float cfdr  = MathF.Cos(_flangeDisplayRoll), sfdr = MathF.Sin(_flangeDisplayRoll);
        var flangeX = cfdr * kukaX - sfdr * kukaY;
        var flangeY = sfdr * kukaX + cfdr * kukaY;

        _renderer.FlangeFrameMatrix = new Matrix4(
            flangeX.X, flangeX.Y, flangeX.Z, 0,
            flangeY.X, flangeY.Y, flangeY.Z, 0,
            kukaZ.X,   kukaZ.Y,   kukaZ.Z,   0,
            pos.X,     pos.Y,     pos.Z,      1f);

        vm.Robot!.FlangeX = Math.Round(pos.X - _robrootWorldPos.X, 1);
        vm.Robot.FlangeY  = Math.Round(pos.Y - _robrootWorldPos.Y, 1);
        vm.Robot.FlangeZ  = Math.Round(pos.Z - _robrootWorldPos.Z, 1);

        vm.Robot.TcpX = Math.Round(tcp.X, 1);
        vm.Robot.TcpY = Math.Round(tcp.Y, 1);
        vm.Robot.TcpZ = Math.Round(tcp.Z, 1);

        var rot = new System.Numerics.Matrix4x4(
            kukaX.X, kukaX.Y, kukaX.Z, 0,
            kukaY.X, kukaY.Y, kukaY.Z, 0,
            kukaZ.X, kukaZ.Y, kukaZ.Z, 0,
            0, 0, 0, 1);
        var (a, b, c) = KukaIkSolver.MatrixToAbc(rot);
        vm.Robot.TcpA = Math.Round(a, 2);
        vm.Robot.TcpB = Math.Round(b, 2);
        vm.Robot.TcpC = Math.Round(c, 2);
    }

    // ── Tool helpers ──────────────────────────────────────────────────────────

    private void RebuildFrameMatrices()
    {
        float cr = MathF.Cos(_toolFrameRoll), sr = MathF.Sin(_toolFrameRoll);
        _gltfToKukaLocal = new Matrix3(
            new Vector3( cr, 0f,  sr),
            new Vector3( sr, 0f, -cr),
            new Vector3( 0f, 1f,  0f));
        _toolMeshMatrix = _toolCorrectionMatrix * Matrix4.CreateRotationY(-_flangeDisplayRoll);
    }

    private void RebuildIkSolver(ViewportViewModel vm)
    {
        if (_fkController is null) return;
        float cr = MathF.Cos(_toolFrameRoll);
        float sr = MathF.Sin(_toolFrameRoll);
        float tx = _tcpOffsetLocal.X, ty = _tcpOffsetLocal.Y, tz = _tcpOffsetLocal.Z;
        var tcpLocal = Matrix4.CreateTranslation(
            (tx * cr + ty * sr) / 1000f,
            tz / 1000f,
            (tx * sr - ty * cr) / 1000f);

        _ikSolver = new GltfNumericalIkSolver(
            _fkController.RestPoses,
            _fkController.ChainRootTransform,
            _robrootWorldPos,
            tcpLocal,
            vm.ActiveCell?.Robot.Joints ?? [],
            _toolFrameRoll);
        if (vm.Robot is not null)
            vm.Robot.IkSolver = _ikSolver;
    }

    // ── Cell swap ─────────────────────────────────────────────────────────────

    private void ApplyCellSwap(CellSwapPayload swap, ViewportViewModel vm)
    {
        foreach (var child in _renderer.SceneRoot.Children.ToList())
        {
            foreach (var n in child.SelfAndDescendants())
                n.Mesh?.Dispose();
            _renderer.SceneRoot.RemoveChild(child);
        }
        while (vm.PendingToolNodes.TryDequeue(out _)) {}
        while (vm.PendingToolSwap.TryDequeue(out _)) {}

        _fkController               = null;
        _ikSolver                   = null;
        _currentToolNode            = null;
        _renderer.TcpFrameMatrix    = null;
        _renderer.FlangeFrameMatrix = null;
        if (vm.Robot is not null) vm.Robot.IkSolver = null;

        vm.ActiveCell = swap.Config;
        var b          = swap.Config.Bed;
        var gridCorner = b.GridOrigin ?? b.Origin;
        _renderer.SetBedBoundary(new Vector3(gridCorner.X, gridCorner.Y, gridCorner.Z), b.Width, b.Depth);

        // Focus on the centre of the print area and set radius to the bed diagonal
        // so the whole bed is comfortably in view at startup.
        _renderer.Camera.Target = new Vector3(
            gridCorner.X + b.Width * 0.5f,
            gridCorner.Y + b.Depth * 0.5f,
            gridCorner.Z);
        _renderer.Camera.Radius = MathF.Sqrt(b.Width * b.Width + b.Depth * b.Depth);

        var rp = swap.Config.Robot.WorldPosition;
        _robrootWorldPos   = new Vector3(rp.X, rp.Y, rp.Z);
        _flangeDisplayRoll = swap.Config.Robot.FlangeDisplayRoll * MathF.PI / 180f;

        foreach (var node in new[] { swap.RobotBaseNode, swap.BoosterNode, swap.BedNode })
        {
            if (node is null) continue;
            _renderer.SceneRoot.AddChild(node);
            UploadPendingMeshes(node);
        }

        if (swap.RobotBaseNode is not null)
            _fkController = RobotFkController.TryBuild(swap.RobotBaseNode, swap.Config.Robot.Joints);

        if (_fkController is not null)
        {
            var h = swap.Config.Robot.HomePosition;
            if (h.Length >= 6)
                _fkController.Apply(h[0], h[1], h[2], h[3], h[4], h[5]);
        }

        if (swap.ToolHolder is not null && swap.FirstTool is { } firstTool
            && _fkController?.FlangeNode is { } flange)
        {
            _tcpOffsetLocal       = new Vector3(firstTool.TcpX, firstTool.TcpY, firstTool.TcpZ);
            _toolFrameRoll        = firstTool.ToolFrameRoll * MathF.PI / 180f;
            _toolCorrectionMatrix = swap.ToolHolder.LocalTransform;
            RebuildFrameMatrices();
            swap.ToolHolder.LocalTransform = _toolMeshMatrix * flange.WorldTransform;
            swap.ToolHolder.Selectable     = true;
            _renderer.SceneRoot.AddChild(swap.ToolHolder);
            UploadPendingMeshes(swap.ToolHolder);
            _currentToolNode = swap.ToolHolder;
        }

        RebuildFrameMatrices();
        RebuildIkSolver(vm);

        // Dispatch UI-thread updates: joint limits, home angles, tool library.
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (vm.Robot is null) return;
            vm.Robot.Configure(swap.Config.Robot.Joints, swap.Config.Robot.HomePosition);
            vm.Robot.SetBridgeConfig(swap.Config.BridgeIp, swap.Config.BridgePort);
            vm.Robot.SetToolLibrary(swap.Config.EffectiveTools);

            if (swap.FirstTool is { } tool)
            {
                var bed = swap.Config.Bed;
                vm.Robot.SetIkData(
                    new System.Numerics.Vector3(
                        bed.BaseData.X + bed.Width  / 2f,
                        bed.BaseData.Y + bed.Depth  / 2f,
                        bed.BaseData.Z),
                    new System.Numerics.Vector3(tool.TcpX, tool.TcpY, tool.TcpZ),
                    new System.Numerics.Vector3(rp.X, rp.Y, rp.Z));
            }
        });
    }

    private static void UploadPendingMeshes(SceneNode root)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is null) continue;
            n.Mesh        = new MeshRenderer(n.PendingMesh);
            n.PendingMesh = null;
        }
    }

    private void OnToolSwapRequested(ToolCellConfig config)
    {
        if (DataContext is not ViewportViewModel vm) return;
        Task.Run(() =>
        {
            try
            {
                var node = LoadToolNode(config);
                if (node is null) return;
                vm.PendingToolSwap.Enqueue((config, node));
            }
            catch { /* silently skip on load failure */ }
        });
    }

    private static SceneNode? LoadToolNode(ToolCellConfig tool)
    {
        if (!File.Exists(tool.ModelPath)) return null;

        bool isGlb = tool.ModelPath.EndsWith(".glb",  StringComparison.OrdinalIgnoreCase)
                  || tool.ModelPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

        if (isGlb)
        {
            var toolRoot = GltfLoader.Load(tool.ModelPath);
            var children = toolRoot.Children.ToList();
            foreach (var child in children) toolRoot.RemoveChild(child);
            var holder = new SceneNode
            {
                Name           = "Tool",
                LocalTransform = Matrix4.CreateRotationY(MathF.PI / 2f),
                Selectable     = false,
            };
            foreach (var child in children) holder.AddChild(child);
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

    // ── Navigation helpers ────────────────────────────────────────────────────

    private NavigationPresetId ActivePreset
        => (DataContext as ViewportViewModel)?.ActivePreset ?? NavigationPresetId.Rhino;

    private static AvaBtn? ToButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed   or PointerUpdateKind.LeftButtonReleased   => AvaBtn.Left,
        PointerUpdateKind.RightButtonPressed  or PointerUpdateKind.RightButtonReleased  => AvaBtn.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => AvaBtn.Middle,
        _ => null,
    };

    private bool IsOrbitButton(AvaBtn btn, KeyModifiers mods) => ActivePreset switch
    {
        NavigationPresetId.Rhino or
        NavigationPresetId.Plasticity => btn == AvaBtn.Right,
        NavigationPresetId.Blender    => btn == AvaBtn.Middle && !mods.HasFlag(KeyModifiers.Shift),
        NavigationPresetId.Maya       => btn == AvaBtn.Left   && mods.HasFlag(KeyModifiers.Alt),
        NavigationPresetId.Mol3D      => btn == AvaBtn.Left,
        NavigationPresetId.Max3ds     => btn == AvaBtn.Middle && mods.HasFlag(KeyModifiers.Alt),
        NavigationPresetId.Fusion360  => btn == AvaBtn.Middle && mods.HasFlag(KeyModifiers.Shift),
        _                             => btn == AvaBtn.Right,
    };

    private bool IsPanButton(AvaBtn btn, KeyModifiers mods) => ActivePreset switch
    {
        NavigationPresetId.Rhino or
        NavigationPresetId.Plasticity or
        NavigationPresetId.Mol3D      => btn == AvaBtn.Middle,
        NavigationPresetId.Blender    => btn == AvaBtn.Middle && mods.HasFlag(KeyModifiers.Shift),
        NavigationPresetId.Maya       => btn == AvaBtn.Middle && mods.HasFlag(KeyModifiers.Alt),
        NavigationPresetId.Max3ds     => btn == AvaBtn.Middle && !mods.HasFlag(KeyModifiers.Alt),
        NavigationPresetId.Fusion360  => btn == AvaBtn.Middle && !mods.HasFlag(KeyModifiers.Shift),
        _                             => btn == AvaBtn.Middle,
    };

    // ── Pointer input ─────────────────────────────────────────────────────────

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        this.Focus();
        var pt   = e.GetCurrentPoint(this);
        var pos  = pt.Position;
        var mods = e.KeyModifiers;
        var kind = pt.Properties.PointerUpdateKind;
        var btn  = ToButton(kind);
        _lastMousePos = pos;

        // Keyboard transform is active — right click cancels, everything else is suppressed
        // (left release will commit via OnPointerReleased)
        if (_kbTransformActive)
        {
            if (kind == PointerUpdateKind.RightButtonPressed)
            {
                CancelKbTransform();
                e.Handled = true;
            }
            return;
        }

        if (kind == PointerUpdateKind.LeftButtonPressed)
        {
            _leftDownPos = pos;
            _leftDragged = false;

            float mx  = (float)pos.X;
            float my  = (float)pos.Y;
            float vpW = (float)GlCanvas.Bounds.Width;
            float vpH = (float)GlCanvas.Bounds.Height;

            var axis = _renderer.GizmoEnabled
                ? _renderer.HitTestGizmo(mx, my, vpW, vpH)
                : GizmoAxis.None;
            if (axis != GizmoAxis.None)
            {
                StartGizmoDrag(axis, mx, my, vpW, vpH);
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
                return;
            }
        }

        if (btn.HasValue)
        {
            if (_orbitButton is null && IsOrbitButton(btn.Value, mods))
            {
                _isOrbiting  = true;
                _orbitButton = btn;
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
            }
            else if (_panButton is null && IsPanButton(btn.Value, mods))
            {
                _isPanning  = true;
                _panButton  = btn;
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pt    = e.GetCurrentPoint(this);
        var pos   = pt.Position;
        var delta = pos - _lastMousePos;
        _lastMousePos = pos;

        if (_kbTransformActive)
        {
            ApplyKbTransform(pos);
            return;
        }

        if (_gizmoDragAxis != GizmoAxis.None)
        {
            _leftDragged = true;
            ProcessGizmoDrag((float)pos.X, (float)pos.Y);
            if (_toolIsDragging)
                RunIkForToolDrag();
            GlCanvas.RequestNextFrameRendering();
            return;
        }

        if (pt.Properties.IsLeftButtonPressed)
        {
            var offset = pos - _leftDownPos;
            if (Math.Abs(offset.X) > 3 || Math.Abs(offset.Y) > 3)
                _leftDragged = true;
        }

        bool changed = false;

        if (_isOrbiting)
        {
            _renderer.Camera.Orbit(
                deltaAzimuth:   -(float)delta.X * 0.4f,
                deltaElevation:  (float)delta.Y * 0.4f);
            changed = true;
        }
        else if (_isPanning)
        {
            _renderer.Camera.Pan(
                deltaX:         (float)delta.X,
                deltaY:         (float)delta.Y,
                viewportWidth:  (float)GlCanvas.Bounds.Width,
                viewportHeight: (float)GlCanvas.Bounds.Height);
            changed = true;
        }

        if (changed) GlCanvas.RequestNextFrameRendering();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pt   = e.GetCurrentPoint(this);
        var kind = pt.Properties.PointerUpdateKind;
        var btn  = ToButton(kind);

        if (kind == PointerUpdateKind.LeftButtonReleased && _kbTransformActive)
        {
            CommitKbTransform();
            _leftDragged = false;
            return;
        }

        if (kind == PointerUpdateKind.LeftButtonReleased)
        {
            if (_gizmoDragAxis != GizmoAxis.None)
            {
                _toolIsDragging          = false;
                _gizmoDragAxis           = GizmoAxis.None;
                _renderer.ActiveDragAxis = GizmoAxis.None;
                _capturedPointer?.Capture(null);
                _capturedPointer = null;
                GlCanvas.RequestNextFrameRendering();
            }
            else if (!_leftDragged)
            {
                float vpW = (float)GlCanvas.Bounds.Width;
                float vpH = (float)GlCanvas.Bounds.Height;
                var ray   = _renderer.Camera.GetPickRay(
                    (float)_leftDownPos.X, (float)_leftDownPos.Y, vpW, vpH);

                if (DataContext is ViewportViewModel flatVm && flatVm.IsLayFlatMode)
                {
                    var (node, normal) = _renderer.PickFace(ray);
                    if (node is not null)
                    {
                        ApplyLayFlat(node, normal, _renderer.BedZ);
                        _renderer.Select(node);
                        UpdateFocusOverlay();
                    }
                    flatVm.IsLayFlatMode = false;
                }
                else
                {
                    float vpW2 = (float)GlCanvas.Bounds.Width;
                    float vpH2 = (float)GlCanvas.Bounds.Height;
                    var picked = _renderer.Pick(ray)
                        ?? _renderer.PickToolpath((float)_leftDownPos.X, (float)_leftDownPos.Y, vpW2, vpH2);
                    _renderer.Select(picked);
                    UpdateFocusOverlay();
                }

                GlCanvas.RequestNextFrameRendering();
            }
            _leftDragged = false;
            return;
        }

        if (btn == _orbitButton) { _isOrbiting = false; _orbitButton = null; }
        if (btn == _panButton)   { _isPanning  = false; _panButton   = null; }

        if (!_isOrbiting && !_isPanning)
        {
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // Avalonia Delta.Y is in lines (typically ±3 per notch).
        // Normalise to ±1 per notch to match the WPF behaviour.
        _renderer.Camera.Zoom((float)e.Delta.Y / 3f);
        GlCanvas.RequestNextFrameRendering();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_kbTransformActive)
        {
            switch (e.Key)
            {
                case Key.X:      SetKbTransformAxis(GizmoAxis.X); e.Handled = true; return;
                case Key.Y:      SetKbTransformAxis(GizmoAxis.Y); e.Handled = true; return;
                case Key.Z:      SetKbTransformAxis(GizmoAxis.Z); e.Handled = true; return;
                case Key.Return: CommitKbTransform();              e.Handled = true; return;
                case Key.Escape: CancelKbTransform();              e.Handled = true; return;
            }
        }

        switch (e.Key)
        {
            case Key.F:      FocusSelected();                          e.Handled = true; break;
            case Key.G:
                if (_renderer.GizmoEnabled) SetGizmoMode(GizmoMode.Translate);
                else StartKbTransform(GizmoMode.Translate);
                e.Handled = true; break;
            case Key.R:
                if (_renderer.GizmoEnabled) SetGizmoMode(GizmoMode.Rotate);
                else StartKbTransform(GizmoMode.Rotate);
                e.Handled = true; break;
            case Key.S:
                if (_renderer.GizmoEnabled) SetGizmoMode(GizmoMode.Scale);
                else StartKbTransform(GizmoMode.Scale);
                e.Handled = true; break;
            case Key.Delete: DeleteSelectedNode();                     e.Handled = true; break;
            case Key.Escape:
                if (DataContext is ViewportViewModel escVm && escVm.IsLayFlatMode)
                {
                    escVm.IsLayFlatMode = false;
                    e.Handled = true;
                }
                break;
        }
    }

    private void DeleteSelectedNode()
    {
        if (_renderer.SelectedNode is not { } node) return;
        if (DataContext is not ViewportViewModel vm) return;
        _renderer.Select(null);
        UpdateFocusOverlay();
        vm.RequestDeleteNode(node);
        GlCanvas.RequestNextFrameRendering();
    }

    // ── Drag and drop ─────────────────────────────────────────────────────────

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        if (DataContext is not ViewportViewModel vm) return;

        var items = e.DataTransfer.TryGetFiles();
        if (items is null) return;

        var files = items
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null && ImportHelper.IsSupported(p))
            .Cast<string>()
            .ToList();

        if (files.Count == 0) return;

        // TODO Phase 3: replace with Avalonia dialog asking "Center on bed?"
        bool place = true;

        foreach (var file in files)
        {
            var node = ImportHelper.LoadAndPlace(file, place ? vm.ActiveCell : null);
            if (node is not null) vm.AddUserNode(node);
        }
    }

    // ── Slice ─────────────────────────────────────────────────────────────────

    private async Task RunSliceAsync(ViewportViewModel vm)
    {
        if (vm.IsSlicing || vm.OutlinerItems.Count == 0) return;
        vm.IsSlicing = true;

        try
        {
            // Snapshot mesh data on the UI thread (OutlinerItems is UI-thread-owned).
            // Only process the outliner item that owns the currently selected node.
            var selectedNode  = _renderer.SelectedNode;
            var meshSnapshots = new List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)>();
            var sourceItems   = new List<OutlinerItemViewModel>();
            foreach (var item in vm.OutlinerItems)
            {
                if (!item.Visible) continue;
                if (selectedNode is null || !item.Node.SelfAndDescendants().Any(n => n == selectedNode)) continue;
                bool contributed = false;
                foreach (var node in item.Node.SelfAndDescendants())
                {
                    if (node.Mesh?.PickingData is not { } md) continue;
                    meshSnapshots.Add((md.Positions, md.Indices, node.WorldTransform));
                    contributed = true;
                }
                if (contributed) sourceItems.Add(item);
            }

            var method = vm.AdditiveSettings?.Method ?? SliceMethod.Planar;
            var settings = vm.AdditiveSettings is { } s
                ? new SliceSettings
                {
                    LayerHeight      = (float)s.LayerHeight,
                    FirstLayerHeight = (float)s.FirstLayerHeight,
                    BeadWidth        = (float)s.BeadWidth,
                    FeedRate         = (float)s.FeedRate,
                    PtpSpeed         = (float)s.PtpSpeed,
                    ApproachZ        = (float)s.ApproachZ,
                    TiltAngle        = (float)s.TiltAngle,
                    TiltAngleX       = (float)s.TiltAngleX,
                }
                : new SliceSettings();

            var toolpath = await Task.Run(() =>
            {
                // Expand indexed meshes to flat triangles and convert to System.Numerics.
                var flatMeshes = new List<NVec3[]>(meshSnapshots.Count);
                foreach (var (positions, indices, world) in meshSnapshots)
                {
                    NVec3[] flat;
                    if (indices is null)
                    {
                        flat = new NVec3[positions.Length];
                        for (int i = 0; i < positions.Length; i++)
                            flat[i] = TransformPoint(positions[i], world);
                    }
                    else
                    {
                        flat = new NVec3[indices.Length];
                        for (int i = 0; i < indices.Length; i++)
                            flat[i] = TransformPoint(positions[indices[i]], world);
                    }
                    flatMeshes.Add(flat);
                }

                return method == SliceMethod.Angled
                    ? AngledPlanarSlicer.Slice(flatMeshes, settings)
                    : PlanarSlicer.Slice(flatMeshes, settings);
            });

            var parentItem   = sourceItems.Count == 1 ? sourceItems[0] : null;
            var toolpathName = method == SliceMethod.Angled
                ? $"Toolpath {settings.TiltAngle:0.##}° W{settings.BeadWidth:0.##}mm H{settings.LayerHeight:0.##}mm"
                : $"Toolpath W{settings.BeadWidth:0.##}mm H{settings.LayerHeight:0.##}mm";
            var toolpathNode = new SceneNode { Name = toolpathName, Selectable = true };
            vm.RegisterToolpathInOutliner(toolpathNode, parentItem);
            vm.PendingToolpath.Enqueue((toolpath, toolpathNode));

            // Hide source meshes so the toolpath is unobstructed, then drop selection.
            foreach (var item in sourceItems)
                item.Visible = false;

            _renderer.Select(null);
            UpdateFocusOverlay();

            GlCanvas.RequestNextFrameRendering();
        }
        finally
        {
            vm.IsSlicing = false;
        }
    }

    private static NVec3 TransformPoint(TkVector3 p, TkMatrix4 m)
    {
        // OpenTK row-vector: world = local * M
        float x = p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41;
        float y = p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42;
        float z = p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43;
        return new NVec3(x, y, z);
    }

    // ── Lay Flat / Drop to Plate ──────────────────────────────────────────────

    private void DropToPlate()
    {
        if (_renderer.SelectedNode is not { } node) return;
        float minZ = LayFlatMinZ(node);
        if (minZ >= float.MaxValue) return;
        node.LocalTransform = node.LocalTransform
            * TkMatrix4.CreateTranslation(0f, 0f, _renderer.BedZ - minZ);
        GlCanvas.RequestNextFrameRendering();
    }

    private static void ApplyLayFlat(SceneNode node, TkVector3 worldFaceNormal, float bedZ)
    {
        if (worldFaceNormal.LengthSquared < 1e-12f) return;

        var from = TkVector3.Normalize(worldFaceNormal);
        var to   = new TkVector3(0f, 0f, -1f); // face must point into the bed

        TkMatrix4 rot;
        var   axis     = TkVector3.Cross(from, to);
        float sinAngle = axis.Length;
        float cosAngle = TkVector3.Dot(from, to);

        const float Eps = 1e-6f;
        if (sinAngle < Eps)
        {
            if (cosAngle > 0f)
            {
                rot = TkMatrix4.Identity; // already pointing down
            }
            else
            {
                // 180° — flip around any axis perpendicular to the face normal.
                var perp = MathF.Abs(from.X) < 0.9f ? TkVector3.UnitX : TkVector3.UnitY;
                perp = TkVector3.Normalize(TkVector3.Cross(from, perp));
                rot  = TkMatrix4.CreateFromAxisAngle(perp, MathF.PI);
            }
        }
        else
        {
            rot = TkMatrix4.CreateFromAxisAngle(axis / sinAngle, MathF.Atan2(sinAngle, cosAngle));
        }

        // Rotate around the world-space bounding-box centre so the object doesn't drift.
        var center = LayFlatWorldCenter(node);
        // Row-vector: p_new = p_old * M  →  W_new = W_old * M
        var M = TkMatrix4.CreateTranslation(-center) * rot * TkMatrix4.CreateTranslation(center);
        node.LocalTransform = node.LocalTransform * M;

        // Drop the object so its lowest point sits exactly on the bed surface.
        float minZ = LayFlatMinZ(node);
        if (minZ < float.MaxValue)
            node.LocalTransform = node.LocalTransform * TkMatrix4.CreateTranslation(0f, 0f, bedZ - minZ);
    }

    private static TkVector3 LayFlatWorldCenter(SceneNode node)
    {
        var mesh = node.Mesh?.PickingData;
        if (mesh is null) return node.WorldTransform.Row3.Xyz;
        var lo = mesh.LocalBounds.Min;
        var hi = mesh.LocalBounds.Max;
        var lc = new TkVector3((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f, (lo.Z + hi.Z) * 0.5f);
        var m  = node.WorldTransform;
        return new TkVector3(
            lc.X * m.M11 + lc.Y * m.M21 + lc.Z * m.M31 + m.M41,
            lc.X * m.M12 + lc.Y * m.M22 + lc.Z * m.M32 + m.M42,
            lc.X * m.M13 + lc.Y * m.M23 + lc.Z * m.M33 + m.M43);
    }

    private static float LayFlatMinZ(SceneNode node)
    {
        float minZ = float.MaxValue;
        foreach (var n in node.SelfAndDescendants())
        {
            var mesh = n.Mesh?.PickingData;
            if (mesh is null) continue;
            var m = n.WorldTransform;
            foreach (var p in mesh.Positions)
            {
                float z = p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43;
                if (z < minZ) minZ = z;
            }
        }
        return minZ;
    }

    // ── Gizmo mode switching ──────────────────────────────────────────────────

    private void SetGizmoMode(GizmoMode mode)
    {
        _renderer.GizmoMode = mode;
        if (DataContext is ViewportViewModel vm)
            vm.ActiveGizmoModeInternal = mode;
        GlCanvas.RequestNextFrameRendering();
    }

    // ── Keyboard-initiated transform (Blender-style G/R/S + X/Y/Z) ───────────

    private void StartKbTransform(GizmoMode op)
    {
        SetGizmoMode(op);
        if (_renderer.SelectedNode is not { } node) return;

        _kbTransformActive       = true;
        _kbTransformOp           = op;
        _kbTransformAxis         = GizmoAxis.None;
        _kbTransformStartPos     = _lastMousePos;
        _kbTransformInitialLocal = node.LocalTransform;

        _toolIsDragging = (node == _currentToolNode);
        if (_toolIsDragging && _ikSolver is not null && _renderer.TcpFrameMatrix is { } tcpMat)
        {
            _ikDragTcpOffset = tcpMat.Row3.Xyz - node.WorldTransform.Row3.Xyz;
            if (DataContext is ViewportViewModel { Robot: { } robot })
                _ikDragTargetRot = _ikSolver.TargetRotFromKukaAbc(
                    (float)robot.TcpA, (float)robot.TcpB, (float)robot.TcpC);
        }

        // Prime the view-plane state so unconstrained translate tracks exactly from the start.
        if (op == GizmoMode.Translate)
            SetupKbViewPlane(node);
    }

    // Stores the camera view-plane (normal + anchor + start-hit) for unconstrained translate.
    private void SetupKbViewPlane(SceneNode node)
    {
        float vpW = (float)GlCanvas.Bounds.Width;
        float vpH = (float)GlCanvas.Bounds.Height;

        _gizmoDragPlaneNormal  = Vector3.Normalize(_renderer.Camera.Target - _renderer.Camera.Eye);
        _gizmoDragPlanePoint   = node.WorldTransform.Row3.Xyz;
        _gizmoDragInitialLocal = node.LocalTransform;

        var startRay = _renderer.Camera.GetPickRay(
            (float)_kbTransformStartPos.X, (float)_kbTransformStartPos.Y, vpW, vpH);
        float denom = Vector3.Dot(startRay.Direction, _gizmoDragPlaneNormal);
        _gizmoDragStartHit = MathF.Abs(denom) > 1e-5f
            ? startRay.At(Vector3.Dot(_gizmoDragPlanePoint - startRay.Origin, _gizmoDragPlaneNormal) / denom)
            : _gizmoDragPlanePoint;
    }

    private void SetKbTransformAxis(GizmoAxis axis)
    {
        if (!_kbTransformActive || _renderer.SelectedNode is not { } node) return;

        // Reset node so WorldTransform reflects the initial position before re-setup.
        node.LocalTransform      = _kbTransformInitialLocal;
        _kbTransformAxis         = axis;
        _renderer.ActiveDragAxis = axis;

        if (axis != GizmoAxis.None && _kbTransformOp == GizmoMode.Translate)
        {
            // Re-use the gizmo drag plane-intersection setup anchored at the keyboard start pos.
            // StartGizmoDrag reads node.LocalTransform (= _kbTransformInitialLocal) for _gizmoDragInitialLocal.
            float vpW = (float)GlCanvas.Bounds.Width;
            float vpH = (float)GlCanvas.Bounds.Height;
            StartGizmoDrag(axis,
                (float)_kbTransformStartPos.X, (float)_kbTransformStartPos.Y,
                vpW, vpH);
        }

        ApplyKbTransform(_lastMousePos);
    }

    private void CommitKbTransform()
    {
        _kbTransformActive       = false;
        _kbTransformAxis         = GizmoAxis.None;
        _gizmoDragAxis           = GizmoAxis.None;
        _renderer.ActiveDragAxis = GizmoAxis.None;
        _toolIsDragging          = false;
        GlCanvas.RequestNextFrameRendering();
    }

    private void CancelKbTransform()
    {
        if (_renderer.SelectedNode is { } node)
            node.LocalTransform  = _kbTransformInitialLocal;
        _kbTransformActive       = false;
        _kbTransformAxis         = GizmoAxis.None;
        _gizmoDragAxis           = GizmoAxis.None;
        _renderer.ActiveDragAxis = GizmoAxis.None;
        _toolIsDragging          = false;
        GlCanvas.RequestNextFrameRendering();
    }

    private void ApplyKbTransform(Point mousePos)
    {
        if (!_kbTransformActive || _renderer.SelectedNode is not { } node) return;

        float mx  = (float)mousePos.X;
        float my  = (float)mousePos.Y;
        float vpW = (float)GlCanvas.Bounds.Width;
        float vpH = (float)GlCanvas.Bounds.Height;
        float dx  = (float)(mousePos.X - _kbTransformStartPos.X);

        switch (_kbTransformOp)
        {
            case GizmoMode.Translate:
                if (_kbTransformAxis != GizmoAxis.None)
                    // Axis-constrained: plane-intersection via existing gizmo drag logic —
                    // _gizmoDragInitialLocal was captured by StartGizmoDrag at SetKbTransformAxis time.
                    ProcessGizmoDrag(mx, my);
                else
                    KbTranslateViewPlane(node, mx, my, vpW, vpH);
                break;

            case GizmoMode.Rotate:
                // dx-based is stable for all camera angles; no plane-intersection singularity.
                KbRotate(node, dx);
                break;

            case GizmoMode.Scale:
                KbScale(node, dx, vpW);
                break;
        }

        if (_toolIsDragging)
            RunIkForToolDrag();

        GlCanvas.RequestNextFrameRendering();
    }

    // Unconstrained translate: follows the mouse exactly in the camera view plane.
    private void KbTranslateViewPlane(SceneNode node, float mx, float my, float vpW, float vpH)
    {
        var ray = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
        float denom = Vector3.Dot(ray.Direction, _gizmoDragPlaneNormal);
        if (MathF.Abs(denom) < 1e-5f) return;
        float t = Vector3.Dot(_gizmoDragPlanePoint - ray.Origin, _gizmoDragPlaneNormal) / denom;
        var hitWorld = ray.At(t);

        var worldDelta  = hitWorld - _gizmoDragStartHit;
        var parentWorld = node.Parent?.WorldTransform ?? Matrix4.Identity;
        Matrix4.Invert(parentWorld, out var invParent);
        var localDelta = TransformDir(worldDelta, invParent);

        var lt = _kbTransformInitialLocal;
        lt.M41 += localDelta.X;
        lt.M42 += localDelta.Y;
        lt.M43 += localDelta.Z;
        node.LocalTransform = lt;
    }

    private void KbRotate(SceneNode node, float dx)
    {
        var axisDir = _kbTransformAxis switch
        {
            GizmoAxis.X => Vector3.UnitX,
            GizmoAxis.Y => Vector3.UnitY,
            GizmoAxis.Z => Vector3.UnitZ,
            _           => Vector3.Normalize(_renderer.Camera.Target - _renderer.Camera.Eye),
        };

        float angle = dx * 0.01f;
        var   rot   = Matrix4.CreateFromAxisAngle(axisDir, angle);

        var lt = _kbTransformInitialLocal;
        var p  = new Vector3(lt.M41, lt.M42, lt.M43);
        lt     = lt * rot;
        lt.M41 = p.X; lt.M42 = p.Y; lt.M43 = p.Z;
        node.LocalTransform = lt;
    }

    private void KbScale(SceneNode node, float dx, float vpW)
    {
        float t     = dx / (vpW * 0.5f);
        float ratio = MathF.Exp(t * MathF.Log(3f));
        if (ratio <= 0f) return;

        var lt = _kbTransformInitialLocal;
        switch (_kbTransformAxis)
        {
            case GizmoAxis.X:
                lt.M11 *= ratio; lt.M12 *= ratio; lt.M13 *= ratio;
                break;
            case GizmoAxis.Y:
                lt.M21 *= ratio; lt.M22 *= ratio; lt.M23 *= ratio;
                break;
            case GizmoAxis.Z:
                lt.M31 *= ratio; lt.M32 *= ratio; lt.M33 *= ratio;
                break;
            default:
                lt.M11 *= ratio; lt.M12 *= ratio; lt.M13 *= ratio;
                lt.M21 *= ratio; lt.M22 *= ratio; lt.M23 *= ratio;
                lt.M31 *= ratio; lt.M32 *= ratio; lt.M33 *= ratio;
                break;
        }
        node.LocalTransform = lt;
    }

    private void UpdateFocusOverlay()
    {
        if (DataContext is not ViewportViewModel vm) return;

        var selected = _renderer.SelectedNode;
        vm.HasSelection       = selected is not null;
        bool isToolpath       = selected is not null && _renderer.IsToolpathNode(selected);
        vm.IsToolpathSelected = isToolpath;

        // Use ResetScrubIndex (not the public setters) so the IK callback is NOT triggered
        // by the programmatic reset — the robot only follows scrubbing the user initiates.
        if (isToolpath && selected is not null && _toolpathByNode.TryGetValue(selected, out var tp))
            vm.ResetScrubIndex(tp.Layers.Sum(l => l.Moves.Count), tp);
        else
            vm.ResetScrubIndex(0, null);
    }

    // ── Scrub IK ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs orientation-constrained IK for the toolpath move at <paramref name="index"/>
    /// and drives the robot joints to the result. The tool orientation is derived from
    /// the slicing-plane normal stored on the layer, so angled paths hold the correct tilt.
    /// Any in-flight solve for a stale index is cancelled before the new one starts,
    /// so only the last-requested position ever drives the robot.
    /// </summary>
    private void ScrubIk(int index)
    {
        var vm       = _vm;
        var toolpath = vm?.ActiveScrubToolpath;
        var solver   = _ikSolver;
        var robot    = vm?.Robot;

        if (vm is null || toolpath is null || solver is null || robot is null) return;

        // Desync live feed so IK drives the robot instead of the C3Bridge stream.
        robot.Desync();

        var info = GetScrubInfo(toolpath, index);
        if (info is null) return;
        var (pos, planeNormal) = info.Value;

        // Toolpath positions are in scene/world space; IK expects ROBROOT frame.
        var robrootPos    = _robrootWorldPos;
        var targetRobroot = new TkVector3(pos.X - robrootPos.X,
                                          pos.Y - robrootPos.Y,
                                          pos.Z - robrootPos.Z);

        // Derive orientation target from the layer's slicing-plane normal.
        // The tool approaches along -planeNormal (e.g. straight down for horizontal layers).
        var targetRot = solver.TargetRotFromPlaneNormal(
            new TkVector3(planeNormal.X, planeNormal.Y, planeNormal.Z));

        // Seed from current joint angles (snapshot on UI thread, safe to read).
        var seed = new float[]
        {
            (float)robot.A1, (float)robot.A2, (float)robot.A3,
            (float)robot.A4, (float)robot.A5, (float)robot.A6,
        };

        // Cancel any still-running solve for a previous index.
        _scrubIkCts?.Cancel();
        _scrubIkCts = new CancellationTokenSource();
        var cts = _scrubIkCts;

        Task.Run(() =>
        {
            if (cts.IsCancellationRequested) return;
            var result = solver.Solve(targetRobroot, seed, targetRot);
            if (result is null || cts.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                robot.A1 = Math.Round(result[0], 2);
                robot.A2 = Math.Round(result[1], 2);
                robot.A3 = Math.Round(result[2], 2);
                robot.A4 = Math.Round(result[3], 2);
                robot.A5 = Math.Round(result[4], 2);
                robot.A6 = Math.Round(result[5], 2);
                GlCanvas.RequestNextFrameRendering();
            });
        }, cts.Token);
    }

    /// <summary>
    /// Maps a flat scrub <paramref name="index"/> to the TCP position and slicing-plane
    /// normal for that move in the toolpath.
    /// <list type="bullet">
    ///   <item>Index 0  → first move's <c>From</c> and its layer's <c>PlaneNormal</c>.</item>
    ///   <item>Index N  → N-th move's <c>To</c> and its layer's <c>PlaneNormal</c>.</item>
    ///   <item>Index &gt; total → last move's <c>To</c> / normal (slider at maximum).</item>
    /// </list>
    /// </summary>
    private static (NVec3 pos, NVec3 normal)? GetScrubInfo(Toolpath tp, int index)
    {
        NVec3 lastPos    = default;
        NVec3 lastNormal = NVec3.UnitZ;
        bool  hasAny     = false;

        foreach (var layer in tp.Layers)
        {
            var n = layer.PlaneNormal;  // System.Numerics.Vector3 — no conversion needed
            foreach (var move in layer.Moves)
            {
                if (index == 0) return (move.From, n);
                lastPos    = move.To;
                lastNormal = n;
                hasAny     = true;
                if (--index == 0) return (move.To, n);
            }
        }

        return hasAny ? (lastPos, lastNormal) : null;
    }

    private void UpdateAnglePlanePreview(ViewportViewModel vm)
    {
        var node = _renderer.SelectedNode;
        if (node is null
            || vm.AdditiveSettings?.Method != SliceMethod.Angled
            || _renderer.IsToolpathNode(node))
        {
            _renderer.SetPlanePreview(null, null);
            return;
        }

        // World-space AABB of the selected node.
        var min = new TkVector3(float.MaxValue);
        var max = new TkVector3(float.MinValue);
        Span<TkVector3> corners = stackalloc TkVector3[8];
        bool hasGeometry = false;
        foreach (var n in node.SelfAndDescendants())
        {
            if (n.Mesh?.PickingData is not { } mesh) continue;
            var world = n.WorldTransform;
            var (bMin, bMax) = mesh.LocalBounds;
            corners[0] = new(bMin.X, bMin.Y, bMin.Z); corners[1] = new(bMax.X, bMin.Y, bMin.Z);
            corners[2] = new(bMin.X, bMax.Y, bMin.Z); corners[3] = new(bMax.X, bMax.Y, bMin.Z);
            corners[4] = new(bMin.X, bMin.Y, bMax.Z); corners[5] = new(bMax.X, bMin.Y, bMax.Z);
            corners[6] = new(bMin.X, bMax.Y, bMax.Z); corners[7] = new(bMax.X, bMax.Y, bMax.Z);
            foreach (var p in corners)
            {
                var w = new TkVector3(
                    p.X * world.M11 + p.Y * world.M21 + p.Z * world.M31 + world.M41,
                    p.X * world.M12 + p.Y * world.M22 + p.Z * world.M32 + world.M42,
                    p.X * world.M13 + p.Y * world.M23 + p.Z * world.M33 + world.M43);
                min = TkVector3.ComponentMin(min, w);
                max = TkVector3.ComponentMax(max, w);
            }
            hasGeometry = true;
        }

        if (!hasGeometry) { _renderer.SetPlanePreview(null, null); return; }

        var center = (min + max) * 0.5f;
        float size = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z)) * 1.3f;

        float ty = (float)(vm.AdditiveSettings.TiltAngle  * Math.PI / 180.0);
        float tx = (float)(vm.AdditiveSettings.TiltAngleX * Math.PI / 180.0);
        var normal = new TkVector3(
            MathF.Sin(ty),
            -MathF.Sin(tx) * MathF.Cos(ty),
             MathF.Cos(tx) * MathF.Cos(ty));

        _renderer.SetPlanePreview(center, normal, size);
    }

    private void FocusSelected()
    {
        if (_renderer.SelectedNode is not { } node) return;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool hasGeometry = false;

        Span<Vector3> corners = stackalloc Vector3[8];
        foreach (var n in node.SelfAndDescendants())
        {
            if (n.Mesh?.PickingData is not { } mesh) continue;
            var world = n.WorldTransform;
            var (bMin, bMax) = mesh.LocalBounds;

            corners[0] = new(bMin.X, bMin.Y, bMin.Z); corners[1] = new(bMax.X, bMin.Y, bMin.Z);
            corners[2] = new(bMin.X, bMax.Y, bMin.Z); corners[3] = new(bMax.X, bMax.Y, bMin.Z);
            corners[4] = new(bMin.X, bMin.Y, bMax.Z); corners[5] = new(bMax.X, bMin.Y, bMax.Z);
            corners[6] = new(bMin.X, bMax.Y, bMax.Z); corners[7] = new(bMax.X, bMax.Y, bMax.Z);

            foreach (var p in corners)
            {
                var w = new Vector3(
                    p.X * world.M11 + p.Y * world.M21 + p.Z * world.M31 + world.M41,
                    p.X * world.M12 + p.Y * world.M22 + p.Z * world.M32 + world.M42,
                    p.X * world.M13 + p.Y * world.M23 + p.Z * world.M33 + world.M43);
                min = Vector3.ComponentMin(min, w);
                max = Vector3.ComponentMax(max, w);
            }
            hasGeometry = true;
        }

        if (!hasGeometry)
        {
            _renderer.Camera.Target = node.WorldTransform.Row3.Xyz;
        }
        else
        {
            var center = (min + max) * 0.5f;
            float radius = (max - min).Length * 0.75f;
            _renderer.Camera.Target = center;
            _renderer.Camera.Radius = Math.Max(radius, 50f);
        }

        GlCanvas.RequestNextFrameRendering();
    }

    private void RunIkForToolDrag()
    {
        if (_ikSolver is null || _currentToolNode is null) return;
        if (DataContext is not ViewportViewModel { Robot: { } robot }) return;

        var targetScene   = _currentToolNode.LocalTransform.Row3.Xyz + _ikDragTcpOffset;
        var targetRobroot = targetScene - _robrootWorldPos;

        var seed   = new[] { (float)robot.A1, (float)robot.A2, (float)robot.A3,
                             (float)robot.A4, (float)robot.A5, (float)robot.A6 };
        var result = _ikSolver.Solve(targetRobroot, seed, _ikDragTargetRot);
        if (result is null) return;

        robot.A1 = Math.Round(result[0], 2);
        robot.A2 = Math.Round(result[1], 2);
        robot.A3 = Math.Round(result[2], 2);
        robot.A4 = Math.Round(result[3], 2);
        robot.A5 = Math.Round(result[4], 2);
        robot.A6 = Math.Round(result[5], 2);
    }

    // ── Gizmo drag ────────────────────────────────────────────────────────────

    private void StartGizmoDrag(GizmoAxis axis, float mx, float my, float vpW, float vpH)
    {
        _gizmoDragAxis           = axis;
        _renderer.ActiveDragAxis = axis;
        _gizmoDragAxisDir = axis switch
        {
            GizmoAxis.X => new Vector3(1f, 0f, 0f),
            GizmoAxis.Y => new Vector3(0f, 1f, 0f),
            _           => new Vector3(0f, 0f, 1f),
        };

        if (_renderer.SelectedNode is not { } node) return;
        _gizmoDragInitialLocal = node.LocalTransform;
        _gizmoDragPlanePoint   = node.WorldTransform.Row3.Xyz;
        _toolIsDragging = (node == _currentToolNode);
        if (_toolIsDragging && _ikSolver is not null && _renderer.TcpFrameMatrix is { } tcpMat)
        {
            _ikDragTcpOffset = tcpMat.Row3.Xyz - node.WorldTransform.Row3.Xyz;
            if (DataContext is ViewportViewModel { Robot: { } robot })
                _ikDragTargetRot = _ikSolver.TargetRotFromKukaAbc(
                    (float)robot.TcpA, (float)robot.TcpB, (float)robot.TcpC);
        }

        switch (_renderer.GizmoMode)
        {
            case GizmoMode.Translate:
            case GizmoMode.Scale:
            {
                var camFwd = Vector3.Normalize(_renderer.Camera.Target - _renderer.Camera.Eye);
                var n      = camFwd - Vector3.Dot(camFwd, _gizmoDragAxisDir) * _gizmoDragAxisDir;
                _gizmoDragPlaneNormal = n.LengthSquared > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitZ;

                var startRay = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
                float denom  = Vector3.Dot(startRay.Direction, _gizmoDragPlaneNormal);
                _gizmoDragStartHit = MathF.Abs(denom) > 1e-5f
                    ? startRay.At(Vector3.Dot(_gizmoDragPlanePoint - startRay.Origin, _gizmoDragPlaneNormal) / denom)
                    : _gizmoDragPlanePoint;
                break;
            }
            case GizmoMode.Rotate:
            {
                _gizmoDragPlaneNormal = _gizmoDragAxisDir;

                var startRay = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
                float denom  = Vector3.Dot(startRay.Direction, _gizmoDragPlaneNormal);
                _gizmoDragStartHit = MathF.Abs(denom) > 1e-5f
                    ? startRay.At(Vector3.Dot(_gizmoDragPlanePoint - startRay.Origin, _gizmoDragPlaneNormal) / denom)
                    : _gizmoDragPlanePoint;

                var rel = _gizmoDragStartHit - _gizmoDragPlanePoint;
                _gizmoDragStartAngle = AxisAngle(axis, rel);
                break;
            }
        }
    }

    private void ProcessGizmoDrag(float mx, float my)
    {
        if (_renderer.SelectedNode is not { } node) return;

        float vpW = (float)GlCanvas.Bounds.Width;
        float vpH = (float)GlCanvas.Bounds.Height;
        var ray   = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);

        float denom = Vector3.Dot(ray.Direction, _gizmoDragPlaneNormal);
        if (MathF.Abs(denom) < 1e-5f) return;
        float t      = Vector3.Dot(_gizmoDragPlanePoint - ray.Origin, _gizmoDragPlaneNormal) / denom;
        var hitWorld = ray.At(t);

        switch (_renderer.GizmoMode)
        {
            case GizmoMode.Translate: ProcessTranslateDrag(node, hitWorld); break;
            case GizmoMode.Scale:     ProcessScaleDrag(node, hitWorld);     break;
            case GizmoMode.Rotate:    ProcessRotateDrag(node, hitWorld);    break;
        }
    }

    private void ProcessTranslateDrag(SceneNode node, Vector3 hitWorld)
    {
        float proj     = Vector3.Dot(hitWorld - _gizmoDragStartHit, _gizmoDragAxisDir);
        var worldDelta = _gizmoDragAxisDir * proj;

        var parentWorld = node.Parent?.WorldTransform ?? Matrix4.Identity;
        Matrix4.Invert(parentWorld, out var invParent);
        var localDelta = TransformDir(worldDelta, invParent);

        var lt = _gizmoDragInitialLocal;
        lt.M41 += localDelta.X; lt.M42 += localDelta.Y; lt.M43 += localDelta.Z;
        node.LocalTransform = lt;
    }

    private void ProcessScaleDrag(SceneNode node, Vector3 hitWorld)
    {
        var relStart   = _gizmoDragStartHit - _gizmoDragPlanePoint;
        var relCurrent = hitWorld           - _gizmoDragPlanePoint;
        float startLen = Vector3.Dot(relStart,   _gizmoDragAxisDir);
        float currLen  = Vector3.Dot(relCurrent, _gizmoDragAxisDir);
        if (MathF.Abs(startLen) < 1e-5f) return;
        float ratio = currLen / startLen;
        if (ratio <= 0f) return;

        var lt = _gizmoDragInitialLocal;
        switch (_gizmoDragAxis)
        {
            case GizmoAxis.X: lt.M11 *= ratio; lt.M12 *= ratio; lt.M13 *= ratio; break;
            case GizmoAxis.Y: lt.M21 *= ratio; lt.M22 *= ratio; lt.M23 *= ratio; break;
            case GizmoAxis.Z: lt.M31 *= ratio; lt.M32 *= ratio; lt.M33 *= ratio; break;
        }
        node.LocalTransform = lt;
    }

    private void ProcessRotateDrag(SceneNode node, Vector3 hitWorld)
    {
        var rel     = hitWorld - _gizmoDragPlanePoint;
        float angle = AxisAngle(_gizmoDragAxis, rel);
        float delta = angle - _gizmoDragStartAngle;

        var rot = _gizmoDragAxis switch
        {
            GizmoAxis.X => Matrix4.CreateRotationX(delta),
            GizmoAxis.Y => Matrix4.CreateRotationY(delta),
            _           => Matrix4.CreateRotationZ(delta),
        };

        var lt = _gizmoDragInitialLocal;
        var p  = new Vector3(lt.M41, lt.M42, lt.M43);
        lt = lt * rot;
        lt.M41 = p.X; lt.M42 = p.Y; lt.M43 = p.Z;
        node.LocalTransform = lt;
    }

    private static float AxisAngle(GizmoAxis axis, Vector3 v) => axis switch
    {
        GizmoAxis.X => MathF.Atan2(v.Z, v.Y),
        GizmoAxis.Y => MathF.Atan2(v.X, v.Z),
        _           => MathF.Atan2(v.Y, v.X),
    };

    private static Vector3 TransformDir(Vector3 d, Matrix4 m)
        => new(
            d.X * m.M11 + d.Y * m.M21 + d.Z * m.M31,
            d.X * m.M12 + d.Y * m.M22 + d.Z * m.M32,
            d.X * m.M13 + d.Y * m.M23 + d.Z * m.M33);
}
