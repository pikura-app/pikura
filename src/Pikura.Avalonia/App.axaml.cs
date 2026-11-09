using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.Views.Dialogs;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
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

        // Apply the persisted UI language before any window is created, so the first frame is
        // already in the right language. Previously AppLanguage was saved but never read by
        // anything, which is why switching it appeared to do nothing (issue #23).
        var settingsService = AppServices.Get<SettingsService>();
        var loc = Localization.LocalizationService.Instance;
        loc.SetLanguage(settingsService.Current.AppLanguage);

        void UpdateTrayMenu()
        {
            var items = TrayIcon.GetIcons(this)
                ?.SelectMany(t => t.Menu?.Items.OfType<NativeMenuItem>() ?? [])
                .ToList();
            if (items is null || items.Count < 2) return;
            items[0].Header = loc["Tray_Open"];
            items[^1].Header = loc["Tray_Quit"];
        }
        UpdateTrayMenu();
        loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LocalizationService.Language)) return;
            UpdateTrayMenu();
            // Deferred: the items' own translated bindings must update first.
            Dispatcher.UIThread.Post(RefreshComboBoxSelections, DispatcherPriority.Background);
        };

        // Sync Windows startup registry with the saved user setting. The app/installer
        // may have left a stale Run key entry under a legacy or canonical name; this
        // makes the user's "Start with Windows" toggle authoritative.
        var settings = settingsService;
        if (OperatingSystem.IsWindows())
        {
            try { Services.StartupHelper.SetStartupEnabled(settings.Current.StartWithWindows); }
            catch { /* non-fatal */ }
        }
    }

    private static readonly MethodInfo? UpdateSelectionBoxItemMethod =
        typeof(ComboBox).GetMethod("UpdateSelectionBoxItem", BindingFlags.Instance | BindingFlags.NonPublic);

    // A ComboBox snapshots its selected item's content into SelectionBoxItem and only refreshes it on
    // selection change or re-attach, so translated items in visible combos would keep the old
    // language until reselected. Avalonia exposes no public refresh, hence the reflection call.
    private void RefreshComboBoxSelections()
    {
        if (UpdateSelectionBoxItemMethod is null || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var window in desktop.Windows)
            foreach (var combo in window.GetVisualDescendants().OfType<ComboBox>())
                UpdateSelectionBoxItemMethod.Invoke(combo, [combo.SelectedItem]);
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
        ApplyAccent(settings.Current.AccentColor);

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

    // Accent currently baked into the generated Actipro brushes ("Default" = App.axaml as loaded).
    private static string _appliedAccent = "Default";
    private static Color? _defaultBlueMidtone;
    private static bool _accentEventsSubscribed;

    /// <summary>Recolors Actipro's accent ramp. "System" feeds the Windows accent color into the
    /// blue ramp; hue names switch ramps. Regenerating brushes is costly, so it's skipped when
    /// nothing changed.</summary>
    private static void ApplyAccent(string? accent)
    {
        if (string.IsNullOrWhiteSpace(accent) || (accent == "System" && !OperatingSystem.IsWindows()))
            accent = "Default";

        Color? systemColor = null;
        if (accent == "System" && GetPlatformSettings() is { } ps)
        {
            systemColor = ps.GetColorValues().AccentColor1;
            if (!_accentEventsSubscribed)
            {
                ps.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(ApplyTheme);
                _accentEventsSubscribed = true;
            }
        }

        var key = systemColor is { } c ? $"System:{c}" : accent;
        if (key == _appliedAccent) return;
        if (!ActiproSoftware.UI.Avalonia.Themes.ModernTheme.TryGetCurrent(out var theme) || theme.Definition is not { } def)
            return;

        if (def.ColorPaletteFactory is ActiproSoftware.UI.Avalonia.Themes.Generation.DefaultColorPaletteFactory palette)
        {
            _defaultBlueMidtone ??= palette.BlueMidtoneColor;
            palette.BlueMidtoneColor = systemColor ?? _defaultBlueMidtone.Value;
        }
        def.AccentColorRampName = accent is "Default" or "System" ? "Blue" : accent;
        theme.RefreshResources();
        _appliedAccent = key;
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

            // Show splash if enabled. The main window is NOT shown yet in either case:
            // the first-run Quick Setup wizard must appear first when it hasn't been
            // completed, and the shared reveal below handles both paths.
            SplashWindow? splash = null;
            if (settings.Current.ShowSplashScreen)
            {
                splash = new SplashWindow();
                splash.Show();
                splash.Activate();
            }

            // Fresh install = never saw a What's New popup; used below to suppress it
            // after the wizard (the wizard IS the onboarding for new users).
            var freshInstall = settings.Current.LastOnboardingVersionShown == null;

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
            SetupWizardWindow? wizard = null;
            _ = global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (splash is not null)
                {
                    await Task.Delay(3000);
                    splash.CloseSplash();
                }

                // First-run Quick Setup wizard — once for everyone while the flag is
                // false. Shown standalone because the main window can't own a dialog
                // while hidden. Finish → reveal main window; X/Alt+F4 → quit and the
                // wizard returns next launch.
                if (!settings.Current.SetupWizardCompleted)
                {
                    var wizardVm = new SetupWizardViewModel(
                        AppServices.Get<PixivLoginService>(),
                        AppServices.Get<AccountService>(),
                        AppServices.Get<OllamaService>(),
                        settings);
                    wizard = new SetupWizardWindow(wizardVm);
                    wizard.Show();
                    wizard.Activate();
                    if (!await wizard.Completion.Task)
                    {
                        desktop.Shutdown();
                        return;
                    }
                    settings.Update(s => s.SetupWizardCompleted = true);
                    // Fresh installs skip What's New — the wizard covered onboarding.
                    if (freshInstall)
                        settings.Update(s => s.LastOnboardingVersionShown = GetCurrentVersionString());
                }

                // Shared reveal: splash path AND no-splash path converge here. The main
                // window is shown BEFORE the wizard closes so OnLastWindowClose can't
                // fire during the hand-off.
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                mainWindow.Activate();
                wizard?.Close();

                // Fresh installs that finished the wizard don't get What's New.
                var showHighlights = !(freshInstall && settings.Current.SetupWizardCompleted);

                // One-time local-library initial scan (background, resumable).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var lib = AppServices.Get<Pikura.Core.Services.Library.LibraryIndexService>();
                        await lib.EnsureInitialScanAsync(null, System.Threading.CancellationToken.None);
                        await lib.HashUnhashedAsync(null, System.Threading.CancellationToken.None);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Library scan failed: {ex.Message}"); }
                });
                AppServices.Get<LibraryAutoTagService>().Start();

                try
                {
                    if (showHighlights)
                        await ShowStartupHighlightsAsync(settings, mainWindow, highlightsPrep);
                    await ShowCrashDialogIfNeededAsync();
                }
                finally
                {
                    // Signals waiters (e.g. the post-update changelog popup) that the
                    // startup dialog sequence is finished, so they never race with it.
                    StartupDialogsComplete = true;
                }

                // One-time Inno→Velopack migration offer (Windows Inno installs only).
                try { await AppServices.Get<VelopackMigrationService>().OfferOnceAsync(); }
                catch (Exception ex) { _ = ex; }
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

    // Highlights describe a feature release, so patch updates (2.5.0 -> 2.5.1) don't re-show them.
    private static string? MajorMinor(string? version) =>
        Version.TryParse(version, out var v) ? $"{v.Major}.{v.Minor}" : version;

    /// <summary>
    /// Builds the onboarding pages and starts pre-decoding screenshots/GIFs in the
    /// background. Returns null when the highlights should not be shown.
    /// </summary>
    private static Task<List<OnboardingPage>?> PrepareStartupHighlightsAsync(SettingsService settings)
    {
        if (!settings.Current.ShowFeatureHighlights ||
            MajorMinor(settings.Current.LastOnboardingVersionShown) == MajorMinor(GetCurrentVersionString()))
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
        var loc = Pikura.Avalonia.Localization.LocalizationService.Instance;
        return new List<OnboardingPage>
        {
            new()
            {
                Title = loc["WhatsNew_Pikura260"],
                Subtitle = loc["WhatsNew_Pikura250_Subtitle"],
                Description = loc["WhatsNew_Pikura260_Description"],
                IconKey = "RefreshIcon"
            },
            new()
            {
                Title = loc["WhatsNew_QuickSetup"],
                Subtitle = loc["WhatsNew_QuickSetup_Subtitle"],
                Description = loc["WhatsNew_QuickSetup_Description"],
                IconKey = "CheckIcon",
                IsNew = true
            },
            new()
            {
                Title = loc["WhatsNew_AccentColors"],
                Subtitle = loc["WhatsNew_AccentColors_Subtitle"],
                Description = loc["WhatsNew_AccentColors_Description"],
                IconKey = "PaletteIcon",
                IsNew = true
            },
            new()
            {
                Title = loc["WhatsNew_PopupPages"],
                Subtitle = loc["WhatsNew_PopupPages_Subtitle"],
                Description = loc["WhatsNew_PopupPages_Description"],
                IconKey = "PopupIcon",
                IsNew = true
            },
            new()
            {
                Title = loc["WhatsNew_LanguageEverywhere"],
                Subtitle = loc["WhatsNew_LanguageEverywhere_Subtitle"],
                Description = loc["WhatsNew_LanguageEverywhere_Description"],
                IconKey = "GlobeIcon"
            },
            new()
            {
                Title = loc["WhatsNew_Fixes260"],
                Subtitle = loc["WhatsNew_Fixes260_Subtitle"],
                Description = loc["WhatsNew_Fixes260_Description"],
                IconKey = "StarIcon"
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
        AppServices.Get<Pikura.Core.Settings.SettingsService>().Flush();
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
