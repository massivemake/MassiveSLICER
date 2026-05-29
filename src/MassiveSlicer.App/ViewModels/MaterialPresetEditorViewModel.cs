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

    private double _materialDensity = 1.05;
    public double MaterialDensity
    {
        get => _materialDensity;
        set => SetField(ref _materialDensity, Math.Max(0, value));
    }

    private double _costPerLb = 5.0;
    public double CostPerLb
    {
        get => _costPerLb;
        set => SetField(ref _costPerLb, Math.Max(0, value));
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
        Name            = Name.Trim().Length > 0 ? Name.Trim() : $"{MaterialType} - {Color}",
        MaterialType    = MaterialType,
        Color           = Color,
        Temperature1    = Temperature1,
        Temperature2    = Temperature2,
        Temperature3    = Temperature3,
        FlowRate        = FlowRate,
        MaterialDensity = MaterialDensity,
        CostPerLb       = CostPerLb,
    };

    public void LoadFrom(MaterialPreset p)
    {
        _expectedAutoName = p.Name;
        Name            = p.Name;
        MaterialType    = p.MaterialType;
        Color           = p.Color;
        Temperature1    = p.Temperature1;
        Temperature2    = p.Temperature2;
        Temperature3    = p.Temperature3;
        FlowRate        = p.FlowRate;
        MaterialDensity = p.MaterialDensity;
        CostPerLb       = p.CostPerLb;
    }
}
