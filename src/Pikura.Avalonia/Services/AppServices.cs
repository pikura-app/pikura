using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pikura.Avalonia.ViewModels;
using Pikura.Core.Data;
using Pikura.Core.DependencyInjection;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Service provider for the Avalonia application
/// </summary>
public static class AppServices
{
    private static IServiceProvider? _serviceProvider;
    private static IHost? _host;

    public static void Initialize()
    {
        // Load settings early to determine log level
        var earlySettings = new SettingsService();
        var logLevel = earlySettings.Current.VerboseLogging ? LogLevel.Debug : LogLevel.Information;
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pikura", "pikura.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(logLevel);
                logging.AddProvider(new FileLoggerProvider(logPath, logLevel));
            })
            .ConfigureServices((context, services) =>
            {
                // Core services (SettingsService, PixivHttpClientFactory, PixivClient, PixivDownloadService, PixivImageLoader)
                services.AddPixivCore();

                // Local favorites (app-side persistence)
                services.AddSingleton<Pikura.Core.Services.LocalFavoritesService>();

                // Download job repository (SQLite)
                var dbPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Pikura", "downloads.db");
                services.AddSingleton<DownloadJobRepository>(provider =>
                    new DownloadJobRepository(dbPath, provider.GetRequiredService<ILogger<DownloadJobRepository>>()));

                // Download preset repository (same database)
                services.AddSingleton<DownloadPresetRepository>(provider =>
                    new DownloadPresetRepository(dbPath, provider.GetRequiredService<ILogger<DownloadPresetRepository>>()));

                // Artist settings repository (same database)
                services.AddSingleton<ArtistSettingsRepository>(provider =>
                    new ArtistSettingsRepository(dbPath, provider.GetRequiredService<ILogger<ArtistSettingsRepository>>()));

                // Artist monitoring repository (same database)
                services.AddSingleton<ArtistMonitorRepository>(provider =>
                    new ArtistMonitorRepository(dbPath, provider.GetRequiredService<ILogger<ArtistMonitorRepository>>()));

                // User image presets repository (same database)
                services.AddSingleton<UserPresetsRepository>(provider =>
                    new UserPresetsRepository(dbPath, provider.GetRequiredService<ILogger<UserPresetsRepository>>()));

                // Schedule repository (same database)
                services.AddSingleton<DownloadScheduleRepository>(provider =>
                    new DownloadScheduleRepository(dbPath, provider.GetRequiredService<ILogger<DownloadScheduleRepository>>()));

                // Download coordinator
                services.AddSingleton<DownloadCoordinator>(provider =>
                    new DownloadCoordinator(
                        provider.GetRequiredService<PixivClient>(),
                        provider.GetRequiredService<PixivDownloadService>(),
                        provider.GetRequiredService<SettingsService>(),
                        provider.GetRequiredService<DownloadJobRepository>(),
                        provider.GetRequiredService<FanboxClient>(),
                        provider.GetRequiredService<ILogger<DownloadCoordinator>>(),
                        provider.GetRequiredService<ImageResizeService>(),
                        provider.GetRequiredService<AccountService>()));

                // Artist monitor service
                services.AddSingleton<ArtistMonitorService>();

                // Schedule executor service
                services.AddSingleton<ScheduleExecutorService>();

                // FANBOX client
                services.AddSingleton<FanboxClient>();

                // Image lookup (AI vision)
                services.AddSingleton<ImageLookupService>();

                // Image resize/processing service
                services.AddSingleton<ImageResizeService>();

                // AI assistant (Ollama)
                services.AddSingleton<HoshiSessionService>();
                services.AddSingleton<OllamaService>();

                // Local ONNX anime image tagger
                services.AddSingleton<AnimeTaggerService>(provider =>
                    new AnimeTaggerService(provider.GetRequiredService<ILogger<AnimeTaggerService>>()));
                services.AddSingleton<Pikura.Avalonia.ViewModels.AiViewModel>(provider =>
                    new Pikura.Avalonia.ViewModels.AiViewModel(
                        provider.GetRequiredService<OllamaService>(),
                        provider.GetRequiredService<Pikura.Core.Services.LocalFavoritesService>(),
                        provider.GetRequiredService<PixivDownloadService>(),
                        provider.GetRequiredService<Pikura.Core.Data.DownloadJobRepository>(),
                        provider.GetRequiredService<DownloadCoordinator>(),
                        provider.GetRequiredService<HoshiSessionService>(),
                        provider.GetRequiredService<PixivClient>(),
                        provider.GetRequiredService<PixivImageLoader>(),
                        provider.GetRequiredService<SettingsService>(),
                        provider.GetRequiredService<ImageLookupService>(),
                        provider.GetRequiredService<AnimeTaggerService>()));

                // Update check
                services.AddSingleton<UpdateCheckService>();

                // Multi-account
                services.AddSingleton<AccountService>(provider =>
                    new AccountService(
                        provider.GetRequiredService<SettingsService>(),
                        provider.GetRequiredService<Pikura.Core.Services.LocalFavoritesService>()));

                // Avalonia-specific services
                services.AddSingleton<NavigationService>();
                services.AddSingleton<DialogService>();
                // Login orchestrator + Linux Playwright backend. PixivLoginService is the
                // single entry point both MainWindow and SettingsViewModel call into so the
                // platform-specific paths (WebView2 vs Playwright vs manual cookie) live in
                // one file instead of being copy-pasted across two call sites.
                services.AddSingleton<PlaywrightLoginService>();
                services.AddSingleton<PixivLoginService>();
                services.AddSingleton<AccessibilityService>();
                services.AddSingleton<FilePickerService>();
                services.AddSingleton<NotificationService>();
                services.AddSingleton<AgentIpcClient>();

                // ViewModels
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<GalleryViewModel>(provider =>
                    new GalleryViewModel(
                        provider.GetRequiredService<PixivClient>(),
                        provider.GetRequiredService<PixivImageLoader>(),
                        provider.GetRequiredService<PixivDownloadService>(),
                        provider.GetRequiredService<SettingsService>(),
                        provider.GetRequiredService<NavigationService>(),
                        provider.GetRequiredService<DialogService>(),
                        provider.GetRequiredService<DownloadJobRepository>(),
                        provider.GetRequiredService<DownloadCoordinator>(),
                        provider.GetRequiredService<AccountService>(),
                        provider.GetRequiredService<Pikura.Core.Services.DeviceCapabilityService>(),
                        provider.GetRequiredService<ILogger<GalleryViewModel>>()));
                services.AddTransient<ArtworkDetailViewModel>();
                services.AddTransient<RankingsViewModel>();
                services.AddSingleton<EnhancedRankingsViewModel>();
                services.AddTransient<SettingsViewModel>(provider =>
                    new SettingsViewModel(
                        provider.GetRequiredService<SettingsService>(),
                        provider.GetRequiredService<DialogService>(),
                        provider.GetRequiredService<PixivClient>(),
                        provider.GetRequiredService<FilePickerService>(),
                        provider.GetRequiredService<OllamaService>(),
                        provider.GetRequiredService<AnimeTaggerService>(),
                        provider.GetRequiredService<AccountService>(),
                        provider.GetRequiredService<FfmpegService>()));
                services.AddSingleton<AnalyticsViewModel>();
                services.AddTransient<DownloadsViewModel>();
                services.AddSingleton<DownloadByArtistViewModel>();
                services.AddTransient<DownloadByImageIdViewModel>();
                services.AddTransient<DownloadBookmarksViewModel>(provider =>
                    new DownloadBookmarksViewModel(
                        provider.GetRequiredService<PixivClient>(),
                        provider.GetRequiredService<DownloadCoordinator>(),
                        provider.GetRequiredService<DialogService>(),
                        provider.GetRequiredService<Pikura.Core.Services.LocalFavoritesService>(),
                        provider.GetRequiredService<SettingsService>()));
                services.AddTransient<DownloadFromListViewModel>(provider =>
                    new DownloadFromListViewModel(
                        provider.GetRequiredService<PixivClient>(),
                        provider.GetRequiredService<DownloadCoordinator>(),
                        provider.GetRequiredService<DialogService>()));
                services.AddTransient<DownloadBySearchViewModel>(provider =>
                    new DownloadBySearchViewModel(
                        provider.GetRequiredService<PixivClient>(),
                        provider.GetRequiredService<DownloadCoordinator>(),
                        provider.GetRequiredService<DialogService>(),
                        provider.GetRequiredService<PixivImageLoader>()));
                services.AddSingleton<SchedulesViewModel>(provider =>
                    new SchedulesViewModel(
                        provider.GetRequiredService<DownloadScheduleRepository>(),
                        provider.GetRequiredService<PixivClient>(),
                        provider.GetRequiredService<SettingsService>(),
                        provider.GetRequiredService<DialogService>(),
                        provider.GetRequiredService<ScheduleExecutorService>()));
                services.AddTransient<DownloadByFanboxViewModel>(provider =>
                    new DownloadByFanboxViewModel(
                        provider.GetRequiredService<FanboxClient>(),
                        provider.GetRequiredService<DownloadCoordinator>(),
                        provider.GetRequiredService<DialogService>(),
                        provider.GetRequiredService<SettingsService>()));
                services.AddTransient<BatchDownloadViewModel>(provider =>
                    new BatchDownloadViewModel(
                        provider.GetRequiredService<DownloadByArtistViewModel>(),
                        provider.GetRequiredService<DownloadByImageIdViewModel>(),
                        provider.GetRequiredService<DownloadBookmarksViewModel>(),
                        provider.GetRequiredService<DownloadFromListViewModel>(),
                        provider.GetRequiredService<DownloadBySearchViewModel>(),
                        provider.GetRequiredService<DownloadByFanboxViewModel>(),
                        provider.GetRequiredService<SchedulesViewModel>(),
                        provider.GetRequiredService<SettingsViewModel>()));
                services.AddSingleton<HistoryViewModel>(provider =>
                    new HistoryViewModel(
                        provider.GetRequiredService<DownloadJobRepository>(),
                        provider.GetRequiredService<DownloadCoordinator>(),
                        provider.GetRequiredService<DialogService>(),
                        provider.GetRequiredService<PixivImageLoader>(),
                        provider.GetRequiredService<SettingsService>()));
                services.AddSingleton<DiscoverViewModel>();
                services.AddSingleton<BookmarksViewModel>(provider =>
                    new BookmarksViewModel(
                        provider.GetRequiredService<PixivClient>(),
                        provider.GetRequiredService<PixivImageLoader>(),
                        provider.GetRequiredService<SettingsService>(),
                        provider.GetRequiredService<Pikura.Core.Services.LocalFavoritesService>(),
                        provider.GetRequiredService<GalleryViewModel>(),
                        provider.GetRequiredService<DownloadCoordinator>(),
                        provider.GetRequiredService<DialogService>()));

                // Logging
                services.AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.AddDebug();
                });
            });

        _host = hostBuilder.Build();
        _serviceProvider = _host.Services;

        // Wire up notifications
        WireUpNotifications();

        // Wire up per-account Hoshi session isolation
        WireUpHoshiSessionIsolation();

        // Wire up per-account download/schedule/history isolation
        WireUpPerAccountDb();
    }

    private static void WireUpPerAccountDb()
    {
        try
        {
            var accounts  = Get<AccountService>();
            var jobRepo   = Get<DownloadJobRepository>();
            var schedRepo = Get<DownloadScheduleRepository>();

            // Startup: set active user on repos so constructors load the right data.
            // Do NOT trigger VM reloads here — each VM constructor handles its own initial load.
            jobRepo.SetActiveUser(accounts.ActiveProfile?.UserId);
            schedRepo.SetActiveUser(accounts.ActiveProfile?.UserId);

            // On account switch: update repos then reload VMs on the UI thread.
            accounts.ActiveProfileChanged += (_, _) =>
            {
                var userId = accounts.ActiveProfile?.UserId;
                jobRepo.SetActiveUser(userId);
                schedRepo.SetActiveUser(userId);

                global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try { await Get<HistoryViewModel>().ReloadAsync(); } catch { }
                    try { await Get<SchedulesViewModel>().ReloadAsync(); } catch { }
                    try { await Get<AnalyticsViewModel>().ReloadAsync(); } catch { }
                });
            };
        }
        catch { /* non-fatal */ }
    }

    private static void WireUpHoshiSessionIsolation()
    {
        try
        {
            var sessions = Get<HoshiSessionService>();
            var accounts = Get<AccountService>();

            // Apply the active user immediately so startup loads the correct dir
            sessions.SwitchUser(accounts.ActiveProfile?.UserId);

            accounts.ActiveProfileChanged += (_, _) =>
                sessions.SwitchUser(accounts.ActiveProfile?.UserId);
        }
        catch { /* non-fatal */ }
    }

    private static void WireUpNotifications()
    {
        try
        {
            var coordinator = Get<DownloadCoordinator>();
            var notificationService = Get<NotificationService>();
            var monitorService = Get<ArtistMonitorService>();

            // Reconcile jobs from a prior session BEFORE HistoryViewModel loads.
            // Downloads run in-process, so a job left "Running" in the DB is from a
            // crash/force-kill (the graceful shutdown hook pauses cleanly). Convert
            // those to Paused so their persisted progress can be resumed; Queued and
            // Pending jobs are left intact so they remain in the queue.
            try
            {
                var jobRepo = Get<DownloadJobRepository>();
                jobRepo.RecoverInterruptedJobsAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to recover interrupted jobs: {ex.Message}");
            }

            // Eagerly construct HistoryViewModel so it subscribes to JobStarted/JobCompleted
            // from startup — otherwise downloads that begin before History is navigated to
            // are never tracked in the Active Downloads list.
            _ = Get<HistoryViewModel>();

            // Download started notifications
            coordinator.JobStarted += (sender, e) =>
            {
                var settings = Get<SettingsService>();
                var thumb = e.Job.Targets.FirstOrDefault()?.ThumbnailUrl;

                // Distinguish a fresh start from a resume: a resumed job already
                // has persisted per-artwork progress (or completed targets) from a
                // prior paused run. Resume uses the pause/resume notification toggle.
                var downloaded = e.Job.Targets.Sum(t => t.DownloadedItems > 0 ? t.DownloadedItems : t.CompletedArtworkIds.Count);
                var found      = e.Job.Targets.Sum(t => t.FoundItems > 0 ? t.FoundItems : t.DownloadedItems);
                var isResume   = downloaded > 0 || e.Job.Targets.Any(t => t.Status == TargetStatus.Completed);

                if (isResume)
                {
                    if (settings.Current.NotifyOnDownloadPaused)
                        notificationService.ShowJobResumedNotification(e.Job.Name, downloaded, found, thumb);
                }
                else if (settings.Current.NotifyOnDownloadStarted)
                {
                    notificationService.ShowJobStartedNotification(e.Job.Name, e.Job.Targets.Count, thumb);
                }
            };

            // Download completion notifications (gated on user setting)
            coordinator.JobCompleted += (sender, e) =>
            {
                var settings = Get<SettingsService>();
                // Use per-artwork counts so notifications match what History shows.
                // DownloadedItems is written on target completion; CompletedArtworkIds
                // is the mid-run fallback for paused/in-progress targets.
                var succeededArtworks = e.Job.Targets.Sum(t => t.DownloadedItems > 0 ? t.DownloadedItems : t.CompletedArtworkIds.Count);
                var succeeded = succeededArtworks;
                var failed    = e.Job.FailedItems;
                var thumb     = e.Job.Targets.FirstOrDefault()?.ThumbnailUrl;
                var firstArtworkId = e.Job.Targets.FirstOrDefault()?.TargetId;

                var pausedDownloaded = e.Job.Targets.Sum(t => t.DownloadedItems > 0 ? t.DownloadedItems : t.CompletedArtworkIds.Count);
                var pausedFound      = e.Job.Targets.Sum(t => t.FoundItems > 0 ? t.FoundItems : t.DownloadedItems);

                if (e.Job.Status == JobStatus.Paused && settings.Current.NotifyOnDownloadPaused)
                    notificationService.ShowJobPausedNotification(e.Job.Name, pausedDownloaded, pausedFound, thumb);
                else if (failed > 0 && e.Job.Status == JobStatus.Failed && settings.Current.NotifyOnDownloadFailed)
                    notificationService.ShowJobFailedNotification(e.Job.Name, e.Job.ErrorMessage, thumb);
                else if (e.Job.Status == JobStatus.Completed && settings.Current.NotifyOnDownloadComplete)
                    notificationService.ShowJobCompletedNotification(e.Job.Name, succeeded, failed, firstArtworkId, thumb);
            };

            // New submission notifications
            monitorService.NewSubmissionsDetected += (sender, e) =>
            {
                var count = e.NewSubmissions.Count;
                var firstSubmission = e.NewSubmissions.FirstOrDefault();
                var firstTitle = firstSubmission?.Title;
                var firstArtworkId = firstSubmission?.ArtworkId;
                notificationService.ShowNewSubmissionNotification(e.Artist.UserName, count, firstTitle, firstArtworkId);
            };

            // Handle notification clicks
            notificationService.NotificationClicked += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Url))
                {
                    // Open the URL in browser
                    try
                    {
                        Process.Start(new ProcessStartInfo(e.Url) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to open URL: {ex.Message}");
                    }
                }
            };

            notificationService.Initialize();

            // Start monitoring (if user is logged in)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000); // Wait 5 seconds for app to fully load
                    monitorService.Start();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to start monitor service: {ex.Message}");
                }
            });

            // Start schedule executor — only if the background agent is NOT already running.
            // If the agent is running it owns all schedule execution; we just listen via IPC.
            _ = Task.Run(async () =>
            {
                try
                {
                    // Give the IPC client a moment to attempt connection
                    await Task.Delay(3000);

                    var ipcClient = Get<AgentIpcClient>();
                    if (ipcClient.IsConnected)
                    {
                        Debug.WriteLine("Agent is running — skipping in-app schedule executor.");
                        return;
                    }

                    var executor = Get<ScheduleExecutorService>();

                    // Execute startup schedules first
                    await executor.ExecuteStartupSchedulesAsync();

                    // Start periodic checking
                    executor.Start();

                    Debug.WriteLine("Schedule executor started (no agent detected)");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to start schedule executor: {ex.Message}");
                }
            });

            // Start IPC client — connects to Pikura.Agent if it is running
            _ = Task.Run(() =>
            {
                try
                {
                    var ipcClient = Get<AgentIpcClient>();
                    var historyVm = Get<HistoryViewModel>();
                    var notificationService = Get<NotificationService>();
                    var settings = Get<SettingsService>();

                    ipcClient.MessageReceived += (_, msg) =>
                    {
                        // Agent just connected — stop the in-app executor to avoid double-runs
                        if (msg.Event is "heartbeat" or "schedule_started")
                        {
                            try
                            {
                                var executor = Get<ScheduleExecutorService>();
                                executor.Stop();
                                Debug.WriteLine("Agent detected via IPC — stopped in-app executor.");
                            }
                            catch { }
                        }

                        // Show tray notification for schedule completions (if enabled)
                        if (msg.Event is "schedule_completed" or "schedule_failed"
                            && settings.Current.ShowScheduleNotifications
                            && !string.IsNullOrEmpty(msg.StatusText))
                        {
                            notificationService.ShowNotification(
                                "Pikura Schedule",
                                msg.StatusText);
                        }

                        // Reload history so agent-triggered jobs appear in the UI
                        if (msg.Event is "schedule_completed" or "schedule_failed")
                            _ = historyVm.ReloadAsync();
                    };

                    ipcClient.Start();
                    Debug.WriteLine("Agent IPC client started");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to start agent IPC client: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            // Log but don't crash if notification setup fails
            Debug.WriteLine($"Failed to wire up notifications: {ex.Message}");
        }
    }

    public static T Get<T>() where T : class
    {
        if (_serviceProvider == null)
            throw new InvalidOperationException("AppServices not initialized. Call Initialize() first.");
        
        return _serviceProvider.GetRequiredService<T>();
    }

    public static async Task ShutdownAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
