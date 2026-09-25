using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using MassiveSlicer.Core.Units;

namespace MassiveSlicer.App;

/// <summary>
/// Lets measurement fields take a length with units — "10in", "10\"", "5 1/2 in", "5-1/2\"", "2ft",
/// "1' 6\"", "12.5cm", "1.2m" — and turns it into millimetres (<see cref="LengthInput"/>). No unit
/// still means millimetres, so plain numbers behave exactly as before.
/// </summary>
/// <remarks>
/// Opt-in per field with the <c>Length</c> style class, like <see cref="NumericFieldUx"/>'s
/// <c>NumericCommit</c>. Only real lengths carry it — never angles, percentages, RPM, temperatures,
/// speeds, counts or radii — so "10in" in a degrees box is simply not accepted rather than silently
/// becoming 254°.
/// <list type="bullet">
/// <item><see cref="TextBox"/> (including <c>TransformNumberBox</c>, which is one): on Enter or when
/// focus leaves, a length is rewritten to its millimetre number before the field's own commit reads
/// it. Rewriting at commit rather than per keystroke means the text never jumps while "10inch" is
/// still being typed. Class handlers run ahead of instance handlers, and Enter is caught on the
/// tunnel, so this is always first.</item>
/// <item><see cref="NumericUpDown"/>: a <see cref="LengthTextConverter"/> is installed as its
/// <c>TextConverter</c>, which is how the spinner turns text into its value.</item>
/// </list>
/// </remarks>
internal static class LengthFieldUx
{
    public const string LengthClass = "Length";

    /// <summary>Installs the global class handlers. Call once at startup.</summary>
    public static void Install()
    {
        InputElement.KeyDownEvent.AddClassHandler<TextBox>((tb, e) =>
        {
            if (e.Key == Key.Enter && tb.Classes.Contains(LengthClass)) Normalise(tb);
        }, RoutingStrategies.Tunnel);

        InputElement.LostFocusEvent.AddClassHandler<TextBox>((tb, _) =>
        {
            if (tb.Classes.Contains(LengthClass)) Normalise(tb);
        });

        Control.LoadedEvent.AddClassHandler<NumericUpDown>((nud, _) =>
        {
            if (nud.Classes.Contains(LengthClass) && nud.TextConverter is not LengthTextConverter)
                nud.TextConverter = new LengthTextConverter(nud);
        });
    }

    /// <summary>
    /// Rewrites a length in <paramref name="tb"/> to plain millimetres, or flags it when it is a
    /// malformed length. Anything that is not a length at all is left for the field as before.
    /// </summary>
    internal static void Normalise(TextBox tb)
    {
        var r = LengthInput.Parse(tb.Text);
        switch (r.Kind)
        {
            case LengthInput.Kind.Length:
                DataValidationErrors.ClearErrors(tb);
                string mm = FormatMm(r.Millimetres);
                if (tb.Text?.Trim() != mm) tb.Text = mm;
                break;
            case LengthInput.Kind.Invalid:
                DataValidationErrors.SetError(tb, new FormatException(r.Error));
                break;
        }
    }

    /// <summary>Millimetres as a plain number, up to three decimals, no trailing zeros.</summary>
    internal static string FormatMm(double mm)
        => Math.Round(mm, 3).ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// <see cref="NumericUpDown.TextConverter"/> that reads lengths with units. Display is unchanged
/// (the spinner's own <c>FormatString</c> and number format). A malformed length keeps the
/// current value instead of clearing the field.
/// </summary>
internal sealed class LengthTextConverter(NumericUpDown owner) : IValueConverter
{
    // NumericUpDown calls this converter in both directions, and which method it uses for which
    // direction is its own business — so each method decides by what it is handed: text is read,
    // a number is formatted.
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text ? Read(text, culture) : Format(value, culture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text ? Read(text, culture) : Format(value, culture);

    private string? Format(object? value, CultureInfo culture)
        => value is decimal d
            ? d.ToString(string.IsNullOrEmpty(owner.FormatString) ? "G" : owner.FormatString, (IFormatProvider?)owner.NumberFormat ?? culture)
            : value?.ToString();

    private decimal? Read(string text, CultureInfo culture)
    {
        var r = LengthInput.Parse(text);
        switch (r.Kind)
        {
            case LengthInput.Kind.Length:
                DataValidationErrors.ClearErrors(owner);
                return (decimal)r.Millimetres;
            case LengthInput.Kind.Invalid:
                DataValidationErrors.SetError(owner, new FormatException(r.Error));
                return owner.Value;
            default:
                if (string.IsNullOrWhiteSpace(text)) return null;
                return decimal.TryParse(text, owner.ParsingNumberStyle, (IFormatProvider?)owner.NumberFormat ?? culture, out var n)
                    ? n
                    : owner.Value;
        }
    }
}
