using System;
using System.Collections.ObjectModel;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Parameters for subtractive (relief milling) toolpaths: tool + cutting params, the relief
/// heightmap and its placement, and the editable KRL post-processor header/footer templates.
/// Spindle on/RPM control lives in the templates (KUKA 0-10V → ATV340 VFD), not hardcoded.
/// </summary>
public sealed class SubtractiveSettingsViewModel : ViewModelBase
{
    public SubtractiveSettingsViewModel()
    {
        BrowseHeightmapCommand = new RelayCommand(() => BrowseHeightmapRequested?.Invoke(this, EventArgs.Empty));
        OpenBitLibraryCommand = new RelayCommand(() => OpenBitLibraryRequested?.Invoke(this, EventArgs.Empty));
        SetOperationCommand = new RelayCommand<object>(p =>
        {
            if (p is MillOperationKind kind)
                SelectedOperation = kind;
            else if (p is string s && Enum.TryParse(s, ignoreCase: true, out MillOperationKind parsed))
                SelectedOperation = parsed;
        });

        SetAreaSelectToolCommand = new RelayCommand<object>(p =>
        {
            if (p is MillAreaSelectTool t)
                AreaSelectTool = t;
            else if (p is string s && Enum.TryParse(s, ignoreCase: true, out MillAreaSelectTool parsed))
                AreaSelectTool = parsed;
        });

        ClearAreaSelectionCommand = new RelayCommand(() =>
        {
            AreaSelectTool = MillAreaSelectTool.WholeModel;
            ClearAreaSelection?.Invoke();
            AreaSelectStatus = "Whole model";
        });

        CapturePlanarFromCameraCommand = new RelayCommand(() => CapturePlanarFromCamera?.Invoke());
        CapturePlanarFromPaintCommand = new RelayCommand(() => CapturePlanarFromPaint?.Invoke());

        ReloadBitLibrary();
    }

    // -- Bit library (step 1 BITS) --------------------------------------------

    private MillBitTool? _selectedBit;
    private MillBitCuttingPreset? _selectedCuttingPreset;
    private bool _suppressBitApply;

    /// <summary>Persisted mill bit library (dropdown + library dialog).</summary>
    public ObservableCollection<MillBitTool> BitLibrary { get; } = [];

    /// <summary>Active cutting tool from <see cref="BitLibrary"/>.</summary>
    public MillBitTool? SelectedBit
    {
        get => _selectedBit;
        set
        {
            if (!SetField(ref _selectedBit, value)) return;
            OnPropertyChanged(nameof(BitName));
            OnPropertyChanged(nameof(CuttingPresets));
            // Prefer first preset when tool changes
            _suppressBitApply = true;
            try
            {
                SelectedCuttingPreset = value?.CuttingPresets.FirstOrDefault();
            }
            finally { _suppressBitApply = false; }
            ApplySelectedBitToPanel();
        }
    }

    /// <summary>Cutting-data presets for the active bit (e.g. Default).</summary>
    public IReadOnlyList<MillBitCuttingPreset> CuttingPresets =>
        SelectedBit?.CuttingPresets ?? (IReadOnlyList<MillBitCuttingPreset>)[];

    /// <summary>Active cutting-data preset; drives RPM / feeds when applied.</summary>
    public MillBitCuttingPreset? SelectedCuttingPreset
    {
        get => _selectedCuttingPreset;
        set
        {
            if (!SetField(ref _selectedCuttingPreset, value)) return;
            if (!_suppressBitApply)
                ApplySelectedBitToPanel();
        }
    }

    /// <summary>Display name of the active bit (bound for summary chips).</summary>
    public string BitName
    {
        get => SelectedBit?.Name ?? "No tool";
        set
        {
            if (SelectedBit is null) return;
            if (SelectedBit.Name == value) return;
            SelectedBit.Name = value ?? "";
            OnPropertyChanged(nameof(BitName));
            PersistBitLibrary();
        }
    }

    /// <summary>Opens the tool-library management dialog (wired by the right panel).</summary>
    public RelayCommand OpenBitLibraryCommand { get; }
    public event EventHandler? OpenBitLibraryRequested;

