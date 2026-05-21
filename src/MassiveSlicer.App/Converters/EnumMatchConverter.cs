using System.Globalization;
using Avalonia.Data.Converters;

namespace MassiveSlicer.Converters;

/// <summary>
/// Returns true when the bound value's string representation equals the converter parameter.
/// Used to drive Classes.active bindings on shader-mode buttons.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public static readonly EnumMatchConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
