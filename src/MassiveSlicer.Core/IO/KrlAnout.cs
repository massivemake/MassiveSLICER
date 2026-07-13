using System.Globalization;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// <c>$ANOUT</c> values for LFAM KRL export.
/// Scales match verified on-cell literals (e.g. 220 °C → <c>0.2272</c>, 1 % idle RPM → <c>0.001</c>, 50 % → <c>0.5</c>).
/// </summary>
public static class KrlAnout
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Temperature channels 1–3:
    /// <c>$ANOUT[n] = ((−150 + T) × 0.032 + 0.032) / 10</c>
    /// </summary>
    public static float TempToAnout(float tempC)
    {
        float v = ((-150f + tempC) * 0.032f + 0.032f) / 10f;
        return Math.Clamp(v, 0f, 1f);
    }

    /// <summary>KRL literal for a temperature channel, e.g. <c>0.2272</c> at 220 °C.</summary>
    public static string TempToAnoutText(float tempC)
        => TempToAnout(tempC).ToString("F4", Inv);

    /// <summary>Idle extruder speed: 1 % RPM → <c>$ANOUT[4] = 0.001</c>.</summary>
    public const float RpmIdlePercent = 1f;

    /// <summary><c>$ANOUT[4]</c> for idle/travel extruder spin.</summary>
    public static float RpmIdleAnout => RpmIdlePercent / 1000f;

    /// <summary>KRL literal for idle RPM, e.g. <c>0.001</c>.</summary>
    public static string RpmIdleAnoutText =>
        RpmIdleAnout.ToString("0.###", Inv);

    /// <summary>
    /// Extruder RPM channel 4 from motor speed percentage:
    /// <c>$ANOUT[4] = RPM% ÷ 100</c>, then <see cref="RoundAnout4UpToPercent"/>
    /// so the machine only sees whole-percent steps (1–100 %).
    /// </summary>
    public static float RpmPercentToAnout(float rpmPercent)
    {
        if (rpmPercent <= 0f) return 0f;
        return RoundAnout4UpToPercent(rpmPercent / 100f);
    }

    /// <summary>
    /// Round a raw <c>$ANOUT[4]</c> extrusion value <b>up</b> to the next whole percent
    /// (0.01 steps). Prefers slight overextrusion over underextrusion — the motor only
    /// accepts 1–100 % resolution.
    /// <list type="bullet">
    /// <item><c>0.0203</c> → <c>0.03</c></item>
    /// <item><c>0.0303</c> → <c>0.04</c></item>
    /// <item><c>0.03</c> exact → <c>0.03</c></item>
    /// <item><c>0</c> stays <c>0</c> (extruder off / travel)</item>
    /// </list>
    /// </summary>
    public static float RoundAnout4UpToPercent(float anout)
    {
        if (anout <= 0f) return 0f;
        // Ceiling to 0.01. Epsilon keeps exact whole percents (0.50, 0.03) from
        // float-noise-bumping to the next step (0.51 / 0.04).
        float percent = MathF.Ceiling(anout * 100f - 1e-4f);
        percent = Math.Clamp(percent, 1f, 100f);
        return percent / 100f;
    }

    /// <summary>KRL literal for an RPM <c>$ANOUT[4]</c> value (always two decimals, e.g. <c>0.03</c>).</summary>
    public static string RpmPercentToAnoutText(float rpmPercent)
        => FormatAnout4(RpmPercentToAnout(rpmPercent));

    /// <summary>KRL literal for a normalized <c>$ANOUT[4]</c> value after percent ceiling.</summary>
    public static string FormatAnout4(float anout)
    {
        if (anout <= 0f) return "0.00";
        return RoundAnout4UpToPercent(anout).ToString("0.00", Inv);
    }

    /// <summary>
    /// Motor RPM percentage from bead geometry, print speed, and material <see cref="Models.MaterialPreset.FlowRate"/>.
    /// <c>rpm% = beadWidth_mm × layerHeight_mm × printSpeed_mps × flowRate_rev_per_cm3 × 60</c>
    /// </summary>
    public static float ComputeRpmPercent(
        float beadWidthMm, float layerHeightMm, float printSpeedMps, float flowRate)
        => beadWidthMm * layerHeightMm * printSpeedMps * flowRate * 60f;

    /// <summary>Normalized <c>$ANOUT[4]</c> for an extrusion move.</summary>
    public static float RpmToAnout(
        float beadWidthMm, float layerHeightMm, float printSpeedMps, float flowRate)
        => RpmPercentToAnout(ComputeRpmPercent(beadWidthMm, layerHeightMm, printSpeedMps, flowRate));

    /// <summary>KRL literal for an extrusion-move <c>$ANOUT[4]</c> TRIGGER value.</summary>
    public static string RpmToAnoutText(
        float beadWidthMm, float layerHeightMm, float printSpeedMps, float flowRate)
        => RpmPercentToAnoutText(ComputeRpmPercent(beadWidthMm, layerHeightMm, printSpeedMps, flowRate));

    /// <summary>Inverse of <see cref="TempToAnout"/> for live $ANOUT display.</summary>
    public static float AnoutToTempC(float anout)
        => (float)(((anout * 10.0 / 0.032) - 0.032) + 150.0);

    /// <summary>Inverse of <see cref="RpmPercentToAnout"/> for live $ANOUT display.</summary>
    public static float AnoutToRpmPercent(float anout) => anout * 100f;
}