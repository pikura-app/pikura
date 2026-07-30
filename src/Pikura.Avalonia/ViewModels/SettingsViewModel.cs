using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.Views.Login;
using Pikura.Avalonia.Views.Dialogs;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Styling;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Pikura.Avalonia.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly DialogService _dialogService;
    private readonly PixivClient _pixivClient;
    private readonly FilePickerService _filePickerService;
    private readonly AccountService _accountService;

    [ObservableProperty]
    private string _accountStatus = "Not signed in";

    [ObservableProperty]
    private string _accountDetail = "Paste your PHPSESSID cookie to sign in.";

    [ObservableProperty]
    private bool _isSignedIn;

    [ObservableProperty]
    private string _downloadRoot = string.Empty;

    [ObservableProperty]
    private string _settingsPathHint = string.Empty;

    public static string AppVersion { get; } =
        System.Reflection.Assembly.GetEntryAssembly()
            ?.GetName().Version
            ?.ToString(3)   // major.minor.patch
        ?? "unknown";

    // Image Processing computed properties
    public bool IsCustomResizePreset => ActiveResizePresetIndex == 13;
    public bool IsJpegOutputFormat => ResizeOutputFormatIndex == 1 || ResizeOutputFormatIndex == 2;

    [ObservableProperty]
    private bool _isLightTheme;

    // Accessibility
    [ObservableProperty] private double _fontSizeScale = 1.0;
    [ObservableProperty] private bool _useHighContrast;
    [ObservableProperty] private bool _useLargeFonts;
    [ObservableProperty] private bool _increaseSpacing;
    [ObservableProperty] private bool _reduceMotion;

    // Download Configuration - Core
    [ObservableProperty] private string _filenameTemplate = "%image_id%_p%page_index%";
    [ObservableProperty] private string _filenameMangaFormat = "%artist% (%member_id%)\\%image_id% - %title%\\%page_number%";
    [ObservableProperty] private string _filenameInfoFormat = "%artist% (%member_id%)\\%image_id% - %title%.txt";
    [ObservableProperty] private string _folderTemplate = "%artist% (%member_id%)";
    [ObservableProperty] private string _dateFormat = "yyyy-MM-dd";
    [ObservableProperty] private string _tagsSeparator = ", ";
    [ObservableProperty] private int _maxConcurrentDownloads = 3;
    [ObservableProperty] private int _maxConcurrentJobs = 3;
    [ObservableProperty] private bool _createSubfolderPerSubmission;
    [ObservableProperty] private bool _separateR18Folder;
    // Anti-suspension safe mode (issue #19): forces concurrency=1, jittered delays,
    // and 429/503 backoff. Default true to protect new users; existing users keep
    // whatever they had stored.
    [ObservableProperty] private bool _safeMode = true;

    // R-18 & Filtering
    [ObservableProperty] private string _r18Mode = "Show";
    [ObservableProperty] private string _r18Type = "Both";
    [ObservableProperty] private bool _blurR18Content;
    [ObservableProperty] private int _blurIntensity = 15;
    [ObservableProperty] private bool _filterAiGenerated;
    [ObservableProperty] private ObservableCollection<string> _excludedTags = new();
    [ObservableProperty] private string _newExcludedTag = "";

    // Metadata Export
    [ObservableProperty] private bool _writeImageJSON;
    [ObservableProperty] private bool _writeImageInfo;
    [ObservableProperty] private bool _writeRawJSON;
    [ObservableProperty] private bool _includeSeriesJSON;
    [ObservableProperty] private bool _writeImageXMP;
    [ObservableProperty] private bool _writeImageXMPPerImage;
    [ObservableProperty] private bool _verifyImage;
    [ObservableProperty] private bool _setLastModified = true;
    [ObservableProperty] private bool _useLocalTimezone;

    // Image Processing
    [ObservableProperty] private bool _enableImageProcessing;
    [ObservableProperty] private int _activeResizePresetIndex; // 0=None, 1-12=presets, 13=custom
    [ObservableProperty] private int _resizeCustomWidth = 1920;
    [ObservableProperty] private int _resizeCustomHeight = 1080;
    [ObservableProperty] private bool _resizeMaintainAspect = true;
    [ObservableProperty] private string _resizeMode = "Fit";
    [ObservableProperty] private int _resizeOutputFormatIndex; // 0=original, 1-2=JPEG, 3=PNG, 4=WebP
    [ObservableProperty] private int _resizeJpegQuality = 90;
    [ObservableProperty] private bool _useCustomProcessingFolder;
    [ObservableProperty] private string _imageProcessingOutputFolder = "";

    // Download Control
    [ObservableProperty] private bool _downloadAvatarAndBanner;
    [ObservableProperty] private int _overwriteMode; // 0=skip, 1=overwrite, 2=backup
    [ObservableProperty] private bool _backupOldFile;
    [ObservableProperty] private int _minFileSizeKB;
    [ObservableProperty] private int _maxFileSizeKB;
    [ObservableProperty] private int _downloadTimeout = 60;
    [ObservableProperty] private int _retryCount = 3;
    [ObservableProperty] private int _retryDelaySeconds = 5;
    [ObservableProperty] private int _downloadDelaySeconds;
    [ObservableProperty] private int _downloadBufferKB = 512;

    // Blacklist Filtering
    [ObservableProperty] private ObservableCollection<string> _blacklistTags = new();
    [ObservableProperty] private bool _useBlacklistTagsRegex;
    [ObservableProperty] private ObservableCollection<string> _blacklistTitles = new();
    [ObservableProperty] private bool _useBlacklistTitlesRegex;
    [ObservableProperty] private ObservableCollection<string> _blacklistMembers = new();
    [ObservableProperty] private string _newBlacklistTag = "";
    [ObservableProperty] private string _newBlacklistTitle = "";
    [ObservableProperty] private string _newBlacklistMember = "";

    // Network / Proxy
    [ObservableProperty] private bool _useProxy;
    [ObservableProperty] private string _proxyAddress = "";
    [ObservableProperty] private bool _enableSSLVerification = true;
    [ObservableProperty] private bool _useRobots = true;

    // Ugoira (Animated Images)
    [ObservableProperty] private bool _createUgoiraMp4 = true;
    [ObservableProperty] private bool _createUgoiraWebm;
    [ObservableProperty] private bool _createUgoiraGif;
    [ObservableProperty] private bool _createUgoiraWebp;
    [ObservableProperty] private bool _createUgoiraApng;
    [ObservableProperty] private bool _keepUgoiraZip = true;
    [ObservableProperty] private bool _saveUgoiraFrames;
    [ObservableProperty] private bool _ugoiraFramesOnly;
    [ObservableProperty] private string _ffMpegCodec = "libvpx-vp9";
    [ObservableProperty] private int _ffMpegCRF = 15;

    // FFmpeg status (computed)
    [ObservableProperty] private bool _isFfmpegInstalled;
    [ObservableProperty] private string _ffmpegStatusText = "Checking…";
    [ObservableProperty] private string _ffmpegVersionText = "";

    // FANBOX
    [ObservableProperty] private string _filenameFanboxCover = "FANBOX %artist% (%member_id%)\\%urlFilename% - %title%";
    [ObservableProperty] private string _filenameFanboxContent = "FANBOX %artist% (%member_id%)\\%urlFilename% - %title%";
    [ObservableProperty] private string _filenameFanboxInfo = "FANBOX %artist% (%member_id%)\\%urlFilename% - %title%.txt";
    [ObservableProperty] private bool _downloadFanboxCoverWhenRestricted = true;
    [ObservableProperty] private bool _writeFanboxHtml;

    // Localization
    [ObservableProperty] private string _pixivLocale = "en";
    [ObservableProperty] private string _appLanguage = "English";

    // Gallery Viewer Keyboard Navigation
    [ObservableProperty] private bool _galleryKeyboardNavEnabled = true;
    [ObservableProperty] private bool _fullscreenKeyboardNavEnabled = true;

    // Startup & System Tray
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _startupWindowState = "Normal";
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startMinimizedToTray;
    [ObservableProperty] private bool _keepSchedulesRunningInBackground;
    [ObservableProperty] private bool _showScheduleNotifications = true;
    [ObservableProperty] private bool _notifyOnDownloadComplete = false;
    [ObservableProperty] private bool _notifyOnDownloadStarted  = false;
    [ObservableProperty] private bool _notifyOnDownloadFailed   = true;
    [ObservableProperty] private bool _notifyOnDownloadPaused   = true;

    /// <summary>0=Disabled, 1=Minimize to tray, 2=Close to tray</summary>
    public int TrayBehavior
    {
        get
        {
            if (CloseToTray) return 2;
            if (MinimizeToTray) return 1;
            return 0;
        }
        set
        {
            MinimizeToTray = value >= 1;
            CloseToTray = value == 2;
            StartMinimizedToTray = false;
            _settingsService.Update(s =>
            {
                s.MinimizeToTray = MinimizeToTray;
                s.CloseToTray = CloseToTray;
                s.StartMinimizedToTray = false;
            });
            OnPropertyChanged();
        }
    }

    // Available options
    public string[] StartupWindowStates { get; } = { "Normal", "Maximized", "Minimized", "System Tray" };
    public string[] AvailableLocales { get; } = { "en", "ja", "zh", "ko", "es", "fr", "de", "it", "pt", "ru" };
    public string[] AvailableAppLanguages { get; } = { "English", "日本語", "中文", "한국어" };
    public string[] AvailableR18Types { get; } = { "Both", "R18", "R18G" };

    // Token definitions with descriptions for UI
    public static readonly Dictionary<string, string> FilenameTokens = new()
    {
        ["%image_id%"] = "Image/post ID (12345678)",
        ["%title%"] = "Artwork title",
        ["%artist%"] = "Artist name",
        ["%member_id%"] = "Artist numeric ID",
        ["%member_token%"] = "Artist token (may change)",
        ["%page_index%"] = "Page number 0-indexed (0, 1, 2...)",
        ["%page_number%"] = "Page number 1-indexed (1, 2, 3...)",
        ["%page_big%"] = "Adds 'big' for manga mode",
        ["%date%"] = "Current date",
        ["%date_fmt{...}%"] = "Custom date format",
        ["%works_date%"] = "Artwork upload date",
        ["%works_date_only%"] = "Artwork date only (no time)",
        ["%works_date_fmt{...}%"] = "Custom works date format",
        ["%works_res%"] = "Image resolution",
        ["%works_tools%"] = "Tools used (e.g., Photoshop)",
        ["%R-18%"] = "R-18 tag if applicable",
        ["%AI%"] = "AI tag if AI-generated",
        ["%image_ext%"] = "File extension (jpg, png)",
        ["%urlFilename%"] = "Original server filename",
        ["%bookmark%"] = "'Bookmarks' string",
        ["%bookmark_count%"] = "Number of bookmarks",
        ["%image_response_count%"] = "Response count",
        ["%manga_series_id%"] = "Manga series ID",
        ["%manga_series_order%"] = "Order in manga series",
        ["%manga_series_title%"] = "Manga series title",
        ["%original_member_id%"] = "Original member ID (bookmarks)",
        ["%original_member_token%"] = "Original token (bookmarks)",
        ["%original_artist%"] = "Original artist (bookmarks)",
        ["%searchTags%"] = "Searched tags",
        ["%tags%"] = "All artwork tags",
    };

    public static readonly Dictionary<string, string> FolderTokens = new()
    {
        ["%artist%"] = "Artist name",
        ["%member_id%"] = "Artist numeric ID",
        ["%member_token%"] = "Artist token",
        ["%R-18%"] = "R-18 folder marker",
    };

    // Diagnostics
    [ObservableProperty] private bool _verboseLogging;

    // ── Update settings ───────────────────────────────────────────────────────
    [ObservableProperty] private string _updateCheckFrequency = "Startup";
    [ObservableProperty] private bool   _autoDownloadUpdates;
    [ObservableProperty] private bool   _notifyOnUpdate = true;
    [ObservableProperty] private string _updateChannel  = "Stable";
    [ObservableProperty] private string _updateStatusMessage = string.Empty;
    [ObservableProperty] private bool   _checkingForUpdates;

    public static string[] UpdateFrequencyOptions { get; } = ["Startup", "Daily", "Weekly", "Never"];
    public static string[] UpdateChannelOptions   { get; } = ["Stable", "PreRelease"];

    // ── Per-account settings ──────────────────────────────────────────────────
    [ObservableProperty] private bool   _useAccountSettings;
    [ObservableProperty] private string _acctDownloadRoot     = string.Empty;
    [ObservableProperty] private string _acctFolderTemplate   = string.Empty;
    [ObservableProperty] private string _acctFilenameTemplate = string.Empty;
    [ObservableProperty] private bool   _acctFilterAiGenerated;
    [ObservableProperty] private bool   _acctSkipR18;
    [ObservableProperty] private bool   _acctSkipR18G;
    [ObservableProperty] private bool   _acctSeparateR18Folder;
    [ObservableProperty] private bool   _acctAllowRedownload;
    [ObservableProperty] private int    _acctMaxConcurrentDownloads = 3;
    [ObservableProperty] private bool   _hasActiveProfile;
    [ObservableProperty] private string _activeProfileLabel = string.Empty;
    [ObservableProperty] private string _activeProfileId    = string.Empty;
    [ObservableProperty] private bool   _hasMultipleAccounts;

    public ObservableCollection<AccountProfile> Profiles { get; } = new();

    [RelayCommand]
    private async Task BrowseAcctDownloadRootAsync()
    {
        var folder = await _filePickerService.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder)) AcctDownloadRoot = folder;
    }

    [RelayCommand]
    private void SaveAccountSettings()
    {
        var profile = _accountService.ActiveProfile;
        if (profile is null) return;
        _accountService.SaveAccountSettings(profile.Id, new AccountSettings
        {
            UseAccountSettings      = UseAccountSettings,
            DownloadRoot            = string.IsNullOrWhiteSpace(AcctDownloadRoot) ? null : AcctDownloadRoot,
            FolderTemplate          = string.IsNullOrWhiteSpace(AcctFolderTemplate) ? null : AcctFolderTemplate,
            FilenameTemplate        = string.IsNullOrWhiteSpace(AcctFilenameTemplate) ? null : AcctFilenameTemplate,
            FilterAiGenerated       = AcctFilterAiGenerated,
            SkipR18                 = AcctSkipR18,
            SkipR18G                = AcctSkipR18G,
            SeparateR18Folder       = AcctSeparateR18Folder,
            MaxConcurrentDownloads  = AcctMaxConcurrentDownloads,
            AllowRedownload         = AcctAllowRedownload,
        });
    }

    private void RefreshProfiles()
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Profiles.Clear();
            foreach (var p in _accountService.Profiles)
                Profiles.Add(p);
            HasMultipleAccounts = Profiles.Count > 1;
        });
    }

    [RelayCommand]
    private async Task SwitchToAccountAsync(AccountProfile profile)
    {
        if (_accountService.ActiveProfile?.Id == profile.Id) return;
        _accountService.SwitchTo(profile.Id);
        LoadSettings();
        try { await AppServices.Get<GalleryViewModel>().SwitchAccountAsync(); } catch { }
    }

    [RelayCommand]
    private async Task SignOutAccountAsync(AccountProfile profile)
    {
        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Sign out", $"Sign out of {profile.DisplayLabel}?");
        if (!confirmed) return;
        _accountService.Remove(profile.Id);
        LoadSettings();
        try { await AppServices.Get<GalleryViewModel>().SwitchAccountAsync(); } catch { }
    }

    [RelayCommand]
    private async Task SignOutAllAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Sign out all", "Sign out of all accounts and clear all sessions?");
        if (!confirmed) return;
        foreach (var p in _accountService.Profiles.ToList())
            _accountService.Remove(p.Id);
        _settingsService.Update(s => { s.PhpSessId = string.Empty; s.UserId = null; s.UserName = null; });
        LoadSettings();
        try { await AppServices.Get<GalleryViewModel>().SwitchAccountAsync(); } catch { }
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow is null) return;
        if (await DoLoginAsync(mainWindow, clearCookies: true))
        {
            LoadSettings();
            try { await AppServices.Get<GalleryViewModel>().SwitchAccountAsync(); } catch { }
        }
    }

    private void LoadAccountSettings()
    {
        var profile = _accountService.ActiveProfile;
        HasActiveProfile   = profile is not null;
        ActiveProfileLabel = profile?.DisplayLabel ?? string.Empty;
        ActiveProfileId    = profile?.Id           ?? string.Empty;
        if (profile?.Settings is { } s)
        {
            UseAccountSettings       = s.UseAccountSettings;
            AcctDownloadRoot         = s.DownloadRoot         ?? string.Empty;
            AcctFolderTemplate       = s.FolderTemplate       ?? string.Empty;
            AcctFilenameTemplate     = s.FilenameTemplate     ?? string.Empty;
            AcctFilterAiGenerated    = s.FilterAiGenerated    ?? false;
            AcctSkipR18              = s.SkipR18              ?? false;
            AcctSkipR18G             = s.SkipR18G             ?? false;
            AcctSeparateR18Folder       = s.SeparateR18Folder       ?? false;
            AcctMaxConcurrentDownloads   = s.MaxConcurrentDownloads  ?? 3;
            AcctAllowRedownload          = s.AllowRedownload          ?? false;
        }
        else
        {
            UseAccountSettings = false;
            AcctDownloadRoot = AcctFolderTemplate = AcctFilenameTemplate = string.Empty;
            AcctFilterAiGenerated = AcctSkipR18 = AcctSkipR18G = AcctSeparateR18Folder = AcctAllowRedownload = false;
            AcctMaxConcurrentDownloads = 3;
        }
    }

    public static string AppDataFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pikura");

    public static string DownloadLogPath =>
        System.IO.Path.Combine(AppDataFolder, "download.log");

    public static string AppLogPath =>
        System.IO.Path.Combine(AppDataFolder, "pikura.log");

    public static string CrashLogPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "pikura-crash.txt");

    // Settings that require restart
    [ObservableProperty] private bool _restartRequired;
    private string _pendingRestartChange = "";

    // ── Hoshi AI Model Management ────────────────────────────────────────────
    private readonly OllamaService _ollama;
    private readonly AnimeTaggerService _animeTagger;
    [ObservableProperty] private bool _useCustomHoshiModels;
    private string _hoshiTextModel = string.Empty;
    private string _hoshiVisionModel = string.Empty;
    [ObservableProperty] private string _modelToInstall = string.Empty;
    [ObservableProperty] private bool _isLoadingModels;
    [ObservableProperty] private bool _isPullingModel;
    [ObservableProperty] private string _modelPullStatus = string.Empty;
    [ObservableProperty] private double _modelPullPercent;
    [ObservableProperty] private string _modelManagementError = string.Empty;
    [ObservableProperty] private ObservableCollection<InstalledModelRow> _installedModels = new();
    /// <summary>Installed Ollama models only — shown in its own list, separate from taggers, to avoid confusion.</summary>
    [ObservableProperty] private ObservableCollection<InstalledModelRow> _installedOllamaModels = new();
    /// <summary>Installed ONNX anime tagger models only — shown in its own list, separate from Ollama models.</summary>
    [ObservableProperty] private ObservableCollection<InstalledModelRow> _installedTaggerModels = new();
    /// <summary>All installed Ollama models — used for the Text model dropdown.</summary>
    [ObservableProperty] private ObservableCollection<string> _installedOllamaModelNames = new();
    /// <summary>Only installed Ollama models that report the "vision" capability — used for the Vision model dropdown.</summary>
    [ObservableProperty] private ObservableCollection<string> _installedVisionModelNames = new();

    // Anime tagger settings
    [ObservableProperty] private bool _hoshiUseAnimeTagger;
    private string _hoshiAnimeTaggerModel = "wd-swinv2-tagger-v3";
    [ObservableProperty] private double _hoshiAnimeTaggerThreshold = 0.35;
    [ObservableProperty] private int _hoshiAnimeTaggerMaxTags = 50;
    [ObservableProperty] private bool _hoshiAnimeTaggerAutoTagDownloads;
    [ObservableProperty] private bool _isSelectedTaggerModelInstalled;
    [ObservableProperty] private bool _isInstallingTaggerModel;
    [ObservableProperty] private string _taggerInstallStatus = string.Empty;
    [ObservableProperty] private double _taggerInstallPercent;
    [ObservableProperty] private string _taggerInstallError = string.Empty;

    public ObservableCollection<RecommendedModelRow> RecommendedModels { get; } = new();
    public IEnumerable<RecommendedModelRow> RecommendedTextModels => RecommendedModels.Where(r => r.Category == "Text / chat");
    public IEnumerable<RecommendedModelRow> RecommendedVisionModels => RecommendedModels.Where(r => r.Category == "Vision");
    public IEnumerable<RecommendedModelRow> RecommendedTaggerModels => RecommendedModels.Where(r => r.Category == "Anime tagging");
    /// <summary>Tagger model keys that are actually installed on disk — used for the model dropdown
    /// so it only ever offers models you can use right now (mirrors the Ollama text/vision dropdowns).
    /// Browsing and installing new taggers happens via the chips below instead.</summary>
    [ObservableProperty] private ObservableCollection<string> _installedTaggerModelNames = new();

    private CancellationTokenSource? _modelPullCts;
    private CancellationTokenSource? _taggerInstallCts;

    /// <summary>Default model name used when no custom text model is configured.</summary>
    public string DefaultTextModelName => OllamaService.DefaultTextModel;
    /// <summary>Default model name used when no custom vision model is configured.</summary>
    public string DefaultVisionModelName => OllamaService.DefaultVisionModel;
    /// <summary>Suggested popular models for quick install.</summary>
    public string[] SuggestedModels { get; } = new[]
    {
        "llama3.2", "llama3.1", "llava", "moondream", "qwen2.5", "phi3", "gemma2", "mistral"
    };

    private readonly FfmpegService _ffmpegService;

    public SettingsViewModel(
        SettingsService settingsService,
        DialogService dialogService,
        PixivClient pixivClient,
        FilePickerService filePickerService,
        OllamaService ollama,
        AnimeTaggerService animeTagger,
        AccountService accountService,
        FfmpegService ffmpegService)
    {
        _settingsService = settingsService;
        _dialogService = dialogService;
        _pixivClient = pixivClient;
        _filePickerService = filePickerService;
        _ollama = ollama;
        _animeTagger = animeTagger;
        _accountService = accountService;
        _ffmpegService = ffmpegService;

        InitializeRecommendedModels();
        RefreshTaggerInstallState();

        LoadSettings();
        LoadAccountSettings();
        RefreshProfiles();
        _settingsService.Changed += (_, _) => LoadSettings();
        _accountService.ProfilesChanged      += (_, _) => RefreshProfiles();
        _accountService.ActiveProfileChanged += (_, _) =>
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => LoadAccountSettings());
            RefreshProfiles();
        };
        IsLightTheme = settingsService.Current.Theme == "Light";

        // Check ffmpeg status on startup
        _ = CheckFfmpegAsync();

        // Kick off an installed-models refresh in the background so the Hoshi AI
        // tab shows results without blocking startup or the UI thread.
        _ = global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try { await RefreshInstalledModelsAsync(); }
            catch { /* ignore — error already surfaces via ModelManagementError */ }
        });
    }

    private void LoadSettings()
    {
        var s = _settingsService.Current;
        IsSignedIn = s.IsConfigured;
        AccountStatus = s.IsConfigured
            ? $"Signed in as {s.UserName ?? s.UserId}"
            : "Not signed in";
        AccountDetail = s.IsConfigured
            ? $"User ID: {s.UserId}  ·  Session active"
            : "Sign in with your Pixiv username and password.";    
        DownloadRoot = s.DownloadRoot;

        // Naming templates
        FilenameTemplate = s.FilenameTemplate;
        FilenameMangaFormat = s.FilenameMangaFormat;
        FilenameInfoFormat = s.FilenameInfoFormat;
        FolderTemplate = s.FolderTemplate;
        DateFormat = s.DateFormat;
        TagsSeparator = s.TagsSeparator;

        // Core download settings
        MaxConcurrentDownloads = s.MaxConcurrentDownloads;
        MaxConcurrentJobs = s.MaxConcurrentJobs;
        CreateSubfolderPerSubmission = s.CreateSubfolderPerSubmission;
        SeparateR18Folder = s.SeparateR18Folder;
        SafeMode = s.SafeMode;

        // R-18 & Filtering
        R18Mode = s.R18Mode.ToString();
        R18Type = s.R18Type.ToString();
        BlurR18Content = s.BlurR18Content;
        BlurIntensity = s.BlurIntensity;
        FilterAiGenerated = s.FilterAiGenerated;
        ExcludedTags = new ObservableCollection<string>(s.ExcludedTags);

        // Metadata Export
        WriteImageJSON = s.WriteImageJSON;
        WriteImageInfo = s.WriteImageInfo;
        WriteRawJSON = s.WriteRawJSON;
        IncludeSeriesJSON = s.IncludeSeriesJSON;
        WriteImageXMP = s.WriteImageXMP;
        WriteImageXMPPerImage = s.WriteImageXMPPerImage;
        VerifyImage = s.VerifyImage;
        SetLastModified = s.SetLastModified;
        UseLocalTimezone = s.UseLocalTimezone;

        // Image Processing
        EnableImageProcessing = s.EnableImageProcessing;
        ActiveResizePresetIndex = (int)s.ActiveResizePreset;
        ResizeCustomWidth = s.ResizeCustomWidth;
        ResizeCustomHeight = s.ResizeCustomHeight;
        ResizeMaintainAspect = s.ResizeMaintainAspect;
        ResizeMode = s.ResizeMode.ToString();
        ResizeOutputFormatIndex = (int)s.ResizeOutputFormat;
        ResizeJpegQuality = s.ResizeJpegQuality;
        UseCustomProcessingFolder = !string.IsNullOrEmpty(s.ImageProcessingOutputFolder);
        ImageProcessingOutputFolder = s.ImageProcessingOutputFolder ?? "";

        // Download Control
        DownloadAvatarAndBanner = s.DownloadAvatarAndBanner;
        OverwriteMode = s.OverwriteMode;
        BackupOldFile = s.BackupOldFile;
        MinFileSizeKB = s.MinFileSizeKB;
        MaxFileSizeKB = s.MaxFileSizeKB;
        DownloadTimeout = s.DownloadTimeout;
        RetryCount = s.RetryCount;
        RetryDelaySeconds = s.RetryDelaySeconds;
        DownloadDelaySeconds = s.DownloadDelaySeconds;
        DownloadBufferKB = s.DownloadBufferKB;

        // Blacklist
        BlacklistTags = new ObservableCollection<string>(s.BlacklistTags);
        UseBlacklistTagsRegex = s.UseBlacklistTagsRegex;
        BlacklistTitles = new ObservableCollection<string>(s.BlacklistTitles);
        UseBlacklistTitlesRegex = s.UseBlacklistTitlesRegex;
        BlacklistMembers = new ObservableCollection<string>(s.BlacklistMembers);

        // Network
        UseProxy = s.UseProxy;
        ProxyAddress = s.ProxyAddress ?? "";
        EnableSSLVerification = s.EnableSSLVerification;
        UseRobots = s.UseRobots;

        // Accessibility
        FontSizeScale = s.FontSizeScale;
        UseHighContrast = s.UseHighContrast;
        UseLargeFonts = s.UseLargeFonts;
        IncreaseSpacing = s.IncreaseSpacing;
        ReduceMotion = s.ReduceMotion;

        // Ugoira
        CreateUgoiraMp4 = s.CreateUgoiraMp4;
        CreateUgoiraWebm = s.CreateUgoiraWebm;
        CreateUgoiraGif = s.CreateUgoiraGif;
        CreateUgoiraWebp = s.CreateUgoiraWebp;
        CreateUgoiraApng = s.CreateUgoiraApng;
        KeepUgoiraZip = s.KeepUgoiraZip;
        SaveUgoiraFrames = s.SaveUgoiraFrames;
        UgoiraFramesOnly = s.UgoiraFramesOnly;
        FfMpegCodec = s.FFmpegCodec;
        FfMpegCRF = s.FFmpegCRF;

        // FANBOX
        FilenameFanboxCover = s.FilenameFanboxCover;
        FilenameFanboxContent = s.FilenameFanboxContent;
        FilenameFanboxInfo = s.FilenameFanboxInfo;
        DownloadFanboxCoverWhenRestricted = s.DownloadFanboxCoverWhenRestricted;
        WriteFanboxHtml = s.WriteFanboxHtml;

        // Localization
        PixivLocale  = s.Locale;
        AppLanguage  = s.AppLanguage;

        // Gallery Viewer Keyboard Navigation
        GalleryKeyboardNavEnabled    = s.GalleryKeyboardNavEnabled;
        FullscreenKeyboardNavEnabled = s.FullscreenKeyboardNavEnabled;

        // Startup & System Tray
        StartWithWindows = s.StartWithWindows;
        StartupWindowState = s.StartupWindowState;
        MinimizeToTray = s.MinimizeToTray;
        CloseToTray = s.CloseToTray;
        StartMinimizedToTray = s.StartMinimizedToTray;
        KeepSchedulesRunningInBackground = s.KeepSchedulesRunningInBackground;
        ShowScheduleNotifications = s.ShowScheduleNotifications;
        NotifyOnDownloadComplete = s.NotifyOnDownloadComplete;
        NotifyOnDownloadStarted  = s.NotifyOnDownloadStarted;
        NotifyOnDownloadFailed   = s.NotifyOnDownloadFailed;
        NotifyOnDownloadPaused   = s.NotifyOnDownloadPaused;
        OnPropertyChanged(nameof(TrayBehavior));

        SettingsPathHint = $"Settings: {SettingsService.DefaultPath()}";

        // Diagnostics
        VerboseLogging = s.VerboseLogging;

        // Updates
        UpdateCheckFrequency = s.UpdateCheckFrequency;
        AutoDownloadUpdates  = s.AutoDownloadUpdates;
        NotifyOnUpdate       = s.NotifyOnUpdate;
        UpdateChannel        = s.UpdateChannel;

        // Hoshi AI model preferences — assign backing fields directly (not through the public
        // property setters) so loading saved values doesn't trip their "UseCustomHoshiModels = true"
        // side effect, which would otherwise flip a non-custom (default-model) configuration into
        // "custom" on every load/restart and persist that incorrect flag right back to disk.
        UseCustomHoshiModels = s.UseCustomHoshiModels;
        _hoshiTextModel = s.UseCustomHoshiModels ? s.HoshiTextModel : DefaultTextModelName;
        OnPropertyChanged(nameof(HoshiTextModel));
        _hoshiVisionModel = s.UseCustomHoshiModels ? s.HoshiVisionModel : DefaultVisionModelName;
        OnPropertyChanged(nameof(HoshiVisionModel));

        // Anime tagger preferences
        HoshiUseAnimeTagger = s.HoshiUseAnimeTagger;
        _hoshiAnimeTaggerModel = s.HoshiAnimeTaggerModel;
        OnPropertyChanged(nameof(HoshiAnimeTaggerModel));
        HoshiAnimeTaggerThreshold = s.HoshiAnimeTaggerThreshold;
        HoshiAnimeTaggerMaxTags = s.HoshiAnimeTaggerMaxTags;
        HoshiAnimeTaggerAutoTagDownloads = s.HoshiAnimeTaggerAutoTagDownloads;
        RefreshTaggerInstallState();
    }

    [RelayCommand]
    private void SaveUpdateSettings()
    {
        _settingsService.Update(s =>
        {
            s.UpdateCheckFrequency = UpdateCheckFrequency;
            s.AutoDownloadUpdates  = AutoDownloadUpdates;
            s.NotifyOnUpdate       = NotifyOnUpdate;
            s.UpdateChannel        = UpdateChannel;
        });
    }

    [RelayCommand]
    private async Task CheckForUpdatesNowAsync()
    {
        CheckingForUpdates  = true;
        UpdateStatusMessage = "Checking for updates...";
        try
        {
            // Force a check regardless of frequency
            _settingsService.Update(s => s.LastUpdateCheck = null);

            var mainVm = AppServices.Get<MainWindowViewModel>();
            await mainVm.CheckForUpdateAsync().ConfigureAwait(false);

            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateStatusMessage = mainVm.UpdateReadyToInstall
                    ? $"v{mainVm.UpdateVersion} downloaded — click \"Install & Restart\" in the banner above."
                    : mainVm.UpdateAvailable
                        ? $"Update available: v{mainVm.UpdateVersion} — see the banner above."
                        : "You're up to date — no newer release found.";
            });
        }
        catch (Exception ex)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                UpdateStatusMessage = $"Check failed: {ex.Message}");
        }
        finally
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                CheckingForUpdates = false);
        }
    }

    [RelayCommand]
    private void SetTheme(string theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        IsLightTheme = theme == "Light";
        _settingsService.Update(s => s.Theme = theme);
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        try
        {
            var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow is null) return;

            if (await DoLoginAsync(mainWindow, clearCookies: true))
            {
                LoadSettings();
                try
                {
                    var galleryVm = AppServices.Get<GalleryViewModel>();
                    await galleryVm.SwitchAccountAsync();
                }
                catch { /* non-fatal */ }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Login failed");
            await _dialogService.ShowMessageAsync("Error", $"Login failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Delegates to <see cref="PixivLoginService"/> which picks the correct
    /// backend (WebView2 on Windows, Playwright Chromium on Linux, manual
    /// cookie only if the primary backend fails). Previously this file
    /// owned ~140 lines of NativeWebDialog plumbing duplicated in
    /// MainWindow.axaml.cs — that's gone now.
    /// </summary>
    private async Task<bool> DoLoginAsync(Window owner, bool clearCookies)
    {
        var login = AppServices.Get<Services.PixivLoginService>();
        var result = await login.LoginAsync(owner, clearCookies);
        return result.Success;
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (!IsSignedIn) return;
        var confirmed = await _dialogService.ShowConfirmationAsync("Sign out", "Clear your session cookie and sign out?");
        if (!confirmed) return;
        _settingsService.Update(s => { s.PhpSessId = string.Empty; s.UserId = null; s.UserName = null; });
        LoadSettings();
    }

    [RelayCommand]
    private async Task ChangeDownloadFolderAsync()
    {
        try
        {
            var folder = await _filePickerService.PickFolderAsync("Select Download Folder");
            if (!string.IsNullOrEmpty(folder))
            {
                _settingsService.Update(s => s.DownloadRoot = folder);
                LoadSettings();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to change download folder");
            await _dialogService.ShowMessageAsync("Error", "Failed to change download folder.");
        }
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationAsync("Reset Settings", "Are you sure you want to reset all settings to defaults?");
        if (confirmed)
        {
            try
            {
                // Simplified for now - will implement actual reset later
                await Task.Delay(500);
                LoadSettings();
                await _dialogService.ShowMessageAsync("Success", "Settings have been reset to defaults (simulated).");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to reset settings");
                await _dialogService.ShowMessageAsync("Error", "Failed to reset settings.");
            }
        }
    }

    // Instant save handlers - triggered when property changes
    partial void OnFilenameTemplateChanged(string value)
    {
        _settingsService.Update(s => s.FilenameTemplate = value);
        OnPropertyChanged(nameof(FilenamePreview));
    }

    partial void OnFolderTemplateChanged(string value)
    {
        _settingsService.Update(s => s.FolderTemplate = value);
        OnPropertyChanged(nameof(FolderPreview));
    }

    partial void OnDateFormatChanged(string value)
    {
        _settingsService.Update(s => s.DateFormat = value);
        OnPropertyChanged(nameof(FilenamePreview));
        OnPropertyChanged(nameof(FolderPreview));
        OnPropertyChanged(nameof(MangaFilenamePreview));
        OnPropertyChanged(nameof(InfoFilenamePreview));
    }

    partial void OnMaxConcurrentDownloadsChanged(int value)
        => _settingsService.Update(s => s.MaxConcurrentDownloads = value);

    partial void OnMaxConcurrentJobsChanged(int value)
        => _settingsService.Update(s => s.MaxConcurrentJobs = value);

    partial void OnSafeModeChanged(bool value)
    {
        _settingsService.Update(s => s.SafeMode = value);
        if (value)
        {
            // Enforce Safe Mode limits immediately in both settings and UI
            MaxConcurrentJobs = 1;
            MaxConcurrentDownloads = 1;
        }
    }

    partial void OnCreateSubfolderPerSubmissionChanged(bool value)
        => _settingsService.Update(s => s.CreateSubfolderPerSubmission = value);

    partial void OnSeparateR18FolderChanged(bool value)
        => _settingsService.Update(s => s.SeparateR18Folder = value);

    partial void OnBlurR18ContentChanged(bool value)
        => _settingsService.Update(s => s.BlurR18Content = value);

    partial void OnBlurIntensityChanged(int value)
        => _settingsService.Update(s => s.BlurIntensity = value);

    partial void OnFilterAiGeneratedChanged(bool value)
        => _settingsService.Update(s => s.FilterAiGenerated = value);

    partial void OnR18ModeChanged(string value)
    {
        if (Enum.TryParse<R18Mode>(value, out var r18Mode))
            _settingsService.Update(s => s.R18Mode = r18Mode);
    }

    partial void OnR18TypeChanged(string value)
    {
        if (Enum.TryParse<R18TypeFilter>(value, out var r18Type))
            _settingsService.Update(s => s.R18Type = r18Type);
    }

    partial void OnPixivLocaleChanged(string value)
        => _settingsService.Update(s => s.Locale = value);

    partial void OnAppLanguageChanged(string value)
    {
        RestartRequired = true;
        _pendingRestartChange = $"Language changed to {value}. Application language requires restart to fully apply.";
    }

    // Manga & Templates
    partial void OnFilenameMangaFormatChanged(string value)
    {
        _settingsService.Update(s => s.FilenameMangaFormat = value);
        OnPropertyChanged(nameof(MangaFilenamePreview));
    }

    partial void OnFilenameInfoFormatChanged(string value)
    {
        _settingsService.Update(s => s.FilenameInfoFormat = value);
        OnPropertyChanged(nameof(InfoFilenamePreview));
    }

    partial void OnTagsSeparatorChanged(string value)
        => _settingsService.Update(s => s.TagsSeparator = value);

    // Metadata Export
    partial void OnWriteImageJSONChanged(bool value)
        => _settingsService.Update(s => s.WriteImageJSON = value);

    partial void OnWriteImageInfoChanged(bool value)
        => _settingsService.Update(s => s.WriteImageInfo = value);

    partial void OnWriteRawJSONChanged(bool value)
        => _settingsService.Update(s => s.WriteRawJSON = value);

    partial void OnIncludeSeriesJSONChanged(bool value)
        => _settingsService.Update(s => s.IncludeSeriesJSON = value);

    partial void OnWriteImageXMPChanged(bool value)
        => _settingsService.Update(s => s.WriteImageXMP = value);

    partial void OnWriteImageXMPPerImageChanged(bool value)
        => _settingsService.Update(s => s.WriteImageXMPPerImage = value);

    partial void OnVerifyImageChanged(bool value)
        => _settingsService.Update(s => s.VerifyImage = value);

    partial void OnSetLastModifiedChanged(bool value)
        => _settingsService.Update(s => s.SetLastModified = value);

    partial void OnUseLocalTimezoneChanged(bool value)
        => _settingsService.Update(s => s.UseLocalTimezone = value);

    // Image Processing
    partial void OnEnableImageProcessingChanged(bool value)
        => _settingsService.Update(s => s.EnableImageProcessing = value);

    partial void OnActiveResizePresetIndexChanged(int value)
    {
        _settingsService.Update(s => s.ActiveResizePreset = (DevicePreset)value);
        OnPropertyChanged(nameof(IsCustomResizePreset));
    }

    partial void OnResizeCustomWidthChanged(int value)
        => _settingsService.Update(s => s.ResizeCustomWidth = value);

    partial void OnResizeCustomHeightChanged(int value)
        => _settingsService.Update(s => s.ResizeCustomHeight = value);

    partial void OnResizeMaintainAspectChanged(bool value)
        => _settingsService.Update(s => s.ResizeMaintainAspect = value);

    partial void OnResizeModeChanged(string value)
        => _settingsService.Update(s => s.ResizeMode = Enum.Parse<ResizeMode>(value));

    partial void OnResizeOutputFormatIndexChanged(int value)
    {
        _settingsService.Update(s => s.ResizeOutputFormat = (ResizeOutputFormat)value);
        OnPropertyChanged(nameof(IsJpegOutputFormat));
    }

    partial void OnResizeJpegQualityChanged(int value)
        => _settingsService.Update(s => s.ResizeJpegQuality = value);

    partial void OnUseCustomProcessingFolderChanged(bool value)
    {
        // Store empty string when disabled, preserve existing path when enabled
        if (!value)
            _settingsService.Update(s => s.ImageProcessingOutputFolder = null);
    }

    partial void OnImageProcessingOutputFolderChanged(string value)
        => _settingsService.Update(s => s.ImageProcessingOutputFolder = value);

    [RelayCommand]
    private async Task BrowseProcessingFolderAsync()
    {
        var folder = await _filePickerService.PickFolderAsync("Select Image Processing Output Folder");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            ImageProcessingOutputFolder = folder;
        }
    }

    [RelayCommand]
    private async Task OpenImageEditorAsync()
    {
        // Open image editor to create a custom preset
        var editor = new Views.Dialogs.ImageEditorWindow();
        await editor.ShowDialog(App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null);
    }

    // Download Control
    partial void OnDownloadAvatarAndBannerChanged(bool value)
        => _settingsService.Update(s => s.DownloadAvatarAndBanner = value);

    partial void OnOverwriteModeChanged(int value)
        => _settingsService.Update(s => s.OverwriteMode = value);

    partial void OnBackupOldFileChanged(bool value)
        => _settingsService.Update(s => s.BackupOldFile = value);

    partial void OnMinFileSizeKBChanged(int value)
        => _settingsService.Update(s => s.MinFileSizeKB = value);

    partial void OnMaxFileSizeKBChanged(int value)
        => _settingsService.Update(s => s.MaxFileSizeKB = value);

    partial void OnDownloadTimeoutChanged(int value)
        => _settingsService.Update(s => s.DownloadTimeout = value);

    partial void OnRetryCountChanged(int value)
        => _settingsService.Update(s => s.RetryCount = value);

    partial void OnRetryDelaySecondsChanged(int value)
        => _settingsService.Update(s => s.RetryDelaySeconds = value);

    partial void OnDownloadDelaySecondsChanged(int value)
        => _settingsService.Update(s => s.DownloadDelaySeconds = value);

    partial void OnDownloadBufferKBChanged(int value)
        => _settingsService.Update(s => s.DownloadBufferKB = value);

    // Blacklist
    partial void OnUseBlacklistTagsRegexChanged(bool value)
        => _settingsService.Update(s => s.UseBlacklistTagsRegex = value);

    partial void OnUseBlacklistTitlesRegexChanged(bool value)
        => _settingsService.Update(s => s.UseBlacklistTitlesRegex = value);

    // Network
    partial void OnUseProxyChanged(bool value)
        => _settingsService.Update(s => s.UseProxy = value);

    partial void OnProxyAddressChanged(string value)
        => _settingsService.Update(s => s.ProxyAddress = value);

    partial void OnEnableSSLVerificationChanged(bool value)
        => _settingsService.Update(s => s.EnableSSLVerification = value);

    partial void OnUseRobotsChanged(bool value)
        => _settingsService.Update(s => s.UseRobots = value);

    // Diagnostics
    partial void OnVerboseLoggingChanged(bool value)
        => _settingsService.Update(s => s.VerboseLogging = value);

    // Accessibility
    partial void OnFontSizeScaleChanged(double value)
        => _settingsService.Update(s => s.FontSizeScale = value);

    partial void OnUseHighContrastChanged(bool value)
        => _settingsService.Update(s => s.UseHighContrast = value);

    partial void OnUseLargeFontsChanged(bool value)
        => _settingsService.Update(s => s.UseLargeFonts = value);

    partial void OnIncreaseSpacingChanged(bool value)
        => _settingsService.Update(s => s.IncreaseSpacing = value);

    partial void OnReduceMotionChanged(bool value)
        => _settingsService.Update(s => s.ReduceMotion = value);

    // Ugoira
    partial void OnCreateUgoiraMp4Changed(bool value)
        => _settingsService.Update(s => s.CreateUgoiraMp4 = value);

    partial void OnCreateUgoiraWebmChanged(bool value)
        => _settingsService.Update(s => s.CreateUgoiraWebm = value);

    partial void OnCreateUgoiraGifChanged(bool value)
        => _settingsService.Update(s => s.CreateUgoiraGif = value);

    partial void OnCreateUgoiraWebpChanged(bool value)
        => _settingsService.Update(s => s.CreateUgoiraWebp = value);

    partial void OnCreateUgoiraApngChanged(bool value)
        => _settingsService.Update(s => s.CreateUgoiraApng = value);

    partial void OnKeepUgoiraZipChanged(bool value)
        => _settingsService.Update(s => s.KeepUgoiraZip = value);

    partial void OnSaveUgoiraFramesChanged(bool value)
        => _settingsService.Update(s => s.SaveUgoiraFrames = value);

    partial void OnUgoiraFramesOnlyChanged(bool value)
        => _settingsService.Update(s => s.UgoiraFramesOnly = value);

    partial void OnFfMpegCodecChanged(string value)
        => _settingsService.Update(s => s.FFmpegCodec = value);

    partial void OnFfMpegCRFChanged(int value)
        => _settingsService.Update(s => s.FFmpegCRF = value);

    // FANBOX
    partial void OnFilenameFanboxCoverChanged(string value)
        => _settingsService.Update(s => s.FilenameFanboxCover = value);

    partial void OnFilenameFanboxContentChanged(string value)
        => _settingsService.Update(s => s.FilenameFanboxContent = value);

    partial void OnFilenameFanboxInfoChanged(string value)
        => _settingsService.Update(s => s.FilenameFanboxInfo = value);

    partial void OnDownloadFanboxCoverWhenRestrictedChanged(bool value)
        => _settingsService.Update(s => s.DownloadFanboxCoverWhenRestricted = value);

    partial void OnWriteFanboxHtmlChanged(bool value)
        => _settingsService.Update(s => s.WriteFanboxHtml = value);

    [RelayCommand]
    private void RestartApplication()
    {
        // Save any pending changes
        _settingsService.Save();

        // Get the current executable path (entry assembly is .dll, need .exe)
        var dllPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(dllPath)) return;

        // Replace .dll with .exe to get the actual executable path
        var exePath = dllPath.Replace(".dll", ".exe", StringComparison.OrdinalIgnoreCase);
        if (!File.Exists(exePath)) return;

        // Start new instance
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true
        };
        System.Diagnostics.Process.Start(psi);

        // Shutdown current instance
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    [RelayCommand]
    private void DismissRestartNotification()
    {
        RestartRequired = false;
        _pendingRestartChange = "";
    }

    [RelayCommand]
    private void SetR18Type(string type)
    {
        if (Enum.TryParse<R18TypeFilter>(type, out var r18Type))
        {
            R18Type = type;
            _settingsService.Update(s => s.R18Type = r18Type);
        }
    }

    [RelayCommand]
    private void SetPixivLocale(string locale)
    {
        PixivLocale = locale;
        _settingsService.Update(s => s.Locale = locale);
    }

    [RelayCommand]
    private void SetAppLanguage(string language)
    {
        AppLanguage = language;
        _settingsService.Update(s => s.AppLanguage = language);
    }

    [RelayCommand]
    private void AddExcludedTag()
    {
        if (string.IsNullOrWhiteSpace(NewExcludedTag)) return;
        var tag = NewExcludedTag.Trim();
        if (!ExcludedTags.Contains(tag))
        {
            ExcludedTags.Add(tag);
            _settingsService.Update(s => s.ExcludedTags = ExcludedTags.ToList());
        }
        NewExcludedTag = "";
    }

    [RelayCommand]
    private void RemoveExcludedTag(string tag)
    {
        if (ExcludedTags.Remove(tag))
            _settingsService.Update(s => s.ExcludedTags = ExcludedTags.ToList());
    }

    // Blacklist management commands
    [RelayCommand] private void AddBlacklistTag()
    {
        if (string.IsNullOrWhiteSpace(NewBlacklistTag)) return;
        var tag = NewBlacklistTag.Trim();
        if (!BlacklistTags.Contains(tag))
        {
            BlacklistTags.Add(tag);
            _settingsService.Update(s => s.BlacklistTags = BlacklistTags.ToList());
        }
        NewBlacklistTag = "";
    }

    [RelayCommand] private void RemoveBlacklistTag(string tag)
    {
        if (BlacklistTags.Remove(tag))
            _settingsService.Update(s => s.BlacklistTags = BlacklistTags.ToList());
    }

    [RelayCommand] private void AddBlacklistTitle()
    {
        if (string.IsNullOrWhiteSpace(NewBlacklistTitle)) return;
        var title = NewBlacklistTitle.Trim();
        if (!BlacklistTitles.Contains(title))
        {
            BlacklistTitles.Add(title);
            _settingsService.Update(s => s.BlacklistTitles = BlacklistTitles.ToList());
        }
        NewBlacklistTitle = "";
    }

    [RelayCommand] private void RemoveBlacklistTitle(string title)
    {
        if (BlacklistTitles.Remove(title))
            _settingsService.Update(s => s.BlacklistTitles = BlacklistTitles.ToList());
    }

    [RelayCommand] private void AddBlacklistMember()
    {
        if (string.IsNullOrWhiteSpace(NewBlacklistMember)) return;
        var member = NewBlacklistMember.Trim();
        if (!BlacklistMembers.Contains(member))
        {
            BlacklistMembers.Add(member);
            _settingsService.Update(s => s.BlacklistMembers = BlacklistMembers.ToList());
        }
        NewBlacklistMember = "";
    }

    [RelayCommand] private void RemoveBlacklistMember(string member)
    {
        if (BlacklistMembers.Remove(member))
            _settingsService.Update(s => s.BlacklistMembers = BlacklistMembers.ToList());
    }

    // FFmpeg commands
    private async Task CheckFfmpegAsync()
    {
        try
        {
            var isAvailable = _ffmpegService.IsAvailable();
            IsFfmpegInstalled = isAvailable;
            if (isAvailable)
            {
                FfmpegStatusText = "Installed";
                var version = await _ffmpegService.ProbeVersionAsync();
                FfmpegVersionText = version != null ? $"Version {version}" : "";
            }
            else
            {
                FfmpegStatusText = "Not installed (required for ugoira conversion)";
                FfmpegVersionText = "";
            }
        }
        catch
        {
            IsFfmpegInstalled = false;
            FfmpegStatusText = "Check failed";
            FfmpegVersionText = "";
        }
    }

    [RelayCommand]
    private async Task CheckFfmpeg()
    {
        FfmpegStatusText = "Checking…";
        await CheckFfmpegAsync();
    }

    [RelayCommand]
    private async Task InstallFfmpeg()
    {
        try
        {
            FfmpegStatusText = "Downloading…";
            var progress = new Progress<string>(msg => FfmpegStatusText = msg);
            var path = await _ffmpegService.InstallAsync(progress);
            IsFfmpegInstalled = true;
            FfmpegStatusText = "Installed";
            var version = await _ffmpegService.ProbeVersionAsync();
            FfmpegVersionText = version != null ? $"Version {version}" : "";
        }
        catch (Exception ex)
        {
            FfmpegStatusText = $"Install failed: {ex.Message}";
            IsFfmpegInstalled = _ffmpegService.IsAvailable();
        }
    }

    // Template preview - generates sample output
    public string FilenamePreview => GenerateFilenamePreview();
    public string FolderPreview => GenerateFolderPreview();
    public string MangaFilenamePreview => GenerateMangaFilenamePreview();
    public string InfoFilenamePreview => GenerateInfoFilenamePreview();

    private string GenerateFilenamePreview()
    {
        var now = DateTime.Now;
        var preview = FilenameTemplate;
        // Core identifiers
        preview = preview.Replace("%image_id%", "12345678");
        preview = preview.Replace("%title%", "Sample Artwork Title");
        preview = preview.Replace("%artist%", "ArtistName");
        preview = preview.Replace("%member_id%", "9876543");
        preview = preview.Replace("%member_token%", "artist_token");
        // Page numbers
        preview = preview.Replace("%page_index%", "0");
        preview = preview.Replace("%page_number%", "1");
        preview = preview.Replace("%page_big%", "big");
        // Dates
        preview = preview.Replace("%date%", now.ToString("yyyyMMdd"));
        preview = preview.Replace("%works_date%", now.ToString(DateFormat));
        preview = preview.Replace("%works_date_only%", now.ToString("yyyy-MM-dd"));
        // Metadata
        preview = preview.Replace("%works_res%", "1920x1080");
        preview = preview.Replace("%works_tools%", "Clip Studio Paint");
        preview = preview.Replace("%R-18%", "R-18");
        preview = preview.Replace("%AI%", "AI");
        preview = preview.Replace("%image_ext%", "jpg");
        preview = preview.Replace("%urlFilename%", "12345678_p0_master1200");
        // Social stats
        preview = preview.Replace("%bookmark%", "Bookmarks");
        preview = preview.Replace("%bookmark_count%", "1234");
        preview = preview.Replace("%image_response_count%", "56");
        // Manga series
        preview = preview.Replace("%manga_series_id%", "876543");
        preview = preview.Replace("%manga_series_order%", "5");
        preview = preview.Replace("%manga_series_title%", "My Manga Series");
        // Bookmark mode
        preview = preview.Replace("%original_member_id%", "111111");
        preview = preview.Replace("%original_member_token%", "original_artist");
        preview = preview.Replace("%original_artist%", "Original Artist");
        preview = preview.Replace("%searchTags%", "tag1 tag2");
        preview = preview.Replace("%tags%", "tag1, tag2, tag3");
        return preview + ".jpg";
    }

    private string GenerateFolderPreview()
    {
        var preview = FolderTemplate;
        preview = preview.Replace("%artist%", "ArtistName");
        preview = preview.Replace("%member_id%", "9876543");
        preview = preview.Replace("%member_token%", "artist_token");
        preview = preview.Replace("%R-18%", "R-18");
        return preview;
    }

    private string GenerateMangaFilenamePreview()
    {
        var now = DateTime.Now;
        var preview = FilenameMangaFormat;
        preview = preview.Replace("%image_id%", "12345678");
        preview = preview.Replace("%title%", "Sample Artwork Title");
        preview = preview.Replace("%artist%", "ArtistName");
        preview = preview.Replace("%member_id%", "9876543");
        preview = preview.Replace("%member_token%", "artist_token");
        preview = preview.Replace("%page_index%", "0");
        preview = preview.Replace("%page_number%", "1");
        preview = preview.Replace("%date%", now.ToString("yyyyMMdd"));
        preview = preview.Replace("%works_date%", now.ToString(DateFormat));
        preview = preview.Replace("%R-18%", "R-18");
        preview = preview.Replace("%image_ext%", "jpg");
        preview = preview.Replace("%urlFilename%", "12345678_p0");
        preview = preview.Replace("%tags%", "tag1, tag2");
        return preview + ".jpg";
    }

    private string GenerateInfoFilenamePreview()
    {
        var now = DateTime.Now;
        var preview = FilenameInfoFormat;
        preview = preview.Replace("%image_id%", "12345678");
        preview = preview.Replace("%title%", "Sample Artwork Title");
        preview = preview.Replace("%artist%", "ArtistName");
        preview = preview.Replace("%member_id%", "9876543");
        preview = preview.Replace("%member_token%", "artist_token");
        preview = preview.Replace("%date%", now.ToString("yyyyMMdd"));
        preview = preview.Replace("%works_date%", now.ToString(DateFormat));
        preview = preview.Replace("%R-18%", "R-18");
        preview = preview.Replace("%urlFilename%", "12345678_p0");
        return preview;
    }

    // Gallery Viewer Keyboard Navigation
    partial void OnGalleryKeyboardNavEnabledChanged(bool value)
        => _settingsService.Update(s => s.GalleryKeyboardNavEnabled = value);

    partial void OnFullscreenKeyboardNavEnabledChanged(bool value)
        => _settingsService.Update(s => s.FullscreenKeyboardNavEnabled = value);

    // Startup & System Tray - Instant save handlers
    partial void OnStartWithWindowsChanged(bool value)
    {
        _settingsService.Update(s => s.StartWithWindows = value);
        StartupHelper.SetStartupEnabled(value);

        // Update startup command if enabling
        if (value)
        {
            StartupHelper.UpdateStartupCommand(
                StartMinimizedToTray,
                MinimizeToTray && (CloseToTray || StartMinimizedToTray));
        }
    }

    partial void OnStartupWindowStateChanged(string value)
        => _settingsService.Update(s => s.StartupWindowState = value);

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settingsService.Update(s => s.MinimizeToTray = value);

        // Update registry if we're set to start with Windows
        if (StartWithWindows)
        {
            StartupHelper.UpdateStartupCommand(
                StartMinimizedToTray,
                value && (CloseToTray || StartMinimizedToTray));
        }

        // If disabling minimize to tray, also disable dependent options
        if (!value)
        {
            if (CloseToTray) CloseToTray = false;
            if (StartMinimizedToTray) StartMinimizedToTray = false;
        }
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        _settingsService.Update(s => s.CloseToTray = value);

        // Update registry if we're set to start with Windows
        if (StartWithWindows && MinimizeToTray)
        {
            StartupHelper.UpdateStartupCommand(
                StartMinimizedToTray,
                MinimizeToTray && (value || StartMinimizedToTray));
        }
    }

    partial void OnStartMinimizedToTrayChanged(bool value)
    {
        _settingsService.Update(s => s.StartMinimizedToTray = value);

        // Update registry if we're set to start with Windows
        if (StartWithWindows && MinimizeToTray)
        {
            StartupHelper.UpdateStartupCommand(
                value,
                MinimizeToTray && (CloseToTray || value));
        }
    }

    partial void OnKeepSchedulesRunningInBackgroundChanged(bool value)
    {
        _settingsService.Update(s => s.KeepSchedulesRunningInBackground = value);
        // Enabling background schedules implies close-to-tray behaviour
        if (value && !CloseToTray)
            CloseToTray = true;
    }

    partial void OnShowScheduleNotificationsChanged(bool value)
        => _settingsService.Update(s => s.ShowScheduleNotifications = value);

    partial void OnNotifyOnDownloadCompleteChanged(bool value)
        => _settingsService.Update(s => s.NotifyOnDownloadComplete = value);

    partial void OnNotifyOnDownloadStartedChanged(bool value)
        => _settingsService.Update(s => s.NotifyOnDownloadStarted = value);

    partial void OnNotifyOnDownloadFailedChanged(bool value)
        => _settingsService.Update(s => s.NotifyOnDownloadFailed = value);

    partial void OnNotifyOnDownloadPausedChanged(bool value)
        => _settingsService.Update(s => s.NotifyOnDownloadPaused = value);

    // ── Hoshi AI Model Management ────────────────────────────────────────────
    partial void OnUseCustomHoshiModelsChanged(bool value)
        => _settingsService.Update(s => s.UseCustomHoshiModels = value);

    /// <summary>
    /// Custom text model name. Setter ignores blank/whitespace assignments entirely (not just for
    /// persistence, but for the in-memory field itself) — the Text model ComboBox binds SelectedItem
    /// directly to this property, and its backing list (InstalledOllamaModelNames) starts empty until
    /// RefreshInstalledModelsAsync finishes. Avalonia deselects a ComboBox whose SelectedItem doesn't
    /// match anything in an empty list and pushes an empty value back through the TwoWay binding —
    /// without this guard, that transient pushback would wipe the saved selection before the list
    /// populates, and nothing would ever restore it.
    /// </summary>
    public string HoshiTextModel
    {
        get => _hoshiTextModel;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (_hoshiTextModel == value) return;
            _hoshiTextModel = value;
            OnPropertyChanged();
            UseCustomHoshiModels = true;
            _settingsService.Update(s => s.HoshiTextModel = value);
        }
    }

    /// <summary>Custom vision model name. See <see cref="HoshiTextModel"/> for why blanks are ignored.</summary>
    public string HoshiVisionModel
    {
        get => _hoshiVisionModel;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (_hoshiVisionModel == value) return;
            _hoshiVisionModel = value;
            OnPropertyChanged();
            UseCustomHoshiModels = true;
            _settingsService.Update(s => s.HoshiVisionModel = value);
        }
    }

    partial void OnHoshiUseAnimeTaggerChanged(bool value)
        => _settingsService.Update(s => s.HoshiUseAnimeTagger = value);

    /// <summary>Active anime tagger model key. See <see cref="HoshiTextModel"/> for why blanks are ignored
    /// (the Tagger ComboBox has the identical binding pattern against InstalledTaggerModelNames).</summary>
    public string HoshiAnimeTaggerModel
    {
        get => _hoshiAnimeTaggerModel;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (_hoshiAnimeTaggerModel == value) return;
            _hoshiAnimeTaggerModel = value;
            OnPropertyChanged();
            RefreshSelectedTaggerInstallState();
            _settingsService.Update(s => s.HoshiAnimeTaggerModel = value);
        }
    }

    partial void OnHoshiAnimeTaggerThresholdChanged(double value)
        => _settingsService.Update(s => s.HoshiAnimeTaggerThreshold = value);

    partial void OnHoshiAnimeTaggerMaxTagsChanged(int value)
        => _settingsService.Update(s => s.HoshiAnimeTaggerMaxTags = value);

    partial void OnHoshiAnimeTaggerAutoTagDownloadsChanged(bool value)
        => _settingsService.Update(s => s.HoshiAnimeTaggerAutoTagDownloads = value);

    /// <summary>Refresh the list of installed Ollama models. Non-blocking (runs on a background task).</summary>
    [RelayCommand]
    private async Task RefreshInstalledModelsAsync()
    {
        if (IsLoadingModels) return;
        IsLoadingModels = true;
        ModelManagementError = string.Empty;
        try
        {
            var list = await Task.Run(() => _ollama.ListInstalledModelsAsync());
            InstalledModels.Clear();
            InstalledOllamaModels.Clear();
            var ollamaNames = new List<string>();
            var visionNames = new List<string>();
            foreach (var m in list)
            {
                var row = new InstalledModelRow("Ollama", m.Name, m.SizeBytes, m.ModifiedAt);
                InstalledModels.Add(row);
                InstalledOllamaModels.Add(row);
                ollamaNames.Add(m.Name);
                if (m.SupportsVision)
                    visionNames.Add(m.Name);
            }

            // Anime taggers get their own separate list — kept apart from Ollama models to avoid
            // confusion, since they're a different kind of model (ONNX, not chat/vision).
            InstalledTaggerModels.Clear();
            var taggerNames = new List<string>();
            foreach (var model in KnownAnimeTaggerModels.All.Where(AnimeTaggerService.IsModelInstalled))
            {
                long sizeBytes = 0;
                DateTime modifiedAt = DateTime.MinValue;
                var dir = AnimeTaggerService.GetModelDirectory(model);
                if (Directory.Exists(dir))
                {
                    var di = new DirectoryInfo(dir);
                    foreach (var file in di.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        sizeBytes += file.Length;
                        if (file.LastWriteTimeUtc > modifiedAt)
                            modifiedAt = file.LastWriteTimeUtc;
                    }
                }
                var row = new InstalledModelRow("Anime tagger", model.Name, sizeBytes, modifiedAt);
                InstalledModels.Add(row);
                InstalledTaggerModels.Add(row);
                taggerNames.Add(model.Key);
            }

            // Make sure the currently selected active models appear in the dropdown even if not installed.
            if (!string.IsNullOrWhiteSpace(HoshiTextModel) && !ollamaNames.Contains(HoshiTextModel))
                ollamaNames.Add(HoshiTextModel);
            if (!string.IsNullOrWhiteSpace(HoshiVisionModel) && !visionNames.Contains(HoshiVisionModel))
                visionNames.Add(HoshiVisionModel);
            if (!string.IsNullOrWhiteSpace(HoshiAnimeTaggerModel) && !taggerNames.Contains(HoshiAnimeTaggerModel))
                taggerNames.Add(HoshiAnimeTaggerModel);

            // Sync in place (never Clear()) so ComboBoxes bound to these collections don't
            // transiently deselect — that would wipe out the Text/Vision/Tagger selection
            // via the OnHoshi*ModelChanged handlers whenever this refresh runs.
            SyncNameCollection(InstalledOllamaModelNames, ollamaNames);
            SyncNameCollection(InstalledVisionModelNames, visionNames);
            SyncNameCollection(InstalledTaggerModelNames, taggerNames);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to list Ollama models");
            ModelManagementError = $"Could not list models: {ex.Message}";
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    /// <summary>Downloads a model in the background, updating progress without freezing the UI.</summary>
    [RelayCommand]
    private async Task PullModelAsync()
    {
        var name = (ModelToInstall ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (IsPullingModel) return;

        IsPullingModel = true;
        ModelPullPercent = 0;
        ModelPullStatus = $"Starting download of {name}…";
        ModelManagementError = string.Empty;

        _modelPullCts?.Cancel();
        _modelPullCts?.Dispose();
        _modelPullCts = new CancellationTokenSource();
        var ct = _modelPullCts.Token;

        try
        {
            // Run the pull on a background thread; marshal progress back via Dispatcher.
            await Task.Run(async () =>
            {
                await foreach (var p in _ollama.PullManualAsync(name, ct))
                {
                    var statusText = p.Total > 0
                        ? $"{p.Status} — {FormatBytes(p.Completed)} / {FormatBytes(p.Total)} ({p.Percent:0.0}%)"
                        : p.Status;
                    var percent = p.Percent;
                    await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ModelPullStatus = statusText;
                        ModelPullPercent = percent;
                    });
                }
            }, ct);

            ModelPullStatus = $"Installed {name}";
            ModelPullPercent = 100;
            await RefreshInstalledModelsAsync();
        }
        catch (OperationCanceledException)
        {
            ModelPullStatus = "Download cancelled.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to pull model {Name}", name);
            ModelManagementError = $"Download failed: {ex.Message}";
            ModelPullStatus = string.Empty;
        }
        finally
        {
            IsPullingModel = false;
        }
    }

    /// <summary>Cancels an in-progress model download.</summary>
    [RelayCommand]
    private void CancelModelPull()
    {
        try { _modelPullCts?.Cancel(); }
        catch { /* ignore */ }
    }

    /// <summary>Sets the ModelToInstall field to a suggested model name (used by chip buttons).</summary>
    [RelayCommand]
    private void SelectSuggestedModel(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) ModelToInstall = name.Trim();
    }

    /// <summary>Uninstalls (deletes) a model.</summary>
    [RelayCommand]
    private async Task DeleteModelAsync(InstalledModelRow? row)
    {
        if (row == null) return;
        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Uninstall model?",
            $"Remove \"{row.Name}\" from your machine? You can reinstall it any time.");
        if (!confirmed) return;

        try
        {
            if (row.IsOllama)
            {
                await Task.Run(() => _ollama.DeleteModelAsync(row.Name));
            }
            else if (row.IsAnimeTagger)
            {
                var model = KnownAnimeTaggerModels.All.FirstOrDefault(m => m.Name == row.Name);
                if (model != null)
                {
                    var dir = AnimeTaggerService.GetModelDirectory(model);
                    await Task.Run(() => { if (Directory.Exists(dir)) Directory.Delete(dir, true); });
                }
            }
            await RefreshInstalledModelsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete model {Name}", row.Name);
            ModelManagementError = $"Could not delete {row.Name}: {ex.Message}";
        }
    }

    /// <summary>Sets the active text or vision model from an installed model row.</summary>
    [RelayCommand]
    private void UseModelForText(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        UseCustomHoshiModels = true;
        HoshiTextModel = name;
    }

    [RelayCommand]
    private void UseModelForVision(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        UseCustomHoshiModels = true;
        HoshiVisionModel = name;
    }

    /// <summary>Sets the active anime tagger model from an installed model row.</summary>
    [RelayCommand]
    private void UseModelForTagger(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var model = KnownAnimeTaggerModels.All.FirstOrDefault(m => m.Name == name);
        if (model == null) return;
        HoshiUseAnimeTagger = true;
        HoshiAnimeTaggerModel = model.Key;
    }

    /// <summary>Populates the unified recommended models list with Ollama and ONNX tagger choices.</summary>
    private void InitializeRecommendedModels()
    {
        RecommendedModels.Clear();

        AddRecommendedOllama("Text / chat", "llama3.2", "Lightweight general-purpose 3B instruction model. Good starting point for chat.");
        AddRecommendedOllama("Text / chat", "qwen2.5", "Alibaba's Qwen2.5 instruction series with strong multilingual support.");
        AddRecommendedOllama("Text / chat", "gemma2", "Google's Gemma 2 instruction models.");
        AddRecommendedOllama("Text / chat", "mistral", "Mistral instruction model with good reasoning.");

        AddRecommendedOllama("Vision", "llava", "Vision-language model that can describe images.");
        AddRecommendedOllama("Vision", "moondream", "Small vision-language model tuned for short, fast image captions.");
        AddRecommendedOllama("Vision", "bakllava", "BakLLaVA vision model based on Mistral.");
        AddRecommendedOllama("Vision", "llava-phi3", "Compact Phi-3 based vision model.");
        AddRecommendedOllama("Vision", "qwen2.5-vl", "Qwen vision-language model with strong detail recognition.");
        AddRecommendedOllama("Vision", "gemma4", "Google Gemma 4 vision model.");

        foreach (var model in KnownAnimeTaggerModels.All)
            RecommendedModels.Add(new RecommendedModelRow(model));
    }

    private void AddRecommendedOllama(string category, string name, string description)
        => RecommendedModels.Add(new RecommendedModelRow(category, name, description));

    /// <summary>Installs a recommended model (Ollama or ONNX tagger) from a chip click.</summary>
    [RelayCommand]
    private async Task InstallRecommendedModelAsync(RecommendedModelRow? row)
    {
        if (row == null) return;

        if (row.IsOllama)
        {
            if (string.IsNullOrWhiteSpace(row.OllamaName)) return;
            ModelToInstall = row.OllamaName.Trim();
            await PullModelAsync();
            return;
        }

        if (row.TaggerModel is { } model)
            await InstallTaggerModelAsync(row, model);
    }

    /// <summary>Installs the tagger model currently selected in the Anime tagger section.</summary>
    [RelayCommand]
    private async Task InstallSelectedTaggerModelAsync()
    {
        var model = KnownAnimeTaggerModels.GetByKey(HoshiAnimeTaggerModel);
        if (model == null) return;

        var row = RecommendedModels.FirstOrDefault(r => r.TaggerModel?.Key == model.Key);
        if (row == null) return;

        await InstallTaggerModelAsync(row, model);
    }

    /// <summary>Refreshes the installed flag on each recommended tagger model row.</summary>
    private void RefreshTaggerInstallState()
    {
        var taggerNames = new List<string>();
        foreach (var row in RecommendedModels.Where(r => !r.IsOllama && r.TaggerModel != null))
        {
            row.IsInstalled = AnimeTaggerService.IsModelInstalled(row.TaggerModel);
            if (row.IsInstalled)
                taggerNames.Add(row.TaggerModel!.Key);
        }
        if (!string.IsNullOrWhiteSpace(HoshiAnimeTaggerModel) && !taggerNames.Contains(HoshiAnimeTaggerModel))
            taggerNames.Add(HoshiAnimeTaggerModel);
        // Sync in place (never Clear()) — this runs on every settings change (e.g. picking a
        // different Text/Vision model triggers LoadSettings -> RefreshTaggerInstallState), so
        // clearing the collection would transiently deselect the Tagger ComboBox and wipe it.
        SyncNameCollection(InstalledTaggerModelNames, taggerNames);
        RefreshSelectedTaggerInstallState();
    }

    /// <summary>
    /// Updates <paramref name="target"/> to contain exactly <paramref name="desired"/> without ever
    /// clearing it — removes stale entries and appends new ones in place. Avoids the transient
    /// empty state a Clear()+re-Add() would cause, which deselects any ComboBox bound to it and
    /// wipes out the underlying saved selection via the OnHoshi*ModelChanged handlers.
    /// </summary>
    private static void SyncNameCollection(ObservableCollection<string> target, IReadOnlyList<string> desired)
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (!desired.Contains(target[i])) target.RemoveAt(i);
        foreach (var name in desired)
            if (!target.Contains(name)) target.Add(name);
    }

    /// <summary>Updates whether the currently selected anime tagger model is installed.</summary>
    private void RefreshSelectedTaggerInstallState()
    {
        var model = KnownAnimeTaggerModels.GetByKey(HoshiAnimeTaggerModel);
        IsSelectedTaggerModelInstalled = model != null && AnimeTaggerService.IsModelInstalled(model);
    }

    /// <summary>Downloads an ONNX anime tagger model from Hugging Face.</summary>
    private async Task InstallTaggerModelAsync(RecommendedModelRow row, TaggerModelInfo model)
    {
        IsInstallingTaggerModel = true;
        TaggerInstallPercent = 0;
        TaggerInstallStatus = $"Downloading {model.Name}…";
        TaggerInstallError = string.Empty;

        _taggerInstallCts?.Cancel();
        _taggerInstallCts?.Dispose();
        _taggerInstallCts = new CancellationTokenSource();
        var ct = _taggerInstallCts.Token;
        row.IsInstalling = true;

        try
        {
            var progress = new Progress<double>(p =>
            {
                TaggerInstallPercent = p * 100.0;
                TaggerInstallStatus = $"Downloading {model.Name}… {p:0%}";
            });

            var ok = await Task.Run(() => _animeTagger.EnsureModelInstalledAsync(model, progress, ct), ct);
            if (ok)
            {
                TaggerInstallStatus = $"Installed {model.Name}";
                TaggerInstallPercent = 100;
                RefreshTaggerInstallState();
            }
            else
            {
                TaggerInstallError = "Download did not complete. Check the log for details.";
                TaggerInstallStatus = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            TaggerInstallStatus = "Download cancelled.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to install tagger model {Key}", model.Key);
            TaggerInstallError = $"Download failed: {ex.Message}";
            TaggerInstallStatus = string.Empty;
        }
        finally
        {
            IsInstallingTaggerModel = false;
            if (row != null) row.IsInstalling = false;
        }
    }

    /// <summary>Cancels an in-progress tagger model download.</summary>
    [RelayCommand]
    private void CancelTaggerInstall()
    {
        try { _taggerInstallCts?.Cancel(); }
        catch { /* ignore */ }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var order = (int)Math.Floor(Math.Log(bytes, 1024));
        order = Math.Min(order, units.Length - 1);
        var value = bytes / Math.Pow(1024, order);
        return $"{value:0.##} {units[order]}";
    }
}

