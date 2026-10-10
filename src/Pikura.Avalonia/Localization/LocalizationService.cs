using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Pikura.Avalonia.Localization;

/// <summary>
/// Central lookup for translated UI strings.
/// <para>
/// XAML binds through the indexer (<c>{loc:Tr SomeKey}</c> compiles to a binding against
/// <c>[SomeKey]</c> on <see cref="Instance"/>). Changing the language raises a change notification
/// for the indexer, so every bound string re-reads itself and the UI switches language live —
/// no restart required.
/// </para>
/// <para>
/// Strings live in <c>Localization/Strings*.resx</c>. A key with no translation in the active
/// language falls back to English, and a key missing entirely falls back to the key name so a typo
/// is visible in the UI rather than rendering blank.
/// </para>
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    private readonly ResourceManager _resources = new(
        "Pikura.Avalonia.Localization.Strings",
        typeof(LocalizationService).Assembly);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>Display name → culture code. The display names are what's stored in
    /// <c>AppSettings.AppLanguage</c> and shown in the Settings dropdown.</summary>
    private static readonly Dictionary<string, string> LanguageCultures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en",
        ["日本語"] = "ja",
        ["中文"] = "zh",
        ["한국어"] = "ko",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The culture currently used for lookups.</summary>
    public CultureInfo Culture { get; private set; } = English;

    /// <summary>The display name of the active language (as stored in settings).</summary>
    public string Language { get; private set; } = "English";

    private LocalizationService() { }

    /// <summary>Translated string for <paramref name="key"/>. Bound from XAML via the
    /// <c>Tr</c> markup extension.</summary>
    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            try
            {
                return _resources.GetString(key, Culture)
                    ?? _resources.GetString(key, English)
                    ?? key;
            }
            catch (MissingManifestResourceException)
            {
                // No resources compiled in at all — show the key so it's obvious something's wrong
                // rather than silently blanking the whole UI.
                return key;
            }
        }
    }

    /// <summary>Non-binding lookup, for strings built in C# (status messages, dialog text).</summary>
    public string Get(string key) => this[key];

    /// <summary>Switches the active language by display name (e.g. "日本語"). Unknown names fall
    /// back to English. Raises an indexer change so bound UI strings refresh immediately.</summary>
    public void SetLanguage(string? displayName)
    {
        var name = displayName ?? "English";
        if (!LanguageCultures.TryGetValue(name, out var code))
        {
            name = "English";
            code = "en";
        }

        var culture = CultureInfo.GetCultureInfo(code);
        if (Equals(culture, Culture) && string.Equals(name, Language, StringComparison.Ordinal))
            return;

        Culture = culture;
        Language = name;

        // "Item[]" is the conventional name for "every indexer value changed" — Avalonia re-evaluates
        // all bindings against the indexer when it sees this.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }
}
