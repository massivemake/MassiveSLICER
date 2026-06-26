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
    // KRL export: $ANOUT[1..3] = ((−150 + T) × 0.032 + 0.032) / 10
    public double Temperature1 { get; set; } = 220.0;
    public double Temperature2 { get; set; } = 220.0;
    public double Temperature3 { get; set; } = 220.0;

    // -- Extrusion properties ----------------------------------------------
    /// <summary>
    /// Material flow rate in rev/cm³ — motor revolutions per cubic centimetre deposited.
    /// KRL export: <c>rpm% = W × H × v × FlowRate × 60</c>, then <c>$ANOUT[4] = rpm% ÷ 10</c>.
    /// Calibrated at W=6, H=3, v=100 mm/s, 50% RPM → <b>0.463</b> (→ <c>5.0</c> ANOUT).
    /// </summary>
    public double FlowRate { get; set; } = 0.463;

    /// <summary>Material density in g/cm³.</summary>
    public double MaterialDensity { get; set; } = 1.05;

    /// <summary>Material cost in USD per pound.</summary>
    public double CostPerLb { get; set; } = 5.0;
}
