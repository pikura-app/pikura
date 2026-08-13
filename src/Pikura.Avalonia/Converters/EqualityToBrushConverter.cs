using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pikura.Avalonia.Converters;

/// <summary>
/// Multi-value converter for a simple "selected dot" indicator: compares its two bound values
/// and returns an accent-colored brush when they're equal (selected), or a dim gray brush
/// otherwise. Used by the feature-highlights media selector to show which screenshot/GIF of a
/// page is currently displayed.
/// </summary>
public sealed class EqualityToBrushConverter : IMultiValueConverter
{
    public static readonly EqualityToBrushConverter Instance = new();

    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush Unselected = new SolidColorBrush(Color.Parse("#66808080"));

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return Unselected;
        return Equals(values[0], values[1]) ? Selected : Unselected;
    }
}
