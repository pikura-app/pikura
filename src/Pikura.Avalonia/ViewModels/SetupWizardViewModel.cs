using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

/// <summary>A template preset: the raw template plus its rendered sample.</summary>
public sealed record TemplatePreset(string Template, string Example);

/// <summary>A pixiv content-language choice (native name, so it needs no translation).</summary>
public sealed record LocaleOption(string Code, string Name);

/// <summary>An anime-tagger model with its on-disk install state.</summary>
public sealed partial class TaggerOption : ObservableObject
{
    public TaggerModelInfo Model { get; }
    public string Name => Model.Name;
    public string SizeText => $"~{Model.EstimatedBytes / 1024 / 1024} MB";
    [ObservableProperty] private bool _isInstalled;

    public TaggerOption(TaggerModelInfo model)
    {
        Model = model;
        Refresh();
    }

    public void Refresh() => IsInstalled = AnimeTaggerService.IsModelInstalled(Model);
}

/// <summary>First-run Quick Setup wizard. Pages bind to <see cref="Settings"/> (a real
/// SettingsViewModel) so every change writes through the existing handlers.</summary>
public partial class SetupWizardViewModel : ViewModelBase
{
    public const int PageCount = 8;
    private const int AccountPage = 1;
    private const int HoshiPage = 6;
    public const string RecommendedVisionModel = "qwen3-vl:4b-instruct";

    private readonly PixivLoginService _login;
    private readonly OllamaService _ollama;
    private readonly SettingsService _settings;

    public SettingsViewModel Settings { get; }

    [ObservableProperty] private int _currentPageIndex;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string? _loginError;
    [ObservableProperty] private bool _isLoggingIn;
    [ObservableProperty] private string _ollamaStatus = "";
    [ObservableProperty] private bool _ollamaAvailable;
    [ObservableProperty] private bool _isCheckingOllama;
    [ObservableProperty] private string _signedInText = "";

    /// <summary>Raised when the user presses Finish on the last page.</summary>
    public event EventHandler? Finished;

