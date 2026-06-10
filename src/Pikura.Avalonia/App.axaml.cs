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
using System.Threading.Tasks;

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
            var mainWindowViewModel = AppServices.Get<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };

            var dialogService = AppServices.Get<DialogService>();
            dialogService.Initialize(desktop.MainWindow);

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
            var settings = AppServices.Get<SettingsService>();
            if (settings.Current.IsConfigured)
            {
                var client = AppServices.Get<PixivClient>();
                _ = Task.Run(async () =>
                {
                    try { await client.ValidateSessionAsync(); }
                    catch { /* non-fatal — UI will show "not signed in" and let user retry */ }
                });
            }

            // Check for previous crash and show dialog after window is loaded
            _ = global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await Task.Delay(500); // Let main window fully load first
                await ShowCrashDialogIfNeededAsync();
            });
        }

        base.OnFrameworkInitializationCompleted();
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

        await dialog.ShowDialog(owner);
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