    /// <summary>Reload library from disk and keep current selection if still present.</summary>
    public void ReloadBitLibrary(string? preferToolId = null)
    {
        var keepId = preferToolId ?? SelectedBit?.Id;
        var list = MillBitLibraryLoader.Load();
        BitLibrary.Clear();
        foreach (var t in list)
            BitLibrary.Add(t);

        MillBitTool? pick = null;
        if (!string.IsNullOrEmpty(keepId))
            pick = BitLibrary.FirstOrDefault(t => t.Id == keepId);
        // Prefer the mounted spindle bit (LFAM 3 AP90 3″ flat) on cold start.
        pick ??= BitLibrary.FirstOrDefault(t => t.IsDefaultSpindleBit);
        pick ??= BitLibrary.FirstOrDefault(t => t.Id == MillBitTool.DefaultSpindleBitId);
        pick ??= BitLibrary.FirstOrDefault();
        SelectedBit = pick;
    }

    /// <summary>Replace library contents (from dialog Save) and re-select.</summary>
    public void ReplaceBitLibrary(IEnumerable<MillBitTool> tools, string? selectId = null)
    {
        var list = tools.ToList();
        MillBitLibraryLoader.Save(list);
        ReloadBitLibrary(selectId ?? SelectedBit?.Id);
    }

    public void PersistBitLibrary()
    {
        MillBitLibraryLoader.Save(BitLibrary);
    }

    /// <summary>Push geometry + cutting preset into the live mill panel fields.</summary>
    public void ApplySelectedBitToPanel()
    {
        if (SelectedBit is null) return;
        var bit = SelectedBit;
        var cut = SelectedCuttingPreset ?? bit.DefaultPreset;

        ToolDiameterMm = bit.DiameterMm;
        BallEnd = bit.IsBallEnd;
        MaxDepthMm = bit.MaxDepthMm;
        OnPropertyChanged(nameof(PreviewCylinderLengthMm));
        SpindleRpm = cut.SpindleRpm;
        SpindleDirection = cut.SpindleDirection;
        CuttingFeedMmS = cut.CuttingFeedMmS > 0 ? cut.CuttingFeedMmS : cut.CuttingFeedMmMin / 60.0;
        PlungeFeedMmMin = cut.PlungeFeedMmMin;
        StepoverMm = cut.StepoverMm;
        StepdownMm = cut.StepdownMm;
        FinishAllowanceMm = cut.FinishAllowanceMm;
        RapidZMm = cut.RapidZMm;
        OnPropertyChanged(nameof(BitName));
        OnPropertyChanged(nameof(PreviewCylinderLengthMm));
    }

    /// <summary>Live preview stick-out (mm). Changing this rebuilds the spindle cylinder.</summary>
    public double PreviewCylinderLengthMm
    {
        get => SelectedBit?.EffectiveCylinderLengthMm ?? 50;
        set
        {
            if (SelectedBit is null) return;
            var next = Math.Max(0, value);
            if (Math.Abs(SelectedBit.CylinderLengthMm - next) < 1e-4) return;
            SelectedBit.CylinderLengthMm = next;
            PersistBitLibrary();
            OnPropertyChanged(nameof(PreviewCylinderLengthMm));
        }
    }

    // -- Operation type (step 2 OPERATION) ------------------------------------

    private MillOperationKind _selectedOperation = MillOperationKind.MultiAxisFinishing;
    private bool _stepBitsExpanded = true;
    private bool _stepOperationExpanded = true;
    private bool _stepToolpathingExpanded = true;
    private bool _stepMoreExpanded;

    /// <summary>Step 1 BITS card expansion.</summary>
    public bool StepBitsExpanded
    {
        get => _stepBitsExpanded;
        set => SetField(ref _stepBitsExpanded, value);
    }

    /// <summary>Step 3 TOOLPATHING card expansion.</summary>
    public bool StepToolpathingExpanded
    {
        get => _stepToolpathingExpanded;
        set => SetField(ref _stepToolpathingExpanded, value);
    }

