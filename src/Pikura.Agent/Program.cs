using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pikura.Agent;
using Pikura.Core.Data;
using Pikura.Core.DependencyInjection;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System.IO;
using System;

var builder = Host.CreateApplicationBuilder(args);

// Logging — file + console
var logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Pikura", "agent.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Core Pixiv services (SettingsService, PixivClient, PixivDownloadService, PixivImageLoader)
builder.Services.AddPixivCore();

// Repositories — same database as the main app (%APPDATA%\Pikura\downloads.db)
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Pikura", "downloads.db");

builder.Services.AddSingleton<DownloadJobRepository>(provider =>
    new DownloadJobRepository(dbPath, provider.GetRequiredService<ILogger<DownloadJobRepository>>()));

builder.Services.AddSingleton<DownloadPresetRepository>(provider =>
    new DownloadPresetRepository(dbPath, provider.GetRequiredService<ILogger<DownloadPresetRepository>>()));

builder.Services.AddSingleton<ArtistSettingsRepository>(provider =>
    new ArtistSettingsRepository(dbPath, provider.GetRequiredService<ILogger<ArtistSettingsRepository>>()));

builder.Services.AddSingleton<DownloadScheduleRepository>(provider =>
    new DownloadScheduleRepository(dbPath, provider.GetRequiredService<ILogger<DownloadScheduleRepository>>()));

// FanboxClient (required by DownloadCoordinator)
builder.Services.AddSingleton<FanboxClient>();

// AccountService (required by DownloadCoordinator)
builder.Services.AddSingleton<Pikura.Core.Settings.AccountService>();

// Local library index (same database)
builder.Services.AddSingleton<Pikura.Core.Data.LibraryRepository>(provider =>
    new Pikura.Core.Data.LibraryRepository(dbPath, provider.GetRequiredService<ILogger<Pikura.Core.Data.LibraryRepository>>()));
builder.Services.AddSingleton<Pikura.Core.Services.Library.LibraryIndexService>(provider =>
    new Pikura.Core.Services.Library.LibraryIndexService(
        provider.GetRequiredService<Pikura.Core.Data.LibraryRepository>(),
        provider.GetRequiredService<SettingsService>(),
        provider.GetRequiredService<ILogger<Pikura.Core.Services.Library.LibraryIndexService>>(),
        provider.GetService<Pikura.Core.Settings.AccountService>(),
        provider.GetService<PixivClient>(),
        null));

// DownloadCoordinator
builder.Services.AddSingleton<DownloadCoordinator>(provider =>
{
    var dl = provider.GetRequiredService<PixivDownloadService>();
    dl.Library ??= provider.GetService<Pikura.Core.Services.Library.LibraryIndexService>();
    return new DownloadCoordinator(
        provider.GetRequiredService<PixivClient>(),
        dl,
        provider.GetRequiredService<SettingsService>(),
        provider.GetRequiredService<DownloadJobRepository>(),
        provider.GetRequiredService<FanboxClient>(),
        provider.GetRequiredService<ILogger<DownloadCoordinator>>(),
        resizeService: null,
        accountService: provider.GetRequiredService<Pikura.Core.Settings.AccountService>());
});

// Schedule executor
builder.Services.AddSingleton<ScheduleExecutorService>();

// IPC server
builder.Services.AddSingleton<IpcServer>();

// Background worker
builder.Services.AddHostedService<AgentWorker>();

// Windows Service support (no-op on Linux/macOS)
if (OperatingSystem.IsWindows())
    builder.Services.AddWindowsService(options => options.ServiceName = "Pikura Agent");

var host = builder.Build();
await host.RunAsync();
