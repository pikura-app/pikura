using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Pikura.Avalonia.Converters;

/// <summary>
/// Converts an icon resource key (e.g. "DownloadIcon") into the corresponding
/// <see cref="Geometry"/> resource from application-level resources.
/// </summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
            return null;

        if (Application.Current?.TryGetResource(key, null, out var resource) == true && resource is Geometry geometry)
            return geometry;

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
