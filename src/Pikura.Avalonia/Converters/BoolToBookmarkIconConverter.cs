using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Pikura.Avalonia.Converters;

/// <summary>
/// Converts a "saved for later" boolean to a background brush for the bookmark toggle icon —
/// accent-colored when saved, translucent black otherwise. The glyph itself ("🔖") stays fixed;
/// only the background changes so the saved state reads clearly at a glance.
/// </summary>
public class BoolToBookmarkIconConverter : IValueConverter
{
    public static readonly BoolToBookmarkIconConverter Instance = new();

    private static readonly IBrush SavedBackground = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush UnsavedBackground = new SolidColorBrush(Color.Parse("#AA000000"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? SavedBackground : UnsavedBackground;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
