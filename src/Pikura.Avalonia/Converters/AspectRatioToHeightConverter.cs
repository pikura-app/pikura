using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Pikura.Avalonia.Converters;

/// <summary>
/// Single-value converter for a fixed-width natural-height image: height = width × aspectRatio,
/// where width is a fixed <see cref="ConverterParameter"/> (a double or string parseable as one).
/// Used where the width is a design-time constant rather than a bindable CardSize property (e.g.
/// InlineArtworkViewer's Collage mode), so <see cref="MultiplyByAspectRatioConverter"/>'s
/// multi-binding form isn't needed.
/// </summary>
public sealed class AspectRatioToHeightConverter : IValueConverter
{
    public static readonly AspectRatioToHeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value switch
        {
            double d => d,
            int i => i,
            float f => f,
            _ => 1.0
        };
        if (ratio <= 0) ratio = 1.0;

        var width = parameter switch
        {
            double d => d,
            int i => i,
            float f => f,
            string s when double.TryParse(s, culture, out var parsed) => parsed,
            _ => 200.0
        };

        return width * ratio;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
