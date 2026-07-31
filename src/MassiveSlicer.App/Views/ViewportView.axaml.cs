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
using MassiveSlicer.App;
using MassiveSlicer.App.Enums;
using MassiveSlicer.App.Undo;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Curved;
using MassiveSlicer.Core.Slicing.Effects;
using MassiveSlicer.Core.Slicing.Modifiers;
using MassiveSlicer.Core.Collision;
using MassiveSlicer.Viewport.Collision;
using MassiveSlicer.Viewport;
using MassiveSlicer.Viewport.Camera;
using MassiveSlicer.Viewport.FK;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.App.Diagnostics;
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
    private SceneNode?             _rotaryBedRoot;   // "RotaryBed" env root — relocated when the bed is recentred
    private bool                   _multiToolFlangeParented;
    private readonly HashSet<SceneNode> _lfamInfrastructureNodes = [];
    private bool                   _lastOutlinerLayerPreview;
    private SceneNode?             _lastLayerPreviewTargetNode;
    private bool                   _lastShowBackFaces = true;
    private SceneNode?             _lastBackFaceTargetNode;
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
    /// <summary>Space held → LMB pans (2D slice / edit-friendly; works globally).</summary>
    private bool     _spaceHeld;
    private bool     _spaceUsedForPan;

    // Selection / gizmo drag tracking
    private Point    _leftDownPos;
    private bool     _leftDragged;
    private GizmoAxis _gizmoDragAxis = GizmoAxis.None;
    private bool     _toolIsDragging;
    private Vector3  _ikDragTcpOffset;
    private Vector3  _ikDragTcpPosition;
    private (Vector3, Vector3, Vector3) _ikDragTargetRot;
    private (Vector3, Vector3, Vector3) _ikDragInitialTargetRot;
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
    // Baseline for linked nodes (a model's toolpath, or vice versa) carried along by
    // MirrorTypedTransformDelta/the drag-link — lets RecordTransformUndo bundle their
    // before/after into the same undo entry so Undo can never desync them from the primary.
    private readonly Dictionary<SceneNode, Matrix4> _lastCommittedFollowerTransform = new();
    private CancellationTokenSource? _panelTransformDebounce;
    private CancellationTokenSource? _devAutoSaveDebounce;

    // Pointer capture
    private IPointer? _capturedPointer;

    // Toolpath seam-point drag: click-hold a seam point, slide the mouse left/right
    // to walk the seam along its contour, release to re-seam the whole toolpath there.
    private bool _seamPointDragging;
    private SceneNode? _seamDragNode;
    private readonly List<TkVector3> _seamDragLoopWorld = [];
    private readonly List<System.Numerics.Vector2> _seamDragLoopLocalXY = [];
    private float[] _seamDragCumLen = [];
    private float _seamDragOffsetMm;
    private float _seamDragMmPerPixel = 1f;
    private int _seamDragVertex;

    // Seam guide drag
    private bool _seamGuideDragging;
    private WeldedMesh? _boundaryEditorMesh;
    private int _sliceStatusClearGen;
    private int  _seamGuideDragIndex = -1;

    // Cached VM reference -- set on the UI thread in WireGlCanvas, read from GL thread in OnRender.
    // Avoids accessing the Avalonia DataContext property (UI-thread-only) from the GL thread.
    private ViewportViewModel? _vm;

    // Toolpath-to-node map -- populated on GL thread, read on UI thread (ConcurrentDictionary is safe)
    private readonly ConcurrentDictionary<SceneNode, Toolpath>                    _toolpathByNode       = new();
    private readonly ConcurrentDictionary<SceneNode, (float BeadWidth, float LayerHeight, NVec3 MaterialColor)> _toolpathMetaByNode = new();

    /// <summary>Robot-validation issue summary per toolpath node (unreachable / singularity counts + Z range).</summary>
    private readonly ConcurrentDictionary<SceneNode, (int Unreachable, int Singular, float ZLo, float ZHi)> _validationIssuesByNode = new();
    private readonly ConcurrentDictionary<SceneNode, MergedToolpathRecord> _mergedByNode = new();
    // Pre-smoothing toolpaths keyed by node -- used to re-apply OrientationSmoother live when settings change.
    private readonly ConcurrentDictionary<SceneNode, Toolpath>                    _rawToolpathByNode    = new();
    // TCP keyframes: per-toolpath-node offset keys + the pristine pre-keyframe path.
    private sealed class TcpKey
    {
        public int   Index;
        public NVec3 Offset;
        public int   InfluenceLeft;    // ease-in window, moves
        public int   InfluenceRight;   // ease-out window, moves
    }
    private readonly Dictionary<SceneNode, List<TcpKey>> _tcpKeyframesByNode = new();
    private readonly Dictionary<SceneNode, Toolpath>     _keyframeBaseByNode = new();
    private int _selectedTcpKey = -1;
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
    /// <summary>Per-move planned rail E1 (mm), parallel to IK solutions / move list.</summary>
    private readonly ConcurrentDictionary<SceneNode, float[]>    _e1MmByNode          = new();

    // Digital-twin collision: model built lazily on the UI thread (scene reads),
    // then shared immutably with the background validation sweep.
    private CollisionWorld? _collisionWorld;
    private readonly ConcurrentDictionary<SceneNode, bool[]> _collisionByNode = new();

    // Playback timing state.
    private double           _playbackStartElapsedMs;
    private readonly System.Diagnostics.Stopwatch _playbackStopwatch = new();

    // Timer that drives playback.  16 ms ≈ 60 fps for smooth real-time motion.
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    // Last joint angles forwarded to SyncTcpReadout -- skip the readout when joints haven't moved.
    private double _lastSyncA1, _lastSyncA2, _lastSyncA3, _lastSyncA4, _lastSyncA5, _lastSyncA6;

    // Dev mode: editable cell environment nodes (bed, rotary bed, stands, docks).
    private readonly Dictionary<SceneNode, (string Kind, string? Id)> _devNodeKinds = new();

    // LFAM 1 rail (E1 mm): robot base wrapper + home pose for carriage translation.
    private SceneNode?            _robotBaseNode;
    private RobotRailCellConfig?  _robotRail;
    private Vector3               _robotHomePos;

    // Rotary bed (E1): the bed mesh wrapper node + its centre, so E1 can spin it about the vertical axis.
    private SceneNode? _bedNode;
    private Vector3    _bedOriginLocal;
    private Vector3    _bedBaseMarker;
    private Vector3    _bedGridCorner;
    private Vector3    _bedGridDatum;
    private float      _bedWidth, _bedDepth, _bedDiameter;
    private float      _bedRotationSign = -1f;   // E1→scene sign; set by config / rotation calibration
    private double     _lastSyncE1 = double.NaN;
    // Set on the UI thread by a manual bed edit; consumed on the GL thread (SetBedBoundary creates GL resources).
    private (float X, float Y, float Z, float Diameter, float Sign)? _pendingBedRebuild;
    private (float Width, float Depth)? _pendingBedGridResize;
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
        PointerMoved += (_, e) => _lastPointerPos = e.GetPosition(this);
        KeyDown             += OnKeyDown;
        KeyUp               += OnKeyUp;

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

    /// <summary>Captures the current 3D viewport as PNG bytes (GL color buffer).</summary>
    public Task<byte[]?> CaptureScreenshotAsync() => GlCanvas.CaptureScreenshotPngAsync();

    /// <summary>The GL surface control, for locating the viewport region within the window
    /// when compositing a full-window screenshot.</summary>
    internal Control ViewportSurface => GlCanvas;

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
                    nameof(ViewportViewModel.ShowContactShadows)      or
                    nameof(ViewportViewModel.ContactShadowSize)     or
                    nameof(ViewportViewModel.ContactShadowDarkness) or
                    nameof(ViewportViewModel.ContactShadowBlur)     or
                    nameof(ViewportViewModel.CavityEnabled)         or
                    nameof(ViewportViewModel.CavityMode)            or
                    nameof(ViewportViewModel.CavityModeOption)      or
                    nameof(ViewportViewModel.CavityScreenRidge)     or
                    nameof(ViewportViewModel.CavityScreenValley)    or
                    nameof(ViewportViewModel.CavityWorldRidge)      or
                    nameof(ViewportViewModel.CavityWorldValley)     or
                    nameof(ViewportViewModel.CavityWorldDistance)   or
                    nameof(ViewportViewModel.ShowDimensions)      or
                    nameof(ViewportViewModel.ActiveShaderMode)    or
                    nameof(ViewportViewModel.LightAzimuth)        or
                    nameof(ViewportViewModel.LightElevation)      or
                    nameof(ViewportViewModel.LightIntensity)      or
                    nameof(ViewportViewModel.Exposure)            or
                    nameof(ViewportViewModel.IblIntensity)        or
                    nameof(ViewportViewModel.PbrMaterial)         or
                    nameof(ViewportViewModel.ShowExtrusionMoves)  or
                    nameof(ViewportViewModel.ShowTravelMoves)     or
                    nameof(ViewportViewModel.ShowLightningMoves)  or
                    nameof(ViewportViewModel.ShowWipeMoves)       or
                    nameof(ViewportViewModel.ShowSeam)               or
                    nameof(ViewportViewModel.ShowBead)               or
                    nameof(ViewportViewModel.ShowBeadOverhang)       or
                    nameof(ViewportViewModel.ShowOrientationPreview))
                    GlCanvas.RequestNextFrameRendering();
                else if (pe.PropertyName is nameof(ViewportViewModel.IsLayFlatMode)
                                         or nameof(ViewportViewModel.IsSeamEditorActive)
                                         or nameof(ViewportViewModel.IsBoundaryEditorActive))
                    Cursor = vm.IsLayFlatMode || vm.IsSeamEditorActive || vm.IsBoundaryEditorActive
                        ? new Cursor(StandardCursorType.Cross)
                        : Cursor.Default;
            };
            vm.RenderNeeded       += (_, _) => GlCanvas.RequestNextFrameRendering();
            vm.OnSeamGuidesChanged = () => UpdateSeamGuideMarkers(vm);
            vm.OnBoundaryDraftChanged = () => UpdateBoundaryMarkers(vm);
            vm.OnSliceRequested       = () => RunSliceAsync(vm);
            if (vm.AdditiveSettings is { } addSettings)
            {
                addSettings.OnAutoTiltRequested = rotateMesh => _ = RunAutoTiltAsync(vm, rotateMesh);
                addSettings.OnOptimizeToolpathRequested = () =>
                {
                    if (vm.ActiveScrubToolpath is not { } tpOpt)
                    {
                        addSettings.OptimizeToolpathSummary = "No toolpath — slice first.";
                        return;
                    }
                    var stats = MassiveSlicer.Core.Slicing.ToolpathOptimizer.Optimize(
                        tpOpt, (float)addSettings.BeadWidth);
                    addSettings.OptimizeToolpathSummary = stats.ToString();
                    vm.RequestActiveToolpathReupload?.Invoke();
                };
            }
            vm.OnMillRequested        = () => RunMillAsync(vm);
            vm.OnPreviewDisplacedRequested = () => RunPreviewDisplacedAsync(vm);
            vm.OnGenerateMultiAxisRequested = () => RunMultiAxisMillAsync(vm);
            vm.OnUpdateSliceRequested = () => RunUpdateSliceAsync(vm);
            vm.CanUpdateSlice         = () => FindResliceSource(vm) is not null
                && (_activeScrubNode is null || !_mergedByNode.ContainsKey(_activeScrubNode));
            vm.GetToolpathSnapshot    = GetToolpathSnapshot;
            vm.OnExportKrlRequested   = () => ExportKrlAsync(vm);
            vm.OnSendToRobotRequested = () => SendToRobotAsync(vm);
            vm.ExportKrlToDirectory = (dir, rev) => ExportKrlToDirectoryAsync(vm, dir, rev);
            vm.OnApplyToolpathSeamRequested = () => ApplyToolpathSeam(vm);
            vm.OnMergeToolpathsRequested = () => MergeToolpaths(vm);
            vm.OnSequenceToggleRequested = node => ToggleSequenceSelection(vm, node);
            vm.OnSequenceRangeRequested  = item => SequenceRangeSelect(vm, item);
            StartViewTagTimer(vm);
            vm.GetSequenceCount          = () => _renderer.SelectedToolpathCount;
            vm.GetSpeedSpread = () =>
            {
                var kv = _toolpathByNode.FirstOrDefault();
                if (kv.Value is null) return "no toolpath";
                float min = float.MaxValue, max = float.MinValue; int n = 0;
                foreach (var l in kv.Value.Layers)
                    foreach (var m in l.Moves)
                        if (m.Kind == MoveKind.Extrude)
                        { min = Math.Min(min, m.PrintSpeedScale); max = Math.Max(max, m.PrintSpeedScale); n++; }
                return $"moves={n} scale min={min:F3} max={max:F3} tags={vm.ViewTags.Count} | renderer: {_renderer.DescribeToolpathEntry(kv.Key)}";
            };
            vm.OnMergeScansRequested     = mode => MergeSelectedScans(vm, mode);
            vm.OnMergedSettingsChanged   = () => RebuildMergedToolpath(vm);
            vm.OnOutlinerSelectRequested = node =>
            {
                vm.SetOutlinerSelection(node);
                RequestSceneSelection(vm, node);
            };
            vm.OnApplyModifiersRequested = ownerItem => _ = ApplyModifierStackAsync(vm, ownerItem);
            vm.OnOutlinerMultiScanViewportSync = _ => SyncScanSelectionToRenderer(vm);
            vm.OnScanSelectionChanged = () => SyncScanSelectionToRenderer(vm);
            vm.OnOutlinerToolheadSelected = toolName => ApplyExclusiveToolheadFromOutliner(toolName, vm);
            vm.OnExportScanPointCloudRequested = node => ExportScanPointCloudAsync(vm, node);
            vm.OnExportScanMeshRequested       = node => ExportScanMeshAsync(vm, node);
            vm.GetSelectedSceneNode = () => _renderer.SelectedNode;
            vm.ForceSelectNode = node =>
            {
                _renderer.Select(node);
                vm.SetOutlinerSelection(node);
                UpdateFocusOverlay();
                GlCanvas.RequestNextFrameRendering();
            };
            vm.RequestApplyPendingUiSession = () => ApplyPendingUiSession(vm);
            vm.OnNodeHidden           = node =>
            {
                if (_renderer.SelectedNode is { } sel && node.SelfAndDescendants().Any(n => n == sel))
                {
                    _renderer.Select(null);
                    UpdateFocusOverlay();
                }
            };
            vm.OnFocusRequested       = FocusSelected;
            vm.OnFrameMoveRequested   = FrameCameraToScrubIndex;
            vm.OnDropToPlateRequested = DropToPlate;
            vm.OnRecenterRequested    = RecenterSelected;
            vm.OnUngroupRequested     = UngroupSelected;
            vm.OnExplodeRequested     = ExplodeSelected;
            vm.OnMeshCleanupRequested = () => _ = MeshCleanupSelectedAsync();
            vm.OnCutToolRequested     = () => BeginCutToolInteractive();
            vm.OnCancelCutToolRequested = CancelCutToolInteractive;
            vm.OnPerformCutToolRequested = () => PerformCutToolInteractive();
            vm.OnScrubIkRequested  = ScrubIk;
            vm.OnSimScrubRequested = SimScrubIk;
            vm.OnSimVideoExportRequested = () => _ = ExportSimVideoAsync(vm);
            vm.OnAddTcpKeyframeRequested    = () => AddTcpKeyframeAtCurrentIndex(vm);
            vm.OnClearTcpKeyframesRequested = () => ClearTcpKeyframes(vm);
            vm.OnKeyframeLaneClicked      = i => JumpToTcpKeyframe(vm, i);
            vm.OnKeyframeInfluenceDragged = (i, left, px, commit) =>
                DragTcpKeyframeInfluence(vm, i, left, px, commit);
            vm.OnFrameAllRequested = FrameAll;
            WireRealtimeSlicing(vm);
            vm.OnViewPresetRequested = ApplyViewPreset;
            vm.OnSlicePlaneViewerChanged = active => ApplySlicePlaneViewerCamera(active);
            vm.OnEnsureEditScrub = () => EnsureScrubArmedForEdit(vm);
            vm.OnApplyOffsetPathRequested = () => ApplyOffsetPathToSelection(vm);
            vm.GetCameraState = () =>
            {
                var c = _renderer.Camera;
                return new MassiveSlicer.Core.Models.CameraView
                {
                    Azimuth        = c.Azimuth,
                    Elevation      = c.Elevation,
                    Radius         = c.Radius,
                    TargetX        = c.Target.X,
                    TargetY        = c.Target.Y,
                    TargetZ        = c.Target.Z,
                    IsOrthographic = c.IsOrthographic,
                };
            };
            vm.ApplyCameraState = view =>
            {
                _renderer.Camera.Azimuth        = view.Azimuth;
                _renderer.Camera.Elevation      = view.Elevation;
                _renderer.Camera.Radius         = view.Radius;
                _renderer.Camera.Target         = new Vector3(view.TargetX, view.TargetY, view.TargetZ);
                _renderer.Camera.IsOrthographic = view.IsOrthographic;
                GlCanvas.RequestNextFrameRendering();
            };
            vm.RequestActiveToolpathReupload = () =>
            {
                if (_activeScrubNode is not { } node) return;
                if (!_toolpathByNode.TryGetValue(node, out var tpUp)) return;
                _rawToolpathByNode.TryGetValue(node, out var rawUp);
                var snapUp = GetToolpathSnapshot(node);
                if (snapUp is null) return;
                vm.PendingToolpathReplace.Enqueue(new PendingToolpathEntry
                {
                    Toolpath      = tpUp,
                    RawToolpath   = rawUp ?? tpUp,
                    Node          = node,
                    BeadWidth     = snapUp.BeadWidth,
                    LayerHeight   = snapUp.LayerHeight,
                    MaterialColor = snapUp.MaterialColor,
                });
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
                    // Data may have arrived while the stopwatch was paused in the
                    // wait-for-validation branch below — resume the clock.
                    if (!_playbackStopwatch.IsRunning)
                        _playbackStopwatch.Start();

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
                        float? e1 = null;
                        if (_e1MmByNode.TryGetValue(node, out var e1s) && e1s.Length > 0)
                        {
                            float e0 = e1s[Math.Clamp(prevIdx, 0, e1s.Length - 1)];
                            float e1b = e1s[Math.Clamp(moveIdx, 0, e1s.Length - 1)];
                            e1 = e0 + (e1b - e0) * t;
                        }
                        SetRobotAnglesDirectly(interp, e1);
                    }
                }
                else
                {
                    // Validation not yet complete — pause the stopwatch and wait.
                    // The play button is disabled while IsValidating, so this branch
                    // only fires in the rare window between button enable and first tick.
                    _playbackStopwatch.Stop();
                    // Self-heal: a toolpath replace clears playback IK data without a
                    // re-validate. If no validation is running, kick one so play can
                    // resume instead of waiting forever (ValidateToolpathAsync dedups).
                    if (!pvm.IsValidating && _toolpathByNode.TryGetValue(node, out var healTp))
                        ValidateToolpathAsync(node, healTp);
                }
            };

            vm.ResetViewportOverlayState();
            UpdateFocusOverlay();
        }

        vm.OnAddStructuralSupportRequested = () => AddStructuralSupportFromSelection(vm);
        if (vm.AdditiveSettings is { } addHook)
            addHook.OnStructuralSupportsChanged = () => GlCanvas.RequestNextFrameRendering();
        vm.OnDevModeChanged = ApplyDevModeSelectability;
        // Hard policy: cell fixtures (print bed, stands, docks, rotary) are only
        // selectable in dev mode — enforced at the renderer so no path around the
        // Selectable flag (e.g. manually unlocking the outliner padlock) can select them.
        // User imports/scans are exempt: they live UNDER the bed node (to ride E1).
        _renderer.SelectionFilter = n => vm.IsDevMode
            || vm.IsUserModelSceneNode(n)
            || FindDevNodeRoot(n) is null;
        vm.DebugPickAtViewport = (fx, fy) => DebugPickAtViewport(vm, fx, fy);
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
                    {
                        MassiveSlicer.Core.IO.CellLoader.SaveBedCenter(
                            path, (float)x, (float)y, (float)z,
                            dia > 0 ? (float)dia : (float?)null, (float)sign);

                        // On a rotary cell (LFAM 3), follow the calibrated axis centre in X/Y only.
                        // Preserve the existing basePos.z — the table HEIGHT is a fixed model property,
                        // not something the axis-centre fit measures (writing the fit's Z drops the bed).
                        if (vm2.ActiveCell?.RotaryBed is { } rbCfg)
                        {
                            var rw = vm2.ActiveCell.Robot.WorldPosition;
                            float keepZ = rbCfg.BasePos.Length > 2 ? rbCfg.BasePos[2] : (float)z - rw.Z;
                            float[] basePos = [ (float)x - rw.X, (float)y - rw.Y, keepZ ];
                            MassiveSlicer.Core.IO.CellLoader.SaveRotaryBedTransform(
                                path, basePos, rbCfg.BaseAbc, out _);
                        }
                    }
                }
            };
            vm.OnBedGridSizeEdited = (w, d) =>
            {
                _pendingBedGridResize = ((float)w, (float)d);
                vm.NotifyRenderNeeded();
                if (vm.ActiveCellPath is { } path)
                    MassiveSlicer.Core.IO.CellLoader.SaveBedGridSize(path, (float)w, (float)d, out _);
            };
            robot.OnBedOrientationEdited = deg =>
            {
                if (DataContext is not ViewportViewModel vm2 || vm2.ActiveCellPath is not { } path)
                    return;
                if (!MassiveSlicer.Core.IO.CellLoader.SaveRotaryOrientation(path, (float)deg, out _))
                    return;
                MassiveSlicer.App.CellSceneCache.Invalidate(path);
                vm2.OnDevCellReloadRequested?.Invoke(path);
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
            var old = node.LocalTransform;
            var lt = node.LocalTransform;
            lt.Row3 = new Vector4((float)x, (float)y, (float)z, 1f);
            node.LocalTransform = lt;
            MirrorTypedTransformDelta(vm, node, old);
            GlCanvas.RequestNextFrameRendering();
            RevalidateSelectedToolpath();
            SchedulePanelTransformUndo(vm, node, "Move");
        };
        vm.OnSelectionRotated = (a, b, c) =>
        {
            if (_renderer.SelectedNode is not { } node) return;
            var old  = node.LocalTransform;
            var lt   = node.LocalTransform;
            float sX = lt.Row0.Xyz.Length;
            float sY = lt.Row1.Xyz.Length;
            float sZ = lt.Row2.Xyz.Length;
            var rt = MassiveSlicer.Core.Kinematics.KukaIkSolver.AbcToMatrix((float)a, (float)b, (float)c);
            lt.Row0 = new Vector4(rt.M11 * sX, rt.M12 * sX, rt.M13 * sX, 0f);
            lt.Row1 = new Vector4(rt.M21 * sY, rt.M22 * sY, rt.M23 * sY, 0f);
            lt.Row2 = new Vector4(rt.M31 * sZ, rt.M32 * sZ, rt.M33 * sZ, 0f);
            node.LocalTransform = lt;
            MirrorTypedTransformDelta(vm, node, old);
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

            additive.OnOpenCurvedBoundaryEditorRequested = () => OpenCurvedBoundaryEditor(vm, additive);
            additive.OnImportCurvedBoundariesRequested  = () => ImportCurvedBoundariesAsync(vm, additive);
            additive.OnHomePositionSelected = angles => vm.Robot?.ApplyViewportJoints(angles);

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
                                    or nameof(AdditiveSettingsViewModel.Method)
                                    or nameof(AdditiveSettingsViewModel.XBracingEnabled)
                                    or nameof(AdditiveSettingsViewModel.XBracingPlaneTiltY)
                                    or nameof(AdditiveSettingsViewModel.XBracingPlaneTiltX)
                                    or nameof(AdditiveSettingsViewModel.XBracingProjectionType)
                                    or nameof(AdditiveSettingsViewModel.XBracingCylinderDiameterMm)
                                    or nameof(AdditiveSettingsViewModel.XBracingCylinderX)
                                    or nameof(AdditiveSettingsViewModel.XBracingCylinderY)
                                    or nameof(AdditiveSettingsViewModel.XBracingCylinderFlipDirection)
                                    or nameof(AdditiveSettingsViewModel.XBracingShowHelper))
                    GlCanvas.RequestNextFrameRendering();

                // Live-effector master toggle: hide inert handles while off, restore
                // each handle's outliner eye state on re-enable.
                if (pe.PropertyName is nameof(AdditiveSettingsViewModel.EffectorEnabled))
                {
                    vm.SetEffectorHandlesEnabled(additive.EffectorEnabled);
                    GlCanvas.RequestNextFrameRendering();
                }

                // Re-solve IK + re-validate when toolhead orientation or E1 rail settings change.
                // E1 planning is O(n) envelope samples (not × multi full DLS per point), so it is
                // safe to re-run; planned E1 is used for both reachability and simulation.
                if (pe.PropertyName is nameof(AdditiveSettingsViewModel.ToolheadA)
                                    or nameof(AdditiveSettingsViewModel.ToolheadB)
                                    or nameof(AdditiveSettingsViewModel.ToolheadC)
                                    or nameof(AdditiveSettingsViewModel.E1MotionEnabled)
                                    or nameof(AdditiveSettingsViewModel.E1YPlusMm)
                                    or nameof(AdditiveSettingsViewModel.E1YMinusMm))
                {
                    if (vm.IsToolpathSelected)
                        ScrubIk(vm.ToolpathScrubIndex);

                    if (_activeScrubNode is { } nd
                        && _toolpathByNode.TryGetValue(nd, out var tp))
                    {
                        _validationCts?.Cancel();
                        _validationDone = false;
                        _validationNode = null; // force re-key so E1 plan is not skipped
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
                                    or nameof(AdditiveSettingsViewModel.SmoothRotationMaxRateDegPerMm)
                                    or nameof(AdditiveSettingsViewModel.OrientationFollowPercent)
                                    or nameof(AdditiveSettingsViewModel.OrientationMaxTiltDeg)
                                    or nameof(AdditiveSettingsViewModel.FirstLayerZeroTilt)
                                    or nameof(AdditiveSettingsViewModel.LayerLeanPercent)
                                    or nameof(AdditiveSettingsViewModel.LayerLeanMaxTiltDeg)
                                    or nameof(AdditiveSettingsViewModel.LayerSpeedAdaptEnabled)
                                    or nameof(AdditiveSettingsViewModel.LayerSpeedBasisDisplay)
                                    or nameof(AdditiveSettingsViewModel.LayerSpeedMinMmS)
                                    or nameof(AdditiveSettingsViewModel.LayerSpeedMaxMmS)
                                    or nameof(AdditiveSettingsViewModel.PrintSpeed))
                    RebuildToolpathsFromRaw(additive);
            };

            additive.OnSimulateThermalRequested = () => RunThermalSimulation(vm, additive);
            vm.Erp.PricingChanged += () => Dispatcher.UIThread.Post(() =>
            {
                if (_activeScrubNode is { } statsNode && _toolpathByNode.TryGetValue(statsNode, out var statsTp))
                    ApplyToolpathStats(vm, statsTp);
            });
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
        if (!vm.IsSeamEditorActive)
        {
            // No ghost left behind, and no stale hold-position leaking into the next session.
            _renderer.SetSeamGuidePreview(null);
            _lastSeamGuideSurfacePoint = null;
        }

        var (zLo, zHi) = SeamGuideHeightRange(vm);
        _renderer.SetSeamGuides(guides, vm.SelectedSeamGuideIndex, zLo, zHi);
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Z extent the seam guide columns span: the visible user models' world height, falling
    /// back to a bed-height stub when nothing is loaded.
    /// </summary>
    private (float Lo, float Hi) SeamGuideHeightRange(ViewportViewModel vm)
    {
        float lo = float.MaxValue, hi = float.MinValue;

        foreach (var item in vm.EnumerateUserModelItems())
        {
            if (!item.Visible) continue;
            var (min, max) = ImportHelper.ComputeSubtreeWorldAabb(item.Node);
            if (min.Z > max.Z) continue;
            lo = MathF.Min(lo, min.Z);
            hi = MathF.Max(hi, max.Z);
        }

        // Toolpath nodes carry no mesh, so the AABB pass above misses them. After slicing the
        // model is usually hidden and only the toolpath is shown — without this the range fell
        // back to a fixed bed+1000mm stub and the columns overshot the part.
        foreach (var (node, tp) in _toolpathByNode)
        {
            if (!node.Visible || tp.Layers.Count == 0) continue;
            var world = node.WorldTransform;
            foreach (var layer in tp.Layers)
            {
                if (layer.Moves.Count == 0) continue;
                var p = TkVector3.TransformPosition(
                    new TkVector3(layer.Moves[0].From.X, layer.Moves[0].From.Y, layer.Moves[0].From.Z),
                    world);
                lo = MathF.Min(lo, p.Z);
                hi = MathF.Max(hi, p.Z);
            }
        }

        if (lo > hi)
        {
            float bedZ = _bedBaseMarker.Z;
            return (bedZ, bedZ + 1000f);
        }
        return (lo, hi);
    }

    private void UpdateBoundaryMarkers(ViewportViewModel vm)
    {
        if (_boundaryEditorMesh is null)
        {
            _renderer.SetCurvedBoundaryLoops([], []);
            GlCanvas.RequestNextFrameRendering();
            return;
        }

        IReadOnlyList<int> lowIdx, highIdx;
        if (vm.IsBoundaryEditorActive)
        {
            lowIdx  = vm.BoundaryLowDraft.ToList();
            highIdx = vm.BoundaryHighDraft.ToList();
        }
        else
        {
            lowIdx  = vm.AdditiveSettings?.BuildCurvedLowBoundaryList()  ?? [];
            highIdx = vm.AdditiveSettings?.BuildCurvedHighBoundaryList() ?? [];
        }

        var lowPts = lowIdx
            .Where(i => i >= 0 && i < _boundaryEditorMesh.VertexCount)
            .Select(i => _boundaryEditorMesh.Vertices[i])
            .Select(v => new TkVector3(v.X, v.Y, v.Z))
            .ToList();
        var highPts = highIdx
            .Where(i => i >= 0 && i < _boundaryEditorMesh.VertexCount)
            .Select(i => _boundaryEditorMesh.Vertices[i])
            .Select(v => new TkVector3(v.X, v.Y, v.Z))
            .ToList();
        _renderer.SetCurvedBoundaryLoops(lowPts, highPts);
        GlCanvas.RequestNextFrameRendering();
    }

    private void OpenCurvedBoundaryEditor(ViewportViewModel vm, AdditiveSettingsViewModel additive)
    {
        var sourceItem = vm.ResolveActivePrintObjectItem();
        if (sourceItem?.Node is null) return;

        var snapshots = CollectMeshSnapshots(sourceItem, requireVisible: false);
        if (snapshots.Count == 0) return;

        var flatMeshes = new List<NVec3[]>();
        foreach (var (positions, indices, world) in snapshots)
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

        _boundaryEditorMesh = MeshGraph.Build(flatMeshes);
        IReadOnlyList<int> low, high;
        if (additive.BuildCurvedLowBoundaryList().Count > 0 && additive.BuildCurvedHighBoundaryList().Count > 0)
        {
            low  = additive.BuildCurvedLowBoundaryList();
            high = additive.BuildCurvedHighBoundaryList();
        }
        else
        {
            (low, high) = BoundaryAutoDetect.Detect(
                _boundaryEditorMesh, (float)additive.CurvedAutoDetectBandMm);
        }

        vm.BeginBoundaryEditor(low, high);
        UpdateBoundaryMarkers(vm);
    }

    private async Task ImportCurvedBoundariesAsync(ViewportViewModel vm, AdditiveSettingsViewModel additive)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import curved slicing boundaries",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] },
            ],
        });
        if (files.Count == 0) return;

        try
        {
            IReadOnlyList<int> low, high;
            if (files.Count >= 2)
            {
                (low, high) = BoundaryJsonIO.LoadPair(files[0].Path.LocalPath, files[1].Path.LocalPath);
            }
            else
            {
                (low, high) = BoundaryJsonIO.LoadCombined(files[0].Path.LocalPath);
            }

            additive.SetCurvedBoundaries(low, high);
            additive.CurvedBoundarySourceDisplay = "JSON Import";
            if (_boundaryEditorMesh is not null && vm.IsBoundaryEditorActive)
                vm.SetBoundaryDraft(low, high);
            UpdateBoundaryMarkers(vm);
        }
        catch
        {
            // Import errors are surfaced via empty boundary state; user can retry.
        }
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

    /// <summary>
    /// Seam guide position under the cursor, always ON the model: a direct face hit when the ray
    /// crosses the model, else the nearest point of the model wall in screen space. Deliberately
    /// does NOT fall back to the bed like <see cref="TryPlaceSeamGuide"/> — a guide belongs on the
    /// wall it seams, and this lets it slide along the wall as the mouse moves past the silhouette.
    /// </summary>
    private TkVector3? _lastSeamGuideSurfacePoint;

    private bool TrySeamGuideOnModel(Ray ray, float mx, float my, out TkVector3 hit)
    {
        // Exact ray/face hit while the cursor is over the model: the guide tracks the wall
        // smoothly and is by construction ON the surface.
        var (node, _, meshHit) = _renderer.PickFace(ray);
        if (node is not null && !_renderer.IsToolpathNode(node))
        {
            _lastSeamGuideSurfacePoint = meshHit;
            hit = meshHit;
            return true;
        }

        // Cursor left the silhouette: hold the last on-surface position. Snapping to the
        // nearest sampled vertex instead made the column jitter between scattered vertices
        // and occasionally land off the visible wall.
        if (_lastSeamGuideSurfacePoint is { } held)
        {
            hit = held;
            return true;
        }

        hit = default;
        return false;
    }


    /// <summary>True after the GL viewport failed to initialise (e.g. GLSL too old).</summary>
    private bool _glInitFailed;
    private string? _glInitError;

    /// <summary>Non-null when the 3D viewport could not start on this GPU.</summary>
    public string? GlInitError => _glInitError;

    private void OnRender(TimeSpan delta, int w, int h)
    {
        // Weak / embedded GPUs (some Linux ARM boards) only expose GLSL 1.40 or ES 3.00.
        // Our shaders require #version 330 core — fail the viewport, not the whole app.
        if (_glInitFailed)
            return;

        try
        {
            _renderer.Initialise();
        }
        catch (Exception ex)
        {
            _glInitFailed = true;
            _glInitError = ex.Message;
            System.Console.Error.WriteLine(
                "[MassiveSlicer] 3D viewport disabled (OpenGL/GLSL unsupported on this GPU):\n" + ex.Message);
            return;
        }

        if (_vm is { } vm)
        {
            _renderer.ShowGrid            = vm.ShowGrid;
            _renderer.ShowAxes            = vm.ShowAxes;
            _renderer.ShowBedGrid         = vm.ShowBedGrid;
            _renderer.ShowContactShadows      = vm.ShowContactShadows;
            _renderer.ContactShadowSize       = vm.ContactShadowSize;
            _renderer.ContactShadowDarkness   = vm.ContactShadowDarkness;
            _renderer.ContactShadowBlur       = vm.ContactShadowBlur;
            _renderer.CavityEnabled              = vm.CavityEnabled;
            _renderer.CavityShadeToolpaths       = vm.CavityShadeToolpaths;
            _renderer.CavityShadeImportedMeshes  = vm.CavityShadeImportedMeshes;
            _renderer.CavityMode              = vm.CavityMode;
            _renderer.CavityScreenRidge       = vm.CavityScreenRidge;
            _renderer.CavityScreenValley      = vm.CavityScreenValley;
            _renderer.CavityWorldRidge        = vm.CavityWorldRidge;
            _renderer.CavityWorldValley       = vm.CavityWorldValley;
            _renderer.CavityWorldDistance     = vm.CavityWorldDistance;
            _renderer.ShowExtrusionMoves = vm.ShowExtrusionMoves;
            _renderer.ShowTravelMoves    = vm.ShowTravelMoves;
            _renderer.ShowLightningMoves = vm.ShowLightningMoves;
            _renderer.ShowWipeMoves      = vm.ShowWipeMoves;
            _renderer.ShowSeam           = vm.ShowSeam;
            _renderer.ShowBead          = vm.ShowBead;
            _renderer.ShowBeadOverhang       = vm.ShowBeadOverhang;
            _renderer.ShowOrientationPreview = vm.ShowOrientationPreview;
            // Scrub hides not-yet-printed moves whenever a scrub session is live
            // (selection or sticky timeline), matching what the user can see.
            bool scrubLive = vm.IsToolpathSelected || vm.IsScrubSessionActive;
            int scrubHi = scrubLive ? vm.ToolpathScrubIndex : int.MaxValue;
            int scrubLo = scrubLive ? vm.ToolpathScrubLowIndex : 0;
            // Empty window [lo, hi) with hi<=lo draws zero vertices — recover to the
            // full stack so a stale scrub (index 0) never blanks the toolpath.
            if (scrubLive && vm.ToolpathScrubMax > 0 && scrubHi <= Math.Max(0, scrubLo))
            {
                scrubHi = vm.ToolpathScrubMax;
                scrubLo = 0;
            }
            _renderer.ToolpathActiveScrubIndex = scrubHi;
            _renderer.ToolpathActiveScrubStart = scrubLo;
            // Apply the window to this node even when the mesh/TCP is selected.
            _renderer.ToolpathActiveScrubNode = scrubLive ? _activeScrubNode : null;
            // 2D Slice Plane Viewer — top-down neighbour-layer context in edit mode.
            // Multi-pass must NOT depend on scrubLive alone: edit mode often has no
            // outliner selection ("Nothing selected") which previously fell through to
            // the normal dual-slider window and stacked every layer into a solid blob.
            bool slicePlane = vm.IsSlicePlaneViewerActive && vm.IsPaintEditOpen;
            if (slicePlane)
            {
                // Keep a scrub target armed so layer ends + multi-pass have a toolpath.
                if (_activeScrubNode is null || vm.ActiveScrubToolpath is null
                    || vm.ScrubLayerEnds is null || vm.ScrubLayerEnds.Length == 0)
                    EnsureScrubArmedForEdit(vm);

                // Contact shadows fight the clean 2D readout (save before hide flag flips).
                if (!_sliceViewerHidScene)
                    _sliceViewerSavedContactShadows = _renderer.ShowContactShadows;
                _renderer.ShowContactShadows = false;

                // Re-assert every frame: robot / env / solid meshes stay hidden.
                EnforceSlicePlaneSceneHiding(hide: true);
                // Camera lock every frame: top-down ortho, no azimuth spin (pan+zoom only).
                // Elevation exactly 90° so the pole up-vector matches locked azimuth with
                // no residual in-plane twist from the near-pole Gram-Schmidt path.
                var cam = _renderer.Camera;
                cam.Elevation = 90f;
                if (_sliceViewerHasSavedCamera)
                    cam.Azimuth = _sliceViewerLockedAzimuth;
                cam.IsOrthographic = true;

                // Scrub layers/timeline: slide the target with the active slice so the
                // geometry stays framed; Radius (zoom) is left untouched.
                FollowSliceLayerCamera(vm, cam);

                // Measurement grid: one bead spacing (half-bead densified into a white sheet).
                float bead = (float)(vm.AdditiveSettings?.BeadWidth ?? 6.0);
                if (bead < 0.1f) bead = 6f;
                _renderer.SlicePlaneGridSpacingMm = MathF.Max(bead, 3f);

                // Grid Z must track real extrusion height (multiplanar used to store
                // march parameter h as layer.Z ≈ −1950, leaving the 2D view empty).
                float gridZ = cam.Target.Z;
                float cx = cam.Target.X, cy = cam.Target.Y;
                if (vm.ActiveScrubToolpath is { Layers.Count: > 0 } tp)
                {
                    int li = Math.Clamp(vm.CurrentScrubLayerIndex, 0, tp.Layers.Count - 1);
                    if (TryGetSliceLayerWorldCenter(tp.Layers[li], out var layerCenter))
                    {
                        gridZ = layerCenter.Z;
                        cx = layerCenter.X;
                        cy = layerCenter.Y;
                    }
                    else
                        gridZ = tp.Layers[li].Z;
                }
                _renderer.SlicePlaneGridZ = gridZ;
                _renderer.SlicePlaneGridCenterX = cx;
                _renderer.SlicePlaneGridCenterY = cy;
            }
            else if (_sliceViewerHidScene)
            {
                EnforceSlicePlaneSceneHiding(hide: false);
                _renderer.ShowContactShadows = _sliceViewerSavedContactShadows;
                _sliceFollowLayerIndex = -1;
            }
            else
            {
                _sliceFollowLayerIndex = -1;
            }

            _renderer.SlicePlaneViewerActive = slicePlane;
            _renderer.SlicePlaneLayerIndex = vm.CurrentScrubLayerIndex;
            _renderer.SlicePlaneBelowLayerCount = vm.SlicePlaneGhostLayers;
            _renderer.SlicePlaneShowAllBelow = vm.SlicePlaneShowAllGhosts;
            // Prefer live ends from ActiveScrubToolpath so multi-pass always has data.
            int[]? sliceEnds = null;
            if (slicePlane && vm.ActiveScrubToolpath is { Layers.Count: > 0 } scrubTp)
            {
                sliceEnds = new int[scrubTp.Layers.Count];
                int acc = 0;
                for (int i = 0; i < scrubTp.Layers.Count; i++)
                {
                    acc += scrubTp.Layers[i].Moves.Count;
                    sliceEnds[i] = acc;
                }
            }
            else if (slicePlane)
                sliceEnds = vm.ScrubLayerEnds;
            _renderer.SlicePlaneLayerEnds = sliceEnds;
            if (slicePlane && _activeScrubNode is not null)
                _renderer.ToolpathActiveScrubNode = _activeScrubNode;
            // Edit Point mode: every bead midpoint, not just contour seam ends.
            // Slice plane viewer is pure centre-line readout — skip dense points.
            _renderer.ShowAllPathPoints =
                vm.IsPaintEditOpen && vm.PaintPointGranularityActive && !slicePlane;
            // Edit Path mode: depth-cued line width (near 2.5x) + far fade.
            // Top-down slice view uses flat line widths instead.
            _renderer.ShowDepthAwareLines =
                vm.IsPaintEditOpen && vm.PaintPathGranularityActive && !slicePlane;
            // Edit mode: force pure-white beads so the whole toolpath reads as
            // "editable" and paint/selection highlights stand out against it.
            if (vm.IsPaintEditOpen)
                _renderer.SetToolpathBeadColor(new TkVector3(1f, 1f, 1f));
            else
            {
                _paintSelectedLine = null;   // clear sticky selection when leaving edit
                _paintSelectedId = null;
                ClearPaintPointAnchor();
                _renderer.SetToolpathBeadColor(
                    new TkVector3(vm.BeadColor.X, vm.BeadColor.Y, vm.BeadColor.Z));
            }
            _renderer.SetToolpathColorMode(vm.ToolpathColorMode);
            _renderer.SetToolpathColors(
                new TkVector3(vm.ToolpathExtrudeColor.X,     vm.ToolpathExtrudeColor.Y,     vm.ToolpathExtrudeColor.Z),
                new TkVector3(vm.ToolpathTravelColor.X,      vm.ToolpathTravelColor.Y,      vm.ToolpathTravelColor.Z),
                new TkVector3(vm.ToolpathSeamColor.X,        vm.ToolpathSeamColor.Y,        vm.ToolpathSeamColor.Z),
                new TkVector3(vm.ToolpathUnselectedColor.X,  vm.ToolpathUnselectedColor.Y,  vm.ToolpathUnselectedColor.Z),
                new TkVector3(vm.ToolpathWipeColor.X,        vm.ToolpathWipeColor.Y,        vm.ToolpathWipeColor.Z),
                new TkVector3(vm.ToolpathRetractionColor.X,  vm.ToolpathRetractionColor.Y,  vm.ToolpathRetractionColor.Z));
            _renderer.GizmoEnabled   = vm.ActiveGizmoModeInternal != GizmoMode.None;
            _renderer.GizmoMode      = vm.ActiveGizmoModeInternal;
            // Per-view display profiles: the view pills own background darkness and
            // shader mode (line views default to dark + MatteBlack, user-overridable).
            _renderer.DarkMattePresentation = vm.DarkViewportBackground;
            _renderer.ShaderMode         = vm.ActiveShaderMode;
            _renderer.LayerPreviewHeight = (float)(vm.AdditiveSettings?.LayerHeight ?? 3.0);
            bool layerPreview = vm.AdditiveSettings?.ShowLayerPreview ?? false;
            var layerTarget = layerPreview ? vm.ResolveActivePrintObjectItem()?.Node : null;
            if (layerPreview != _lastOutlinerLayerPreview || layerTarget != _lastLayerPreviewTargetNode)
            {
                _lastOutlinerLayerPreview    = layerPreview;
                _lastLayerPreviewTargetNode  = layerTarget;
                vm.SyncLayerPreviewFlags(layerPreview);
                _renderer.InvalidateShaderAppearance();
                if (layerPreview && layerTarget is not null)
                    _ = ComputeLayerPreviewAsync(vm);
            }
            bool showBackFaces = vm.ShowBackFaces;
            var backFaceTarget = vm.ResolveActivePrintObjectItem()?.Node;
            if (showBackFaces != _lastShowBackFaces || backFaceTarget != _lastBackFaceTargetNode)
            {
                _lastShowBackFaces     = showBackFaces;
                _lastBackFaceTargetNode = backFaceTarget;
                vm.SyncBackFaceFlags(showBackFaces);
                GlCanvas.RequestNextFrameRendering();
            }
            _renderer.LightAzimuth   = vm.LightAzimuth;
            _renderer.LightElevation = vm.LightElevation;
            _renderer.LightIntensity = vm.LightIntensity;
            _renderer.Exposure       = vm.Exposure;
            _renderer.IblIntensity   = vm.IblIntensity;
            _renderer.SyncPbrMaterial(vm.PbrMaterial);

            if (!MassiveSlicer.Core.IO.AssetPaths.BackdropPathsEqual(
                    _renderer.BackdropPath, vm.ActiveBackdropPath))
            {
                _renderer.SetBackdrop(vm.ActiveBackdropPath);
                _renderer.InvalidateShaderAppearance();
            }
            _renderer.BackdropBlur     = vm.BackdropBlur;
            _renderer.BackdropOpacity  = vm.BackdropOpacity;
            _renderer.ShowTcpFrame     = vm.ShowTcpFrame;
            _renderer.ToolpathLineOpacity = vm.ToolpathLineOpacity;
            _renderer.ToolpathSimProgress = vm.SimRenderProgress;
            _renderer.ToolpathFullAppearance = vm.ViewMode != "Body";

            while (vm.PendingCellSwap.TryDequeue(out var swap))
            {
                if (swap.Generation > 0 && swap.Generation < vm.AcceptedCellSwapGeneration)
                    continue;
                ApplyCellSwap(swap, vm);
            }

            if (ProcessCellGpuUploadQueue())
                GlCanvas.RequestNextFrameRendering();

            while (vm.PendingLayerPreview.TryDequeue(out var lp))
                _renderer.SetLayerPreview(lp.zBounds, lp.heights);

            while (vm.PendingRecenterJobs.TryDequeue(out var recenterJob))
                ProcessRecenterJob(vm, recenterJob);

            while (vm.PendingModelRefresh.TryDequeue(out var refreshed))
            {
                RefreshSubtreeGpuMeshes(refreshed);
                _renderer.InvalidateShaderAppearance();
                GlCanvas.RequestNextFrameRendering();
            }

            while (vm.PendingRemoveNodes.TryDequeue(out var removing))
            {
                _toolpathByNode.TryRemove(removing, out _);
                _rawToolpathByNode.TryRemove(removing, out _);
                if (ReferenceEquals(removing, _activeScrubNode) && _vm is { } vmRm)
                {
                    _activeScrubNode = null;
                    Dispatcher.UIThread.Post(() =>
                    {
                        vmRm.IsScrubSessionActive = false;
                        vmRm.ResetScrubIndex(0, null);
                    });
                }
                _toolpathMetaByNode.TryRemove(removing, out _);
                _mergedByNode.TryRemove(removing, out _);
                _toolpathOriginByNode.TryRemove(removing, out _);
                _scrubCacheByNode.TryRemove(removing, out _);
                _ikSolutionsByNode.TryRemove(removing, out _);
                _moveTimesMsByNode.TryRemove(removing, out _);
                _collisionByNode.TryRemove(removing, out _);
                _singularityByNode.TryRemove(removing, out _);
                _e1MmByNode.TryRemove(removing, out _);
                _renderer.RemoveToolpathIfExists(removing);
                GpuMeshCache.ReleaseSubtree(removing);
                // Detach from the node's actual parent — scans live under the rotary pivot, not SceneRoot.
                (removing.Parent ?? _renderer.SceneRoot).RemoveChild(removing);
                if (_renderer.SelectedNode is not null &&
                    removing.SelfAndDescendants().Any(n => n == _renderer.SelectedNode))
                    _renderer.Select(null);
            }

            while (vm.PendingNodes.TryDequeue(out var incoming))
            {
                MarkUserImportSubtree(incoming);
                AttachUserImportToCell(incoming);
                UploadPendingMeshes(incoming);
                MarkUserImportSubtree(incoming);
                _renderer.InvalidateShaderAppearance();

                if (_fkController is null)
                    _fkController = RobotFkController.TryBuild(incoming,
                        vm.ActiveCell?.Robot.Joints ?? []);

                _renderer.Select(incoming);
                Dispatcher.UIThread.Post(UpdateFocusOverlay);
            }

            // Scans and imports destined for the rotary turntable: parent under the E1 pivot so they
            // rotate with the bed. Their LocalTransform is a WORLD pose; convert it to pivot-local
            // (World = Local * ParentWorld ⇒ Local = World * ParentWorld⁻¹) so the world pose is
            // preserved at the current E1 and every later E1 change rotates the content with the table.
            while (vm.PendingRotaryNodes.TryDequeue(out var rotaryChild))
            {
                MarkUserImportSubtree(rotaryChild);
                if (_rotaryBedPivot is { } pivot)
                {
                    rotaryChild.LocalTransform = rotaryChild.LocalTransform * pivot.WorldTransform.Inverted();
                    pivot.AddChild(rotaryChild);
                }
                else
                    _renderer.SceneRoot.AddChild(rotaryChild);   // fallback: no pivot this cell

                UploadPendingMeshes(rotaryChild);
                MarkUserImportSubtree(rotaryChild);
                _renderer.InvalidateShaderAppearance();
                _renderer.Select(rotaryChild);
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is ViewportViewModel syncVm)
                        syncVm.SetOutlinerSelection(rotaryChild);
                    UpdateFocusOverlay();
                });
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
                toolNode.PickTier       = PickTier.Content;
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
                swap.Node.PickTier       = PickTier.Content;
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

            bool uploadedPending = false;
            while (vm.PendingToolpath.TryDequeue(out var entry))
            {
                uploadedPending = true;
                UploadToolpathEntry(entry, addToScene: true);
                // Keep the current selection (usually the model) — auto-selecting the
                // toolpath would flip the sidebar to the Toolpath view mid-workflow.
                var adoptNode = entry.Node;
                var adoptTp   = entry.Toolpath;
                // When restoring a saved UI session, skip auto-adopt jump-to-end —
                // the session restore will arm the correct toolpath and scrub window.
                bool deferAdopt = vm.PendingUiSession is not null;
                Dispatcher.UIThread.Post(() =>
                {
                    // Adopt the new toolpath as the live scrub session so the timeline
                    // shows immediately without requiring a selection.
                    // Also re-arm while edit mode is open so the LAYERS dual-slider
                    // picks up the real layer count (otherwise it stays at 1–2).
                    if (!deferAdopt
                        && DataContext is ViewportViewModel vmAdopt
                        && (_activeScrubNode is null || vmAdopt.IsPaintEditOpen))
                    {
                        bool sameNode = ReferenceEquals(_activeScrubNode, adoptNode);
                        bool canPreserve = sameNode
                            && vmAdopt.IsScrubSessionActive
                            && vmAdopt.ToolpathScrubIndex > 0
                            && vmAdopt.ToolpathScrubIndex > vmAdopt.ToolpathScrubLowIndex;
                        _activeScrubNode = adoptNode;
                        vmAdopt.ResetScrubIndex(
                            adoptTp.Layers.Sum(l => l.Moves.Count),
                            adoptTp,
                            preservePosition: canPreserve);
                        vmAdopt.IsScrubSessionActive = true;
                    }
                    UpdateFocusOverlay();
                });
            }

            // After all workspace toolpaths are on the GPU, restore edit mode / tools /
            // isolated layers from the .mass file.
            if (uploadedPending
                && vm.PendingUiSession is not null
                && vm.PendingToolpath.IsEmpty)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is ViewportViewModel vmSession)
                        ApplyPendingUiSession(vmSession);
                });
            }

            while (vm.PendingToolpathReplace.TryDequeue(out var entry))
            {
                _ikSolutionsByNode.TryRemove(entry.Node, out _);
                _moveTimesMsByNode.TryRemove(entry.Node, out _);
                _collisionByNode.TryRemove(entry.Node, out _);
                _singularityByNode.TryRemove(entry.Node, out _);
                _e1MmByNode.TryRemove(entry.Node, out _);
                _validationIssuesByNode.TryRemove(entry.Node, out _);
                // The playback data above is gone — reset the validation dedup key so a
                // re-validate of this node isn't skipped as "already done" (stale guard
                // left the play button enabled but permanently starved of IK data).
                if (ReferenceEquals(_validationNode, entry.Node))
                {
                    _validationDone = false;
                    _validationNode = null;
                }
                UploadToolpathEntry(entry, addToScene: false);
                var replacedNode = entry.Node;
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateFocusOverlay();
                    // After re-slice upload, keep robot on the current timeline frame.
                    if (DataContext is ViewportViewModel vRep
                        && ReferenceEquals(_activeScrubNode, replacedNode)
                        && vRep.IsScrubSessionActive)
                    {
                        ScrubIkForNode(replacedNode, vRep.ToolpathScrubIndex);
                        // Repopulate playback IK data so the timeline can play again.
                        if (_toolpathByNode.TryGetValue(replacedNode, out var freshTp))
                            ValidateToolpathAsync(replacedNode, freshTp);
                    }
                });
            }

            // Apply any completed reachability results on the GL thread.
            while (_pendingReachability.TryDequeue(out var reach))
                _renderer.UpdateToolpathReachability(reach.node, reach.reachable);

            while (_pendingSingularityPoints.TryDequeue(out var sing))
                _renderer.UpdateToolpathSingularityPoints(sing.node, sing.singularity);

            if (vm.IsCutToolActive)
                UpdateCutToolOverlay(vm);
            else
                UpdateAnglePlanePreview(vm);
            UpdatePaintOverlay(vm);

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
                    // E1 spins the turntable about the bed's WORLD-vertical axis. The pivot lives
                    // inside the rotary root's tilted frame (baseAbc, e.g. C=-90 to stand the GLB
                    // up), so a local-Z rotation would tip the top over. Rotate about the local
                    // axis that maps to world +Z under the parent's orientation.
                    var parentWorld = _rotaryBedPivot.Parent?.WorldTransform ?? Matrix4.Identity;
                    var axisLocal = Vector3.TransformNormal(Vector3.UnitZ, parentWorld.Inverted());
                    axisLocal = axisLocal.LengthSquared > 1e-12f ? Vector3.Normalize(axisLocal) : Vector3.UnitZ;
                    _rotaryBedPivot.LocalTransform = Matrix4.CreateFromAxisAngle(axisLocal, e1Rad);
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

            if (_pendingBedGridResize is { } gridSize)
            {
                _pendingBedGridResize = null;
                RebuildBedGridSize(gridSize.Width, gridSize.Depth);
            }

            if (vm.Robot is { } e1Robot && e1Robot.E1 != _lastSyncE1)
            {
                _lastSyncE1 = e1Robot.E1;

                // LFAM 1 rail: E1 is linear travel in mm — slide the robot along the configured axis.
                if (_robotRail is { } rail && _robotBaseNode is not null)
                {
                    var off = rail.SceneOffsetMm(e1Robot.E1);
                    _robotBaseNode.LocalTransform = Matrix4.CreateTranslation(
                        _robotHomePos.X + off.X,
                        _robotHomePos.Y + off.Y,
                        _robotHomePos.Z + off.Z);
                    RefreshIkSceneKinematics();

                    // The rail moves ROBROOT without touching A1-A6, so the joint-change
                    // guard above skips the readout. Refresh it here or the flange/TCP
                    // numbers (and `cal-check`) stay stale after a rail-only move.
                    SyncTcpReadout(vm);
                }

                // LFAM 3 rotary: spin the turntable about the vertical axis through its centre.
                if (_rotaryBedPivot is not null)
                {
                    float e1Rad = (float)(_bedRotationSign * e1Robot.E1 * Math.PI / 180.0);
                    var c = _bedOriginLocal;

                    if (_bedNode is not null)
                        _bedNode.LocalTransform =
                            Matrix4.CreateRotationZ(e1Rad) *
                            Matrix4.CreateTranslation(c.X, c.Y, c.Z);

                    _renderer.BedBoundaryModel =
                        Matrix4.CreateTranslation(-c.X, -c.Y, -c.Z) *
                        Matrix4.CreateRotationZ(e1Rad) *
                        Matrix4.CreateTranslation(c.X, c.Y, c.Z);
                }
            }
        }

        if (_fkController is not null && IsToolNodeSelected() && _renderer.TcpFrameMatrix is null
            && _vm is ViewportViewModel { Robot: not null } tcpVm)
            SyncTcpReadout(tcpVm);

        // Edit-mode Structural Support: the gizmo sits on the selected pocket's centre and
        // drives its fields. Nothing needs to be selected in the outliner for this — the
        // support IS the selection, which is what makes an orphaned one reachable again.
        TkMatrix4? supportAxisBasis = null;
        if (ActiveGizmoSupport(_vm) is { } gizmoSupport && _vm is { } supportVm)
        {
            var centre = SupportCentreWorld(supportVm, gizmoSupport);
            _renderer.GizmoPivotWorld = new Vector3(centre.X, centre.Y, centre.Z);
            _renderer.GizmoEnabled = true;
            _renderer.GizmoMode = supportVm.ActiveGizmoModeInternal is GizmoMode.None or GizmoMode.Scale
                ? GizmoMode.Translate   // Scale has no meaning for a fixed-size pocket
                : supportVm.ActiveGizmoModeInternal;
            supportAxisBasis = SupportAxisBasis(gizmoSupport);
        }
        else if (_vm is ViewportViewModel cutVm && cutVm.IsCutToolActive && cutVm.CutToolSession is { } cutS)
        {
            _renderer.GizmoPivotWorld = new Vector3(
                (float)cutS.CenterX, (float)cutS.CenterY, (float)cutS.CenterZ);
        }
        else
        {
            _renderer.GizmoPivotWorld = IsToolNodeSelected() && _renderer.TcpFrameMatrix is { } tcpGizmo
                ? tcpGizmo.Row3.Xyz
                : null;
        }

        _renderer.GizmoAxisBasis = supportAxisBasis ?? GetModifierAxisBasis(_renderer.SelectedNode);

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

        var robroot = GetLiveRobrootWorldPos();
        vm.Robot!.FlangeX = Math.Round(pos.X - robroot.X, 1);
        vm.Robot.FlangeY  = Math.Round(pos.Y - robroot.Y, 1);
        vm.Robot.FlangeZ  = Math.Round(pos.Z - robroot.Z, 1);

        // Scene-world nozzle tip, kept separate from TcpX/Y/Z because the live sync
        // overwrites those with the controller's BASE-frame pose (see `cal-check`).
        vm.Robot.SceneTcpX = Math.Round(tcp.X, 1);
        vm.Robot.SceneTcpY = Math.Round(tcp.Y, 1);
        vm.Robot.SceneTcpZ = Math.Round(tcp.Z, 1);

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

        // Hand-eye calibration writes the full camera-in-flange transform as the live TCP
        // (_tcpOffsetLocal + _tcpOrientationABC). Use that unified frame for scan registration —
        // do NOT mix static sensorOrigin XYZ (legacy TOOL_DATA[5] optical centre) with calibrated ABC.
        float ox = _tcpOffsetLocal.X;
        float oy = _tcpOffsetLocal.Y;
        float oz = _tcpOffsetLocal.Z;
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
        _bedGridCorner = corner;
        _bedGridDatum  = new Vector3(x, y, z);
        _renderer.SetBedBoundary(_bedBaseMarker, corner, _bedWidth, _bedDepth, _bedGridDatum, diameter);

        // Recentre the rotary-bed mesh in X/Y onto the calibrated axis, but PRESERVE its Z. The bed
        // calibration measures where the rotary AXIS is (X/Y centre) and its rotation — it does not
        // measure the table HEIGHT, which is a fixed property of the physical assembly / model. So
        // only the in-plane translation follows the grid; the existing Z (and the baseAbc tilt in
        // Rows 0-2) are kept, otherwise applying a calibration drops the turntable to the fit's Z.
        if (_rotaryBedRoot is not null)
        {
            var lt = _rotaryBedRoot.LocalTransform;
            lt.Row3 = new Vector4(x, y, lt.Row3.Z, 1f);   // X/Y → axis centre; Z unchanged
            _rotaryBedRoot.LocalTransform = lt;
        }

        _lastSyncE1 = double.NaN;   // re-apply E1 rotation (mesh + boundary) about the new pivot next frame
    }

    /// <summary>
    /// Re-applies width/depth to the rectangular print grid while keeping the back-left corner fixed.
    /// </summary>
    private void RebuildBedGridSize(float width, float depth)
    {
        _bedWidth = width;
        _bedDepth = depth;
        _renderer.SetBedBoundary(
            _bedBaseMarker, _bedGridCorner, _bedWidth, _bedDepth, _bedGridDatum, _bedDiameter);
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
        _collisionWorld = null;   // tool geometry/mount changed — re-extract
    }

    private Vector3 GetLiveRobrootWorldPos()
        => _robotBaseNode?.WorldTransform.Row3.Xyz ?? _robrootWorldPos;

    private void RefreshIkSceneKinematics()
    {
        if (_ikSolver is null || _fkController is null) return;
        _ikSolver.UpdateSceneBase(_fkController.LiveChainRootTransform(), GetLiveRobrootWorldPos());
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
            _fkController.LiveChainRootTransform(),
            GetLiveRobrootWorldPos(),
            tcpLocal,
            vm.ActiveCell?.Robot.Joints ?? [],
            totalRoll);
        if (vm.Robot is not null)
            vm.Robot.IkSolver = _ikSolver;
    }

    // -- Workspace UI session restore (edit mode / tools / layer isolation) ----

    /// <summary>
    /// Reapplies the UI session saved with the .mass file: re-arms the scrubbed
    /// toolpath, restores the isolated layer window, reopens edit mode, and
    /// re-selects the paint/path tool that was active at save time.
    /// </summary>
    private void ApplyPendingUiSession(ViewportViewModel vm)
    {
        if (vm.PendingUiSession is not { } session) return;
        vm.PendingUiSession = null;

        SceneNode? target = FindSessionToolpathNode(vm, session.ScrubModelName, session.ScrubToolpathName);

        if (target is not null && _toolpathByNode.TryGetValue(target, out var tp))
        {
            if (session.SelectToolpath)
            {
                // Selection path also arms scrub via UpdateFocusOverlay (lands at end).
                _renderer.Select(target);
                vm.SetOutlinerSelection(target);
                UpdateFocusOverlay();
            }
            else if (session.IsScrubSessionActive || session.IsPaintEditOpen)
            {
                // Arm scrub without forcing outliner selection onto the toolpath.
                _activeScrubNode = target;
                vm.ResetScrubIndex(tp.Layers.Sum(l => l.Moves.Count), tp, preservePosition: false);
                vm.IsScrubSessionActive = true;
            }

            if (session.IsScrubSessionActive || session.IsPaintEditOpen || session.SelectToolpath)
                vm.ApplyUiSessionScrubWindow(session);
        }
        else if (session.IsScrubSessionActive && _activeScrubNode is { } armed
                 && _toolpathByNode.TryGetValue(armed, out _))
        {
            // Fallback: whatever was auto-armed — still restore the window.
            vm.ApplyUiSessionScrubWindow(session);
        }

        vm.ApplyUiSessionViewState(session);

        // Restore realtime-slice pause *after* toolpaths exist so BAKED matrices stay intact.
        // When pause is requested, also drop any pending realtime work from prefs load.
        if (session.RealtimeSlicingPaused == true || HasProtectedBakedToolpath(vm))
        {
            _realtimeSlicePending = false;
            vm.RealtimeSlicingPaused = true;
        }
        else if (session.RealtimeSlicingPaused == false)
        {
            vm.RealtimeSlicingPaused = false;
        }

        // Re-bind MODIFICATIONS after scrub/toolpath is armed (layers available).
        if (session.PaintModifications is { Count: > 0 } mods)
            RestorePaintModificationsState(mods);
        // Re-apply slice-plane camera lock / robot hide after tools + scrub are armed
        // (ApplyCameraState may have run before edit session restored).
        if (session.IsSlicePlaneViewerActive && session.IsPaintEditOpen)
        {
            vm.IsSlicePlaneViewerActive = true;
            ApplySlicePlaneViewerCamera(true);
            vm.RefreshSlicePlaneStats();
        }
        UpdateFocusOverlay();
        GlCanvas.RequestNextFrameRendering();
        vm.OnDevLog?.Invoke(
            $"[workspace] Restored UI session"
            + (session.IsPaintEditOpen ? " (edit mode)" : "")
            + (session.IsSlicePlaneViewerActive ? " (2D slice view)" : "")
            + (session.ScrubToolpathName is { } n ? $" toolpath '{n}'" : "")
            + $" layers {session.ToolpathScrubLayerLow:0}–{session.ToolpathScrubLayerHigh:0}."
            + (session.PaintModifications.Count > 0
                ? $" paintMods={session.PaintModifications.Count}"
                : ""));
    }

    /// <summary>Finds a restored toolpath node by parent model + toolpath name.</summary>
    private SceneNode? FindSessionToolpathNode(
        ViewportViewModel vm, string? modelName, string? toolpathName)
    {
        if (string.IsNullOrEmpty(toolpathName))
        {
            // No name saved — first uploaded toolpath if any.
            foreach (var kv in _toolpathByNode)
                return kv.Key;
            return null;
        }

        SceneNode? nameOnlyMatch = null;
        foreach (var item in vm.EnumerateUserModelItems())
        {
            bool modelOk = string.IsNullOrEmpty(modelName)
                || string.Equals(item.Node.Name, modelName, StringComparison.OrdinalIgnoreCase);
            if (!modelOk) continue;

            foreach (var child in item.Children)
            {
                if (!string.Equals(child.Node.Name, toolpathName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!_toolpathByNode.ContainsKey(child.Node)) continue;
                return child.Node;
            }
        }

        // Model name may have changed or been a placeholder — match toolpath name alone.
        foreach (var item in vm.EnumerateUserModelItems())
        {
            foreach (var child in item.Children)
            {
                if (string.Equals(child.Node.Name, toolpathName, StringComparison.OrdinalIgnoreCase)
                    && _toolpathByNode.ContainsKey(child.Node))
                {
                    nameOnlyMatch = child.Node;
                    break;
                }
            }
            if (nameOnlyMatch is not null) break;
        }

        return nameOnlyMatch;
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
        _collisionByNode.Clear();
        _singularityByNode.Clear();
        _e1MmByNode.Clear();
        _activeScrubNode = null;
    }

    /// <summary>
    /// Parents a user import under the rotary pivot when the cell has one (LFAM 3 turntable).
    /// Flat-bed cells (LFAM 1/2) attach at scene root so picks are not treated as cell infrastructure.
    /// </summary>
    private void AttachUserImportToCell(SceneNode node)
    {
        var world = node.WorldTransform;
        node.Parent?.RemoveChild(node);

        // Content lives under the print bed: the rotary pivot where one exists (so E1
        // spins the part with the table), else the flat bed node. World pose preserved.
        if ((_rotaryBedPivot ?? _bedNode) is { } bed)
        {
            node.LocalTransform = world * bed.WorldTransform.Inverted();
            bed.AddChild(node);
        }
        else
        {
            node.LocalTransform = world;
            _renderer.SceneRoot.AddChild(node);
        }
    }

    private static void MarkUserImportSubtree(SceneNode root)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            n.Selectable = true;
            n.PickTier   = PickTier.Content;
        }
    }

    private readonly record struct PreservedUserModel(SceneNode Node, Matrix4 WorldTransform);

    private readonly record struct PreservedToolpathUpload(
        SceneNode Node,
        ToolpathSnapshot Snapshot,
        Matrix4 LocalTransform);

    /// <summary>Detaches user imports from the scene graph without releasing GPU meshes.</summary>
    private static List<PreservedUserModel> DetachUserModelsForCellSwap(ViewportViewModel vm)
    {
        var preserved = new List<PreservedUserModel>();
        foreach (var item in vm.EnumerateUserModelItems())
        {
            var node = item.Node;
            preserved.Add(new PreservedUserModel(node, node.WorldTransform));
            node.Parent?.RemoveChild(node);
        }
        return preserved;
    }

    private List<PreservedToolpathUpload> SnapshotToolpathsForCellSwap(ViewportViewModel vm)
    {
        var snaps = new List<PreservedToolpathUpload>();
        foreach (var model in vm.EnumerateUserModelItems())
        {
            foreach (var tpItem in model.Children)
            {
                if (GetToolpathSnapshot(tpItem.Node) is not { } snap) continue;
                snaps.Add(new PreservedToolpathUpload(tpItem.Node, snap, tpItem.Node.LocalTransform));
            }
        }
        return snaps;
    }

    /// <summary>
    /// World-aligned frame at the centre of a cell's import surface (same anchor new
    /// imports land on). Used to transfer user content between cells instead of raw
    /// bed/pivot node transforms: the LFAM 3 rotary pivot lives in the GLB's tilted
    /// mesh frame (baseAbc, e.g. C=-90), so re-basing against it would tip content
    /// over and drag it to the mesh origin. A translation-only frame keeps content
    /// upright at its offset from the print-surface centre.
    /// </summary>
    private static Matrix4 ImportSurfaceFrame(CellConfig? cfg)
    {
        if (cfg?.Bed is not { } bed) return Matrix4.Identity;
        var c = bed.ImportSurfaceCenter(cfg.Robot.WorldPosition);
        return Matrix4.CreateTranslation(c.X, c.Y, c.Z);
    }

    private void RestoreUserContentAfterCellSwap(
        ViewportViewModel vm,
        List<PreservedUserModel> users,
        List<PreservedToolpathUpload> toolpaths,
        Matrix4 oldBedWorld)
    {
        if (users.Count == 0 && toolpaths.Count == 0) return;

        // Old-bed → new-bed frame change. Content keeps its pose relative to the print
        // surface centre, so it lands on the new cell's bed instead of floating at the
        // old cell's world coordinates. Identity when either cell has no bed config.
        var newBedWorld = ImportSurfaceFrame(vm.ActiveCell);
        var bedDelta    = oldBedWorld.Inverted() * newBedWorld;

        foreach (var (node, world) in users)
        {
            node.LocalTransform = world * bedDelta;
            AttachUserImportToCell(node);
        }

        foreach (var (tpNode, snap, local) in toolpaths)
        {
            UploadToolpathEntry(new PendingToolpathEntry
            {
                Node                   = tpNode,
                Toolpath               = snap.Smoothed,
                RawToolpath            = snap.Raw,
                BeadWidth              = snap.BeadWidth,
                LayerHeight            = snap.LayerHeight,
                MaterialColor          = snap.MaterialColor,
                LocalTransformOverride = local * bedDelta,
            }, addToScene: true);
        }

        _renderer.InvalidateShaderAppearance();
        GlCanvas.RequestNextFrameRendering();
        System.Console.WriteLine(
            $"[cell] kept {users.Count} model(s) and {toolpaths.Count} toolpath(s) aligned after reload");
    }

    private void ApplyCellSwap(CellSwapPayload swap, ViewportViewModel vm)
    {
        // Stop tool-change playback on the UI thread before FK / multi-tool state is torn down.
        if (Dispatcher.UIThread.CheckAccess())
            ClearToolChangeSequence(restorePriorMount: false);
        else
            Dispatcher.UIThread.Invoke(() => ClearToolChangeSequence(restorePriorMount: false));

        // Content transfers bed-relative: capture the outgoing cell's import-surface
        // frame before vm.ActiveCell is overwritten, so restored models/toolpaths land
        // on the NEW bed where they sat on the old one (see ImportSurfaceFrame).
        var oldBedWorld = ImportSurfaceFrame(vm.ActiveCell);

        var preservedUsers      = DetachUserModelsForCellSwap(vm);
        var preservedToolpaths  = SnapshotToolpathsForCellSwap(vm);
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
        _rotaryBedRoot              = null;
        _robotBaseNode              = null;
        _collisionWorld             = null;
        _robotRail                  = null;
        _multiToolFlangeParented    = false;
        _lfamInfrastructureNodes.Clear();
        _renderer.TcpFrameMatrix    = null;
        _renderer.FlangeFrameMatrix = null;
        if (vm.Robot is not null) vm.Robot.IkSolver = null;

        vm.ActiveCell     = swap.Config;
        vm.ActiveCellPath = swap.CellPath;
        var swapCellForPost = swap.Config;
        var swapCellPath    = swap.CellPath;
        // Cell home presets + robot panel limits are applied together on the UI thread (see InvokeAsync below).
        var b          = swap.Config.Bed;
        var rpBed      = swap.Config.Robot.WorldPosition;
        var baseMarker = b.BaseMarkerWorld(rpBed);
        var gridCorner = b.VisualGridCorner(rpBed);
        var gridDatum  = b.HasVisualShift && b.GridOrigin is null
            ? gridCorner
            : new Float3(b.Origin.X, b.Origin.Y, gridCorner.Z);
        _bedBaseMarker   = new Vector3(baseMarker.X, baseMarker.Y, baseMarker.Z);
        _bedGridCorner   = new Vector3(gridCorner.X, gridCorner.Y, gridCorner.Z);
        _bedGridDatum    = new Vector3(gridDatum.X, gridDatum.Y, gridDatum.Z);
        _bedWidth        = b.Width;
        _bedDepth        = b.Depth;
        _bedDiameter     = b.Diameter ?? 0f;
        _bedRotationSign = b.RotationSign ?? -1f;
        // Blue origin marker stays at BASE 0,0,0; grid/border follow visual placement.
        _renderer.SetBedBoundary(
            _bedBaseMarker, _bedGridCorner, _bedWidth, _bedDepth, _bedGridDatum, _bedDiameter);
        Dispatcher.UIThread.Post(() => vm.SyncBedGridSize(b.Width, b.Depth));

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
        // The rail rewrites the robot node's transform every frame from _robotHomePos, so the
        // render-only ModelOffset has to be folded in here too or the rail would undo it.
        var mp = swap.Config.Robot.ModelWorldPosition;
        _robotHomePos      = new Vector3(mp.X, mp.Y, mp.Z);
        _robotRail         = swap.Config.RobotRail;
        _flangeDisplayRoll = swap.Config.Robot.FlangeDisplayRoll * MathF.PI / 180f;

        if (swap.RobotBaseNode is { } robot)
        {
            _robotBaseNode = robot;
            _renderer.SceneRoot.AddChild(robot);
            UploadVisiblePendingMeshes(robot);

            // Outliner visibility for robot — selection blocked on LFAM 1/2/3 (see RequestSceneSelection).
            var robotRoot = robot;
            var pedestal  = robot.FindDescendant("KR_120_R2700-2_BASE");
            var arm       = robot.FindDescendant("joint_1");
            RegisterLfamInfrastructure(robotRoot, pedestal, arm);
            Dispatcher.UIThread.Post(() => vm.SetRobotGroup(robotRoot, pedestal, arm));
        }
        else
            Dispatcher.UIThread.Post(() => vm.SetRobotGroup(null, null, null));
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
            {
                _rotaryBedRoot = env;   // so bed recentring can relocate the turntable to match
                UploadVisiblePendingMeshes(env);
            }
            else
                EnqueueCellGpuUpload(env);
        }

        // Expose the rotary bed as an outliner group that scans nest under (so they ride E1).
        var rotaryPivot = _rotaryBedPivot;
        var cellEnvOutliner = new List<(SceneNode Node, string DisplayName)>();
        foreach (var env in swap.EnvironmentNodes)
        {
            if (env.Name is "Extruder Stand" or "Scanner Stand" or "Spindle Stand")
                cellEnvOutliner.Add((env, env.Name));
        }
        if (swap.BedNode is { } bedNode)
        {
            cellEnvOutliner.Add((bedNode, "Print Bed"));
            RegisterLfamInfrastructure(bedNode);
        }

        RegisterLfamInfrastructure(rotaryPivot, _rotaryBedRoot);

        Dispatcher.UIThread.Post(() =>
        {
            vm.SetCellEnvironmentOutliner(cellEnvOutliner);
            vm.SetRotaryBedGroup(rotaryPivot, "KP1-MB2000 HW-2 Rotary Bed");
            if (swap.MultiTools is { } mt)
            {
                var toolEntries = swap.Config.EffectiveTools
                    .Where(t => mt.Tools.ContainsKey(t.Name))
                    .Select(t => (t.Name, mt.Tools[t.Name].FlangeHolder))
                    .ToList();
                vm.SetMultiToolOutliner(toolEntries);
            }
            else
                vm.SetMultiToolOutliner([]);
        });

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
                swap.ToolHolder.PickTier       = PickTier.Content;
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

        RestoreUserContentAfterCellSwap(vm, preservedUsers, preservedToolpaths, oldBedWorld);

        // Dispatch UI-thread updates: joint limits, home angles, tool library.
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClearToolChangeSequence(restorePriorMount: false);
            vm.ResetViewportOverlayState();
            UpdateFocusOverlay();
            vm.NotifyCellChanged();

            var posData = CellLoader.LoadPositionData(swap.CellPath);
            if (vm.AdditiveSettings is { } additive)
                additive.UpdateFromCell(swap.Config, posData.Default, posData.Positions);

            if (vm.Robot is null) return;

            vm.Robot.SetNextPositionName(posData.Positions.Count + 1);
            var bed = swap.Config.Bed;
            float orient = swap.Config.RotaryBed?.OrientationOffsetDeg
                           ?? RotaryBedCellConfig.DefaultOrientationOffsetDeg;
            vm.Robot.ConfigureBed(bed.Origin.X, bed.Origin.Y, bed.Origin.Z,
                                  bed.Diameter ?? 0f, bed.RotationSign ?? -1f, orient,
                                  bed.Diameter is > 0f);
            vm.Robot.ConfigureRail(swap.Config.RobotRail);

            var home = vm.AdditiveSettings?.SelectedHomeAngles ?? swap.Config.Robot.HomePosition;
            vm.Robot.Configure(swap.Config.Robot.Joints, home);
            vm.Robot.SetDefaultToolheadOrientation(
                swap.Config.Robot.DefaultToolheadA,
                swap.Config.Robot.DefaultToolheadB,
                swap.Config.Robot.DefaultToolheadC);
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

            vm.AcceptedCellSwapGeneration = swap.Generation;
            vm.OnCellSwapCompleted?.Invoke(swap.Generation);
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

    /// <summary>Space + left drag pans in every preset (handy in 2D slice / edit mode).</summary>
    private bool IsSpaceLeftPan(AvaBtn btn) => _spaceHeld && btn == AvaBtn.Left;

    /// <summary>
    /// 2D slice plane locks the camera to top-down orthographic: pan + zoom only,
    /// no orbit/rotate (including touchpad two-finger rotate).
    /// </summary>
    private bool IsSlicePlaneNavLocked =>
        DataContext is ViewportViewModel sliceNavVm
        && sliceNavVm.IsSlicePlaneViewerActive
        && sliceNavVm.IsPaintEditOpen;

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

        // Space + left drag → pan (before paint/select so 2D slice edit stays usable).
        if (kind == PointerUpdateKind.LeftButtonPressed && _spaceHeld)
        {
            _isPanning = true;
            _panButton = AvaBtn.Left;
            _spaceUsedForPan = true;
            GlCanvas.InteractionRenderScale = InteractionScale;
            e.Pointer.Capture(this);
            _capturedPointer = e.Pointer;
            e.Handled = true;
            return;
        }

        // 2D slice plane: right-drag pans (orbit is locked; this is the primary pan).
        if (kind == PointerUpdateKind.RightButtonPressed && IsSlicePlaneNavLocked)
        {
            _isPanning = true;
            _panButton = AvaBtn.Right;
            GlCanvas.InteractionRenderScale = InteractionScale;
            e.Pointer.Capture(this);
            _capturedPointer = e.Pointer;
            e.Handled = true;
            return;
        }

        // Double-click: in 2D slice view, expand the line under the cursor to the full
        // connected path (single-click stays a short local section). Elsewhere / on miss
        // → frame the hit as the orbit centre.
        if (kind == PointerUpdateKind.LeftButtonPressed && e.ClickCount >= 2)
        {
            if (IsSlicePlaneNavLocked
                && DataContext is ViewportViewModel dblVm
                && dblVm.ViewMode == "Preview"
                && dblVm.IsPaintEditOpen
                && !dblVm.PaintHandActive
                && !dblVm.PaintBoxSelectActive
                && !_spaceHeld
                && (dblVm.PaintLineToolActive || !dblVm.PaintBrushActive)
                && PickSpanUnderCursor(pos, fullConnectedPath: true) is not null)
            {
                TryPaintLineAt(dblVm, pos, erase: mods.HasFlag(KeyModifiers.Alt),
                    applyMarks: dblVm.PaintLineToolActive,
                    additive: mods.HasFlag(KeyModifiers.Shift),
                    fullConnectedPath: true);
                if (_paintStrokeChanged)
                {
                    _paintStrokeChanged = false;
                    dblVm.AdditiveSettings?.BumpPaintStamp();
                }
                GlCanvas.RequestNextFrameRendering();
                e.Handled = true;
                return;
            }

            FrameUnderCursorOrSelection(pos);
            e.Handled = true;
            return;
        }

        // Toolpath paint / line-select owns the pointer while the Edit menu is open
        // in Preview: left paints (Alt erases), right-drag resizes the brush. A plain
        // click with no tool (or with a line tool) always sticky-highlights the contour.
        if (DataContext is ViewportViewModel pbVm
            && pbVm.ViewMode == "Preview"
            && (pbVm.PaintBrushActive || pbVm.IsPaintEditOpen)
            && !_spaceHeld)
        {
            if (kind == PointerUpdateKind.RightButtonPressed && pbVm.PaintBrushActive
                && !pbVm.PaintLineToolActive)
            {
                _paintResizing = true;
                _paintResizeStartX = (float)pos.X;
                _paintResizeStartRadius = pbVm.PaintBrushRadiusMm;
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
                e.Handled = true;
                return;
            }
            if (kind == PointerUpdateKind.LeftButtonPressed)
            {
                // Hand tool: leave the click alone so orbit/pan/select mesh still work.
                if (pbVm.PaintHandActive)
                    return;

                // Structural Support gets the click before any bead pick: grabbing its
                // gizmo, or clicking inside a pocket footprint to make it the live one.
                {
                    // MUST go through GetGlPickViewport, like the bead picker does — the
                    // GL canvas can be inset/scaled inside this control, and using raw
                    // pointer coords offsets the ray enough to miss the pocket entirely.
                    var (sMx, sMy, sVpW, sVpH) = GetGlPickViewport(pos);

                    if (_renderer.GizmoEnabled && ActiveGizmoSupport(pbVm) is not null)
                    {
                        var sAxis = _renderer.HitTestGizmo(sMx, sMy, sVpW, sVpH);
                        if (sAxis != GizmoAxis.None)
                        {
                            StartGizmoDrag(sAxis, sMx, sMy, sVpW, sVpH);
                            e.Pointer.Capture(this);
                            _capturedPointer = e.Pointer;
                            e.Handled = true;
                            return;
                        }
                    }

                    int supportPick = PickStructuralSupportUnderCursor(pbVm, sMx, sMy, sVpW, sVpH);
                    if (supportPick >= 0)
                    {
                        SelectStructuralSupport(pbVm, supportPick);
                        e.Handled = true;
                        return;
                    }
                }

                // Region select (square marquee or lasso) — start drag, not a click-pick.
                if (pbVm.PaintBoxSelectActive)
                {
                    _paintBoxDragging = true;
                    _paintBoxStart = pos;
                    _paintLassoPts.Clear();
                    if (pbVm.PaintRegionSelectIsLasso)
                    {
                        pbVm.PaintMarqueeVisible = false;
                        _paintLassoPts.Add(pos);
                        pbVm.SetPaintLassoPoints(_paintLassoPts);
                        pbVm.PaintLassoVisible = true;
                    }
                    else
                    {
                        pbVm.PaintLassoVisible = false;
                        pbVm.ClearPaintLassoPoints();
                        pbVm.PaintMarqueeX = pos.X;
                        pbVm.PaintMarqueeY = pos.Y;
                        pbVm.PaintMarqueeW = 0;
                        pbVm.PaintMarqueeH = 0;
                        pbVm.PaintMarqueeVisible = true;
                    }
                    e.Pointer.Capture(this);
                    _capturedPointer = e.Pointer;
                    e.Handled = true;
                    return;
                }

                if (pbVm.PaintLineToolActive || !pbVm.PaintBrushActive)
                {
                    // Line tool active → mark/unmark; edit open with no tool → select
                    // only. Shift accumulates: earlier picks stay highlighted.
                    // Single click = short local section (full path is double-click in 2D slice).
                    TryPaintLineAt(pbVm, pos, erase: mods.HasFlag(KeyModifiers.Alt),
                        applyMarks: pbVm.PaintLineToolActive,
                        additive: mods.HasFlag(KeyModifiers.Shift),
                        fullConnectedPath: false);
                    if (_paintStrokeChanged)
                    {
                        _paintStrokeChanged = false;
                        pbVm.AdditiveSettings?.BumpPaintStamp();   // pending while paused
                    }
                    GlCanvas.RequestNextFrameRendering();          // show highlight / marks
                    e.Handled = true;
                    return;
                }
                _paintStroking = true;
                _lastPaintPx = new Avalonia.Point(double.MinValue, double.MinValue);
                TryPaintAt(pbVm, pos, erase: mods.HasFlag(KeyModifiers.Alt));
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
                e.Handled = true;
                return;
            }
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

            if (DataContext is ViewportViewModel spVm
                && TryBeginSeamPointDrag(spVm, mx, my, vpW, vpH))
            {
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
                e.Handled = true;
                return;
            }

            if (DataContext is ViewportViewModel gpVm
                && TryBeginGuidePlaneDrag(gpVm, mx, my, vpW, vpH))
            {
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
                e.Handled = true;
                return;
            }

            if (DataContext is ViewportViewModel xcVm
                && TryBeginXBraceCylinderDrag(xcVm, mx, my, vpW, vpH))
            {
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
                e.Handled = true;
                return;
            }
        }

        if (btn.HasValue)
        {
            // 2D slice: never start an orbit — top view is pan + zoom only.
            bool allowOrbit = !IsSlicePlaneNavLocked;
            if (allowOrbit
                && _orbitButton is null
                && IsOrbitButton(btn.Value, mods)
                && !IsSpaceLeftPan(btn.Value))
            {
                _isOrbiting  = true;
                _orbitButton = btn;
                GlCanvas.InteractionRenderScale = InteractionScale;
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
            }
            else if (_panButton is null
                     && (IsPanButton(btn.Value, mods)
                         || IsSpaceLeftPan(btn.Value)
                         || (IsSlicePlaneNavLocked && btn.Value == AvaBtn.Right)))
            {
                _isPanning  = true;
                _panButton  = btn;
                if (IsSpaceLeftPan(btn.Value))
                    _spaceUsedForPan = true;
                GlCanvas.InteractionRenderScale = InteractionScale;
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
            }
            // In 2D slice, orbit bindings are ignored (pan via right-drag / middle / Space+LMB).
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

        if (_paintBoxDragging && DataContext is ViewportViewModel pbxVm)
        {
            if (pbxVm.PaintRegionSelectIsLasso)
            {
                // Thin the stream — only keep points that moved enough.
                if (_paintLassoPts.Count == 0
                    || Dist2D(_paintLassoPts[^1], pos) >= 4.0)
                {
                    _paintLassoPts.Add(pos);
                    pbxVm.SetPaintLassoPoints(_paintLassoPts);
                }
            }
            else
            {
                pbxVm.PaintMarqueeX = Math.Min(pos.X, _paintBoxStart.X);
                pbxVm.PaintMarqueeY = Math.Min(pos.Y, _paintBoxStart.Y);
                pbxVm.PaintMarqueeW = Math.Abs(pos.X - _paintBoxStart.X);
                pbxVm.PaintMarqueeH = Math.Abs(pos.Y - _paintBoxStart.Y);
            }
            e.Handled = true;
            return;
        }
        if (_paintResizing && DataContext is ViewportViewModel prVm)
        {
            prVm.PaintBrushRadiusMm =
                _paintResizeStartRadius + ((float)pos.X - _paintResizeStartX) * 0.15;
            e.Handled = true;
            return;
        }
        if (_paintStroking && DataContext is ViewportViewModel psVm)
        {
            if (Math.Abs(pos.X - _lastPaintPx.X) + Math.Abs(pos.Y - _lastPaintPx.Y) >= 6)
                TryPaintAt(psVm, pos, erase: e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            GlCanvas.RequestNextFrameRendering();
            e.Handled = true;
            return;
        }
        // Edit menu open (or a paint tool armed): live hover feedback.
        if (DataContext is ViewportViewModel phVm
            && phVm.ViewMode == "Preview"
            && (phVm.PaintBrushActive || phVm.IsPaintEditOpen)
            && !_isOrbiting && !_isPanning)
            if (!phVm.PaintHandActive) UpdatePaintHover(phVm, pos);

        if (_gizmoDragAxis != GizmoAxis.None)
        {
            _leftDragged = true;
            ProcessGizmoDrag((float)pos.X, (float)pos.Y);
            if (_toolIsDragging)
                RunIkForToolDrag();
            GlCanvas.RequestNextFrameRendering();
            return;
        }

        if (_seamPointDragging)
        {
            _leftDragged = true;
            UpdateSeamPointDrag((float)delta.X);
            return;
        }

        if (_guidePlaneDragging && DataContext is ViewportViewModel gpDragVm)
        {
            _leftDragged = true;
            UpdateGuidePlaneDrag(gpDragVm, (float)delta.X, (float)delta.Y);
            return;
        }

        if (_xBraceCylinderDragging && DataContext is ViewportViewModel xcDragVm)
        {
            _leftDragged = true;
            float vpW = (float)GlCanvas.Bounds.Width;
            float vpH = (float)GlCanvas.Bounds.Height;
            var ray = _renderer.Camera.GetPickRay((float)pos.X, (float)pos.Y, vpW, vpH);
            UpdateXBraceCylinderDrag(xcDragVm, ray);
            return;
        }

        // Placing a guide: ghost the column the click would create, so the seam position is
        // visible before committing (previously you clicked blind and got a marker).
        if (!_seamGuideDragging && DataContext is ViewportViewModel hoverVm)
        {
            if (hoverVm.IsSeamEditorActive && hoverVm.SeamEditorTool == SeamEditorToolKind.AddPoint)
            {
                float vpW = (float)GlCanvas.Bounds.Width;
                float vpH = (float)GlCanvas.Bounds.Height;
                var ray   = _renderer.Camera.GetPickRay((float)pos.X, (float)pos.Y, vpW, vpH);
                _renderer.SetSeamGuidePreview(
                    TrySeamGuideOnModel(ray, (float)pos.X, (float)pos.Y, out var previewHit)
                        ? previewHit
                        : null);
                GlCanvas.RequestNextFrameRendering();
            }
            else
                _renderer.SetSeamGuidePreview(null);
        }

        if (_seamGuideDragging && DataContext is ViewportViewModel dragVm)
        {
            _leftDragged = true;
            float vpW = (float)GlCanvas.Bounds.Width;
            float vpH = (float)GlCanvas.Bounds.Height;
            var ray   = _renderer.Camera.GetPickRay((float)pos.X, (float)pos.Y, vpW, vpH);
            // Drag rides the model wall too (was a flat Z-plane slide, which let a guide
            // drift off the surface it seams).
            if (TrySeamGuideOnModel(ray, (float)pos.X, (float)pos.Y, out var hit))
            {
                dragVm.MoveSeamGuidePoint(_seamGuideDragIndex,
                    new SeamGuidePoint(hit.X, hit.Y, hit.Z));
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

        // 2D slice plane: pan + zoom only — drop any in-flight orbit.
        if (_isOrbiting && IsSlicePlaneNavLocked)
        {
            _isOrbiting  = false;
            _orbitButton = null;
        }

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

        if (_paintBoxDragging)
        {
            _paintBoxDragging = false;
            if (_capturedPointer == e.Pointer) { e.Pointer.Capture(null); _capturedPointer = null; }
            if (DataContext is ViewportViewModel boxVm)
            {
                bool additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                if (boxVm.PaintRegionSelectIsLasso)
                {
                    boxVm.PaintLassoVisible = false;
                    if (_paintLassoPts.Count >= 3)
                        SelectSpansInLasso(boxVm, _paintLassoPts, additive);
                    _paintLassoPts.Clear();
                    boxVm.ClearPaintLassoPoints();
                }
                else
                {
                    boxVm.PaintMarqueeVisible = false;
                    var rect = new Avalonia.Rect(
                        Math.Min(pt.Position.X, _paintBoxStart.X),
                        Math.Min(pt.Position.Y, _paintBoxStart.Y),
                        Math.Abs(pt.Position.X - _paintBoxStart.X),
                        Math.Abs(pt.Position.Y - _paintBoxStart.Y));
                    if (rect.Width > 4 && rect.Height > 4)
                        SelectSpansInRect(boxVm, rect, additive);
                }
            }
            e.Handled = true;
            return;
        }
        if (_paintStroking || _paintResizing)
        {
            _paintStroking = false;
            _paintResizing = false;
            if (_capturedPointer == e.Pointer) { e.Pointer.Capture(null); _capturedPointer = null; }
            if (_paintStrokeChanged && DataContext is ViewportViewModel pcVm)
            {
                _paintStrokeChanged = false;
                pcVm.AdditiveSettings?.BumpPaintStamp();   // commit → re-slice
            }
            e.Handled = true;
            return;
        }

        // Stop an active orbit/pan FIRST, for ANY button. The left-button release is
        // otherwise consumed (and returned) by the selection/gizmo branch below, so a
        // left-bound orbit/pan (e.g. Mol3D, Maya+Alt) would never stop: the camera keeps
        // spinning, the pointer stays captured, and the reduced interaction render scale
        // leaves a small, torn viewport. Selection only happens when not dragging, so a
        // genuine click (no orbit/pan in progress) still falls through unchanged.
        if (btn is not null && (btn == _orbitButton || btn == _panButton))
        {
            if (btn == _orbitButton) { _isOrbiting = false; _orbitButton = null; }
            if (btn == _panButton)   { _isPanning  = false; _panButton   = null; }
            if (!_isOrbiting && !_isPanning && GlCanvas.InteractionRenderScale < 1f)
                GlCanvas.InteractionRenderScale = 1f;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            _leftDragged = false;
            GlCanvas.RequestNextFrameRendering();
            return;
        }

        if (kind == PointerUpdateKind.LeftButtonReleased && _kbTransformActive)
        {
            CommitKbTransform();
            _leftDragged = false;
            return;
        }

        if (kind == PointerUpdateKind.LeftButtonReleased && _guidePlaneDragging)
        {
            if (DataContext is ViewportViewModel gpVm)
                FinishGuidePlaneDrag(gpVm);
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            _leftDragged = false;
            return;
        }

        if (kind == PointerUpdateKind.LeftButtonReleased && _xBraceCylinderDragging)
        {
            if (DataContext is ViewportViewModel xcVm)
                FinishXBraceCylinderDrag(xcVm);
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            _leftDragged = false;
            return;
        }

        if (kind == PointerUpdateKind.LeftButtonReleased && _seamPointDragging)
        {
            if (DataContext is ViewportViewModel spVm)
                FinishSeamPointDrag(spVm);
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
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
            if (_gizmoDragAxis != GizmoAxis.None && _structSupportGizmoDrag)
            {
                // Support pocket drag: no node transform, no transform-undo entry — the
                // spec is the state. Bake with one re-slice, same as creating a support.
                _gizmoDragAxis           = GizmoAxis.None;
                _renderer.ActiveDragAxis = GizmoAxis.None;
                _capturedPointer?.Capture(null);
                _capturedPointer = null;
                _leftDragged = false;
                if (DataContext is ViewportViewModel ssUpVm)
                    FinishStructuralSupportGizmoDrag(ssUpVm);
                else
                    _structSupportGizmoDrag = false;
                GlCanvas.RequestNextFrameRendering();
                return;
            }

            if (_gizmoDragAxis != GizmoAxis.None)
            {
                bool cutDragging = DataContext is ViewportViewModel { IsCutToolActive: true };
                bool tilted = false;
                if (!cutDragging
                    && _renderer.SelectedNode is { } gzNode
                    && DataContext is ViewportViewModel vmGz)
                {
                    var op = _kbTransformActive ? _kbTransformOp : _renderer.GizmoMode;
                    RecordTransformUndo(vmGz, gzNode, _gizmoDragInitialLocal, gzNode.LocalTransform, TransformUndoLabel(op));
                    tilted = DragClassifier.ChangedUpAxis(_gizmoDragInitialLocal, gzNode.LocalTransform);
                }
                _toolIsDragging          = false;
                _gizmoDragAxis           = GizmoAxis.None;
                _renderer.ActiveDragAxis = GizmoAxis.None;
                EndTransformLink();
                _capturedPointer?.Capture(null);
                _capturedPointer = null;
                if (!cutDragging && DataContext is ViewportViewModel vmGz2)
                {
                    SyncSelectionTransformDisplay(vmGz2);
                    if (IsToolNodeSelected() && vmGz2.IsScrubSessionActive && _activeScrubNode is not null)
                    {
                        // Dragging the TCP mid-scrub = keyframe the offset at this moment
                        // (no re-slice — the adjustment lives on the toolpath timeline).
                        AddTcpKeyframeAtCurrentIndex(vmGz2);
                    }
                    else if (tilted)
                    {
                        // A real tilt (rotation around anything but the node's own up-axis)
                        // changes what's actually printable — the toolpath's layer-stacking
                        // direction no longer matches this object's new orientation, so this
                        // always needs a fresh slice, same as before.
                        vmGz2.OnModelGeometryChanged?.Invoke();
                    }
                    // A plain move or a pure spin around the object's own up-axis changes
                    // neither the sliced layer geometry nor anything printability-related
                    // (confirmed: PlanarSlicer re-derives zMin/zMax from the mesh's own current
                    // world-transformed vertices every time, never an absolute world-Z grid) —
                    // so there is nothing here for a re-slice to correct. Skipping it avoids
                    // both the wasted recompute and, for a Cut-modifier piece specifically,
                    // resetting any layer-progressive effect's phase (Wave, X-bracing) as if
                    // this piece were being sliced independently for the first time, which
                    // would silently break its continuity with sibling pieces from the same cut.
                }
                GlCanvas.RequestNextFrameRendering();
                RevalidateSelectedToolpath();
            }
            else if (!_leftDragged)
            {
                float vpW = (float)GlCanvas.Bounds.Width;
                float vpH = (float)GlCanvas.Bounds.Height;
                var ray   = _renderer.Camera.GetPickRay(
                    (float)_leftDownPos.X, (float)_leftDownPos.Y, vpW, vpH);

                if (DataContext is ViewportViewModel bndVm && bndVm.IsBoundaryEditorActive
                    && _boundaryEditorMesh is not null
                    && TryPlaceSeamGuide(ray, out var bndHit))
                {
                    int seed = CurvedBoundaryPicker.FindNearestVertex(_boundaryEditorMesh, bndHit);
                    float band = (float)(bndVm.AdditiveSettings?.CurvedAutoDetectBandMm ?? 2.0);
                    bool isLow = bndVm.BoundaryEditorTarget == CurvedBoundaryEditorTarget.Low;
                    var ring = CurvedBoundaryPicker.GrowRingFromSeed(_boundaryEditorMesh, seed, band, isLow);
                    if (isLow)
                        bndVm.SetBoundaryDraft(ring, bndVm.BoundaryHighDraft);
                    else
                        bndVm.SetBoundaryDraft(bndVm.BoundaryLowDraft, ring);
                }
                else if (DataContext is ViewportViewModel flatVm
                         && (flatVm.IsSeamEditorActive || flatVm.IsToolpathSeamEditActive))
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
                             && TrySeamGuideOnModel(ray, (float)_leftDownPos.X, (float)_leftDownPos.Y,
                                                    out var placeHit))
                    {
                        // Same resolver as the hover ghost: what you previewed is what you place.
                        flatVm.AddSeamGuidePoint(new SeamGuidePoint(placeHit.X, placeHit.Y, placeHit.Z));
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
                        var oldLocal = node.LocalTransform;
                        ApplyLayFlat(node, normal, _renderer.BedZ);
                        // Same one-shot-edit gap as DropToPlate (mesh/toolpath are scene-graph
                        // siblings, nothing carries the toolpath along automatically) — but this
                        // one is worse if left unfixed: Lay Flat is, by definition, a real tilt,
                        // so without a fresh re-slice the toolpath isn't just offset, it's for
                        // the wrong orientation entirely.
                        MirrorTypedTransformDelta(flatVm2, node, oldLocal);
                        if (DragClassifier.ChangedUpAxis(oldLocal, node.LocalTransform))
                            flatVm2.OnModelGeometryChanged?.Invoke();
                        _renderer.Select(node);
                        UpdateFocusOverlay();
                        RevalidateSelectedToolpath();
                    }
                    flatVm2.IsLayFlatMode = false;
                }
                else if (DataContext is ViewportViewModel seqVm
                         && seqVm.IsDevMode
                         && TryPickSequenceWaypoint((float)_leftDownPos.X, (float)_leftDownPos.Y, vpW, vpH))
                {
                    GlCanvas.RequestNextFrameRendering();
                }
                else if (DataContext is ViewportViewModel pickVm)
                {
                    float vpW2 = (float)GlCanvas.Bounds.Width;
                    float vpH2 = (float)GlCanvas.Bounds.Height;
                    // Effector handles get pick priority: they float inside the toolpath
                    // cloud, and the toolpath's screen-distance pick would otherwise
                    // claim every click near a handle (made them unclickable).
                    var effectorHit = Picker.PickWhere(
                        ray, _renderer.SceneRoot, n => pickVm.IsEffectorNode(n), out _);
                    var picked = effectorHit is not null
                        ? Picker.FindSelectableRoot(effectorHit, _renderer.SceneRoot)
                        : (_renderer.PickToolpath((float)_leftDownPos.X, (float)_leftDownPos.Y, vpW2, vpH2)
                           ?? PickForSceneSelection(pickVm, ray));
                    var shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    if (shiftHeld && picked is not null
                        && ResolveSequenceToolpath(pickVm, picked) is not null)
                        ToggleSequenceSelection(pickVm, picked);
                    else
                        RequestSceneSelection(pickVm, picked);
                }
                else
                {
                    float vpW2 = (float)GlCanvas.Bounds.Width;
                    float vpH2 = (float)GlCanvas.Bounds.Height;
                    var toolpathHit = _renderer.PickToolpath((float)_leftDownPos.X, (float)_leftDownPos.Y, vpW2, vpH2);
                    var picked = toolpathHit ?? _renderer.Pick(ray);
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

    // Touchpad gesture tuning (Preferences → Navigation; mirrored onto ViewportViewModel).
    private float TouchpadPanSpeed   => (_vm as ViewportViewModel)?.TouchpadPanSpeed   ?? 9f;
    private float TouchpadOrbitSpeed => (_vm as ViewportViewModel)?.TouchpadOrbitSpeed ?? 2f;
    private float TouchpadZoomSpeed  => (_vm as ViewportViewModel)?.TouchpadZoomSpeed  ?? 1f;
    private bool  TouchpadInvertPan  => (_vm as ViewportViewModel)?.TouchpadInvertPan  ?? false;

    /// <summary>
    /// Two-finger pan. The part follows your fingers on BOTH axes: swipe left it goes left,
    /// swipe up it goes up. <see cref="TouchpadInvertPan"/> flips the vertical axis only
    /// (the "natural scrolling" preference people actually disagree about); horizontal always
    /// tracks the fingers, so it can never end up mirrored against the vertical.
    /// </summary>
    private void PanFromTouchpad(float dx, float dy)
    {
        float s     = TouchpadPanSpeed;
        float ySign = TouchpadInvertPan ? -1f : 1f;
        _renderer.Camera.Pan(
            deltaX:          dx * s,
            deltaY:          dy * s * ySign,
            viewportWidth:  (float)GlCanvas.Bounds.Width,
            viewportHeight: (float)GlCanvas.Bounds.Height);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var mods = e.KeyModifiers;
        float dx = (float)e.Delta.X;
        float dy = (float)e.Delta.Y;

        // 2D slice plane: pan + zoom only — never orbit from the wheel/trackpad.
        if (IsSlicePlaneNavLocked)
        {
            if (ActivePreset == NavigationPresetId.Touchpad
                && !mods.HasFlag(KeyModifiers.Shift))
            {
                // Touchpad: plain two fingers → pan, same as the unlocked view. Cmd would
                // orbit, which this mode forbids, so it also falls through to pan here.
                PanFromTouchpad(dx, dy);
            }
            else
            {
                // Mouse presets: plain scroll → zoom. Touchpad: Shift + two fingers → zoom.
                float zoom = (MathF.Abs(dy) >= MathF.Abs(dx) ? dy : dx) / 3f;
                if (ActivePreset == NavigationPresetId.Touchpad) zoom *= TouchpadZoomSpeed;
                _renderer.Camera.Zoom(zoom);
            }

            GlCanvas.RequestNextFrameRendering();
            e.Handled = true;
            return;
        }

        // Every preset except Touchpad maps a plain scroll to zoom (see NavigationPreset table).
        // The Touchpad preset keeps two-finger gestures: no-modifier orbit, Shift zoom, Cmd pan.
        if (ActivePreset != NavigationPresetId.Touchpad)
        {
            float zoom = (MathF.Abs(dy) >= MathF.Abs(dx) ? dy : dx) / 3f;
            _renderer.Camera.Zoom(zoom);
        }
        else if (mods.HasFlag(KeyModifiers.Shift))
        {
            // Shift + two fingers → zoom
            float zoom = (MathF.Abs(dy) >= MathF.Abs(dx) ? dy : dx) / 3f;
            _renderer.Camera.Zoom(zoom);
        }
        else if (mods.HasFlag(KeyModifiers.Meta))
        {
            // Cmd + two fingers → orbit/rotate
            float o = TouchpadOrbitSpeed;
            _renderer.Camera.Orbit(
                deltaAzimuth:   -dx * o,
                deltaElevation:  dy * o);
        }
        else
        {
            // Two fingers (no modifier) → pan/reposition (macOS convention: plain
            // two-finger scroll moves content). Speeds and direction are user prefs;
            // keep NavigationPreset.All's Touchpad labels in sync with these bindings.
            PanFromTouchpad(dx, dy);
        }

        GlCanvas.RequestNextFrameRendering();
        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;

        bool wasHeld = _spaceHeld;
        bool usedPan = _spaceUsedForPan;
        _spaceHeld = false;
        _spaceUsedForPan = false;

        // Space alone (no pan) still toggles playback when a toolpath is selected,
        // except in edit mode where Space is reserved for temporary pan.
        if (wasHeld && !usedPan
            && DataContext is ViewportViewModel spaceVm
            && spaceVm.IsToolpathSelected
            && !spaceVm.IsPaintEditOpen)
        {
            spaceVm.TogglePlaybackCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>True when keyboard focus is in a text field (don't steal ↑/↓).</summary>
    private bool IsTextInputFocused()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is null) return false;
        if (focused is TextBox) return true;
        // Nested text presenter / custom editors.
        var t = focused.GetType();
        return t.Name.Contains("TextBox", StringComparison.Ordinal)
            || t.Name.Contains("TextPresenter", StringComparison.Ordinal)
            || t.Name.Contains("TextEditor", StringComparison.Ordinal);
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

        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (DataContext is ViewportViewModel pieVm)
            {
                // Overlay chrome is inset; convert view coords to overlay coords.
                var overlayPos = this.TranslatePoint(_lastPointerPos, OverlayView) ?? _lastPointerPos;
                pieVm.ViewPieX = overlayPos.X;
                pieVm.ViewPieY = overlayPos.Y;
                pieVm.IsViewPieOpen = true;
                e.Handled = true;
                return;
            }
        }

        // Space alone arms temporary pan mode (Space + LMB drag). Playback toggle
        // moves to KeyUp so a pan gesture does not start/stop the timeline.
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            if (!_spaceHeld)
            {
                _spaceHeld = true;
                _spaceUsedForPan = false;
            }
            e.Handled = true;
            return;
        }

        // Number-row / numpad view keys: 1 Top, 2 Front, 3 Right, 4 Iso+Frame, 5 Left.
        // First press → that view in orthographic; press again → same view in perspective.
        if (e.KeyModifiers == KeyModifiers.None
            && TryHandleViewNumberKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.X when e.KeyModifiers == KeyModifiers.None:
                if (TryDeleteSelectedWithUndo()) e.Handled = true;
                break;
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
            case Key.Escape:
                if (DataContext is ViewportViewModel pieEscVm && pieEscVm.IsViewPieOpen)
                {
                    pieEscVm.IsViewPieOpen = false;
                    e.Handled = true;
                    break;
                }
                if (DataContext is ViewportViewModel cutEscVm && cutEscVm.IsCutToolActive)
                {
                    CancelCutToolInteractive();
                    e.Handled = true;
                    break;
                }
                if (DataContext is ViewportViewModel bridgeEscVm
                    && bridgeEscVm.PaintBridgePickModificationId.HasValue)
                {
                    bridgeEscVm.PaintBridgePickModificationId = null;
                    LogPaintConsole("[edit] bridge target pick cancelled");
                    e.Handled = true;
                    break;
                }
                if (DataContext is ViewportViewModel escVm && escVm.IsLayFlatMode)
                {
                    escVm.IsLayFlatMode = false;
                    e.Handled = true;
                    break;
                }
                // Edit mode: clear all selected path/point lines (same as Deselect).
                if (DataContext is ViewportViewModel paintEscVm
                    && paintEscVm.IsPaintEditOpen
                    && (_paintSelection.Count > 0
                        || _paintSelectedLine is { Count: > 0 }
                        || _paintMultiLines.Count > 0))
                {
                    DeselectPaintSelection(paintEscVm);
                    _paintHoverLine = null;
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

            // Preview: ↑ / ↓ step the layer scrub up / down one layer.
            case Key.Up:
            case Key.Down:
                if (e.KeyModifiers == KeyModifiers.None
                    && !IsTextInputFocused()
                    && DataContext is ViewportViewModel layerVm
                    && layerVm.ViewMode == "Preview")
                {
                    // Up = next higher layer, Down = previous lower layer.
                    int delta = e.Key == Key.Up ? +1 : -1;
                    if (layerVm.StepScrubLayer(delta))
                        e.Handled = true;
                }
                break;
        }
    }

    private void AddMultiToolVisualsToScene(CellEnvironmentBuilder.CellMultiToolSet mt, SceneNode? flange)
    {
        foreach (var pair in mt.Tools.Values)
        {
            if (flange is not null)
                flange.AddChild(pair.FlangeHolder);

            if (pair.DockHolder is { } dock)
            {
                if (dock.Parent is null)
                    _renderer.SceneRoot.AddChild(dock);
                if (dock.Visible)
                    EnqueueCellGpuUpload(dock);
            }
        }

        RefreshMultiToolSelectability();
    }

    /// <summary>LFAM 3: all toolheads parked on docks; flange empty until a Pick simulation or manual mount.</summary>
    void ApplyInitialMultiToolState(ViewportViewModel vm) => ApplyMultiToolUnmount(vm, updateVm: false);

    void ApplyExclusiveToolheadFromOutliner(string toolName, ViewportViewModel vm)
    {
        if (_multiTools is null || vm.ActiveCell is not { } cell) return;
        var cfg = cell.EffectiveTools.FirstOrDefault(t => t.Name == toolName);
        if (cfg is null) return;

        ApplyMultiToolMount(cfg, vm, hideAllDocks: true);
        GlCanvas.RequestNextFrameRendering();
    }

    private void ApplyMultiToolMount(ToolCellConfig tool, ViewportViewModel vm, bool hideAllDocks = false)
    {
        if (_multiTools is null) return;

        _multiTools.MountedToolName = tool.Name;
        foreach (var (name, pair) in _multiTools.Tools)
        {
            bool mounted = name == tool.Name;
            pair.FlangeHolder.Visible = mounted;
            if (pair.DockHolder is { } dock)
                dock.Visible = hideAllDocks ? false : !mounted;

            if (mounted)
                EnqueueCellGpuUpload(pair.FlangeHolder);
            else if (!hideAllDocks && pair.DockHolder is { } d)
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

        RefreshMultiToolSelectability();
        RebuildIkSolver(vm);
        if (vm.Robot is not null)
            SyncTcpReadout(vm);
        PostMultiToolVmState(vm, tool.Name);
        if (_currentToolNode is not null)
        {
            _renderer.Select(_currentToolNode);
            _lastOutlinerSyncedNode = _currentToolNode;
            Dispatcher.UIThread.Post(UpdateFocusOverlay);
        }
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

        var paths = items.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToList();

        // A dropped .mass workspace file opens it outright (same as File -> Open),
        // replacing the current workspace -- it's not a mesh to import into the scene.
        if (paths.FirstOrDefault(p => p.EndsWith(".mass", StringComparison.OrdinalIgnoreCase)) is { } massPath)
        {
            vm.Erp.OpenWorkspaceFile?.Invoke(massPath);
            return;
        }

        var files = paths.Where(ImportHelper.IsSupported).ToList();

        if (files.Count == 0) return;

        bool place = true;

        foreach (var file in files)
        {
            var node = ImportHelper.LoadAndPlace(file, place ? vm.ActiveCell : null);
            if (node is not null) vm.AddImportNode(node);
        }
    }

    // -- Slice -----------------------------------------------------------------

    private (OutlinerItemViewModel parent, OutlinerItemViewModel toolpathItem)? FindResliceSource(ViewportViewModel vm)
    {
        if (_renderer.SelectedNode is not { } selected) return null;
        if (!_renderer.IsToolpathNode(selected)) return null;

        foreach (var meshItem in vm.EnumerateUserModelItems())
        {
            foreach (var child in meshItem.Children)
            {
                if (child.Node != selected) continue;
                if (!CollectMeshSnapshots(meshItem, requireVisible: false).Any()) return null;
                return (meshItem, child);
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
            if (node.PickTier == PickTier.Environment) continue;
            if (node.IsAuthoringOverlay) continue;
            if (node.Mesh?.PickingData is not { } md) continue;
            meshSnapshots.Add((md.Positions, md.Indices, node.WorldTransform));
        }
        return meshSnapshots;
    }

    /// <summary>
    /// Map UI infill name → core enum. Formbound paint marks force the matching
    /// Formbound pattern (per-mark style majority) even when FILL PATTERN is None.
    /// Tree-only paint leaves pattern None — the slicer force-enables Tree separately.
    /// </summary>
    private static InfillPattern ResolveInfillPatternForSlice(AdditiveSettingsViewModel s)
    {
        var mapped = s.InfillPattern switch
        {
            "Rectilinear"        => InfillPattern.Rectilinear,
            "Grid"               => InfillPattern.Grid,
            "Triangle"           => InfillPattern.Triangle,
            "Ghost Mesh Grid"    => InfillPattern.GhostMeshGrid,
            "Formbound Bridge"   => InfillPattern.LightningBridge,
            "Lightning Bridge"   => InfillPattern.LightningBridge,
            "Formbound Buttress" => InfillPattern.FormboundButtress,
            _                    => InfillPattern.None,
        };
        // Explicit Formbound dropdown wins for auto whole-part mode.
        if (mapped is InfillPattern.LightningBridge or InfillPattern.FormboundButtress)
            return mapped;
        // Paint Formbound marks force enable even when dropdown is None/Grid.
        if (Core.Models.PaintSupportStyleUtil.ResolveFormboundPatternFromPaint(s.PaintMarks)
            is { } fromPaint)
            return fromPaint;
        return mapped;
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
            PatternType      = Enum.TryParse<MassiveSlicer.Core.Slicing.Effects.PatternType>(s.PatternType, out var pt)
                                   ? pt : MassiveSlicer.Core.Slicing.Effects.PatternType.Smooth,
            PatternMapping   = s.PatternMapping.StartsWith("Radial", StringComparison.OrdinalIgnoreCase)
                                   ? MassiveSlicer.Core.Slicing.Effects.PatternMappingMode.Radial
                                   : s.PatternMapping.StartsWith("Wavelength", StringComparison.OrdinalIgnoreCase)
                                       ? MassiveSlicer.Core.Slicing.Effects.PatternMappingMode.Wavelength
                                       : MassiveSlicer.Core.Slicing.Effects.PatternMappingMode.ArcLength,
            PatternWavelengthMm  = (float)s.PatternWavelengthMm,
            PatternAmplitude     = (float)s.PatternAmplitude,
            PatternFrequency     = (float)s.PatternFrequency,
            PatternTwistDegPerMm = (float)s.PatternTwist,
            PatternOffsetDeg     = (float)s.PatternOffset,
            PatternFadeInMm      = (float)s.PatternFadeIn,
            PatternFadeOutMm     = (float)s.PatternFadeOut,
            TiltAngle        = (float)s.TiltAngle,
            TiltAngleX       = (float)s.TiltAngleX,
            DisableContourOffset   = s.DisableContourOffset,
            ZigZagSeam             = s.SeamMode == "Zig-zag",
            ZigZagAllowSameLayerTravel = s.ZigZagAllowSameLayerTravel,
            Spiralize              = s.SeamMode.StartsWith("Spiral", StringComparison.OrdinalIgnoreCase),
            BrimEnabled         = s.BrimEnabled,
            BrimLoops           = s.BrimLoops,
            XBracingEnabled     = s.XBracingEnabled,
            XBracingDepthMm     = (float)s.XBracingDepthMm,
            XBracingDepthBottomMm = (float)s.XBracingDepthBottomMm,
            XBracingDepthEaseBottom = s.XBracingDepthEaseBottom,
            XBracingDepthEaseTop    = s.XBracingDepthEaseTop,
            XBracingSpanMm      = (float)s.XBracingSpanMm,
            XBracingAngleDeg    = (float)s.XBracingAngleDeg,
            XBracingExtendEdges = s.XBracingExtendEdges,
            XBracingPlaneTiltY  = (float)s.XBracingPlaneTiltY,
            XBracingPlaneTiltX  = (float)s.XBracingPlaneTiltX,
            XBracingProjectionType = s.XBracingProjectionType,
            XBracingCylinderDiameterMm = (float)s.XBracingCylinderDiameterMm,
            XBracingCylinderX   = (float)s.XBracingCylinderX,
            XBracingCylinderY   = (float)s.XBracingCylinderY,
            XBracingCylinderFlipDirection = s.XBracingCylinderFlipDirection,
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
            WavePhaseMethod = s.WavePhaseMethod,
            AdaptiveLayerHeight = s.AdaptiveLayerHeight,
            AdaptiveQuality     = (float)s.AdaptiveQuality,
            MinLayerHeight      = (float)s.MinLayerHeight,
            OverhangOrientation = s.OverhangOrientation,
            MaxOverhangTiltDeg  = (float)s.MaxOverhangTiltDeg,
            SmoothRotation                = s.SmoothRotation,
            SmoothRotationRadius          = s.SmoothRotationRadius,
            SmoothRotationMaxRateDegPerMm = (float)s.SmoothRotationMaxRateDegPerMm,
            InfillPattern = ResolveInfillPatternForSlice(s),
            InfillSpacingMm = (float)s.InfillSpacingMm,
            InfillAngleDeg  = (float)s.InfillAngleDeg,
            LightningOverhangDeg     = (float)s.LightningOverhangDeg,
            LightningBranchSpacingMm = (float)s.LightningBranchSpacingMm,
            LightningTipLoopRadiusMm = (float)s.LightningTipLoopRadiusMm,
            // Affect Interior / Affect Exterior (UI) map onto anchor + exterior demand.
            LightningAnchorInterior  = s.LightningAffectInterior,
            LightningAnchorExterior  = s.LightningAffectExterior,
            LightningExteriorOverhangs = s.LightningAffectExterior,
            LightningButtressBarMm         = (float)s.LightningButtressBarMm,
            LightningPreferInteriorMouths  = s.LightningPreferInteriorMouths,
            LightningTargetSupportSelections = s.LightningTargetSupportSelections,
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
            WipeSkipShortTravels = s.WipeSkipShortTravels,
            FlowRate     = (float)(s.SelectedPreset?.FlowRate ?? 0.463),
            ResumeRampEnabled          = s.ResumeRampEnabled,
            ResumeRampStartSpeedMps    = (float)(s.ResumeRampStartSpeed / 1000.0),
            ResumeRampStartRpmPercent  = (float)s.ResumeRampStartRpmPercent,
            ResumeRampDistanceMm       = (float)s.ResumeRampDistanceMm,
            ResumeRampSteps            = s.ResumeRampSteps,
            LayerSpeedAdaptEnabled     = s.LayerSpeedAdaptEnabled,
            LayerSpeedBasis            = s.LayerSpeedBasis,
            LayerSpeedMinMmS           = (float)s.LayerSpeedMinMmS,
            LayerSpeedMaxMmS           = (float)s.LayerSpeedMaxMmS,
            MultiPlanarPlanes          = s.MultiPlanarPlanes
                .Select(r => new MassiveSlicer.Core.Models.MultiPlanarPlane((float)r.HeightPct, (float)r.AngleDeg))
                .ToList(),
            MultiPlanarAxisX           = s.MultiPlanarAxisX,
            ThermalDepositTempC        = (float)Math.Max(s.Temperature1, Math.Max(s.Temperature2, s.Temperature3)),
            ThermalGlassTransitionC    = ResolveThermalGlassTransitionC(s.SelectedPreset),
            ThermalAmbientTempC        = (float)(s.SelectedPreset?.ThermalAmbientC ?? 30.0),
            ThermalDensityGmCc         = (float)(s.SelectedPreset?.MaterialDensity ?? 1.05),
            ThermalBondMarginC         = ResolveThermalBondMarginC(s.SelectedPreset),
            ThermalSagMarginC          = ResolveThermalSagMarginC(s.SelectedPreset),
            SeamGuidePoints = s.BuildSeamGuideList(),
            PaintMarks      = s.BuildPaintMarkList(),
            StructuralSupports = s.BuildStructuralSupportList(),
            CurvedBoundaryLowVertices   = s.BuildCurvedLowBoundaryList(),
            CurvedBoundaryHighVertices  = s.BuildCurvedHighBoundaryList(),
            CurvedBoundarySource        = s.CurvedBoundarySource,
            CurvedAutoDetectBandMm      = (float)s.CurvedAutoDetectBandMm,
            CurvedEnableRegionSplit     = s.CurvedEnableRegionSplit,
            OrientationFollowStrength   = s.OrientationFollowStrength,
            OrientationMaxTiltDeg       = (float)s.OrientationMaxTiltDeg,
            FirstLayerZeroTilt          = s.FirstLayerZeroTilt,
            LayerLeanStrength           = (float)(s.LayerLeanPercent / 100.0),
            LayerLeanMaxTiltDeg         = (float)s.LayerLeanMaxTiltDeg,
        };
    }

    /// <summary>
    /// Glass-transition for thermal sim: material preset override when &gt; 0,
    /// otherwise the family lookup from material type (ABS→105, PEI→217, …).
    /// </summary>
    private static float ResolveThermalGlassTransitionC(MaterialPreset? preset)
    {
        if (preset is not null && preset.GlassTransitionC > 0)
            return (float)preset.GlassTransitionC;
        return ThermalSimulator.GlassTransitionC(preset?.MaterialType);
    }

    private static float ResolveThermalBondMarginC(MaterialPreset? preset)
    {
        if (preset is not null && preset.ThermalBondMarginC > 0)
            return (float)preset.ThermalBondMarginC;
        return ThermalSimulator.DefaultBondMarginC;
    }

    private static float ResolveThermalSagMarginC(MaterialPreset? preset)
    {
        if (preset is not null && preset.ThermalSagMarginC > 0)
            return (float)preset.ThermalSagMarginC;
        return ThermalSimulator.DefaultSagMarginC;
    }

    private static async Task<(Toolpath smoothed, Toolpath raw, SliceSettings settings)> ComputeToolpathAsync(
        List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)> meshSnapshots,
        SliceMethod method,
        SliceSettings settings,
        Action<string>? reportProgress = null,
        Action<double>? reportPercent = null,
        CancellationToken cancel = default)
    {
        void Report(string msg) => reportProgress?.Invoke(msg);
        void Pct(double p)      => reportPercent?.Invoke(p);
        void ThrowIfCancel() => cancel.ThrowIfCancellationRequested();

        Report("Preparing mesh…");
        Pct(1);
        ThrowIfCancel();
        SliceLogger.BeginSession($"ComputeToolpathAsync  method={method}  wave={settings.WaveEffect}");
        var (smoothedToolpath, rawToolpath) = await Task.Run(() =>
        {
            ThrowIfCancel();
            SliceLogger.Step("background thread started");
            var flatMeshes = new List<NVec3[]>(meshSnapshots.Count);
            foreach (var (positions, indices, world) in meshSnapshots)
            {
                ThrowIfCancel();
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
            SliceLogger.Step($"mesh prepared  snapshots={meshSnapshots.Count}  tris={flatMeshes.Sum(m => m.Length) / 3:N0}");
            ThrowIfCancel();

            Report(method switch
            {
                SliceMethod.Curved   => "Curved (Sweep): computing boundaries and layers…",
                SliceMethod.Geodesic => "Geodesic: computing surface-distance layers…",
                SliceMethod.Angled   => "Angled: intersecting tilted planes…",
                SliceMethod.MultiPlanar => "Multi-Planar: interpolating guide planes…",
                _                    => "Planar: intersecting layers…",
            });
            Pct(5);

            // Stage weights: slicing dominates wall-clock, so it owns 5→75%.
            // Progress callback also polls cancel so long planar runs abort mid-stack.
            void SlicePct(float f)
            {
                ThrowIfCancel();
                Pct(5 + f * 70);
            }

            Toolpath tp;
            if (method == SliceMethod.Angled)        tp = AngledPlanarSlicer.Slice(flatMeshes, settings);
            else if (method == SliceMethod.MultiPlanar) tp = AngledPlanarSlicer.SliceMultiPlanar(flatMeshes, settings);
            else if (method == SliceMethod.Geodesic) tp = GeodesicSlicer.Slice(flatMeshes, settings, SlicePct);
            else if (method == SliceMethod.Curved)   tp = CurvedSlicer.Slice(flatMeshes, settings);
            else                                     tp = PlanarSlicer.Slice(flatMeshes, settings, SlicePct);
            ThrowIfCancel();
            int lightningMoves = 0;
            foreach (var lyr in tp.Layers)
                foreach (var mv in lyr.Moves)
                    if (mv.IsLightning) lightningMoves++;
            SliceLogger.Step($"slicer done  layers={tp.Layers.Count}  moves={tp.Layers.Sum(l => l.Moves.Count)}  lightningMoves={lightningMoves}");
            if (MassiveSlicer.Core.Slicing.Lightning.LightningPlanner.IsFormboundPattern(settings.InfillPattern)
                || settings.XBracingEnabled)
            {
                // Stdout mirror during background slice; UI Console.Log runs on completion.
                if (tp.FormboundStats is { } fs)
                    System.Console.WriteLine(fs.ToLogLine());
                System.Console.WriteLine(
                    $"[formbound] emit: {lightningMoves} lightning bead segment(s) across {tp.Layers.Count} layer(s) " +
                    $"(pattern={settings.InfillPattern}, bar={settings.LightningButtressBarMm:0.#}mm" +
                    $", xBracing={settings.XBracingEnabled})");
            }
            Pct(75);
            ThrowIfCancel();

            // Effects/post-processors rebuild the toolpath and drop FormboundStats —
            // capture now and re-stamp on the results so the console report survives.
            var fbStats = tp.FormboundStats;
            var sliceWarnings = tp.Warnings.Count > 0 ? tp.Warnings.ToList() : null;

            Report("Applying post-processing…");
            tp = WaveEffect.Apply(tp, settings);
            ThrowIfCancel();
            tp = MassiveSlicer.Core.Slicing.Effects.PatternEffect.Apply(tp, settings);
            tp = MassiveSlicer.Core.Slicing.Effects.SpiralizeEffect.Apply(tp, settings);
            SliceLogger.Step($"WaveEffect done  moves={tp.Layers.Sum(l => l.Moves.Count)}");
            Pct(80);
            ThrowIfCancel();

            tp = MovementPostProcessor.Apply(tp, settings);
            SliceLogger.Step("MovementPostProcessor done");

            tp = ResumeRampPostProcessor.Apply(tp, settings);
            SliceLogger.Step("ResumeRampPostProcessor done");
            Pct(84);
            ThrowIfCancel();

            var raw = ToolpathClone.Copy(tp);
            SliceLogger.Step("ToolpathClone.Copy(raw) done");
            Pct(88);
            ThrowIfCancel();

            var withSpeed = LayerSpeedPostProcessor.Apply(ToolpathClone.Copy(tp), settings);
            SliceLogger.Step("LayerSpeedPostProcessor done");
            Pct(93);
            ThrowIfCancel();

            var toSmooth = ToolpathClone.Copy(withSpeed);
            SliceLogger.Step("ToolpathClone.Copy(toSmooth) done");
            Pct(96);

            OrientationBlender.ApplyInPlace(toSmooth, settings.OrientationFollowStrength, settings.OrientationMaxTiltDeg, settings.FirstLayerZeroTilt);
            LayerLeanOrienter.ApplyInPlace(toSmooth, settings.LayerLeanStrength, settings.LayerLeanMaxTiltDeg, settings.BeadWidth);
            SliceLogger.Step("OrientationBlender done");
            ThrowIfCancel();

            var smoothed = OrientationSmoother.Apply(toSmooth, settings);
            SliceLogger.Step("OrientationSmoother done");

            ThermalSimulator.StampLayerTemps(smoothed, settings);
            Pct(100);

            smoothed.FormboundStats ??= fbStats;
            raw.FormboundStats ??= fbStats;
            if (sliceWarnings is not null)
            {
                smoothed.Warnings.AddRange(sliceWarnings);
                raw.Warnings.AddRange(sliceWarnings);
            }
            return (smoothed, raw);
        }, cancel);

        SliceLogger.Step("back on UI thread — returning result");
        SliceLogger.EndSession();
        return (smoothedToolpath, rawToolpath, settings);
    }

    private Toolpath RebuildProcessedToolpath(Toolpath raw, AdditiveSettingsViewModel s)
    {
        var settings = BuildSliceSettings(s);
        var withLayerSpeed = LayerSpeedPostProcessor.Apply(ToolpathClone.Copy(raw), settings);
        var toSmooth       = ToolpathClone.Copy(withLayerSpeed);
        OrientationBlender.ApplyInPlace(toSmooth, settings.OrientationFollowStrength, settings.OrientationMaxTiltDeg, settings.FirstLayerZeroTilt);
            LayerLeanOrienter.ApplyInPlace(toSmooth, settings.LayerLeanStrength, settings.LayerLeanMaxTiltDeg, settings.BeadWidth);
        var smoothed = OrientationSmoother.Apply(toSmooth, settings);
        ThermalSimulator.StampLayerTemps(smoothed, settings);
        return smoothed;
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

        // The timeline must always follow the DISPLAYED toolpath. Some replace
        // paths (workspace-restore ordering, background re-uploads) staged a new
        // toolpath without re-pointing the scrub, leaving ActiveScrubToolpath on
        // a stale object — the speed/RPM readout, tpfix/tpopt and KRL export then
        // saw pre-Adaptive-Speed values while the renderer drew the new ones.
        if (ReferenceEquals(entry.Node, _activeScrubNode))
        {
            var tpNew = entry.Toolpath;
            var node  = entry.Node;
            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not ViewportViewModel vmStage) return;
                if (!ReferenceEquals(node, _activeScrubNode)) return;
                if (ReferenceEquals(vmStage.ActiveScrubToolpath, tpNew)) return;
                int newMax = tpNew.Layers.Sum(l => l.Moves.Count);
                vmStage.ResetScrubIndex(newMax, tpNew, preservePosition: true);
            });
        }
    }

    private void UploadToolpathEntry(PendingToolpathEntry entry, bool addToScene)
    {
        int moveCount = entry.Toolpath.Layers.Sum(l => l.Moves.Count);
        SliceLogger.BeginSession($"UploadToolpathEntry  addToScene={addToScene}  moves={moveCount}");
        try
        {
            SliceLogger.Step("StageToolpathMaps");
            StageToolpathMaps(entry);

            SliceLogger.Step(addToScene ? "renderer.AddToolpath" : "renderer.ReplaceToolpath");
            if (addToScene)
                _renderer.AddToolpath(entry.Toolpath, entry.Node, entry.BeadWidth, entry.LayerHeight, entry.MaterialColor);
            else
                _renderer.ReplaceToolpath(entry.Toolpath, entry.Node, entry.BeadWidth, entry.LayerHeight, entry.MaterialColor);

            SliceLogger.Step("pose/transform");
            // Add/Replace always parks the node at the pure geometry-centroid translation.
            // Keep that as the origin for scrub/IK and for PreserveRelativePose on the next
            // re-slice — never substitute the full node translation (which may include user
            // gizmo offsets and would double-count on the next update).
            var centroidLocal = entry.Node.LocalTransform;
            var geometryCentroid = new NVec3(
                centroidLocal.Row3.X, centroidLocal.Row3.Y, centroidLocal.Row3.Z);

            if (entry.PreserveRelativePose && entry.PreservedLocalTransform is Matrix4 preservedLocal)
            {
                // Just keep the node exactly where it already was — do NOT rebase through the
                // old/new centroids (the previous formula here was preservedLocal * invOldOrigin
                // * centroidLocal, which double-counts whenever the mesh actually moved: the new
                // centroid already reflects that move — since ComputeToolpathAsync slices the
                // mesh's CURRENT world-transformed vertices — so re-adding the delta via the old
                // origin added it a second time. Worked through algebraically: however the node
                // got to its current position (drag-link mesh-follow, a direct manual nudge, or
                // no move at all), preservedLocal already IS the one correct answer once the
                // mesh's own contribution is accounted for — the old/new centroid terms cancel
                // out completely. This was the root cause of a toolpath appearing to "move extra"
                // whenever a drag triggered an auto-reslice.
                entry.Node.LocalTransform = preservedLocal;
            }
            else if (entry.LocalTransformOverride is Matrix4 lt)
            {
                // Workspace restore: apply the saved node transform unless it is effectively
                // identity while the geometry lives far from the origin (a bad save would
                // pin the toolpath at robot-root / world zero).
                bool nearIdentity =
                    MathF.Abs(lt.Row3.X) < 1f && MathF.Abs(lt.Row3.Y) < 1f && MathF.Abs(lt.Row3.Z) < 1f
                    && MathF.Abs(lt.Row0.X - 1f) < 1e-3f && MathF.Abs(lt.Row1.Y - 1f) < 1e-3f
                    && MathF.Abs(lt.Row2.Z - 1f) < 1e-3f;
                bool geometryFar =
                    geometryCentroid.LengthSquared() > 100f * 100f; // >100 mm from origin
                if (!(nearIdentity && geometryFar))
                    entry.Node.LocalTransform = lt;
            }

            SliceLogger.Step("ComputeOverhangPerFlatMove");
            var overhang = ComputeOverhangPerFlatMove(entry.Toolpath, entry.BeadWidth);
            SliceLogger.Step("UpdateToolpathBeadOverhang");
            _renderer.UpdateToolpathBeadOverhang(entry.Node, overhang);
            SliceLogger.Step("ComputeOrientationRatePerFlatMove");
            var orientationRates = ComputeOrientationRatePerFlatMove(entry.Toolpath);
            SliceLogger.Step("UpdateToolpathBeadOrientation");
            _renderer.UpdateToolpathBeadOrientation(entry.Node, orientationRates);

            // Always the geometry centroid used when building the VBO (points are relative to it).
            _toolpathOriginByNode[entry.Node] = geometryCentroid;
            SliceLogger.EndSession("UploadToolpathEntry done");
        }
        catch (Exception ex)
        {
            SliceLogger.Error("UploadToolpathEntry", ex);
            // Swallow so the GL thread survives — the toolpath just won't render.
            // Full multi-line detail goes to the console (per-line copy) so shader
            // logs are not lost in the truncated status bar overlay.
            LogRenderExceptionToConsole(ex);
        }
    }

    /// <summary>
    /// Writes a render/shader failure to the app console as separate error lines
    /// (easy to copy one-by-one) and sets a short status-bar summary.
    /// </summary>
    private void LogRenderExceptionToConsole(Exception ex)
    {
        var lines = new List<string>
        {
            $"[gl] Render error: {ex.GetType().Name}: {FirstLine(ex.Message)}",
        };
        // Shader compilers put the useful log after the first line / in the message body.
        foreach (var part in SplitLines(ex.Message).Skip(1))
            lines.Add($"[gl]   {part}");
        if (ex.InnerException is { } inner)
        {
            lines.Add($"[gl]   inner: {FirstLine(inner.Message)}");
            foreach (var part in SplitLines(inner.Message).Skip(1))
                lines.Add($"[gl]     {part}");
        }
        if (ex.StackTrace is { Length: > 0 } st)
        {
            lines.Add("[gl]   stack:");
            foreach (var frame in SplitLines(st).Take(12))
                lines.Add($"[gl]     {frame.Trim()}");
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is { } vm)
                SetSliceStatus(vm,
                    $"Render error: {ex.GetType().Name}: {FirstLine(ex.Message)}",
                    isError: true);

            if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel mvm)
            {
                foreach (var line in lines)
                    mvm.Console.LogError(line);
            }
            else if (_vm?.OnDevLog is { } log)
            {
                foreach (var line in lines)
                    log(line);
            }
        });
    }

    private static string FirstLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int n = s.IndexOfAny(['\r', '\n']);
        return n < 0 ? s : s[..n];
    }

    private static IEnumerable<string> SplitLines(string? s)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        using var reader = new StringReader(s);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0)
                yield return line;
    }

    private void ApplyToolpathStats(ViewportViewModel vm, Toolpath smoothedToolpath)
    {
        if (vm.AdditiveSettings is not { } as2) return;
        ApplyToolpathStats(vm, smoothedToolpath, as2);
    }

    private static void ApplyToolpathStats(ViewportViewModel vm, Toolpath toolpath, AdditiveSettingsViewModel s)
    {
        var (t, w, c, layerStats, massKg) = ComputeToolpathStats(toolpath, s, vm.Erp.PricingConfig);
        vm.StatsTimeSeconds        = layerStats.TotalTimeSeconds;
        vm.StatsWeightKg           = massKg;
        vm.StatsTime               = t;
        vm.StatsWeight             = w;
        vm.StatsCost               = c;
        vm.StatsLongestLayerLength = ToolpathStatistics.FormatLayerLength(layerStats.LongestCutLength);
        vm.StatsShortestLayerLength = ToolpathStatistics.FormatLayerLength(layerStats.ShortestCutLength);
        vm.StatsLongestLayerTime   = ToolpathStatistics.FormatLayerTime(layerStats.LongestTime);
        vm.StatsShortestLayerTime  = ToolpathStatistics.FormatLayerTime(layerStats.ShortestTime);
        vm.HasToolpathStats        = true;
    }

    private static void ClearToolpathStats(ViewportViewModel vm)
    {
        vm.HasToolpathStats         = false;
        vm.StatsTime                = "";
        vm.StatsWeight              = "";
        vm.StatsCost                = "";
        vm.StatsLongestLayerLength  = "";
        vm.StatsShortestLayerLength = "";
        vm.StatsLongestLayerTime    = "";
        vm.StatsShortestLayerTime   = "";
    }

    private void SetSliceStatus(ViewportViewModel vm, string message, bool isError = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            vm.SliceStatusIsError = isError;
            vm.SliceStatusMessage = message;
        });
    }

    private void ScheduleClearSliceStatus(ViewportViewModel vm, int delayMs = 6000)
    {
        int gen = ++_sliceStatusClearGen;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _sliceStatusClearGen || vm.IsSlicing) return;
                vm.SliceStatusMessage = string.Empty;
                vm.SliceStatusIsError = false;
            });
        });
    }

    private void LogFormboundEmitStats(Toolpath tp, SliceSettings settings)
    {
        bool formbound = MassiveSlicer.Core.Slicing.Lightning.LightningPlanner
            .IsFormboundPattern(settings.InfillPattern);
        if (!formbound && !settings.XBracingEnabled)
            return;
        int lightningMoves = 0;
        foreach (var lyr in tp.Layers)
            foreach (var mv in lyr.Moves)
                if (mv.IsLightning) lightningMoves++;
        void Log(string msg)
        {
            if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel mvm)
                mvm.Console.Log(msg);
            else
                System.Console.WriteLine(msg);
        }
        // Planner diagnostics (demand / coverage / inherit / x-bracing).
        if (tp.FormboundStats is { } stats)
        {
            Log(stats.ToLogLine());
            foreach (var line in stats.UncoveredLog)
                Log(line);
        }
        // X-bracing logs live on UncoveredLog even when FormboundStats is null —
        // re-emit any x-bracing lines from toolpath if present via FormboundStats only.
        bool zigZag = settings.ZigZagSeam;
        Log($"[formbound] emit: {lightningMoves} lightning bead segment(s) / {tp.Layers.Count} layer(s) " +
            $"(bar={settings.LightningButtressBarMm:0.#}mm" +
            (settings.XBracingEnabled
                ? $", xBracing depth={settings.XBracingDepthMm:0.#} span={settings.XBracingSpanMm:0.#}"
                : "") +
            (zigZag ? ", zigZag single-skin" : "") +
            ")");
        // Zig-zag X-bracing inserts hairpin detours into open paths (not IsLightning moves).
        // Do not treat lightningMoves==0 as failure in that mode.
        if (settings.XBracingEnabled && lightningMoves == 0 && !zigZag)
            Log("[x-bracing] no lightning geometry emitted — try lower Depth, or enable Formbound bridges visibility");
        else if (settings.XBracingEnabled && zigZag)
            Log("[x-bracing] zig-zag single-skin: hairpins are open-path detours (not formbound lightning beads)");
    }

    private static string SliceMethodLabel(SliceMethod method) => method switch
    {
        SliceMethod.Curved   => "Curved (Sweep)",
        SliceMethod.Geodesic => "Geodesic",
        SliceMethod.Angled   => "Angled",
        _                    => "Planar",
    };

    private async Task RunSliceAsync(ViewportViewModel vm)
    {
        if (vm.IsSlicing || vm.OutlinerItems.Count == 0) return;
        var cancel = BeginSliceCancellation();
        _sliceStatusClearGen++;
        vm.IsSlicing = true;
        vm.SliceStatusIsError = false;
        SetSliceStatus(vm, "Slicing…");

        try
        {
            var sourceItem = vm.OwningModelItem(
                                 vm.FindUserMeshOutlinerItem(_renderer.SelectedNode)
                                 ?? vm.ResolveActivePrintObjectItem())
                             ?? vm.EnumerateUserModelItems().FirstOrDefault();
            if (sourceItem is null)
            {
                SetSliceStatus(vm, "Slice failed: select a mesh to slice.", isError: true);
                return;
            }

            // One evolving toolpath per model: if this model was already sliced,
            // update it in place instead of adding another toolpath node.
            if (sourceItem.Children.FirstOrDefault(c => c.IsToolpath) is { } existingToolpath)
            {
                vm.IsSlicing = false;   // hand off to the update path (it re-guards)
                await RunUpdateSliceAsync(vm, (sourceItem, existingToolpath));
                return;
            }

            var meshSnapshots = CollectMeshSnapshots(sourceItem, requireVisible: true);
            if (meshSnapshots.Count == 0)
            {
                SetSliceStatus(vm, "Slice failed: mesh has no geometry.", isError: true);
                return;
            }

            // Additive stock from the material maps: print the displaced surface (low-poly mesh +
            // PBR-map detail) inflated by a uniform allowance, so the blank carries the detail and
            // the mill has consistent material everywhere. Map + distance come from the MILLING panel
            // (single source of truth); additive only adds the allowance.
            if (vm.AdditiveSettings?.UseDisplacedStock == true && vm.SubtractiveSettings is { } sub2)
            {
                var built = await ComputeDisplacedSurfaceAsync(vm, sub2, extraOffsetMm: (float)vm.AdditiveSettings.StockAllowanceMm);
                if (built is { } db)
                {
                    var tkPos = Array.ConvertAll(db.result.Positions, p => new TkVector3(p.X, p.Y, p.Z));
                    var tkIdx = Array.ConvertAll(db.result.Indices, i => (uint)i);
                    meshSnapshots = [(tkPos, tkIdx, TkMatrix4.Identity)];
                    System.Console.Error.WriteLine(
                        $"[slice] additive stock from PBR maps: {db.result.VertexCount:N0} verts + " +
                        $"{vm.AdditiveSettings.StockAllowanceMm:0.#} mm allowance.");
                }
                else
                {
                    System.Console.Error.WriteLine("[slice] displaced stock requested but unavailable; slicing raw mesh.");
                }
            }

            cancel.ThrowIfCancellationRequested();
            var method   = vm.AdditiveSettings?.Method ?? SliceMethod.Planar;
            var settings = BuildSliceSettings(vm.AdditiveSettings);
            ApplyEffectorSettings(vm, settings);
            SetSliceStatus(vm, $"{SliceMethodLabel(method)}: slicing…");
            var (smoothedToolpath, rawToolpath, _) = await ComputeToolpathAsync(
                meshSnapshots, method, settings, msg => SetSliceStatus(vm, msg),
                pct => Dispatcher.UIThread.Post(() => vm.SliceProgressPercent = pct),
                cancel);

            cancel.ThrowIfCancellationRequested();
            LogFormboundEmitStats(smoothedToolpath, settings);

            int layerCount = smoothedToolpath.Layers.Count;
            if (layerCount == 0)
            {
                SetSliceStatus(vm, "Slice finished with 0 layers — check mesh, boundaries, and settings.", isError: true);
                return;
            }

            var toolpathName = ToolpathNameFrom(sourceItem.Name);
            var toolpathNode = new SceneNode { Name = toolpathName, Selectable = true };
            vm.RegisterToolpathInOutliner(toolpathNode, sourceItem);
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

            // Visibility is governed by the view mode pills; slicing keeps the current
            // mode (no auto-jump to Toolpath) so imports land in Body view.
            vm.ApplyViewMode();

            _renderer.Select(null);
            UpdateFocusOverlay();
            GlCanvas.RequestNextFrameRendering();

            int moveCount = smoothedToolpath.Layers.Sum(l => l.Moves.Count);
            if (smoothedToolpath.Warnings.Count > 0)
                SetSliceStatus(vm, $"⚠ {smoothedToolpath.Warnings[0]}", isError: true);
            else
            {
                SetSliceStatus(vm, $"Slice complete — {layerCount} layers, {moveCount:N0} moves");
                ScheduleClearSliceStatus(vm);
            }
            vm.MarkWorkspaceDirty?.Invoke();
        }
        catch (OperationCanceledException)
        {
            SetSliceStatus(vm, "Slice cancelled — applying latest changes…");
            System.Console.WriteLine("[slice] cancelled (superseded by newer settings)");
        }
        catch (Exception ex)
        {
            SetSliceStatus(vm, $"Slice failed: {ex.Message}", isError: true);
            System.Console.Error.WriteLine($"[slice] {ex}");
        }
        finally
        {
            vm.IsSlicing = false;
        }
    }

    /// <summary>
    /// Auto-calculates the angled-slicer tilt with the least overhang risk for the active print
    /// object. With <paramref name="rotateMesh"/> the whole lean azimuth is searched and the mesh
    /// is yaw-rotated (about Z, staying flat on the bed) so the winner becomes a pure Y tilt.
    /// </summary>
    private async Task RunAutoTiltAsync(ViewportViewModel vm, bool rotateMesh)
    {
        if (vm.AdditiveSettings is not { } add || add.IsAutoTiltRunning) return;

        var item = vm.ResolveActivePrintObjectItem()
                   ?? vm.FindUserMeshOutlinerItem(_renderer.SelectedNode);
        if (item is null)
        {
            SetSliceStatus(vm, "Auto tilt: select a mesh first.", isError: true);
            return;
        }

        var snapshots = CollectMeshSnapshots(item, requireVisible: false);
        if (snapshots.Count == 0)
        {
            SetSliceStatus(vm, "Auto tilt: mesh has no geometry.", isError: true);
            return;
        }

        add.IsAutoTiltRunning = true;
        SetSliceStatus(vm, rotateMesh
            ? "Auto tilt: optimising mesh rotation + tilt…"
            : "Auto tilt: optimising X/Y tilt…");
        try
        {
            float curX = (float)add.TiltAngleX;
            float curY = (float)add.TiltAngle;

            var (result, center) = await Task.Run(() =>
            {
                // Same world-space triangle soup the angled slicer consumes.
                var soup = new List<NVec3[]>(snapshots.Count);
                var min  = new NVec3(float.MaxValue);
                var max  = new NVec3(float.MinValue);
                foreach (var (positions, indices, world) in snapshots)
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
                    foreach (var p in flat)
                    {
                        min = NVec3.Min(min, p);
                        max = NVec3.Max(max, p);
                    }
                    soup.Add(flat);
                }
                var opt = TiltOptimizer.Optimize(soup, curX, curY, allowMeshYaw: rotateMesh);
                return (opt, (min + max) * 0.5f);
            });

            if (rotateMesh && MathF.Abs(result.MeshYawDeg) > 0.05f)
            {
                // Yaw about world Z through the part's footprint centre — stays flat on the bed.
                var node        = item.Node;
                var parentWorld = node.Parent?.WorldTransform ?? TkMatrix4.Identity;
                var c           = new TkVector3(center.X, center.Y, center.Z);
                var rot         = TkMatrix4.CreateTranslation(-c)
                                * TkMatrix4.CreateRotationZ(result.MeshYawDeg * MathF.PI / 180f)
                                * TkMatrix4.CreateTranslation(c);
                node.LocalTransform = node.WorldTransform * rot * parentWorld.Inverted();
                vm.NotifyRenderNeeded();
                GlCanvas.RequestNextFrameRendering();
            }

            add.TiltAngleX = Math.Round(result.TiltXDeg, 1);
            add.TiltAngle  = Math.Round(result.TiltYDeg, 1);

            string summary = rotateMesh
                ? $"Auto tilt: mesh rotated {result.MeshYawDeg:0.#}°, Y tilt {result.TiltYDeg:0.#}°"
                : $"Auto tilt: X {result.TiltXDeg:0.#}°, Y {result.TiltYDeg:0.#}°";
            SetSliceStatus(vm, $"{summary} — overhang risk {result.RiskBefore * 100:0.#}% → {result.RiskAfter * 100:0.#}%");
            ScheduleClearSliceStatus(vm);
            System.Console.WriteLine(
                $"[tilt] {summary}  risk {result.RiskBefore * 100:0.##}% -> {result.RiskAfter * 100:0.##}%");
        }
        catch (Exception ex)
        {
            SetSliceStatus(vm, $"Auto tilt failed: {ex.Message}", isError: true);
            System.Console.Error.WriteLine($"[tilt] {ex}");
        }
        finally
        {
            add.IsAutoTiltRunning = false;
        }
    }

    /// <summary>Feeds the live-effector handles (world positions + range/strength) into the slice.</summary>
    private static void ApplyEffectorSettings(ViewportViewModel vm, SliceSettings settings)
    {
        if (vm.AdditiveSettings is not { EffectorEnabled: true } add) return;
        settings.EffectorPoints     = vm.GetActiveEffectorPositions();
        settings.EffectorRadiusMm   = (float)add.EffectorRange;
        settings.EffectorStrengthMm = (float)add.EffectorStrength;
        settings.EffectorMode       = add.IsEffectorAmplify
            ? MassiveSlicer.Core.Models.EffectorMode.Amplify
            : MassiveSlicer.Core.Models.EffectorMode.Erase;
    }

    private static MassiveSlicer.Core.Models.MillSettings BuildMillSettings(SubtractiveSettingsViewModel s) => new()
    {
        ToolDiameterMm    = (float)s.ToolDiameterMm,
        ToolEnd           = s.BallEnd ? MassiveSlicer.Core.Models.ToolEndType.Ball
                                      : MassiveSlicer.Core.Models.ToolEndType.Flat,
        StepoverMm        = (float)s.StepoverMm,
        StepdownMm        = (float)s.StepdownMm,
        FinishAllowanceMm = (float)s.FinishAllowanceMm,
        FeedRateMmMin     = (float)s.FeedRateMmMin,
        PlungeFeedMmMin   = (float)s.PlungeFeedMmMin,
        RapidZMm          = (float)s.RapidZMm,
        SpindleRpm        = (float)s.SpindleRpm,
        MaxDepthMm        = s.MaxDepthMm > 0 ? (float)s.MaxDepthMm : float.PositiveInfinity,
    };

    /// <summary>Generates a relief-milling toolpath from the subtractive heightmap, referencing
    /// the selected blank's nominal top face (v1). Carves into the blank; the blank stays visible.</summary>
    private async Task RunMillAsync(ViewportViewModel vm)
    {
        if (vm.IsSlicing) return;
        var sub = vm.SubtractiveSettings;
        if (sub is null) return;
        if (string.IsNullOrWhiteSpace(sub.HeightmapPath) || !System.IO.File.Exists(sub.HeightmapPath))
        {
            System.Console.Error.WriteLine("[mill] no heightmap selected (set one in the Subtractive tab).");
            return;
        }

        vm.IsSlicing = true;
        try
        {
            var selectedNode = _renderer.SelectedNode;
            if (vm.FindUserMeshOutlinerItem(selectedNode) is not { } sourceItem)
            {
                System.Console.Error.WriteLine("[mill] select the blank mesh first.");
                return;
            }

            var meshSnapshots = CollectMeshSnapshots(sourceItem, requireVisible: true);
            if (meshSnapshots.Count == 0)
            {
                System.Console.Error.WriteLine("[mill] select the blank mesh first.");
                return;
            }

            // World-space AABB of the selected blank (for auto reference plane + footprint).
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            foreach (var (positions, _, world) in meshSnapshots)
                foreach (var p in positions)
                {
                    var w = TransformPoint(p, world);
                    if (w.X < minX) minX = w.X; if (w.X > maxX) maxX = w.X;
                    if (w.Y < minY) minY = w.Y; if (w.Y > maxY) maxY = w.Y;
                    if (w.Z > maxZ) maxZ = w.Z;
                }

            float refZ = sub.AutoReferenceFromTop ? maxZ : (float)sub.ReferencePlaneZ;
            float ox   = sub.AutoFootprint ? minX : (float)sub.FootprintOriginX;
            float oy   = sub.AutoFootprint ? minY : (float)sub.FootprintOriginY;
            float fw   = sub.AutoFootprint ? (maxX - minX) : (float)sub.FootprintWidthMm;
            float fl   = sub.AutoFootprint ? (maxY - minY) : (float)sub.FootprintLengthMm;

            var map  = MassiveSlicer.App.Services.ReliefMapLoader.LoadFromImage(
                sub.HeightmapPath, ox, oy, fw, fl, (float)sub.HeightScaleMm, sub.InvertHeightmap, refZ);
            var mill = BuildMillSettings(sub);

            var toolpath = await Task.Run(() => MassiveSlicer.Core.Slicing.ReliefMillSlicer.Slice(map, mill));
            if (toolpath.Layers.Count == 0)
            {
                System.Console.Error.WriteLine("[mill] relief produced no cuts (check height scale / footprint).");
                return;
            }

            var toolpathNode = new SceneNode
            {
                Name = $"Relief Mill D{mill.ToolDiameterMm:0.#} SO{mill.StepoverMm:0.#}",
                Selectable = true,
            };
            vm.RegisterToolpathInOutliner(toolpathNode, sourceItem);
            vm.PendingToolpath.Enqueue(new PendingToolpathEntry
            {
                Toolpath      = toolpath,
                RawToolpath   = toolpath,
                Node          = toolpathNode,
                BeadWidth     = (float)sub.ToolDiameterMm,
                LayerHeight   = mill.StepdownMm,
                MaterialColor = MapMaterialColor(null),
            });

            ApplyToolpathStats(vm, toolpath);
            // Keep the blank visible — milling carves into it.
            _renderer.Select(null);
            UpdateFocusOverlay();
            GlCanvas.RequestNextFrameRendering();
        }
        finally
        {
            vm.IsSlicing = false;
        }
    }

    /// <summary>
    /// Builds the world-space displaced surface for the selected model (low-poly mesh pushed along
    /// its normals by a PBR-map height field) — the detailed geometry both the preview and the
    /// multi-axis mill use. World space, so the displacement distance is true mm regardless of the
    /// model's transform. Returns null (with a logged reason) when the selection can't be displaced.
    /// </summary>
    private async Task<(MassiveSlicer.Core.Slicing.DisplacedSurfaceBuilder.Result result, MeshData source)?>
        ComputeDisplacedSurfaceAsync(ViewportViewModel vm, SubtractiveSettingsViewModel sub, float extraOffsetMm = 0f)
    {
        if (_renderer.SelectedNode is not { } selected)
        {
            System.Console.Error.WriteLine("[displace] select a model first.");
            return null;
        }
        var meshNode = selected.SelfAndDescendants()
            .FirstOrDefault(n => n.Mesh?.PickingData is { Uvs: not null });
        if (meshNode?.Mesh?.PickingData is not { } mesh || mesh.Uvs is null)
        {
            System.Console.Error.WriteLine("[displace] selected model has no UVs — cannot sample its maps.");
            return null;
        }

        var height = MassiveSlicer.App.Services.PbrHeightFieldFactory.FromMaterial(
            mesh.Material,
            string.IsNullOrWhiteSpace(sub.HeightmapPath) ? null : sub.HeightmapPath,
            sub.InvertHeightmap);
        if (height is null)
        {
            System.Console.Error.WriteLine(
                "[displace] no displacement/height image supplied and no normal map on this model.");
            return null;
        }

        var world = meshNode.WorldTransform;
        float distance = (float)sub.DisplacementDistanceMm;
        int vcount = mesh.Positions.Length;

        // Lift to world space so the displacement distance is true mm.
        var wpos = new NVec3[vcount];
        var wnrm = new NVec3[vcount];
        var uv   = new System.Numerics.Vector2[vcount];
        for (int i = 0; i < vcount; i++)
        {
            wpos[i] = TransformPoint(mesh.Positions[i], world);
            wnrm[i] = TransformNormalWorld(mesh.Normals[i], world);
            uv[i]   = new System.Numerics.Vector2(mesh.Uvs[i].X, mesh.Uvs[i].Y);
        }
        int[] idx = mesh.Indices is { } mi
            ? Array.ConvertAll(mi, u => (int)u)
            : System.Linq.Enumerable.Range(0, vcount).ToArray();

        var result = await Task.Run(() => MassiveSlicer.Core.Slicing.DisplacedSurfaceBuilder.Build(
            wpos, wnrm, uv, idx, height, distance, bias: 0f, extraOffsetMm: extraOffsetMm));

        if (result.VertexCount == 0)
        {
            System.Console.Error.WriteLine("[displace] produced no geometry.");
            return null;
        }
        return (result, mesh);
    }

    /// <summary>Builds the displaced surface and adds it to the scene as a textured preview mesh.</summary>
    private async Task RunPreviewDisplacedAsync(ViewportViewModel vm)
    {
        if (vm.IsSlicing) return;
        var sub = vm.SubtractiveSettings;
        if (sub is null) return;

        vm.IsSlicing = true;
        try
        {
            if (await ComputeDisplacedSurfaceAsync(vm, sub) is not { } built) return;
            var (result, mesh) = built;
            float distance = (float)sub.DisplacementDistanceMm;

            var tkPos = Array.ConvertAll(result.Positions, p => new TkVector3(p.X, p.Y, p.Z));
            var tkNrm = Array.ConvertAll(result.Normals,   p => new TkVector3(p.X, p.Y, p.Z));
            var tkUv  = Array.ConvertAll(result.Uvs, p => new OpenTK.Mathematics.Vector2(p.X, p.Y));
            var tkIdx = Array.ConvertAll(result.Indices, i => (uint)i);

            // Keep the model's material so the displaced surface stays textured.
            var meshData = new MeshData(tkPos, tkNrm, tkIdx,
                $"Displaced surface ({distance:0.#} mm)", mesh.BaseColor, mesh.Metallic, mesh.Roughness,
                tkUv, null, mesh.Material);

            var previewNode = new SceneNode
            {
                Name           = meshData.Name,
                PendingMesh    = meshData,
                LocalTransform = TkMatrix4.Identity,
                Selectable     = true,
            };
            vm.AddImportNode(previewNode);
            System.Console.Error.WriteLine(
                $"[displace] {result.VertexCount:N0} verts, {result.TriangleCount:N0} tris @ {distance:0.#} mm.");
            GlCanvas.RequestNextFrameRendering();
        }
        finally
        {
            vm.IsSlicing = false;
        }
    }

    /// <summary>
    /// Generates a multi-axis surface-following finish toolpath over the displaced surface
    /// (tool axis follows the surface normal) and registers it as a toolpath node.
    /// </summary>
    private async Task RunMultiAxisMillAsync(ViewportViewModel vm)
    {
        if (vm.IsSlicing) return;
        var sub = vm.SubtractiveSettings;
        if (sub is null) return;

        vm.IsSlicing = true;
        try
        {
            if (await ComputeDisplacedSurfaceAsync(vm, sub) is not { } built) return;
            var result = built.result;
            var mill   = BuildMillSettings(sub);

            var toolpath = await Task.Run(() => MassiveSlicer.Core.Slicing.SurfaceFollowMillGenerator.GenerateMultiAxis(
                result.Positions, result.Normals, result.Indices, mill));
            if (toolpath.Layers.Count == 0)
            {
                System.Console.Error.WriteLine("[mill] multi-axis pass produced no cuts.");
                return;
            }

            var toolpathNode = new SceneNode
            {
                Name = $"Multi-Axis Mill D{mill.ToolDiameterMm:0.#} SO{mill.StepoverMm:0.#}",
                Selectable = true,
            };
            vm.RegisterToolpathInOutliner(toolpathNode, null);
            vm.PendingToolpath.Enqueue(new PendingToolpathEntry
            {
                Toolpath      = toolpath,
                RawToolpath   = toolpath,
                Node          = toolpathNode,
                BeadWidth     = (float)sub.ToolDiameterMm,
                LayerHeight   = mill.StepdownMm,
                MaterialColor = MapMaterialColor(null),
            });
            ApplyToolpathStats(vm, toolpath);
            _renderer.Select(null);
            UpdateFocusOverlay();
            GlCanvas.RequestNextFrameRendering();
            int moves = toolpath.Layers.Sum(l => l.Moves.Count);
            System.Console.Error.WriteLine($"[mill] multi-axis surface pass: {moves:N0} moves.");

            // Fail-rate analysis: how much of the ideal surface the tool over-cuts vs leaves proud.
            float toolR = (float)sub.ToolDiameterMm / 2f;
            float tol   = (float)sub.AnalysisToleranceMm;
            var report  = await Task.Run(() => MassiveSlicer.Core.Slicing.ToolpathSurfaceDeviation.Analyze(
                result.Positions, toolpath, toolR, tol));
            string summary = $"Fail {report.FailPct:F1}%  —  gouge {report.GougePct:F1}% (max {report.MaxGougeMm:F2} mm) / " +
                             $"residual {report.ResidualPct:F1}% (max {report.MaxResidualMm:F2} mm)  @ tol {report.ToleranceMm:F2} mm";
            sub.MillAnalysisText = summary;
            System.Console.Error.WriteLine($"[mill] {summary}");
        }
        finally
        {
            vm.IsSlicing = false;
        }
    }

    /// <summary>Transforms a normal by the matrix's 3x3 (row-vector convention) and renormalizes.</summary>
    private static NVec3 TransformNormalWorld(TkVector3 n, TkMatrix4 m)
    {
        float x = n.X * m.M11 + n.Y * m.M21 + n.Z * m.M31;
        float y = n.X * m.M12 + n.Y * m.M22 + n.Z * m.M32;
        float z = n.X * m.M13 + n.Y * m.M23 + n.Z * m.M33;
        var v = new NVec3(x, y, z);
        return v.LengthSquared() > 1e-12f ? NVec3.Normalize(v) : new NVec3(0, 0, 1);
    }

    // ── Realtime slicing (effector-style) ──────────────────────────────────
    // Any relevant parameter change re-slices the active model automatically,
    // debounced; updates replace the existing toolpath node in place.
    // If a change arrives mid-slice, the in-flight run is cancelled and the
    // latest settings are used for the next run (no stale queue).

    private DispatcherTimer? _realtimeSliceTimer;
    private bool _realtimeSlicePending;   // need another pass after current/cancelled slice
    private CancellationTokenSource? _sliceCts; // cancels ComputeToolpathAsync

    private static readonly HashSet<string> RealtimeSliceProps =
    [
        nameof(AdditiveSettingsViewModel.LayerHeight),
        nameof(AdditiveSettingsViewModel.FirstLayerHeight),
        nameof(AdditiveSettingsViewModel.BeadWidth),
        nameof(AdditiveSettingsViewModel.Method),
        nameof(AdditiveSettingsViewModel.MethodDisplayName),
        nameof(AdditiveSettingsViewModel.SlicingMode),
        nameof(AdditiveSettingsViewModel.TiltAngle),
        nameof(AdditiveSettingsViewModel.TiltAngleX),
        nameof(AdditiveSettingsViewModel.AdaptiveLayerHeight),
        nameof(AdditiveSettingsViewModel.AdaptiveQuality),
        nameof(AdditiveSettingsViewModel.MinLayerHeight),
        nameof(AdditiveSettingsViewModel.DisableContourOffset),
        nameof(AdditiveSettingsViewModel.InfillPattern),
        nameof(AdditiveSettingsViewModel.InfillSpacingMm),
        nameof(AdditiveSettingsViewModel.InfillAngleDeg),
        nameof(AdditiveSettingsViewModel.PatternType),
        nameof(AdditiveSettingsViewModel.SeamMode),
        nameof(AdditiveSettingsViewModel.ZigZagAllowSameLayerTravel),
        nameof(AdditiveSettingsViewModel.PatternMapping),
        nameof(AdditiveSettingsViewModel.PatternWavelengthMm),
        nameof(AdditiveSettingsViewModel.PatternAmplitude),
        nameof(AdditiveSettingsViewModel.PatternFrequency),
        nameof(AdditiveSettingsViewModel.PatternTwist),
        nameof(AdditiveSettingsViewModel.PatternOffset),
        nameof(AdditiveSettingsViewModel.PatternFadeIn),
        nameof(AdditiveSettingsViewModel.PatternFadeOut),
        nameof(AdditiveSettingsViewModel.EffectorEnabled),
        nameof(AdditiveSettingsViewModel.EffectorRange),
        nameof(AdditiveSettingsViewModel.EffectorStrength),
        nameof(AdditiveSettingsViewModel.EffectorMode),
        nameof(AdditiveSettingsViewModel.LightningOverhangDeg),
        nameof(AdditiveSettingsViewModel.LightningBranchSpacingMm),
        nameof(AdditiveSettingsViewModel.LightningTipLoopRadiusMm),
        nameof(AdditiveSettingsViewModel.LightningAffectInterior),
        nameof(AdditiveSettingsViewModel.LightningAffectExterior),
        nameof(AdditiveSettingsViewModel.LightningAnchorInterior),
        nameof(AdditiveSettingsViewModel.LightningAnchorExterior),
        nameof(AdditiveSettingsViewModel.LightningExteriorOverhangs),
        nameof(AdditiveSettingsViewModel.LightningButtressBarMm),
        nameof(AdditiveSettingsViewModel.LightningPreferInteriorMouths),
        nameof(AdditiveSettingsViewModel.LightningTargetSupportSelections),
        nameof(AdditiveSettingsViewModel.OverhangOrientation),
        nameof(AdditiveSettingsViewModel.MaxOverhangTiltDeg),
        nameof(AdditiveSettingsViewModel.MultiPlanarStamp),
        nameof(AdditiveSettingsViewModel.PaintStamp),
        nameof(AdditiveSettingsViewModel.BrimEnabled),
        nameof(AdditiveSettingsViewModel.BrimLoops),
        nameof(AdditiveSettingsViewModel.XBracingEnabled),
        nameof(AdditiveSettingsViewModel.XBracingDepthMm),
        nameof(AdditiveSettingsViewModel.XBracingDepthBottomMm),
        nameof(AdditiveSettingsViewModel.XBracingDepthEaseBottom),
        nameof(AdditiveSettingsViewModel.XBracingDepthEaseTop),
        nameof(AdditiveSettingsViewModel.XBracingSpanMm),
        nameof(AdditiveSettingsViewModel.XBracingAngleDeg),
        nameof(AdditiveSettingsViewModel.XBracingExtendEdges),
        nameof(AdditiveSettingsViewModel.XBracingPlaneTiltY),
        nameof(AdditiveSettingsViewModel.XBracingPlaneTiltX),
        nameof(AdditiveSettingsViewModel.XBracingProjectionType),
        nameof(AdditiveSettingsViewModel.XBracingCylinderDiameterMm),
        nameof(AdditiveSettingsViewModel.XBracingCylinderX),
        nameof(AdditiveSettingsViewModel.XBracingCylinderY),
        nameof(AdditiveSettingsViewModel.XBracingCylinderFlipDirection),
        nameof(AdditiveSettingsViewModel.WaveEffect),
        nameof(AdditiveSettingsViewModel.WaveAmplitude),
        nameof(AdditiveSettingsViewModel.WaveWavelength),
    ];

    private void WireRealtimeSlicing(ViewportViewModel vm)
    {
        if (vm.AdditiveSettings is { } add)
            add.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AdditiveSettingsViewModel.EffectorRange))
                    vm.UpdateEffectorRangeIndicators((float)add.EffectorRange);
                if (e.PropertyName is { } name && RealtimeSliceProps.Contains(name))
                    ScheduleRealtimeSlice(vm);
            };
        vm.OnModelGeometryChanged = () => ScheduleRealtimeSlice(vm);
        vm.OnPaintDeselectRequested = () => DeselectPaintSelection(vm);
        vm.OnPaintDeselectItemRequested = (layerIdx, moveStart, moveCount) =>
            RemovePaintSelectionItem(vm, layerIdx, moveStart, moveCount);
        vm.OnPaintModificationSelectRequested = id => ReselectPaintModification(vm, id);
        vm.OnPaintModificationDeleteRequested = id => DeletePaintModification(vm, id);
        vm.OnPaintModificationsClearRequested = () => ClearAllPaintModifications(vm);
        vm.OnDeleteSelectedStructuralSupportRequested = () => DeleteSelectedStructuralSupport(vm);
        vm.DescribeSupportPick = () => DescribeSupportPickState(vm);
        vm.OnPaintModificationPickBridgeRequested = id => BeginPickBridgeTarget(vm, id);
        vm.OnPaintModificationClearBridgeRequested = id => ClearBridgeTarget(vm, id);
        vm.OnPaintModificationToggleExpandRequested = id => TogglePaintModificationExpand(vm, id);
        vm.OnPaintModificationSupportTypeChanged = (id, type) =>
            SetPaintModificationSupportType(vm, id, type);
        vm.OnPaintModificationSupportSideChanged = (id, side) =>
            SetPaintModificationSupportSide(vm, id, side);
        vm.OnPaintApplyRequested = support => ApplyPaintSelection(vm, support);
        vm.OnPaintResliceRequested = () => _ = ForcePaintResliceAsync(vm);
        vm.CapturePaintModifications = CapturePaintModificationsState;
        vm.RestorePaintModifications = RestorePaintModificationsState;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewportViewModel.RealtimeSlicingPaused)
                && !vm.RealtimeSlicingPaused && _realtimeSlicePending)
            {
                // Never auto-fire a deferred re-slice over a protected baked toolpath.
                if (HasProtectedBakedToolpath(vm))
                {
                    _realtimeSlicePending = false;
                    return;
                }

                _realtimeSlicePending = false;
                ScheduleRealtimeSlice(vm);
            }
        };
    }

    /// <summary>
    /// Edit-toolbar Reslice: bake paint marks into Formbound even while
    /// <see cref="ViewportViewModel.RealtimeSlicingPaused"/> is held for edit mode.
    /// </summary>
    private async Task ForcePaintResliceAsync(ViewportViewModel vm)
    {
        // Cancel stale work and always run with the latest paint/settings.
        if (vm.IsSlicing)
        {
            LogPaintConsole("[edit] reslice: cancelling in-flight slice for latest paint edits");
            try { _sliceCts?.Cancel(); } catch { /* ignore */ }
            // Wait briefly for IsSlicing to clear (cancel path).
            for (int i = 0; i < 100 && vm.IsSlicing; i++)
                await Task.Delay(20);
        }
        _realtimeSlicePending = false;

        var item = vm.OwningModelItem(vm.ResolveActivePrintObjectItem())
                   ?? vm.EnumerateUserModelItems().FirstOrDefault();
        if (item is null)
        {
            LogPaintConsole("[edit] reslice: no model to slice");
            SetSliceStatus(vm, "Reslice failed: no model loaded.", isError: true);
            return;
        }

        // Bridge paint only grows when InfillPattern is Formbound. Paint Support
        // type / first Support mod drives that; never reslice with pattern=None.
        EnsureFormboundForPaintReslice(vm);

        int bridges = vm.AdditiveSettings?.PaintMarks.Count(m =>
            m.Kind == Core.Models.PaintMarkKind.Bridge) ?? 0;
        int feet = vm.AdditiveSettings?.PaintMarks.Count(m =>
            m.Kind == Core.Models.PaintMarkKind.Bridge
            && m.BridgeRole == Core.Models.PaintBridgeRole.ColumnFoot) ?? 0;
        int bars = vm.AdditiveSettings?.PaintMarks.Count(m =>
            m.Kind == Core.Models.PaintMarkKind.Bridge
            && m.BridgeRole == Core.Models.PaintBridgeRole.SupportBar) ?? 0;
        LogPaintConsole(
            $"[edit] reslice: marks bridge={bridges} (bar={bars} foot={feet}) " +
            $"pattern={vm.AdditiveSettings?.InfillPattern ?? "?"} " +
            $"mods={_paintModifications.Count}");

        if (bridges == 0)
            LogPaintConsole("[edit] reslice: WARNING — no Bridge paint marks; Support will not grow");

        // Show Formbound layer so the buttress is not invisible after bake.
        if (!vm.ShowLightningMoves)
        {
            vm.ShowLightningMoves = true;
            LogPaintConsole("[edit] reslice: turned on Formbound bridges visibility");
        }

        var toolpathChild = item.Children.FirstOrDefault(c => c.IsToolpath);
        LogPaintConsole(toolpathChild is not null
            ? "[edit] reslice: updating toolpath with paint edits…"
            : "[edit] reslice: slicing model with paint edits…");

        // Keep auto-slice paused for further edits; this call bypasses the pause.
        var keepPaused = vm.IsPaintEditOpen;
        try
        {
            if (toolpathChild is not null)
                await RunUpdateSliceAsync(vm, (item, toolpathChild));
            else
                await RunSliceAsync(vm);
        }
        finally
        {
            if (keepPaused)
                vm.RealtimeSlicingPaused = true;
        }
    }

    /// <summary>
    /// Paint Support force-enables the right pipeline from marks. Formbound paint
    /// soft-sets FILL PATTERN from mark styles; Tree paint does not steal the dropdown.
    /// </summary>
    private void EnsureFormboundForPaintReslice(ViewportViewModel vm)
    {
        if (vm.AdditiveSettings is null) return;

        bool hasBridge = vm.AdditiveSettings.PaintMarks.Any(m =>
            m.Kind == Core.Models.PaintMarkKind.Bridge);
        if (!hasBridge) return;

        if (!string.Equals(vm.PaintModificationMode, "Support", StringComparison.OrdinalIgnoreCase))
            vm.PaintModificationMode = "Support";

        // With Target Support Selections, Formbound is paint-driven at slice time
        // (ResolveInfillPatternForSlice) — do not overwrite the UI FILL PATTERN so
        // the saved dropdown value survives save/reopen and reslice.
        if (!vm.AdditiveSettings.LightningTargetSupportSelections
            && Core.Models.PaintSupportStyleUtil.ResolveFormboundPatternFromPaint(
                vm.AdditiveSettings.PaintMarks) is { } formPat)
        {
            string label = formPat == InfillPattern.LightningBridge
                ? Core.Models.PaintSupportStyleUtil.LabelBridge
                : Core.Models.PaintSupportStyleUtil.LabelButtress;
            var cur = vm.AdditiveSettings.InfillPattern ?? "";
            if (cur is "None" or "")
            {
                vm.AdditiveSettings.InfillPattern = label;
                LogPaintConsole($"[edit] reslice: seed FILL PATTERN → {label} (was None)");
            }
        }

        int treeN = vm.AdditiveSettings.PaintMarks.Count(m =>
            m.Kind == Core.Models.PaintMarkKind.Bridge
            && m.SupportStyle == Core.Models.PaintSupportStyle.Tree);
        int formN = vm.AdditiveSettings.PaintMarks.Count(m =>
            m.Kind == Core.Models.PaintMarkKind.Bridge
            && Core.Models.PaintSupportStyleUtil.IsFormbound(m.SupportStyle));
        if (treeN > 0 || formN > 0)
            LogPaintConsole($"[edit] reslice: support paint formbound={formN} tree={treeN}");
    }

    /// <summary>
    /// Start/Stop calibration (and similar) workspaces ship a pre-baked multi-recipe toolpath.
    /// Realtime re-slice would replace it with one global wipe/z-hop and destroy the matrix.
    /// </summary>
    private static bool HasProtectedBakedToolpath(ViewportViewModel vm)
    {
        foreach (var model in vm.EnumerateUserModelItems())
        {
            foreach (var child in model.Children)
            {
                if (child.IsToolpath
                    && child.Name.Contains("BAKED", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private void ScheduleRealtimeSlice(ViewportViewModel vm)
    {
        if (HasProtectedBakedToolpath(vm))
        {
            _realtimeSlicePending = false;
            return;
        }

        // Always mark pending so a mid-slice change forces a follow-up with latest state.
        _realtimeSlicePending = true;

        if (vm.RealtimeSlicingPaused) return;

        // Cancel in-flight slice so we do not wait for a stale result.
        if (vm.IsSlicing)
        {
            try { _sliceCts?.Cancel(); }
            catch { /* ignore */ }
        }

        if (_realtimeSliceTimer is null)
        {
            _realtimeSliceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _realtimeSliceTimer.Tick += async (_, _) =>
            {
                if (DataContext is not ViewportViewModel tickVm)
                {
                    _realtimeSliceTimer!.Stop();
                    return;
                }
                // Still running (cancel in progress) — keep polling until free.
                if (tickVm.IsSlicing) return;

                _realtimeSliceTimer!.Stop();
                // Drain: each completion may leave another pending if edits kept coming.
                while (_realtimeSlicePending && !tickVm.RealtimeSlicingPaused)
                {
                    _realtimeSlicePending = false;
                    await RunRealtimeSliceAsync(tickVm);
                    if (tickVm.IsSlicing) break; // should not happen after await
                }
                // More changes arrived after we finished the loop's last run.
                if (_realtimeSlicePending && !tickVm.RealtimeSlicingPaused)
                    ScheduleRealtimeSlice(tickVm);
            };
        }
        _realtimeSliceTimer.Stop();
        _realtimeSliceTimer.Start();
    }

    private async Task RunRealtimeSliceAsync(ViewportViewModel vm)
    {
        if (HasProtectedBakedToolpath(vm))
            return;

        var item = vm.OwningModelItem(vm.ResolveActivePrintObjectItem())
                   ?? vm.EnumerateUserModelItems().FirstOrDefault();
        if (item is null) return;

        var toolpathChild = item.Children.FirstOrDefault(c => c.IsToolpath);
        if (toolpathChild is not null)
            await RunUpdateSliceAsync(vm, (item, toolpathChild));
        else
            await RunSliceAsync(vm);
    }

    /// <summary>Begin a new slice cancellation token (cancels any previous).</summary>
    private CancellationToken BeginSliceCancellation()
    {
        try { _sliceCts?.Cancel(); } catch { /* ignore */ }
        _sliceCts?.Dispose();
        _sliceCts = new CancellationTokenSource();
        return _sliceCts.Token;
    }

    private async Task RunUpdateSliceAsync(ViewportViewModel vm)
        => await RunUpdateSliceAsync(vm, null);

    private async Task RunUpdateSliceAsync(
        ViewportViewModel vm,
        (OutlinerItemViewModel parent, OutlinerItemViewModel toolpath)? explicitSource)
    {
        if (vm.IsSlicing) return;
        var resolved = explicitSource ?? FindResliceSource(vm);
        if (resolved is not { } source) return;

        var cancel = BeginSliceCancellation();
        _sliceStatusClearGen++;
        vm.IsSlicing = true;
        vm.SliceStatusIsError = false;
        SetSliceStatus(vm, "Updating slice…");
        try
        {
            var (parentItem, toolpathItem) = source;
            var toolpathNode = toolpathItem.Node;
            var meshSnapshots = CollectMeshSnapshots(parentItem, requireVisible: false);
            if (meshSnapshots.Count == 0)
            {
                SetSliceStatus(vm, "Update failed: source mesh has no geometry.", isError: true);
                return;
            }

            cancel.ThrowIfCancellationRequested();
            // Build settings NOW so this run always uses latest UI values.
            var method   = vm.AdditiveSettings?.Method ?? SliceMethod.Planar;
            var settings = BuildSliceSettings(vm.AdditiveSettings);
            ApplyEffectorSettings(vm, settings);
            var (smoothedToolpath, rawToolpath, _) = await ComputeToolpathAsync(
                meshSnapshots, method, settings, msg => SetSliceStatus(vm, msg),
                pct => Dispatcher.UIThread.Post(() => vm.SliceProgressPercent = pct),
                cancel);

            cancel.ThrowIfCancellationRequested();
            LogFormboundEmitStats(smoothedToolpath, settings);

            if (smoothedToolpath.Layers.Count == 0)
            {
                SetSliceStatus(vm, "Update finished with 0 layers.", isError: true);
                return;
            }

            toolpathNode.Name = ToolpathNameFrom(parentItem.Name);

            var selectedPreset = vm.AdditiveSettings is { } asp
                && asp.SelectedPresetIndex >= 0
                && asp.SelectedPresetIndex < asp.MaterialPresets.Count
                ? asp.MaterialPresets[asp.SelectedPresetIndex] : null;

            _validationCts?.Cancel();
            _validationDone = false;

            // Toolpath moves are always baked in absolute world space (computed from the
            // mesh's CURRENT world-transformed vertices — see ComputeToolpathAsync), so a
            // fresh re-slice's raw data already reflects whatever rotation the mesh
            // currently has. The toolpath node's own rotation, when non-identity, only ever
            // got there by mirroring the mesh's rotation (the drag-link mesh-follow system) —
            // never from an independent user action — so re-applying it here would rotate
            // the already-rotated data a second time (verified live: rotate a piece, then
            // Create New Toolpath on it, and the toolpath rendered twisted at double the
            // angle while the mesh stayed correct). Only the translation is ever legitimate
            // to preserve (e.g. a toolpath nudged independently of its mesh), so strip the
            // rotation and keep an identity-rotation, translation-only transform.
            var preservedFull  = toolpathNode.LocalTransform;
            var preservedLocal = Matrix4.CreateTranslation(preservedFull.M41, preservedFull.M42, preservedFull.M43);
            if (!_toolpathOriginByNode.TryGetValue(toolpathNode, out var preservedOrigin))
            {
                preservedOrigin = new NVec3(
                    preservedLocal.M41, preservedLocal.M42, preservedLocal.M43);
            }

            ClearTcpKeyframeState(toolpathNode, vm);
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
            // Keep timeline scrub position across re-slice (clamp if path got shorter).
            int newMax = smoothedToolpath.Layers.Sum(l => l.Moves.Count);
            bool keepScrub = vm.IsScrubSessionActive
                             || ReferenceEquals(_activeScrubNode, toolpathNode);
            _activeScrubNode = toolpathNode;
            vm.IsScrubSessionActive = true;
            vm.ResetScrubIndex(newMax, smoothedToolpath, preservePosition: keepScrub);
            // Re-pose robot at the preserved index once scrub cache is rebuilt on GL thread.
            int restoreIdx = vm.ToolpathScrubIndex;
            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is ViewportViewModel v2
                    && ReferenceEquals(_activeScrubNode, toolpathNode))
                    ScrubIkForNode(toolpathNode, restoreIdx);
            }, DispatcherPriority.Background);
            GlCanvas.RequestNextFrameRendering();

            if (smoothedToolpath.Warnings.Count > 0)
                SetSliceStatus(vm, $"⚠ {smoothedToolpath.Warnings[0]}", isError: true);
            else
            {
                SetSliceStatus(vm, $"Update complete — {smoothedToolpath.Layers.Count} layers");
                ScheduleClearSliceStatus(vm);
            }
            vm.MarkWorkspaceDirty?.Invoke();
        }
        catch (OperationCanceledException)
        {
            SetSliceStatus(vm, "Update cancelled — applying latest changes…");
            System.Console.WriteLine("[slice] update cancelled (superseded by newer settings)");
        }
        catch (Exception ex)
        {
            SetSliceStatus(vm, $"Update failed: {ex.Message}", isError: true);
            System.Console.Error.WriteLine($"[slice] update: {ex}");
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
        if (vm.ResolveActivePrintObjectItem() is not { } sourceItem) return;

        var meshSnapshots = CollectMeshSnapshots(sourceItem, requireVisible: true);
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
        _         => new(0.95f, 0.95f, 0.95f),  // Other / no preset → white
    };

    /// <summary>
    /// Runs the analytical thermomechanical screen on the visible toolpath, fills the
    /// Adaptive Speed low/high values from the safe layer-time window, and stamps the
    /// per-layer interlayer temperatures for the Thermal view.
    /// </summary>
    private void RunThermalSimulation(ViewportViewModel vm, AdditiveSettingsViewModel s)
    {
        var pair = _toolpathByNode.FirstOrDefault(kv => kv.Key.Visible);
        if (pair.Value is null)
        {
            s.ThermalSummary = "Slice a model first — the simulation runs on the toolpath.";
            return;
        }

        var settings = BuildSliceSettings(s);
        var result = ThermalSimulator.Simulate(pair.Value, settings);

        var sb = new System.Text.StringBuilder();
        if (result.RecommendedMaxMmS > 0f)
        {
            // Setting these re-processes the toolpath, which re-stamps layer temps.
            s.LayerSpeedBasisDisplay = "Cut length";
            s.LayerSpeedMinMmS = Math.Round(result.RecommendedMinMmS, 1);
            s.LayerSpeedMaxMmS = Math.Round(result.RecommendedMaxMmS, 1);
            s.LayerSpeedAdaptEnabled = true;

            sb.Append($"Deposit {result.DepositTempC:0}°C, cooling τ {result.TimeConstantS:0} s. ");
            sb.Append($"Safe layer time {result.MinLayerTimeS:0}–{result.MaxLayerTimeS:0} s ");
            sb.Append($"(sag ≤{result.SagTempC:0}°C, bond ≥{result.BondTempC:0}°C); ");
            sb.Append($"targeting {result.TargetLayerTimeS:0} s → interface ≈{result.PredictedInterfaceTempC:0}°C. ");
            sb.Append($"Speeds set to {result.RecommendedMinMmS:0.#}–{result.RecommendedMaxMmS:0.#} mm/s.");
        }
        foreach (var w in result.Warnings)
            sb.Append($"\n⚠ {w}");
        sb.Append("\nAnalytical lumped-capacitance model — verify on the part.");
        s.ThermalSummary = sb.ToString();
    }

    private static (string time, string weight, string cost, ToolpathStatsResult layerStats, double massKg)
        ComputeToolpathStats(Toolpath toolpath, AdditiveSettingsViewModel s, Erp.ErpPricingConfig? pricing)
    {
        var rates = new ToolpathMotionRates(s.PrintSpeed, s.TravelSpeed, s.WipeSpeed);
        var stats = ToolpathStatistics.Compute(toolpath, rates, s.BeadWidth, s.LayerHeight);

        var preset = s.SelectedPresetIndex >= 0 && s.SelectedPresetIndex < s.MaterialPresets.Count
            ? s.MaterialPresets[s.SelectedPresetIndex] : null;

        double densityGCm3 = preset?.MaterialDensity ?? 1.05;
        double costPerLb   = preset?.CostPerLb       ?? 0.0;

        double massLbs = stats.VolumeMm3 / 1000.0 * densityGCm3 / 453.592;
        double massKg  = massLbs * 0.453592;

        // Live rough estimate from the cached ERP pricing config when connected —
        // the authoritative number is always a POST /quote or a slice's costing block.
        string costStr;
        if (pricing is { RatePerHour: { } ratePerHour })
        {
            double machine = stats.TotalTimeSeconds / 3600.0 * ratePerHour;
            var erpMat = pricing.MatchMaterial(preset?.Name) ?? pricing.MatchMaterial(preset?.MaterialType);
            double? perKg = erpMat?.CostPerKg
                ?? (erpMat?.CostPerLb is { } perLb ? perLb / 0.453592 : null);
            double material = perKg is { } pk ? massKg * pk : massLbs * costPerLb;
            double markup = 1.0 + (pricing.OverheadRate ?? 0.0) + (pricing.ProfitRate ?? 0.0);
            costStr = $"${(machine + material) * markup:F2} (ERP est.)";
        }
        else
            costStr = preset is not null ? $"${cost(massLbs, costPerLb):F2}" : "--";

        return (
            ToolpathStatistics.FormatDuration(stats.TotalTimeSeconds),
            $"{massLbs:F3} lbs",
            costStr,
            stats,
            massKg);

        static double cost(double lbs, double perLb) => lbs * perLb;
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
        if (LayFlatMinZ(node) >= float.MaxValue) return;
        var old = node.LocalTransform;
        DropNodeToBed(node, _renderer.BedZ);
        // A one-shot transform edit, same category as a typed coordinate change — mesh and
        // toolpath are scene-graph siblings, not real parent/child, so nothing carries the
        // toolpath along unless explicitly told to (same mechanism OnSelectionTranslated
        // already uses for a typed Move edit).
        if (DataContext is ViewportViewModel vm) MirrorTypedTransformDelta(vm, node, old);
        GlCanvas.RequestNextFrameRendering();
        RevalidateSelectedToolpath();
    }

    private void RecenterSelected()
    {
        if (_renderer.SelectedNode is not { } selected) return;
        if (DataContext is not ViewportViewModel vm) return;
        if (!vm.HasMeshSelected) return;

        var node = vm.FindUserMeshOutlinerItem(selected)?.Node ?? selected;
        if (node != selected)
            _renderer.Select(node);

        vm.PendingRecenterJobs.Enqueue(new ViewportViewModel.PendingRecenterJob(node));
        vm.NotifyRenderNeeded();
    }

    private void ProcessRecenterJob(ViewportViewModel vm, ViewportViewModel.PendingRecenterJob job)
    {
        var node = job.Node;
        var transformsBefore = ImportHelper.SnapshotSubtreeTransforms(node);
        var meshesBefore     = ImportHelper.SnapshotSubtreeMeshes(node);

        if (!ImportHelper.RecenterPivotToBottomCenter(node))
        {
            System.Console.WriteLine("[recenter] aborted: pivot edit failed");
            return;
        }

        if (!TryRefreshSubtreeGpuMeshes(node))
        {
            System.Console.WriteLine("[recenter] aborted: GPU refresh failed — rolling back");
            ImportHelper.RestoreSubtreeSnapshot(node, transformsBefore, meshesBefore);
            TryRefreshSubtreeGpuMeshes(node);
            _renderer.InvalidateShaderAppearance();
            return;
        }

        _renderer.InvalidateShaderAppearance();

        var transformsAfter = ImportHelper.SnapshotSubtreeTransforms(node);
        var meshesAfter     = ImportHelper.SnapshotSubtreeMeshes(node);

        Dispatcher.UIThread.Post(() =>
        {
            vm.UndoRedo?.Push(new NodeRecenterAction(
                node, transformsBefore, transformsAfter,
                meshesBefore, meshesAfter,
                () =>
                {
                    vm.PendingModelRefresh.Enqueue(node);
                    vm.NotifyRenderNeeded();
                    OnRecenterApplied(vm, node);
                }));
            OnRecenterApplied(vm, node);
        });

        if (_renderer.SelectedNode is not null &&
            node.SelfAndDescendants().Any(n => n == _renderer.SelectedNode))
            _renderer.Select(node);

        GlCanvas.RequestNextFrameRendering();
    }

    private static bool TryRefreshSubtreeGpuMeshes(SceneNode root)
    {
        var uploads = new List<(SceneNode Node, MeshData Data)>();
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } data) continue;
            if (data.Positions.Length == 0)
            {
                System.Console.WriteLine($"[recenter] GPU refresh: empty mesh on '{n.Name}'");
                return false;
            }
            uploads.Add((n, data));
        }

        if (uploads.Count == 0)
        {
            System.Console.WriteLine("[recenter] GPU refresh: no PendingMesh nodes");
            return false;
        }

        var acquired = new List<(SceneNode Node, MeshRenderer Gpu)>(uploads.Count);
        try
        {
            foreach (var (node, data) in uploads)
                acquired.Add((node, GpuMeshCache.Acquire(data)));

            foreach (var (node, gpu) in acquired)
            {
                var previous = node.Mesh;
                node.Mesh        = gpu;
                node.PendingMesh = null;
                GpuMeshCache.Release(previous);
            }
            return true;
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[recenter] GPU refresh exception: {ex.Message}");
            foreach (var (node, gpu) in acquired)
                GpuMeshCache.Release(gpu);
            return false;
        }
    }

    private static void RefreshSubtreeGpuMeshes(SceneNode root)
        => TryRefreshSubtreeGpuMeshes(root);

    private void OnRecenterApplied(ViewportViewModel vm, SceneNode node)
    {
        SyncSelectionTransformDisplay(vm);
        UpdateFocusOverlay();
        GlCanvas.RequestNextFrameRendering();
        RevalidateSelectedToolpath();
        RememberCommittedTransform(vm, node);
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

    /// <summary>Root node being cut (kept while the interactive cut session is open).</summary>
    private SceneNode? _cutToolRoot;

    /// <summary>Enter interactive Cut Tool: ghosted plane + RGB gizmo, floating panel.</summary>
    private void BeginCutToolInteractive()
    {
        if (_renderer.SelectedNode is not { } root) return;
        if (DataContext is not ViewportViewModel vm) return;
        if (!HasCleanableMeshes(root)) return;
        if (vm.IsCutToolActive) return;

        // World AABB of the whole selection for plane size + default center.
        var min = new TkVector3(float.MaxValue);
        var max = new TkVector3(float.MinValue);
        double modelMinZ = double.MaxValue, modelMaxZ = double.MinValue;
        bool has = false;
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.Mesh?.PickingData is not { } mesh) continue;
            var world = n.WorldTransform;
            var (bMin, bMax) = mesh.LocalBounds;
            modelMinZ = Math.Min(modelMinZ, bMin.Z);
            modelMaxZ = Math.Max(modelMaxZ, bMax.Z);
            for (int ci = 0; ci < 8; ci++)
            {
                var pLocal = new TkVector3(
                    (ci & 1) == 0 ? bMin.X : bMax.X,
                    (ci & 2) == 0 ? bMin.Y : bMax.Y,
                    (ci & 4) == 0 ? bMin.Z : bMax.Z);
                var w = TkVector3.TransformPosition(pLocal, world);
                min = TkVector3.ComponentMin(min, w);
                max = TkVector3.ComponentMax(max, w);
            }
            has = true;
        }
        if (!has) return;

        var center = (min + max) * 0.5f;
        float size = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z)) * 1.25f;

        var session = new CutToolDialogViewModel
        {
            ModelName = root.Name,
            ModelMinZ = modelMinZ,
            ModelMaxZ = modelMaxZ,
            PlaneSize = size,
        };
        session.SetPose(new System.Numerics.Vector3(center.X, center.Y, center.Z),
                        System.Numerics.Vector3.UnitZ);
        session.OnChanged = () => GlCanvas.RequestNextFrameRendering();

        _cutToolRoot = root;
        vm.BeginCutToolSession(session);
        // Ensure selection stays on the model so the RGB gizmo draws.
        _renderer.Select(root);
        vm.SetOutlinerSelection(root);
        if (vm.ActiveGizmoModeInternal is GizmoMode.None or GizmoMode.Scale)
            SetGizmoMode(GizmoMode.Translate);
        GlCanvas.RequestNextFrameRendering();
    }

    private void CancelCutToolInteractive()
    {
        if (DataContext is not ViewportViewModel vm) return;
        _cutToolRoot = null;
        vm.EndCutToolSession();
        _renderer.SetPlanePreview(null, null);
        GlCanvas.RequestNextFrameRendering();
    }

    private void PerformCutToolInteractive()
    {
        if (DataContext is not ViewportViewModel vm) return;
        if (vm.CutToolSession is not { } result) return;
        if (_cutToolRoot is not { } root) return;

        var planeN = new TkVector3((float)result.NormalX, (float)result.NormalY, (float)result.NormalZ);
        if (planeN.LengthSquared < 1e-10f) planeN = TkVector3.UnitZ;
        planeN = TkVector3.Normalize(planeN);
        var planePointWorld = new TkVector3((float)result.CenterX, (float)result.CenterY, (float)result.CenterZ);

        var outlinerItem = vm.FindOutlinerItem(root);
        var newNodes = new List<SceneNode>();
        int totalConnectors = 0;

        foreach (var n in root.SelfAndDescendants())
        {
            if (n.Mesh?.PickingData is not { } mesh) continue;
            var w = n.WorldTransform;
            var inv = w.Inverted();

            var localPt = TkVector3.TransformPosition(planePointWorld, inv);
            var localN = TkVector3.Normalize(TkVector3.TransformNormal(planeN, inv));

            var split = PlanarMeshSplitter.Split(mesh, localPt, localN);
            MeshData meshA = split.Positive;
            MeshData meshB = split.Negative;

            if (result.AddConnectors && split.CutLoops.Count > 0)
            {
                var conn = CutConnectorBuilder.Apply(
                    meshA, meshB, split.CutLoops, localPt, localN,
                    new CutConnectorBuilder.Options
                    {
                        SpacingMm = (float)result.ConnectorSpacing,
                        TabWidthMm = (float)result.TabWidth,
                        TabDepthMm = (float)result.TabDepth,
                        TabHeightMm = (float)result.TabHeight,
                        BoltDiameterMm = (float)result.BoltDiameter,
                        BoltLugDiameterMm = (float)result.BoltLugDiameter,
                        MinCornerRadiusMm = (float)result.MinCornerRadius,
                    });
                meshA = conn.PositiveWithConnectors;
                meshB = conn.NegativeWithConnectors;
                totalConnectors += conn.ConnectorCount;
            }

            var ta = w;
            var tb = w;
            if (!result.PlaceOnCut)
            {
                ta = w * TkMatrix4.CreateTranslation(planeN * 30f);
                tb = w * TkMatrix4.CreateTranslation(planeN * -30f);
            }

            newNodes.Add(new SceneNode
            {
                Name = meshA.Name,
                PendingMesh = meshA,
                LocalTransform = ta,
                Selectable = root.Selectable,
                CullFaces = false,
                Visible = root.Visible,
                LayerPreview = root.LayerPreview,
                SourceFilePath = root.SourceFilePath,
            });
            newNodes.Add(new SceneNode
            {
                Name = meshB.Name,
                PendingMesh = meshB,
                LocalTransform = tb,
                Selectable = root.Selectable,
                CullFaces = false,
                Visible = root.Visible,
                LayerPreview = root.LayerPreview,
                SourceFilePath = root.SourceFilePath,
            });
        }

        _cutToolRoot = null;
        vm.EndCutToolSession();
        _renderer.SetPlanePreview(null, null);

        if (newNodes.Count == 0 || outlinerItem is null) return;

        vm.OutlinerItems.Remove(outlinerItem);
        foreach (var child in outlinerItem.Children)
            vm.PendingRemoveNodes.Enqueue(child.Node);
        vm.PendingRemoveNodes.Enqueue(root);

        for (int i = 0; i < newNodes.Count; i++)
            vm.AttachUserNode(newNodes[i], i == 0 ? outlinerItem : null);

        _renderer.Select(newNodes[0]);
        UpdateFocusOverlay();

        var msg = $"[cut] Split into {newNodes.Count} part(s)" +
                  (totalConnectors > 0 ? $", {totalConnectors} connector site(s)" : "") + ".";
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel mvm)
            mvm.Console.Log(msg);

        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>One piece in the working set while folding a Modifiers stack over the master —
    /// its own mesh/toolpath content, plus the world pose it should keep (splitting never moves
    /// anything, only divides geometry).</summary>
    private readonly record struct ApplyPiece(MeshData Mesh, TkMatrix4 World, string Name);

    /// <summary>
    /// Runs a mesh's Modifiers stack: folds an ordered list of Cut modifiers over a working set
    /// that starts as just the master (mesh + its current toolpath, if any). Each Cut replaces
    /// every piece it actually crosses with two new pieces; a piece it doesn't cross passes
    /// through unchanged — this is what makes a later Cut naturally re-cut an earlier one's
    /// output (e.g. Horizontal then Vertical yields 4 pieces, not 2), with no special-casing.
    /// The plane itself always lives in world space (the modifier's real, independent gizmo
    /// node) and gets converted into each current piece's own local space — the same
    /// world-to-local step the existing (destructive) Cut Tool already does — before handing off
    /// to the already-built, already-tested splitters (PlanarMeshSplitter for the mesh,
    /// Horizontal/VerticalCutSplitter for the toolpath). Non-destructive: the master and its
    /// stack are never touched; results land in a fresh sibling group.
    /// </summary>
    /// <summary>
    /// Returns <paramref name="root"/>'s own mesh if it carries one directly (STL/OBJ/3MF/STEP
    /// imports, and the common case generally), or — for a GLB import, whose primitives live on
    /// child <see cref="SceneNode"/>s rather than the wrapper root the outliner registers — merges
    /// every descendant mesh into one combined <see cref="MeshData"/> expressed in root's own local
    /// space, so Cut Apply works the same regardless of which loader produced the hierarchy.
    /// Skips <see cref="SceneNode.IsAuthoringOverlay"/> nodes — the Modifiers group and each Cut's
    /// own preview-plane/corner-marker geometry are real, pickable, GPU-uploaded meshes parented
    /// under this same root, and must never be swept into the geometry actually being cut.
    /// </summary>
    private static MeshData? GetSubtreeMeshInLocalSpace(SceneNode root)
    {
        if (root.Mesh?.PickingData is { } direct) return direct;

        var positions = new List<TkVector3>();
        var normals   = new List<TkVector3>();
        var indices   = new List<uint>();
        Vector4? color = null;
        float metallic = 0f, roughness = 1f;
        var rootWorldInv = root.WorldTransform.Inverted();

        foreach (var n in root.SelfAndDescendants())
        {
            if (ReferenceEquals(n, root) || n.IsAuthoringOverlay) continue;
            if (n.Mesh?.PickingData is not { } mesh) continue;

            color    ??= mesh.BaseColor;
            metallic   = mesh.Metallic;
            roughness  = mesh.Roughness;

            var toRoot = n.WorldTransform * rootWorldInv;
            uint baseIndex = (uint)positions.Count;
            foreach (var p in mesh.Positions)
                positions.Add(TkVector3.TransformPosition(p, toRoot));
            foreach (var nrm in mesh.Normals)
                normals.Add(TransformNormalDirection(nrm, toRoot));

            if (mesh.Indices is { Length: > 0 } idx)
                foreach (var i in idx)
                    indices.Add(baseIndex + i);
            else
                for (uint i = 0; i < mesh.Positions.Length; i++)
                    indices.Add(baseIndex + i);
        }

        if (positions.Count == 0) return null;
        return new MeshData(positions.ToArray(), normals.ToArray(), indices.ToArray(),
                             root.Name, color, metallic, roughness);
    }

    private static TkVector3 TransformNormalDirection(TkVector3 n, TkMatrix4 m)
    {
        var d = new TkVector3(
            n.X * m.M11 + n.Y * m.M21 + n.Z * m.M31,
            n.X * m.M12 + n.Y * m.M22 + n.Z * m.M32,
            n.X * m.M13 + n.Y * m.M23 + n.Z * m.M33);
        return d.LengthSquared > 1e-12f ? TkVector3.Normalize(d) : n;
    }

    private async Task ApplyModifierStackAsync(ViewportViewModel vm, OutlinerItemViewModel ownerItem)
    {
        if (vm.IsSlicing) return;

        var groupItem = ownerItem.Children.FirstOrDefault(c => c.IsModifiersGroup);
        if (groupItem is null) return;

        var cuts = new List<CutModifier>();
        foreach (var childNode in groupItem.Node.Children)
            if (vm.FindModifierForNode(childNode) is { } cut && cut.Enabled && cut.Cut)
                cuts.Add(cut);
        if (cuts.Count == 0)
        {
            LogToConsole("[apply] no enabled Cut modifiers in the stack — nothing to do.");
            return;
        }

        if (GetSubtreeMeshInLocalSpace(ownerItem.Node) is not { } masterMesh)
        {
            LogToConsole("[apply] master mesh has no geometry yet.");
            return;
        }

        var tpItem = ownerItem.Children.FirstOrDefault(c => c.IsToolpath);

        var pieces = new List<ApplyPiece> { new(masterMesh, ownerItem.Node.WorldTransform, ownerItem.Node.Name) };
        bool anyChange = false;

        foreach (var cut in cuts)
        {
            if (vm.GetModifierGizmoNode(cut) is not { } gizmoNode) continue;
            var gw = gizmoNode.WorldTransform;
            var worldPoint = gw.Row3.Xyz;
            // Same rows BuildModifierPlaneMesh already uses for this orientation's preview quad:
            // Horizontal's plane spans local X/Y (Row0/Row1), normal is Z (Row2); Vertical's
            // spans local Y/Z (Row1/Row2 — Row2 is always world Z, since RotationDegrees only
            // ever rotates about Z), normal is X (Row0, see CutModifierNodeSync).
            var worldNormal   = TkVector3.Normalize(cut.Orientation == CutOrientation.Horizontal ? gw.Row2.Xyz : gw.Row0.Xyz);
            var worldTangentU = TkVector3.Normalize(cut.Orientation == CutOrientation.Horizontal ? gw.Row0.Xyz : gw.Row1.Xyz);
            var worldTangentV = TkVector3.Normalize(cut.Orientation == CutOrientation.Horizontal ? gw.Row1.Xyz : gw.Row2.Xyz);

            var next = new List<ApplyPiece>();

            // A single flat plane can cross a curled/spiral wall at more than one point along
            // its length — PlanarMeshSplitter only buckets triangles by which side of the plane
            // they're on, with no idea whether that bucket is one connected piece or several, so
            // without this, two physically separate chunks on the same side silently end up
            // glued into one mesh/outliner entry. Split each side into its real connected islands
            // and give each its own piece.
            void AddSplitPieces(MeshData sideMesh, TkMatrix4 world, string baseName)
                => AddResolvedPieces(MeshIslands.Split(sideMesh), world, baseName);

            void AddResolvedPieces(List<MeshData> islands, TkMatrix4 world, string baseName)
            {
                if (islands.Count == 1) { next.Add(new ApplyPiece(islands[0], world, baseName)); return; }
                for (int i = 0; i < islands.Count; i++)
                    next.Add(new ApplyPiece(islands[i], world, $"{baseName} {i + 1}"));
            }

            foreach (var piece in pieces)
            {
                var inv = piece.World.Inverted();
                var localPt = TkVector3.TransformPosition(worldPoint, inv);
                var localN  = TkVector3.Normalize(TkVector3.TransformNormal(worldNormal, inv));

                if (cut.Infinite)
                {
                    var meshSplit = PlanarMeshSplitter.Split(piece.Mesh, localPt, localN);
                    bool crosses = meshSplit.Positive.Positions.Length > 0 && meshSplit.Negative.Positions.Length > 0;
                    if (!crosses) { next.Add(piece); continue; }

                    anyChange = true;
                    AddSplitPieces(meshSplit.Positive, piece.World, $"{piece.Name} +");
                    AddSplitPieces(meshSplit.Negative, piece.World, $"{piece.Name} -");
                }
                else
                {
                    var localTanU = TkVector3.Normalize(TkVector3.TransformNormal(worldTangentU, inv));
                    var localTanV = TkVector3.Normalize(TkVector3.TransformNormal(worldTangentV, inv));
                    var bounded = BoundedCutSplitter.Split(
                        piece.Mesh, localPt, localN, localTanU, localTanV, cut.SizeX * 0.5f, cut.SizeY * 0.5f);

                    if (bounded is null) { next.Add(piece); continue; }

                    anyChange = true;
                    AddResolvedPieces(bounded, piece.World, piece.Name);
                }
            }
            pieces = next;
        }

        if (!anyChange)
        {
            LogToConsole("[apply] no modifier's plane actually crossed the model — nothing produced.");
            return;
        }

        vm.IsSlicing = true;
        try
        {
            var outputGroup = vm.CreateAppliedPiecesGroup(ownerItem);
            SceneNode? firstNode = null;
            var created = new List<(ApplyPiece piece, SceneNode node, OutlinerItemViewModel item)>();

            for (int i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                var node = new SceneNode
                {
                    Name           = $"{outputGroup.Name} {i + 1:D2}",
                    PendingMesh    = piece.Mesh,
                    LocalTransform = piece.World,
                    Selectable     = true,
                    CullFaces      = false,
                    Visible        = true,
                };
                firstNode ??= node;
                vm.PendingNodes.Enqueue(node);

                var pieceItem = vm.CreateOutlinerItem(node, it =>
                {
                    // The toolpath is only an OUTLINER child of the piece, not a real scene child
                    // (same convention as any other model — see the toolpath-positioning research
                    // this session) — so deleting the piece must explicitly also remove its
                    // toolpath's own node, or it's orphaned: gone from the outliner, still rendered,
                    // still pickable. Mirrors the existing (untouched) Cut Tool's delete flow.
                    foreach (var child in it.Children)
                        vm.PendingRemoveNodes.Enqueue(child.Node);
                    outputGroup.RemoveChild(it);
                    vm.PendingRemoveNodes.Enqueue(it.Node);
                    vm.NotifyRenderNeeded();
                }, displayName: node.Name, canDelete: true, modelFileOps: true);
                outputGroup.AddChild(pieceItem);

                created.Add((piece, node, pieceItem));
            }

            // Each piece gets a REAL slice through the same pipeline any imported mesh uses
            // (see RunSliceAsync) — not a cheap split of the pre-cut toolpath's old moves.
            // Splitting old moves carried over motion computed for the whole, uncut wall:
            // misaligned starts, no brim/seam re-evaluation for the new cut face, and dead-end
            // paths right at the cut. Slicing fresh from each piece's own mesh (current
            // AdditiveSettings — same seam/brim/infill pipeline as any other model) is what
            // actually makes a cut piece printable.
            foreach (var (piece, node, pieceItem) in created)
            {
                var cancel = BeginSliceCancellation();
                var meshSnapshots = new List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)>
                {
                    (piece.Mesh.Positions, piece.Mesh.Indices, piece.World),
                };

                var method   = vm.AdditiveSettings?.Method ?? SliceMethod.Planar;
                var settings = BuildSliceSettings(vm.AdditiveSettings);
                ApplyEffectorSettings(vm, settings);
                SetSliceStatus(vm, $"{SliceMethodLabel(method)}: slicing \"{node.Name}\"…");

                Toolpath smoothedToolpath, rawToolpath;
                try
                {
                    (smoothedToolpath, rawToolpath, _) = await ComputeToolpathAsync(
                        meshSnapshots, method, settings, msg => SetSliceStatus(vm, msg),
                        pct => Dispatcher.UIThread.Post(() => vm.SliceProgressPercent = pct),
                        cancel);
                }
                catch (OperationCanceledException)
                {
                    LogToConsole($"[apply] slicing \"{node.Name}\" was cancelled.");
                    continue;
                }

                LogFormboundEmitStats(smoothedToolpath, settings);

                if (smoothedToolpath.Layers.Count == 0)
                {
                    LogToConsole($"[apply] \"{node.Name}\" sliced to 0 layers — check the piece's geometry.");
                    continue;
                }

                var toolpathName = ToolpathNameFrom(pieceItem.Name);
                var toolpathNode = new SceneNode { Name = toolpathName, Selectable = true };
                vm.RegisterToolpathInOutliner(toolpathNode, pieceItem);
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
            }

            // The master's own pre-cut toolpath (if any) no longer represents anything
            // printable — each piece now has its own real, independent toolpath — so remove
            // it outright rather than just hiding it (a hidden-but-still-registered toolpath
            // is what caused the stale toolpath to resurface on a view-mode switch).
            if (tpItem is not null)
            {
                foreach (var child in tpItem.Children)
                    vm.PendingRemoveNodes.Enqueue(child.Node);
                ownerItem.RemoveChild(tpItem);
                vm.PendingRemoveNodes.Enqueue(tpItem.Node);
            }

            // Hide the source of truth now that its output exists — looking at both the master
            // and its fresh pieces at once is just clutter, and the master/stack are still there
            // (non-destructive), just tucked away until you want to re-Apply.
            ownerItem.Visible = false;
            groupItem.Visible = false;

            if (firstNode is not null) _renderer.Select(firstNode);
            UpdateFocusOverlay();
            LogToConsole($"[apply] \"{outputGroup.Name}\": {pieces.Count} piece(s) from {cuts.Count} modifier(s), sliced and printable.");
            vm.MarkWorkspaceDirty?.Invoke();
            GlCanvas.RequestNextFrameRendering();
        }
        finally
        {
            vm.IsSlicing = false;
        }
    }

    private void LogToConsole(string message)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel mvm)
            mvm.Console.Log(message);
    }

    /// <summary>Ghosted cut plane + gizmo pivot while the interactive Cut Tool is open.</summary>
    private void UpdateCutToolOverlay(ViewportViewModel vm)
    {
        if (!vm.IsCutToolActive || vm.CutToolSession is not { } s)
        {
            // Only clear if we own the preview (angled mode will set its own).
            return;
        }

        var c = new TkVector3((float)s.CenterX, (float)s.CenterY, (float)s.CenterZ);
        var n = new TkVector3((float)s.NormalX, (float)s.NormalY, (float)s.NormalZ);
        if (n.LengthSquared < 1e-10f) n = TkVector3.UnitZ;
        else n = TkVector3.Normalize(n);

        _renderer.SetPlanePreview(c, n, s.PlaneSize);
        _renderer.GizmoPivotWorld = c;
        // Keep gizmo on for cut session.
        if (vm.ActiveGizmoModeInternal == GizmoMode.None)
            SetGizmoMode(GizmoMode.Translate);
        else
            _renderer.GizmoMode = vm.ActiveGizmoModeInternal;
        _renderer.GizmoEnabled = true;
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
        // center and rot are both WORLD quantities, so they must be conjugated into the
        // parent frame — see ApplyWorldTransformToNode.
        var center = LayFlatWorldCenter(node);
        var M = TkMatrix4.CreateTranslation(-center) * rot * TkMatrix4.CreateTranslation(center);
        ApplyWorldTransformToNode(node, M);

        // Drop the object so its lowest point sits exactly on the bed surface.
        DropNodeToBed(node, bedZ);
    }

    /// <summary>
    /// Applies <paramref name="world"/> — a transform expressed in WORLD space — to
    /// <paramref name="node"/>, converting it into the node's parent frame.
    /// <para>
    /// Row-vector convention: p_world = p_local · L · P. Wanting p_world' = p_world · W gives
    /// L' = L · P · W · P⁻¹. Post-multiplying L by W directly (the old code) silently assumes
    /// P is identity. It is a pure translation on LFAM 1/2 (so only the rotation pivot drifted)
    /// but on LFAM 3 user models hang off the rotary pivot, whose frame carries
    /// baseAbc C = −90° — mapping local +Z onto world ±Y. That turned Drop to Plate into a
    /// sideways slide of a foot or two instead of a drop (reported 2026-07-29).
    /// </para>
    /// </summary>
    internal static void ApplyWorldTransformToNode(SceneNode node, TkMatrix4 world)
    {
        var parent = node.Parent?.WorldTransform ?? TkMatrix4.Identity;
        node.LocalTransform = node.LocalTransform * parent * world * parent.Inverted();
    }

    /// <summary>Drops <paramref name="node"/> straight down (world −Z) until its lowest
    /// point rests on <paramref name="bedZ"/>. No-op when the node has no geometry.</summary>
    internal static void DropNodeToBed(SceneNode node, float bedZ)
    {
        float minZ = LayFlatMinZ(node);
        if (minZ >= float.MaxValue) return;
        ApplyWorldTransformToNode(node, TkMatrix4.CreateTranslation(0f, 0f, bedZ - minZ));
    }

    private static TkVector3 LayFlatWorldCenter(SceneNode node)
    {
        var mesh = node.Mesh?.PickingData ?? node.PendingMesh;
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
            var mesh = n.Mesh?.PickingData ?? n.PendingMesh;
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

    // -- LFAM tool TCP selection (robot/bed blocked; IK follows drag) ----------

    static bool IsLfamProductionCell(ViewportViewModel vm)
    {
        var name = vm.ActiveCell?.Name;
        if (name is null) return false;
        return name.Contains("LFAM 1", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LFAM 2", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LFAM 3", StringComparison.OrdinalIgnoreCase);
    }

    void RegisterLfamInfrastructure(params SceneNode?[] nodes)
    {
        foreach (var node in nodes)
        {
            if (node is not null)
                _lfamInfrastructureNodes.Add(node);
        }
    }

    bool IsLfamInfrastructureNode(SceneNode? node)
    {
        if (node is null) return false;
        for (var cur = node; cur is not null; cur = cur.Parent)
        {
            if (_lfamInfrastructureNodes.Contains(cur))
                return true;
        }
        return false;
    }

    static bool IsSameOrDescendant(SceneNode root, SceneNode node)
    {
        if (node == root) return true;
        foreach (var d in root.SelfAndDescendants())
        {
            if (d == node) return true;
        }
        return false;
    }

    bool TryResolveMultiToolPick(SceneNode picked, out string toolName, out SceneNode toolRoot, out bool onDock)
    {
        toolName = "";
        toolRoot = picked;
        onDock   = false;
        if (_multiTools is null) return false;

        foreach (var (name, pair) in _multiTools.Tools)
        {
            if (IsSameOrDescendant(pair.FlangeHolder, picked))
            {
                toolName = name;
                toolRoot = pair.FlangeHolder;
                onDock   = false;
                return true;
            }

            if (pair.DockHolder is { } dock && IsSameOrDescendant(dock, picked))
            {
                toolName = name;
                toolRoot = dock;
                onDock   = true;
                return true;
            }
        }

        return false;
    }

    void RefreshMultiToolSelectability()
    {
        if (_multiTools is null) return;

        foreach (var pair in _multiTools.Tools.Values)
        {
            bool mounted = pair.FlangeHolder.Visible;
            pair.FlangeHolder.Selectable = mounted;
            pair.FlangeHolder.PickTier   = PickTier.Content;

            if (pair.DockHolder is { } dock)
            {
                bool docked = dock.Visible;
                dock.Selectable = docked;
                dock.PickTier   = PickTier.Content;
            }
        }
    }

    void SyncScanSelectionToRenderer(ViewportViewModel vm)
    {
        var scans = vm.SelectedScanItems.Select(i => i.Node).ToList();
        var primary = scans.Count > 0 ? scans[^1] : null;
        _renderer.SetScanSelection(scans, primary);
        _lastOutlinerSyncedNode = primary;
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>Debug: run the full viewport-click selection path at fractional
    /// viewport coords (0-1) and trace every gate. Backs the `pick` console command.</summary>
    private string DebugPickAtViewport(ViewportViewModel vm, double fx, double fy)
    {
        float vpW = (float)GlCanvas.Bounds.Width;
        float vpH = (float)GlCanvas.Bounds.Height;
        if (vpW < 2 || vpH < 2) return "[pick] viewport not ready";
        var ray = _renderer.Camera.GetPickRay(
            (float)(fx * vpW), (float)(fy * vpH), vpW, vpH);

        var sb = new System.Text.StringBuilder();
        var raw = _renderer.PickToolpath((float)(fx * vpW), (float)(fy * vpH), vpW, vpH);
        if (raw is not null)
            sb.AppendLine($"[pick] toolpath hit: \"{raw.Name}\"");
        var picked = raw ?? PickForSceneSelection(vm, ray);
        sb.AppendLine($"[pick] PickForSceneSelection -> {(picked is null ? "null" : $"\"{picked.Name}\" tier={picked.PickTier} selectable={picked.Selectable}")}");
        if (picked is not null)
        {
            var userItem = vm.FindUserMeshOutlinerItem(picked);
            sb.AppendLine($"[pick] userItem={(userItem?.Name ?? "null")} isUserContent={vm.IsUserModelSceneNode(picked)}");
            var devRoot = FindDevNodeRoot(picked);
            sb.AppendLine($"[pick] devRoot={(devRoot is null ? "null" : DevLabel(devRoot))} infra={IsLfamInfrastructureNode(picked)} lockedRow={vm.IsNodeLockedInOutliner(picked)}");
            var chain = new List<string>();
            for (var c = picked; c is not null; c = c.Parent) chain.Add(c.Name);
            sb.AppendLine($"[pick] ancestry: {string.Join(" < ", chain)}");
        }
        RequestSceneSelection(vm, picked);
        sb.Append($"[pick] final selection: {(_renderer.SelectedNode is { } s ? $"\"{s.Name}\"" : "null")}");
        return sb.ToString();
    }

    SceneNode? PickForSceneSelection(ViewportViewModel vm, Ray ray)
    {
        // On LFAM cells the mounted tool often occludes the print volume — prefer user imports/scans.
        if (IsLfamProductionCell(vm) && !vm.IsDevMode)
        {
            var userHit = Picker.PickWhere(
                ray, _renderer.SceneRoot,
                n => vm.IsUserModelSceneNode(n) || vm.IsEffectorNode(n)
                     || vm.IsModifierNode(n) || vm.IsModifiersGroupNode(n), out _);
            if (userHit is not null)
                return Picker.FindSelectableRoot(userHit, _renderer.SceneRoot);
        }

        return _renderer.Pick(ray);
    }

    void RequestSceneSelection(ViewportViewModel vm, SceneNode? node)
    {
        // Resolve mesh-leaf picks to the owning import/scan outliner root for gizmo + transform UI.
        if (node is not null && vm.FindUserMeshOutlinerItem(node)?.Node is { } userRoot)
            node = userRoot;

        bool isUserContent = node is not null
            && (vm.IsUserModelSceneNode(node) || vm.IsEffectorNode(node)
                || vm.IsModifierNode(node) || vm.IsModifiersGroupNode(node));

        // Dev mode: registered environment props (print bed, stands, docks) are pickable —
        // resolve the mesh-leaf hit to its dev root so the gizmo transforms the whole prop.
        if (!isUserContent && node is not null && vm.IsDevMode
            && FindDevNodeRoot(node) is { } devRoot)
        {
            node = devRoot;
        }
        // LFAM production cells block infrastructure picks unless they are user imports/scans.
        else if (!isUserContent && node is not null && IsLfamProductionCell(vm) && IsLfamInfrastructureNode(node))
        {
            if (_currentToolNode is not null)
                node = _currentToolNode;
            else
                node = null;
        }
        else if (!isUserContent && node is not null && _multiTools is not null
                 && TryResolveMultiToolPick(node, out var toolName, out var toolRoot, out var onDock))
        {
            if (onDock && vm.ActiveCell is { } cell)
            {
                var cfg = cell.EffectiveTools.FirstOrDefault(t => t.Name == toolName);
                if (cfg is not null)
                {
                    ApplyMultiToolMount(cfg, vm);
                    node = _currentToolNode;
                }
                else
                    node = toolRoot;
            }
            else
                node = toolRoot;
        }
        else if (!isUserContent && node is not null && _currentToolNode is not null
                 && IsSameOrDescendant(_currentToolNode, node))
        {
            node = _currentToolNode;
        }

        // Cell fixtures (print bed, stands, docks, rotary) are dev-mode-only —
        // regardless of outliner padlock state. This must null the node BEFORE the
        // outliner sync below, or the outliner still shows the fixture as selected
        // even when the renderer vetoes it. User imports/scans are exempt: they are
        // parented UNDER the bed node (to ride E1), so the dev-root walk would
        // otherwise swallow every model click.
        if (!isUserContent && node is not null && !vm.IsDevMode && FindDevNodeRoot(node) is not null)
            node = null;

        // The scene root itself is never a meaningful selection.
        if (node == _renderer.SceneRoot)
            node = null;

        // Locked outliner rows (robot, bed, locked toolheads) can't be selected —
        // except dev-editable props while dev mode is on.
        if (node is not null && vm.IsNodeLockedInOutliner(node)
            && !(vm.IsDevMode && IsDevNode(node)))
            node = null;

        if (node is not null && OutlinerModelOps.IsScan(node) && vm.SelectedScanCount >= 1)
            SyncScanSelectionToRenderer(vm);
        else
        {
            _renderer.Select(node);
            vm.SetOutlinerSelection(node);
        }

        UpdateFocusOverlay();
        GlCanvas.RequestNextFrameRendering();
    }

    // -- Toolhead selection check ----------------------------------------------

    private Vector3 GetGizmoPivotWorld(SceneNode node)
    {
        if (IsToolNodeSelected() && _renderer.TcpFrameMatrix is { } tcp)
            return tcp.Row3.Xyz;
        return node.WorldTransform.Row3.Xyz;
    }

    /// <summary>Rotation-only basis (no translation) the gizmo should be drawn/hit-tested/dragged
    /// in for <paramref name="node"/> — a Vertical Cut modifier's own RotationDegrees around Z
    /// (X/Y follow the plane, Z stays world-up since RotationDegrees only ever rotates about Z
    /// anyway), or null (world-axis-aligned, every other selectable object's existing behavior)
    /// for anything else. Scoped to Cut modifiers only — see feedback from Jeff 2026-07-22.</summary>
    private Matrix4? GetModifierAxisBasis(SceneNode? node)
    {
        // Called from OnRender (GL thread) every frame — must use the cached _vm field, never
        // DataContext (an Avalonia dispatcher-verified property that throws
        // InvalidOperationException off the UI thread). See the GL-thread DataContext audit
        // referenced in project memory; this exact mistake crashed on the very next model import.
        if (node is null) return null;
        if (_vm is not { } vm) return null;
        if (vm.FindModifierForNode(node) is not { Orientation: CutOrientation.Vertical } cut) return null;
        return Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(cut.RotationDegrees));
    }

    private void BeginToolIkDrag(SceneNode node)
    {
        if (node != _currentToolNode || _ikSolver is null || _renderer.TcpFrameMatrix is not { } tcpMat)
        {
            _toolIsDragging = false;
            return;
        }

        _toolIsDragging = true;
        RefreshIkSceneKinematics();

        if (DataContext is ViewportViewModel { Robot: { } robot })
        {
            robot.Desync();
            _ikDragTargetRot = _ikDragInitialTargetRot = _ikSolver.TargetRotFromKukaAbc(
                (float)robot.TcpA, (float)robot.TcpB, (float)robot.TcpC);
        }

        _ikDragTcpOffset   = tcpMat.Row3.Xyz - node.WorldTransform.Row3.Xyz;
        _ikDragTcpPosition = tcpMat.Row3.Xyz;
    }

    private bool IsToolIkRotating()
    {
        if (!_toolIsDragging) return false;
        return _kbTransformActive
            ? _kbTransformOp == GizmoMode.Rotate
            : _renderer.GizmoMode == GizmoMode.Rotate;
    }

    private void ApplyToolRotationDelta(float delta)
    {
        var rot = _gizmoDragAxis switch
        {
            GizmoAxis.X => Matrix4.CreateRotationX(delta),
            GizmoAxis.Y => Matrix4.CreateRotationY(delta),
            _           => Matrix4.CreateRotationZ(delta),
        };

        var (r0, r1, r2) = _ikDragInitialTargetRot;
        r0 = Vector3.Normalize(TransformDir(r0, rot));
        r1 = Vector3.Normalize(TransformDir(r1, rot));
        r2 = Vector3.Normalize(TransformDir(r2, rot));
        _ikDragTargetRot = (r0, r1, r2);
    }

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
        if (IsToolNodeSelected() && mode == GizmoMode.Scale) return;
        _renderer.GizmoMode = mode;
        if (_vm is { } vm)
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
            var   nodePos0 = GetGizmoPivotWorld(node);
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

        BeginToolIkDrag(node);

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
        _gizmoDragPlanePoint   = GetGizmoPivotWorld(node);
        _gizmoDragInitialLocal = node.LocalTransform;
        BeginTransformLink(node);

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

    // ── Toolpath paint brush ───────────────────────────────────────────────────

    private bool _paintStroking, _paintResizing, _paintStrokeChanged;
    private float _paintResizeStartX;
    private double _paintResizeStartRadius;
    private Avalonia.Point _lastPaintPx;
    private TkVector3? _paintHoverWorld;        // bead under cursor (brush circle)
    private List<TkVector3>? _paintHoverLine;   // contour a line tool would pick
    private List<TkVector3>? _paintSelectedLine; // last clicked contour (sticky highlight)
    private TkVector3 _paintSelectedColor = new(1f, 0.55f, 0.08f); // amber = selected
    // Shift+click accumulates: previous selections stay lit so multiple lines can
    // be marked in one editing pass.
    private readonly List<(List<TkVector3> Pts, TkVector3 Color)> _paintMultiLines = [];
    // Region-select (square marquee / lasso) drag state.
    private bool _paintBoxDragging;
    private Avalonia.Point _paintBoxStart;
    private readonly List<Avalonia.Point> _paintLassoPts = [];
    /// <summary>Applied modifications (reselectable from the MODIFICATIONS panel).</summary>
    private readonly List<PaintModificationRecord> _paintModifications = [];

    /// <summary>One path/point span inside a grouped modification (Shift multi-select).</summary>
    private sealed class PaintModMember
    {
        public required ToolpathLayer Layer { get; init; }
        public required ContourSpan Span { get; init; }
        public required System.Numerics.Vector3 Origin { get; init; }
        public required TkMatrix4 Wt { get; init; }
        public required List<TkVector3> World { get; init; }
        public required List<System.Numerics.Vector3> MarkCenters { get; init; }
    }

    private sealed class PaintModificationRecord
    {
        public required Guid Id { get; init; }
        public required Core.Models.PaintMarkKind Kind { get; init; }
        /// <summary>Primary (first) span — kept for bridge/offset compat.</summary>
        public required ToolpathLayer Layer { get; init; }
        public required ContourSpan Span { get; init; }
        public required System.Numerics.Vector3 Origin { get; init; }
        public required TkMatrix4 Wt { get; init; }
        public required List<TkVector3> World { get; init; }
        /// <summary>Union of all member mark centres (restyle / delete / style apply).</summary>
        public required List<System.Numerics.Vector3> MarkCenters { get; init; }
        public required string Title { get; set; }
        public required string Detail { get; set; }

        /// <summary>
        /// All spans in this modification. Shift multi-select Apply creates one card
        /// with multiple members so Support type / side / delete apply to the group.
        /// Empty = legacy single-span mod (use Layer/Span only).
        /// </summary>
        public List<PaintModMember> Members { get; } = [];

        public int MemberCount => Members.Count > 0 ? Members.Count : 1;
        public bool IsGroup => MemberCount > 1;

        /// <summary>Optional second anchor for multi-layer Formbound scaffold.</summary>
        public ToolpathLayer? TargetLayer { get; set; }
        public ContourSpan? TargetSpan { get; set; }
        public System.Numerics.Vector3 TargetOrigin { get; set; }
        public TkMatrix4 TargetWt { get; set; } = TkMatrix4.Identity;
        public List<TkVector3>? TargetWorld { get; set; }
        public List<System.Numerics.Vector3> TargetMarkCenters { get; } = [];
        public List<System.Numerics.Vector3> ScaffoldMarkCenters { get; } = [];
        public int ScaffoldLayerCount { get; set; }
        public bool IsExpanded { get; set; }
        public bool HasBridgeTarget => TargetLayer is not null;

        /// <summary>"Formbound Buttress", "Formbound Bridge", "Tree Support",
        /// or "Structural Support".</summary>
        public string SupportType { get; set; } = "Formbound Buttress";

        /// <summary>Index into AdditiveSettings.StructuralSupports when this card is
        /// a Structural Support (its pocket settings render inline). -1 = not linked.</summary>
        public int StructuralIndex { get; set; } = -1;

        /// <summary>"Inside" or "Outside" — Formbound wall side for this selection.</summary>
        public string SupportSide { get; set; } = "Inside";

        /// <summary>Enumerate spans (members if present, else primary only).</summary>
        public IEnumerable<PaintModMember> EnumerateMembers()
        {
            if (Members.Count > 0)
            {
                foreach (var m in Members) yield return m;
                yield break;
            }
            yield return new PaintModMember
            {
                Layer = Layer,
                Span = Span,
                Origin = Origin,
                Wt = Wt,
                World = World,
                MarkCenters = MarkCenters,
            };
        }
    }

    // The actionable edit selection: picks the toolbar's Support/Remove apply to.
    private readonly List<(ToolpathLayer Layer, ContourSpan Span,
        System.Numerics.Vector3 Origin, TkMatrix4 Wt, List<TkVector3> World)> _paintSelection = [];
    private DateTime _paintHoverAt = DateTime.MinValue;
    /// <summary>Stable id of the sticky selection — used for console feedback + undo.</summary>
    private PaintLineId? _paintSelectedId;
    private bool _paintSelectionUndoSuppress;

    /// <summary>
    /// Point-mode range anchor: first click (or last non-range pick). Shift+click another
    /// point on the same contour selects every bead on the shortest path between them.
    /// </summary>
    private ToolpathLayer? _paintPointAnchorLayer;
    private int _paintPointAnchorMove = -1;
    private System.Numerics.Vector3 _paintPointAnchorOrigin;
    private TkMatrix4 _paintPointAnchorWt = TkMatrix4.Identity;

    /// <summary>Hover feedback while the Edit menu is open (or a paint tool is armed)
    /// and no stroke is active: yellow contour under the pointer for line-select, or
    /// the brush circle under a brush tool. Throttled — full-path pick is expensive.</summary>
    /// <summary>Converts an existing modification group (card) into a Structural
    /// Support: spec anchored at the group's anchor move, default 2×4 rectangle
    /// one bead inboard. The card stays in MODIFICATIONS with the pocket settings
    /// inline. Returns the new spec's index, or -1.</summary>
    private int ConvertModificationToStructuralSupport(
        ViewportViewModel vm, PaintModificationRecord rec)
    {
        var add = vm.AdditiveSettings;
        if (add is null) return -1;

        int mi = Math.Clamp(rec.Span.Start, 0, rec.Layer.Moves.Count - 1);
        var mv = rec.Layer.Moves[mi];
        var mid = (mv.From + mv.To) * 0.5f;

        // The layer knows its own index — ask it. Resolving this via ActiveScrubToolpath
        // silently fell back to layer 0 whenever no scrub was armed, so the support anchored
        // at the BOTTOM of the model with the XY of wherever you clicked. Combined with the
        // reach gate that now (correctly) terminates where the wall isn't, that killed the
        // arm a dozen layers up instead of building it where you asked.
        int layerIdx = rec.Layer.Index;
        if (layerIdx < 0 && vm.ActiveScrubToolpath is { } tp)
            layerIdx = Math.Max(0, tp.Layers.IndexOf(rec.Layer));


        var dir = new System.Numerics.Vector2(mv.To.X - mv.From.X, mv.To.Y - mv.From.Y);
        if (dir.LengthSquared() < 1e-6f) dir = new(1, 0);
        dir = System.Numerics.Vector2.Normalize(dir);
        var left = new System.Numerics.Vector2(-dir.Y, dir.X);
        float beadW = (float)add.BeadWidth;
        const float depth = 42f;
        var center = new System.Numerics.Vector2(mid.X, mid.Y) + left * (depth * 0.5f + beadW * 2f);

        add.AddStructuralSupport(new Core.Models.StructuralSupportSpec
        {
            AnchorX = mid.X, AnchorY = mid.Y, AnchorLayer = layerIdx,
            CenterX = center.X, CenterY = center.Y,
            WidthMm = 92f, DepthMm = depth,
            LayersUp = 9999, LayersDown = 0,
        });
        LogPaintConsole($"[support] group converted to Structural Support @ L{layerIdx} " +
            $"({mid.X:F0}, {mid.Y:F0}) — tune it on its MODIFICATIONS card, then Update Slice.");
        vm.UpdateSliceCommand?.Execute(null);
        return add.StructuralSupports.Count - 1;
    }

    /// <summary>Creates a Structural Support spec anchored at the last point/section
    /// selected in edit mode. The helper shape starts one bead inboard (left of travel
    /// — into the wall for zig-zag panels); adjust in the right panel, then Update Slice.</summary>
    private void AddStructuralSupportFromSelection(ViewportViewModel vm)
    {
        var add = vm.AdditiveSettings;
        if (add is null) return;
        if (_paintSelection.Count == 0)
        {
            LogPaintConsole("[support] select a point on the wall first (edit mode → click)");
            return;
        }

        var sel = _paintSelection[^1];
        int mi = Math.Clamp(sel.Span.Start, 0, sel.Layer.Moves.Count - 1);
        var mv = sel.Layer.Moves[mi];
        var mid = (mv.From + mv.To) * 0.5f;

        // Same as ConvertModificationToStructuralSupport: take the layer's own index rather
        // than looking it up in ActiveScrubToolpath, which silently anchored to layer 0 when
        // no scrub was armed.
        int layerIdx = sel.Layer.Index;
        if (layerIdx < 0 && vm.ActiveScrubToolpath is { } tp)
            layerIdx = Math.Max(0, tp.Layers.IndexOf(sel.Layer));

        var dir = new System.Numerics.Vector2(mv.To.X - mv.From.X, mv.To.Y - mv.From.Y);
        if (dir.LengthSquared() < 1e-6f) dir = new(1, 0);
        dir = System.Numerics.Vector2.Normalize(dir);
        var left = new System.Numerics.Vector2(-dir.Y, dir.X);
        float beadW = (float)add.BeadWidth;
        const float depth = 42f;
        var center = new System.Numerics.Vector2(mid.X, mid.Y) + left * (depth * 0.5f + beadW * 2f);

        add.AddStructuralSupport(new Core.Models.StructuralSupportSpec
        {
            AnchorX = mid.X, AnchorY = mid.Y, AnchorLayer = layerIdx,
            CenterX = center.X, CenterY = center.Y,
            WidthMm = 92f, DepthMm = depth,
            LayersUp = 9999, LayersDown = 0,
        });
        int newSpecIdx = add.StructuralSupports.Count - 1;
        string newSpecName = add.SupportNameAt(newSpecIdx);

        // The applied group stays in MODIFICATIONS as a Structural card — the
        // pocket helper settings live on the card itself.
        _paintModifications.Add(new PaintModificationRecord
        {
            Id = Guid.NewGuid(),
            Kind = Core.Models.PaintMarkKind.Bridge,
            Layer = sel.Layer,
            Span = sel.Span,
            Origin = sel.Origin,
            Wt = sel.Wt,
            World = sel.World,
            MarkCenters = [],
            Title = $"{newSpecName} · layer {layerIdx + 1}",
            Detail = $"anchor ({mid.X:F0}, {mid.Y:F0}) · Update Slice to bake",
            SupportType = Core.Models.PaintSupportStyleUtil.LabelStructural,
            StructuralIndex = newSpecIdx,
            IsExpanded = true,
        });
        SyncPaintModificationsUi(vm);
        vm.MarkWorkspaceDirty?.Invoke();
        // New support becomes the gizmo target immediately — drag it, don't type at it.
        if (vm.ActiveGizmoModeInternal is GizmoMode.None or GizmoMode.Scale)
            vm.ActiveGizmoModeInternal = GizmoMode.Translate;

        LogPaintConsole($"[support] {newSpecName} @ L{layerIdx} anchor ({mid.X:F0}, {mid.Y:F0}) — " +
            "drag its gizmo to place it, or tune the shape on its MODIFICATIONS card.");
        vm.UpdateSliceCommand?.Execute(null);
    }

    private void UpdatePaintHover(ViewportViewModel vm, Avalonia.Point pos)
    {
        // ~12 Hz hover: path pick walks every move; higher rates freeze 50k+ move paths.
        if ((DateTime.UtcNow - _paintHoverAt).TotalMilliseconds < 80) return;
        _paintHoverAt = DateTime.UtcNow;
        // Line tools, or edit open with no brush → contour/point hover.
        // Brush tools → bead-sphere cursor.
        if (vm.PaintLineToolActive || !vm.PaintBrushActive)
        {
            _paintHoverWorld = null;
            _paintHoverLine = PickSpanUnderCursor(pos) is { } pick
                ? SpanWorldHighlight(pick, pointMode: vm.PaintPointGranularityActive)
                : null;
        }
        else
        {
            _paintHoverLine = null;
            _paintHoverWorld = PickBeadUnderCursor(pos)?.World;
        }
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>Per-frame (GL thread) paint overlay: mark spheres (cyan = bridge,
    /// red = remove), the brush cursor circle, hovered-line highlight, and sticky
    /// selected-line highlight after a click.</summary>
    private void UpdatePaintOverlay(ViewportViewModel vm)
    {
        // While the edit menu is open always draw the overlay (white-bead edit mode
        // + selection feedback), even before any marks exist.
        bool show = vm.ViewMode == "Preview" && vm.AdditiveSettings is not null
            && (vm.IsPaintEditOpen
                || vm.PaintBrushActive
                || (vm.AdditiveSettings?.PaintMarks.Count ?? 0) > 0);
        if (!show)
        {
            _renderer.SetPaintOverlay([], null);
            return;
        }
        var add = vm.AdditiveSettings!;
        var segs = new List<(TkVector3 Pos, TkVector3 Color)>();
        // Point-mode hover/select: yellow bead recolour (GL points), not line spheres.
        var hlPts = new List<(TkVector3 Pos, TkVector3 Color)>();

        // Raw toolpath coords → display world via the first visible toolpath node.
        System.Numerics.Vector3 origin = default;
        var wt = TkMatrix4.Identity;
        foreach (var (node, _) in _toolpathByNode)
        {
            if (!node.Visible) continue;
            _toolpathOriginByNode.TryGetValue(node, out origin);
            wt = node.WorldTransform;
            break;
        }

        var cBridge = new TkVector3(0.15f, 0.85f, 1f);   // cyan = bridge / buttress
        var cRemove = new TkVector3(1f, 0.28f, 0.2f);    // red = remove
        // Cap mark spheres — thousands of line-bridge dabs would freeze every frame.
        // Honour ShowPaintMarkers so the user can hide Support/Remove dabs while editing.
        // Visual radius is ~10× smaller than the stored dab radius (planning still uses
        // full m.Radius for coverage / half-band projection).
        const float paintMarkDisplayScale = 0.1f;
        if (vm.ShowPaintMarkers)
        {
            int markCount = add.PaintMarks.Count;
            int markStep = markCount <= 400 ? 1 : (markCount + 399) / 400;
            for (int mi = 0; mi < markCount; mi += markStep)
            {
                var m = add.PaintMarks[mi];
                var w = TransformPoint(new TkVector3(
                    m.Center.X - origin.X, m.Center.Y - origin.Y, m.Center.Z - origin.Z), wt);
                float rVis = MathF.Max(0.35f, m.Radius * paintMarkDisplayScale);
                AddMarkSphere(segs, new TkVector3(w.X, w.Y, w.Z), rVis,
                    m.Kind == Core.Models.PaintMarkKind.Bridge ? cBridge : cRemove);
            }
        }

        bool pointMode = vm.PaintPointGranularityActive;
        float pointR = MathF.Max(1.4f, (float)(vm.AdditiveSettings?.BeadWidth ?? 6) * 0.28f);
        // Bright yellow for hover + selected beads (over lime path points).
        var cYellow = new TkVector3(1f, 0.92f, 0.18f);
        var cSelAmber = new TkVector3(1f, 0.55f, 0.08f);

        // Only light the portion of each selection that is inside the current
        // layer-slider / timeline scrub window (hidden layers stay un-highlighted).
        int scrubLimit = GetPaintScrubMoveLimit();
        int scrubStart = GetPaintScrubMoveStart();

        void DrawSelectionHighlight(List<TkVector3> pts, TkVector3 col, bool sticky)
        {
            if (pts.Count == 0) return;
            if (pointMode || pts.Count == 1)
            {
                foreach (var p in pts)
                {
                    hlPts.Add((p, cYellow));
                    AddMarkSphere(segs, p, sticky ? pointR : pointR * 0.85f,
                        col.LengthSquared > 0.01f ? col : cSelAmber);
                }
            }
            else if (sticky)
            {
                AddThickPolyline(segs, pts, col, radiusMm: 0.8f, fat: true);
                float tipR = MathF.Max(0.8f, (float)(vm.AdditiveSettings?.BeadWidth ?? 6) * 0.15f);
                AddMarkSphere(segs, pts[0], tipR, col);
                AddMarkSphere(segs, pts[^1], tipR, col);
            }
            else
                AddThickPolyline(segs, pts, col, radiusMm: 0.5f, fat: false);
        }

        if (_paintSelection.Count > 0)
        {
            var fallbackCol = _paintSelectedColor.LengthSquared > 0.01f
                ? _paintSelectedColor : cSelAmber;
            for (int si = 0; si < _paintSelection.Count; si++)
            {
                var sel = _paintSelection[si];
                if (!TryClipSpanToScrubWindow(sel.Layer, sel.Span, scrubStart, scrubLimit,
                        out var clipped))
                    continue;
                var poly = SpanWorldHighlight(
                    (sel.Layer, clipped, sel.Origin, sel.Wt), pointMode);
                bool sticky = si == _paintSelection.Count - 1;
                DrawSelectionHighlight(poly, fallbackCol, sticky);
            }
        }
        else
        {
            // Fallback when selection list is empty but sticky/multi caches exist.
            foreach (var (mpts, mcol) in _paintMultiLines)
                DrawSelectionHighlight(mpts, mcol, sticky: false);
            if (_paintSelectedLine is { Count: > 0 } sel)
                DrawSelectionHighlight(sel, _paintSelectedColor, sticky: true);
        }

        // Live hover: path → yellow contour; point → yellow bead only (no sphere).
        // Hover is already pick-filtered to the scrub window.
        var cHover = new TkVector3(1f, 0.95f, 0.15f);
        bool lineHover = vm.PaintLineToolActive || !vm.PaintBrushActive;
        if (lineHover && _paintHoverLine is { Count: > 0 } hl)
        {
            if (pointMode || hl.Count == 1)
            {
                foreach (var p in hl)
                    hlPts.Add((p, cYellow));
            }
            else
                AddThickPolyline(segs, hl, cHover, radiusMm: 0.6f, fat: false);
        }
        if (!lineHover && _paintHoverWorld is { } hw && vm.PaintBrushActive)
            AddMarkSphere(segs, hw, (float)vm.PaintBrushRadiusMm, cHover);

        // Structural Support helpers: shape outline + anchor tick + neck preview,
        // visible while edit mode is open (cyan = selected support, dim = others).
        if (vm.IsPaintEditOpen && add.StructuralSupports.Count > 0)
        {
            var cSel = new TkVector3(0.2f, 0.95f, 1f);
            var cDim = new TkVector3(0.25f, 0.55f, 0.6f);
            for (int si = 0; si < add.StructuralSupports.Count; si++)
            {
                var spec = add.StructuralSupports[si];
                if (!spec.Enabled) continue;
                var col = si == add.SelectedSupportIndex ? cSel : cDim;
                var outline = spec.BuildOutline();
                if (outline.Length < 3) continue;

                float helperZ = 0f;
                if (_activeScrubNode is { } hn && _toolpathByNode.TryGetValue(hn, out var htp)
                    && htp.Layers.Count > 0)
                {
                    int li = Math.Clamp(vm.CurrentScrubLayerIndex, 0, htp.Layers.Count - 1);
                    helperZ = htp.Layers[li].Z;
                }

                TkVector3 W(float x, float y)
                {
                    var w = TransformPoint(new TkVector3(
                        x - origin.X, y - origin.Y, helperZ - origin.Z), wt);
                    return new TkVector3(w.X, w.Y, w.Z);
                }

                var poly = new List<TkVector3>(outline.Length + 1);
                foreach (var v in outline) poly.Add(W(v.X, v.Y));
                poly.Add(W(outline[0].X, outline[0].Y));
                AddThickPolyline(segs, poly, col, radiusMm: 0.9f, fat: si == add.SelectedSupportIndex);

                // Anchor tick + neck preview to nearest outline vertex.
                var anchorW = W(spec.AnchorX, spec.AnchorY);
                AddMarkSphere(segs, anchorW, 2.2f, col);
                int near = 0; float nd = float.MaxValue;
                for (int i = 0; i < outline.Length; i++)
                {
                    float dx = outline[i].X - spec.AnchorX, dy = outline[i].Y - spec.AnchorY;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < nd) { nd = d2; near = i; }
                }
                AddThickPolyline(segs,
                    [anchorW, W(outline[near].X, outline[near].Y)], col, radiusMm: 0.6f, fat: false);
            }
        }

        _renderer.SetPaintOverlay(segs, hlPts.Count > 0 ? hlPts : null);
    }

    /// <summary>
    /// Highlight polyline for paint overlay. Default is a single centre-line
    /// (fast). When <paramref name="fat"/> is true, adds one parallel offset pair
    /// so the stroke still reads on dense toolpaths without the old 9× tube cost.
    /// </summary>
    private static void AddThickPolyline(
        List<(TkVector3 Pos, TkVector3 Color)> segs,
        IReadOnlyList<TkVector3> pts,
        TkVector3 color,
        float radiusMm,
        bool fat = false)
    {
        if (pts.Count < 2) return;
        // Downsample very long picks so a full-layer wall can't stall the GL thread.
        const int maxPts = 256;
        int step = pts.Count <= maxPts ? 1 : (pts.Count + maxPts - 1) / maxPts;

        for (int i = step; i < pts.Count; i += step)
        {
            int i0 = i - step;
            segs.Add((pts[i0], color));
            segs.Add((pts[i], color));
        }
        // Always include the true tip.
        if ((pts.Count - 1) % step != 0)
        {
            segs.Add((pts[^2], color));
            segs.Add((pts[^1], color));
        }

        if (!fat || radiusMm < 0.2f) return;
        // One offset pair only (was four axes × two sides = 8× extra).
        for (int i = step; i < pts.Count; i += step)
        {
            int i0 = i - step;
            var d = pts[i] - pts[i0];
            float len2 = d.LengthSquared;
            if (len2 < 1e-10f) continue;
            d /= MathF.Sqrt(len2);
            var side = TkVector3.Cross(d, TkVector3.UnitZ);
            if (side.LengthSquared < 1e-8f)
                side = TkVector3.Cross(d, TkVector3.UnitY);
            if (side.LengthSquared < 1e-8f) continue;
            side = TkVector3.Normalize(side) * radiusMm;
            segs.Add((pts[i0] + side, color));
            segs.Add((pts[i] + side, color));
            segs.Add((pts[i0] - side, color));
            segs.Add((pts[i] - side, color));
        }
    }

    /// <summary>Three orthogonal circles — reads as a sphere from any camera angle.</summary>
    private static void AddMarkSphere(
        List<(TkVector3 Pos, TkVector3 Color)> segs, TkVector3 c, float r, TkVector3 col)
    {
        const int N = 16;
        ReadOnlySpan<(TkVector3 E1, TkVector3 E2)> bases =
        [
            (TkVector3.UnitX, TkVector3.UnitY),
            (TkVector3.UnitX, TkVector3.UnitZ),
            (TkVector3.UnitY, TkVector3.UnitZ),
        ];
        foreach (var (e1, e2) in bases)
        {
            var prev = c + e1 * r;
            for (int k = 1; k <= N; k++)
            {
                float a = MathF.Tau * k / N;
                var p = c + e1 * (r * MathF.Cos(a)) + e2 * (r * MathF.Sin(a));
                segs.Add((prev, col));
                segs.Add((p, col));
                prev = p;
            }
        }
    }

    /// <summary>
    /// Pointer position and size in the same space as the GL host (DIP), so picks
    /// line up with the rendered beads regardless of parent chrome / DPI.
    /// </summary>
    private (float mx, float my, float vpW, float vpH) GetGlPickViewport(Avalonia.Point posOnViewport)
    {
        // Convert from ViewportView space → GlCanvas space (they usually match, but
        // any inset/scale must not drift the ray vs the painted beads).
        var inGl = this.TranslatePoint(posOnViewport, GlCanvas) ?? posOnViewport;
        float mx = (float)inGl.X;
        float my = (float)inGl.Y;
        float vpW = (float)Math.Max(1.0, GlCanvas.Bounds.Width);
        float vpH = (float)Math.Max(1.0, GlCanvas.Bounds.Height);
        return (mx, my, vpW, vpH);
    }

    /// <summary>
    /// Global move index at which the active scrub hides further geometry
    /// (moves with index ≥ this are not drawn and must not be pickable).
    /// <see cref="int.MaxValue"/> = no scrub limit.
    /// In 2D slice view only the <em>active</em> layer is pickable — neighbours
    /// (three below + dashed above) are drawn for context but not selectable.
    /// </summary>
    private int GetPaintScrubMoveLimit()
    {
        if (_vm is not { } vm) return int.MaxValue;
        if (!(vm.IsToolpathSelected || vm.IsScrubSessionActive)) return int.MaxValue;
        if (vm.ToolpathScrubMax <= 0) return int.MaxValue;
        // ScrubCount uses cumulative[scrubIndex]: vertices for moves [0, scrubIndex).
        // Match that — at scrub S, move S and above are not yet printed.
        int hi = Math.Clamp(vm.ToolpathScrubIndex, 0, vm.ToolpathScrubMax);

        // 2D slice: exclusive end of the active layer only (no layer above).
        if (vm.IsSlicePlaneViewerActive && vm.IsPaintEditOpen
            && vm.ScrubLayerEnds is { Length: > 0 } ends)
        {
            int cur = Math.Clamp(vm.CurrentScrubLayerIndex, 0, ends.Length - 1);
            hi = ends[cur];
        }
        return hi;
    }

    /// <summary>Lower bound of the pickable move window.
    /// In 2D slice view this is the start of the active layer only — layers below
    /// remain visible for context but are not hoverable/selectable.</summary>
    private int GetPaintScrubMoveStart()
    {
        if (_vm is not { } vm) return 0;
        if (!(vm.IsToolpathSelected || vm.IsScrubSessionActive)) return 0;

        if (vm.IsSlicePlaneViewerActive && vm.IsPaintEditOpen
            && vm.ScrubLayerEnds is { Length: > 0 } ends)
        {
            int cur = Math.Clamp(vm.CurrentScrubLayerIndex, 0, ends.Length - 1);
            // Exclusive end of previous layer = start of current.
            return cur <= 0 ? 0 : ends[cur - 1];
        }

        return Math.Max(0, vm.ToolpathScrubLowIndex);
    }

    /// <summary>
    /// Global move index where <paramref name="layer"/> begins in its toolpath
    /// (sum of prior layers' move counts). Used to clip selection highlights to the
    /// scrub window.
    /// </summary>
    private bool TryGetLayerGlobalMoveStart(ToolpathLayer layer, out int globalStart)
    {
        foreach (var (_, tp) in _toolpathByNode)
        {
            int g = 0;
            foreach (var l in tp.Layers)
            {
                if (ReferenceEquals(l, layer))
                {
                    globalStart = g;
                    return true;
                }
                g += l.Moves.Count;
            }
        }
        globalStart = 0;
        return false;
    }

    /// <summary>
    /// Clips a layer-local span to the visible scrub window
    /// <c>[scrubStart, scrubLimit)</c>. Returns false when the span is fully hidden.
    /// </summary>
    private bool TryClipSpanToScrubWindow(
        ToolpathLayer layer, ContourSpan span, int scrubStart, int scrubLimit,
        out ContourSpan clipped)
    {
        clipped = span;
        if (span.Count <= 0) return false;
        // No scrub isolation → everything is visible.
        if (scrubLimit == int.MaxValue && scrubStart <= 0) return true;
        if (!TryGetLayerGlobalMoveStart(layer, out int g0))
            return true; // unknown owner — keep highlight rather than drop it

        int spanG0 = g0 + span.Start;
        int spanG1 = spanG0 + span.Count; // exclusive
        int visG0 = Math.Max(spanG0, scrubStart);
        int visG1 = Math.Min(spanG1, scrubLimit);
        if (visG1 <= visG0) return false;

        clipped = new ContourSpan(
            visG0 - g0,
            visG1 - visG0,
            Closed: false, // clipped portion is an open section even if parent was closed
            EntryTravelIndex: span.EntryTravelIndex);
        return true;
    }

    /// <summary>Nearest visible extrude-bead midpoint under the cursor: raw
    /// toolpath coordinates (for marks) + display world (for the cursor circle).</summary>
    private (System.Numerics.Vector3 Raw, TkVector3 World)? PickBeadUnderCursor(Avalonia.Point pos)
    {
        var (mx, my, vpW, vpH) = GetGlPickViewport(pos);
        if (vpW <= 1f || vpH <= 1f) return null;
        int scrubLimit = GetPaintScrubMoveLimit();
        int scrubStart0 = GetPaintScrubMoveStart();
        var viewProj = _renderer.GetViewProjectionMatrix(vpW, vpH);

        const float pickPx = 30f;
        const float tightPx = 9f;
        // Same tiered rule as the line picker: within the tight radius the FRONT
        // bead wins (depth bucketed); the wide ring is a nearest-on-screen fallback.
        const float depthBucket = 12f;
        long t1Bucket = long.MaxValue;
        float t1Screen = float.MaxValue;
        (System.Numerics.Vector3, TkVector3)? t1Hit = null;
        float bestD = pickPx;
        (System.Numerics.Vector3, TkVector3)? hit = null;
        foreach (var (node, tp) in _toolpathByNode)
        {
            if (!node.Visible) continue;
            _toolpathOriginByNode.TryGetValue(node, out var origin);
            // Match SceneRenderer toolpath draw (LocalTransform * worldMVP).
            var wt = node.LocalTransform;
            float ox = origin.X, oy = origin.Y, oz = origin.Z;
            int globalMove = 0;
            foreach (var layer in tp.Layers)
            {
                var moves = layer.Moves;
                int layerCount = moves.Count;
                if (globalMove + layerCount <= scrubStart0)
                {
                    globalMove += layerCount;
                    continue;
                }
                if (globalMove >= scrubLimit) break;

                int i0 = Math.Max(0, scrubStart0 - globalMove);
                int i1 = Math.Min(layerCount, scrubLimit - globalMove);
                for (int i = i0; i < i1; i++)
                {
                    var mv = moves[i];
                    if (mv.Kind != MoveKind.Extrude) continue;
                    if (DataContext is ViewportViewModel bpVm && !PaintPickAllowed(mv, bpVm)) continue;
                    var mid = (mv.From + mv.To) * 0.5f;
                    var world = TransformPoint(
                        new TkVector3(mid.X - ox, mid.Y - oy, mid.Z - oz), wt);
                    var wp = new TkVector3(world.X, world.Y, world.Z);
                    var p = _renderer.ProjectToScreenDepth(
                        new Vector3(wp.X, wp.Y, wp.Z), viewProj, vpW, vpH);
                    if (float.IsNaN(p.X)) continue;
                    float d = MathF.Sqrt((p.X - mx) * (p.X - mx) + (p.Y - my) * (p.Y - my));
                    if (d <= tightPx)
                    {
                        long bucket = (long)(p.Z / depthBucket);
                        if (bucket < t1Bucket || (bucket == t1Bucket && d < t1Screen))
                        {
                            t1Bucket = bucket;
                            t1Screen = d;
                            t1Hit = (mid, wp);
                        }
                    }
                    if (d < bestD) { bestD = d; hit = (mid, wp); }
                }
                globalMove += layerCount;
            }
        }
        return t1Hit ?? hit;
    }

    /// <summary>Paints (or Alt-erases) one brush dab at the toolpath bead under the
    /// cursor. Marks are stored in RAW toolpath coordinates (the slicer's own world
    /// space) so <see cref="MassiveSlicer.Core.Slicing.ToolpathPaintFilter"/> and the
    /// planner compare like with like across re-slices.</summary>
    private void TryPaintAt(ViewportViewModel vm, Avalonia.Point pos, bool erase)
    {
        if (vm.AdditiveSettings is not { } add) return;
        _lastPaintPx = pos;
        if (PickBeadUnderCursor(pos) is not { } pick)
        {
            _paintHoverWorld = null;
            return;
        }
        _paintHoverWorld = pick.World;
        var h = pick.Raw;

        float radius = (float)vm.PaintBrushRadiusMm;
        if (erase)
        {
            int removed = add.PaintMarks.RemoveAll(m =>
                System.Numerics.Vector3.Distance(m.Center, h) < m.Radius + radius * 0.5f);
            if (removed > 0) _paintStrokeChanged = true;
            return;
        }

        var markKind = vm.PaintBridgeActive
            ? Core.Models.PaintMarkKind.Bridge
            : Core.Models.PaintMarkKind.Remove;
        // Dab spam control: skip when an equal mark already covers this spot.
        foreach (var m in add.PaintMarks)
            if (m.Kind == markKind
                && System.Numerics.Vector3.Distance(m.Center, h) < radius * 0.4f)
                return;
        var brushRole = markKind == Core.Models.PaintMarkKind.Bridge
            ? Core.Models.PaintBridgeRole.SupportBar
            : Core.Models.PaintBridgeRole.None;
        var brushStyle = markKind == Core.Models.PaintMarkKind.Bridge
            ? Core.Models.PaintSupportStyleUtil.FromLabel(vm.PaintSupportType)
            : Core.Models.PaintSupportStyle.FormboundButtress;
        add.PaintMarks.Add(new Core.Models.PaintMark(h, radius, markKind, brushRole, brushStyle));
        _paintStrokeChanged = true;
    }

    /// <summary>Clears the actionable edit selection and its highlights.</summary>
    private void DeselectPaintSelection(ViewportViewModel vm)
    {
        _paintSelection.Clear();
        _paintMultiLines.Clear();
        _paintSelectedLine = null;
        _paintSelectedId = null;
        ClearPaintPointAnchor();
        SyncPaintSelectionUi(vm);
        GlCanvas.RequestNextFrameRendering();
    }

    private void ClearPaintPointAnchor()
    {
        _paintPointAnchorLayer = null;
        _paintPointAnchorMove = -1;
    }

    private void SetPaintPointAnchor(
        ToolpathLayer layer, int moveIndex,
        System.Numerics.Vector3 origin, TkMatrix4 wt)
    {
        _paintPointAnchorLayer = layer;
        _paintPointAnchorMove = moveIndex;
        _paintPointAnchorOrigin = origin;
        _paintPointAnchorWt = wt;
    }

    /// <summary>Rebuild multi + sticky highlights from the current selection list.</summary>
    private void RebuildPaintSelectionHighlights(TkVector3 color)
    {
        _paintMultiLines.Clear();
        _paintSelectedColor = color;
        for (int i = 0; i < _paintSelection.Count - 1; i++)
            _paintMultiLines.Add((_paintSelection[i].World, color));
        if (_paintSelection.Count > 0)
        {
            _paintSelectedLine = new List<TkVector3>(_paintSelection[^1].World);
            _paintHoverLine = _paintSelectedLine;
        }
        else
        {
            _paintSelectedLine = null;
            _paintHoverLine = null;
        }
    }

    /// <summary>Drops one selected path/point from the multi-selection (popup ✕).</summary>
    private void RemovePaintSelectionItem(
        ViewportViewModel vm, int layerIndex, int moveStart, int moveCount)
    {
        int idx = _paintSelection.FindIndex(s =>
            s.Layer.Index == layerIndex
            && s.Span.Start == moveStart
            && s.Span.Count == moveCount);
        if (idx < 0) return;

        _paintSelection.RemoveAt(idx);
        // Rebuild highlights: multi-lines = all but last; sticky = last (or clear).
        var col = _paintSelectedColor;
        _paintMultiLines.Clear();
        for (int i = 0; i < _paintSelection.Count - 1; i++)
            _paintMultiLines.Add((_paintSelection[i].World, col));
        if (_paintSelection.Count > 0)
        {
            _paintSelectedLine = new List<TkVector3>(_paintSelection[^1].World);
            _paintHoverLine = _paintSelectedLine;
        }
        else
        {
            _paintSelectedLine = null;
            _paintHoverLine = null;
            _paintSelectedId = null;
        }
        SyncPaintSelectionUi(vm);
        LogPaintConsole($"[edit] deselected L{layerIndex + 1} m{moveStart}+{moveCount} "
            + $"→ {_paintSelection.Count} remaining");
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>Pushes the live selection into the ViewModel popup list + count label.</summary>
    private void SyncPaintSelectionUi(ViewportViewModel vm)
    {
        bool pointMode = vm.PaintPointGranularityActive;
        var rows = new List<(int, int, int, float, bool, string, string)>(_paintSelection.Count);
        int pointBeads = 0;
        foreach (var s in _paintSelection)
        {
            int layerNum = s.Layer.Index + 1;
            bool isPoint = pointMode || s.Span.Count <= 1;
            int beadN = Math.Max(1, s.Span.Count);
            if (pointMode) pointBeads += beadN;
            string kind = !isPoint ? "Path"
                : beadN == 1 ? "Point"
                : $"{beadN} points";
            rows.Add((
                s.Layer.Index,
                s.Span.Start,
                s.Span.Count,
                s.Layer.Z,
                isPoint,
                $"Layer {layerNum} · {kind}",
                $"Z {s.Layer.Z:0.#} · m{s.Span.Start}+{s.Span.Count}"));
        }
        vm.SetPaintSelectionItems(rows);
        // Point mode counts beads (a shift-range is one row with many points).
        if (pointMode && pointBeads > 0)
            vm.PaintSelectionCount = pointBeads;
    }

    /// <summary>
    /// CREATE MODIFICATION → Offset path: insert parallel copies of each selected
    /// span into its layer, re-upload the toolpath, and record a MODIFICATIONS entry.
    /// </summary>
    private void ApplyOffsetPathToSelection(ViewportViewModel vm)
    {
        if (_paintSelection.Count == 0)
        {
            LogPaintConsole("[edit] Offset path needs a path selection first.");
            return;
        }
        if (_activeScrubNode is not { } node
            || !_toolpathByNode.TryGetValue(node, out var toolpath))
        {
            LogPaintConsole("[edit] Offset path: no active toolpath.");
            return;
        }

        float distance = (float)vm.OffsetDistanceMm;
        int count = Math.Max(1, vm.OffsetCount);
        var side = vm.OffsetSide switch
        {
            "Left" => MassiveSlicer.Core.Slicing.PathOffsetSide.Left,
            "Right" => MassiveSlicer.Core.Slicing.PathOffsetSide.Right,
            _ => MassiveSlicer.Core.Slicing.PathOffsetSide.Both,
        };

        int pathsAdded = 0;
        int segsAdded = 0;
        foreach (var existing in _paintModifications)
            existing.IsExpanded = false;

        // Process highest span starts first so earlier inserts don't shift later indices
        // when multiple selections share a layer.
        var ordered = _paintSelection
            .Select((s, i) => (s, i))
            .OrderByDescending(t => t.s.Layer.Index)
            .ThenByDescending(t => t.s.Span.Start)
            .ToList();

        foreach (var (sel, _) in ordered)
        {
            if (sel.Span.Count < 1) continue;
            var polylines = MassiveSlicer.Core.Slicing.PathOffsetter.OffsetSpan(
                sel.Layer, sel.Span, distance, count, side);
            if (polylines.Count == 0) continue;

            ToolpathMove? template = null;
            int end = Math.Min(sel.Layer.Moves.Count, sel.Span.Start + sel.Span.Count);
            for (int i = sel.Span.Start; i < end; i++)
            {
                if (sel.Layer.Moves[i].Kind == MoveKind.Extrude)
                {
                    template = sel.Layer.Moves[i];
                    break;
                }
            }

            // Insert after the selected span.
            int insertAt = Math.Min(sel.Layer.Moves.Count, sel.Span.Start + sel.Span.Count);
            var block = new List<ToolpathMove>();
            NVec3? cursor = insertAt > 0
                ? sel.Layer.Moves[insertAt - 1].To
                : null;

            for (int pi = 0; pi < polylines.Count; pi++)
            {
                var poly = polylines[pi];
                var extrudes = MassiveSlicer.Core.Slicing.PathOffsetter.PolylineToExtrudes(
                    poly, template);
                if (extrudes.Count == 0) continue;
                if (cursor is { } c
                    && NVec3.Distance(c, extrudes[0].From) > 0.05f)
                    block.Add(new ToolpathMove(c, extrudes[0].From, MoveKind.Travel));
                block.AddRange(extrudes);
                cursor = extrudes[^1].To;
                pathsAdded++;
                segsAdded += extrudes.Count;
            }

            if (block.Count == 0) continue;
            sel.Layer.Moves.InsertRange(insertAt, block);

            // Shift contour records that start after the insert point.
            for (int ci = 0; ci < sel.Layer.Contours.Count; ci++)
            {
                var c = sel.Layer.Contours[ci];
                if (c.Start >= insertAt)
                    sel.Layer.Contours[ci] = c with { Start = c.Start + block.Count };
            }
            // Record new contour for the inserted block (extrudes only region).
            int extrudeStart = insertAt;
            while (extrudeStart < insertAt + block.Count
                   && sel.Layer.Moves[extrudeStart].Kind != MoveKind.Extrude)
                extrudeStart++;
            int extrudeCount = 0;
            for (int i = extrudeStart; i < insertAt + block.Count; i++)
                if (sel.Layer.Moves[i].Kind == MoveKind.Extrude) extrudeCount++;
                else if (extrudeCount > 0) break;
            if (extrudeCount > 0)
                sel.Layer.Contours.Add(new ContourSpan(
                    extrudeStart, extrudeCount, Closed: false, EntryTravelIndex: -1));

            // Highlight of the first offset poly for the list entry.
            var world = new List<TkVector3>();
            if (polylines[0].Count >= 2)
            {
                foreach (var p in polylines[0])
                {
                    var w = TransformPoint(new TkVector3(
                        p.X - sel.Origin.X, p.Y - sel.Origin.Y, p.Z - sel.Origin.Z), sel.Wt);
                    world.Add(new TkVector3(w.X, w.Y, w.Z));
                }
            }

            int layerNum = sel.Layer.Index + 1;
            var rec = new PaintModificationRecord
            {
                Id = Guid.NewGuid(),
                Kind = Core.Models.PaintMarkKind.Offset,
                Layer = sel.Layer,
                Span = sel.Span,
                Origin = sel.Origin,
                Wt = sel.Wt,
                World = world.Count > 0 ? world : new List<TkVector3>(sel.World),
                MarkCenters = [],
                Title = $"Offset · Layer {layerNum}",
                Detail = $"{distance:0.##} mm · ×{count} · {side} · {polylines.Count} path(s)",
                IsExpanded = false,
                SupportType = "Formbound Buttress",
            };
            _paintModifications.Add(rec);
        }

        if (pathsAdded == 0)
        {
            LogPaintConsole("[edit] Offset path produced no geometry (need ≥2 points per path).");
            return;
        }

        // Re-upload so the GPU/scrub match the mutated toolpath.
        _toolpathByNode.TryGetValue(node, out _);
        if (!_toolpathMetaByNode.TryGetValue(node, out var meta))
            meta = (6f, 3f, default);
        _rawToolpathByNode.TryGetValue(node, out var raw);
        var preservedLocal = node.LocalTransform;
        if (!_toolpathOriginByNode.TryGetValue(node, out var preservedOrigin))
        {
            preservedOrigin = new NVec3(
                preservedLocal.M41, preservedLocal.M42, preservedLocal.M43);
        }
        vm.PendingToolpathReplace.Enqueue(new PendingToolpathEntry
        {
            Toolpath = toolpath,
            RawToolpath = raw ?? toolpath,
            Node = node,
            BeadWidth = meta.BeadWidth > 0 ? meta.BeadWidth : 6f,
            LayerHeight = meta.LayerHeight > 0 ? meta.LayerHeight : 3f,
            MaterialColor = meta.MaterialColor,
            PreserveRelativePose = true,
            PreservedLocalTransform = preservedLocal,
            PreservedOrigin = preservedOrigin,
        });

        int newMax = toolpath.Layers.Sum(l => l.Moves.Count);
        vm.ResetScrubIndex(newMax, toolpath, preservePosition: true);
        vm.IsScrubSessionActive = true;
        SyncPaintModificationsUi(vm);
        vm.MarkWorkspaceDirty?.Invoke();
        LogPaintConsole(
            $"[edit] Offset path → {pathsAdded} path(s), {segsAdded} segments "
            + $"({distance:0.##} mm ×{count}, {side})");
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Toolbar Support/Remove: lays marks along EVERY selected path and records
    /// them as one MODIFICATIONS card. Shift multi-select becomes a group so
    /// Support type / side / delete apply to all members together.
    /// </summary>
    private void ApplyPaintSelection(ViewportViewModel vm, bool support)
    {
        if (vm.AdditiveSettings is not { } add || _paintSelection.Count == 0) return;
        var kind = support ? Core.Models.PaintMarkKind.Bridge : Core.Models.PaintMarkKind.Remove;
        int added = 0;
        // Collapse existing cards so newly applied ones stand out.
        foreach (var existing in _paintModifications)
            existing.IsExpanded = false;
        var applyStyle = support
            ? Core.Models.PaintSupportStyleUtil.FromLabel(vm.PaintSupportType)
            : Core.Models.PaintSupportStyle.FormboundButtress;
        var applyStyleLabel = Core.Models.PaintSupportStyleUtil.ToLabel(applyStyle);
        // Default new Formbound selections to Inside; user can flip per-mod later.
        var applySide = Core.Models.PaintSupportSide.Inside;
        var applySideLabel = Core.Models.PaintSupportSideUtil.ToLabel(applySide);

        var members = new List<PaintModMember>(_paintSelection.Count);
        var allCenters = new List<System.Numerics.Vector3>();
        var allWorld = new List<TkVector3>();
        var role = support
            ? Core.Models.PaintBridgeRole.SupportBar
            : Core.Models.PaintBridgeRole.None;

        foreach (var sel in _paintSelection)
        {
            var centers = new List<System.Numerics.Vector3>();
            int n = MarkPickedSpanDabs(add, sel.Layer, sel.Span, kind, centers, role, applyStyle, applySide);
            added += n;
            var world = new List<TkVector3>(sel.World);
            members.Add(new PaintModMember
            {
                Layer = sel.Layer,
                Span = sel.Span,
                Origin = sel.Origin,
                Wt = sel.Wt,
                World = world,
                MarkCenters = centers,
            });
            allCenters.AddRange(centers);
            allWorld.AddRange(world);
        }

        var primary = members[0];
        string kindLabel = support ? "Support" : "Remove";
        var rec = new PaintModificationRecord
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Layer = primary.Layer,
            Span = primary.Span,
            Origin = primary.Origin,
            Wt = primary.Wt,
            World = allWorld.Count > 0 ? allWorld : new List<TkVector3>(primary.World),
            MarkCenters = allCenters,
            Title = kindLabel, // RefreshModificationLabels fills title/detail
            Detail = "",
            IsExpanded = true,
            SupportType = support ? applyStyleLabel
                : Core.Models.PaintSupportStyleUtil.LabelButtress,
            SupportSide = support ? applySideLabel
                : Core.Models.PaintSupportSideUtil.LabelInside,
        };
        rec.Members.AddRange(members);
        RefreshModificationLabels(rec);
        _paintModifications.Add(rec);

        // Re-tint highlights to the action colour so the state reads at a glance.
        var col = support ? new TkVector3(0.2f, 0.9f, 1f) : new TkVector3(1f, 0.45f, 0.15f);
        for (int i = 0; i < _paintMultiLines.Count; i++)
            _paintMultiLines[i] = (_paintMultiLines[i].Pts, col);
        _paintSelectedColor = col;
        if (added > 0) add.BumpPaintStamp();
        SyncPaintModificationsUi(vm);
        vm.MarkWorkspaceDirty?.Invoke();
        int treeN = add.PaintMarks.Count(m =>
            m.Kind == Core.Models.PaintMarkKind.Bridge
            && m.SupportStyle == Core.Models.PaintSupportStyle.Tree);
        int formN = add.PaintMarks.Count(m =>
            m.Kind == Core.Models.PaintMarkKind.Bridge
            && Core.Models.PaintSupportStyleUtil.IsFormbound(m.SupportStyle));
        LogPaintConsole($"[edit] {(support ? "support" : "remove")} applied → "
            + $"group of {members.Count} path(s), {added} mark(s), 1 modification"
            + (support ? $" · paint tree={treeN} formbound={formN}" : ""));
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>Lays Bridge/Remove dabs along one picked span (raw toolpath coords).
    /// Returns how many marks were added; <paramref name="centersOut"/> receives their centres.
    /// Support-bar Bridge dabs are denser so Formbound can open a full-width T.</summary>
    private int MarkPickedSpanDabs(
        MassiveSlicer.ViewModels.AdditiveSettingsViewModel add,
        ToolpathLayer layer, ContourSpan span, Core.Models.PaintMarkKind kind,
        List<System.Numerics.Vector3>? centersOut = null,
        Core.Models.PaintBridgeRole bridgeRole = Core.Models.PaintBridgeRole.None,
        Core.Models.PaintSupportStyle supportStyle = Core.Models.PaintSupportStyle.FormboundButtress,
        Core.Models.PaintSupportSide supportSide = Core.Models.PaintSupportSide.Inside)
    {
        float bead = (float)add.BeadWidth;
        if (bead < 0.5f) bead = 6f;
        float dabRadius = kind == Core.Models.PaintMarkKind.Bridge ? bead * 1.5f : bead * 1.2f;
        // Column foot: one mark at geometric mid of the target span (single mouth).
        if (kind == Core.Models.PaintMarkKind.Bridge
            && bridgeRole == Core.Models.PaintBridgeRole.ColumnFoot)
        {
            var mid = SpanCenterRaw(layer, span);
            if (TryAddPaintMark(add, mid, dabRadius, kind,
                    Core.Models.PaintBridgeRole.ColumnFoot, supportStyle, supportSide))
            {
                centersOut?.Add(mid);
                return 1;
            }
            return 0;
        }

        var role = kind == Core.Models.PaintMarkKind.Bridge
            ? (bridgeRole == Core.Models.PaintBridgeRole.None
                ? Core.Models.PaintBridgeRole.SupportBar
                : bridgeRole)
            : Core.Models.PaintBridgeRole.None;
        // Target Support Selections: dense dabs (sub-bead) so Formbound can cover
        // the entire selected line, not sparse midpoints only.
        bool aggressive = add.LightningTargetSupportSelections
                          && kind == Core.Models.PaintMarkKind.Bridge;
        float spacing = aggressive ? MathF.Max(1.5f, bead * 0.4f) : bead * 1.5f;
        float accum = spacing; // force a dab on the first extrude
        int added = 0;
        System.Numerics.Vector3? firstMid = null, lastMid = null;
        for (int i = 0; i < span.Count && span.Start + i < layer.Moves.Count; i++)
        {
            var mv = layer.Moves[span.Start + i];
            if (mv.Kind != MoveKind.Extrude || mv.IsWipe) continue;
            float segLen = System.Numerics.Vector3.Distance(mv.From, mv.To);
            firstMid ??= (mv.From + mv.To) * 0.5f;
            lastMid = (mv.From + mv.To) * 0.5f;

            if (aggressive && segLen > spacing * 0.5f)
            {
                // Walk the segment so long beads still get multiple support samples.
                int n = Math.Max(1, (int)MathF.Ceiling(segLen / spacing));
                for (int k = 0; k <= n; k++)
                {
                    float t = k / (float)n;
                    var p = mv.From + (mv.To - mv.From) * t;
                    if (TryAddPaintMark(add, p, dabRadius, kind, role, supportStyle, supportSide))
                    {
                        centersOut?.Add(p);
                        added++;
                    }
                }
                accum = 0f;
                continue;
            }

            accum += segLen;
            if (accum < spacing) continue;
            accum = 0f;
            var mid = (mv.From + mv.To) * 0.5f;
            if (TryAddPaintMark(add, mid, dabRadius, kind, role, supportStyle, supportSide))
            {
                centersOut?.Add(mid);
                added++;
            }
        }
        if (firstMid is { } f && TryAddPaintMark(add, f, dabRadius, kind, role, supportStyle, supportSide))
        { centersOut?.Add(f); added++; }
        if (lastMid is { } l
            && System.Numerics.Vector3.Distance(l, firstMid ?? l) > dabRadius * 0.25f
            && TryAddPaintMark(add, l, dabRadius, kind, role, supportStyle, supportSide))
        { centersOut?.Add(l); added++; }
        return added;
    }

    private static bool TryAddPaintMark(
        MassiveSlicer.ViewModels.AdditiveSettingsViewModel add,
        System.Numerics.Vector3 mid, float dabRadius, Core.Models.PaintMarkKind kind,
        Core.Models.PaintBridgeRole bridgeRole = Core.Models.PaintBridgeRole.None,
        Core.Models.PaintSupportStyle supportStyle = Core.Models.PaintSupportStyle.FormboundButtress,
        Core.Models.PaintSupportSide supportSide = Core.Models.PaintSupportSide.Inside)
    {
        // Same-center collision: restyle in place. Previously we returned false and
        // left Formbound dabs when the user re-applied as Tree Support — the slicer
        // then grew short Formbound columns while the UI said "Tree Support".
        for (int i = 0; i < add.PaintMarks.Count; i++)
        {
            var m = add.PaintMarks[i];
            if (m.Kind != kind) continue;
            if (System.Numerics.Vector3.Distance(m.Center, mid) >= dabRadius * 0.5f) continue;
            if (m.BridgeRole == bridgeRole
                && m.SupportStyle == supportStyle
                && m.SupportSide == supportSide
                && MathF.Abs(m.Radius - dabRadius) < 0.05f)
                return false; // identical — no change
            add.PaintMarks[i] = m with
            {
                Radius = dabRadius,
                BridgeRole = bridgeRole,
                SupportStyle = supportStyle,
                SupportSide = supportSide,
            };
            return true;
        }
        add.PaintMarks.Add(new Core.Models.PaintMark(
            mid, dabRadius, kind, bridgeRole, supportStyle, supportSide));
        return true;
    }

    private void SyncPaintModificationsUi(ViewportViewModel vm)
    {
        var rows = _paintModifications.Select(m => new ViewportViewModel.PaintModRow(
            m.Id,
            m.Kind == Core.Models.PaintMarkKind.Bridge,
            m.Kind == Core.Models.PaintMarkKind.Offset,
            m.Title,
            m.Detail,
            FormatGroupAnchorSummary(m),
            m.HasBridgeTarget,
            m.TargetLayer is { } tl && m.TargetSpan is { } ts
                ? FormatAnchorSummary(tl, ts)
                : "",
            m.ScaffoldLayerCount,
            m.ScaffoldMarkCenters.Count,
            m.IsExpanded,
            m.SupportType,
            m.SupportSide)).ToList();
        vm.SetPaintModifications(rows);
    }

    /// <summary>
    /// Revise support style on a modification: re-stamp that mod's paint marks only
    /// (does not change other areas). Soft-syncs FILL PATTERN for Formbound styles.
    /// </summary>
    private void SetPaintModificationSupportType(ViewportViewModel vm, Guid id, string type)
    {
        var rec = _paintModifications.FirstOrDefault(m => m.Id == id);
        if (rec is null || rec.Kind != Core.Models.PaintMarkKind.Bridge) return;
        var style = Core.Models.PaintSupportStyleUtil.FromLabel(type);

        // Structural Support is a toolpath modifier, not a mark style: convert the
        // group — spec anchored at the group's anchor point. The card STAYS in
        // MODIFICATIONS (its marks are retired; the pocket settings render inline).
        if (style == Core.Models.PaintSupportStyle.StructuralSupport)
        {
            if (rec.StructuralIndex >= 0) return; // already converted
            int specIdx = ConvertModificationToStructuralSupport(vm, rec);
            if (specIdx < 0) return;
            if (vm.AdditiveSettings is { } addS)
            {
                int stripped = RemoveMarksNearCenters(addS, rec.Kind, rec.MarkCenters);
                stripped += RemoveMarksNearCenters(addS, rec.Kind, rec.TargetMarkCenters);
                stripped += RemoveMarksNearCenters(addS, rec.Kind, rec.ScaffoldMarkCenters);
                if (stripped > 0) addS.BumpPaintStamp();
            }
            rec.SupportType = Core.Models.PaintSupportStyleUtil.LabelStructural;
            rec.StructuralIndex = specIdx;
            if (vm.AdditiveSettings is { } addName)
                rec.Title = addName.SupportNameAt(specIdx);
            rec.Detail = "pocket settings below · Update Slice to bake";
            rec.IsExpanded = true;
            vm.MarkWorkspaceDirty?.Invoke();
            SyncPaintModificationsUi(vm);
            return;
        }
        var v = Core.Models.PaintSupportStyleUtil.ToLabel(style);
        if (string.Equals(rec.SupportType, v, StringComparison.Ordinal)) return;

        // Leaving Structural: retire the spec; the card reverts to a mark-style
        // support (re-Apply the selection to stamp fresh marks).
        if (rec.StructuralIndex >= 0 && vm.AdditiveSettings is { } addOld)
        {
            RemoveStructuralSpec(vm, addOld, rec.StructuralIndex);
            rec.StructuralIndex = -1;
            LogPaintConsole("[support] structural spec removed — re-Apply the selection to stamp "
                + $"{v} marks, then Update Slice.");
            vm.UpdateSliceCommand?.Execute(null);
        }
        rec.SupportType = v;

        // Re-stamp SupportStyle on this mod's marks (match by center).
        if (vm.AdditiveSettings is { } add)
        {
            for (int i = 0; i < add.PaintMarks.Count; i++)
            {
                var m = add.PaintMarks[i];
                if (m.Kind != Core.Models.PaintMarkKind.Bridge) continue;
                bool hit = rec.MarkCenters.Any(c =>
                    System.Numerics.Vector3.Distance(c, m.Center) < MathF.Max(m.Radius, 0.5f) * 1.1f);
                if (!hit) continue;
                add.PaintMarks[i] = m with { SupportStyle = style };
            }
            // Also re-stamp ColumnFoot marks from bridge target for this mod.
            foreach (var tc in rec.TargetMarkCenters)
            {
                for (int i = 0; i < add.PaintMarks.Count; i++)
                {
                    var m = add.PaintMarks[i];
                    if (m.Kind != Core.Models.PaintMarkKind.Bridge) continue;
                    if (System.Numerics.Vector3.Distance(tc, m.Center) < MathF.Max(m.Radius, 0.5f) * 1.1f)
                        add.PaintMarks[i] = m with { SupportStyle = style };
                }
            }
            add.BumpPaintStamp();
        }

        vm.PaintSupportType = v;
        vm.ApplyPaintSupportTypeToSettings();
        vm.MarkWorkspaceDirty?.Invoke();
        LogPaintConsole($"[edit] modification support type → {v} (reslice to bake)");
        SyncPaintModificationsUi(vm);
    }

    /// <summary>
    /// Revise Inside/Outside wall side on a Formbound modification; re-stamps marks.
    /// </summary>
    private void SetPaintModificationSupportSide(ViewportViewModel vm, Guid id, string side)
    {
        var rec = _paintModifications.FirstOrDefault(m => m.Id == id);
        if (rec is null || rec.Kind != Core.Models.PaintMarkKind.Bridge) return;
        var sideEnum = Core.Models.PaintSupportSideUtil.FromLabel(side);
        var v = Core.Models.PaintSupportSideUtil.ToLabel(sideEnum);
        if (string.Equals(rec.SupportSide, v, StringComparison.Ordinal)) return;
        rec.SupportSide = v;

        if (vm.AdditiveSettings is { } add)
        {
            for (int i = 0; i < add.PaintMarks.Count; i++)
            {
                var m = add.PaintMarks[i];
                if (m.Kind != Core.Models.PaintMarkKind.Bridge) continue;
                bool hit = rec.MarkCenters.Any(c =>
                    System.Numerics.Vector3.Distance(c, m.Center) < MathF.Max(m.Radius, 0.5f) * 1.1f);
                if (!hit) continue;
                add.PaintMarks[i] = m with { SupportSide = sideEnum };
            }
            foreach (var tc in rec.TargetMarkCenters)
            {
                for (int i = 0; i < add.PaintMarks.Count; i++)
                {
                    var m = add.PaintMarks[i];
                    if (m.Kind != Core.Models.PaintMarkKind.Bridge) continue;
                    if (System.Numerics.Vector3.Distance(tc, m.Center) < MathF.Max(m.Radius, 0.5f) * 1.1f)
                        add.PaintMarks[i] = m with { SupportSide = sideEnum };
                }
            }
            add.BumpPaintStamp();
        }

        vm.MarkWorkspaceDirty?.Invoke();
        LogPaintConsole($"[edit] modification wall side → {v} (reslice to bake)");
        SyncPaintModificationsUi(vm);
    }

    /// <summary>Serialize MODIFICATIONS for workspace save.</summary>
    private List<Core.Models.WorkspacePaintModification> CapturePaintModificationsState()
    {
        static List<float[]> Pts(IEnumerable<System.Numerics.Vector3> src) =>
            src.Select(p => new[] { p.X, p.Y, p.Z }).ToList();
        static List<float[]> WorldPts(IEnumerable<TkVector3> src) =>
            src.Select(p => new[] { p.X, p.Y, p.Z }).ToList();

        return _paintModifications.Select(m =>
        {
            var dto = new Core.Models.WorkspacePaintModification
            {
                Id = m.Id,
                Kind = m.Kind.ToString(),
                LayerIndex = m.Layer.Index,
                LayerZ = m.Layer.Z,
                SpanStart = m.Span.Start,
                SpanCount = m.Span.Count,
                SpanClosed = m.Span.Closed,
                SpanEntryTravelIndex = m.Span.EntryTravelIndex,
                MarkCenters = Pts(m.MarkCenters),
                Title = m.Title,
                Detail = m.Detail,
                IsExpanded = m.IsExpanded,
                SupportType = m.SupportType,
                SupportSide = m.SupportSide,
                StructuralIndex = m.StructuralIndex,
                WorldPoints = WorldPts(m.World),
                ScaffoldLayerCount = m.ScaffoldLayerCount,
                ScaffoldMarkCenters = Pts(m.ScaffoldMarkCenters),
            };
            if (m.Members.Count > 0)
            {
                dto.Members = m.Members.Select(mem => new Core.Models.WorkspacePaintModMember
                {
                    LayerIndex = mem.Layer.Index,
                    LayerZ = mem.Layer.Z,
                    SpanStart = mem.Span.Start,
                    SpanCount = mem.Span.Count,
                    SpanClosed = mem.Span.Closed,
                    SpanEntryTravelIndex = mem.Span.EntryTravelIndex,
                    MarkCenters = Pts(mem.MarkCenters),
                    WorldPoints = WorldPts(mem.World),
                }).ToList();
            }
            if (m.TargetLayer is { } tl && m.TargetSpan is { } ts)
            {
                dto.TargetLayerIndex = tl.Index;
                dto.TargetLayerZ = tl.Z;
                dto.TargetSpanStart = ts.Start;
                dto.TargetSpanCount = ts.Count;
                dto.TargetSpanClosed = ts.Closed;
                dto.TargetSpanEntryTravelIndex = ts.EntryTravelIndex;
                dto.TargetMarkCenters = Pts(m.TargetMarkCenters);
                if (m.TargetWorld is { } tw)
                    dto.TargetWorldPoints = WorldPts(tw);
            }
            return dto;
        }).ToList();
    }

    /// <summary>Rebuild MODIFICATIONS after workspace load when toolpaths are ready.</summary>
    private void RestorePaintModificationsState(
        IReadOnlyList<Core.Models.WorkspacePaintModification> saved)
    {
        _paintModifications.Clear();
        if (saved.Count == 0)
        {
            if (DataContext is ViewportViewModel emptyVm)
                SyncPaintModificationsUi(emptyVm);
            return;
        }

        // Index layers by Index and by Z for robust rebinding after re-slice.
        var byIndex = new Dictionary<int, ToolpathLayer>();
        var byZ = new List<(float Z, ToolpathLayer Layer)>();
        foreach (var (node, tp) in _toolpathByNode)
        {
            if (!node.Visible && !ReferenceEquals(node, _activeScrubNode)) continue;
            foreach (var layer in tp.Layers)
            {
                byIndex[layer.Index] = layer;
                byZ.Add((layer.Z, layer));
            }
        }

        ToolpathLayer? FindLayer(int index, float z)
        {
            if (byIndex.TryGetValue(index, out var exact)) return exact;
            ToolpathLayer? best = null;
            float bestD = float.MaxValue;
            foreach (var (lz, layer) in byZ)
            {
                float d = MathF.Abs(lz - z);
                if (d < bestD) { bestD = d; best = layer; }
            }
            return bestD < 2f ? best : null;
        }

        static ContourSpan MakeSpan(int start, int count, bool closed, int entry) =>
            new(start, Math.Max(1, count), closed, entry);

        static List<System.Numerics.Vector3> ReadPts(List<float[]>? src)
        {
            var list = new List<System.Numerics.Vector3>();
            if (src is null) return list;
            foreach (var a in src)
            {
                if (a is { Length: >= 3 })
                    list.Add(new System.Numerics.Vector3(a[0], a[1], a[2]));
            }
            return list;
        }

        static List<TkVector3> ReadWorld(List<float[]>? src)
        {
            var list = new List<TkVector3>();
            if (src is null) return list;
            foreach (var a in src)
            {
                if (a is { Length: >= 3 })
                    list.Add(new TkVector3(a[0], a[1], a[2]));
            }
            return list;
        }

        System.Numerics.Vector3 origin = default;
        var wt = TkMatrix4.Identity;
        foreach (var (node, _) in _toolpathByNode)
        {
            _toolpathOriginByNode.TryGetValue(node, out origin);
            wt = node.LocalTransform;
            break;
        }

        foreach (var s in saved)
        {
            var layer = FindLayer(s.LayerIndex, s.LayerZ);
            if (layer is null) continue;
            var span = MakeSpan(s.SpanStart, s.SpanCount, s.SpanClosed, s.SpanEntryTravelIndex);
            // Clamp span into layer.
            if (span.Start >= layer.Moves.Count) continue;
            int count = Math.Min(span.Count, layer.Moves.Count - span.Start);
            span = new ContourSpan(span.Start, Math.Max(1, count), span.Closed, span.EntryTravelIndex);

            var kind = string.Equals(s.Kind, "Remove", StringComparison.OrdinalIgnoreCase)
                ? Core.Models.PaintMarkKind.Remove
                : Core.Models.PaintMarkKind.Bridge;

            var world = ReadWorld(s.WorldPoints);
            if (world.Count == 0)
                world = SpanWorldHighlight((layer, span, origin, wt),
                    pointMode: span.Count <= 1);

            var rec = new PaintModificationRecord
            {
                Id = s.Id == Guid.Empty ? Guid.NewGuid() : s.Id,
                Kind = kind,
                Layer = layer,
                Span = span,
                Origin = origin,
                Wt = wt,
                World = world,
                MarkCenters = ReadPts(s.MarkCenters),
                Title = s.Title,
                Detail = s.Detail,
                IsExpanded = s.IsExpanded,
                ScaffoldLayerCount = s.ScaffoldLayerCount,
                SupportType = string.IsNullOrWhiteSpace(s.SupportType)
                    ? "Formbound Buttress"
                    : s.SupportType,
                SupportSide = string.IsNullOrWhiteSpace(s.SupportSide)
                    ? Core.Models.PaintSupportSideUtil.LabelInside
                    : s.SupportSide,
                StructuralIndex = s.StructuralIndex,
            };
            rec.ScaffoldMarkCenters.AddRange(ReadPts(s.ScaffoldMarkCenters));

            // Restore group members (Shift multi-select). Fall back to primary only.
            if (s.Members is { Count: > 0 })
            {
                foreach (var sm in s.Members)
                {
                    var mLayer = FindLayer(sm.LayerIndex, sm.LayerZ);
                    if (mLayer is null) continue;
                    var mSpan = MakeSpan(sm.SpanStart, sm.SpanCount, sm.SpanClosed, sm.SpanEntryTravelIndex);
                    if (mSpan.Start >= mLayer.Moves.Count) continue;
                    int mCount = Math.Min(mSpan.Count, mLayer.Moves.Count - mSpan.Start);
                    mSpan = new ContourSpan(mSpan.Start, Math.Max(1, mCount), mSpan.Closed, mSpan.EntryTravelIndex);
                    var mWorld = ReadWorld(sm.WorldPoints);
                    if (mWorld.Count == 0)
                        mWorld = SpanWorldHighlight((mLayer, mSpan, origin, wt),
                            pointMode: mSpan.Count <= 1);
                    rec.Members.Add(new PaintModMember
                    {
                        Layer = mLayer,
                        Span = mSpan,
                        Origin = origin,
                        Wt = wt,
                        World = mWorld,
                        MarkCenters = ReadPts(sm.MarkCenters),
                    });
                }
                // If members restored, rebuild union MarkCenters/World when empty.
                if (rec.Members.Count > 0 && rec.MarkCenters.Count == 0)
                {
                    foreach (var mem in rec.Members)
                        rec.MarkCenters.AddRange(mem.MarkCenters);
                }
            }
            else
            {
                // Legacy single: seed Members so EnumerateMembers stays consistent.
                rec.Members.Add(new PaintModMember
                {
                    Layer = layer,
                    Span = span,
                    Origin = origin,
                    Wt = wt,
                    World = world,
                    MarkCenters = rec.MarkCenters,
                });
            }

            if (s.TargetLayerIndex is int tli && s.TargetLayerZ is float tlz
                && s.TargetSpanStart is int tss && s.TargetSpanCount is int tsc)
            {
                var tLayer = FindLayer(tli, tlz);
                if (tLayer is not null)
                {
                    var tSpan = MakeSpan(tss, tsc, s.TargetSpanClosed, s.TargetSpanEntryTravelIndex);
                    if (tSpan.Start < tLayer.Moves.Count)
                    {
                        tSpan = new ContourSpan(tSpan.Start,
                            Math.Min(tSpan.Count, tLayer.Moves.Count - tSpan.Start),
                            tSpan.Closed, tSpan.EntryTravelIndex);
                        rec.TargetLayer = tLayer;
                        rec.TargetSpan = tSpan;
                        rec.TargetOrigin = origin;
                        rec.TargetWt = wt;
                        rec.TargetWorld = ReadWorld(s.TargetWorldPoints);
                        if (rec.TargetWorld.Count == 0)
                            rec.TargetWorld = SpanWorldHighlight(
                                (tLayer, tSpan, origin, wt), pointMode: tSpan.Count <= 1);
                        rec.TargetMarkCenters.AddRange(ReadPts(s.TargetMarkCenters));
                    }
                }
            }

            RefreshModificationLabels(rec);
            _paintModifications.Add(rec);
        }

        if (DataContext is ViewportViewModel vm)
        {
            SyncPaintModificationsUi(vm);
            LogPaintConsole(
                $"[workspace] restored {_paintModifications.Count} paint modification(s)");
        }
    }

    private static string FormatAnchorSummary(ToolpathLayer layer, ContourSpan span)
    {
        bool isPoint = span.Count <= 1;
        string kind = isPoint ? "Point" : "Path";
        return $"Layer {layer.Index + 1} · {kind} · Z {layer.Z:0.#} · m{span.Start}+{span.Count}";
    }

    private static string FormatGroupAnchorSummary(PaintModificationRecord rec)
    {
        if (!rec.IsGroup)
            return FormatAnchorSummary(rec.Layer, rec.Span);
        var layers = rec.Members
            .Select(m => m.Layer.Index + 1)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        string layerTxt = layers.Count <= 4
            ? string.Join(", ", layers.Select(n => $"L{n}"))
            : $"L{layers.First()}…L{layers.Last()}";
        return $"Group · {rec.MemberCount} paths · {layerTxt}";
    }

    private static void RefreshModificationLabels(PaintModificationRecord rec)
    {
        string kindLabel = rec.Kind switch
        {
            Core.Models.PaintMarkKind.Bridge => "Support",
            Core.Models.PaintMarkKind.Offset => "Offset",
            _ => "Remove",
        };
        int marks = rec.MarkCenters.Count + rec.TargetMarkCenters.Count + rec.ScaffoldMarkCenters.Count;

        if (rec.IsGroup)
        {
            var layers = rec.Members
                .Select(m => m.Layer.Index + 1)
                .Distinct()
                .OrderBy(n => n)
                .ToList();
            string layerTxt = layers.Count <= 3
                ? string.Join(",", layers.Select(n => $"L{n}"))
                : $"L{layers.First()}–L{layers.Last()}";
            rec.Title = $"{kindLabel} · Group · {rec.MemberCount}";
            rec.Detail = $"{layerTxt}"
                + (marks > 0 ? $" · {marks} mark(s)" : "")
                + (rec.Kind == Core.Models.PaintMarkKind.Bridge && !string.IsNullOrWhiteSpace(rec.SupportType)
                    ? $" · {rec.SupportType}"
                    : "");
            if (rec.HasBridgeTarget && rec.TargetLayer is { } tlg)
                rec.Detail += $" · bridge→L{tlg.Index + 1}";
            return;
        }

        int layerNum = rec.Layer.Index + 1;
        bool isPoint = rec.Span.Count <= 1;
        string section = isPoint ? "Point" : "Path";

        if (rec.HasBridgeTarget && rec.TargetLayer is { } tl && rec.TargetSpan is not null)
        {
            rec.Title = $"{kindLabel} · L{layerNum} → L{tl.Index + 1}";
            rec.Detail = $"{section} · Z {rec.Layer.Z:0.#}→{tl.Z:0.#}"
                + (rec.ScaffoldLayerCount > 0
                    ? $" · {rec.ScaffoldLayerCount} mid-layer(s)"
                    : "")
                + (marks > 0 ? $" · {marks} mark(s)" : "");
        }
        else
        {
            rec.Title = $"{kindLabel} · Layer {layerNum}";
            rec.Detail = $"{section} · Z {rec.Layer.Z:0.#} · m{rec.Span.Start}+{rec.Span.Count}"
                + (rec.MarkCenters.Count > 0 ? $" · {rec.MarkCenters.Count} mark(s)" : "");
        }
    }

    /// <summary>Toggle expand + reselect so the user can inspect options.</summary>
    private void TogglePaintModificationExpand(ViewportViewModel vm, Guid id)
    {
        var rec = _paintModifications.FirstOrDefault(m => m.Id == id);
        if (rec is null) return;
        rec.IsExpanded = !rec.IsExpanded;
        // Collapse siblings so only one card is open at a time (cleaner panel).
        if (rec.IsExpanded)
        {
            foreach (var other in _paintModifications)
                if (other.Id != id) other.IsExpanded = false;
            // Structural card: point the inline settings at this card's spec.
            if (rec.StructuralIndex >= 0 && vm.AdditiveSettings is { } add)
                add.SelectedSupportIndex = rec.StructuralIndex;
        }
        ReselectPaintModification(vm, id);
        SyncPaintModificationsUi(vm);
    }

    /// <summary>Restores the selection for a stored modification so it can be edited.
    /// Groups reselect every member so Create Mod / re-Apply hit the whole set.</summary>
    private void ReselectPaintModification(ViewportViewModel vm, Guid id)
    {
        var rec = _paintModifications.FirstOrDefault(m => m.Id == id);
        if (rec is null) return;

        _paintSelection.Clear();
        _paintMultiLines.Clear();
        var col = rec.Kind == Core.Models.PaintMarkKind.Bridge
            ? new TkVector3(0.2f, 0.9f, 1f)
            : new TkVector3(1f, 0.45f, 0.15f);

        // Restore every group member (or the single primary span).
        foreach (var mem in rec.EnumerateMembers())
        {
            var w = new List<TkVector3>(mem.World);
            _paintSelection.Add((mem.Layer, mem.Span, mem.Origin, mem.Wt, w));
        }

        // Bridge target (extra anchor) after group members.
        if (rec.TargetWorld is { Count: > 0 } tw
            && rec.TargetLayer is { } tLayer
            && rec.TargetSpan is { } tSpan)
        {
            _paintMultiLines.Add((new List<TkVector3>(tw), col));
            _paintSelection.Add((tLayer, tSpan, rec.TargetOrigin, rec.TargetWt,
                new List<TkVector3>(tw)));
        }

        // Sticky highlight = last selection; multi = earlier members.
        _paintMultiLines.Clear();
        for (int i = 0; i < _paintSelection.Count - 1; i++)
            _paintMultiLines.Add((new List<TkVector3>(_paintSelection[i].World), col));
        var sticky = _paintSelection.Count > 0
            ? new List<TkVector3>(_paintSelection[^1].World)
            : new List<TkVector3>(rec.World);
        _paintSelectedLine = sticky;
        _paintHoverLine = sticky;
        _paintSelectedColor = col;
        _paintSelectedId = null;
        vm.PaintModificationMode = rec.Kind == Core.Models.PaintMarkKind.Bridge ? "Support" : "Remove";
        if (rec.Kind == Core.Models.PaintMarkKind.Bridge)
        {
            vm.PaintSupportType = rec.SupportType;
            vm.ApplyPaintSupportTypeToSettings();
        }
        // Structural card: reselecting it also makes its pocket the live support, so the
        // STRUCTURAL SUPPORT panel and the gizmo both follow the card you just clicked.
        if (rec.StructuralIndex >= 0)
            SelectStructuralSupport(vm, rec.StructuralIndex);

        // Jump timeline / LAYERS dual-slider to the anchor layer(s) so the
        // reselected paths are inside the visible layer window.
        int loLayer = int.MaxValue, hiLayer = int.MinValue;
        foreach (var mem in rec.EnumerateMembers())
        {
            int li = mem.Layer.Index;
            if (li < loLayer) loLayer = li;
            if (li > hiLayer) hiLayer = li;
        }
        // Include bridge target layer if present (second anchor may be elsewhere).
        if (rec.TargetLayer is { } bridgeLayer)
        {
            int bli = bridgeLayer.Index;
            if (bli < loLayer) loLayer = bli;
            if (bli > hiLayer) hiLayer = bli;
        }
        if (loLayer <= hiLayer)
            vm.FocusScrubOnLayers(loLayer, hiLayer);

        SyncPaintSelectionUi(vm);
        LogPaintConsole($"[edit] reselected modification: {rec.Title} · {rec.Detail}"
            + (rec.IsGroup ? $" ({rec.MemberCount} paths)" : "")
            + (loLayer <= hiLayer
                ? $" · scrub L{loLayer + 1}" + (loLayer != hiLayer ? $"–L{hiLayer + 1}" : "")
                : ""));
        GlCanvas.RequestNextFrameRendering();
    }

    private void BeginPickBridgeTarget(ViewportViewModel vm, Guid id)
    {
        var rec = _paintModifications.FirstOrDefault(m => m.Id == id);
        if (rec is null) return;
        if (rec.Kind != Core.Models.PaintMarkKind.Bridge)
        {
            LogPaintConsole("[edit] bridge targets are only for Support modifications");
            return;
        }

        // Toggle off if already picking this mod.
        if (vm.PaintBridgePickModificationId == id)
        {
            vm.PaintBridgePickModificationId = null;
            LogPaintConsole("[edit] bridge target pick cancelled");
            return;
        }

        rec.IsExpanded = true;
        vm.PaintBridgePickModificationId = id;
        // Clear other tools so a click is a clean path pick.
        vm.PaintHandActive = false;
        vm.PaintBoxSelectActive = false;
        vm.PaintBridgeActive = false;
        vm.PaintRemoveActive = false;
        vm.PaintLineBridgeActive = false;
        vm.PaintLineRemoveActive = false;
        ReselectPaintModification(vm, id);
        SyncPaintModificationsUi(vm);
        LogPaintConsole("[edit] pick bridge target: click a path/point on another layer (Esc cancels)");
    }

    private void ClearBridgeTarget(ViewportViewModel vm, Guid id)
    {
        var rec = _paintModifications.FirstOrDefault(m => m.Id == id);
        if (rec is null || !rec.HasBridgeTarget) return;

        if (vm.AdditiveSettings is { } add)
        {
            int removed = RemoveMarksNearCenters(add, rec.Kind, rec.TargetMarkCenters);
            removed += RemoveMarksNearCenters(add, rec.Kind, rec.ScaffoldMarkCenters);
            if (removed > 0) add.BumpPaintStamp();
        }

        rec.TargetLayer = null;
        rec.TargetSpan = null;
        rec.TargetWorld = null;
        rec.TargetMarkCenters.Clear();
        rec.ScaffoldMarkCenters.Clear();
        rec.ScaffoldLayerCount = 0;
        RefreshModificationLabels(rec);
        if (vm.PaintBridgePickModificationId == id)
            vm.PaintBridgePickModificationId = null;
        ReselectPaintModification(vm, id);
        SyncPaintModificationsUi(vm);
        LogPaintConsole("[edit] bridge target cleared");
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Attach a second path/point as the bridge end of a Support modification and
    /// plant Formbound Bridge marks through every intermediate layer along the column.
    /// </summary>
    private void AttachBridgeTarget(
        ViewportViewModel vm, Guid id,
        ToolpathLayer targetLayer, ContourSpan targetSpan,
        System.Numerics.Vector3 origin, TkMatrix4 wt, List<TkVector3> world)
    {
        var rec = _paintModifications.FirstOrDefault(m => m.Id == id);
        if (rec is null) return;
        if (rec.Kind != Core.Models.PaintMarkKind.Bridge)
        {
            LogPaintConsole("[edit] bridge targets are only for Support modifications");
            vm.PaintBridgePickModificationId = null;
            return;
        }

        // Same span re-picked — ignore.
        if (ReferenceEquals(rec.Layer, targetLayer)
            && rec.Span.Start == targetSpan.Start && rec.Span.Count == targetSpan.Count)
        {
            LogPaintConsole("[edit] bridge target is the same as the anchor — pick a different path/point");
            return;
        }

        if (vm.AdditiveSettings is not { } add)
        {
            vm.PaintBridgePickModificationId = null;
            return;
        }

        // Drop previous target + scaffold dabs.
        RemoveMarksNearCenters(add, rec.Kind, rec.TargetMarkCenters);
        RemoveMarksNearCenters(add, rec.Kind, rec.ScaffoldMarkCenters);
        rec.TargetMarkCenters.Clear();
        rec.ScaffoldMarkCenters.Clear();

        rec.TargetLayer = targetLayer;
        rec.TargetSpan = targetSpan;
        rec.TargetOrigin = origin;
        rec.TargetWt = wt;
        rec.TargetWorld = new List<TkVector3>(world);

        // Single ColumnFoot at the mid of the target line — ONE perimeter mouth.
        // Support-bar marks on the upper selection already define full-width T.
        var targetCenters = new List<System.Numerics.Vector3>();
        MarkPickedSpanDabs(add, targetLayer, targetSpan, Core.Models.PaintMarkKind.Bridge,
            targetCenters, Core.Models.PaintBridgeRole.ColumnFoot);
        rec.TargetMarkCenters.AddRange(targetCenters);

        // No intermediate scaffold demand — inheritance carries one vertical column.
        int midLayers = GenerateScaffoldMarks(add, rec);
        rec.ScaffoldLayerCount = midLayers;
        rec.IsExpanded = true;
        RefreshModificationLabels(rec);

        add.BumpPaintStamp();
        vm.PaintBridgePickModificationId = null;
        ReselectPaintModification(vm, id);
        SyncPaintModificationsUi(vm);
        vm.MarkWorkspaceDirty?.Invoke();
        LogPaintConsole(
            $"[edit] bridge L{rec.Layer.Index + 1} → L{targetLayer.Index + 1}"
            + $" · {midLayers} intermediate layer(s)"
            + $" · {rec.ScaffoldMarkCenters.Count} scaffold mark(s) — reslice to bake Formbound");
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Bridge-target scaffolding: do <b>not</b> plant demand on intermediate layers.
    /// Intermediate ribbon marks caused Formbound to re-birth a new mouth every
    /// few layers (stop/start columns). The planner now births once under the
    /// painted selection and inherits the same tree downward via MaxStep.
    /// Target-end marks are planted separately by <see cref="MarkPickedSpanDabs"/>.
    /// </summary>
    private int GenerateScaffoldMarks(
        MassiveSlicer.ViewModels.AdditiveSettingsViewModel add,
        PaintModificationRecord rec)
    {
        // Intentionally no intermediate marks — inheritance carries the column.
        _ = add;
        _ = rec;
        return 0;
    }

    /// <summary>Ordered midpoints along a path/point span for scaffold ribbons.</summary>
    private static List<System.Numerics.Vector3> SampleSpanPolyline(
        ToolpathLayer layer, ContourSpan span, float stepMm)
    {
        var pts = new List<System.Numerics.Vector3>();
        float accum = stepMm; // include first
        for (int i = 0; i < span.Count && span.Start + i < layer.Moves.Count; i++)
        {
            var mv = layer.Moves[span.Start + i];
            if (mv.Kind != MoveKind.Extrude) continue;
            accum += System.Numerics.Vector3.Distance(mv.From, mv.To);
            if (accum < stepMm && pts.Count > 0) continue;
            accum = 0f;
            pts.Add((mv.From + mv.To) * 0.5f);
        }
        if (pts.Count == 0)
            pts.Add(new System.Numerics.Vector3(0, 0, layer.Z));
        // Ensure last bead is represented.
        for (int i = span.Count - 1; i >= 0; i--)
        {
            int mi = span.Start + i;
            if (mi < 0 || mi >= layer.Moves.Count) continue;
            var mv = layer.Moves[mi];
            if (mv.Kind != MoveKind.Extrude) continue;
            var last = (mv.From + mv.To) * 0.5f;
            if (System.Numerics.Vector3.Distance(pts[^1], last) > stepMm * 0.25f)
                pts.Add(last);
            break;
        }
        return pts;
    }

    private static List<System.Numerics.Vector3> ResamplePolyline(
        List<System.Numerics.Vector3> poly, int count)
    {
        if (count <= 1) return [poly[0]];
        if (poly.Count == 1)
        {
            var one = new List<System.Numerics.Vector3>(count);
            for (int i = 0; i < count; i++) one.Add(poly[0]);
            return one;
        }
        float total = 0f;
        for (int i = 1; i < poly.Count; i++)
            total += System.Numerics.Vector3.Distance(poly[i - 1], poly[i]);
        if (total < 1e-4f)
        {
            var flat = new List<System.Numerics.Vector3>(count);
            for (int i = 0; i < count; i++) flat.Add(poly[0]);
            return flat;
        }

        var result = new List<System.Numerics.Vector3>(count);
        for (int s = 0; s < count; s++)
        {
            float target = total * (s / (float)(count - 1));
            float acc = 0f;
            for (int i = 1; i < poly.Count; i++)
            {
                float seg = System.Numerics.Vector3.Distance(poly[i - 1], poly[i]);
                if (acc + seg >= target - 1e-5f || i == poly.Count - 1)
                {
                    float u = seg < 1e-6f ? 0f : Math.Clamp((target - acc) / seg, 0f, 1f);
                    result.Add(System.Numerics.Vector3.Lerp(poly[i - 1], poly[i], u));
                    break;
                }
                acc += seg;
            }
        }
        return result;
    }

    private static System.Numerics.Vector3 SpanCenterRaw(ToolpathLayer layer, ContourSpan span)
    {
        System.Numerics.Vector3 sum = default;
        int n = 0;
        for (int i = 0; i < span.Count && span.Start + i < layer.Moves.Count; i++)
        {
            var mv = layer.Moves[span.Start + i];
            if (mv.Kind != MoveKind.Extrude) continue;
            sum += (mv.From + mv.To) * 0.5f;
            n++;
        }
        if (n == 0)
            return new System.Numerics.Vector3(0, 0, layer.Z);
        return sum / n;
    }

    private static int RemoveMarksNearCenters(
        MassiveSlicer.ViewModels.AdditiveSettingsViewModel add,
        Core.Models.PaintMarkKind kind,
        List<System.Numerics.Vector3> centers)
    {
        if (centers.Count == 0) return 0;
        float tol = (float)(add.BeadWidth > 0.5 ? add.BeadWidth : 6f) * 0.75f;
        return add.PaintMarks.RemoveAll(m =>
            m.Kind == kind
            && centers.Any(c => System.Numerics.Vector3.Distance(m.Center, c) < tol));
    }

    /// <summary>Removes a structural spec and re-links every card pointing past it.</summary>
    private void RemoveStructuralSpec(
        ViewportViewModel vm,
        MassiveSlicer.ViewModels.AdditiveSettingsViewModel add,
        int specIndex)
    {
        add.RemoveStructuralSupportAt(specIndex);
        foreach (var m in _paintModifications)
        {
            if (m.StructuralIndex > specIndex) m.StructuralIndex--;
            else if (m.StructuralIndex == specIndex) m.StructuralIndex = -1;
        }
    }

    /// <summary>Removes one modification and its paint-mark dabs (anchor + target + scaffold).</summary>
    private void DeletePaintModification(ViewportViewModel vm, Guid id)
    {
        int idx = _paintModifications.FindIndex(m => m.Id == id);
        if (idx < 0) return;
        var rec = _paintModifications[idx];
        _paintModifications.RemoveAt(idx);

        if (vm.PaintBridgePickModificationId == id)
            vm.PaintBridgePickModificationId = null;

        // Structural card: retire its pocket spec too and reslice it away.
        if (rec.StructuralIndex >= 0 && vm.AdditiveSettings is { } addStruct)
        {
            RemoveStructuralSpec(vm, addStruct, rec.StructuralIndex);
            vm.UpdateSliceCommand?.Execute(null);
        }

        if (vm.AdditiveSettings is { } add)
        {
            int removed = RemoveMarksNearCenters(add, rec.Kind, rec.MarkCenters);
            removed += RemoveMarksNearCenters(add, rec.Kind, rec.TargetMarkCenters);
            removed += RemoveMarksNearCenters(add, rec.Kind, rec.ScaffoldMarkCenters);
            if (removed > 0) add.BumpPaintStamp();
            LogPaintConsole($"[edit] deleted modification · {removed} mark(s) removed");
        }
        else
            LogPaintConsole("[edit] deleted modification");

        SyncPaintModificationsUi(vm);
        vm.MarkWorkspaceDirty?.Invoke();
        GlCanvas.RequestNextFrameRendering();
    }

    private void ClearAllPaintModifications(ViewportViewModel vm)
    {
        // Retire structural specs linked to cards (highest index first).
        var specIdxs = _paintModifications
            .Where(m => m.StructuralIndex >= 0)
            .Select(m => m.StructuralIndex)
            .Distinct()
            .OrderByDescending(i => i)
            .ToList();
        if (specIdxs.Count > 0 && vm.AdditiveSettings is { } addStruct)
        {
            foreach (var si in specIdxs)
                addStruct.RemoveStructuralSupportAt(si);
            vm.UpdateSliceCommand?.Execute(null);
        }

        _paintModifications.Clear();
        vm.PaintBridgePickModificationId = null;
        if (vm.AdditiveSettings is { } add && add.PaintMarks.Count > 0)
        {
            add.PaintMarks.Clear();
            add.BumpPaintStamp();
        }
        SyncPaintModificationsUi(vm);
        vm.MarkWorkspaceDirty?.Invoke();
        LogPaintConsole("[edit] all modifications cleared");
        GlCanvas.RequestNextFrameRendering();
    }

    private static double Dist2D(Avalonia.Point a, Avalonia.Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Square marquee: paths with a sample point inside the screen rect join selection.</summary>
    private void SelectSpansInRect(ViewportViewModel vm, Avalonia.Rect rect, bool additive = false)
    {
        var (rx, ry, vpW, vpH) = GetGlPickViewport(new Avalonia.Point(rect.X, rect.Y));
        if (vpW <= 1f || vpH <= 1f) return;
        var (rx2, ry2, _, _) = GetGlPickViewport(new Avalonia.Point(rect.Right, rect.Bottom));
        float x0 = MathF.Min(rx, rx2), x1 = MathF.Max(rx, rx2);
        float y0 = MathF.Min(ry, ry2), y1 = MathF.Max(ry, ry2);
        SelectSpansInRegion(vm, additive, (sx, sy) => sx >= x0 && sx <= x1 && sy >= y0 && sy <= y1,
            label: "box");
    }

    /// <summary>Lasso: freehand polygon (viewport px); midpoints inside the loop join selection.</summary>
    private void SelectSpansInLasso(
        ViewportViewModel vm, List<Avalonia.Point> lassoPts, bool additive = false)
    {
        if (lassoPts.Count < 3) return;
        // Convert each lasso vertex into GL-pick space once.
        var poly = new List<(float X, float Y)>(lassoPts.Count);
        float vpW = 0, vpH = 0;
        foreach (var p in lassoPts)
        {
            var (x, y, w, h) = GetGlPickViewport(p);
            vpW = w; vpH = h;
            poly.Add((x, y));
        }
        if (vpW <= 1f || vpH <= 1f) return;
        SelectSpansInRegion(vm, additive, (sx, sy) => PointInPolygon(sx, sy, poly), label: "lasso");
    }

    /// <summary>
    /// Shared region test: for every visible extrude bead (in scrub window + filter),
    /// if its screen midpoint is inside the region, add a short local section (or whole
    /// contour when Contours exist and path mode wants full loops inside the region).
    /// </summary>
    private void SelectSpansInRegion(
        ViewportViewModel vm, bool additive,
        Func<float, float, bool> screenInside, string label)
    {
        float vpW = (float)Math.Max(1.0, GlCanvas.Bounds.Width);
        float vpH = (float)Math.Max(1.0, GlCanvas.Bounds.Height);
        var viewProj = _renderer.GetViewProjectionMatrix(vpW, vpH);
        int scrubLimit = GetPaintScrubMoveLimit();
        int scrubStart0 = GetPaintScrubMoveStart();
        if (!additive)
        {
            _paintSelection.Clear();
            _paintMultiLines.Clear();
        }
        int before = _paintSelection.Count;
        var selCol = new TkVector3(1f, 0.55f, 0.08f);
        float beadMm = (float)(vm.AdditiveSettings?.BeadWidth ?? 6);
        if (beadMm < 0.5f) beadMm = 6f;
        bool pointMode = vm.PaintPointGranularityActive;

        foreach (var (node, tp) in _toolpathByNode)
        {
            if (node.Visible == false && !ReferenceEquals(node, _activeScrubNode)) continue;
            _toolpathOriginByNode.TryGetValue(node, out var origin);
            var wt = node.LocalTransform;
            int globalMove = 0;
            foreach (var layer in tp.Layers)
            {
                var moves = layer.Moves;
                int layerCount = moves.Count;
                if (globalMove + layerCount <= scrubStart0)
                {
                    globalMove += layerCount;
                    continue;
                }
                if (globalMove >= scrubLimit) break;

                int i0 = Math.Max(0, scrubStart0 - globalMove);
                int i1 = Math.Min(layerCount, scrubLimit - globalMove);

                // Prefer recorded contours when present (whole loops inside the region).
                if (layer.Contours.Count > 0 && !pointMode)
                {
                    foreach (var span in layer.Contours)
                    {
                        if (span.Count < 1 || span.Start < 0
                            || span.Start + span.Count > layer.Moves.Count) continue;
                        int spanGlobal = globalMove + span.Start;
                        if (spanGlobal >= scrubLimit || spanGlobal + span.Count <= scrubStart0)
                            continue;
                        if (_paintSelection.Any(sel =>
                            ReferenceEquals(sel.Layer, layer) && sel.Span.Start == span.Start
                            && sel.Span.Count == span.Count)) continue;

                        int stride = Math.Max(1, span.Count / 48);
                        bool hit = false;
                        for (int i = 0; i < span.Count && !hit; i += stride)
                        {
                            var mv = layer.Moves[span.Start + i];
                            if (mv.Kind != MoveKind.Extrude) continue;
                            // continue (not break): mixed contours may start with a
                            // filtered bead type then contain allowed ones.
                            if (!PaintPickAllowed(mv, vm)) continue;
                            if (!TryProjectMoveMid(mv, origin, wt, viewProj, vpW, vpH,
                                    out float sx, out float sy)) continue;
                            if (screenInside(sx, sy)) hit = true;
                        }
                        if (!hit) continue;

                        var poly = SpanWorldHighlight((layer, span, origin, wt), pointMode: false);
                        _paintSelection.Add((layer, span, origin, wt, poly));
                        _paintMultiLines.Add((poly, selCol));
                    }
                }
                else
                {
                    // No contours / point mode: every extrude mid inside → local section or bead.
                    for (int i = i0; i < i1; i++)
                    {
                        var mv = moves[i];
                        if (mv.Kind != MoveKind.Extrude) continue;
                        if (mv.IsLayerStitch || mv.IsLayerChange) continue;
                        if (!PaintPickAllowed(mv, vm)) continue;
                        if (!TryProjectMoveMid(mv, origin, wt, viewProj, vpW, vpH,
                                out float sx, out float sy)) continue;
                        if (!screenInside(sx, sy)) continue;

                        ContourSpan span = pointMode
                            ? new ContourSpan(i, 1, false, -1)
                            : ExpandLocalSection(layer, i, beadMm, i1 - 1,
                                maxMovesCap: 32, minMoveInclusive: i0);
                        if (_paintSelection.Any(sel =>
                            ReferenceEquals(sel.Layer, layer) && sel.Span.Start == span.Start
                            && sel.Span.Count == span.Count)) continue;

                        var poly = SpanWorldHighlight((layer, span, origin, wt), pointMode);
                        _paintSelection.Add((layer, span, origin, wt, poly));
                        _paintMultiLines.Add((poly, selCol));
                        // Skip ahead past this section so we don't add overlapping slices.
                        if (!pointMode) i = Math.Max(i, span.Start + span.Count - 1);
                    }
                }

                globalMove += layerCount;
            }
        }

        // Sticky highlight = last selected.
        if (_paintSelection.Count > 0)
        {
            _paintSelectedLine = new List<TkVector3>(_paintSelection[^1].World);
            _paintSelectedColor = selCol;
            // Multi-lines: everything except last
            _paintMultiLines.Clear();
            for (int i = 0; i < _paintSelection.Count - 1; i++)
                _paintMultiLines.Add((_paintSelection[i].World, selCol));
        }

        SyncPaintSelectionUi(vm);
        if (_paintSelection.Count != before)
            LogPaintConsole($"[edit] {label} select → {_paintSelection.Count} path(s)");
        GlCanvas.RequestNextFrameRendering();
    }

    private bool TryProjectMoveMid(
        ToolpathMove mv, System.Numerics.Vector3 origin, TkMatrix4 wt,
        TkMatrix4 viewProj, float vpW, float vpH, out float sx, out float sy)
    {
        sx = sy = 0;
        var mid = (mv.From + mv.To) * 0.5f;
        var world = TransformPoint(new TkVector3(
            mid.X - origin.X, mid.Y - origin.Y, mid.Z - origin.Z), wt);
        var sp = _renderer.ProjectToScreen(
            new Vector3(world.X, world.Y, world.Z), viewProj, vpW, vpH);
        if (float.IsNaN(sp.X)) return false;
        sx = sp.X; sy = sp.Y;
        return true;
    }

    /// <summary>Ray-cast point-in-polygon (screen space).</summary>
    private static bool PointInPolygon(float x, float y, List<(float X, float Y)> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = poly[i].X, yi = poly[i].Y;
            float xj = poly[j].X, yj = poly[j].Y;
            if (((yi > y) != (yj > y))
                && (x < (xj - xi) * (y - yi) / (yj - yi + 1e-12f) + xi))
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Pick filter from the toolbar: what hover/click/box may select.
    /// Wipes are never selectable (pre-travel purge paths, not part geometry).</summary>
    private static bool PaintPickAllowed(ToolpathMove mv, ViewportViewModel vm)
    {
        if (mv.IsWipe) return false;
        return vm.PaintPickFilter switch
        {
            "Formbound" => mv.IsLightning,
            "Perimeter" => !mv.IsLightning,
            _ => true,
        };
    }

    /// <summary>Nearest local path <em>section</em> under the cursor (not the whole
    /// layer contour). Screen-space proximity is the primary gate (cursor must sit on
    /// the bead), with 3D ray depth as a tie-break so front geometry wins. Timeline
    /// scrub hides not-yet-printed moves — those are not pickable.
    /// <para>
    /// When <paramref name="fullConnectedPath"/> is true (2D slice double-click), expand
    /// to the full connected contour / extrusion run instead of a short local section.
    /// </para>
    /// <para>
    /// Performance: view×projection is built once per call (never per-move — that
    /// froze 10k+ bead paths). Layers fully outside the scrub window are skipped.
    /// Midpoint reject uses a single transform+project before the full segment test.
    /// </para></summary>
    private (ToolpathLayer Layer, ContourSpan Span, System.Numerics.Vector3 Origin, TkMatrix4 Wt)?
        PickSpanUnderCursor(Avalonia.Point pos, bool fullConnectedPath = false)
    {
        var (mx, my, vpW, vpH) = GetGlPickViewport(pos);
        if (vpW <= 1f || vpH <= 1f) return null;

        var vmPick = DataContext as ViewportViewModel;
        float beadMm = (float)(vmPick?.AdditiveSettings?.BeadWidth ?? 6);
        if (beadMm < 0.5f) beadMm = 6f;
        // Screen pick radius — generous so thin centre-lines in path-edit mode are hittable.
        float pickPx = MathF.Max(36f, MathF.Min(64f, beadMm * 5f));
        // Midpoint early-out: must be wide enough that clicks near either END of a long
        // projected bead still reach the full segment test. The old pickPx*3 gate
        // rejected most zoomed-in wall clicks (midpoint hundreds of px from the tip).
        float midGate = MathF.Max(pickPx * 14f, MathF.Min(vpW, vpH) * 0.45f);
        float midRejectPx2 = midGate * midGate;
        // 3D ray gate: centre-line pick can sit farther from the bead axis than the
        // brush sphere did; keep this soft so screen-near segments still win.
        float looseRayDist = beadMm * 24f;

        var ray = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
        var rayO = ray.Origin;
        var rayD = ray.Direction;
        int scrubLimit = GetPaintScrubMoveLimit();
        int scrubStart0 = GetPaintScrubMoveStart();

        // ONE matrix for the whole pick — ProjectToScreen used to rebuild this every move.
        var viewProj = _renderer.GetViewProjectionMatrix(vpW, vpH);

        // Tier 1 — TIGHT radius (the line under the cursor): the FRONT candidate
        // wins. Depth is bucketed to ~2 beads so pixel jitter along one line can't
        // flip the pick; screen distance breaks ties inside a bucket.
        const float tightPx = 14f;
        float depthBucket = MathF.Max(beadMm * 2f, 4f);
        long t1Bucket = long.MaxValue;
        float t1Screen = float.MaxValue;
        ToolpathLayer? t1Layer = null;
        int t1Move = -1, t1Global = -1;
        System.Numerics.Vector3 t1Origin = default;
        TkMatrix4 t1Wt = TkMatrix4.Identity;

        // Tier 2 — forgiving ring out to pickPx: nearest on screen, depth tie-break.
        float bestScreenD = pickPx;
        float bestRayT = float.MaxValue;
        ToolpathLayer? bestLayer = null;
        int bestMove = -1;
        int bestGlobal = -1;
        System.Numerics.Vector3 bestOrigin = default;
        TkMatrix4 bestWt = TkMatrix4.Identity;

        foreach (var (node, tp) in _toolpathByNode)
        {
            // Scrub session often shows toolpaths while the mesh/TCP is selected —
            // don't require node.Visible (hidden-in-outliner paths still draw under scrub).
            if (node.Visible == false && !ReferenceEquals(node, _activeScrubNode)) continue;
            _toolpathOriginByNode.TryGetValue(node, out var origin);
            // Match draw path: toolpath MVP uses LocalTransform, not full WorldTransform.
            var wt = node.LocalTransform;
            float ox = origin.X, oy = origin.Y, oz = origin.Z;
            int globalMove = 0;
            foreach (var layer in tp.Layers)
            {
                var moves = layer.Moves;
                int layerCount = moves.Count;
                // Whole layer outside the scrub window → skip without per-move work.
                if (globalMove + layerCount <= scrubStart0)
                {
                    globalMove += layerCount;
                    continue;
                }
                if (globalMove >= scrubLimit)
                    break;

                int i0 = Math.Max(0, scrubStart0 - globalMove);
                int i1 = Math.Min(layerCount, scrubLimit - globalMove); // exclusive
                for (int i = i0; i < i1; i++)
                {
                    var mv = moves[i];
                    if (mv.Kind != MoveKind.Extrude) continue;
                    if (mv.IsLayerStitch || mv.IsLayerChange) continue;
                    if (vmPick is not null && !PaintPickAllowed(mv, vmPick)) continue;

                    // Midpoint only first (1 transform + 1 project) — rejects far-away moves.
                    float midx = (mv.From.X + mv.To.X) * 0.5f - ox;
                    float midy = (mv.From.Y + mv.To.Y) * 0.5f - oy;
                    float midz = (mv.From.Z + mv.To.Z) * 0.5f - oz;
                    var wMid = TransformPoint(new TkVector3(midx, midy, midz), wt);
                    var sM = _renderer.ProjectToScreen(
                        new Vector3(wMid.X, wMid.Y, wMid.Z), viewProj, vpW, vpH);
                    if (float.IsNaN(sM.X)) continue;
                    float midDx = sM.X - mx, midDy = sM.Y - my;
                    if (midDx * midDx + midDy * midDy > midRejectPx2)
                        continue;

                    // Near the cursor: full segment ends for accurate screen distance.
                    var nFrom = TransformPoint(
                        new TkVector3(mv.From.X - ox, mv.From.Y - oy, mv.From.Z - oz), wt);
                    var nTo = TransformPoint(
                        new TkVector3(mv.To.X - ox, mv.To.Y - oy, mv.To.Z - oz), wt);
                    var wFrom = new Vector3(nFrom.X, nFrom.Y, nFrom.Z);
                    var wTo = new Vector3(nTo.X, nTo.Y, nTo.Z);

                    var sA = _renderer.ProjectToScreen(wFrom, viewProj, vpW, vpH);
                    var sB = _renderer.ProjectToScreen(wTo, viewProj, vpW, vpH);
                    if (float.IsNaN(sA.X) || float.IsNaN(sB.X)) continue;
                    float screenD = DistPointToSegment2D(mx, my, sA.X, sA.Y, sB.X, sB.Y);
                    if (screenD > pickPx) continue;

                    float dist3 = DistanceRayToSegment(rayO, rayD, wFrom, wTo, out float rayT);
                    // Screen proximity already proved the click is on the line; only
                    // reject when the ray clearly misses behind the camera or is absurdly far.
                    if (rayT < 0f || dist3 > looseRayDist) continue;

                    int g = globalMove + i;
                    if (screenD <= tightPx)
                    {
                        long bucket = (long)(rayT / depthBucket);
                        if (bucket < t1Bucket
                            || (bucket == t1Bucket && screenD < t1Screen))
                        {
                            t1Bucket = bucket;
                            t1Screen = screenD;
                            t1Layer = layer;
                            t1Move = i;
                            t1Global = g;
                            t1Origin = origin;
                            t1Wt = wt;
                        }
                    }

                    bool better = screenD < bestScreenD - 0.5f
                        || (MathF.Abs(screenD - bestScreenD) <= 0.5f && rayT < bestRayT);
                    if (!better) continue;

                    bestScreenD = screenD;
                    bestRayT = rayT;
                    bestLayer = layer;
                    bestMove = i;
                    bestGlobal = g;
                    bestOrigin = origin;
                    bestWt = wt;
                }

                globalMove += layerCount;
            }
        }

        // The tight front pick trumps the forgiving ring whenever it exists.
        if (t1Layer is not null)
        {
            bestLayer = t1Layer;
            bestMove = t1Move;
            bestGlobal = t1Global;
            bestOrigin = t1Origin;
            bestWt = t1Wt;
        }
        if (bestLayer is null || bestMove < 0) return null;
        // Point granularity: exactly one bead — never expand into a multi-metre section
        // (that was freezing the UI on long walls / formbound runs).
        // 2D slice + fullConnectedPath still expands (double-click path select).
        bool pointGran = vmPick?.PaintSelectGranularity == "Point";
        if (pointGran && !fullConnectedPath)
            return (bestLayer, new ContourSpan(bestMove, 1, Closed: false, EntryTravelIndex: -1),
                bestOrigin, bestWt);

        // Do not expand the section into moves the scrub has hidden (below or above).
        int layerStartGlobal = bestGlobal - bestMove;
        int maxMoveInLayer = scrubLimit == int.MaxValue
            ? bestLayer.Moves.Count - 1
            : Math.Min(bestLayer.Moves.Count - 1, scrubLimit - 1 - layerStartGlobal);
        int minMoveInLayer = scrubStart0 <= 0
            ? 0
            : Math.Max(0, scrubStart0 - layerStartGlobal);

        // Double-click (2D slice): entire connected contour / extrusion run.
        if (fullConnectedPath)
        {
            var full = ExpandFullConnectedPath(bestLayer, bestMove, beadMm, maxMoveInLayer,
                minMoveInclusive: minMoveInLayer);
            return (bestLayer, full, bestOrigin, bestWt);
        }

        // Single-click Path mode: local section. In 2D slice multiplanar paths have
        // larger 3D gaps / corners — use a looser expand so we get a real line, not
        // a one-bead "point" pick.
        bool sliceNav = vmPick is { IsSlicePlaneViewerActive: true, IsPaintEditOpen: true };
        var section = sliceNav
            ? ExpandLocalSection(bestLayer, bestMove, beadMm, maxMoveInLayer,
                maxMovesCap: 256, minMoveInclusive: minMoveInLayer,
                slicePlaneLoose: true)
            : ExpandLocalSection(bestLayer, bestMove, beadMm, maxMoveInLayer,
                maxMovesCap: 32, minMoveInclusive: minMoveInLayer);
        return (bestLayer, section, bestOrigin, bestWt);
    }

    private static float DistPointToSegment2D(float px, float py, float ax, float ay, float bx, float by)
    {
        float abx = bx - ax, aby = by - ay;
        float len2 = abx * abx + aby * aby;
        float t = len2 < 1e-12f ? 0f : Math.Clamp(((px - ax) * abx + (py - ay) * aby) / len2, 0f, 1f);
        float cx = ax + t * abx, cy = ay + t * aby;
        float dx = px - cx, dy = py - cy;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Shortest distance from ray (origin + t·dir, t≥0) to segment AB.
    /// <paramref name="rayT"/> is the ray parameter of the closest point on the ray.</summary>
    private static float DistanceRayToSegment(
        Vector3 rayO, Vector3 rayD, Vector3 a, Vector3 b, out float rayT)
    {
        var ab = b - a;
        float abLen2 = ab.LengthSquared;
        if (abLen2 < 1e-12f)
        {
            // Degenerate segment → point-to-ray.
            var toPt = a - rayO;
            rayT = MathF.Max(0f, Vector3.Dot(toPt, rayD));
            return (a - (rayO + rayD * rayT)).Length;
        }

        // Closest points between infinite line and infinite segment-line, then clamp.
        // Ray: P(s) = rayO + s·rayD  (s ≥ 0). Segment: Q(u) = a + u·ab  (u ∈ [0,1]).
        var w0 = rayO - a;
        float ra = 1f; // rayD assumed unit
        float rb = Vector3.Dot(rayD, ab);
        float rc = abLen2;
        float rd = Vector3.Dot(rayD, w0);
        float re = Vector3.Dot(ab, w0);
        float denom = ra * rc - rb * rb;

        float s, u;
        if (MathF.Abs(denom) < 1e-10f)
        {
            // Nearly parallel — pin segment param by projecting ray origin onto AB.
            u = Math.Clamp(Vector3.Dot(rayO - a, ab) / abLen2, 0f, 1f);
            s = Vector3.Dot(a + ab * u - rayO, rayD);
        }
        else
        {
            s = (rb * re - rc * rd) / denom;
            u = (ra * re - rb * rd) / denom;
            if (u < 0f)
            {
                u = 0f;
                s = Vector3.Dot(a - rayO, rayD);
            }
            else if (u > 1f)
            {
                u = 1f;
                s = Vector3.Dot(b - rayO, rayD);
            }
        }

        if (s < 0f) s = 0f;
        rayT = s;
        var closestRay = rayO + rayD * s;
        var closestSeg = a + ab * u;
        return (closestRay - closestSeg).Length;
    }

    /// <summary>
    /// Grow a short local section around <paramref name="hitMove"/> for path/line select.
    /// Stops at corners, gaps, and a tight length budget so a click does not grab a
    /// long run of the layer contour.
    /// </summary>
    /// <param name="maxMoveInclusive">Last move index that may be included (scrub high).</param>
    /// <param name="maxMovesCap">Hard cap on span length in moves (each side of the hit).</param>
    /// <param name="minMoveInclusive">First move index that may be included (scrub low).</param>
    /// <param name="slicePlaneLoose">2D slice multiplanar: larger gap / softer corners so
    /// Path mode selects a real line segment, not a single bead "point".</param>
    private static ContourSpan ExpandLocalSection(
        ToolpathLayer layer, int hitMove, float beadMm, int maxMoveInclusive = int.MaxValue,
        int maxMovesCap = 32, int minMoveInclusive = 0, bool slicePlaneLoose = false)
    {
        var moves = layer.Moves;
        if (hitMove < 0 || hitMove >= moves.Count)
            return new ContourSpan(0, 0, false, -1);
        int loCap = Math.Clamp(minMoveInclusive, 0, Math.Max(0, moves.Count - 1));
        int hiCap = Math.Min(moves.Count - 1, maxMoveInclusive);
        if (hiCap < loCap) return new ContourSpan(0, 0, false, -1);
        if (hitMove > hiCap) hitMove = hiCap;
        if (hitMove < loCap) hitMove = loCap;

        // Snap hit onto nearest non-wipe extrude if needed.
        int hit = hitMove;
        if (moves[hit].Kind != MoveKind.Extrude || moves[hit].IsWipe)
        {
            int found = -1;
            for (int d = 0; d < moves.Count; d++)
            {
                int a = hitMove - d, b = hitMove + d;
                if (a >= loCap && a <= hiCap
                    && moves[a].Kind == MoveKind.Extrude && !moves[a].IsWipe) { found = a; break; }
                if (b >= loCap && b <= hiCap
                    && moves[b].Kind == MoveKind.Extrude && !moves[b].IsWipe) { found = b; break; }
            }
            if (found < 0) return new ContourSpan(hitMove, 1, false, -1);
            hit = found;
        }

        // Stay inside the recorded contour that contains the hit (when present),
        // further clamped to the scrub window [loCap, hiCap].
        int loBound = loCap, hiBound = hiCap;
        if (layer.Contours.Count > 0)
        {
            for (int ci = 0; ci < layer.Contours.Count; ci++)
            {
                var c = layer.Contours[ci];
                int cEnd = c.Start + Math.Max(0, c.Count) - 1;
                if (hit >= c.Start && hit <= cEnd)
                {
                    loBound = Math.Max(loCap, c.Start);
                    hiBound = Math.Min(hiCap, cEnd);
                    break;
                }
            }
        }

        bool lightning = moves[hit].IsLightning;
        // Short local section (~a few beads / tens of mm). Slice multiplanar uses a
        // longer budget so Path mode still lights a continuous arc, not one bead.
        float maxLen = slicePlaneLoose
            ? (lightning ? MathF.Max(200f, beadMm * 40f) : MathF.Max(160f, beadMm * 28f))
            : (lightning ? MathF.Max(48f, beadMm * 10f) : MathF.Max(36f, beadMm * 6f));
        float gapTol = slicePlaneLoose ? beadMm * 4f : beadMm * 1.25f;
        // cos(~60°) in slice — multiplanar turns freely; cos(~30°) elsewhere.
        float minCornerDot = slicePlaneLoose ? 0.50f : 0.85f;

        int lo = hit, hi = hit;
        float lenLo = 0f, lenHi = 0f;
        int halfCap = Math.Max(1, maxMovesCap / 2);

        while (lo > loBound && (hit - lo) < halfCap)
        {
            int prev = lo - 1;
            if (!CanJoinSection(moves, prev, lo, lightning, gapTol, minCornerDot)) break;
            float seg = System.Numerics.Vector3.Distance(moves[prev].From, moves[prev].To);
            if (lenLo + seg > maxLen) break;
            lenLo += seg;
            lo = prev;
        }
        while (hi < hiBound && (hi - hit) < halfCap)
        {
            int next = hi + 1;
            if (!CanJoinSection(moves, hi, next, lightning, gapTol, minCornerDot)) break;
            float seg = System.Numerics.Vector3.Distance(moves[next].From, moves[next].To);
            if (lenHi + seg > maxLen) break;
            lenHi += seg;
            hi = next;
        }

        int count = hi - lo + 1;
        return new ContourSpan(lo, count, Closed: false, EntryTravelIndex: -1);
    }

    /// <summary>
    /// Entire connected path around <paramref name="hitMove"/>: prefer the recorded
    /// contour that contains the hit; otherwise grow through all gap-joined extrudes
    /// (no length/move cap, no corner stops) within the scrub window.
    /// Used by 2D slice double-click select.
    /// </summary>
    private static ContourSpan ExpandFullConnectedPath(
        ToolpathLayer layer, int hitMove, float beadMm, int maxMoveInclusive = int.MaxValue,
        int minMoveInclusive = 0)
    {
        var moves = layer.Moves;
        if (hitMove < 0 || hitMove >= moves.Count)
            return new ContourSpan(0, 0, false, -1);
        int loCap = Math.Clamp(minMoveInclusive, 0, Math.Max(0, moves.Count - 1));
        int hiCap = Math.Min(moves.Count - 1, maxMoveInclusive);
        if (hiCap < loCap) return new ContourSpan(0, 0, false, -1);
        if (hitMove > hiCap) hitMove = hiCap;
        if (hitMove < loCap) hitMove = loCap;

        int hit = hitMove;
        if (moves[hit].Kind != MoveKind.Extrude || moves[hit].IsWipe)
        {
            int found = -1;
            for (int d = 0; d < moves.Count; d++)
            {
                int a = hitMove - d, b = hitMove + d;
                if (a >= loCap && a <= hiCap
                    && moves[a].Kind == MoveKind.Extrude && !moves[a].IsWipe) { found = a; break; }
                if (b >= loCap && b <= hiCap
                    && moves[b].Kind == MoveKind.Extrude && !moves[b].IsWipe) { found = b; break; }
            }
            if (found < 0) return new ContourSpan(hitMove, 1, false, -1);
            hit = found;
        }

        // Prefer the slicer-recorded contour that contains the hit (true full path).
        if (layer.Contours.Count > 0)
        {
            for (int ci = 0; ci < layer.Contours.Count; ci++)
            {
                var c = layer.Contours[ci];
                int cEnd = c.Start + Math.Max(0, c.Count) - 1;
                if (hit < c.Start || hit > cEnd) continue;
                int lo = Math.Max(loCap, c.Start);
                int hi = Math.Min(hiCap, cEnd);
                if (hi < lo) break;
                // Closed only if the full contour is visible in the scrub window.
                bool closed = c.Closed && lo == c.Start && hi == cEnd;
                return new ContourSpan(lo, hi - lo + 1, closed, c.EntryTravelIndex);
            }
        }

        // No Contours (or hit outside them): grow along continuous extrudes until a gap.
        bool lightning = moves[hit].IsLightning;
        float gapTol = beadMm * 1.25f;
        // minCornerDot = -1 → never stop on corners; only gap / kind / lightning split.
        const float noCornerStop = -1f;
        int loGrow = hit, hiGrow = hit;
        while (loGrow > loCap)
        {
            int prev = loGrow - 1;
            if (!CanJoinSection(moves, prev, loGrow, lightning, gapTol, noCornerStop)) break;
            loGrow = prev;
        }
        while (hiGrow < hiCap)
        {
            int next = hiGrow + 1;
            if (!CanJoinSection(moves, hiGrow, next, lightning, gapTol, noCornerStop)) break;
            hiGrow = next;
        }

        // Closed if ends meet (within gap) inside the grown span.
        bool endsMeet = loGrow < hiGrow
            && System.Numerics.Vector3.Distance(moves[hiGrow].To, moves[loGrow].From) <= gapTol;
        return new ContourSpan(loGrow, hiGrow - loGrow + 1, endsMeet, EntryTravelIndex: -1);
    }

    /// <summary>Whether two consecutive moves belong to the same pickable section.</summary>
    private static bool CanJoinSection(
        IReadOnlyList<ToolpathMove> moves, int a, int b,
        bool wantLightning, float gapTol, float minCornerDot)
    {
        if ((uint)a >= (uint)moves.Count || (uint)b >= (uint)moves.Count) return false;
        var ma = moves[a];
        var mb = moves[b];
        if (ma.Kind != MoveKind.Extrude || mb.Kind != MoveKind.Extrude) return false;
        // Wipes are pre-travel purge — never part of a selectable support/remove span.
        if (ma.IsWipe || mb.IsWipe) return false;
        if (ma.IsLayerStitch || mb.IsLayerStitch || ma.IsLayerChange || mb.IsLayerChange)
            return false;
        // Formbound vs wall must not cross.
        if (ma.IsLightning != wantLightning || mb.IsLightning != wantLightning) return false;

        float gap = System.Numerics.Vector3.Distance(ma.To, mb.From);
        if (gap > gapTol) return false;

        // Lightning / formbound: join through corners (T mouths, elbows).
        if (wantLightning) return true;

        // Wall: stop at sharp direction changes so a rim click is a local arc.
        var da = ma.To - ma.From;
        var db = mb.To - mb.From;
        float la2 = da.LengthSquared(), lb2 = db.LengthSquared();
        if (la2 < 1e-6f || lb2 < 1e-6f) return true; // micro-segments don't force a split
        da /= MathF.Sqrt(la2);
        db /= MathF.Sqrt(lb2);
        float dot = System.Numerics.Vector3.Dot(da, db);
        return dot >= minCornerDot;
    }

    /// <summary>Build contour spans from contiguous Extrude runs separated by Travels
    /// or layer stitches — used when the slicer did not record Contours.</summary>
    private static List<ContourSpan> SynthesizeContourSpans(ToolpathLayer layer)
    {
        var spans = new List<ContourSpan>();
        int i = 0;
        var moves = layer.Moves;
        while (i < moves.Count)
        {
            // Skip non-extrude lead-in.
            while (i < moves.Count && moves[i].Kind != MoveKind.Extrude) i++;
            if (i >= moves.Count) break;
            int start = i;
            int entryTravel = (start > 0 && moves[start - 1].Kind == MoveKind.Travel)
                ? start - 1 : -1;
            while (i < moves.Count && moves[i].Kind == MoveKind.Extrude
                   && !moves[i].IsLayerStitch && !moves[i].IsLayerChange)
                i++;
            int count = i - start;
            if (count >= 1)
            {
                // Closed if the run ends near its start (need ≥2 moves for a loop).
                bool closed = false;
                if (count >= 2)
                {
                    var a = moves[start].From;
                    var b = moves[start + count - 1].To;
                    closed = System.Numerics.Vector3.DistanceSquared(a, b) < 1.0f; // ~1 mm²
                }
                spans.Add(new ContourSpan(start, count, closed, entryTravel));
            }
        }
        return spans;
    }

    /// <summary>
    /// World highlight for a pick. In point mode (or a single-bead span) returns one
    /// midpoint so the overlay draws a sphere — never a From→To segment that looks
    /// like a line selection.
    /// </summary>
    private List<TkVector3> SpanWorldHighlight(
        (ToolpathLayer Layer, ContourSpan Span, System.Numerics.Vector3 Origin, TkMatrix4 Wt) pick,
        bool pointMode)
    {
        if (pointMode)
            return SpanWorldMidpoints(pick);

        // A single MOVE can be a metre-long wall segment (straight panel sides are
        // one move each) — Lines mode must highlight it as the actual line, not a
        // midpoint dot. Only genuinely short beads render as points.
        if (pick.Span.Count == 1
            && pick.Span.Start >= 0 && pick.Span.Start < pick.Layer.Moves.Count)
        {
            var mv = pick.Layer.Moves[pick.Span.Start];
            float beadW = (float)(_vm?.AdditiveSettings?.BeadWidth ?? 6.0);
            if (mv.Kind == MoveKind.Extrude && SingleMoveRendersAsLine(mv, beadW))
            {
                var a = TransformPoint(new TkVector3(
                    mv.From.X - pick.Origin.X, mv.From.Y - pick.Origin.Y, mv.From.Z - pick.Origin.Z), pick.Wt);
                var b = TransformPoint(new TkVector3(
                    mv.To.X - pick.Origin.X, mv.To.Y - pick.Origin.Y, mv.To.Z - pick.Origin.Z), pick.Wt);
                return [new TkVector3(a.X, a.Y, a.Z), new TkVector3(b.X, b.Y, b.Z)];
            }
        }

        if (pick.Span.Count <= 1)
            return SpanWorldMidpoints(pick);
        return SpanWorldPolyline(pick);
    }

    /// <summary>Line-mode policy: a lone move longer than ~1.5 beads is a wall line,
    /// not a bead — highlight it as a segment. (Testable split of the visual rule.)</summary>
    internal static bool SingleMoveRendersAsLine(ToolpathMove mv, float beadWidthMm) =>
        (mv.To - mv.From).Length() > MathF.Max(beadWidthMm, 1f) * 1.5f;

    /// <summary>One world-space midpoint per extrude bead in the span (point picks).</summary>
    private List<TkVector3> SpanWorldMidpoints(
        (ToolpathLayer Layer, ContourSpan Span, System.Numerics.Vector3 Origin, TkMatrix4 Wt) pick)
    {
        var pts = new List<TkVector3>();
        int end = Math.Min(pick.Layer.Moves.Count, pick.Span.Start + Math.Max(0, pick.Span.Count));
        for (int i = pick.Span.Start; i < end; i++)
        {
            var mv = pick.Layer.Moves[i];
            if (mv.Kind != MoveKind.Extrude) continue;
            var mid = (mv.From + mv.To) * 0.5f;
            var w = TransformPoint(new TkVector3(
                mid.X - pick.Origin.X, mid.Y - pick.Origin.Y, mid.Z - pick.Origin.Z), pick.Wt);
            pts.Add(new TkVector3(w.X, w.Y, w.Z));
        }
        return pts;
    }

    /// <summary>World polyline of a picked path span (hover / selection highlight).</summary>
    private List<TkVector3> SpanWorldPolyline(
        (ToolpathLayer Layer, ContourSpan Span, System.Numerics.Vector3 Origin, TkMatrix4 Wt) pick)
    {
        var pts = new List<TkVector3>();
        if (pick.Span.Count <= 0) return pts;
        // Single bead → midpoint only (never From+To, which draws as a short line).
        if (pick.Span.Count == 1)
            return SpanWorldMidpoints(pick);

        int stride = Math.Max(1, pick.Span.Count / 400);
        for (int i = 0; i < pick.Span.Count; i += stride)
        {
            var mv = pick.Layer.Moves[pick.Span.Start + i];
            var w = TransformPoint(new TkVector3(
                mv.From.X - pick.Origin.X, mv.From.Y - pick.Origin.Y, mv.From.Z - pick.Origin.Z), pick.Wt);
            pts.Add(new TkVector3(w.X, w.Y, w.Z));
        }
        // Close with last segment's To so the highlight covers the full contour end.
        var last = pick.Layer.Moves[pick.Span.Start + pick.Span.Count - 1];
        var wTo = TransformPoint(new TkVector3(
            last.To.X - pick.Origin.X, last.To.Y - pick.Origin.Y, last.To.Z - pick.Origin.Z), pick.Wt);
        var end = new TkVector3(wTo.X, wTo.Y, wTo.Z);
        if (pts.Count == 0 || (pts[^1] - end).LengthSquared > 1e-4f)
            pts.Add(end);
        return pts;
    }

    /// <summary>Stable identity for a picked contour — console feedback + undo/redo.</summary>
    private sealed record PaintLineId(
        int LayerIndex,
        float LayerZ,
        int ContourIndex,
        int MoveStart,
        int MoveCount,
        bool Closed,
        bool IsFormbound,
        float LengthMm,
        System.Numerics.Vector3 Mid,
        string Action)
    {
        public string ShortId =>
            $"L{LayerIndex} C{ContourIndex} m{MoveStart}+{MoveCount}";

        public string Describe()
        {
            // Local picks are open sections carved out of a larger contour.
            string kind = IsFormbound ? "formbound" : (Closed ? "loop" : "section");
            return $"{ShortId}  {kind}  z={LayerZ:0.#}  len={LengthMm:0}mm  " +
                   $"mid=({Mid.X:0},{Mid.Y:0},{Mid.Z:0})";
        }
    }

    /// <summary>Click-a-line: picks the contour under the cursor, sticky-highlights it,
    /// broadcasts identity to the console, and (when <paramref name="applyMarks"/>) lays
    /// Bridge/Remove marks along it. Edit menu open with no tool → select-only.
    /// <paramref name="fullConnectedPath"/> (2D slice double-click) selects the entire
    /// connected path instead of a short local section.</summary>
    private DateTime _paintMissHintAt = DateTime.MinValue;

    private void TryPaintLineAt(ViewportViewModel vm, Avalonia.Point pos, bool erase,
        bool applyMarks = true, bool additive = false, bool fullConnectedPath = false)
    {
        // Selection highlight works even without AdditiveSettings; marks need it.
        if (PickSpanUnderCursor(pos, fullConnectedPath) is not { } pickHit)
        {
            _paintHoverLine = null;
            // Quiet miss is confusing — nudge when the pick filter is likely the cause.
            if (!string.Equals(vm.PaintPickFilter, "All", StringComparison.OrdinalIgnoreCase)
                && (DateTime.UtcNow - _paintMissHintAt).TotalSeconds > 2.5)
            {
                _paintMissHintAt = DateTime.UtcNow;
                LogPaintConsole(
                    $"[edit] no path under cursor (filter={vm.PaintPickFilter}). "
                    + "Switch filter to All to pick wall paths, or Formbound for support fingers.");
            }
            // Keep sticky selection so a miss doesn't blank the previous pick.
            return;
        }

        // Bridge-target pick: attach this span as the second anchor of a Support mod.
        if (vm.PaintBridgePickModificationId is Guid bridgeModId)
        {
            var bridgePoly = SpanWorldHighlight(pickHit, pointMode: vm.PaintPointGranularityActive);
            AttachBridgeTarget(vm, bridgeModId, pickHit.Layer, pickHit.Span,
                pickHit.Origin, pickHit.Wt, bridgePoly);
            return;
        }

        string action = !applyMarks
            ? "select"
            : erase
                ? "erase-marks"
                : vm.PaintLineBridgeActive ? "line-bridge" : "line-remove";

        var id = BuildPaintLineId(pickHit, action);
        // Point mode → single midpoint sphere; path mode → polyline section.
        var poly = SpanWorldHighlight(pickHit, pointMode: vm.PaintPointGranularityActive);
        var newColor = !applyMarks
            ? new TkVector3(1f, 0.55f, 0.08f)   // amber = select-only
            : erase
                ? new TkVector3(1f, 0.35f, 0.25f)   // red-ish when erasing marks
                : vm.PaintLineBridgeActive
                    ? new TkVector3(0.2f, 0.9f, 1f) // cyan = bridge / buttress
                    : new TkVector3(1f, 0.45f, 0.15f); // orange = remove

        // Snapshot previous selection for undo before mutating.
        var prevPoly = _paintSelectedLine is { } pp ? new List<TkVector3>(pp) : null;
        var prevColor = _paintSelectedColor;
        var prevId = _paintSelectedId;

        // ── Point mode + Shift: range-select shortest path between anchor and click ──
        // Pick A (sets anchor) → Shift+click B → every bead on the shortest route A↔B.
        bool pointMode = vm.PaintPointGranularityActive;
        float beadForPath = (float)(vm.AdditiveSettings?.BeadWidth ?? 6);
        if (beadForPath < 0.5f) beadForPath = 6f;

        if (pointMode && additive
            && _paintPointAnchorLayer is not null
            && _paintPointAnchorMove >= 0
            && ReferenceEquals(_paintPointAnchorLayer, pickHit.Layer)
            && Core.Slicing.ContourPointPath.ShortestPath(
                    pickHit.Layer, _paintPointAnchorMove, pickHit.Span.Start, beadForPath)
                is { Count: > 0 } pathSpans)
        {
            // Drop prior entries on the same contour (the range replaces them).
            if (Core.Slicing.ContourPointPath.TryResolveSharedContour(
                    pickHit.Layer, _paintPointAnchorMove, pickHit.Span.Start, beadForPath,
                    out var sharedContour))
            {
                int c0 = sharedContour.Start;
                int c1 = sharedContour.Start + Math.Max(0, sharedContour.Count) - 1;
                _paintSelection.RemoveAll(sel =>
                    ReferenceEquals(sel.Layer, pickHit.Layer)
                    && sel.Span.Start >= c0
                    && sel.Span.Start + Math.Max(0, sel.Span.Count) - 1 <= c1);
            }

            int totalPts = 0;
            foreach (var span in pathSpans)
            {
                if (span.Count <= 0) continue;
                totalPts += span.Count;
                var pathPoly = SpanWorldHighlight(
                    (pickHit.Layer, span, pickHit.Origin, pickHit.Wt), pointMode: true);
                bool already = _paintSelection.Any(sel =>
                    ReferenceEquals(sel.Layer, pickHit.Layer)
                    && sel.Span.Start == span.Start
                    && sel.Span.Count == span.Count);
                if (!already)
                    _paintSelection.Add(
                        (pickHit.Layer, span, pickHit.Origin, pickHit.Wt, pathPoly));
            }

            RebuildPaintSelectionHighlights(newColor);
            _paintSelectedId = id;
            // Keep the original anchor so further Shift+clicks re-range from first pick.
            SyncPaintSelectionUi(vm);
            LogPaintConsole(
                $"[edit] shift-range · {totalPts} point(s) on shortest path "
                + $"(m{_paintPointAnchorMove} → m{pickHit.Span.Start})");

            int markDeltaRange = 0;
            if (applyMarks && vm.AdditiveSettings is { } addRange)
            {
                foreach (var sel in _paintSelection)
                {
                    if (!ReferenceEquals(sel.Layer, pickHit.Layer)) continue;
                    markDeltaRange += ApplyPaintMarksAlongSpan(
                        vm, addRange, sel.Layer, sel.Span, erase);
                }
            }

            BroadcastPaintLineSelection(id, markDeltaRange);
            PushPaintLineSelectionUndo(vm, prevPoly, prevColor, prevId,
                _paintSelectedLine is { } sl ? new List<TkVector3>(sl) : poly,
                newColor, id);
            return;
        }

        // Shift keeps earlier picks lit (multi-line marking); a plain click starts over.
        if (additive)
        {
            if (prevPoly is not null)
                _paintMultiLines.Add((prevPoly, prevColor));
        }
        else
            _paintMultiLines.Clear();

        _paintHoverLine = poly;
        _paintSelectedLine = poly;
        _paintSelectedColor = newColor;
        _paintSelectedId = id;

        // Actionable selection list: replace on plain click, accumulate on shift.
        if (!additive) _paintSelection.Clear();
        bool dupPick = _paintSelection.Any(sel =>
            ReferenceEquals(sel.Layer, pickHit.Layer) && sel.Span.Start == pickHit.Span.Start
            && sel.Span.Count == pickHit.Span.Count);
        if (!dupPick)
            _paintSelection.Add((pickHit.Layer, pickHit.Span, pickHit.Origin, pickHit.Wt, poly));

        // Point-mode anchor: plain click (or non-path shift add) becomes the range start.
        if (pointMode)
            SetPaintPointAnchor(pickHit.Layer, pickHit.Span.Start, pickHit.Origin, pickHit.Wt);
        else
            ClearPaintPointAnchor();

        SyncPaintSelectionUi(vm);

        int markDelta = 0;
        if (applyMarks && vm.AdditiveSettings is { } add)
            markDelta = ApplyPaintMarksAlongSpan(vm, add, pickHit.Layer, pickHit.Span, erase);

        BroadcastPaintLineSelection(id, markDelta);
        PushPaintLineSelectionUndo(vm, prevPoly, prevColor, prevId, poly, newColor, id);
    }

    /// <summary>
    /// Lay Bridge/Remove dabs (or erase them) along a span. Returns +added or −removed.
    /// </summary>
    private int ApplyPaintMarksAlongSpan(
        ViewportViewModel vm,
        AdditiveSettingsViewModel add,
        ToolpathLayer layer,
        ContourSpan span,
        bool erase)
    {
        float bead = (float)add.BeadWidth;
        if (bead < 0.5f) bead = 6f;
        int delta = 0;

        if (erase)
        {
            int removed = 0;
            for (int i = 0; i < span.Count; i++)
            {
                int mi = span.Start + i;
                if ((uint)mi >= (uint)layer.Moves.Count) break;
                var mv = layer.Moves[mi];
                var mid = (mv.From + mv.To) * 0.5f;
                removed += add.PaintMarks.RemoveAll(m =>
                    System.Numerics.Vector3.Distance(m.Center, mid) < m.Radius + bead);
            }
            if (removed > 0) { _paintStrokeChanged = true; delta = -removed; }
            return delta;
        }

        var markKind = vm.PaintLineBridgeActive
            ? Core.Models.PaintMarkKind.Bridge
            : Core.Models.PaintMarkKind.Remove;
        float dabRadius = markKind == Core.Models.PaintMarkKind.Bridge ? bead * 1.5f : bead * 1.2f;
        float spacing   = markKind == Core.Models.PaintMarkKind.Bridge ? bead * 3f : bead * 1.5f;
        float accum = spacing;
        int added = 0;
        for (int i = 0; i < span.Count; i++)
        {
            int mi = span.Start + i;
            if ((uint)mi >= (uint)layer.Moves.Count) break;
            var mv = layer.Moves[mi];
            if (mv.Kind != MoveKind.Extrude) continue;
            accum += System.Numerics.Vector3.Distance(mv.From, mv.To);
            if (accum < spacing) continue;
            accum = 0f;
            var mid = (mv.From + mv.To) * 0.5f;
            bool covered = false;
            foreach (var m in add.PaintMarks)
                if (m.Kind == markKind
                    && System.Numerics.Vector3.Distance(m.Center, mid) < dabRadius * 0.5f)
                { covered = true; break; }
            if (covered) continue;
            var lineRole = markKind == Core.Models.PaintMarkKind.Bridge
                ? Core.Models.PaintBridgeRole.SupportBar
                : Core.Models.PaintBridgeRole.None;
            var lineStyle = markKind == Core.Models.PaintMarkKind.Bridge
                ? Core.Models.PaintSupportStyleUtil.FromLabel(vm.PaintSupportType)
                : Core.Models.PaintSupportStyle.FormboundButtress;
            add.PaintMarks.Add(new Core.Models.PaintMark(mid, dabRadius, markKind, lineRole, lineStyle));
            _paintStrokeChanged = true;
            added++;
        }
        return added;
    }

    private static PaintLineId BuildPaintLineId(
        (ToolpathLayer Layer, ContourSpan Span, System.Numerics.Vector3 Origin, TkMatrix4 Wt) pick,
        string action)
    {
        var layer = pick.Layer;
        var span = pick.Span;
        int contourIndex = IndexOfSpan(layer, span);

        float len = 0f;
        int lightning = 0, extrudes = 0;
        var sum = System.Numerics.Vector3.Zero;
        int n = 0;
        int end = Math.Min(layer.Moves.Count, span.Start + Math.Max(0, span.Count));
        for (int i = span.Start; i < end; i++)
        {
            var mv = layer.Moves[i];
            if (mv.Kind != MoveKind.Extrude) continue;
            len += System.Numerics.Vector3.Distance(mv.From, mv.To);
            extrudes++;
            if (mv.IsLightning) lightning++;
            sum += (mv.From + mv.To) * 0.5f;
            n++;
        }
        var mid = n > 0 ? sum / n : System.Numerics.Vector3.Zero;
        bool formbound = extrudes > 0 && lightning * 2 >= extrudes; // majority lightning
        return new PaintLineId(
            LayerIndex: layer.Index,
            LayerZ: layer.Z,
            ContourIndex: contourIndex,
            MoveStart: span.Start,
            MoveCount: span.Count,
            Closed: span.Closed,
            IsFormbound: formbound,
            LengthMm: len,
            Mid: mid,
            Action: action);
    }

    /// <summary>Parent contour index that contains this local section, or -1.
    /// Never synthesizes full-layer contours (that walked every move on large walls).</summary>
    private static int IndexOfSpan(ToolpathLayer layer, ContourSpan span)
    {
        // Exact match first (full contour still possible for tiny loops).
        for (int i = 0; i < layer.Contours.Count; i++)
        {
            var c = layer.Contours[i];
            if (c.Start == span.Start && c.Count == span.Count) return i;
        }
        // Local section → contour that fully contains its move range.
        int secEnd = span.Start + Math.Max(0, span.Count);
        for (int i = 0; i < layer.Contours.Count; i++)
        {
            var c = layer.Contours[i];
            if (c.Start <= span.Start && c.Start + c.Count >= secEnd) return i;
        }
        // No Contours metadata (or section spans gaps): O(1) synthetic index from start.
        return span.Start >= 0 ? span.Start : -1;
    }

    private void BroadcastPaintLineSelection(PaintLineId id, int markDelta)
    {
        string marks = markDelta switch
        {
            > 0 => $"  +{markDelta} mark(s)",
            < 0 => $"  cleared {-markDelta} mark(s)",
            _   => "",
        };
        string msg = $"[edit] {id.Action}  {id.Describe()}{marks}";
        LogPaintConsole(msg);
    }

    private void LogPaintConsole(string msg)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel mvm)
            mvm.Console.Log(msg);
        else
            System.Console.WriteLine(msg);
    }

    private void PushPaintLineSelectionUndo(
        ViewportViewModel vm,
        List<TkVector3>? prevPoly, TkVector3 prevColor, PaintLineId? prevId,
        List<TkVector3> newPoly, TkVector3 newColor, PaintLineId newId)
    {
        if (_paintSelectionUndoSuppress) return;
        // Same line re-clicked → no undo entry (still logged above).
        if (prevId is { } p
            && p.LayerIndex == newId.LayerIndex
            && p.MoveStart == newId.MoveStart
            && p.MoveCount == newId.MoveCount
            && p.Action == newId.Action)
            return;

        string desc = prevId is null
            ? $"Select line {newId.ShortId}"
            : $"Select line {newId.ShortId} (was {prevId.ShortId})";

        // Capture copies — undo stack must not share mutable lists with live state.
        var beforePoly = prevPoly is null ? null : new List<TkVector3>(prevPoly);
        var afterPoly  = new List<TkVector3>(newPoly);
        var beforeId   = prevId;
        var afterId    = newId;
        var beforeCol  = prevColor;
        var afterCol   = newColor;

        vm.UndoRedo?.Push(new PaintLineSelectionAction(
            desc,
            undo: () => ApplyPaintLineSelection(beforePoly, beforeCol, beforeId, fromUndo: true),
            redo: () => ApplyPaintLineSelection(afterPoly, afterCol, afterId, fromUndo: true)));
    }

    private void ApplyPaintLineSelection(
        List<TkVector3>? poly, TkVector3 color, PaintLineId? id, bool fromUndo)
    {
        _paintSelectionUndoSuppress = true;
        try
        {
            _paintSelectedLine = poly is null ? null : new List<TkVector3>(poly);
            _paintSelectedColor = color;
            _paintSelectedId = id;
            _paintHoverLine = _paintSelectedLine;
            if (fromUndo)
            {
                if (id is { } restored)
                    LogPaintConsole($"[edit] undo/redo → {restored.Action}  {restored.Describe()}");
                else
                    LogPaintConsole("[edit] undo/redo → selection cleared");
            }
            GlCanvas.RequestNextFrameRendering();
        }
        finally
        {
            _paintSelectionUndoSuppress = false;
        }
    }

    /// <summary>
    /// Hit-tests the rendered toolpath seam points (each closed contour's start vertex).
    /// On a hit within 12 px, grabs that contour for the slide-along-the-path seam drag.
    /// </summary>
    private bool TryBeginSeamPointDrag(ViewportViewModel vm, float mx, float my, float vpW, float vpH)
    {
        if (vm.IsSeamEditorActive || vm.IsToolpathSeamEditActive || vm.IsBoundaryEditorActive)
            return false;
        if (!vm.ShowSeam || vm.ViewMode is "Body" or "Preview")
            return false;

        const float pickPx = 12f;
        float bestD = pickPx;
        SceneNode? bestNode = null;
        Toolpath? bestTp = null;
        ToolpathLayer? bestLayer = null;
        ContourSpan? bestSpan = null;

        foreach (var (node, tp) in _toolpathByNode)
        {
            // Seam points only render on the selected toolpath — and requiring selection
            // keeps a plain click near the seam free to do normal scene selection.
            if (!node.Visible || !ReferenceEquals(node, _renderer.SelectedNode)) continue;
            _toolpathOriginByNode.TryGetValue(node, out var origin);
            var wt = node.WorldTransform;
            foreach (var layer in tp.Layers)
            {
                foreach (var span in layer.Contours)
                {
                    if (!span.Closed || span.Count < 3) continue;
                    if (span.Start < 0 || span.Start + span.Count > layer.Moves.Count) continue;
                    var v = layer.Moves[span.Start].From;
                    var world = TransformPoint(
                        new TkVector3(v.X - origin.X, v.Y - origin.Y, v.Z - origin.Z), wt);
                    var p = _renderer.ProjectToScreen(new TkVector3(world.X, world.Y, world.Z), vpW, vpH);
                    if (float.IsNaN(p.X)) continue;
                    float d = MathF.Sqrt((p.X - mx) * (p.X - mx) + (p.Y - my) * (p.Y - my));
                    if (d < bestD)
                    {
                        bestD = d; bestNode = node; bestTp = tp;
                        bestLayer = layer; bestSpan = span;
                    }
                }
            }
        }
        if (bestNode is null || bestTp is null || bestLayer is null || bestSpan is null)
            return false;

        // Cache the grabbed loop: vertex 0 = the current seam. World positions drive the
        // preview marker and pixel scale; toolpath-local XY feeds ApplySeams on release.
        _seamDragLoopWorld.Clear();
        _seamDragLoopLocalXY.Clear();
        _toolpathOriginByNode.TryGetValue(bestNode, out var org);
        var w = bestNode.WorldTransform;
        var span2 = bestSpan;
        var cum = new float[span2.Count + 1];
        for (int i = 0; i < span2.Count; i++)
        {
            var v = bestLayer.Moves[span2.Start + i].From;
            var wp = TransformPoint(new TkVector3(v.X - org.X, v.Y - org.Y, v.Z - org.Z), w);
            _seamDragLoopWorld.Add(new TkVector3(wp.X, wp.Y, wp.Z));
            _seamDragLoopLocalXY.Add(new System.Numerics.Vector2(v.X, v.Y));
            if (i > 0)
                cum[i] = cum[i - 1] + TkVector3.Distance(_seamDragLoopWorld[i - 1], _seamDragLoopWorld[i]);
        }
        cum[span2.Count] = cum[span2.Count - 1]
            + TkVector3.Distance(_seamDragLoopWorld[^1], _seamDragLoopWorld[0]);
        if (cum[span2.Count] < 1f) return false;   // degenerate loop

        // World size of one screen pixel at the seam point (for a natural drag feel).
        var s0 = _renderer.ProjectToScreen(_seamDragLoopWorld[0], vpW, vpH);
        var sx = _renderer.ProjectToScreen(_seamDragLoopWorld[0] + TkVector3.UnitX, vpW, vpH);
        var sy = _renderer.ProjectToScreen(_seamDragLoopWorld[0] + TkVector3.UnitY, vpW, vpH);
        float pxPerMm = MathF.Max(
            MathF.Sqrt((sx.X - s0.X) * (sx.X - s0.X) + (sx.Y - s0.Y) * (sx.Y - s0.Y)),
            MathF.Sqrt((sy.X - s0.X) * (sy.X - s0.X) + (sy.Y - s0.Y) * (sy.Y - s0.Y)));
        _seamDragMmPerPixel = pxPerMm > 0.01f ? 1f / pxPerMm : 1f;

        _seamPointDragging = true;
        _seamDragNode      = bestNode;
        _seamDragCumLen    = cum;
        _seamDragOffsetMm  = 0f;
        _seamDragVertex    = 0;
        _renderer.SetSeamGuides([_seamDragLoopWorld[0]], 0);
        GlCanvas.RequestNextFrameRendering();
        return true;
    }

    /// <summary>Slides the seam preview along the grabbed contour by the horizontal mouse delta.</summary>
    private void UpdateSeamPointDrag(float deltaX)
    {
        if (!_seamPointDragging || _seamDragLoopWorld.Count == 0) return;

        float total = _seamDragCumLen[^1];
        _seamDragOffsetMm += deltaX * _seamDragMmPerPixel;
        float off = ((_seamDragOffsetMm % total) + total) % total;

        // Nearest vertex to the arc-length offset (cum[] is ascending).
        int idx = Array.BinarySearch(_seamDragCumLen, off);
        if (idx < 0) idx = ~idx;
        int n = _seamDragLoopWorld.Count;
        int vertex;
        if (idx <= 0) vertex = 0;
        else if (idx >= n) vertex = 0;   // wrapped past the closing edge — back to start
        else vertex = off - _seamDragCumLen[idx - 1] < _seamDragCumLen[idx] - off ? idx - 1 : idx % n;

        if (vertex != _seamDragVertex)
        {
            _seamDragVertex = vertex;
            _renderer.SetSeamGuides([_seamDragLoopWorld[vertex]], 0);
        }
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Applies the dragged seam position to the whole toolpath: every closed loop re-seams
    /// to its vertex nearest the new XY (same semantics as the seam-point editor), the raw
    /// export toolpath follows, and the geometry re-uploads without a re-slice.
    /// </summary>
    private void FinishSeamPointDrag(ViewportViewModel vm)
    {
        bool moved = _seamPointDragging && _seamDragVertex != 0 && _seamDragNode is not null;
        var node   = _seamDragNode;
        var xy     = moved ? _seamDragLoopLocalXY[_seamDragVertex] : default;

        _seamPointDragging = false;
        _seamDragNode      = null;
        _seamDragLoopWorld.Clear();
        _seamDragLoopLocalXY.Clear();
        _seamDragCumLen    = [];
        UpdateSeamGuideMarkers(vm);   // restore the regular guide markers

        if (!moved || node is null || !_toolpathByNode.TryGetValue(node, out var tp))
        {
            GlCanvas.RequestNextFrameRendering();
            return;
        }

        var pts = new List<System.Numerics.Vector2> { xy };
        MassiveSlicer.Core.Slicing.ToolpathSeamEditor.ApplySeams(tp, pts);

        _rawToolpathByNode.TryGetValue(node, out var raw);
        if (raw is not null && !ReferenceEquals(raw, tp))
            MassiveSlicer.Core.Slicing.ToolpathSeamEditor.ApplySeams(raw, pts);

        var snap = GetToolpathSnapshot(node);
        if (snap is not null)
        {
            vm.PendingToolpathReplace.Enqueue(new PendingToolpathEntry
            {
                Toolpath      = tp,
                RawToolpath   = raw ?? tp,
                Node          = node,
                BeadWidth     = snap.BeadWidth,
                LayerHeight   = snap.LayerHeight,
                MaterialColor = snap.MaterialColor,
            });
        }
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Re-seams the selected toolpath in place toward the placed seam points (no re-slice),
    /// then re-renders on the GL thread. The same Toolpath object feeds KRL export, so the
    /// new seam flows through to exported programs.
    /// </summary>
    private void ApplyToolpathSeam(ViewportViewModel vm)
    {
        if (_activeScrubNode is not { } node) return;
        if (!_toolpathByNode.TryGetValue(node, out var tp)) return;

        var pts = vm.SeamGuideDraft
            .Select(g => new System.Numerics.Vector2(g.X, g.Y))
            .ToList();
        if (pts.Count == 0) return;

        int moved = MassiveSlicer.Core.Slicing.ToolpathSeamEditor.ApplySeams(tp, pts);

        // Keep the raw (save/export source) consistent when it is a distinct object.
        _rawToolpathByNode.TryGetValue(node, out var raw);
        if (raw is not null && !ReferenceEquals(raw, tp))
            MassiveSlicer.Core.Slicing.ToolpathSeamEditor.ApplySeams(raw, pts);

        // Geometry is unchanged (loops only rotate), so re-upload without touching the pose.
        var snap = GetToolpathSnapshot(node);
        if (snap is not null)
        {
            vm.PendingToolpathReplace.Enqueue(new PendingToolpathEntry
            {
                Toolpath      = tp,
                RawToolpath   = raw ?? tp,
                Node          = node,
                BeadWidth     = snap.BeadWidth,
                LayerHeight   = snap.LayerHeight,
                MaterialColor = snap.MaterialColor,
            });
        }

        // Apply is a one-shot: leave edit mode and clear the placement markers.
        vm.DoneToolpathSeamCommand.Execute(null);
        UpdateSeamGuideMarkers(vm);
        GlCanvas.RequestNextFrameRendering();

        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel mvm)
            mvm.Console.Log($"[seam] Re-seamed {moved} loop(s) toward {pts.Count} seam point(s).");
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
        ApplyTransformLink(node);

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
        if (_kbTransformAxis == GizmoAxis.None)
            _gizmoDragAxis = GizmoAxis.All;

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

        if (_toolIsDragging)
        {
            _gizmoDragAxis = _kbTransformAxis == GizmoAxis.None ? GizmoAxis.All : _kbTransformAxis;
            if (_gizmoDragAxis == GizmoAxis.All)
            {
                var rot = Matrix4.CreateFromAxisAngle(axisDir, angle);
                var (r0, r1, r2) = _ikDragInitialTargetRot;
                r0 = Vector3.Normalize(TransformDir(r0, rot));
                r1 = Vector3.Normalize(TransformDir(r1, rot));
                r2 = Vector3.Normalize(TransformDir(r2, rot));
                _ikDragTargetRot = (r0, r1, r2);
            }
            else
            {
                ApplyToolRotationDelta(angle);
            }
            return;
        }

        var rotNode = Matrix4.CreateFromAxisAngle(axisDir, angle);
        var lt  = _kbTransformInitialLocal;
        var p   = new Vector3(lt.M41, lt.M42, lt.M43);
        lt      = lt * rotNode;
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

    private void RememberCommittedTransform(ViewportViewModel vm, SceneNode node)
    {
        _lastCommittedTransformNode = node;
        _lastCommittedTransform     = node.LocalTransform;
        foreach (var f in ResolveLinkedNodes(vm, node))
            _lastCommittedFollowerTransform[f] = f.LocalTransform;
    }

    /// <summary>Linked nodes (a model's toolpath, etc.) whose LocalTransform has moved on since
    /// the last commit — paired with the baseline it moved on from, so they can be folded into
    /// the same undo entry as the primary node instead of being left to drift out of sync.</summary>
    private List<(SceneNode Node, Matrix4 Before, Matrix4 After)> CaptureFollowerDeltas(
        ViewportViewModel vm, SceneNode node)
    {
        var result = new List<(SceneNode, Matrix4, Matrix4)>();
        foreach (var f in ResolveLinkedNodes(vm, node))
        {
            var after  = f.LocalTransform;
            var before = _lastCommittedFollowerTransform.TryGetValue(f, out var b) ? b : after;
            if (!Matrix4Util.NearlyEquals(before, after))
                result.Add((f, before, after));
        }
        return result;
    }

    private void RecordTransformUndo(
        ViewportViewModel vm,
        SceneNode node,
        Matrix4 before,
        Matrix4 after,
        string description)
    {
        var followerDeltas  = CaptureFollowerDeltas(vm, node);
        bool primaryChanged = !Matrix4Util.NearlyEquals(before, after);
        if (!primaryChanged && followerDeltas.Count == 0) return;

        if (followerDeltas.Count == 0)
        {
            vm.UndoRedo?.Push(new NodeTransformAction(
                node, before, after, description, () => OnTransformApplied(vm)));
        }
        else
        {
            var entries = new List<(SceneNode, Matrix4, Matrix4)>();
            if (primaryChanged) entries.Add((node, before, after));
            entries.AddRange(followerDeltas);
            vm.UndoRedo?.Push(new LinkedTransformAction(
                entries, description, () => OnTransformApplied(vm)));
        }

        RememberCommittedTransform(vm, node);
        if (DataContext is ViewportViewModel devVm && devVm.IsDevMode && IsDevNode(node))
            ScheduleDevTransformAutoSave(devVm, node);
    }

    private void OnTransformApplied(ViewportViewModel vm)
    {
        SyncSelectionTransformDisplay(vm);
        GlCanvas.RequestNextFrameRendering();
        RevalidateSelectedToolpath();
        if (_renderer.SelectedNode is { } node)
            RememberCommittedTransform(vm, node);
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

        // Unlock the matching outliner rows (e.g. "Print Bed") while dev mode is on;
        // re-lock them the moment it turns off.
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is ViewportViewModel lockVm)
                lockVm.SetCellEnvironmentDevLock(IsDevNode, locked: !enabled);
        });

        if (!enabled && _renderer.SelectedNode is { } sel && IsDevNode(sel))
        {
            _renderer.Select(null);
            UpdateFocusOverlay();
        }
    }

    private bool IsDevNode(SceneNode? node)
        => node is not null && _devNodeKinds.ContainsKey(node);

    /// <summary>Nearest ancestor (or self) registered as a dev-editable prop, else null.</summary>
    private SceneNode? FindDevNodeRoot(SceneNode? node)
    {
        for (var cur = node; cur is not null; cur = cur.Parent)
            if (_devNodeKinds.ContainsKey(cur))
                return cur;
        return null;
    }

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

        var w = IsToolNodeSelected() && _renderer.TcpFrameMatrix is { } tcp
            ? tcp
            : node.WorldTransform;

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
        RememberCommittedTransform(vm, node);
    }

    private SceneNode? _lastOutlinerSyncedNode;
    private bool _wasToolNodeSelected;

    private void UpdateFocusOverlay()
    {
        if (_vm is not { } vm) return;

        var selected = _renderer.SelectedNode;
        if (!ReferenceEquals(selected, _lastOutlinerSyncedNode) && vm.SelectedScanCount < 2)
        {
            vm.SetOutlinerSelection(selected);
            _lastOutlinerSyncedNode = selected;
        }

        vm.HasSelection       = selected is not null;
        bool isToolpath       = selected is not null && _renderer.IsToolpathNode(selected);
        bool isToolNode       = IsToolNodeSelected();
        bool isDevNode        = vm.IsDevMode && IsDevNode(selected);
        bool multiToolpath    = _renderer.SelectedToolpathCount >= 2;
        vm.CanMergeToolpaths  = multiToolpath;
        vm.SyncSequenceRowHighlights(_renderer.SelectedToolpaths);
        vm.IsToolpathSelected = isToolpath && !multiToolpath;
        bool isMerged = isToolpath && selected is not null && _mergedByNode.ContainsKey(selected);
        vm.IsMergedToolpathSelected = isMerged;
        if (isMerged && _mergedByNode.TryGetValue(selected!, out var mergedRec))
            vm.SyncMergedSettingsDisplay(mergedRec.RetractionHeightMm, mergedRec.TravelSpeedMps * 1000.0);
        vm.UpdateSliceCommand?.RaiseCanExecuteChanged();
        vm.IsDevObjectSelected = isDevNode;
        vm.DevSelectedLabel    = isDevNode && selected is not null ? DevLabel(selected) : "";
        bool isPrintBed = isDevNode && selected is not null
                          && _devNodeKinds.TryGetValue(selected, out var bedMeta)
                          && bedMeta.Kind == "bed"
                          && vm.ActiveCell?.Bed.IsRotaryPrintBed != true;
        vm.IsPrintBedSelected = isPrintBed;
        if (isPrintBed)
            vm.SyncBedGridSize(_bedWidth, _bedDepth);
        vm.HasMeshSelected     = selected is not null && !isToolpath && !isToolNode && !isDevNode
                                 && vm.FindUserMeshOutlinerItem(selected) is not null;
        vm.CanUngroup         = selected is not null && !isToolpath && !isToolNode && selected.Children.Count > 0;
        vm.CanExplode         = selected is not null && !isToolpath && !isToolNode && HasExplodableMeshes(selected);
        vm.CanMeshCleanup     = selected is not null && !isToolpath && !isToolNode && HasCleanableMeshes(selected);
        vm.CanCutTool         = selected is not null && !isToolpath && !isToolNode && HasCleanableMeshes(selected);

        if (selected is null)
            SetGizmoMode(GizmoMode.None);
        else if (isToolNode)
        {
            vm.Robot?.Desync();
            SetGizmoMode(GizmoMode.Translate);
        }
        if (isToolNode && !_wasToolNodeSelected)
            vm.OnToolheadSelected?.Invoke();
        _wasToolNodeSelected = isToolNode;

        // Use ResetScrubIndex (not the public setters) so the IK callback is NOT triggered
        // by the programmatic reset -- the robot only follows scrubbing the user initiates.
        if (isToolpath && selected is not null && _toolpathByNode.TryGetValue(selected, out var tp))
        {
            // Re-selecting the same toolpath keeps the scrub position (keyframe workflow).
            bool sameSession = ReferenceEquals(_activeScrubNode, selected)
                               && ReferenceEquals(vm.ActiveScrubToolpath, tp);
            _activeScrubNode = selected;
            if (!sameSession)
                vm.ResetScrubIndex(tp.Layers.Sum(l => l.Moves.Count), tp);
            vm.IsScrubSessionActive = true;
            ValidateToolpathAsync(selected, tp);
            if (vm.AdditiveSettings is { } ads)
                ApplyToolpathStats(vm, tp, ads);
        }
        else if (_activeScrubNode is { } keepNode && _toolpathByNode.ContainsKey(keepNode))
        {
            // Persistent timeline: deselecting (or clicking the TCP / another object)
            // keeps the scrub session alive as long as the toolpath still exists.
            vm.IsScrubSessionActive = true;
        }
        else
        {
            _activeScrubNode = null;
            vm.IsScrubSessionActive = false;
            vm.ResetScrubIndex(0, null);
            ClearToolpathStats(vm);
            vm.StatsReachability = "";
            vm.IsValidating      = false;
            vm.SetScrubMarkers([], []);
            _validationCts?.Cancel();
        }

        SyncSelectionTransformDisplay(vm);
    }

    // -- Speed/RPM value tags ------------------------------------------------------

    private const float ViewTagBandMm = 152.4f;   // 6 inches
    private DispatcherTimer? _viewTagTimer;
    private Toolpath? _viewTagCachedToolpath;
    private readonly List<(NVec3 Local, float Scale, float TempC)> _viewTagBands = [];

    private void StartViewTagTimer(ViewportViewModel vm)
    {
        _viewTagTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _viewTagTimer.Tick += (_, _) => UpdateViewTags(vm);
        _viewTagTimer.Start();
    }

    /// <summary>Projects one value tag per 6" of height beside the toolpath: layer speed
    /// (Speed view) or extrusion RPM % with the material flow rate (RPM view).</summary>
    private void UpdateViewTags(ViewportViewModel vm)
    {
        if (vm.ViewMode is not ("Speed" or "RPM" or "Thermal"))
        {
            if (vm.ViewTags.Count > 0) vm.ViewTags = [];
            return;
        }

        var pair = _toolpathByNode.FirstOrDefault(kv => kv.Key.Visible);
        if (pair.Value is null)
        {
            if (vm.ViewTags.Count > 0) vm.ViewTags = [];
            return;
        }
        var (node, toolpath) = (pair.Key, pair.Value);
        _toolpathOriginByNode.TryGetValue(node, out var origin);

        // Rebuild band samples when the toolpath changes: one sample per height band,
        // at the band layer's +X-most extrude endpoint (origin-relative local space).
        if (!ReferenceEquals(_viewTagCachedToolpath, toolpath))
        {
            _viewTagCachedToolpath = toolpath;
            _viewTagBands.Clear();
            if (toolpath.Layers.Count > 0)
            {
                float nextBand = toolpath.Layers[0].Z;
                foreach (var layer in toolpath.Layers)
                {
                    if (layer.Z < nextBand) continue;
                    NVec3 best = default; bool found = false; float scale = 1f;
                    int stride = Math.Max(1, layer.Moves.Count / 400);
                    for (int i = 0; i < layer.Moves.Count; i += stride)
                    {
                        var m = layer.Moves[i];
                        if (m.Kind != MoveKind.Extrude) continue;
                        if (!found || m.To.X > best.X) { best = m.To; found = true; scale = m.PrintSpeedScale; }
                    }
                    if (found)
                    {
                        _viewTagBands.Add((new NVec3(best.X - origin.X, best.Y - origin.Y, best.Z - origin.Z),
                                           scale, layer.ThermalTempC));
                        nextBand += ViewTagBandMm;
                    }
                }
            }
        }

        float vpW = (float)GlCanvas.Bounds.Width;
        float vpH = (float)GlCanvas.Bounds.Height;
        if (vpW < 10 || vpH < 10) return;

        float baseSpeed = (float)(vm.AdditiveSettings?.PrintSpeed ?? 100.0);
        float rpmBase   = vm.AdditiveSettings?.GetEffectiveExtrusionSpeedPercent() ?? 100f;
        float flow      = (float)(vm.AdditiveSettings?.SelectedPreset?.FlowRateFor(
                              vm.AdditiveSettings?.ActiveExtruderIsHf ?? false) ?? 0.463);

        var wt   = node.WorldTransform;
        var tags = new List<ViewportViewModel.ViewTag>(_viewTagBands.Count);
        foreach (var (local, scale, tempC) in _viewTagBands)
        {
            var world = new TkVector3(
                local.X * wt.M11 + local.Y * wt.M21 + local.Z * wt.M31 + wt.M41,
                local.X * wt.M12 + local.Y * wt.M22 + local.Z * wt.M32 + wt.M42,
                local.X * wt.M13 + local.Y * wt.M23 + local.Z * wt.M33 + wt.M43);
            var p = _renderer.ProjectToScreen(world, vpW, vpH);
            if (float.IsNaN(p.X) || p.X < -50 || p.X > vpW + 50 || p.Y < -20 || p.Y > vpH + 20)
                continue;

            var overlayPt = this.TranslatePoint(new Point(p.X + 14, p.Y - 9), OverlayView)
                            ?? new Point(p.X + 14, p.Y - 9);
            string text = vm.ViewMode switch
            {
                "Speed"   => $"{baseSpeed * scale:0} mm/s",
                "Thermal" => float.IsNaN(tempC) ? "— °C" : $"{tempC:0} °C interface",
                _         => $"{rpmBase * scale:0}% RPM (Flowrate {flow:0.###})",
            };
            tags.Add(new ViewportViewModel.ViewTag(overlayPt.X, overlayPt.Y, text));
        }
        vm.ViewTags = tags;
    }

    // -- TCP keyframes -----------------------------------------------------------

    /// <summary>
    /// Records (or updates) a TCP offset keyframe at the current scrub index: the delta
    /// between where the user dragged the TCP and the nominal toolpath position there.
    /// The offsets are eased over neighbouring moves and baked into the rendered path.
    /// </summary>
    private void AddTcpKeyframeAtCurrentIndex(ViewportViewModel vm)
    {
        if (_activeScrubNode is not { } node) return;
        if (!_scrubCacheByNode.TryGetValue(node, out var cache) || cache.Length == 0) return;

        int index = Math.Clamp(vm.ToolpathScrubIndex, 0, cache.Length - 1);

        // Nominal path position at this move (same transform chain as ScrubIkForNode).
        var (pos, _) = cache[index];
        NVec3 nominal;
        if (_toolpathOriginByNode.TryGetValue(node, out var origin))
        {
            var wt = node.WorldTransform;
            float lx = pos.X - origin.X, ly = pos.Y - origin.Y, lz = pos.Z - origin.Z;
            nominal = new NVec3(
                lx * wt.M11 + ly * wt.M21 + lz * wt.M31 + wt.M41,
                lx * wt.M12 + ly * wt.M22 + lz * wt.M32 + wt.M42,
                lx * wt.M13 + ly * wt.M23 + lz * wt.M33 + wt.M43);
        }
        else nominal = pos;

        // Where the TCP actually is now (after the user's drag).
        if (_renderer.TcpFrameMatrix is not { } tcpMat) return;
        var tcpWorld = new NVec3(tcpMat.M41, tcpMat.M42, tcpMat.M43);
        var offset   = tcpWorld - nominal;

        if (!_tcpKeyframesByNode.TryGetValue(node, out var keys))
            _tcpKeyframesByNode[node] = keys = [];
        if (!_keyframeBaseByNode.ContainsKey(node) && _toolpathByNode.TryGetValue(node, out var basePath))
            _keyframeBaseByNode[node] = basePath;

        int influence = Math.Max((int)vm.KeyframeSmoothing, 5);
        var existing  = keys.Find(k => k.Index == index);
        if (existing is not null)
        {
            existing.Offset = offset;
        }
        else
        {
            keys.Add(new TcpKey
            {
                Index = index, Offset = offset,
                InfluenceLeft = influence, InfluenceRight = influence,
            });
            keys.Sort((a, b) => a.Index.CompareTo(b.Index));
        }
        _selectedTcpKey = keys.FindIndex(k => k.Index == index);

        ApplyTcpKeyframes(vm, node);
    }

    /// <summary>Removes all keyframes for the active toolpath and restores the pristine path.</summary>
    private void ClearTcpKeyframes(ViewportViewModel vm)
    {
        if (_activeScrubNode is not { } node) return;
        _tcpKeyframesByNode.Remove(node);
        if (_keyframeBaseByNode.Remove(node, out var basePath))
            SwapScrubbedToolpath(vm, node, basePath);
        vm.HasTcpKeyframes = false;
        _selectedTcpKey = -1;
        vm.SetScrubKeyframes([]);
    }

    /// <summary>Drops keyframe state without restoring (a fresh slice replaced the path).</summary>
    private void ClearTcpKeyframeState(SceneNode node, ViewportViewModel vm)
    {
        _tcpKeyframesByNode.Remove(node);
        _keyframeBaseByNode.Remove(node);
        if (ReferenceEquals(node, _activeScrubNode))
        {
            vm.HasTcpKeyframes = false;
            _selectedTcpKey = -1;
            vm.SetScrubKeyframes([]);
        }
    }

    /// <summary>Keyframe diamond clicked: select it and jump the scrubber to its moment.</summary>
    private void JumpToTcpKeyframe(ViewportViewModel vm, int keyIdx)
    {
        if (_activeScrubNode is not { } node) return;
        if (!_tcpKeyframesByNode.TryGetValue(node, out var keys)) return;
        if (keyIdx < 0 || keyIdx >= keys.Count) return;
        _selectedTcpKey = keyIdx;
        vm.SetScrubKeyframes(
            keys.Select(k => (k.Index, k.InfluenceLeft, k.InfluenceRight)).ToArray(),
            keyIdx);
        vm.ToolpathScrubIndex = keys[keyIdx].Index;   // drives the robot to this moment
    }

    /// <summary>Influence tick dragged: resize the key's ease window (bake on release).</summary>
    private void DragTcpKeyframeInfluence(ViewportViewModel vm, int keyIdx, bool left, double px, bool commit)
    {
        if (_activeScrubNode is not { } node) return;
        if (!_tcpKeyframesByNode.TryGetValue(node, out var keys)) return;
        if (keyIdx < 0 || keyIdx >= keys.Count) return;

        var k   = keys[keyIdx];
        int idx = vm.ScrubIndexAtPixel(px);
        if (left) k.InfluenceLeft  = Math.Max(k.Index - idx, 5);
        else      k.InfluenceRight = Math.Max(idx - k.Index, 5);

        _selectedTcpKey = keyIdx;
        vm.SetScrubKeyframes(
            keys.Select(x => (x.Index, x.InfluenceLeft, x.InfluenceRight)).ToArray(),
            keyIdx);
        if (commit)
            ApplyTcpKeyframes(vm, node);   // heavy re-bake only on release
    }

    /// <summary>Re-bakes the eased keyframe offsets into the toolpath and re-poses the robot.</summary>
    private void ApplyTcpKeyframes(ViewportViewModel vm, SceneNode node)
    {
        if (!_tcpKeyframesByNode.TryGetValue(node, out var keys) || keys.Count == 0) return;
        if (!_keyframeBaseByNode.TryGetValue(node, out var basePath)) return;

        var modified = BuildOffsetToolpath(basePath, keys);
        SwapScrubbedToolpath(vm, node, modified);

        vm.HasTcpKeyframes = true;
        vm.SetScrubKeyframes(
            keys.Select(k => (k.Index, k.InfluenceLeft, k.InfluenceRight)).ToArray(),
            _selectedTcpKey);

        ScrubIkForNode(node, vm.ToolpathScrubIndex);
    }

    /// <summary>In-place toolpath swap that keeps the scrub position and node pose.</summary>
    private void SwapScrubbedToolpath(ViewportViewModel vm, SceneNode node, Toolpath toolpath)
    {
        _toolpathByNode[node]    = toolpath;
        _scrubCacheByNode[node]  = BuildScrubCache(toolpath);
        _rawToolpathByNode.TryGetValue(node, out var raw);

        var preservedLocal = node.LocalTransform;
        if (!_toolpathOriginByNode.TryGetValue(node, out var preservedOrigin))
            preservedOrigin = new NVec3(preservedLocal.M41, preservedLocal.M42, preservedLocal.M43);

        vm.PendingToolpathReplace.Enqueue(new PendingToolpathEntry
        {
            Toolpath                = toolpath,
            RawToolpath             = raw ?? toolpath,
            Node                    = node,
            BeadWidth               = (float)(vm.AdditiveSettings?.BeadWidth   ?? 6.0),
            LayerHeight             = (float)(vm.AdditiveSettings?.LayerHeight ?? 3.0),
            PreserveRelativePose    = true,
            PreservedLocalTransform = preservedLocal,
            PreservedOrigin         = preservedOrigin,
        });
        if (ReferenceEquals(node, _activeScrubNode))
            vm.ReplaceScrubToolpathInPlace(toolpath);
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Clones <paramref name="src"/> with the keyframe offsets applied: each key
    /// contributes a smoothstep bell over its own left/right influence windows; where
    /// bells overlap the offsets blend by normalised weight, so adjacent keyframes
    /// ease into each other and everything fades to zero outside the influence spans.
    /// </summary>
    private static Toolpath BuildOffsetToolpath(Toolpath src, List<TcpKey> keys)
    {
        NVec3 OffsetAt(double v)
        {
            double wsum = 0;
            var    acc  = NVec3.Zero;
            foreach (var k in keys)
            {
                double d    = v - k.Index;
                double span = d < 0 ? Math.Max(k.InfluenceLeft, 1) : Math.Max(k.InfluenceRight, 1);
                double t    = Math.Abs(d) / span;
                if (t >= 1.0) continue;
                double x = 1.0 - t;
                double wgt = x * x * (3.0 - 2.0 * x);
                acc  += k.Offset * (float)wgt;
                wsum += wgt;
            }
            if (wsum <= 0) return NVec3.Zero;
            return acc / (float)wsum * (float)Math.Min(wsum, 1.0);
        }

        var dst = new Toolpath();
        int fi  = 0;
        foreach (var layer in src.Layers)
        {
            var nl = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height      = layer.Height,
                PlaneNormal = layer.PlaneNormal,
            };
            nl.Contours.AddRange(layer.Contours);
            foreach (var m in layer.Moves)
            {
                nl.Moves.Add(m with { From = m.From + OffsetAt(fi), To = m.To + OffsetAt(fi + 1) });
                fi++;
            }
            dst.Layers.Add(nl);
        }
        return dst;
    }

    /// <summary>
    /// X-key delete: removes the selected user mesh or toolpath without confirmation
    /// and pushes a <see cref="NodeDeleteAction"/> so Cmd/Ctrl+Z restores it (outliner
    /// row, meshes re-uploaded from retained CPU data, toolpaths rebuilt from snapshots).
    /// </summary>
    private bool TryDeleteSelectedWithUndo()
    {
        if (DataContext is not ViewportViewModel vm) return false;
        var selected = _renderer.SelectedNode;
        if (selected is null) return false;

        bool isToolpath = _renderer.IsToolpathNode(selected);
        var  node       = selected;
        if (!isToolpath)
        {
            // Only user imports — never the robot/toolhead/bed/dev objects; effectors
            // have their own toggle lifecycle (pattern panel buttons).
            var userItem = vm.FindUserMeshOutlinerItem(selected);
            if (userItem is null || userItem.IsEffector) return false;
            if (IsToolNodeSelected() || IsDevNode(selected)) return false;
            node = userItem.Node;
        }

        if (vm.CaptureOutlinerContext(node) is not { } ctx) return false;

        // Snapshot every toolpath being deleted so undo can rebuild the renderers.
        var restores = new List<NodeDeleteAction.ToolpathRestore>();
        void Capture(SceneNode n)
        {
            if (GetToolpathSnapshot(n) is not { } snap) return;
            _toolpathOriginByNode.TryGetValue(n, out var origin);
            restores.Add(new NodeDeleteAction.ToolpathRestore(n, snap, n.LocalTransform, origin));
        }
        if (isToolpath) Capture(node);
        else
            foreach (var child in ctx.Item.Children)
                if (child.IsToolpath) Capture(child.Node);

        vm.RequestDeleteNode(node);
        vm.UndoRedo?.Push(new NodeDeleteAction(vm, ctx.Item, ctx.Parent, ctx.Index, node, isToolpath, restores));
        return true;
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
        if (_vm?.ActiveScrubToolpath is null || _activeScrubNode is null) return;
        ScrubIkForNode(_activeScrubNode, index);
    }

    /// <summary>Simulate-timeline hook: drives the robot along the first visible
    /// toolpath (no selection required), mapping 0–1 progress onto its move range.</summary>
    /// <summary>Toolpath display name = the source mesh's name (file extension stripped),
    /// so KRL export dialogs carry it straight through to the .src filename.</summary>
    private static string ToolpathNameFrom(string meshName)
    {
        var n = meshName.Trim();
        foreach (var ext in new[] { ".stl", ".obj", ".glb", ".gltf", ".3mf", ".ply", ".step", ".stp" })
            if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { n = n[..^ext.Length].TrimEnd(); break; }
        return n.Length > 0 ? n : "Toolpath";
    }

    private void SimScrubIk(double progress)
    {
        foreach (var (node, cache) in _scrubCacheByNode)
        {
            if (!node.Visible || cache.Length == 0) continue;
            SimScrubResolveVisible(node, cache, progress);
            return;
        }
    }

    private void SimScrubResolveVisible(SceneNode node, (NVec3, NVec3)[] cache, double progress)
    {
        int index = (int)Math.Round(Math.Clamp(progress, 0.0, 1.0) * (cache.Length - 1));
        ScrubIkForNode(node, index);
    }

    private void ScrubIkForNode(SceneNode scrubNode, int index)
    {
        var vm       = _vm;
        var solver   = _ikSolver;
        var robot    = vm?.Robot;

        if (vm is null || solver is null || robot is null) return;

        // Desync live feed so IK drives the robot instead of the C3Bridge stream.
        robot.Desync();

        if (!_scrubCacheByNode.TryGetValue(scrubNode, out var scrubCache) ||
            scrubCache.Length == 0) return;
        var (pos, planeNormal) = scrubCache[Math.Clamp(index, 0, scrubCache.Length - 1)];

        // Apply the node's current world transform so scrubbing follows a moved toolpath.
        // Stored Toolpath positions are in the original sliced world space; the renderer
        // stores them as (pos − origin) and uses LocalTransform to put them back in world
        // space.  If the user has moved the node we need to apply that same transform here.
        var node = scrubNode;
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

        // Prefer pre-solved joints + planned E1 from validation (instant, matches reachability).
        int moveIdx = Math.Clamp(index > 0 ? index - 1 : 0, 0, int.MaxValue);
        if (_ikSolutionsByNode.TryGetValue(scrubNode, out var sols)
            && sols is { Length: > 0 })
        {
            moveIdx = Math.Clamp(moveIdx, 0, sols.Length - 1);
            var angles = sols[moveIdx];
            float? e1 = null;
            if (_e1MmByNode.TryGetValue(scrubNode, out var e1s) && e1s.Length > 0)
                e1 = e1s[Math.Clamp(moveIdx, 0, e1s.Length - 1)];
            SetRobotAnglesDirectly(angles, e1);
            return;
        }

        // Fallback live IK: target relative to planned (or live) rail pose.
        float scrubE1 = (float)robot.E1;
        if (_e1MmByNode.TryGetValue(scrubNode, out var e1Arr) && e1Arr.Length > 0)
            scrubE1 = e1Arr[Math.Clamp(moveIdx, 0, e1Arr.Length - 1)];
        else if (_toolpathByNode.TryGetValue(scrubNode, out var tpScrub))
        {
            int mi = 0, want = Math.Max(0, moveIdx);
            foreach (var layer in tpScrub.Layers)
            foreach (var mv in layer.Moves)
            {
                if (mi == want && !float.IsNaN(mv.E1Mm))
                {
                    scrubE1 = mv.E1Mm;
                    break;
                }
                mi++;
            }
        }

        RefreshIkSceneKinematics();
        var cell = vm.ActiveCell;
        TkVector3 targetRobroot;
        if (robot.IsRobotRail && cell?.RobotRail is { } rail
            && vm.AdditiveSettings is { E1MotionEnabled: true })
        {
            var homeWorld = new NVec3(
                cell.Robot.WorldPosition.X, cell.Robot.WorldPosition.Y, cell.Robot.WorldPosition.Z);
            var baseW = RailE1Planner.BaseWorld(homeWorld, rail, scrubE1);
            targetRobroot = new TkVector3(
                worldPos.X - baseW.X, worldPos.Y - baseW.Y, worldPos.Z - baseW.Z);
        }
        else
        {
            var robrootPos = GetLiveRobrootWorldPos();
            targetRobroot = worldPos - robrootPos;
        }

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
        float e1Capture = scrubE1;
        bool setE1 = robot.IsRobotRail && vm.AdditiveSettings is { E1MotionEnabled: true };

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
                if (setE1)
                    robot.E1 = Math.Round(e1Capture, 2);
                GlCanvas.RequestNextFrameRendering();
            });
        }, cts.Token);
    }

    /// <summary>
    /// Runs IK for every move in <paramref name="toolpath"/> (background task) and enqueues
    /// a reachability bool[] into <see cref="_pendingReachability"/> for the GL thread to apply.
    /// Any previous validation for a different toolpath is cancelled first.
    /// </summary>
    /// <summary>
    /// Builds (or reuses) the digital-twin collision world: robot link hulls +
    /// environment triangle BVH. UI thread only — reads the live scene graph.
    /// Returns null (collision checking disabled) when the robot model can't be
    /// extracted; never throws.
    /// </summary>
    private CollisionWorld? BuildOrGetCollisionWorld()
    {
        if (_collisionWorld is not null) return _collisionWorld;
        var vm = _vm;
        var robotRoot = _robotBaseNode;
        var fk = _fkController;
        if (vm is null || robotRoot is null || fk is null) return null;

        try
        {
            var joints = vm.ActiveCell?.Robot.Joints ?? [];
            if (joints.Count < 6) return null;
            Action<string> log = s => vm.OnDevLog?.Invoke(s);

            var robot = CollisionModelExtractor.ExtractRobot(
                robotRoot, joints, fk.RestPoses, log, _currentToolNode, _toolMeshMatrix);
            if (robot is null) return null;

            var env = CollisionModelExtractor.ExtractEnvironment(
                _renderer.SceneRoot, robotRoot,
                _currentToolNode is null ? null : [_currentToolNode], log);

            var world = new CollisionWorld(robot, env, new CollisionSettings());

            // Baseline: exclude self pairs already within margin at the cell home pose
            // (conservative-hull overlap at rest must not flood the results).
            var home = vm.ActiveCell?.Robot.HomePosition;
            if (home is { Length: >= 6 })
            {
                var chainRoot = CollisionModelExtractor.ToNumericsMatrix(fk.LiveChainRootTransform());
                Span<float> homeKrl = [home[0], home[1], home[2], home[3], home[4], home[5]];
                var excluded = robot.ApplySelfBaseline(homeKrl, chainRoot,
                    MathF.Max(world.Settings.SelfClearanceMm, 1f));
                foreach (var (a, b, d) in excluded)
                    log($"[collision] baseline-excluded {RobotCollisionModel.LinkNames[a]} ↔ " +
                        $"{RobotCollisionModel.LinkNames[b]} ({d:F1} at home)");
            }

            _collisionWorld = world;
            return world;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[collision] extraction failed: {ex.Message}");
            return null;
        }
    }

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
        RefreshIkSceneKinematics();
        var   robroot = GetLiveRobrootWorldPos();
        float offA    = addSettings is not null ? (float)addSettings.ToolheadA : 0f;
        float offB    = addSettings is not null ? (float)addSettings.ToolheadB : 0f;
        float offC    = addSettings is not null ? (float)addSettings.ToolheadC : 0f;
        bool  hasOff  = addSettings is not null;
        var   seed    = new float[]
        {
            (float)robot.A1, (float)robot.A2, (float)robot.A3,
            (float)robot.A4, (float)robot.A5, (float)robot.A6,
        };
        // Plan E1 (reachability-aware) before IK when rail motion is enabled.
        bool e1Motion = addSettings is { E1MotionEnabled: true }
                        && vm?.ActiveCell?.RobotRail is not null;
        float homeE1 = (float)robot.E1;
        var cellForE1 = vm?.ActiveCell;
        var homeWorld = cellForE1 is not null
            ? new NVec3(cellForE1.Robot.WorldPosition.X, cellForE1.Robot.WorldPosition.Y, cellForE1.Robot.WorldPosition.Z)
            : new NVec3(robroot.X, robroot.Y, robroot.Z);

        // Digital-twin collision captures (UI thread; immutable afterward).
        var collisionWorld = BuildOrGetCollisionWorld();
        var chainRootColl = _fkController is { } fkColl
            ? CollisionModelExtractor.ToNumericsMatrix(fkColl.LiveChainRootTransform())
            : System.Numerics.Matrix4x4.Identity;
        var wtColl = CollisionModelExtractor.ToNumericsMatrix(wt);
        var originColl = new NVec3(origin.X, origin.Y, origin.Z);
        float beadWidthColl = addSettings is not null ? (float)addSettings.BeadWidth : 6f;

        Task.Run(() =>
        {
            int total = 0;
            foreach (var layer in toolpath.Layers) total += layer.Moves.Count;
            if (total == 0) return;

            // Bake planned E1 onto moves (workspace envelope + mid-reach scoring).
            if (e1Motion && cellForE1?.RobotRail is { } railPlan && addSettings is not null)
            {
                PlanRailE1ForExport(toolpath, cellForE1, addSettings, origin, wt, homeE1);
            }
            else
            {
                foreach (var layer in toolpath.Layers)
                foreach (var m in layer.Moves)
                    m.E1Mm = float.NaN;
            }

            var e1PerMove = new float[total];
            var targets    = new TkVector3[total];
            var normals    = new TkVector3[total];
            int mi         = 0;
            var lastNormN  = NVec3.UnitZ; // last valid extrude normal; held through transitions
            var railCfg    = cellForE1?.RobotRail;
            foreach (var layer in toolpath.Layers)
            {
                foreach (var move in layer.Moves)
                {
                    var (pos, _) = cache[Math.Min(mi + 1, cache.Length - 1)];
                    float lx = pos.X - origin.X, ly = pos.Y - origin.Y, lz = pos.Z - origin.Z;
                    var world = new TkVector3(
                        lx * wt.M11 + ly * wt.M21 + lz * wt.M31 + wt.M41,
                        lx * wt.M12 + ly * wt.M22 + lz * wt.M32 + wt.M42,
                        lx * wt.M13 + ly * wt.M23 + lz * wt.M33 + wt.M43);

                    float e1 = !float.IsNaN(move.E1Mm) ? move.E1Mm : homeE1;
                    e1PerMove[mi] = e1;

                    // Target in ROBROOT of the carriage at planned E1 (pure translation rail).
                    if (e1Motion && railCfg is { } rail)
                    {
                        var baseW = RailE1Planner.BaseWorld(homeWorld, rail, e1);
                        targets[mi] = new TkVector3(
                            world.X - baseW.X, world.Y - baseW.Y, world.Z - baseW.Z);
                    }
                    else
                        targets[mi] = world - robroot;

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

            // -- TCP auto-rotate repair -------------------------------------------
            // The nozzle is rotationally symmetric, so spinning it about its own axis
            // (KUKA C offset) is print-neutral — but it swings the flange/wrist into a
            // different configuration. For each flagged span, search for the smallest
            // spin that clears the wrist singularity, ramp it in/out smoothly over
            // neighbouring moves, and re-solve IK for the affected range.
            {
                bool anyBad = false;
                for (int i = 0; i < total && !anyBad; i++)
                    anyBad = !result[i] || singularity[i];

                if (anyBad)
                {
                    var flatMoves = new ToolpathMove[total];
                    {
                        int fi = 0;
                        foreach (var layer in toolpath.Layers)
                            foreach (var mv in layer.Moves)
                            { if (fi < total) flatMoves[fi] = mv; fi++; }
                    }

                    const int   Ramp  = 60;   // moves over which yaw ramps in/out
                    const float MinA5 = 6f;   // deg of wrist margin required
                    var yawByMove = new float[total];
                    bool Bad(int i) => !result[i] || singularity[i];

                    int s0 = 0;
                    while (s0 < total)
                    {
                        if (cts.IsCancellationRequested) return;
                        if (!Bad(s0)) { s0++; continue; }
                        int s1 = s0;
                        while (s1 + 1 < total && Bad(s1 + 1)) s1++;

                        // Smallest nozzle spin that clears the span's start/middle/end.
                        float chosen = 0f;
                        foreach (float mag in new[] { 20f, 40f, 60f, 90f, 120f, 150f, 180f })
                        {
                            foreach (float sgn in new[] { 1f, -1f })
                            {
                                float y = mag * sgn;
                                bool ok = true;
                                foreach (int ti in new[] { s0, (s0 + s1) / 2, s1 })
                                {
                                    var rot = solver.TargetRotFromGlobalOrientation(
                                        normals[ti], offA, offB, offC + y);
                                    var sol = solver.Solve(targets[ti],
                                        solutions[Math.Max(0, ti - 1)], rot, maxIterations: 60);
                                    if (sol is null || MathF.Abs(sol[4]) < MinA5) { ok = false; break; }
                                }
                                if (ok) { chosen = y; break; }
                            }
                            if (chosen != 0f) break;
                        }

                        if (chosen != 0f)
                        {
                            int rIn  = Math.Max(0, s0 - Ramp);
                            int rOut = Math.Min(total - 1, s1 + Ramp);
                            for (int i = rIn; i <= rOut; i++)
                            {
                                float w = i < s0 ? (i - rIn)  / (float)Math.Max(1, s0 - rIn)
                                        : i > s1 ? (rOut - i) / (float)Math.Max(1, rOut - s1)
                                        : 1f;
                                float y = chosen * w;
                                if (MathF.Abs(y) > MathF.Abs(yawByMove[i])) yawByMove[i] = y;
                            }

                            // Re-solve the affected range with the yawed orientation.
                            var chunkSeed = solutions[Math.Max(0, rIn - 1)];
                            for (int i = rIn; i <= rOut; i++)
                            {
                                var rot = solver.TargetRotFromGlobalOrientation(
                                    normals[i], offA, offB, offC + yawByMove[i]);
                                var sol = solver.Solve(targets[i], chunkSeed, rot, maxIterations: 40);
                                result[i] = sol is not null;
                                if (sol is not null) { solutions[i] = sol; chunkSeed = sol; }
                                singularity[i] = MathF.Abs(solutions[i][4]) < 5f;
                            }
                        }
                        s0 = s1 + 1;
                    }

                    // Bake the repair into the toolpath so KRL export writes the
                    // rotated orientations.
                    for (int i = 0; i < total; i++)
                        flatMoves[i].TcpYawDeg = yawByMove[i];
                }
            }

            // ── Digital-twin collision sweep (environment + self + material) ────
            bool[]? collision = null;
            CollisionHit? firstCollHit = null;
            int collCount = 0, collStride = 1;
            if (collisionWorld is not null && total > 0)
            {
                try
                {
                    collisionWorld.Beads = collisionWorld.Settings.CheckMaterial
                        ? new BeadObstacleGrid(toolpath, beadWidthColl, wtColl, originColl)
                        : null;

                    var chainRoots = new System.Numerics.Matrix4x4[total];
                    var tcpWorlds = new NVec3[total];
                    var railColl = cellForE1?.RobotRail;
                    for (int i = 0; i < total; i++)
                    {
                        if (e1Motion && railColl is { } rc)
                        {
                            var bw = RailE1Planner.BaseWorld(homeWorld, rc, e1PerMove[i]);
                            var bh = RailE1Planner.BaseWorld(homeWorld, rc, homeE1);
                            chainRoots[i] = chainRootColl *
                                System.Numerics.Matrix4x4.CreateTranslation(
                                    bw.X - bh.X, bw.Y - bh.Y, bw.Z - bh.Z);
                            tcpWorlds[i] = new NVec3(
                                targets[i].X + bw.X, targets[i].Y + bw.Y, targets[i].Z + bw.Z);
                        }
                        else
                        {
                            chainRoots[i] = chainRootColl;
                            tcpWorlds[i] = new NVec3(
                                targets[i].X + robroot.X, targets[i].Y + robroot.Y, targets[i].Z + robroot.Z);
                        }
                    }

                    var solved = new float[total][];
                    for (int i = 0; i < total; i++) solved[i] = solutions[i] ?? seed;

                    var collResult = ToolpathCollisionChecker.Check(
                        collisionWorld, solved, chainRoots, tcpWorlds, cts.Token);
                    collision = collResult.Colliding;
                    collStride = collResult.SampleStride;
                    for (int i = 0; i < total; i++)
                        if (collision[i])
                        {
                            collCount++;
                            firstCollHit ??= collResult.Hits[i];
                        }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[collision] sweep failed: {ex.Message}");
                    collision = null;
                }
                finally
                {
                    collisionWorld.Beads = null;   // free the per-toolpath grid
                }
            }

            _ikSolutionsByNode[node]  = solutions;
            _moveTimesMsByNode[node]  = moveTimes;
            _singularityByNode[node]  = singularity;
            _e1MmByNode[node]         = e1PerMove;
            if (collision is not null) _collisionByNode[node] = collision;
            else _collisionByNode.TryRemove(node, out _);

            int failCount = 0;
            foreach (var r in result) if (!r) failCount++;
            int singCount = 0;
            foreach (var sg in singularity) if (sg) singCount++;

            // Height range of flagged moves — tells the operator where the robot
            // would fault, in part coordinates they can check against the model.
            float zLo = float.MaxValue, zHi = float.MinValue;
            {
                int fi = 0;
                foreach (var layer in toolpath.Layers)
                    foreach (var mv in layer.Moves)
                    {
                        if (fi < total && (!result[fi] || singularity[fi]))
                        {
                            zLo = Math.Min(zLo, mv.From.Z);
                            zHi = Math.Max(zHi, mv.From.Z);
                        }
                        fi++;
                    }
            }
            _validationIssuesByNode[node] = (failCount, singCount, zLo, zHi);

            _pendingReachability.Enqueue((node, result));
            _pendingSingularityPoints.Enqueue((node, singularity));
            string reachLabel = failCount == 0
                ? $"All {result.Length} reachable"
                : $"{failCount} / {result.Length} unreachable";
            if (collCount > 0)
                reachLabel += collStride > 1
                    ? $" · {collCount:N0} collision (sampled 1/{collStride})"
                    : $" · {collCount:N0} collision";
            Dispatcher.UIThread.Post(() =>
            {
                _validationDone = true;
                if (vm is not null)
                {
                    vm.StatsReachability = reachLabel;
                    vm.IsValidating = false;
                    vm.SetScrubMarkers(result, singularity, collision);
                    int firstBad = -1;
                    for (int i = 0; i < total; i++)
                        if (!result[i] || singularity[i] || (collision is not null && collision[i]))
                        { firstBad = i; break; }
                    vm.FirstValidationIssueIndex = firstBad;
                    // Loud warning: a fault mid-print wastes material and hours.
                    if (failCount + singCount + collCount > 0)
                    {
                        string collPart = collCount > 0
                            ? $" and {collCount:N0} predicted collision moves" +
                              (firstCollHit is { } fh
                                  ? $" (first: {RobotCollisionModel.LinkNames[fh.Link]} ↔ {fh.Other})"
                                  : "")
                            : "";
                        SetSliceStatus(vm,
                            $"⚠ Robot validation: {singCount:N0} singularity-risk, {failCount:N0} unreachable{collPart}" +
                            (zLo <= zHi ? $" between Z {zLo:0} and {zHi:0} mm" : "") +
                            " — the robot may fault or crash mid-print.",
                            isError: true);
                    }
                }
                GlCanvas.RequestNextFrameRendering();
            });
        });
    }

    /// <summary>
    /// Re-applies layer-speed scaling and orientation smoothing to every cached raw toolpath.
    /// </summary>
    private void RebuildToolpathsFromRaw(AdditiveSettingsViewModel s)
    {
        if (_rawToolpathByNode.IsEmpty) return;
        if (DataContext is not ViewportViewModel vm) return;

        foreach (var (node, raw) in _rawToolpathByNode)
        {
            var smoothed = RebuildProcessedToolpath(raw, s);
            // Swap in place AND re-upload — the Speed/RPM gradients are baked into the
            // line VBOs at upload, so without a replace the view keeps stale colours.
            SwapScrubbedToolpath(vm, node, smoothed);
            _pendingOrientationUpdate.Enqueue((node, ComputeOrientationRatePerFlatMove(smoothed)));
        }

        if (vm.IsToolpathSelected && _renderer.SelectedNode is { } selected
            && _toolpathByNode.TryGetValue(selected, out var active))
            ApplyToolpathStats(vm, active);

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
        if (total == 0 || beadWidth <= 0f) return result;

        // Spatial hash over the previous layer's cut segments (cell = bead width).
        // Any segment outside the 3×3 neighbourhood is ≥ one bead away, which already
        // clamps to score 1 — so the ring query is exact, and the whole pass is O(n).
        // (The old per-layer pairwise search was O(n×m) and silently bailed above
        // 600k moves, leaving wave-expanded toolpaths entirely white.)
        float cell = MathF.Max(beadWidth, 0.5f);
        Dictionary<(int, int), List<(NVec3 a, NVec3 b)>>? prevGrid = null;
        int fi = 0;
        foreach (var layer in tp.Layers)
        {
            var curGrid = new Dictionary<(int, int), List<(NVec3 a, NVec3 b)>>();
            foreach (var move in layer.Moves)
            {
                if (ToolpathMoveKinds.IsCutSegment(move.Kind))
                {
                    if (prevGrid is { Count: > 0 })
                    {
                        var mid = (move.From + move.To) * 0.5f;
                        int cx = (int)MathF.Floor(mid.X / cell);
                        int cy = (int)MathF.Floor(mid.Y / cell);
                        float minD = float.MaxValue;
                        for (int gx = cx - 1; gx <= cx + 1; gx++)
                        for (int gy = cy - 1; gy <= cy + 1; gy++)
                            if (prevGrid.TryGetValue((gx, gy), out var segs))
                                foreach (var (a, b) in segs)
                                {
                                    float d = SegDist2D(mid, a, b);
                                    if (d < minD) minD = d;
                                }
                        result[fi] = minD == float.MaxValue
                            ? 1f
                            : Math.Clamp(minD / beadWidth, 0f, 1f);
                    }
                    InsertSegment(curGrid, move.From, move.To, cell);
                }
                fi++;
            }
            prevGrid = curGrid;
        }
        return result;

        static void InsertSegment(
            Dictionary<(int, int), List<(NVec3 a, NVec3 b)>> grid, NVec3 a, NVec3 b, float cell)
        {
            int x0 = (int)MathF.Floor(MathF.Min(a.X, b.X) / cell);
            int x1 = (int)MathF.Floor(MathF.Max(a.X, b.X) / cell);
            int y0 = (int)MathF.Floor(MathF.Min(a.Y, b.Y) / cell);
            int y1 = (int)MathF.Floor(MathF.Max(a.Y, b.Y) / cell);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                if (!grid.TryGetValue((x, y), out var list))
                    grid[(x, y)] = list = [];
                list.Add((a, b));
            }
        }

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
    /// When <paramref name="e1Mm"/> is set, also slides the linear rail for simulation.
    /// </summary>
    private void SetRobotAnglesDirectly(float[] angles, float? e1Mm = null)
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
        if (e1Mm is { } e && robot.IsRobotRail)
            robot.E1 = Math.Round(e, 2);
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
            if (moves[i].IsWipe)
                vProg[i] = wipeMmS;
            else if (moves[i].Kind == MoveKind.Extrude)
            {
                float speed = printMmS * Math.Max(moves[i].PrintSpeedScale, 1e-6f);
                if (moves[i].IsResumeRamp)
                    speed *= Math.Max(moves[i].ResumeSpeedScale, 1e-6f);
                vProg[i] = speed;
            }
            else
                vProg[i] = travelMmS;
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

    // Multi-Planar guide planes (world space) — cached for viewport hit-testing.
    private readonly List<TkVector3> _guidePlaneCenters = [];
    private readonly List<TkVector3> _guidePlaneNormals = [];
    private readonly List<MultiPlanarPlaneRow> _guidePlaneRows = [];
    private float _guidePlaneSize;
    private bool  _guidePlanesActive;
    private int   _guidePlaneSelected = -1;
    private bool  _guidePlaneDragging;
    private MultiPlanarPlaneRow? _guidePlaneDragRow;
    private Toolpath? _spineCachedToolpath;
    private TkMatrix4 _spineCachedWorld;

    private bool _xBraceCylinderSelected;
    private bool _xBraceCylinderDragging;
    private float _xBraceCylinderDragGrabDx, _xBraceCylinderDragGrabDy;
    private float _xBraceCylZMin, _xBraceCylZMax, _xBraceCylRadius;

    private void UpdateAnglePlanePreview(ViewportViewModel vm)
    {
        var node = _renderer.SelectedNode;
        bool multiPlanar = vm.AdditiveSettings?.Method == SliceMethod.MultiPlanar;
        if (!multiPlanar && _guidePlanesActive)
        {
            _guidePlanesActive = false;
            _guidePlaneSelected = -1;
            _renderer.SetGuidePlanes([], 0f);
            _renderer.SetMultiPlanarSpine([]);
        }
        if (multiPlanar)
        {
            UpdateMultiPlanarOverlay(vm);
            _renderer.SetPlanePreview(null, null);
            _renderer.SetCylinderPreview(TkVector3.Zero, 0f, 0f, 0f);
            _renderer.SetSliceDirectionArrow(false, TkVector3.Zero, TkVector3.Zero, 0f);
            return;
        }

        bool angled = vm.AdditiveSettings?.Method == SliceMethod.Angled;
        // Helper hidden → treat as no X-brace overlay (clears cylinder/plane/arrow);
        // slicing still uses the projection, this is visual only.
        bool xBrace = vm.AdditiveSettings is { XBracingEnabled: true, XBracingShowHelper: true }
            && !angled;
        bool xBraceCyl = xBrace
            && string.Equals(vm.AdditiveSettings!.XBracingProjectionType, "Cylinder",
                StringComparison.OrdinalIgnoreCase);
        bool xBracePlane = xBrace && !xBraceCyl;

        // Prefer model geometry for X-bracing overlays even when a toolpath is selected.
        SceneNode? planeNode = node;
        if (xBrace && (planeNode is null || _renderer.IsToolpathNode(planeNode)))
            planeNode = vm.GetUserModelItems().FirstOrDefault()?.Node;

        if (planeNode is null
            || (!angled && !xBrace)
            || (angled && _renderer.IsToolpathNode(planeNode)))
        {
            _renderer.SetPlanePreview(null, null);
            _renderer.SetCylinderPreview(TkVector3.Zero, 0f, 0f, 0f);
            _renderer.SetSliceDirectionArrow(false, TkVector3.Zero, TkVector3.Zero, 0f);
            return;
        }

        // World-space AABB of the selected node.
        var min = new TkVector3(float.MaxValue);
        var max = new TkVector3(float.MinValue);
        Span<TkVector3> corners = stackalloc TkVector3[8];
        bool hasGeometry = false;
        foreach (var n in planeNode.SelfAndDescendants())
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

        if (!hasGeometry)
        {
            _renderer.SetPlanePreview(null, null);
            _renderer.SetCylinderPreview(TkVector3.Zero, 0f, 0f, 0f);
            _renderer.SetSliceDirectionArrow(false, TkVector3.Zero, TkVector3.Zero, 0f);
            return;
        }

        // ── Cylinder projection: vertical cage on the bed, height = part AABB ──
        if (xBraceCyl)
        {
            _renderer.SetPlanePreview(null, null);
            float z0 = min.Z;
            float z1 = max.Z;
            if (z1 < z0 + 1f) z1 = z0 + 50f;
            float r = (float)(vm.AdditiveSettings!.XBracingCylinderDiameterMm * 0.5);
            var cxy = new TkVector3(
                (float)vm.AdditiveSettings.XBracingCylinderX,
                (float)vm.AdditiveSettings.XBracingCylinderY,
                0f);
            _xBraceCylZMin = z0;
            _xBraceCylZMax = z1;
            _xBraceCylRadius = r;
            _renderer.SetCylinderPreview(cxy, r, z0, z1, _xBraceCylinderSelected || _xBraceCylinderDragging);
            // Direction cue: default pull toward axis (inward); flip = radiate outward.
            var radial = new TkVector3(1f, 0f, 0f);
            var cueDir = vm.AdditiveSettings.XBracingCylinderFlipDirection ? radial : -radial;
            var cueOrigin = new TkVector3(
                cxy.X + (vm.AdditiveSettings.XBracingCylinderFlipDirection ? 0f : r * 0.85f),
                cxy.Y,
                0.5f * (z0 + z1));
            _renderer.SetSliceDirectionArrow(
                true,
                cueOrigin,
                cueDir,
                MathF.Max(r * 0.8f, 40f));
            return;
        }

        _renderer.SetCylinderPreview(TkVector3.Zero, 0f, 0f, 0f);

        var center = (min + max) * 0.5f;
        float size = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z)) * 1.3f;

        float tiltY = angled
            ? (float)vm.AdditiveSettings!.TiltAngle
            : (float)vm.AdditiveSettings!.XBracingPlaneTiltY;
        float tiltX = angled
            ? (float)vm.AdditiveSettings!.TiltAngleX
            : (float)vm.AdditiveSettings!.XBracingPlaneTiltX;
        float ty = (float)(tiltY * Math.PI / 180.0);
        float tx = (float)(tiltX * Math.PI / 180.0);
        var normal = new TkVector3(
            MathF.Sin(ty),
            -MathF.Sin(tx) * MathF.Cos(ty),
             MathF.Cos(tx) * MathF.Cos(ty));

        _renderer.SetPlanePreview(center, normal, size);

        // Direction arrow: layer advance (angled) or brace grow direction (X-bracing).
        var dirXY = new TkVector3(normal.X, normal.Y, 0f);
        if (dirXY.LengthSquared > 1e-6f)
        {
            dirXY = dirXY.Normalized();
            _renderer.SetSliceDirectionArrow(true, center - dirXY * (size * 0.5f), dirXY, size * 0.6f);
        }
        else
        {
            _renderer.SetSliceDirectionArrow(false, TkVector3.Zero, TkVector3.Zero, 0f);
        }
    }

    /// <summary>
    /// Multi-Planar viewport overlay: three guide-plane quads (base / middle / top,
    /// click-and-drag horizontally to rotate one) plus the spine — a polyline through
    /// every layer's centre coloured by wedge distortion (green = uniform thickness,
    /// red = the thin side nearing self-crossing or the thick side nearing a gap).
    /// </summary>
    private void UpdateMultiPlanarOverlay(ViewportViewModel vm)
    {
        // Note: model BODIES are hidden in the toolpath views — the guide planes must
        // still show, so take the first user model regardless of visibility.
        var model = vm.GetUserModelItems().FirstOrDefault()?.Node;
        if (model is null || vm.AdditiveSettings is not { } s || s.MultiPlanarPlanes.Count < 2)
        {
            ClearMultiPlanarOverlay();
            return;
        }

        // World AABB of the model.
        var min = new TkVector3(float.MaxValue);
        var max = new TkVector3(float.MinValue);
        bool hasGeometry = false;
        foreach (var n in model.SelfAndDescendants())
        {
            if (n.Mesh?.PickingData is not { } mesh) continue;
            var world = n.WorldTransform;
            var (bMin, bMax) = mesh.LocalBounds;
            for (int ci = 0; ci < 8; ci++)
            {
                var pLocal = new TkVector3(
                    (ci & 1) == 0 ? bMin.X : bMax.X,
                    (ci & 2) == 0 ? bMin.Y : bMax.Y,
                    (ci & 4) == 0 ? bMin.Z : bMax.Z);
                var w = TransformTk(pLocal, world);
                min = TkVector3.ComponentMin(min, w);
                max = TkVector3.ComponentMax(max, w);
            }
            hasGeometry = true;
        }
        if (!hasGeometry)
        {
            ClearMultiPlanarOverlay();
            return;
        }

        float cx = (min.X + max.X) * 0.5f, cy = (min.Y + max.Y) * 0.5f;
        float size = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z)) * 1.15f;
        bool axisX = s.MultiPlanarAxisX;

        _guidePlaneCenters.Clear();
        _guidePlaneNormals.Clear();
        _guidePlaneRows.Clear();
        var planes = new List<(TkVector3 Center, TkVector3 Normal)>();
        foreach (var row in s.MultiPlanarPlanes.OrderBy(r => r.HeightPct))
        {
            float t = (float)(row.AngleDeg * Math.PI / 180.0);
            var nrm = axisX
                ? new TkVector3(0f, -MathF.Sin(t), MathF.Cos(t))
                : new TkVector3(MathF.Sin(t), 0f, MathF.Cos(t));
            float h = min.Z + (float)(row.HeightPct / 100.0) * (max.Z - min.Z);
            var c = new TkVector3(cx, cy, h);
            planes.Add((c, nrm));
            _guidePlaneCenters.Add(c);
            _guidePlaneNormals.Add(nrm);
            _guidePlaneRows.Add(row);
        }
        _guidePlaneSize = size;
        if (_guidePlaneSelected >= planes.Count) _guidePlaneSelected = -1;
        // Viewport toggle: hide the quads (and the drag hit-testing with them) while
        // keeping the distortion spine visible.
        _guidePlanesActive = vm.ShowMultiPlanarPlanes;
        _renderer.SetGuidePlanes(_guidePlanesActive ? planes : [], size, _guidePlaneSelected);

        // Combined rotate+translate affordance on the selected plane: a rotation ring
        // about the tilt axis plus a vertical height arrow, drawn as one polyline.
        if (_guidePlaneSelected >= 0 && _guidePlanesActive)
        {
            var c = _guidePlaneCenters[_guidePlaneSelected];
            var gizmo = new List<(TkVector3, TkVector3)>();
            var yellow = new TkVector3(1f, 0.85f, 0.1f);
            float r = size * 0.28f;
            // Ring in the plane perpendicular to the tilt axis (XZ for Y-tilt, YZ for X-tilt).
            for (int i = 0; i <= 32; i++)
            {
                float a = MathF.Tau * i / 32f;
                var pt = axisX
                    ? new TkVector3(c.X, c.Y + r * MathF.Cos(a), c.Z + r * MathF.Sin(a))
                    : new TkVector3(c.X + r * MathF.Cos(a), c.Y, c.Z + r * MathF.Sin(a));
                gizmo.Add((pt, yellow));
            }
            // Height arrow: ring → up spike → down spike (drawn within the same strip).
            float ah = size * 0.38f;
            gizmo.Add((c, yellow));
            gizmo.Add((c + new TkVector3(0, 0, ah), yellow));
            gizmo.Add((c + new TkVector3(0, 0, -ah), yellow));
            gizmo.Add((c, yellow));
            _renderer.SetGuidePlaneGizmo(gizmo);
        }
        else
            _renderer.SetGuidePlaneGizmo([]);

        // The Planes toggle governs the whole overlay: hide the spine with the quads.
        if (!vm.ShowMultiPlanarPlanes)
        {
            _spineCachedToolpath = null;   // rebuild when re-enabled
            _renderer.SetMultiPlanarSpine([]);
            return;
        }

        // Spine from the sliced toolpath: layer centres coloured by wedge distortion.
        // Rebuilt only when the toolpath or its pose changes — this runs per frame.
        var pair = _toolpathByNode.FirstOrDefault(kv => kv.Key.Visible);
        if (pair.Value is null)
        {
            _spineCachedToolpath = null;
            _renderer.SetMultiPlanarSpine([]);
            return;
        }
        if (ReferenceEquals(pair.Value, _spineCachedToolpath)
            && pair.Key.WorldTransform == _spineCachedWorld)
            return;
        _spineCachedToolpath = pair.Value;
        _spineCachedWorld    = pair.Key.WorldTransform;
        _toolpathOriginByNode.TryGetValue(pair.Key, out var origin);
        var wt = pair.Key.WorldTransform;
        var spine = new List<(TkVector3 Pos, TkVector3 Color)>(pair.Value.Layers.Count);
        foreach (var layer in pair.Value.Layers)
        {
            float minS = float.MaxValue, maxS = float.MinValue;
            var sum = System.Numerics.Vector3.Zero;
            int cnt = 0;
            foreach (var m in layer.Moves)
            {
                if (m.Kind != MoveKind.Extrude || m.IsWipe) continue;
                sum += (m.From + m.To) * 0.5f;
                cnt++;
                if (m.HeightScale < minS) minS = m.HeightScale;
                if (m.HeightScale > maxS) maxS = m.HeightScale;
            }
            if (cnt == 0) continue;
            var centroid = sum / cnt;
            var world = TransformTk(new TkVector3(
                centroid.X - origin.X, centroid.Y - origin.Y, centroid.Z - origin.Z), wt);

            // Distortion 0…1: thin side approaching the 0.25 crossing clamp, or thick
            // side approaching the 3× gap clamp.
            float thin  = Math.Clamp((1f - minS) / 0.75f, 0f, 1f);
            float thick = Math.Clamp((maxS - 1f) / 2f, 0f, 1f);
            float d = MathF.Max(thin, thick);
            var color = d < 0.5f
                ? TkVector3.Lerp(new TkVector3(0.15f, 0.85f, 0.25f), new TkVector3(1f, 0.85f, 0.1f), d * 2f)
                : TkVector3.Lerp(new TkVector3(1f, 0.85f, 0.1f), new TkVector3(1f, 0.15f, 0.1f), (d - 0.5f) * 2f);
            spine.Add((world, color));
        }
        _renderer.SetMultiPlanarSpine(SmoothSpine(spine));
    }

    /// <summary>Denoises the per-layer spine (seam wobble makes raw layer centroids
    /// zigzag) with a moving average, then interpolates a Catmull-Rom spline through
    /// the result so the curve renders butter-smooth. Colors ride along.</summary>
    private static List<(TkVector3 Pos, TkVector3 Color)> SmoothSpine(
        List<(TkVector3 Pos, TkVector3 Color)> raw)
    {
        if (raw.Count < 4) return raw;

        // Moving average (window 7, clamped at the ends).
        var avg = new List<(TkVector3 Pos, TkVector3 Color)>(raw.Count);
        const int half = 3;
        for (int i = 0; i < raw.Count; i++)
        {
            var pSum = TkVector3.Zero; var cSum = TkVector3.Zero; int n = 0;
            for (int k = Math.Max(0, i - half); k <= Math.Min(raw.Count - 1, i + half); k++)
            {
                pSum += raw[k].Pos; cSum += raw[k].Color; n++;
            }
            avg.Add((pSum / n, cSum / n));
        }

        // Catmull-Rom through the averaged points, 6 subdivisions per segment.
        var smooth = new List<(TkVector3 Pos, TkVector3 Color)>((avg.Count - 1) * 6 + 1);
        for (int i = 0; i < avg.Count - 1; i++)
        {
            var p0 = avg[Math.Max(0, i - 1)].Pos;
            var p1 = avg[i].Pos;
            var p2 = avg[i + 1].Pos;
            var p3 = avg[Math.Min(avg.Count - 1, i + 2)].Pos;
            for (int sdiv = 0; sdiv < 6; sdiv++)
            {
                float t = sdiv / 6f, t2 = t * t, t3 = t2 * t;
                var pos = 0.5f * ((2f * p1)
                    + (p2 - p0) * t
                    + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                    + (3f * p1 - p3 - 3f * p2 + p0) * t3);
                var col = TkVector3.Lerp(avg[i].Color, avg[i + 1].Color, t);
                smooth.Add((pos, col));
            }
        }
        smooth.Add(avg[^1]);
        return smooth;
    }

    private void ClearMultiPlanarOverlay()
    {
        if (!_guidePlanesActive) return;
        _guidePlanesActive = false;
        _guidePlaneSelected = -1;
        _guidePlaneRows.Clear();
        _renderer.SetGuidePlanes([], 0f);
        _renderer.SetMultiPlanarSpine([]);
        _renderer.SetGuidePlaneGizmo([]);
    }

    private static TkVector3 TransformTk(TkVector3 p, TkMatrix4 m) => new(
        p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
        p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
        p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);

    /// <summary>Ray-tests the guide-plane quads; grabs the nearest for the combined
    /// rotate (horizontal drag) + height slide (vertical drag) interaction.</summary>
    private bool TryBeginGuidePlaneDrag(ViewportViewModel vm, float mx, float my, float vpW, float vpH)
    {
        if (!_guidePlanesActive || vm.AdditiveSettings is not { } s) return false;

        var ray = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
        int best = -1; float bestT = float.MaxValue;
        for (int k = 0; k < _guidePlaneCenters.Count; k++)
        {
            var n = _guidePlaneNormals[k];
            var c = _guidePlaneCenters[k];
            float denom = TkVector3.Dot(ray.Direction, n);
            if (MathF.Abs(denom) < 1e-6f) continue;
            float t = TkVector3.Dot(c - ray.Origin, n) / denom;
            if (t <= 0f || t >= bestT) continue;
            var hit = ray.Origin + ray.Direction * t;
            var up = MathF.Abs(n.Y) < 0.9f ? TkVector3.UnitY : TkVector3.UnitX;
            var u = TkVector3.Normalize(TkVector3.Cross(n, up));
            var v = TkVector3.Cross(n, u);
            var rel = hit - c;
            float hu = MathF.Abs(TkVector3.Dot(rel, u));
            float hv = MathF.Abs(TkVector3.Dot(rel, v));
            if (hu <= _guidePlaneSize * 0.5f && hv <= _guidePlaneSize * 0.5f)
            {
                best = k; bestT = t;
            }
        }
        if (best < 0) return false;

        _guidePlaneSelected = best;
        _guidePlaneDragging = true;
        _guidePlaneDragRow  = _guidePlaneRows[best];
        vm.RealtimeSlicingPaused = true;   // one re-slice on release, not per pixel
        // No GL calls here (UI thread) — the per-frame overlay pass redraws.
        GlCanvas.RequestNextFrameRendering();
        return true;
    }

    private void UpdateGuidePlaneDrag(ViewportViewModel vm, float deltaX, float deltaY)
    {
        if (!_guidePlaneDragging || _guidePlaneDragRow is not { } row) return;
        row.AngleDeg  += deltaX * 0.2;        // horizontal → rotate
        row.HeightPct -= deltaY * 0.12;       // vertical → slide the plane up/down
        GlCanvas.RequestNextFrameRendering();  // overlay refreshes on the GL thread
    }

    private void FinishGuidePlaneDrag(ViewportViewModel vm)
    {
        if (!_guidePlaneDragging) return;
        _guidePlaneDragging = false;
        _guidePlaneDragRow  = null;
        vm.RealtimeSlicingPaused = false;   // fires the deferred re-slice
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Select / begin translating the X-bracing projection cylinder on the bed plane.
    /// Hit-test: ray vs vertical cylinder shell (or axis cross within radius).
    /// </summary>
    private bool TryBeginXBraceCylinderDrag(ViewportViewModel vm, float mx, float my, float vpW, float vpH)
    {
        // Hidden helper can't be grabbed (and must not swallow clicks).
        if (vm.AdditiveSettings is not { XBracingShowHelper: true }) return false;
        if (vm.AdditiveSettings is not { XBracingEnabled: true } s) return false;
        if (!string.Equals(s.XBracingProjectionType, "Cylinder", StringComparison.OrdinalIgnoreCase))
            return false;

        var ray = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
        float cx = (float)s.XBracingCylinderX;
        float cy = (float)s.XBracingCylinderY;
        float r  = MathF.Max(_xBraceCylRadius, (float)(s.XBracingCylinderDiameterMm * 0.5));
        float z0 = _xBraceCylZMin;
        float z1 = _xBraceCylZMax;
        if (z1 <= z0 + 1f) { z0 = _renderer.BedZ; z1 = z0 + 100f; }

        // Infinite vertical cylinder pick (XY circle), then clamp Z to [z0,z1] band.
        // Ray: O + t D. Solve |Oxy + t Dxy - Cxy|^2 = r^2.
        float ox = ray.Origin.X - cx, oy = ray.Origin.Y - cy;
        float dx = ray.Direction.X, dy = ray.Direction.Y;
        float a = dx * dx + dy * dy;
        bool hit = false;
        float hitT = 0f;
        if (a > 1e-10f)
        {
            float b = 2f * (ox * dx + oy * dy);
            float c = ox * ox + oy * oy - r * r;
            float disc = b * b - 4f * a * c;
            if (disc >= 0f)
            {
                float sqrt = MathF.Sqrt(disc);
                float t0 = (-b - sqrt) / (2f * a);
                float t1 = (-b + sqrt) / (2f * a);
                foreach (float t in new[] { t0, t1 })
                {
                    if (t <= 0f) continue;
                    float z = ray.Origin.Z + ray.Direction.Z * t;
                    if (z < z0 - r * 0.1f || z > z1 + r * 0.1f) continue;
                    hit = true;
                    hitT = t;
                    break;
                }
            }
        }
        // Also allow grabbing the axis cross via bed-plane hit near center.
        if (!hit && SceneRenderer.TryPickHorizontalPlane(ray, z0, out var bedHit))
        {
            float ddx = bedHit.X - cx, ddy = bedHit.Y - cy;
            if (ddx * ddx + ddy * ddy <= (r * 0.35f) * (r * 0.35f))
            {
                hit = true;
                // grab offset so drag keeps relative position
                _xBraceCylinderDragGrabDx = ddx;
                _xBraceCylinderDragGrabDy = ddy;
                _xBraceCylinderSelected = true;
                _xBraceCylinderDragging = true;
                vm.RealtimeSlicingPaused = true;
                GlCanvas.RequestNextFrameRendering();
                return true;
            }
        }
        if (!hit) return false;

        var hitPt = ray.Origin + ray.Direction * hitT;
        _xBraceCylinderDragGrabDx = hitPt.X - cx;
        _xBraceCylinderDragGrabDy = hitPt.Y - cy;
        _xBraceCylinderSelected = true;
        _xBraceCylinderDragging = true;
        vm.RealtimeSlicingPaused = true;
        GlCanvas.RequestNextFrameRendering();
        return true;
    }

    private void UpdateXBraceCylinderDrag(ViewportViewModel vm, Ray ray)
    {
        if (!_xBraceCylinderDragging || vm.AdditiveSettings is not { } s) return;
        float z = _xBraceCylZMin;
        if (!SceneRenderer.TryPickHorizontalPlane(ray, z, out var hit)
            && !SceneRenderer.TryPickHorizontalPlane(ray, _renderer.BedZ, out hit))
            return;
        s.XBracingCylinderX = hit.X - _xBraceCylinderDragGrabDx;
        s.XBracingCylinderY = hit.Y - _xBraceCylinderDragGrabDy;
        GlCanvas.RequestNextFrameRendering();
    }

    private void FinishXBraceCylinderDrag(ViewportViewModel vm)
    {
        if (!_xBraceCylinderDragging) return;
        _xBraceCylinderDragging = false;
        vm.RealtimeSlicingPaused = false;
        GlCanvas.RequestNextFrameRendering();
    }

    // ── Structural Support: viewport pick + move/rotate gizmo ─────────────────
    // A Structural Support is not a SceneNode — it's a spec on AdditiveSettings that
    // the slicer re-applies to every affected layer. So it gets the same treatment as
    // the Cut tool's ghost plane: the real RGB gizmo drives the spec's own fields
    // (branches in StartGizmoDrag / ProcessGizmoDrag), never a node transform.

    /// <summary>True while the gizmo is driving a Structural Support spec.</summary>
    private bool _structSupportGizmoDrag;
    /// <summary>Pocket centre (sliced XY, mm) captured at drag start.</summary>
    private System.Numerics.Vector2 _structDragStartCenter;
    private float _structDragStartRotationDeg;

    /// <summary>
    /// The Structural Support the gizmo currently drives, or null. Safe to call from
    /// OnRender: takes the view-model explicitly so it never touches DataContext.
    /// </summary>
    private static Core.Models.StructuralSupportSpec? ActiveGizmoSupport(ViewportViewModel? vm)
    {
        if (vm is not { IsPaintEditOpen: true }) return null;
        if (vm.ViewMode != "Preview") return null;
        if (vm.AdditiveSettings is not { } add) return null;
        int i = add.SelectedSupportIndex;
        return i >= 0 && i < add.StructuralSupports.Count ? add.StructuralSupports[i] : null;
    }

    /// <summary>Sliced-space → world frame for support helpers. Mirrors
    /// <see cref="UpdatePaintOverlay"/> exactly (first visible toolpath node, its
    /// WorldTransform) so the gizmo lands on the outline that's actually drawn.</summary>
    private void GetSupportFrame(out NVec3 origin, out TkMatrix4 wt)
    {
        origin = default;
        wt = TkMatrix4.Identity;
        foreach (var (node, _) in _toolpathByNode)
        {
            if (!node.Visible) continue;
            _toolpathOriginByNode.TryGetValue(node, out origin);
            wt = node.WorldTransform;
            return;
        }
    }

    /// <summary>Z (sliced space) the helper outlines are drawn at — the current scrub
    /// layer, same as the overlay, so grabbing matches what you see.</summary>
    private float GetSupportHelperZ(ViewportViewModel vm)
    {
        if (_activeScrubNode is { } hn
            && _toolpathByNode.TryGetValue(hn, out var htp)
            && htp.Layers.Count > 0)
        {
            int li = Math.Clamp(vm.CurrentScrubLayerIndex, 0, htp.Layers.Count - 1);
            return htp.Layers[li].Z;
        }
        return 0f;
    }

    /// <summary>World position of a support's pocket centre (the gizmo pivot).</summary>
    private NVec3 SupportCentreWorld(ViewportViewModel vm, Core.Models.StructuralSupportSpec spec)
    {
        GetSupportFrame(out var origin, out var wt);
        float z = GetSupportHelperZ(vm);
        return TransformPoint(
            new TkVector3(spec.CenterX - origin.X, spec.CenterY - origin.Y, z - origin.Z), wt);
    }

    /// <summary>Pocket-local axis basis: X/Y follow the rectangle's own rotation, Z stays
    /// world-up. Same shape as <see cref="GetModifierAxisBasis"/> (rotation about Z only).</summary>
    private static TkMatrix4 SupportAxisBasis(Core.Models.StructuralSupportSpec spec)
        => TkMatrix4.CreateRotationZ(MathHelper.DegreesToRadians(spec.RotationDeg));

    /// <summary>World → sliced XY, for turning a gizmo drag back into CenterX/CenterY.</summary>
    private System.Numerics.Vector2 SupportWorldToSliceXY(Vector3 world)
    {
        GetSupportFrame(out var origin, out var wt);
        TkMatrix4.Invert(wt, out var inv);
        var local = TransformPoint(new TkVector3(world.X, world.Y, world.Z), inv);
        return new System.Numerics.Vector2(local.X + origin.X, local.Y + origin.Y);
    }

    /// <summary>
    /// Index of the Structural Support pocket under the cursor, or -1. Pick is the
    /// pocket footprint only — deliberately NOT the anchor tick, which sits on the wall
    /// and would steal bead clicks from the very selection workflow that places it.
    /// </summary>
    private int PickStructuralSupportUnderCursor(
        ViewportViewModel vm, float mx, float my, float vpW, float vpH)
    {
        if (vm.AdditiveSettings is not { } add || add.StructuralSupports.Count == 0) return -1;

        var ray = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
        GetSupportFrame(out var origin, out var wt);
        float zSlice = GetSupportHelperZ(vm);
        // Helpers are drawn on one plane; a toolpath node only ever rotates about Z
        // (rotary bed), so that plane stays horizontal in world space too.
        var probe = TransformPoint(new TkVector3(-origin.X, -origin.Y, zSlice - origin.Z), wt);
        if (!SceneRenderer.TryPickHorizontalPlane(ray, probe.Z, out var worldHit)) return -1;

        TkMatrix4.Invert(wt, out var inv);
        var local = TransformPoint(worldHit, inv);
        var q = new System.Numerics.Vector2(local.X + origin.X, local.Y + origin.Y);

        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < add.StructuralSupports.Count; i++)
        {
            var spec = add.StructuralSupports[i];
            if (!spec.Enabled) continue;                 // hidden helpers can't be grabbed
            if (!spec.ContainsPoint(q)) continue;
            // Overlapping pockets: whichever centre is nearer the cursor wins.
            float d = System.Numerics.Vector2.Distance(q, new(spec.CenterX, spec.CenterY));
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>Makes a support the live one: its settings drive the panel, its outline
    /// goes cyan, and the gizmo appears on it. Also expands its MODIFICATIONS card when
    /// it has one — a support created in an earlier session won't.</summary>
    private void SelectStructuralSupport(ViewportViewModel vm, int index)
    {
        if (vm.AdditiveSettings is not { } add) return;
        if (index < 0 || index >= add.StructuralSupports.Count) return;

        add.SelectedSupportIndex = index;
        // The gizmo needs an active mode to draw in; a fresh edit session has none.
        if (vm.ActiveGizmoModeInternal is GizmoMode.None or GizmoMode.Scale)
            vm.ActiveGizmoModeInternal = GizmoMode.Translate;

        foreach (var m in _paintModifications)
            if (m.StructuralIndex == index) m.IsExpanded = true;
        SyncPaintModificationsUi(vm);

        var spec = add.StructuralSupports[index];
        LogPaintConsole($"[support] selected {add.SupportNameAt(index)} · "
            + $"centre ({spec.CenterX:F0}, {spec.CenterY:F0}) · {spec.RotationDeg:F0}° · "
            + "drag the gizmo to move, Rotate tool for the ring");
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Reports the frame the support helpers are drawn/picked in. Exists because a
    /// failed pick has several indistinguishable causes from the outside: no visible
    /// toolpath node, a helper Z that doesn't match the toolpath, or a Local/World
    /// transform mismatch (the toolpath itself is drawn with LocalTransform, while the
    /// paint overlay uses WorldTransform — if those differ, the outline you see is not
    /// where the toolpath is).
    /// </summary>
    private string DescribeSupportPickState(ViewportViewModel vm)
    {
        if (vm.AdditiveSettings is not { } add)
            return "[support where] no additive settings";
        if (add.StructuralSupports.Count == 0)
            return "[support where] no structural supports";

        var lines = new List<string>();
        SceneNode? frameNode = null;
        int visibleToolpaths = 0;
        foreach (var (node, _) in _toolpathByNode)
        {
            if (!node.Visible) continue;
            visibleToolpaths++;
            frameNode ??= node;
        }

        lines.Add($"[support where] editOpen={vm.IsPaintEditOpen} view={vm.ViewMode} "
            + $"2d={vm.IsSlicePlaneViewerActive} gizmoMode={vm.ActiveGizmoModeInternal} "
            + $"gizmoEnabled={_renderer.GizmoEnabled}");
        // SelectedNode used to be a hard gate on both the gizmo draw and its hit test, so
        // a pocket gizmo silently did not exist with nothing selected in the outliner.
        // Report it: "selected=none + pivot set" must now still give a live gizmo.
        lines.Add($"[support where] renderer.SelectedNode="
            + $"{(_renderer.SelectedNode is null ? "none" : _renderer.SelectedNode.Name)} "
            + $"gizmoPivot={(_renderer.GizmoPivotWorld is { } gp ? $"({gp.X:0.#},{gp.Y:0.#},{gp.Z:0.#})" : "null")}");
        lines.Add($"[support where] toolpath nodes={_toolpathByNode.Count} visible={visibleToolpaths} "
            + $"activeScrubNode={(_activeScrubNode is null ? "none" : _activeScrubNode.Name)}");

        if (frameNode is null)
        {
            lines.Add("[support where] NO VISIBLE TOOLPATH NODE → helpers fall back to raw "
                + "sliced coords at Z=0; that is almost certainly not where you're clicking.");
        }
        else
        {
            var lt = frameNode.LocalTransform;
            var wt = frameNode.WorldTransform;
            float dt = (lt.Row3.Xyz - wt.Row3.Xyz).Length;
            _toolpathOriginByNode.TryGetValue(frameNode, out var origin);
            lines.Add($"[support where] frame node='{frameNode.Name}' "
                + $"origin=({origin.X:0.#},{origin.Y:0.#},{origin.Z:0.#})");
            lines.Add($"[support where] localT translation=({lt.Row3.X:0.#},{lt.Row3.Y:0.#},{lt.Row3.Z:0.#}) "
                + $"worldT translation=({wt.Row3.X:0.#},{wt.Row3.Y:0.#},{wt.Row3.Z:0.#}) "
                + $"→ delta {dt:0.##} mm "
                + (dt > 0.5f
                    ? "*** MISMATCH: the pocket outline is drawn offset from the toolpath ***"
                    : "(match — transform is not the problem)"));
        }

        float helperZ = GetSupportHelperZ(vm);
        lines.Add($"[support where] helper Z (sliced) = {helperZ:0.##} · "
            + $"scrubLayerIndex={vm.CurrentScrubLayerIndex}");

        float vpW = (float)Math.Max(1.0, GlCanvas.Bounds.Width);
        float vpH = (float)Math.Max(1.0, GlCanvas.Bounds.Height);
        var viewProj = _renderer.GetViewProjectionMatrix(vpW, vpH);

        for (int i = 0; i < add.StructuralSupports.Count; i++)
        {
            var spec = add.StructuralSupports[i];
            var cw = SupportCentreWorld(vm, spec);
            var clip = new Vector4(cw.X, cw.Y, cw.Z, 1f) * viewProj;
            string screen = clip.W > 1e-4f
                ? $"screen=({(clip.X / clip.W * 0.5f + 0.5f) * vpW:0}, "
                  + $"{(1f - (clip.Y / clip.W * 0.5f + 0.5f)) * vpH:0}) of {vpW:0}x{vpH:0}"
                : "OFF-SCREEN / behind camera";
            lines.Add($"[support where] {add.SupportNameAt(i)}: sliced centre "
                + $"({spec.CenterX:0.#},{spec.CenterY:0.#}) → world "
                + $"({cw.X:0.#},{cw.Y:0.#},{cw.Z:0.#}) · {screen}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Deletes the live support plus any MODIFICATIONS card bound to it, repairs
    /// the remaining cards' spec indices, and re-slices without it.</summary>
    private void DeleteSelectedStructuralSupport(ViewportViewModel vm)
    {
        if (vm.AdditiveSettings is not { } add) return;
        int idx = add.SelectedSupportIndex;
        if (idx < 0 || idx >= add.StructuralSupports.Count) return;

        string name = add.SupportNameAt(idx);
        // A structural card without its spec is a support with no marks and no shape —
        // drop it, then let RemoveStructuralSpec re-link every card pointing past it.
        _paintModifications.RemoveAll(m => m.StructuralIndex == idx);
        RemoveStructuralSpec(vm, add, idx);
        SyncPaintModificationsUi(vm);
        vm.MarkWorkspaceDirty?.Invoke();
        LogPaintConsole($"[support] deleted {name} — reslicing");
        vm.UpdateSliceCommand?.Execute(null);
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>Gizmo drag → pocket centre / rotation. Z is deliberately ignored: the
    /// pocket is a per-layer 2D footprint, so there is no height field to drive.</summary>
    private void ProcessStructuralSupportGizmoDrag(ViewportViewModel vm, float mx, float my)
    {
        if (vm.AdditiveSettings is not { } add) return;
        if (ActiveGizmoSupport(vm) is null) return;

        float vpW = (float)GlCanvas.Bounds.Width;
        float vpH = (float)GlCanvas.Bounds.Height;
        var ray = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
        float denom = Vector3.Dot(ray.Direction, _gizmoDragPlaneNormal);
        if (MathF.Abs(denom) < 1e-5f) return;
        float t = Vector3.Dot(_gizmoDragPlanePoint - ray.Origin, _gizmoDragPlaneNormal) / denom;
        var hitWorld = ray.At(t);

        var dragOp = _kbTransformActive ? _kbTransformOp : _renderer.GizmoMode;
        if (dragOp == GizmoMode.Rotate)
        {
            // Only the Z ring means anything — tipping the footprint out of the layer
            // plane has no field to land in (same constraint as a Vertical cut plane).
            if (_gizmoDragAxis != GizmoAxis.Z) return;
            var rel = hitWorld - _gizmoDragPlanePoint;
            float angle = AxisAngle(_gizmoDragAxis, rel);
            float deltaDeg = MathHelper.RadiansToDegrees(angle - _gizmoDragStartAngle);
            add.SupportRotationDeg = _structDragStartRotationDeg + deltaDeg;
        }
        else if (dragOp == GizmoMode.Translate)
        {
            // Absolute from drag start (never incremental) so the pocket can't drift
            // away from the cursor over a long drag.
            float along = Vector3.Dot(hitWorld - _gizmoDragStartHit, _gizmoDragAxisDir);
            var sliced = SupportWorldToSliceXY(_gizmoDragPlanePoint + _gizmoDragAxisDir * along);
            add.SupportCenterX = sliced.X;
            add.SupportCenterY = sliced.Y;
        }
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>Ends a support gizmo drag and bakes it — one re-slice on release, matching
    /// what creating a support already does (never per pixel).</summary>
    private void FinishStructuralSupportGizmoDrag(ViewportViewModel vm)
    {
        if (!_structSupportGizmoDrag) return;
        _structSupportGizmoDrag = false;
        if (vm.AdditiveSettings is { } add)
        {
            int i = add.SelectedSupportIndex;
            if (i >= 0 && i < add.StructuralSupports.Count)
            {
                var s = add.StructuralSupports[i];
                bool moved =
                    System.Numerics.Vector2.Distance(
                        _structDragStartCenter, new(s.CenterX, s.CenterY)) > 0.05f
                    || MathF.Abs(s.RotationDeg - _structDragStartRotationDeg) > 0.05f;
                if (!moved) return;                     // a click that didn't drag — no reslice
                LogPaintConsole($"[support] {add.SupportNameAt(i)} → centre "
                    + $"({s.CenterX:F1}, {s.CenterY:F1}) · {s.RotationDeg:F0}° — reslicing");
            }
        }
        vm.MarkWorkspaceDirty?.Invoke();
        vm.UpdateSliceCommand?.Execute(null);
    }


    // ── Frame / orbit-on-selection (F key + double-click) ────────────────────

    /// <summary>Minimum orbit radius so a single bead/point still has useful zoom.</summary>
    private const float FrameMinRadiusMm = 40f;

    /// <summary>
    /// Sets the orbit target to the AABB centre and zooms so the bounds fill most of
    /// the view. Used for meshes, toolpath layers, paths, and multi-point selections.
    /// </summary>
    private void FrameCameraToWorldAabb(Vector3 min, Vector3 max)
    {
        var extent = max - min;
        // Degenerate / single-point selection → tight framing around the centre.
        if (extent.LengthSquared < 1e-4f)
        {
            FrameCameraToPoint((min + max) * 0.5f, FrameMinRadiusMm * 2f);
            return;
        }

        var center = (min + max) * 0.5f;
        // 0.75 of diagonal matches the existing Frame All / Focus behaviour.
        float radius = extent.Length * 0.75f;
        _renderer.Camera.Target = center;
        _renderer.Camera.Radius = Math.Max(radius, FrameMinRadiusMm);
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Centres orbit on a world point and sets zoom so the point is comfortably framed.
    /// </summary>
    private void FrameCameraToPoint(Vector3 worldPoint, float radiusMm)
    {
        _renderer.Camera.Target = worldPoint;
        _renderer.Camera.Radius = Math.Max(radiusMm, FrameMinRadiusMm);
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>Frames the camera on the world position of a flat move index of the
    /// active scrub toolpath (timeline validation-tick click). Applies the same
    /// origin + world-transform mapping as ScrubIkForNode.</summary>
    private void FrameCameraToScrubIndex(int index)
    {
        var node = _activeScrubNode;
        if (node is null) return;
        if (!_scrubCacheByNode.TryGetValue(node, out var cache) || cache.Length == 0) return;

        var (pos, _) = cache[Math.Clamp(index, 0, cache.Length - 1)];
        var world = new TkVector3(pos.X, pos.Y, pos.Z);
        if (_toolpathOriginByNode.TryGetValue(node, out var origin))
        {
            var wt = node.WorldTransform;
            float lx = pos.X - origin.X, ly = pos.Y - origin.Y, lz = pos.Z - origin.Z;
            world = new TkVector3(
                lx * wt.M11 + ly * wt.M21 + lz * wt.M31 + wt.M41,
                lx * wt.M12 + ly * wt.M22 + lz * wt.M32 + wt.M42,
                lx * wt.M13 + ly * wt.M23 + lz * wt.M33 + wt.M43);
        }
        FrameCameraToPoint(world, 300f);
    }

    /// <summary>Frames an arbitrary set of world points (selection polylines, layer beads…).</summary>
    private bool FrameCameraToWorldPoints(IEnumerable<Vector3> points, float pointFallbackRadiusMm = 120f)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int n = 0;
        foreach (var p in points)
        {
            min = Vector3.ComponentMin(min, p);
            max = Vector3.ComponentMax(max, p);
            n++;
        }
        if (n == 0) return false;
        if (n == 1 || (max - min).LengthSquared < 1e-2f)
        {
            FrameCameraToPoint(n == 1 ? min : (min + max) * 0.5f, pointFallbackRadiusMm);
            return true;
        }
        FrameCameraToWorldAabb(min, max);
        return true;
    }

    /// <summary>Zoom-to-fit a scene node (mesh subtree, toolpath, or empty transform pivot).</summary>
    private void FrameNode(SceneNode node)
    {
        // Toolpath geometry lives outside Mesh — special-case it.
        if (_toolpathByNode.TryGetValue(node, out var tp))
        {
            FrameToolpathNode(node, tp);
            return;
        }

        if (SceneBounds.TryComputeSubtreeWorldAabb(node, out var min, out var max))
        {
            FrameCameraToWorldAabb(min, max);
            return;
        }

        // No mesh: still make the node the orbit centre (robot TCP, empty group, etc.).
        FrameCameraToPoint(node.WorldTransform.Row3.Xyz, Math.Max(_renderer.Camera.Radius * 0.5f, 200f));
    }

    /// <summary>Frames every extrusion move on a toolpath node.</summary>
    private void FrameToolpathNode(SceneNode node, Toolpath tp)
    {
        _toolpathOriginByNode.TryGetValue(node, out var origin);
        var wt = node.LocalTransform;
        var pts = new List<Vector3>(Math.Max(64, tp.Layers.Sum(l => l.Moves.Count)));
        foreach (var layer in tp.Layers)
            CollectLayerWorldPoints(layer, origin, wt, pts);
        if (!FrameCameraToWorldPoints(pts, pointFallbackRadiusMm: 200f))
            FrameCameraToPoint(node.WorldTransform.Row3.Xyz, 200f);
    }

    /// <summary>Frames one layer of a toolpath (all extrusions on that layer).</summary>
    private void FrameToolpathLayer(
        ToolpathLayer layer, System.Numerics.Vector3 origin, TkMatrix4 wt)
    {
        var pts = new List<Vector3>(Math.Max(16, layer.Moves.Count * 2));
        CollectLayerWorldPoints(layer, origin, wt, pts);
        if (!FrameCameraToWorldPoints(pts, pointFallbackRadiusMm: 150f))
            return;
    }

    private static void CollectLayerWorldPoints(
        ToolpathLayer layer, System.Numerics.Vector3 origin, TkMatrix4 wt, List<Vector3> pts)
    {
        foreach (var mv in layer.Moves)
        {
            if (mv.Kind != MoveKind.Extrude) continue;
            if (mv.IsLayerStitch || mv.IsLayerChange) continue;
            var a = TransformPoint(
                new TkVector3(mv.From.X - origin.X, mv.From.Y - origin.Y, mv.From.Z - origin.Z), wt);
            var b = TransformPoint(
                new TkVector3(mv.To.X - origin.X, mv.To.Y - origin.Y, mv.To.Z - origin.Z), wt);
            pts.Add(new Vector3(a.X, a.Y, a.Z));
            pts.Add(new Vector3(b.X, b.Y, b.Z));
        }
    }

    /// <summary>
    /// Double-click / universal frame: zoom to fit whatever is under the cursor
    /// (or the current selection) and make it the orbit centre.
    /// </summary>
    private void FrameUnderCursorOrSelection(Avalonia.Point pos)
    {
        var vm = DataContext as ViewportViewModel;
        float beadMm = (float)(vm?.AdditiveSettings?.BeadWidth ?? 6f);
        if (beadMm < 0.5f) beadMm = 6f;
        float pointRadius = MathF.Max(FrameMinRadiusMm * 2f, beadMm * 12f);

        // 1) Edit selection (paths / points) takes priority — frame the sticky picks.
        if (vm is { IsPaintEditOpen: true })
        {
            if (_paintSelection.Count > 0)
            {
                var all = new List<TkVector3>();
                foreach (var s in _paintSelection)
                    all.AddRange(s.World);
                if (FrameCameraToWorldPoints(all, pointRadius))
                    return;
            }
            if (_paintSelectedLine is { Count: > 0 } sticky)
            {
                if (FrameCameraToWorldPoints(sticky, pointRadius))
                    return;
            }

            // Nothing sticky yet — pick under cursor (point = single bead, path = section,
            // otherwise the whole layer around that bead).
            if (PickSpanUnderCursor(pos) is { } hit)
            {
                var poly = SpanWorldHighlight(hit, pointMode: vm.PaintPointGranularityActive);
                if (poly is { Count: > 0 } && FrameCameraToWorldPoints(poly, pointRadius))
                    return;
                // Layer frame for anything else under the cursor in edit mode.
                FrameToolpathLayer(hit.Layer, hit.Origin, hit.Wt);
                return;
            }
        }

        // 2) Toolpath bead under cursor → frame that layer (isolated layer window).
        if (PickSpanUnderCursor(pos) is { } layerHit)
        {
            FrameToolpathLayer(layerHit.Layer, layerHit.Origin, layerHit.Wt);
            return;
        }

        // 3) Whole toolpath node under cursor.
        var (mx, my, vpW, vpH) = GetGlPickViewport(pos);
        if (vpW > 1f && vpH > 1f)
        {
            var tpNode = _renderer.PickToolpath(mx, my, vpW, vpH);
            if (tpNode is not null && _toolpathByNode.TryGetValue(tpNode, out var tp))
            {
                FrameToolpathNode(tpNode, tp);
                return;
            }

            // 4) Mesh / scene node under cursor.
            var ray = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
            SceneNode? meshHit = vm is not null
                ? PickForSceneSelection(vm, ray)
                : _renderer.Pick(ray);
            if (meshHit is not null)
            {
                FrameNode(meshHit);
                return;
            }
        }

        // 5) Fall back to whatever is currently selected / scrubbed.
        if (_renderer.SelectedNode is { } sel)
        {
            FrameNode(sel);
            return;
        }
        if (_activeScrubNode is { } scrub && _toolpathByNode.TryGetValue(scrub, out var scrubTp))
        {
            FrameToolpathNode(scrub, scrubTp);
            return;
        }
    }

    private void FocusSelected()
    {
        // Prefer current selection; fall back to the live scrub toolpath.
        if (_renderer.SelectedNode is { } node)
        {
            FrameNode(node);
            return;
        }
        if (_activeScrubNode is { } scrub && _toolpathByNode.TryGetValue(scrub, out var tp))
        {
            FrameToolpathNode(scrub, tp);
            return;
        }
    }

    private Point _lastPointerPos;

    /// <summary>Applies a named camera preset (view pie menu / shortcuts).</summary>
    private void ApplyViewPreset(string name)
    {
        // 2D slice plane is fixed top-down: only Frame (fit) is allowed; orientation
        // presets would fight the per-frame lock and feel like rotate.
        if (IsSlicePlaneNavLocked && name != "Frame")
            return;

        var cam = _renderer.Camera;
        switch (name)
        {
            case "Top":    cam.Elevation = 89.9f;  break;
            case "Bottom": cam.Elevation = -89.9f; break;
            case "Right":  cam.Azimuth = 0f;    cam.Elevation = 0f; break;
            case "Back":   cam.Azimuth = 90f;   cam.Elevation = 0f; break;
            case "Left":   cam.Azimuth = 180f;  cam.Elevation = 0f; break;
            case "Front":  cam.Azimuth = 270f;  cam.Elevation = 0f; break;
            case "Iso":    cam.Azimuth = 45f;   cam.Elevation = 30f; break;
            case "Frame":  FrameAll(); return;
        }
        GlCanvas.RequestNextFrameRendering();
    }

    // Saved camera projection when entering the 2D Slice Plane Viewer.
    private float _sliceViewerSavedElevation = 30f;
    private float _sliceViewerSavedAzimuth = 45f;
    private float _sliceViewerLockedAzimuth; // frozen while slice view is active (no rotate)
    private bool  _sliceViewerSavedOrtho;
    private bool  _sliceViewerHasSavedCamera;

    // Layer-follow while scrubbing in 2D slice (zoom/Radius preserved; Target tracks geometry).
    private int     _sliceFollowLayerIndex = -1;
    private Vector3 _sliceFollowLayerCenter;

    // Scene visibility restored when leaving 2D slice view (robot, env, solid meshes).
    private bool _sliceViewerHidScene;
    private readonly List<(SceneNode Node, bool WasVisible)> _sliceViewerHiddenNodes = [];
    private bool _sliceViewerSavedContactShadows = true;

    /// <summary>
    /// Enters top-down orthographic for the slice plane viewer, restoring the previous
    /// elevation/projection/azimuth when leaving. Hides robot / cell / solid meshes so only
    /// toolpath centre-lines remain for the multi-pass stack.
    /// Default azimuth is square to the robot rail with the rail/robot side at the top.
    /// </summary>
    private void ApplySlicePlaneViewerCamera(bool active)
    {
        var cam = _renderer.Camera;
        if (active)
        {
            if (!_sliceViewerHasSavedCamera)
            {
                _sliceViewerSavedElevation = cam.Elevation;
                _sliceViewerSavedAzimuth   = cam.Azimuth;
                _sliceViewerSavedOrtho     = cam.IsOrthographic;
                // Square to the rail, then +90° CCW on screen (azimuth −90° at top-down).
                _sliceViewerLockedAzimuth  = NormalizeAzimuth(
                    ComputeSliceViewerAzimuthRailUp(cam.Target) - 90f);
                _sliceViewerHasSavedCamera = true;
            }
            // Exactly 90° so the pole up-vector matches azimuth with no residual twist.
            cam.Elevation      = 90f;
            cam.Azimuth        = _sliceViewerLockedAzimuth;
            cam.IsOrthographic = true;
            EnforceSlicePlaneSceneHiding(hide: true);
            // Seed follow state so the first scrub delta is measured from this layer.
            _sliceFollowLayerIndex = -1;
        }
        else
        {
            if (_sliceViewerHasSavedCamera)
            {
                cam.Elevation      = _sliceViewerSavedElevation;
                cam.Azimuth        = _sliceViewerSavedAzimuth;
                cam.IsOrthographic = _sliceViewerSavedOrtho;
                _sliceViewerHasSavedCamera = false;
            }
            EnforceSlicePlaneSceneHiding(hide: false);
            _sliceFollowLayerIndex = -1;
        }
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// When the LAYERS dual-slider or timeline moves to another layer, translate the
    /// camera target by the change in that layer's world centre so the slice stays in
    /// view. Zoom (<see cref="OrbitCamera.Radius"/>) is never modified. User pan is
    /// preserved as an offset relative to the geometry centre.
    /// </summary>
    private void FollowSliceLayerCamera(ViewportViewModel vm, OrbitCamera cam)
    {
        if (vm.ActiveScrubToolpath is not { Layers.Count: > 0 } tp) return;
        int li = Math.Clamp(vm.CurrentScrubLayerIndex, 0, tp.Layers.Count - 1);
        if (li == _sliceFollowLayerIndex) return;

        if (!TryGetSliceLayerWorldCenter(tp.Layers[li], out var center))
            return;

        if (_sliceFollowLayerIndex >= 0)
        {
            // Keep zoom; slide target with the geometry so multiplanar stacks track.
            cam.Target += center - _sliceFollowLayerCenter;
        }

        _sliceFollowLayerIndex = li;
        _sliceFollowLayerCenter = center;
    }

    /// <summary>World-space AABB centre of extrusions on a layer (toolpath local + origin).</summary>
    private bool TryGetSliceLayerWorldCenter(ToolpathLayer layer, out Vector3 center)
    {
        center = default;
        System.Numerics.Vector3 origin = default;
        var wt = TkMatrix4.Identity;
        if (_activeScrubNode is not null)
        {
            _toolpathOriginByNode.TryGetValue(_activeScrubNode, out origin);
            wt = _activeScrubNode.LocalTransform;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int n = 0;
        var moves = layer.Moves;
        // Stride long layers — centre only needs a stable average, not every bead.
        int stride = moves.Count > 400 ? Math.Max(1, moves.Count / 200) : 1;
        for (int i = 0; i < moves.Count; i += stride)
        {
            var mv = moves[i];
            if (mv.Kind != MoveKind.Extrude) continue;
            if (mv.IsLayerStitch || mv.IsLayerChange) continue;
            var mid = (mv.From + mv.To) * 0.5f;
            var w = TransformPoint(new TkVector3(
                mid.X - origin.X, mid.Y - origin.Y, mid.Z - origin.Z), wt);
            var p = new Vector3(w.X, w.Y, w.Z);
            min = Vector3.ComponentMin(min, p);
            max = Vector3.ComponentMax(max, p);
            n++;
        }

        if (n == 0)
        {
            // Empty / travel-only layer: fall back to nominal layer Z at toolpath origin.
            float z = layer.Z - origin.Z;
            var w = TransformPoint(new TkVector3(-origin.X, -origin.Y, z), wt);
            center = new Vector3(w.X, w.Y, w.Z);
            return true;
        }

        center = (min + max) * 0.5f;
        return true;
    }

    /// <summary>
    /// Azimuth for a top-down 2D view that is square to the robot rail, with the
    /// rail / robot side toward the top of the screen.
    /// <para>
    /// Prefer the cell's linear-rail axis (X/Y) so the view is orthogonal to the rail —
    /// NOT the diagonal vector from the part to the robot base (that left a skew angle
    /// on LFAM1 where the base sits off-axis from the bed).
    /// </para>
    /// At elevation = 90°, OrbitCamera screen-up is (-cos az, -sin az) in world XY.
    /// </summary>
    private float ComputeSliceViewerAzimuthRailUp(OpenTK.Mathematics.Vector3 lookTarget)
    {
        var robot = _robotBaseNode?.WorldTransform.Row3.Xyz ?? _robrootWorldPos;
        float toRobotX = robot.X - lookTarget.X;
        float toRobotY = robot.Y - lookTarget.Y;

        // Unit screen-up direction in world XY (will point toward the top of the view).
        float upX, upY;
        if (_robotRail is { } rail)
        {
            // Rail axis is world-aligned (X or Y). Use it so the view is square to the rail.
            switch (rail.Axis.ToUpperInvariant())
            {
                case "X":
                    upX = 1f; upY = 0f;
                    break;
                case "Y":
                    upX = 0f; upY = 1f;
                    break;
                default:
                    // Z-axis rail has no in-plane direction — fall through to robot vector.
                    upX = 0f; upY = 0f;
                    break;
            }

            if (upX * upX + upY * upY > 0.5f)
            {
                // Flip so the robot sits toward the top half (not the bottom).
                if (toRobotX * upX + toRobotY * upY < 0f)
                {
                    upX = -upX;
                    upY = -upY;
                }
            }
            else
            {
                upX = toRobotX;
                upY = toRobotY;
            }
        }
        else
        {
            // No linear rail (e.g. LFAM3): aim screen-up at the robot base, but snap to
            // the nearest world axis so the view stays square (no residual skew).
            upX = toRobotX;
            upY = toRobotY;
            float ax = MathF.Abs(upX), ay = MathF.Abs(upY);
            if (ax * ax + ay * ay > 1e-4f)
            {
                if (ax >= ay)
                {
                    upX = upX >= 0f ? 1f : -1f;
                    upY = 0f;
                }
                else
                {
                    upX = 0f;
                    upY = upY >= 0f ? 1f : -1f;
                }
            }
        }

        if (upX * upX + upY * upY < 1e-8f)
            return 90f; // degenerate: default quarter-turn

        // screen-up = (-cos az, -sin az) = (upX, upY)
        // ⇒ cos az = -upX, sin az = -upY
        float az = MathF.Atan2(-upY, -upX) * (180f / MathF.PI);
        return NormalizeAzimuth(az);
    }

    private static float NormalizeAzimuth(float az)
    {
        az %= 360f;
        if (az < 0f) az += 360f;
        return az;
    }

    /// <summary>
    /// While 2D slice is on, hide everything that is not a registered toolpath
    /// (robot, pedestal, env, solid part meshes). Re-assert every frame so a later
    /// cell/tool attach cannot resurrect the arm mid-view.
    /// </summary>
    private void EnforceSlicePlaneSceneHiding(bool hide)
    {
        if (hide)
        {
            if (!_sliceViewerHidScene)
            {
                _sliceViewerHiddenNodes.Clear();
                foreach (var child in _renderer.SceneRoot.Children)
                {
                    // Keep toolpath nodes (multi-pass draws them).
                    if (_toolpathByNode.ContainsKey(child)) continue;
                    _sliceViewerHiddenNodes.Add((child, child.Visible));
                    child.Visible = false;
                }
                _sliceViewerHidScene = true;
            }
            else
            {
                // Re-assert: hide anything newly attached that isn't a toolpath.
                foreach (var child in _renderer.SceneRoot.Children)
                {
                    if (_toolpathByNode.ContainsKey(child)) continue;
                    if (child.Visible)
                    {
                        if (!_sliceViewerHiddenNodes.Any(h => ReferenceEquals(h.Node, child)))
                            _sliceViewerHiddenNodes.Add((child, true));
                        child.Visible = false;
                    }
                }
            }
            if (_vm is { } vmHide)
                vmHide.RefreshRobotOutlinerVisibilityFromScene();
        }
        else if (_sliceViewerHidScene)
        {
            foreach (var (node, wasVisible) in _sliceViewerHiddenNodes)
                node.Visible = wasVisible;
            _sliceViewerHiddenNodes.Clear();
            _sliceViewerHidScene = false;
            if (_vm is { } vmShow)
                vmShow.RefreshRobotOutlinerVisibilityFromScene();
        }
    }

    /// <summary>
    /// Makes sure the edit-mode LAYERS dual-slider has a real toolpath + layer ends.
    /// Without an armed scrub session <see cref="ViewportViewModel.ToolpathScrubLayerCount"/>
    /// falls back to 1 and the control paints the empty 1–2 range (drag is a no-op).
    /// </summary>
    private void EnsureScrubArmedForEdit(ViewportViewModel vm)
    {
        // Prefer the already-armed node if its GPU toolpath is still registered.
        if (_activeScrubNode is { } armed
            && _toolpathByNode.TryGetValue(armed, out var armedTp)
            && armedTp.Layers.Count > 0)
        {
            int max = armedTp.Layers.Sum(l => l.Moves.Count);
            // Rebuild ends even when the toolpath object is the same — layer ends can
            // be null after a failed session or mid-slice race.
            bool blankWindow = vm.ToolpathScrubIndex <= 0
                               || vm.ToolpathScrubIndex <= vm.ToolpathScrubLowIndex;
            bool needsRebuild = !vm.IsScrubSessionActive
                                || vm.ActiveScrubToolpath is null
                                || !ReferenceEquals(vm.ActiveScrubToolpath, armedTp)
                                || (vm.ToolpathScrubLayerCount <= 1 && armedTp.Layers.Count > 1)
                                || blankWindow;
            if (needsRebuild)
            {
                // Preserve only when we already have a non-empty visible window.
                bool preserve = vm.IsScrubSessionActive && !blankWindow;
                vm.ResetScrubIndex(max, armedTp, preservePosition: preserve);
                vm.IsScrubSessionActive = true;
            }
            else
            {
                vm.IsScrubSessionActive = true;
            }
            return;
        }

        // Pick the first uploaded toolpath (prefer more layers if several exist).
        SceneNode? bestNode = null;
        Toolpath?  bestTp   = null;
        foreach (var (node, tp) in _toolpathByNode)
        {
            if (tp.Layers.Count == 0) continue;
            if (bestTp is null || tp.Layers.Count > bestTp.Layers.Count)
            {
                bestNode = node;
                bestTp   = tp;
            }
        }

        if (bestNode is null || bestTp is null)
            return;

        _activeScrubNode = bestNode;
        vm.ResetScrubIndex(bestTp.Layers.Sum(l => l.Moves.Count), bestTp, preservePosition: false);
        vm.IsScrubSessionActive = true;
        GlCanvas.RequestNextFrameRendering();
    }

    // ── Number-key view presets (CAD-style ortho / perspective toggle) ────────

    /// <summary>Last number-key view ("1"…"5") so a second press toggles projection.</summary>
    private string? _activeNumberViewKey;

    /// <summary>
    /// Handles D1–D5 / NumPad1–5. Returns true if consumed.
    /// First press: jump to that view in orthographic. Same key again: flip to
    /// perspective (and back). Switching to another number key starts in ortho.
    /// </summary>
    private bool TryHandleViewNumberKey(Key key)
    {
        string? id = key switch
        {
            Key.D1 or Key.NumPad1 => "1",
            Key.D2 or Key.NumPad2 => "2",
            Key.D3 or Key.NumPad3 => "3",
            Key.D4 or Key.NumPad4 => "4",
            Key.D5 or Key.NumPad5 => "5",
            _ => null,
        };
        if (id is null) return false;

        var cam = _renderer.Camera;
        bool sameView = _activeNumberViewKey == id && MatchesNumberViewOrientation(id);
        if (sameView)
        {
            // Toggle ortho ↔ perspective while staying on this standard view.
            cam.IsOrthographic = !cam.IsOrthographic;
        }
        else
        {
            ApplyNumberViewOrientation(id);
            cam.IsOrthographic = true; // first entry into a view is always orthographic
            _activeNumberViewKey = id;
        }

        GlCanvas.RequestNextFrameRendering();
        return true;
    }

    private void ApplyNumberViewOrientation(string id)
    {
        var cam = _renderer.Camera;
        switch (id)
        {
            case "1": // Top
                cam.Elevation = 89.9f;
                break;
            case "2": // Front
                cam.Azimuth   = 270f;
                cam.Elevation = 0f;
                break;
            case "3": // Right
                cam.Azimuth   = 0f;
                cam.Elevation = 0f;
                break;
            case "4": // 3D framed iso
                cam.Azimuth   = 45f;
                cam.Elevation = 30f;
                FrameAll();
                break;
            case "5": // Left
                cam.Azimuth   = 180f;
                cam.Elevation = 0f;
                break;
        }
    }

    private bool MatchesNumberViewOrientation(string id)
    {
        var cam = _renderer.Camera;
        static bool NearAz(float a, float b)
        {
            float d = MathF.Abs(((a - b) % 360f + 540f) % 360f - 180f);
            return d < 3f;
        }
        static bool NearEl(float a, float b) => MathF.Abs(a - b) < 3f;

        return id switch
        {
            "1" => cam.Elevation > 87f, // top pole
            "2" => NearAz(cam.Azimuth, 270f) && NearEl(cam.Elevation, 0f),
            "3" => NearAz(cam.Azimuth, 0f)   && NearEl(cam.Elevation, 0f),
            "4" => NearAz(cam.Azimuth, 45f)  && NearEl(cam.Elevation, 30f),
            "5" => NearAz(cam.Azimuth, 180f) && NearEl(cam.Elevation, 0f),
            _   => false,
        };
    }

    private void FrameAll()
    {
        if (SceneBounds.TryComputeSubtreeWorldAabb(_renderer.SceneRoot, out var min, out var max))
            FrameCameraToWorldAabb(min, max);
        else
            GlCanvas.RequestNextFrameRendering();
    }

    private void RunIkForToolDrag()
    {
        if (_ikSolver is null || _currentToolNode is null) return;
        if (DataContext is not ViewportViewModel { Robot: { } robot }) return;

        RefreshIkSceneKinematics();

        var targetScene   = IsToolIkRotating()
            ? _ikDragTcpPosition
            : _currentToolNode.LocalTransform.Row3.Xyz + _ikDragTcpOffset;
        var targetRobroot = targetScene - GetLiveRobrootWorldPos();

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
        // Every drag starts clean: a support drag that ended by anything other than a
        // normal pointer-release must not make the NEXT mesh drag take the support path.
        _structSupportGizmoDrag  = false;
        if (axis == GizmoAxis.All)
        {
            var camFwdAll = Vector3.Normalize(_renderer.Camera.Target - _renderer.Camera.Eye);
            var r = Vector3.Cross(Vector3.UnitZ, camFwdAll);
            _gizmoDragAxisDir = r.LengthSquared > 1e-6f ? Vector3.Normalize(r) : Vector3.UnitX;
        }
        else
        {
            // Every other gizmo (meshes, effectors, etc.) stays world-axis-aligned — this is
            // scoped to Cut modifiers only. A Vertical cut's X/Y axes follow its own
            // RotationDegrees so dragging "sideways" moves along the plane's own line instead of
            // requiring a diagonal X+Y combination; Z stays world-up always (RotationDegrees only
            // ever rotates about Z for a Vertical cut, so Z was never tilting anyway — this just
            // makes that explicit rather than accidental).
            var basis = GetModifierAxisBasis(_renderer.SelectedNode) ?? Matrix4.Identity;
            _gizmoDragAxisDir = axis switch
            {
                GizmoAxis.X => basis.Row0.Xyz,
                GizmoAxis.Y => basis.Row1.Xyz,
                _           => basis.Row2.Xyz,
            };
        }

        // Cut tool: gizmo drives the cut plane, not the mesh transform.
        if (DataContext is ViewportViewModel { IsCutToolActive: true, CutToolSession: { } cutS })
        {
            _gizmoDragPlanePoint = new Vector3(
                (float)cutS.CenterX, (float)cutS.CenterY, (float)cutS.CenterZ);
            _toolIsDragging = false;
            _transformLinkFollowers = null;
            SetupPivotGizmoDrag(mx, my, vpW, vpH);
            return;
        }

        // Structural Support: gizmo drives the spec's pocket, not a node transform.
        if (DataContext is ViewportViewModel sgVm && ActiveGizmoSupport(sgVm) is { } sgSpec)
        {
            // Axis dir follows the pocket's own rotation, so the X arrow slides along the
            // rectangle's own long edge instead of needing a diagonal X+Y combination.
            if (axis != GizmoAxis.All)
            {
                var basis = SupportAxisBasis(sgSpec);
                _gizmoDragAxisDir = axis switch
                {
                    GizmoAxis.X => basis.Row0.Xyz,
                    GizmoAxis.Y => basis.Row1.Xyz,
                    _           => basis.Row2.Xyz,
                };
            }
            var centre = SupportCentreWorld(sgVm, sgSpec);
            _gizmoDragPlanePoint = new Vector3(centre.X, centre.Y, centre.Z);
            _structSupportGizmoDrag = true;
            _structDragStartCenter = new System.Numerics.Vector2(sgSpec.CenterX, sgSpec.CenterY);
            _structDragStartRotationDeg = sgSpec.RotationDeg;
            _toolIsDragging = false;
            _transformLinkFollowers = null;
            SetupPivotGizmoDrag(mx, my, vpW, vpH);
            return;
        }

        if (_renderer.SelectedNode is not { } node) return;
        if (IsToolNodeSelected() && _renderer.GizmoMode == GizmoMode.Scale) return;
        // A modifier plane isn't a solid part — scaling it means nothing (no field for it).
        if (DataContext is ViewportViewModel vmScale && vmScale.IsModifierNode(node)
            && _renderer.GizmoMode == GizmoMode.Scale) return;
        _gizmoDragInitialLocal = node.LocalTransform;
        _gizmoDragPlanePoint   = GetGizmoPivotWorld(node);
        BeginTransformLink(node);
        BeginToolIkDrag(node);

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

    /// <summary>Gizmo drag setup for anything driven by a bare pivot point rather than a
    /// SceneNode transform (the Cut tool's ghost plane, a Structural Support pocket).</summary>
    private void SetupPivotGizmoDrag(float mx, float my, float vpW, float vpH)
    {
        var dragOp = _kbTransformActive ? _kbTransformOp : _renderer.GizmoMode;
        switch (dragOp)
        {
            case GizmoMode.Translate:
            case GizmoMode.Scale:
            {
                var camFwd = Vector3.Normalize(_renderer.Camera.Target - _renderer.Camera.Eye);
                var n = camFwd - Vector3.Dot(camFwd, _gizmoDragAxisDir) * _gizmoDragAxisDir;
                _gizmoDragPlaneNormal = n.LengthSquared > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitZ;
                var startRay = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
                float denom = Vector3.Dot(startRay.Direction, _gizmoDragPlaneNormal);
                _gizmoDragStartHit = MathF.Abs(denom) > 1e-5f
                    ? startRay.At(Vector3.Dot(_gizmoDragPlanePoint - startRay.Origin, _gizmoDragPlaneNormal) / denom)
                    : _gizmoDragPlanePoint;
                break;
            }
            case GizmoMode.Rotate:
            {
                _gizmoDragPlaneNormal = _gizmoDragAxisDir;
                var startRay = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
                float denom = Vector3.Dot(startRay.Direction, _gizmoDragPlaneNormal);
                _gizmoDragStartHit = MathF.Abs(denom) > 1e-5f
                    ? startRay.At(Vector3.Dot(_gizmoDragPlanePoint - startRay.Origin, _gizmoDragPlaneNormal) / denom)
                    : _gizmoDragPlanePoint;
                var rel = _gizmoDragStartHit - _gizmoDragPlanePoint;
                _gizmoDragStartAngle = AxisAngle(_gizmoDragAxis, rel);
                break;
            }
        }
    }

    private void ProcessGizmoDrag(float mx, float my)
    {
        // Interactive cut plane: translate/rotate the ghost plane only.
        if (DataContext is ViewportViewModel { IsCutToolActive: true, CutToolSession: { } cutS })
        {
            ProcessCutPlaneGizmoDrag(cutS, mx, my);
            return;
        }

        // Structural Support pocket: drives spec fields, no node involved.
        if (_structSupportGizmoDrag && DataContext is ViewportViewModel ssVm)
        {
            ProcessStructuralSupportGizmoDrag(ssVm, mx, my);
            return;
        }

        if (_renderer.SelectedNode is not { } node) return;

        _gizmoDragCurrScreenX = mx;

        float vpW = (float)GlCanvas.Bounds.Width;
        float vpH = (float)GlCanvas.Bounds.Height;
        var ray   = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);

        float denom = Vector3.Dot(ray.Direction, _gizmoDragPlaneNormal);
        if (MathF.Abs(denom) < 1e-5f) return;
        float t      = Vector3.Dot(_gizmoDragPlanePoint - ray.Origin, _gizmoDragPlaneNormal) / denom;
        var hitWorld = ray.At(t);

        // A modifier plane is a real, independent SceneNode — dragging it runs through the
        // exact same Process*Drag code as any other object, just constrained to the one axis
        // its data model can actually represent (only the Z rotate ring means anything for a
        // Vertical plane; Scale never means anything — already blocked in BeginGizmoDrag).
        var modifierCut = DataContext is ViewportViewModel vmCut ? vmCut.FindModifierForNode(node) : null;

        var dragOp = _kbTransformActive ? _kbTransformOp : _renderer.GizmoMode;
        switch (dragOp)
        {
            case GizmoMode.Translate:
                ProcessTranslateDrag(node, hitWorld);
                break;
            case GizmoMode.Scale when modifierCut is null:
                ProcessScaleDrag(node, hitWorld);
                break;
            case GizmoMode.Rotate when modifierCut is null
                || (modifierCut.Orientation == CutOrientation.Vertical && _gizmoDragAxis == GizmoAxis.Z):
                // Only the Z ring means anything for a Vertical plane (it only ever rotates
                // around the vertical axis); Horizontal has no rotation field at all — X/Y/Z
                // rotate rings are all ignored rather than producing a tilt the data model
                // (and CutModifierNodeSync's extraction) can't actually represent.
                ProcessRotateDrag(node, hitWorld);
                break;
        }
        ApplyTransformLink(node);

        if (modifierCut is { } cut && DataContext is ViewportViewModel vmMod)
            vmMod.SyncModifierAfterGizmoEdit(cut, node);
    }

    private void ProcessCutPlaneGizmoDrag(CutToolDialogViewModel session, float mx, float my)
    {
        _gizmoDragCurrScreenX = mx;
        float vpW = (float)GlCanvas.Bounds.Width;
        float vpH = (float)GlCanvas.Bounds.Height;
        var ray = _renderer.Camera.GetPickRay(mx, my, vpW, vpH);
        float denom = Vector3.Dot(ray.Direction, _gizmoDragPlaneNormal);
        if (MathF.Abs(denom) < 1e-5f) return;
        float t = Vector3.Dot(_gizmoDragPlanePoint - ray.Origin, _gizmoDragPlaneNormal) / denom;
        var hitWorld = ray.At(t);

        var dragOp = _kbTransformActive ? _kbTransformOp : _renderer.GizmoMode;
        var center = new Vector3((float)session.CenterX, (float)session.CenterY, (float)session.CenterZ);
        var normal = new Vector3((float)session.NormalX, (float)session.NormalY, (float)session.NormalZ);
        if (normal.LengthSquared < 1e-10f) normal = Vector3.UnitZ;
        else normal = Vector3.Normalize(normal);

        if (dragOp == GizmoMode.Rotate)
        {
            var rel = hitWorld - _gizmoDragPlanePoint;
            float angle = AxisAngle(_gizmoDragAxis, rel);
            float delta = angle - _gizmoDragStartAngle;
            _gizmoDragStartAngle = angle;
            // Rotate normal about the dragged axis (world axes).
            var rot = _gizmoDragAxis switch
            {
                GizmoAxis.X => Matrix4.CreateRotationX(delta),
                GizmoAxis.Y => Matrix4.CreateRotationY(delta),
                _           => Matrix4.CreateRotationZ(delta),
            };
            var n4 = new Vector4(normal, 0f) * rot;
            normal = Vector3.Normalize(new Vector3(n4.X, n4.Y, n4.Z));
            session.SetPose(
                new System.Numerics.Vector3(center.X, center.Y, center.Z),
                new System.Numerics.Vector3(normal.X, normal.Y, normal.Z));
        }
        else
        {
            // Translate: project movement onto the active axis.
            var delta = hitWorld - _gizmoDragStartHit;
            float along = Vector3.Dot(delta, _gizmoDragAxisDir);
            center += _gizmoDragAxisDir * along;
            _gizmoDragStartHit += _gizmoDragAxisDir * along;
            _gizmoDragPlanePoint = center;
            session.SetPose(
                new System.Numerics.Vector3(center.X, center.Y, center.Z),
                new System.Numerics.Vector3(normal.X, normal.Y, normal.Z));
        }
        GlCanvas.RequestNextFrameRendering();
    }

    // ── Model ↔ toolpath transform linking ───────────────────────────────────
    // A model and its toolpath(s) move as one rigid assembly: dragging either one
    // carries the others along. They are scene-graph siblings with independent
    // transforms (and different parents on rotary cells), so the link is applied
    // as a world-space delta re-expressed in each follower's parent frame.
    private List<(SceneNode Node, Matrix4 InitialWorld, Matrix4 InvParentWorld)>? _transformLinkFollowers;
    private Matrix4 _transformLinkDraggedInvInitialWorld;

    /// <summary>All nodes rigidly linked to <paramref name="node"/>: for a model, its
    /// toolpaths; for a toolpath, its model plus sibling toolpaths.</summary>
    private static List<SceneNode> ResolveLinkedNodes(ViewportViewModel vm, SceneNode node)
    {
        var result = new List<SceneNode>();
        foreach (var item in vm.GetUserModelItems())
        {
            var toolpaths = item.Children.Where(c => c.IsToolpath).ToList();
            bool isModel    = ReferenceEquals(item.Node, node);
            bool isToolpath = toolpaths.Any(c => ReferenceEquals(c.Node, node));
            if (!isModel && !isToolpath) continue;

            if (!isModel) result.Add(item.Node);
            foreach (var c in toolpaths)
                if (!ReferenceEquals(c.Node, node))
                    result.Add(c.Node);
            break;
        }
        return result;
    }

    /// <summary>Captures follower baselines at drag start.</summary>
    private void BeginTransformLink(SceneNode node)
    {
        _transformLinkFollowers = null;
        if (DataContext is not ViewportViewModel vm) return;
        var linked = ResolveLinkedNodes(vm, node);
        if (linked.Count == 0) return;

        Matrix4.Invert(node.WorldTransform, out _transformLinkDraggedInvInitialWorld);
        var followers = new List<(SceneNode, Matrix4, Matrix4)>(linked.Count);
        foreach (var ln in linked)
        {
            var parentWorld = ln.Parent?.WorldTransform ?? Matrix4.Identity;
            Matrix4.Invert(parentWorld, out var invParent);
            followers.Add((ln, ln.WorldTransform, invParent));
        }
        _transformLinkFollowers = followers;
    }

    /// <summary>Re-poses every follower from the dragged node's current transform.</summary>
    private void ApplyTransformLink(SceneNode node)
    {
        if (_transformLinkFollowers is not { Count: > 0 } followers) return;
        // Row-vector convention: worldDelta applied on the right.
        var worldDelta = _transformLinkDraggedInvInitialWorld * node.WorldTransform;
        foreach (var (fn, initialWorld, invParent) in followers)
            fn.LocalTransform = initialWorld * worldDelta * invParent;
    }

    private void EndTransformLink() => _transformLinkFollowers = null;

    /// <summary>One-shot link for typed coordinate edits (no drag session).</summary>
    private void MirrorTypedTransformDelta(ViewportViewModel vm, SceneNode node, Matrix4 oldLocal)
    {
        var linked = ResolveLinkedNodes(vm, node);
        if (linked.Count == 0) return;

        var parentWorld = node.Parent?.WorldTransform ?? Matrix4.Identity;
        var oldWorld    = oldLocal * parentWorld;
        Matrix4.Invert(oldWorld, out var invOldWorld);
        var worldDelta = invOldWorld * node.WorldTransform;

        foreach (var ln in linked)
        {
            var lp = ln.Parent?.WorldTransform ?? Matrix4.Identity;
            Matrix4.Invert(lp, out var invLp);
            ln.LocalTransform = ln.WorldTransform * worldDelta * invLp;
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

        if (_toolIsDragging)
        {
            ApplyToolRotationDelta(delta);
            return;
        }

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

    private void MergeSelectedScans(ViewportViewModel vm, ScanMergeOutput output)
    {
        var items = vm.SelectedScanItems.ToList();
        if (items.Count < 2)
        {
            System.Console.WriteLine($"[scan-merge] need 2+ scans, have {items.Count}");
            return;
        }

        var nodes = items.Select(i => i.Node).ToList();
        var result = ScanMerger.Merge(nodes, output);
        if (result is null)
        {
            System.Console.WriteLine("[scan-merge] no geometry to merge");
            return;
        }

        var label = output == ScanMergeOutput.PointCloud
            ? $"Merged Scan ({result.SourceCount} clouds)"
            : $"Merged Scan ({result.SourceCount} meshes)";

        var anchorWorld = nodes[0].WorldTransform;
        var localMesh   = ScanMerger.ToLocalFrame(result.Mesh, anchorWorld);

        var merged = new SceneNode
        {
            Name            = label,
            PendingMesh     = localMesh,
            LocalTransform  = anchorWorld,
            Selectable      = true,
            Visible         = true,
            CullFaces       = false,
            PickTier        = PickTier.Content,
        };

        vm.AddScanNode(merged);

        foreach (var item in items)
            item.Visible = false;

        vm.ClearScanOutlinerSelection();
        _renderer.Select(merged);
        vm.SetOutlinerSelection(merged);
        UpdateFocusOverlay();
        GlCanvas.RequestNextFrameRendering();

        System.Console.WriteLine(
            $"[scan-merge] {label}: {result.PointCount:N0} points, {result.TriangleCount:N0} triangles");
    }

    /// <summary>Toolpath node used for sequence selection: the node itself when it is a
    /// toolpath, else the picked model's toolpath child (shift+click on a mesh).</summary>
    private SceneNode? ResolveSequenceToolpath(ViewportViewModel vm, SceneNode picked)
    {
        if (_renderer.IsToolpathNode(picked)) return picked;
        var item = vm.FindUserMeshOutlinerItem(picked);
        return item?.Children.FirstOrDefault(c => c.IsToolpath)?.Node;
    }

    /// <summary>
    /// Shift+click sequence toggle (viewport pick or outliner row). Selection order
    /// defines the print order; when starting from a plain single selection, that
    /// object's toolpath is added first so "select A, shift+click B" yields A → B.
    /// </summary>
    private void ToggleSequenceSelection(ViewportViewModel vm, SceneNode picked)
    {
        var tp = ResolveSequenceToolpath(vm, picked);
        if (tp is null) return;

        if (_renderer.SelectedToolpathCount == 0 && _renderer.SelectedNode is { } cur)
        {
            var curTp = ResolveSequenceToolpath(vm, cur);
            if (curTp is not null && curTp != tp)
                _renderer.ToggleToolpathSelection(curTp);
        }
        _renderer.ToggleToolpathSelection(tp);
        UpdateFocusOverlay();
        GlCanvas.RequestNextFrameRendering();
    }

    /// <summary>
    /// Outliner shift+click: standard range extension — selects every user model's
    /// toolpath from the anchor (current selection / first sequence member) through
    /// the clicked row, in outliner order. No anchor falls back to a single toggle.
    /// </summary>
    private void SequenceRangeSelect(ViewportViewModel vm, OutlinerItemViewModel clicked)
    {
        var clickedModel = clicked.IsToolpath ? vm.OwningModelItem(clicked) : clicked;
        if (clickedModel is null) return;

        var models = vm.GetUserModelItems();
        int target = models.IndexOf(clickedModel);
        if (target < 0) return;

        OutlinerItemViewModel? anchorModel = null;
        if (_renderer.SelectedToolpathCount > 0 &&
            vm.FindOutlinerItem(_renderer.SelectedToolpaths[0]) is { } firstItem)
            anchorModel = vm.OwningModelItem(firstItem);
        else if (_renderer.SelectedNode is { } cur
                 && vm.FindUserMeshOutlinerItem(cur) is { } curItem)
            anchorModel = curItem.IsToolpath ? vm.OwningModelItem(curItem) : curItem;

        int a = anchorModel is not null ? models.IndexOf(anchorModel) : -1;
        if (a < 0)
        {
            ToggleSequenceSelection(vm, clickedModel.Node);
            return;
        }

        int step = target >= a ? 1 : -1;
        for (int i = a; i != target + step; i += step)
        {
            var tp = models[i].Children.FirstOrDefault(c => c.IsToolpath)?.Node;
            if (tp is null) continue;
            if (!_renderer.SelectedToolpaths.Contains(tp))
                _renderer.ToggleToolpathSelection(tp);
        }
        UpdateFocusOverlay();
        GlCanvas.RequestNextFrameRendering();
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
        ClearTcpKeyframeState(node, vm);
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
            int newMax = merged.Layers.Sum(l => l.Moves.Count);
            vm.ResetScrubIndex(newMax, merged, preservePosition: vm.IsScrubSessionActive);
            ApplyToolpathStats(vm, merged);
            ValidateToolpathAsync(node, merged);
            ScrubIkForNode(node, vm.ToolpathScrubIndex);
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

    /// <summary>
    /// Records the simulate-timeline sweep (0–100% over 6 s) as an MP4: steps the
    /// timeline at 30 fps, reads back each GL frame as PNG, and pipes the sequence
    /// into ffmpeg. The robot IK follow runs exactly as it does during live playback.
    /// </summary>
    private async Task ExportSimVideoAsync(ViewportViewModel vm)
    {
        if (vm.SimRecording) return;
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        string? ffmpeg = new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg" }
            .FirstOrDefault(File.Exists);
        var mvmLog = topLevel.DataContext as MainWindowViewModel;
        if (ffmpeg is null)
        {
            mvmLog?.Console.Log("[simvideo] ffmpeg not found — install it (brew install ffmpeg) to export videos.");
            SetSliceStatus(vm, "Video export needs ffmpeg (brew install ffmpeg).", isError: true);
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export Simulation Video",
            DefaultExtension  = "mp4",
            SuggestedFileName = "toolpath-simulation",
            FileTypeChoices   = [new("MP4 Video") { Patterns = ["*.mp4"] }],
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (path is null) return;
        path = SavePathUtil.Normalize(path, "mp4");

        const int fps = 30, seconds = 6, totalFrames = fps * seconds;
        double restorePercent = vm.SimTimelinePercent;
        vm.SimRecording = true;
        SetSliceStatus(vm, "Recording simulation video…");

        System.Diagnostics.Process? proc = null;
        try
        {
            proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = ffmpeg,
                Arguments = "-y -f image2pipe -framerate 30 -i pipe:0 " +
                            "-c:v libx264 -pix_fmt yuv420p -crf 18 " +
                            "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" " +
                            $"\"{path}\"",
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute       = false,
            });
            if (proc is null) throw new InvalidOperationException("could not start ffmpeg");
            // Drain stderr so ffmpeg never blocks on a full pipe.
            _ = proc.StandardError.ReadToEndAsync();

            var stdin = proc.StandardInput.BaseStream;
            for (int i = 0; i < totalFrames; i++)
            {
                vm.SimTimelinePercent = i * 100.0 / (totalFrames - 1);
                await Task.Delay(12);                    // let the async IK land on the pose
                GlCanvas.RequestNextFrameRendering();
                var png = await GlCanvas.CaptureScreenshotPngAsync();
                if (png is null) throw new InvalidOperationException($"frame {i} capture failed");
                await stdin.WriteAsync(png);
                if (i % fps == 0)
                    SetSliceStatus(vm, $"Recording simulation video… {i * 100 / totalFrames}%");
            }
            stdin.Close();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited with {proc.ExitCode}");

            mvmLog?.Console.Log($"[simvideo] Simulation video → {path}");
            SetSliceStatus(vm, $"Simulation video saved: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
            mvmLog?.Console.Log($"[simvideo] Export failed: {ex.Message}");
            SetSliceStatus(vm, $"Video export failed: {ex.Message}", isError: true);
        }
        finally
        {
            vm.SimRecording = false;
            vm.SimTimelinePercent = restorePercent;
        }
    }

    private async Task ExportScanPointCloudAsync(ViewportViewModel vm, SceneNode node)
    {
        if (!OutlinerModelOps.IsScan(node)) return;
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var safeName = string.Join('_', node.Name.Split(Path.GetInvalidFileNameChars()));
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export Point Cloud",
            DefaultExtension  = "ply",
            SuggestedFileName = safeName,
            FileTypeChoices   = [new("PLY Point Cloud") { Patterns = ["*.ply"] }],
        });
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (path is null) return;
        path = SavePathUtil.Normalize(path, "ply");

        try
        {
            await Task.Run(() => ScanGeometryExporter.ExportPointCloud(path, node));
            if (topLevel.DataContext is MainWindowViewModel mvm)
                mvm.Console.Log($"[export] Point cloud → {path}");
        }
        catch (Exception ex)
        {
            if (topLevel.DataContext is MainWindowViewModel mvm)
                mvm.Console.Log($"[export] Failed: {ex.Message}");
        }
    }

    private async Task ExportScanMeshAsync(ViewportViewModel vm, SceneNode node)
    {
        if (!OutlinerModelOps.IsScan(node)) return;
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var safeName = string.Join('_', node.Name.Split(Path.GetInvalidFileNameChars()));
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export Mesh",
            DefaultExtension  = "stl",
            SuggestedFileName = safeName,
            FileTypeChoices   = [new("STL Mesh") { Patterns = ["*.stl"] }],
        });
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (path is null) return;
        path = SavePathUtil.Normalize(path, "stl");

        try
        {
            await Task.Run(() => ScanGeometryExporter.ExportMesh(path, node));
            if (topLevel.DataContext is MainWindowViewModel mvm)
                mvm.Console.Log($"[export] Mesh → {path}");
        }
        catch (Exception ex)
        {
            if (topLevel.DataContext is MainWindowViewModel mvm)
                mvm.Console.Log($"[export] Failed: {ex.Message}");
        }
    }

    /// <summary>
    /// If robot validation flagged issues for this toolpath, asks the operator to confirm
    /// before exporting. Returns false to abort the export.
    /// </summary>
    private async Task<bool> ConfirmExportDespiteValidationAsync(SceneNode node)
    {
        if (!_validationIssuesByNode.TryGetValue(node, out var vi) || vi.Unreachable + vi.Singular == 0)
            return true;

        if (Avalonia.Controls.TopLevel.GetTopLevel(this) is not Window owner) return true;

        string zRange = vi.ZLo <= vi.ZHi ? $" between Z {vi.ZLo:0} and {vi.ZHi:0} mm" : "";
        var dlg = new Window
        {
            Title = "Robot Validation Warning",
            Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };
        var msg = new TextBlock
        {
            Text = $"⚠ This toolpath has {vi.Singular:N0} singularity-risk moves and " +
                   $"{vi.Unreachable:N0} unreachable moves{zRange}.\n\n" +
                   "The robot is likely to fault mid-print. " +
                   "Scrub the timeline to the purple/red markers to inspect, or adjust the toolhead " +
                   "orientation before exporting.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(20, 18, 20, 12),
        };
        var exportBtn = new Button { Content = "Export anyway", Padding = new Thickness(14, 6, 14, 6) };
        var gotoBtn   = new Button { Content = "Go to issue",   Padding = new Thickness(14, 6, 14, 6) };
        var cancelBtn = new Button { Content = "Cancel",        Padding = new Thickness(14, 6, 14, 6) };
        exportBtn.Click += (_, _) => dlg.Close(true);
        gotoBtn.Click   += (_, _) => { dlg.Close(false); _vm?.JumpToValidationIssue(); };
        cancelBtn.Click += (_, _) => dlg.Close(false);
        dlg.Content = new StackPanel
        {
            Children =
            {
                msg,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(20, 0, 20, 16),
                    Children = { cancelBtn, gotoBtn, exportBtn },
                },
            },
        };
        return await dlg.ShowDialog<bool?>(owner) == true;
    }

    private async Task ExportKrlAsync(ViewportViewModel vm)
    {
        var toolpath = vm.ActiveScrubToolpath;
        var node     = _activeScrubNode;
        var cell     = vm.ActiveCell;
        var settings = vm.AdditiveSettings;

        if (toolpath is null || node is null || cell is null || settings is null) return;

        if (!await ConfirmExportDespiteValidationAsync(node)) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title              = "Export KRL",
            DefaultExtension   = "src",
            SuggestedFileName  = RobotKrlPaths.SuggestedFileName(node.Name),
            FileTypeChoices    = [new("KRL Source") { Patterns = ["*.src"] }],
        });
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (path is null) return;

        // Enforce PointLoader-safe stem even if the user typed special characters in the dialog.
        path = SavePathUtil.Normalize(path, "src");
        var dir = Path.GetDirectoryName(path) ?? ".";
        var stem = RobotKrlPaths.SanitizeStem(Path.GetFileNameWithoutExtension(path));
        if (string.IsNullOrWhiteSpace(stem)) stem = "PrintJob";
        path = Path.Combine(dir, stem + ".src");

        await WriteKrlAsync(vm, toolpath, node, cell, settings, path);
    }

    /// <summary>Writes the active toolpath's KRL into <paramref name="dir"/> named after
    /// the source geometry; returns the path or null when no toolpath is active.</summary>
    private async Task<string?> ExportKrlToDirectoryAsync(ViewportViewModel vm, string dir, int rev)
    {
        var toolpath = vm.ActiveScrubToolpath;
        var node     = _activeScrubNode;
        var cell     = vm.ActiveCell;
        var settings = vm.AdditiveSettings;
        if (toolpath is null || node is null || cell is null || settings is null) return null;

        // Rev in the filename (and therefore the KRL program name) so the operator
        // can tell revisions apart on the controller, e.g. "2026_0710 - Drone Print V90 Rev08.src".
        // Sanitized for PointLoader (keeps spaces / " - " / RevNN; drops crazy punctuation).
        string path = Path.Combine(dir, RobotKrlPaths.SuggestedSrcFileName(node.Name, rev));
        await WriteKrlAsync(vm, toolpath, node, cell, settings, path);
        return path;
    }

    private async Task SendToRobotAsync(ViewportViewModel vm)
    {
        // Destination dropdown: MassiveDRIVE package send vs classic KRL→robot SMB.
        if (vm.SelectedSendTarget?.Kind == SendTargetKind.MassiveDrive)
        {
            await SendToMassiveDriveAsync(vm);
            return;
        }

        var toolpath = vm.ActiveScrubToolpath;
        var node     = _activeScrubNode;
        var cell     = vm.ActiveCell;
        var settings = vm.AdditiveSettings;

        if (toolpath is null || node is null || cell is null || settings is null) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        var mvm = topLevel?.DataContext as MainWindowViewModel;

        var cfg = vm.RobotSmb.ActiveConfig;
        if (cfg is null || !vm.RobotSmb.IsConfigured)
        {
            mvm?.Console.LogError(
                $"[robot] {cell.Name} has no SMB credentials — set IP/username/password under ROBOT NETWORK in the cell panel, or use the save button to export .src manually.");
            SetSliceStatus(vm,
                $"⚠ Send to Robot: {cell.Name} has no robot-network credentials. " +
                "Set IP/username/password under ROBOT NETWORK in the cell panel, or export the .src manually.",
                isError: true);
            return;
        }

        if (!await ConfirmExportDespiteValidationAsync(node)) return;

        // The NAS keeps a copy per revision (3D Print Files/Rev N/) and the robot
        // receives the same Rev-numbered filename; temp fallback when the workspace
        // has never been saved to the share.
        string srcPath;
        string fileName;
        var nasSrc = mvm is not null ? await mvm.ExportSrcToPrintFilesAsync() : null;
        if (nasSrc is { } n)
        {
            srcPath  = n.Path;
            // Sanitize upload name (PointLoader rejects odd punctuation in the module filename).
            fileName = RobotKrlPaths.SanitizeStem(Path.GetFileNameWithoutExtension(n.Path));
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "PrintJob";
            fileName += ".src";
        }
        else
        {
            fileName = RobotKrlPaths.SuggestedSrcFileName(node.Name);
            srcPath  = Path.Combine(Path.GetTempPath(), fileName);
            await WriteKrlAsync(vm, toolpath, node, cell, settings, srcPath);
            mvm?.Console.Log("[robot] workspace not saved on the NAS — no 3D Print Files copy kept.");
        }
        byte[] content = await File.ReadAllBytesAsync(srcPath);

        // Pre-flight: a KRL program must end with END — a truncated file is exactly
        // how the Jefre curtain print died. Never send an incomplete program.
        string tail = System.Text.Encoding.ASCII.GetString(
            content, Math.Max(0, content.Length - 512), Math.Min(512, content.Length)).TrimEnd();
        if (content.Length == 0 || !tail.EndsWith("END", StringComparison.OrdinalIgnoreCase))
        {
            mvm?.Console.LogError($"[robot] REFUSED: {fileName} is incomplete (no trailing END, {content.Length:N0} bytes).");
            SetSliceStatus(vm,
                $"⚠ Send to Robot refused: {fileName} is incomplete (no trailing END) — re-export before sending.",
                isError: true);
            return;
        }

        mvm?.Console.Log($"[robot] Uploading {fileName} to \\\\{cfg.Host}\\{cfg.Share} ({cell.Name})…");
        var (ok, message) = await Task.Run(() => RobotSmbUploader.Upload(cfg, fileName, content));
        if (!ok)
        {
            mvm?.Console.LogError($"[robot] Upload failed — {message}");
            SetSliceStatus(vm, $"⚠ Send to Robot failed: {message}", isError: true);
            return;
        }
        mvm?.Console.Log($"[robot] Sent to {cell.Name}: {message}");
        if (mvm is not null)
            mvm.StatusBar.OperationFeedback =
                $"✓ Sent {fileName} to {cell.Name} — {content.Length:N0} bytes, verified END";

        if (mvm is not null)
            await mvm.NotifyErpSentToRobotAsync(srcPath, fileName, cell.Name, cfg.Host);
    }

    /// <summary>
    /// Export toolpath as massivedrive.job/v1 and start MassiveDRIVE path executor.
    /// Does not upload KRL to the robot.
    /// </summary>
    private async Task SendToMassiveDriveAsync(ViewportViewModel vm)
    {
        var toolpath = vm.ActiveScrubToolpath;
        var node     = _activeScrubNode;
        var cell     = vm.ActiveCell;
        var settings = vm.AdditiveSettings;
        var target   = vm.SelectedSendTarget;

        if (toolpath is null || node is null || cell is null || settings is null) return;
        if (target is null || target.Kind != SendTargetKind.MassiveDrive
            || string.IsNullOrWhiteSpace(target.Url))
        {
            SetSliceStatus(vm,
                "⚠ MassiveDRIVE URL not configured for this cell (massiveDriveUrl in cell JSON).",
                isError: true);
            return;
        }

        if (!await ConfirmExportDespiteValidationAsync(node)) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        var mvm = topLevel?.DataContext as MainWindowViewModel;

        var exportSettings = new MassiveDriveExportSettings
        {
            Name = string.IsNullOrWhiteSpace(node.Name) ? "print-job" : node.Name,
            CellId = target.CellId ?? cell.MassiveDriveCellId ?? "lfam3",
            Tool = settings.ToolDataIndex,
            Base = settings.BaseDataIndex,
            PrintSpeedMmS = (float)settings.PrintSpeed,
            TravelSpeedMmS = (float)settings.TravelSpeed,
            ReverseMs = 200f,
            ReversePercent = 40f,
            TravelReverse = true,
            // Same toolhead offsets as KRL export — ABC must match viewport / KukaAbc
            ToolheadOffsetA = (float)settings.ToolheadA,
            ToolheadOffsetB = (float)settings.ToolheadB,
            ToolheadOffsetC = (float)settings.ToolheadC,
            WorkspacePath = mvm?.AppPreferences.LastWorkspacePath,
            SourceNote = $"cell={cell.Name}",
        };

        Dictionary<string, object?> package;
        try
        {
            package = MassiveDriveJobExporter.ExportDict(toolpath, exportSettings);
        }
        catch (Exception ex)
        {
            mvm?.Console.LogError($"[drive] Export package failed: {ex.Message}");
            SetSliceStatus(vm, $"⚠ MassiveDRIVE export failed: {ex.Message}", isError: true);
            return;
        }

        var segCount = (package["segments"] as System.Collections.ICollection)?.Count ?? 0;
        mvm?.Console.Log(
            $"[drive] Sending \"{exportSettings.Name}\" ({segCount} segments) → {target.Url} …");

        try
        {
            using var client = new MassiveDriveClient(target.Url!);
            // Health check first for clearer errors
            try
            {
                using var health = await client.HealthAsync();
            }
            catch (Exception hex)
            {
                mvm?.Console.LogError($"[drive] Health check failed at {target.Url}: {hex.Message}");
                SetSliceStatus(vm,
                    $"⚠ MassiveDRIVE unreachable at {target.Url} — is serve running?",
                    isError: true);
                return;
            }

            var result = await client.SendAndStartAsync(package);
            mvm?.Console.Log(
                $"[drive] Sent package {result.PackageId} to {cell.Name} — path executor started.");
            if (mvm is not null)
            {
                mvm.StatusBar.OperationFeedback =
                    $"✓ Sent to MassiveDRIVE ({cell.Name}): {result.PackageId} — {segCount} segments";
            }
            SetSliceStatus(vm,
                $"✓ Sent to MassiveDRIVE — {result.PackageId} ({segCount} segs). Robot needs RSI runtime armed.",
                isError: false);
        }
        catch (MassiveDriveClientException ex)
        {
            mvm?.Console.LogError($"[drive] Send failed: {ex.Message}");
            SetSliceStatus(vm, $"⚠ MassiveDRIVE send failed: {ex.Message}", isError: true);
        }
        catch (Exception ex)
        {
            mvm?.Console.LogError($"[drive] Send failed: {ex.Message}");
            SetSliceStatus(vm, $"⚠ MassiveDRIVE send failed: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Home PTP E1: first planned move E1 when motion is on, else live rail pose.
    /// </summary>
    private static float ResolveHomeE1Mm(
        CellConfig cell, ViewportViewModel vm, Toolpath toolpath, AdditiveSettingsViewModel settings)
    {
        if (cell.RobotRail is null) return float.NaN;
        if (settings.E1MotionEnabled)
        {
            foreach (var layer in toolpath.Layers)
            foreach (var m in layer.Moves)
                if (!float.IsNaN(m.E1Mm))
                    return m.E1Mm;
        }
        return vm.Robot is { } r ? (float)r.E1 : 0f;
    }

    /// <summary>
    /// For each move endpoint, sample E1 across the Y+/Y− allowance and pick the
    /// carriage position that keeps the TCP in the arm workspace (prefer mid-reach).
    /// Bakes <see cref="ToolpathMove.E1Mm"/> for the KRL exporter.
    /// </summary>
    private void PlanRailE1ForExport(
        Toolpath toolpath,
        CellConfig cell,
        AdditiveSettingsViewModel settings,
        NVec3 origin,
        Matrix4 wt,
        float homeE1)
    {
        var rail = cell.RobotRail;
        if (rail is null) return;

        float yPlus  = (float)settings.E1YPlusMm;
        float yMinus = (float)settings.E1YMinusMm;
        var homeWorld = new NVec3(
            cell.Robot.WorldPosition.X,
            cell.Robot.WorldPosition.Y,
            cell.Robot.WorldPosition.Z);

        // Collect world-space move endpoints in export order.
        var worlds = new List<NVec3>(4096);
        var moves  = new List<ToolpathMove>(4096);
        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                float lx = move.To.X - origin.X, ly = move.To.Y - origin.Y, lz = move.To.Z - origin.Z;
                var world = new NVec3(
                    lx * wt.M11 + ly * wt.M21 + lz * wt.M31 + wt.M41,
                    lx * wt.M12 + ly * wt.M22 + lz * wt.M32 + wt.M42,
                    lx * wt.M13 + ly * wt.M23 + lz * wt.M33 + wt.M43);
                worlds.Add(world);
                moves.Add(move);
            }
        }
        if (worlds.Count == 0) return;

        // Prefer mid-reach from the live IK envelope when available; else ~900 mm.
        float prefReach = 900f;
        Func<NVec3, bool>? inWs = null;
        var solver = _ikSolver;
        if (solver is not null)
        {
            prefReach = solver.PreferredHorizontalReachMm;
            // Envelope is translation-invariant for pure rail travel — evaluate TCP
            // relative to a virtual base at candidate E1 (no UpdateSceneBase needed).
            inWs = rel => solver.IsInWorkspace(new TkVector3(rel.X, rel.Y, rel.Z));
        }

        // Subsample dense paths for speed: plan every keyframe, interpolate between.
        const float KeyMm = 40f;
        var keyIdx = new List<int> { 0 };
        float acc = 0f;
        for (int i = 1; i < worlds.Count; i++)
        {
            acc += NVec3.Distance(worlds[i - 1], worlds[i]);
            if (acc >= KeyMm)
            {
                keyIdx.Add(i);
                acc = 0f;
            }
        }
        if (keyIdx[^1] != worlds.Count - 1)
            keyIdx.Add(worlds.Count - 1);

        var keyWorlds = new List<NVec3>(keyIdx.Count);
        foreach (int i in keyIdx)
            keyWorlds.Add(worlds[i]);

        float[] keyE1 = RailE1Planner.PlanPath(
            keyWorlds, homeWorld, rail, homeE1, yPlus, yMinus,
            prefReach, inWs, gridCount: 11, smoothBlend: 0.45f);

        // Interpolate key E1 → every move; sparse full-IK refinement on unreachable keys.
        if (solver is not null)
            RefineKeyE1WithIk(keyWorlds, keyE1, homeWorld, rail, homeE1, yPlus, yMinus, solver, settings);

        // Paint onto moves
        int k = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            while (k + 1 < keyIdx.Count && i > keyIdx[k + 1]) k++;
            float e1;
            if (k + 1 < keyIdx.Count && keyIdx[k + 1] != keyIdx[k])
            {
                float t = (i - keyIdx[k]) / (float)(keyIdx[k + 1] - keyIdx[k]);
                e1 = keyE1[k] * (1f - t) + keyE1[k + 1] * t;
            }
            else
                e1 = keyE1[Math.Min(k, keyE1.Length - 1)];

            e1 = RailE1Planner.ClampToAllowance(e1, homeE1, yPlus, yMinus, rail.MinMm, rail.MaxMm);
            moves[i].E1Mm = e1;
        }
    }

    /// <summary>
    /// For keyframes still outside the workspace envelope at their planned E1, try a few
    /// more E1 samples with a cheap position-only IK solve (serial — no Parallel.For).
    /// </summary>
    private static void RefineKeyE1WithIk(
        List<NVec3> keyWorlds,
        float[] keyE1,
        NVec3 homeWorld,
        RobotRailCellConfig rail,
        float homeE1,
        float yPlus,
        float yMinus,
        GltfNumericalIkSolver solver,
        AdditiveSettingsViewModel settings)
    {
        float offA = (float)settings.ToolheadA;
        float offB = (float)settings.ToolheadB;
        float offC = (float)settings.ToolheadC;
        var seed = new float[6]; // home-ish zeros; Solve will iterate

        for (int i = 0; i < keyWorlds.Count; i++)
        {
            var w = keyWorlds[i];
            var baseW = RailE1Planner.BaseWorld(homeWorld, rail, keyE1[i]);
            var rel = w - baseW;
            if (solver.IsInWorkspace(new TkVector3(rel.X, rel.Y, rel.Z)))
                continue;

            // Failed envelope at planned E1 — re-pick using full sample set + quick Solve.
            var candidates = RailE1Planner.BuildCandidates(
                w, homeWorld, rail, homeE1, yPlus, yMinus, gridCount: 11);
            float best = keyE1[i];
            float bestScore = float.MaxValue;
            var normal = TkVector3.UnitZ;
            var rot = solver.TargetRotFromGlobalOrientation(normal, offA, offB, offC);

            foreach (float e1 in candidates)
            {
                var b = RailE1Planner.BaseWorld(homeWorld, rail, e1);
                var r = w - b;
                var tgt = new TkVector3(r.X, r.Y, r.Z);
                bool env = solver.IsInWorkspace(tgt);
                // Position-only solve (faster) as quality check when in envelope
                float[]? sol = env
                    ? solver.Solve(tgt, seed, maxIterations: 25, finalTolerance: 15f)
                    : null;
                float dxy = MathF.Sqrt(r.X * r.X + r.Y * r.Y);
                float score = (sol is not null ? 0f : env ? 50_000f : 1_000_000f)
                    + MathF.Abs(dxy - solver.PreferredHorizontalReachMm)
                    + 0.1f * MathF.Abs(e1 - homeE1);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = e1;
                    if (sol is not null) Array.Copy(sol, seed, 6);
                }
            }
            keyE1[i] = best;
        }
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

        // Reachability-aware E1 plan: sample +/− allowance, pick carriage pose that
        // keeps the TCP in the arm workspace, bake onto each move before export.
        if (settings.E1MotionEnabled && cell.RobotRail is not null)
        {
            float homeE1 = vm.Robot is { } rr ? (float)rr.E1 : 0f;
            RefreshIkSceneKinematics();
            await Task.Run(() => PlanRailE1ForExport(
                toolpath, cell, settings, origin, wt, homeE1));
            float eMin = float.MaxValue, eMax = float.MinValue;
            int nSet = 0;
            foreach (var layer in toolpath.Layers)
            foreach (var m in layer.Moves)
            {
                if (float.IsNaN(m.E1Mm)) continue;
                eMin = MathF.Min(eMin, m.E1Mm);
                eMax = MathF.Max(eMax, m.E1Mm);
                nSet++;
            }
            if (nSet > 0)
            {
                var mvm = TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;
                mvm?.Console.Log(
                    $"[E1] Reachability plan: {nSet} points, E1 range [{eMin:0.#} … {eMax:0.#}] mm " +
                    $"(home ± Y+={settings.E1YPlusMm:0}/Y−={settings.E1YMinusMm:0})");
            }
        }

        // Relief-milling toolpath: export a spindle program (LIN cuts + rapids), not extrusion.
        bool isMill = toolpath.Layers.Any(l => l.Moves.Any(m => m.Kind == MoveKind.Mill));
        if (isMill && vm.SubtractiveSettings is { } sub)
        {
            int spindleIdx = cell.EffectiveTools
                ?.FirstOrDefault(t => t.Name.Contains("Spindle", StringComparison.OrdinalIgnoreCase))?.KrlIndex ?? 3;

            var millExport = new KrlExportSettings
            {
                ProgramName      = Path.GetFileNameWithoutExtension(path),
                ToolDataIndex    = spindleIdx,
                BaseDataIndex    = settings.BaseDataIndex,
                IsMilling        = true,
                SpindleRpm       = (float)sub.SpindleRpm,
                CuttingFeedMmMin = (float)sub.FeedRateMmMin,
                PlungeFeedMmMin  = (float)sub.PlungeFeedMmMin,
                TravelSpeedMps   = (float)(settings.TravelSpeed / 1000.0),
                ApproachZMm      = (float)sub.RapidZMm,
                HomePosition     = settings.SelectedHomeAngles,
                HomeE1Mm         = cell.RobotRail is not null && vm.Robot is { } millRobot
                                       ? (float)millRobot.E1
                                       : float.NaN,
                RotaryExternalKinematic = cell.RotaryBed is not null,
                RotaryMachineDefIndex   = cell.RotaryBed?.MachineDefIndex ?? 2,
                E1MotionEnabled  = cell.RobotRail is not null && settings.E1MotionEnabled,
                E1YPlusMm        = (float)settings.E1YPlusMm,
                E1YMinusMm       = (float)settings.E1YMinusMm,
                RailMinMm        = cell.RobotRail?.MinMm ?? -4650f,
                RailMaxMm        = cell.RobotRail?.MaxMm ?? 150f,
                RailAxis         = cell.RobotRail?.Axis ?? "Y",
                RailE1Sign       = cell.RobotRail?.E1Sign ?? 1f,
                ApoCvel          = (int)settings.ApoCvel,
                NodeWorldTransform = sysWt,
                NodeOrigin       = new System.Numerics.Vector3(origin.X, origin.Y, origin.Z),
                RobrootWorldPos  = new System.Numerics.Vector3(
                    cell.Robot.WorldPosition.X, cell.Robot.WorldPosition.Y, cell.Robot.WorldPosition.Z),
                BaseDataOffset   = new System.Numerics.Vector3(
                    cell.Bed.BaseData.X, cell.Bed.BaseData.Y, cell.Bed.BaseData.Z),
                SliceBedWorldZ   = _renderer.BedZ,
                HeaderTemplate   = string.IsNullOrWhiteSpace(sub.HeaderTemplate) ? null : sub.HeaderTemplate,
                FooterTemplate   = string.IsNullOrWhiteSpace(sub.FooterTemplate) ? null : sub.FooterTemplate,
            };
            var millKrl = await Task.Run(() => KrlExporter.Export(toolpath, millExport));
            await File.WriteAllTextAsync(path, millKrl);
            return;
        }

        var selectedPreset = settings.SelectedPreset;
        var postProcess    = settings.KrlPostProcess.ToSettings();
        // Per-zone material setpoints + the all-zones additional offset.
        float exportTemp1  = settings.GetEffectiveExportTemperature(1);
        float exportTemp2  = settings.GetEffectiveExportTemperature(2);
        float exportTemp3  = settings.GetEffectiveExportTemperature(3);
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
            Temperature1        = exportTemp1,
            Temperature2        = exportTemp2,
            Temperature3        = exportTemp3,
            BeadWidthMm         = (float)settings.BeadWidth,
            LayerHeightMm       = (float)settings.LayerHeight,
            FlowRate            = flow,
            HomePosition              = settings.SelectedHomeAngles,
            HomeE1Mm                  = ResolveHomeE1Mm(cell, vm, toolpath, settings),
            RotaryExternalKinematic   = cell.RotaryBed is not null,
            RotaryMachineDefIndex     = cell.RotaryBed?.MachineDefIndex ?? 2,
            E1MotionEnabled           = cell.RobotRail is not null && settings.E1MotionEnabled,
            E1YPlusMm                 = (float)settings.E1YPlusMm,
            E1YMinusMm                = (float)settings.E1YMinusMm,
            RailMinMm                 = cell.RobotRail?.MinMm ?? -4650f,
            RailMaxMm                 = cell.RobotRail?.MaxMm ?? 150f,
            RailAxis                  = cell.RobotRail?.Axis ?? "Y",
            RailE1Sign                = cell.RobotRail?.E1Sign ?? 1f,
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
            SliceBedWorldZ     = _renderer.BedZ,
            TravelSetAnout4Zero = postProcess.TravelSetAnout4Zero,
            // URM: never pass LFAM post-process header/footer ($ANOUT MAT). Exporter also
            // The exporter renders placeholders and, in URM mode, keeps the edited header
            // only if it is still URM-shaped (else falls back to the Caracol URM default).
            HeaderTemplate = postProcess.HeaderText,
            FooterTemplate = postProcess.FooterText,
            ExtrusionRpmPercent     = settings.GetEffectiveExtrusionSpeedPercent(),
            // First-layer overrides: only pass a value when the operator set an override
            // (or it differs from the normal), so a plain print is unchanged. The exporter
            // treats 0 as "use the normal speed/RPM".
            FirstLayerSpeedMps      = settings.FirstLayerAdjustmentsEnabled && settings.FirstLayerSpeed > 0.0
                                          ? (float)(settings.FirstLayerSpeedEffective / 1000.0) : 0f,
            FirstLayerRpmPercent    = settings.FirstLayerAdjustmentsEnabled && settings.FirstLayerRpm > 0.0
                                          ? (float)settings.FirstLayerRpmEffective : 0f,
            ExtrusionStartWaitSec   = (float)settings.ExtrusionStartWaitSec,
            ExtrusionResumeWaitSec  = (float)settings.ExtrusionResumeWaitSec,
            SsPreTravelWaitSec      = (float)settings.SsPreTravelWaitSec,
            SsResumePrimePercent    = (float)settings.SsResumePrimePercent,
            DigitalStartStopEnabled = settings.DigitalStartStopEnabled,
        };

        var krl = await Task.Run(() => KrlExporter.Export(toolpath, exportSettings));
        await File.WriteAllTextAsync(path, krl);
    }
}
