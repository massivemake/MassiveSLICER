namespace MassiveSlicer.Core.Models;

/// <summary>
/// A named material profile used by the additive slicer.
/// Selecting a preset copies its temperature and property values into the active settings.
/// </summary>
public sealed class MaterialPreset
{
    public string Name { get; set; } = "New Preset";
    public string MaterialType { get; set; } = "ABS";
    public string Color { get; set; } = "Black";

    // -- Temperatures (deg C) -------------------------------------------------
    public double Temperature1 { get; set; } = 220.0;
    public double Temperature2 { get; set; } = 220.0;
    public double Temperature3 { get; set; } = 220.0;

    // -- Extrusion properties ----------------------------------------------
    /// <summary>
    /// Material flow rate in rev/cm³ -- extruder motor revolutions per cubic centimetre deposited.
    /// Combined with bead cross-section and feed rate to determine the $ANOUT[4] RPM voltage.
    /// </summary>
    public double FlowRate { get; set; } = 1.0;

    /// <summary>Material density in g/cm³.</summary>
    public double MaterialDensity { get; set; } = 1.05;

    /// <summary>Material cost in USD per pound.</summary>
    public double CostPerLb { get; set; } = 5.0;
}