    public SetupWizardViewModel(PixivLoginService login, AccountService accounts,
        OllamaService ollama, SettingsService settings)
    {
        _login = login;
        _ollama = ollama;
        _settings = settings;
        Settings = AppServices.Get<SettingsViewModel>();
        IsSignedIn = settings.Current.IsConfigured;
        if (IsSignedIn)
            SignedInText = settings.Current.UserName ?? settings.Current.UserId ?? "";

        TaggerOptions = KnownAnimeTaggerModels.All.Select(m => new TaggerOption(m)).ToList();
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        Settings.InstalledVisionModelNames.CollectionChanged += (_, _) => RaiseVisionState();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.R18Mode):
                OnPropertyChanged(nameof(ShowR18Type));
                break;
            case nameof(SettingsViewModel.FolderTemplate):
            case nameof(SettingsViewModel.FilenameTemplate):
            case nameof(SettingsViewModel.DownloadRoot):
            case nameof(SettingsViewModel.DateFormat):
                OnPropertyChanged(nameof(PathExample));
                break;
            case nameof(SettingsViewModel.HoshiAnimeTaggerModel):
                OnPropertyChanged(nameof(SelectedTagger));
                OnPropertyChanged(nameof(SelectedTaggerInstalled));
                break;
            case nameof(SettingsViewModel.IsInstallingTaggerModel):
                if (!Settings.IsInstallingTaggerModel) RefreshTaggerState();
                break;
            case nameof(SettingsViewModel.HoshiVisionModel):
                RaiseVisionState();
                break;
            case nameof(SettingsViewModel.SelectedTheme):
                OnPropertyChanged(nameof(SelectedThemeOption));
                break;
        }
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    public string StepText => LocalizationService.F("Setup_StepOf", CurrentPageIndex + 1, PageCount);
    public bool CanGoBack => CurrentPageIndex > 0;
    public bool IsLastPage => CurrentPageIndex == PageCount - 1;
    public bool Page0 => CurrentPageIndex == 0;
    public bool Page1 => CurrentPageIndex == 1;
    public bool Page2 => CurrentPageIndex == 2;
    public bool Page3 => CurrentPageIndex == 3;
    public bool Page4 => CurrentPageIndex == 4;
    public bool Page5 => CurrentPageIndex == 5;
    public bool Page6 => CurrentPageIndex == 6;
    public bool Page7 => CurrentPageIndex == 7;
    public bool IsAccountPage => CurrentPageIndex == AccountPage;
    /// <summary>Next is blocked only on the required sign-in page.</summary>
    public bool CanGoNext => !(IsAccountPage && !IsSignedIn);

    partial void OnCurrentPageIndexChanged(int value)
    {
        foreach (var name in new[]
                 {
                     nameof(StepText), nameof(Page0), nameof(Page1), nameof(Page2), nameof(Page3),
                     nameof(Page4), nameof(Page5), nameof(Page6), nameof(Page7), nameof(CanGoBack),
                     nameof(IsLastPage), nameof(IsAccountPage), nameof(CanGoNext)
                 })
            OnPropertyChanged(name);
        if (value == HoshiPage) _ = PrepareHoshiPageAsync();
    }

    partial void OnIsSignedInChanged(bool value) => OnPropertyChanged(nameof(CanGoNext));

    [RelayCommand]
    private void Back() { if (CanGoBack) CurrentPageIndex--; }

    [RelayCommand]
    private void Next() { if (CurrentPageIndex < PageCount - 1) CurrentPageIndex++; }

    [RelayCommand]
    private void Finish() => Finished?.Invoke(this, EventArgs.Empty);

    // ── Welcome ─────────────────────────────────────────────────────────────

    public IReadOnlyList<LocaleOption> LocaleOptions { get; } =
    [
        new("en", "English"), new("ja", "日本語"), new("zh", "中文"), new("ko", "한국어"),
        new("es", "Español"), new("fr", "Français"), new("de", "Deutsch"),
        new("it", "Italiano"), new("pt", "Português"), new("ru", "Русский"),
    ];

    /// <summary>pixiv's <c>lang=</c> (titles/tags from pixiv), separate from the app language.</summary>
    public LocaleOption? SelectedLocale
    {
        get => LocaleOptions.FirstOrDefault(o => o.Code == Settings.PixivLocale) ?? LocaleOptions[0];
        set
        {
            if (value is null || value.Code == Settings.PixivLocale) return;
            Settings.SetPixivLocaleCommand.Execute(value.Code);
            OnPropertyChanged();
        }
    }

    // ── Account ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoginAsync(Window? owner)
    {
        if (IsLoggingIn) return;
        IsLoggingIn = true;
        LoginError = null;
        try
        {
            var result = await _login.LoginAsync(owner!, clearCookies: true);
            if (result.Success)
            {
                IsSignedIn = true;
                SignedInText = result.UserName ?? "";
                // Keep the rest of the app in sync; safe even before the main window shows.
                try { await AppServices.Get<GalleryViewModel>().SwitchAccountAsync(); }
                catch { /* non-fatal — gallery re-reads on show anyway */ }
            }
            else
            {
                LoginError = result.ErrorMessage ?? LocalizationService.Instance["Setup_LoginFailed"];
            }
        }
        catch (Exception ex)
        {
            LoginError = ex.Message;
        }
        finally { IsLoggingIn = false; }
    }

    // ── Downloads ───────────────────────────────────────────────────────────

    private static readonly ArtworkPreview SampleArtwork = new()
    {
        Id = "12345678",
        Title = "Sample Artwork Title",
        UserId = "9876543",
        UserName = "ArtistName",
        PageCount = 3,
        CreateDate = new DateTimeOffset(2025, 4, 1, 12, 0, 0, TimeSpan.Zero),
        Tags = ["tag1", "tag2"],
    };

    /// <summary>Renders a template with the real download resolver and a sample artwork.</summary>
    private string Render(string template) =>
        new FilenameTemplate(Settings.DateFormat).Resolve(template,
            new FilenameContext { Artwork = SampleArtwork, PageIndex = 0, PageCount = 3, OriginalUrl = "x.jpg" });

    private IReadOnlyList<TemplatePreset> Presets(params string[] templates) =>
        templates.Select(t => new TemplatePreset(t, Render(t) is { Length: > 0 } r ? r : "—")).ToList();

    public IReadOnlyList<TemplatePreset> FolderPresets => Presets(
        "%artist% (%member_id%)", "%member_id%", "%artist%", "%artist%\\%works_date%",
        "%artist% (%member_id%)\\%title% (%image_id%)", "");

    public IReadOnlyList<TemplatePreset> FilenamePresets => Presets(
        "%image_id%_p%page_index%", "%image_id% - %title%_p%page_index%", "%title%_p%page_number%",
        "%artist% - %title% (%image_id%)_p%page_index%", "%works_date%_%image_id%_p%page_index%");

    public TemplatePreset? SelectedFolderPreset
    {
        get => null;
        set { if (value is not null) Settings.FolderTemplate = value.Template; OnPropertyChanged(); }
    }

    public TemplatePreset? SelectedFilenamePreset
    {
        get => null;
        set { if (value is not null) Settings.FilenameTemplate = value.Template; OnPropertyChanged(); }
    }

    public string[] FolderTokens { get; } =
        ["%artist%", "%member_id%", "%member_token%", "%title%", "%image_id%", "%works_date%", "%R-18%"];

    public string[] FilenameTokens { get; } =
    [
        "%image_id%", "%title%", "%artist%", "%page_index%", "%page_number%", "%date%",
        "%works_date%", "%member_id%", "%member_token%", "%bookmark_count%", "%manga_series_title%", "%tags%"
    ];

    public string? SelectedFolderToken
    {
        get => null;
        set { if (value is not null) Settings.FolderTemplate += value; OnPropertyChanged(); }
    }

    public string? SelectedFilenameToken
    {
        get => null;
        set { if (value is not null) Settings.FilenameTemplate += value; OnPropertyChanged(); }
    }

    /// <summary>Full example path for page 1 of a sample artwork.</summary>
    public string PathExample
    {
        get
        {
            try
            {
                return Path.Combine(Settings.DownloadRoot, Render(Settings.FolderTemplate),
                    Render(Settings.FilenameTemplate) + ".jpg");
            }
            catch { return ""; }
        }
    }

    // ── Content filters ─────────────────────────────────────────────────────

    public string[] R18ModeOptions { get; } = ["Off", "Show", "Only"];
    public bool ShowR18Type => Settings.R18Mode != "Off";

    // ── Appearance ──────────────────────────────────────────────────────────

    public string[] ThemeOptions { get; } = ["System", "Light", "Dark"];

    /// <summary>Theme selection that goes through SettingsViewModel.SetThemeCommand —
    /// the only path that applies the theme live AND persists it.</summary>
    public string SelectedThemeOption
    {
        get => Settings.SelectedTheme;
        set
        {
            if (string.IsNullOrEmpty(value) || value == Settings.SelectedTheme) return;
            Settings.SetThemeCommand.Execute(value);
        }
    }

    // ── Startup & updates ───────────────────────────────────────────────────

    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Update-check frequency. Settings only saves this via its Save button, so save here.</summary>
    public string UpdateCheckFrequency
    {
        get => Settings.UpdateCheckFrequency;
        set
        {
            if (string.IsNullOrEmpty(value) || value == Settings.UpdateCheckFrequency) return;
            Settings.UpdateCheckFrequency = value;
            Settings.SaveUpdateSettingsCommand.Execute(null);
            OnPropertyChanged();
        }
    }

    // ── Hoshi ───────────────────────────────────────────────────────────────

    private async Task CheckOllamaAsync()
    {
        IsCheckingOllama = true;
        try
        {
            OllamaAvailable = await _ollama.IsOllamaAvailableAsync();
            OllamaStatus = LocalizationService.Instance[OllamaAvailable ? "Setup_OllamaDetected" : "Setup_OllamaNotFound"];
            if (OllamaAvailable)
                await Settings.RefreshInstalledModelsCommand.ExecuteAsync(null);
        }
        finally { IsCheckingOllama = false; }
    }

    [RelayCommand]
    private Task CheckOllamaAgainAsync() => CheckOllamaAsync();

    private async Task PrepareHoshiPageAsync()
    {
        RefreshTaggerState();
        await CheckOllamaAsync();
    }

    public IReadOnlyList<TaggerOption> TaggerOptions { get; }

    public TaggerOption? SelectedTagger
    {
        get => TaggerOptions.FirstOrDefault(o =>
                   string.Equals(o.Model.Key, Settings.HoshiAnimeTaggerModel, StringComparison.OrdinalIgnoreCase))
               ?? TaggerOptions.FirstOrDefault();
        set
        {
            if (value is null) return;
            Settings.HoshiAnimeTaggerModel = value.Model.Key;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTaggerInstalled));
        }
    }

    public bool SelectedTaggerInstalled => SelectedTagger?.IsInstalled == true;

    private void RefreshTaggerState()
    {
        foreach (var o in TaggerOptions) o.Refresh();
        OnPropertyChanged(nameof(SelectedTaggerInstalled));
    }

    /// <summary>Downloads the selected tagger through Settings' install path, then turns tagging on.</summary>
    [RelayCommand]
    private async Task InstallSelectedTaggerAsync()
    {
        var key = SelectedTagger?.Model.Key;
        var row = Settings.RecommendedTaggerModels.FirstOrDefault(r =>
            string.Equals(r.TaggerModel?.Key, key, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        await Settings.InstallRecommendedModelCommand.ExecuteAsync(row);
        RefreshTaggerState();
        if (SelectedTaggerInstalled) Settings.HoshiUseAnimeTagger = true;
    }

    private static bool IsRecommendedVision(string? name) =>
        string.Equals(name, RecommendedVisionModel, StringComparison.OrdinalIgnoreCase);

    public bool RecommendedVisionInstalled => Settings.InstalledVisionModelNames.Any(IsRecommendedVision);
    public bool UsingRecommendedVision => IsRecommendedVision(Settings.HoshiVisionModel);
    /// <summary>The recommended model is on disk but a different one is selected.</summary>
    public bool CanSwitchToRecommendedVision => RecommendedVisionInstalled && !UsingRecommendedVision;

    private void RaiseVisionState()
    {
        OnPropertyChanged(nameof(RecommendedVisionInstalled));
        OnPropertyChanged(nameof(UsingRecommendedVision));
        OnPropertyChanged(nameof(CanSwitchToRecommendedVision));
    }

    [RelayCommand]
    private void UseRecommendedVision() => Settings.UseModelForVisionCommand.Execute(RecommendedVisionModel);

    /// <summary>Pulls the recommended vision model through Settings' pull path and selects it.</summary>
    [RelayCommand]
    private async Task InstallRecommendedVisionAsync()
    {
        Settings.ModelToInstall = RecommendedVisionModel;
        await Settings.PullModelCommand.ExecuteAsync(null);
        if (RecommendedVisionInstalled)
            Settings.UseModelForVisionCommand.Execute(RecommendedVisionModel);
    }

    /// <summary>Background tagging of the Library (no SettingsViewModel wrapper exists).</summary>
    public bool LibraryAutoTag
    {
        get => _settings.Current.LibraryAutoTag;
        set
        {
            if (value == _settings.Current.LibraryAutoTag) return;
            _settings.Update(s => s.LibraryAutoTag = value);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void OpenOllamaSite()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://ollama.com/download") { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Re-renders code-built strings in the new language.</summary>
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(StepText));
        if (!string.IsNullOrEmpty(OllamaStatus))
            OllamaStatus = LocalizationService.Instance[OllamaAvailable ? "Setup_OllamaDetected" : "Setup_OllamaNotFound"];
    }
}
