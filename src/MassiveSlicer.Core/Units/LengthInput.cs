using System.Globalization;
using System.Text.RegularExpressions;

namespace MassiveSlicer.Core.Units;

/// <summary>
/// Reads a length typed into a field — "254", "10in", "10\"", "5 1/2 in", "5-1/2\"", "3/4in",
/// "2ft", "1' 6\"", "12.5cm", "1.2m" — and returns millimetres. No unit means millimetres, even
/// with a fraction ("5 1/2" is 5.5 mm), so a plain number behaves exactly as it always has.
/// </summary>
/// <remarks>
/// Units: <c>mm</c>; <c>cm</c>; <c>m</c>; inches as <c>"</c>, <c>in</c>, <c>inch</c>, <c>inches</c>;
/// feet as <c>'</c>, <c>ft</c>, <c>feet</c>, <c>foot</c>. <c>'</c> is feet, as on a tape measure.
/// A whole number and a fraction are joined by a dash or a space: "5-1/2" or "5 1/2". A bare
/// fraction must be a proper one ("3/4"): "51/2" is refused rather than guessed at, since it
/// could mean 25.5 or 5 1/2.
/// </remarks>
public static class LengthInput
{
    /// <summary>What a piece of text turned out to be.</summary>
    public enum Kind
    {
        /// <summary>Not a length at all (empty, or not a number) — let the field handle it as before.</summary>
        NotALength,
        /// <summary>A length, in <see cref="Result.Millimetres"/>.</summary>
        Length,
        /// <summary>Looks like a length but is malformed; <see cref="Result.Error"/> says why.</summary>
        Invalid,
    }

    public readonly record struct Result(Kind Kind, double Millimetres, string? Error, bool HadUnit);

    private const string Number = @"\d+(?:\.\d+)?|\.\d+";

    // One amount: optional whole part joined to a fraction by '-' or spaces, or a plain number,
    // then an optional unit.
    private static readonly Regex Amount = new(
        @"^\s*(?<neg>-)?\s*" +
        @"(?:(?<whole>\d+)(?:\s*-\s*|\s+)(?<num>\d+)\s*/\s*(?<den>\d+)" +   // 5-1/2, 5 1/2
        @"|(?<fnum>\d+)\s*/\s*(?<fden>\d+)" +                               // 3/4
        @"|(?<dec>" + Number + @"))" +                                      // 12.5
        @"\s*(?<unit>mm|millimet(?:er|re)s?|cm|centimet(?:er|re)s?|m|met(?:er|re)s?|""|''|in|inch|inches|'|ft|feet|foot)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Feet then inches: 1'6", 1' 6", 1ft 6in, 1' 6 1/2", 1'-6".
    private static readonly Regex FeetInches = new(
        @"^\s*(?<neg>-)?\s*(?<ft>\d+(?:\.\d+)?)\s*(?:'|ft|feet|foot)\s*-?\s*(?<rest>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Parses <paramref name="text"/>; see the class remarks for what is accepted.</summary>
    public static Result Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(Kind.NotALength, 0, null, false);
        var t = Normalise(text);

        var fi = FeetInches.Match(t);
        if (fi.Success)
        {
            var rest = ParseAmount(fi.Groups["rest"].Value, defaultUnit: "in");
            if (rest.Kind == Kind.Length && rest.Unit is null or "in")
            {
                double mm = double.Parse(fi.Groups["ft"].Value, CultureInfo.InvariantCulture) * 304.8 + rest.Millimetres;
                return new(Kind.Length, fi.Groups["neg"].Success ? -mm : mm, null, true);
            }
            if (rest.Kind == Kind.Invalid) return new(Kind.Invalid, 0, rest.Error, true);
        }

        var a = ParseAmount(t, defaultUnit: null);
        return a.Kind switch
        {
            Kind.Length => new(Kind.Length, a.Millimetres, null, a.Unit is not null),
            Kind.Invalid => new(Kind.Invalid, 0, a.Error, true),
            _ => new(Kind.NotALength, 0, null, false),
        };
    }

    /// <summary>Millimetres for <paramref name="text"/>, or false when it is not a valid length.</summary>
    public static bool TryParseMm(string? text, out double mm)
    {
        var r = Parse(text);
        mm = r.Millimetres;
        return r.Kind == Kind.Length;
    }

    private readonly record struct Amt(Kind Kind, double Millimetres, string? Unit, string? Error);

    private static Amt ParseAmount(string t, string? defaultUnit)
    {
        var m = Amount.Match(t);
        if (!m.Success)
        {
            // A digit run that failed only because of an unknown word is a typo, not "not a length".
            return Regex.IsMatch(t, @"^\s*-?\s*[\d.]") && Regex.IsMatch(t, @"[a-zA-Z""']")
                ? new(Kind.Invalid, 0, null, $"\"{t.Trim()}\" isn't a length this field understands (use mm, cm, m, in, \" , ft or ').")
                : new(Kind.NotALength, 0, null, null);
        }

        double value;
        if (m.Groups["whole"].Success)
        {
            double den = double.Parse(m.Groups["den"].Value, CultureInfo.InvariantCulture);
            if (den == 0) return new(Kind.Invalid, 0, null, "A fraction can't divide by zero.");
            value = double.Parse(m.Groups["whole"].Value, CultureInfo.InvariantCulture)
                  + double.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture) / den;
        }
        else if (m.Groups["fnum"].Success)
        {
            double num = double.Parse(m.Groups["fnum"].Value, CultureInfo.InvariantCulture);
            double den = double.Parse(m.Groups["fden"].Value, CultureInfo.InvariantCulture);
            if (den == 0) return new(Kind.Invalid, 0, null, "A fraction can't divide by zero.");
            if (num >= den)
                return new(Kind.Invalid, 0, null,
                    $"\"{m.Groups["fnum"].Value}/{m.Groups["fden"].Value}\" is ambiguous — write a whole number and fraction as 5-1/2 or 5 1/2.");
            value = num / den;
        }
        else
            value = double.Parse(m.Groups["dec"].Value.StartsWith('.') ? "0" + m.Groups["dec"].Value : m.Groups["dec"].Value,
                CultureInfo.InvariantCulture);

        if (m.Groups["neg"].Success) value = -value;

        string? unit = m.Groups["unit"].Success ? Canonical(m.Groups["unit"].Value) : null;
        double factor = (unit ?? defaultUnit) switch
        {
            "in" => 25.4,
            "ft" => 304.8,
            "cm" => 10.0,
            "m" => 1000.0,
            _ => 1.0,   // mm, or no unit
        };
        return new(Kind.Length, value * factor, unit, null);
    }

    private static string Canonical(string unit) => unit.ToLowerInvariant() switch
    {
        "\"" or "''" or "in" or "inch" or "inches" => "in",
        "'" or "ft" or "feet" or "foot" => "ft",
        "cm" or "centimeter" or "centimeters" or "centimetre" or "centimetres" => "cm",
        "m" or "meter" or "meters" or "metre" or "metres" => "m",
        _ => "mm",
    };

    /// <summary>Curly quotes and primes from phones and word processors read as ' and ".</summary>
    private static string Normalise(string s) => s
        .Replace('‘', '\'').Replace('’', '\'').Replace('′', '\'')
        .Replace('“', '"').Replace('”', '"').Replace('″', '"');
}
