using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Pikura.Avalonia.Converters;

/// <summary>
/// Picks a background/border brush for a Hoshi chat bubble based on whether the message is from
/// the user (true) or Hoshi/system (false), so the two are visually distinguishable at a glance
/// instead of relying solely on the small role label.
/// </summary>
public sealed class BoolToBubbleBrushConverter : IValueConverter
{
    private enum Kind { Background, Border }

    private static readonly IBrush UserBackground = new SolidColorBrush(Color.Parse("#2E3F63"));
    private static readonly IBrush UserBorder      = new SolidColorBrush(Color.Parse("#4A6BAE"));
    private static readonly IBrush OtherBackground = new SolidColorBrush(Color.Parse("#2A2A32"));
    private static readonly IBrush OtherBorder      = new SolidColorBrush(Color.Parse("#3A3A44"));

    private readonly Kind _kind;

    private BoolToBubbleBrushConverter(Kind kind) => _kind = kind;

    public static readonly BoolToBubbleBrushConverter Background = new(Kind.Background);
    public static readonly BoolToBubbleBrushConverter Border     = new(Kind.Border);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isUser = value is true;
        return _kind switch
        {
            Kind.Background => isUser ? UserBackground : OtherBackground,
            Kind.Border      => isUser ? UserBorder     : OtherBorder,
            _ => OtherBackground,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
