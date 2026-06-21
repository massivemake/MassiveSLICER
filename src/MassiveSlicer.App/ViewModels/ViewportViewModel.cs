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
using MassiveSlicer.Viewport;
using MassiveSlicer.Viewport.Scene;
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

    // -- Toolpath colors -------------------------------------------------------

    private System.Numerics.Vector3 _toolpathExtrudeColor     = new(0.1f,  0.45f, 0.9f);
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
        SelectLfam3Tool(toolName);
        NotifyWorkflowStateChanged();
    }

    void SyncWorkflowPhaseFromMountedTool()
    {
        if (!ShowLfam3ToolPicker) return;
        int? phase = MountedToolName switch
        {
            "HV Extruder" => PrintPhaseIndex,
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
        "HV Extruder" => "Extruder_Pick",
        "Scanner"     => "Scanner_Pick",
        "Spindle"     => "Spindle_Pick",
        _             => "",
    };

    static string DepositSequenceId(string cellToolName) => cellToolName switch
    {
        "HV Extruder" => "Extruder_Deposit",
        "Scanner"     => "Scanner_Deposit",
        "Spindle"     => "Spindle_Deposit",
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

    // -- Selection transform readout / input -----------------------------------

    private bool   _suppressTransformCb;
    private double _selX, _selY, _selZ, _selA, _selB, _selC;

    public double SelectionX { get => _selX; set { if (SetField(ref _selX, value)) FireSelTranslated(); } }
    public double SelectionY { get => _selY; set { if (SetField(ref _selY, value)) FireSelTranslated(); } }
    public double SelectionZ { get => _selZ; set { if (SetField(ref _selZ, value)) FireSelTranslated(); } }
    public double SelectionA { get => _selA; set { if (SetField(ref _selA, value)) FireSelRotated(); } }
    public double SelectionB { get => _selB; set { if (SetField(ref _selB, value)) FireSelRotated(); } }
    public double SelectionC { get => _selC; set { if (SetField(ref _selC, value)) FireSelRotated(); } }

    internal Action<double, double, double>? OnSelectionTranslated { get; set; }
    internal Action<double, double, double>? OnSelectionRotated    { get; set; }

    /// <summary>Shared undo/redo stack for transform edits in the viewport.</summary>
    internal UndoRedoService? UndoRedo { get; set; }

    private void FireSelTranslated() { if (!_suppressTransformCb) OnSelectionTranslated?.Invoke(_selX, _selY, _selZ); }
    private void FireSelRotated()    { if (!_suppressTransformCb) OnSelectionRotated?.Invoke(_selA, _selB, _selC); }

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

    private bool _hasMeshSelected;

    /// <summary>True when a sliceable user mesh (not a toolpath or toolhead) is selected.</summary>
    public bool HasMeshSelected
    {
        get => _hasMeshSelected;
        set
        {
            if (SetField(ref _hasMeshSelected, value))
                SliceCommand?.RaiseCanExecuteChanged();
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

    private bool _isToolpathSelected;

    /// <summary>True when the active toolpath node is the current selection.</summary>
    public bool IsToolpathSelected
    {
        get => _isToolpathSelected;
        set
        {
            if (SetField(ref _isToolpathSelected, value))
            {
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
                // Pause playback if the user manually moves the scrubber.
                if (_isPlaying)
                {
                    _isPlaying = false;
                    OnPropertyChanged(nameof(IsPlaying));
                    OnPlaybackToggled?.Invoke(false);
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
        if (_isPlaying)
        {
            _isPlaying = false;
            OnPropertyChanged(nameof(IsPlaying));
            OnPlaybackToggled?.Invoke(false);
        }
        ActiveScrubToolpath = toolpath;
        ExportKrlCommand?.RaiseCanExecuteChanged();
        UpdateSliceCommand?.RaiseCanExecuteChanged();

        _toolpathScrubMax   = max;
        OnPropertyChanged(nameof(ToolpathScrubMax));
        OnPropertyChanged(nameof(ToolpathScrubMaxLabel));

        _toolpathScrubIndex = max;
        _toolpathScrubText  = max.ToString();
        OnPropertyChanged(nameof(ToolpathScrubIndex));
        OnPropertyChanged(nameof(ToolpathScrubText));
        OnPropertyChanged(nameof(ToolpathScrubLabel));
        OnPropertyChanged(nameof(ToolpathScrubThumbOffsetY));
        OnPropertyChanged(nameof(ToolpathScrubFillHeight));
    }

    /// <summary>
    /// Updates the scrub index and slider UI without triggering IK — used during playback
    /// when joints are driven directly from pre-solved angles.
    /// </summary>
    internal void SetPlaybackIndex(int index)
    {
        if (!SetField(ref _toolpathScrubIndex, index)) return;
        OnPropertyChanged(nameof(ToolpathScrubLabel));
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

    internal void SetScrubMarkers(bool[] reachable, bool[] singular)
    {
        _scrubReachable = reachable;
        _scrubSingular  = singular;
        RecomputeScrubMarkers();
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
        ScrubUnreachableMarkers = unr;
        ScrubSingularityMarkers = sin;
    }

    public RelayCommand FocusCommand                { get; }
    public RelayCommand DropToPlateCommand          { get; }
    public RelayCommand UngroupCommand              { get; }
    public RelayCommand ExplodeCommand              { get; }
    public RelayCommand MeshCleanupCommand          { get; }
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
    /// <summary>Callback set by the viewport code-behind to ungroup the selection.</summary>
    internal Action? OnUngroupRequested { get; set; }
    /// <summary>Callback set by the viewport code-behind to explode disconnected mesh shells.</summary>
    internal Action? OnExplodeRequested { get; set; }
    /// <summary>Callback set by the viewport code-behind to open mesh cleanup on the selection.</summary>
    internal Action? OnMeshCleanupRequested { get; set; }
    /// <summary>Callback set by the viewport code-behind to frame all scene objects in view.</summary>
    internal Action? OnFrameAllRequested    { get; set; }
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

    /// <summary>Applies a saved camera pose; set by the viewport code-behind.</summary>
    internal Action<CameraView>? ApplyCameraState { get; set; }

    public ViewportViewModel()
    {
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
        FocusCommand          = new RelayCommand(() => OnFocusRequested?.Invoke());
        DropToPlateCommand    = new RelayCommand(() => OnDropToPlateRequested?.Invoke());
        UngroupCommand        = new RelayCommand(() => OnUngroupRequested?.Invoke(), () => CanUngroup);
        ExplodeCommand        = new RelayCommand(() => OnExplodeRequested?.Invoke(), () => CanExplode);
        MeshCleanupCommand    = new RelayCommand(() => OnMeshCleanupRequested?.Invoke(), () => CanMeshCleanup);
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

        UpdateSliceCommand = new RelayCommand(
            execute:    () => _ = OnUpdateSliceRequested?.Invoke(),
            canExecute: () => !IsSlicing && IsToolpathSelected && (CanUpdateSlice?.Invoke() ?? false));

        ExportKrlCommand = new RelayCommand(
            execute:    () => _ = OnExportKrlRequested?.Invoke(),
            canExecute: () => IsToolpathSelected && ActiveScrubToolpath is not null);

        SendToRobotCommand = new RelayCommand(
            execute:    () => _ = OnSendToRobotRequested?.Invoke(),
            canExecute: () => IsToolpathSelected && ActiveScrubToolpath is not null && ActiveCell is not null);

        MergeToolpathsCommand = new RelayCommand(
            execute:    () => OnMergeToolpathsRequested?.Invoke(),
            canExecute: () => CanMergeToolpaths);

        TogglePrePrintScanStepCommand = new RelayCommand(
            () => HasPrePrintScanStep = !HasPrePrintScanStep, () => ShowLfam3ToolPicker);
        SelectPrePrintScanPhaseCommand = new RelayCommand(
            () => SelectLfam3WorkflowPhase(0, "Scanner"),
            () => ShowLfam3ToolPicker && HasPrePrintScanStep);
        SelectPrintPhaseCommand = new RelayCommand(
            () => SelectLfam3WorkflowPhase(PrintPhaseIndex, "HV Extruder"), () => ShowLfam3ToolPicker);
        SelectVerifyScanPhaseCommand = new RelayCommand(
            () => SelectLfam3WorkflowPhase(ScanPhaseIndex, "Scanner"), () => ShowLfam3ToolPicker);
        SelectMillPhaseCommand = new RelayCommand(
            () => SelectLfam3WorkflowPhase(MillPhaseIndex, "Spindle"), () => ShowLfam3ToolPicker);
        ToggleLfam3WorkflowCommand = new RelayCommand(
            () => IsLfam3WorkflowExpanded = !IsLfam3WorkflowExpanded, () => ShowLfam3ToolPicker);

        SimulateExtruderPickCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Extruder_Pick"),
            () => CanSimulateToolPick("HV Extruder", MountedToolName, ShowLfam3ToolPicker));
        SimulateExtruderDepositCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Extruder_Deposit"),
            () => CanSimulateToolDeposit("HV Extruder", MountedToolName, ShowLfam3ToolPicker));
        SimulateScannerPickCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Scanner_Pick"),
            () => CanSimulateToolPick("Scanner", MountedToolName, ShowLfam3ToolPicker));
        SimulateScannerDepositCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Scanner_Deposit"),
            () => CanSimulateToolDeposit("Scanner", MountedToolName, ShowLfam3ToolPicker));
        SimulateSpindlePickCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Spindle_Pick"),
            () => CanSimulateToolPick("Spindle", MountedToolName, ShowLfam3ToolPicker));
        SimulateSpindleDepositCommand = new RelayCommand(
            () => RequestToolChangeSimulation("Spindle_Deposit"),
            () => CanSimulateToolDeposit("Spindle", MountedToolName, ShowLfam3ToolPicker));

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
        SeamGuideDraft.Add(point);
        SelectedSeamGuideIndex = SeamGuideDraft.Count - 1;
        IsSeamGuideLayerOpen = true;
        OnPropertyChanged(nameof(HasSeamGuideDraft));
        OnPropertyChanged(nameof(SeamGuideLayerLabel));
        RaiseSeamGuideCommands();
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
            }
        }
    }

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

    /// <summary>Returns live toolpath data for a scene node (wired by the viewport).</summary>
    internal Func<SceneNode, ToolpathSnapshot?>? GetToolpathSnapshot { get; set; }

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

    /// <summary>Re-slices the source mesh at its current transform and replaces the selected toolpath.</summary>
    internal Func<Task>? OnUpdateSliceRequested { get; set; }

    /// <summary>Set by the viewport to gate <see cref="UpdateSliceCommand"/> when a parent mesh exists.</summary>
    internal Func<bool>? CanUpdateSlice { get; set; }

    /// <summary>Callback registered by the viewport code-behind to run the save-file dialog and write the KRL file.</summary>
    internal Func<Task>? OnExportKrlRequested { get; set; }
    internal Func<Task>? OnSendToRobotRequested { get; set; }

    /// <summary>Merges the currently shift-selected toolpaths into one exportable toolpath.</summary>
    internal Action? OnMergeToolpathsRequested { get; set; }

    /// <summary>Re-merges the selected merged toolpath when connector settings change.</summary>
    internal Action? OnMergedSettingsChanged { get; set; }

    /// <summary>Selects a scene node when the user clicks it in the outliner.</summary>
    internal Action<SceneNode>? OnOutlinerSelectRequested { get; set; }

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
    internal Action? OnCellSwapCompleted { get; set; }

    /// <summary>Viewport plays a KUKA tool-change path overlay (Pick/Deposit simulation).</summary>
    internal Action<string>? OnSimulateToolChangeRequested { get; set; }

    internal Action? OnToggleToolChangePlaybackRequested { get; set; }
    internal Action? OnCollapseToolChangePlaybackRequested { get; set; }
    internal Action<int>? OnToolChangeScrubRequested { get; set; }

    /// <summary>Triggers a planar slice using the current additive settings.</summary>
    public RelayCommand SliceCommand { get; }

    /// <summary>Re-slices the parent mesh at its current pose and replaces the selected toolpath.</summary>
    public RelayCommand UpdateSliceCommand { get; }

    /// <summary>Opens a save dialog and exports the selected toolpath as a KUKA KRL .src file.</summary>
    public RelayCommand ExportKrlCommand { get; }

    /// <summary>Exports KRL to the active cell's robot D: share via a pre-targeted save dialog.</summary>
    public RelayCommand SendToRobotCommand { get; }

    /// <summary>Merges shift-selected toolpaths into one continuous toolpath.</summary>
    public RelayCommand MergeToolpathsCommand { get; }

    // -- Outliner / user scene objects -----------------------------------------

    /// <summary>User-imported scene objects shown in the outliner panel.</summary>
    public ObservableCollection<OutlinerItemViewModel> OutlinerItems { get; } = [];

    /// <summary>Nodes queued for GL-thread removal and GPU resource disposal.</summary>
    public ConcurrentQueue<SceneNode> PendingRemoveNodes { get; } = new();

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

    /// <summary>
    /// Registers an already-uploaded scene node in the outliner and queues it for
    /// attachment to the scene root on the GL thread.
    /// </summary>
    internal void AttachUserNode(SceneNode node, OutlinerItemViewModel? adoptToolpathsFrom = null)
    {
        PendingNodes.Enqueue(node);
        var item = RegisterOutlinerItem(node);
        if (adoptToolpathsFrom is not null)
        {
            foreach (var child in adoptToolpathsFrom.Children.ToList())
            {
                adoptToolpathsFrom.RemoveChild(child);
                item.AddChild(child);
            }
        }
        SliceCommand.RaiseCanExecuteChanged();
        NotifyRenderNeeded();
    }

    private OutlinerItemViewModel RegisterOutlinerItem(SceneNode node)
    {
        var item = new OutlinerItemViewModel(node, NotifyRenderNeeded, RemoveUserNode, () => OnNodeHidden?.Invoke(node));
        OutlinerItems.Add(item);
        return item;
    }

    /// <summary>Returns the outliner item whose root node matches <paramref name="node"/>.</summary>
    internal OutlinerItemViewModel? FindOutlinerItem(SceneNode node)
    {
        foreach (var item in OutlinerItems)
            if (item.Node == node) return item;
        return null;
    }

    /// <summary>Returns the outliner item for a toolpath node (top-level or child).</summary>
    internal OutlinerItemViewModel? FindToolpathOutlinerItem(SceneNode node)
    {
        foreach (var item in OutlinerItems)
        {
            if (item.Node == node) return item;
            foreach (var child in item.Children)
                if (child.Node == node) return child;
        }
        return null;
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
        }, () => OnNodeHidden?.Invoke(toolpathNode));

        if (parentItem is not null)
            parentItem.AddChild(item);
        else
            OutlinerItems.Add(item);

        NotifyRenderNeeded();
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
        ClearArmatureScanMeshIfRemoved(item.Node);
        // Queue child toolpath nodes for cleanup before the parent
        foreach (var child in item.Children)
            PendingRemoveNodes.Enqueue(child.Node);
        PendingRemoveNodes.Enqueue(item.Node);
        SliceCommand.RaiseCanExecuteChanged();
        NotifyRenderNeeded();
    }

    /// <summary>Removes all user outliner entries and queues their nodes for GL disposal.</summary>
    public void ClearUserScene()
    {
        foreach (var item in OutlinerItems.ToList())
            RemoveUserNode(item);
        _armatureScanNode = null;
    }

}