/// <summary>Row in the installed models list shown in Settings.</summary>
public sealed class InstalledModelRow
{
    public string Category { get; }
    public string Name { get; }
    public long SizeBytes { get; }
    public DateTime ModifiedAt { get; }
    public string DisplaySize { get; }
    public string DisplayModified { get; }
    public bool IsOllama { get; }
    public bool IsAnimeTagger { get; }

    public InstalledModelRow(string category, string name, long sizeBytes, DateTime modifiedAt)
    {
        Category = category;
        Name = name;
        SizeBytes = sizeBytes;
        ModifiedAt = modifiedAt;
        DisplaySize = FormatBytes(sizeBytes);
        DisplayModified = modifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        IsOllama = category == "Ollama";
        IsAnimeTagger = category == "Anime tagger";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var order = (int)Math.Floor(Math.Log(bytes, 1024));
        order = Math.Min(order, units.Length - 1);
        var value = bytes / Math.Pow(1024, order);
        return $"{value:0.##} {units[order]}";
    }
}

/// <summary>Row in the unified recommended models list (Ollama or ONNX tagger).</summary>
public sealed partial class RecommendedModelRow : ObservableObject
{
    public string Name { get; }
    public string Category { get; }
    public string Description { get; }
    public bool IsOllama { get; }
    public string? OllamaName { get; }
    public TaggerModelInfo? TaggerModel { get; }
    public long? EstimatedBytes { get; }

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isInstalling;

    public RecommendedModelRow(TaggerModelInfo model)
    {
        Name = model.Name;
        Category = "Anime tagging";
        Description = model.Description;
        TaggerModel = model;
        EstimatedBytes = model.EstimatedBytes;
    }

    public RecommendedModelRow(string category, string ollamaName, string description)
    {
        Name = ollamaName;
        Category = category;
        Description = description;
        IsOllama = true;
        OllamaName = ollamaName;
        EstimatedBytes = null;
    }

    public string DisplaySize => EstimatedBytes is long b && b > 0 ? FormatBytes(b) : string.Empty;

    public string ToolTipText
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine(Category);
            sb.AppendLine();
            sb.Append(Description);
            if (!string.IsNullOrEmpty(DisplaySize))
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.Append($"Estimated size: {DisplaySize}");
            }
            return sb.ToString();
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var order = (int)Math.Floor(Math.Log(bytes, 1024));
        order = Math.Min(order, units.Length - 1);
        var value = bytes / Math.Pow(1024, order);
        return $"{value:0.##} {units[order]}";
    }
}
