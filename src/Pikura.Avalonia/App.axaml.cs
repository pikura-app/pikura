using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AppServices.Initialize();

        // Apply persisted theme before any window is created
        var settings = AppServices.Get<SettingsService>();
        RequestedThemeVariant = settings.Current.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark"  => ThemeVariant.Dark,
            _       => null  // system default
        };

        // Sync Windows startup registry with the saved user setting. The app/installer
        // may have left a stale Run key entry under a legacy or canonical name; this
        // makes the user's "Start with Windows" toggle authoritative.
        if (OperatingSystem.IsWindows())
        {
            try { Services.StartupHelper.SetStartupEnabled(settings.Current.StartWithWindows); }
            catch { /* non-fatal */ }
        }
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
    /// Builds the onboarding pages and pre-decodes every screenshot/GIF into the
    /// shared frame cache. Returns null when the highlights should not be shown.
    /// </summary>
    private static async Task<List<OnboardingPage>?> PrepareStartupHighlightsAsync(SettingsService settings)
    {
        if (!settings.Current.ShowFeatureHighlights ||
            settings.Current.LastOnboardingVersionShown == GetCurrentVersionString())
            return null;

        var pages = BuildOnboardingPages();

        // Pre-decode every screenshot/GIF into the shared frame cache so each
        // page displays instantly when the user navigates to it.
        var preloads = new List<Task>();
        foreach (var page in pages)
        {
            if (string.IsNullOrEmpty(page.Screenshot)) continue;
            if (Converters.AssetPathConverter.Instance.Convert(page.Screenshot, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture) is string localPath)
                preloads.Add(Controls.AnimatedImage.PreloadAsync(localPath));
        }
        await Task.WhenAll(preloads);

        return pages;
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
                Title = "Pixivision",
                Subtitle = "Curated editorial content",
                Description = "Browse official Pixivision articles, artist interviews and featured spotlights without leaving the app. You can also go back and look at previous articles using the built-in calendar.",
                IconKey = "GlobeIcon",
                Screenshot = "avares://Pikura/Assets/FeatureHighlights/pixivision.png"
            },
            new()
            {
                Title = "Search Tab",
                Subtitle = "Find everything in one place",
                Description = "Open the Search tab to look up artworks, artists, novels, and users with a single query across the app.",
                IconKey = "SearchIcon",
                Screenshot = "avares://Pikura/Assets/FeatureHighlights/search-tab.gif"
            },
            new()
            {
                Title = "Artwork Background Overlay",
                Subtitle = "Set any artwork as your background",
                Description = "Use any artwork as a full-window background overlay. Tune opacity, brighten/darken, pan and zoom per image, or cycle through up to five favorites.",
                IconKey = "ImageIcon",
                Screenshot = "avares://Pikura/Assets/FeatureHighlights/background-overlay.gif"
            },
            new()
            {
                Title = "Viewed Tab",
                Subtitle = "Revisit what you've already seen",
                Description = "Every artwork you open is remembered in the new Viewed tab. Scroll back through your viewing history to rediscover images you've come across, or use the built-in calendar to jump to what you viewed on any past day.",
                IconKey = "ClockIcon",
                Screenshot = "avares://Pikura/Assets/FeatureHighlights/viewed.png"
            },
            new()
            {
                Title = "Gallery Search",
                Subtitle = "Search and filter inside the gallery",
                Description = "Search within the gallery by tag, title, artist, caption, date range, R-18 mode, AI generation and more. Combine filters and sorting to narrow down the displayed collection quickly.",
                IconKey = "SearchIcon"
            },
            new()
            {
                Title = "Advanced Filtering",
                Subtitle = "Fine-grained control over your view",
                Description = "Filter by AI generation, R-18 type, blocklist scope, tags, titles and artists. Apply filters independently in Gallery, Rankings, Discover, Search and Pixivision.",
                IconKey = "FilterIcon"
            },
            new()
            {
                Title = "Performance Improvements",
                Subtitle = "Faster, smoother experience",
                Description = "Reduced memory usage, faster startup, smoother gallery scrolling, and more responsive download coordination across the board.",
                IconKey = "RefreshIcon"
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