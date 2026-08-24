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

    /// <summary>True once <see cref="LoadFrom"/> has loaded an existing library entry —
    /// distinguishes "editing preset N" from "building a brand-new preset" so the dialog
    /// can hide Delete when there's nothing in the library to delete yet.</summary>
    public bool IsExistingPreset { get; private set; }

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

    // Every calibration value below is stored PER HEAD. The dialog shows whichever head
    // CalibIsHf selects, so toggling reveals that head's own test conditions — an
    // uncalibrated head reads 0 g instead of inheriting the other head's numbers.
    private string _calibratedOnHv = "",  _calibratedOnHf = "";
    private string _calibNoteHv    = "",  _calibNoteHf    = "";
    private double _calibPctHv     = 50.0, _calibPctHf    = 50.0;
    private double _calibSecHv     = 60.0, _calibSecHf    = 60.0;
    private double _calibGHv       = 0.0,  _calibGHf      = 0.0;

    public string CalibratedOn
    {
        get => _calibIsHf ? _calibratedOnHf : _calibratedOnHv;
        set
        {
            if (_calibIsHf) { if (_calibratedOnHf == value) return; _calibratedOnHf = value; }
            else            { if (_calibratedOnHv == value) return; _calibratedOnHv = value; }
            OnPropertyChanged(); OnPropertyChanged(nameof(CalibrationStatus));
        }
    }

    public string CalibrationNote
    {
        get => _calibIsHf ? _calibNoteHf : _calibNoteHv;
        set
        {
            if (_calibIsHf) { if (_calibNoteHf == value) return; _calibNoteHf = value; }
            else            { if (_calibNoteHv == value) return; _calibNoteHv = value; }
            OnPropertyChanged(); OnPropertyChanged(nameof(CalibrationStatus));
        }
    }

    public string CalibrationStatus =>
        string.IsNullOrWhiteSpace(CalibratedOn)
            ? $"{(_calibIsHf ? "HF" : "HV")} not calibrated — flow rate is the default/manual value."
            : $"{(_calibIsHf ? "HF" : "HV")} calibrated {CalibratedOn}   ({CalibrationNote})";

    /// <summary>
    /// Screw speed used for the purge test, as a percentage of drive maximum — the same
    /// number the slicer exports. Stored separately for HV and HF.
    /// </summary>
    public double CalibMotorPercent
    {
        get => _calibIsHf ? _calibPctHf : _calibPctHv;
        set
        {
            double v = Math.Clamp(value, 0.1, 100.0);
            if (_calibIsHf) { if (_calibPctHf == v) return; _calibPctHf = v; }
            else            { if (_calibPctHv == v) return; _calibPctHv = v; }
            OnPropertyChanged(); OnPropertyChanged(nameof(CalibComputedText));
        }
    }

    /// <summary>Purge duration (s) for the selected head.</summary>
    public double CalibTimeSec
    {
        get => _calibIsHf ? _calibSecHf : _calibSecHv;
        set
        {
            double v = Math.Max(1, value);
            if (_calibIsHf) { if (_calibSecHf == v) return; _calibSecHf = v; }
            else            { if (_calibSecHv == v) return; _calibSecHv = v; }
            OnPropertyChanged(); OnPropertyChanged(nameof(CalibComputedText));
        }
    }

    /// <summary>Purge weight (g) for the selected head. 0 = this head not yet purged.</summary>
    public double CalibWeightG
    {
        get => _calibIsHf ? _calibGHf : _calibGHv;
        set
        {
            double v = Math.Max(0, value);
            if (_calibIsHf) { if (_calibGHf == v) return; _calibGHf = v; }
            else            { if (_calibGHv == v) return; _calibGHv = v; }
            OnPropertyChanged(); OnPropertyChanged(nameof(CalibComputedText));
        }
    }

    /// <summary>Flow rate computed from the calibration inputs, or null when incomplete.</summary>
    public double? CalibComputedFlowRate
    {
        get
        {
            if (CalibWeightG <= 0 || _materialDensity <= 0 || CalibMotorPercent <= 0) return null;
            double volumeCm3 = CalibWeightG / _materialDensity;
            return CalibMotorPercent * (CalibTimeSec / 60.0) / volumeCm3;
        }
    }

    public string CalibComputedText => CalibComputedFlowRate is double f
        ? $"Computed flow rate: {f:F4} rev/cm³"
        : "Enter purge weight to compute.";

    /// <summary>Applies the computed calibration to the preset's flow rate.</summary>
    private bool _calibIsHf;

    /// <summary>
    /// Which head the purge test was run on. The two heads have different screws and so
    /// different flow rates; calibration used to always write the HV field, silently leaving
    /// HF uncalibrated no matter which head was actually purged.
    /// Defaults to the active cell's extruder when the dialog opens.
    /// </summary>
    public bool CalibIsHf
    {
        get => _calibIsHf;
        set
        {
            if (!SetField(ref _calibIsHf, value)) return;
            OnPropertyChanged(nameof(CalibIsHv));
            OnPropertyChanged(nameof(CalibTargetLabel));
            // Every calibration field is per head — re-read them all for the new selection.
            OnPropertyChanged(nameof(CalibMotorPercent));
            OnPropertyChanged(nameof(CalibTimeSec));
            OnPropertyChanged(nameof(CalibWeightG));
            OnPropertyChanged(nameof(CalibratedOn));
            OnPropertyChanged(nameof(CalibrationNote));
            OnPropertyChanged(nameof(CalibrationStatus));
            OnPropertyChanged(nameof(CalibComputedText));
        }
    }

    /// <summary>Inverse of <see cref="CalibIsHf"/>, for a two-radio selector.</summary>
    public bool CalibIsHv
    {
        get => !_calibIsHf;
        set { if (value) CalibIsHf = false; }
    }

    /// <summary>Which flow field Apply will write, shown next to the button.</summary>
    public string CalibTargetLabel => _calibIsHf ? "Applies to HF flow rate" : "Applies to HV flow rate";

    public void ApplyCalibration()
    {
        if (CalibComputedFlowRate is not double f) return;
        if (_calibIsHf) FlowRateHf = f; else FlowRate = f;
        CalibratedOn    = DateTime.Now.ToString("yyyy-MM-dd");
        CalibrationNote = $"{CalibMotorPercent:0.#}% × {CalibTimeSec:0}s → {CalibWeightG:0.#}g" +
                          $" @ {Temperature1:0}/{Temperature2:0}/{Temperature3:0}°C";
    }

    // -- Auto-name logic ---------------------------------------------------

    /// <summary>Skip type/color → name rewriting while <see cref="LoadFrom"/> applies fields.</summary>
    private bool _suppressAutoName;

    /// <summary>
    /// Updates the Name field to match the new type/color combination, but only
    /// if the name still equals what was previously auto-generated (meaning the
    /// user has not manually typed a custom name).
    /// </summary>
    private void TryAutoUpdateName()
    {
        if (_suppressAutoName) return;
        string newAuto = $"{_materialType} - {_color}";
        if (_name == _expectedAutoName)
            Name = newAuto;
        _expectedAutoName = newAuto;
    }

    // -- Import / export status (dialog chrome) ----------------------------

    private string _importExportStatus = "";
    /// <summary>One-line feedback after Import/Export JSON in the dialog.</summary>
    public string ImportExportStatus
    {
        get => _importExportStatus;
        set => SetField(ref _importExportStatus, value);
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
        CalibratedOn       = _calibratedOnHv,
        CalibrationNote    = _calibNoteHv,
        CalibratedOnHf      = _calibratedOnHf,
        CalibrationNoteHf   = _calibNoteHf,
        CalibMotorPercent   = _calibPctHv,
        CalibTimeSec        = _calibSecHv,
        CalibWeightG        = _calibGHv,
        CalibMotorPercentHf = _calibPctHf,
        CalibTimeSecHf      = _calibSecHf,
        CalibWeightGHf      = _calibGHf,
        CalibIsHf           = CalibIsHf,
    };

    public void LoadFrom(MaterialPreset p)
    {
        IsExistingPreset = true;

        // Setting MaterialType/Color fires TryAutoUpdateName. Without suppress, a custom
        // name like "PPGF" was treated as the prior auto-name and rewritten to "Other - Natural".
        _suppressAutoName = true;
        try
        {
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

            // Both heads into their own stores (private fields, so no auto-name side effects),
            // then pick which one the dialog shows.
            _calibratedOnHv = p.CalibratedOn;
            _calibNoteHv    = p.CalibrationNote;
            _calibPctHv     = p.CalibMotorPercent > 0 ? p.CalibMotorPercent : 50.0;
            _calibSecHv     = p.CalibTimeSec      > 0 ? p.CalibTimeSec      : 60.0;
            _calibGHv       = p.CalibWeightG;
            _calibratedOnHf = p.CalibratedOnHf;
            _calibNoteHf    = p.CalibrationNoteHf;
            _calibPctHf     = p.CalibMotorPercentHf > 0 ? p.CalibMotorPercentHf : 50.0;
            _calibSecHf     = p.CalibTimeSecHf      > 0 ? p.CalibTimeSecHf      : 60.0;
            _calibGHf       = p.CalibWeightGHf;
            _calibIsHf      = p.CalibIsHf;
        }
        finally
        {
            _suppressAutoName = false;
        }

        // Auto-rename baseline = type/color pattern. Custom names (e.g. "PPGF") stay put because
        // _name != _expectedAutoName; auto-style names ("ASA - Black") still track type/color.
        _expectedAutoName = $"{_materialType} - {_color}";

        // Backing stores were written directly — refresh every calibration view.
        OnPropertyChanged(nameof(CalibIsHf));
        OnPropertyChanged(nameof(CalibIsHv));
        OnPropertyChanged(nameof(CalibTargetLabel));
        OnPropertyChanged(nameof(CalibMotorPercent));
        OnPropertyChanged(nameof(CalibTimeSec));
        OnPropertyChanged(nameof(CalibWeightG));
        OnPropertyChanged(nameof(CalibratedOn));
        OnPropertyChanged(nameof(CalibrationNote));
        OnPropertyChanged(nameof(CalibrationStatus));
        OnPropertyChanged(nameof(CalibComputedText));

        OnPropertyChanged(nameof(GlassTransitionHint));
        OnPropertyChanged(nameof(ThermalThresholdsHint));
    }
}
