using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Avalonia.Services;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly NavigationService _navigationService;
    private readonly SettingsService _settingsService;
    private readonly UpdateCheckService _updateCheck;
    private readonly ILogger<MainWindowViewModel> _logger;
    private ContentControl? _mainContentControl;

    public BackgroundOverlayService OverlayService { get; }

    [ObservableProperty] private string _sidebarUserName    = "Guest User";
    [ObservableProperty] private string _sidebarUserStatus  = "Not signed in";
    [ObservableProperty] private string _sidebarUserInitial = "G";

    /// <summary>Mirrors <see cref="AppSettings.IncognitoModeEnabled"/> so a persistent indicator can be shown from any tab, not just the Viewed tab.</summary>
    [ObservableProperty] private bool _incognitoModeEnabled;
    [ObservableProperty] private bool   _updateAvailable;
    [ObservableProperty] private string _updateVersion      = string.Empty;
    [ObservableProperty] private string _updateUrl          = string.Empty;
    [ObservableProperty] private string? _updateDownloadUrl;
    [ObservableProperty] private bool   _updateDownloading;
    [ObservableProperty] private int    _updateDownloadProgress;
    [ObservableProperty] private bool   _updateReadyToInstall;
    [ObservableProperty] private string _updateStatusText   = string.Empty;

    private UpdateInfo? _pendingUpdate;
    private string?     _downloadedPath;
    private System.Threading.CancellationTokenSource? _downloadCts;

    [ObservableProperty] private bool   _changelogAvailable;
    [ObservableProperty] private string _changelogVersion     = string.Empty;
    [ObservableProperty] private string _changelogNotes       = string.Empty;
    [ObservableProperty] private string _changelogReleaseUrl  = string.Empty;

    // Polls the update endpoint periodically while the app is running. ShouldCheck()
    // inside UpdateCheckService still honours the user's Daily/Weekly/Never setting,
    // so this just wakes up often enough to notice when a check is due — it doesn't
    // actually hit GitHub every interval.
    private static readonly TimeSpan UpdateCheckPollInterval = TimeSpan.FromHours(6);
    private System.Threading.Timer? _updateCheckTimer;

    public MainWindowViewModel(NavigationService navigationService, SettingsService settingsService, UpdateCheckService updateCheck, ILogger<MainWindowViewModel> logger, BackgroundOverlayService overlayService)
    {
        _navigationService = navigationService;
        _settingsService   = settingsService;
        _updateCheck       = updateCheck;
        _logger            = logger;
        OverlayService     = overlayService;
        Title = "Pikura";
        RefreshUserChip();
        _settingsService.Changed += (_, _) => RefreshUserChip();
        IncognitoModeEnabled = _settingsService.ActiveIncognitoEnabled;
        _settingsService.ActiveIncognitoChanged += (_, _) =>
        {
            var incognito = _settingsService.ActiveIncognitoEnabled;
            if (IncognitoModeEnabled != incognito) IncognitoModeEnabled = incognito;
        };
        _ = Task.Run(CheckForUpdateAsync);
        _ = Task.Run(CheckChangelogAsync);
        _ = Task.Run(RestoreDownloadedUpdateAsync);

        // Re-check periodically so long-running sessions notice new releases without
        // the user opening Settings → Check Now. UpdateCheckService.ShouldCheck()
        // applies the Daily/Weekly/Never throttle internally.
        _updateCheckTimer = new System.Threading.Timer(
            _ =>
            {
                // Skip if a banner is already showing — re-checking would clobber the
                // user's current dismiss/download state.
                if (UpdateAvailable || UpdateDownloading || UpdateReadyToInstall) return;
                _ = Task.Run(CheckForUpdateAsync);
            },
            null,
            UpdateCheckPollInterval,
            UpdateCheckPollInterval);
    }

    /// <summary>
    /// If the app version is newer than LastSeenVersion, fetch that release's notes
    /// from the GitHub API and signal the UI to show the changelog popup.
    /// </summary>
    public async Task CheckChangelogAsync()
    {
        try
        {
            var current = UpdateCheckService.CurrentVersion;
            var lastSeen = _settingsService.Current.LastSeenVersion;

            // Mark seen immediately so we don't show it again on next launch
            _settingsService.Update(s => s.LastSeenVersion = current);

            // First-ever launch or same version — nothing to show.
            // Use SemVer-aware compare so prerelease tags (e.g. 1.7.0-beta.1)
            // don't bypass the changelog when running upgrade-from-prerelease.
            if (string.IsNullOrEmpty(lastSeen)) return;
            if (UpdateCheckService.CompareSemVer(current, lastSeen) <= 0) return;

            // Fetch release notes for the current version tag; fall back to local notes
            // when the GitHub release tag doesn't exist yet (e.g. freshly bumped version).
            var notes = await _updateCheck.FetchReleaseNotesAsync(current).ConfigureAwait(false);
            notes ??= GetLocalReleaseNotes(current);
            if (notes is not { } releaseNotes) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ChangelogVersion    = releaseNotes.Version;
                ChangelogNotes      = releaseNotes.ReleaseNotes;
                ChangelogReleaseUrl = releaseNotes.ReleasePageUrl;
                ChangelogAvailable  = true;
            });
        }
        catch { /* non-fatal */ }
    }

    [RelayCommand]
    private void DismissChangelog() => ChangelogAvailable = false;

    // Version we last surfaced an OS toast for — prevents the periodic poll from
    // spamming a notification every 6 hours for the same release.
    private string? _lastNotifiedVersion;

    /// <summary>
    /// Scans the temp folder for a previously-downloaded update file and restores the
    /// "ready to install" state without requiring CheckAsync to succeed. This ensures
    /// the Install & Restart button works even when the update-check is throttled.
    /// </summary>
    private async Task RestoreDownloadedUpdateAsync()
    {
        try
        {
            var tempDir = System.IO.Path.GetTempPath();
            // Match any Pikura-update-X.Y.Z.exe in temp
            var file = System.IO.Directory
                .EnumerateFiles(tempDir, "Pikura-update-*.exe")
                .OrderByDescending(f => System.IO.File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            if (file is null) return;

            // Extract version from filename: Pikura-update-1.6.4.exe → 1.6.4
            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            var ver  = name.Replace("Pikura-update-", "").Trim();
            if (string.IsNullOrEmpty(ver)) return;

            // Only restore if this version is newer than current
            if (UpdateCheckService.CompareSemVer(ver, UpdateCheckService.CurrentVersion) <= 0) return;

            _downloadedPath = file;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Only set if nothing else has already shown a banner
                if (UpdateAvailable || UpdateDownloading || UpdateReadyToInstall) return;
                UpdateVersion        = ver;
                UpdateReadyToInstall = true;
                UpdateStatusText     = $"v{ver} ready to install";
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RestoreDownloadedUpdate failed (non-fatal)");
        }
    }

    public async Task CheckForUpdateAsync()
    {
        var info = await _updateCheck.CheckAsync().ConfigureAwait(false);
        if (info is null) return;

        var settings = AppServices.Get<SettingsService>();
        var shouldToast = settings.Current.NotifyOnUpdate
                          && _lastNotifiedVersion != info.Version;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _pendingUpdate       = info;
            UpdateVersion        = info.Version;
            UpdateUrl            = info.ReleasePageUrl;
            UpdateDownloadUrl    = info.DownloadUrl;
            UpdateReadyToInstall = false;
            UpdateDownloading    = false;
            UpdateStatusText     = string.Empty;

            if (settings.Current.NotifyOnUpdate)
                UpdateAvailable = true;

                // Restore previously downloaded file if it still exists in temp
            var expectedPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Pikura-update-{info.Version}{System.IO.Path.GetExtension(info.DownloadUrl?.Split('?')[0] ?? ".exe")}");
            if (System.IO.File.Exists(expectedPath))
            {
                _downloadedPath      = expectedPath;
                UpdateReadyToInstall = true;
                UpdateAvailable      = false;
                UpdateStatusText     = $"v{info.Version} ready to install";
            }
            else if (settings.Current.AutoDownloadUpdates && !string.IsNullOrEmpty(info.DownloadUrl))
                _ = Task.Run(StartDownloadAsync);
        });

        // OS toast — only fire once per detected version so the periodic poll
        // doesn't keep popping notifications while the user has the app open.
        if (shouldToast)
        {
            _lastNotifiedVersion = info.Version;
            try
            {
                var notifier = AppServices.Get<NotificationService>();
                notifier.ShowNotification(
                    "Pikura update available",
                    $"v{info.Version} is ready to download. See the banner at the top of the window.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Update toast notification failed (non-fatal)");
            }
        }
    }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        // Don't dismiss the banner — the user is just reading the notes, not
        // acknowledging the update. They should still be able to click Download
        // (or X to dismiss) afterwards.
        if (!string.IsNullOrEmpty(UpdateUrl))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateUrl) { UseShellExecute = true });
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        UpdateAvailable      = false;
        UpdateReadyToInstall = false;
        UpdateDownloading    = false;
        _downloadCts?.Cancel();
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (_pendingUpdate is null || string.IsNullOrWhiteSpace(_pendingUpdate.DownloadUrl)) return;
        UpdateAvailable = false;
        await StartDownloadAsync();
    }

    private async Task StartDownloadAsync()
    {
        if (_pendingUpdate is null) return;
        _downloadCts = new System.Threading.CancellationTokenSource();
        var progress = new Progress<int>(p => Dispatcher.UIThread.Post(() =>
        {
            UpdateDownloadProgress = p;
            UpdateStatusText       = $"Downloading update... {p}%";
        }));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            UpdateDownloading    = true;
            UpdateReadyToInstall = false;
            UpdateStatusText     = "Starting download...";
        });

        try
        {
            _downloadedPath = await _updateCheck
                .DownloadUpdateAsync(_pendingUpdate, progress, _downloadCts.Token)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateDownloading    = false;
                UpdateReadyToInstall = true;
                UpdateStatusText     = $"v{_pendingUpdate.Version} ready to install";
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateDownloading = false;
                UpdateStatusText  = "Download cancelled.";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateDownloading = false;
                UpdateStatusText  = $"Download failed: {ex.Message}";
            });
        }
    }

    [RelayCommand]
    private async Task InstallAndRestartUpdate() => await InstallAndRestartAsync();

    public async Task InstallAndRestartAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => UpdateStatusText = "Starting installer...");

            // Try to recover _downloadedPath from temp using the version shown in the banner.
            // UpdateVersion may or may not have a leading 'v' — strip it for the filename match.
            if ((_downloadedPath is null || !System.IO.File.Exists(_downloadedPath))
                && !string.IsNullOrEmpty(UpdateVersion))
            {
                var ver     = UpdateVersion.TrimStart('v');
                var tempDir = System.IO.Path.GetTempPath();
                var recovered = System.IO.Directory
                    .EnumerateFiles(tempDir, $"Pikura-update-{ver}*")
                    .FirstOrDefault();
                if (recovered != null)
                    _downloadedPath = recovered;
            }

            // If still missing, re-download
            if (_downloadedPath is null || !System.IO.File.Exists(_downloadedPath))
            {
                if (_pendingUpdate is null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        UpdateStatusText = "Cannot install: update info lost. Please restart the app and try again.");
                    return;
                }
                await StartDownloadAsync();
                if (_downloadedPath is null) return;
            }

            await _updateCheck.InstallAndRestartAsync(_downloadedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InstallAndRestartUpdate failed");
            await Dispatcher.UIThread.InvokeAsync(() =>
                UpdateStatusText = $"Install failed: {ex.Message}");
        }
    }

    private void RefreshUserChip()
    {
        var s = _settingsService.Current;
        SidebarUserName    = s.IsConfigured ? (s.UserName ?? s.UserId ?? "Pixiv User") : "Guest User";
        SidebarUserStatus  = s.IsConfigured ? $"ID: {s.UserId}" : "Not signed in";
        SidebarUserInitial = string.IsNullOrEmpty(SidebarUserName) ? "G" : SidebarUserName[0].ToString().ToUpper();
    }

    public string Title { get; }

    public void SetMainContentControl(ContentControl contentControl)
    {
        _mainContentControl = contentControl;
    }

    [RelayCommand]
    private void NavigateToGallery()
    {
        try
        {
            if (_mainContentControl != null)
            {
                var galleryView = new Pikura.Avalonia.Views.Gallery.GalleryView();
                _mainContentControl.Content = galleryView;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NavigateToGallery failed");
        }
    }

    [RelayCommand]
    private void NavigateToRankings()
    {
        try
        {
            if (_mainContentControl != null)
            {
                var rankingsView = new Pikura.Avalonia.Views.RankingsView();
                _mainContentControl.Content = rankingsView;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NavigateToRankings failed");
        }
    }

    [RelayCommand]
    private void NavigateToDownloads()
    {
        try
        {
            if (_mainContentControl != null)
            {
                var downloadsView = new Pikura.Avalonia.Views.DownloadsView();
                _mainContentControl.Content = downloadsView;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NavigateToDownloads failed");
        }
    }

    [RelayCommand]
    private void NavigateToHistory()
    {
        try
        {
            if (_mainContentControl != null)
            {
                var historyView = new Pikura.Avalonia.Views.History.HistoryView();
                _mainContentControl.Content = historyView;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NavigateToHistory failed");
        }
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        try
        {
            if (_mainContentControl != null)
            {
                var settingsView = new Pikura.Avalonia.Views.Settings.SettingsView();
                _mainContentControl.Content = settingsView;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NavigateToSettings failed");
        }
    }

    public bool IsConfigured => _settingsService.Current.IsConfigured;

    public static UpdateInfo? GetLocalReleaseNotes(string version) => version switch
    {
        "2.1.0" => new UpdateInfo(
            "2.1.0",
            "Pikura 2.1.0",
            """
            ## 2.1.0

            ### New Features
            - **Collections** — full support for Pixiv's collection-curation feature: browse Featured and All collections, open collection details, download selected or entire collections, bookmark collections, and post/read collection comments
            - **Collage** — open any selection of artworks as a single unique collage tab, add or remove individual images, and download the whole collage
            - **Pixiv-connected actions** — likes, public/private bookmarks, follows/unfollows, views, comments, replies, stickers, and Pixiv emoji now operate on your real Pixiv account
            - **Viewer tabs persist across restarts** — the tabs you had open (including the collage tab) are automatically reopened the next time you launch Pikura

            ### Improved
            - **Chrome-style tab reordering** — drag inline viewer tabs left or right and watch the strip slide around the tab in real time
            - **View in new tabs** — open selected artworks in their own inline viewer tabs from Gallery, Discover, Rankings, Pixivision, Bookmarks, Search, and Collections
            - **Bookmarks** now includes every artwork you've liked on Pixiv (even ones that aren't bookmarked) alongside your public/private bookmarks and bookmarked collections
            - **Search** now has a History dropdown to jump back to a previous search (with the filters used at the time) and Popular Tags as clickable chips
            - Synchronization updates immediately across the gallery, Discover, Rankings, Search, Pixivision, and the inline viewer
            - Comment threads support correct-thread replies, stickers, Pixiv custom-emoji shortcodes, and deletion of your own comments after confirmation
            - Refined light and dark themes with better contrast, rounded button hover highlights, visible caption-button hover states, and a working system-theme toggle
            - Fixed the inline viewer **Liked** button readability in both themes
            - Collections tile context menu now has an "Open on pixiv.net" link alongside Copy collection link
            - The Windows uninstaller can now fully remove Pikura (settings, saved Pixiv login, download history/database, cached images, and downloaded AI tagging models) or keep that data in place for a future reinstall

            ### Fixed
            - Pixivision Monthly Ranking/Featured sidebar banners were unreadable in light/dark mode; both now use a fixed black background with white text
            - Updated the Pixiv-connected actions preview GIF used on the feature highlights page
            """,
            "https://github.com/pikura-app/pikura/releases/latest",
            null),
        "2.0.0" => new UpdateInfo(
            "2.0.0",
            "Pikura 2.0.0 — Major Release",
            """
            ## 2.0.0

            This is the biggest update to Pikura yet — three brand-new tabs, a new onboarding experience, and significant performance work across the app.

            ### New Features
            - **Pixivision tab** — browse official Pixivision articles, artist interviews and featured spotlights without leaving the app; save articles, filter content, and go back to previous articles using the built-in calendar
            - **Search tab** — global search across artworks, artists, novels and users with a single query
            - **Viewed tab** — every artwork you open is remembered; scroll back through your history or use the built-in calendar to jump to any past day. Clear history from the past hour, day, week, month, year, or all time — or set it to clear automatically after a configurable retention window in Settings
            - **Artwork background overlay** — use any artwork as a full-window background with per-image opacity, brightness, pan and zoom; cycle through up to five favorites
            - **Gallery search** — search inside the gallery by tag, title, artist, caption, date range, R-18 mode, AI generation and more
            - **Advanced filtering** — filter by AI generation, R-18 type, blocklist scope, tags, titles and artists independently in Gallery, Rankings, Discover, Search, Pixivision, Viewed and Bookmarks

            ### Performance Improvements
            - Reduced memory usage and faster startup
            - Smoother gallery scrolling
            - More responsive download coordination
            - Thread-safe animated image decoding with instant-display frame preloading

            ### Other Changes
            - What's New onboarding splash showcasing new features on major updates
            - GitHub Page link and View Changelog button on the About settings page
            - Numerous bug fixes and UI polish throughout the app
            """,
            "https://github.com/pikura-app/pikura/releases/latest",
            null),
        "1.8.0" => new UpdateInfo(
            "1.8.0",
            "Pikura v1.8.0",
            """
            ## 1.8.0

            ### Downloads
            - **Download artist avatar and banner** — new option in Settings → Advanced to save `avatar.jpg` and `banner.jpg` to the artist's folder alongside their artworks
            - **Live settings for running jobs** — changes to Safe Mode, delay between downloads, retry count, and retry delay now apply immediately to in-progress jobs without restarting
            - **DownloadDelaySeconds now live** — was previously read once at job start from a snapshot; now always reads the current global setting

            ### Inline Artwork Viewer
            - **HTTP 429 error panel** — when Pixiv rate-limits an image load, an error message with a Retry button is shown instead of a blank viewer
            - **Retry button** — click to reload the current artwork page without navigating away

            ### Fullscreen Viewer
            - **Full-resolution images on keyboard navigation** — pressing arrow keys to navigate between artworks now correctly loads the full-size original image for each artwork
            - **Canvas state reset on navigation** — scale, position, and image dimensions are cleared when moving to a new artwork, preventing stale content from flashing

            ### Settings UI
            - **R-18 Content mode and Type filter buttons** — improved spacing and padding for a cleaner, easier-to-click look
            - **Overwrite behavior buttons** — matching spacing improvements
            - **Blacklist renamed to Blocklist** — section header updated across the Settings panel
            - **Blocklist Add buttons** — improved spacing between the text input and Add button in all three blocklist columns (Tags, Titles, Member IDs)
            - **Download artist avatar and banner checkbox** — added to the Download Behavior section in Advanced settings
            """,
            "https://github.com/pikura-app/pikura/releases/latest",
            null),
        _ => null,
    };
}
