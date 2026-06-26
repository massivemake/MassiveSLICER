using MassiveSlicer.Core.C3Bridge;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>Formats raw poll values for the live I/O monitor UI.</summary>
public static class LiveIoValueFormatter
{
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