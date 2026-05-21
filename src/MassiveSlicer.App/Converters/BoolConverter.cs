using System.Globalization;
using Avalonia.Data.Converters;

namespace MassiveSlicer.Converters;

/// <summary>
/// Converts a bool to bool (with optional inversion) for use with IsVisible bindings.
/// Replaces WPF's BoolToVisibilityConverter — in Avalonia, IsVisible accepts bool directly.
/// </summary>
public sealed class BoolConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool bval = value is bool b && b;
        return Invert ? !bval : bval;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool result = value is bool b && b;
        return Invert ? !result : result;
    }
}
