using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Pikura.Avalonia.Localization;

/// <summary>
/// Translates a stable English label (e.g. a pixivision category name defined in Core) via the
/// key <c>{ConverterParameter}{Label without spaces}</c>, falling back to the label itself.
/// Used in a MultiBinding whose second binding targets <see cref="LocalizationService"/>'s
/// indexer purely so the text re-evaluates when the language changes.
/// </summary>
public sealed class LocLabelConverter : IMultiValueConverter
{
    public static readonly LocLabelConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not string label || label.Length == 0) return values.Count > 0 ? values[0] : null;
        var key = (parameter as string ?? "") + label.Replace(" ", "");
        var text = LocalizationService.Instance[key];
        return text == key ? label : text;
    }
}
