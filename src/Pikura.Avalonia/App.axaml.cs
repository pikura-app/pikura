using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.Views.Dialogs;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Pikura.Avalonia.Models;

namespace Pikura.Avalonia;

public partial class App : Application
{
    private CrashReportService? _crashService;
    private static DispatcherTimer? _themeTimer;
    private static IPlatformSettings? _platformSettings;
    private static bool _themeEventsSubscribed;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AppServices.Initialize();

        // Apply persisted theme before any window is created
        ApplyTheme();

        // Sync Windows startup registry with the saved user setting. The app/installer
        // may have left a stale Run key entry under a legacy or canonical name; this
        // makes the user's "Start with Windows" toggle authoritative.
        var settings = AppServices.Get<SettingsService>();
        if (OperatingSystem.IsWindows())
        {
            try { Services.StartupHelper.SetStartupEnabled(settings.Current.StartWithWindows); }
            catch { /* non-fatal */ }
        }
    }

    public static void ApplyTheme()
    {
        if (Current is null) return;
        var settings = AppServices.Get<SettingsService>();
        var theme = settings.Current.Theme;
        ThemeVariant? variant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            "Scheduled" => IsScheduledDark(settings.Current.ThemeScheduleDarkStart, settings.Current.ThemeScheduleDarkEnd)
                ? ThemeVariant.Dark
                : ThemeVariant.Light,
            "System" or "Default" or _ => null
        };

        if (variant is null)
        {
            variant = GetSystemThemeVariant();
            SubscribeToSystemThemeChanges();
        }
        else
        {
            UnsubscribeFromSystemThemeChanges();
        }

        Current.RequestedThemeVariant = variant;

        if (theme == "Scheduled" && _themeTimer is null)
        {
            _themeTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => ApplyTheme())
            {
                IsEnabled = true
            };
        }
        else if (theme != "Scheduled")
        {
            _themeTimer?.Stop();
            _themeTimer = null;
        }
    }

    private static ThemeVariant GetSystemThemeVariant()
    {
        try
        {
            var values = GetPlatformSettings()?.GetColorValues();
            if (values is not null)
            {
                var name = values.ThemeVariant.ToString();
                if (name == "Dark") return ThemeVariant.Dark;
                if (name == "Light") return ThemeVariant.Light;
            }
        }
        catch { /* fall back to light if platform settings are unavailable */ }
        return ThemeVariant.Light;
    }

    private static IPlatformSettings? GetPlatformSettings()
    {
        if (_platformSettings is not null) return _platformSettings;
        _platformSettings = Current?.PlatformSettings;
        return _platformSettings;
    }

    private static void SubscribeToSystemThemeChanges()
    {
        if (_themeEventsSubscribed) return;
        var ps = GetPlatformSettings();
        if (ps is null) return;
        ps.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(ApplyTheme);
        _themeEventsSubscribed = true;
    }

    private static void UnsubscribeFromSystemThemeChanges()
    {
        if (!_themeEventsSubscribed) return;
        var ps = GetPlatformSettings();
        if (ps is not null)
            ps.ColorValuesChanged -= (_, _) => Dispatcher.UIThread.Post(ApplyTheme);
        _themeEventsSubscribed = false;
    }

    private static bool IsScheduledDark(TimeSpan start, TimeSpan end)
    {
        var now = DateTime.Now.TimeOfDay;
        if (start <= end)
        {
            return now >= start && now < end;
        }
        return now >= start || now < end;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize crash reporting service
        _crashService = new CrashReportService();

        // Set up comprehensive unhandled exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        global::Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnUIThreadException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppServices.Get<SettingsService>();
            var mainWindowViewModel = AppServices.Get<MainWindowViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };

            var dialogService = AppServices.Get<DialogService>();
            dialogService.Initialize(mainWindow);

            // Gracefully pause any running downloads on shutdown so their persisted
            // per-artwork progress can be resumed on next launch instead of being
            // cancelled as orphans. Run off the UI thread to avoid sync-over-async
            // reentrancy during shutdown.
            desktop.ShutdownRequested += OnShutdownRequested;

            var accessibilityService = AppServices.Get<AccessibilityService>();

            // Eagerly construct HistoryViewModel synchronously so it subscribes to
            // coordinator events before any download is triggered — otherwise jobs
            // started before the user opens the History tab would be missed.
            var historyVm = AppServices.Get<HistoryViewModel>();

            // Show splash if enabled; otherwise show the main window right away.
            SplashWindow? splash = null;
            if (settings.Current.ShowSplashScreen)
            {
                splash = new SplashWindow();
                splash.Show();
                splash.Activate();
            }
            else
            {
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
            }

            // Pre-load persisted history data after the window finishes initializing.
            _ = global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await Task.Delay(200); // let window finish initializing
                try { await historyVm.ReloadAsync(); } catch { }
                // Recover any jobs left in Running state from a hard kill (→ Paused),
                // then auto-start Pending jobs up to the concurrent-job limit.
                try
                {
                    var coordinator = AppServices.Get<DownloadCoordinator>();
                    await coordinator.StartupRecoveryAsync();
                    await historyVm.ReloadAsync(); // refresh UI after recovery
                }
                catch { }
            });

            // Auto-validate existing session cookie in background (shared with WPF app)
            if (settings.Current.IsConfigured)
            {
                var client = AppServices.Get<PixivClient>();
                _ = Task.Run(async () =>
                {
                    try { await client.ValidateSessionAsync(); }
                    catch { /* non-fatal — UI will show "not signed in" and let user retry */ }
                });

                // Refresh the Cloudflare cf_clearance cookie in the background if it's missing
                // or stale — needed for pixiv.net subdomains (e.g. embed.pixiv.net, Collection
                // collage thumbnails) that enforce bot-management beyond plain PHPSESSID.
                var loginService = AppServices.Get<PixivLoginService>();
                if (loginService.IsCloudflareSessionStale)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await loginService.RefreshCloudflareSessionAsync(); }
                        catch { /* non-fatal — collage thumbnails just won't load */ }
                    });
                }
            }

            // Prepare the feature highlights (page list + pre-decoded GIF frames) in the
            // background while the startup splash is visible, so the popup opens instantly.
            var highlightsPrep = PrepareStartupHighlightsAsync(settings);

            // Close the splash after a couple of seconds, then reveal the main window
            // and show any feature highlights / crash dialogs.
            _ = global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (splash is not null)
                {
                    await Task.Delay(3000);
                    splash.CloseSplash();
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    mainWindow.Activate();
                }

                try
                {
                    await ShowStartupHighlightsAsync(settings, mainWindow, highlightsPrep);
                    await ShowCrashDialogIfNeededAsync();
                }
                finally
                {
                    // Signals waiters (e.g. the post-update changelog popup) that the
                    // startup dialog sequence is finished, so they never race with it.
                    StartupDialogsComplete = true;
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// True once the startup dialog sequence (feature highlights, crash report) has
    /// finished. Popups triggered by background checks wait on this to avoid racing
    /// the startup modals for the main window.
    /// </summary>
    public static bool StartupDialogsComplete { get; private set; }

    private static string GetCurrentVersionString()
    {
        var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version;
        return currentVersion is null
            ? "2.0.0"
            : $"{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}";
    }

    /// <summary>
    /// Builds the onboarding pages and starts pre-decoding screenshots/GIFs in the
    /// background. Returns null when the highlights should not be shown.
    /// </summary>
    private static Task<List<OnboardingPage>?> PrepareStartupHighlightsAsync(SettingsService settings)
    {
        if (!settings.Current.ShowFeatureHighlights ||
            settings.Current.LastOnboardingVersionShown == GetCurrentVersionString())
            return Task.FromResult<List<OnboardingPage>?>(null);

        var pages = BuildOnboardingPages();

        // Pre-decode screenshots/GIFs in the background so the dialog can open
        // immediately while assets continue to cache for faster page navigation.
        _ = Task.Run(async () =>
        {
            foreach (var page in pages)
            {
                foreach (var shot in page.Screenshots)
                {
                    if (string.IsNullOrEmpty(shot)) continue;
                    if (Converters.AssetPathConverter.Instance.Convert(shot, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture) is string localPath)
                        await Controls.AnimatedImage.PreloadAsync(localPath);
                }
            }
        });

        return Task.FromResult<List<OnboardingPage>?>(pages);
    }

    /// <summary>
    /// Shows the feature highlights / onboarding dialog for new installs or major updates.
    /// </summary>
    private async Task ShowStartupHighlightsAsync(SettingsService settings, Window owner, Task<List<OnboardingPage>?> preparation)
    {
        var pages = await preparation;
        if (pages is null) return;

        var vm = new FeatureHighlightsViewModel(pages);
        var dialog = new FeatureHighlightsWindow(vm);
        await dialog.ShowDialog<bool>(owner);

        settings.Update(s => s.LastOnboardingVersionShown = GetCurrentVersionString());
    }

    private static List<OnboardingPage> BuildOnboardingPages()
    {
        return new List<OnboardingPage>
        {
            new()
            {
                Title = "Pikura 2.1.0",
                Subtitle = "What's new in this update",
                Description = "This release focuses on making Pikura feel more connected to Pixiv and easier to work with. Tabs reorder like a browser, collages behave like first-class tabs, and likes, bookmarks, follows, and comments now talk directly to Pixiv.",
                IconKey = "RefreshIcon"
            },
            new()
            {
                Title = "Refreshed Light & Dark Themes",
                Subtitle = "A cleaner look in both modes",
                Description = "The light and dark themes have been refined with better contrast, smoother button highlights, and improved readability for toggles, caption buttons, and the inline viewer.",
                IconKey = "PaletteIcon",
                Screenshots =
                [
                    "avares://Pikura/Assets/FeatureHighlights/light-mode.png",
                    "avares://Pikura/Assets/FeatureHighlights/dark-mode.png",
                    "avares://Pikura/Assets/FeatureHighlights/light-dark-mode-toggle.gif"
                ],
                IsNew = true
            },
            new()
            {
                Title = "Chrome-Style Tab Reordering",
                Subtitle = "Drag tabs to reorder",
                Description = "Grab any inline viewer tab and drag it left or right. The tab lifts and scales while the other tabs smoothly slide out of the way, just like in a web browser.",
                IconKey = "NewTabIcon"
            },
            new()
            {
                Title = "Collage",
                Subtitle = "Unique collage tabs",
                Description = "Open any selection of artworks as a single collage tab. Add or remove individual images, open each image in its own tab, or download the whole collage in one go.",
                IconKey = "ImageIcon",
                Screenshot = "avares://Pikura/Assets/FeatureHighlights/collage.gif",
                IsNew = true
            },
            new()
            {
                Title = "View in New Tabs",
                Subtitle = "Open selections individually",
                Description = "From Gallery, Discover, Rankings, Pixivision, Bookmarks, Search, and Collections, open selected artworks as separate inline viewer tabs so you can compare them side by side.",
                IconKey = "PopupIcon"
            },
            new()
            {
                Title = "Pixiv-Connected Actions",
                Subtitle = "Real likes, bookmarks, follows, and comments",
                Description = "Likes, public/private bookmarks, follows, unfollows, views, comments, replies, stickers, and Pixiv emoji now operate on your real Pixiv account and stay in sync across the app.",
                IconKey = "HeartFilledIcon",
                Screenshots =
                [
                    "avares://Pikura/Assets/FeatureHighlights/follows-bookmarks-favorites.png",
                    "avares://Pikura/Assets/FeatureHighlights/comments.gif"
                ],
                IsNew = true
            },
            new()
            {
                Title = "Collections",
                Subtitle = "A new way Pixiv artists curate their work",
                Description = "Browse Featured and All Pixiv collections, open collection details, download selected works or an entire collection, bookmark collections, and read and post collection comments.",
                IconKey = "FolderIcon",
                Screenshots =
                [
                    "avares://Pikura/Assets/FeatureHighlights/collections.png",
                    "avares://Pikura/Assets/FeatureHighlights/collections-see-view.gif"
                ],
                IsNew = true
            },
            new()
            {
                Title = "Bookmarks Improvements",
                Subtitle = "Liked artworks and collections, all in one place",
                Description = "The Bookmarks section now includes every artwork you've liked on Pixiv — even ones that aren't bookmarked — alongside your public/private bookmarks and bookmarked collections, all kept in sync as you browse.",
                IconKey = "BookmarkFilledIcon",
                Screenshots =
                [
                    "avares://Pikura/Assets/FeatureHighlights/bookmarks.png",
                    "avares://Pikura/Assets/FeatureHighlights/follows-bookmarks-favorites.png"
                ],
                IsNew = true
            },
            new()
            {
                Title = "Search Improvements",
                Subtitle = "History and popular tags",
                Description = "Jump back to a previous search from the History dropdown under the search box — your last 25 queries are remembered along with the filters you used. Popular tags are also shown as clickable chips so you can search by tag with one click. Click anywhere outside the History dropdown to close it.",
                IconKey = "SearchIcon",
                Screenshots =
                [
                    "avares://Pikura/Assets/FeatureHighlights/search-history.png",
                    "avares://Pikura/Assets/FeatureHighlights/popular-tags.png"
                ]
            },
        };
    }

    /// <summary>
    /// Shows the crash report dialog if a crash was detected from previous session.
    /// </summary>
    private async Task ShowCrashDialogIfNeededAsync()
    {
        if (_crashService?.WasCrashDetected() != true) return;

        var crashInfo = _crashService.GetLastCrashInfo();
        if (crashInfo == null) { _crashService?.ClearCrashFlag(); return; }

        // Don't surface crashes that are stale (> 5 min old) — these are leftover flags
        // from failed build/launch attempts that the user has already moved past.
        if ((DateTime.Now - crashInfo.Timestamp).TotalMinutes > 5)
        {
            _crashService?.ClearCrashFlag();
            return;
        }

        // Don't surface build-artifact exceptions — XamlLoadException means the binary
        // was stale/incomplete, not that the app itself crashed in a meaningful way.
        if (crashInfo.ExceptionType.Contains("XamlLoad", StringComparison.OrdinalIgnoreCase))
        {
            _crashService?.ClearCrashFlag();
            return;
        }

        var dialog = new CrashReportDialog(crashInfo);

        // Get the main window to use as owner
        Window? owner = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            owner = desktop.MainWindow;
        }

        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    /// <summary>
    /// Handler for AppDomain unhandled exceptions (fatal - app will terminate).
    /// </summary>
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _crashService?.GenerateCrashReport(ex, $"AppDomain - IsTerminating: {e.IsTerminating}");
        }
        else
        {
            _crashService?.GenerateCrashReport(
                new Exception("Unknown non-exception error: " + e.ExceptionObject?.ToString()),
                "AppDomain - Non-exception error object");
        }
    }

    /// <summary>
    /// Handler for TaskScheduler unobserved task exceptions.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Always mark observed first so we never accidentally crash the process while
        // deciding whether to report.
        e.SetObserved();

        if (IsBenignBackgroundException(e.Exception))
            return;

        _crashService?.GenerateCrashReport(e.Exception, "TaskScheduler - Unobserved task exception");
    }

    /// <summary>
    /// Identifies background exceptions that are harmless on certain platforms and
    /// should not generate noisy crash reports. Currently filters:
    /// - Avalonia.FreeDesktop AppMenu DBus errors on GNOME (no Canonical AppMenu service).
    /// </summary>
    private static bool IsBenignBackgroundException(Exception? ex)
    {
        if (ex == null) return false;

        // Walk both AggregateException.InnerExceptions and the InnerException chain.
        var queue = new Queue<Exception>();
        queue.Enqueue(ex);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            var name = cur.GetType().FullName ?? string.Empty;
            var msg  = cur.Message ?? string.Empty;

            // GNOME / non-Unity desktops don't provide com.canonical.AppMenu.Registrar.
            // Avalonia.FreeDesktop tries to register a global menu and throws.
            if (name == "Tmds.DBus.Protocol.DBusException" &&
                msg.Contains("com.canonical.AppMenu.Registrar", StringComparison.Ordinal))
            {
                return true;
            }

            if (cur is AggregateException agg)
                foreach (var inner in agg.InnerExceptions) queue.Enqueue(inner);
            if (cur.InnerException != null)
                queue.Enqueue(cur.InnerException);
        }
        return false;
    }

    /// <summary>
    /// Handler for UI thread unhandled exceptions (non-fatal if handled).
    /// </summary>
    private void OnUIThreadException(object? sender, global::Avalonia.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _crashService?.GenerateCrashReport(e.Exception, "UI Thread - Dispatcher exception");
        e.Handled = true; // Prevent app crash, but log it
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        try
        {
            var coordinator = AppServices.Get<DownloadCoordinator>();
            // Off the UI thread to avoid sync-over-async reentrancy while shutting down.
            Task.Run(() => coordinator.PauseAllRunningForShutdownAsync()).GetAwaiter().GetResult();
        }
        catch { /* best-effort: startup recovery will re-pause any orphans */ }
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();
    private void OnTrayOpen(object? sender, EventArgs e)       => ShowMainWindow();
    private void OnTrayQuit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var w = desktop.MainWindow;
        if (w == null) return;
        w.Show();
        w.WindowState = WindowState.Normal;
        w.Activate();
    }
}