    /// <summary>Catch-all card under OPERATION (legacy / not-yet-sorted panels).</summary>
    public bool StepMoreExpanded
    {
        get => _stepMoreExpanded;
        set => SetField(ref _stepMoreExpanded, value);
    }

    /// <summary>Active milling strategy; drives which parameter groups apply later.</summary>
    public MillOperationKind SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (!SetField(ref _selectedOperation, value)) return;
            OnPropertyChanged(nameof(SelectedOperationDisplayName));
            OnPropertyChanged(nameof(SelectedOperationDescription));
            OnPropertyChanged(nameof(SelectedOperationIcon));
            OnPropertyChanged(nameof(IsMultiAxisFinishing));
            OnPropertyChanged(nameof(IsDrilling));
            OnPropertyChanged(nameof(IsPlanarFacing));
            OnPropertyChanged(nameof(IsPlanarClearing));
            OnPropertyChanged(nameof(IsCutout));
            OnPropertyChanged(nameof(IsContouring));
            OnPropertyChanged(nameof(IsSwarf));
            OnPropertyChanged(nameof(ShowsPlanarToolAxis));
        }
    }

    /// <summary>Catalog of supported operations (tiles in step 2).</summary>
    public IReadOnlyList<MillOperationInfo> OperationCatalog => MillOperationInfo.Catalog;

    public string SelectedOperationDisplayName =>
        MillOperationInfo.Find(SelectedOperation)?.DisplayName ?? SelectedOperation.ToString();

    public string SelectedOperationDescription =>
        MillOperationInfo.Find(SelectedOperation)?.Description ?? "";

    public string SelectedOperationIcon =>
        MillOperationInfo.Find(SelectedOperation)?.Icon ?? "mdi-cog";

    public bool IsMultiAxisFinishing => SelectedOperation == MillOperationKind.MultiAxisFinishing;
    public bool IsDrilling           => SelectedOperation == MillOperationKind.Drilling;
    public bool IsPlanarFacing       => SelectedOperation == MillOperationKind.PlanarFacing;
    public bool IsPlanarClearing     => SelectedOperation == MillOperationKind.PlanarClearing;
    public bool IsCutout             => SelectedOperation == MillOperationKind.Cutout;
    public bool IsContouring         => SelectedOperation == MillOperationKind.Contouring;
    public bool IsSwarf              => SelectedOperation == MillOperationKind.Swarf;
    public bool ShowsPlanarToolAxis  => IsPlanarFacing || IsPlanarClearing;

    /// <summary>Step 2 OPERATION card expansion.</summary>
    public bool StepOperationExpanded
    {
        get => _stepOperationExpanded;
        set => SetField(ref _stepOperationExpanded, value);
    }

    /// <summary>Selects a <see cref="MillOperationKind"/> from a tile (enum or name string).</summary>
    public RelayCommand<object> SetOperationCommand { get; }

    // -- Area selection (under OPERATION — which part of the model to machine) --

    private MillAreaSelectTool _areaSelectTool = MillAreaSelectTool.WholeModel;
    private string _areaSelectStatus = "Whole model";

    /// <summary>Active model-area pick tool for the current mill operation.</summary>
    public MillAreaSelectTool AreaSelectTool
    {
        get => _areaSelectTool;
        set
        {
            if (!SetField(ref _areaSelectTool, value)) return;
            OnPropertyChanged(nameof(IsAreaWholeModel));
            OnPropertyChanged(nameof(IsAreaFace));
            OnPropertyChanged(nameof(IsAreaBox));
            OnPropertyChanged(nameof(IsAreaLasso));
            OnPropertyChanged(nameof(IsAreaBrush));
            ApplyAreaSelectTool?.Invoke(value);
        }
    }

    public bool IsAreaWholeModel => AreaSelectTool == MillAreaSelectTool.WholeModel;
    public bool IsAreaFace       => AreaSelectTool == MillAreaSelectTool.Face;
    public bool IsAreaBox        => AreaSelectTool == MillAreaSelectTool.Box;
    public bool IsAreaLasso      => AreaSelectTool == MillAreaSelectTool.Lasso;
    public bool IsAreaBrush      => AreaSelectTool == MillAreaSelectTool.Brush;

    /// <summary>Short status line under the area tools (e.g. “3 faces selected”).</summary>
    public string AreaSelectStatus
    {
        get => _areaSelectStatus;
        set => SetField(ref _areaSelectStatus, value ?? "");
    }

    /// <summary>Set area tool from a tile (enum name string or <see cref="MillAreaSelectTool"/>).</summary>
    public RelayCommand<object> SetAreaSelectToolCommand { get; }

    /// <summary>Clear the current mill area selection (viewport wiring).</summary>
    public RelayCommand ClearAreaSelectionCommand { get; }

    /// <summary>Host sets this to arm viewport selection (SelectionMode / marquee / clear).</summary>
    public Action<MillAreaSelectTool>? ApplyAreaSelectTool { get; set; }

    /// <summary>Host clears face/region selection for the mill operation.</summary>
    public Action? ClearAreaSelection { get; set; }

    /// <summary>Host captures the current camera as the planar approach (eye → work).</summary>
    public Action? CapturePlanarFromCamera { get; set; }

    /// <summary>Host averages the painted mill area into the planar axis.</summary>
    public Action? CapturePlanarFromPaint { get; set; }

    // -- Planar tool axis (facing / clearing) ---------------------------------

    private MillPlanarAxisOption _planarToolAxis = MillPlanarAxisOption.Default;
    private double _planarTiltDeg;
    private double _planarAzimuthDeg;
    private double _planarCustomX;
    private double _planarCustomY;
    private double _planarCustomZ = -1;
    private double _planarCapturedX;
    private double _planarCapturedY;
    private double _planarCapturedZ = -1;

    public static IReadOnlyList<MillPlanarAxisOption> PlanarToolAxisOptions => MillPlanarAxisOption.All;

    /// <summary>Where T12 +Z points before tilt. Raster is along the opposite direction.</summary>
    public MillPlanarAxisOption PlanarToolAxis
    {
        get => _planarToolAxis ?? MillPlanarAxisOption.Default;
        set
        {
            var next = value ?? MillPlanarAxisOption.Default;
            if (!SetField(ref _planarToolAxis, next)) return;
            OnPropertyChanged(nameof(PlanarToolAxisIndex));
            OnPropertyChanged(nameof(IsPlanarAxisCustom));
            NotifyPlanarAxis();
        }
    }

    public int PlanarToolAxisIndex
    {
        get => PlanarToolAxisOptions.ToList().FindIndex(o => o.Kind == PlanarToolAxis.Kind);
        set
        {
            if (value < 0 || value >= PlanarToolAxisOptions.Count) return;
            PlanarToolAxis = PlanarToolAxisOptions[value];
        }
    }

    public bool IsPlanarAxisCustom => PlanarToolAxis.Kind == MillPlanarAxisKind.Custom;

    /// <summary>Tilt of T12 +Z off the chosen axis (deg).</summary>
    public double PlanarTiltDeg
    {
        get => _planarTiltDeg;
        set
        {
            if (!SetField(ref _planarTiltDeg, Math.Clamp(value, -90, 90))) return;
            NotifyPlanarAxis();
        }
    }

    /// <summary>Which way the tilt leans, around the chosen tool axis (deg).</summary>
    public double PlanarAzimuthDeg
    {
        get => _planarAzimuthDeg;
        set
        {
            if (!SetField(ref _planarAzimuthDeg, value)) return;
            NotifyPlanarAxis();
        }
    }

    public double PlanarCustomX
    {
        get => _planarCustomX;
        set { if (SetField(ref _planarCustomX, value)) NotifyPlanarAxis(); }
    }

    public double PlanarCustomY
    {
        get => _planarCustomY;
        set { if (SetField(ref _planarCustomY, value)) NotifyPlanarAxis(); }
    }

    public double PlanarCustomZ
    {
        get => _planarCustomZ;
        set { if (SetField(ref _planarCustomZ, value)) NotifyPlanarAxis(); }
    }

    public string PlanarAxisStatus
    {
        get
        {
            var tool = ResolvePlanarToolAxis();
            var approach = MillPlanarOrientation.ApproachFromToolAxis(tool);
            return $"T12 +Z = ({tool.X:0.###}, {tool.Y:0.###}, {tool.Z:0.###})  ·  project from ({approach.X:0.###}, {approach.Y:0.###}, {approach.Z:0.###})";
        }
    }

    public RelayCommand CapturePlanarFromCameraCommand { get; }
    public RelayCommand CapturePlanarFromPaintCommand { get; }

    void NotifyPlanarAxis()
    {
        OnPropertyChanged(nameof(PlanarAxisStatus));
        OnPropertyChanged(nameof(IsPlanarAxisCustom));
    }

    /// <summary>T12 +Z after preset + tilt. Used by planar Generate + mill ABC.</summary>
    public System.Numerics.Vector3 ResolvePlanarToolAxis()
    {
        var kind = PlanarToolAxis.Kind;
        var captured = kind switch
        {
            MillPlanarAxisKind.Custom => new System.Numerics.Vector3(
                (float)PlanarCustomX, (float)PlanarCustomY, (float)PlanarCustomZ),
            _ => new System.Numerics.Vector3(
                (float)_planarCapturedX, (float)_planarCapturedY, (float)_planarCapturedZ),
        };
        return MillPlanarOrientation.ResolveToolAxis(
            kind, captured, (float)PlanarTiltDeg, (float)PlanarAzimuthDeg);
    }

    /// <summary>World direction the cutter comes from (raster +Z).</summary>
    public System.Numerics.Vector3 ResolvePlanarApproach()
        => MillPlanarOrientation.ApproachFromToolAxis(ResolvePlanarToolAxis());

    /// <summary>Store a captured T12 +Z (from paint or camera) and switch the combo.</summary>
    public void SetCapturedToolAxis(System.Numerics.Vector3 toolZ, MillPlanarAxisKind kind)
    {
        if (toolZ.LengthSquared() < 1e-12f) toolZ = -System.Numerics.Vector3.UnitZ;
        toolZ = System.Numerics.Vector3.Normalize(toolZ);
        _planarCapturedX = toolZ.X;
        _planarCapturedY = toolZ.Y;
        _planarCapturedZ = toolZ.Z;
        if (kind == MillPlanarAxisKind.Custom)
        {
            _planarCustomX = toolZ.X;
            _planarCustomY = toolZ.Y;
            _planarCustomZ = toolZ.Z;
            OnPropertyChanged(nameof(PlanarCustomX));
            OnPropertyChanged(nameof(PlanarCustomY));
            OnPropertyChanged(nameof(PlanarCustomZ));
        }
        PlanarToolAxis = MillPlanarAxisOption.Find(kind);
        NotifyPlanarAxis();
    }

    // -- Tool geometry (from bit library; not edited in TOOLPATHING) ------------

    private double _toolDiameterMm = 76.2;
    private bool   _ballEnd;
    private double _maxDepthMm;   // 0 = unlimited

    /// <summary>Cutter diameter (mm); used for the anti-gouge inverse offset.</summary>
    public double ToolDiameterMm { get => _toolDiameterMm; set => SetField(ref _toolDiameterMm, value); }
    /// <summary>True = ball-nose (rounded), false = flat end-mill.</summary>
    public bool   BallEnd        { get => _ballEnd; set => SetField(ref _ballEnd, value); }
    /// <summary>Hard depth limit below the reference plane (mm); 0 = unlimited.</summary>
    public double MaxDepthMm     { get => _maxDepthMm; set => SetField(ref _maxDepthMm, value); }

    // -- Toolpathing (step 3) — Passes / Travel / Movement ---------------------

    private int    _numberOfDepthCuts = 1;
    private double _stepoverMm = 4;
    private double _stepdownMm = 2;
    private double _finishAllowanceMm; // stock to leave
    private double _passAngleDeg;
    private string _passStrategy = "Linear";
    private string _cuttingDirection = "Both ways";
    private bool   _keepToolWithinSurface = true;
    private bool   _clipPath;
    private bool   _enableAntiGouging;
    private double _approachClearanceMm = 100;
    private double _rapidZMm = 50; // retract height
    private double _feedRateMmMin = 10.44 * 60; // 10.44 mm/s
    private double _skimFeedMmS = 60;
    private double _plungeFeedMmMin = 400;
    private double _spindleRpm = 2088;
    private SpindleDirection _spindleDirection = SpindleDirection.Clockwise;

    public static IReadOnlyList<string> PassStrategyOptions { get; } =
        ["Linear", "Spiral", "Offset", "Adaptive"];

    public static IReadOnlyList<string> CuttingDirectionOptions { get; } =
        ["Both ways", "Climb", "Conventional", "One way"];

    public static IReadOnlyList<SpindleDirection> SpindleDirectionOptions { get; } =
        [SpindleDirection.Clockwise, SpindleDirection.CounterClockwise];

    /// <summary>How many Z depth levels to cut (roughing stack).</summary>
    public int NumberOfDepthCuts
    {
        get => _numberOfDepthCuts;
        set => SetField(ref _numberOfDepthCuts, Math.Max(1, value));
    }

    /// <summary>Finish raster spacing (mm).</summary>
    public double StepoverMm { get => _stepoverMm; set => SetField(ref _stepoverMm, value); }

    /// <summary>Axial stepdown per depth cut (mm).</summary>
    public double StepdownMm { get => _stepdownMm; set => SetField(ref _stepdownMm, value); }

    /// <summary>Stock to leave on the surface (mm). Same as finish allowance for generators.</summary>
    public double FinishAllowanceMm { get => _finishAllowanceMm; set => SetField(ref _finishAllowanceMm, value); }
    public double StockToLeaveMm
    {
        get => FinishAllowanceMm;
        set => FinishAllowanceMm = value;
    }

    public string PassStrategy
    {
        get => _passStrategy;
        set => SetField(ref _passStrategy, value ?? "Linear");
    }

    public string CuttingDirection
    {
        get => _cuttingDirection;
        set => SetField(ref _cuttingDirection, value ?? "Both ways");
    }

    public double PassAngleDeg { get => _passAngleDeg; set => SetField(ref _passAngleDeg, value); }
    public bool KeepToolWithinSurface { get => _keepToolWithinSurface; set => SetField(ref _keepToolWithinSurface, value); }
    public bool ClipPath { get => _clipPath; set => SetField(ref _clipPath, value); }
    public bool EnableAntiGouging { get => _enableAntiGouging; set => SetField(ref _enableAntiGouging, value); }

    /// <summary>Approach clearance above the surface before engaging (mm).</summary>
    public double ApproachClearanceMm { get => _approachClearanceMm; set => SetField(ref _approachClearanceMm, value); }

    /// <summary>Retract / safe rapid height (mm). Feeds MillSettings.RapidZMm.</summary>
    public double RapidZMm { get => _rapidZMm; set => SetField(ref _rapidZMm, value); }
    public double RetractHeightMm
    {
        get => RapidZMm;
        set => RapidZMm = value;
    }

    /// <summary>Cutting feed (mm/min) for toolpath generators.</summary>
    public double FeedRateMmMin
    {
        get => _feedRateMmMin;
        set
        {
            if (!SetField(ref _feedRateMmMin, value)) return;
            OnPropertyChanged(nameof(CuttingFeedMmS));
        }
    }

    /// <summary>Cutting feed (mm/s) — UI unit matching shop CAM cards.</summary>
    public double CuttingFeedMmS
    {
        get => FeedRateMmMin / 60.0;
        set => FeedRateMmMin = value * 60.0;
    }

    /// <summary>Skim / air-cut feed (mm/s).</summary>
    public double SkimFeedMmS { get => _skimFeedMmS; set => SetField(ref _skimFeedMmS, value); }

    /// <summary>Plunge feed (mm/min).</summary>
    public double PlungeFeedMmMin { get => _plungeFeedMmMin; set => SetField(ref _plungeFeedMmMin, value); }

    public double SpindleRpm { get => _spindleRpm; set => SetField(ref _spindleRpm, value); }

    public SpindleDirection SpindleDirection
    {
        get => _spindleDirection;
        set => SetField(ref _spindleDirection, value);
    }

    // -- Relief heightmap ------------------------------------------------------

    private string _heightmapPath = string.Empty;
    private double _heightScaleMm = 5;
    private bool   _invertHeightmap;
    private bool   _autoReferenceFromTop = true;
    private double _referencePlaneZ;
    private bool   _autoFootprint = true;
    private double _footprintOriginX, _footprintOriginY, _footprintWidthMm = 100, _footprintLengthMm = 100;

    /// <summary>Path to the grayscale relief image (PNG/JPG). White = high surface.</summary>
    public string HeightmapPath  { get => _heightmapPath; set => SetField(ref _heightmapPath, value); }
    /// <summary>Relief depth between black and white (mm).</summary>
    public double HeightScaleMm  { get => _heightScaleMm; set => SetField(ref _heightScaleMm, value); }
    /// <summary>Flip black/white.</summary>
    public bool   InvertHeightmap { get => _invertHeightmap; set => SetField(ref _invertHeightmap, value); }

    // -- Displaced surface (PBR maps -> geometry) ------------------------------

    private double _displacementDistanceMm = 3;

    /// <summary>
    /// How far the detail map pushes the low-poly surface along its normal (mm). The map's
    /// source is the supplied displacement/bump/height image (<see cref="HeightmapPath"/>) if set,
    /// otherwise the model's embedded normal map integrated to height.
    /// </summary>
    public double DisplacementDistanceMm { get => _displacementDistanceMm; set => SetField(ref _displacementDistanceMm, value); }

    private double _analysisToleranceMm = 0.1;
    private string _millAnalysisText = string.Empty;

    /// <summary>Tolerance band (mm) for the gouge/residual fail-rate analysis after a multi-axis pass.</summary>
    public double AnalysisToleranceMm { get => _analysisToleranceMm; set => SetField(ref _analysisToleranceMm, value); }

    /// <summary>Human-readable result of the last multi-axis surface deviation analysis (shown in the panel).</summary>
    public string MillAnalysisText { get => _millAnalysisText; set => SetField(ref _millAnalysisText, value); }

    /// <summary>Use the selected part's top-face Z as the reference plane.</summary>
    public bool   AutoReferenceFromTop { get => _autoReferenceFromTop; set => SetField(ref _autoReferenceFromTop, value); }
    /// <summary>Manual reference plane Z (world mm) when <see cref="AutoReferenceFromTop"/> is false.</summary>
    public double ReferencePlaneZ { get => _referencePlaneZ; set => SetField(ref _referencePlaneZ, value); }

    /// <summary>Map the relief onto the selected part's XY bounding box.</summary>
    public bool   AutoFootprint  { get => _autoFootprint; set => SetField(ref _autoFootprint, value); }
    public double FootprintOriginX { get => _footprintOriginX; set => SetField(ref _footprintOriginX, value); }
    public double FootprintOriginY { get => _footprintOriginY; set => SetField(ref _footprintOriginY, value); }
    public double FootprintWidthMm { get => _footprintWidthMm; set => SetField(ref _footprintWidthMm, value); }
    public double FootprintLengthMm { get => _footprintLengthMm; set => SetField(ref _footprintLengthMm, value); }

    /// <summary>Opens the heightmap file picker (handled by the window).</summary>
    public RelayCommand BrowseHeightmapCommand { get; }
    public event EventHandler? BrowseHeightmapRequested;

    // -- KRL post-processor templates ------------------------------------------

    private string _headerTemplate = string.Empty;
    private string _footerTemplate = string.Empty;

    /// <summary>Editable KRL program header (spindle on/RPM lives here). Supports {PROGNAME}, {TOOL_NO}, {DATE}, etc.</summary>
    public string HeaderTemplate { get => _headerTemplate; set => SetField(ref _headerTemplate, value); }
    /// <summary>Editable KRL program footer (spindle off lives here).</summary>
    public string FooterTemplate { get => _footerTemplate; set => SetField(ref _footerTemplate, value); }
}
