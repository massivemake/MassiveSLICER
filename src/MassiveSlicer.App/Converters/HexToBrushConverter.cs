using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MassiveSlicer.Converters;

public sealed class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || hex.Length == 0) return Brushes.White;
        return Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
