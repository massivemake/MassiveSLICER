using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>ViewModel for the Add / Edit Material Preset dialog.</summary>
public sealed class MaterialPresetEditorViewModel : ViewModelBase
{
    public static readonly string[] MaterialTypes =
        ["ABS", "ASA", "PETG", "PLA", "Nylon", "PC", "TPU", "PEI", "Other"];

    public static readonly string[] Colors =
        ["Black", "Gray", "White", "Clear", "Red", "Blue", "Green", "Yellow", "Orange", "Natural", "Other"];

    // -- Identification ----------------------------------------------------

    private string _expectedAutoName = "ABS - Black";

    private string _name = "ABS - Black";
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    private string _materialType = "ABS";
    public string MaterialType
    {
        get => _materialType;
        set
        {
            if (!SetField(ref _materialType, value)) return;
            TryAutoUpdateName();
            OnPropertyChanged(nameof(GlassTransitionHint));
            OnPropertyChanged(nameof(ThermalThresholdsHint));
        }
    }

    private string _color = "Black";
    public string Color
    {
        get => _color;
        set
        {
            if (!SetField(ref _color, value)) return;
            TryAutoUpdateName();
        }
    }

    // -- Temperatures ------------------------------------------------------

    private double _temperature1 = 220.0;
    public double Temperature1
    {
        get => _temperature1;
        set => SetField(ref _temperature1, Math.Clamp(value, 0, 450));
    }

    private double _temperature2 = 220.0;
    public double Temperature2
    {
        get => _temperature2;
        set => SetField(ref _temperature2, Math.Clamp(value, 0, 450));
    }

    private double _temperature3 = 220.0;
    public double Temperature3
    {
        get => _temperature3;
        set => SetField(ref _temperature3, Math.Clamp(value, 0, 450));
    }

    // -- Material properties -----------------------------------------------

    private double _flowRate = 1.0;
    public double FlowRate
    {
        get => _flowRate;
        set => SetField(ref _flowRate, Math.Max(0, value));
    }

    private double _flowRateHf;
    /// <summary>HF-extruder flow (rev/cm³). 0 = use the HV <see cref="FlowRate"/>.</summary>
    public double FlowRateHf
    {
        get => _flowRateHf;
        set => SetField(ref _flowRateHf, Math.Max(0, value));
    }

    private double _materialDensity = 1.05;
    public double MaterialDensity
    {
        get => _materialDensity;
        set { if (SetField(ref _materialDensity, Math.Max(0, value))) OnPropertyChanged(nameof(CalibComputedText)); }
    }

    private double _costPerLb = 5.0;
    public double CostPerLb
    {
        get => _costPerLb;
        set => SetField(ref _costPerLb, Math.Max(0, value));
    }

    // -- Thermomechanical simulation thresholds ------------------------------

    private double _glassTransitionC;
    /// <summary>Tg (°C). 0 = auto from material type family.</summary>
    public double GlassTransitionC
    {
        get => _glassTransitionC;
        set
        {
            if (SetField(ref _glassTransitionC, Math.Clamp(value, 0, 400)))
                OnPropertyChanged(nameof(GlassTransitionHint));
        }
    }

    private double _thermalBondMarginC = 10;
    public double ThermalBondMarginC
    {
        get => _thermalBondMarginC;
        set
        {
            if (SetField(ref _thermalBondMarginC, Math.Clamp(value, 1, 80)))
                OnPropertyChanged(nameof(ThermalThresholdsHint));
        }
    }

    private double _thermalSagMarginC = 45;
    public double ThermalSagMarginC
    {
        get => _thermalSagMarginC;
        set
        {
            if (SetField(ref _thermalSagMarginC, Math.Clamp(value, 5, 120)))
                OnPropertyChanged(nameof(ThermalThresholdsHint));
        }
    }

    private double _thermalAmbientC = 30;
    public double ThermalAmbientC
    {
        get => _thermalAmbientC;
        set => SetField(ref _thermalAmbientC, Math.Clamp(value, -20, 120));
    }

