using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Avalonia.Threading;
using MassiveSlicer.App;
using MassiveSlicer.App.Enums;
using MassiveSlicer.App.Undo;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Scanning;
using MassiveSlicer.Viewport;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.Viewport.Scene.Modifiers;
using MassiveSlicer.ViewModels.Base;
using OpenTK.Mathematics;
using ToolSwapRequest = (MassiveSlicer.Core.Models.ToolCellConfig Config, MassiveSlicer.Viewport.Scene.SceneNode Node);
using Toolpath = MassiveSlicer.Core.Models.Toolpath;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Manages the state of the 3D viewport: selection mode, active transform tool,
/// and overlay visibility flags. The actual OpenGL rendering lives in
/// <c>MassiveSlicer.Viewport</c>; this ViewModel only holds bindable state.
/// </summary>
public sealed partial class ViewportViewModel : ViewModelBase
{
    private SelectionMode _selectionMode = SelectionMode.Object;

    /// <summary>The active component selection mode (vertex/edge/face/object).</summary>
    public SelectionMode SelectionMode
    {
        get => _selectionMode;
        set => SetField(ref _selectionMode, value);
    }

    // ── Mill OPERATION → SELECT AREA (mesh-face region on the workpiece) ──────

    private MillAreaSelectTool _millAreaSelectTool = MillAreaSelectTool.WholeModel;

    /// <summary>
    /// Active mill area tool from the Mill sidebar. When not
    /// <see cref="MillAreaSelectTool.WholeModel"/>, viewport input paints faces
    /// exclusively on user workpieces (imports/scans) — never robot / bed / cell.
    /// </summary>
    public MillAreaSelectTool MillAreaSelectTool
    {
        get => _millAreaSelectTool;
        set
        {
            if (!SetField(ref _millAreaSelectTool, value)) return;
            OnPropertyChanged(nameof(IsMillAreaSelectActive));
            OnPropertyChanged(nameof(IsMillAreaBrush));
            OnPropertyChanged(nameof(IsMillAreaBox));
            OnPropertyChanged(nameof(IsMillAreaLasso));
            OnPropertyChanged(nameof(IsMillAreaFace));
            OnPropertyChanged(nameof(ShowMillBrushToolbar));
            NotifyRenderNeeded();
        }
    }

    /// <summary>True while a face-region tool is armed (Face / Box / Lasso / Brush).</summary>
    public bool IsMillAreaSelectActive =>
        MillAreaSelectTool is MillAreaSelectTool.Face
            or MillAreaSelectTool.Box
            or MillAreaSelectTool.Lasso
            or MillAreaSelectTool.Brush;

    public bool IsMillAreaBrush => MillAreaSelectTool == MillAreaSelectTool.Brush;
    public bool IsMillAreaBox   => MillAreaSelectTool == MillAreaSelectTool.Box;
    public bool IsMillAreaLasso => MillAreaSelectTool == MillAreaSelectTool.Lasso;
    public bool IsMillAreaFace  => MillAreaSelectTool == MillAreaSelectTool.Face;

    private double _millBrushRadiusMm = 25.0;

    /// <summary>Soft-brush radius in world millimetres. Edit via right-click brush menu.</summary>
    public double MillBrushRadiusMm
    {
        get => _millBrushRadiusMm;
        set => SetField(ref _millBrushRadiusMm, Math.Clamp(value, 2.0, 400.0));
    }

    private double _millBrushFalloff = 0.65;

    /// <summary>Brush edge falloff 0 = hard, 1 = soft gaussian. Right-click menu.</summary>
    public double MillBrushFalloff
    {
        get => _millBrushFalloff;
        set => SetField(ref _millBrushFalloff, Math.Clamp(value, 0.0, 1.0));
    }

    /// <summary>
    /// Bottom-center brush toolbar while SELECT AREA → Brush is armed.
    /// Fixed above the timeline (~250px from bottom).
    /// </summary>
    public bool ShowMillBrushToolbar => IsMillAreaBrush;

    /// <summary>
    /// Workpiece root locked once the first stroke is painted (one model per operation).
    /// Null until first hit or when cleared.
    /// </summary>
    public SceneNode? MillAreaTargetRoot { get; private set; }

    private int _millPaintedVertices;
    private float _millPaintCoverage;

    /// <summary>Vertices with selection weight (soft brush coverage proxy).</summary>
    public int MillPaintedVertices
    {
        get => _millPaintedVertices;
        private set
        {
            if (!SetField(ref _millPaintedVertices, value)) return;
            OnPropertyChanged(nameof(MillAreaStatusText));
        }
    }

    /// <summary>Alias for older bindings / status.</summary>
    public int MillPaintedTexels => MillPaintedVertices;

    /// <summary>Fraction of vertices painted (0..1).</summary>
    public float MillPaintCoverage
    {
        get => _millPaintCoverage;
        private set => SetField(ref _millPaintCoverage, value);
    }

    /// <summary>Status line for the SELECT AREA card.</summary>
    public string MillAreaStatusText
    {
        get
        {
            if (MillAreaSelectTool == MillAreaSelectTool.WholeModel)
                return "Whole model";
            string tool = MillAreaSelectTool switch
            {
                MillAreaSelectTool.Face  => "Face",
                MillAreaSelectTool.Box   => "Box",
                MillAreaSelectTool.Lasso => "Lasso",
                MillAreaSelectTool.Brush => "Brush",
                _ => "Area",
            };
            string target = MillAreaTargetRoot?.Name is { Length: > 0 } n
                ? $" on \"{n}\""
                : " — paint the milling model only";
            if (MillPaintedVertices <= 0)
                return $"{tool}: soft brush{target} · Alt erases · size/falloff on bottom bar";
            return $"{tool}: {MillPaintCoverage * 100f:0.#}% ({MillPaintedVertices:N0} verts){target}";
        }
    }

    /// <summary>Raised when mill surface paint changes.</summary>
    internal Action? OnMillAreaSelectionChanged { get; set; }

    /// <summary>Viewport owns the paint masks; calls this to clear GL resources.</summary>
    internal Action? ClearMillSurfacePaint { get; set; }

    /// <summary>Optional host log sink (app console).</summary>
    internal Action<string>? LogMill { get; set; }

    /// <summary>Describe paint layers for console/MCP diagnostics.</summary>
    internal Func<string>? DescribeMillPaint { get; set; }

    internal void SetMillAreaTargetRoot(SceneNode? root)
    {
        if (MillAreaTargetRoot == root) return;
        MillAreaTargetRoot = root;
        OnPropertyChanged(nameof(MillAreaStatusText));
    }

    internal void UpdateMillPaintStats(int paintedVertices, float coverage01)
    {
        MillPaintCoverage = Math.Clamp(coverage01, 0f, 1f);
        MillPaintedVertices = paintedVertices;
        OnPropertyChanged(nameof(MillAreaStatusText));
        OnMillAreaSelectionChanged?.Invoke();
        NotifyRenderNeeded();
    }

    /// <summary>Clears surface paint and unlocks the target model.</summary>
    public void ClearMillAreaSelection()
    {
        ClearMillSurfacePaint?.Invoke();
        MillAreaTargetRoot = null;
        MillPaintCoverage = 0;
        MillPaintedVertices = 0;
        OnPropertyChanged(nameof(MillAreaStatusText));
        OnMillAreaSelectionChanged?.Invoke();
        NotifyRenderNeeded();
    }

    /// <summary>
    /// True when <paramref name="node"/> (or its selectable root) is a user workpiece
    /// mesh suitable for milling area selection — imports and scans, never robot/bed/cell
    /// infrastructure, toolpaths, effectors, or modifiers.
    /// </summary>
    internal bool IsMillableWorkpiece(SceneNode? node)
    {
        if (node is null) return false;
        if (IsEffectorNode(node) || IsModifierNode(node) || IsModifiersGroupNode(node))
            return false;

        var item = FindUserMeshOutlinerItem(node);
        if (item is null) return false;
        if (item.IsToolpath || item.IsEffector || item.IsModifier || item.IsModifiersGroup)
            return false;

        // Lock to first painted model (same outliner item or under that root).
        if (MillAreaTargetRoot is { } locked)
        {
            for (var c = node; c is not null; c = c.Parent)
                if (c == locked) return true;
            var lockedItem = FindUserMeshOutlinerItem(locked);
            if (lockedItem is not null && ReferenceEquals(lockedItem, item))
                return true;
            return false;
        }

        return true;
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

    private bool _showAxes = false;

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

    private bool _cavityShadeToolpaths = true;

    /// <summary>Include toolpaths (lines + bead) in cavity shading.</summary>
    public bool CavityShadeToolpaths
    {
        get => _cavityShadeToolpaths;
        set { if (SetField(ref _cavityShadeToolpaths, value)) NotifyRenderNeeded(); }
    }

    private bool _cavityShadeImportedMeshes = true;

    /// <summary>Include user-imported meshes in cavity shading (cell geometry always shades).</summary>
    public bool CavityShadeImportedMeshes
    {
        get => _cavityShadeImportedMeshes;
        set { if (SetField(ref _cavityShadeImportedMeshes, value)) NotifyRenderNeeded(); }
    }

    private bool _showTcpFrame = true;

    /// <summary>Whether the TCP X/Y/Z orientation axes are visible.</summary>
    public bool ShowTcpFrame
    {
        get => _showTcpFrame;
        set { if (SetField(ref _showTcpFrame, value)) NotifyRenderNeeded(); }
    }

    private bool _showContactShadows = true;

    /// <summary>Soft ground-contact shadows beneath robot, rail, and print bed.</summary>
    public bool ShowContactShadows
    {
        get => _showContactShadows;
        set
        {
            if (SetField(ref _showContactShadows, value))
                NotifyRenderNeeded();
        }
    }

    private float _contactShadowSize = 1f;

    /// <summary>Contact shadow spread. 1 = default; higher = larger/softer footprint.</summary>
    public float ContactShadowSize
    {
        get => _contactShadowSize;
        set
        {
            if (SetField(ref _contactShadowSize, Math.Clamp(value, 0.25f, 3f)))
                NotifyRenderNeeded();
        }
    }

    private float _contactShadowDarkness = 1f;

    /// <summary>Contact shadow strength. 0 = off; 1 = default; higher = darker.</summary>
    public float ContactShadowDarkness
    {
        get => _contactShadowDarkness;
        set
        {
            if (SetField(ref _contactShadowDarkness, Math.Clamp(value, 0f, 2f)))
                NotifyRenderNeeded();
        }
    }

    private float _contactShadowBlur = 1f;

    /// <summary>Contact shadow edge softness. 0 = sharp; 1 = default; higher = softer.</summary>
    public float ContactShadowBlur
    {
        get => _contactShadowBlur;
        set
        {
            if (SetField(ref _contactShadowBlur, Math.Clamp(value, 0f, 3f)))
                NotifyRenderNeeded();
        }
    }

    private bool _cavityEnabled;

    /// <summary>Blender-style ridge/valley cavity accentuation on viewport shading.</summary>
    public bool CavityEnabled
    {
        get => _cavityEnabled;
        set
        {
            if (SetField(ref _cavityEnabled, value))
                NotifyRenderNeeded();
        }
    }

    private CavityMode _cavityMode = CavityMode.Both;

    public CavityMode CavityMode
    {
        get => _cavityMode;
        set
        {
            if (!SetField(ref _cavityMode, value)) return;
            _cavityModeOption = value.ToString();
            OnPropertyChanged(nameof(CavityModeOption));
            NotifyRenderNeeded();
        }
    }

    public IReadOnlyList<string> CavityModeOptions { get; } = ["Screen", "World", "Both"];

    private string _cavityModeOption = "Both";

    public string CavityModeOption
    {
        get => _cavityModeOption;
        set
        {
            if (!SetField(ref _cavityModeOption, value)) return;
            if (Enum.TryParse<CavityMode>(value, out var mode))
                CavityMode = mode;
            else
                OnPropertyChanged(nameof(CavityModeOption));
        }
    }

    private float _cavityScreenRidge = 1f;
    public float CavityScreenRidge
    {
        get => _cavityScreenRidge;
        set
        {
            if (SetField(ref _cavityScreenRidge, Math.Clamp(value, 0f, 2f)))
                NotifyRenderNeeded();
        }
    }

    private float _cavityScreenValley = 1f;
    public float CavityScreenValley
    {
        get => _cavityScreenValley;
        set
        {
            if (SetField(ref _cavityScreenValley, Math.Clamp(value, 0f, 2f)))
                NotifyRenderNeeded();
        }
    }

    private float _cavityWorldRidge = 1f;
    public float CavityWorldRidge
    {
        get => _cavityWorldRidge;
        set
        {
            if (SetField(ref _cavityWorldRidge, Math.Clamp(value, 0f, 2f)))
                NotifyRenderNeeded();
        }
    }

    private float _cavityWorldValley = 1f;
    public float CavityWorldValley
    {
        get => _cavityWorldValley;
        set
        {
            if (SetField(ref _cavityWorldValley, Math.Clamp(value, 0f, 2f)))
                NotifyRenderNeeded();
        }
    }

    private float _cavityWorldDistance = 5f;
    public float CavityWorldDistance
    {
        get => _cavityWorldDistance;
        set
        {
            if (SetField(ref _cavityWorldDistance, Math.Clamp(value, 0.5f, 50f)))
                NotifyRenderNeeded();
        }
    }

    private bool _showBead = false;
    public bool ShowBead
    {
        get => _showBead;
        set => SetField(ref _showBead, value);
    }

    private bool _showBeadOverhang = false;
    public bool ShowBeadOverhang
    {
        get => _showBeadOverhang;
        set
        {
            if (!SetField(ref _showBeadOverhang, value)) return;
            if (value && _showOrientationPreview) { _showOrientationPreview = false; OnPropertyChanged(nameof(ShowOrientationPreview)); }
        }
    }

    private bool _showOrientationPreview = false;
    public bool ShowOrientationPreview
    {
        get => _showOrientationPreview;
        set
        {
            if (!SetField(ref _showOrientationPreview, value)) return;
            if (value && _showBeadOverhang) { _showBeadOverhang = false; OnPropertyChanged(nameof(ShowBeadOverhang)); }
        }
    }

    private bool _showRpmOverLimit;
    /// <summary>
    /// Highlight extrusion moves whose exported RPM exceeds
    /// <see cref="MassiveSlicer.Core.IO.ToolpathRpm.MaxRpmPercent"/>. Per-view setting
    /// (on by default in the RPM view); those moves also block export.
    /// </summary>
    public bool ShowRpmOverLimit
    {
        get => _showRpmOverLimit;
        set { if (SetField(ref _showRpmOverLimit, value)) NotifyRenderNeeded(); }
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

    private bool _showWipeMoves = true;
    /// <summary>Wipe extrusion segments (pre-travel filament wipes) as their own display layer.</summary>
    public bool ShowWipeMoves
    {
        get => _showWipeMoves;
        set => SetField(ref _showWipeMoves, value);
    }

    private bool _showLightningMoves = true;
    /// <summary>Lightning Bridge finger segments (orange display layer).</summary>
    public bool ShowLightningMoves
    {
        get => _showLightningMoves;
        set => SetField(ref _showLightningMoves, value);
    }

    private bool _showSeam = true;
    public bool ShowSeam
    {
        get => _showSeam;
        set => SetField(ref _showSeam, value);
    }

    private bool _showBackFaces = true;
    /// <summary>Whether the active model's inside faces render (vs. being culled away) — on by
    /// default so a Cut cross-section is visible from either side. See
    /// <see cref="SyncBackFaceFlags"/> for how this reaches each mesh node's own CullFaces.</summary>
    public bool ShowBackFaces
    {
        get => _showBackFaces;
        set
        {
            if (!SetField(ref _showBackFaces, value)) return;
            // Applied here directly (not just left to the per-frame poll loop that also handles
            // the active-model-changed case) so toggling the checkbox takes effect immediately —
            // that poll only runs during an already-scheduled frame, so without this it silently
            // waited for the next unrelated redraw (e.g. orbiting the camera) to catch up.
            SyncBackFaceFlags(value);
            NotifyRenderNeeded();
        }
    }

    /// <summary>Applies <see cref="ShowBackFaces"/> to every mesh node under the active print
    /// object — CullFaces lives per-node (not inherited from an ancestor), so this has to walk
    /// the whole subtree rather than set one flag at the root.</summary>
    internal void SyncBackFaceFlags(bool showBackFaces)
    {
        if (ResolveActivePrintObjectItem()?.Node is not { } target) return;
        foreach (var n in target.SelfAndDescendants())
            if (n.Mesh is not null || n.PendingMesh is not null)
                n.CullFaces = !showBackFaces;
    }

    private bool _showDimensions;

    /// <summary>Whether the bounding-box dimension overlay is visible.</summary>
    public bool ShowDimensions
    {
        get => _showDimensions;
        set => SetField(ref _showDimensions, value);
    }

    // -- Toolpath colors -------------------------------------------------------

    private System.Numerics.Vector3 _toolpathExtrudeColor     = new(1f, 1f, 1f);
    private System.Numerics.Vector3 _toolpathTravelColor      = new(0.85f, 0.18f, 0.18f);
    private System.Numerics.Vector3 _toolpathWipeColor        = new(1.0f,  0.53f, 0.0f);
    private System.Numerics.Vector3 _toolpathRetractionColor  = new(0.61f, 0.15f, 0.69f);
    private System.Numerics.Vector3 _toolpathSeamColor        = new(1.0f,  0.9f,  0.0f);
    private System.Numerics.Vector3 _toolpathUnselectedColor  = new(0.38f, 0.38f, 0.38f);

    public System.Numerics.Vector3 ToolpathExtrudeColor
    {
        get => _toolpathExtrudeColor;
        set => SetField(ref _toolpathExtrudeColor, value);
    }

    private float _toolpathLineOpacity = 1f;

    /// <summary>Opacity of the toolpath extrusion/travel lines (per-view profile setting).</summary>
    public float ToolpathLineOpacity
    {
        get => _toolpathLineOpacity;
        set { if (SetField(ref _toolpathLineOpacity, Math.Clamp(value, 0f, 1f))) NotifyRenderNeeded(); }
    }

    // Live bead colour — applied every frame as a shader uniform, so changing it
    // recolours already-sliced beads instantly (no re-slice, no VBO rebuild).
    private System.Numerics.Vector3 _beadColor = new(0.655f, 0.906f, 0.05f);   // lime, matches Blender "3dp.001"

    public System.Numerics.Vector3 BeadColor
    {
        get => _beadColor;
        set { if (SetField(ref _beadColor, value)) OnPropertyChanged(nameof(BeadPickerColor)); }
    }

    /// <summary>Avalonia-Color bridge for the ColorPicker control in the Toolpath panel.</summary>
    public Avalonia.Media.Color BeadPickerColor
    {
        get => Avalonia.Media.Color.FromRgb(
            (byte)Math.Clamp(_beadColor.X * 255f, 0f, 255f),
            (byte)Math.Clamp(_beadColor.Y * 255f, 0f, 255f),
            (byte)Math.Clamp(_beadColor.Z * 255f, 0f, 255f));
        set => BeadColor = new System.Numerics.Vector3(value.R / 255f, value.G / 255f, value.B / 255f);
    }

    public System.Numerics.Vector3 ToolpathTravelColor
    {
        get => _toolpathTravelColor;
        set => SetField(ref _toolpathTravelColor, value);
    }

    public System.Numerics.Vector3 ToolpathWipeColor
    {
        get => _toolpathWipeColor;
        set => SetField(ref _toolpathWipeColor, value);
    }

    public System.Numerics.Vector3 ToolpathRetractionColor
    {
        get => _toolpathRetractionColor;
        set => SetField(ref _toolpathRetractionColor, value);
    }

    public System.Numerics.Vector3 ToolpathSeamColor
    {
        get => _toolpathSeamColor;
        set => SetField(ref _toolpathSeamColor, value);
    }

    public System.Numerics.Vector3 ToolpathUnselectedColor
    {
        get => _toolpathUnselectedColor;
        set => SetField(ref _toolpathUnselectedColor, value);
    }

    private NavigationPresetId _activePreset = NavigationPresetId.Rhino;

    /// <summary>Active mouse-button navigation preset -- controls which buttons perform orbit/pan.</summary>
    public NavigationPresetId ActivePreset
    {
        get => _activePreset;
        set => SetField(ref _activePreset, value);
    }

    private float _touchpadPanSpeed = 9f;
    private float _touchpadOrbitSpeed = 2f;
    private float _touchpadZoomSpeed = 1f;
    private bool  _touchpadInvertPan;

    /// <summary>Two-finger pan speed (Touchpad preset). Mirrors the preference; applied in OnPointerWheelChanged.</summary>
    public float TouchpadPanSpeed
    {
        get => _touchpadPanSpeed;
        set => SetField(ref _touchpadPanSpeed, value);
    }

    /// <summary>Cmd + two-finger rotate speed (Touchpad preset).</summary>
    public float TouchpadOrbitSpeed
    {
        get => _touchpadOrbitSpeed;
        set => SetField(ref _touchpadOrbitSpeed, value);
    }

    /// <summary>Shift + two-finger zoom speed (Touchpad preset).</summary>
    public float TouchpadZoomSpeed
    {
        get => _touchpadZoomSpeed;
        set => SetField(ref _touchpadZoomSpeed, value);
    }

    /// <summary>When true, two-finger pan direction is inverted ("game style").</summary>
    public bool TouchpadInvertPan
    {
        get => _touchpadInvertPan;
        set => SetField(ref _touchpadInvertPan, value);
    }

    /// <summary>Plasticity live-bridge state, surfaced as a collapsible section in the N-key HUD.</summary>
    public PlasticityViewModel Plasticity { get; } = new();

    /// <summary>MassiveBRAIN sync server (Blender/Rhino push bridge), below Plasticity in the N-key HUD.</summary>
    public MassiveBrainViewModel MassiveBrain { get; } = new();

    /// <summary>ERP project-attachment dock (bottom-left of the viewport).</summary>
    public ErpViewModel Erp { get; } = new();

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

    /// <summary>Generation of the last cell swap applied to the GL scene.</summary>
    internal int AcceptedCellSwapGeneration { get; set; }

    /// <summary>When set, the next <see cref="PendingCellSwap"/> enqueue uses this generation.</summary>
    internal int? WorkspaceCellLoadGeneration { get; set; }

    /// <summary>
    /// Reference to the robot panel ViewModel. Set by <c>MainWindowViewModel</c>
    /// at startup so the viewport render loop can read joint angles for FK.
    /// </summary>
    public RobotPanelViewModel? Robot { get; set; }

    /// <summary>
    /// The active cell configuration. Set at startup after loading the cell JSON.
    /// The viewport render loop applies bed boundary settings on the GL thread.
    /// </summary>
    public CellConfig? ActiveCell
    {
        get => _activeCell;
        set
        {
            _activeCell = value;
            if (value is not null)
                RobotSmb.SetActiveCell(value.Name, value.BridgeIp);
            RebuildSendTargets();
        }
    }
    private CellConfig? _activeCell;

    /// <summary>Per-cell SMB credentials for direct Export-to-Robot uploads.</summary>
    public RobotSmbViewModel RobotSmb { get; } = new();

    // -- Send destination (Robot SMB vs MassiveDRIVE) -------------------------

    /// <summary>Where the top-bar Send action delivers the active toolpath.</summary>
    public ObservableCollection<SendTargetOption> SendTargets { get; } = [];

    private SendTargetOption? _selectedSendTarget;

    /// <summary>Selected destination for <see cref="SendToRobotCommand"/>.</summary>
    public SendTargetOption? SelectedSendTarget
    {
        get => _selectedSendTarget;
        set
        {
            if (!SetField(ref _selectedSendTarget, value)) return;
            OnPropertyChanged(nameof(SendActionLabel));
            OnPropertyChanged(nameof(SendActionTip));
        }
    }

    /// <summary>Label on the green Send button (updates with destination).</summary>
    public string SendActionLabel =>
        SelectedSendTarget?.Kind == SendTargetKind.MassiveDrive
            ? "Send to Drive"
            : "Export to Robot";

    /// <summary>Tooltip for the green Send button.</summary>
    public string SendActionTip =>
        SelectedSendTarget?.Kind == SendTargetKind.MassiveDrive
            ? "Upload toolpath package to MassiveDRIVE and start path executor (RSI + extruder). No print KRL on the robot."
            : "Send the KRL program to the selected cell's robot (D drive over SMB) and report it to the ERP";

    /// <summary>Rebuild Robot / MassiveDRIVE send destinations from the active cell.</summary>
    public void RebuildSendTargets()
    {
        var prevKind = SelectedSendTarget?.Kind;
        SendTargets.Clear();

        var cell = ActiveCell;
        var robotLabel = cell is null ? "Robot (KRC)" : $"Robot — {cell.Name}";
        SendTargets.Add(new SendTargetOption(
            SendTargetKind.Robot,
            robotLabel,
            Url: null,
            CellId: null));

        if (cell is not null && !string.IsNullOrWhiteSpace(cell.MassiveDriveUrl))
        {
            var driveId = string.IsNullOrWhiteSpace(cell.MassiveDriveCellId)
                ? InferDriveCellId(cell.Name)
                : cell.MassiveDriveCellId!;
            SendTargets.Add(new SendTargetOption(
                SendTargetKind.MassiveDrive,
                $"MassiveDRIVE — {cell.Name}",
                Url: cell.MassiveDriveUrl!.TrimEnd('/'),
                CellId: driveId));
        }

        // Prefer MassiveDRIVE when the cell has it (new architecture); else Robot.
        SendTargetOption? pick = null;
        if (prevKind is not null)
            pick = SendTargets.FirstOrDefault(t => t.Kind == prevKind);
        pick ??= SendTargets.FirstOrDefault(t => t.Kind == SendTargetKind.MassiveDrive)
                 ?? SendTargets.FirstOrDefault();
        SelectedSendTarget = pick;
    }

    static string InferDriveCellId(string cellName)
    {
        var n = cellName.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (n.Contains("lfam1", StringComparison.Ordinal)) return "lfam1";
        if (n.Contains("lfam2", StringComparison.Ordinal)) return "lfam2";
        if (n.Contains("lfam3", StringComparison.Ordinal)) return "lfam3";
        if (n.Contains("lfam4", StringComparison.Ordinal)) return "lfam4";
        return n.Length <= 12 ? n : "lfam3";
    }

    /// <summary>Writes the active toolpath's KRL program into the given directory,
    /// named "&lt;geometry&gt; RevNN.src", and returns the written path — or null when
    /// no toolpath is active. Wired by the viewport view.</summary>
    internal Func<string, int, Task<string?>>? ExportKrlToDirectory { get; set; }

    /// <summary>File path of the active cell JSON. Set alongside <see cref="ActiveCell"/>.</summary>
    public string? ActiveCellPath { get; set; }

    // -- N-key HUD (hidden until N; no viewport-edge tab) ----------------------

    private bool _isSyncHudOpen;

    /// <summary>Whether the left-side HUD is visible (toggle with N).</summary>
    public bool IsSyncHudOpen
    {
        get => _isSyncHudOpen;
        set => SetField(ref _isSyncHudOpen, value);
    }

    /// <summary>True when robot sync is active — HUD panels show live data.</summary>
    public bool IsRobotSynced => Robot?.IsConnected == true;

    private string _mountedToolName = "—";
    private int _lfam3WorkflowPhaseIndex;
    private bool _hasPrePrintScanStep;
    private string? _lfam3WorkflowCellName;
    private SceneNode? _armatureScanNode;

    /// <summary>Currently mounted tool on the flange (multi-tool cells).</summary>
    public string MountedToolName
    {
        get => _mountedToolName;
        set
        {
            if (!SetField(ref _mountedToolName, value)) return;
            OnPropertyChanged(nameof(MountedToolLabel));
            OnPropertyChanged(nameof(HasFlangeMountedTool));
            SyncWorkflowPhaseFromMountedTool();
            NotifyWorkflowStateChanged();
            RaiseToolChangeCommandsCanExecuteChanged();
        }
    }

    /// <summary>Human-readable mounted tool for HUD (empty flange → "No tool").</summary>
    public string MountedToolLabel =>
        string.IsNullOrEmpty(MountedToolName) ? "No tool" : MountedToolName;

    /// <summary>True when a toolhead mesh is on the robot flange.</summary>
    public bool HasFlangeMountedTool => !string.IsNullOrEmpty(MountedToolName);

    /// <summary>LFAM 3 workflow timeline (Print → Scan → Mill, optional pre-print scan).</summary>
    public bool ShowLfam3ToolPicker =>
        ActiveCell?.Name.Contains("LFAM 3", StringComparison.OrdinalIgnoreCase) == true
        && !Lfam3MinimalProbe.IsActive(ActiveCell.Name);

    private bool _isLfam3WorkflowExpanded = true;

    /// <summary>When true, the full LFAM 3 phase timeline is shown; false = slim minimized bar.</summary>
    public bool IsLfam3WorkflowExpanded
    {
        get => _isLfam3WorkflowExpanded;
        set
        {
            if (!SetField(ref _isLfam3WorkflowExpanded, value)) return;
            if (!value && LiveIo.IsExpanded)
                LiveIo.IsExpanded = false;
            OnPropertyChanged(nameof(Lfam3WorkflowMinimizeIcon));
            OnPropertyChanged(nameof(Lfam3WorkflowMinimizeTip));
            OnPropertyChanged(nameof(Lfam3WorkflowMargin));
            OnPropertyChanged(nameof(Lfam3WorkflowMaxHeight));
            OnPropertyChanged(nameof(Lfam3LiveIoMaxHeight));
        }
    }

    public string Lfam3WorkflowMinimizeIcon =>
        IsLfam3WorkflowExpanded ? "mdi-chevron-down" : "mdi-chevron-up";

    public string Lfam3WorkflowMinimizeTip =>
        IsLfam3WorkflowExpanded ? "Collapse workflow panel" : "Expand workflow panel upward";

    public string Lfam3WorkflowStatusLabel =>
        IsPrePrintScanStepActive ? "Pre-print scan"
        : IsPrintStepActive       ? "Print"
        : IsVerifyScanStepActive  ? "Verify scan"
        : IsMillStepActive          ? "Mill"
        : "Workflow";

    /// <summary>When true, inserts a scene scan step before print (armatures &amp; fixtures).</summary>
    public bool HasPrePrintScanStep
    {
        get => _hasPrePrintScanStep;
        set
        {
            if (!SetField(ref _hasPrePrintScanStep, value)) return;
            AdjustPhaseIndexForPrePrintScanToggle(value);
            NotifyWorkflowStateChanged();
        }
    }

    public string PrePrintScanToggleIcon  => HasPrePrintScanStep ? "mdi-minus" : "mdi-plus";
    public string PrePrintScanToggleTip   => HasPrePrintScanStep
        ? "Remove pre-print scene scan step"
        : "Add pre-print scene scan before print";

    /// <summary>Active LFAM 3 workflow step index within the visible timeline.</summary>
    public int Lfam3WorkflowPhaseIndex => _lfam3WorkflowPhaseIndex;

    int PrintPhaseIndex  => HasPrePrintScanStep ? 1 : 0;
    int ScanPhaseIndex   => HasPrePrintScanStep ? 2 : 1;
    int MillPhaseIndex   => HasPrePrintScanStep ? 3 : 2;

    public bool IsWorkflowSegment0Complete => _lfam3WorkflowPhaseIndex > 0;
    public bool IsWorkflowSegment1Complete => _lfam3WorkflowPhaseIndex > 1;
    public bool IsWorkflowSegment2Complete => HasPrePrintScanStep && _lfam3WorkflowPhaseIndex > 2;

    public bool IsPrePrintScanStepCompleted => HasPrePrintScanStep && _lfam3WorkflowPhaseIndex > 0;
    public bool IsPrePrintScanStepActive    => HasPrePrintScanStep && _lfam3WorkflowPhaseIndex == 0;
    public bool IsPrePrintScanStepPending   => HasPrePrintScanStep && _lfam3WorkflowPhaseIndex < 0;

    public bool IsPrintStepCompleted => _lfam3WorkflowPhaseIndex > PrintPhaseIndex;
    public bool IsPrintStepActive    => _lfam3WorkflowPhaseIndex == PrintPhaseIndex;
    public bool IsPrintStepPending   => _lfam3WorkflowPhaseIndex < PrintPhaseIndex;

    public bool IsVerifyScanStepCompleted => _lfam3WorkflowPhaseIndex > ScanPhaseIndex;
    public bool IsVerifyScanStepActive    => _lfam3WorkflowPhaseIndex == ScanPhaseIndex;
    public bool IsVerifyScanStepPending   => _lfam3WorkflowPhaseIndex < ScanPhaseIndex;

    public bool IsMillStepCompleted => _lfam3WorkflowPhaseIndex > MillPhaseIndex;
    public bool IsMillStepActive    => _lfam3WorkflowPhaseIndex == MillPhaseIndex;
    public bool IsMillStepPending   => _lfam3WorkflowPhaseIndex < MillPhaseIndex;

    /// <summary>Active phase column shows playback/details after Pick or Deposit is clicked.</summary>
    public bool IsPrePrintScanStepExpanded => IsPrePrintScanStepActive && ScannerToolPanel.ShowPlayback;
    public bool IsPrintStepExpanded        => IsPrintStepActive && ExtruderToolPanel.ShowPlayback;
    public bool IsVerifyScanStepExpanded   => IsVerifyScanStepActive && ScannerToolPanel.ShowPlayback;
    public bool IsMillStepExpanded         => IsMillStepActive && SpindleToolPanel.ShowPlayback;

    // Inactive phase cards stack over the viewport when Live I/O is open — only the active
    // phase column expands; click another phase icon to switch.
    public bool ShowPrePrintScanParamCard => false;
    public bool ShowPrintParamCard        => false;
    public bool ShowVerifyScanParamCard   => false;
    public bool ShowMillParamCard         => false;

    public bool IsExtruderToolActive => IsPrintStepActive;
    public bool IsScannerToolActive  => IsPrePrintScanStepActive || IsVerifyScanStepActive;
    public bool IsSpindleToolActive  => IsMillStepActive;

    /// <summary>LFAM 3 toolpath panel uses phase-specific option groups.</summary>
    public bool IsLfam3ToolpathPhased => ShowLfam3ToolPicker;

    public bool Lfam3ToolpathShowPrintOptions => !ShowLfam3ToolPicker || IsPrintStepActive;

    public bool Lfam3ToolpathShowScanOptions => ShowLfam3ToolPicker && IsScannerToolActive;

    public bool Lfam3ToolpathShowMillOptions => ShowLfam3ToolPicker && IsMillStepActive;

    /// <summary>True when the initial-scan armature/fixture mesh is loaded in the scene.</summary>
    public bool HasArmatureScanMesh => _armatureScanNode is not null;

    public string PrePrintScanParamLine1 => HasArmatureScanMesh
        ? "Scene mesh in scene"
        : "Import point cloud / mesh";

    public string PrePrintScanParamLine2 => ActiveCell?.BedScan is { } scan
        ? $"Capture fixtures · {scan.ScanSteps} rotations"
        : "Capture fixtures & armatures";

    public string PrintParamLine1 => AdditiveSettings is { } a
        ? $"Layer {a.LayerHeight:F1} mm · Bead {a.BeadWidth:F1} mm"
        : "Pellet extrusion";

    public string PrintParamLine2 => HasPrePrintScanStep && !HasArmatureScanMesh
        ? "Requires pre-print scan mesh"
        : HasArmatureScanMesh
            ? (AdditiveSettings?.SelectedPreset?.Name ?? "Print-in armatures if needed")
            : (AdditiveSettings?.SelectedPreset?.Name ?? "Pellet extrusion");

    public string VerifyScanParamLine1 => HasArmatureScanMesh
        ? "Collision check vs armature"
        : "Load armature scan first";

    public string VerifyScanParamLine2 => ActiveCell?.BedScan is { } scan
        ? $"Re-scan before laydown · {scan.ScanSteps} steps"
        : "Re-scan before laydown";

    public string MillParamLine1 => "Subtractive finish";
    public string MillParamLine2 => IsMillStepActive ? "Spindle · mounted" : "Spindle · on dock";

    public LiveIoMonitorViewModel LiveIo { get; } = new();

    /// <summary>
    /// Bottom-right Live I/O toggle + panel on the viewport for every active cell.
    /// (LFAM 3 used to host this inside the large workflow timeline; that bar is hidden
    /// while phase switching lives in the sidebar, so the dock must show on LFAM 3 too.)
    /// </summary>
    public bool ShowStandaloneLiveIo => ActiveCell is not null;

    private Avalonia.Thickness _bottomDockMargin = new(8, 8, 8, 8);
    /// <summary>Margin for the bottom corner docks (ERP left, Live I/O right). The overlay
    /// code-behind lifts them 16px above whichever bottom timeline bar is visible.</summary>
    public Avalonia.Thickness BottomDockMargin
    {
        get => _bottomDockMargin;
        set => SetField(ref _bottomDockMargin, value);
    }

    private Avalonia.Thickness _bottomRightLegendMargin = new(8, 8, 8, 8);
    /// <summary>Margin for bottom-right legends (bead overhang) — code-behind stacks them
    /// above the Live I/O dock, which itself floats above the timeline bars.</summary>
    public Avalonia.Thickness BottomRightLegendMargin
    {
        get => _bottomRightLegendMargin;
        set => SetField(ref _bottomRightLegendMargin, value);
    }

    /// <summary>Viewport inset for workflow bar — 20px sides/bottom; lifts above scrubber when a toolpath is selected.</summary>
    public Avalonia.Thickness Lfam3WorkflowMargin
    {
        get
        {
            var bottom = _isToolpathSelected ? 88 : 32;
            return new Avalonia.Thickness(20, 0, 20, bottom);
        }
    }

    const double Lfam3WorkflowPanelPadding = 14;
    const double Lfam3WorkflowCollapsedMaxHeight = 56;
    const double Lfam3WorkflowExpandedMaxHeight = 240;
    const double Lfam3WorkflowSeqPlaybackMaxHeight = 340;
    const double Lfam3WorkflowLiveIoExpandedMaxHeight = 720;
    const double Lfam3WorkflowPhaseTrackHeight = 68;
    const double Lfam3WorkflowSeqStripHeight = 120;

    bool AnyLfam3PhaseColumnExpanded => AnyLfam3SeqPlaybackExpanded;

    /// <summary>Viewport chrome above the Live I/O scroll region (header, timeline, gaps).</summary>
    double Lfam3LiveIoLayoutChromeHeight
    {
        get
        {
            var panelPadding = Lfam3WorkflowPanelPadding * 2;
            const double headerRow = 36;
            const double sectionGap = 8;
            const double dividerBlock = 9;
            var timeline = AnyLfam3SeqPlaybackExpanded
                ? Lfam3WorkflowPhaseTrackHeight + Lfam3WorkflowSeqStripHeight
                : AnyLfam3PhaseColumnExpanded ? 200.0 : Lfam3WorkflowPhaseTrackHeight;
            return panelPadding + headerRow + sectionGap + timeline + sectionGap + dividerBlock;
        }
    }

    /// <summary>Max height for the workflow overlay — taller when Live I/O is expanded.</summary>
    public double Lfam3WorkflowMaxHeight
    {
        get
        {
            if (!IsLfam3WorkflowExpanded) return Lfam3WorkflowCollapsedMaxHeight;
            if (LiveIo.IsExpanded) return Lfam3WorkflowLiveIoExpandedMaxHeight;
            if (AnyLfam3SeqPlaybackExpanded) return Lfam3WorkflowSeqPlaybackMaxHeight;
            return Lfam3WorkflowExpandedMaxHeight;
        }
    }

    /// <summary>Scroll area height for the expanded Live I/O monitor — fills remaining overlay space.</summary>
    public double Lfam3LiveIoMaxHeight =>
        !LiveIo.IsExpanded ? 0 :
        Math.Max(240, Lfam3WorkflowLiveIoExpandedMaxHeight - Lfam3LiveIoLayoutChromeHeight);

    void NotifyPhaseExpansionChanged()
    {
        OnPropertyChanged(nameof(IsPrePrintScanStepExpanded));
        OnPropertyChanged(nameof(IsPrintStepExpanded));
        OnPropertyChanged(nameof(IsVerifyScanStepExpanded));
        OnPropertyChanged(nameof(IsMillStepExpanded));
        OnPropertyChanged(nameof(ShowPrePrintScanParamCard));
        OnPropertyChanged(nameof(ShowPrintParamCard));
        OnPropertyChanged(nameof(ShowVerifyScanParamCard));
        OnPropertyChanged(nameof(ShowMillParamCard));
        OnPropertyChanged(nameof(Lfam3LiveIoMaxHeight));
        OnPropertyChanged(nameof(Lfam3WorkflowMaxHeight));
        OnPropertyChanged(nameof(Lfam3LiveIoLayoutChromeHeight));
    }

    void NotifyWorkflowStateChanged()
    {
        OnPropertyChanged(nameof(Lfam3WorkflowPhaseIndex));
        OnPropertyChanged(nameof(IsWorkflowSegment0Complete));
        OnPropertyChanged(nameof(IsWorkflowSegment1Complete));
        OnPropertyChanged(nameof(IsWorkflowSegment2Complete));
        OnPropertyChanged(nameof(HasPrePrintScanStep));
        OnPropertyChanged(nameof(PrePrintScanToggleIcon));
        OnPropertyChanged(nameof(PrePrintScanToggleTip));
        OnPropertyChanged(nameof(IsPrePrintScanStepCompleted));
        OnPropertyChanged(nameof(IsPrePrintScanStepActive));
        OnPropertyChanged(nameof(IsPrePrintScanStepPending));
        OnPropertyChanged(nameof(IsPrintStepCompleted));
        OnPropertyChanged(nameof(IsPrintStepActive));
        OnPropertyChanged(nameof(IsPrintStepPending));
        OnPropertyChanged(nameof(IsVerifyScanStepCompleted));
        OnPropertyChanged(nameof(IsVerifyScanStepActive));
        OnPropertyChanged(nameof(IsVerifyScanStepPending));
        OnPropertyChanged(nameof(IsMillStepCompleted));
        OnPropertyChanged(nameof(IsMillStepActive));
        OnPropertyChanged(nameof(IsMillStepPending));
        NotifyPhaseExpansionChanged();
        OnPropertyChanged(nameof(IsExtruderToolActive));
        OnPropertyChanged(nameof(IsScannerToolActive));
        OnPropertyChanged(nameof(IsSpindleToolActive));
        OnPropertyChanged(nameof(IsLfam3ToolpathPhased));
        OnPropertyChanged(nameof(Lfam3ToolpathShowPrintOptions));
        OnPropertyChanged(nameof(Lfam3ToolpathShowScanOptions));
        OnPropertyChanged(nameof(Lfam3ToolpathShowMillOptions));
        LiveIo.UpdateWorkflowPhase(
            showExtruder: IsPrintStepActive,
            showScanner:  IsScannerToolActive,
            showMilling:  IsMillStepActive);
        OnPropertyChanged(nameof(HasArmatureScanMesh));
        OnPropertyChanged(nameof(PrePrintScanParamLine1));
        OnPropertyChanged(nameof(PrePrintScanParamLine2));
        OnPropertyChanged(nameof(PrintParamLine2));
        OnPropertyChanged(nameof(VerifyScanParamLine1));
        OnPropertyChanged(nameof(VerifyScanParamLine2));
    }

    /// <summary>Refreshes workflow parameter cards when slice settings change.</summary>
    public void NotifyWorkflowParamsChanged()
    {
        OnPropertyChanged(nameof(PrintParamLine1));
        OnPropertyChanged(nameof(PrintParamLine2));
        OnPropertyChanged(nameof(PrePrintScanParamLine1));
        OnPropertyChanged(nameof(PrePrintScanParamLine2));
        OnPropertyChanged(nameof(VerifyScanParamLine1));
        OnPropertyChanged(nameof(VerifyScanParamLine2));
        OnPropertyChanged(nameof(MillParamLine1));
        OnPropertyChanged(nameof(MillParamLine2));
    }

    public RelayCommand TogglePrePrintScanStepCommand { get; }
    public RelayCommand SelectPrePrintScanPhaseCommand { get; }
    public RelayCommand SelectPrintPhaseCommand        { get; }
    public RelayCommand SelectVerifyScanPhaseCommand   { get; }
    public RelayCommand SelectMillPhaseCommand         { get; }
    public RelayCommand ToggleLfam3WorkflowCommand     { get; }

    public RelayCommand SimulateExtruderPickCommand    { get; }
    public RelayCommand SimulateExtruderDepositCommand { get; }
    public RelayCommand SimulateScannerPickCommand     { get; }
    public RelayCommand SimulateScannerDepositCommand  { get; }
    public RelayCommand SimulateSpindlePickCommand     { get; }
    public RelayCommand SimulateSpindleDepositCommand  { get; }

    public ToolChangePanelBinding ExtruderToolPanel { get; }
    public ToolChangePanelBinding ScannerToolPanel  { get; }
    public ToolChangePanelBinding SpindleToolPanel  { get; }

    /// <summary>Dev-mode editor for tool-change sequence waypoints (Global_Points.dat).</summary>
    public SequenceWaypointEditorViewModel SequenceWaypointEditor { get; }

    public RelayCommand ToggleToolChangePlaybackCommand { get; }
    public RelayCommand CollapseToolChangePlaybackCommand { get; }

    string? _activeToolChangeSequenceId;
    bool _isToolChangePlaybackExpanded;

    /// <summary>KRL tool-change sequence playing in the viewport overlay, or null.</summary>
    public string? ActiveToolChangeSequenceId
    {
        get => _activeToolChangeSequenceId;
        set
        {
            if (!SetField(ref _activeToolChangeSequenceId, value)) return;
            OnPropertyChanged(nameof(IsExtruderPickSequenceActive));
            OnPropertyChanged(nameof(IsExtruderDepositSequenceActive));
            OnPropertyChanged(nameof(IsScannerPickSequenceActive));
            OnPropertyChanged(nameof(IsScannerDepositSequenceActive));
            OnPropertyChanged(nameof(IsSpindlePickSequenceActive));
            OnPropertyChanged(nameof(IsSpindleDepositSequenceActive));
            if (value is null)
            {
                IsToolChangePlaybackExpanded = false;
                ToolChangeStepText = "";
                ToolChangeStepTextCompact = "";
                SetToolChangeScrubFromViewport(0);
                ToolChangeIsPlaying = false;
                ClearSequenceWaypointTags();
            }
            else
                IsToolChangePlaybackExpanded = true;

            NotifyToolChangePanels();
            ToggleToolChangePlaybackCommand.RaiseCanExecuteChanged();
            CollapseToolChangePlaybackCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>When true, the compact scrub/play strip is shown above the active phase dome.</summary>
    public bool IsToolChangePlaybackExpanded
    {
        get => _isToolChangePlaybackExpanded;
        set
        {
            if (!SetField(ref _isToolChangePlaybackExpanded, value)) return;
            NotifyToolChangePanels();
            CollapseToolChangePlaybackCommand.RaiseCanExecuteChanged();
        }
    }

    string _toolChangeStepText = "";
    int _toolChangeScrubValue;
    bool _toolChangeIsPlaying;
    bool _suppressToolChangeScrubCallback;

    public string ToolChangeStepText
    {
        get => _toolChangeStepText;
        set
        {
            if (!SetField(ref _toolChangeStepText, value)) return;
            NotifyToolChangePanels();
        }
    }

    string _toolChangeStepTextCompact = "";

    /// <summary>Compact playback caption (waypoint + move only, no I/O lines).</summary>
    public string ToolChangeStepTextCompact
    {
        get => _toolChangeStepTextCompact;
        set
        {
            if (!SetField(ref _toolChangeStepTextCompact, value)) return;
            NotifyToolChangePanels();
        }
    }

    public ObservableCollection<SequenceWaypointTag> SequenceWaypointTags { get; } = [];

    public bool HasSequenceWaypointTags => SequenceWaypointTags.Count > 0;

    public void SetSequenceWaypointTags(IReadOnlyList<SequenceWaypointTag> tags)
    {
        SequenceWaypointTags.Clear();
        foreach (var tag in tags)
            SequenceWaypointTags.Add(tag);
        OnPropertyChanged(nameof(HasSequenceWaypointTags));
    }

    public void ClearSequenceWaypointTags()
    {
        if (SequenceWaypointTags.Count == 0) return;
        SequenceWaypointTags.Clear();
        OnPropertyChanged(nameof(HasSequenceWaypointTags));
    }

    public int ToolChangeScrubValue
    {
        get => _toolChangeScrubValue;
        set
        {
            if (!SetField(ref _toolChangeScrubValue, value)) return;
            if (!_suppressToolChangeScrubCallback)
                OnToolChangeScrubRequested?.Invoke(value);
            NotifyToolChangePanels();
        }
    }

    public bool ToolChangeIsPlaying
    {
        get => _toolChangeIsPlaying;
        set
        {
            if (!SetField(ref _toolChangeIsPlaying, value)) return;
            OnPropertyChanged(nameof(ToolChangePlaybackToggleIcon));
            NotifyToolChangePanels();
        }
    }

    public string ToolChangePlaybackToggleIcon =>
        ToolChangeIsPlaying ? "mdi-pause" : "mdi-play";

    internal void SetToolChangeScrubFromViewport(int value)
    {
        _suppressToolChangeScrubCallback = true;
        ToolChangeScrubValue = value;
        _suppressToolChangeScrubCallback = false;
    }

    void NotifyToolChangePanels()
    {
        ExtruderToolPanel.NotifyStateChanged();
        ScannerToolPanel.NotifyStateChanged();
        SpindleToolPanel.NotifyStateChanged();
        OnPropertyChanged(nameof(AnyLfam3SeqPlaybackExpanded));
        NotifyPhaseExpansionChanged();
        OnPropertyChanged(nameof(Lfam3WorkflowMaxHeight));
        OnPropertyChanged(nameof(Lfam3LiveIoLayoutChromeHeight));
    }

    public bool AnyLfam3SeqPlaybackExpanded =>
        ExtruderToolPanel.ShowPlayback || ScannerToolPanel.ShowPlayback || SpindleToolPanel.ShowPlayback;

    public bool IsExtruderPickSequenceActive    => ActiveToolChangeSequenceId == "Extruder_Pick";
    public bool IsExtruderDepositSequenceActive => ActiveToolChangeSequenceId == "Extruder_Deposit";
    public bool IsScannerPickSequenceActive     => ActiveToolChangeSequenceId == "Scanner_Pick";
    public bool IsScannerDepositSequenceActive  => ActiveToolChangeSequenceId == "Scanner_Deposit";
    public bool IsSpindlePickSequenceActive     => ActiveToolChangeSequenceId == "Spindle_Pick";
    public bool IsSpindleDepositSequenceActive  => ActiveToolChangeSequenceId == "Spindle_Deposit";

    /// <summary>Refreshes LFAM 3 tool-picker visibility after a cell swap (UI thread).</summary>
    public void NotifyCellChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(NotifyCellChanged);
            return;
        }

        KrlToolChangeSequenceParser.KrcRootOverride = ActiveCell?.KrcRoot;
        OnPropertyChanged(nameof(ShowLfam3ToolPicker));
        OnPropertyChanged(nameof(ShowStandaloneLiveIo));
        if (ShowLfam3ToolPicker)
            IsLfam3WorkflowExpanded = true;
        else
        {
            if (LiveIo.IsExpanded)
                LiveIo.IsExpanded = false;
            IsLfam3WorkflowExpanded = false;
        }
        var cellName = ActiveCell?.Name;
        if (!string.Equals(cellName, _lfam3WorkflowCellName, StringComparison.Ordinal))
        {
            _lfam3WorkflowCellName = cellName;
            ResetLfam3WorkflowPhase();
        }
        NotifyWorkflowStateChanged();
        NotifyWorkflowParamsChanged();
        RaiseLfam3PhaseCommandsCanExecuteChanged();
        RaiseToolChangeCommandsCanExecuteChanged();
    }

    void SelectLfam3WorkflowPhase(int phaseIndex, string toolName)
    {
        if (!ShowLfam3ToolPicker) return;
        _lfam3WorkflowPhaseIndex = phaseIndex;
        // Phase UI / sidebar only. Do not mount or select the phase tool here —
        // that was selecting the TCP toolhead in the viewport on every Print/Scan/Mill click.
        // Explicit pick/deposit (or robot tool dropdown) still mounts tools when the user asks.
        _ = toolName;
        NotifyWorkflowStateChanged();
    }

    void SyncWorkflowPhaseFromMountedTool()
    {
        if (!ShowLfam3ToolPicker) return;
        int? phase = MountedToolName switch
        {
            "Extruder" or "HV Extruder" => PrintPhaseIndex,
            "Spindle"     => MillPhaseIndex,
            _             => null,
        };
        if (phase is int p && p != _lfam3WorkflowPhaseIndex)
            _lfam3WorkflowPhaseIndex = p;
    }

    void AdjustPhaseIndexForPrePrintScanToggle(bool prePrintScanEnabled)
    {
        if (prePrintScanEnabled)
            _lfam3WorkflowPhaseIndex++;
        else if (_lfam3WorkflowPhaseIndex > 0)
            _lfam3WorkflowPhaseIndex--;
    }

    void ResetLfam3WorkflowPhase()
    {
        _hasPrePrintScanStep = false;
        _lfam3WorkflowPhaseIndex = 0;
        _armatureScanNode = null;
    }

    void RaiseLfam3PhaseCommandsCanExecuteChanged()
    {
        TogglePrePrintScanStepCommand.RaiseCanExecuteChanged();
        SelectPrePrintScanPhaseCommand.RaiseCanExecuteChanged();
        SelectPrintPhaseCommand.RaiseCanExecuteChanged();
        SelectVerifyScanPhaseCommand.RaiseCanExecuteChanged();
        SelectMillPhaseCommand.RaiseCanExecuteChanged();
        ToggleLfam3WorkflowCommand.RaiseCanExecuteChanged();
        RaiseToolChangeCommandsCanExecuteChanged();
    }

    public void RaiseToolChangeCommandsCanExecuteChanged()
    {
        SimulateExtruderPickCommand.RaiseCanExecuteChanged();
        SimulateExtruderDepositCommand.RaiseCanExecuteChanged();
        SimulateScannerPickCommand.RaiseCanExecuteChanged();
        SimulateScannerDepositCommand.RaiseCanExecuteChanged();
        SimulateSpindlePickCommand.RaiseCanExecuteChanged();
        SimulateSpindleDepositCommand.RaiseCanExecuteChanged();
    }

    static bool CanSimulateToolPick(string cellToolName, string mountedToolName, bool showPicker) =>
        showPicker && string.IsNullOrEmpty(mountedToolName)
        && KrlToolChangeSequenceParser.IsSequenceAvailable(PickSequenceId(cellToolName));

    static bool CanSimulateToolDeposit(string cellToolName, string mountedToolName, bool showPicker) =>
        showPicker && mountedToolName == cellToolName
        && KrlToolChangeSequenceParser.IsSequenceAvailable(DepositSequenceId(cellToolName));

    static string PickSequenceId(string cellToolName) => cellToolName switch
    {
        "Extruder" or "HV Extruder" => "Extruder_Pick",
        "Scanner" or "Scanner (Calibrated)" or "Scanner (No Calibration)" => "Scanner_Pick",
        "Spindle" or "Spindle (No Bit)" or "Spindle (Probe)" => "Spindle_Pick",
        _             => "",
    };

    static string DepositSequenceId(string cellToolName) => cellToolName switch
    {
        "Extruder" or "HV Extruder" => "Extruder_Deposit",
        "Scanner" or "Scanner (Calibrated)" or "Scanner (No Calibration)" => "Scanner_Deposit",
        "Spindle" or "Spindle (No Bit)" or "Spindle (Probe)" => "Spindle_Deposit",
        _             => "",
    };

    void RequestToolChangeSimulation(string sequenceId)
    {
        if (string.IsNullOrEmpty(sequenceId)) return;
        OnSimulateToolChangeRequested?.Invoke(sequenceId);
    }

    void SelectLfam3Tool(string toolName)
    {
        if (!ShowLfam3ToolPicker || Robot is null || ActiveCell is null) return;
        var tools = ActiveCell.EffectiveTools;
        for (int i = 0; i < tools.Count; i++)
        {
            if (!string.Equals(tools[i].Name, toolName, StringComparison.Ordinal)) continue;
            Robot.SelectedToolIndex = i;
            return;
        }
    }

    /// <summary>True when a bed scan should register as the pre-print scene mesh.</summary>
    public bool IsPrePrintScanRegistrationPhase =>
        HasPrePrintScanStep && _lfam3WorkflowPhaseIndex == 0;

    /// <summary>Registers the pre-print scan mesh used for armature/fixture collision checks.</summary>
    public void RegisterArmatureScanMesh(SceneNode node)
    {
        _armatureScanNode = node;
        node.Name = node.Name.StartsWith("Armature Scan", StringComparison.Ordinal)
            ? node.Name
            : $"Armature Scan · {node.Name}";
        NotifyWorkflowStateChanged();
    }

    void ClearArmatureScanMeshIfRemoved(SceneNode node)
    {
        if (_armatureScanNode == node)
        {
            _armatureScanNode = null;
            NotifyWorkflowStateChanged();
        }
    }

    /// <summary>Toggles the N-key sync HUD panel.</summary>
    public void ToggleSyncHud() => IsSyncHudOpen = !IsSyncHudOpen;

    public RelayCommand ToggleSyncHudCommand { get; }

    /// <summary>Closes transient viewport overlays (seam editor, gizmo, lay-flat).</summary>
    public void ResetViewportOverlayState()
    {
        IsSyncHudOpen         = false;
        IsLayFlatMode         = false;
        IsSeamEditorActive    = false;
        IsSeamGuideLayerOpen  = false;
        SeamGuideDraft.Clear();
        SelectedSeamGuideIndex = -1;
        ActiveGizmoModeInternal = GizmoMode.None;
    }

    /// <summary>Refreshes sync-HUD bindings when robot connection state changes.</summary>
    public void NotifyRobotSyncChanged() => OnPropertyChanged(nameof(IsRobotSynced));

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

    private float _backdropOpacity = 1f;

    /// <summary>Backdrop blend over shader background. 0 = shader only, 1 = full HDR.</summary>
    public float BackdropOpacity
    {
        get => _backdropOpacity;
        set
        {
            if (SetField(ref _backdropOpacity, Math.Clamp(value, 0f, 1f)))
                NotifyRenderNeeded();
        }
    }

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

    private float _exposure = 1f;
    private float _iblIntensity = 1f;

    /// <summary>Final-render exposure multiplier applied before tonemapping (1 = neutral).</summary>
    public float Exposure
    {
        get => _exposure;
        set => SetField(ref _exposure, value);
    }

    /// <summary>Environment reflection / image-based-lighting gain (1 = neutral).</summary>
    public float IblIntensity
    {
        get => _iblIntensity;
        set => SetField(ref _iblIntensity, value);
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

    /// <summary>Global PBR layer toggles, overlay compositing, and optional factor overrides.</summary>
    public PbrMaterialSettings PbrMaterial { get; } = new();

    /// <summary>Raises change notification after in-place <see cref="PbrMaterial"/> mutation (bridge/MCP).</summary>
    public void NotifyPbrMaterialChanged()
    {
        OnPropertyChanged(nameof(PbrMaterial));
        NotifyRenderNeeded();
    }

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
    public RelayCommand RecenterCommand    { get; }

    // -- Selection transform readout / input -----------------------------------

    private bool   _suppressTransformCb;
    private double _selX, _selY, _selZ, _selA, _selB, _selC;

    public double SelectionX { get => _selX; set { if (SetField(ref _selX, value)) FireSelTranslated(); } }
    public double SelectionY { get => _selY; set { if (SetField(ref _selY, value)) FireSelTranslated(); } }
    public double SelectionZ { get => _selZ; set { if (SetField(ref _selZ, value)) FireSelTranslated(); } }
    public double SelectionA { get => _selA; set { if (SetField(ref _selA, value)) FireSelRotated(); } }
    public double SelectionB { get => _selB; set { if (SetField(ref _selB, value)) FireSelRotated(); } }
    public double SelectionC { get => _selC; set { if (SetField(ref _selC, value)) FireSelRotated(); } }

    private double _selSx = 1, _selSy = 1, _selSz = 1;

    /// <summary>Per-axis scale of the selection. Displayed as real millimetre dimensions or as a
    /// percentage of the imported size depending on the scale tool's own mm/% toggle.</summary>
    public double SelectionScaleX { get => _selSx; set { if (SetField(ref _selSx, value)) FireSelScaled(); } }
    public double SelectionScaleY { get => _selSy; set { if (SetField(ref _selSy, value)) FireSelScaled(); } }
    public double SelectionScaleZ { get => _selSz; set { if (SetField(ref _selSz, value)) FireSelScaled(); } }

    internal Action<double, double, double>? OnSelectionTranslated { get; set; }
    internal Action<double, double, double>? OnSelectionRotated    { get; set; }
    internal Action<double, double, double>? OnSelectionScaled     { get; set; }

    /// <summary>Shared undo/redo stack for transform edits in the viewport.</summary>
    internal UndoRedoService? UndoRedo { get; set; }

    /// <summary>Marks the open .mass as having unsaved changes (status-bar yellow dot).</summary>
    internal Action? MarkWorkspaceDirty { get; set; }

    private void FireSelTranslated() { if (!_suppressTransformCb) OnSelectionTranslated?.Invoke(_selX, _selY, _selZ); }
    private void FireSelRotated()    { if (!_suppressTransformCb) OnSelectionRotated?.Invoke(_selA, _selB, _selC); }
    private void FireSelScaled()     { if (!_suppressTransformCb) OnSelectionScaled?.Invoke(_selSx, _selSy, _selSz); }

    /// <summary>Syncs the displayed per-axis scale without triggering the apply callback.</summary>
    internal void SyncSelectionScaleDisplay(double x, double y, double z)
    {
        _suppressTransformCb = true;
        SelectionScaleX = x; SelectionScaleY = y; SelectionScaleZ = z;
        _suppressTransformCb = false;
    }

    /// <summary>Syncs the displayed transform values without triggering apply callbacks.</summary>
    internal void SyncSelectionDisplay(double x, double y, double z, double a, double b, double c)
    {
        _suppressTransformCb = true;
        SelectionX = x; SelectionY = y; SelectionZ = z;
        SelectionA = a; SelectionB = b; SelectionC = c;
        _suppressTransformCb = false;
    }

    // -- Selection / focus overlay ---------------------------------------------

    private bool _hasSelection;

    /// <summary>True when an object is selected in the viewport (shows the focus overlay).</summary>
    public bool HasSelection
    {
        get => _hasSelection;
        set => SetField(ref _hasSelection, value);
    }

    private bool _showMultiPlanarPlanes = true;
    /// <summary>Viewport toggle: show/hide the Multi-Planar guide plane quads.</summary>
    public bool ShowMultiPlanarPlanes
    {
        get => _showMultiPlanarPlanes;
        set { if (SetField(ref _showMultiPlanarPlanes, value)) NotifyRenderNeeded(); }
    }

    // ── Toolpath edit menu: paint brushes + line marking (preview view) ────────

    private bool _isPaintEditOpen;
    /// <summary>Expands the Edit toolbar in the Preview view. Realtime slicing is
    /// PAUSED while the menu is open (edits accumulate); collapsing the menu (or
    /// the Reslice button) fires the deferred re-slice.</summary>
    public bool IsPaintEditOpen
    {
        get => _isPaintEditOpen;
        set
        {
            if (!SetField(ref _isPaintEditOpen, value)) return;
            if (!value)
            {
                PaintBridgeActive = false;
                PaintRemoveActive = false;
                PaintLineBridgeActive = false;
                PaintLineRemoveActive = false;
                PaintBoxSelectActive = false;
                PaintHandActive = false;
                PaintBridgePickModificationId = null;
                // Slice plane viewer is edit-mode only.
                if (_isSlicePlaneViewerActive)
                    IsSlicePlaneViewerActive = false;
                // Restore normal toolpath display when leaving edit.
                ToolpathLineOpacity = 1f;
            }
            else if (_viewMode == "Preview")
            {
                // Default to path-select with an unrestricted filter so click-to-select
                // works immediately (Formbound/Perimeter filters often look "broken"
                // when the user is aiming at wall paths).
                PaintBoxSelectActive = false;
                PaintHandActive = false;
                if (!string.Equals(PaintPickFilter, "All", StringComparison.OrdinalIgnoreCase))
                    PaintPickFilter = "All";
                // Default support type for edit Apply only — do NOT overwrite the
                // saved FILL PATTERN dropdown (that is restored from workspace prefs).
                if (string.IsNullOrWhiteSpace(PaintSupportType))
                    PaintSupportType = "Formbound Buttress";
                // Path / Point granularity owns the display hierarchy while editing.
                ApplyPaintEditDisplayMode();
                // Arm the scrub/layer window so the LAYERS dual-slider has a real
                // Maximum (otherwise it sticks at the empty 1–2 default).
                OnEnsureEditScrub?.Invoke();
                // Seed CREATE MODIFICATION catalog (search + Offset path, …).
                EnsureCreateModificationCatalog();
                // Layers-triple (2D slice plane) is on by default in edit mode.
                // Session restore may override this immediately after.
                IsSlicePlaneViewerActive = true;
            }
            RealtimeSlicingPaused = value;   // collapse → deferred re-slice fires
            // Edit mode borrows the Toolpath view's display profile (dark,
            // line-oriented); leaving restores the active view's own profile.
            ApplyViewDisplayProfile();
            OnPropertyChanged(nameof(ShowToolpathStatsOverlay));
            OnPropertyChanged(nameof(ShowMultiPlanarPlanesButton));
            OnPaintEditModeChanged?.Invoke(value);
            NotifyRenderNeeded();
        }
    }

    /// <summary>Notifies the right panel to swap workflow cards for edit-mode cards.</summary>
    internal Action<bool>? OnPaintEditModeChanged { get; set; }

    /// <summary>
    /// Viewport arms the scrub session / layer ends for the edit-mode LAYERS slider.
    /// Without this the dual-slider stays at the empty 1–2 default when no toolpath
    /// was previously selected.
    /// </summary>
    internal Action? OnEnsureEditScrub { get; set; }

    /// <summary>Closes paint edit mode (EXIT EDIT MODE floating card).</summary>
    public RelayCommand ExitPaintEditCommand => _exitPaintEdit ??= new RelayCommand(() =>
        IsPaintEditOpen = false);
    private RelayCommand? _exitPaintEdit;

    // ── 2D Slice Plane Viewer (edit mode only) ───────────────────────────────

    private bool _isSlicePlaneViewerActive;

    /// <summary>
    /// Top-down orthographic slice context: current layer solid, one layer below
    /// transparent, one layer above transparent + dashed. Only meaningful while
    /// <see cref="IsPaintEditOpen"/>; the overlay button is hidden outside edit mode.
    /// </summary>
    public bool IsSlicePlaneViewerActive
    {
        get => _isSlicePlaneViewerActive;
        set
        {
            // Outside edit mode the viewer cannot stay on.
            if (value && !IsPaintEditOpen) value = false;
            if (!SetField(ref _isSlicePlaneViewerActive, value)) return;
            OnPropertyChanged(nameof(ShowSlicePlaneStatsOverlay));
            RefreshSlicePlaneStats();
            OnSlicePlaneViewerChanged?.Invoke(value);
            NotifyRenderNeeded();
        }
    }

    /// <summary>Camera lock / restore when the 2D slice plane viewer toggles.</summary>
    internal Action<bool>? OnSlicePlaneViewerChanged { get; set; }

    /// <summary>0-based layer index at the current scrub high handle.</summary>
    internal int CurrentScrubLayerIndex => GetScrubLayerIndex();

    /// <summary>Exclusive move-count ends per layer (prefix sums), or null.</summary>
    internal int[]? ScrubLayerEnds => _scrubLayerEnds;

    // ── 2D Slice Plane Viewer HUD stats ──────────────────────────────────────

    /// <summary>True when the top-right slice stats panel should show.</summary>
    public bool ShowSlicePlaneStatsOverlay =>
        IsSlicePlaneViewerActive && IsPaintEditOpen && ActiveScrubToolpath is not null;

    private string _slicePlaneStatsHeader = "";
    public string SlicePlaneStatsHeader
    {
        get => _slicePlaneStatsHeader;
        private set => SetField(ref _slicePlaneStatsHeader, value);
    }

    private string _slicePlaneStatsBody = "";
    public string SlicePlaneStatsBody
    {
        get => _slicePlaneStatsBody;
        private set => SetField(ref _slicePlaneStatsBody, value);
    }

    private string _slicePlaneStatsBelow = "";
    public string SlicePlaneStatsBelow
    {
        get => _slicePlaneStatsBelow;
        private set
        {
            if (!SetField(ref _slicePlaneStatsBelow, value)) return;
            OnPropertyChanged(nameof(HasSlicePlaneStatsBelow));
        }
    }

    public bool HasSlicePlaneStatsBelow => !string.IsNullOrEmpty(_slicePlaneStatsBelow);

    /// <summary>
    /// Rebuilds the 2D slice HUD from the active scrub toolpath and current layer.
    /// Call when the slice viewer toggles, scrub layer changes, or toolpath updates.
    /// </summary>
    internal void RefreshSlicePlaneStats()
    {
        OnPropertyChanged(nameof(ShowSlicePlaneStatsOverlay));
        if (!ShowSlicePlaneStatsOverlay || ActiveScrubToolpath is not { Layers.Count: > 0 } tp)
        {
            SlicePlaneStatsHeader = "";
            SlicePlaneStatsBody = "";
            SlicePlaneStatsBelow = "";
            return;
        }

        float bead = (float)(AdditiveSettings?.BeadWidth ?? 6.0);
        float height = (float)(AdditiveSettings?.LayerHeight ?? 3.0);
        if (bead < 0.1f) bead = 6f;
        if (height < 0.05f) height = 3f;
        var rates = new MassiveSlicer.Core.Slicing.ToolpathMotionRates(
            AdditiveSettings?.PrintSpeed ?? 60,
            AdditiveSettings?.TravelSpeed ?? 600,
            AdditiveSettings?.WipeSpeed ?? 60);

        int cur = Math.Clamp(CurrentScrubLayerIndex, 0, tp.Layers.Count - 1);
        var s = MassiveSlicer.Core.Slicing.SliceLayerAnalyzer.Analyze(
            tp, cur, bead, height, rates);

        SlicePlaneStatsHeader =
            $"SLICE  L{s.LayerNumber} / {tp.Layers.Count}    Z {s.Z:0.#} mm";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Path length     {FmtLen(s.ExtrudeLengthMm)}");
        sb.AppendLine($"  Travel        {FmtLen(s.TravelLengthMm)}");
        if (s.LightningLengthMm > 0.5)
            sb.AppendLine($"  Formbound     {FmtLen(s.LightningLengthMm)}  ({s.FormboundPercent:0.#}%)");
        if (s.WipeLengthMm > 0.5)
            sb.AppendLine($"  Wipe          {FmtLen(s.WipeLengthMm)}");
        sb.AppendLine($"Islands         {s.Islands}   ({s.ClosedLoops} closed · {s.OpenPaths} open)");
        sb.AppendLine($"Moves           {s.ExtrudeMoves:N0} extrude · {s.TravelMoves:N0} travel");
        if (cur > 0)
            sb.AppendLine($"Overhang        {s.OverhangPercent:0.#}%   ({FmtLen(s.OverhangLengthMm)})");
        else
            sb.AppendLine("Overhang        —  (first layer)");
        sb.AppendLine($"Est. time       {MassiveSlicer.Core.Slicing.ToolpathStatistics.FormatDuration(s.EstTimeSeconds)}");
        sb.AppendLine($"Volume          {FmtVol(s.VolumeMm3)}");
        if (s.BoundsWidthMm > 0 || s.BoundsDepthMm > 0)
            sb.AppendLine($"Bounds          {s.BoundsWidthMm:0.#} × {s.BoundsDepthMm:0.#} mm");
        if (s.BoundsHeightSpanMm > 0.5f)
            sb.AppendLine($"Z span          {s.BoundsHeightSpanMm:0.#} mm");
        SlicePlaneStatsBody = sb.ToString().TrimEnd();

        // Compact readout for the three layers below (context in the 2D view).
        var below = new System.Text.StringBuilder();
        for (int d = 1; d <= 3; d++)
        {
            int li = cur - d;
            if (li < 0) break;
            var b = MassiveSlicer.Core.Slicing.SliceLayerAnalyzer.Analyze(
                tp, li, bead, height, rates);
            string oh = li > 0 ? $"{b.OverhangPercent:0.#}% OH" : "base";
            below.AppendLine(
                $"L{b.LayerNumber}  {FmtLen(b.ExtrudeLengthMm)}  ·  {b.Islands} isl  ·  {oh}");
        }
        SlicePlaneStatsBelow = below.Length > 0
            ? "BELOW\n" + below.ToString().TrimEnd()
            : "";
    }

    private static string FmtLen(double mm)
        => MassiveSlicer.Core.Slicing.ToolpathStatistics.FormatCutLength(mm);

    private static string FmtVol(double mm3)
        => mm3 >= 1_000_000 ? $"{mm3 / 1_000_000.0:0.###} L"
            : mm3 >= 1000 ? $"{mm3 / 1000.0:0.#} cm³"
            : $"{mm3:0} mm³";

    /// <summary>
    /// Path granularity → centre-line only (no fat beads) unless
    /// <see cref="PaintShowBeads"/> is on.
    /// Point granularity → every bead midpoint as a point; lines grayed + thin.
    /// (SceneRenderer.ShowAllPathPoints is set from the viewport each frame.)
    /// </summary>
    internal void ApplyPaintEditDisplayMode()
    {
        if (!IsPaintEditOpen || _viewMode != "Preview") return;
        if (PaintPointGranularityActive)
        {
            // Points hierarchy: all bead points; centre-lines stay readable (thicker in
            // the renderer when ShowAllPathPoints is on).
            ShowBead = PaintShowBeads;
            ShowExtrusionMoves = true;
            ShowTravelMoves = false;
            ShowWipeMoves = false;
            // Seam-only overlay off — ShowAllPathPoints draws every extrude midpoint.
            ShowSeam = false;
            ToolpathLineOpacity = 0.65f;
        }
        else
        {
            // Line / path view: clean centre-lines only (beads optional via toggle).
            ShowBead = PaintShowBeads;
            ShowExtrusionMoves = true;
            ShowTravelMoves = false;
            ShowWipeMoves = false;
            ShowSeam = false;
            ToolpathLineOpacity = 1f;
        }
    }

    // ── Edit-mode display toggles (eye menu: markers / beads / seams / …) ────

    private bool _showPaintMarkers = true;
    /// <summary>When true, Support/Remove paint mark spheres are drawn in the viewport.</summary>
    public bool ShowPaintMarkers
    {
        get => _showPaintMarkers;
        set
        {
            if (!SetField(ref _showPaintMarkers, value)) return;
            NotifyRenderNeeded();
        }
    }

    private bool _paintShowBeads;
    /// <summary>
    /// When true in edit mode, draw the full bead mesh on top of centre-lines.
    /// Default off so path/point selection stays readable. Controlled from the
    /// edit toolbar eye / Visibility menu.
    /// </summary>
    public bool PaintShowBeads
    {
        get => _paintShowBeads;
        set
        {
            if (!SetField(ref _paintShowBeads, value)) return;
            if (IsPaintEditOpen)
            {
                ShowBead = value;
                NotifyRenderNeeded();
            }
        }
    }

    /// <summary>
    /// Multi-Planar "Planes" toggle — hidden in toolpath Edit mode so it does not
    /// crowd the paint / line-select HUD.
    /// </summary>
    public bool ShowMultiPlanarPlanesButton =>
        !IsPaintEditOpen && (AdditiveSettings?.ShowMultiPlanarControls ?? false);

    private void SetPaintTool(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        // Tools are mutually exclusive — turn the others off first.
        if (value)
        {
            _paintBridgeActive = false;
            _paintRemoveActive = false;
            _paintLineBridgeActive = false;
            _paintLineRemoveActive = false;
            OnPropertyChanged(nameof(PaintBridgeActive));
            OnPropertyChanged(nameof(PaintRemoveActive));
            OnPropertyChanged(nameof(PaintLineBridgeActive));
            OnPropertyChanged(nameof(PaintLineRemoveActive));
        }
        field = value;
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(PaintBrushActive));
        OnPropertyChanged(nameof(PaintLineToolActive));
        OnPropertyChanged(nameof(PaintPathSelectActive));
        NotifyRenderNeeded();
    }

    private bool _paintBridgeActive;
    /// <summary>Brush: paint Bridge marks (fingers grow under painted beads).</summary>
    public bool PaintBridgeActive
    {
        get => _paintBridgeActive;
        set => SetPaintTool(ref _paintBridgeActive, value);
    }

    private bool _paintRemoveActive;
    /// <summary>Brush: paint Remove marks (painted beads are deleted).</summary>
    public bool PaintRemoveActive
    {
        get => _paintRemoveActive;
        set => SetPaintTool(ref _paintRemoveActive, value);
    }

    private bool _paintLineBridgeActive;
    /// <summary>Line tool: click one bead line to mark the whole contour as needing
    /// support (Bridge marks along its length).</summary>
    public bool PaintLineBridgeActive
    {
        get => _paintLineBridgeActive;
        set => SetPaintTool(ref _paintLineBridgeActive, value);
    }

    private bool _paintLineRemoveActive;
    /// <summary>Line tool: click one bead line to remove the whole contour from the
    /// toolpath (Remove marks along its length).</summary>
    public bool PaintLineRemoveActive
    {
        get => _paintLineRemoveActive;
        set => SetPaintTool(ref _paintLineRemoveActive, value);
    }

    /// <summary>True while any paint tool is selected (camera drag disabled).</summary>
    public bool PaintBrushActive =>
        PaintBridgeActive || PaintRemoveActive || PaintLineBridgeActive || PaintLineRemoveActive;

    /// <summary>True while a click-a-line tool is selected (click picks a contour).</summary>
    public bool PaintLineToolActive => PaintLineBridgeActive || PaintLineRemoveActive;

    private double _paintBrushRadiusMm = 15.0;
    /// <summary>Brush radius in world millimetres. Right-click-drag horizontally
    /// while a brush is active to resize; Alt+paint erases marks.</summary>
    public double PaintBrushRadiusMm
    {
        get => _paintBrushRadiusMm;
        set => SetField(ref _paintBrushRadiusMm, Math.Clamp(value, 3.0, 200.0));
    }

    // ── Edit-mode selection toolbar ────────────────────────────────────────────

    private bool _paintHandActive;
    /// <summary>Hand/navigate tool: clicks do nothing, camera drags as normal.</summary>
    public bool PaintHandActive
    {
        get => _paintHandActive;
        set
        {
            if (!SetField(ref _paintHandActive, value)) return;
            if (value)
            {
                PaintBridgeActive = false;
                PaintRemoveActive = false;
                PaintLineBridgeActive = false;
                PaintLineRemoveActive = false;
                PaintBoxSelectActive = false;
            }
            OnPropertyChanged(nameof(PaintPathSelectActive));
            NotifyRenderNeeded();
        }
    }

    private bool _paintBoxSelectActive;
    /// <summary>
    /// Region select tool armed (square marquee or lasso — see
    /// <see cref="PaintRegionSelectMode"/>). Long-press the toolbar icon to switch mode.
    /// </summary>
    public bool PaintBoxSelectActive
    {
        get => _paintBoxSelectActive;
        set
        {
            if (!SetField(ref _paintBoxSelectActive, value)) return;
            if (value)
            {
                PaintHandActive = false;
                PaintBridgeActive = false;
                PaintRemoveActive = false;
                PaintLineBridgeActive = false;
                PaintLineRemoveActive = false;
            }
            OnPropertyChanged(nameof(PaintPathSelectActive));
            OnPropertyChanged(nameof(PaintRegionSelectIcon));
            OnPropertyChanged(nameof(PaintRegionSelectToolTip));
            NotifyRenderNeeded();
        }
    }

    /// <summary>"Square" (marquee) or "Lasso". Toggled by long-pressing the region-select icon.</summary>
    private string _paintRegionSelectMode = "Square";
    public string PaintRegionSelectMode
    {
        get => _paintRegionSelectMode;
        set
        {
            if (!SetField(ref _paintRegionSelectMode, value is "Lasso" ? "Lasso" : "Square")) return;
            OnPropertyChanged(nameof(PaintRegionSelectIsLasso));
            OnPropertyChanged(nameof(PaintRegionSelectIcon));
            OnPropertyChanged(nameof(PaintRegionSelectToolTip));
            NotifyRenderNeeded();
        }
    }

    public bool PaintRegionSelectIsLasso => PaintRegionSelectMode == "Lasso";
    public bool PaintRegionSelectIsSquare => !PaintRegionSelectIsLasso;

    public string PaintRegionSelectIcon =>
        PaintRegionSelectIsLasso ? "mdi-lasso" : "mdi-selection";

    public string PaintRegionSelectToolTip =>
        PaintRegionSelectIsLasso
            ? "Lasso select — drag a freehand loop. Long-press icon for square marquee."
            : "Square select — drag a rectangle. Long-press icon for lasso.";

    /// <summary>Cycle Square ↔ Lasso (long-press on the region-select button).</summary>
    public void TogglePaintRegionSelectMode()
    {
        PaintRegionSelectMode = PaintRegionSelectIsLasso ? "Square" : "Lasso";
        // Arm the tool if it was off so the mode change is immediately usable.
        if (!PaintBoxSelectActive)
            PaintBoxSelectActive = true;
    }

    /// <summary>Default click-a-path select mode (no other tool armed).</summary>
    public bool PaintPathSelectActive =>
        IsPaintEditOpen && !PaintHandActive && !PaintBoxSelectActive && !PaintBrushActive;

    public RelayCommand SelectPathToolCommand => _selectPathTool ??= new RelayCommand(() =>
    {
        PaintHandActive = false;
        PaintBoxSelectActive = false;
        PaintBridgeActive = false;
        PaintRemoveActive = false;
        PaintLineBridgeActive = false;
        PaintLineRemoveActive = false;
        OnPropertyChanged(nameof(PaintPathSelectActive));
    });
    private RelayCommand? _selectPathTool;

    private int _paintSelectionCount;
    /// <summary>Paths in the current edit selection (set by the viewport).</summary>
    public int PaintSelectionCount
    {
        get => _paintSelectionCount;
        set
        {
            if (SetField(ref _paintSelectionCount, value))
            {
                OnPropertyChanged(nameof(PaintSelectionLabel));
                OnPropertyChanged(nameof(HasPaintSelection));
            }
        }
    }

    public bool HasPaintSelection => PaintSelectionCount > 0;

    public string PaintSelectionLabel
    {
        get
        {
            if (PaintSelectionCount == 0) return "0 paths selected";
            if (PaintPointGranularityActive)
                return PaintSelectionCount == 1
                    ? "1 point selected"
                    : $"{PaintSelectionCount} points selected";
            return PaintSelectionCount == 1
                ? "1 path selected"
                : $"{PaintSelectionCount} paths selected";
        }
    }

    /// <summary>Rows for the selection popup (layer / span list with per-item remove).</summary>
    public ObservableCollection<PaintSelectionListItem> PaintSelectionItems { get; } = [];

    /// <summary>Applied paint modifications (Support/Remove) — reselectable from MODIFICATIONS.</summary>
    public ObservableCollection<PaintModificationListItem> PaintModifications { get; } = [];

    public bool HasPaintModifications => PaintModifications.Count > 0;

    public string PaintModificationsSummary =>
        PaintModifications.Count == 0
            ? "No modifications yet"
            : $"{PaintModifications.Count} modification(s)";

    /// <summary>
    /// When set, the next viewport path/point pick attaches as the bridge target
    /// for this modification (multi-layer Formbound scaffold).
    /// </summary>
    private Guid? _paintBridgePickModificationId;
    public Guid? PaintBridgePickModificationId
    {
        get => _paintBridgePickModificationId;
        set
        {
            if (!SetField(ref _paintBridgePickModificationId, value)) return;
            OnPropertyChanged(nameof(IsPaintBridgePicking));
            OnPropertyChanged(nameof(PaintBridgePickHint));
            // Refresh per-row picking flags without rebuilding the list.
            foreach (var item in PaintModifications)
                item.IsPickingBridgeTarget = value.HasValue && item.Id == value.Value;
        }
    }

    public bool IsPaintBridgePicking => PaintBridgePickModificationId.HasValue;

    public string PaintBridgePickHint =>
        IsPaintBridgePicking
            ? "Click a path or point on another layer to bridge — Escape to cancel."
            : "";

    /// <summary>Viewport reselects / deletes a stored modification by id.</summary>
    internal Action<Guid>? OnPaintModificationSelectRequested;
    internal Action<Guid>? OnPaintModificationDeleteRequested;
    internal Action? OnPaintModificationsClearRequested;
    internal Action<Guid>? OnPaintModificationPickBridgeRequested;
    internal Action<Guid>? OnPaintModificationClearBridgeRequested;
    internal Action<Guid>? OnPaintModificationToggleExpandRequested;
    internal Action<Guid, string>? OnPaintModificationSupportTypeChanged;
    internal Action<Guid, string>? OnPaintModificationSupportSideChanged;

    public RelayCommand ClearPaintModificationsCommand => _clearPaintMods ??= new RelayCommand(() =>
        OnPaintModificationsClearRequested?.Invoke());
    private RelayCommand? _clearPaintMods;

    // ── Quick-add modifier (autocomplete in the MODIFICATIONS panel) ─────────

    /// <summary>Type-to-find modifier names — replaces the CREATE MODIFICATION card.</summary>
    public string[] ModifierQuickAddCatalog { get; } =
    [
        Core.Models.PaintSupportStyleUtil.LabelStructural,
        Core.Models.PaintSupportStyleUtil.LabelButtress,
        Core.Models.PaintSupportStyleUtil.LabelBridge,
        Core.Models.PaintSupportStyleUtil.LabelTree,
        "Remove selection",
        "Offset path",
    ];

    private string _modifierQuickAddText = "";
    public string ModifierQuickAddText
    {
        get => _modifierQuickAddText;
        set => SetField(ref _modifierQuickAddText, value ?? "");
    }

    private string? _modifierQuickAddPick;
    public string? ModifierQuickAddPick
    {
        get => _modifierQuickAddPick;
        set
        {
            if (!SetField(ref _modifierQuickAddPick, value)) return;
            if (string.IsNullOrWhiteSpace(value)) return;
            var pick = value;
            // Clear the box after commit so the next search starts fresh.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _modifierQuickAddPick = null;
                OnPropertyChanged(nameof(ModifierQuickAddPick));
                ModifierQuickAddText = "";
                ApplyNamedModifier(pick);
            });
        }
    }

    /// <summary>Applies a modifier by catalog name to the current edit selection.</summary>
    internal void ApplyNamedModifier(string label)
    {
        if (string.Equals(label, "Offset path", StringComparison.OrdinalIgnoreCase))
        {
            // Show the Offset path settings inline (Apply lives on that block).
            SelectedCreateModificationId = "offset";
            return;
        }
        if (string.Equals(label, "Remove selection", StringComparison.OrdinalIgnoreCase))
        {
            OnPaintApplyRequested?.Invoke(false);
            return;
        }
        var style = Core.Models.PaintSupportStyleUtil.FromLabel(label);
        if (style == Core.Models.PaintSupportStyle.StructuralSupport)
        {
            OnAddStructuralSupportRequested?.Invoke();
            return;
        }
        PaintModificationMode = "Support";
        PaintSupportType = Core.Models.PaintSupportStyleUtil.ToLabel(style);
        ApplyPaintSupportTypeToSettings();
        OnPaintApplyRequested?.Invoke(true);
    }

    /// <summary>Row data for one applied modification (viewport → panel).</summary>
    internal readonly record struct PaintModRow(
        Guid Id,
        bool IsSupport,
        bool IsOffset,
        string Title,
        string Detail,
        string AnchorSummary,
        bool HasBridgeTarget,
        string BridgeTargetSummary,
        int ScaffoldLayerCount,
        int ScaffoldMarkCount,
        bool KeepExpanded,
        string SupportType,
        string SupportSide);

    internal void SetPaintModifications(IReadOnlyList<PaintModRow> rows)
    {
        var expandedIds = PaintModifications
            .Where(m => m.IsExpanded)
            .Select(m => m.Id)
            .ToHashSet();
        var pickId = PaintBridgePickModificationId;

        PaintModifications.Clear();
        foreach (var r in rows)
        {
            var id = r.Id;
            var item = new PaintModificationListItem
            {
                Id = id,
                IsSupport = r.IsSupport,
                IsOffset = r.IsOffset,
                KindLabel = r.IsOffset ? "Offset"
                    : !r.IsSupport ? "Remove"
                    : Core.Models.PaintSupportStyleUtil.FromLabel(r.SupportType)
                        == Core.Models.PaintSupportStyle.StructuralSupport
                        ? "Structural" : "Support",
                Title = r.Title,
                Detail = r.Detail,
                AnchorSummary = r.AnchorSummary,
                HasBridgeTarget = r.HasBridgeTarget,
                BridgeTargetSummary = r.BridgeTargetSummary,
                ScaffoldLayerCount = r.ScaffoldLayerCount,
                ScaffoldMarkCount = r.ScaffoldMarkCount,
                IsExpanded = r.KeepExpanded || expandedIds.Contains(id),
                IsPickingBridgeTarget = pickId.HasValue && pickId.Value == id,
                ToggleExpandCommand = new RelayCommand(() =>
                    OnPaintModificationToggleExpandRequested?.Invoke(id)),
                SelectCommand = new RelayCommand(() => OnPaintModificationSelectRequested?.Invoke(id)),
                DeleteCommand = new RelayCommand(() => OnPaintModificationDeleteRequested?.Invoke(id)),
                PickBridgeTargetCommand = new RelayCommand(() =>
                    OnPaintModificationPickBridgeRequested?.Invoke(id)),
                ClearBridgeTargetCommand = new RelayCommand(() =>
                    OnPaintModificationClearBridgeRequested?.Invoke(id)),
            };
            // Init type/side first, then wire change handlers (avoids apply-on-rebuild).
            item.SupportType = string.IsNullOrWhiteSpace(r.SupportType)
                ? "Formbound Buttress"
                : r.SupportType;
            item.SupportSide = string.IsNullOrWhiteSpace(r.SupportSide)
                ? Core.Models.PaintSupportSideUtil.LabelInside
                : r.SupportSide;
            item.SupportTypeChanged = (modId, type) =>
                OnPaintModificationSupportTypeChanged?.Invoke(modId, type);
            item.SupportSideChanged = (modId, side) =>
                OnPaintModificationSupportSideChanged?.Invoke(modId, side);
            PaintModifications.Add(item);
        }
        OnPropertyChanged(nameof(HasPaintModifications));
        OnPropertyChanged(nameof(PaintModificationsSummary));
    }

    /// <summary>
    /// Replaces the popup list from the viewport's live selection. Call after any
    /// paint select / deselect / box-select change. Wires each row's RemoveCommand.
    /// </summary>
    internal void SetPaintSelectionItems(IReadOnlyList<(int LayerIndex, int MoveStart, int MoveCount, float LayerZ, bool IsPoint, string Title, string Detail)> rows)
    {
        PaintSelectionItems.Clear();
        foreach (var r in rows)
        {
            int li = r.LayerIndex, ms = r.MoveStart, mc = r.MoveCount;
            PaintSelectionItems.Add(new PaintSelectionListItem
            {
                LayerIndex = li,
                MoveStart  = ms,
                MoveCount  = mc,
                LayerZ     = r.LayerZ,
                IsPoint    = r.IsPoint,
                Title      = r.Title,
                Detail     = r.Detail,
                RemoveCommand = new RelayCommand(() =>
                    OnPaintDeselectItemRequested?.Invoke(li, ms, mc)),
            });
        }
        PaintSelectionCount = rows.Count;
        OnPropertyChanged(nameof(PaintSelectionLabel));
        OnPropertyChanged(nameof(HasPaintSelection));
        OnPropertyChanged(nameof(PaintActiveSelectionSummary));
        ApplyPaintModificationCommand.RaiseCanExecuteChanged();
        ApplyCreateModificationCommand.RaiseCanExecuteChanged();
        RefreshSupportBridgeEstimate();
    }

    /// <summary>Viewport removes one entry identified by layer + span.</summary>
    internal Action<int, int, int>? OnPaintDeselectItemRequested;

    // ── Support-bridge proximity helper (SELECTION sidebar, Mode = Support) ──

    private string _supportBridgeSummary = "";
    /// <summary>Short line: layers needed / gap / already supported.</summary>
    public string SupportBridgeSummary
    {
        get => _supportBridgeSummary;
        private set => SetField(ref _supportBridgeSummary, value);
    }

    private string _supportBridgeDetail = "";
    /// <summary>Longer MaxStep / angle / sample explanation.</summary>
    public string SupportBridgeDetail
    {
        get => _supportBridgeDetail;
        private set => SetField(ref _supportBridgeDetail, value);
    }

    private int _supportBridgeLayers;
    public int SupportBridgeLayers
    {
        get => _supportBridgeLayers;
        private set => SetField(ref _supportBridgeLayers, value);
    }

    private float _supportBridgeGapMm;
    public float SupportBridgeGapMm
    {
        get => _supportBridgeGapMm;
        private set => SetField(ref _supportBridgeGapMm, value);
    }

    private float _supportBridgeMaxStepMm;
    public float SupportBridgeMaxStepMm
    {
        get => _supportBridgeMaxStepMm;
        private set => SetField(ref _supportBridgeMaxStepMm, value);
    }

    private float _supportBridgeOverhangDeg = 30f;
    public float SupportBridgeOverhangDeg
    {
        get => _supportBridgeOverhangDeg;
        private set => SetField(ref _supportBridgeOverhangDeg, value);
    }

    private bool _supportBridgeAlreadyOk;
    public bool SupportBridgeAlreadyOk
    {
        get => _supportBridgeAlreadyOk;
        private set => SetField(ref _supportBridgeAlreadyOk, value);
    }

    private string _supportBridgeGapStepLabel = "";
    /// <summary>e.g. "4.2 / 1.73 mm" (max gap / MaxStep).</summary>
    public string SupportBridgeGapStepLabel
    {
        get => _supportBridgeGapStepLabel;
        private set => SetField(ref _supportBridgeGapStepLabel, value);
    }

    /// <summary>Show the bridge helper when Mode=Support and something is selected.</summary>
    public bool ShowSupportBridgeHelper =>
        ShowPaintSupportTypePicker && HasPaintSelection && !string.IsNullOrEmpty(SupportBridgeSummary);

    /// <summary>
    /// From the current edit selection, estimate how many layers of steppable
    /// support (at the Formbound overhang angle, default 30°) are needed to bridge
    /// down to solid geometry or the bed.
    /// </summary>
    public void RefreshSupportBridgeEstimate()
    {
        if (!ShowPaintSupportTypePicker || PaintSelectionItems.Count == 0
            || ActiveScrubToolpath is not { Layers.Count: > 0 } tp)
        {
            SupportBridgeSummary = "";
            SupportBridgeDetail = "";
            SupportBridgeGapStepLabel = "";
            SupportBridgeLayers = 0;
            SupportBridgeGapMm = 0;
            SupportBridgeAlreadyOk = false;
            OnPropertyChanged(nameof(ShowSupportBridgeHelper));
            return;
        }

        float layerH = (float)(AdditiveSettings?.LayerHeight ?? 3.0);
        if (layerH < 0.1f) layerH = 3f;
        float bead = (float)(AdditiveSettings?.BeadWidth ?? 6.0);
        if (bead < 0.5f) bead = 6f;
        float deg = (float)(AdditiveSettings?.LightningOverhangDeg ?? 30.0);
        if (deg < 5f) deg = 30f;

        var spans = new List<(int, int, int)>(PaintSelectionItems.Count);
        foreach (var item in PaintSelectionItems)
            spans.Add((item.LayerIndex, item.MoveStart, item.MoveCount));

        // Tree Support is bed-rooted — never stop the estimate at a mid-air solid plane.
        bool toBed = Core.Models.PaintSupportStyleUtil.IsTree(
            Core.Models.PaintSupportStyleUtil.FromLabel(PaintSupportType));

        var r = MassiveSlicer.Core.Slicing.SupportBridgeEstimate.Compute(
            tp, spans, layerH, bead, overhangDeg: deg, capMaxStepToHalfBead: true,
            toBedFoundation: toBed);

        SupportBridgeSummary = r.Summary;
        SupportBridgeDetail = r.Detail;
        SupportBridgeLayers = r.LayersRequired;
        SupportBridgeGapMm = r.MaxGapMm;
        SupportBridgeMaxStepMm = r.MaxStepMm;
        SupportBridgeOverhangDeg = r.OverhangDeg;
        SupportBridgeAlreadyOk = r.AlreadySupported;
        SupportBridgeGapStepLabel = $"{r.MaxGapMm:0.#} / {r.MaxStepMm:0.##} mm";
        OnPropertyChanged(nameof(ShowSupportBridgeHelper));
    }

    private string _paintSelectGranularity = "Path";
    /// <summary>Selection granularity: "Path" picks a whole contour section per
    /// click; "Point" picks the single bead under the cursor.</summary>
    public string PaintSelectGranularity
    {
        get => _paintSelectGranularity;
        set
        {
            if (!SetField(ref _paintSelectGranularity, value)) return;
            OnPropertyChanged(nameof(PaintPathGranularityActive));
            OnPropertyChanged(nameof(PaintPointGranularityActive));
            OnPropertyChanged(nameof(PaintSelectionLabel));
            ApplyPaintEditDisplayMode();
            NotifyRenderNeeded();
        }
    }

    public bool PaintPathGranularityActive  => PaintSelectGranularity == "Path";
    public bool PaintPointGranularityActive => PaintSelectGranularity == "Point";

    public RelayCommand SetPathGranularityCommand => _setPathGran ??= new RelayCommand(() =>
        PaintSelectGranularity = "Path");
    private RelayCommand? _setPathGran;

    public RelayCommand SetPointGranularityCommand => _setPointGran ??= new RelayCommand(() =>
        PaintSelectGranularity = "Point");
    private RelayCommand? _setPointGran;

    public string[] PaintPickFilterOptions { get; } = ["All", "Formbound", "Perimeter"];

    private string _paintPickFilter = "All";
    /// <summary>Restricts what hover/click/box selection can pick.</summary>
    public string PaintPickFilter
    {
        get => _paintPickFilter;
        set => SetField(ref _paintPickFilter, value);
    }

    /// <summary>Mark action for the left SELECTION panel Apply button.</summary>
    public string[] PaintModificationModeOptions { get; } = ["Support", "Remove"];

    private string _paintModificationMode = "Support";
    public string PaintModificationMode
    {
        get => _paintModificationMode;
        set
        {
            if (!SetField(ref _paintModificationMode, value ?? "Support")) return;
            OnPropertyChanged(nameof(ShowPaintSupportTypePicker));
            OnPropertyChanged(nameof(ShowSupportBridgeHelper));
            RefreshSupportBridgeEstimate();
        }
    }

    /// <summary>
    /// Support strategy used when Mode = Support. Stored per applied modification
    /// (and on each Bridge mark). Default: Formbound Buttress (T-column).
    /// </summary>
    public string[] PaintSupportTypeOptions { get; } =
        Core.Models.PaintSupportStyleUtil.AllLabels;

    private string _paintSupportType = Core.Models.PaintSupportStyleUtil.LabelButtress;
    public string PaintSupportType
    {
        get => _paintSupportType;
        set
        {
            var v = Core.Models.PaintSupportStyleUtil.ToLabel(
                Core.Models.PaintSupportStyleUtil.FromLabel(value));
            if (!SetField(ref _paintSupportType, v)) return;
            // Do not overwrite FILL PATTERN here — that is a separate saved setting.
            // Explicit Support Apply may soft-sync via ApplyPaintSupportTypeToSettings.
            // Tree vs Formbound changes how the bridge estimate counts (bed vs plane).
            RefreshSupportBridgeEstimate();
        }
    }

    public bool ShowPaintSupportTypePicker =>
        string.Equals(PaintModificationMode, "Support", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Soft-push the SELECTION support-type into FILL PATTERN only when the user
    /// is explicitly applying Formbound Support and FILL PATTERN is still None.
    /// Never clobbers a user-chosen Grid/None/etc. that was restored from a workspace.
    /// With Target Support Selections on, Formbound is paint-driven — leave dropdown alone.
    /// </summary>
    public void ApplyPaintSupportTypeToSettings()
    {
        if (AdditiveSettings is null) return;
        if (AdditiveSettings.LightningTargetSupportSelections)
            return; // paint marks drive Formbound; FILL PATTERN is independent

        bool supportMode = string.Equals(PaintModificationMode, "Support", StringComparison.OrdinalIgnoreCase);
        bool hasBridgePaint = AdditiveSettings.PaintMarks.Any(m =>
            m.Kind == Core.Models.PaintMarkKind.Bridge);
        if (!supportMode && !hasBridgePaint) return;

        var style = Core.Models.PaintSupportStyleUtil.FromLabel(PaintSupportType);
        if (Core.Models.PaintSupportStyleUtil.IsTree(style))
            return; // Tree is paint-only; leave FILL PATTERN alone.

        // Only seed Formbound when the user has not chosen another fill yet.
        var cur = AdditiveSettings.InfillPattern ?? "None";
        if (cur is not ("None" or "" or "Formbound Buttress" or "Formbound Bridge" or "Lightning Bridge"))
            return;

        AdditiveSettings.InfillPattern = Core.Models.PaintSupportStyleUtil.ToLabel(style);
    }

    /// <summary>Summary for ACTIVE SELECTION (e.g. layer numbers). Multi-select = group.</summary>
    public string PaintActiveSelectionSummary
    {
        get
        {
            if (PaintSelectionItems.Count == 0)
                return "Nothing selected";
            if (PaintSelectionItems.Count == 1)
                return PaintSelectionItems[0].Title;
            var layers = PaintSelectionItems.Select(i => i.LayerNumber).Distinct().OrderBy(n => n).ToList();
            string layerTxt = layers.Count <= 4
                ? string.Join(", ", layers)
                : $"{layers.First()}…{layers.Last()}";
            // Shift multi-select is one Apply group on the MODIFICATIONS panel.
            return $"Group · {PaintSelectionCount} paths · layers {layerTxt}";
        }
    }

    public RelayCommand ApplyPaintModificationCommand => _applyPaintMod ??= new RelayCommand(() =>
    {
        bool support = !string.Equals(PaintModificationMode, "Remove", StringComparison.OrdinalIgnoreCase);
        // Structural Support is a toolpath modifier, not a paint mark: Apply creates
        // a 2×4 pocket / cylinder wrap spec anchored at the selection instead.
        if (support && Core.Models.PaintSupportStyleUtil.FromLabel(PaintSupportType)
                == Core.Models.PaintSupportStyle.StructuralSupport)
        {
            OnAddStructuralSupportRequested?.Invoke();
            return;
        }
        if (support)
            ApplyPaintSupportTypeToSettings();
        OnPaintApplyRequested?.Invoke(support);
    }, () => HasPaintSelection);
    private RelayCommand? _applyPaintMod;

    /// <summary>Marquee rectangle (viewport logical px) while square-dragging.</summary>
    private bool _paintMarqueeVisible;
    public bool PaintMarqueeVisible { get => _paintMarqueeVisible; set => SetField(ref _paintMarqueeVisible, value); }
    private double _paintMarqueeX, _paintMarqueeY, _paintMarqueeW, _paintMarqueeH;
    public double PaintMarqueeX { get => _paintMarqueeX; set => SetField(ref _paintMarqueeX, value); }
    public double PaintMarqueeY { get => _paintMarqueeY; set => SetField(ref _paintMarqueeY, value); }
    public double PaintMarqueeW { get => _paintMarqueeW; set => SetField(ref _paintMarqueeW, value); }
    public double PaintMarqueeH { get => _paintMarqueeH; set => SetField(ref _paintMarqueeH, value); }

    /// <summary>Lasso polyline (viewport logical px) while freehand-dragging.</summary>
    private bool _paintLassoVisible;
    public bool PaintLassoVisible
    {
        get => _paintLassoVisible;
        set => SetField(ref _paintLassoVisible, value);
    }

    /// <summary>
    /// Bound to overlay <c>Polyline.Points</c>. Reassigned (not mutated) so Avalonia
    /// refreshes the stroke each sample while dragging.
    /// </summary>
    private Avalonia.Points _paintLassoPoints = new();
    public Avalonia.Points PaintLassoPoints
    {
        get => _paintLassoPoints;
        private set => SetField(ref _paintLassoPoints, value);
    }

    /// <summary>Replace lasso geometry from freehand samples (closes the loop for fill).</summary>
    public void SetPaintLassoPoints(IReadOnlyList<Avalonia.Point> samples)
    {
        var pts = new Avalonia.Points();
        for (int i = 0; i < samples.Count; i++)
            pts.Add(samples[i]);
        if (samples.Count >= 3)
            pts.Add(samples[0]);
        PaintLassoPoints = pts;
    }

    public void ClearPaintLassoPoints()
    {
        PaintLassoPoints = new Avalonia.Points();
    }

    /// <summary>View hooks: the viewport owns the selection list.</summary>
    internal Action? OnPaintDeselectRequested;
    internal Action<bool>? OnPaintApplyRequested;   // true = Support, false = Remove

    public RelayCommand DeselectPaintCommand => _deselectPaint ??= new RelayCommand(() =>
        OnPaintDeselectRequested?.Invoke());
    private RelayCommand? _deselectPaint;

    public RelayCommand ApplySupportSelectionCommand => _applySupportSel ??= new RelayCommand(() =>
    {
        ApplyPaintSupportTypeToSettings();
        OnPaintApplyRequested?.Invoke(true);
    });
    private RelayCommand? _applySupportSel;

    public RelayCommand ApplyRemoveSelectionCommand => _applyRemoveSel ??= new RelayCommand(() =>
        OnPaintApplyRequested?.Invoke(false));
    private RelayCommand? _applyRemoveSel;

    // ── CREATE MODIFICATION catalog (search + Offset path, …) ────────────────

    /// <summary>Full catalog of edit-mode operations (search filters this).</summary>
    public ObservableCollection<CreateModificationItem> CreateModificationCatalog { get; } = new();

    /// <summary>Search-filtered operations bound to the CREATE MODIFICATION list.</summary>
    public ObservableCollection<CreateModificationItem> FilteredCreateModifications { get; } = new();

    private string _createModificationSearch = "";
    /// <summary>Filter text for the CREATE MODIFICATION search bar.</summary>
    public string CreateModificationSearch
    {
        get => _createModificationSearch;
        set
        {
            if (!SetField(ref _createModificationSearch, value ?? "")) return;
            RefreshFilteredCreateModifications();
        }
    }

    private string? _selectedCreateModificationId;
    public string? SelectedCreateModificationId
    {
        get => _selectedCreateModificationId;
        set
        {
            if (!SetField(ref _selectedCreateModificationId, value)) return;
            foreach (var item in CreateModificationCatalog)
                item.IsSelected = string.Equals(item.Id, value, StringComparison.Ordinal);
            OnPropertyChanged(nameof(ShowOffsetPathSettings));
            OnPropertyChanged(nameof(ShowCreateModSettings));
            OnPropertyChanged(nameof(SelectedCreateModificationTitle));
            ApplyCreateModificationCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ShowCreateModSettings => !string.IsNullOrEmpty(SelectedCreateModificationId)
        && CreateModificationCatalog.Any(c =>
            c.Id == SelectedCreateModificationId && c.IsAvailable);

    public bool ShowOffsetPathSettings =>
        string.Equals(SelectedCreateModificationId, "offset", StringComparison.OrdinalIgnoreCase);

    public string SelectedCreateModificationTitle =>
        CreateModificationCatalog.FirstOrDefault(c => c.Id == SelectedCreateModificationId)?.Title
        ?? "OPERATION";

    // Offset path settings (AiBuild-style)
    private double _offsetDistanceMm = -1.0;
    public double OffsetDistanceMm
    {
        get => _offsetDistanceMm;
        set => SetField(ref _offsetDistanceMm, value);
    }

    public string[] OffsetJoinTypeOptions { get; } = ["Miter", "Round", "Square"];
    private string _offsetJoinType = "Miter";
    public string OffsetJoinType
    {
        get => _offsetJoinType;
        set => SetField(ref _offsetJoinType, value ?? "Miter");
    }

    public string[] OffsetModeOptions { get; } = ["Add offsets"];
    private string _offsetMode = "Add offsets";
    public string OffsetMode
    {
        get => _offsetMode;
        set => SetField(ref _offsetMode, value ?? "Add offsets");
    }

    private int _offsetCount = 1;
    public int OffsetCount
    {
        get => _offsetCount;
        set => SetField(ref _offsetCount, Math.Clamp(value, 1, 32));
    }

    public string[] OffsetSideOptions { get; } = ["Both", "Left", "Right"];
    private string _offsetSide = "Both";
    public string OffsetSide
    {
        get => _offsetSide;
        set => SetField(ref _offsetSide, value ?? "Both");
    }

    public RelayCommand<string> SelectCreateModificationCommand =>
        _selectCreateMod ??= new RelayCommand<string>(id =>
        {
            if (string.IsNullOrEmpty(id)) return;
            var item = CreateModificationCatalog.FirstOrDefault(c => c.Id == id);
            if (item is null || !item.IsAvailable) return;
            SelectedCreateModificationId =
                string.Equals(SelectedCreateModificationId, id, StringComparison.Ordinal)
                    ? null
                    : id;
        });
    private RelayCommand<string>? _selectCreateMod;

    public RelayCommand CancelCreateModificationCommand => _cancelCreateMod ??= new RelayCommand(() =>
        SelectedCreateModificationId = null);
    private RelayCommand? _cancelCreateMod;

    public RelayCommand ApplyCreateModificationCommand => _applyCreateMod ??= new RelayCommand(() =>
    {
        if (ShowOffsetPathSettings)
            OnApplyOffsetPathRequested?.Invoke();
    }, () => ShowOffsetPathSettings && HasPaintSelection);
    private RelayCommand? _applyCreateMod;

    public RelayCommand<string> NudgeOffsetCountCommand => _nudgeOffsetCount ??= new RelayCommand<string>(delta =>
    {
        if (!int.TryParse(delta, out int d)) return;
        OffsetCount = OffsetCount + d;
    });
    private RelayCommand<string>? _nudgeOffsetCount;

    /// <summary>Viewport applies Offset path to the current selection.</summary>
    internal Action? OnApplyOffsetPathRequested { get; set; }

    private void EnsureCreateModificationCatalog()
    {
        if (CreateModificationCatalog.Count > 0) return;
        CreateModificationCatalog.Add(new CreateModificationItem
        {
            Id = "offset",
            Title = "Offset path",
            Description = "Creates parallel copies of existing toolpaths.",
            Icon = "mdi-vector-polyline",
            IsAvailable = true,
        });
        CreateModificationCatalog.Add(new CreateModificationItem
        {
            Id = "chamfer",
            Title = "Chamfer",
            Description = "Bevel sharp corners on selected paths.",
            Icon = "mdi-angle-acute",
            IsAvailable = false,
        });
        CreateModificationCatalog.Add(new CreateModificationItem
        {
            Id = "clip-plane",
            Title = "Clip by plane",
            Description = "Trim paths with a cutting plane.",
            Icon = "mdi-scissors-cutting",
            IsAvailable = false,
        });
        CreateModificationCatalog.Add(new CreateModificationItem
        {
            Id = "clip-sketch",
            Title = "Clip with sketch",
            Description = "Trim paths using a sketch boundary.",
            Icon = "mdi-vector-curve",
            IsAvailable = false,
        });
        CreateModificationCatalog.Add(new CreateModificationItem
        {
            Id = "cut-point",
            Title = "Cut at point",
            Description = "Split a path at a chosen point.",
            Icon = "mdi-content-cut",
            IsAvailable = false,
        });
        RefreshFilteredCreateModifications();
    }

    private void RefreshFilteredCreateModifications()
    {
        EnsureCreateModificationCatalog();
        string q = (CreateModificationSearch ?? "").Trim();
        FilteredCreateModifications.Clear();
        foreach (var item in CreateModificationCatalog)
        {
            if (q.Length == 0
                || item.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredCreateModifications.Add(item);
        }
    }

    /// <summary>Re-slices NOW with the accumulated paint edits, keeping the edit
    /// menu open and auto-slice paused afterwards.</summary>
    public RelayCommand ReslicePaintCommand => _reslicePaint ??= new RelayCommand(() =>
    {
        AdditiveSettings?.BumpPaintStamp();
        // Must force a real bake — the old pause/unpause dance left edit mode paused
        // so ScheduleRealtimeSlice only set a pending flag and never started a slice.
        OnPaintResliceRequested?.Invoke();
    });
    private RelayCommand? _reslicePaint;

    /// <summary>ViewportView: force RunRealtimeSliceAsync ignoring edit-mode pause.</summary>
    internal Action? OnPaintResliceRequested { get; set; }

    private bool _isDevMode;

    /// <summary>When true, cell environment props (bed, stands, docks) can be picked and edited.</summary>
    public bool IsDevMode
    {
        get => _isDevMode;
        set
        {
            if (!SetField(ref _isDevMode, value)) return;
            OnDevModeChanged?.Invoke(value);
            SaveDevTransformCommand.RaiseCanExecuteChanged();
            SaveAllDevTransformsCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _isDevObjectSelected;

    /// <summary>True when a dev-mode environment object is selected.</summary>
    public bool IsDevObjectSelected
    {
        get => _isDevObjectSelected;
        set
        {
            if (SetField(ref _isDevObjectSelected, value))
                SaveDevTransformCommand.RaiseCanExecuteChanged();
        }
    }

    private string _devSelectedLabel = "";

    /// <summary>Display name of the selected dev object (e.g. stand or dock).</summary>
    public string DevSelectedLabel
    {
        get => _devSelectedLabel;
        set => SetField(ref _devSelectedLabel, value);
    }

    private bool _isPrintBedSelected;

    /// <summary>True when a rectangular print bed is selected in dev mode (N-menu grid sizing).</summary>
    public bool IsPrintBedSelected
    {
        get => _isPrintBedSelected;
        set => SetField(ref _isPrintBedSelected, value);
    }

    private double _bedGridWidth = 3000;
    private double _bedGridDepth = 3000;
    private bool _suppressBedGridCallback;

    /// <summary>Print-area grid extent along +X (mm). Editable when <see cref="IsPrintBedSelected"/>.</summary>
    public double BedGridWidth
    {
        get => _bedGridWidth;
        set
        {
            if (value < 1) value = 1;
            if (SetField(ref _bedGridWidth, Math.Round(value, 1)))
                FireBedGridSizeEdited();
        }
    }

    /// <summary>Print-area grid extent along +Y (mm). Editable when <see cref="IsPrintBedSelected"/>.</summary>
    public double BedGridDepth
    {
        get => _bedGridDepth;
        set
        {
            if (value < 1) value = 1;
            if (SetField(ref _bedGridDepth, Math.Round(value, 1)))
                FireBedGridSizeEdited();
        }
    }

    /// <summary>Invoked when <see cref="BedGridWidth"/> or <see cref="BedGridDepth"/> is edited.</summary>
    internal Action<double, double>? OnBedGridSizeEdited { get; set; }

    private void FireBedGridSizeEdited()
    {
        if (!_suppressBedGridCallback)
            OnBedGridSizeEdited?.Invoke(_bedGridWidth, _bedGridDepth);
    }

    /// <summary>Loads bed grid dimensions from the active cell (no callback fired).</summary>
    public void SyncBedGridSize(double width, double depth)
    {
        _suppressBedGridCallback = true;
        BedGridWidth = Math.Round(width, 1);
        BedGridDepth = Math.Round(depth, 1);
        _suppressBedGridCallback = false;
    }

    private bool _hasMeshSelected;

    /// <summary>True when a sliceable user mesh (not a toolpath or toolhead) is selected.</summary>
    public bool HasMeshSelected
    {
        get => _hasMeshSelected;
        set
        {
            if (SetField(ref _hasMeshSelected, value))
            {
                SliceCommand?.RaiseCanExecuteChanged();
                RecenterCommand?.RaiseCanExecuteChanged();
                ResetModelTransformUi();
            }
        }
    }

    private bool _canUngroup;

    /// <summary>True when the selection can be ungrouped (has child objects).</summary>
    public bool CanUngroup
    {
        get => _canUngroup;
        set
        {
            if (SetField(ref _canUngroup, value))
                UngroupCommand?.RaiseCanExecuteChanged();
        }
    }

    private bool _canExplode;

    /// <summary>True when the selection contains disconnected mesh shells to split apart.</summary>
    public bool CanExplode
    {
        get => _canExplode;
        set
        {
            if (SetField(ref _canExplode, value))
                ExplodeCommand?.RaiseCanExecuteChanged();
        }
    }

    private bool _canMeshCleanup;

    /// <summary>True when the selection contains triangle mesh geometry to repair.</summary>
    public bool CanMeshCleanup
    {
        get => _canMeshCleanup;
        set
        {
            if (SetField(ref _canMeshCleanup, value))
                MeshCleanupCommand?.RaiseCanExecuteChanged();
        }
    }

    private bool _canCutTool;

    /// <summary>True when the selection can be split with the Cut Tool.</summary>
    public bool CanCutTool
    {
        get => _canCutTool;
        set
        {
            if (SetField(ref _canCutTool, value))
                CutToolCommand?.RaiseCanExecuteChanged();
        }
    }

    private bool _isCutToolActive;
    private CutToolDialogViewModel? _cutToolSession;

    /// <summary>True while the interactive Cut Tool (ghost plane + gizmo) is open.</summary>
    public bool IsCutToolActive
    {
        get => _isCutToolActive;
        private set
        {
            if (SetField(ref _isCutToolActive, value))
            {
                OnPropertyChanged(nameof(ShowCutToolPanel));
                CutToolCommand?.RaiseCanExecuteChanged();
                CancelCutToolCommand?.RaiseCanExecuteChanged();
                PerformCutToolCommand?.RaiseCanExecuteChanged();
                CutToolNormalXCommand?.RaiseCanExecuteChanged();
                CutToolNormalYCommand?.RaiseCanExecuteChanged();
                CutToolNormalZCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Overlay panel visibility (same as <see cref="IsCutToolActive"/>).</summary>
    public bool ShowCutToolPanel => _isCutToolActive;

    /// <summary>Live cut-plane / connector parameters for the floating panel.</summary>
    public CutToolDialogViewModel? CutToolSession
    {
        get => _cutToolSession;
        private set => SetField(ref _cutToolSession, value);
    }

    public RelayCommand? CancelCutToolCommand { get; private set; }
    public RelayCommand? PerformCutToolCommand { get; private set; }
    public RelayCommand? CutToolNormalZCommand { get; private set; }
    public RelayCommand? CutToolNormalYCommand { get; private set; }
    public RelayCommand? CutToolNormalXCommand { get; private set; }

    internal Action? OnCancelCutToolRequested { get; set; }
    internal Action? OnPerformCutToolRequested { get; set; }

    /// <summary>Enter interactive cut-tool mode with a plane session (viewport wires gizmo).</summary>
    internal void BeginCutToolSession(CutToolDialogViewModel session)
    {
        CutToolSession = session;
        IsCutToolActive = true;
        // Prefer translate gizmo so the plane can be moved immediately.
        if (ActiveGizmoModeInternal == GizmoMode.None || ActiveGizmoModeInternal == GizmoMode.Scale)
            ActiveGizmoModeInternal = GizmoMode.Translate;
        NotifyRenderNeeded();
    }

    internal void EndCutToolSession()
    {
        CutToolSession = null;
        IsCutToolActive = false;
        NotifyRenderNeeded();
    }

    private bool _isToolpathSelected;

    /// <summary>True when the active toolpath node is the current selection.</summary>
    public bool IsToolpathSelected
    {
        get => _isToolpathSelected;
        set
        {
            if (SetField(ref _isToolpathSelected, value))
            {
                OnPropertyChanged(nameof(ShowSimTimeline));
                OnPropertyChanged(nameof(Lfam3WorkflowMargin));
                ExportKrlCommand?.RaiseCanExecuteChanged();
                SendToRobotCommand?.RaiseCanExecuteChanged();
                UpdateSliceCommand?.RaiseCanExecuteChanged();
                TogglePlaybackCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _canMergeToolpaths;

    /// <summary>True when two or more toolpaths are shift-selected in the viewport.</summary>
    public bool CanMergeToolpaths
    {
        get => _canMergeToolpaths;
        set
        {
            if (SetField(ref _canMergeToolpaths, value))
                MergeToolpathsCommand?.RaiseCanExecuteChanged();
        }
    }

    private bool _canMergeScans;
    private readonly HashSet<OutlinerItemViewModel> _selectedScanItems = new();
    private OutlinerItemViewModel? _scanSelectionAnchor;
    private OutlinerItemViewModel? _selectedOutlinerItem;

    /// <summary>The outliner row matching whatever's currently selected (in the outliner or the
    /// viewport) — null if nothing is. Drives the Modifiers panel's settings inspector.</summary>
    public OutlinerItemViewModel? SelectedOutlinerItem => _selectedOutlinerItem;

    /// <summary>True when two or more scans are multi-selected in the outliner.</summary>
    public bool CanMergeScans
    {
        get => _canMergeScans;
        private set
        {
            if (SetField(ref _canMergeScans, value))
            {
                MergeScansAsPointCloudCommand?.RaiseCanExecuteChanged();
                MergeScansAsMeshCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public int SelectedScanCount => _selectedScanItems.Count;

    internal IReadOnlyCollection<OutlinerItemViewModel> SelectedScanItems => _selectedScanItems;

    private bool _isMergedToolpathSelected;

    /// <summary>True when the selected toolpath was created by merging multiple toolpaths.</summary>
    public bool IsMergedToolpathSelected
    {
        get => _isMergedToolpathSelected;
        set => SetField(ref _isMergedToolpathSelected, value);
    }

    private double _mergedRetractionHeightMm;
    private bool _suppressMergedSettingsCb;

    /// <summary>Z-hop retraction height (mm) between merged toolpath segments.</summary>
    public double MergedRetractionHeightMm
    {
        get => _mergedRetractionHeightMm;
        set
        {
            if (SetField(ref _mergedRetractionHeightMm, value) && !_suppressMergedSettingsCb)
                OnMergedSettingsChanged?.Invoke();
        }
    }

    private double _mergedTravelSpeed = 120.0;

    /// <summary>Travel speed (mm/s) for connectors between merged toolpath segments.</summary>
    public double MergedTravelSpeed
    {
        get => _mergedTravelSpeed;
        set
        {
            if (SetField(ref _mergedTravelSpeed, value) && !_suppressMergedSettingsCb)
                OnMergedSettingsChanged?.Invoke();
        }
    }

    /// <summary>Syncs merged connector settings without triggering a re-merge.</summary>
    internal void SyncMergedSettingsDisplay(double retractionMm, double travelMmS)
    {
        _suppressMergedSettingsCb = true;
        MergedRetractionHeightMm = retractionMm;
        MergedTravelSpeed        = travelMmS;
        _suppressMergedSettingsCb = false;
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
    /// <summary>Exclusive end move index per layer (prefix sums) for O(log n) layer lookup.</summary>
    private int[]? _scrubLayerEnds;

    private int _toolpathScrubLowIndex;
    /// <summary>Lower bound of the layer window (edit mode): moves below this are
    /// hidden. 0 = show from the first layer. Clamped under the upper scrub.</summary>
    public int ToolpathScrubLowIndex
    {
        get => _toolpathScrubLowIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, Math.Max(0, ToolpathScrubIndex - 1));
            if (SetField(ref _toolpathScrubLowIndex, clamped))
            {
                OnPropertyChanged(nameof(ToolpathScrubLowLayerLabel));
                OnPropertyChanged(nameof(ToolpathScrubLayerLow));
                NotifyRenderNeeded();
            }
        }
    }

    /// <summary>Layer number (1-based) at the low handle for display.</summary>
    public string ToolpathScrubLowLayerLabel
    {
        get
        {
            if (_scrubLayerEnds is null || _scrubLayerEnds.Length == 0) return "1";
            int idx = 0;
            while (idx < _scrubLayerEnds.Length && _scrubLayerEnds[idx] <= _toolpathScrubLowIndex) idx++;
            return (idx + 1).ToString();
        }
    }

    // ── Layer-unit range (the edit-mode LAYERS window binds in layers, not moves) ──

    /// <summary>Total layers of the scrubbed toolpath (1 when none).</summary>
    public int ToolpathScrubLayerCount =>
        Math.Max(1, _scrubLayerEnds?.Length ?? ActiveScrubToolpath?.Layers.Count ?? 1);

    /// <summary>Upper bound in LAYER units (1-based). Maps to the move scrub
    /// (show moves through the end of this layer). Round-trips with the dual slider.</summary>
    public double ToolpathScrubLayerHigh
    {
        get
        {
            if (_scrubLayerEnds is null || _scrubLayerEnds.Length == 0) return ToolpathScrubLayerCount;
            return GetScrubLayerIndex() + 1;
        }
        set
        {
            if (_scrubLayerEnds is null || _scrubLayerEnds.Length == 0) return;
            int n = _scrubLayerEnds.Length;
            int layer = Math.Clamp((int)Math.Round(value), 1, n);
            // Keep at least one layer of window above the low handle.
            int lowLayer = (int)Math.Round(ToolpathScrubLayerLow);
            if (layer <= lowLayer) layer = Math.Min(n, lowLayer + 1);
            // Exclusive end of this layer = show all of its moves.
            int newIdx = _scrubLayerEnds[layer - 1];
            if (newIdx != _toolpathScrubIndex)
                ToolpathScrubIndex = newIdx;
            else
                OnPropertyChanged(nameof(ToolpathScrubLayerHigh));
            // Re-clamp low if high dropped under it (move index).
            if (_toolpathScrubLowIndex >= _toolpathScrubIndex)
            {
                int lo = Math.Max(0, layer - 2);
                ToolpathScrubLowIndex = lo < 0 ? 0 : (layer <= 1 ? 0 : _scrubLayerEnds[layer - 2]);
            }
        }
    }

    /// <summary>Lower bound in LAYER units (1-based). 1 = show from the first layer.</summary>
    public double ToolpathScrubLayerLow
    {
        get
        {
            if (_scrubLayerEnds is null || _scrubLayerEnds.Length == 0) return 1;
            // First layer whose exclusive end is still above the low move cut.
            int idx = 0;
            while (idx < _scrubLayerEnds.Length && _scrubLayerEnds[idx] <= _toolpathScrubLowIndex)
                idx++;
            return Math.Min(_scrubLayerEnds.Length, idx + 1);
        }
        set
        {
            if (_scrubLayerEnds is null || _scrubLayerEnds.Length == 0) return;
            int n = _scrubLayerEnds.Length;
            int layer = Math.Clamp((int)Math.Round(value), 1, n);
            int highLayer = (int)Math.Round(ToolpathScrubLayerHigh);
            if (layer >= highLayer) layer = Math.Max(1, highLayer - 1);
            // Start of this layer in move units (end of previous).
            int newLow = layer <= 1 ? 0 : _scrubLayerEnds[layer - 2];
            ToolpathScrubLowIndex = newLow;
        }
    }

    /// <summary>
    /// True only while <see cref="ResetScrubIndex"/> is swapping in a new path. Blocks write-backs
    /// from bound controls during the window where the layer table, the max and the index describe
    /// different paths — see the long comment in that method.
    /// </summary>
    private bool _scrubResetting;

    /// <summary>Current scrubber position (move index). Bound to the slider value.</summary>
    public int ToolpathScrubIndex
    {
        get => _toolpathScrubIndex;
        set
        {
            // A control echoing a half-swapped state back at us is not a user edit.
            if (_scrubResetting) return;
            int clamped = Math.Clamp(value, 0, Math.Max(0, _toolpathScrubMax));
            if (SetField(ref _toolpathScrubIndex, clamped))
            {
                // Keep the lower window bound strictly under the upper scrub.
                if (_toolpathScrubLowIndex >= _toolpathScrubIndex)
                {
                    _toolpathScrubLowIndex = Math.Max(0, _toolpathScrubIndex - 1);
                    OnPropertyChanged(nameof(ToolpathScrubLowIndex));
                    OnPropertyChanged(nameof(ToolpathScrubLayerLow));
                    OnPropertyChanged(nameof(ToolpathScrubLowLayerLabel));
                }
                OnPropertyChanged(nameof(ToolpathScrubLabel));
                OnPropertyChanged(nameof(ToolpathScrubLayerLabel));
                OnPropertyChanged(nameof(ToolpathScrubSpeedRpmLabel));
                OnPropertyChanged(nameof(ToolpathScrubLayerHigh));
                OnPropertyChanged(nameof(ToolpathScrubThumbOffsetY));
                OnPropertyChanged(nameof(ToolpathScrubFillHeight));
                // Keep the editable text box in sync unless we're already being
                // called from ToolpathScrubText's setter (avoids a re-entry loop).
                if (!_scrubSyncing && _toolpathScrubText != clamped.ToString())
                {
                    _scrubSyncing = true;
                    ToolpathScrubText = clamped.ToString();
                    _scrubSyncing = false;
                }
                // Pause playback if the user manually moves the scrubber.
                if (_isPlaying)
                {
                    _isPlaying = false;
                    OnPropertyChanged(nameof(IsPlaying));
                    OnPlaybackToggled?.Invoke(false);
                }
                // Drive IK when the user is actively scrubbing a toolpath.
                if (_isToolpathSelected || _isScrubSessionActive)
                    OnScrubIkRequested?.Invoke(clamped);
                // 2D slice HUD tracks the active layer.
                if (_isSlicePlaneViewerActive)
                    RefreshSlicePlaneStats();
                // Always repaint: the IK callback only repaints on a successful solve,
                // so without this the viewport freezes when scrubbing through
                // unreachable poses.
                NotifyRenderNeeded();
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
                OnPropertyChanged(nameof(ToolpathScrubLayerLabel));
                OnPropertyChanged(nameof(ToolpathScrubLayerHigh));
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
    /// Current layer (1-based) and total layers for the scrubbed toolpath.
    /// Empty when no toolpath / no layers.
    /// </summary>
    public string ToolpathScrubLayerLabel
    {
        get
        {
            int total = _scrubLayerEnds?.Length ?? ActiveScrubToolpath?.Layers.Count ?? 0;
            if (total <= 0) return string.Empty;
            int cur = GetScrubLayerIndex() + 1; // 1-based for humans
            return $"Layer {cur} / {total}";
        }
    }

    /// <summary>
    /// Live "speed · RPM" readout for the current scrub/playback move, matching
    /// what KRL export writes ($VEL.CP and $ANOUT[4]): print speed × per-move
    /// scale, and geometry RPM % × speed/height scales capped at 100 %.
    /// </summary>
    public string ToolpathScrubSpeedRpmLabel
    {
        get
        {
            if (AdditiveSettings is not { } add || ActiveScrubToolpath is not { } tp
                || _scrubLayerEnds is null || _scrubLayerEnds.Length == 0)
                return string.Empty;
            int li = GetScrubLayerIndex();
            if (li >= tp.Layers.Count) return string.Empty;
            var moves = tp.Layers[li].Moves;
            if (moves.Count == 0) return string.Empty;
            int start = li == 0 ? 0 : _scrubLayerEnds[li - 1];
            var mv = moves[Math.Clamp(_toolpathScrubIndex - start, 0, moves.Count - 1)];
            if (mv.Kind == MoveKind.Mill) return string.Empty;
            if (mv.Kind == MoveKind.Travel)
            {
                double tSpeed = mv.TravelSpeedMps is { } o ? o * 1000.0 : add.TravelSpeed;
                return $"{tSpeed:0} mm/s · RPM {KrlAnout.RpmIdlePercent:0}%";
            }
            double speed;
            float rpmScale;
            if (mv.IsWipe)
            {
                speed = add.WipeSpeed;
                rpmScale = mv.WipeRpmScale;
            }
            else
            {
                float sScale = Math.Max(mv.PrintSpeedScale, 1e-6f);
                rpmScale = sScale * Math.Max(mv.HeightScale, 1e-6f);
                if (mv.IsResumeRamp)
                {
                    sScale *= Math.Max(mv.ResumeSpeedScale, 1e-6f);
                    rpmScale *= Math.Max(mv.ResumeRpmScale, 1e-6f);
                }
                speed = add.PrintSpeed * sScale;
            }
            // An absolute demand (brim RPM) replaces the nominal-times-scale product outright —
            // no scale can express it, so reporting the scaled value here would contradict both
            // the RPM gradient and the exported program.
            float pct = mv.RpmPercentOverride is { } abs
                ? Math.Min(Math.Max(abs, 0f), 100f)
                : Math.Min(add.GetEffectiveExtrusionSpeedPercent() * Math.Max(rpmScale, 0f), 100f);
            return $"{speed:0} mm/s · RPM {pct:0}%";
        }
    }

    /// <summary>
    /// 0-based layer index for the current scrub move.
    /// <see cref="_toolpathScrubIndex"/> is treated as an exclusive end (show moves
    /// <c>[0, index)</c>), so when index equals a layer's exclusive end we report
    /// that layer — not the next one. Keeps the LAYERS dual-slider high handle from
    /// fighting TwoWay bindings and snapping when dragged.
    /// </summary>
    private int GetScrubLayerIndex()
    {
        if (_scrubLayerEnds is null || _scrubLayerEnds.Length == 0)
            return 0;
        int idx = Math.Clamp(_toolpathScrubIndex, 0, Math.Max(0, _toolpathScrubMax));
        int last = _scrubLayerEnds.Length - 1;
        if (idx <= 0) return 0;
        if (idx >= _scrubLayerEnds[last]) return last;
        // First layer whose exclusive end is ≥ idx.
        int lo = 0, hi = last;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_scrubLayerEnds[mid] >= idx) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// Step the toolpath view by one layer. <paramref name="delta"/> +1 = next layer
    /// (higher), −1 = previous layer (lower). Used by Preview ↑/↓ arrow keys.
    /// Returns true when the scrub position changed.
    /// </summary>
    public bool StepScrubLayer(int delta)
    {
        if (delta == 0) return false;
        if (ActiveScrubToolpath is not { Layers.Count: > 0 }) return false;
        // Ensure layer ends are available (scrub session may not have rebuilt yet).
        if (_scrubLayerEnds is null || _scrubLayerEnds.Length == 0)
            RebuildScrubLayerEnds(ActiveScrubToolpath);
        if (_scrubLayerEnds is null || _scrubLayerEnds.Length == 0) return false;

        int n = _scrubLayerEnds.Length;
        int cur = GetScrubLayerIndex(); // 0-based
        int next = Math.Clamp(cur + delta, 0, n - 1);
        if (next == cur && ToolpathScrubIndex == _scrubLayerEnds[next])
            return false;

        // Show through the end of the target layer (exclusive end move index).
        ToolpathScrubIndex = _scrubLayerEnds[next];
        return true;
    }

    /// <summary>
    /// Jump the timeline / LAYERS dual-slider so the active (high) handle is on
    /// <paramref name="layerIndex0Hi"/> (0-based). The low handle stays at layer 1
    /// so bed foundation and all layers below the selection remain visible —
    /// never raise the low handle to the selection (that hid layers 1…N−1 in 2D/3D).
    /// </summary>
    public void FocusScrubOnLayers(int layerIndex0Lo, int layerIndex0Hi)
    {
        if (ActiveScrubToolpath is not { Layers.Count: > 0 } tp) return;
        if (_scrubLayerEnds is null || _scrubLayerEnds.Length == 0)
            RebuildScrubLayerEnds(tp);
        if (_scrubLayerEnds is null || _scrubLayerEnds.Length == 0) return;

        int n = _scrubLayerEnds.Length;
        int hi = Math.Clamp(Math.Max(layerIndex0Lo, layerIndex0Hi), 0, n - 1);

        // Keep scrub session live so the dual-slider / timeline stays armed.
        if (!_isScrubSessionActive)
            IsScrubSessionActive = true;

        // High = end of focus layer (current slice / scrub position).
        // Low = always layer 1 (move 0) so tree foundation and early layers stay drawn.
        int newHigh = _scrubLayerEnds[hi];
        if (newHigh <= 0)
            newHigh = Math.Min(_toolpathScrubMax, 1);

        ToolpathScrubIndex = newHigh;
        ToolpathScrubLowIndex = 0;

        OnPropertyChanged(nameof(ToolpathScrubLayerHigh));
        OnPropertyChanged(nameof(ToolpathScrubLayerLow));
        OnPropertyChanged(nameof(ToolpathScrubLowLayerLabel));
        if (_isSlicePlaneViewerActive)
            RefreshSlicePlaneStats();
        NotifyRenderNeeded();
    }

    /// <summary>Jump scrub high handle to a single layer (0-based); low stays at bed.</summary>
    public void FocusScrubOnLayer(int layerIndex0) =>
        FocusScrubOnLayers(layerIndex0, layerIndex0);

    private void RebuildScrubLayerEnds(Toolpath? toolpath)
    {
        if (toolpath is null || toolpath.Layers.Count == 0)
        {
            _scrubLayerEnds = null;
        }
        else
        {
            var ends = new int[toolpath.Layers.Count];
            int acc = 0;
            for (int i = 0; i < toolpath.Layers.Count; i++)
            {
                acc += toolpath.Layers[i].Moves.Count;
                ends[i] = acc;
            }
            _scrubLayerEnds = ends;
        }
        // Dual-slider Maximum/High/Low bind these — must notify after every rebuild.
        OnPropertyChanged(nameof(ToolpathScrubLayerCount));
        OnPropertyChanged(nameof(ToolpathScrubLayerHigh));
        OnPropertyChanged(nameof(ToolpathScrubLayerLow));
        OnPropertyChanged(nameof(ToolpathScrubLayerLabel));
        OnPropertyChanged(nameof(ToolpathScrubLowLayerLabel));
    }

    /// <summary>
    /// Resets the scrubber to position 0 and records the active toolpath without
    /// firing <see cref="OnScrubIkRequested"/>. Use this for programmatic selection
    /// changes so the robot is not driven automatically when a new toolpath is picked.
    /// </summary>
    private bool _isScrubSessionActive;

    /// <summary>True while a toolpath scrub session is live — stays true when the user
    /// clicks the TCP to adjust a keyframe, so the timeline card never drops.</summary>
    public bool IsScrubSessionActive
    {
        get => _isScrubSessionActive;
        internal set
        {
            if (SetField(ref _isScrubSessionActive, value))
            {
                OnPropertyChanged(nameof(ShowSimTimeline));
                OnPropertyChanged(nameof(ShowPlaybackTimeline));
                ExportKrlCommand?.RaiseCanExecuteChanged();
                SendToRobotCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    // ── TCP keyframes (timeline-scoped TCP offsets, eased over move proximity) ──

    private IReadOnlyList<double> _scrubKeyframeMarkers = [];
    public IReadOnlyList<double> ScrubKeyframeMarkers
    {
        get => _scrubKeyframeMarkers;
        internal set => SetField(ref _scrubKeyframeMarkers, value);
    }

    private bool _hasTcpKeyframes;
    public bool HasTcpKeyframes
    {
        get => _hasTcpKeyframes;
        internal set => SetField(ref _hasTcpKeyframes, value);
    }

    private double _keyframeSmoothing = 150;
    /// <summary>Ease window in moves on each side of / between keyframes.</summary>
    public double KeyframeSmoothing
    {
        get => _keyframeSmoothing;
        set
        {
            if (SetField(ref _keyframeSmoothing, Math.Clamp(value, 5, 2000)))
                OnTcpKeyframeSmoothingChanged?.Invoke();
        }
    }

    internal Action? OnAddTcpKeyframeRequested { get; set; }
    internal Action? OnClearTcpKeyframesRequested { get; set; }
    internal Action? OnTcpKeyframeSmoothingChanged { get; set; }

    public RelayCommand AddTcpKeyframeCommand => _addTcpKeyframeCommand ??=
        new RelayCommand(() => OnAddTcpKeyframeRequested?.Invoke());
    private RelayCommand? _addTcpKeyframeCommand;

    public RelayCommand ClearTcpKeyframesCommand => _clearTcpKeyframesCommand ??=
        new RelayCommand(() => OnClearTcpKeyframesRequested?.Invoke());
    private RelayCommand? _clearTcpKeyframesCommand;

    /// <summary>Swaps the scrubbed toolpath (same move count) without resetting the position.</summary>
    internal void ReplaceScrubToolpathInPlace(Toolpath toolpath)
    {
        ActiveScrubToolpath = toolpath;
        RebuildScrubLayerEnds(toolpath);
        ExportKrlCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ToolpathScrubSpeedRpmLabel));
        if (_isSlicePlaneViewerActive)
            RefreshSlicePlaneStats();
    }

    /// <summary>
    /// Sets the scrub slider range for a toolpath.
    /// When <paramref name="preservePosition"/> is true (re-slice / update), keeps the
    /// current move index clamped to the new range so the timeline does not jump to
    /// the end. When false (first select / new toolpath), jumps to the end as before.
    /// Does not fire <see cref="OnScrubIkRequested"/> — call ScrubIk separately if needed.
    /// </summary>
    internal void ResetScrubIndex(int max, Toolpath? toolpath, bool preservePosition = false)
    {
        if (_isPlaying)
        {
            _isPlaying = false;
            OnPropertyChanged(nameof(IsPlaying));
            OnPlaybackToggled?.Invoke(false);
        }
        // Everything from here to the end of the method is one atomic swap as far as the bound
        // controls are concerned. Without the guard, RebuildScrubLayerEnds below raises property
        // changes while _scrubLayerEnds already describes the NEW path but _toolpathScrubMax still
        // holds the OLD path's total — and the layer-high slider's two-way binding writes back into
        // ToolpathScrubIndex in exactly that window. Traced live on a 50% scale: a full path at
        // 95,206/95,206 came back as 34,659 clamped against 95,206, i.e. a third of the way in.
        //
        // The damage lands in two visible places, which is why it looked like two unrelated bugs:
        // Body view draws the scrub window, so the part renders unfinished; and the robot is posed
        // at this index after a re-slice, so it drives to a pose nobody asked for.
        _scrubResetting = true;
        try
        {
        ActiveScrubToolpath = toolpath;
        RebuildScrubLayerEnds(toolpath);
        ExportKrlCommand?.RaiseCanExecuteChanged();
        UpdateSliceCommand?.RaiseCanExecuteChanged();

        int previous    = _toolpathScrubIndex;
        int previousMax = _toolpathScrubMax;
        _toolpathScrubMax = Math.Max(0, max);
        OnPropertyChanged(nameof(ToolpathScrubMax));
        OnPropertyChanged(nameof(ToolpathScrubMaxLabel));

        int index;
        if (toolpath is null || _toolpathScrubMax <= 0)
            index = 0;
        else if (preservePosition)
        {
            // Hold the same FRACTION of the path, not the same absolute move number. A resize
            // changes the move count wholesale — 95,206 → 34,659 on a 50% scale — so an absolute
            // index means something completely different afterwards. Shrinking, it clamped to the
            // end; GROWING, it stayed put: coming back from 25% to full size left the scrub on
            // move 10,433 of 95,206 and drew 11% of the part. Jeff: "a reset scale gave me the
            // wrong sized toolpath."
            //
            // This is the same rule that was reverted earlier today, and it is only correct now
            // because `previous` can be trusted: the _scrubResetting guard above stops a bound
            // control writing a new-path index over it mid-swap. Fed that corrupted value, this
            // arithmetic turned a full path into a third of one, which is what made it look like
            // the fraction idea itself was wrong. It was the input.
            double fraction = previousMax > 0 ? (double)previous / previousMax : 1d;
            index = (int)Math.Round(Math.Clamp(fraction, 0d, 1d) * _toolpathScrubMax,
                                    MidpointRounding.AwayFromZero);
            index = Math.Clamp(index, 0, _toolpathScrubMax);
            // Scrub index is an exclusive end: 0 → draw zero moves. Never preserve a
            // blank window when the path has content (common after re-arming edit scrub
            // with a stale default index of 0).
            if (index <= 0)
                index = _toolpathScrubMax;
        }
        else
            index = _toolpathScrubMax; // historical: land at end of path on first select

        _toolpathScrubIndex = index;
        _toolpathScrubText  = index.ToString();
        // Full stack on first select: low at bed, high at top layer.
        if (!preservePosition || toolpath is null || index <= 0)
            _toolpathScrubLowIndex = 0;
        else if (_toolpathScrubLowIndex >= _toolpathScrubIndex)
            _toolpathScrubLowIndex = Math.Max(0, _toolpathScrubIndex - 1);
        }
        finally { _scrubResetting = false; }

        // Notifications go out only now, with index, max and layer table all describing the same
        // path. A control writing back at this point writes back the right value.
        OnPropertyChanged(nameof(ToolpathScrubIndex));
        OnPropertyChanged(nameof(ToolpathScrubText));
        OnPropertyChanged(nameof(ToolpathScrubLabel));
        OnPropertyChanged(nameof(ToolpathScrubLayerLabel));
        OnPropertyChanged(nameof(ToolpathScrubLayerCount));
        OnPropertyChanged(nameof(ToolpathScrubLayerHigh));
        OnPropertyChanged(nameof(ToolpathScrubLayerLow));
        OnPropertyChanged(nameof(ToolpathScrubLowIndex));
        OnPropertyChanged(nameof(ToolpathScrubLowLayerLabel));
        OnPropertyChanged(nameof(ToolpathScrubThumbOffsetY));
        OnPropertyChanged(nameof(ToolpathScrubFillHeight));
        if (_isSlicePlaneViewerActive)
            RefreshSlicePlaneStats();
    }

    /// <summary>
    /// Updates the scrub index and slider UI without triggering IK — used during playback
    /// when joints are driven directly from pre-solved angles.
    /// </summary>
    internal void SetPlaybackIndex(int index)
    {
        if (!SetField(ref _toolpathScrubIndex, index)) return;
        OnPropertyChanged(nameof(ToolpathScrubLabel));
        OnPropertyChanged(nameof(ToolpathScrubLayerLabel));
        OnPropertyChanged(nameof(ToolpathScrubSpeedRpmLabel));
        OnPropertyChanged(nameof(ToolpathScrubThumbOffsetY));
        OnPropertyChanged(nameof(ToolpathScrubFillHeight));
        _scrubSyncing     = true;
        ToolpathScrubText = index.ToString();
        _scrubSyncing     = false;
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
            double trackLength  = ScrubSliderHeight - ScrubThumbSize;
            double normalised   = _toolpathScrubMax > 0
                ? (double)_toolpathScrubIndex / _toolpathScrubMax
                : 0.0;
            double thumbCentre  = ScrubBorderPadding + ScrubThumbSize / 2.0
                                + (1.0 - normalised) * trackLength;
            return thumbCentre - ScrubLabelHalfHeight;
        }
    }

    // -- Scrubber markers (unreachable = red, singularity = purple) ---------------

    private const double ScrubThumbWidth = 12.0;

    private double _scrubTrackPixelWidth = 400.0;
    public double ScrubTrackPixelWidth
    {
        get => _scrubTrackPixelWidth;
        set
        {
            if (Math.Abs(_scrubTrackPixelWidth - value) < 0.5) return;
            _scrubTrackPixelWidth = value;
            RecomputeScrubMarkers();
        }
    }

    private bool[] _scrubReachable = [];
    private bool[] _scrubSingular  = [];
    private bool[] _scrubCollision = [];

    private IReadOnlyList<double> _scrubUnreachableMarkers = [];
    public IReadOnlyList<double> ScrubUnreachableMarkers
    {
        get => _scrubUnreachableMarkers;
        private set => SetField(ref _scrubUnreachableMarkers, value);
    }

    private IReadOnlyList<double> _scrubSingularityMarkers = [];
    public IReadOnlyList<double> ScrubSingularityMarkers
    {
        get => _scrubSingularityMarkers;
        private set => SetField(ref _scrubSingularityMarkers, value);
    }

    private IReadOnlyList<double> _scrubCollisionMarkers = [];
    /// <summary>Digital-twin collision ticks (orange) — robot body vs env/self/material.</summary>
    public IReadOnlyList<double> ScrubCollisionMarkers
    {
        get => _scrubCollisionMarkers;
        private set => SetField(ref _scrubCollisionMarkers, value);
    }

    /// <summary>Timeline tick legend visibility (any validation markers present).</summary>
    public bool ShowScrubLegend => HasUnreachableMarkers || HasSingularityMarkers || HasCollisionMarkers;
    public bool HasUnreachableMarkers => _scrubUnreachableMarkers.Count > 0;
    public bool HasSingularityMarkers => _scrubSingularityMarkers.Count > 0;
    public bool HasCollisionMarkers   => _scrubCollisionMarkers.Count > 0;

    internal void SetScrubMarkers(bool[] reachable, bool[] singular, bool[]? collision = null)
    {
        _scrubReachable = reachable;
        _scrubSingular  = singular;
        _scrubCollision = collision ?? [];
        RecomputeScrubMarkers();
    }

    private (int Index, int InfL, int InfR)[] _scrubKeyframes = [];
    private int _selectedKeyframeIdx = -1;

    /// <summary>Sets the TCP keyframes (move index + per-side influence in moves) shown
    /// on the scrubber ticks and the interactive keyframe lane.</summary>
    internal void SetScrubKeyframes((int Index, int InfL, int InfR)[] keys, int selectedIdx = -1)
    {
        _scrubKeyframes      = keys;
        _selectedKeyframeIdx = selectedIdx;
        RecomputeScrubMarkers();
    }

    private IReadOnlyList<MassiveSlicer.Controls.KeyframeLaneItem> _keyframeLaneItems = [];
    public IReadOnlyList<MassiveSlicer.Controls.KeyframeLaneItem> KeyframeLaneItems
    {
        get => _keyframeLaneItems;
        private set => SetField(ref _keyframeLaneItems, value);
    }

    /// <summary>Wired by the viewport code-behind: keyframe diamond clicked (jump + select).</summary>
    /// <summary>Edit-mode toolbar: create a Structural Support anchored at the
    /// current point selection (handled by the viewport code-behind).</summary>
    // ── 2D slice viewer ghost layers ─────────────────────────────────────────
    private int _slicePlaneGhostLayers = 3;
    /// <summary>How many below-layers fade in under the active slice line (default 3).</summary>
    public int SlicePlaneGhostLayers
    {
        get => _slicePlaneGhostLayers;
        set
        {
            if (!SetField(ref _slicePlaneGhostLayers, Math.Clamp(value, 0, 10))) return;
            NotifyRenderNeeded();
        }
    }

    private bool _slicePlaneShowAllGhosts;
    /// <summary>Draw every layer below faintly (single pass) under the ghost band.</summary>
    public bool SlicePlaneShowAllGhosts
    {
        get => _slicePlaneShowAllGhosts;
        set
        {
            if (!SetField(ref _slicePlaneShowAllGhosts, value)) return;
            NotifyRenderNeeded();
        }
    }

    internal Action? OnAddStructuralSupportRequested { get; set; }

    /// <summary>Debug: run the viewport click-selection path at fractional coords
    /// (0-1) and return a gate-by-gate trace. Backs the `pick` console command.</summary>
    internal Func<double, double, string>? DebugPickAtViewport { get; set; }

    private RelayCommand? _addStructSupportCmd;
    public RelayCommand AddStructuralSupportCommand => _addStructSupportCmd ??=
        new RelayCommand(() => OnAddStructuralSupportRequested?.Invoke());

    internal Action<int>? OnKeyframeLaneClicked { get; set; }

    /// <summary>Wired by the viewport code-behind: influence tick dragged
    /// (keyIdx, isLeft, lanePixelX, commit).</summary>
    internal Action<int, bool, double, bool>? OnKeyframeInfluenceDragged { get; set; }

    /// <summary>Converts a lane/track pixel X to a move index (clamped).</summary>
    internal int ScrubIndexAtPixel(double x)
    {
        double denom = Math.Max(_scrubTrackPixelWidth - ScrubThumbWidth, 1.0);
        return (int)Math.Round(Math.Clamp((x - ScrubThumbWidth / 2.0) / denom, 0.0, 1.0) * _toolpathScrubMax);
    }

    /// <summary>Wired by the viewport code-behind: frame the camera on a flat move index
    /// (timeline validation-tick click).</summary>
    internal Action<int>? OnFrameMoveRequested { get; set; }

    /// <summary>Validation-tick click on the timeline: snap the scrubber to the marker's
    /// move and ask the viewport to frame the camera there.</summary>
    internal void JumpToScrubPixel(double px)
    {
        if (_toolpathScrubMax <= 0) return;
        ToolpathScrubIndex = ScrubIndexAtPixel(px);
        OnFrameMoveRequested?.Invoke(ToolpathScrubIndex);
    }

    private void RecomputeScrubMarkers()
    {
        int    max = _toolpathScrubMax;
        double w   = _scrubTrackPixelWidth;
        var unr = new List<double>();
        var sin = new List<double>();
        for (int i = 0; i < _scrubReachable.Length; i++)
        {
            double x = max > 0 ? ScrubThumbWidth / 2.0 + (double)i / max * (w - ScrubThumbWidth) - 0.5 : 0;
            if (!_scrubReachable[i]) unr.Add(x);
        }
        for (int i = 0; i < _scrubSingular.Length; i++)
        {
            double x = max > 0 ? ScrubThumbWidth / 2.0 + (double)i / max * (w - ScrubThumbWidth) - 0.5 : 0;
            if (_scrubSingular[i]) sin.Add(x);
        }
        var col = new List<double>();
        for (int i = 0; i < _scrubCollision.Length; i++)
        {
            double x = max > 0 ? ScrubThumbWidth / 2.0 + (double)i / max * (w - ScrubThumbWidth) - 0.5 : 0;
            if (_scrubCollision[i]) col.Add(x);
        }
        ScrubUnreachableMarkers = unr;
        ScrubSingularityMarkers = sin;
        ScrubCollisionMarkers   = col;
        OnPropertyChanged(nameof(ShowScrubLegend));
        OnPropertyChanged(nameof(HasUnreachableMarkers));
        OnPropertyChanged(nameof(HasSingularityMarkers));
        OnPropertyChanged(nameof(HasCollisionMarkers));

        var kf   = new List<double>();
        var lane = new List<MassiveSlicer.Controls.KeyframeLaneItem>();
        double Px(double i) => max > 0 ? ScrubThumbWidth / 2.0 + i / max * (w - ScrubThumbWidth) - 0.5 : 0;
        for (int j = 0; j < _scrubKeyframes.Length; j++)
        {
            var k = _scrubKeyframes[j];
            kf.Add(Px(k.Index));
            lane.Add(new MassiveSlicer.Controls.KeyframeLaneItem(
                j, Px(k.Index),
                Px(Math.Max(k.Index - k.InfL, 0)),
                Px(Math.Min(k.Index + k.InfR, Math.Max(max, 1))),
                j == _selectedKeyframeIdx));
        }
        ScrubKeyframeMarkers = kf;
        KeyframeLaneItems    = lane;
    }

    public RelayCommand FocusCommand                { get; }
    public RelayCommand DropToPlateCommand          { get; }
    public RelayCommand UngroupCommand              { get; }
    public RelayCommand ExplodeCommand              { get; }
    public RelayCommand MeshCleanupCommand          { get; }
    public RelayCommand CutToolCommand              { get; }
    public RelayCommand SaveViewCommand             { get; }
    public RelayCommand SaveDevTransformCommand     { get; }
    public RelayCommand SaveAllDevTransformsCommand { get; }
    public RelayCommand TogglePlaybackCommand       { get; }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetField(ref _isPlaying, value);
    }

    private bool _isValidating;
    /// <summary>True while the background IK validation pass is running for the selected toolpath.</summary>
    public bool IsValidating
    {
        get => _isValidating;
        set => SetField(ref _isValidating, value);
    }

    public string[] PlaybackSpeedOptions { get; } = ["25%", "50%", "100%", "200%", "400%"];

    private string _playbackSpeedOption = "100%";
    public string PlaybackSpeedOption
    {
        get => _playbackSpeedOption;
        set
        {
            if (!SetField(ref _playbackSpeedOption, value ?? "100%")) return;
            if (value is not null && value.EndsWith('%') &&
                double.TryParse(value[..^1], System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                // Notify before changing speed so the code-behind can reseed the
                // elapsed base at the current position (prevents position jump).
                if (_isPlaying) OnPlaybackSpeedChanging?.Invoke();
                _playbackSpeed = Math.Clamp(d, 1.0, 1000.0);
            }
        }
    }

    private double _playbackSpeed = 100.0;
    public double PlaybackSpeed => _playbackSpeed;

    /// <summary>
    /// Fired immediately before <see cref="PlaybackSpeed"/> changes while playing,
    /// so the code-behind can freeze the current simulated position as the new elapsed base.
    /// </summary>
    internal Action? OnPlaybackSpeedChanging { get; set; }

    /// <summary>Callback set by the viewport code-behind to start/stop playback.</summary>
    internal Action<bool>? OnPlaybackToggled { get; set; }

    /// <summary>Callback set by the viewport code-behind to perform focus-on-selection.</summary>
    internal Action? OnFocusRequested      { get; set; }
    /// <summary>Callback set by the viewport code-behind to drop the selection to the bed.</summary>
    internal Action? OnDropToPlateRequested { get; set; }

    // ── Model quick-transform (MODEL step card) ─────────────────────────────
    private double _modelScale = 1.0;
    private Vector3? _modelPivot;   // bottom-centre pivot, cached per selection

    /// <summary>Uniform scale multiplier for the selected model (relative to selection time).</summary>
    public double ModelScale
    {
        get => _modelScale;
        set
        {
            value = Math.Clamp(value, 0.05, 10.0);
            double ratio = value / _modelScale;
            if (!SetField(ref _modelScale, value)) return;
            if (Math.Abs(ratio - 1.0) > 1e-9) ScaleSelectedModel((float)ratio);
        }
    }

    private void ResetModelTransformUi()
    {
        _modelScale = 1.0;
        _modelPivot = null;
        OnPropertyChanged(nameof(ModelScale));
    }

    private SceneNode? SelectedUserMesh()
        => HasMeshSelected ? GetSelectedSceneNode?.Invoke() : null;

    private Vector3? ComputeWorldPivot(SceneNode node)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var n in node.SelfAndDescendants())
        {
            var positions = n.Mesh?.PickingData?.Positions ?? (n.PendingMesh?.Positions);
            if (positions is null) continue;
            var w = n.WorldTransform;
            foreach (var lp in positions)
            {
                var p = OpenTK.Mathematics.Vector3.TransformPosition(lp, w);
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }
        }
        if (min.X > max.X) return null;
        return new Vector3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, min.Z);
    }

    /// <summary>Full bounding-box centre (mid Z, unlike <see cref="ComputeWorldPivot"/>
    /// whose Z is the base). Used to spawn effector handles inside the model.</summary>
    private Vector3? ComputeWorldCenter(SceneNode node)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var n in node.SelfAndDescendants())
        {
            var positions = n.Mesh?.PickingData?.Positions ?? (n.PendingMesh?.Positions);
            if (positions is null) continue;
            var w = n.WorldTransform;
            foreach (var lp in positions)
            {
                var p = OpenTK.Mathematics.Vector3.TransformPosition(lp, w);
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }
        }
        if (min.X > max.X) return null;
        return (min + max) * 0.5f;
    }

    private void ApplyWorldTransformToSelected(Matrix4 worldOp, bool dropAfter)
    {
        var node = SelectedUserMesh();
        if (node is null) return;
        var parentWorld = node.Parent?.WorldTransform ?? Matrix4.Identity;
        node.LocalTransform = node.WorldTransform * worldOp * parentWorld.Inverted();
        if (dropAfter)
        {
            _modelPivot = null;
            OnDropToPlateRequested?.Invoke();
        }
        NotifyRenderNeeded();
        OnModelGeometryChanged?.Invoke();
    }

    private void ScaleSelectedModel(float ratio)
    {
        var node = SelectedUserMesh();
        if (node is null) return;
        _modelPivot ??= ComputeWorldPivot(node);
        if (_modelPivot is not { } pivot) return;
        var op = Matrix4.CreateTranslation(-pivot)
               * Matrix4.CreateScale(ratio)
               * Matrix4.CreateTranslation(pivot);
        ApplyWorldTransformToSelected(op, dropAfter: false);
    }

    private void RotateSelectedModel(Vector3 axis)
    {
        var node = SelectedUserMesh();
        if (node is null) return;
        var pivot = ComputeWorldPivot(node);
        if (pivot is not { } c) return;
        var centre = new Vector3(c.X, c.Y, c.Z);
        var op = Matrix4.CreateTranslation(-centre)
               * Matrix4.CreateFromAxisAngle(axis, MathF.PI / 2f)
               * Matrix4.CreateTranslation(centre);
        ApplyWorldTransformToSelected(op, dropAfter: true);
    }

    /// <summary>Rotates the selected model 90° about world X, then drops it to the plate.</summary>
    public RelayCommand RotateModelXCommand => _rotateModelXCommand ??=
        new RelayCommand(() => RotateSelectedModel(Vector3.UnitX));
    private RelayCommand? _rotateModelXCommand;

    /// <summary>Rotates the selected model 90° about world Y, then drops it to the plate.</summary>
    public RelayCommand RotateModelYCommand => _rotateModelYCommand ??=
        new RelayCommand(() => RotateSelectedModel(Vector3.UnitY));
    private RelayCommand? _rotateModelYCommand;

    /// <summary>Removes the selected model from the scene.</summary>
    public RelayCommand ClearModelCommand => _clearModelCommand ??=
        new RelayCommand(() =>
        {
            var node = SelectedUserMesh();
            if (node is not null) RequestDeleteNode(node);
        });
    private RelayCommand? _clearModelCommand;

    internal Action? OnRecenterRequested { get; set; }
    /// <summary>Callback set by the viewport code-behind to ungroup the selection.</summary>
    internal Action? OnUngroupRequested { get; set; }
    /// <summary>Callback set by the viewport code-behind to explode disconnected mesh shells.</summary>
    internal Action? OnExplodeRequested { get; set; }
    /// <summary>Callback set by the viewport code-behind to open mesh cleanup on the selection.</summary>
    internal Action? OnMeshCleanupRequested { get; set; }
    internal Action? OnCutToolRequested { get; set; }
    /// <summary>Callback set by the viewport code-behind to frame all scene objects in view.</summary>
    internal Action? OnFrameAllRequested    { get; set; }

    // ── View pie menu (Ctrl+Space) ──────────────────────────────────────────
    private bool _isViewPieOpen;
    /// <summary>True while the Ctrl+Space view pie menu is showing.</summary>
    public bool IsViewPieOpen
    {
        get => _isViewPieOpen;
        set { if (SetField(ref _isViewPieOpen, value)) NotifyRenderNeeded(); }
    }

    private double _viewPieX, _viewPieY;
    /// <summary>Pie menu centre (overlay coordinates, set from the pointer position).</summary>
    public double ViewPieX { get => _viewPieX; set => SetField(ref _viewPieX, value); }
    public double ViewPieY { get => _viewPieY; set => SetField(ref _viewPieY, value); }

    /// <summary>Applies a named camera preset (Top/Bottom/Left/Right/Front/Back/Iso/Frame).</summary>
    internal Action<string>? OnViewPresetRequested { get; set; }

    /// <summary>Pie menu selection: applies the preset and closes the pie.</summary>
    public RelayCommand<string> SelectViewPresetCommand => _selectViewPresetCommand ??=
        new RelayCommand<string>(name =>
        {
            IsViewPieOpen = false;
            if (name is not null) OnViewPresetRequested?.Invoke(name);
        });
    private RelayCommand<string>? _selectViewPresetCommand;

    // ── View mode (Body / Toolpath / Both) ─────────────────────────────────
    private string _viewMode = "Body";

    /// <summary>Viewport content mode: Body, Toolpath, Speed, RPM, Preview (mesh + paths).</summary>
    public string ViewMode
    {
        get => _viewMode;
        set
        {
            if (!SetField(ref _viewMode, value)) return;
            ApplyViewMode();
            ApplyViewDisplayProfile();
            if (value != "Toolpath") StopSimTimeline();
            // Each view owns where the arm sits, so changing view re-poses it. Ignored while the
            // robot is synced — the live machine outranks any of this.
            OnViewGovernedPoseChanged?.Invoke();
            OnPropertyChanged(nameof(IsToolpathViewActive));
            OnPropertyChanged(nameof(ShowSimTimeline));
            OnPropertyChanged(nameof(ShowPlaybackTimeline));
            OnPropertyChanged(nameof(ShowViewTags));
        }
    }

    public RelayCommand<string> SetViewModeCommand => _setViewModeCommand ??=
        new RelayCommand<string>(m => ViewMode = m ?? "Body");
    private RelayCommand<string>? _setViewModeCommand;

    // ── Simulate timeline (Toolpath view): sweep the whole path in 6 s ─────
    private const double SimDurationSeconds = 6.0;
    private double _simTimelinePercent = 100.0;
    private bool   _simPlaying;
    private long   _simLastTickMs;
    private Avalonia.Threading.DispatcherTimer? _simTimer;

    public bool IsToolpathViewActive => _viewMode == "Toolpath";

    /// <summary>The simplified bar is the Toolpath view's timeline.</summary>
    public bool ShowSimTimeline => IsToolpathViewActive;

    /// <summary>The full playback/keyframe timeline lives on the Preview view only.</summary>
    public bool ShowPlaybackTimeline => _isScrubSessionActive && _viewMode == "Preview";

    /// <summary>Timeline position, 0–100 %. 100 = full toolpath drawn.</summary>
    public double SimTimelinePercent
    {
        get => _simTimelinePercent;
        set
        {
            if (SetField(ref _simTimelinePercent, Math.Clamp(value, 0.0, 100.0)))
            {
                OnPropertyChanged(nameof(SimTimelineLabel));
                if (ShowSimTimeline)
                    OnSimScrubRequested?.Invoke(_simTimelinePercent / 100.0);
                // Camera keyframes drive the view only while the timeline is in
                // motion (play or video export) — manual scrubbing leaves the
                // camera alone so the user can compose the next keyframe.
                if (_simPlaying || _simRecording)
                    ApplySimCameraAt(_simTimelinePercent);
                NotifyRenderNeeded();
            }
        }
    }

    public string SimTimelineLabel => $"{_simTimelinePercent:0}%";

    // ── Sim-timeline camera keyframes ──────────────────────────────────────
    // Double-click the slider (or the camera-plus button) to pin the current
    // viewport camera at that timeline position. During play / video export the
    // camera eases between pins, so the recording flies the shot.
    private readonly List<(double Percent, CameraView View)> _simCameraKeys = [];

    public bool HasSimCameraKeyframes => _simCameraKeys.Count > 0;

    /// <summary>Normalized 0–1 marker positions for the timeline tick bar.</summary>
    public IReadOnlyList<double> SimCameraKeyframeMarkers
        => _simCameraKeys.Select(k => k.Percent / 100.0).ToList();

    public RelayCommand AddSimCameraKeyframeCommand => _addSimCameraKeyframe ??= new RelayCommand(() =>
    {
        if (GetCameraState?.Invoke() is not { } view) return;
        AddSimCameraKeyframe(_simTimelinePercent, view);
    });
    private RelayCommand? _addSimCameraKeyframe;

    public RelayCommand ClearSimCameraKeyframesCommand => _clearSimCameraKeyframes ??= new RelayCommand(() =>
    {
        _simCameraKeys.Clear();
        NotifySimCameraKeyframesChanged();
    });
    private RelayCommand? _clearSimCameraKeyframes;

    internal void AddSimCameraKeyframe(double percent, CameraView view)
    {
        percent = Math.Clamp(percent, 0.0, 100.0);
        // Re-keying near an existing pin replaces it (nudge-and-repin workflow).
        _simCameraKeys.RemoveAll(k => Math.Abs(k.Percent - percent) < 1.5);
        _simCameraKeys.Add((percent, view));
        _simCameraKeys.Sort((a, b) => a.Percent.CompareTo(b.Percent));
        NotifySimCameraKeyframesChanged();
    }

    private void NotifySimCameraKeyframesChanged()
    {
        OnPropertyChanged(nameof(HasSimCameraKeyframes));
        OnPropertyChanged(nameof(SimCameraKeyframeMarkers));
    }

    /// <summary>Workspace persistence: [percent, azimuth, elevation, radius, tx, ty, tz].</summary>
    internal List<double[]>? CaptureSimCameraKeyframes()
        => _simCameraKeys.Count == 0
            ? null
            : _simCameraKeys
                .Select(k => new[]
                {
                    k.Percent, k.View.Azimuth, k.View.Elevation, k.View.Radius,
                    k.View.TargetX, k.View.TargetY, k.View.TargetZ,
                })
                .ToList();

    internal void RestoreSimCameraKeyframes(List<double[]>? data)
    {
        _simCameraKeys.Clear();
        if (data is not null)
            foreach (var d in data)
            {
                if (d is not { Length: >= 7 }) continue;
                _simCameraKeys.Add((Math.Clamp(d[0], 0.0, 100.0), new CameraView
                {
                    Azimuth = (float)d[1], Elevation = (float)d[2], Radius = (float)d[3],
                    TargetX = (float)d[4], TargetY = (float)d[5], TargetZ = (float)d[6],
                }));
            }
        _simCameraKeys.Sort((a, b) => a.Percent.CompareTo(b.Percent));
        NotifySimCameraKeyframesChanged();
    }

    private void ApplySimCameraAt(double percent)
    {
        if (_simCameraKeys.Count == 0 || ApplyCameraState is null) return;
        CameraView view;
        if (percent <= _simCameraKeys[0].Percent || _simCameraKeys.Count == 1)
            view = _simCameraKeys[0].View;
        else if (percent >= _simCameraKeys[^1].Percent)
            view = _simCameraKeys[^1].View;
        else
        {
            int i = 0;
            while (i < _simCameraKeys.Count - 2 && percent >= _simCameraKeys[i + 1].Percent) i++;
            var (p0, a) = _simCameraKeys[i];
            var (p1, b) = _simCameraKeys[i + 1];
            float t = (float)Math.Clamp((percent - p0) / Math.Max(p1 - p0, 1e-6), 0.0, 1.0);
            t = t * t * (3f - 2f * t); // ease in/out per segment
            view = new CameraView
            {
                Azimuth   = LerpAngleDeg(a.Azimuth, b.Azimuth, t),
                Elevation = a.Elevation + (b.Elevation - a.Elevation) * t,
                Radius    = a.Radius + (b.Radius - a.Radius) * t,
                TargetX   = a.TargetX + (b.TargetX - a.TargetX) * t,
                TargetY   = a.TargetY + (b.TargetY - a.TargetY) * t,
                TargetZ   = a.TargetZ + (b.TargetZ - a.TargetZ) * t,
            };
        }
        ApplyCameraState(view);
    }

    /// <summary>Shortest-arc angle lerp in degrees (azimuth can wrap through ±180°).</summary>
    private static float LerpAngleDeg(float a, float b, float t)
    {
        float d = ((b - a + 180f) % 360f + 360f) % 360f - 180f;
        return a + d * t;
    }

    public bool SimPlaying
    {
        get => _simPlaying;
        private set { if (SetField(ref _simPlaying, value)) NotifyRenderNeeded(); }
    }

    /// <summary>Raised when the user selects the toolhead/TCP in the viewport
    /// (rising edge only) — used to guide attention to the orientation settings.</summary>
    internal Action? OnToolheadSelected { get; set; }

    /// <summary>Wired by the viewport code-behind: robot IK follow for the sim timeline.</summary>
    internal Action<double>? OnSimScrubRequested { get; set; }

    /// <summary>Wired by the viewport code-behind: record the 6 s simulation to a video.</summary>
    internal Action? OnSimVideoExportRequested { get; set; }

    private bool _simRecording;

    /// <summary>True while the simulation video is being captured/encoded.</summary>
    public bool SimRecording
    {
        get => _simRecording;
        internal set => SetField(ref _simRecording, value);
    }

    public RelayCommand SimExportVideoCommand => _simExportVideoCommand ??= new RelayCommand(() =>
    {
        if (_simRecording) return;
        StopSimTimeline();
        OnSimVideoExportRequested?.Invoke();
    });
    private RelayCommand? _simExportVideoCommand;

    /// <summary>
    /// Where the robot belongs for the view currently on screen — the same rule the renderer uses
    /// to decide how much of the path to draw, so the arm and the picture always agree.
    /// </summary>
    /// <remarks>
    /// A re-slice re-poses the arm on purpose (RunUpdateSliceAsync, and the pending-replace drain),
    /// and it used to pass <see cref="ToolpathScrubIndex"/> raw. That is the toolpath EDIT
    /// scrubber's position, so scaling, rotating, optimizing or rebuilding drove the arm back to a
    /// mid-print pose from a mode the user had left — in every view, every time. Jeff, 2026-08-04:
    /// "Scaling, rotating, optimizing, rebuilding path. Arm still stays in toolpath edit position."
    /// <para>
    /// Body carries no timeline, so it reports the end of the path: the same "no timeline means
    /// 100%" rule that governs what Body draws.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Raised when the view changes, so the viewport can move the arm to that view's position.
    /// </summary>
    internal Action? OnViewGovernedPoseChanged { get; set; }

    /// <summary>
    /// True when the live robot owns the arm's pose and nothing here may drive it. Sync outranks
    /// every view rule — driving IK calls <c>Desync()</c>, which would silently drop the machine
    /// connection just because someone clicked a view tab.
    /// </summary>
    internal bool RobotOwnsPose => Robot?.IsConnected == true;

    internal int ViewGovernedScrubIndex
    {
        get
        {
            float sim = SimRenderProgress;
            if (sim >= 0f)
                return Math.Clamp((int)Math.Round(sim * _toolpathScrubMax), 0, _toolpathScrubMax);
            if (_viewMode == "Body")
                return _toolpathScrubMax;
            return _toolpathScrubIndex;
        }
    }

    /// <summary>Drained by the GL loop: 0–1 while the sim timeline governs, −1 = off
    /// (also off while a selected toolpath's full playback card owns the scrub).</summary>
    internal float SimRenderProgress
    {
        get
        {
            if (_simRecording || ShowSimTimeline) return (float)(_simTimelinePercent / 100.0);
            // Edit mode + 2D slice use dual-slider / multi-pass windows — never the
            // sim-progress override (it was blanking multipass when nothing is selected).
            if (IsPaintEditOpen || _isSlicePlaneViewerActive) return -1f;
            if (_viewMode == "Preview" && _isScrubSessionActive && !_isToolpathSelected && _toolpathScrubMax > 0)
                return (float)_toolpathScrubIndex / _toolpathScrubMax;
            return -1f;
        }
    }

    public RelayCommand SimPlayPauseCommand => _simPlayPauseCommand ??= new RelayCommand(() =>
    {
        if (_simPlaying) { StopSimTimeline(); return; }
        if (_simTimelinePercent >= 100.0) SimTimelinePercent = 0.0;
        _simTimer ??= CreateSimTimer();
        _simLastTickMs = Environment.TickCount64;
        SimPlaying = true;
        _simTimer.Start();
    });
    private RelayCommand? _simPlayPauseCommand;

    public RelayCommand SimResetCommand => _simResetCommand ??= new RelayCommand(() =>
    {
        StopSimTimeline();
        SimTimelinePercent = 0.0;
    });
    private RelayCommand? _simResetCommand;

    private Avalonia.Threading.DispatcherTimer CreateSimTimer()
    {
        var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        t.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            double dt = (now - _simLastTickMs) / 1000.0;
            _simLastTickMs = now;
            SimTimelinePercent = _simTimelinePercent + dt / SimDurationSeconds * 100.0;
            if (_simTimelinePercent >= 100.0) StopSimTimeline();
        };
        return t;
    }

    private void StopSimTimeline()
    {
        _simTimer?.Stop();
        SimPlaying = false;
    }

    /// <summary>Extrude-line colour mode implied by the view mode (drained by the GL loop).</summary>
    public MassiveSlicer.Viewport.Rendering.ToolpathColorMode ToolpathColorMode => _viewMode switch
    {
        "Speed" => MassiveSlicer.Viewport.Rendering.ToolpathColorMode.Speed,
        "RPM"   => MassiveSlicer.Viewport.Rendering.ToolpathColorMode.Rpm,
        "Thermal" => MassiveSlicer.Viewport.Rendering.ToolpathColorMode.Thermal,
        _       => MassiveSlicer.Viewport.Rendering.ToolpathColorMode.Normal,
    };

    /// <summary>Applies the view mode to every user model and its toolpath children.</summary>
    internal void ApplyViewMode()
    {
        bool showBody = _viewMode == "Body";
        bool showPath = _viewMode != "Body";

        // Per-mode toolpath render presets (users can still override in VISIBILITY):
        // Preview = printed-part look (bead surface only); Speed/RPM = clean gradient
        // lines; Toolpath = classic extrusion + travel lines.
        switch (_viewMode)
        {
            case "Preview":
                ShowBead = true;  ShowExtrusionMoves = false; ShowTravelMoves = false; ShowSeam = false;
                ShowWipeMoves = false;
                break;
            case "Speed":
            case "RPM":
            case "Thermal":
                ShowBead = false; ShowExtrusionMoves = true;  ShowTravelMoves = false;
                ShowWipeMoves = true;
                break;
            case "Toolpath":
                ShowBead = false; ShowExtrusionMoves = true;  ShowTravelMoves = true;
                ShowWipeMoves = true;
                break;
        }
        foreach (var item in EnumerateUserModelItems().ToList())
        {
            if (item.Visible != showBody) item.Visible = showBody;
            foreach (var child in item.Children)
                if (child.IsToolpath && child.Visible != showPath)
                    child.Visible = showPath;
        }
        NotifyRenderNeeded();
    }

    /// <summary>Raised whenever model geometry/placement changes (import, scale, rotate) —
    /// drives the realtime re-slice.</summary>
    internal Action? OnModelGeometryChanged { get; set; }

    private bool _realtimeSlicingPaused;

    /// <summary>Holds off realtime re-slicing while the user batches up changes;
    /// releasing the pause runs one re-slice if anything changed meanwhile.</summary>
    public bool RealtimeSlicingPaused
    {
        get => _realtimeSlicingPaused;
        set => SetField(ref _realtimeSlicingPaused, value);
    }

    // ── Per-view display profiles ───────────────────────────────────────────
    // Each view pill (Body/Toolpath/Speed/RPM/Preview) keeps its own viewport
    // display settings; changing a tracked setting saves into the active view's
    // profile, and switching views applies that view's profile.

    /// <summary>Display settings remembered per view mode.</summary>
    public sealed class ViewDisplayProfile
    {
        public bool ShowGrid { get; set; } = true;
        public bool ShowAxes { get; set; } = true;
        public bool ShowBedGrid { get; set; } = true;
        public bool ShowContactShadows { get; set; } = true;
        public bool ShowTcpFrame { get; set; } = true;
        public bool CavityEnabled { get; set; }
        public bool DarkBackground { get; set; }
        public string ShaderMode { get; set; } = "Standard";
        public float BackdropOpacity { get; set; } = 1f;
        public float BackdropBlur { get; set; } = 2.5f;
        public float ToolpathLineOpacity { get; set; } = 1f;

        /// <summary>Null = never chosen for this view; falls back to the per-view default
        /// (on in RPM). Nullable so profiles saved before this setting existed don't
        /// deserialize to false and silently turn the highlight off.</summary>
        public bool? ShowRpmOverLimit { get; set; }
    }

    private static readonly string[] ViewModeNames = ["Body", "Toolpath", "Speed", "RPM", "Thermal", "Preview"];
    private readonly Dictionary<string, ViewDisplayProfile> _viewProfiles = BuildDefaultProfiles();
    private bool _applyingViewProfile;

    private static Dictionary<string, ViewDisplayProfile> BuildDefaultProfiles()
    {
        var d = new Dictionary<string, ViewDisplayProfile>();
        foreach (var m in ViewModeNames)
        {
            bool lineView = m is "Toolpath" or "Speed" or "RPM" or "Thermal";
            d[m] = lineView
                ? new ViewDisplayProfile
                {
                    ShowGrid = false, ShowAxes = false, ShowBedGrid = false,
                    ShowContactShadows = false, CavityEnabled = false,
                    DarkBackground = true, ShaderMode = "MatteBlack",
                    BackdropOpacity = 0.15f,
                }
                : new ViewDisplayProfile();
        }
        return d;
    }

    private bool _darkViewportBackground;
    /// <summary>Flat near-black viewport background (per-view profile setting).</summary>
    public bool DarkViewportBackground
    {
        get => _darkViewportBackground;
        set { if (SetField(ref _darkViewportBackground, value)) NotifyRenderNeeded(); }
    }

    private static readonly HashSet<string> ProfileTrackedProps =
    [
        nameof(ShowGrid), nameof(ShowAxes), nameof(ShowBedGrid),
        nameof(ShowContactShadows), nameof(ShowTcpFrame), nameof(CavityEnabled),
        nameof(DarkViewportBackground), nameof(ActiveShaderMode),
        nameof(BackdropOpacity), nameof(BackdropBlur), nameof(ToolpathLineOpacity),
        nameof(ShowRpmOverLimit),
    ];

    /// <summary>
    /// Profile key currently driving the display: edit mode borrows the
    /// Toolpath view's profile (dark line-view look) regardless of the pill.
    /// </summary>
    private string EffectiveProfileKey => IsPaintEditOpen ? "Toolpath" : _viewMode;

    /// <summary>Call once from the constructor: saves tracked changes into the active profile.</summary>
    private void WireViewProfileTracking()
    {
        PropertyChanged += (_, e) =>
        {
            if (_applyingViewProfile || e.PropertyName is not { } name || !ProfileTrackedProps.Contains(name))
                return;
            // Edit mode dims lines programmatically — don't bake that into the profile.
            if (IsPaintEditOpen && name == nameof(ToolpathLineOpacity)) return;
            if (!_viewProfiles.TryGetValue(EffectiveProfileKey, out var prof)) return;
            prof.ShowGrid           = ShowGrid;
            prof.ShowAxes           = ShowAxes;
            prof.ShowBedGrid        = ShowBedGrid;
            prof.ShowContactShadows = ShowContactShadows;
            prof.ShowTcpFrame       = ShowTcpFrame;
            prof.CavityEnabled      = CavityEnabled;
            prof.DarkBackground     = DarkViewportBackground;
            prof.ShaderMode         = ActiveShaderMode.ToString();
            prof.BackdropOpacity    = BackdropOpacity;
            prof.BackdropBlur       = BackdropBlur;
            prof.ToolpathLineOpacity = ToolpathLineOpacity;
            prof.ShowRpmOverLimit   = ShowRpmOverLimit;
        };
    }

    /// <summary>Applies the active view mode's display profile to the viewport.</summary>
    internal void ApplyViewDisplayProfile()
    {
        if (!_viewProfiles.TryGetValue(EffectiveProfileKey, out var prof)) return;
        _applyingViewProfile = true;
        try
        {
            ShowGrid           = prof.ShowGrid;
            ShowAxes           = prof.ShowAxes;
            ShowBedGrid        = prof.ShowBedGrid;
            ShowContactShadows = prof.ShowContactShadows;
            ShowTcpFrame       = prof.ShowTcpFrame;
            CavityEnabled      = prof.CavityEnabled;
            DarkViewportBackground = prof.DarkBackground;
            BackdropOpacity    = prof.BackdropOpacity;
            BackdropBlur       = prof.BackdropBlur;
            ToolpathLineOpacity = prof.ToolpathLineOpacity;
            // The RPM view is where an over-limit stretch matters most, so it starts on
            // there and off elsewhere — until the operator ticks it for a view themselves.
            ShowRpmOverLimit   = prof.ShowRpmOverLimit ?? (EffectiveProfileKey == "RPM");
            if (Enum.TryParse<ShaderMode>(prof.ShaderMode, out var sm))
                ActiveShaderMode = sm;
        }
        finally { _applyingViewProfile = false; }
        NotifyRenderNeeded();
    }

    /// <summary>Round-trips all profiles as JSON for app preferences.</summary>
    public string SerializeViewProfiles()
        => System.Text.Json.JsonSerializer.Serialize(_viewProfiles);

    public void LoadViewProfiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { ApplyViewDisplayProfile(); return; }
        try
        {
            var loaded = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, ViewDisplayProfile>>(json);
            if (loaded is not null)
                foreach (var (k, v) in loaded)
                    if (_viewProfiles.ContainsKey(k)) _viewProfiles[k] = v;
        }
        catch { /* corrupt prefs — keep defaults */ }
        ApplyViewDisplayProfile();
    }

    // ── Live effector handles (glowing draggable points, up to 3) ──────────
    private readonly SceneNode?[] _effectorNodes = new SceneNode?[3];
    private readonly OutlinerItemViewModel?[] _effectorItems = new OutlinerItemViewModel?[3];
    private readonly bool[] _effectorEyeBeforeDisable = [true, true, true];

    /// <summary>True when the node is (or is inside) a spawned effector handle.</summary>
    public bool IsEffectorNode(SceneNode? node)
    {
        if (node is null) return false;
        foreach (var en in _effectorNodes)
            if (en is not null && (en == node || en.SelfAndDescendants().Any(n => n == node)))
                return true;
        return false;
    }

    /// <summary>
    /// Master-toggle sync: while "Live effector" is off, spawned handles are hidden
    /// (they have no effect on the slice, so showing them was misleading). Re-enabling
    /// restores each handle's own outliner eye state.
    /// </summary>
    public void SetEffectorHandlesEnabled(bool enabled)
    {
        for (int i = 0; i < _effectorNodes.Length; i++)
        {
            if (_effectorNodes[i] is not { } node) continue;
            if (!enabled)
            {
                _effectorEyeBeforeDisable[i] = node.Visible;
                node.Visible = false;
            }
            else
            {
                node.Visible = _effectorEyeBeforeDisable[i];
            }
            _effectorItems[i]?.NotifyVisibilityFromScene();
        }
        NotifyRenderNeeded();
    }

    public bool EffectorPoint1Active => _effectorNodes[0] is not null;
    public bool EffectorPoint2Active => _effectorNodes[1] is not null;
    public bool EffectorPoint3Active => _effectorNodes[2] is not null;

    public RelayCommand<string> ToggleEffectorPointCommand => _toggleEffectorPointCommand ??=
        new RelayCommand<string>(idxStr =>
        {
            if (!int.TryParse(idxStr, out int n) || n < 1 || n > 3) return;
            int i = n - 1;
            if (_effectorNodes[i] is { } existing)
            {
                if (_effectorItems[i] is { } it) OutlinerItems.Remove(it);
                PendingRemoveNodes.Enqueue(existing);
                _effectorNodes[i] = null;
                _effectorItems[i] = null;
            }
            else
            {
                // Purge stale rows with this effector's name (restored from an old
                // workspace; not registered in the slots) so we never show duplicates.
                foreach (var stale in OutlinerItems
                             .Where(it => it.Node.Name == $"Effector {n}").ToList())
                {
                    OutlinerItems.Remove(stale);
                    PendingRemoveNodes.Enqueue(stale.Node);
                }

                var node = BuildEffectorNode(n);
                PendingNodes.Enqueue(node);
                var item = new OutlinerItemViewModel(node, NotifyRenderNeeded, it =>
                {
                    OutlinerItems.Remove(it);
                    PendingRemoveNodes.Enqueue(it.Node);
                    int slot = Array.IndexOf(_effectorNodes, it.Node);
                    if (slot >= 0) { _effectorNodes[slot] = null; _effectorItems[slot] = null; NotifyEffectorPoints(); }
                    NotifyRenderNeeded();
                }, null, $"Effector {n}", canDelete: true)
                { IsEffector = true };
                OutlinerItems.Add(item);
                _effectorNodes[i] = node;
                _effectorItems[i] = item;
            }
            NotifyEffectorPoints();
            OnModelGeometryChanged?.Invoke();
            NotifyRenderNeeded();   // repaint now — handles otherwise appear only on next camera move
        });
    private RelayCommand<string>? _toggleEffectorPointCommand;

    void NotifyEffectorPoints()
    {
        OnPropertyChanged(nameof(EffectorPoint1Active));
        OnPropertyChanged(nameof(EffectorPoint2Active));
        OnPropertyChanged(nameof(EffectorPoint3Active));
    }

    /// <summary>World positions of the active effector handles (for the slicer).</summary>
    internal List<System.Numerics.Vector3> GetActiveEffectorPositions()
    {
        var list = new List<System.Numerics.Vector3>();
        foreach (var node in _effectorNodes)
        {
            if (node is null) continue;
            var w = node.WorldTransform;
            list.Add(new System.Numerics.Vector3(w.M41, w.M42, w.M43));
        }
        return list;
    }

    private static (OpenTK.Mathematics.Vector3[] Pos, OpenTK.Mathematics.Vector3[] Nrm, uint[] Idx)
        BuildSphereGeometry(float r, int seg = 18, int rings = 12)
    {
        var positions = new List<OpenTK.Mathematics.Vector3>();
        var normals   = new List<OpenTK.Mathematics.Vector3>();
        var indices   = new List<uint>();
        for (int ri = 0; ri <= rings; ri++)
        {
            float v = ri / (float)rings * MathF.PI;
            for (int si = 0; si <= seg; si++)
            {
                float u = si / (float)seg * 2f * MathF.PI;
                var nrm = new OpenTK.Mathematics.Vector3(
                    MathF.Sin(v) * MathF.Cos(u), MathF.Sin(v) * MathF.Sin(u), MathF.Cos(v));
                positions.Add(nrm * r);
                normals.Add(nrm);
            }
        }
        for (int ri = 0; ri < rings; ri++)
            for (int si = 0; si < seg; si++)
            {
                uint a = (uint)(ri * (seg + 1) + si);
                uint b = (uint)(a + seg + 1);
                indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        return (positions.ToArray(), normals.ToArray(), indices.ToArray());
    }

    private static readonly OpenTK.Mathematics.Vector3 EffectorLime = new(0.64f, 0.87f, 0.22f);

    /// <summary>Glowing lime sphere handle, spawned near the active model (or bed centre).</summary>
    private SceneNode BuildEffectorNode(int number)
    {
        var core = BuildSphereGeometry(40f);
        var mesh = new MeshData(core.Pos, core.Nrm, core.Idx,
            $"Effector {number}",
            new OpenTK.Mathematics.Vector4(EffectorLime.X, EffectorLime.Y, EffectorLime.Z, 1f),
            0f, 1f, uvs: null, tangents: null,
            material: new MaterialData
            {
                BaseColorFactor = new OpenTK.Mathematics.Vector4(EffectorLime.X, EffectorLime.Y, EffectorLime.Z, 1f),
                MetallicFactor  = 0f,
                RoughnessFactor = 1f,
                // Self-lit so the handle reads as a luminous point in every shader/view.
                EmissiveFactor  = EffectorLime * 1.1f,
            });

        // Spawn at the model's bounding-box centre (even when the body is hidden by a
        // line view), else above the print bed's centre, else a bed-ish default.
        var spawn = new OpenTK.Mathematics.Vector3(0f, 0f, 600f);
        if (ComputeBedCenter() is { } bedCentre)
            spawn = bedCentre;
        else if ((ResolveActivePrintObjectItem()
                  ?? EnumerateUserModelItems().FirstOrDefault(i => i.Visible)) is { } model
                 && ComputeWorldCenter(model.Node) is { } centre)
            spawn = centre;

        var node = new SceneNode
        {
            Name            = $"Effector {number}",
            PendingMesh     = mesh,
            Selectable      = true,
            KeepOwnMaterial = true,
            LocalTransform  = OpenTK.Mathematics.Matrix4.CreateTranslation(spawn),
        };

        // Soft glow shell visualising the influence radius (Range slider, mm).
        var shell = BuildSphereGeometry(1f, seg: 36, rings: 24);
        var glowMesh = new MeshData(shell.Pos, shell.Nrm, shell.Idx,
            $"Effector {number} Range",
            new OpenTK.Mathematics.Vector4(EffectorLime.X, EffectorLime.Y, EffectorLime.Z, 0.10f),
            0f, 1f, uvs: null, tangents: null,
            material: new MaterialData
            {
                BaseColorFactor = new OpenTK.Mathematics.Vector4(EffectorLime.X, EffectorLime.Y, EffectorLime.Z, 0.10f),
                MetallicFactor  = 0f,
                RoughnessFactor = 1f,
                EmissiveFactor  = EffectorLime * 0.35f,
                AlphaMode       = MassiveSlicer.Viewport.Scene.AlphaMode.Blend,
            });
        float range = (float)(AdditiveSettings?.EffectorRange ?? 400.0);
        node.AddChild(new SceneNode
        {
            Name            = $"Effector {number} Range",
            PendingMesh     = glowMesh,
            Selectable      = false,
            PickIgnore      = true,
            TranslucentPass = true,
            KeepOwnMaterial = true,
            LocalTransform  = OpenTK.Mathematics.Matrix4.CreateScale(MathF.Max(range, 1f)),
        });

        // Inner shell marking the full-effect core (60% of the radius) — in Erase mode
        // the wall must pass through this sphere to go completely flat; outside it only
        // the blend band applies. Slightly stronger tint so it reads against the range.
        var coreShell = BuildSphereGeometry(1f, seg: 36, rings: 24);
        var coreMesh = new MeshData(coreShell.Pos, coreShell.Nrm, coreShell.Idx,
            $"Effector {number} Core",
            new OpenTK.Mathematics.Vector4(EffectorLime.X, EffectorLime.Y, EffectorLime.Z, 0.16f),
            0f, 1f, uvs: null, tangents: null,
            material: new MaterialData
            {
                BaseColorFactor = new OpenTK.Mathematics.Vector4(EffectorLime.X, EffectorLime.Y, EffectorLime.Z, 0.16f),
                MetallicFactor  = 0f,
                RoughnessFactor = 1f,
                EmissiveFactor  = EffectorLime * 0.5f,
                AlphaMode       = MassiveSlicer.Viewport.Scene.AlphaMode.Blend,
            });
        node.AddChild(new SceneNode
        {
            Name            = $"Effector {number} Core",
            PendingMesh     = coreMesh,
            Selectable      = false,
            PickIgnore      = true,
            TranslucentPass = true,
            KeepOwnMaterial = true,
            LocalTransform  = OpenTK.Mathematics.Matrix4.CreateScale(MathF.Max(range, 1f) * EffectorCoreFraction),
        });
        return node;
    }

    /// <summary>Centre of the print bed, hovered 400 mm above its surface.</summary>
    private OpenTK.Mathematics.Vector3? ComputeBedCenter()
    {
        var bedItem = _cellEnvOutlinerItems.FirstOrDefault(i => i.Name == "Print Bed");
        if (bedItem?.Node is { } bed && ComputeWorldCenter(bed) is { } c)
            return new OpenTK.Mathematics.Vector3(c.X, c.Y, c.Z + 400f);
        return null;
    }

    /// <summary>Fraction of the influence radius with full effect (matches PatternEffect's erase core).</summary>
    internal const float EffectorCoreFraction = 0.6f;

    /// <summary>Rescales every effector's glow shells (range + core) to the current Range (mm).</summary>
    internal void UpdateEffectorRangeIndicators(float rangeMm)
    {
        float scale = MathF.Max(rangeMm, 1f);
        foreach (var node in _effectorNodes)
        {
            if (node is null) continue;
            foreach (var child in node.Children)
                if (child.TranslucentPass)
                    child.LocalTransform = OpenTK.Mathematics.Matrix4.CreateScale(
                        child.Name.EndsWith("Core", StringComparison.Ordinal)
                            ? scale * EffectorCoreFraction
                            : scale);
        }
        NotifyRenderNeeded();
    }

    /// <summary>Fits the whole scene in view (viewport top-right icon).</summary>
    public RelayCommand FrameAllCommand => _frameAllCommand ??=
        new RelayCommand(() => OnFrameAllRequested?.Invoke());
    private RelayCommand? _frameAllCommand;

    /// <summary>Dismisses the pie menu without selecting.</summary>
    public RelayCommand CloseViewPieCommand => _closeViewPieCommand ??=
        new RelayCommand(() => IsViewPieOpen = false);
    private RelayCommand? _closeViewPieCommand;
    /// <summary>Callback (wired by MainWindowViewModel) to save the current camera view to the active cell.</summary>
    internal Action? OnSaveViewRequested    { get; set; }
    /// <summary>Callback set by the viewport code-behind when dev-mode toggles.</summary>
    internal Action<bool>? OnDevModeChanged { get; set; }
    /// <summary>Callback set by the viewport code-behind to persist a dev-object transform.</summary>
    internal Action? OnSaveDevTransformRequested { get; set; }
    internal Action? OnSaveAllDevTransformsRequested { get; set; }
    /// <summary>Callback wired by MainWindow to reload the active cell after dev saves.</summary>
    internal Action<string>? OnDevCellReloadRequested { get; set; }
    internal Action<string>? OnDevLog { get; set; }
    /// <summary>Returns the current orbit-camera pose; set by the viewport code-behind.</summary>
    internal Func<CameraView?>? GetCameraState { get; set; }

    /// <summary>Wired by the view: re-upload the active scrub toolpath's GPU buffers
    /// after in-place mutation (seam edits, tpfix injections).</summary>
    internal Action? RequestActiveToolpathReupload { get; set; }

    /// <summary>Applies a saved camera pose; set by the viewport code-behind.</summary>
    internal Action<CameraView>? ApplyCameraState { get; set; }

    public ViewportViewModel()
    {
        WireViewProfileTracking();
        LiveIo.ExpandedChanged += () =>
        {
            OnPropertyChanged(nameof(Lfam3WorkflowMargin));
            OnPropertyChanged(nameof(Lfam3WorkflowMaxHeight));
            OnPropertyChanged(nameof(Lfam3LiveIoMaxHeight));
            NotifyPhaseExpansionChanged();
        };

        SetShaderModeCommand = new RelayCommand<string>(name =>
        {
            if (Enum.TryParse<ShaderMode>(name, out var mode))
                ActiveShaderMode = mode;
        });
        ToggleSyncHudCommand = new RelayCommand(ToggleSyncHud);
        LayFlatCommand     = new RelayCommand(() => IsLayFlatMode = !IsLayFlatMode);
        SeamEditorSaveCommand   = new RelayCommand(SaveSeamEditor, () => IsSeamEditorActive);
        SeamEditorCancelCommand = new RelayCommand(CancelSeamEditor, () => IsSeamEditorActive);
        SeamEditorDeleteCommand = new RelayCommand(DeleteSeamGuide, () => IsSeamEditorActive && SeamGuideDraft.Count > 0);
        SeamEditorAddPointCommand = new RelayCommand(() => SeamEditorTool = SeamEditorToolKind.AddPoint, () => IsSeamEditorActive);
        SeamEditorSelectPointCommand = new RelayCommand(() => SeamEditorTool = SeamEditorToolKind.SelectPoint, () => IsSeamEditorActive);
        ToggleSeamGuideLayerCommand = new RelayCommand(() => IsSeamGuideLayerOpen = !IsSeamGuideLayerOpen, () => IsSeamEditorActive && SeamGuideDraft.Count > 0);
        SelectSeamGuideByIndexCommand = new RelayCommand<int>(SelectSeamGuideByIndex, _ => IsSeamEditorActive);
        EditToolpathSeamCommand  = new RelayCommand(ToggleToolpathSeamEdit, () => IsToolpathSelected);
        ApplyToolpathSeamCommand = new RelayCommand(() => OnApplyToolpathSeamRequested?.Invoke(),
            () => IsToolpathSeamEditActive && HasSeamGuideDraft && IsToolpathSelected);
        ClearToolpathSeamCommand = new RelayCommand(ClearToolpathSeam, () => IsToolpathSeamEditActive && HasSeamGuideDraft);
        DoneToolpathSeamCommand  = new RelayCommand(ExitToolpathSeamEdit, () => IsToolpathSeamEditActive);
        BoundaryEditorSaveCommand       = new RelayCommand(SaveBoundaryEditor, () => IsBoundaryEditorActive);
        BoundaryEditorCancelCommand     = new RelayCommand(CancelBoundaryEditor, () => IsBoundaryEditorActive);
        BoundaryEditorLowTargetCommand  = new RelayCommand(() => BoundaryEditorTarget = CurvedBoundaryEditorTarget.Low, () => IsBoundaryEditorActive);
        BoundaryEditorHighTargetCommand = new RelayCommand(() => BoundaryEditorTarget = CurvedBoundaryEditorTarget.High, () => IsBoundaryEditorActive);
        FocusCommand          = new RelayCommand(() => OnFocusRequested?.Invoke());
        DropToPlateCommand    = new RelayCommand(() => OnDropToPlateRequested?.Invoke());
        RecenterCommand       = new RelayCommand(() => OnRecenterRequested?.Invoke(), () => HasMeshSelected);
        UngroupCommand        = new RelayCommand(() => OnUngroupRequested?.Invoke(), () => CanUngroup);
        ExplodeCommand        = new RelayCommand(() => OnExplodeRequested?.Invoke(), () => CanExplode);
        MeshCleanupCommand    = new RelayCommand(() => OnMeshCleanupRequested?.Invoke(), () => CanMeshCleanup);
        CutToolCommand        = new RelayCommand(() => OnCutToolRequested?.Invoke(), () => CanCutTool && !IsCutToolActive);
        CancelCutToolCommand  = new RelayCommand(() => OnCancelCutToolRequested?.Invoke(), () => IsCutToolActive);
        PerformCutToolCommand = new RelayCommand(() => OnPerformCutToolRequested?.Invoke(), () => IsCutToolActive);
        CutToolNormalZCommand = new RelayCommand(() => CutToolSession?.SetNormalPreset(0, 0, 1), () => IsCutToolActive);
        CutToolNormalYCommand = new RelayCommand(() => CutToolSession?.SetNormalPreset(0, 1, 0), () => IsCutToolActive);
        CutToolNormalXCommand = new RelayCommand(() => CutToolSession?.SetNormalPreset(1, 0, 0), () => IsCutToolActive);
        SaveViewCommand       = new RelayCommand(() => OnSaveViewRequested?.Invoke());
        SaveDevTransformCommand = new RelayCommand(
            () => OnSaveDevTransformRequested?.Invoke(),
            () => IsDevMode && IsDevObjectSelected);
        SaveAllDevTransformsCommand = new RelayCommand(
            () => OnSaveAllDevTransformsRequested?.Invoke(),
            () => IsDevMode);
        TogglePlaybackCommand = new RelayCommand(() =>
        {
            bool starting = !IsPlaying;
            if (starting && ToolpathScrubIndex >= ToolpathScrubMax)
                ToolpathScrubIndex = 0;
            IsPlaying = starting;
            OnPlaybackToggled?.Invoke(IsPlaying);
        }, canExecute: () => _isToolpathSelected);
        GizmoMoveCommand   = new RelayCommand(() => ActiveGizmoModeInternal = _activeGizmoMode == GizmoMode.Translate ? GizmoMode.None : GizmoMode.Translate);
        GizmoRotateCommand = new RelayCommand(() => ActiveGizmoModeInternal = _activeGizmoMode == GizmoMode.Rotate    ? GizmoMode.None : GizmoMode.Rotate);
        GizmoScaleCommand  = new RelayCommand(() => ActiveGizmoModeInternal = _activeGizmoMode == GizmoMode.Scale     ? GizmoMode.None : GizmoMode.Scale);
        SliceCommand = new RelayCommand(
            execute:    () => _ = OnSliceRequested?.Invoke(),
            canExecute: () => !IsSlicing && HasMeshSelected);

        // Relief milling: guard inside RunMillAsync (no canExecute predicate to avoid
        // threading RaiseCanExecuteChanged through every selection-change site).
        MillCommand = new RelayCommand(() => _ = OnMillRequested?.Invoke());
        PreviewDisplacedCommand = new RelayCommand(() => _ = OnPreviewDisplacedRequested?.Invoke());
        GenerateMultiAxisCommand = new RelayCommand(() => _ = OnGenerateMultiAxisRequested?.Invoke());

        UpdateSliceCommand = new RelayCommand(
            execute:    () => _ = OnUpdateSliceRequested?.Invoke(),
            canExecute: () => !IsSlicing && IsToolpathSelected && (CanUpdateSlice?.Invoke() ?? false));

        ExportKrlCommand = new RelayCommand(
            execute:    () => _ = OnExportKrlRequested?.Invoke(),
            canExecute: () => IsScrubSessionActive && ActiveScrubToolpath is not null);

        SendToRobotCommand = new RelayCommand(
            execute:    () => _ = OnSendToRobotRequested?.Invoke(),
            canExecute: () => IsScrubSessionActive && ActiveScrubToolpath is not null && ActiveCell is not null);

        MergeToolpathsCommand = new RelayCommand(
            execute:    () => OnMergeToolpathsRequested?.Invoke(),
            canExecute: () => CanMergeToolpaths);

        MergeScansAsPointCloudCommand = new RelayCommand(
            execute:    () => OnMergeScansRequested?.Invoke(ScanMergeOutput.PointCloud),
            canExecute: () => CanMergeScans);

        MergeScansAsMeshCommand = new RelayCommand(
            execute:    () => OnMergeScansRequested?.Invoke(ScanMergeOutput.Mesh),
            canExecute: () => CanMergeScans);

        TogglePrePrintScanStepCommand = new RelayCommand(
            () => HasPrePrintScanStep = !HasPrePrintScanStep, () => ShowLfam3ToolPicker);
        SelectPrePrintScanPhaseCommand = new RelayCommand(
            () => SelectLfam3WorkflowPhase(0, "Scanner (Calibrated)"),
            () => ShowLfam3ToolPicker && HasPrePrintScanStep);
        SelectPrintPhaseCommand = new RelayCommand(
            () => SelectLfam3WorkflowPhase(PrintPhaseIndex, "Extruder"), () => ShowLfam3ToolPicker);
        SelectVerifyScanPhaseCommand = new RelayCommand(
            () => SelectLfam3WorkflowPhase(ScanPhaseIndex, "Scanner (Calibrated)"), () => ShowLfam3ToolPicker);
        SelectMillPhaseCommand = new RelayCommand(
            () => SelectLfam3WorkflowPhase(MillPhaseIndex, "Spindle (No Bit)"), () => ShowLfam3ToolPicker);
        ToggleLfam3WorkflowCommand = new RelayCommand(
            () => IsLfam3WorkflowExpanded = !IsLfam3WorkflowExpanded, () => ShowLfam3ToolPicker);

        SimulateExtruderPickCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Extruder_Pick"),
            () => CanSimulateToolPick("Extruder", MountedToolName, ShowLfam3ToolPicker));
        SimulateExtruderDepositCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Extruder_Deposit"),
            () => CanSimulateToolDeposit("Extruder", MountedToolName, ShowLfam3ToolPicker));
        SimulateScannerPickCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Scanner_Pick"),
            () => CanSimulateToolPick("Scanner (Calibrated)", MountedToolName, ShowLfam3ToolPicker));
        SimulateScannerDepositCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Scanner_Deposit"),
            () => CanSimulateToolDeposit("Scanner (Calibrated)", MountedToolName, ShowLfam3ToolPicker));
        SimulateSpindlePickCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Spindle_Pick"),
            () => CanSimulateToolPick("Spindle (No Bit)", MountedToolName, ShowLfam3ToolPicker));
        SimulateSpindleDepositCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Spindle_Deposit"),
            () => CanSimulateToolDeposit("Spindle (No Bit)", MountedToolName, ShowLfam3ToolPicker));

        ExtruderToolPanel = new ToolChangePanelBinding(
            this, "HV EXTRUDER", "Extruder_Pick", "Extruder_Deposit",
            SimulateExtruderPickCommand, SimulateExtruderDepositCommand);
        ScannerToolPanel = new ToolChangePanelBinding(
            this, "SCANNER", "Scanner_Pick", "Scanner_Deposit",
            SimulateScannerPickCommand, SimulateScannerDepositCommand);
        SpindleToolPanel = new ToolChangePanelBinding(
            this, "SPINDLE", "Spindle_Pick", "Spindle_Deposit",
            SimulateSpindlePickCommand, SimulateSpindleDepositCommand);
        SequenceWaypointEditor = new SequenceWaypointEditorViewModel(this);
        SequenceWaypointEditor.WireCommands();
        ToggleToolChangePlaybackCommand = new RelayCommand(
            () => OnToggleToolChangePlaybackRequested?.Invoke(),
            () => ActiveToolChangeSequenceId is not null);
        CollapseToolChangePlaybackCommand = new RelayCommand(
            () => OnCollapseToolChangePlaybackRequested?.Invoke(),
            () => ActiveToolChangeSequenceId is not null && IsToolChangePlaybackExpanded);

        var options = new List<BackdropOption> { new("None", null) };
        options.AddRange(
            AssetPaths.EnumerateBackdropHdrPaths()
                .Select(p => new BackdropOption(Path.GetFileNameWithoutExtension(p), p)));
        AvailableBackdrops = options;
        // Default to a soft, balanced HDRI so imported models get environment lighting
        // (reflections + fill) out of the box. Falls back to the first available image,
        // then to "None". The user can change it in the BACKDROP selector.
        _activeBackdrop = options[0];
        string[] preferred = ["AmbienceExposure4k", "CasualDay4K", "DayInTheClouds4k", "FluffballDay4k"];
        foreach (var name in preferred)
        {
            var match = options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) { _activeBackdrop = match; break; }
        }
        if (ReferenceEquals(_activeBackdrop, options[0]) && options.Count > 1)
            _activeBackdrop = options[1];
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

    // -- Seam guide editor -----------------------------------------------------

    private bool _isSeamEditorActive;

    public bool IsSeamEditorActive
    {
        get => _isSeamEditorActive;
        set
        {
            if (SetField(ref _isSeamEditorActive, value))
            {
                SeamEditorSaveCommand.RaiseCanExecuteChanged();
                SeamEditorCancelCommand.RaiseCanExecuteChanged();
                SeamEditorDeleteCommand.RaiseCanExecuteChanged();
                SeamEditorAddPointCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<SeamGuidePoint> SeamGuideDraft { get; } = [];

    private SeamEditorToolKind _seamEditorTool = SeamEditorToolKind.AddPoint;

    public SeamEditorToolKind SeamEditorTool
    {
        get => _seamEditorTool;
        set
        {
            if (SetField(ref _seamEditorTool, value))
            {
                OnPropertyChanged(nameof(IsSeamAddPointActive));
                OnPropertyChanged(nameof(IsSeamSelectPointActive));
            }
        }
    }

    public bool IsSeamAddPointActive => SeamEditorTool == SeamEditorToolKind.AddPoint;
    public bool IsSeamSelectPointActive => SeamEditorTool == SeamEditorToolKind.SelectPoint;
    public bool HasSeamGuideDraft => SeamGuideDraft.Count > 0;

    public string SeamGuideLayerLabel =>
        SeamGuideDraft.Count == 0 ? "Points" : $"Points ({SeamGuideDraft.Count})";

    private bool _isSeamGuideLayerOpen;

    /// <summary>When true, the guide-point list panel is visible in the viewport.</summary>
    public bool IsSeamGuideLayerOpen
    {
        get => _isSeamGuideLayerOpen;
        set => SetField(ref _isSeamGuideLayerOpen, value);
    }

    private int _selectedSeamGuideIndex = -1;

    /// <summary>Index of the guide point selected for move/delete, or -1.</summary>
    public int SelectedSeamGuideIndex
    {
        get => _selectedSeamGuideIndex;
        set
        {
            if (!SetField(ref _selectedSeamGuideIndex, value)) return;
            if (value >= 0 && IsSeamEditorActive)
            {
                SeamEditorTool = SeamEditorToolKind.SelectPoint;
                OnPropertyChanged(nameof(IsSeamAddPointActive));
                OnPropertyChanged(nameof(IsSeamSelectPointActive));
            }
            OnSeamGuidesChanged?.Invoke();
        }
    }

    public RelayCommand SeamEditorSaveCommand { get; }
    public RelayCommand SeamEditorCancelCommand { get; }
    public RelayCommand SeamEditorDeleteCommand { get; }
    public RelayCommand SeamEditorAddPointCommand { get; }
    public RelayCommand SeamEditorSelectPointCommand { get; }
    public RelayCommand ToggleSeamGuideLayerCommand { get; }
    public RelayCommand<int> SelectSeamGuideByIndexCommand { get; }

    // -- Toolpath seam editing (Toolpath tab): re-seam a generated toolpath in place ----------
    public RelayCommand EditToolpathSeamCommand  { get; }
    public RelayCommand ApplyToolpathSeamCommand { get; }
    public RelayCommand ClearToolpathSeamCommand { get; }
    public RelayCommand DoneToolpathSeamCommand  { get; }

    /// <summary>Wired in ViewportView: applies the placed seam points to the selected toolpath.</summary>
    internal Action? OnApplyToolpathSeamRequested { get; set; }

    private bool _isToolpathSeamEditActive;

    /// <summary>When true, clicks in the viewport place seam points for in-place re-seaming
    /// of the selected toolpath (no re-slice), reusing the seam-guide markers.</summary>
    public bool IsToolpathSeamEditActive
    {
        get => _isToolpathSeamEditActive;
        private set
        {
            if (SetField(ref _isToolpathSeamEditActive, value))
            {
                OnPropertyChanged(nameof(ToolpathSeamSummary));
                ApplyToolpathSeamCommand.RaiseCanExecuteChanged();
                ClearToolpathSeamCommand.RaiseCanExecuteChanged();
                DoneToolpathSeamCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ToolpathSeamSummary =>
        !IsToolpathSeamEditActive ? "Off"
        : SeamGuideDraft.Count == 0 ? "Click in the viewport to place seam point(s)."
        : $"{SeamGuideDraft.Count} point(s) placed — press Apply.";

    private void ToggleToolpathSeamEdit()
    {
        if (IsToolpathSeamEditActive) ExitToolpathSeamEdit();
        else BeginToolpathSeamEdit();
    }

    private void BeginToolpathSeamEdit()
    {
        SeamGuideDraft.Clear();
        SeamEditorTool = SeamEditorToolKind.AddPoint;
        SelectedSeamGuideIndex = -1;
        IsToolpathSeamEditActive = true;
        OnPropertyChanged(nameof(HasSeamGuideDraft));
        OnSeamGuidesChanged?.Invoke();
    }

    private void ExitToolpathSeamEdit()
    {
        IsToolpathSeamEditActive = false;
        SeamGuideDraft.Clear();
        SelectedSeamGuideIndex = -1;
        OnPropertyChanged(nameof(HasSeamGuideDraft));
        OnPropertyChanged(nameof(ToolpathSeamSummary));
        OnSeamGuidesChanged?.Invoke();
    }

    private void ClearToolpathSeam()
    {
        SeamGuideDraft.Clear();
        SelectedSeamGuideIndex = -1;
        OnPropertyChanged(nameof(HasSeamGuideDraft));
        OnPropertyChanged(nameof(ToolpathSeamSummary));
        ApplyToolpathSeamCommand.RaiseCanExecuteChanged();
        ClearToolpathSeamCommand.RaiseCanExecuteChanged();
        OnSeamGuidesChanged?.Invoke();
    }

    /// <summary>Refresh toolpath-seam UI after a point is placed (called from AddSeamGuidePoint).</summary>
    private void RefreshToolpathSeamState()
    {
        if (!IsToolpathSeamEditActive) return;
        OnPropertyChanged(nameof(ToolpathSeamSummary));
        ApplyToolpathSeamCommand.RaiseCanExecuteChanged();
        ClearToolpathSeamCommand.RaiseCanExecuteChanged();
    }

    public void BeginSeamEditor(IReadOnlyList<SeamGuidePoint> current)
    {
        SeamGuideDraft.Clear();
        foreach (var g in current)
            SeamGuideDraft.Add(g);
        SeamEditorTool = SeamEditorToolKind.AddPoint;
        SelectedSeamGuideIndex = -1;
        IsSeamGuideLayerOpen = SeamGuideDraft.Count > 0;
        IsSeamEditorActive = true;
        OnPropertyChanged(nameof(IsSeamAddPointActive));
        OnPropertyChanged(nameof(IsSeamSelectPointActive));
        OnPropertyChanged(nameof(HasSeamGuideDraft));
        OnPropertyChanged(nameof(SeamGuideLayerLabel));
        RaiseSeamGuideCommands();
        OnSeamGuidesChanged?.Invoke();
    }

    public void AddSeamGuidePoint(SeamGuidePoint point)
    {
        // One guide, and placing a new one replaces it. The slicer resolves a single guide per
        // closed contour, so on a one-island part every extra point beyond the nearest is dead
        // weight — they stacked up in the list, could not be told apart, and blocked each other
        // from being removed. Placing again is now how you move the seam.
        SeamGuideDraft.Clear();
        SeamGuideDraft.Add(point);
        SelectedSeamGuideIndex = 0;
        IsSeamGuideLayerOpen = true;
        OnPropertyChanged(nameof(HasSeamGuideDraft));
        OnPropertyChanged(nameof(SeamGuideLayerLabel));
        RaiseSeamGuideCommands();
        RefreshToolpathSeamState();
        OnSeamGuidesChanged?.Invoke();
    }

    public void MoveSeamGuidePoint(int index, SeamGuidePoint point)
    {
        if (index < 0 || index >= SeamGuideDraft.Count) return;
        SeamGuideDraft[index] = point;
        OnSeamGuidesChanged?.Invoke();
    }

    private void SelectSeamGuideByIndex(int index)
    {
        if (index < 0 || index >= SeamGuideDraft.Count) return;
        SelectedSeamGuideIndex = index;
        SeamEditorTool = SeamEditorToolKind.SelectPoint;
        OnPropertyChanged(nameof(IsSeamAddPointActive));
        OnPropertyChanged(nameof(IsSeamSelectPointActive));
    }

    private void SaveSeamEditor()
    {
        AdditiveSettings?.SetSeamGuides(SeamGuideDraft);
        IsSeamEditorActive = false;
        IsSeamGuideLayerOpen = false;
        SelectedSeamGuideIndex = -1;
        OnSeamGuidesChanged?.Invoke();
    }

    private void CancelSeamEditor()
    {
        IsSeamEditorActive = false;
        IsSeamGuideLayerOpen = false;
        SeamGuideDraft.Clear();
        SelectedSeamGuideIndex = -1;
        OnPropertyChanged(nameof(HasSeamGuideDraft));
        OnPropertyChanged(nameof(SeamGuideLayerLabel));
        RaiseSeamGuideCommands();
        OnSeamGuidesChanged?.Invoke();
    }

    private void DeleteSeamGuide()
    {
        if (SeamGuideDraft.Count == 0) return;
        int index = SelectedSeamGuideIndex >= 0 && SelectedSeamGuideIndex < SeamGuideDraft.Count
            ? SelectedSeamGuideIndex
            : SeamGuideDraft.Count - 1;
        SeamGuideDraft.RemoveAt(index);
        SelectedSeamGuideIndex = SeamGuideDraft.Count == 0
            ? -1
            : Math.Min(index, SeamGuideDraft.Count - 1);
        OnPropertyChanged(nameof(HasSeamGuideDraft));
        OnPropertyChanged(nameof(SeamGuideLayerLabel));
        RaiseSeamGuideCommands();
        OnSeamGuidesChanged?.Invoke();
    }

    private void RaiseSeamGuideCommands()
    {
        SeamEditorDeleteCommand.RaiseCanExecuteChanged();
        ToggleSeamGuideLayerCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Raised when seam guide draft changes — viewport refreshes markers.</summary>
    public Action? OnSeamGuidesChanged;

    // -- Curved boundary editor ------------------------------------------------

    private bool _isBoundaryEditorActive;

    public bool IsBoundaryEditorActive
    {
        get => _isBoundaryEditorActive;
        set
        {
            if (SetField(ref _isBoundaryEditorActive, value))
            {
                BoundaryEditorSaveCommand.RaiseCanExecuteChanged();
                BoundaryEditorCancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private CurvedBoundaryEditorTarget _boundaryEditorTarget = CurvedBoundaryEditorTarget.Low;

    public CurvedBoundaryEditorTarget BoundaryEditorTarget
    {
        get => _boundaryEditorTarget;
        set
        {
            if (SetField(ref _boundaryEditorTarget, value))
            {
                OnPropertyChanged(nameof(IsBoundaryLowTarget));
                OnPropertyChanged(nameof(IsBoundaryHighTarget));
            }
        }
    }

    public bool IsBoundaryLowTarget  => BoundaryEditorTarget == CurvedBoundaryEditorTarget.Low;
    public bool IsBoundaryHighTarget => BoundaryEditorTarget == CurvedBoundaryEditorTarget.High;

    public ObservableCollection<int> BoundaryLowDraft  { get; } = [];
    public ObservableCollection<int> BoundaryHighDraft { get; } = [];

    public RelayCommand BoundaryEditorSaveCommand { get; }
    public RelayCommand BoundaryEditorCancelCommand { get; }
    public RelayCommand BoundaryEditorLowTargetCommand { get; }
    public RelayCommand BoundaryEditorHighTargetCommand { get; }

    public void BeginBoundaryEditor(IReadOnlyList<int> low, IReadOnlyList<int> high)
    {
        BoundaryLowDraft.Clear();
        BoundaryHighDraft.Clear();
        foreach (var v in low)  BoundaryLowDraft.Add(v);
        foreach (var v in high) BoundaryHighDraft.Add(v);
        BoundaryEditorTarget = CurvedBoundaryEditorTarget.Low;
        IsBoundaryEditorActive = true;
        OnBoundaryDraftChanged?.Invoke();
    }

    public void SetBoundaryDraft(IReadOnlyList<int> low, IReadOnlyList<int> high)
    {
        BoundaryLowDraft.Clear();
        BoundaryHighDraft.Clear();
        foreach (var v in low)  BoundaryLowDraft.Add(v);
        foreach (var v in high) BoundaryHighDraft.Add(v);
        OnBoundaryDraftChanged?.Invoke();
    }

    private void SaveBoundaryEditor()
    {
        AdditiveSettings?.SetCurvedBoundaries(BoundaryLowDraft, BoundaryHighDraft);
        if (AdditiveSettings is not null)
            AdditiveSettings.CurvedBoundarySourceDisplay = "Viewport Pick";
        IsBoundaryEditorActive = false;
        OnBoundaryDraftChanged?.Invoke();
    }

    private void CancelBoundaryEditor()
    {
        IsBoundaryEditorActive = false;
        BoundaryLowDraft.Clear();
        BoundaryHighDraft.Clear();
        OnBoundaryDraftChanged?.Invoke();
    }

    /// <summary>Raised when curved boundary draft changes — viewport refreshes markers.</summary>
    public Action? OnBoundaryDraftChanged;

    // -- Slicing ---------------------------------------------------------------

    private bool _isSlicing;

    /// <summary>True while a slice operation is running (disables the slice button).</summary>
    public bool IsSlicing
    {
        get => _isSlicing;
        set
        {
            if (SetField(ref _isSlicing, value))
            {
                SliceCommand?.RaiseCanExecuteChanged();
                UpdateSliceCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ShowSliceStatus));
            }
        }
    }

    private double _sliceProgressPercent;

    /// <summary>Slice progress 0–100, driven by per-stage/per-layer callbacks.</summary>
    public double SliceProgressPercent
    {
        get => _sliceProgressPercent;
        set => SetField(ref _sliceProgressPercent, Math.Clamp(value, 0.0, 100.0));
    }

    private int _firstValidationIssueIndex = -1;

    /// <summary>Flat move index of the first validation-flagged move, or -1.</summary>
    public int FirstValidationIssueIndex
    {
        get => _firstValidationIssueIndex;
        set { if (SetField(ref _firstValidationIssueIndex, value)) OnPropertyChanged(nameof(HasValidationIssueJump)); }
    }

    public bool HasValidationIssueJump => _firstValidationIssueIndex >= 0;

    /// <summary>Jumps the scrubber to the first flagged move so the failing pose is visible.</summary>
    public void JumpToValidationIssue()
    {
        if (_firstValidationIssueIndex < 0) return;
        // Preserve the red/purple timeline markers: a selection-sync side effect of
        // the jump can clear them, so re-emit the stored validation data afterwards.
        var reach = _scrubReachable; var sing = _scrubSingular;
        ToolpathScrubIndex = Math.Clamp(_firstValidationIssueIndex, 0, ToolpathScrubMax);
        if (reach.Length > 0) SetScrubMarkers(reach, sing);
    }

    private string _sliceStatusMessage = string.Empty;

    /// <summary>Human-readable slice progress, result, or error (shown in overlay + status bar).</summary>
    public string SliceStatusMessage
    {
        get => _sliceStatusMessage;
        set
        {
            if (SetField(ref _sliceStatusMessage, value))
                OnPropertyChanged(nameof(ShowSliceStatus));
        }
    }

    private bool _sliceStatusIsError;

    /// <summary>When true, <see cref="SliceStatusMessage"/> is styled as an error.</summary>
    public bool SliceStatusIsError
    {
        get => _sliceStatusIsError;
        set => SetField(ref _sliceStatusIsError, value);
    }

    /// <summary>True when a slice error message should be shown in-panel (progress uses footer line + console).</summary>
    public bool ShowSliceStatus => SliceStatusIsError && !string.IsNullOrWhiteSpace(SliceStatusMessage);

    /// <summary>
    /// Completed toolpaths queued for upload on the GL thread.
    /// Produced by the slice task; consumed by the render loop.
    /// Each entry is a freshly-created SceneNode -- never re-uses an existing node.
    /// </summary>
    public ConcurrentQueue<PendingToolpathEntry> PendingToolpath { get; } = new();

    /// <summary>
    /// Toolpath geometry replacements for an existing outliner node (Update Slice).
    /// Consumed on the GL thread; does not create a new outliner entry.
    /// </summary>
    public ConcurrentQueue<PendingToolpathEntry> PendingToolpathReplace { get; } = new();

    /// <summary>
    /// UI session (edit mode / tools / layer isolation) to re-apply after workspace
    /// toolpaths finish uploading. Set by workspace restore; consumed by the viewport
    /// once <see cref="PendingToolpath"/> is empty.
    /// </summary>
    internal WorkspaceUiSession? PendingUiSession { get; set; }

    /// <summary>Wired by the viewport: re-select scrub toolpath + apply pending UI session.</summary>
    internal Action? RequestApplyPendingUiSession { get; set; }

    /// <summary>Viewport captures the MODIFICATIONS list for workspace save.</summary>
    internal Func<List<WorkspacePaintModification>>? CapturePaintModifications { get; set; }

    /// <summary>Viewport rebuilds the MODIFICATIONS list after toolpath restore.</summary>
    internal Action<IReadOnlyList<WorkspacePaintModification>>? RestorePaintModifications { get; set; }

    /// <summary>
    /// Applies pure ViewModel pieces of a saved UI session (view mode, edit tools).
    /// Scrub node selection and move indices are applied by the viewport code-behind.
    /// </summary>
    internal void ApplyUiSessionViewState(WorkspaceUiSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.ViewMode))
            ViewMode = session.ViewMode;

        // Multi-Planar Planes toggle — only apply when the workspace actually saved it
        // (null = older .mass → keep default on).
        if (session.ShowMultiPlanarPlanes is bool showPlanes)
            ShowMultiPlanarPlanes = showPlanes;

        // X-bracing helper visibility (plane / cylinder guide). Prefer UiSession when
        // present so it always round-trips with the .mass even if Settings lagged.
        if (session.XBracingShowHelper is bool showXHelper && AdditiveSettings is { } add)
            add.XBracingShowHelper = showXHelper;

        if (!string.IsNullOrWhiteSpace(session.PaintSelectGranularity))
            PaintSelectGranularity = session.PaintSelectGranularity;
        if (!string.IsNullOrWhiteSpace(session.PaintPickFilter))
            PaintPickFilter = session.PaintPickFilter;
        if (session.PaintBrushRadiusMm > 0)
            PaintBrushRadiusMm = session.PaintBrushRadiusMm;

        if (!string.IsNullOrWhiteSpace(session.PaintRegionSelectMode))
            PaintRegionSelectMode = session.PaintRegionSelectMode;
        if (!string.IsNullOrWhiteSpace(session.PaintModificationMode))
            PaintModificationMode = session.PaintModificationMode;
        if (!string.IsNullOrWhiteSpace(session.PaintSupportType))
            PaintSupportType = session.PaintSupportType;
        ShowPaintMarkers = session.ShowPaintMarkers;
        PaintShowBeads = session.PaintShowBeads;

        // Open edit first so tool mutual-exclusion and display modes apply correctly.
        IsPaintEditOpen = session.IsPaintEditOpen;
        if (session.IsPaintEditOpen)
        {
            PaintHandActive = false;
            PaintBoxSelectActive = false;
            PaintBridgeActive = false;
            PaintRemoveActive = false;
            PaintLineBridgeActive = false;
            PaintLineRemoveActive = false;

            if (session.PaintHandActive)
                PaintHandActive = true;
            else if (session.PaintBoxSelectActive)
                PaintBoxSelectActive = true;
            else if (session.PaintBridgeActive)
                PaintBridgeActive = true;
            else if (session.PaintRemoveActive)
                PaintRemoveActive = true;
            else if (session.PaintLineBridgeActive)
                PaintLineBridgeActive = true;
            else if (session.PaintLineRemoveActive)
                PaintLineRemoveActive = true;
            // else: path-select is the implicit default when no other tool is armed

            // Do not call ApplyPaintSupportTypeToSettings here — that would overwrite
            // the workspace-restored FILL PATTERN with the edit Support type.
            ApplyPaintEditDisplayMode();

            // 2D Slice Plane Viewer — after edit is open so the toggle is allowed.
            IsSlicePlaneViewerActive = session.IsSlicePlaneViewerActive;
            if (session.IsSlicePlaneViewerActive)
                RefreshSlicePlaneStats();
        }
        else
        {
            IsSlicePlaneViewerActive = false;
        }

        // MODIFICATIONS list — after toolpath is armed (caller may re-invoke with layers ready).
        if (session.PaintModifications is { Count: > 0 } mods)
            RestorePaintModifications?.Invoke(mods);
    }

    /// <summary>
    /// Restores the isolated layer window after the scrub toolpath is armed.
    /// Prefers exact move indices; falls back to saved layer numbers when the
    /// move count differs.
    /// </summary>
    internal void ApplyUiSessionScrubWindow(WorkspaceUiSession session)
    {
        if (ToolpathScrubMax <= 0) return;

        int high = session.ToolpathScrubIndex;
        int low = session.ToolpathScrubLowIndex;

        // If saved move indices look out of range, map from layer numbers instead.
        if (high > ToolpathScrubMax || high < 0
            || (session.ToolpathScrubLayerHigh > 0 && high == 0 && session.ToolpathScrubLayerHigh > 1))
        {
            if (session.ToolpathScrubLayerHigh > 0)
                ToolpathScrubLayerHigh = session.ToolpathScrubLayerHigh;
            if (session.ToolpathScrubLayerLow > 0)
                ToolpathScrubLayerLow = session.ToolpathScrubLayerLow;
            return;
        }

        high = Math.Clamp(high, 0, ToolpathScrubMax);
        low = Math.Clamp(low, 0, Math.Max(0, high - 1));
        // High first so the low clamp has the correct ceiling.
        ToolpathScrubIndex = high;
        ToolpathScrubLowIndex = low;
    }

    /// <summary>Returns live toolpath data for a scene node (wired by the viewport).</summary>
    internal Func<SceneNode, ToolpathSnapshot?>? GetToolpathSnapshot { get; set; }

    /// <summary>
    /// The centroid a toolpath's GPU geometry was built relative to, so a diagnostic can reproduce
    /// what is actually on screen: <c>rendered = (move - origin) * node.LocalTransform</c>. Without
    /// it, reading raw move coordinates and calling them "world" silently ignores the node's own
    /// transform — the mistake that made <c>align-debug</c> report a toolpath 179mm adrift as
    /// perfectly aligned.
    /// </summary>
    internal Func<SceneNode, System.Numerics.Vector3?>? GetToolpathRenderOrigin { get; set; }

    /// <summary>
    /// Reference to the additive settings ViewModel. Set by <c>MainWindowViewModel</c>
    /// so the slice command can read current parameters.
    /// </summary>
    private AdditiveSettingsViewModel? _additiveSettings;
    public AdditiveSettingsViewModel? AdditiveSettings
    {
        get => _additiveSettings;
        set
        {
            if (ReferenceEquals(_additiveSettings, value)) return;
            if (_additiveSettings is not null)
                _additiveSettings.PropertyChanged -= OnAdditiveSettingsPropertyChanged;
            _additiveSettings = value;
            if (_additiveSettings is not null)
                _additiveSettings.PropertyChanged += OnAdditiveSettingsPropertyChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowMultiPlanarPlanesButton));
        }
    }

    private void OnAdditiveSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AdditiveSettingsViewModel.ShowMultiPlanarControls)
            or nameof(AdditiveSettingsViewModel.Method)
            or nameof(AdditiveSettingsViewModel.SelectedPreset))
            OnPropertyChanged(nameof(ShowMultiPlanarPlanesButton));
    }

    /// <summary>Subtractive (relief milling) settings, wired from the right panel.</summary>
    public SubtractiveSettingsViewModel? SubtractiveSettings { get; set; }

    // -- Toolpath stats --------------------------------------------------------

    private bool _hasToolpathStats;
    public bool HasToolpathStats
    {
        get => _hasToolpathStats;
        set
        {
            if (SetField(ref _hasToolpathStats, value))
                OnPropertyChanged(nameof(ShowToolpathStatsOverlay));
        }
    }

    /// <summary>Stats + cost hide while the Edit menu is open — the numbers are
    /// stale mid-edit (slicing is paused) and the boxes crowd the editing HUD.</summary>
    public bool ShowToolpathStatsOverlay => HasToolpathStats && !IsPaintEditOpen;

    private string _statsTime = "";
    public string StatsTime
    {
        get => _statsTime;
        set => SetField(ref _statsTime, value);
    }

    private string _statsWeight = "";
    /// <summary>Numeric twins of the display stats — feed ERP quotes/slice costing.</summary>
    public double StatsTimeSeconds { get; set; }
    public double StatsWeightKg    { get; set; }

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

    private string _statsLongestLayerLength = "";
    public string StatsLongestLayerLength
    {
        get => _statsLongestLayerLength;
        set => SetField(ref _statsLongestLayerLength, value);
    }

    private string _statsShortestLayerLength = "";
    public string StatsShortestLayerLength
    {
        get => _statsShortestLayerLength;
        set => SetField(ref _statsShortestLayerLength, value);
    }

    private string _statsLongestLayerTime = "";
    public string StatsLongestLayerTime
    {
        get => _statsLongestLayerTime;
        set => SetField(ref _statsLongestLayerTime, value);
    }

    private string _statsShortestLayerTime = "";
    public string StatsShortestLayerTime
    {
        get => _statsShortestLayerTime;
        set => SetField(ref _statsShortestLayerTime, value);
    }

    private string _statsReachability = "";
    public string StatsReachability
    {
        get => _statsReachability;
        set => SetField(ref _statsReachability, value);
    }

    /// <summary>
    /// Callback registered by the viewport code-behind to perform the actual slice
    /// computation on a background thread.
    /// </summary>
    internal Func<Task>? OnSliceRequested { get; set; }

    /// <summary>Callback registered by the viewport code-behind to generate a relief-milling toolpath.</summary>
    internal Func<Task>? OnMillRequested { get; set; }

    /// <summary>Callback registered by the viewport code-behind to build + show the displaced surface.</summary>
    internal Func<Task>? OnPreviewDisplacedRequested { get; set; }

    /// <summary>Callback registered by the viewport code-behind to generate a multi-axis surface toolpath.</summary>
    internal Func<Task>? OnGenerateMultiAxisRequested { get; set; }

    /// <summary>Re-slices the source mesh at its current transform and replaces the selected toolpath.</summary>
    internal Func<Task>? OnUpdateSliceRequested { get; set; }

    /// <summary>Set by the viewport to gate <see cref="UpdateSliceCommand"/> when a parent mesh exists.</summary>
    internal Func<bool>? CanUpdateSlice { get; set; }

    /// <summary>Callback registered by the viewport code-behind to run the save-file dialog and write the KRL file.</summary>
    internal Func<Task>? OnExportKrlRequested { get; set; }
    /// <summary>Per-move extruder RPM report for the selected toolpath, including every
    /// stretch above the export limit. Wired by the viewport for the rpm-report command.</summary>
    internal Func<string>? OnRpmReportRequested { get; set; }

    internal Func<Task>? OnSendToRobotRequested { get; set; }

    /// <summary>Merges the currently shift-selected toolpaths into one exportable toolpath.</summary>
    internal Action? OnMergeToolpathsRequested { get; set; }

    /// <summary>Merges shift-selected outliner scans into one result.</summary>
    internal Action<ScanMergeOutput>? OnMergeScansRequested { get; set; }

    /// <summary>Re-merges the selected merged toolpath when connector settings change.</summary>
    internal Action? OnMergedSettingsChanged { get; set; }

    /// <summary>Selects a scene node when the user clicks it in the outliner.</summary>
    internal Action<SceneNode>? OnOutlinerSelectRequested { get; set; }

    /// <summary>Fired when the "Apply" action runs on a mesh's Modifiers group — the View does
    /// the actual fold-over-working-set split (needs OpenTK transform math + PlanarMeshSplitter,
    /// same as the Cut Tool), then reports results back through the normal node/outliner queues.</summary>
    internal Action<OutlinerItemViewModel>? OnApplyModifiersRequested { get; set; }

    /// <summary>Diagnostic: force the renderer to select a node directly, bypassing the
    /// RequestSceneSelection filtering (LFAM infrastructure blocking, tool resolution, etc.).</summary>
    internal Action<SceneNode?>? ForceSelectNode { get; set; }

    /// <summary>Highlights the last ctrl/shift-selected scan in the viewport without clearing multi-select.</summary>
    internal Action<SceneNode>? OnOutlinerMultiScanViewportSync { get; set; }

    /// <summary>Invoked when outliner scan multi-selection changes (viewport highlight sync).</summary>
    internal Action? OnScanSelectionChanged { get; set; }

    /// <summary>Merges the current outliner scan multi-selection (bypasses command CanExecute timing).</summary>
    internal void RequestMergeScans(ScanMergeOutput output)
    {
        if (!CanMergeScans) return;
        OnMergeScansRequested?.Invoke(output);
    }

    /// <summary>Mounts one LFAM 3 toolhead exclusively when its outliner row is clicked.</summary>
    internal Action<string>? OnOutlinerToolheadSelected { get; set; }

    internal Func<SceneNode, Task>? OnExportScanPointCloudRequested { get; set; }
    internal Func<SceneNode, Task>? OnExportScanMeshRequested { get; set; }

    /// <summary>Reloads an outliner model from its saved source path. Wired by MainWindow.</summary>
    internal Action<SceneNode>? OnModelReloadRequested { get; set; }

    /// <summary>Opens a file picker to replace the outliner model. Wired by MainWindow.</summary>
    internal Action<SceneNode>? OnModelReplaceRequested { get; set; }

    /// <summary>Returns the viewport's currently selected scene node (wired by the GL view).</summary>
    internal Func<SceneNode?>? GetSelectedSceneNode { get; set; }

    /// <summary>Callback registered by the viewport code-behind to deselect a node when it is hidden.</summary>
    internal Action<SceneNode>? OnNodeHidden { get; set; }

    /// <summary>
    /// Returns the world-space pose of a KUKA tool frame (TCP XYZ + ABC orientation)
    /// evaluated at the robot's current joint state, or <c>null</c> when no robot is
    /// loaded. Registered by the viewport code-behind; used to register scans
    /// captured by the flange-mounted Zivid camera into the scene.
    /// </summary>
    internal Func<ToolCellConfig, Matrix4?>? GetToolWorldPose { get; set; }

    /// <summary>
    /// Returns the current flange-to-world pose in the SAME convention used by
    /// <see cref="GetToolWorldPose"/> (rendered flange node × glTF→KUKA correction),
    /// as a row-vector <see cref="System.Numerics.Matrix4x4"/>, or <c>null</c> when no
    /// robot is loaded. Hand-eye calibration MUST use this — not the analytic FK — so
    /// the learned camera transform is expressed in the frame registration applies it in.
    /// </summary>
    internal Func<System.Numerics.Matrix4x4?>? GetFlangeInBaseForCalibration { get; set; }

    /// <summary>
    /// Invoked on the UI thread once a cell swap has fully completed and the tool
    /// library, bridge config, and IK data are up to date. Used to fire one-time
    /// startup actions (tool selection, auto-sync).
    /// </summary>
    internal Action<int>? OnCellSwapCompleted { get; set; }

    /// <summary>Viewport plays a KUKA tool-change path overlay (Pick/Deposit simulation).</summary>
    internal Action<string>? OnSimulateToolChangeRequested { get; set; }

    internal Action? OnToggleToolChangePlaybackRequested { get; set; }
    internal Action? OnCollapseToolChangePlaybackRequested { get; set; }
    internal Action<int>? OnToolChangeScrubRequested { get; set; }

    /// <summary>Triggers a planar slice using the current additive settings.</summary>
    public RelayCommand SliceCommand { get; }

    /// <summary>Generates a relief-milling toolpath from the subtractive settings' heightmap.</summary>
    public RelayCommand MillCommand { get; }

    /// <summary>Builds the displaced surface (low-poly mesh + PBR map detail) and adds it to the scene.</summary>
    public RelayCommand PreviewDisplacedCommand { get; }

    /// <summary>Generates a multi-axis surface-following finish toolpath over the displaced surface.</summary>
    public RelayCommand GenerateMultiAxisCommand { get; }

    /// <summary>Re-slices the parent mesh at its current pose and replaces the selected toolpath.</summary>
    public RelayCommand UpdateSliceCommand { get; }

    /// <summary>Opens a save dialog and exports the selected toolpath as a KUKA KRL .src file.</summary>
    public RelayCommand ExportKrlCommand { get; }

    /// <summary>Exports KRL to the active cell's robot D: share via a pre-targeted save dialog.</summary>
    public RelayCommand SendToRobotCommand { get; }

    /// <summary>Merges shift-selected toolpaths into one continuous toolpath.</summary>
    public RelayCommand MergeToolpathsCommand { get; }

    /// <summary>Merges shift-selected scans into one aligned point cloud.</summary>
    public RelayCommand MergeScansAsPointCloudCommand { get; }

    /// <summary>Merges shift-selected scans into one aligned triangle mesh.</summary>
    public RelayCommand MergeScansAsMeshCommand { get; }

    // -- Outliner / user scene objects -----------------------------------------

    /// <summary>User-imported scene objects shown in the outliner panel.</summary>
    public ObservableCollection<OutlinerItemViewModel> OutlinerItems { get; } = [];

    /// <summary>Nodes queued for GL-thread removal and GPU resource disposal.</summary>
    public ConcurrentQueue<SceneNode> PendingRemoveNodes { get; } = new();

    /// <summary>Nodes whose subtree was reloaded on the UI thread and need GPU refresh.</summary>
    public ConcurrentQueue<SceneNode> PendingModelRefresh { get; } = new();

    /// <summary>
    /// Layer boundary data queued after each slice so the GL thread can upload the
    /// layer-preview heatmap texture. zBounds has numLayers+1 entries (sorted);
    /// heights has numLayers entries, one thickness per layer.
    /// </summary>
    public ConcurrentQueue<(float[] zBounds, float[] heights)> PendingLayerPreview { get; } = new();

    /// <summary>
    /// Enqueues <paramref name="node"/> for GPU upload and registers it in the outliner.
    /// Must be called on the UI thread.
    /// </summary>
    public void AddUserNode(SceneNode node)
    {
        PendingNodes.Enqueue(node);
        RegisterOutlinerItem(node);
        SliceCommand.RaiseCanExecuteChanged();
        NotifyRenderNeeded();
    }

    /// <summary>Scans waiting to be parented under the rotary-bed E1 pivot on the GL thread.</summary>
    public ConcurrentQueue<SceneNode> PendingRotaryNodes { get; } = new();

    private OutlinerItemViewModel? _rotaryGroupItem;
    private SceneNode? _rotaryPivotForScans;
    private readonly List<OutlinerItemViewModel> _cellEnvOutlinerItems = [];
    private OutlinerItemViewModel? _toolheadGroupItem;
    private readonly List<OutlinerItemViewModel> _toolheadOutlinerItems = [];
    private readonly Dictionary<OutlinerItemViewModel, string> _toolheadNames = [];

    /// <summary>True when the active cell has a rotary bed, so scans should ride the turntable.</summary>
    public bool HasRotaryBed => _rotaryPivotForScans is not null;

    /// <summary>
    /// Exposes the rotary bed as a top-level outliner group that scans nest under. The group is backed
    /// by the E1 pivot node, so anything parented to it (in the scene graph) rotates with the table.
    /// Call on cell swap (UI thread); pass null pivot to clear (non-rotary cell).
    /// </summary>
    internal void SetRotaryBedGroup(SceneNode? pivot, string displayName)
    {
        _rotaryPivotForScans = pivot;
        if (_rotaryGroupItem is not null) { OutlinerItems.Remove(_rotaryGroupItem); _rotaryGroupItem = null; }
        if (pivot is null) return;
        // The group itself isn't deletable; visibility toggling falls through to the pivot node.
        _rotaryGroupItem = new OutlinerItemViewModel(pivot, NotifyRenderNeeded, _ => { }, null, displayName, canDelete: false);
        _rotaryGroupItem.IsLocked = true;
        OutlinerItems.Add(_rotaryGroupItem);
    }

    /// <summary>LFAM 3 multi-tool rows under a top-level "Toolheads" group (exclusive visibility on click).</summary>
    internal void SetMultiToolOutliner(IReadOnlyList<(string ToolName, SceneNode FlangeNode)> tools)
    {
        ClearMultiToolOutliner();
        if (tools.Count == 0) return;

        var groupNode = new SceneNode
        {
            Name       = "Toolheads",
            Selectable = false,
            Visible    = true,
            PickTier   = PickTier.Environment,
        };

        _toolheadGroupItem = new OutlinerItemViewModel(
            groupNode, NotifyRenderNeeded, _ => { }, null, "Toolheads", canDelete: false);

        foreach (var (toolName, flangeNode) in tools)
        {
            var item = new OutlinerItemViewModel(
                flangeNode,
                NotifyRenderNeeded,
                _ => { },
                null,
                toolName,
                canDelete: false,
                usesExclusiveVisibility: true);
            item.IsLocked = true;
            _toolheadGroupItem.AddChild(item);
            _toolheadOutlinerItems.Add(item);
            _toolheadNames[item] = toolName;
        }

        if (_robotArmItem is not null)
        {
            // Chain: Robot Root -> Pedestal -> Robot Arm -> Toolheads.
            _robotArmItem.AddChild(_toolheadGroupItem);
        }
        else
        {
            int insertAt = _robotGroupItem is not null
                ? OutlinerItems.IndexOf(_robotGroupItem) + 1
                : OutlinerItems.Count;
            OutlinerItems.Insert(insertAt, _toolheadGroupItem);
        }
    }

    void ClearMultiToolOutliner()
    {
        if (_toolheadGroupItem is not null)
        {
            OutlinerItems.Remove(_toolheadGroupItem);
            _robotArmItem?.RemoveChild(_toolheadGroupItem);
        }
        _toolheadGroupItem = null;
        _toolheadOutlinerItems.Clear();
        _toolheadNames.Clear();
    }

    internal bool IsToolheadGroupItem(OutlinerItemViewModel item) => item == _toolheadGroupItem;

    internal bool TryGetToolheadName(OutlinerItemViewModel item, out string toolName)
        => _toolheadNames.TryGetValue(item, out toolName!);

    /// <summary>Highlights the active LFAM 3 toolhead row; pass null to clear.</summary>
    internal void SetActiveToolheadOutliner(string? toolName)
    {
        foreach (var item in _toolheadOutlinerItems)
        {
            var active = toolName is not null
                         && _toolheadNames.TryGetValue(item, out var name)
                         && name == toolName;
            item.IsOutlinerSelected = active;
        }
    }

    /// <summary>Registers stands and flat print-bed meshes in the outliner (visibility only, not deletable).
    /// Tool stands nest under Robot Root when present; print bed stays top-level.</summary>
    internal void SetCellEnvironmentOutliner(IEnumerable<(SceneNode Node, string DisplayName)> entries)
    {
        foreach (var item in _cellEnvOutlinerItems)
        {
            OutlinerItems.Remove(item);
            _robotGroupItem?.RemoveChild(item);
        }
        _cellEnvOutlinerItems.Clear();

        foreach (var (node, displayName) in entries)
        {
            var item = new OutlinerItemViewModel(node, NotifyRenderNeeded, _ => { }, null, displayName, canDelete: false)
            { IsLocked = true };
            // Stands live under Robot Root in the outliner (siblings of Pedestal / Arm).
            if (_robotGroupItem is not null && IsCellStandDisplayName(displayName))
                _robotGroupItem.AddChild(item);
            else
                OutlinerItems.Add(item);
            _cellEnvOutlinerItems.Add(item);
        }
    }

    static bool IsCellStandDisplayName(string displayName) =>
        displayName is "Extruder Stand" or "Scanner Stand" or "Spindle Stand"
        || displayName.EndsWith(" Stand", StringComparison.Ordinal);

    /// <summary>Locks/unlocks the cell-environment rows whose scene node is dev-editable
    /// (print bed, stands) — dev mode unlocks them so they can be selected and transformed.</summary>
    internal void SetCellEnvironmentDevLock(Func<SceneNode, bool> isDevNode, bool locked)
    {
        foreach (var item in _cellEnvOutlinerItems)
            if (isDevNode(item.Node))
                item.IsLocked = locked;
    }

    private OutlinerItemViewModel? _robotGroupItem;
    private OutlinerItemViewModel? _robotPedestalItem;
    private OutlinerItemViewModel? _robotArmItem;

    /// <summary>
    /// Exposes the robot as a selectable outliner group "Robot Root" with "Robot Pedestal" and
    /// "Robot Arm" as direct children, each backed by the real scene node (so selection + visibility
    /// work). Not deletable. The outliner renders one level of children, so Pedestal/Arm are siblings
    /// under Root rather than further nested. Call on cell swap (UI thread); pass null root to clear.
    /// </summary>
    internal void SetRobotGroup(SceneNode? root, SceneNode? pedestal, SceneNode? arm)
    {
        // Detach stand rows before dropping the group so SetCellEnvironmentOutliner can re-home them.
        if (_robotGroupItem is not null)
        {
            foreach (var env in _cellEnvOutlinerItems)
                _robotGroupItem.RemoveChild(env);
            OutlinerItems.Remove(_robotGroupItem);
            _robotGroupItem = null;
        }
        _robotPedestalItem = null;
        _robotArmItem      = null;
        if (root is null) return;

        _robotGroupItem = new OutlinerItemViewModel(root, NotifyRenderNeeded, _ => { }, null, "Robot Root", canDelete: false);
        _robotGroupItem.IsLocked = true;
        _robotGroupItem.IsExpanded = false;
        OutlinerItems.Add(_robotGroupItem);

        if (pedestal is not null)
        {
            _robotPedestalItem = new OutlinerItemViewModel(pedestal, NotifyRenderNeeded, _ => { }, null, "Robot Pedestal", canDelete: false) { IsLocked = true };
            _robotGroupItem.AddChild(_robotPedestalItem);
        }
        if (arm is not null)
        {
            // Chain: Root -> Pedestal -> Arm (arm nests under the pedestal when present).
            _robotArmItem = new OutlinerItemViewModel(arm, NotifyRenderNeeded, _ => { }, null, "Robot Arm", canDelete: false) { IsLocked = true };
            (_robotPedestalItem ?? _robotGroupItem).AddChild(_robotArmItem);
        }

        // Re-parent any already-registered stands under the new Robot Root.
        foreach (var env in _cellEnvOutlinerItems)
        {
            if (!IsCellStandDisplayName(env.Name)) continue;
            OutlinerItems.Remove(env);
            _robotGroupItem.AddChild(env);
        }
    }

    /// <summary>
    /// Syncs Robot Root / Pedestal / Arm outliner eye state from the live scene nodes
    /// (e.g. after 2D slice view temporarily hides the robot).
    /// </summary>
    internal void RefreshRobotOutlinerVisibilityFromScene()
    {
        _robotGroupItem?.NotifyVisibilityFromScene();
        _robotPedestalItem?.NotifyVisibilityFromScene();
        _robotArmItem?.NotifyVisibilityFromScene();
    }

    /// <summary>
    /// Adds a scan result. Outliner: direct child of the rotary-bed group (sibling to imports).
    /// Scene: on a rotary cell, parented to the E1 pivot so it tracks E1.
    /// Must be called on the UI thread.
    /// </summary>
    /// <summary>Ctrl-click toggle for scan multi-selection in the outliner.</summary>
    internal void ToggleScanOutlinerSelection(OutlinerItemViewModel item)
    {
        if (!OutlinerModelOps.IsScanItem(item)) return;

        if (_selectedScanItems.Contains(item))
            _selectedScanItems.Remove(item);
        else
            _selectedScanItems.Add(item);

        RefreshCanMergeScans();
    }

    /// <summary>Clears multi-selected scan rows (e.g. after merge or plain click).</summary>
    internal void ClearScanOutlinerSelection()
    {
        _selectedScanItems.Clear();
        _scanSelectionAnchor = null;
        RefreshCanMergeScans();
    }

    /// <summary>Highlights the matching outliner row (scan → blue row; import → accent row).</summary>
    internal void SetOutlinerSelection(SceneNode? node)
    {
        // Viewport-driven sync must not wipe ctrl/shift multi-select in the outliner.
        if (_selectedScanItems.Count >= 2)
        {
            RefreshScanSelectionVisuals();
            return;
        }

        var item = FindOutlinerItemForSelection(node);

        if (_selectedOutlinerItem is not null
            && !OutlinerModelOps.IsScanItem(_selectedOutlinerItem)
            && !OutlinerModelOps.IsToolheadItem(_selectedOutlinerItem))
            _selectedOutlinerItem.IsOutlinerSelected = false;

        _selectedOutlinerItem = item;
        OnPropertyChanged(nameof(SelectedOutlinerItem));
        OnPropertyChanged(nameof(SelectedModifierOwner));
        if (item is null) return;

        if (OutlinerModelOps.IsScanItem(item))
        {
            SetActiveToolheadOutliner(null);
            if (!_selectedScanItems.Contains(item))
            {
                ClearScanOutlinerSelection();
                _selectedScanItems.Add(item);
                _scanSelectionAnchor = item;
            }

            RefreshCanMergeScans();
            return;
        }

        ClearScanOutlinerSelection();

        if (OutlinerModelOps.IsToolheadItem(item))
        {
            SetActiveToolheadOutliner(_toolheadNames.TryGetValue(item, out var toolName) ? toolName : null);
            return;
        }

        SetActiveToolheadOutliner(null);
        item.IsOutlinerSelected = true;

        // Selecting an effector, a modifier plane, or a whole Modifiers group arms the move
        // gizmo immediately (same convention as the cut tool) — placing it is the whole point
        // of selecting it. Selecting the group moves/rotates every modifier under it together,
        // for free, via ordinary parent-child transforms.
        if ((item.IsEffector || item.IsModifier || item.IsModifiersGroup)
            && (ActiveGizmoModeInternal == GizmoMode.None || ActiveGizmoModeInternal == GizmoMode.Scale))
            ActiveGizmoModeInternal = GizmoMode.Translate;
    }

    internal void OnOutlinerScanClicked(OutlinerItemViewModel item, bool shiftHeld, bool ctrlHeld)
    {
        if (!OutlinerModelOps.IsScanItem(item))
        {
            ClearScanOutlinerSelection();
            return;
        }

        FlattenScansToBedGroup();

        if (shiftHeld && _scanSelectionAnchor is not null)
        {
            SelectScanRange(_scanSelectionAnchor, item);
            _scanSelectionAnchor = item;
            return;
        }

        if (ctrlHeld)
        {
            ToggleScanOutlinerSelection(item);
            _scanSelectionAnchor = item;
            return;
        }

        ClearScanOutlinerSelection();
        _selectedScanItems.Add(item);
        _scanSelectionAnchor = item;
        RefreshCanMergeScans();
    }

    void SelectScanRange(OutlinerItemViewModel anchor, OutlinerItemViewModel end)
    {
        var siblings = EnumerateAllScanItems().ToList();
        int a = siblings.IndexOf(anchor);
        int b = siblings.IndexOf(end);
        if (a < 0 || b < 0)
        {
            ClearScanOutlinerSelection();
            _selectedScanItems.Add(anchor);
            _selectedScanItems.Add(end);
            RefreshCanMergeScans();
            return;
        }

        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        ClearScanOutlinerSelection();
        for (int i = lo; i <= hi; i++)
            _selectedScanItems.Add(siblings[i]);
        RefreshCanMergeScans();
    }

    internal IReadOnlyList<OutlinerItemViewModel> GetBedLevelScanItems()
    {
        if (_rotaryGroupItem is not null)
            return _rotaryGroupItem.Children.Where(OutlinerModelOps.IsScanItem).ToList();

        return OutlinerItems.Where(OutlinerModelOps.IsScanItem).ToList();
    }

    /// <summary>
    /// Ensures every scan is a direct child of the rotary-bed group so they stay individually selectable.
    /// </summary>
    internal void FlattenScansToBedGroup()
    {
        if (_rotaryGroupItem is null) return;

        var nested = new List<OutlinerItemViewModel>();
        CollectNestedScans(_rotaryGroupItem, nested);
        foreach (var scan in nested)
        {
            if (FindParentOutlinerItem(scan) is { } parent)
                parent.RemoveChild(scan);
            if (!_rotaryGroupItem.Children.Contains(scan))
                _rotaryGroupItem.AddChild(scan);
        }
    }

    static void CollectNestedScans(OutlinerItemViewModel item, List<OutlinerItemViewModel> nested)
    {
        foreach (var child in item.Children)
        {
            if (OutlinerModelOps.IsScanItem(child))
                nested.Add(child);
            else
                CollectNestedScans(child, nested);
        }
    }

    private void RefreshCanMergeScans()
    {
        CanMergeScans = _selectedScanItems.Count >= 2
                        && _selectedScanItems.All(OutlinerModelOps.IsScanItem);
        OnPropertyChanged(nameof(SelectedScanCount));
        RefreshScanSelectionVisuals();
        OnScanSelectionChanged?.Invoke();
    }

    void RefreshScanSelectionVisuals()
    {
        foreach (var item in EnumerateAllContentItems())
        {
            if (!OutlinerModelOps.IsScanItem(item)) continue;
            item.IsScanMultiSelected = _selectedScanItems.Contains(item);
        }
    }

    IEnumerable<OutlinerItemViewModel> EnumerateAllScanItems()
    {
        foreach (var item in EnumerateAllContentItems())
        {
            if (OutlinerModelOps.IsScanItem(item))
                yield return item;
        }
    }

    public void AddScanNode(SceneNode node)
    {
        // Ensure lime translucent look even for restored STLs / re-meshed ZDFs.
        ApplyScanAppearance(node);
        FlattenScansToBedGroup();
        var parentObject = _rotaryGroupItem;
        EnqueueRotarySceneNode(node);

        var item = CreateOutlinerItem(node, child =>
        {
            parentObject?.RemoveChild(child);
            PendingRemoveNodes.Enqueue(child.Node);
            NotifyRenderNeeded();
        }, () => OnNodeHidden?.Invoke(node), modelFileOps: true);

        if (parentObject is not null)
            parentObject.AddChild(item);
        else
            OutlinerItems.Add(item);

        SliceCommand.RaiseCanExecuteChanged();
        NotifyRenderNeeded();
    }

    /// <summary>
    /// Lime green (opaque) scan look. Applied for live captures, ZDF recover, and workspace restore.
    /// </summary>
    internal static void ApplyScanAppearance(SceneNode node)
    {
        // #8CFF26 ≈ (0.55, 1.00, 0.15)
        var lime = new OpenTK.Mathematics.Vector4(0.55f, 1.00f, 0.15f, 1f);
        foreach (var n in node.SelfAndDescendants())
        {
            n.CullFaces       = false;
            n.TranslucentPass = false;
            n.KeepOwnMaterial = true;
            if (n.Mesh is { } gpu)
            {
                gpu.Color        = lime;
                gpu.AlphaModeInt = (int)MassiveSlicer.Viewport.Scene.AlphaMode.Opaque;
            }
            // Restored STLs often have non-lime PendingMesh — re-stamp before GPU upload.
            if (n.PendingMesh is { } pm
                && (pm.BaseColor.X < 0.5f || pm.BaseColor.Y < 0.9f || pm.BaseColor.W < 0.99f))
            {
                n.PendingMesh = new MassiveSlicer.Viewport.Scene.MeshData(
                    pm.Positions, pm.Normals, pm.Indices, pm.Name,
                    lime, 0f, 0.85f);
            }
        }
    }

    private void EnqueueRotarySceneNode(SceneNode node)
    {
        if (_rotaryPivotForScans is null)
            PendingNodes.Enqueue(node);
        else
            PendingRotaryNodes.Enqueue(node);
    }

    /// <summary>
    /// Picks the active print object — selected user import, else the sole model on the bed.
    /// Used for scan nesting, adaptive layer preview, and slice targeting.
    /// </summary>
    internal OutlinerItemViewModel? ResolveActivePrintObjectItem()
    {
        if (GetSelectedSceneNode?.Invoke() is { } selected)
        {
            var selectedItem = FindUserMeshOutlinerItem(selected);
            if (selectedItem is not null && !selectedItem.IsEffector
                && !OutlinerModelOps.IsScanItem(selectedItem))
                return selectedItem;
        }

        var models = EnumerateUserModelItems().Where(i => i.Visible).ToList();
        return models.Count == 1 ? models[0] : null;
    }

    /// <summary>
    /// Outliner parent for imported/sliced toolpaths: active print object when present,
    /// otherwise the rotary-bed group (so KRL imports don't land at the top level).
    /// </summary>
    internal OutlinerItemViewModel? ResolveToolpathParentOutlinerItem()
        => ResolveActivePrintObjectItem() ?? _rotaryGroupItem;

    /// <summary>
    /// Clears layer-preview shading from cell groups and user objects, then enables it on the active print object.
    /// </summary>
    internal void SyncLayerPreviewFlags(bool enabled)
    {
        foreach (var item in OutlinerItems)
            ClearLayerPreviewOnOutlinerItem(item);

        if (enabled && ResolveActivePrintObjectItem() is { } target)
            target.Node.LayerPreview = true;
    }

    private static void ClearLayerPreviewOnOutlinerItem(OutlinerItemViewModel item)
    {
        item.Node.LayerPreview = false;
        foreach (var child in item.Children)
            ClearLayerPreviewOnOutlinerItem(child);
    }

    /// <summary>
    /// Adds imported user geometry. On a rotary cell it nests under the rotary group and is parented
    /// to the E1 pivot (so bed rotation is reflected in <see cref="SceneNode.WorldTransform"/> for
    /// toolpath generation); otherwise it falls back to <see cref="AddUserNode"/>.
    /// Must be called on the UI thread.
    /// </summary>
    public void AddImportNode(SceneNode node)
    {
        AddRotaryBedChildNode(node);
        OnModelGeometryChanged?.Invoke();
    }

    private void AddRotaryBedChildNode(SceneNode node, OutlinerItemViewModel? adoptToolpathsFrom = null)
    {
        EnqueueRotarySceneNode(node);

        if (_rotaryPivotForScans is null || _rotaryGroupItem is null)
        {
            var rootItem = RegisterOutlinerItem(node);
            AdoptToolpaths(adoptToolpathsFrom, rootItem);
            SliceCommand.RaiseCanExecuteChanged();
            NotifyRenderNeeded();
            return;
        }

        var item = CreateOutlinerItem(node, child =>
        {
            _rotaryGroupItem?.RemoveChild(child);
            PendingRemoveNodes.Enqueue(child.Node);
            NotifyRenderNeeded();
        }, () => OnNodeHidden?.Invoke(node), modelFileOps: true);
        AttachVisibilityCascade(item);
        _rotaryGroupItem.AddChild(item);
        AdoptToolpaths(adoptToolpathsFrom, item);
        SliceCommand.RaiseCanExecuteChanged();
        NotifyRenderNeeded();
    }

    private static void AdoptToolpaths(OutlinerItemViewModel? from, OutlinerItemViewModel to)
    {
        if (from is null) return;
        foreach (var child in from.Children.ToList())
        {
            from.RemoveChild(child);
            to.AddChild(child);
        }
    }

    /// <summary>
    /// Maps any outliner item to the user model that owns it: toolpath children resolve to their
    /// parent model; a modifier, or its whole Modifiers group, resolves to the mesh it's really
    /// nested under (outliner nesting mirrors the real scene-graph parenting exactly, so this is
    /// just walking up the tree); model items return themselves.
    /// </summary>
    internal OutlinerItemViewModel? OwningModelItem(OutlinerItemViewModel? item)
    {
        if (item is null) return null;
        if (item.IsModifier || item.IsModifiersGroup)
        {
            var groupItem = item.IsModifiersGroup ? item : FindParentOutlinerItem(item);
            return groupItem is null ? null : FindParentOutlinerItem(groupItem);
        }
        if (!item.IsToolpath) return item;
        return EnumerateUserModelItems().FirstOrDefault(m => m.Children.Contains(item));
    }

    /// <summary>
    /// The model that owns whatever's currently selected (itself, or via its toolpath) —
    /// null if nothing selected or the selection isn't part of any model. Drives the
    /// Modifiers panel, which shows/edits that model's modifier stack.
    /// </summary>
    public OutlinerItemViewModel? SelectedModifierOwner => OwningModelItem(_selectedOutlinerItem);

    /// <summary>True when the outliner row backing <paramref name="node"/> is locked.</summary>
    internal bool IsNodeLockedInOutliner(SceneNode node)
    {
        bool Search(IEnumerable<OutlinerItemViewModel> items)
        {
            foreach (var item in items)
            {
                if (item.Node == node) return item.IsLocked;
                if (Search(item.Children)) return true;
            }
            return false;
        }
        return Search(OutlinerItems);
    }

    /// <summary>Yields outliner entries for user-imported print models (excludes scans).</summary>
    internal IEnumerable<OutlinerItemViewModel> EnumerateUserModelItems()
    {
        foreach (var item in OutlinerItems)
        {
            if (item == _rotaryGroupItem)
            {
                foreach (var child in item.Children)
                {
                    if (!OutlinerModelOps.IsScanItem(child) && !child.IsEffector
                        && !child.IsModifier && !child.IsModifiersGroup && !child.IsPiecesGroup)
                        yield return child;
                }
                continue;
            }
            if (item == _robotGroupItem) continue;
            if (item == _toolheadGroupItem) continue;
            if (_cellEnvOutlinerItems.Contains(item)) continue;
            if (item.IsEffector) continue;
            if (item.IsModifier) continue;
            if (item.IsModifiersGroup) continue;
            if (item.IsPiecesGroup)
            {
                // The group itself is just a label (see CreateAppliedPiecesGroup) — the pieces
                // inside are real, independent models and must be slicable/exportable/arrangeable
                // like any other, so surface THEM, not the group.
                foreach (var piece in item.Children)
                    yield return piece;
                continue;
            }
            if (!OutlinerModelOps.IsScanItem(item))
                yield return item;
        }
    }

    /// <summary>
    /// Returns the outliner item that owns <paramref name="node"/> (import, scan, toolpath, etc.),
    /// not the rotary-bed group whose scene subtree also contains the turntable mesh. A modifier
    /// (or its Modifiers group) is a real SceneNode child of its owning mesh now, but must NOT
    /// resolve up to that mesh here — it's an independently selectable object in its own right,
    /// not an anonymous mesh-leaf shard.
    /// </summary>
    internal OutlinerItemViewModel? FindUserMeshOutlinerItem(SceneNode? node)
    {
        if (node is null) return null;
        if (IsModifierNode(node) || IsModifiersGroupNode(node)) return null;
        foreach (var item in EnumerateAllContentItems())
        {
            if (item.Node == node || item.Node.SelfAndDescendants().Any(n => n == node))
                return item;
        }
        return null;
    }

    IEnumerable<OutlinerItemViewModel> EnumerateAllContentItems()
    {
        foreach (var item in OutlinerItems)
        {
            if (item == _robotGroupItem) continue;
            if (item == _toolheadGroupItem) continue;
            if (_cellEnvOutlinerItems.Contains(item)) continue;
            if (item == _rotaryGroupItem)
            {
                foreach (var child in EnumerateOutlinerDescendants(item))
                    yield return child;
                continue;
            }
            yield return item;
            foreach (var child in EnumerateOutlinerDescendants(item))
                yield return child;
        }
    }

    static IEnumerable<OutlinerItemViewModel> EnumerateOutlinerDescendants(OutlinerItemViewModel item)
    {
        foreach (var child in item.Children)
        {
            yield return child;
            foreach (var desc in EnumerateOutlinerDescendants(child))
                yield return desc;
        }
    }

    /// <summary>
    /// True when <paramref name="node"/> belongs to a user import or scan. Such nodes are often
    /// parented under the LFAM bed / rotary pivot and must not be treated as cell infrastructure.
    /// </summary>
    internal bool IsUserModelSceneNode(SceneNode? node)
    {
        if (node is null) return false;

        foreach (var item in EnumerateUserModelItems())
        {
            if (item.Node == node || item.Node.SelfAndDescendants().Any(n => n == node))
                return true;
        }

        // Scans and toolpaths are also user content parented under the rotary pivot.
        return FindUserMeshOutlinerItem(node) is not null;
    }

    // ── Bridge/console selection + object diagnostics ─────────────────────────

    /// <summary>Dumps the full outliner hierarchy (real parent/child nesting, not the flattened
    /// EnumerateUserModelItems view) with indentation and per-row flags — built to verify
    /// structural fixes (e.g. Modifiers/Applied-Pieces group persistence) without needing a
    /// screenshot. Backs the console/bridge `outliner-tree` command.</summary>
    public string DescribeOutlinerTree()
    {
        var sb = new System.Text.StringBuilder();
        void Walk(OutlinerItemViewModel item, int depth)
        {
            var flags = new List<string>();
            if (item.IsModifiersGroup) flags.Add("ModifiersGroup");
            if (item.IsPiecesGroup) flags.Add("PiecesGroup");
            if (item.IsModifier) flags.Add("Modifier");
            if (item.IsToolpath) flags.Add("Toolpath");
            if (item.IsEffector) flags.Add("Effector");
            if (!item.Visible) flags.Add("Hidden");
            string flagStr = flags.Count == 0 ? "" : $" [{string.Join(",", flags)}]";
            sb.AppendLine($"{new string(' ', depth * 2)}- {item.Name}{flagStr}");
            foreach (var child in item.Children)
                Walk(child, depth + 1);
        }
        foreach (var root in OutlinerItems)
            Walk(root, 0);
        return sb.Length == 0 ? "[outliner-tree] (empty)" : sb.ToString().TrimEnd();
    }

    /// <summary>Lists user-content outliner items (imports/scans/toolpaths) with mesh + pick info.
    /// Backs the console/bridge `objects` command.</summary>
    public string ListContentObjects()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        int i = 0;
        foreach (var item in EnumerateAllContentItems())
        {
            var (nodes, meshes, picking, pending) = SummarizeSubtree(item.Node);
            var p = item.Node.WorldTransform.Row3;
            sb.AppendLine(string.Format(inv,
                "[{0}] \"{1}\"  nodes={2} mesh={3} pickData={4} pending={5} selectable={6} tier={7} pos=({8:F0},{9:F0},{10:F0})",
                i++, item.Name, nodes, meshes, picking, pending, item.Node.Selectable, item.Node.PickTier, p.X, p.Y, p.Z));
        }
        return i == 0 ? "[objects] (no user content)" : sb.ToString().TrimEnd();
    }

    /// <summary>Selects a content object by partial (case-insensitive) name through the outliner
    /// selection path, then reports what the renderer ended up selecting. Backs `select`.</summary>
    public string SelectByName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return "[select] usage: select <name>";

        OutlinerItemViewModel? match = null;
        foreach (var item in EnumerateAllContentItems())
            if (item.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) { match = item; break; }
        if (match is null)
            return $"[select] no content object matching '{name}'. Run `objects` to list them.";

        OnOutlinerSelectRequested?.Invoke(match.Node);
        var sel = GetSelectedSceneNode?.Invoke();
        return $"[select] \"{match.Name}\" -> renderer.SelectedNode={(sel is null ? "null" : $"\"{sel.Name}\"")}.";
    }

    /// <summary>Reports the renderer's current selection (what would be highlighted). Backs `selection`.</summary>
    public string DescribeSelection()
    {
        var sel = GetSelectedSceneNode?.Invoke();
        if (sel is null) return "[selection] nothing selected (renderer.SelectedNode = null).";
        var (nodes, meshes, picking, pending) = SummarizeSubtree(sel);
        var p = sel.WorldTransform.Row3;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[selection] \"{0}\"  nodes={1} mesh={2} pickData={3} pending={4} selectable={5} tier={6} userContent={7} pos=({8:F0},{9:F0},{10:F0})",
            sel.Name, nodes, meshes, picking, pending, sel.Selectable, sel.PickTier,
            FindUserMeshOutlinerItem(sel) is not null, p.X, p.Y, p.Z);
    }

    static (int Nodes, int Meshes, int Picking, int Pending) SummarizeSubtree(SceneNode root)
    {
        int n = 0, m = 0, pick = 0, pend = 0;
        foreach (var node in root.SelfAndDescendants())
        {
            n++;
            if (node.Mesh is not null) m++;
            if (node.Mesh?.PickingData is not null) pick++;
            if (node.PendingMesh is not null) pend++;
        }
        return (n, m, pick, pend);
    }

    // ── Rotary scan diagnostics ───────────────────────────────────────────────
    // Stash each registered scan's capture-time WORLD points + E1 so we can export them and solve
    // the true rotary model (axis tilt / deg-per-E1 / phase / centre) offline against the scans.
    private readonly List<(string Name, float E1, float[] WorldXyz)> _scanDiag = [];

    /// <summary>Number of scans stashed for the diagnostic export.</summary>
    public int ScanDiagCount => _scanDiag.Count;

    /// <summary>
    /// Records a scan's points (mesh/camera frame) mapped to WORLD via <paramref name="worldPose"/>,
    /// plus its capture E1. Decimated to keep the export light. Call at capture time, before the node
    /// is reparented under the E1 pivot (so the pose is the clean camera→world transform).
    /// </summary>
    public void StashScanDiag(string name, float e1, IReadOnlyList<Vector3> camPoints, Matrix4 worldPose)
    {
        if (camPoints is null || camPoints.Count == 0) return;
        const int target = 8000;
        int step = Math.Max(1, camPoints.Count / target);
        var xyz = new List<float>(target * 3);
        for (int i = 0; i < camPoints.Count; i += step)
        {
            var p = camPoints[i];
            float wx = p.X * worldPose.M11 + p.Y * worldPose.M21 + p.Z * worldPose.M31 + worldPose.M41;
            float wy = p.X * worldPose.M12 + p.Y * worldPose.M22 + p.Z * worldPose.M32 + worldPose.M42;
            float wz = p.X * worldPose.M13 + p.Y * worldPose.M23 + p.Z * worldPose.M33 + worldPose.M43;
            if (float.IsNaN(wx) || float.IsNaN(wy) || float.IsNaN(wz)) continue;
            xyz.Add(wx); xyz.Add(wy); xyz.Add(wz);
        }
        _scanDiag.Add((name, e1, xyz.ToArray()));
    }

    /// <summary>
    /// Records already-transformed world XYZ (e.g. from <see cref="ScanPointCloudTransform.ToWorld"/>).
    /// Decimated for export size.
    /// </summary>
    public void StashScanDiagWorld(string name, float e1, float[] worldXyz)
    {
        if (worldXyz is null || worldXyz.Length < 3) return;
        _scanDiag.Add((name, e1, ScanPointCloudTransform.Decimate(worldXyz)));
    }

    /// <summary>Clears stashed diagnostic scans (e.g. before a new bed-cal run).</summary>
    public void ClearScanDiag() => _scanDiag.Clear();

    /// <summary>
    /// Writes the stashed scans (one .xyz of world points each) + a manifest (per-scan E1, the rotary
    /// rotation centre, and sign) to <paramref name="dir"/> for offline calibration analysis.
    /// </summary>
    public string ExportScanDiag(string dir, Vector3 center, float sign)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Directory.CreateDirectory(dir);
        var man = new System.Text.StringBuilder();
        man.Append("{\n");
        man.Append($"  \"rotaryCenter\": [{center.X.ToString("F4", inv)}, {center.Y.ToString("F4", inv)}, {center.Z.ToString("F4", inv)}],\n");
        man.Append($"  \"rotationSign\": {sign.ToString("F0", inv)},\n");
        man.Append("  \"scans\": [\n");
        for (int s = 0; s < _scanDiag.Count; s++)
        {
            var (name, e1, xyz) = _scanDiag[s];
            var safe = new string([.. name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')]);
            var file = $"{s:D2}_{safe}.xyz";
            var sb = new System.Text.StringBuilder(xyz.Length * 8);
            for (int i = 0; i + 2 < xyz.Length; i += 3)
                sb.Append(xyz[i].ToString("F2", inv)).Append(' ')
                  .Append(xyz[i + 1].ToString("F2", inv)).Append(' ')
                  .Append(xyz[i + 2].ToString("F2", inv)).Append('\n');
            File.WriteAllText(Path.Combine(dir, file), sb.ToString());
            man.Append($"    {{ \"file\": \"{file}\", \"e1\": {e1.ToString("F4", inv)}, \"points\": {xyz.Length / 3} }}{(s < _scanDiag.Count - 1 ? "," : "")}\n");
        }
        man.Append("  ]\n}\n");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), man.ToString());
        return $"{_scanDiag.Count} scans → {dir}";
    }

    /// <summary>
    /// Registers an already-uploaded scene node in the outliner and queues it for
    /// attachment to the scene root on the GL thread.
    /// </summary>
    internal void AttachUserNode(SceneNode node, OutlinerItemViewModel? adoptToolpathsFrom = null)
        => AddRotaryBedChildNode(node, adoptToolpathsFrom);

    /// <summary>
    /// Adds an imported KRL toolpath as a scrubbable outliner node under the active print object
    /// or rotary-bed group (same nesting as slice-generated toolpaths). Must be called on the UI thread.
    /// </summary>
    public void AddImportedToolpath(MassiveSlicer.Core.Models.Toolpath tp, string name, float beadWidth = 6f)
    {
        var node = new SceneNode { Name = name, Selectable = true };
        RegisterToolpathInOutliner(node, ResolveToolpathParentOutlinerItem());
        PendingToolpath.Enqueue(new PendingToolpathEntry
        {
            Toolpath      = tp,
            RawToolpath   = tp,
            Node          = node,
            BeadWidth     = beadWidth,
            LayerHeight   = 3f,
            MaterialColor = new System.Numerics.Vector3(0.95f, 0.95f, 0.95f),
        });
        NotifyRenderNeeded();
    }

    /// <summary>Reloads an outliner model from its saved <see cref="SceneNode.SourceFilePath"/>.</summary>
    public bool ReloadModelFromSource(SceneNode node)
    {
        var path = OutlinerModelOps.ResolveSourceFilePath(node);
        if (path is null)
            return false;
        return ReloadModel(node, path);
    }

    /// <summary>Reloads or replaces an outliner model from disk, preserving its scene transform.</summary>
    public bool ReloadModel(SceneNode node, string path)
    {
        path = Path.GetFullPath(path);
        if (!ImportHelper.IsSupported(path))
            return false;
        if (!File.Exists(path))
            return false;
        if (!ImportHelper.TryReloadInto(node, path))
            return false;

        PendingModelRefresh.Enqueue(node);
        NotifyRenderNeeded();
        return true;
    }

    internal OutlinerItemViewModel CreateOutlinerItem(
        SceneNode node,
        Action<OutlinerItemViewModel> onDelete,
        Action? onHide = null,
        string? displayName = null,
        bool canDelete = true,
        bool modelFileOps = false)
        => new(
            node, NotifyRenderNeeded, onDelete, onHide, displayName, canDelete,
            usesExclusiveVisibility: false,
            canReloadModel: modelFileOps ? OutlinerModelOps.CanReload : null,
            canReplaceModel: modelFileOps ? OutlinerModelOps.CanReplace : null,
            onReloadModel: modelFileOps ? item => OnModelReloadRequested?.Invoke(item.Node) : null,
            onReplaceModel: modelFileOps ? item => OnModelReplaceRequested?.Invoke(item.Node) : null);

    private OutlinerItemViewModel RegisterOutlinerItem(SceneNode node)
    {
        var item = CreateOutlinerItem(node, RemoveUserNode, () => OnNodeHidden?.Invoke(node), modelFileOps: true);
        AttachVisibilityCascade(item);
        OutlinerItems.Add(item);
        return item;
    }

    /// <summary>Cascades a user-model item's own Visible toggle down through its descendant rows
    /// in the outliner tree — EXCEPT its toolpath, which is deliberately its own independent
    /// toggle (a common workflow is hiding the mesh to look at its toolpath alone, or vice versa,
    /// so hiding one must never hide the other). The Modifiers group, though a real scene child
    /// (already hidden for rendering via SceneNode.Draw()'s ancestor-visibility walk), has its own
    /// outliner row that wouldn't otherwise reflect the mesh's hide — cascaded here purely so its
    /// eye icon stays accurate, not because anything new needs to be hidden.</summary>
    private static void AttachVisibilityCascade(OutlinerItemViewModel item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OutlinerItemViewModel.Visible)) return;
            CascadeVisible(item, item.Visible);
        };
    }

    private static void CascadeVisible(OutlinerItemViewModel parent, bool visible)
    {
        foreach (var child in parent.Children)
        {
            if (child.IsToolpath) continue;
            child.Visible = visible;
            CascadeVisible(child, visible);
        }
    }

    /// <summary>Resolves an outliner row for viewport/outliner selection (reference, subtree, or name).</summary>
    internal OutlinerItemViewModel? FindOutlinerItemForSelection(SceneNode? node)
    {
        if (node is null) return null;

        // Effector handles resolve directly from their registered slots, so clicking
        // a handle highlights its "Effector N" row (identifies which one is which).
        for (int i = 0; i < _effectorNodes.Length; i++)
            if (_effectorNodes[i] is { } en
                && (en == node || en.SelfAndDescendants().Any(n => n == node)))
                return _effectorItems[i];

        // Modifiers (and their Modifiers group) resolve directly too, for the same reason —
        // they're real scene-graph descendants of their owning mesh now (so a modifier moves
        // with it), but the generic subtree-matching fallbacks below would resolve them up to
        // whatever they happen to sit under scene-wise (the mesh, or even the bed/cell
        // environment above that) instead of their own outliner row.
        if (_modifiersGroupItems.TryGetValue(node, out var groupItem)) return groupItem;
        if (FindModifierForNode(node) is { } modCut && _modifierOutlinerItems.TryGetValue(modCut, out var modItem))
            return modItem;

        var item = FindToolheadOutlinerItem(node)
                   ?? FindUserMeshOutlinerItem(node)
                   ?? FindOutlinerItem(node);
        if (item is not null) return item;

        OutlinerItemViewModel? nameMatch = null;
        foreach (var candidate in EnumerateAllContentItems())
        {
            if (!candidate.Node.Name.Equals(node.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (nameMatch is not null)
                return null;

            nameMatch = candidate;
        }

        return nameMatch;
    }

    internal OutlinerItemViewModel? FindToolheadOutlinerItem(SceneNode? node)
    {
        if (node is null) return null;
        foreach (var item in _toolheadOutlinerItems)
        {
            if (item.Node == node || item.Node.SelfAndDescendants().Any(n => n == node))
                return item;
        }
        return null;
    }

    /// <summary>Returns the outliner item whose root node matches <paramref name="node"/>.</summary>
    internal OutlinerItemViewModel? FindOutlinerItem(SceneNode node)
    {
        foreach (var item in OutlinerItems)
        {
            var found = FindOutlinerItemInSubtree(item, node);
            if (found is not null) return found;
        }
        return null;
    }

    private static OutlinerItemViewModel? FindOutlinerItemInSubtree(OutlinerItemViewModel item, SceneNode node)
    {
        if (item.Node == node || item.Node.SelfAndDescendants().Any(n => n == node))
            return item;

        foreach (var child in item.Children)
        {
            var deeper = FindOutlinerItemInSubtree(child, node);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    internal OutlinerItemViewModel? FindParentOutlinerItem(OutlinerItemViewModel item)
    {
        foreach (var root in OutlinerItems)
        {
            var parent = FindParentInSubtree(root, item);
            if (parent is not null) return parent;
        }
        return null;
    }

    private static OutlinerItemViewModel? FindParentInSubtree(OutlinerItemViewModel root, OutlinerItemViewModel target)
    {
        foreach (var child in root.Children)
        {
            if (ReferenceEquals(child, target)) return root;
            var deeper = FindParentInSubtree(child, target);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>Returns the outliner item for a toolpath node (any depth).</summary>
    internal OutlinerItemViewModel? FindToolpathOutlinerItem(SceneNode node)
        => FindOutlinerItem(node);

    /// <summary>
    /// Creates a new toolpath outliner item as a child of <paramref name="parentItem"/>
    /// (or top-level if <c>null</c>), and enqueues its node for GL upload.
    /// Must be called on the UI thread.
    /// </summary>
    internal void RegisterToolpathInOutliner(SceneNode toolpathNode, OutlinerItemViewModel? parentItem)
    {
        var item = CreateOutlinerItem(toolpathNode, child =>
        {
            parentItem?.RemoveChild(child);
            if (parentItem is null) OutlinerItems.Remove(child);
            PendingRemoveNodes.Enqueue(child.Node);
            NotifyRenderNeeded();
        }, () => OnNodeHidden?.Invoke(toolpathNode), modelFileOps: true);
        item.IsToolpath = true;

        if (parentItem is not null)
            parentItem.AddChild(item);
        else
            OutlinerItems.Add(item);

        NotifyRenderNeeded();
    }

    /// <summary>
    /// Cross-wired by MainWindowViewModel (mirrors how AdditiveSettings/SubtractiveSettings are
    /// wired the other way) so the GL-thread render loop can read which modifier is selected,
    /// to draw its plane preview. Only ever read from the cached <c>_vm</c> on the GL thread,
    /// never written there.
    /// </summary>
    public ModifierPanelViewModel? ModifiersPanel { get; set; }

    // -- Modifier stack ----------------------------------------------------------
    // A mesh's Cut modifiers live as real children of its "Modifiers" outliner group
    // (see GetOrCreateModifiersGroup), itself a real child SceneNode of the mesh — so
    // stack membership and order ARE the scene graph / outliner structure, not a
    // separate bookkeeping dictionary that could drift out of sync with it (dragging a
    // modifier to a different mesh, or reordering it, just is the answer to "what stack
    // is this in" — nothing else to keep in sync).

    /// <summary>
    /// Gets (or creates) a non-deletable, geometry-less child row under <paramref name="parent"/>
    /// used purely to organize related rows (e.g. "Toolpaths") — never a modifier or toolpath
    /// itself, so it never shows a kind icon, and never selectable as a unit (contrast
    /// <see cref="GetOrCreateModifiersGroup"/>, which deliberately is).
    /// </summary>
    internal OutlinerItemViewModel GetOrCreateOutlinerGroup(OutlinerItemViewModel parent, string groupName)
    {
        foreach (var child in parent.Children)
            if (!child.CanDelete && child.Node.Name == groupName)
                return child;

        var groupNode = new SceneNode { Name = groupName, Selectable = false, PickIgnore = true };
        parent.Node.AddChild(groupNode);

        var groupItem = CreateOutlinerItem(groupNode, _ => { }, canDelete: false);
        parent.AddChild(groupItem);
        return groupItem;
    }

    /// <summary>
    /// Gets (or creates) <paramref name="ownerItem"/>'s "Modifiers" group: a real, selectable
    /// child SceneNode — selecting its outliner row arms the gizmo for the WHOLE stack at once,
    /// moving/rotating it moves every modifier under it together, for free, via ordinary
    /// parent-child transforms. Also where the Apply action lives (see IsModifiersGroup).
    /// Deleting it removes every modifier inside it.
    /// </summary>
    private readonly Dictionary<SceneNode, OutlinerItemViewModel> _modifiersGroupItems = new();

    /// <summary>True when the node is a mesh's "Modifiers" group SceneNode itself (not one of
    /// the modifiers inside it — see <see cref="IsModifierNode"/> for that).</summary>
    internal bool IsModifiersGroupNode(SceneNode? node)
        => node is not null && _modifiersGroupItems.ContainsKey(node);

    internal SceneNode GetOrCreateModifiersGroup(OutlinerItemViewModel ownerItem)
    {
        var existing = ownerItem.Children.FirstOrDefault(c => c.IsModifiersGroup);
        if (existing is not null) return existing.Node;

        var groupNode = new SceneNode { Name = "Modifiers", Selectable = true, PickIgnore = true, IsAuthoringOverlay = true };
        ownerItem.Node.AddChild(groupNode);

        var groupItem = CreateOutlinerItem(groupNode, it =>
        {
            foreach (var child in it.Children.ToList())
                if (FindModifierForNode(child.Node) is { } cut)
                    RemoveModifierGizmoNode(cut);
            ownerItem.RemoveChild(it);
            PendingRemoveNodes.Enqueue(it.Node);
            _modifiersGroupItems.Remove(groupNode);
            NotifyRenderNeeded();
        }, canDelete: true);
        groupItem.IsModifiersGroup = true;
        _modifiersGroupItems[groupNode] = groupItem;
        ownerItem.AddChild(groupItem);
        return groupNode;
    }

    /// <summary>
    /// Creates a fresh, top-level sibling group for one Apply's worth of results — same display
    /// name as the master (auto-numbered "Wall 01 (2)" on a repeat Apply, matching the Cut 01/02
    /// convention), distinguished only by icon (see <see cref="OutlinerItemViewModel.IsPiecesGroup"/>).
    /// Never reused — every Apply press makes a new one; nothing here is ever the input to
    /// another Apply. Its own node is a bare label, never added to the scene tree: the pieces
    /// inside are NOT its real scene children (they attach independently, same as any import),
    /// so each stays movable on its own — unlike the Modifiers group, which deliberately IS a
    /// real parent so the whole stack moves together.
    /// </summary>
    internal OutlinerItemViewModel CreateAppliedPiecesGroup(OutlinerItemViewModel ownerItem)
    {
        // The first Apply's group deliberately shares the master's exact name (icon is the only
        // distinction — that's the point). Only a REPEAT Apply, colliding with an earlier
        // pieces-group's name, needs a numbered suffix — never the master's own name.
        string baseName = ownerItem.Name;
        var existingPiecesGroupNames = new HashSet<string>(
            OutlinerItems.Where(i => i.IsPiecesGroup).Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        string name = baseName;
        int n = 2;
        while (existingPiecesGroupNames.Contains(name)) name = $"{baseName} ({n++})";
        return CreateAppliedPiecesGroupNamed(name);
    }

    /// <summary>Recreates a previously-saved Applied-Pieces group verbatim by name — used when
    /// restoring a workspace, where the name was already made unique at save time (see
    /// WorkspaceService.Capture's WorkspaceModelEntry.PiecesGroupName), so no collision-suffix
    /// logic is needed here.</summary>
    internal OutlinerItemViewModel CreateAppliedPiecesGroupNamed(string name)
    {
        var groupNode = new SceneNode { Name = name, Selectable = false, PickIgnore = true };
        var groupItem = CreateOutlinerItem(groupNode, it =>
        {
            // Deleting the whole batch at once: cascade through every piece AND each piece's
            // own toolpath child — none of these are real scene children of this group (or of
            // each other), so nothing gets cleaned up unless explicitly walked and enqueued here.
            foreach (var pieceItem in it.Children)
            {
                foreach (var tpItem in pieceItem.Children)
                    PendingRemoveNodes.Enqueue(tpItem.Node);
                PendingRemoveNodes.Enqueue(pieceItem.Node);
            }
            OutlinerItems.Remove(it);
            PendingRemoveNodes.Enqueue(it.Node);
            NotifyRenderNeeded();
        }, displayName: name, canDelete: true);
        groupItem.IsPiecesGroup = true;
        groupItem.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OutlinerItemViewModel.Visible)) return;
            // Pieces (and their toolpaths) are deliberately NOT real scene children of this
            // group — so each stays independently movable — which means, unlike the Modifiers
            // group (a real scene parent, where hiding it already cascades for free via
            // SceneNode.Draw()'s ancestor-visibility walk), hiding this one has to be done
            // explicitly here instead of falling out of the scene graph automatically.
            foreach (var pieceItem in groupItem.Children)
            {
                pieceItem.Visible = groupItem.Visible;
                foreach (var tpItem in pieceItem.Children)
                    tpItem.Visible = groupItem.Visible;
            }
        };
        OutlinerItems.Add(groupItem);
        return groupItem;
    }

    /// <summary>Restores a single Applied-Pieces piece from a workspace file: registers its
    /// SceneNode the same way any import is (GPU upload, rotary-bed awareness via
    /// EnqueueRotarySceneNode), but parents its outliner row under <paramref name="groupItem"/>
    /// (an Applied-Pieces group recreated via <see cref="CreateAppliedPiecesGroupNamed"/>)
    /// instead of at the outliner root, with a delete callback that mirrors the live Apply flow
    /// (removes from the group, not the root — <see cref="RemoveUserNode"/> would no-op here
    /// since the item was never added to the top-level OutlinerItems).</summary>
    internal OutlinerItemViewModel AddRestoredPieceToGroup(SceneNode node, OutlinerItemViewModel groupItem)
    {
        EnqueueRotarySceneNode(node);
        var pieceItem = CreateOutlinerItem(node, it =>
        {
            foreach (var child in it.Children)
                PendingRemoveNodes.Enqueue(child.Node);
            groupItem.RemoveChild(it);
            PendingRemoveNodes.Enqueue(it.Node);
            NotifyRenderNeeded();
        }, displayName: node.Name, canDelete: true, modelFileOps: true);
        groupItem.AddChild(pieceItem);
        OnModelGeometryChanged?.Invoke();
        return pieceItem;
    }

    /// <summary>The modifier stack for a mesh, in application (= outliner) order. Empty if it
    /// has no Modifiers group yet.</summary>
    internal IReadOnlyList<IModifier> GetModifiers(OutlinerItemViewModel ownerItem)
    {
        var groupItem = ownerItem.Children.FirstOrDefault(c => c.IsModifiersGroup);
        if (groupItem is null) return [];
        var result = new List<IModifier>();
        foreach (var childNode in groupItem.Node.Children)
            if (FindModifierForNode(childNode) is { } cut)
                result.Add(cut);
        return result;
    }

    /// <summary>
    /// Adds a new Cut modifier to <paramref name="ownerItem"/>'s stack — real geometry, its own
    /// outliner row nested under (creating, if needed) the mesh's Modifiers group, from the
    /// moment it's created (see GetOrCreateModifierGizmoNode). Must be called on the UI thread.
    /// The name (whether a freshly-computed "Cut NN" or a saved name being restored) is resolved
    /// and assigned BEFORE the node/outliner row are built, so both the SceneNode's own name and
    /// the outliner row's display name are correct from the very first frame — assigning it
    /// after creation (the previous approach) left every Cut showing as literally "Cut" in the
    /// outliner forever, since the row's display name is captured once at construction and never
    /// re-bound to the modifier's own Name afterward.
    /// </summary>
    internal CutModifier AddCutModifier(OutlinerItemViewModel ownerItem, string? name = null)
    {
        var modifier = new CutModifier { Name = name ?? NextCutName(GetModifiers(ownerItem)) };
        GetOrCreateModifierGizmoNode(modifier, ownerItem);
        return modifier;
    }

    /// <summary>"Cut 01" if free, else the lowest "Cut NN" not already used by a sibling Cut modifier.</summary>
    private static string NextCutName(IReadOnlyList<IModifier> siblings)
    {
        var used = new HashSet<int>();
        foreach (var m in siblings)
            if (m is CutModifier && m.Name.StartsWith("Cut ", StringComparison.Ordinal)
                && int.TryParse(m.Name.AsSpan(4), out var n))
                used.Add(n);

        int next = 1;
        while (used.Contains(next)) next++;
        return $"Cut {next:D2}";
    }

    /// <summary>The real, currently-sliced toolpath layers belonging to <paramref name="cut"/>'s
    /// owner mesh, or null if the owner has no toolpath yet (never sliced) or isn't resolvable.
    /// Backs the Cut modifier's "Layer" field — converting a layer index into the same world-Z
    /// space Offset already uses for Horizontal cuts (see CutModifierNodeSync).</summary>
    internal IReadOnlyList<ToolpathLayer>? GetOwnerToolpathLayers(CutModifier cut)
    {
        if (!_modifierOutlinerItems.TryGetValue(cut, out var cutItem)) return null;
        if (OwningModelItem(cutItem) is not { } ownerItem) return null;
        if (ownerItem.Children.FirstOrDefault(c => c.IsToolpath) is not { } tpItem) return null;
        return GetToolpathSnapshot?.Invoke(tpItem.Node)?.Raw.Layers;
    }

    /// <summary>Removes a modifier — deletes its plane object and outliner row outright
    /// (Apply already ran or didn't; there's nothing else referencing it).</summary>
    internal void RemoveModifier(IModifier modifier)
    {
        if (modifier is CutModifier cut) RemoveModifierGizmoNode(cut);
    }

    /// <summary>Reorders a mesh's modifier stack (both the real SceneNode children and the
    /// matching outliner rows, kept in lockstep) to match a reordered outliner-row list.</summary>
    internal void MoveModifier(OutlinerItemViewModel ownerItem, int fromIndex, int toIndex)
    {
        var groupItem = ownerItem.Children.FirstOrDefault(c => c.IsModifiersGroup);
        if (groupItem is null) return;
        var nodeList = groupItem.Node.Children;
        if (fromIndex < 0 || fromIndex >= nodeList.Count || toIndex < 0 || toIndex >= nodeList.Count) return;

        var node = nodeList[fromIndex];
        nodeList.RemoveAt(fromIndex);
        nodeList.Insert(toIndex, node);

        var rowItem = groupItem.Children[fromIndex];
        groupItem.RemoveChild(rowItem);
        groupItem.InsertChild(rowItem, toIndex);
    }

    // -- Modifier gizmo node -------------------------------------------------------
    // Each Cut modifier gets its own dedicated, fully independent SceneNode — real geometry,
    // pickable, its own outliner row, never parented to (or hidden/moved by) any mesh. It's a
    // genuinely separate object the EXISTING, unmodified translate/rotate drag and pick code
    // operate on directly, same as any mesh or effector handle. The node is kept in sync with
    // the modifier's plain Offset/RotationDegrees fields (the real, persisted source of truth)
    // in both directions: settings-panel edits push into the node; gizmo drags pull back out of
    // it afterward.

    private readonly Dictionary<CutModifier, SceneNode> _modifierGizmoNodes = new();
    private readonly Dictionary<CutModifier, OutlinerItemViewModel> _modifierOutlinerItems = new();

    private static readonly Vector3 ModifierPlaneTint = new(0.91f, 0.64f, 0.24f); // matches the outliner's Cut-modifier icon color

    /// <summary>The four (sign X, sign Y) corner directions of a rectangle, used to place
    /// per-corner markers on a modifier's plane.</summary>
    private static readonly (float sx, float sy)[] CornerSigns = [(-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f)];

    /// <summary>True when the node is (or is inside) a modifier's own plane object.</summary>
    public bool IsModifierNode(SceneNode? node)
        => FindModifierForNode(node) is not null;

    /// <summary>The Cut modifier whose plane object this is, or null if <paramref name="node"/>
    /// isn't (inside) any modifier's gizmo node. Used to route gizmo drags on a modifier plane
    /// back into its own Offset/RotationDegrees fields, same as any settings-panel edit.</summary>
    internal CutModifier? FindModifierForNode(SceneNode? node)
    {
        if (node is null) return null;
        foreach (var (cut, n) in _modifierGizmoNodes)
            if (n == node || n.SelfAndDescendants().Any(d => d == node))
                return cut;
        return null;
    }

    /// <summary>Bed center in world space (X/Y/Z) — the pivot Vertical modifiers rotate/measure around.</summary>
    internal Vector3 ResolveBedCenterXYZ()
    {
        if (ActiveCell?.Bed is not { } bed) return Vector3.Zero;
        var c = bed.ImportSurfaceCenter(ActiveCell.Robot.WorldPosition);
        return new Vector3(c.X, c.Y, c.Z);
    }

    /// <summary>Bed footprint (mm) — sizes an Infinite modifier's plane so it visibly extends past the model.</summary>
    private (float Width, float Depth) ResolveBedSizeXY()
    {
        var bed = ActiveCell?.Bed;
        return (bed?.Width ?? 3000f, bed?.Depth ?? 3000f);
    }

    /// <summary>Builds this modifier's plane geometry for its current Orientation/Infinite/SizeX/SizeY —
    /// a flat, double-sided, translucent quad in local space (see CutModifierNodeSync for how the
    /// node's transform then places/orients it in world space).</summary>
    private MeshData BuildModifierPlaneMesh(CutModifier cut)
    {
        var (bedWidth, bedDepth) = ResolveBedSizeXY();
        float extent = Math.Max(bedWidth, bedDepth);
        float halfA = (cut.Infinite ? extent : Math.Max(cut.SizeX, 10f)) * 0.5f;
        float halfB = (cut.Infinite ? extent : Math.Max(cut.SizeY, 10f)) * 0.5f;

        Vector3[] positions;
        Vector3[] normals;
        if (cut.Orientation == CutOrientation.Horizontal)
        {
            // Flat in local X/Y, facing local +Z — matches BuildHorizontalTransform (no rotation).
            positions =
            [
                new Vector3(-halfA, -halfB, 0f), new Vector3(halfA, -halfB, 0f),
                new Vector3(halfA, halfB, 0f), new Vector3(-halfA, halfB, 0f),
            ];
            normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        }
        else
        {
            // Upright in local Y/Z, facing local +X — Row0 is the plane's normal per
            // CutModifierNodeSync.BuildVerticalTransform.
            positions =
            [
                new Vector3(0f, -halfA, -halfB), new Vector3(0f, halfA, -halfB),
                new Vector3(0f, halfA, halfB), new Vector3(0f, -halfA, halfB),
            ];
            normals = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX];
        }
        uint[] indices = [0, 1, 2, 0, 2, 3];

        var color = new Vector4(ModifierPlaneTint.X, ModifierPlaneTint.Y, ModifierPlaneTint.Z, 0.28f);
        return new MeshData(positions, normals, indices, cut.Name, color, 0f, 1f,
            uvs: null, tangents: null,
            material: new MaterialData
            {
                BaseColorFactor = color,
                MetallicFactor  = 0f,
                RoughnessFactor = 1f,
                EmissiveFactor  = ModifierPlaneTint * 0.25f,
                AlphaMode       = MassiveSlicer.Viewport.Scene.AlphaMode.Blend,
            });
    }

    /// <summary>
    /// Builds the small corner markers that show at a glance whether a plane is Infinite or
    /// restricted, sitting a hair in front of the main plane fill (same flat color, no per-vertex
    /// alpha, so a visible offset along the normal is the only way to keep them from blending
    /// into the translucent fill underneath): four inward-opening 90° brackets (like a
    /// bounding-box/crop icon) when restricted, or four small arrowheads near each corner
    /// pointing outward along the diagonal — inset so the tip always stays inside the plane's
    /// own visible extent — when Infinite.
    /// </summary>
    private MeshData? BuildModifierCornerMarkerMesh(CutModifier cut)
    {
        var (bedWidth, bedDepth) = ResolveBedSizeXY();
        float extent = Math.Max(bedWidth, bedDepth);
        float halfA = (cut.Infinite ? extent : Math.Max(cut.SizeX, 10f)) * 0.5f;
        float halfB = (cut.Infinite ? extent : Math.Max(cut.SizeY, 10f)) * 0.5f;
        float minExtent = Math.Min(halfA, halfB) * 2f;

        const float NormalOffset = 0.75f; // mm in front of the fill — avoids z-fighting, not a real depth
        Vector3 Embed(float u, float v) => cut.Orientation == CutOrientation.Horizontal
            ? new Vector3(u, v, NormalOffset)
            : new Vector3(NormalOffset, u, v);
        var normal = cut.Orientation == CutOrientation.Horizontal ? Vector3.UnitZ : Vector3.UnitX;

        var positions = new List<Vector3>();
        var indices   = new List<uint>();

        void AddQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            uint i = (uint)positions.Count;
            positions.Add(Embed(a.X, a.Y)); positions.Add(Embed(b.X, b.Y));
            positions.Add(Embed(c.X, c.Y)); positions.Add(Embed(d.X, d.Y));
            indices.Add(i); indices.Add(i + 1); indices.Add(i + 2);
            indices.Add(i); indices.Add(i + 2); indices.Add(i + 3);
        }

        void AddTri(Vector2 a, Vector2 b, Vector2 c)
        {
            uint i = (uint)positions.Count;
            positions.Add(Embed(a.X, a.Y)); positions.Add(Embed(b.X, b.Y)); positions.Add(Embed(c.X, c.Y));
            indices.Add(i); indices.Add(i + 1); indices.Add(i + 2);
        }

        // Pulled in from the true edge on both axes so neither marker ever sits right on the
        // plane's boundary line (kept clear of any border/edge highlight drawn elsewhere).
        float edgeInset = Math.Clamp(minExtent * 0.06f, 6f, 30f);

        if (!cut.Infinite)
        {
            float armLen    = Math.Clamp(minExtent * 0.2f, 8f, 60f);
            float thickness = Math.Clamp(armLen * 0.18f, 1.5f, 8f);

            foreach (var (sx, sy) in CornerSigns)
            {
                float cx = sx * (halfA - edgeInset), cy = sy * (halfB - edgeInset);
                float ix = -sx, iy = -sy; // inward direction along each axis

                // A single non-overlapping "L" hexagon (outer corner, out armLen along U, step in
                // by thickness, inner corner, out armLen along V, back to outer corner) — two
                // separate overlapping quads used to double-blend at the shared corner square,
                // making that corner render more opaque than the rest of each arm.
                var p0 = new Vector2(cx, cy);
                var p1 = new Vector2(cx + ix * armLen, cy);
                var p2 = new Vector2(cx + ix * armLen, cy + iy * thickness);
                var p3 = new Vector2(cx + ix * thickness, cy + iy * thickness);
                var p4 = new Vector2(cx + ix * thickness, cy + iy * armLen);
                var p5 = new Vector2(cx, cy + iy * armLen);
                AddTri(p0, p1, p2); AddTri(p0, p2, p3); AddTri(p0, p3, p4); AddTri(p0, p4, p5);
            }
        }
        else
        {
            float arrowLen   = Math.Clamp(minExtent * 0.02f, 14f, 60f);
            float arrowWidth = arrowLen * 0.75f;
            float tailLen    = arrowLen * 0.7f;
            float tailWidth  = arrowWidth * 0.35f;

            foreach (var (sx, sy) in CornerSigns)
            {
                var dir    = Vector2.Normalize(new Vector2(sx, sy));
                var perp   = new Vector2(-dir.Y, dir.X);
                var corner = new Vector2(sx * (halfA - edgeInset), sy * (halfB - edgeInset));

                // Tip sits at the inset corner (already pulled in from the true edge above);
                // arrowhead points outward along the diagonal, with a short tail behind it.
                var tip      = corner;
                var headBase = tip - dir * arrowLen;
                var tailEnd  = headBase - dir * tailLen;

                AddTri(tip, headBase + perp * (arrowWidth * 0.5f), headBase - perp * (arrowWidth * 0.5f));
                AddQuad(
                    headBase + perp * (tailWidth * 0.5f), headBase - perp * (tailWidth * 0.5f),
                    tailEnd  - perp * (tailWidth * 0.5f), tailEnd  + perp * (tailWidth * 0.5f));
            }
        }

        if (positions.Count == 0) return null;

        var normals = new Vector3[positions.Count];
        Array.Fill(normals, normal);

        // Deliberately translucent, matching the plane fill (Jeff: "I like transparent for
        // both") — just a touch brighter/more opaque than the fill so the shape still reads.
        var color = new Vector4(ModifierPlaneTint.X, ModifierPlaneTint.Y, ModifierPlaneTint.Z, 0.4f);
        return new MeshData(positions.ToArray(), normals, indices.ToArray(), $"{cut.Name} Markers", color, 0f, 1f,
            uvs: null, tangents: null,
            material: new MaterialData
            {
                BaseColorFactor = color,
                MetallicFactor  = 0f,
                RoughnessFactor = 1f,
                EmissiveFactor  = ModifierPlaneTint * 0.3f,
                AlphaMode       = MassiveSlicer.Viewport.Scene.AlphaMode.Blend,
            });
    }

    /// <summary>Regenerates a modifier's plane geometry and its Infinite/restricted corner
    /// markers (call after Orientation/Infinite/SizeX/SizeY changes) and queues the GPU
    /// re-upload. No-op if the modifier has no gizmo node yet.</summary>
    internal void RebuildModifierPlaneMesh(CutModifier cut)
    {
        if (!_modifierGizmoNodes.TryGetValue(cut, out var node)) return;
        node.PendingMesh = BuildModifierPlaneMesh(cut);
        PendingModelRefresh.Enqueue(node);

        if (node.Children.FirstOrDefault(c => c.IsAuthoringOverlay) is { } markerNode)
        {
            markerNode.PendingMesh = BuildModifierCornerMarkerMesh(cut);
            PendingModelRefresh.Enqueue(markerNode);
        }
        NotifyRenderNeeded();
    }

    /// <summary>
    /// Gets (or lazily creates) a modifier's dedicated plane object: real geometry, pickable,
    /// its own outliner row, parented into <paramref name="ownerItem"/>'s Modifiers group from
    /// the moment it's created (so it moves/rotates with that mesh, and reorders/relinks via the
    /// outliner drag system — never a special case in code). A brand-new modifier spawns
    /// centered on the owner's current world height rather than always at bed center, so it
    /// starts somewhere sensible relative to whatever you were looking at.
    /// </summary>
    internal SceneNode GetOrCreateModifierGizmoNode(CutModifier cut, OutlinerItemViewModel ownerItem)
    {
        if (!_modifierGizmoNodes.TryGetValue(cut, out var node))
        {
            var groupNode = GetOrCreateModifiersGroup(ownerItem);

            if (cut.Orientation == CutOrientation.Horizontal
                && ComputeWorldCenter(ownerItem.Node) is { } ownerCenter)
                cut.Offset = ownerCenter.Z - ResolveBedCenterXYZ().Z;
            else if (cut.Orientation == CutOrientation.Vertical
                && ComputeWorldCenter(ownerItem.Node) is { } ownerCenterV)
            {
                // Same idea as Horizontal above — start the plane at the owner mesh's own
                // location instead of always at raw bed center, since a mesh won't always sit
                // at bed center. RotationDegrees is always 0 for a brand-new modifier (normal =
                // +X, tangent = +Y), so this is just the mesh's bed-center-relative X/Y/Z split
                // directly across Offset/PositionTangent/PositionZ.
                var bedCenterV = ResolveBedCenterXYZ();
                var deltaV = ownerCenterV - bedCenterV;
                cut.Offset          = deltaV.X;
                cut.PositionTangent = deltaV.Y;
                cut.PositionZ       = deltaV.Z;
            }

            node = new SceneNode
            {
                Name               = cut.Name,
                Selectable         = true,
                PickIgnore         = false,
                CullFaces          = false,
                TranslucentPass    = true,
                KeepOwnMaterial    = true,
                Visible            = cut.PreviewVisible,
                PendingMesh        = BuildModifierPlaneMesh(cut),
                IsAuthoringOverlay = true,
            };
            _modifierGizmoNodes[cut] = node;
            groupNode.AddChild(node);
            PendingModelRefresh.Enqueue(node);

            var markerNode = new SceneNode
            {
                Name               = $"{cut.Name} Markers",
                Selectable         = false,
                PickIgnore         = true,
                CullFaces          = false,
                TranslucentPass    = true,
                KeepOwnMaterial    = true,
                Visible            = cut.PreviewVisible,
                PendingMesh        = BuildModifierCornerMarkerMesh(cut),
                IsAuthoringOverlay = true,
                AlwaysOnTop        = true,
            };
            node.AddChild(markerNode);
            PendingModelRefresh.Enqueue(markerNode);

            var groupItem = ownerItem.Children.First(c => c.IsModifiersGroup);
            var item = new OutlinerItemViewModel(node, NotifyRenderNeeded, it =>
            {
                (FindParentOutlinerItem(it) as OutlinerItemViewModel)?.RemoveChild(it);
                PendingRemoveNodes.Enqueue(it.Node);
                _modifierGizmoNodes.Remove(cut);
                _modifierOutlinerItems.Remove(cut);
                NotifyRenderNeeded();
            }, displayName: cut.Name, canDelete: true)
            { IsModifier = true };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OutlinerItemViewModel.Visible))
                    cut.PreviewVisible = item.Visible;
            };
            _modifierOutlinerItems[cut] = item;
            groupItem.AddChild(item);

            // New modifier starts selected — placing it is the whole point of creating one
            // (same convention as spawning an effector handle). Safe now in a way it wasn't
            // earlier: the panel has no separate list/selection state of its own left to
            // desync from the real selection this triggers.
            OnOutlinerSelectRequested?.Invoke(node);
        }
        SyncModifierGizmoNodeFromFields(cut);
        return node;
    }

    internal SceneNode? GetModifierGizmoNode(CutModifier cut)
        => _modifierGizmoNodes.TryGetValue(cut, out var node) ? node : null;

    internal void RemoveModifierGizmoNode(CutModifier cut)
    {
        if (_modifierOutlinerItems.Remove(cut, out var item))
            FindParentOutlinerItem(item)?.RemoveChild(item);
        if (_modifierGizmoNodes.Remove(cut, out var node))
            PendingRemoveNodes.Enqueue(node);
    }

    /// <summary>Pushes Offset/RotationDegrees/Orientation into the modifier's plane object —
    /// call after any settings-panel edit. Converts the intended world pose into local space
    /// relative to whatever the node's current parent is (its Modifiers group), so the plane
    /// ends up in the right place in the scene regardless of where that group — and the mesh it
    /// belongs to — currently sit.</summary>
    internal void SyncModifierGizmoNodeFromFields(CutModifier cut)
    {
        if (!_modifierGizmoNodes.TryGetValue(cut, out var node)) return;

        var bedCenter = ResolveBedCenterXYZ();
        var world = cut.Orientation == CutOrientation.Horizontal
            ? CutModifierNodeSync.BuildHorizontalTransform(cut.PositionX, cut.PositionY, cut.Offset, bedCenter)
            : CutModifierNodeSync.BuildVerticalTransform(
                cut.RotationDegrees, cut.Offset, cut.PositionZ, cut.PositionTangent, bedCenter);

        node.LocalTransform = node.Parent is { } parent ? world * parent.WorldTransform.Inverted() : world;
    }

    /// <summary>
    /// Sets a Vertical Cut modifier's RotationDegrees WITHOUT moving its plane — call this
    /// instead of setting <see cref="CutModifier.RotationDegrees"/> directly whenever the change
    /// comes from something other than a live gizmo drag (the panel's numeric field, or a
    /// console/bridge command). <see cref="CutModifierNodeSync.BuildVerticalTransform"/> treats
    /// Offset/PositionTangent as distances along the CURRENT normal/tangent, which both rotate
    /// with RotationDegrees — so naively changing RotationDegrees alone swings the plane through
    /// an arc around bed center at radius Offset, instead of spinning it in place. This captures
    /// the plane's actual current world position first, then re-solves Offset/PositionTangent for
    /// the NEW rotation so that position doesn't move (PositionZ is untouched — it's Z-only and
    /// was never rotation-dependent). The live gizmo-drag path doesn't need this: dragging the
    /// rotate ring already rotates the node in place, and SyncModifierAfterGizmoEdit's
    /// extract-then-rebuild round-trip already preserves position the same way.
    /// </summary>
    internal void SetVerticalRotationInPlace(CutModifier cut, float newRotationDegrees)
    {
        if (cut.Orientation != CutOrientation.Vertical || cut.RotationDegrees == newRotationDegrees)
        {
            cut.RotationDegrees = newRotationDegrees;
            return;
        }

        var bedCenter  = ResolveBedCenterXYZ();
        var oldRot     = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(cut.RotationDegrees));
        var currentPos = bedCenter + oldRot.Row0.Xyz * cut.Offset + oldRot.Row1.Xyz * cut.PositionTangent;

        var newRot    = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(newRotationDegrees));
        var delta     = currentPos - bedCenter;
        cut.Offset          = Vector3.Dot(delta, newRot.Row0.Xyz);
        cut.PositionTangent = Vector3.Dot(delta, newRot.Row1.Xyz);
        cut.RotationDegrees = newRotationDegrees;
    }

    /// <summary>
    /// After a gizmo drag has changed a modifier's node transform: pulls the fields back out of
    /// its WORLD transform (not local — the node is a real child of its Modifiers group now, so
    /// local space is relative to that group/mesh, not the bed), then rebuilds the node from
    /// those extracted values. For Horizontal this round-trips X/Y too (free position — dragging
    /// it sideways to get it out of the way is meant to work; only Z is the actual cut value). For
    /// Vertical this round-trips PositionZ and PositionTangent the same way (free position in the
    /// two directions that aren't the cut's own Offset), so a drag in any direction — not just
    /// exactly along the current normal — lands where you actually dragged it instead of being
    /// silently discarded.
    /// </summary>
    internal void SyncModifierAfterGizmoEdit(CutModifier cut, SceneNode node)
    {
        var bedCenter = ResolveBedCenterXYZ();
        var world = node.WorldTransform;
        if (cut.Orientation == CutOrientation.Horizontal)
        {
            (cut.PositionX, cut.PositionY, cut.Offset) = CutModifierNodeSync.ExtractHorizontal(world, bedCenter);
        }
        else
        {
            var (offset, rotation, positionZ, positionTangent) = CutModifierNodeSync.ExtractVertical(world, bedCenter);
            cut.Offset          = offset;
            cut.RotationDegrees = rotation;
            cut.PositionZ       = positionZ;
            cut.PositionTangent = positionTangent;
        }
        SyncModifierGizmoNodeFromFields(cut);

        // The fields above are plain data on CutModifier (no INotifyPropertyChanged of its
        // own), so the settings panel -- if it's the one currently showing this exact cut --
        // needs an explicit nudge to know Offset/RotationDegrees/LayerNumber just changed from
        // under it. Without this, a gizmo drag updates the real cut correctly but the panel's
        // displayed numbers go stale until the next selection change.
        if (ModifiersPanel?.SelectedSettings is { } settings && ReferenceEquals(settings.Cut, cut))
            settings.NotifyAllFieldsChanged();
    }

    /// <summary>
    /// Finds the outliner item whose subtree contains <paramref name="node"/>,
    /// removes it from the outliner, and queues the root for GL disposal.
    /// Must be called on the UI thread.
    /// </summary>
    /// <summary>Snapshot of an outliner row's position for delete-undo.</summary>
    internal (OutlinerItemViewModel Item, OutlinerItemViewModel? Parent, int Index)? CaptureOutlinerContext(SceneNode node)
    {
        if (FindOutlinerItem(node) is not { } item) return null;
        var parent = FindParentOutlinerItem(item);
        int index  = parent is null ? OutlinerItems.IndexOf(item) : parent.Children.IndexOf(item);
        return (item, parent, Math.Max(index, 0));
    }

    /// <summary>Re-inserts a previously deleted outliner row at its old position (delete-undo).</summary>
    internal void RestoreOutlinerItem(OutlinerItemViewModel item, OutlinerItemViewModel? parent, int index)
    {
        if (parent is null)
        {
            if (!OutlinerItems.Contains(item))
                OutlinerItems.Insert(Math.Clamp(index, 0, OutlinerItems.Count), item);
        }
        else if (!parent.Children.Contains(item))
        {
            parent.InsertChild(item, index);
        }
        SliceCommand.RaiseCanExecuteChanged();
        NotifyRenderNeeded();
    }

    /// <summary>Wired by the viewport code-behind: shift+click sequence toggle.</summary>
    internal Action<SceneNode>? OnSequenceToggleRequested { get; set; }

    /// <summary>Wired by the viewport code-behind: current sequence-selection size (diagnostics).</summary>
    internal Func<int>? GetSequenceCount { get; set; }

    /// <summary>Wired by the viewport code-behind: PrintSpeedScale spread of the first toolpath (diagnostics).</summary>
    internal Func<string>? GetSpeedSpread { get; set; }

    /// <summary>One floating value tag beside the toolpath in the Speed/RPM views.</summary>
    public sealed record ViewTag(double X, double Y, string Text);

    private IReadOnlyList<ViewTag> _viewTags = [];

    /// <summary>Height-interval value tags (screen coords, overlay space).</summary>
    public IReadOnlyList<ViewTag> ViewTags
    {
        get => _viewTags;
        internal set => SetField(ref _viewTags, value);
    }

    public bool ShowViewTags => _viewMode is "Speed" or "RPM";

    /// <summary>Wired by the viewport code-behind: outliner shift+click range-extend.</summary>
    internal Action<OutlinerItemViewModel>? OnSequenceRangeRequested { get; set; }

    /// <summary>Outliner shift+click: extends the print-sequence selection from the
    /// current anchor through the clicked row (models resolve to their toolpath child).</summary>
    internal bool TryToggleToolpathSequenceSelection(OutlinerItemViewModel item)
    {
        bool sequenceable = item.IsToolpath || IsUserModelItem(item)
            || (item.IsToolpath is false && OwningModelItem(item) is not null);
        if (!sequenceable || OnSequenceRangeRequested is null) return false;
        OnSequenceRangeRequested.Invoke(item);
        return true;
    }

    /// <summary>Top-level user model rows in outliner order (sequence range selection).</summary>
    internal List<OutlinerItemViewModel> GetUserModelItems() => EnumerateUserModelItems().ToList();

    /// <summary>Highlights outliner rows whose toolpath is in the sequence selection.</summary>
    internal void SyncSequenceRowHighlights(IReadOnlyList<SceneNode> selectedToolpaths)
    {
        bool multi = selectedToolpaths.Count >= 2;
        foreach (var model in EnumerateUserModelItems())
        {
            bool anyChild = false;
            foreach (var child in model.Children)
            {
                if (!child.IsToolpath) continue;
                bool on = multi && selectedToolpaths.Contains(child.Node);
                child.IsSequenceSelected = on;
                anyChild |= on;
            }
            model.IsSequenceSelected = anyChild;
        }
    }

    /// <summary>True when the row is a user-imported print model (context-menu gating).</summary>
    internal bool IsUserModelItem(OutlinerItemViewModel item)
        => EnumerateUserModelItems().Contains(item);

    /// <summary>Explicitly (re)creates the toolpath for a model — recovery path after the
    /// realtime toolpath was deleted; slicing re-adopts it into the live sync loop.</summary>
    internal void RequestCreateToolpath(OutlinerItemViewModel item)
    {
        ForceSelectNode?.Invoke(item.Node);
        if (SliceCommand.CanExecute(null))
            SliceCommand.Execute(null);
    }

    public void RequestDeleteNode(SceneNode node)
    {
        if (FindOutlinerItem(node) is not { } item) return;
        if (item == _rotaryGroupItem || item == _robotGroupItem) return;

        if (FindParentOutlinerItem(item) is { } parent)
        {
            parent.RemoveChild(item);
            QueueOutlinerSubtreeForRemoval(item);
            ClearArmatureScanMeshIfRemoved(item.Node);
            NotifyRenderNeeded();
            return;
        }

        RemoveUserNode(item);
    }

    private void QueueOutlinerSubtreeForRemoval(OutlinerItemViewModel item)
    {
        foreach (var child in item.Children)
            QueueOutlinerSubtreeForRemoval(child);
        PendingRemoveNodes.Enqueue(item.Node);
    }

    private void RemoveUserNode(OutlinerItemViewModel item)
    {
        OutlinerItems.Remove(item);
        ClearArmatureScanMeshIfRemoved(item.Node);
        foreach (var child in item.Children.ToList())
            QueueOutlinerSubtreeForRemoval(child);
        PendingRemoveNodes.Enqueue(item.Node);
        SliceCommand.RaiseCanExecuteChanged();
        NotifyRenderNeeded();
    }

    /// <summary>Removes all user outliner entries and queues their nodes for GL disposal.</summary>
    public void ClearUserScene()
    {
        foreach (var item in OutlinerItems.ToList())
        {
            // Preserve cell-owned scenery (robot, rotary, print bed, tools, stands) —
            // these belong to the active cell, not the user's workspace. Without this,
            // opening a workspace on the already-active cell deletes the print bed and
            // tools (the cell isn't reloaded to rebuild them).
            if (item == _rotaryGroupItem
                || item == _robotGroupItem
                || _cellEnvOutlinerItems.Contains(item)
                || _toolheadOutlinerItems.Contains(item)) continue;
            RemoveUserNode(item);
        }

        if (_rotaryGroupItem is not null)
        {
            foreach (var child in _rotaryGroupItem.Children.ToList())
            {
                _rotaryGroupItem.RemoveChild(child);
                QueueOutlinerSubtreeForRemoval(child);
            }
        }

        _armatureScanNode = null;
        SliceCommand.RaiseCanExecuteChanged();
        NotifyRenderNeeded();
    }

}
