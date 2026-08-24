using System.Globalization;
using MassiveSlicer.Core.C3Bridge;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>Formats raw poll values for the live I/O monitor UI.</summary>
public static class LiveIoValueFormatter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool? TryParseBool(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (t is "1" or "0") return t == "1";
        return KrlVarParser.ParseBool(raw);
    }

    /// <summary>Normalises bridge JSON values to the string form <see cref="FormatDisplay"/> expects.</summary>
    public static string? FormatBridgeRaw(object? value) => value switch
    {
        null            => null,
        bool b          => b ? "TRUE" : "FALSE",
        int or long or float or double => value.ToString(),
        _               => value.ToString(),
    };

    public static string FormatDisplay(LiveIoSignalConfig signal, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        if (signal.Kind is LiveIoSignalKind.DigitalInput or LiveIoSignalKind.DigitalOutput)
            return KrlVarParser.ParseBool(raw) ? "HIGH" : "LOW";

        double scalar = KrlVarParser.ParseScalar(raw);

        if (signal.Source == LiveIoSource.ExtruderModbus && signal.ValueFormat == LiveIoValueFormat.TempC)
            return $"{scalar:F1} °C";

        if (signal.Source == LiveIoSource.ExtruderBridge)
        {
            if (signal.Key.StartsWith("RTDValue_", StringComparison.Ordinal))
                return $"{scalar / 10.0:F1} °C";
            if (signal.ValueFormat == LiveIoValueFormat.Millivolt)
                return $"{scalar / 1000.0:F3} V";
        }

        return signal.ValueFormat switch
        {
            LiveIoValueFormat.TempC       => $"{KrlAnout.AnoutToTempC((float)scalar):F1} °C",
            LiveIoValueFormat.RpmPercent  => $"{KrlAnout.AnoutToRpmPercent((float)scalar):F1} %",
            LiveIoValueFormat.Millivolt   => $"{scalar:F2} V",
            _                             => signal.Unit is { } u ? $"{scalar:F2} {u}" : $"{scalar:F3}",
        };
    }

    /// <summary>Editable engineering value for an analog channel (no unit).</summary>
    public static string FormatEditValue(LiveIoSignalConfig signal, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "0";
        double scalar = KrlVarParser.ParseScalar(raw);
        return signal.ValueFormat switch
        {
            LiveIoValueFormat.TempC      => KrlAnout.AnoutToTempC((float)scalar).ToString("F1", Inv),
            LiveIoValueFormat.RpmPercent => KrlAnout.AnoutToRpmPercent((float)scalar).ToString("F1", Inv),
            _                            => scalar.ToString("F4", Inv),
        };
    }

    /// <summary>
    /// Convert UI engineering text to a KRL write string for <c>$ANOUT</c> (or raw REAL).
    /// O1-O3: C -> ANOUT; O4: % -> ANOUT; Raw: 0-1 ANOUT.
    /// </summary>
    public static bool TryParseAnalogWrite(LiveIoSignalConfig signal, string? editText, out string krlValue, out string error)
    {
        krlValue = "0";
        error = "";
        if (string.IsNullOrWhiteSpace(editText))
        {
            error = "Enter a number";
            return false;
        }

        var cleaned = editText.Trim()
            .TrimEnd('%')
            .Replace("°C", "", StringComparison.OrdinalIgnoreCase)
            .Replace("C", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double eng)
            && !double.TryParse(cleaned, NumberStyles.Float, CultureInfo.CurrentCulture, out eng))
        {
            error = "Enter a number";
            return false;
        }

        float anout = signal.ValueFormat switch
        {
            LiveIoValueFormat.TempC      => KrlAnout.TempToAnout((float)eng),
            LiveIoValueFormat.RpmPercent => KrlAnout.RpmPercentToAnout((float)eng),
            _                            => (float)Math.Clamp(eng, 0.0, 1.0),
        };

        krlValue = signal.ValueFormat == LiveIoValueFormat.RpmPercent
            ? KrlAnout.FormatAnout4(anout)
            : anout.ToString("0.####", Inv);
        return true;
    }

    /// <summary>Whether the indicator should show the lime active state.</summary>
    public static bool IsActiveIndicator(LiveIoSignalConfig signal, bool? value)
    {
        if (value is not bool b) return false;
        return signal.Highlight switch
        {
            LiveIoHighlight.Fault  => !b,
            LiveIoHighlight.Safety => !b,
            _                      => b,
        };
    }

    /// <summary>Whether the indicator should show amber warning.</summary>
    public static bool IsWarningIndicator(LiveIoSignalConfig signal, bool? value)
        => signal.Highlight == LiveIoHighlight.Safety && value == true;

    /// <summary>Whether the indicator should show red fault.</summary>
    public static bool IsFaultIndicator(LiveIoSignalConfig signal, bool? value)
        => signal.Highlight == LiveIoHighlight.Fault && value == true;
}
