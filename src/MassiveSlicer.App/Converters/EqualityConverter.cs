using System.Globalization;
using Avalonia.Data.Converters;

namespace MassiveSlicer.Converters;

/// <summary>
/// Multi-value converter that returns true when both bound values are equal.
/// Used to highlight the active selection in tab bars and preset lists.
/// </summary>
public sealed class EqualityConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count == 2 && Equals(values[0], values[1]);
}
