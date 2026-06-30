using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MassiveSlicer.Converters;

/// <summary>Maps bool → themed brush (e.g. outliner row highlight).</summary>
public sealed class BoolToThemedBrushConverter : IValueConverter
{
    public string TrueResourceKey { get; set; } = "AccentMuted";
    public string FalseResourceKey { get; set; } = "Transparent";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value is true;
        if (!on)
        {
            if (Application.Current?.TryGetResource(FalseResourceKey, null, out var off) == true && off is IBrush offBrush)
                return offBrush;
            return Brushes.Transparent;
        }

        if (Application.Current?.TryGetResource(TrueResourceKey, null, out var resource) == true && resource is IBrush brush)
            return brush;

        // Fallback when theme resources are not resolved (nested outliner templates).
        return TrueResourceKey switch
        {
            "Accent" => new SolidColorBrush(Color.Parse("#6fcf00")),
            "TextPrimary" => new SolidColorBrush(Color.Parse("#e8eaed")),
            _ => new SolidColorBrush(Color.Parse("#2e5c00")),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}