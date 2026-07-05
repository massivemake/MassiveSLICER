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
    /// Flow rate in rev/cm³ for the <b>HV</b> extruder — motor revolutions per cubic centimetre
    /// deposited. KRL export: <c>rpm% = W × H × v × FlowRate × 60</c>.
    /// Calibrated at W=6, H=3, v=100 mm/s, 50% RPM → <b>0.463</b>.
    /// </summary>
    public double FlowRate { get; set; } = 0.463;

    /// <summary>
    /// Flow rate in rev/cm³ for the <b>HF</b> extruder. The HF and HV extruders deposit a
    /// slightly different volume per revolution, so each carries its own calibration.
    /// <c>0</c> (unset) falls back to <see cref="FlowRate"/>. Chosen automatically from the
    /// active cell's extruder.
    /// </summary>
    public double FlowRateHf { get; set; } = 0.0;

    /// <summary>Flow rate for the active extruder: the HF value when <paramref name="isHf"/> and
    /// set, otherwise the HV <see cref="FlowRate"/>.</summary>
    public double FlowRateFor(bool isHf) => isHf && FlowRateHf > 0.0 ? FlowRateHf : FlowRate;

    /// <summary>Material density in g/cm³.</summary>
    public double MaterialDensity { get; set; } = 1.05;

    /// <summary>Material cost in USD per pound.</summary>
    public double CostPerLb { get; set; } = 5.0;
}
