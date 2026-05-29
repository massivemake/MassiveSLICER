using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.ViewModels.Base;
using ToolSwapRequest = (MassiveSlicer.Core.Models.ToolCellConfig Config, MassiveSlicer.Viewport.Scene.SceneNode Node);
using Toolpath = MassiveSlicer.Core.Models.Toolpath;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Manages the state of the 3D viewport: selection mode, active transform tool,
/// and overlay visibility flags. The actual OpenGL rendering lives in
/// <c>MassiveSlicer.Viewport</c>; this ViewModel only holds bindable state.
/// </summary>
public sealed class ViewportViewModel : ViewModelBase
{
    private SelectionMode _selectionMode = SelectionMode.Object;

    /// <summary>The active component selection mode (vertex/edge/face/object).</summary>
    public SelectionMode SelectionMode
    {
        get => _selectionMode;
        set => SetField(ref _selectionMode, value);
    }

    private TransformTool _activeTool = TransformTool.Select;

    /// <summary>The active transform gizmo tool (select/move/rotate/scale).</summary>
    public TransformTool ActiveTool
    {
        get => _activeTool;
        set => SetField(ref _activeTool, value);
    }

    private bool _showGrid = true;

    /// <summary>Whether the ground-plane grid is visible.</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set => SetField(ref _showGrid, value);
    }

    private bool _showAxes = true;

    /// <summary>Whether the world-space axis indicator is visible.</summary>
    public bool ShowAxes
    {
        get => _showAxes;
        set => SetField(ref _showAxes, value);
    }

    private bool _showBedGrid = true;

    /// <summary>Whether the print-bed boundary grid overlay is visible.</summary>
    public bool ShowBedGrid
    {
        get => _showBedGrid;
        set => SetField(ref _showBedGrid, value);
    }

    private bool _showBead = false;
    public bool ShowBead
    {
        get => _showBead;
        set => SetField(ref _showBead, value);
    }

    private bool _showExtrusionMoves = true;
    public bool ShowExtrusionMoves
    {
        get => _showExtrusionMoves;
        set => SetField(ref _showExtrusionMoves, value);
    }

    private bool _showTravelMoves = true;
    public bool ShowTravelMoves
    {
        get => _showTravelMoves;
        set => SetField(ref _showTravelMoves, value);
    }

    private bool _showSeam = true;
    public bool ShowSeam
    {
        get => _showSeam;
        set => SetField(ref _showSeam, value);
    }

    private bool _showDimensions;

    /// <summary>Whether the bounding-box dimension overlay is visible.</summary>
    public bool ShowDimensions
    {
        get => _showDimensions;
        set => SetField(ref _showDimensions, value);
    }

    private NavigationPresetId _activePreset = NavigationPresetId.Rhino;

    /// <summary>Active mouse-button navigation preset -- controls which buttons perform orbit/pan.</summary>
    public NavigationPresetId ActivePreset
    {
        get => _activePreset;
        set => SetField(ref _activePreset, value);
    }

    /// <summary>
    /// Scene nodes queued for addition to the scene graph. The producer enqueues after
    /// CPU-side loading; the render loop dequeues on the GL thread, uploads PendingMesh
    /// data to the GPU, then attaches the node to the scene root.
    /// </summary>
    public ConcurrentQueue<SceneNode> PendingNodes { get; } = new();

    /// <summary>
    /// Tool nodes queued for attachment to the robot's flange joint (joint_6).
    /// Drained by the render loop after <see cref="PendingNodes"/> so the FK
    /// controller and its joint references are guaranteed to exist in the same frame.
    /// Nodes are in raw GLTF space (no coordinate-conversion root); the robot
    /// root's GltfToScene transform in the parent chain handles the conversion.
    /// </summary>
    public ConcurrentQueue<SceneNode> PendingToolNodes { get; } = new();

    /// <summary>
    /// Tool swap requests. Each entry carries the new <see cref="ToolCellConfig"/>
    /// (for TCP/IK rebuild) and the pre-loaded <see cref="SceneNode"/> to attach.
    /// The render loop removes the old tool, uploads GPU resources, and re-attaches.
    /// </summary>
    public ConcurrentQueue<ToolSwapRequest> PendingToolSwap { get; } = new();

    /// <summary>
    /// Full cell swap requests. Each payload carries a pre-loaded set of scene nodes
    /// (robot, booster, bed, tool) plus the new <see cref="CellConfig"/>. The render
    /// loop clears the current scene and rebuilds it atomically on the GL thread.
    /// </summary>
    internal ConcurrentQueue<CellSwapPayload> PendingCellSwap { get; } = new();

    /// <summary>
    /// Reference to the robot panel ViewModel. Set by <c>MainWindowViewModel</c>
    /// at startup so the viewport render loop can read joint angles for FK.
    /// </summary>
    public RobotPanelViewModel? Robot { get; set; }

    /// <summary>
    /// The active cell configuration. Set at startup after loading the cell JSON.
    /// The viewport render loop applies bed boundary settings on the GL thread.
    /// </summary>
    public CellConfig? ActiveCell { get; set; }

    // -- Render request --------------------------------------------------------

    /// <summary>
    /// Raised when state has changed that requires the viewport to redraw.
    /// The viewport code-behind subscribes and calls <c>GlControl.InvalidateVisual()</c>.
    /// </summary>
    public event EventHandler? RenderNeeded;

    /// <summary>Signals the viewport to repaint on the next composition frame.</summary>
    public void NotifyRenderNeeded() => RenderNeeded?.Invoke(this, EventArgs.Empty);

    // -- Backdrop --------------------------------------------------------------

    /// <summary>A named backdrop option shown in the selector. <see cref="Path"/> is <c>null</c> for "None".</summary>
    public sealed record BackdropOption(string Name, string? Path);

    /// <summary>All backdrop images found in <c>assets/Images</c> plus a "None" entry.</summary>
    public IReadOnlyList<BackdropOption> AvailableBackdrops { get; }

    private BackdropOption _activeBackdrop;

    /// <summary>Currently selected backdrop. Set to the "None" entry to clear the backdrop.</summary>
    public BackdropOption ActiveBackdrop
    {
        get => _activeBackdrop;
        set
        {
            if (SetField(ref _activeBackdrop, value))
                NotifyRenderNeeded();
        }
    }

    /// <summary>Path of the active backdrop image, or <c>null</c> when none is selected.</summary>
    public string? ActiveBackdropPath => _activeBackdrop.Path;

    private float _backdropBlur = 2.5f;

    /// <summary>Mipmap LOD level for backdrop blur. 0 = sharp, 7 = maximum blur.</summary>
    public float BackdropBlur
    {
        get => _backdropBlur;
        set
        {
            if (SetField(ref _backdropBlur, value))
                NotifyRenderNeeded();
        }
    }

    /// <summary>
    /// Saves <see cref="ActiveBackdropPath"/> as the default via <see cref="OnSetDefaultBackdrop"/>.
    /// Wired by <c>MainWindowViewModel</c>.
    /// </summary>
    public ICommand SetDefaultBackdropCommand { get; }

    /// <summary>Callback invoked when the user sets a new default backdrop. Wired by MainWindowViewModel.</summary>
    internal Action<string?>? OnSetDefaultBackdrop { get; set; }

    // -- World light -----------------------------------------------------------

    private float _lightAzimuth   = 45f;
    private float _lightElevation = 45f;
    private float _lightIntensity = 1f;

    /// <summary>Horizontal rotation of the key light around the Z axis, in degrees.</summary>
    public float LightAzimuth
    {
        get => _lightAzimuth;
        set => SetField(ref _lightAzimuth, value);
    }

    /// <summary>Vertical angle of the key light above the XY plane, in degrees.</summary>
    public float LightElevation
    {
        get => _lightElevation;
        set => SetField(ref _lightElevation, value);
    }

    /// <summary>Directional light multiplier (0 = dark, 1 = default, 2 = bright).</summary>
    public float LightIntensity
    {
        get => _lightIntensity;
        set => SetField(ref _lightIntensity, value);
    }

    // -- Shader mode -----------------------------------------------------------

    private ShaderMode _activeShaderMode = ShaderMode.Standard;

    /// <summary>Active viewport shader/material mode.</summary>
    public ShaderMode ActiveShaderMode
    {
        get => _activeShaderMode;
        set => SetField(ref _activeShaderMode, value);
    }

    /// <summary>Sets <see cref="ActiveShaderMode"/> from a string enum name (e.g. "Clay").</summary>
    public RelayCommand<string> SetShaderModeCommand { get; }

    // -- Gizmo mode (synced to renderer via OnRender) -------------------------

    private GizmoMode _activeGizmoMode;

    internal GizmoMode ActiveGizmoModeInternal
    {
        get => _activeGizmoMode;
        set
        {
            if (_activeGizmoMode == value) return;
            _activeGizmoMode = value;
            OnPropertyChanged(nameof(IsMoveActive));
            OnPropertyChanged(nameof(IsRotateActive));
            OnPropertyChanged(nameof(IsScaleActive));
            NotifyRenderNeeded();
        }
    }

    public bool IsMoveActive   => _activeGizmoMode == GizmoMode.Translate;
    public bool IsRotateActive => _activeGizmoMode == GizmoMode.Rotate;
    public bool IsScaleActive  => _activeGizmoMode == GizmoMode.Scale;

    public RelayCommand GizmoMoveCommand   { get; }
    public RelayCommand GizmoRotateCommand { get; }
    public RelayCommand GizmoScaleCommand  { get; }

    // -- Gizmo visibility toggle -----------------------------------------------

    private bool _gizmoEnabled = true;

    /// <summary>
    /// When true the transform gizmo handles are shown and G/R/S switches mode.
    /// When false the gizmo is hidden and G/R/S starts a keyboard+mouse transform.
    /// </summary>
    public bool GizmoEnabled
    {
        get => _gizmoEnabled;
        set
        {
            if (SetField(ref _gizmoEnabled, value))
                NotifyRenderNeeded();
        }
    }

    public RelayCommand GizmoToggleCommand { get; }

    // -- Selection / focus overlay ---------------------------------------------

    private bool _hasSelection;

    /// <summary>True when an object is selected in the viewport (shows the focus overlay).</summary>
    public bool HasSelection
    {
        get => _hasSelection;
        set
        {
            if (SetField(ref _hasSelection, value))
                SliceCommand?.RaiseCanExecuteChanged();
        }
    }

    private bool _isToolpathSelected;

    /// <summary>True when the active toolpath node is the current selection.</summary>
    public bool IsToolpathSelected
    {
        get => _isToolpathSelected;
        set
        {
            if (SetField(ref _isToolpathSelected, value))
                ExportKrlCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// The <see cref="Toolpath"/> whose scrubber is currently active.
    /// Set by the viewport code-behind in <c>UpdateFocusOverlay</c>; cleared when
    /// the selection changes away from a toolpath node.
    /// </summary>
    internal Toolpath? ActiveScrubToolpath { get; set; }

    /// <summary>
    /// Invoked when the scrubber index changes while a toolpath is selected.
    /// The viewport code-behind subscribes to run IK for the scrubbed position.
    /// Argument is the new move index.
    /// </summary>
    internal Action<int>? OnScrubIkRequested { get; set; }

    private int    _toolpathScrubIndex;
    private int    _toolpathScrubMax;
    private string _toolpathScrubText = "0";
    /// <summary>Guards against the index↔text two-way binding feedback loop.</summary>
    private bool   _scrubSyncing;

    /// <summary>Current scrubber position (move index). Bound to the slider value.</summary>
    public int ToolpathScrubIndex
    {
        get => _toolpathScrubIndex;
        set
        {
            if (SetField(ref _toolpathScrubIndex, value))
            {
                OnPropertyChanged(nameof(ToolpathScrubLabel));
                OnPropertyChanged(nameof(ToolpathScrubThumbOffsetY));
                OnPropertyChanged(nameof(ToolpathScrubFillHeight));
                // Keep the editable text box in sync unless we're already being
                // called from ToolpathScrubText's setter (avoids a re-entry loop).
                if (!_scrubSyncing && _toolpathScrubText != value.ToString())
                {
                    _scrubSyncing = true;
                    ToolpathScrubText = value.ToString();
                    _scrubSyncing = false;
                }
                // Drive IK when the user is actively scrubbing a toolpath.
                if (_isToolpathSelected)
                    OnScrubIkRequested?.Invoke(value);
            }
        }
    }

    /// <summary>Total number of moves in the selected toolpath. Sets the slider maximum.</summary>
    public int ToolpathScrubMax
    {
        get => _toolpathScrubMax;
        set
        {
            if (SetField(ref _toolpathScrubMax, value))
            {
                OnPropertyChanged(nameof(ToolpathScrubLabel));
                OnPropertyChanged(nameof(ToolpathScrubMaxLabel));
                OnPropertyChanged(nameof(ToolpathScrubThumbOffsetY));
                OnPropertyChanged(nameof(ToolpathScrubFillHeight));
            }
        }
    }

    /// <summary>
    /// The editable move index. Typing a number and committing (Enter / focus loss)
    /// jumps the slider to that position. Updated automatically when the slider moves.
    /// </summary>
    public string ToolpathScrubText
    {
        get => _toolpathScrubText;
        set
        {
            if (!SetField(ref _toolpathScrubText, value)) return;
            // When this setter fires from the TextBox (not from the slider sync above),
            // parse and clamp the value to drive the slider.
            if (!_scrubSyncing && int.TryParse(value, out var n))
            {
                _scrubSyncing = true;
                ToolpathScrubIndex = Math.Clamp(n, 0, _toolpathScrubMax);
                _scrubSyncing = false;
            }
        }
    }

    /// <summary>Human-readable position label shown beside the scrubber slider.</summary>
    public string ToolpathScrubLabel
        => _toolpathScrubMax > 0 ? $"Move {_toolpathScrubIndex} / {_toolpathScrubMax}" : string.Empty;

    /// <summary>The static " / N" suffix shown to the right of the editable index box.</summary>
    public string ToolpathScrubMaxLabel
        => _toolpathScrubMax > 0 ? $" / {_toolpathScrubMax}" : string.Empty;

    /// <summary>
    /// Resets the scrubber to position 0 and records the active toolpath without
    /// firing <see cref="OnScrubIkRequested"/>. Use this for programmatic selection
    /// changes so the robot is not driven automatically when a new toolpath is picked.
    /// </summary>
    internal void ResetScrubIndex(int max, Toolpath? toolpath)
    {
        ActiveScrubToolpath = toolpath;

        _toolpathScrubMax   = max;
        OnPropertyChanged(nameof(ToolpathScrubMax));
        OnPropertyChanged(nameof(ToolpathScrubMaxLabel));

        _toolpathScrubIndex = 0;
        _toolpathScrubText  = "0";
        OnPropertyChanged(nameof(ToolpathScrubIndex));
        OnPropertyChanged(nameof(ToolpathScrubText));
        OnPropertyChanged(nameof(ToolpathScrubLabel));
        OnPropertyChanged(nameof(ToolpathScrubThumbOffsetY));
        OnPropertyChanged(nameof(ToolpathScrubFillHeight));
    }

    // Slider geometry constants (must match the Slider Height / thumb size in the AXAML).
    private const double ScrubSliderHeight     = 480.0; // Slider control Height
    private const double ScrubThumbSize        = 20.0;  // Avalonia 12 SimpleTheme thumb MinHeight
    private const double ScrubBorderPadding    = 4.0;   // Border Padding top/bottom
    private const double ScrubLabelHalfHeight  = 7.0;   // ~half of a 14px label row

    /// <summary>
    /// Height in pixels of the accent-coloured fill rectangle that grows from the
    /// bottom of the slider as the index increases.
    /// </summary>
    public double ToolpathScrubFillHeight =>
        _toolpathScrubMax > 0
            ? (double)_toolpathScrubIndex / _toolpathScrubMax * (ScrubSliderHeight - ScrubThumbSize)
            : 0.0;

    /// <summary>
    /// Pixel offset from the top of the slider Border to the top of the floating label,
    /// computed so the label centre tracks the slider thumb centre.
    /// </summary>
    public double ToolpathScrubThumbOffsetY
    {
        get
        {
            // Thumb centre measured from the top of the Canvas (= top of the slider Border).
            double trackLength  = ScrubSliderHeight - ScrubThumbSize;
            double normalised   = _toolpathScrubMax > 0
                ? (double)_toolpathScrubIndex / _toolpathScrubMax
                : 0.0;
            double thumbCentre  = ScrubBorderPadding + ScrubThumbSize / 2.0
                                + (1.0 - normalised) * trackLength;
            return thumbCentre - ScrubLabelHalfHeight;
        }
    }

    public RelayCommand FocusCommand       { get; }
    public RelayCommand DropToPlateCommand { get; }

    /// <summary>Callback set by the viewport code-behind to perform focus-on-selection.</summary>
    internal Action? OnFocusRequested      { get; set; }
    /// <summary>Callback set by the viewport code-behind to drop the selection to the bed.</summary>
    internal Action? OnDropToPlateRequested { get; set; }

    public ViewportViewModel()
    {
        SetShaderModeCommand = new RelayCommand<string>(name =>
        {
            if (Enum.TryParse<ShaderMode>(name, out var mode))
                ActiveShaderMode = mode;
        });
        SetDefaultBackdropCommand = new RelayCommand(() => OnSetDefaultBackdrop?.Invoke(ActiveBackdropPath));
        LayFlatCommand     = new RelayCommand(() => IsLayFlatMode = !IsLayFlatMode);
        FocusCommand       = new RelayCommand(() => OnFocusRequested?.Invoke());
        DropToPlateCommand = new RelayCommand(() => OnDropToPlateRequested?.Invoke());
        GizmoMoveCommand   = new RelayCommand(() => ActiveGizmoModeInternal = _activeGizmoMode == GizmoMode.Translate ? GizmoMode.None : GizmoMode.Translate);
        GizmoRotateCommand = new RelayCommand(() => ActiveGizmoModeInternal = _activeGizmoMode == GizmoMode.Rotate    ? GizmoMode.None : GizmoMode.Rotate);
        GizmoScaleCommand  = new RelayCommand(() => ActiveGizmoModeInternal = _activeGizmoMode == GizmoMode.Scale     ? GizmoMode.None : GizmoMode.Scale);
        GizmoToggleCommand = new RelayCommand(() => GizmoEnabled = !GizmoEnabled);
        SliceCommand = new RelayCommand(
            execute:    () => _ = OnSliceRequested?.Invoke(),
            canExecute: () => !IsSlicing && HasSelection);

        ExportKrlCommand = new RelayCommand(
            execute:    () => _ = OnExportKrlRequested?.Invoke(),
            canExecute: () => IsToolpathSelected && ActiveScrubToolpath is not null);

        var options = new List<BackdropOption> { new("None", null) };
        if (Directory.Exists("assets/Images"))
        {
            options.AddRange(
                Directory.EnumerateFiles("assets/Images", "*.hdr", SearchOption.AllDirectories)
                    .Order()
                    .Select(f => new BackdropOption(Path.GetFileNameWithoutExtension(f), f)));
        }
        AvailableBackdrops = options;
        _activeBackdrop    = options[0];
    }

    // -- Lay Flat --------------------------------------------------------------

    private bool _isLayFlatMode;

    /// <summary>
    /// When <c>true</c> the viewport is waiting for the user to click a face;
    /// the clicked face will be aligned to the build plate.
    /// </summary>
    public bool IsLayFlatMode
    {
        get => _isLayFlatMode;
        set => SetField(ref _isLayFlatMode, value);
    }

    /// <summary>Toggles <see cref="IsLayFlatMode"/> to begin or cancel face-pick mode.</summary>
    public RelayCommand LayFlatCommand { get; }

    // -- Slicing ---------------------------------------------------------------

    private bool _isSlicing;

    /// <summary>True while a slice operation is running (disables the slice button).</summary>
    public bool IsSlicing
    {
        get => _isSlicing;
        set
        {
            if (SetField(ref _isSlicing, value))
                SliceCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Completed toolpaths queued for upload on the GL thread.
    /// Produced by the slice task; consumed by the render loop.
    /// Each entry is a freshly-created SceneNode -- never re-uses an existing node.
    /// </summary>
    public ConcurrentQueue<(Toolpath Toolpath, SceneNode Node, float BeadWidth, float LayerHeight, System.Numerics.Vector3 MaterialColor)> PendingToolpath { get; } = new();

    /// <summary>
    /// Reference to the additive settings ViewModel. Set by <c>MainWindowViewModel</c>
    /// so the slice command can read current parameters.
    /// </summary>
    public AdditiveSettingsViewModel? AdditiveSettings { get; set; }

    // -- Toolpath stats --------------------------------------------------------

    private bool _hasToolpathStats;
    public bool HasToolpathStats
    {
        get => _hasToolpathStats;
        set => SetField(ref _hasToolpathStats, value);
    }

    private string _statsTime = "";
    public string StatsTime
    {
        get => _statsTime;
        set => SetField(ref _statsTime, value);
    }

    private string _statsWeight = "";
    public string StatsWeight
    {
        get => _statsWeight;
        set => SetField(ref _statsWeight, value);
    }

    private string _statsCost = "";
    public string StatsCost
    {
        get => _statsCost;
        set => SetField(ref _statsCost, value);
    }

    /// <summary>
    /// Callback registered by the viewport code-behind to perform the actual slice
    /// computation on a background thread.
    /// </summary>
    internal Func<Task>? OnSliceRequested { get; set; }

    /// <summary>Callback registered by the viewport code-behind to run the save-file dialog and write the KRL file.</summary>
    internal Func<Task>? OnExportKrlRequested { get; set; }

    /// <summary>Triggers a planar slice using the current additive settings.</summary>
    public RelayCommand SliceCommand { get; }

    /// <summary>Opens a save dialog and exports the selected toolpath as a KUKA KRL .src file.</summary>
    public RelayCommand ExportKrlCommand { get; }

    // -- Outliner / user scene objects -----------------------------------------

    /// <summary>User-imported scene objects shown in the outliner panel.</summary>
    public ObservableCollection<OutlinerItemViewModel> OutlinerItems { get; } = [];

    /// <summary>Nodes queued for GL-thread removal and GPU resource disposal.</summary>
    public ConcurrentQueue<SceneNode> PendingRemoveNodes { get; } = new();

    /// <summary>
    /// Enqueues <paramref name="node"/> for GPU upload and registers it in the outliner.
    /// Must be called on the UI thread.
    /// </summary>
    public void AddUserNode(SceneNode node)
    {
        PendingNodes.Enqueue(node);
        OutlinerItems.Add(new OutlinerItemViewModel(node, NotifyRenderNeeded, RemoveUserNode));
        SliceCommand.RaiseCanExecuteChanged();
        NotifyRenderNeeded();
    }

    /// <summary>
    /// Creates a new toolpath outliner item as a child of <paramref name="parentItem"/>
    /// (or top-level if <c>null</c>), and enqueues its node for GL upload.
    /// Must be called on the UI thread.
    /// </summary>
    internal void RegisterToolpathInOutliner(SceneNode toolpathNode, OutlinerItemViewModel? parentItem)
    {
        var item = new OutlinerItemViewModel(toolpathNode, NotifyRenderNeeded, child =>
        {
            parentItem?.RemoveChild(child);
            if (parentItem is null) OutlinerItems.Remove(child);
            PendingRemoveNodes.Enqueue(child.Node);
            NotifyRenderNeeded();
        });

        if (parentItem is not null)
            parentItem.AddChild(item);
        else
            OutlinerItems.Add(item);
    }

    /// <summary>
    /// Finds the outliner item whose subtree contains <paramref name="node"/>,
    /// removes it from the outliner, and queues the root for GL disposal.
    /// Must be called on the UI thread.
    /// </summary>
    public void RequestDeleteNode(SceneNode node)
    {
        foreach (var item in OutlinerItems)
        {
            // Check direct match (top-level mesh or its sub-nodes)
            if (item.Node == node || item.Node.SelfAndDescendants().Any(n => n == node))
            {
                RemoveUserNode(item);
                return;
            }
            // Check child toolpath nodes
            var child = item.Children.FirstOrDefault(c => c.Node == node);
            if (child is not null)
            {
                item.RemoveChild(child);
                PendingRemoveNodes.Enqueue(child.Node);
                NotifyRenderNeeded();
                return;
            }
        }
    }

    private void RemoveUserNode(OutlinerItemViewModel item)
    {
        OutlinerItems.Remove(item);
        // Queue child toolpath nodes for cleanup before the parent
        foreach (var child in item.Children)
            PendingRemoveNodes.Enqueue(child.Node);
        PendingRemoveNodes.Enqueue(item.Node);
        SliceCommand.RaiseCanExecuteChanged();
        NotifyRenderNeeded();
    }
}
