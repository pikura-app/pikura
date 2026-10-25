using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace Pikura.Avalonia.Services;

/// <summary>Velopack update backend — active only when running from a Velopack install.</summary>
public sealed class VelopackUpdateBackend : IAppUpdateBackend
{
    private readonly SettingsService _settings;
    private readonly ILogger<VelopackUpdateBackend> _logger;
    private UpdateManager? _mgr;

    public VelopackUpdateBackend(SettingsService settings, ILogger<VelopackUpdateBackend> logger)
    {
        _settings = settings;
        _logger = logger;
        try
        {
            var src = new GithubSource(
                "https://github.com/pikura-app/pikura",
                accessToken: null,
                prerelease: settings.Current.UpdateChannel == "PreRelease");
            _mgr = new UpdateManager(src);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Velopack UpdateManager init failed"); }
    }

    public bool IsInstalled => OperatingSystem.IsWindows() && (_mgr?.IsInstalled ?? false);

    public async Task<bool> DownloadAndApplyAsync(IProgress<int>? progress, CancellationToken ct)
    {
        if (_mgr == null || !_mgr.IsInstalled) return false;
        try
        {
            var info = await _mgr.CheckForUpdatesAsync();
            if (info == null) return false;
            await _mgr.DownloadUpdatesAsync(info, x => progress?.Report(x), ct);
            // The Agent may run as a service from the install dir — stop it so the
            // files Velopack replaces aren't locked (restarted by the after-update hook).
            AgentServiceGuard.StopService();
            _settings.Flush();
            _mgr.ApplyUpdatesAndRestart(info);
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "Velopack update failed"); throw; }
    }
}
