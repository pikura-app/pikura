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

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Mode = BindingMode.OneWay,
            Source = LocalizationService.Instance,
            // Show the key if lookup somehow yields nothing, so missing strings are visible.
            FallbackValue = Key,
        };
}
