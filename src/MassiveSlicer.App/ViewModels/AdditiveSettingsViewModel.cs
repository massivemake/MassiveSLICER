using System.Collections.ObjectModel;
using System.Globalization;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Parameters for additive (wire-arc / paste extrusion) slicing.
/// All dimensions are stored in millimetres; unit conversion for display
/// is handled at the binding layer.
/// </summary>
public sealed class AdditiveSettingsViewModel : ViewModelBase
{
    /// <summary>KRL SRC post-processing rules and header/footer templates.</summary>
    public KrlPostProcessSettingsViewModel KrlPostProcess { get; } = new();

    public AdditiveSettingsViewModel()
    {
        SetDefaultHomePositionCommand = new RelayCommand(() => OnSetDefaultHomePositionRequested?.Invoke());
        OpenSeamEditorCommand            = new RelayCommand(() => OnOpenSeamEditorRequested?.Invoke());
        OpenCurvedBoundaryEditorCommand  = new RelayCommand(() => OnOpenCurvedBoundaryEditorRequested?.Invoke());
        ImportCurvedBoundariesCommand    = new RelayCommand(() => OnImportCurvedBoundariesRequested?.Invoke());

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BeadWidth) or nameof(LayerHeight) or nameof(PrintSpeed)
                or nameof(SelectedPresetIndex) or nameof(ExtrusionSpeedOffset))
                OnPropertyChanged(nameof(ExtrusionSpeedPercent));

            if (e.PropertyName is nameof(SelectedPresetIndex) or nameof(TemperatureOffset))
                OnPropertyChanged(nameof(ExportTemperatureC));
        };
    }

    // -- Geometry -------------------------------------------------------------

    private double _layerHeight = 3.0;

    /// <summary>Height of each deposited layer in mm (0.5 - 100).</summary>
    public double LayerHeight
    {
        get => _layerHeight;
        set => SetField(ref _layerHeight, Math.Clamp(value, 0.5, 100.0));
    }

    private double _beadWidth = 6.0;

    /// <summary>Width of the deposited bead in mm (1 - 100).</summary>
    public double BeadWidth
    {
        get => _beadWidth;
        set => SetField(ref _beadWidth, Math.Clamp(value, 1.0, 100.0));
    }

    private bool   _useDisplacedStock;
    private double _stockAllowanceMm = 2.0;

    /// <summary>
    /// Print the displaced surface (low-poly mesh + PBR-map detail from the MILLING panel) instead of
    /// the raw mesh, so the blank carries the detail and the later mill has material to cut.
    /// </summary>
    public bool UseDisplacedStock
    {
        get => _useDisplacedStock;
        set => SetField(ref _useDisplacedStock, value);
    }

    /// <summary>Uniform extra skin added over the displaced surface for the mill to remove (mm).</summary>
    public double StockAllowanceMm
    {
        get => _stockAllowanceMm;
        set => SetField(ref _stockAllowanceMm, Math.Clamp(value, 0.0, 50.0));
    }

    private double _firstLayerHeight = 3.0;

    /// <summary>Override height for the first layer only, in mm.</summary>
    public double FirstLayerHeight
    {
        get => _firstLayerHeight;
        set => SetField(ref _firstLayerHeight, Math.Clamp(value, 0.5, 100.0));
    }

    /// <summary>Mirrors AdaptiveLayerHeight — layer-stripe preview is active whenever adaptive mode is on.</summary>
    public bool ShowLayerPreview => _adaptiveLayerHeight;

    // -- Adaptive layer height ------------------------------------------------

    private bool _adaptiveLayerHeight;

    /// <summary>When true, the planar slicer adapts layer spacing to local surface slope.</summary>
    public bool AdaptiveLayerHeight
    {
        get => _adaptiveLayerHeight;
        set
        {
            if (SetField(ref _adaptiveLayerHeight, value))
            {
                OnPropertyChanged(nameof(ShowAdaptiveControls));
                OnPropertyChanged(nameof(ShowLayerPreview));
            }
        }
    }

    /// <summary>Visible when adaptive is checked and method is Planar.</summary>
    public bool ShowAdaptiveControls => _adaptiveLayerHeight && _method == SliceMethod.Planar;

    /// <summary>Visible when method is Planar (for the checkbox itself).</summary>
    public bool ShowAdaptiveLayerHeight => _method == SliceMethod.Planar;

    // -- Slicing mode (Normal vs Surface) -------------------------------------

    public string[] SlicingModeOptions { get; } = ["Normal", "Surface"];

    private string _slicingMode = "Normal";

    /// <summary>
    /// Normal = volumetric shells + infill. Surface = boundary/cladding paths; tool stays vertical unless Overhang orientation is on.
    /// </summary>
    public string SlicingMode
    {
        get => _slicingMode;
        set
        {
            if (SetField(ref _slicingMode, value))
            {
                OnPropertyChanged(nameof(ShowInfillControls));
                OnPropertyChanged(nameof(SurfaceModeActive));
            }
        }
    }

    public bool SurfaceModeActive => _slicingMode == "Surface";

    /// <summary>Surface mode is for planar/angled strategies (not geodesic/curved).</summary>
    public bool ShowSlicingMode => _method is not SliceMethod.Geodesic and not SliceMethod.Curved;

    private double _adaptiveQuality = 0.5;

    /// <summary>0 = finest detail (thin layers on slopes), 1 = fastest (thick layers where possible).</summary>
    public double AdaptiveQuality
    {
        get => _adaptiveQuality;
        set => SetField(ref _adaptiveQuality, Math.Clamp(value, 0.0, 1.0));
    }

    private double _minLayerHeight = 1.0;

    /// <summary>Minimum layer height used by adaptive slicing (mm).</summary>
    public double MinLayerHeight
    {
        get => _minLayerHeight;
        set => SetField(ref _minLayerHeight, Math.Clamp(value, 0.1, 100.0));
    }

    // -- Slicing method -------------------------------------------------------

    private SliceMethod _method = SliceMethod.Planar;

    /// <summary>Which slicing algorithm to use.</summary>
    public SliceMethod Method
    {
        get => _method;
        set
        {
            if (SetField(ref _method, value))
            {
                OnPropertyChanged(nameof(MethodDisplayName));
                OnPropertyChanged(nameof(ShowTiltAngle));
                OnPropertyChanged(nameof(ShowContourOffsetOption));
                OnPropertyChanged(nameof(ShowAdaptiveLayerHeight));
                OnPropertyChanged(nameof(ShowAdaptiveControls));
                OnPropertyChanged(nameof(ShowSlicingMode));
                OnPropertyChanged(nameof(ShowCurvedControls));
                OnPropertyChanged(nameof(IsCurvedMethod));
            }
        }
    }

    public string[] AvailableMethodNames { get; } =
        ["Planar", "Angled", "Geodesic (Experimental)", "Curved (Sweep)"];

    public string MethodDisplayName
    {
        get => Method switch
        {
            SliceMethod.Angled   => "Angled",
            SliceMethod.Geodesic => "Geodesic (Experimental)",
            SliceMethod.Curved   => "Curved (Sweep)",
            _                    => "Planar",
        };
        set => Method = value switch
        {
            "Angled"                  => SliceMethod.Angled,
            "Geodesic (Experimental)" => SliceMethod.Geodesic,
            "Geodesic"                => SliceMethod.Geodesic,
            "Curved (Sweep)"          => SliceMethod.Curved,
            "Curved"                  => SliceMethod.Curved,
            _                         => SliceMethod.Planar,
        };
    }

    public bool IsCurvedMethod          => Method == SliceMethod.Curved;
    public bool ShowCurvedControls      => Method == SliceMethod.Curved;
    public bool ShowTiltAngle           => Method == SliceMethod.Angled;
    public bool ShowContourOffsetOption => Method is not SliceMethod.Geodesic and not SliceMethod.Curved;

    private bool _disableContourOffset;

    /// <summary>When true, skips the bead-width/2 inset so the raw contour is the centerline.</summary>
    public bool DisableContourOffset
    {
        get => _disableContourOffset;
        set => SetField(ref _disableContourOffset, value);
    }

    public string[] SeamModeOptions { get; } = ["Normal", "Zig-zag"];

    private string _seamMode = "Normal";

    public string SeamMode
    {
        get => _seamMode;
        set => SetField(ref _seamMode, value);
    }

    /// <summary>World-space seam position guides for planar slicing.</summary>
    public ObservableCollection<SeamGuidePoint> SeamGuides { get; } = [];

    public string SeamGuideSummary =>
        SeamGuides.Count == 0 ? "No guides" : $"{SeamGuides.Count} guide point(s)";

    public void SetSeamGuides(IEnumerable<SeamGuidePoint> guides)
    {
        SeamGuides.Clear();
        foreach (var g in guides)
            SeamGuides.Add(g);
        OnPropertyChanged(nameof(SeamGuideSummary));
    }

    public IReadOnlyList<SeamGuidePoint> BuildSeamGuideList() => [.. SeamGuides];

    /// <summary>Opens the viewport seam guide editor.</summary>
    public RelayCommand OpenSeamEditorCommand { get; }

    internal Action? OnOpenSeamEditorRequested { get; set; }

    // -- Curved slicing boundaries --------------------------------------------

    public string[] CurvedBoundarySourceOptions { get; } = ["Auto", "Viewport Pick", "JSON Import"];

    private string _curvedBoundarySource = "Auto";

    public string CurvedBoundarySourceDisplay
    {
        get => _curvedBoundarySource;
        set
        {
            if (SetField(ref _curvedBoundarySource, value))
                OnPropertyChanged(nameof(CurvedBoundarySummary));
        }
    }

    public CurvedBoundarySource CurvedBoundarySource => _curvedBoundarySource switch
    {
        "Viewport Pick" => CurvedBoundarySource.ViewportPick,
        "JSON Import"   => CurvedBoundarySource.JsonImport,
        _               => CurvedBoundarySource.AutoDetect,
    };

    private double _curvedAutoDetectBandMm = 2.0;

    public double CurvedAutoDetectBandMm
    {
        get => _curvedAutoDetectBandMm;
        set => SetField(ref _curvedAutoDetectBandMm, Math.Clamp(value, 0.1, 50.0));
    }

    private bool _curvedEnableRegionSplit = true;

    public bool CurvedEnableRegionSplit
    {
        get => _curvedEnableRegionSplit;
        set => SetField(ref _curvedEnableRegionSplit, value);
    }

    public ObservableCollection<int> CurvedBoundaryLowVertices  { get; } = [];
    public ObservableCollection<int> CurvedBoundaryHighVertices { get; } = [];

    public string CurvedBoundarySummary =>
        $"LOW: {CurvedBoundaryLowVertices.Count} verts, HIGH: {CurvedBoundaryHighVertices.Count} verts";

    public void SetCurvedBoundaries(IEnumerable<int> low, IEnumerable<int> high)
    {
        CurvedBoundaryLowVertices.Clear();
        CurvedBoundaryHighVertices.Clear();
        foreach (var v in low)  CurvedBoundaryLowVertices.Add(v);
        foreach (var v in high) CurvedBoundaryHighVertices.Add(v);
        OnPropertyChanged(nameof(CurvedBoundarySummary));
    }

    public IReadOnlyList<int> BuildCurvedLowBoundaryList()  => [.. CurvedBoundaryLowVertices];
    public IReadOnlyList<int> BuildCurvedHighBoundaryList() => [.. CurvedBoundaryHighVertices];

    public RelayCommand OpenCurvedBoundaryEditorCommand { get; }
    public RelayCommand ImportCurvedBoundariesCommand { get; }

    internal Action? OnOpenCurvedBoundaryEditorRequested { get; set; }
    internal Func<Task>? OnImportCurvedBoundariesRequested { get; set; }

    private double _passAngle;

    /// <summary>Rotation of each pass relative to the previous, in degrees (Planar/Angled).</summary>
    public double PassAngle
    {
        get => _passAngle;
        set => SetField(ref _passAngle, value);
    }

    private double _tiltAngle;

    /// <summary>Tilt around Y-axis in degrees (leans the cutting plane toward ±X).</summary>
    public double TiltAngle
    {
        get => _tiltAngle;
        set => SetField(ref _tiltAngle, Math.Clamp(value, -89.0, 89.0));
    }

    private double _tiltAngleX;

    /// <summary>Tilt around X-axis in degrees (leans the cutting plane toward ±Y).</summary>
    public double TiltAngleX
    {
        get => _tiltAngleX;
        set => SetField(ref _tiltAngleX, Math.Clamp(value, -89.0, 89.0));
    }

    // -- Motion ---------------------------------------------------------------

    private double _printSpeed = 100.0;

    /// <summary>Deposition print speed in mm/s.</summary>
    public double PrintSpeed
    {
        get => _printSpeed;
        set => SetField(ref _printSpeed, Math.Clamp(value, 1.0, 2000.0));
    }

    private double _travelSpeed = 120.0;

    /// <summary>Travel (non-extrusion) move speed in mm/s.</summary>
    public double TravelSpeed
    {
        get => _travelSpeed;
        set => SetField(ref _travelSpeed, Math.Clamp(value, 1.0, 2000.0));
    }

    private double _apoCvel = 100.0;

    /// <summary>
    /// KUKA $APO.CVEL value (0–100). Controls the minimum speed fraction the robot
    /// must maintain through corners. 50 = slow to at most 50% of programmed speed
    /// at a sharp turn; 100 = maintain full speed (no blending slowdown).
    /// Used only by the simulation velocity profile — set this to match your KRL preset.
    /// </summary>
    public double ApoCvel
    {
        get => _apoCvel;
        set => SetField(ref _apoCvel, Math.Clamp(value, 0.0, 100.0));
    }

    private int _acceleration = 100;

    /// <summary>Acceleration as a percentage of robot-rated maximum (1 - 100).</summary>
    public int Acceleration
    {
        get => _acceleration;
        set => SetField(ref _acceleration, Math.Clamp(value, 1, 100));
    }

    private double _approachZ = 50.0;

    /// <summary>Z height above the part to approach before each pass, in mm.</summary>
    public double ApproachZ
    {
        get => _approachZ;
        set => SetField(ref _approachZ, value);
    }

    // -- KUKA frame indices ----------------------------------------------------

    private int _toolDataIndex = 1;

    /// <summary>KUKA TOOL_DATA index (1 - 16) used in the generated KRL program.</summary>
    public int ToolDataIndex
    {
        get => _toolDataIndex;
        set => SetField(ref _toolDataIndex, Math.Clamp(value, 1, 16));
    }

    private int _baseDataIndex = 1;

    /// <summary>KUKA BASE_DATA index (1 - 32) used in the generated KRL program.</summary>
    public int BaseDataIndex
    {
        get => _baseDataIndex;
        set => SetField(ref _baseDataIndex, Math.Clamp(value, 1, 32));
    }

    // -- Wave effect -------------------------------------------------------------

    public string[] WaveEffectOptions { get; } = ["None", "Sine", "Sawtooth", "Triangle"];

    private string _waveEffect = "None";

    /// <summary>Selected wave effect type. "None" disables the effect.</summary>
    public string WaveEffect
    {
        get => _waveEffect;
        set
        {
            if (SetField(ref _waveEffect, value))
                OnPropertyChanged(nameof(ShowWaveControls));
        }
    }

    public bool ShowWaveControls => WaveEffect != "None";

    private double _waveAmplitude = 3.0;

    /// <summary>Peak displacement in mm (0.5 – 100).</summary>
    public double WaveAmplitude
    {
        get => _waveAmplitude;
        set => SetField(ref _waveAmplitude, Math.Clamp(value, 0.5, 100.0));
    }

    public string[] WaveFrequencyModeOptions { get; } = ["Wavelength", "Cycles"];

    private string _waveFrequencyMode = "Wavelength";

    public string WaveFrequencyMode
    {
        get => _waveFrequencyMode;
        set
        {
            if (SetField(ref _waveFrequencyMode, value))
            {
                OnPropertyChanged(nameof(ShowWavelengthInput));
                OnPropertyChanged(nameof(ShowCyclesInput));
                OnPropertyChanged(nameof(ShowSingleWavelength));
                OnPropertyChanged(nameof(ShowGradientWavelength));
            }
        }
    }

    public bool ShowWavelengthInput => WaveFrequencyMode == "Wavelength";
    public bool ShowCyclesInput     => WaveFrequencyMode == "Cycles";

    private double _waveWavelength = 20.0;

    /// <summary>Length of one complete wave cycle in mm (1 – 1000).</summary>
    public double WaveWavelength
    {
        get => _waveWavelength;
        set => SetField(ref _waveWavelength, Math.Clamp(value, 1.0, 1000.0));
    }

    private int _waveCycles = 8;

    /// <summary>Fixed number of complete wave cycles per layer (1 – 500). Used when WaveFrequencyMode == Cycles.</summary>
    public int WaveCycles
    {
        get => _waveCycles;
        set => SetField(ref _waveCycles, Math.Clamp(value, 1, 500));
    }

    private double _waveShape = 1.0;

    /// <summary>Wave shape: 1.0 = full waveform, lower clips peaks toward a square wave (0.01 – 1.0).</summary>
    public double WaveShape
    {
        get => _waveShape;
        set => SetField(ref _waveShape, Math.Clamp(value, 0.01, 1.0));
    }

    private double _waveStagger = 0.0;

    /// <summary>
    /// Phase offset per layer as a fraction of one wavelength (0 – 1).
    /// 0 = all layers identical. 0.5 = consecutive layers alternate peak/valley.
    /// </summary>
    public double WaveStagger
    {
        get => _waveStagger;
        set => SetField(ref _waveStagger, Math.Clamp(value, 0.0, 1.0));
    }

    // -- Wave gradient ----------------------------------------------------------

    private bool _waveGradient;

    public bool WaveGradient
    {
        get => _waveGradient;
        set
        {
            if (SetField(ref _waveGradient, value))
            {
                OnPropertyChanged(nameof(ShowWaveGradientControls));
                OnPropertyChanged(nameof(ShowSingleAmplitude));
                OnPropertyChanged(nameof(ShowSingleWavelength));
                OnPropertyChanged(nameof(ShowGradientWavelength));
            }
        }
    }

    public bool ShowWaveGradientControls => WaveGradient;
    public bool ShowSingleAmplitude      => !WaveGradient;
    public bool ShowSingleWavelength     => ShowWavelengthInput && !WaveGradient;
    public bool ShowGradientWavelength   => ShowWavelengthInput && WaveGradient;

    private double _waveAmplitudeBottom = 0.0;
    public double WaveAmplitudeBottom
    {
        get => _waveAmplitudeBottom;
        set => SetField(ref _waveAmplitudeBottom, Math.Clamp(value, 0.0, 100.0));
    }

    private double _waveAmplitudeTop = 3.0;
    public double WaveAmplitudeTop
    {
        get => _waveAmplitudeTop;
        set => SetField(ref _waveAmplitudeTop, Math.Clamp(value, 0.0, 100.0));
    }

    private double _waveWavelengthBottom = 20.0;
    public double WaveWavelengthBottom
    {
        get => _waveWavelengthBottom;
        set => SetField(ref _waveWavelengthBottom, Math.Clamp(value, 1.0, 1000.0));
    }

    private double _waveWavelengthTop = 20.0;
    public double WaveWavelengthTop
    {
        get => _waveWavelengthTop;
        set => SetField(ref _waveWavelengthTop, Math.Clamp(value, 1.0, 1000.0));
    }

    private double _waveGradientCenter = 0.5;
    public double WaveGradientCenter
    {
        get => _waveGradientCenter;
        set => SetField(ref _waveGradientCenter, Math.Clamp(value, 0.001, 0.999));
    }

    public string[] WaveGradientCurveOptions { get; } = ["Linear", "Smooth", "Ease In", "Ease Out"];

    private string _waveGradientCurve = "Linear";
    public string WaveGradientCurve
    {
        get => _waveGradientCurve;
        set => SetField(ref _waveGradientCurve, value);
    }

    // -- Infill pattern -------------------------------------------------------

    public string[] InfillPatternOptions { get; } = ["None", "Rectilinear", "Grid", "Triangle", "Ghost Mesh Grid"];

    private string _infillPattern = "None";

    /// <summary>Selected infill pattern. "None" = emit shells as normal.</summary>
    public string InfillPattern
    {
        get => _infillPattern;
        set
        {
            if (SetField(ref _infillPattern, value))
                OnPropertyChanged(nameof(ShowInfillControls));
        }
    }

    public bool ShowInfillControls => InfillPattern != "None" && !SurfaceModeActive;

    private double _infillSpacingMm = 0.0;

    /// <summary>Centre-to-centre infill line spacing in mm. 0 = use bead width.</summary>
    public double InfillSpacingMm
    {
        get => _infillSpacingMm;
        set => SetField(ref _infillSpacingMm, Math.Clamp(value, 0.0, 500.0));
    }

    private double _infillAngleDeg = 0.0;

    /// <summary>Base angle of infill lines in degrees (0 = parallel to X axis).</summary>
    public double InfillAngleDeg
    {
        get => _infillAngleDeg;
        set => SetField(ref _infillAngleDeg, value % 360.0);
    }

    // -- Overhang orientation -------------------------------------------------

    private bool _overhangOrientation;

    /// <summary>When true, the planar slicer tilts the toolhead to follow mesh surface normals.</summary>
    public bool OverhangOrientation
    {
        get => _overhangOrientation;
        set
        {
            if (SetField(ref _overhangOrientation, value))
                OnPropertyChanged(nameof(ShowOverhangTilt));
        }
    }

    public bool ShowOverhangTilt => _overhangOrientation;

    private double _maxOverhangTiltDeg = 45.0;

    /// <summary>Maximum tool tilt from vertical in degrees (0 – 89).</summary>
    public double MaxOverhangTiltDeg
    {
        get => _maxOverhangTiltDeg;
        set => SetField(ref _maxOverhangTiltDeg, Math.Clamp(value, 0.0, 89.0));
    }

    // -- Surface follow (vertical ↔ path-normal blend) --------------------------

    private double _orientationFollowPercent = 100.0;

    /// <summary>
    /// How strongly the tool follows surface/stacking normals (0–100%).
    /// 0 = vertical (world +Z), 100 = full path/surface follow.
    /// </summary>
    public double OrientationFollowPercent
    {
        get => _orientationFollowPercent;
        set => SetField(ref _orientationFollowPercent, Math.Clamp(value, 0.0, 100.0));
    }

    public float OrientationFollowStrength => (float)(OrientationFollowPercent / 100.0);

    // -- Orientation smoothing ------------------------------------------------

    private bool _smoothRotation;

    public bool SmoothRotation
    {
        get => _smoothRotation;
        set
        {
            if (SetField(ref _smoothRotation, value))
                OnPropertyChanged(nameof(ShowSmoothRotationRadius));
        }
    }

    public bool ShowSmoothRotationRadius => _smoothRotation;

    private int _smoothRotationRadius = 5;

    /// <summary>Half-width of the smoothing window in moves (1 – 50).</summary>
    public int SmoothRotationRadius
    {
        get => _smoothRotationRadius;
        set => SetField(ref _smoothRotationRadius, Math.Clamp(value, 1, 50));
    }

    private double _smoothRotationMaxRateDegPerMm = 0.0;

    /// <summary>
    /// Maximum orientation change in degrees per mm of travel.
    /// Clamps the rate of toolhead rotation to prevent KUKA axis overspeed at sharp turns.
    /// 0 = disabled.
    /// </summary>
    public double SmoothRotationMaxRateDegPerMm
    {
        get => _smoothRotationMaxRateDegPerMm;
        set => SetField(ref _smoothRotationMaxRateDegPerMm, Math.Clamp(value, 0.0, 90.0));
    }

    private double _orientationLookAheadMm = 0.0;

    /// <summary>
    /// Forward look-ahead distance (mm) for the KRL exporter's Gaussian normal-smoothing kernel.
    /// At 60 mm/s print speed, 60 mm = 1 second of pre-rotation. 0 = disabled.
    /// </summary>
    public double OrientationLookAheadMm
    {
        get => _orientationLookAheadMm;
        set => SetField(ref _orientationLookAheadMm, Math.Clamp(value, 0.0, 500.0));
    }

    private double _orientationSigmaMm = 30.0;

    /// <summary>
    /// Gaussian sigma (mm) for the KRL exporter's normal-smoothing kernel.
    /// Controls the width of the orientation transition ramp. Typically half of OrientationLookAheadMm.
    /// </summary>
    public double OrientationSigmaMm
    {
        get => _orientationSigmaMm;
        set => SetField(ref _orientationSigmaMm, Math.Clamp(value, 1.0, 200.0));
    }

    // -- Toolhead approach orientation -----------------------------------------
    // These ABC angles (KUKA ZYX Euler, degrees) define the target tool orientation
    // used by the IK solver when scrubbing through a toolpath.  They are analogous
    // to the "toolhead ABC" setting in Eidos CAM: a fixed approach orientation applied
    // uniformly to every toolpath point.
    //
    // Defaults: A=0, B=0, C=0 -- identity (no additional rotation).
    // With these defaults the IK behaviour is identical to before this setting was added.
    // Increasing A rotates the tool around its own approach axis (e.g. spin the nozzle);
    // B/C tilt the tool relative to the plane-normal-derived approach direction.

    private double _toolheadA = 0.0;

    /// <summary>KUKA A angle (deg, rotation about Z) applied locally after the
    /// plane-normal-derived orientation. 0deg = no additional rotation.</summary>
    public double ToolheadA
    {
        get => _toolheadA;
        set => SetField(ref _toolheadA, Math.Clamp(value, -180.0, 180.0));
    }

    private double _toolheadB = 0.0;

    /// <summary>KUKA B angle (deg, rotation about Y') applied locally after the
    /// plane-normal-derived orientation. 0deg = no additional rotation.</summary>
    public double ToolheadB
    {
        get => _toolheadB;
        set => SetField(ref _toolheadB, Math.Clamp(value, -180.0, 180.0));
    }

    private double _toolheadC = 0.0;

    /// <summary>KUKA C angle (deg, rotation about X'') applied locally after the
    /// plane-normal-derived orientation. 0deg = no additional rotation.</summary>
    public double ToolheadC
    {
        get => _toolheadC;
        set => SetField(ref _toolheadC, Math.Clamp(value, -180.0, 180.0));
    }

    // -- Material temperatures -------------------------------------------------

    // T1/T2/T3 are set by the selected material preset (see ApplyPreset).
    // They are not shown in the ADDITIVE tab; the TOOLPATH tab's material dropdown drives them.
    // Defaults to 230deg C when no material is selected.

    private double _temperature1 = 230.0;
    public double Temperature1
    {
        get => _temperature1;
        set => SetField(ref _temperature1, Math.Clamp(value, 0.0, 450.0));
    }

    private double _temperature2 = 230.0;
    public double Temperature2
    {
        get => _temperature2;
        set => SetField(ref _temperature2, Math.Clamp(value, 0.0, 450.0));
    }

    private double _temperature3 = 230.0;
    public double Temperature3
    {
        get => _temperature3;
        set => SetField(ref _temperature3, Math.Clamp(value, 0.0, 450.0));
    }

    // -- Material presets ------------------------------------------------------

    /// <summary>User's saved material preset library. Loaded at startup, persisted on each add.</summary>
    public ObservableCollection<MaterialPreset> MaterialPresets { get; } = [];

    private int _selectedPresetIndex = -1;

    /// <summary>
    /// Index of the selected material preset, or -1 for none.
    /// Setting this applies the preset's temperatures to T1/T2/T3.
    /// </summary>
    public int SelectedPresetIndex
    {
        get => _selectedPresetIndex;
        set
        {
            if (!SetField(ref _selectedPresetIndex, value)) return;
            OnPropertyChanged(nameof(HasSelectedPreset));
            if (value >= 0 && value < MaterialPresets.Count)
                ApplyPreset(MaterialPresets[value]);
        }
    }

    public bool HasSelectedPreset => _selectedPresetIndex >= 0 && _selectedPresetIndex < MaterialPresets.Count;

    /// <summary>Active material preset, or <c>null</c> when none is selected.</summary>
    public MaterialPreset? SelectedPreset =>
        HasSelectedPreset ? MaterialPresets[_selectedPresetIndex] : null;

    private void ApplyPreset(MaterialPreset p)
    {
        Temperature1 = p.Temperature1;
        Temperature2 = p.Temperature2;
        Temperature3 = p.Temperature3;
        OnPropertyChanged(nameof(ExtrusionSpeedPercent));
        OnPropertyChanged(nameof(ExportTemperatureC));
    }

    // -- KRL export (Toolpath tab) ---------------------------------------------

    private string _temperatureOffset = "";

    /// <summary>±°C adjustment applied to all extruder zones at export. Empty = no change.</summary>
    public string TemperatureOffset
    {
        get => _temperatureOffset;
        set => SetField(ref _temperatureOffset, value);
    }

    /// <summary>Material preset temperature (°C) shown for all zones before offset.</summary>
    public double ExportTemperatureC => Temperature1;

    /// <summary>Zone temperature (°C) written to KRL after applying <see cref="TemperatureOffset"/>.</summary>
    public float GetEffectiveExportTemperature()
    {
        float temp = (float)Temperature1 + (float)ParseSignedOffset(_temperatureOffset);
        return Math.Clamp(temp, 0f, 450f);
    }

    private string _extrusionSpeedOffset = "";

    /// <summary>±% adjustment applied to computed extrusion speed at export. Empty = no change.</summary>
    public string ExtrusionSpeedOffset
    {
        get => _extrusionSpeedOffset;
        set => SetField(ref _extrusionSpeedOffset, value);
    }

    /// <summary>Computed extrusion motor speed (%) from bead geometry and material flow.</summary>
    public double ExtrusionSpeedPercent => ComputeExtrusionSpeedPercent();

    private double ComputeExtrusionSpeedPercent()
    {
        float flow = (float)(SelectedPreset?.FlowRate ?? 0.463);
        return KrlAnout.ComputeRpmPercent(
            (float)BeadWidth, (float)LayerHeight, (float)(PrintSpeed / 1000.0), flow);
    }

    /// <summary>Extrusion motor speed (%) written to KRL after applying <see cref="ExtrusionSpeedOffset"/>.</summary>
    public float GetEffectiveExtrusionSpeedPercent()
    {
        float pct = (float)ComputeExtrusionSpeedPercent() + (float)ParseSignedOffset(_extrusionSpeedOffset);
        return Math.Max(0f, pct);
    }

    private double _extrusionStartWaitSec = 1.0;

    /// <summary>Pause (seconds) after first RPM-on before the first extrusion move.</summary>
    public double ExtrusionStartWaitSec
    {
        get => _extrusionStartWaitSec;
        set => SetField(ref _extrusionStartWaitSec, Math.Clamp(value, 0.0, 60.0));
    }

    private double _extrusionResumeWaitSec;

    /// <summary>Pause (seconds) after each travel before the next extrusion move.</summary>
    public double ExtrusionResumeWaitSec
    {
        get => _extrusionResumeWaitSec;
        set => SetField(ref _extrusionResumeWaitSec, Math.Clamp(value, 0.0, 60.0));
    }

    // -- Movement (z-hop, wipe) ------------------------------------------------

    private double _zHopMm;

    /// <summary>Vertical lift on travel moves in mm. 0 = disabled.</summary>
    public double ZHopMm
    {
        get => _zHopMm;
        set => SetField(ref _zHopMm, Math.Max(0.0, value));
    }

    public string[] WipeModeOptions { get; } = ["Off", "Retrace", "Same-Direction"];

    private string _wipeModeDisplay = "Off";

    /// <summary>Wipe path before travel: Off, Retrace (back), or Same-Direction (forward past the point).</summary>
    public string WipeModeDisplay
    {
        get => _wipeModeDisplay;
        set => SetField(ref _wipeModeDisplay, value);
    }

    private double _wipeLengthMm = 10.0;

    /// <summary>Total wipe distance in mm.</summary>
    public double WipeLengthMm
    {
        get => _wipeLengthMm;
        set => SetField(ref _wipeLengthMm, Math.Max(0.0, value));
    }

    private double _wipeRampMm = 5.0;

    /// <summary>
    /// Wipe ramp (mm). Positive = last N mm of wipe length ramps RPM down.
    /// Negative = extra |N| mm past wipe length with ramp-down squeeze.
    /// </summary>
    public double WipeRampMm
    {
        get => _wipeRampMm;
        set => SetField(ref _wipeRampMm, Math.Clamp(value, -500.0, 500.0));
    }

    private double _wipeSpeed = 120.0;

    /// <summary>Linear speed for wipe moves in mm/s (independent of travel speed).</summary>
    public double WipeSpeed
    {
        get => _wipeSpeed;
        set => SetField(ref _wipeSpeed, Math.Clamp(value, 1.0, 2000.0));
    }

    private bool _resumeRampEnabled;

    /// <summary>Stepped speed/RPM ramp after each travel before full extrusion resumes.</summary>
    public bool ResumeRampEnabled
    {
        get => _resumeRampEnabled;
        set => SetField(ref _resumeRampEnabled, value);
    }

    private double _resumeRampStartSpeed = 0.5;

    /// <summary>Print speed at the start of the post-travel ramp (mm/s).</summary>
    public double ResumeRampStartSpeed
    {
        get => _resumeRampStartSpeed;
        set => SetField(ref _resumeRampStartSpeed, Math.Clamp(value, 0.01, 2000.0));
    }

    private double _resumeRampStartRpmPercent = 1.0;

    /// <summary>Extruder motor speed at ramp start (%).</summary>
    public double ResumeRampStartRpmPercent
    {
        get => _resumeRampStartRpmPercent;
        set => SetField(ref _resumeRampStartRpmPercent, Math.Clamp(value, 0.0, 100.0));
    }

    private double _resumeRampDistanceMm = 609.6;

    /// <summary>Total ramp distance along the path (mm). Default 609.6 ≈ 2 ft.</summary>
    public double ResumeRampDistanceMm
    {
        get => _resumeRampDistanceMm;
        set => SetField(ref _resumeRampDistanceMm, Math.Clamp(value, 1.0, 10000.0));
    }

    private int _resumeRampSteps = 10;

    /// <summary>Number of discrete speed/RPM steps over the ramp distance.</summary>
    public int ResumeRampSteps
    {
        get => _resumeRampSteps;
        set => SetField(ref _resumeRampSteps, Math.Clamp(value, 1, 50));
    }

    private static double ParseSignedOffset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('+'))
            trimmed = trimmed[1..];

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;
    }

    // -- Home positions --------------------------------------------------------

    private List<(string Name, float[] Angles)> _homePositions =
        [("Default", [0f, -90f, 90f, 0f, 15f, 0f])];

    private string[] _availableHomePositionNames = ["Default"];

    /// <summary>Names of the home positions available for the active cell.</summary>
    public string[] AvailableHomePositionNames
    {
        get => _availableHomePositionNames;
        private set => SetField(ref _availableHomePositionNames, value);
    }

    private int _selectedHomePositionIndex = 0;
    private string _selectedHomePositionName = "Default";

    /// <summary>
    /// Name of the selected home position. Bound as SelectedItem (string) to avoid the
    /// Avalonia ComboBox SelectedIndex reset that occurs when ItemsSource changes.
    /// </summary>
    public string SelectedHomePositionName
    {
        get => _selectedHomePositionName;
        set
        {
            if (value is null) return;
            if (!SetField(ref _selectedHomePositionName, value)) return;
            _selectedHomePositionIndex = Math.Max(0, _homePositions.FindIndex(p => p.Name == value));
        }
    }

    /// <summary>Joint angles (A1-A6, KRL degrees) for the currently selected home position.</summary>
    public float[] SelectedHomeAngles
        => _selectedHomePositionIndex < _homePositions.Count
            ? _homePositions[_selectedHomePositionIndex].Angles
            : [0f, -90f, 90f, 0f, 15f, 0f];

    /// <summary>
    /// Adds or replaces a named home position in the active list.
    /// If a position with the same name already exists it is updated in place; otherwise it is appended.
    /// </summary>
    public void AddHomePosition(string name, float[] angles)
    {
        var idx = _homePositions.FindIndex(p => p.Name == name);
        if (idx >= 0)
            _homePositions[idx] = (name, angles);
        else
            _homePositions.Add((name, angles));
        AvailableHomePositionNames = _homePositions.Select(p => p.Name).ToArray();
    }

    /// <summary>Wired by ViewportView.axaml.cs; invoked when "Set as Default" is clicked.</summary>
    internal Action? OnSetDefaultHomePositionRequested { get; set; }

    /// <summary>Saves the currently selected home position as the default for this cell.</summary>
    public RelayCommand SetDefaultHomePositionCommand { get; }

    /// <summary>
    /// Refreshes the available home position list from the given cell config and restores
    /// <paramref name="defaultPositionName"/> as the selected entry (falls back to index 0).
    /// <paramref name="userPositions"/> are appended after the cell's built-in positions.
    /// </summary>
    public void UpdateFromCell(CellConfig cell, string? defaultPositionName,
                               IReadOnlyList<HomePositionConfig>? userPositions = null)
    {
        if (userPositions is { Count: > 0 })
        {
            _homePositions = userPositions.Select(p => (p.Name, p.Angles)).ToList();
        }
        else
        {
            var positions = cell.Robot.HomePositions;
            _homePositions = positions.Count > 0
                ? positions.Select(p => (p.Name, p.Angles)).ToList()
                : [("Default", cell.Robot.HomePosition)];
        }

        AvailableHomePositionNames = _homePositions.Select(p => p.Name).ToArray();

        string nameToSelect = _homePositions.Count > 0 ? _homePositions[0].Name : "Default";
        if (defaultPositionName is not null)
        {
            int found = _homePositions.FindIndex(p => p.Name == defaultPositionName);
            if (found >= 0) nameToSelect = _homePositions[found].Name;
        }
        SelectedHomePositionName = nameToSelect;

        // Apply cell-specific toolhead orientation defaults.
        ToolheadA = cell.Robot.DefaultToolheadA;
        ToolheadB = cell.Robot.DefaultToolheadB;
        ToolheadC = cell.Robot.DefaultToolheadC;
    }
}
