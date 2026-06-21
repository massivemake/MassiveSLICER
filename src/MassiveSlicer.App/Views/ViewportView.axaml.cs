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
using MassiveSlicer.App.Enums;
using MassiveSlicer.App.Undo;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Effects;
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
    private CellEnvironmentBuilder.CellMultiToolSet? _multiTools;
    private SceneNode?             _rotaryBedPivot;
    private bool                   _multiToolFlangeParented;
    private bool                   _lastOutlinerLayerPreview;
    private const float InteractionScale = 0.55f;
    private readonly Queue<SceneNode> _cellGpuUploadQueue = new();
    private bool _cellGpuUploadPending;
    private const int MaxCellGpuUploadsPerFrame = 48;

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
    private float    _gizmoDragStartScreenX;
    private float    _gizmoDragCurrScreenX;

    // Keyboard-initiated transform state (Blender-style G/R/S)
    private bool      _kbTransformActive;
    private GizmoMode _kbTransformOp;
    private GizmoAxis _kbTransformAxis = GizmoAxis.None;
    private Point     _kbTransformStartPos;
    private Matrix4   _kbTransformInitialLocal;
    private Vector2   _kbObjScreenCenter;

    // Transform undo (panel numeric edits debounced; gizmo commits immediately)
    private SceneNode? _lastCommittedTransformNode;
    private Matrix4    _lastCommittedTransform = Matrix4.Identity;
    private CancellationTokenSource? _panelTransformDebounce;
    private CancellationTokenSource? _devAutoSaveDebounce;

    // Pointer capture
    private IPointer? _capturedPointer;

    // Seam guide drag
    private bool _seamGuideDragging;
    private int  _seamGuideDragIndex = -1;

    // Cached VM reference -- set on the UI thread in WireGlCanvas, read from GL thread in OnRender.
    // Avoids accessing the Avalonia DataContext property (UI-thread-only) from the GL thread.
    private ViewportViewModel? _vm;

    // Toolpath-to-node map -- populated on GL thread, read on UI thread (ConcurrentDictionary is safe)
    private readonly ConcurrentDictionary<SceneNode, Toolpath>                    _toolpathByNode       = new();
    private readonly ConcurrentDictionary<SceneNode, (float BeadWidth, float LayerHeight, NVec3 MaterialColor)> _toolpathMetaByNode = new();
    private readonly ConcurrentDictionary<SceneNode, MergedToolpathRecord> _mergedByNode = new();
    // Pre-smoothing toolpaths keyed by node -- used to re-apply OrientationSmoother live when settings change.
    private readonly ConcurrentDictionary<SceneNode, Toolpath>                    _rawToolpathByNode    = new();
    // Original centroid for each toolpath node. Used by ScrubIk to un-localise positions
    // before re-applying the node's current WorldTransform (which may have been moved by gizmo).
    private readonly ConcurrentDictionary<SceneNode, NVec3>                       _toolpathOriginByNode = new();
    // Flat (pos, normal) array per toolpath -- built once at upload for O(1) scrub lookup.
    private readonly ConcurrentDictionary<SceneNode, (NVec3 pos, NVec3 normal)[]> _scrubCacheByNode     = new();
    // Pending reachability results from background validation -- consumed on the GL thread.
    private readonly ConcurrentQueue<(SceneNode node, bool[] reachable)>          _pendingReachability      = new();
    // Pending singularity results from background validation -- consumed on the GL thread.
    private readonly ConcurrentQueue<(SceneNode node, bool[] singularity)>        _pendingSingularityPoints = new();
    // Pending orientation-rate colormap updates triggered by live smoothing changes -- consumed on the GL thread.
    private readonly ConcurrentQueue<(SceneNode node, float[] rates)>             _pendingOrientationUpdate = new();

    // The toolpath node whose scrubber is active. Set/cleared on the UI thread in
    // UpdateFocusOverlay; read on the UI thread in ScrubIk -- no cross-thread access.
    private SceneNode? _activeScrubNode;

    // Cancellation for in-flight scrub-IK tasks -- replaced on each scrub step so only
    // the most recent index drives the robot.
    private CancellationTokenSource? _scrubIkCts;
    // Cancellation for in-flight toolpath reachability validation.
    private CancellationTokenSource? _validationCts;
    // Cache for the last validation run. Prevents redundant restarts on every click.
    // _validationDone flips to true in the UI-thread dispatch when results are enqueued,
    // so cancelled tasks don't block a future re-run for the same key.
    private SceneNode?   _validationNode;
    private TkMatrix4    _validationTransform;
    private bool         _validationDone;

    // Pre-computed playback data -- populated by ValidateToolpathAsync on the background thread.
    private readonly ConcurrentDictionary<SceneNode, float[][]>  _ikSolutionsByNode  = new();
    private readonly ConcurrentDictionary<SceneNode, float[]>    _moveTimesMsByNode   = new(); // ms per move
    private readonly ConcurrentDictionary<SceneNode, bool[]>     _singularityByNode   = new();

    // Playback timing state.
    private double           _playbackStartElapsedMs;
    private readonly System.Diagnostics.Stopwatch _playbackStopwatch = new();

    // Timer that drives playback.  16 ms ≈ 60 fps for smooth real-time motion.
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    // Last joint angles forwarded to SyncTcpReadout -- skip the readout when joints haven't moved.
    private double _lastSyncA1, _lastSyncA2, _lastSyncA3, _lastSyncA4, _lastSyncA5, _lastSyncA6;

    // Dev mode: editable cell environment nodes (bed, rotary bed, stands, docks).
    private readonly Dictionary<SceneNode, (string Kind, string? Id)> _devNodeKinds = new();

    // Rotary bed (E1): the bed mesh wrapper node + its centre, so E1 can spin it about the vertical axis.
    private SceneNode? _bedNode;
    private Vector3    _bedOriginLocal;
    private Vector3    _bedBaseMarker;
    private float      _bedWidth, _bedDepth, _bedDiameter;
    private float      _bedRotationSign = -1f;   // E1→scene sign; set by config / rotation calibration
    private double     _lastSyncE1 = double.NaN;
    // Set on the UI thread by a manual bed edit; consumed on the GL thread (SetBedBoundary creates GL resources).
    private (float X, float Y, float Z, float Diameter, float Sign)? _pendingBedRebuild;

    // Robot cell state
    private Vector3  _robrootWorldPos;
    private Vector3  _tcpOffsetLocal;
    private Vector3  _tcpOrientationABC;  // TcpA/B/C in degrees, applied on top of the flange frame
    private Vector3? _sensorOriginLocal; // null when the current tool has no sensor origin
    private float   _toolFrameRoll;
    private float   _flangeDisplayRoll;

    private Matrix3 _gltfToKukaLocal = Matrix3.Identity;
    private Matrix4 _toolMeshMatrix  = Matrix4.Identity;

    // Simple button enum -- avoids dependency on WPF MouseButton.
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
        DataContextChanged   += (_, _) => WireGlCanvas();

        // Drag & drop
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent,  OnDragEnter);
        AddHandler(DragDrop.DropEvent,      OnDrop);
    }

    // -- GL lifecycle ----------------------------------------------------------

    private bool _glRenderWired;
    private bool _vmGlWired;

    private void WireGlCanvas()
    {
        if (!_glRenderWired)
        {
            _glRenderWired = true;
            GlCanvas.GlRender += OnRender;
        }

        if (_vmGlWired || DataContext is not ViewportViewModel vm) return;
        _vmGlWired = true;
        _vm = vm;

        {
            vm.PropertyChanged += (_, pe) =>
            {
                if (pe.PropertyName is
                    nameof(ViewportViewModel.ShowGrid)            or
                    nameof(ViewportViewModel.ShowAxes)            or
                    nameof(ViewportViewModel.ShowBedGrid)         or
                    nameof(ViewportViewModel.ShowDimensions)      or
                    nameof(ViewportViewModel.ActiveShaderMode)    or
                    nameof(ViewportViewModel.LightAzimuth)        or
                    nameof(ViewportViewModel.LightElevation)      or
                    nameof(ViewportViewModel.LightIntensity)      or
                    nameof(ViewportViewModel.ShowExtrusionMoves)  or
                    nameof(ViewportViewModel.ShowTravelMoves)     or
                    nameof(ViewportViewModel.ShowSeam)               or
                    nameof(ViewportViewModel.ShowBead)               or
                    nameof(ViewportViewModel.ShowBeadOverhang)       or
                    nameof(ViewportViewModel.ShowOrientationPreview))
                    GlCanvas.RequestNextFrameRendering();
                else if (pe.PropertyName is nameof(ViewportViewModel.IsLayFlatMode)
                                         or nameof(ViewportViewModel.IsSeamEditorActive))
                    Cursor = vm.IsLayFlatMode || vm.IsSeamEditorActive
                        ? new Cursor(StandardCursorType.Cross)
                        : Cursor.Default;
            };
            vm.RenderNeeded       += (_, _) => GlCanvas.RequestNextFrameRendering();
            vm.OnSeamGuidesChanged = () => UpdateSeamGuideMarkers(vm);
            vm.OnSliceRequested       = () => RunSliceAsync(vm);
            vm.OnUpdateSliceRequested = () => RunUpdateSliceAsync(vm);
            vm.CanUpdateSlice         = () => FindResliceSource(vm) is not null
                && (_activeScrubNode is null || !_mergedByNode.ContainsKey(_activeScrubNode));
            vm.GetToolpathSnapshot    = GetToolpathSnapshot;
            vm.OnExportKrlRequested   = () => ExportKrlAsync(vm);
            vm.OnSendToRobotRequested = () => SendToRobotAsync(vm);
            vm.OnMergeToolpathsRequested = () => MergeToolpaths(vm);
            vm.OnMergedSettingsChanged   = () => RebuildMergedToolpath(vm);
            vm.OnOutlinerSelectRequested = node =>
            {
                _renderer.Select(node);
                UpdateFocusOverlay();
                GlCanvas.RequestNextFrameRendering();
            };
            vm.OnNodeHidden           = node =>
            {
                if (_renderer.SelectedNode is { } sel && node.SelfAndDescendants().Any(n => n == sel))
                {
                    _renderer.Select(null);
                    UpdateFocusOverlay();
                }
            };
            vm.OnFocusRequested       = FocusSelected;
            vm.OnDropToPlateRequested = DropToPlate;
            vm.OnUngroupRequested     = UngroupSelected;
            vm.OnExplodeRequested     = ExplodeSelected;
            vm.OnMeshCleanupRequested = () => _ = MeshCleanupSelectedAsync();
            vm.OnScrubIkRequested  = ScrubIk;
            vm.OnFrameAllRequested = FrameAll;
            vm.GetCameraState = () =>
            {
                var c = _renderer.Camera;
                return new MassiveSlicer.Core.Models.CameraView
                {
                    Azimuth   = c.Azimuth,
                    Elevation = c.Elevation,
                    Radius    = c.Radius,
                    TargetX   = c.Target.X,
                    TargetY   = c.Target.Y,
                    TargetZ   = c.Target.Z,
                };
            };
            vm.ApplyCameraState = view =>
            {
                _renderer.Camera.Azimuth   = view.Azimuth;
                _renderer.Camera.Elevation = view.Elevation;
                _renderer.Camera.Radius    = view.Radius;
                _renderer.Camera.Target    = new Vector3(view.TargetX, view.TargetY, view.TargetZ);
                GlCanvas.RequestNextFrameRendering();
            };
            vm.OnPlaybackSpeedChanging = () =>
            {
                // Freeze the current simulated position so changing speed doesn't jump the toolhead.
                _playbackStartElapsedMs += _playbackStopwatch.Elapsed.TotalMilliseconds * (vm.PlaybackSpeed / 100.0);
                _playbackStopwatch.Restart();
            };

            WireToolChangeSequence(vm);

            vm.OnPlaybackToggled = playing =>
            {
                if (playing)
                {
                    // Seed elapsed time from the current scrub position so playback
                    // resumes from wherever the slider is, not always from the start.
                    _playbackStartElapsedMs = 0;
                    var node = _activeScrubNode;
                    if (node is not null && _moveTimesMsByNode.TryGetValue(node, out var mt))
                    {
                        int idx = vm.ToolpathScrubIndex;
                        for (int i = 0; i < idx && i < mt.Length; i++)
                            _playbackStartElapsedMs += mt[i];
                    }
                    _playbackStopwatch.Restart();
                    _playbackTimer.Start();
                }
                else
                {
                    _playbackTimer.Stop();
                    _playbackStopwatch.Stop();
                }
            };

            _playbackTimer.Tick += (_, _) =>
            {
                if (_vm is not { IsToolpathSelected: true } pvm) { _playbackTimer.Stop(); return; }
                var node = _activeScrubNode;
                if (node is null) { _playbackTimer.Stop(); return; }

                _ikSolutionsByNode.TryGetValue(node, out var solutions);
                _moveTimesMsByNode.TryGetValue(node, out var moveTimes);
                bool hasData = solutions is { Length: > 0 } && moveTimes is { Length: > 0 };

                if (hasData)
                {
                    double elapsed = _playbackStartElapsedMs
                        + _playbackStopwatch.Elapsed.TotalMilliseconds * (pvm.PlaybackSpeed / 100.0);

                    // Find which move contains this elapsed time, and the fraction within it.
                    double cumTime  = 0;
                    int    moveIdx  = moveTimes!.Length; // default: finished
                    float  tFrac    = 1f;
                    for (int i = 0; i < moveTimes.Length; i++)
                    {
                        double segEnd = cumTime + moveTimes[i];
                        if (elapsed < segEnd)
                        {
                            moveIdx = i;
                            tFrac   = moveTimes[i] > 0f ? (float)((elapsed - cumTime) / moveTimes[i]) : 1f;
                            break;
                        }
                        cumTime = segEnd;
                    }

                    if (moveIdx >= pvm.ToolpathScrubMax)
                    {
                        pvm.IsPlaying = false;
                        _playbackTimer.Stop();
                        _playbackStopwatch.Stop();
                        pvm.SetPlaybackIndex(pvm.ToolpathScrubMax);
                        return;
                    }

                    pvm.SetPlaybackIndex(moveIdx);

                    // solutions[i] = joint config at the END of move i.
                    // Interpolate from end-of-previous (= start of this move) to end-of-this.
                    int prevIdx = Math.Max(0, moveIdx - 1);
                    var a = solutions![prevIdx];
                    var b = solutions![moveIdx];
                    if (a is not null && b is not null)
                    {
                        float t = Math.Clamp(tFrac, 0f, 1f);
                        var interp = new float[6];
                        for (int j = 0; j < 6; j++)
                            interp[j] = a[j] + (b[j] - a[j]) * t;
                        SetRobotAnglesDirectly(interp);
                    }
                }
                else
                {
                    // Validation not yet complete — pause the stopwatch and wait.
                    // The play button is disabled while IsValidating, so this branch
                    // only fires in the rare window between button enable and first tick.
                    _playbackStopwatch.Stop();
                }
            };

            vm.ResetViewportOverlayState();
            UpdateFocusOverlay();
        }

        vm.OnDevModeChanged = ApplyDevModeSelectability;
        vm.OnSaveDevTransformRequested     = () => SaveDevTransform(vm);
        vm.OnSaveAllDevTransformsRequested = () => SaveAllDevTransforms(vm);

        if (vm.Robot is { } robot)
        {
            robot.PropertyChanged += (_, pe) =>
            {
                if (pe.PropertyName is nameof(RobotPanelViewModel.A1) or nameof(RobotPanelViewModel.A2) or
                    nameof(RobotPanelViewModel.A3) or nameof(RobotPanelViewModel.A4) or
                    nameof(RobotPanelViewModel.A5) or nameof(RobotPanelViewModel.A6) or
                    nameof(RobotPanelViewModel.E1))
                    GlCanvas.RequestNextFrameRendering();
            };
            robot.OnToolSelected              = OnToolSwapRequested;
            robot.OnSaveHomePositionRequested = (name, angles) => SaveHomePosition(vm, name, angles);
            robot.OnBedEdited = (x, y, z, dia, sign) =>
            {
                // GL resource rebuild must run on the render thread — queue it.
                _pendingBedRebuild = ((float)x, (float)y, (float)z, (float)dia, (float)sign);
                if (DataContext is ViewportViewModel vm2)
                {
                    vm2.NotifyRenderNeeded();
                    if (vm2.ActiveCellPath is { } path)
                        MassiveSlicer.Core.IO.CellLoader.SaveBedCenter(
                            path, (float)x, (float)y, (float)z,
                            dia > 0 ? (float)dia : (float?)null, (float)sign);
                }
            };
            robot.OnTcpOffsetEdited = (x, y, z, a, b, c) =>
            {
                _tcpOffsetLocal    = new Vector3((float)x, (float)y, (float)z);
                _tcpOrientationABC = new Vector3((float)a, (float)b, (float)c);
                if (DataContext is ViewportViewModel vm2 && vm2.Robot is not null)
                {
                    RebuildIkSolver(vm2);
                    SyncTcpReadout(vm2);
                    vm2.NotifyRenderNeeded();
                    if (vm2.ActiveCellPath is { } path)
                        MassiveSlicer.Core.IO.CellLoader.SaveToolTcp(
                            path, vm2.Robot.KrlToolIndex,
                            (float)x, (float)y, (float)z, (float)a, (float)b, (float)c);
                }
            };
        }

        vm.OnSelectionTranslated = (x, y, z) =>
        {
            if (_renderer.SelectedNode is not { } node) return;
            var lt = node.LocalTransform;
            lt.Row3 = new Vector4((float)x, (float)y, (float)z, 1f);
            node.LocalTransform = lt;
            GlCanvas.RequestNextFrameRendering();
            RevalidateSelectedToolpath();
            SchedulePanelTransformUndo(vm, node, "Move");
        };
        vm.OnSelectionRotated = (a, b, c) =>
        {
            if (_renderer.SelectedNode is not { } node) return;
            var lt   = node.LocalTransform;
            float sX = lt.Row0.Xyz.Length;
            float sY = lt.Row1.Xyz.Length;
            float sZ = lt.Row2.Xyz.Length;
            var rt = MassiveSlicer.Core.Kinematics.KukaIkSolver.AbcToMatrix((float)a, (float)b, (float)c);
            lt.Row0 = new Vector4(rt.M11 * sX, rt.M12 * sX, rt.M13 * sX, 0f);
            lt.Row1 = new Vector4(rt.M21 * sY, rt.M22 * sY, rt.M23 * sY, 0f);
            lt.Row2 = new Vector4(rt.M31 * sZ, rt.M32 * sZ, rt.M33 * sZ, 0f);
            node.LocalTransform = lt;
            GlCanvas.RequestNextFrameRendering();
            RevalidateSelectedToolpath();
            SchedulePanelTransformUndo(vm, node, "Rotate");
        };

        vm.GetToolWorldPose = ComputeToolWorldPose;
        vm.GetFlangeInBaseForCalibration = GetFlangeInBaseForCalibration;

        if (vm.AdditiveSettings is { } additive)
        {
            additive.OnOpenSeamEditorRequested = () =>
                vm.BeginSeamEditor(additive.BuildSeamGuideList());

            additive.PropertyChanged += (_, pe) =>
            {
                // Recompute layer-preview heatmap when any relevant setting changes.
                if (pe.PropertyName is nameof(AdditiveSettingsViewModel.ShowLayerPreview)
                                    or nameof(AdditiveSettingsViewModel.LayerHeight)
                                    or nameof(AdditiveSettingsViewModel.FirstLayerHeight)
                                    or nameof(AdditiveSettingsViewModel.AdaptiveLayerHeight)
                                    or nameof(AdditiveSettingsViewModel.AdaptiveQuality)
                                    or nameof(AdditiveSettingsViewModel.MinLayerHeight))
                {
                    if (additive.ShowLayerPreview)
                        _ = ComputeLayerPreviewAsync(vm);
                    else
                        GlCanvas.RequestNextFrameRendering();
                }

                if (pe.PropertyName is nameof(AdditiveSettingsViewModel.TiltAngle)
                                    or nameof(AdditiveSettingsViewModel.TiltAngleX)
                                    or nameof(AdditiveSettingsViewModel.Method))
                    GlCanvas.RequestNextFrameRendering();

                // Re-solve IK live when the toolhead orientation offset changes so the
                // user can see the effect in the viewport without moving the scrubber.
                // Also re-run full validation so reachability and singularity markers update.
                if (pe.PropertyName is nameof(AdditiveSettingsViewModel.ToolheadA)
                                    or nameof(AdditiveSettingsViewModel.ToolheadB)
                                    or nameof(AdditiveSettingsViewModel.ToolheadC))
                {
                    if (vm.IsToolpathSelected)
                        ScrubIk(vm.ToolpathScrubIndex);

                    if (_activeScrubNode is { } nd
                        && _toolpathByNode.TryGetValue(nd, out var tp))
                    {
                        _validationCts?.Cancel();
                        _validationDone = false;
                        ValidateToolpathAsync(nd, tp);
                    }
                }

                if (pe.PropertyName == nameof(AdditiveSettingsViewModel.ApoCvel))
                {
                    if (vm.IsToolpathSelected && _activeScrubNode is { } nd
                        && _toolpathByNode.TryGetValue(nd, out var tp))
                    {
                        _validationCts?.Cancel();
                        _validationDone = false;
                        ValidateToolpathAsync(nd, tp);
                    }
                }

                if (pe.PropertyName is nameof(AdditiveSettingsViewModel.SmoothRotation)
                                    or nameof(AdditiveSettingsViewModel.SmoothRotationRadius)
                                    or nameof(AdditiveSettingsViewModel.SmoothRotationMaxRateDegPerMm))
                    ReapplyOrientationSmoothing(additive);
            };

            additive.OnSetDefaultHomePositionRequested = () => SaveDefaultHomePosition(vm);
            UpdateSeamGuideMarkers(vm);
            GlCanvas.RequestNextFrameRendering();
        }
    }

    private void UpdateSeamGuideMarkers(ViewportViewModel vm)
    {
        IReadOnlyList<TkVector3> guides;
        if (vm.IsSeamEditorActive)
        {
            guides = vm.SeamGuideDraft
                .Select(g => new TkVector3(g.X, g.Y, g.Z))
                .ToList();
        }
        else
        {
            guides = vm.AdditiveSettings?.SeamGuides
                .Select(g => new TkVector3(g.X, g.Y, g.Z))
                .ToList() ?? [];
        }
        _renderer.SetSeamGuides(guides, vm.SelectedSeamGuideIndex);
        GlCanvas.RequestNextFrameRendering();
    }

    private bool TryPlaceSeamGuide(Ray ray, out System.Numerics.Vector3 hit)
    {
        var (node, _, meshHit) = _renderer.PickFace(ray);
        if (node is not null && !_renderer.IsToolpathNode(node))
        {
            hit = new System.Numerics.Vector3(meshHit.X, meshHit.Y, meshHit.Z);
            return true;
        }

        if (_renderer.TryPickBed(ray, out var bedHit))
        {
            hit = new System.Numerics.Vector3(bedHit.X, bedHit.Y, bedHit.Z);
            return true;
        }

        hit = default;
        return false;
    }

    private bool TryDragSeamGuide(Ray ray, ViewportViewModel vm, int index, out System.Numerics.Vector3 hit)
    {
        if (index < 0 || index >= vm.SeamGuideDraft.Count)
        {
            hit = default;
            return false;
        }

        float planeZ = vm.SeamGuideDraft[index].Z;
        if (SceneRenderer.TryPickHorizontalPlane(ray, planeZ, out var planeHit))
        {
            hit = new System.Numerics.Vector3(planeHit.X, planeHit.Y, planeHit.Z);
            return true;
        }

        hit = default;
        return false;
    }

    private void OnRender(TimeSpan delta, int w, int h)
    {
        _renderer.Initialise();

        if (_vm is { } vm)
        {
            _renderer.ShowGrid    = vm.ShowGrid;
            _renderer.ShowAxes    = vm.ShowAxes;
            _renderer.ShowBedGrid = vm.ShowBedGrid;
            _renderer.ShowExtrusionMoves = vm.ShowExtrusionMoves;
            _renderer.ShowTravelMoves    = vm.ShowTravelMoves;
            _renderer.ShowSeam           = vm.ShowSeam;
            _renderer.ShowBead          = vm.ShowBead;
            _renderer.ShowBeadOverhang       = vm.ShowBeadOverhang;
            _renderer.ShowOrientationPreview = vm.ShowOrientationPreview;
            _renderer.ToolpathActiveScrubIndex  = vm.IsToolpathSelected
                ? vm.ToolpathScrubIndex
                : int.MaxValue;
            _renderer.SetToolpathColors(
                new TkVector3(vm.ToolpathExtrudeColor.X,     vm.ToolpathExtrudeColor.Y,     vm.ToolpathExtrudeColor.Z),
                new TkVector3(vm.ToolpathTravelColor.X,      vm.ToolpathTravelColor.Y,      vm.ToolpathTravelColor.Z),
                new TkVector3(vm.ToolpathSeamColor.X,        vm.ToolpathSeamColor.Y,        vm.ToolpathSeamColor.Z),
                new TkVector3(vm.ToolpathUnselectedColor.X,  vm.ToolpathUnselectedColor.Y,  vm.ToolpathUnselectedColor.Z),
                new TkVector3(vm.ToolpathWipeColor.X,        vm.ToolpathWipeColor.Y,        vm.ToolpathWipeColor.Z),
                new TkVector3(vm.ToolpathRetractionColor.X,  vm.ToolpathRetractionColor.Y,  vm.ToolpathRetractionColor.Z));
            _renderer.GizmoEnabled   = vm.ActiveGizmoModeInternal != GizmoMode.None;
            _renderer.GizmoMode      = vm.ActiveGizmoModeInternal;
            _renderer.ShaderMode         = vm.ActiveShaderMode;
            _renderer.LayerPreviewHeight = (float)(vm.AdditiveSettings?.LayerHeight ?? 3.0);
            bool layerPreview = vm.AdditiveSettings?.ShowLayerPreview ?? false;
            if (layerPreview != _lastOutlinerLayerPreview)
            {
                _lastOutlinerLayerPreview = layerPreview;
                foreach (var item in vm.OutlinerItems)
                {
                    if (!_renderer.IsToolpathNode(item.Node))
                        item.Node.LayerPreview = layerPreview;
                }
                _renderer.InvalidateShaderAppearance();
            }
            _renderer.LightAzimuth   = vm.LightAzimuth;
            _renderer.LightElevation = vm.LightElevation;
            _renderer.LightIntensity = vm.LightIntensity;

            if (_renderer.BackdropPath != vm.ActiveBackdropPath)
            {
                _renderer.SetBackdrop(vm.ActiveBackdropPath);
                _renderer.InvalidateShaderAppearance();
            }
            _renderer.BackdropBlur = vm.BackdropBlur;

            while (vm.PendingCellSwap.TryDequeue(out var swap))
                ApplyCellSwap(swap, vm);

            if (ProcessCellGpuUploadQueue())
                GlCanvas.RequestNextFrameRendering();

            while (vm.PendingLayerPreview.TryDequeue(out var lp))
                _renderer.SetLayerPreview(lp.zBounds, lp.heights);

            while (vm.PendingRemoveNodes.TryDequeue(out var removing))
            {
                _toolpathByNode.TryRemove(removing, out _);
                _rawToolpathByNode.TryRemove(removing, out _);
                _toolpathMetaByNode.TryRemove(removing, out _);
                _mergedByNode.TryRemove(removing, out _);
                _toolpathOriginByNode.TryRemove(removing, out _);
                _scrubCacheByNode.TryRemove(removing, out _);
                _ikSolutionsByNode.TryRemove(removing, out _);
                _moveTimesMsByNode.TryRemove(removing, out _);
                _singularityByNode.TryRemove(removing, out _);
                _renderer.RemoveToolpathIfExists(removing);
                GpuMeshCache.ReleaseSubtree(removing);
                _renderer.SceneRoot.RemoveChild(removing);
                if (_renderer.SelectedNode is not null &&
                    removing.SelfAndDescendants().Any(n => n == _renderer.SelectedNode))
                    _renderer.Select(null);
            }

            while (vm.PendingNodes.TryDequeue(out var incoming))
            {
                _renderer.SceneRoot.AddChild(incoming);
                UploadPendingMeshes(incoming);
                _renderer.InvalidateShaderAppearance();

                if (_fkController is null)
                    _fkController = RobotFkController.TryBuild(incoming,
                        vm.ActiveCell?.Robot.Joints ?? []);

                _renderer.Select(incoming);
                Dispatcher.UIThread.Post(UpdateFocusOverlay);
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
                if (_multiTools is not null)
                {
                    ApplyMultiToolMount(swap.Config, vm);
                    continue;
                }

                if (_fkController?.FlangeNode is not { } flange) continue;

                if (_currentToolNode is not null)
                {
                    GpuMeshCache.ReleaseSubtree(_currentToolNode);
                    _renderer.SceneRoot.RemoveChild(_currentToolNode);
                    _currentToolNode = null;
                }

                _toolCorrectionMatrix    = swap.Node.LocalTransform;
                var t = swap.Config;

                _tcpOffsetLocal    = new Vector3(t.TcpX, t.TcpY, t.TcpZ);
                _tcpOrientationABC = new Vector3(t.TcpA, t.TcpB, t.TcpC);

                _sensorOriginLocal = t.HasSensorOrigin
                    ? new Vector3(t.SensorOriginX!.Value, t.SensorOriginY!.Value, t.SensorOriginZ!.Value)
                    : (Vector3?)null;

                _toolFrameRoll   = t.ToolFrameRoll * MathF.PI / 180f;
                RebuildFrameMatrices();
                swap.Node.LocalTransform = _toolMeshMatrix * flange.WorldTransform;
                swap.Node.Selectable     = true;
                _renderer.SceneRoot.AddChild(swap.Node);
                UploadPendingMeshes(swap.Node);
                _currentToolNode = swap.Node;

                RebuildIkSolver(vm);

                // Immediately refresh the TCP gizmo and readout so the viewport
                // shows the new TCP without waiting for the next joint-angle event.
                if (vm.Robot is not null)
                {
                    SyncTcpReadout(vm);

                    // Debug: log TCP offset so we can verify 100mm from flange in correct direction
                    if (_fkController?.FlangeNode is { } dbgFlange)
                    {
                        var fw2  = dbgFlange.WorldTransform;
                        var pos2 = fw2.Row3.Xyz;
                        float sc2 = fw2.Row0.Xyz.Length;
                        var gRot2 = new Matrix3(fw2.Row0.Xyz / sc2, fw2.Row1.Xyz / sc2, fw2.Row2.Xyz / sc2);
                        var kRot2 = _gltfToKukaLocal * gRot2;
                        var tcpPt = pos2 + _tcpOffsetLocal.X * kRot2.Row0
                                        + _tcpOffsetLocal.Y * kRot2.Row1
                                        + _tcpOffsetLocal.Z * kRot2.Row2;
                        var tcpDelta = tcpPt - pos2;
                        System.Console.WriteLine($"[tcp] Tool={t.Name}  Flange=({pos2.X:F1},{pos2.Y:F1},{pos2.Z:F1})  TCP=({tcpPt.X:F1},{tcpPt.Y:F1},{tcpPt.Z:F1})  Δ=({tcpDelta.X:F1},{tcpDelta.Y:F1},{tcpDelta.Z:F1}) len={tcpDelta.Length:F1}mm  KukaZ=({kRot2.Row2.X:F3},{kRot2.Row2.Y:F3},{kRot2.Row2.Z:F3})");
                    }
                }
            }

            while (_pendingOrientationUpdate.TryDequeue(out var upd))
                _renderer.UpdateToolpathBeadOrientation(upd.node, upd.rates);

            while (vm.PendingToolpath.TryDequeue(out var entry))
            {
                UploadToolpathEntry(entry, addToScene: true);
                _renderer.Select(entry.Node);
                Dispatcher.UIThread.Post(UpdateFocusOverlay);
            }

            while (vm.PendingToolpathReplace.TryDequeue(out var entry))
            {
                _ikSolutionsByNode.TryRemove(entry.Node, out _);
                _moveTimesMsByNode.TryRemove(entry.Node, out _);
                _singularityByNode.TryRemove(entry.Node, out _);
                UploadToolpathEntry(entry, addToScene: false);
                _renderer.Select(entry.Node);
                Dispatcher.UIThread.Post(UpdateFocusOverlay);
            }

            // Apply any completed reachability results on the GL thread.
            while (_pendingReachability.TryDequeue(out var reach))
                _renderer.UpdateToolpathReachability(reach.node, reach.reachable);

            while (_pendingSingularityPoints.TryDequeue(out var sing))
                _renderer.UpdateToolpathSingularityPoints(sing.node, sing.singularity);

            UpdateAnglePlanePreview(vm);

            if (_fkController is not null && vm.Robot is { } fkRobot)
            {
                double a1 = fkRobot.A1, a2 = fkRobot.A2, a3 = fkRobot.A3,
                       a4 = fkRobot.A4, a5 = fkRobot.A5, a6 = fkRobot.A6;

                _fkController.Apply((float)a1, (float)a2, (float)a3,
                                    (float)a4, (float)a5, (float)a6);

                if (_currentToolNode is not null && _fkController.FlangeNode is { } flange
                    && !_toolIsDragging && !_multiToolFlangeParented)
                    _currentToolNode.LocalTransform = _toolMeshMatrix * flange.WorldTransform;

                if (_rotaryBedPivot is not null && vm.Robot is { } rbE1)
                {
                    float e1Rad = (float)(_bedRotationSign * rbE1.E1 * Math.PI / 180.0);
                    _rotaryBedPivot.LocalTransform = Matrix4.CreateRotationZ(e1Rad);
                }

                if (a1 != _lastSyncA1 || a2 != _lastSyncA2 || a3 != _lastSyncA3 ||
                    a4 != _lastSyncA4 || a5 != _lastSyncA5 || a6 != _lastSyncA6)
                {
                    _lastSyncA1 = a1; _lastSyncA2 = a2; _lastSyncA3 = a3;
                    _lastSyncA4 = a4; _lastSyncA5 = a5; _lastSyncA6 = a6;
                    SyncTcpReadout(vm);
                }
            }

            // Apply a queued manual bed edit (GL resource rebuild — safe here on the GL thread).
            if (_pendingBedRebuild is { } pend)
            {
                _pendingBedRebuild = null;
                RebuildBed(pend.X, pend.Y, pend.Z, pend.Diameter, pend.Sign);
            }

            // Rotate the rotary bed (mesh + print-grid overlay) about the vertical axis
            // through its centre to match E1. Sign comes from rotation calibration.
            if (vm.Robot is { } e1Robot && e1Robot.E1 != _lastSyncE1)
            {
                _lastSyncE1 = e1Robot.E1;
                float e1Rad = (float)(_bedRotationSign * e1Robot.E1 * Math.PI / 180.0);
                var c = _bedOriginLocal;

                if (_bedNode is not null)
                    _bedNode.LocalTransform =
                        Matrix4.CreateRotationZ(e1Rad) *
                        Matrix4.CreateTranslation(c.X, c.Y, c.Z);

                // Boundary geometry is in absolute world coords, so rotate about the centre:
                // translate centre→origin, rotate, translate back.
                _renderer.BedBoundaryModel =
                    Matrix4.CreateTranslation(-c.X, -c.Y, -c.Z) *
                    Matrix4.CreateRotationZ(e1Rad) *
                    Matrix4.CreateTranslation(c.X, c.Y, c.Z);
            }
        }

        _renderer.Render(w, h);
        UpdateSequenceWaypointTags(w, h);
    }

    // -- TCP readout -----------------------------------------------------------

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

        // Apply TcpA/B/C to get tool-frame axes in world space.
        // AbcToMatrix returns R^T (row-major), so toolWorldRot = abcMat * kukaRot.
        var abcMat  = KukaIkSolver.AbcToMatrix(_tcpOrientationABC.X, _tcpOrientationABC.Y, _tcpOrientationABC.Z);
        var kukaN   = new System.Numerics.Matrix4x4(
            kukaX.X, kukaX.Y, kukaX.Z, 0,
            kukaY.X, kukaY.Y, kukaY.Z, 0,
            kukaZ.X, kukaZ.Y, kukaZ.Z, 0,
            0, 0, 0, 1);
        var toolN   = abcMat * kukaN;
        var tcpAxisX = new Vector3(toolN.M11, toolN.M12, toolN.M13);
        var tcpAxisY = new Vector3(toolN.M21, toolN.M22, toolN.M23);
        var tcpAxisZ = new Vector3(toolN.M31, toolN.M32, toolN.M33);

        _renderer.TcpFrameMatrix = new Matrix4(
            tcpAxisX.X, tcpAxisX.Y, tcpAxisX.Z, 0,
            tcpAxisY.X, tcpAxisY.Y, tcpAxisY.Z, 0,
            tcpAxisZ.X, tcpAxisZ.Y, tcpAxisZ.Z, 0,
            tcp.X,      tcp.Y,      tcp.Z,       1f);

        _renderer.FlangeFrameMatrix = new Matrix4(
            kukaX.X, kukaX.Y, kukaX.Z, 0,
            kukaY.X, kukaY.Y, kukaY.Z, 0,
            kukaZ.X, kukaZ.Y, kukaZ.Z, 0,
            pos.X,   pos.Y,   pos.Z,   1f);

        if (_sensorOriginLocal is { } so)
        {
            var sensorPt = pos
                + so.X * kukaX
                + so.Y * kukaY
                + so.Z * kukaZ;
            _renderer.SensorOriginFrameMatrix = new Matrix4(
                kukaX.X, kukaX.Y, kukaX.Z, 0,
                kukaY.X, kukaY.Y, kukaY.Z, 0,
                kukaZ.X, kukaZ.Y, kukaZ.Z, 0,
                sensorPt.X, sensorPt.Y, sensorPt.Z, 1f);
        }
        else
        {
            _renderer.SensorOriginFrameMatrix = null;
        }

        vm.Robot!.FlangeX = Math.Round(pos.X - _robrootWorldPos.X, 1);
        vm.Robot.FlangeY  = Math.Round(pos.Y - _robrootWorldPos.Y, 1);
        vm.Robot.FlangeZ  = Math.Round(pos.Z - _robrootWorldPos.Z, 1);

        vm.Robot.TcpX = Math.Round(tcp.X, 1);
        vm.Robot.TcpY = Math.Round(tcp.Y, 1);
        vm.Robot.TcpZ = Math.Round(tcp.Z, 1);

        var (a, b, c) = KukaIkSolver.MatrixToAbc(toolN);
        vm.Robot.TcpA = Math.Round(a, 2);
        vm.Robot.TcpB = Math.Round(b, 2);
        vm.Robot.TcpC = Math.Round(c, 2);
    }

    // -- Tool helpers ----------------------------------------------------------

    /// <summary>
    /// World-space pose of a KUKA tool frame (TCP offset + ABC orientation applied
    /// to the current flange pose). Same flange-frame math as <see cref="SyncTcpReadout"/>,
    /// extended with the tool's calibrated orientation so a flange-mounted camera
    /// frame can be placed in the scene.
    /// </summary>
    private Matrix4? ComputeToolWorldPose(ToolCellConfig tool)
    {
        if (_fkController?.FlangeNode is not { } flange) return null;

        var fw  = flange.WorldTransform;
        var pos = fw.Row3.Xyz;
        float sc = fw.Row0.Xyz.Length;

        var gltfRot = new Matrix3(fw.Row0.Xyz / sc, fw.Row1.Xyz / sc, fw.Row2.Xyz / sc);
        var kukaRot = _gltfToKukaLocal * gltfRot;
        var fx = kukaRot.Row0;
        var fy = kukaRot.Row1;
        var fz = kukaRot.Row2;

        // Flange-frame vector → world.
        Vector3 ToWorld(float x, float y, float z) => x * fx + y * fy + z * fz;

        // Sensor origin is the optical centre; use it for scan registration when available.
        // Falls back to live TCP offset (_tcpOffsetLocal) so edits in the TCP OFFSET panel
        // take effect immediately without a cell reload.
        float ox = tool.HasSensorOrigin ? tool.SensorOriginX!.Value : _tcpOffsetLocal.X;
        float oy = tool.HasSensorOrigin ? tool.SensorOriginY!.Value : _tcpOffsetLocal.Y;
        float oz = tool.HasSensorOrigin ? tool.SensorOriginZ!.Value : _tcpOffsetLocal.Z;
        // Always use the live A/B/C from the TCP OFFSET panel — _tcpOrientationABC is updated
        // by OnTcpOffsetEdited and all tool-load paths, so it always reflects the current edit.
        float oA = _tcpOrientationABC.X;
        float oB = _tcpOrientationABC.Y;
        float oC = _tcpOrientationABC.Z;

        var rt = KukaIkSolver.AbcToMatrix(oA, oB, oC);
        var tx = ToWorld(rt.M11, rt.M12, rt.M13);
        var ty = ToWorld(rt.M21, rt.M22, rt.M23);
        var tz = ToWorld(rt.M31, rt.M32, rt.M33);
        var origin = pos + ToWorld(ox, oy, oz);

        return new Matrix4(
            tx.X,     tx.Y,     tx.Z,     0f,
            ty.X,     ty.Y,     ty.Z,     0f,
            tz.X,     tz.Y,     tz.Z,     0f,
            origin.X, origin.Y, origin.Z, 1f);
    }

    /// <summary>
    /// Current flange-to-world pose as a row-vector <see cref="System.Numerics.Matrix4x4"/>,
    /// using the EXACT same flange frame as <see cref="ComputeToolWorldPose"/>
    /// (rendered glTF flange × <c>_gltfToKukaLocal</c>). Hand-eye calibration feeds this
    /// so its result is expressed in the frame registration later applies it in — the
    /// analytic <c>KukaIkSolver.ForwardKinematics</c> flange does NOT match this frame.
    /// Rows 0–2 are the flange X/Y/Z axes in world; row 3 is the flange origin (mm).
    /// </summary>
    private System.Numerics.Matrix4x4? GetFlangeInBaseForCalibration()
    {
        if (_fkController?.FlangeNode is not { } flange) return null;

        var fw  = flange.WorldTransform;
        var pos = fw.Row3.Xyz;
        float sc = fw.Row0.Xyz.Length;

        var gltfRot = new Matrix3(fw.Row0.Xyz / sc, fw.Row1.Xyz / sc, fw.Row2.Xyz / sc);
        var kukaRot = _gltfToKukaLocal * gltfRot;
        var fx = kukaRot.Row0;
        var fy = kukaRot.Row1;
        var fz = kukaRot.Row2;

        return new System.Numerics.Matrix4x4(
            fx.X,  fx.Y,  fx.Z,  0f,
            fy.X,  fy.Y,  fy.Z,  0f,
            fz.X,  fz.Y,  fz.Z,  0f,
            pos.X, pos.Y, pos.Z, 1f);
    }

    /// <summary>
    /// Re-applies a manually-edited rotary-bed centre/diameter to the live scene: moves the
    /// rotation pivot + grid datum, rebuilds the boundary (circular when diameter &gt; 0), and
    /// forces the next frame to re-apply the E1 rotation about the new centre.
    /// </summary>
    private void RebuildBed(float x, float y, float z, float diameter, float rotationSign)
    {
        _bedOriginLocal  = new Vector3(x, y, z);
        _bedDiameter     = diameter;
        _bedRotationSign = rotationSign;
        // Centre-derived corner keeps a rectangular grid centred; ignored for circular beds.
        var corner = new Vector3(x - _bedWidth * 0.5f, y - _bedDepth * 0.5f, z);
        _renderer.SetBedBoundary(_bedBaseMarker, corner, _bedWidth, _bedDepth, new Vector3(x, y, z), diameter);
        _lastSyncE1 = double.NaN;   // re-apply E1 rotation (mesh + boundary) about the new pivot next frame
    }

    private void RebuildFrameMatrices()
    {
        // Total roll = per-tool mounting offset + per-cell flange reference mark offset.
        float totalRoll = _toolFrameRoll + _flangeDisplayRoll;
        float cr = MathF.Cos(totalRoll), sr = MathF.Sin(totalRoll);
        _gltfToKukaLocal = new Matrix3(
            new Vector3( cr, 0f,  sr),
            new Vector3( sr, 0f, -cr),
            new Vector3( 0f, 1f,  0f));
        _toolMeshMatrix = _toolCorrectionMatrix * Matrix4.CreateRotationY(-_flangeDisplayRoll);
    }

    private void RebuildIkSolver(ViewportViewModel vm)
    {
        if (_fkController is null) return;
        float totalRoll = _toolFrameRoll + _flangeDisplayRoll;
        float cr = MathF.Cos(totalRoll);
        float sr = MathF.Sin(totalRoll);
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
            totalRoll);
        if (vm.Robot is not null)
            vm.Robot.IkSolver = _ikSolver;
    }

    // -- Cell swap -------------------------------------------------------------

    void ClearAllViewportToolpaths()
    {
        _renderer.ClearAllToolpaths();
        _toolpathByNode.Clear();
        _rawToolpathByNode.Clear();
        _toolpathMetaByNode.Clear();
        _mergedByNode.Clear();
        _toolpathOriginByNode.Clear();
        _scrubCacheByNode.Clear();
        _ikSolutionsByNode.Clear();
        _moveTimesMsByNode.Clear();
        _singularityByNode.Clear();
        _activeScrubNode = null;
    }

    private void ApplyCellSwap(CellSwapPayload swap, ViewportViewModel vm)
    {
        // Stop tool-change playback on the UI thread before FK / multi-tool state is torn down.
        if (Dispatcher.UIThread.CheckAccess())
            ClearToolChangeSequence(restorePriorMount: false);
        else
            Dispatcher.UIThread.Invoke(() => ClearToolChangeSequence(restorePriorMount: false));

        ClearAllViewportToolpaths();

        _cellGpuUploadQueue.Clear();
        _cellGpuUploadPending = false;

        foreach (var child in _renderer.SceneRoot.Children.ToList())
        {
            GpuMeshCache.ReleaseSubtree(child);
            _renderer.SceneRoot.RemoveChild(child);
        }
        while (vm.PendingToolNodes.TryDequeue(out _)) {}
        while (vm.PendingToolSwap.TryDequeue(out _)) {}

        _fkController               = null;
        _ikSolver                   = null;
        _currentToolNode            = null;
        _multiTools                 = null;
        _rotaryBedPivot             = null;
        _multiToolFlangeParented    = false;
        _renderer.TcpFrameMatrix    = null;
        _renderer.FlangeFrameMatrix = null;
        if (vm.Robot is not null) vm.Robot.IkSolver = null;

        vm.ActiveCell     = swap.Config;
        vm.ActiveCellPath = swap.CellPath;
        var swapCellForPost = swap.Config;
        var swapCellPath    = swap.CellPath;
        Dispatcher.UIThread.Post(() =>
        {
            var additive = vm.AdditiveSettings;
            if (additive is null) return;
            var posData = CellLoader.LoadPositionData(swapCellPath);
            additive.UpdateFromCell(swapCellForPost, posData.Default, posData.Positions);
            if (vm.Robot is not null)
            {
                vm.Robot.SetNextPositionName(posData.Positions.Count + 1);
                var bed = swapCellForPost.Bed;
                vm.Robot.ConfigureBed(bed.Origin.X, bed.Origin.Y, bed.Origin.Z,
                                      bed.Diameter ?? 0f, bed.RotationSign ?? -1f, bed.Diameter is > 0f);
            }
        });
        var b          = swap.Config.Bed;
        var rpBed      = swap.Config.Robot.WorldPosition;
        var baseMarker = b.BaseMarkerWorld(rpBed);
        var gridCorner = b.VisualGridCorner(rpBed);
        var gridDatum  = b.HasVisualShift && b.GridOrigin is null
            ? gridCorner
            : new Float3(b.Origin.X, b.Origin.Y, gridCorner.Z);
        _bedBaseMarker   = new Vector3(baseMarker.X, baseMarker.Y, baseMarker.Z);
        _bedWidth        = b.Width;
        _bedDepth        = b.Depth;
        _bedDiameter     = b.Diameter ?? 0f;
        _bedRotationSign = b.RotationSign ?? -1f;
        // Blue origin marker stays at BASE 0,0,0; grid/border follow visual placement.
        _renderer.SetBedBoundary(
            new Vector3(baseMarker.X, baseMarker.Y, baseMarker.Z),
            new Vector3(gridCorner.X, gridCorner.Y, gridCorner.Z),
            b.Width, b.Depth,
            new Vector3(gridDatum.X, gridDatum.Y, gridDatum.Z), _bedDiameter);

        // Focus on the centre of the print area and set radius to the bed diagonal
        // so the whole bed is comfortably in view at startup.
        _renderer.Camera.Target = new Vector3(
            gridCorner.X + b.Width * 0.5f,
            gridCorner.Y + b.Depth * 0.5f,
            gridCorner.Z);
        _renderer.Camera.Radius = MathF.Sqrt(b.Width * b.Width + b.Depth * b.Depth);

        // A saved per-cell view (shared via the cell JSON) overrides the default framing.
        if (swap.Config.View is { } sv)
        {
            _renderer.Camera.Azimuth   = sv.Azimuth;
            _renderer.Camera.Elevation = sv.Elevation;
            _renderer.Camera.Radius    = sv.Radius;
            _renderer.Camera.Target    = new Vector3(sv.TargetX, sv.TargetY, sv.TargetZ);
        }

        var rp = swap.Config.Robot.WorldPosition;
        _robrootWorldPos   = new Vector3(rp.X, rp.Y, rp.Z);
        _flangeDisplayRoll = swap.Config.Robot.FlangeDisplayRoll * MathF.PI / 180f;

        if (swap.RobotBaseNode is { } robot)
        {
            _renderer.SceneRoot.AddChild(robot);
            UploadVisiblePendingMeshes(robot);
        }
        EnqueueCellGpuUpload(swap.BoosterNode);
        EnqueueCellGpuUpload(swap.BedNode);

        // Retain the bed wrapper so E1 can rotate it about the vertical axis through its centre.
        _bedNode        = swap.BedNode;
        var meshOrigin  = b.VisualMeshOrigin(rpBed);
        _bedOriginLocal = new Vector3(meshOrigin.X, meshOrigin.Y, meshOrigin.Z);
        if (_bedNode is not null)
            _bedNode.LocalTransform = Matrix4.CreateTranslation(meshOrigin.X, meshOrigin.Y, meshOrigin.Z);
        _lastSyncE1     = double.NaN;   // force the bed transform to refresh on the next frame

        if (swap.RobotBaseNode is not null)
            _fkController = RobotFkController.TryBuild(swap.RobotBaseNode, swap.Config.Robot.Joints);

        if (_fkController is not null)
        {
            var h = swap.Config.Robot.HomePosition;
            if (h.Length >= 6)
                _fkController.Apply(h[0], h[1], h[2], h[3], h[4], h[5]);
        }

        _multiTools     = swap.MultiTools;
        _rotaryBedPivot = swap.RotaryBedPivot;

        foreach (var env in swap.EnvironmentNodes)
        {
            _renderer.SceneRoot.AddChild(env);
            if (env.Name == "RotaryBed")
                UploadVisiblePendingMeshes(env);
            else
                EnqueueCellGpuUpload(env);
        }

        if (_fkController?.FlangeNode is { } flange)
        {
            if (swap.FlangeAttachment is { } aff)
            {
                aff.Selectable     = false;
                aff.LocalTransform = Matrix4.CreateRotationY(MathF.PI / 2f);
                flange.AddChild(aff);
                EnqueueCellGpuUpload(aff);
            }

            if (_multiTools is { } mt)
            {
                _multiToolFlangeParented = true;
                AddMultiToolVisualsToScene(mt, flange);
                ApplyInitialMultiToolState(vm);
            }
            else if (swap.ToolHolder is not null && swap.FirstTool is { } firstTool)
            {
                _tcpOffsetLocal    = new Vector3(firstTool.TcpX, firstTool.TcpY, firstTool.TcpZ);
                _tcpOrientationABC = new Vector3(firstTool.TcpA, firstTool.TcpB, firstTool.TcpC);
                _sensorOriginLocal = firstTool.HasSensorOrigin
                    ? new Vector3(firstTool.SensorOriginX!.Value, firstTool.SensorOriginY!.Value, firstTool.SensorOriginZ!.Value)
                    : (Vector3?)null;
                _toolFrameRoll        = firstTool.ToolFrameRoll * MathF.PI / 180f;
                _toolCorrectionMatrix = swap.ToolHolder.LocalTransform;
                RebuildFrameMatrices();
                swap.ToolHolder.LocalTransform = _toolMeshMatrix * flange.WorldTransform;
                swap.ToolHolder.Selectable     = true;
                _renderer.SceneRoot.AddChild(swap.ToolHolder);
                UploadVisiblePendingMeshes(swap.ToolHolder);
                _currentToolNode = swap.ToolHolder;
            }
        }
        else if (_multiTools is { } mtNoFlange)
        {
            System.Console.Error.WriteLine("[cell] robot flange not found — docked tools only");
            AddMultiToolVisualsToScene(mtNoFlange, flange: null);
            ApplyInitialMultiToolState(vm);
        }

        RebuildFrameMatrices();
        RebuildIkSolver(vm);
        RebuildDevNodeRegistry(swap);
        ApplyDevModeSelectability(vm.IsDevMode);
        _renderer.InvalidateShaderAppearance();
        _cellGpuUploadPending = _cellGpuUploadQueue.Count > 0;
        _renderer.Select(null);
        GlCanvas.RequestNextFrameRendering();

        {
            int pending = _cellGpuUploadQueue.Count;
            if (pending > 0)
                System.Console.WriteLine($"[cell] GPU upload queued: {pending} mesh(es)");
            else if (swap.RobotBaseNode is not null)
                System.Console.WriteLine("[cell] scene swap applied — robot visible");
        }

        // Dispatch UI-thread updates: joint limits, home angles, tool library.
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClearToolChangeSequence(restorePriorMount: false);
            vm.ResetViewportOverlayState();
            UpdateFocusOverlay();
            vm.NotifyCellChanged();

            if (vm.Robot is null) return;
            vm.Robot.Configure(swap.Config.Robot.Joints, swap.Config.Robot.HomePosition);
            vm.Robot.SetBridgeConfig(swap.Config.BridgeIp, swap.Config.BridgePort);
            vm.LiveIo.SetExtruderBridgeConfig(swap.Config.ExtIp, swap.Config.ExtBridgePort);
            vm.LiveIo.SetMillingBridgeConfig(swap.Config.MillIp, swap.Config.HasMilling, swap.Config.MillBridgePort);
            vm.Robot.SetToolLibrary(swap.Config.EffectiveTools);

            if (swap.MultiTools is not null)
                vm.MountedToolName = swap.MultiTools.MountedToolName ?? "";
            else if (swap.FirstTool is { Name: var mountName })
                vm.MountedToolName = mountName;

            KrlToolChangeSequenceParser.KrcRootOverride = swap.Config.KrcRoot;
            vm.RaiseToolChangeCommandsCanExecuteChanged();

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

            var bd = swap.Config.Bed.BaseData;
            vm.Robot.SetBaseFrameData(bd.X, bd.Y, bd.Z);

            vm.OnCellSwapCompleted?.Invoke();
        });
    }

    private static void UploadPendingMeshes(SceneNode root)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } data) continue;
            n.Mesh        = GpuMeshCache.Acquire(data);
            n.PendingMesh = null;
        }
    }

    private static void UploadVisiblePendingMeshes(SceneNode root)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } data) continue;
            if (!IsInVisibleSubtree(n)) continue;
            n.Mesh        = GpuMeshCache.Acquire(data);
            n.PendingMesh = null;
        }
    }

    private void EnqueueCellGpuUpload(SceneNode? root)
    {
        if (root is null) return;
        if (root.Parent is null)
            _renderer.SceneRoot.AddChild(root);
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is null) continue;
            if (!IsInVisibleSubtree(n)) continue;
            _cellGpuUploadQueue.Enqueue(n);
        }
    }

    private static bool HasPendingVisibleMesh(SceneNode root)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is null) continue;
            if (IsInVisibleSubtree(n)) return true;
        }
        return false;
    }

    /// <returns>True when more uploads remain.</returns>
    private bool ProcessCellGpuUploadQueue()
    {
        if (_cellGpuUploadQueue.Count == 0)
        {
            if (_cellGpuUploadPending)
            {
                _cellGpuUploadPending = false;
                System.Console.WriteLine("[cell] GPU upload complete");
            }
            return false;
        }

        int uploaded = 0;
        while (_cellGpuUploadQueue.Count > 0 && uploaded < MaxCellGpuUploadsPerFrame)
        {
            var n = _cellGpuUploadQueue.Dequeue();
            if (n.PendingMesh is not { } data) continue;
            n.Mesh        = GpuMeshCache.Acquire(data);
            n.PendingMesh = null;
            uploaded++;
        }

        _cellGpuUploadPending = _cellGpuUploadQueue.Count > 0;
        return _cellGpuUploadPending;
    }

    private static bool IsInVisibleSubtree(SceneNode node)
    {
        for (var cur = node; cur is not null; cur = cur.Parent)
            if (!cur.Visible) return false;
        return true;
    }

    private void OnToolSwapRequested(ToolCellConfig config)
    {
        if (DataContext is not ViewportViewModel vm) return;
        if (_multiTools is not null)
        {
            vm.PendingToolSwap.Enqueue((config, null!));
            vm.NotifyRenderNeeded();
            return;
        }
        Task.Run(() =>
        {
            try
            {
                var node = LoadToolNode(config);
                if (node is null) return;
                vm.PendingToolSwap.Enqueue((config, node));
                vm.NotifyRenderNeeded();
            }
            catch { /* silently skip on load failure */ }
        });
    }

    private static SceneNode? LoadToolNode(ToolCellConfig tool)
    {
        if (!AssetPaths.Exists(tool.ModelPath)) return null;

        bool isGlb = tool.ModelPath.EndsWith(".glb",  StringComparison.OrdinalIgnoreCase)
                  || tool.ModelPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

        if (isGlb)
        {
            var toolRoot = GltfLoader.Load(AssetPaths.Resolve(tool.ModelPath));
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

        var stlNode = StlLoader.Load(AssetPaths.Resolve(tool.ModelPath), "Tool");
        var stlHolder = new SceneNode
        {
            Name           = "Tool",
            LocalTransform = Matrix4.CreateScale(1f / 1000f)
                           * Matrix4.CreateRotationX(-MathF.PI / 2f)
                           * Matrix4.CreateRotationY(MathF.PI / 2f),
            Selectable     = false,
        };
        stlHolder.AddChild(stlNode);
        return stlHolder;
    }

    // -- Navigation helpers ----------------------------------------------------

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
        NavigationPresetId.Rhino        => btn == AvaBtn.Right && !mods.HasFlag(KeyModifiers.Shift),
        NavigationPresetId.Plasticity  => btn == AvaBtn.Right,
        NavigationPresetId.Blender    => btn == AvaBtn.Middle && !mods.HasFlag(KeyModifiers.Shift),
        NavigationPresetId.Maya       => btn == AvaBtn.Left   && mods.HasFlag(KeyModifiers.Alt),
        NavigationPresetId.Mol3D      => btn == AvaBtn.Left,
        NavigationPresetId.Max3ds     => btn == AvaBtn.Middle && mods.HasFlag(KeyModifiers.Alt),
        NavigationPresetId.Fusion360  => btn == AvaBtn.Middle && mods.HasFlag(KeyModifiers.Shift),
        _                             => btn == AvaBtn.Right,
    };

    private bool IsPanButton(AvaBtn btn, KeyModifiers mods) => ActivePreset switch
    {
        NavigationPresetId.Rhino        => btn == AvaBtn.Right && mods.HasFlag(KeyModifiers.Shift),
        NavigationPresetId.Plasticity or
        NavigationPresetId.Mol3D      => btn == AvaBtn.Middle,
        NavigationPresetId.Blender    => btn == AvaBtn.Middle && mods.HasFlag(KeyModifiers.Shift),
        NavigationPresetId.Maya       => btn == AvaBtn.Middle && mods.HasFlag(KeyModifiers.Alt),
        NavigationPresetId.Max3ds     => btn == AvaBtn.Middle && !mods.HasFlag(KeyModifiers.Alt),
        NavigationPresetId.Fusion360  => btn == AvaBtn.Middle && !mods.HasFlag(KeyModifiers.Shift),
        _                             => btn == AvaBtn.Middle,
    };

    // -- Pointer input ---------------------------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        this.Focus();
        var pt   = e.GetCurrentPoint(this);
        var pos  = pt.Position;
        var mods = e.KeyModifiers;
        var kind = pt.Properties.PointerUpdateKind;
        var btn  = ToButton(kind);
        _lastMousePos = pos;

        // Keyboard transform is active -- right click cancels, everything else is suppressed
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
                GlCanvas.InteractionRenderScale = InteractionScale;
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
            }
            else if (_panButton is null && IsPanButton(btn.Value, mods))
            {
                _isPanning  = true;
                _panButton  = btn;
                GlCanvas.InteractionRenderScale = InteractionScale;
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

        if (_seamGuideDragging && DataContext is ViewportViewModel dragVm)
        {
            _leftDragged = true;
            float vpW = (float)GlCanvas.Bounds.Width;
            float vpH = (float)GlCanvas.Bounds.Height;
            var ray   = _renderer.Camera.GetPickRay((float)pos.X, (float)pos.Y, vpW, vpH);
            if (TryDragSeamGuide(ray, dragVm, _seamGuideDragIndex, out var hit))
            {
                dragVm.MoveSeamGuidePoint(_seamGuideDragIndex, SeamGuidePoint.FromVector3(hit));
                UpdateSeamGuideMarkers(dragVm);
            }
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

        if (kind == PointerUpdateKind.LeftButtonReleased && _seamGuideDragging)
        {
            _seamGuideDragging  = false;
            _seamGuideDragIndex = -1;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            _leftDragged = false;
            return;
        }

        if (kind == PointerUpdateKind.LeftButtonReleased)
        {
            if (_gizmoDragAxis != GizmoAxis.None)
            {
                if (_renderer.SelectedNode is { } gzNode && DataContext is ViewportViewModel vmGz)
                {
                    var op = _kbTransformActive ? _kbTransformOp : _renderer.GizmoMode;
                    RecordTransformUndo(vmGz, gzNode, _gizmoDragInitialLocal, gzNode.LocalTransform, TransformUndoLabel(op));
                }
                _toolIsDragging          = false;
                _gizmoDragAxis           = GizmoAxis.None;
                _renderer.ActiveDragAxis = GizmoAxis.None;
                _capturedPointer?.Capture(null);
                _capturedPointer = null;
                if (DataContext is ViewportViewModel vmGz2) SyncSelectionTransformDisplay(vmGz2);
                GlCanvas.RequestNextFrameRendering();
                RevalidateSelectedToolpath();
            }
            else if (!_leftDragged)
            {
                float vpW = (float)GlCanvas.Bounds.Width;
                float vpH = (float)GlCanvas.Bounds.Height;
                var ray   = _renderer.Camera.GetPickRay(
                    (float)_leftDownPos.X, (float)_leftDownPos.Y, vpW, vpH);

                if (DataContext is ViewportViewModel flatVm && flatVm.IsSeamEditorActive)
                {
                    int guideHit = _renderer.PickSeamGuide(
                        (float)_leftDownPos.X, (float)_leftDownPos.Y, vpW, vpH);
                    if (guideHit >= 0)
                    {
                        flatVm.SelectedSeamGuideIndex = guideHit;
                        flatVm.SeamEditorTool = SeamEditorToolKind.SelectPoint;
                        _seamGuideDragging   = true;
                        _seamGuideDragIndex  = guideHit;
                        _capturedPointer     = e.Pointer;
                        e.Pointer.Capture(this);
                        UpdateSeamGuideMarkers(flatVm);
                    }
                    else if (flatVm.SeamEditorTool == SeamEditorToolKind.AddPoint
                             && TryPlaceSeamGuide(ray, out var placeHit))
                    {
                        flatVm.AddSeamGuidePoint(SeamGuidePoint.FromVector3(placeHit));
                        UpdateSeamGuideMarkers(flatVm);
                    }
                    else if (flatVm.SeamEditorTool == SeamEditorToolKind.SelectPoint)
                    {
                        flatVm.SelectedSeamGuideIndex = -1;
                        UpdateSeamGuideMarkers(flatVm);
                    }
                }
                else if (DataContext is ViewportViewModel flatVm2 && flatVm2.IsLayFlatMode)
                {
                    var (node, normal, _) = _renderer.PickFace(ray);
                    if (node is not null)
                    {
                        ApplyLayFlat(node, normal, _renderer.BedZ);
                        _renderer.Select(node);
                        UpdateFocusOverlay();
                    }
                    flatVm2.IsLayFlatMode = false;
                }
                else if (DataContext is ViewportViewModel seqVm
                         && seqVm.IsDevMode
                         && TryPickSequenceWaypoint((float)_leftDownPos.X, (float)_leftDownPos.Y, vpW, vpH))
                {
                    GlCanvas.RequestNextFrameRendering();
                }
                else
                {
                    float vpW2 = (float)GlCanvas.Bounds.Width;
                    float vpH2 = (float)GlCanvas.Bounds.Height;
                    var toolpathHit = _renderer.PickToolpath((float)_leftDownPos.X, (float)_leftDownPos.Y, vpW2, vpH2);
                    var picked = toolpathHit ?? _renderer.Pick(ray);
                    var shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    if (shiftHeld && picked is not null && _renderer.IsToolpathNode(picked))
                        _renderer.ToggleToolpathSelection(picked);
                    else
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
            if (GlCanvas.InteractionRenderScale < 1f)
            {
                GlCanvas.InteractionRenderScale = 1f;
                GlCanvas.RequestNextFrameRendering();
            }
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
                SetGizmoMode(GizmoMode.None);
                StartKbTransform(GizmoMode.Translate);
                e.Handled = true; break;
            case Key.R:
                if (!IsToolNodeSelected())
                {
                    SetGizmoMode(GizmoMode.None);
                    StartKbTransform(GizmoMode.Rotate);
                }
                e.Handled = true; break;
            case Key.S:
                if (!IsToolNodeSelected())
                {
                    SetGizmoMode(GizmoMode.None);
                    StartKbTransform(GizmoMode.Scale);
                }
                e.Handled = true; break;
            case Key.Delete: DeleteSelectedNode();                     e.Handled = true; break;
            case Key.Space:
                if (DataContext is ViewportViewModel spaceVm && spaceVm.IsToolpathSelected)
                {
                    spaceVm.TogglePlaybackCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                if (DataContext is ViewportViewModel escVm && escVm.IsLayFlatMode)
                {
                    escVm.IsLayFlatMode = false;
                    e.Handled = true;
                }
                break;
            case Key.N:
                if (DataContext is ViewportViewModel hudVm)
                {
                    hudVm.ToggleSyncHud();
                    e.Handled = true;
                }
                break;
        }
    }

    private void AddMultiToolVisualsToScene(CellEnvironmentBuilder.CellMultiToolSet mt, SceneNode? flange)
    {
        foreach (var pair in mt.Tools.Values)
        {
            pair.FlangeHolder.Selectable = false;
            if (flange is not null)
                flange.AddChild(pair.FlangeHolder);

            if (pair.DockHolder is { } dock)
            {
                dock.Selectable = false;
                if (dock.Parent is null)
                    _renderer.SceneRoot.AddChild(dock);
                if (dock.Visible)
                    EnqueueCellGpuUpload(dock);
            }
        }
    }

    /// <summary>LFAM 3: all toolheads parked on docks; flange empty until a Pick simulation or manual mount.</summary>
    void ApplyInitialMultiToolState(ViewportViewModel vm) => ApplyMultiToolUnmount(vm, updateVm: false);

    private void ApplyMultiToolMount(ToolCellConfig tool, ViewportViewModel vm)
    {
        if (_multiTools is null) return;

        _multiTools.MountedToolName = tool.Name;
        foreach (var (name, pair) in _multiTools.Tools)
        {
            bool mounted = name == tool.Name;
            pair.FlangeHolder.Visible = mounted;
            if (pair.DockHolder is { } dock)
                dock.Visible = !mounted;

            if (mounted)
                EnqueueCellGpuUpload(pair.FlangeHolder);
            else if (pair.DockHolder is { } d)
                EnqueueCellGpuUpload(d);
        }

        _cellGpuUploadPending = _cellGpuUploadQueue.Count > 0 || _cellGpuUploadPending;

        _tcpOffsetLocal    = new Vector3(tool.TcpX, tool.TcpY, tool.TcpZ);
        _tcpOrientationABC = new Vector3(tool.TcpA, tool.TcpB, tool.TcpC);
        _sensorOriginLocal = tool.HasSensorOrigin
            ? new Vector3(tool.SensorOriginX!.Value, tool.SensorOriginY!.Value, tool.SensorOriginZ!.Value)
            : null;
        _toolFrameRoll = tool.ToolFrameRoll * MathF.PI / 180f;

        _toolCorrectionMatrix = Matrix4.CreateRotationY(MathF.PI / 2f);
        RebuildFrameMatrices();

        if (_multiTools.Tools.TryGetValue(tool.Name, out var active))
        {
            active.FlangeHolder.LocalTransform = _toolMeshMatrix;
            _currentToolNode = active.FlangeHolder;
        }

        RebuildIkSolver(vm);
        if (vm.Robot is not null)
            SyncTcpReadout(vm);
        PostMultiToolVmState(vm, tool.Name);
        Dispatcher.UIThread.Post(vm.NotifyCellChanged);
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

    // -- Drag and drop ---------------------------------------------------------

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

        bool place = true;

        foreach (var file in files)
        {
            var node = ImportHelper.LoadAndPlace(file, place ? vm.ActiveCell : null);
            if (node is not null) vm.AddUserNode(node);
        }
    }

    // -- Slice -----------------------------------------------------------------

    private (OutlinerItemViewModel parent, OutlinerItemViewModel toolpathItem)? FindResliceSource(ViewportViewModel vm)
    {
        if (_renderer.SelectedNode is not { } selected) return null;
        if (!_renderer.IsToolpathNode(selected)) return null;

        foreach (var item in vm.OutlinerItems)
        {
            foreach (var child in item.Children)
            {
                if (child.Node != selected) continue;
                if (!CollectMeshSnapshots(item, requireVisible: false).Any()) return null;
                return (item, child);
            }
        }
        return null;
    }

    private static List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)> CollectMeshSnapshots(
        OutlinerItemViewModel item, bool requireVisible)
    {
        if (requireVisible && !item.Visible) return [];
        var meshSnapshots = new List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)>();
        foreach (var node in item.Node.SelfAndDescendants())
        {
            if (node.Mesh?.PickingData is not { } md) continue;
            meshSnapshots.Add((md.Positions, md.Indices, node.WorldTransform));
        }
        return meshSnapshots;
    }

    private static SliceSettings BuildSliceSettings(AdditiveSettingsViewModel? additive)
    {
        if (additive is not { } s) return new SliceSettings();
        var slicingMode = s.SlicingMode == "Surface" ? SlicingMode.Surface : SlicingMode.Normal;
        return new SliceSettings
        {
            SlicingMode      = slicingMode,
            LayerHeight      = (float)s.LayerHeight,
            FirstLayerHeight = (float)s.FirstLayerHeight,
            BeadWidth        = (float)s.BeadWidth,
            PrintSpeedMps    = (float)(s.PrintSpeed / 1000.0),
            TravelSpeed      = (float)(s.TravelSpeed / 1000.0),
            ApproachZ        = (float)s.ApproachZ,
            TiltAngle        = (float)s.TiltAngle,
            TiltAngleX       = (float)s.TiltAngleX,
            DisableContourOffset   = s.DisableContourOffset,
            ZigZagSeam             = s.SeamMode == "Zig-zag",
            WaveEffect    = s.WaveEffect switch
            {
                "Sine"     => WaveEffectType.Sine,
                "Sawtooth" => WaveEffectType.Sawtooth,
                "Triangle" => WaveEffectType.Triangle,
                _          => WaveEffectType.None,
            },
            WaveAmplitude  = (float)s.WaveAmplitude,
            WaveWavelength = (float)s.WaveWavelength,
            WaveGradient         = s.WaveGradient,
            WaveAmplitudeBottom  = (float)s.WaveAmplitudeBottom,
            WaveAmplitudeTop     = (float)s.WaveAmplitudeTop,
            WaveWavelengthBottom = (float)s.WaveWavelengthBottom,
            WaveWavelengthTop    = (float)s.WaveWavelengthTop,
            WaveGradientCenter   = (float)s.WaveGradientCenter,
            WaveGradientCurve    = s.WaveGradientCurve switch
            {
                "Smooth"   => WaveGradientCurveType.Smooth,
                "Ease In"  => WaveGradientCurveType.EaseIn,
                "Ease Out" => WaveGradientCurveType.EaseOut,
                _          => WaveGradientCurveType.Linear,
            },
            WaveCycles     = s.WaveFrequencyMode == "Cycles" ? s.WaveCycles : 0,
            WaveShape      = (float)s.WaveShape,
            WaveStagger    = (float)s.WaveStagger,
            AdaptiveLayerHeight = s.AdaptiveLayerHeight,
            AdaptiveQuality     = (float)s.AdaptiveQuality,
            MinLayerHeight      = (float)s.MinLayerHeight,
            OverhangOrientation = s.OverhangOrientation,
            MaxOverhangTiltDeg  = (float)s.MaxOverhangTiltDeg,
            SmoothRotation                = s.SmoothRotation,
            SmoothRotationRadius          = s.SmoothRotationRadius,
            SmoothRotationMaxRateDegPerMm = (float)s.SmoothRotationMaxRateDegPerMm,
            InfillPattern = s.InfillPattern switch
            {
                "Rectilinear"     => InfillPattern.Rectilinear,
                "Grid"            => InfillPattern.Grid,
                "Triangle"        => InfillPattern.Triangle,
                "Ghost Mesh Grid" => InfillPattern.GhostMeshGrid,
                _                 => InfillPattern.None,
            },
            InfillSpacingMm = (float)s.InfillSpacingMm,
            InfillAngleDeg  = (float)s.InfillAngleDeg,
            ZHopMm          = (float)s.ZHopMm,
            WipeMode        = s.WipeModeDisplay switch
            {
                "Retrace"        => WipeMode.Retrace,
                "Same-Direction" => WipeMode.SameDirection,
                "Natural" or "Normal" => WipeMode.SameDirection,
                _                => WipeMode.None,
            },
            WipeLengthMm = (float)s.WipeLengthMm,
            WipeRampMm   = (float)s.WipeRampMm,
            WipeSpeed    = (float)(s.WipeSpeed / 1000.0),
            FlowRate     = (float)(s.SelectedPreset?.FlowRate ?? 0.463),
            ResumeRampEnabled          = s.ResumeRampEnabled,
            ResumeRampStartSpeedMps    = (float)(s.ResumeRampStartSpeed / 1000.0),
            ResumeRampStartRpmPercent  = (float)s.ResumeRampStartRpmPercent,
            ResumeRampDistanceMm       = (float)s.ResumeRampDistanceMm,
            ResumeRampSteps            = s.ResumeRampSteps,
            SeamGuidePoints = s.BuildSeamGuideList(),
        };
    }

    private static async Task<(Toolpath smoothed, Toolpath raw, SliceSettings settings)> ComputeToolpathAsync(
        List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)> meshSnapshots,
        SliceMethod method,
        SliceSettings settings)
    {
        var toolpath = await Task.Run(() =>
        {
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

            Toolpath tp;
            if (method == SliceMethod.Angled)        tp = AngledPlanarSlicer.Slice(flatMeshes, settings);
            else if (method == SliceMethod.Geodesic) tp = GeodesicSlicer.Slice(flatMeshes, settings);
            else                                     tp = PlanarSlicer.Slice(flatMeshes, settings);
            tp = WaveEffect.Apply(tp, settings);
            tp = MovementPostProcessor.Apply(tp, settings);
            return ResumeRampPostProcessor.Apply(tp, settings);
        });

        var rawToolpath      = toolpath;
        var smoothedToolpath = OrientationSmoother.Apply(rawToolpath, settings);
        return (smoothedToolpath, rawToolpath, settings);
    }

    private ToolpathSnapshot? GetToolpathSnapshot(SceneNode node)
    {
        if (!_toolpathByNode.TryGetValue(node, out var smoothed)) return null;
        _rawToolpathByNode.TryGetValue(node, out var raw);
        raw ??= smoothed;
        _toolpathMetaByNode.TryGetValue(node, out var meta);
        return new ToolpathSnapshot(
            smoothed,
            raw,
            meta.BeadWidth > 0 ? meta.BeadWidth : 6f,
            meta.LayerHeight > 0 ? meta.LayerHeight : 3f,
            meta.MaterialColor);
    }

    private void StageToolpathMaps(PendingToolpathEntry entry)
    {
        _toolpathByNode[entry.Node]     = entry.Toolpath;
        _rawToolpathByNode[entry.Node]  = entry.RawToolpath;
        _toolpathMetaByNode[entry.Node] = (entry.BeadWidth, entry.LayerHeight, entry.MaterialColor);
        _scrubCacheByNode[entry.Node]   = BuildScrubCache(entry.Toolpath);
    }

    private void UploadToolpathEntry(PendingToolpathEntry entry, bool addToScene)
    {
        StageToolpathMaps(entry);
        if (addToScene)
            _renderer.AddToolpath(entry.Toolpath, entry.Node, entry.BeadWidth, entry.LayerHeight, entry.MaterialColor);
        else
            _renderer.ReplaceToolpath(entry.Toolpath, entry.Node, entry.BeadWidth, entry.LayerHeight, entry.MaterialColor);

        var centroidLocal = entry.Node.LocalTransform;

        if (entry.PreserveRelativePose
            && entry.PreservedLocalTransform is Matrix4 preservedLocal
            && entry.PreservedOrigin is NVec3 preservedOrigin)
        {
            var oldOriginT = Matrix4.CreateTranslation(preservedOrigin.X, preservedOrigin.Y, preservedOrigin.Z);
            Matrix4.Invert(oldOriginT, out var invOldOrigin);
            entry.Node.LocalTransform = preservedLocal * invOldOrigin * centroidLocal;
        }
        else if (entry.LocalTransformOverride is Matrix4 lt)
        {
            entry.Node.LocalTransform = lt;
        }

        var overhang = ComputeOverhangPerFlatMove(entry.Toolpath, entry.BeadWidth);
        _renderer.UpdateToolpathBeadOverhang(entry.Node, overhang);
        var orientationRates = ComputeOrientationRatePerFlatMove(entry.Toolpath);
        _renderer.UpdateToolpathBeadOrientation(entry.Node, orientationRates);

        // Scrub/IK un-localise against the geometry centroid, not the user translation component.
        var originRow = entry.PreserveRelativePose || entry.LocalTransformOverride is null
            ? centroidLocal.Row3
            : entry.Node.LocalTransform.Row3;
        _toolpathOriginByNode[entry.Node] = new NVec3(originRow.X, originRow.Y, originRow.Z);
    }

    private void ApplyToolpathStats(ViewportViewModel vm, Toolpath smoothedToolpath)
    {
        if (vm.AdditiveSettings is not { } as2) return;
        var (t, w, c) = ComputeToolpathStats(smoothedToolpath, as2);
        vm.StatsTime        = t;
        vm.StatsWeight      = w;
        vm.StatsCost        = c;
        vm.HasToolpathStats = true;
    }

    private async Task RunSliceAsync(ViewportViewModel vm)
    {
        if (vm.IsSlicing || vm.OutlinerItems.Count == 0) return;
        vm.IsSlicing = true;

        try
        {
            var selectedNode  = _renderer.SelectedNode;
            var meshSnapshots = new List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)>();
            var sourceItems   = new List<OutlinerItemViewModel>();
            foreach (var item in vm.OutlinerItems)
            {
                if (selectedNode is null || !item.Node.SelfAndDescendants().Any(n => n == selectedNode)) continue;
                var snaps = CollectMeshSnapshots(item, requireVisible: true);
                if (snaps.Count == 0) continue;
                meshSnapshots.AddRange(snaps);
                sourceItems.Add(item);
            }
            if (meshSnapshots.Count == 0) return;

            var method   = vm.AdditiveSettings?.Method ?? SliceMethod.Planar;
            var settings = BuildSliceSettings(vm.AdditiveSettings);
            var (smoothedToolpath, rawToolpath, _) = await ComputeToolpathAsync(meshSnapshots, method, settings);

            var parentItem   = sourceItems.Count == 1 ? sourceItems[0] : null;
            var toolpathName = method switch
            {
                SliceMethod.Angled => $"Toolpath {settings.TiltAngle:0.##}deg W{settings.BeadWidth:0.##}mm H{settings.LayerHeight:0.##}mm",
                _                  => $"Toolpath W{settings.BeadWidth:0.##}mm H{settings.LayerHeight:0.##}mm",
            };
            var toolpathNode = new SceneNode { Name = toolpathName, Selectable = true };
            vm.RegisterToolpathInOutliner(toolpathNode, parentItem);
            var selectedPreset = vm.AdditiveSettings is { } asp
                && asp.SelectedPresetIndex >= 0
                && asp.SelectedPresetIndex < asp.MaterialPresets.Count
                ? asp.MaterialPresets[asp.SelectedPresetIndex] : null;
            vm.PendingToolpath.Enqueue(new PendingToolpathEntry
            {
                Toolpath      = smoothedToolpath,
                RawToolpath   = rawToolpath,
                Node          = toolpathNode,
                BeadWidth     = (float)(vm.AdditiveSettings?.BeadWidth  ?? 6.0),
                LayerHeight   = (float)(vm.AdditiveSettings?.LayerHeight ?? 3.0),
                MaterialColor = MapMaterialColor(selectedPreset?.Color),
            });

            ApplyToolpathStats(vm, smoothedToolpath);

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

    private async Task RunUpdateSliceAsync(ViewportViewModel vm)
    {
        if (vm.IsSlicing) return;
        if (FindResliceSource(vm) is not { } source) return;

        vm.IsSlicing = true;
        try
        {
            var (parentItem, toolpathItem) = source;
            var toolpathNode = toolpathItem.Node;
            var meshSnapshots = CollectMeshSnapshots(parentItem, requireVisible: false);
            if (meshSnapshots.Count == 0) return;

            var method   = vm.AdditiveSettings?.Method ?? SliceMethod.Planar;
            var settings = BuildSliceSettings(vm.AdditiveSettings);
            var (smoothedToolpath, rawToolpath, _) = await ComputeToolpathAsync(meshSnapshots, method, settings);

            toolpathNode.Name = method switch
            {
                SliceMethod.Angled => $"Toolpath {settings.TiltAngle:0.##}deg W{settings.BeadWidth:0.##}mm H{settings.LayerHeight:0.##}mm",
                _                  => $"Toolpath W{settings.BeadWidth:0.##}mm H{settings.LayerHeight:0.##}mm",
            };

            var selectedPreset = vm.AdditiveSettings is { } asp
                && asp.SelectedPresetIndex >= 0
                && asp.SelectedPresetIndex < asp.MaterialPresets.Count
                ? asp.MaterialPresets[asp.SelectedPresetIndex] : null;

            _validationCts?.Cancel();
            _validationDone = false;

            var preservedLocal = toolpathNode.LocalTransform;
            if (!_toolpathOriginByNode.TryGetValue(toolpathNode, out var preservedOrigin))
            {
                preservedOrigin = new NVec3(
                    preservedLocal.M41, preservedLocal.M42, preservedLocal.M43);
            }

            vm.PendingToolpathReplace.Enqueue(new PendingToolpathEntry
            {
                Toolpath               = smoothedToolpath,
                RawToolpath            = rawToolpath,
                Node                   = toolpathNode,
                BeadWidth              = (float)(vm.AdditiveSettings?.BeadWidth  ?? 6.0),
                LayerHeight            = (float)(vm.AdditiveSettings?.LayerHeight ?? 3.0),
                MaterialColor          = MapMaterialColor(selectedPreset?.Color),
                PreserveRelativePose   = true,
                PreservedLocalTransform = preservedLocal,
                PreservedOrigin        = preservedOrigin,
            });

            ApplyToolpathStats(vm, smoothedToolpath);
            vm.ResetScrubIndex(smoothedToolpath.Layers.Sum(l => l.Moves.Count), smoothedToolpath);
            GlCanvas.RequestNextFrameRendering();
        }
        finally
        {
            vm.IsSlicing = false;
        }
    }

    // -- Layer preview ---------------------------------------------------------

    private async Task ComputeLayerPreviewAsync(ViewportViewModel vm)
    {
        if (vm.AdditiveSettings is not { ShowLayerPreview: true } s) return;

        // Snapshot mesh data on the UI thread.
        var meshSnapshots = new List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)>();
        foreach (var item in vm.OutlinerItems)
        {
            if (!item.Visible || _renderer.IsToolpathNode(item.Node)) continue;
            foreach (var node in item.Node.SelfAndDescendants())
            {
                if (node.Mesh?.PickingData is not { } md) continue;
                meshSnapshots.Add((md.Positions, md.Indices, node.WorldTransform));
            }
        }
        if (meshSnapshots.Count == 0) return;

        float layerH   = (float)s.LayerHeight;
        float firstH   = (float)s.FirstLayerHeight;
        float minH     = (float)s.MinLayerHeight;
        float quality  = (float)s.AdaptiveQuality;
        bool  adaptive = s.AdaptiveLayerHeight && s.ShowAdaptiveLayerHeight;

        var result = await Task.Run(() =>
        {
            var flatMeshes = new List<NVec3[]>(meshSnapshots.Count);
            float zMin = float.MaxValue, zMax = float.MinValue;

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
                foreach (var v in flat) { zMin = MathF.Min(zMin, v.Z); zMax = MathF.Max(zMax, v.Z); }
                flatMeshes.Add(flat);
            }

            if (zMax <= zMin + 1e-4f) return ((float[])[], (float[])[]);

            float[] zPositions = adaptive
                ? AdaptiveLayerHeights.ComputeZPositions(flatMeshes, zMin, zMax, firstH, minH, layerH, quality)
                : BuildUniformZPositions(zMin, zMax, firstH, layerH);

            if (zPositions.Length == 0) return ((float[])[], (float[])[]);

            var bounds  = new float[zPositions.Length + 1];
            var heights = new float[zPositions.Length];
            bounds[0] = zMin;
            for (int i = 0; i < zPositions.Length; i++)
            {
                bounds[i + 1] = zPositions[i];
                heights[i]    = zPositions[i] - (i == 0 ? zMin : zPositions[i - 1]);
            }
            return (bounds, heights);
        });

        if (result.Item1.Length >= 2)
        {
            vm.PendingLayerPreview.Enqueue(result);
            GlCanvas.RequestNextFrameRendering();
        }
    }

    private static float[] BuildUniformZPositions(float zMin, float zMax, float firstH, float layerH)
    {
        var list = new List<float>();
        float z  = zMin + firstH;
        while (z < zMax - 1e-4f) { list.Add(z); z += layerH; }
        return [.. list];
    }

    private static NVec3 MapMaterialColor(string? name) => name switch
    {
        "White"   => new(0.95f, 0.95f, 0.95f),
        "Gray"    => new(0.60f, 0.60f, 0.60f),
        "Clear"   => new(0.80f, 0.88f, 0.95f),
        "Red"     => new(0.85f, 0.15f, 0.15f),
        "Blue"    => new(0.15f, 0.35f, 0.85f),
        "Green"   => new(0.15f, 0.70f, 0.25f),
        "Yellow"  => new(0.95f, 0.85f, 0.10f),
        "Orange"  => new(0.95f, 0.45f, 0.10f),
        "Natural" => new(0.92f, 0.88f, 0.75f),
        "Black"   => new(0.15f, 0.15f, 0.15f),
        _         => new(0.15f, 0.35f, 0.85f),  // Other / no preset → blue
    };

    private static (string time, string weight, string cost) ComputeToolpathStats(
        Toolpath toolpath, AdditiveSettingsViewModel s)
    {
        double printMmS   = s.PrintSpeed;
        double travelMmS  = s.TravelSpeed;
        double wipeMmS    = s.WipeSpeed;
        double beadW      = s.BeadWidth;
        double layerH     = s.LayerHeight;

        var preset = s.SelectedPresetIndex >= 0 && s.SelectedPresetIndex < s.MaterialPresets.Count
            ? s.MaterialPresets[s.SelectedPresetIndex] : null;

        double densityGCm3 = preset?.MaterialDensity ?? 1.05;
        double costPerLb   = preset?.CostPerLb       ?? 0.0;

        double timeSecs = 0.0, volMm3 = 0.0;
        foreach (var layer in toolpath.Layers)
            foreach (var move in layer.Moves)
            {
                double dist = NVec3.Distance(move.From, move.To);
                if (move.IsWipe)                   { timeSecs += dist / wipeMmS; }
                else if (move.Kind == MoveKind.Extrude) { timeSecs += dist / printMmS;  volMm3 += dist * beadW * layerH; }
                else                               { timeSecs += dist / travelMmS; }
            }

        double massLbs = volMm3 / 1000.0 * densityGCm3 / 453.592;
        double cost    = massLbs * costPerLb;

        var ts      = TimeSpan.FromSeconds(timeSecs);
        string time = ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s"
            : $"{ts.Minutes}m {ts.Seconds:D2}s";

        return (time, $"{massLbs:F3} lbs", preset is not null ? $"${cost:F2}" : "--");
    }

    private static NVec3 TransformPoint(TkVector3 p, TkMatrix4 m)
    {
        // OpenTK row-vector: world = local * M
        float x = p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41;
        float y = p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42;
        float z = p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43;
        return new NVec3(x, y, z);
    }

    // -- Lay Flat / Drop to Plate ----------------------------------------------

    private void DropToPlate()
    {
        if (_renderer.SelectedNode is not { } node) return;
        float minZ = LayFlatMinZ(node);
        if (minZ >= float.MaxValue) return;
        node.LocalTransform = node.LocalTransform
            * TkMatrix4.CreateTranslation(0f, 0f, _renderer.BedZ - minZ);
        GlCanvas.RequestNextFrameRendering();
    }

    private static MeshData CloneMeshData(MeshData mesh) =>
        new(mesh.Positions, mesh.Normals, mesh.Indices, mesh.Name,
            mesh.BaseColor, mesh.Metallic, mesh.Roughness,
            mesh.Uvs, mesh.Tangents, mesh.Material);

    private static bool HasExplodableMeshes(SceneNode root)
    {
        foreach (var node in root.SelfAndDescendants())
        {
            if (node.Mesh?.PickingData is { } mesh && MeshConnectedComponents.HasMultipleComponents(mesh))
                return true;
        }
        return false;
    }

    private void UngroupSelected()
    {
        if (_renderer.SelectedNode is not { } root) return;
        if (DataContext is not ViewportViewModel vm) return;
        if (root.Children.Count == 0) return;

        var outlinerItem = vm.FindOutlinerItem(root);
        var promoted = new List<SceneNode>();

        foreach (var child in root.Children.ToList())
        {
            root.RemoveChild(child);
            child.LocalTransform  = child.WorldTransform;
            child.SourceFilePath ??= root.SourceFilePath;
            promoted.Add(child);
        }

        if (root.Mesh is not null)
        {
            var meshNode = new SceneNode
            {
                Name           = root.Name,
                LocalTransform = root.WorldTransform,
                Mesh           = root.Mesh,
                Selectable     = root.Selectable,
                CullFaces      = root.CullFaces,
                Visible        = root.Visible,
                LayerPreview   = root.LayerPreview,
                SourceFilePath = root.SourceFilePath,
            };
            root.Mesh = null;
            promoted.Insert(0, meshNode);
        }

        if (promoted.Count == 0 || outlinerItem is null) return;

        vm.OutlinerItems.Remove(outlinerItem);
        foreach (var child in outlinerItem.Children)
            vm.PendingRemoveNodes.Enqueue(child.Node);
        vm.PendingRemoveNodes.Enqueue(root);

        for (int i = 0; i < promoted.Count; i++)
            vm.AttachUserNode(promoted[i], i == 0 ? outlinerItem : null);

        _renderer.Select(promoted[0]);
        UpdateFocusOverlay();
        GlCanvas.RequestNextFrameRendering();
    }

    private void ExplodeSelected()
    {
        if (_renderer.SelectedNode is not { } root) return;
        if (DataContext is not ViewportViewModel vm) return;
        if (!HasExplodableMeshes(root)) return;

        var outlinerItem = vm.FindOutlinerItem(root);
        var newNodes = new List<SceneNode>();

        foreach (var meshNode in root.SelfAndDescendants())
        {
            if (meshNode.Mesh?.PickingData is not { } mesh) continue;

            var parts = MeshConnectedComponents.Split(mesh);
            var world = meshNode.WorldTransform;

            if (parts.Count <= 1)
            {
                newNodes.Add(new SceneNode
                {
                    Name           = mesh.Name,
                    PendingMesh    = CloneMeshData(mesh),
                    LocalTransform = world,
                    Selectable     = root.Selectable,
                    CullFaces      = root.CullFaces,
                    Visible        = root.Visible,
                    LayerPreview   = root.LayerPreview,
                    SourceFilePath = root.SourceFilePath,
                });
                continue;
            }

            foreach (var part in parts)
            {
                newNodes.Add(new SceneNode
                {
                    Name           = part.Name,
                    PendingMesh    = part,
                    LocalTransform = world,
                    Selectable     = root.Selectable,
                    CullFaces      = root.CullFaces,
                    Visible        = root.Visible,
                    LayerPreview   = root.LayerPreview,
                    SourceFilePath = root.SourceFilePath,
                });
            }
        }

        if (newNodes.Count <= 1 || outlinerItem is null) return;

        vm.OutlinerItems.Remove(outlinerItem);
        foreach (var child in outlinerItem.Children)
            vm.PendingRemoveNodes.Enqueue(child.Node);
        vm.PendingRemoveNodes.Enqueue(root);

        for (int i = 0; i < newNodes.Count; i++)
            vm.AttachUserNode(newNodes[i], i == 0 ? outlinerItem : null);

        _renderer.Select(newNodes[0]);
        UpdateFocusOverlay();
        GlCanvas.RequestNextFrameRendering();
    }

    private async Task MeshCleanupSelectedAsync()
    {
        if (_renderer.SelectedNode is not { } root) return;
        if (DataContext is not ViewportViewModel vm) return;
        if (!HasCleanableMeshes(root)) return;
        if (TopLevel.GetTopLevel(this) is not Window parent) return;

        var dialog = new MeshCleanupDialog
        {
            DataContext = new MeshCleanupDialogViewModel(),
        };
        var options = await dialog.ShowDialog<MeshCleanupOptions?>(parent);
        if (options is null) return;

        int meshCount = 0;
        int removedDegenerate = 0, removedDuplicate = 0, mergedVerts = 0, removedColinear = 0, insertedGaps = 0;

        foreach (var node in root.SelfAndDescendants())
        {
            if (node.Mesh?.PickingData is not { } mesh) continue;

            var result = MeshCleanup.Clean(mesh, options);
            GpuMeshCache.Release(node.Mesh);
            node.Mesh = GpuMeshCache.Acquire(result.Mesh);
            meshCount++;
            removedDegenerate += result.RemovedDegenerateTriangles;
            removedDuplicate  += result.RemovedDuplicateTriangles;
            mergedVerts       += result.MergedVertices;
            removedColinear   += result.RemovedColinearVertices;
            insertedGaps      += result.InsertedGapVertices;
        }

        if (meshCount == 0) return;

        var msg = $"[mesh] Cleanup on {meshCount} mesh(es): " +
                  $"{removedDegenerate} degenerate, {removedDuplicate} duplicate, " +
                  $"{mergedVerts} welded, {removedColinear} colinear, {insertedGaps} gap splits.";
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel mvm)
            mvm.Console.Log(msg);
        else
            System.Console.WriteLine(msg);

        GlCanvas.RequestNextFrameRendering();
    }

    private static bool HasCleanableMeshes(SceneNode root)
    {
        foreach (var node in root.SelfAndDescendants())
        {
            if (node.Mesh?.PickingData is not { } mesh) continue;
            int triCount = mesh.Indices is { } idx ? idx.Length / 3 : mesh.Positions.Length / 3;
            if (triCount > 0) return true;
        }
        return false;
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
                // 180deg -- flip around any axis perpendicular to the face normal.
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
        // Row-vector: p_new = p_old * M  ->  W_new = W_old * M
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

    // -- Toolhead selection check ----------------------------------------------

    private bool IsToolNodeSelected()
    {
        var sel = _renderer.SelectedNode;
        if (sel is null || _currentToolNode is null) return false;
        foreach (var n in _currentToolNode.SelfAndDescendants())
            if (n == sel) return true;
        return false;
    }

    // -- Gizmo mode switching --------------------------------------------------

    private void SetGizmoMode(GizmoMode mode)
    {
        if (IsToolNodeSelected() && mode != GizmoMode.Translate && mode != GizmoMode.None) return;
        _renderer.GizmoMode = mode;
        if (DataContext is ViewportViewModel vm)
            vm.ActiveGizmoModeInternal = mode;
        GlCanvas.RequestNextFrameRendering();
    }

    // -- Keyboard-initiated transform (Blender-style G/R/S + X/Y/Z) -----------

    private void StartKbTransform(GizmoMode op)
    {
        if (_renderer.SelectedNode is not { } node) return;

        _kbTransformActive       = true;
        _kbTransformOp           = op;
        _kbTransformAxis         = GizmoAxis.None;
        _kbTransformStartPos     = _lastMousePos;
        _kbTransformInitialLocal = node.LocalTransform;

        // Project the node's world position to screen so KbRotate can use atan2.
        float vpW0 = (float)GlCanvas.Bounds.Width;
        float vpH0 = (float)GlCanvas.Bounds.Height;
        if (vpW0 > 0 && vpH0 > 0)
        {
            float aspect0  = vpW0 / vpH0;
            var   vp0      = _renderer.Camera.GetViewMatrix() * _renderer.Camera.GetProjectionMatrix(aspect0);
            var   nodePos0 = node.WorldTransform.Row3.Xyz;
            var   clip0    = new Vector4(nodePos0, 1f) * vp0;
            _kbObjScreenCenter = clip0.W > 1e-5f
                ? new Vector2(
                    (clip0.X / clip0.W * 0.5f + 0.5f) * vpW0,
                    (1f - (clip0.Y / clip0.W * 0.5f + 0.5f)) * vpH0)
                : new Vector2(vpW0 * 0.5f, vpH0 * 0.5f);
        }
        else
        {
            _kbObjScreenCenter = Vector2.Zero;
        }

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
        if (_renderer.SelectedNode is { } node && DataContext is ViewportViewModel vmCb)
            RecordTransformUndo(vmCb, node, _kbTransformInitialLocal, node.LocalTransform, TransformUndoLabel(_kbTransformOp));

        _kbTransformActive       = false;
        _kbTransformAxis         = GizmoAxis.None;
        _gizmoDragAxis           = GizmoAxis.None;
        _renderer.ActiveDragAxis = GizmoAxis.None;
        _toolIsDragging          = false;
        if (DataContext is ViewportViewModel vmCb2) SyncSelectionTransformDisplay(vmCb2);
        GlCanvas.RequestNextFrameRendering();
        RevalidateSelectedToolpath();
    }

    /// <summary>
    /// Re-runs reachability validation if the selected toolpath's transform changed since
    /// the last completed validation. Called after gizmo-drag-end and keyboard transforms.
    /// </summary>
    private void RevalidateSelectedToolpath()
    {
        if (_activeScrubNode is not { } node) return;
        if (_toolpathByNode.TryGetValue(node, out var tp))
            ValidateToolpathAsync(node, tp);
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
                    // Axis-constrained: plane-intersection via existing gizmo drag logic --
                    // _gizmoDragInitialLocal was captured by StartGizmoDrag at SetKbTransformAxis time.
                    ProcessGizmoDrag(mx, my);
                else
                    KbTranslateViewPlane(node, mx, my, vpW, vpH);
                break;

            case GizmoMode.Rotate:
                KbRotate(node, mousePos);
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

    private void KbRotate(SceneNode node, Point mousePos)
    {
        var axisDir = _kbTransformAxis switch
        {
            GizmoAxis.X => Vector3.UnitX,
            GizmoAxis.Y => Vector3.UnitY,
            GizmoAxis.Z => Vector3.UnitZ,
            _           => Vector3.Normalize(_renderer.Camera.Eye - _renderer.Camera.Target),
        };

        // Compute rotation as the 2-D angle swept around the object's screen center.
        // This makes the object "track" the mouse regardless of which axis is constrained.
        var vStart = new Vector2((float)_kbTransformStartPos.X, (float)_kbTransformStartPos.Y)
                   - _kbObjScreenCenter;
        var vCurr  = new Vector2((float)mousePos.X, (float)mousePos.Y)
                   - _kbObjScreenCenter;

        float angle;
        if (vStart.LengthSquared < 4f || vCurr.LengthSquared < 4f)
        {
            // Too close to center -- fall back to pure horizontal drag.
            angle = (float)(mousePos.X - _kbTransformStartPos.X) * 0.01f;
        }
        else
        {
            // Negate Y to convert screen-space (Y-down) to math-space (Y-up) before atan2,
            // so the resulting angle follows the right-hand rule used by CreateFromAxisAngle.
            angle = MathF.Atan2(-vCurr.Y, vCurr.X) - MathF.Atan2(-vStart.Y, vStart.X);
            // Wrap to [-π, π] to avoid a sudden jump when crossing the ±180deg boundary.
            if (angle >  MathF.PI) angle -= MathF.Tau;
            if (angle < -MathF.PI) angle += MathF.Tau;
        }

        var rot = Matrix4.CreateFromAxisAngle(axisDir, angle);
        var lt  = _kbTransformInitialLocal;
        var p   = new Vector3(lt.M41, lt.M42, lt.M43);
        lt      = lt * rot;
        lt.M41  = p.X; lt.M42 = p.Y; lt.M43 = p.Z;
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

    private static string TransformUndoLabel(GizmoMode mode) => mode switch
    {
        GizmoMode.Translate => "Move",
        GizmoMode.Rotate    => "Rotate",
        GizmoMode.Scale     => "Scale",
        _                   => "Transform",
    };

    private void RememberCommittedTransform(SceneNode node)
    {
        _lastCommittedTransformNode = node;
        _lastCommittedTransform     = node.LocalTransform;
    }

    private void RecordTransformUndo(
        ViewportViewModel vm,
        SceneNode node,
        Matrix4 before,
        Matrix4 after,
        string description)
    {
        if (Matrix4Util.NearlyEquals(before, after)) return;
        vm.UndoRedo?.Push(new NodeTransformAction(
            node, before, after, description, () => OnTransformApplied(vm)));
        RememberCommittedTransform(node);
        if (DataContext is ViewportViewModel devVm && devVm.IsDevMode && IsDevNode(node))
            ScheduleDevTransformAutoSave(devVm, node);
    }

    private void OnTransformApplied(ViewportViewModel vm)
    {
        SyncSelectionTransformDisplay(vm);
        GlCanvas.RequestNextFrameRendering();
        RevalidateSelectedToolpath();
        if (_renderer.SelectedNode is { } node)
            RememberCommittedTransform(node);
    }

    private void RebuildDevNodeRegistry(CellSwapPayload swap)
    {
        _devNodeKinds.Clear();
        foreach (var stand in swap.Config.Stands)
        {
            var node = swap.EnvironmentNodes.FirstOrDefault(n => n.Name == stand.Name);
            if (node is not null)
                _devNodeKinds[node] = ("stand", stand.Id);
        }
        foreach (var env in swap.EnvironmentNodes)
        {
            if (env.Name == "RotaryBed")
                _devNodeKinds[env] = ("rotary", null);
        }
        if (_multiTools is not null)
        {
            foreach (var (toolName, pair) in _multiTools.Tools)
            {
                if (pair.DockHolder is { } dock)
                    _devNodeKinds[dock] = ("dock", toolName);
            }
        }
        if (_bedNode is not null)
            _devNodeKinds[_bedNode] = ("bed", null);
    }

    private void ApplyDevModeSelectability(bool enabled)
    {
        foreach (var node in _devNodeKinds.Keys)
            node.Selectable = enabled;

        if (!enabled && _renderer.SelectedNode is { } sel && IsDevNode(sel))
        {
            _renderer.Select(null);
            UpdateFocusOverlay();
        }
    }

    private bool IsDevNode(SceneNode? node)
        => node is not null && _devNodeKinds.ContainsKey(node);

    private string DevLabel(SceneNode node)
    {
        if (!_devNodeKinds.TryGetValue(node, out var meta)) return node.Name;
        return meta.Kind switch
        {
            "stand"  => $"Stand: {node.Name}",
            "rotary" => "Rotary bed",
            "dock"   => $"Dock: {meta.Id}",
            "bed"    => "Print bed",
            _        => node.Name,
        };
    }

    private static void DevLog(ViewportViewModel vm, string message)
    {
        System.Console.WriteLine(message);
        vm.OnDevLog?.Invoke(message);
    }

    private void SaveDevTransform(ViewportViewModel vm)
        => SaveDevTransforms(vm, _renderer.SelectedNode is { } n && _devNodeKinds.ContainsKey(n)
            ? [n]
            : [], reloadScene: false);

    private void SaveAllDevTransforms(ViewportViewModel vm)
        => SaveDevTransforms(vm, _devNodeKinds.Keys.ToList(), reloadScene: true);

    private void ScheduleDevTransformAutoSave(ViewportViewModel vm, SceneNode node)
    {
        _devAutoSaveDebounce?.Cancel();
        _devAutoSaveDebounce = new CancellationTokenSource();
        var token = _devAutoSaveDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, token);
                Dispatcher.UIThread.Post(() =>
                    SaveDevTransforms(vm, [node], reloadScene: false, quiet: true));
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    private void SaveDevTransforms(
        ViewportViewModel vm,
        IReadOnlyList<SceneNode> nodes,
        bool reloadScene,
        bool quiet = false)
    {
        if (nodes.Count == 0
            || vm.ActiveCellPath is not { } path
            || vm.ActiveCell is not { } cell)
            return;

        path = System.IO.Path.GetFullPath(path);
        int saved = 0;
        string? lastError = null;

        foreach (var node in nodes)
        {
            if (!_devNodeKinds.TryGetValue(node, out var meta)) continue;
            if (!CellDevTransformSaver.TrySave(path, cell, node, meta.Kind, meta.Id, out var error))
            {
                lastError = error ?? "unknown error";
                if (!quiet)
                    DevLog(vm, $"[dev] Failed to save {DevLabel(node)}: {lastError}");
                continue;
            }

            saved++;
            if (!quiet)
                DevLog(vm, $"[dev] Saved {DevLabel(node)}");
        }

        if (saved == 0)
        {
            if (!quiet)
                DevLog(vm, "[dev] Nothing saved — check console for errors.");
            return;
        }

        CellSceneCache.Invalidate(path);
        vm.ActiveCell = CellLoader.Load(path);
        if (reloadScene)
        {
            DevLog(vm, $"[dev] Wrote {saved} transform(s) → {path}");
            vm.OnDevCellReloadRequested?.Invoke(path);
        }
        else
        {
            RefreshDevPlacementsInPlace(vm);
            if (!quiet)
                DevLog(vm, $"[dev] Auto-saved {saved} transform(s) → {path}");
        }
    }

    private void RefreshDevPlacementsInPlace(ViewportViewModel vm)
    {
        if (vm.ActiveCell is not { } config) return;

        var envNodes = _renderer.SceneRoot.Children
            .Where(n => n.Name is "Extruder Stand" or "Scanner Stand" or "Spindle Stand" or "RotaryBed")
            .ToList();

        var payload = new CellSwapPayload(
            config,
            vm.ActiveCellPath ?? "",
            RobotBaseNode: null,
            BoosterNode: null,
            BedNode: _bedNode,
            ToolHolder: null,
            FirstTool: config.EffectiveTools.FirstOrDefault(),
            EnvironmentNodes: envNodes,
            RotaryBedPivot: _rotaryBedPivot,
            MultiTools: _multiTools,
            FlangeAttachment: null);

        CellEnvironmentBuilder.RefreshPlacements(payload);

        if (_bedNode is not null && config.Bed is { } bed)
        {
            var rp   = config.Robot.WorldPosition;
            var mesh = bed.VisualMeshOrigin(rp);
            _bedNode.LocalTransform = Matrix4.CreateTranslation(mesh.X, mesh.Y, mesh.Z);
            _bedOriginLocal = new Vector3(mesh.X, mesh.Y, mesh.Z);
            _lastSyncE1     = double.NaN;
        }

        GlCanvas.RequestNextFrameRendering();
    }

    private void SchedulePanelTransformUndo(ViewportViewModel vm, SceneNode node, string description)
    {
        _panelTransformDebounce?.Cancel();
        _panelTransformDebounce = new CancellationTokenSource();
        var token = _panelTransformDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                Dispatcher.UIThread.Post(() => CommitPanelTransformUndo(vm, node, description));
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    private void CommitPanelTransformUndo(ViewportViewModel vm, SceneNode node, string description)
    {
        if (_renderer.SelectedNode != node) return;

        var before = _lastCommittedTransformNode == node
            ? _lastCommittedTransform
            : node.LocalTransform;
        RecordTransformUndo(vm, node, before, node.LocalTransform, description);
    }

    private void SyncSelectionTransformDisplay(ViewportViewModel vm)
    {
        if (_renderer.SelectedNode is not { } node) return;
        var w  = node.WorldTransform;
        var pos = w.Row3.Xyz;
        float sc = w.Row0.Xyz.Length;
        if (sc < 1e-6f) return;
        var nm = new System.Numerics.Matrix4x4(
            w.Row0.X / sc, w.Row0.Y / sc, w.Row0.Z / sc, 0,
            w.Row1.X / sc, w.Row1.Y / sc, w.Row1.Z / sc, 0,
            w.Row2.X / sc, w.Row2.Y / sc, w.Row2.Z / sc, 0,
            0, 0, 0, 1);
        var (a, b, c) = MassiveSlicer.Core.Kinematics.KukaIkSolver.MatrixToAbc(nm);
        vm.SyncSelectionDisplay(
            Math.Round(pos.X, 2), Math.Round(pos.Y, 2), Math.Round(pos.Z, 2),
            Math.Round(a, 2), Math.Round(b, 2), Math.Round(c, 2));
        RememberCommittedTransform(node);
    }

    private void UpdateFocusOverlay()
    {
        if (DataContext is not ViewportViewModel vm) return;

        var selected = _renderer.SelectedNode;
        vm.HasSelection       = selected is not null;
        bool isToolpath       = selected is not null && _renderer.IsToolpathNode(selected);
        bool isToolNode       = IsToolNodeSelected();
        bool isDevNode        = vm.IsDevMode && IsDevNode(selected);
        bool multiToolpath    = _renderer.SelectedToolpathCount >= 2;
        vm.CanMergeToolpaths  = multiToolpath;
        vm.IsToolpathSelected = isToolpath && !multiToolpath;
        bool isMerged = isToolpath && selected is not null && _mergedByNode.ContainsKey(selected);
        vm.IsMergedToolpathSelected = isMerged;
        if (isMerged && _mergedByNode.TryGetValue(selected!, out var mergedRec))
            vm.SyncMergedSettingsDisplay(mergedRec.RetractionHeightMm, mergedRec.TravelSpeedMps * 1000.0);
        vm.UpdateSliceCommand?.RaiseCanExecuteChanged();
        vm.IsDevObjectSelected = isDevNode;
        vm.DevSelectedLabel    = isDevNode && selected is not null ? DevLabel(selected) : "";
        vm.HasMeshSelected     = selected is not null && !isToolpath && !isToolNode && !isDevNode;
        vm.CanUngroup         = selected is not null && !isToolpath && !isToolNode && selected.Children.Count > 0;
        vm.CanExplode         = selected is not null && !isToolpath && !isToolNode && HasExplodableMeshes(selected);
        vm.CanMeshCleanup     = selected is not null && !isToolpath && !isToolNode && HasCleanableMeshes(selected);

        if (selected is null)
            SetGizmoMode(GizmoMode.None);

        // Use ResetScrubIndex (not the public setters) so the IK callback is NOT triggered
        // by the programmatic reset -- the robot only follows scrubbing the user initiates.
        if (isToolpath && selected is not null && _toolpathByNode.TryGetValue(selected, out var tp))
        {
            _activeScrubNode = selected;
            vm.ResetScrubIndex(tp.Layers.Sum(l => l.Moves.Count), tp);
            ValidateToolpathAsync(selected, tp);
            if (vm.AdditiveSettings is { } ads)
            {
                var (t, w, c) = ComputeToolpathStats(tp, ads);
                vm.StatsTime        = t;
                vm.StatsWeight      = w;
                vm.StatsCost        = c;
                vm.HasToolpathStats = true;
            }
        }
        else
        {
            _activeScrubNode = null;
            vm.ResetScrubIndex(0, null);
            vm.HasToolpathStats  = false;
            vm.StatsReachability = "";
            vm.IsValidating      = false;
            vm.SetScrubMarkers([], []);
            _validationCts?.Cancel();
        }

        SyncSelectionTransformDisplay(vm);
    }

    // -- Scrub IK --------------------------------------------------------------

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

        if (_activeScrubNode is null ||
            !_scrubCacheByNode.TryGetValue(_activeScrubNode, out var scrubCache) ||
            scrubCache.Length == 0) return;
        var (pos, planeNormal) = scrubCache[Math.Clamp(index, 0, scrubCache.Length - 1)];

        // Apply the node's current world transform so scrubbing follows a moved toolpath.
        // Stored Toolpath positions are in the original sliced world space; the renderer
        // stores them as (pos − origin) and uses LocalTransform to put them back in world
        // space.  If the user has moved the node we need to apply that same transform here.
        var node = _activeScrubNode;
        TkVector3 worldPos;
        TkVector3 worldNormal;
        if (node is not null && _toolpathOriginByNode.TryGetValue(node, out var origin))
        {
            var wt = node.WorldTransform;                   // UI-thread -- safe to read here
            float lx = pos.X - origin.X, ly = pos.Y - origin.Y, lz = pos.Z - origin.Z;
            worldPos = new TkVector3(
                lx * wt.M11 + ly * wt.M21 + lz * wt.M31 + wt.M41,
                lx * wt.M12 + ly * wt.M22 + lz * wt.M32 + wt.M42,
                lx * wt.M13 + ly * wt.M23 + lz * wt.M33 + wt.M43);
            // Normals transform by the rotation part only (no translation).
            float nx = planeNormal.X, ny = planeNormal.Y, nz = planeNormal.Z;
            worldNormal = TkVector3.Normalize(new TkVector3(
                nx * wt.M11 + ny * wt.M21 + nz * wt.M31,
                nx * wt.M12 + ny * wt.M22 + nz * wt.M32,
                nx * wt.M13 + ny * wt.M23 + nz * wt.M33));
        }
        else
        {
            // Fallback: no node transform -- use stored directions as-is.
            worldPos    = new TkVector3(pos.X,         pos.Y,         pos.Z);
            worldNormal = new TkVector3(planeNormal.X, planeNormal.Y, planeNormal.Z);
        }

        // IK expects the target in ROBROOT frame (world − ROBROOT origin).
        var robrootPos    = _robrootWorldPos;
        var targetRobroot = worldPos - robrootPos;

        // Tool orientation: approach along -normal, forward fixed to world +X.
        // Fixing the forward eliminates azimuthal spin when tilt axis changes.
        var targetRot = vm.AdditiveSettings is { } addSettings
            ? solver.TargetRotFromGlobalOrientation(worldNormal,
                (float)addSettings.ToolheadA,
                (float)addSettings.ToolheadB,
                (float)addSettings.ToolheadC)
            : solver.TargetRotFromGlobalOrientation(worldNormal, 0f, 0f, 0f);

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
    /// Runs IK for every move in <paramref name="toolpath"/> (background task) and enqueues
    /// a reachability bool[] into <see cref="_pendingReachability"/> for the GL thread to apply.
    /// Any previous validation for a different toolpath is cancelled first.
    /// </summary>
    private void ValidateToolpathAsync(SceneNode node, Toolpath toolpath)
    {
        var currentTransform = node.WorldTransform;
        bool sameKey = ReferenceEquals(_validationNode, node)
                       && _validationTransform == currentTransform;

        if (sameKey && _validationDone) return;
        if (sameKey && _validationCts is { IsCancellationRequested: false }) return;

        _validationNode      = node;
        _validationTransform = currentTransform;
        _validationDone      = false;
        _validationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _validationCts = cts;

        var solver      = _ikSolver;
        var vm          = _vm;
        var addSettings = vm?.AdditiveSettings;
        var robot       = vm?.Robot;
        if (solver is null || robot is null) return;

        if (!_scrubCacheByNode.TryGetValue(node, out var cache) || cache.Length == 0) return;
        if (vm is not null) { vm.StatsReachability = "…"; vm.IsValidating = true; vm.SetScrubMarkers([], []); }
        _toolpathOriginByNode.TryGetValue(node, out var origin);
        var   wt      = node.WorldTransform;
        var   robroot = _robrootWorldPos;
        float offA    = addSettings is not null ? (float)addSettings.ToolheadA : 0f;
        float offB    = addSettings is not null ? (float)addSettings.ToolheadB : 0f;
        float offC    = addSettings is not null ? (float)addSettings.ToolheadC : 0f;
        bool  hasOff  = addSettings is not null;
        var   seed    = new float[]
        {
            (float)robot.A1, (float)robot.A2, (float)robot.A3,
            (float)robot.A4, (float)robot.A5, (float)robot.A6,
        };

        Task.Run(() =>
        {
            int total = 0;
            foreach (var layer in toolpath.Layers) total += layer.Moves.Count;

            var targets    = new TkVector3[total];
            var normals    = new TkVector3[total];
            int mi         = 0;
            var lastNormN  = NVec3.UnitZ; // last valid extrude normal; held through transitions
            foreach (var layer in toolpath.Layers)
            {
                foreach (var move in layer.Moves)
                {
                    var (pos, _) = cache[Math.Min(mi + 1, cache.Length - 1)];
                    float lx = pos.X - origin.X, ly = pos.Y - origin.Y, lz = pos.Z - origin.Z;
                    targets[mi] = new TkVector3(
                        lx * wt.M11 + ly * wt.M21 + lz * wt.M31 + wt.M41,
                        lx * wt.M12 + ly * wt.M22 + lz * wt.M32 + wt.M42,
                        lx * wt.M13 + ly * wt.M23 + lz * wt.M33 + wt.M43) - robroot;

                    // Travel and layer-stitch moves carry no orientation — hold the last
                    // extrude normal to prevent a sudden IK jump at layer transitions.
                    // Per-move normal (overhang orientation) takes priority; falls back to UnitZ.
                    NVec3 effNorm;
                    if (move.Kind == MoveKind.Travel || move.IsLayerStitch)
                        effNorm = lastNormN;
                    else
                    {
                        effNorm    = move.Normal.LengthSquared() > 1e-6f ? move.Normal : NVec3.UnitZ;
                        lastNormN  = effNorm;
                    }
                    float nx = effNorm.X, ny = effNorm.Y, nz = effNorm.Z;
                    normals[mi] = TkVector3.Normalize(new TkVector3(
                        nx * wt.M11 + ny * wt.M21 + nz * wt.M31,
                        nx * wt.M12 + ny * wt.M22 + nz * wt.M32,
                        nx * wt.M13 + ny * wt.M23 + nz * wt.M33));
                    mi++;
                }
            }

            if (cts.IsCancellationRequested) return;

            var targetRots = new (TkVector3 r0, TkVector3 r1, TkVector3 r2)[total];
            for (int i = 0; i < total; i++)
            {
                targetRots[i] = solver.TargetRotFromGlobalOrientation(normals[i], offA, offB, offC);
            }

            if (cts.IsCancellationRequested) return;

            // Chunked parallel IK: each chunk propagates solutions sequentially so each
            // move seeds from its predecessor.  Adjacent toolpath moves are ~1–6 mm apart,
            // so the previous solution typically converges in 2–5 iterations instead of
            // 20–80 from the static home-position seed.
            var result      = new bool[total];
            var ikSolutions = new float[]?[total]; // null = unreachable
            int numChunks   = Math.Max(1, Math.Min(Environment.ProcessorCount, total));
            int chunkSize   = (total + numChunks - 1) / numChunks;

            try
            {
                Parallel.For(0, numChunks,
                    new ParallelOptions { CancellationToken = cts.Token },
                    ci =>
                    {
                        int start     = ci * chunkSize;
                        int end       = Math.Min(start + chunkSize, total);
                        var chunkSeed = (float[])seed.Clone();

                        for (int i = start; i < end; i++)
                        {
                            if (cts.IsCancellationRequested) return;
                            var sol = solver.Solve(targets[i], chunkSeed, targetRots[i], maxIterations: 40);
                            result[i]      = sol is not null;
                            ikSolutions[i] = sol;
                            if (sol is not null) chunkSeed = sol;
                        }
                    });
            }
            catch (OperationCanceledException) { return; }

            // Fill unreachable gaps with nearest valid solution so playback stays smooth.
            var solutions = new float[total][];
            var lastValid = seed;
            for (int i = 0; i < total; i++)
            {
                if (ikSolutions[i] is not null) lastValid = ikSolutions[i]!;
                solutions[i] = (float[])lastValid.Clone();
            }

            // Unwrap joint angles to prevent ±360° configuration discontinuities at
            // chunk boundaries and travel→extrude transitions.  Each axis is adjusted
            // by the nearest multiple of 360° so consecutive solutions stay continuous.
            for (int i = 1; i < total; i++)
            {
                for (int j = 0; j < 6; j++)
                {
                    float diff = solutions[i][j] - solutions[i - 1][j];
                    if      (diff >  180f) solutions[i][j] -= 360f;
                    else if (diff < -180f) solutions[i][j] += 360f;
                }
            }

            // Velocity profile: time (ms) per move accounting for C_VEL corner blending.
            float printMmS       = addSettings is not null ? (float)addSettings.PrintSpeed  : 60f;
            float travelMmS      = addSettings is not null ? (float)addSettings.TravelSpeed : 150f;
            float wipeMmS        = addSettings is not null ? (float)addSettings.WipeSpeed   : 120f;
            float apoCvelFrac    = addSettings is not null ? (float)(addSettings.ApoCvel / 100.0) : 0.5f;
            var (moveTimes, peakVelocities) = BuildMoveProfile(toolpath, printMmS, travelMmS, wipeMmS, apoCvelFrac);

            // Singularity detection: flag moves where |A5| < 5° (wrist singularity).
            var singularity = new bool[total];
            for (int i = 0; i < total; i++)
                singularity[i] = MathF.Abs(solutions[i][4]) < 5f;

            _ikSolutionsByNode[node]  = solutions;
            _moveTimesMsByNode[node]  = moveTimes;
            _singularityByNode[node]  = singularity;

            int failCount = 0;
            foreach (var r in result) if (!r) failCount++;

            _pendingReachability.Enqueue((node, result));
            _pendingSingularityPoints.Enqueue((node, singularity));
            string reachLabel = failCount == 0
                ? $"All {result.Length} reachable"
                : $"{failCount} / {result.Length} unreachable";
            Dispatcher.UIThread.Post(() =>
            {
                _validationDone = true;
                if (vm is not null)
                {
                    vm.StatsReachability = reachLabel;
                    vm.IsValidating = false;
                    vm.SetScrubMarkers(result, singularity);
                }
                GlCanvas.RequestNextFrameRendering();
            });
        });
    }

    /// <summary>
    /// Re-applies OrientationSmoother to every cached raw toolpath using the current settings,
    /// updates _toolpathByNode and _scrubCacheByNode for IK scrubbing, and enqueues colormap
    /// updates for the GL thread. Called whenever a smoothing setting changes.
    /// </summary>
    private void ReapplyOrientationSmoothing(AdditiveSettingsViewModel s)
    {
        if (_rawToolpathByNode.IsEmpty) return;
        var smoothSettings = new SliceSettings
        {
            SmoothRotation                = s.SmoothRotation,
            SmoothRotationRadius          = s.SmoothRotationRadius,
            SmoothRotationMaxRateDegPerMm = (float)s.SmoothRotationMaxRateDegPerMm,
        };
        foreach (var (node, raw) in _rawToolpathByNode)
        {
            var smoothed = OrientationSmoother.Apply(raw, smoothSettings);
            _toolpathByNode[node]   = smoothed;
            _scrubCacheByNode[node] = BuildScrubCache(smoothed);
            _pendingOrientationUpdate.Enqueue((node, ComputeOrientationRatePerFlatMove(smoothed)));
        }
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Computes a per-flat-move overhang score in [0,1].
    /// 0 = move midpoint is within beadWidth of the previous layer (fully supported).
    /// 1 = move midpoint has no nearby segment in the previous layer (unsupported).
    /// Travel moves always score 0.
    /// </summary>
    private static float[] ComputeOverhangPerFlatMove(Toolpath tp, float beadWidth)
    {
        int total = tp.Layers.Sum(l => l.Moves.Count);
        var result = new float[total];
        if (total == 0) return result;

        List<(NVec3 from, NVec3 to)>? prevSegs = null;
        int fi = 0;
        foreach (var layer in tp.Layers)
        {
            var curSegs = new List<(NVec3, NVec3)>();
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude)
                {
                    if (prevSegs is { Count: > 0 })
                    {
                        var mid = (move.From + move.To) * 0.5f;
                        float minD = float.MaxValue;
                        foreach (var (a, b) in prevSegs)
                        {
                            float d = SegDist2D(mid, a, b);
                            if (d < minD) minD = d;
                        }
                        result[fi] = Math.Clamp(minD / beadWidth, 0f, 1f);
                    }
                    curSegs.Add((move.From, move.To));
                }
                fi++;
            }
            prevSegs = curSegs;
        }
        return result;

        static float SegDist2D(NVec3 p, NVec3 a, NVec3 b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-10f)
            {
                float ex = p.X - a.X, ey = p.Y - a.Y;
                return MathF.Sqrt(ex * ex + ey * ey);
            }
            float t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0f, 1f);
            float cx = a.X + t * dx - p.X, cy = a.Y + t * dy - p.Y;
            return MathF.Sqrt(cx * cx + cy * cy);
        }
    }

    /// <summary>
    /// Returns a per-flat-move score in [0,1] representing how fast the toolhead orientation
    /// is changing relative to a reference max rate of 5°/mm. A score of 1 (red) means
    /// ≥5°/mm change — the KUKA will slow down to interpolate the orientation.
    /// Moves without per-move normals (planar layers) always score 0.
    /// </summary>
    private static float[] ComputeOrientationRatePerFlatMove(Toolpath tp)
    {
        // 3°/mm = top of scale (purple). Gradient: dark blue → cyan → green → yellow
        // → orange → red → magenta → purple, matching the 8-stop legend in the UI.
        const float maxDegPerMm = 3f;

        int total = tp.Layers.Sum(l => l.Moves.Count);
        var result = new float[total];
        if (total == 0) return result;

        NVec3 prevNormal = NVec3.Zero;
        bool  hasPrev    = false;
        int   fi         = 0;

        foreach (var layer in tp.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude && !move.IsLayerStitch &&
                    move.Normal.LengthSquared() > 1e-6f)
                {
                    var   normal = NVec3.Normalize(move.Normal);
                    // Use the segment length — that's the distance over which the
                    // orientation changes, giving deg/mm. The previous code used
                    // Distance(move.From, prevTo) which is the gap between consecutive
                    // moves (≈ 0 for adjacent segments) so the guard always failed.
                    float dist   = (move.To - move.From).Length();
                    if (hasPrev && dist > 1e-3f)
                    {
                        float cosA     = Math.Clamp(NVec3.Dot(normal, prevNormal), -1f, 1f);
                        float degPerMm = MathF.Acos(cosA) * (180f / MathF.PI) / dist;
                        result[fi]     = Math.Clamp(degPerMm / maxDegPerMm, 0f, 1f);
                    }
                    prevNormal = normal;
                    hasPrev    = true;
                }
                else
                {
                    hasPrev = false;
                }
                fi++;
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a flat cache of (pos, normal) entries for O(1) scrub index lookup.
    /// Entry 0 = first move's From; entries 1..N = each move's To in order.
    /// </summary>
    private static (NVec3 pos, NVec3 normal)[] BuildScrubCache(Toolpath tp)
    {
        int total = 0;
        foreach (var layer in tp.Layers) total += layer.Moves.Count;
        if (total == 0) return [];

        var arr      = new (NVec3 pos, NVec3 normal)[total + 1];
        int i        = 0;
        bool first   = true;
        NVec3 lastN  = NVec3.UnitZ;
        foreach (var layer in tp.Layers)
        {
            foreach (var move in layer.Moves)
            {
                // Travel and layer-stitch moves carry no orientation — hold last extrude normal.
                // Per-move normal (overhang orientation) overrides UnitZ fallback.
                NVec3 n;
                if (move.Kind == MoveKind.Travel || move.IsLayerStitch)
                    n = lastN;
                else
                {
                    n     = move.Normal.LengthSquared() > 1e-6f ? move.Normal : NVec3.UnitZ;
                    lastN = n;
                }
                if (first) { arr[i++] = (move.From, n); first = false; }
                arr[i++] = (move.To, n);
            }
        }
        return arr[..i];
    }

    /// <summary>
    /// Drives the robot joints directly from pre-solved angles without launching an IK task.
    /// Used by the playback timer to animate Cartesian motion in real time.
    /// </summary>
    private void SetRobotAnglesDirectly(float[] angles)
    {
        var robot = _vm?.Robot;
        if (robot is null) return;
        robot.Desync();
        robot.A1 = Math.Round(angles[0], 2);
        robot.A2 = Math.Round(angles[1], 2);
        robot.A3 = Math.Round(angles[2], 2);
        robot.A4 = Math.Round(angles[3], 2);
        robot.A5 = Math.Round(angles[4], 2);
        robot.A6 = Math.Round(angles[5], 2);
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Computes per-move timing (ms) and peak velocity (mm/s) for the toolpath using a
    /// two-pass trapezoidal velocity profile with KUKA C_VEL corner-speed limits.
    /// <para>
    /// Corner speed at each junction = <c>apoCvelFraction × min(v_in, v_out)</c> scaled by
    /// the cosine of the direction change — straight runs carry full speed, sharp turns
    /// slow to <paramref name="apoCvelFraction"/> × programmed speed (default 0.5, matching
    /// <c>$APO.CVEL=50</c>). A two-pass forward/backward sweep propagates acceleration
    /// constraints so short segments between close corners also show realistic slowdowns.
    /// </para>
    /// </summary>
    private static (float[] timesMs, float[] peakVelocities) BuildMoveProfile(
        Toolpath tp, float printMmS, float travelMmS, float wipeMmS,
        float apoCvelFraction = 0.5f, float accelMmS2 = 2000f)
    {
        var moves = new List<ToolpathMove>(tp.Layers.Sum(l => l.Moves.Count));
        foreach (var layer in tp.Layers) moves.AddRange(layer.Moves);

        int n = moves.Count;
        if (n == 0) return ([], []);

        var vProg = new float[n];
        var dist  = new float[n];
        for (int i = 0; i < n; i++)
        {
            vProg[i] = moves[i].IsWipe ? wipeMmS
                       : moves[i].Kind == MoveKind.Extrude ? printMmS : travelMmS;
            dist[i]  = NVec3.Distance(moves[i].From, moves[i].To);
        }

        // Junction speeds: the robot must not exceed this speed at waypoint i.
        // At each junction the factor blends linearly between apoCvel (sharp reversal)
        // and 1.0 (perfectly straight) based on the cosine of the direction change.
        var jV = new float[n + 1]; // jV[0]=0 (start at rest), jV[n]=0 (end at rest)
        for (int i = 1; i < n; i++)
        {
            var d1 = moves[i - 1].To - moves[i - 1].From;
            var d2 = moves[i].To     - moves[i].From;
            float l1 = d1.Length(), l2 = d2.Length();
            float cosA = l1 > 1e-6f && l2 > 1e-6f
                ? NVec3.Dot(d1 / l1, d2 / l2)
                : 1f;
            float factor = apoCvelFraction + (1f - apoCvelFraction) * 0.5f * (cosA + 1f);
            jV[i] = factor * MathF.Min(vProg[i - 1], vProg[i]);
        }

        // Forward pass: max speed reachable by accelerating from entry junction speed.
        var vFwd = new float[n];
        for (int i = 0; i < n; i++)
            vFwd[i] = MathF.Min(vProg[i], MathF.Sqrt(jV[i] * jV[i] + 2f * accelMmS2 * dist[i]));

        // Backward pass: cap so the robot can decelerate to the exit junction speed.
        var vPeak = (float[])vFwd.Clone();
        for (int i = n - 1; i >= 0; i--)
        {
            float vReachable = MathF.Sqrt(jV[i + 1] * jV[i + 1] + 2f * accelMmS2 * dist[i]);
            vPeak[i] = MathF.Min(vFwd[i], MathF.Min(vProg[i], vReachable));
        }

        // Compute time per move using a trapezoidal (or triangular) velocity profile.
        var timesMs = new float[n];
        for (int i = 0; i < n; i++)
        {
            float d    = dist[i];
            float v0   = jV[i];
            float v1   = jV[i + 1];
            float vTop = vPeak[i];

            if (d < 1e-6f)  { timesMs[i] = 1f;    continue; }
            if (vTop < 1e-6f) { timesMs[i] = 1000f; continue; }

            float dAccel  = (vTop * vTop - v0 * v0) / (2f * accelMmS2);
            float dDecel  = (vTop * vTop - v1 * v1) / (2f * accelMmS2);
            float dCruise = d - dAccel - dDecel;

            float t;
            if (dCruise >= 0f)
            {
                t = (vTop - v0) / accelMmS2 + dCruise / vTop + (vTop - v1) / accelMmS2;
            }
            else
            {
                // Triangle: didn't reach vTop — solve for actual peak.
                float vActual = MathF.Sqrt((2f * accelMmS2 * d + v0 * v0 + v1 * v1) * 0.5f);
                vActual = MathF.Max(vActual, MathF.Max(v0, v1));
                t       = (vActual - v0) / accelMmS2 + (vActual - v1) / accelMmS2;
            }
            timesMs[i] = MathF.Max(t * 1000f, 0.1f);
        }

        return (timesMs, vPeak);
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

    private void FrameAll()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool hasGeometry = false;

        Span<Vector3> corners = stackalloc Vector3[8];
        foreach (var child in _renderer.SceneRoot.Children)
        {
            foreach (var n in child.SelfAndDescendants())
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
        }

        if (hasGeometry)
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

    // -- Gizmo drag ------------------------------------------------------------

    private void StartGizmoDrag(GizmoAxis axis, float mx, float my, float vpW, float vpH)
    {
        _gizmoDragAxis           = axis;
        _renderer.ActiveDragAxis = axis;
        _gizmoDragStartScreenX   = mx;
        _gizmoDragCurrScreenX    = mx;
        if (axis == GizmoAxis.All)
        {
            var camFwdAll = Vector3.Normalize(_renderer.Camera.Target - _renderer.Camera.Eye);
            var r = Vector3.Cross(Vector3.UnitZ, camFwdAll);
            _gizmoDragAxisDir = r.LengthSquared > 1e-6f ? Vector3.Normalize(r) : Vector3.UnitX;
        }
        else
        {
            _gizmoDragAxisDir = axis switch
            {
                GizmoAxis.X => new Vector3(1f, 0f, 0f),
                GizmoAxis.Y => new Vector3(0f, 1f, 0f),
                _           => new Vector3(0f, 0f, 1f),
            };
        }

        if (_renderer.SelectedNode is not { } node) return;
        if (IsToolNodeSelected() && _renderer.GizmoMode != GizmoMode.Translate) return;
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

        var dragOp = _kbTransformActive ? _kbTransformOp : _renderer.GizmoMode;
        switch (dragOp)
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

        _gizmoDragCurrScreenX = mx;

        float vpW = (float)GlCanvas.Bounds.Width;
        float vpH = (float)GlCanvas.Bounds.Height;
        var ray   = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);

        float denom = Vector3.Dot(ray.Direction, _gizmoDragPlaneNormal);
        if (MathF.Abs(denom) < 1e-5f) return;
        float t      = Vector3.Dot(_gizmoDragPlanePoint - ray.Origin, _gizmoDragPlaneNormal) / denom;
        var hitWorld = ray.At(t);

        var dragOp = _kbTransformActive ? _kbTransformOp : _renderer.GizmoMode;
        switch (dragOp)
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
        float ratio;
        if (_gizmoDragAxis == GizmoAxis.All)
        {
            float vpW = (float)GlCanvas.Bounds.Width;
            float dx  = _gizmoDragCurrScreenX - _gizmoDragStartScreenX;
            ratio = MathF.Exp(dx / (vpW * 0.3f) * MathF.Log(3f));
            if (ratio <= 0f) return;
        }
        else
        {
            var relStart   = _gizmoDragStartHit - _gizmoDragPlanePoint;
            var relCurrent = hitWorld           - _gizmoDragPlanePoint;
            float startLen = Vector3.Dot(relStart,   _gizmoDragAxisDir);
            float currLen  = Vector3.Dot(relCurrent, _gizmoDragAxisDir);
            if (MathF.Abs(startLen) < 1e-5f) return;
            ratio = currLen / startLen;
            if (ratio <= 0f) return;
        }

        var lt = _gizmoDragInitialLocal;
        switch (_gizmoDragAxis)
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
            case GizmoAxis.All:
                lt.M11 *= ratio; lt.M12 *= ratio; lt.M13 *= ratio;
                lt.M21 *= ratio; lt.M22 *= ratio; lt.M23 *= ratio;
                lt.M31 *= ratio; lt.M32 *= ratio; lt.M33 *= ratio;
                break;
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

    // -- KRL export ------------------------------------------------------------

    private void SaveDefaultHomePosition(ViewportViewModel vm)
    {
        var cellPath = vm.ActiveCellPath;
        var settings = vm.AdditiveSettings;
        if (cellPath is null || settings is null) return;
        var data = CellLoader.LoadPositionData(cellPath);
        data.Default = settings.SelectedHomePositionName;
        CellLoader.SavePositionData(cellPath, data);
    }

    private void SaveHomePosition(ViewportViewModel vm, string name, float[] angles)
    {
        var cellPath = vm.ActiveCellPath;
        var additive = vm.AdditiveSettings;
        var robot    = vm.Robot;
        if (cellPath is null || additive is null || robot is null) return;

        var data     = CellLoader.LoadPositionData(cellPath);
        var existing = data.Positions.FindIndex(p => p.Name == name);
        var config   = new HomePositionConfig { Name = name, Angles = angles };
        if (existing >= 0)
            data.Positions[existing] = config;
        else
            data.Positions.Add(config);

        CellLoader.SavePositionData(cellPath, data);
        additive.AddHomePosition(name, angles);
        robot.SetNextPositionName(data.Positions.Count + 1);
    }

    private void MergeToolpaths(ViewportViewModel vm)
    {
        var nodes = _renderer.SelectedToolpaths.ToList();
        if (nodes.Count < 2) return;

        var sources = new List<MergeSourceEntry>();
        float beadWidth = 6f, layerHeight = 3f;
        NVec3 materialColor = default;

        foreach (var node in nodes)
        {
            if (!_toolpathByNode.TryGetValue(node, out var local)) continue;
            _toolpathOriginByNode.TryGetValue(node, out var origin);
            _toolpathMetaByNode.TryGetValue(node, out var meta);
            if (meta.BeadWidth > 0) beadWidth = meta.BeadWidth;
            if (meta.LayerHeight > 0) layerHeight = meta.LayerHeight;
            materialColor = meta.MaterialColor;

            var wt = node.WorldTransform;
            sources.Add(new MergeSourceEntry
            {
                LocalToolpath  = DeepCopyToolpath(local),
                Origin         = origin,
                WorldTransform = ToSysMatrix4(wt),
                BeadWidth      = meta.BeadWidth > 0 ? meta.BeadWidth : 6f,
                LayerHeight    = meta.LayerHeight > 0 ? meta.LayerHeight : 3f,
                MaterialColor  = meta.MaterialColor,
            });
        }

        if (sources.Count < 2) return;

        float retraction = (float)(vm.AdditiveSettings?.ZHopMm ?? vm.MergedRetractionHeightMm);
        float travelMps  = (float)((vm.AdditiveSettings?.TravelSpeed ?? vm.MergedTravelSpeed) / 1000.0);

        var record = new MergedToolpathRecord
        {
            Sources              = sources,
            RetractionHeightMm   = retraction,
            TravelSpeedMps       = travelMps,
        };

        var merged     = BuildMergedToolpath(record);
        var mergedNode = new SceneNode { Name = $"Merged Toolpath ({sources.Count})", Selectable = true, Visible = true };
        vm.RegisterToolpathInOutliner(mergedNode, parentItem: null);
        _mergedByNode[mergedNode] = record;

        foreach (var sourceNode in nodes)
        {
            if (vm.FindToolpathOutlinerItem(sourceNode) is { } sourceItem)
                sourceItem.Visible = false;
            else
                sourceNode.Visible = false;
        }

        var pending = new PendingToolpathEntry
        {
            Toolpath      = merged,
            RawToolpath   = DeepCopyToolpath(merged),
            Node          = mergedNode,
            BeadWidth     = beadWidth,
            LayerHeight   = layerHeight,
            MaterialColor = materialColor,
        };
        StageToolpathMaps(pending);
        vm.PendingToolpath.Enqueue(pending);

        _renderer.Select(mergedNode);
        vm.SyncMergedSettingsDisplay(retraction, travelMps * 1000.0);
        UpdateFocusOverlay();
        ApplyToolpathStats(vm, merged);
        GlCanvas.RequestNextFrameRendering();
    }

    private void RebuildMergedToolpath(ViewportViewModel vm)
    {
        if (_activeScrubNode is not { } node || !_mergedByNode.TryGetValue(node, out var record)) return;

        record.RetractionHeightMm = (float)vm.MergedRetractionHeightMm;
        record.TravelSpeedMps     = (float)(vm.MergedTravelSpeed / 1000.0);

        var merged = BuildMergedToolpath(record);
        var src    = record.Sources[0];
        vm.PendingToolpathReplace.Enqueue(new PendingToolpathEntry
        {
            Toolpath      = merged,
            RawToolpath   = DeepCopyToolpath(merged),
            Node          = node,
            BeadWidth     = src.BeadWidth,
            LayerHeight   = src.LayerHeight,
            MaterialColor = src.MaterialColor,
        });

        if (_renderer.SelectedNode == node)
        {
            vm.ResetScrubIndex(merged.Layers.Sum(l => l.Moves.Count), merged);
            ApplyToolpathStats(vm, merged);
            ValidateToolpathAsync(node, merged);
        }
        GlCanvas.RequestNextFrameRendering();
    }

    private static Toolpath BuildMergedToolpath(MergedToolpathRecord record)
    {
        var worldPaths = record.Sources
            .Select(s => ToolpathMerger.ToWorldSpace(s.LocalToolpath, s.Origin, s.WorldTransform))
            .ToList();
        return ToolpathMerger.Merge(worldPaths, record.RetractionHeightMm, record.TravelSpeedMps);
    }

    private static Toolpath DeepCopyToolpath(Toolpath source)
    {
        var copy = new Toolpath();
        foreach (var layer in source.Layers)
        {
            var newLayer = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height      = layer.Height,
                PlaneNormal = layer.PlaneNormal,
            };
            foreach (var move in layer.Moves)
                newLayer.Moves.Add(move with { });
            copy.Layers.Add(newLayer);
        }
        return copy;
    }

    private static System.Numerics.Matrix4x4 ToSysMatrix4(TkMatrix4 wt)
        => new(wt.M11, wt.M12, wt.M13, wt.M14,
               wt.M21, wt.M22, wt.M23, wt.M24,
               wt.M31, wt.M32, wt.M33, wt.M34,
               wt.M41, wt.M42, wt.M43, wt.M44);

    private async Task ExportKrlAsync(ViewportViewModel vm)
    {
        var toolpath = vm.ActiveScrubToolpath;
        var node     = _activeScrubNode;
        var cell     = vm.ActiveCell;
        var settings = vm.AdditiveSettings;

        if (toolpath is null || node is null || cell is null || settings is null) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title              = "Export KRL",
            DefaultExtension   = "src",
            SuggestedFileName  = "print_job.src",
            FileTypeChoices    = [new("KRL Source") { Patterns = ["*.src"] }],
        });
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (path is null) return;

        await WriteKrlAsync(vm, toolpath, node, cell, settings, path);
    }

    private async Task SendToRobotAsync(ViewportViewModel vm)
    {
        var toolpath = vm.ActiveScrubToolpath;
        var node     = _activeScrubNode;
        var cell     = vm.ActiveCell;
        var settings = vm.AdditiveSettings;

        if (toolpath is null || node is null || cell is null || settings is null) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var robotFolder = RobotKrlPaths.UncDFolder(cell);
        var startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(robotFolder);

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title                  = $"Send to Robot — {cell.Name}",
            DefaultExtension       = "src",
            SuggestedFileName      = RobotKrlPaths.SuggestedFileName(node.Name),
            SuggestedStartLocation = startFolder,
            FileTypeChoices        = [new("KRL Source") { Patterns = ["*.src"] }],
        });
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (path is null) return;

        path = RobotKrlPaths.ToExtendedUncPath(path);
        await WriteKrlAsync(vm, toolpath, node, cell, settings, path);

        if (topLevel.DataContext is MainWindowViewModel mvm)
            mvm.Console.Log($"[krl] Sent to {cell.Name} ({cell.BridgeIp}): {path}");
    }

    private async Task WriteKrlAsync(
        ViewportViewModel vm,
        Toolpath toolpath,
        SceneNode node,
        CellConfig cell,
        AdditiveSettingsViewModel settings,
        string path)
    {
        var wt    = node.WorldTransform;
        var sysWt = new System.Numerics.Matrix4x4(
            wt.M11, wt.M12, wt.M13, wt.M14,
            wt.M21, wt.M22, wt.M23, wt.M24,
            wt.M31, wt.M32, wt.M33, wt.M34,
            wt.M41, wt.M42, wt.M43, wt.M44);

        _toolpathOriginByNode.TryGetValue(node, out var origin);

        var selectedPreset = settings.SelectedPreset;
        var postProcess    = settings.KrlPostProcess.ToSettings();
        float exportTemp   = settings.GetEffectiveExportTemperature();
        float flow         = (float)(selectedPreset?.FlowRate ?? 0.463);

        var exportSettings = new KrlExportSettings
        {
            ProgramName         = Path.GetFileNameWithoutExtension(path),
            ToolDataIndex       = settings.ToolDataIndex,
            BaseDataIndex       = settings.BaseDataIndex,
            PrintSpeedMps       = (float)(settings.PrintSpeed / 1000.0),
            TravelSpeedMps      = (float)(settings.TravelSpeed / 1000.0),
            WipeSpeedMps        = (float)(settings.WipeSpeed / 1000.0),
            AccelerationPercent = settings.Acceleration,
            ApproachZMm         = (float)settings.ApproachZ,
            ToolheadOffsetA     = (float)settings.ToolheadA,
            ToolheadOffsetB     = (float)settings.ToolheadB,
            ToolheadOffsetC     = (float)settings.ToolheadC,
            Temperature1        = exportTemp,
            Temperature2        = exportTemp,
            Temperature3        = exportTemp,
            BeadWidthMm         = (float)settings.BeadWidth,
            LayerHeightMm       = (float)settings.LayerHeight,
            FlowRate            = flow,
            HomePosition              = settings.SelectedHomeAngles,
            ApoCvel                   = (int)settings.ApoCvel,
            OrientationLookAheadMm    = (float)settings.OrientationLookAheadMm,
            OrientationSigmaMm        = (float)settings.OrientationSigmaMm,
            NodeWorldTransform = sysWt,
            NodeOrigin         = new System.Numerics.Vector3(origin.X, origin.Y, origin.Z),
            RobrootWorldPos    = new System.Numerics.Vector3(
                cell.Robot.WorldPosition.X,
                cell.Robot.WorldPosition.Y,
                cell.Robot.WorldPosition.Z),
            BaseDataOffset     = new System.Numerics.Vector3(
                cell.Bed.BaseData.X,
                cell.Bed.BaseData.Y,
                cell.Bed.BaseData.Z),
            TravelSetAnout4Zero = postProcess.TravelSetAnout4Zero,
            HeaderTemplate      = postProcess.HeaderText,
            FooterTemplate      = postProcess.FooterText,
            ExtrusionRpmPercent     = settings.GetEffectiveExtrusionSpeedPercent(),
            ExtrusionStartWaitSec   = (float)settings.ExtrusionStartWaitSec,
            ExtrusionResumeWaitSec  = (float)settings.ExtrusionResumeWaitSec,
        };

        var krl = await Task.Run(() => KrlExporter.Export(toolpath, exportSettings));
        await File.WriteAllTextAsync(path, krl);
    }
}
