using Avalonia.Data;
using Avalonia.Markup.Xaml;
using System;

namespace Pikura.Avalonia.Localization;

/// <summary>
/// XAML markup extension for translated strings: <c>Text="{loc:Tr Settings_Title}"</c>.
/// <para>
/// Produces a one-way binding against <see cref="LocalizationService"/>'s indexer rather than a
/// plain string, so switching language updates every translated string in place instead of
/// requiring a restart.
/// </para>
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    /// <summary>Resource key to look up in <c>Localization/Strings*.resx</c>.</summary>
    public string Key { get; set; } = string.Empty;

    // Binds to the plain Language property (whose change notification Avalonia reliably honours)
    // and resolves the key in a converter; Avalonia ignores "Item[]" for indexer bindings, so
    // binding to [Key] directly never refreshed on a live language switch.
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding(nameof(LocalizationService.Language))
        {
            Mode = BindingMode.OneWay,
            Source = LocalizationService.Instance,
            Converter = TrConverter.Instance,
            ConverterParameter = Key,
            // Show the key if lookup somehow yields nothing, so missing strings are visible.
            FallbackValue = Key,
        };
}

internal sealed class TrConverter : global::Avalonia.Data.Converters.IValueConverter
{
    public static readonly TrConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => LocalizationService.Instance[parameter as string ?? string.Empty];

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
