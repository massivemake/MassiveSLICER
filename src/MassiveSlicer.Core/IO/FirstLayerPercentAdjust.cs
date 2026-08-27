using System.Globalization;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// First-layer Print Speed / RPM % increase — same +/- box as KRL Export
/// Extrusion Speed, but only applied to layer 0.
/// Print speed is multiplicative (+20 = 1.20×). RPM is additive points
/// (+10 = ten points higher), matching <c>ExtrusionSpeedOffset</c>.
/// </summary>
public static class FirstLayerPercentAdjust
{
    public static double Parse(string? text)
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

    public static bool Has(string? text) => Math.Abs(Parse(text)) > 1e-9;

    public static double SpeedMmS(double baseMmS, string? offset)
        => Math.Clamp(baseMmS * (1.0 + Parse(offset) / 100.0), 0.1, 2000.0);

    public static double RpmPercent(double baseRpm, string? offset)
        => Math.Clamp(baseRpm + Parse(offset), 0.0, 100.0);
}
