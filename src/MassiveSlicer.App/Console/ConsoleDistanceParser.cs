using System.Globalization;
using System.Text.RegularExpressions;

namespace MassiveSlicer.App.Console;

/// <summary>Parses shop-floor distances (feet, inches, mm) to millimetres for relative jog commands.</summary>
internal static partial class ConsoleDistanceParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Default step when distance is omitted: 1 foot.</summary>
    public const double DefaultMm = 304.8;

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DistanceRegex();

    /// <summary>
    /// Parses <paramref name="text"/> to mm. Bare number = feet (shop-floor default).
    /// Supports: 1' 1ft 12" 12in 100mm 0.5m
    /// </summary>
    public static bool TryParseToMm(string text, out double mm)
    {
        mm = 0;
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            mm = DefaultMm;
            return true;
        }

        var m = DistanceRegex().Match(text);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out var n))
            return false;

        var unit = m.Groups[2].Value.Trim().ToLowerInvariant().Replace(" ", "");
        mm = unit switch
        {
            "" or "'" or "ft" or "foot" or "feet" or "′" => n * 304.8,
            "\"" or "in" or "inch" or "inches" or "″" => n * 25.4,
            "mm" => n,
            "m" or "meter" or "meters" or "metre" or "metres" => n * 1000.0,
            "cm" => n * 10.0,
            _ => n * 304.8,
        };
        return true;
    }

    /// <summary>Strips optional trailing velocity token (1–100) from move args.</summary>
    public static (string DistanceText, int Vel) SplitDistanceAndVel(string args, int defaultVel = 20)
    {
        args = (args ?? string.Empty).Trim();
        if (args.Length == 0)
            return (string.Empty, defaultVel);

        var parts = args.Split([' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2
            && int.TryParse(parts[^1], NumberStyles.Integer, Inv, out var vel)
            && vel is >= 1 and <= 100
            && TryParseToMm(string.Join(' ', parts[..^1]), out _))
            return (string.Join(' ', parts[..^1]), vel);

        return (args, defaultVel);
    }
}