    /// <summary>Live readout of the family Tg when GlassTransitionC is 0 (auto).</summary>
    public string GlassTransitionHint
    {
        get
        {
            float autoTg = MassiveSlicer.Core.Slicing.Effects.ThermalSimulator.GlassTransitionC(MaterialType);
            if (_glassTransitionC > 0)
                return $"Using override Tg = {_glassTransitionC:0.#} °C (auto for {MaterialType} would be {autoTg:0} °C).";
            return $"Auto Tg for {MaterialType} = {autoTg:0} °C (set a value to override).";
        }
    }

    public string ThermalThresholdsHint
    {
        get
        {
            float tg = _glassTransitionC > 0
                ? (float)_glassTransitionC
                : MassiveSlicer.Core.Slicing.Effects.ThermalSimulator.GlassTransitionC(MaterialType);
            float bond = tg + (float)_thermalBondMarginC;
            float sag  = tg + (float)_thermalSagMarginC;
            return $"Safe window: bond ≥ {bond:0} °C (Tg+{_thermalBondMarginC:0}), sag ≤ {sag:0} °C (Tg+{_thermalSagMarginC:0}).";
        }
    }

    // -- Purge-and-weigh calibration -----------------------------------------
    //
    // Run the extruder into the air at a fixed screw RPM for a fixed time, weigh
    // the cooled purge.  The operator enters true RPM (read off the drive); the
    // machine's $ANOUT[4] channel is a percentage, so RPM converts via the drive
    // scale (RPM at 100 % output):
    //
    //   motor% = rpm ÷ maxRpm × 100
    //   flowRate (rev/cm³-equivalent) = motor% × (seconds/60) ÷ (grams ÷ density)

    private string _calibratedOn = "";
    public string CalibratedOn
    {
        get => _calibratedOn;
        set { if (SetField(ref _calibratedOn, value)) OnPropertyChanged(nameof(CalibrationStatus)); }
    }

    private string _calibrationNote = "";
    public string CalibrationNote
    {
        get => _calibrationNote;
        set { if (SetField(ref _calibrationNote, value)) OnPropertyChanged(nameof(CalibrationStatus)); }
    }

    public string CalibrationStatus =>
        string.IsNullOrWhiteSpace(_calibratedOn)
            ? "Not calibrated — flow rate is the default/manual value."
            : $"Calibrated {_calibratedOn}   ({_calibrationNote})";

    private double _calibMotorRpm = 50.0;

    /// <summary>Screw speed during the purge test, in true RPM (read off the drive).</summary>
    public double CalibMotorRpm
    {
        get => _calibMotorRpm;
        set { if (SetField(ref _calibMotorRpm, Math.Max(0.1, value))) OnPropertyChanged(nameof(CalibComputedText)); }
    }

    private double _calibMaxRpm = 100.0;

    /// <summary>Drive scale: screw RPM at 100 % ($ANOUT[4] = 1.0). Machine property.</summary>
    public double CalibMaxRpm
    {
        get => _calibMaxRpm;
        set { if (SetField(ref _calibMaxRpm, Math.Max(1, value))) OnPropertyChanged(nameof(CalibComputedText)); }
    }

    /// <summary>Motor % equivalent of <see cref="CalibMotorRpm"/> — the $ANOUT[4] signal value × 100.</summary>
    public double CalibMotorPercent => _calibMotorRpm / _calibMaxRpm * 100.0;

    private double _calibTimeSec = 60.0;
    public double CalibTimeSec
    {
        get => _calibTimeSec;
        set { if (SetField(ref _calibTimeSec, Math.Max(1, value))) OnPropertyChanged(nameof(CalibComputedText)); }
    }

    private double _calibWeightG;
    public double CalibWeightG
    {
        get => _calibWeightG;
        set { if (SetField(ref _calibWeightG, Math.Max(0, value))) OnPropertyChanged(nameof(CalibComputedText)); }
    }

