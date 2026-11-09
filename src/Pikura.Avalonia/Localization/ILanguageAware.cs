namespace Pikura.Avalonia.Localization;

/// <summary>
/// Implemented by view models and other objects that produce localized strings in C# (computed
/// labels, status messages). Registered with <see cref="LocalizationService.Register"/>; called on
/// the UI thread after every language change so the object can re-raise its localized properties.
/// </summary>
public interface ILanguageAware
{
    void OnLanguageChanged();
}
