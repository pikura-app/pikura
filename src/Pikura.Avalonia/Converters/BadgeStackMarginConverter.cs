using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pikura.Avalonia.Converters;

/// <summary>
/// Computes the right-edge margin for one badge in a stack of overlay badges (local favorite,
/// Pixiv bookmark, liked heart, etc.) that share a corner of an artwork thumbnail. Each badge's
/// slot used to be a hardcoded margin (e.g. 54/30/6px) that assumed every badge before it toward
/// the edge was always visible — when one was hidden, the remaining badges stayed in their fixed
/// slots and left a gap between them and the true edge instead of packing together.
///
/// Bind the visibility flags of every badge that sits *closer to the edge* than this one (in
/// edge-to-far order isn't required; any order works since only the count of true values
/// matters) as MultiBinding values. The result is the base inset plus 24px for each
/// currently-visible closer badge, so badges always pack flush against the edge regardless of
/// which combination is showing.
///
/// ConverterParameter controls the mode/base inset: pass a plain number (or omit) for the
/// default horizontal bottom-right stack (base inset 6, applied as the Right+Bottom margin);
/// prefix with "V" (e.g. "V6") for a vertical top-right stack instead (base inset applied as
/// the Top+Right margin) — used where badges stack downward from the top-right corner rather
/// than leftward along the bottom-right edge.
/// </summary>
public sealed class BadgeStackMarginConverter : IMultiValueConverter
{
    public static readonly BadgeStackMarginConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var baseInset = 6.0;
        var vertical = false;
        if (parameter is string s)
        {
            if (s.StartsWith("V", StringComparison.OrdinalIgnoreCase))
            {
                vertical = true;
                s = s[1..];
            }
            if (s.Length > 0 && double.TryParse(s, NumberStyles.Any, culture, out var custom))
                baseInset = custom;
        }

        var visibleCount = 0;
        foreach (var v in values)
            if (v is bool b && b) visibleCount++;

        var offset = baseInset + visibleCount * 24;
        return vertical ? new Thickness(0, offset, baseInset, 0) : new Thickness(0, 0, offset, baseInset);
    }
}
