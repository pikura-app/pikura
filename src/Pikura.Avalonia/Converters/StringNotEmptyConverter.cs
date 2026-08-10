using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Pikura.Avalonia.Converters;

public sealed class StringNotEmptyConverter : IValueConverter
{
    public static readonly StringNotEmptyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrEmpty(s);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return global::Avalonia.AvaloniaProperty.UnsetValue;
    }
}
