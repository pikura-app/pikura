using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pikura.Avalonia.Localization;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.Services;

/// <summary>
/// One-time offer to migrate a legacy Inno install to the Velopack install.
/// Downloads Pikura-{ver}-windows.exe, runs it, then runs the Inno uninstaller
/// with /KEEPDATA and exits so the new install takes over.
/// </summary>
public sealed class VelopackMigrationService
{
    private readonly UpdateCheckService _updateCheck;
    private readonly SettingsService _settings;
    private readonly DialogService _dialog;
    private readonly ILogger<VelopackMigrationService> _logger;

    public VelopackMigrationService(UpdateCheckService updateCheck, SettingsService settings,
        DialogService dialog, ILogger<VelopackMigrationService> logger)
    {
        _updateCheck = updateCheck; _settings = settings; _dialog = dialog; _logger = logger;
    }

    /// <summary>True when this process is a legacy Inno install that hasn't migrated/declined.</summary>
    public bool ShouldOffer()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (_updateCheck.IsManagedInstall) return false; // already Velopack
        var exeDir = AppContext.BaseDirectory;
        if (!WindowsInstallInfo.IsInnoInstall(exeDir)) return false;
        var s = _settings.Current;
        return !s.VelopackMigrationDeclined && !s.VelopackMigrationOffered;
    }

    /// <summary>True when a migration may be re-offered via the Settings button (Inno install, not yet Velopack).</summary>
    public bool CanMigrate =>
        OperatingSystem.IsWindows() && !_updateCheck.IsManagedInstall &&
        WindowsInstallInfo.IsInnoInstall(AppContext.BaseDirectory);

    /// <summary>Shows the offer dialog once (marks VelopackMigrationOffered/Declined). Call after the main window is up.</summary>
    public async Task OfferOnceAsync()
    {
        if (!ShouldOffer()) return;
        // Nothing to migrate to until a release ships the Velopack installer — stay quiet
        // (and don't burn the one-time offer) until then.
        try { if (await _updateCheck.GetLatestVelopackSetupAsync() is null) return; }
        catch { return; }
        _settings.Update(s => s.VelopackMigrationOffered = true);
        var yes = await _dialog.ShowConfirmationAsync(
            LocalizationService.Instance["Update_MigrateTitle"],
            LocalizationService.Instance["Update_MigrateMessage"]);
        if (!yes)
        {
            _settings.Update(s => s.VelopackMigrationDeclined = true);
            return;
        }
        await MigrateAsync();
    }

    /// <summary>Runs the full migration. On failure shows an error and leaves the Inno install intact.</summary>
    public async Task MigrateAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var exeDir = AppContext.BaseDirectory;
        var unins = WindowsInstallInfo.FindInnoUninstaller(exeDir);
        var found = await _updateCheck.GetLatestVelopackSetupAsync(ct);
        if (found is null)
        {
            await _dialog.ShowMessageAsync(
                LocalizationService.Instance["Update_MigrateTitle"],
                LocalizationService.Instance["Update_MigrateNoAsset"]);
            return;
        }
        var (ver, url) = found.Value;

        // Download via the existing release-download path (progress 0-100).
        var info = new UpdateInfo(ver, $"Pikura v{ver}", "", "", url);
        string setupPath;
        try { setupPath = await _updateCheck.DownloadUpdateAsync(info, progress ?? new Progress<int>(), ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Velopack setup download failed");
            await _dialog.ShowMessageAsync(LocalizationService.Instance["Update_MigrateTitle"],
                string.Format(LocalizationService.Instance["Update_MigrateFailed"], ex.Message));
            return;
        }

        // Run the Velopack Setup — it installs to %LocalAppData%\PikuraApp and launches the new copy.
        try
        {
            var psi = new ProcessStartInfo(setupPath, "--silent") { UseShellExecute = true };
            var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Setup did not start");
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0) throw new InvalidOperationException($"Setup exited with {proc.ExitCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Velopack setup failed");
            await _dialog.ShowMessageAsync(LocalizationService.Instance["Update_MigrateTitle"],
                string.Format(LocalizationService.Instance["Update_MigrateFailed"], ex.Message));
            return;
        }

        // Inno uninstall with /KEEPDATA, elevated when installed under Program Files.
        if (unins != null)
        {
            var needsElevate = exeDir.StartsWith(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
                || exeDir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = unins,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /KEEPDATA",
                    UseShellExecute = true,
                    Verb = needsElevate ? "runas" : null!,
                });
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Inno uninstaller launch failed — leaving old install"); }
        }
        _settings.Flush();
        Environment.Exit(0);
    }
}
