using System.Collections.ObjectModel;
using MassiveSlicer.Commands;
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
    public AdditiveSettingsViewModel()
    {
        SetDefaultHomePositionCommand = new RelayCommand(() => OnSetDefaultHomePositionRequested?.Invoke());
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
            }
        }
    }

    public string[] AvailableMethodNames { get; } = ["Planar", "Angled", "Geodesic (Experimental)"];

    public string MethodDisplayName
    {
        get => Method switch
        {
            SliceMethod.Angled   => "Angled",
            SliceMethod.Geodesic => "Geodesic (Experimental)",
            _                    => "Planar",
        };
        set => Method = value switch
        {
            "Angled"                  => SliceMethod.Angled,
            "Geodesic (Experimental)" => SliceMethod.Geodesic,
            "Geodesic"                => SliceMethod.Geodesic,
            _                         => SliceMethod.Planar,
        };
    }

    public bool ShowTiltAngle           => Method == SliceMethod.Angled;
    public bool ShowContourOffsetOption => Method != SliceMethod.Geodesic;

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

    private double _apoCvel = 50.0;

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

    private void ApplyPreset(MaterialPreset p)
    {
        Temperature1 = p.Temperature1;
        Temperature2 = p.Temperature2;
        Temperature3 = p.Temperature3;
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