    /// <summary>Flow rate computed from the calibration inputs, or null when incomplete.</summary>
    public double? CalibComputedFlowRate
    {
        get
        {
            if (_calibWeightG <= 0 || _materialDensity <= 0 || _calibMaxRpm <= 0) return null;
            double motorPercent = _calibMotorRpm / _calibMaxRpm * 100.0;
            double volumeCm3    = _calibWeightG / _materialDensity;
            return motorPercent * (_calibTimeSec / 60.0) / volumeCm3;
        }
    }

    public string CalibComputedText => CalibComputedFlowRate is double f
        ? $"Computed flow rate: {f:F4} rev/cm³"
        : "Enter purge weight to compute.";

    /// <summary>Applies the computed calibration to the preset's flow rate.</summary>
    public void ApplyCalibration()
    {
        if (CalibComputedFlowRate is not double f) return;
        FlowRate        = f;
        CalibratedOn    = DateTime.Now.ToString("yyyy-MM-dd");
        CalibrationNote = $"{_calibMotorRpm:0.#} RPM × {_calibTimeSec:0}s → {_calibWeightG:0.#}g" +
                          $" (max {_calibMaxRpm:0} RPM) @ {Temperature1:0}/{Temperature2:0}/{Temperature3:0}°C";
    }

    // -- Auto-name logic ---------------------------------------------------

    /// <summary>
    /// Updates the Name field to match the new type/color combination, but only
    /// if the name still equals what was previously auto-generated (meaning the
    /// user has not manually typed a custom name).
    /// </summary>
    private void TryAutoUpdateName()
    {
        string newAuto = $"{_materialType} - {_color}";
        if (_name == _expectedAutoName)
            Name = newAuto;
        _expectedAutoName = newAuto;
    }

    // -- Serialisation -----------------------------------------------------

    public MaterialPreset ToPreset() => new()
    {
        Name               = Name.Trim().Length > 0 ? Name.Trim() : $"{MaterialType} - {Color}",
        MaterialType       = MaterialType,
        Color              = Color,
        Temperature1       = Temperature1,
        Temperature2       = Temperature2,
        Temperature3       = Temperature3,
        FlowRate           = FlowRate,
        FlowRateHf         = FlowRateHf,
        MaterialDensity    = MaterialDensity,
        CostPerLb          = CostPerLb,
        GlassTransitionC   = GlassTransitionC,
        ThermalBondMarginC = ThermalBondMarginC,
        ThermalSagMarginC  = ThermalSagMarginC,
        ThermalAmbientC    = ThermalAmbientC,
        CalibratedOn       = CalibratedOn,
        CalibrationNote    = CalibrationNote,
    };

    public void LoadFrom(MaterialPreset p)
    {
        _expectedAutoName = p.Name;
        Name               = p.Name;
        MaterialType       = p.MaterialType;
        Color              = p.Color;
        Temperature1       = p.Temperature1;
        Temperature2       = p.Temperature2;
        Temperature3       = p.Temperature3;
        FlowRate           = p.FlowRate;
        FlowRateHf         = p.FlowRateHf;
        MaterialDensity    = p.MaterialDensity;
        CostPerLb          = p.CostPerLb;
        GlassTransitionC   = p.GlassTransitionC;
        ThermalBondMarginC = p.ThermalBondMarginC > 0 ? p.ThermalBondMarginC : 10;
        ThermalSagMarginC  = p.ThermalSagMarginC > 0 ? p.ThermalSagMarginC : 45;
        // Model default is 30; ambient can be 0 °C if the user set it.
        ThermalAmbientC    = p.ThermalAmbientC;
        CalibratedOn       = p.CalibratedOn;
        CalibrationNote    = p.CalibrationNote;
        OnPropertyChanged(nameof(GlassTransitionHint));
        OnPropertyChanged(nameof(ThermalThresholdsHint));
    }
}
