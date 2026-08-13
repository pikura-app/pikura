using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pikura.Core.Settings;

namespace Pikura.Core.Services;

/// <summary>
/// Checks the GitHub Releases API for a newer version of Pikura,
/// downloads the asset, and launches an updater script that swaps
/// the binary and restarts the application.
/// </summary>
public sealed class UpdateCheckService
{
    public static string CurrentVersion { get; } =
        (System.Reflection.Assembly.GetEntryAssembly()
             ?? System.Reflection.Assembly.GetExecutingAssembly())
            .GetName().Version
            ?.ToString(3)
        ?? "0.0.0";
    private const string Owner           = "pikura-app";
    private const string Repo            = "pikura";
    private const string ReleasesApiUrl  = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    private const string PreReleaseApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases";
    private const string ReleasesPageUrl = $"https://github.com/{Owner}/{Repo}/releases/latest";

    private readonly ILogger<UpdateCheckService> _logger;
    private readonly SettingsService _settings;
    private readonly HttpClient _http;

    public UpdateCheckService(ILogger<UpdateCheckService> logger, SettingsService settings)
    {
        _logger   = logger;
        _settings = settings;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Pikura/" + CurrentVersion);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    // ── Check ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks for a newer release respecting the configured channel and frequency.
    /// Returns <see cref="UpdateInfo"/> if an update is available, null otherwise.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var s = _settings.Current;

        if (!ShouldCheck(s)) return null;

        try
        {
            var url     = s.UpdateChannel == "PreRelease" ? PreReleaseApiUrl : ReleasesApiUrl;
            GitHubRelease? release;

            if (s.UpdateChannel == "PreRelease")
            {
                // List endpoint — pick first pre-release (or any release if none marked)
                var list = await _http.GetFromJsonAsync<GitHubRelease[]>(url, ct).ConfigureAwait(false);
                release  = list?.FirstOrDefault(r => r.Prerelease)
                           ?? list?.FirstOrDefault();
            }
            else
            {
                // Latest endpoint returns the newest non-prerelease by default.
                // Double-check the flag in case the API ever returns a pre-release.
                var candidate = await _http.GetFromJsonAsync<GitHubRelease>(url, ct).ConfigureAwait(false);
                release = candidate?.Prerelease == true ? null : candidate;
            }

            _settings.Update(s2 => s2.LastUpdateCheck = DateTime.UtcNow);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName)) return null;

            var latestTag = release.TagName.TrimStart('v');
            if (CompareSemVer(latestTag, CurrentVersion) <= 0) return null;

            _logger.LogInformation("Update available: {Latest} (current: {Current})", latestTag, CurrentVersion);

            GitHubAsset? asset;
            if (OperatingSystem.IsWindows())      asset = FindWindowsAsset(release);
            else if (OperatingSystem.IsMacOS())   asset = FindMacAsset(release);
            else                                  asset = FindLinuxAsset(release);

            return new UpdateInfo(
                latestTag,
                release.Name ?? $"Pikura v{latestTag}",
                release.Body ?? string.Empty,
                ReleasesPageUrl,
                asset?.BrowserDownloadUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Update check failed (non-fatal)");
            return null;
        }
    }

    /// <summary>
    /// Compares two version tags using SemVer-style precedence rules. Returns
    /// <c>&gt;0</c> if <paramref name="a"/> is newer, <c>&lt;0</c> if older, <c>0</c>
    /// if equal. Handles tags with prerelease suffixes (e.g. <c>1.7.0-beta.1</c>)
    /// where a stable release outranks any prerelease at the same base version.
    /// </summary>
    /// <remarks>
    /// Examples (a vs b → return sign):
    ///   1.7.0       vs 1.6.0        → +1  (newer base)
    ///   1.7.0-beta  vs 1.7.0        → -1  (stable > prerelease at same base)
    ///   1.7.0-beta  vs 1.7.0-alpha  → +1  (lex order of suffix)
    ///   1.7.0       vs 1.7.0        →  0
    /// </remarks>
    public static int CompareSemVer(string a, string b)
    {
        SplitTag(a, out var baseA, out var preA);
        SplitTag(b, out var baseB, out var preB);

        // Compare numeric base parts (major.minor.patch[.build]) component-wise so
        // missing trailing components default to 0 rather than failing parse.
        var partsA = baseA.Split('.');
        var partsB = baseB.Split('.');
        var max = Math.Max(partsA.Length, partsB.Length);
        for (int i = 0; i < max; i++)
        {
            int ai = i < partsA.Length && int.TryParse(partsA[i], out var pa) ? pa : 0;
            int bi = i < partsB.Length && int.TryParse(partsB[i], out var pb) ? pb : 0;
            if (ai != bi) return ai.CompareTo(bi);
        }

        // Same numeric base. Per SemVer 11: a release with NO prerelease suffix
        // is greater than the same base with any prerelease suffix.
        if (preA.Length == 0 && preB.Length == 0) return 0;
        if (preA.Length == 0) return 1;
        if (preB.Length == 0) return -1;

        // Both have suffixes — ordinal compare is fine for our tagging scheme
        // (alpha < beta < rc, numeric increments within each).
        return string.CompareOrdinal(preA, preB);
    }

    private static void SplitTag(string tag, out string baseVersion, out string prerelease)
    {
        var dash = tag.IndexOf('-');
        if (dash < 0) { baseVersion = tag; prerelease = string.Empty; }
        else          { baseVersion = tag[..dash]; prerelease = tag[(dash + 1)..]; }
    }

    private static bool ShouldCheck(AppSettings s)
    {
        return s.UpdateCheckFrequency switch
        {
            "Never"   => false,
            "Daily"   => s.LastUpdateCheck is null || (DateTime.UtcNow - s.LastUpdateCheck.Value).TotalHours >= 24,
            "Weekly"  => s.LastUpdateCheck is null || (DateTime.UtcNow - s.LastUpdateCheck.Value).TotalDays  >= 7,
            _         => true,  // "Startup" — always check
        };
    }

    private static GitHubAsset? FindWindowsAsset(GitHubRelease release)
    {
        if (release.Assets == null) return null;
        // Prefer the Inno Setup installer over portable zip
        var installer = release.Assets.FirstOrDefault(a =>
            a.Name != null &&
            (a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
             a.Name.Contains("install", StringComparison.OrdinalIgnoreCase)) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (installer != null) return installer;
        // Fall back to portable zip
        return release.Assets.FirstOrDefault(a =>
            a.Name != null &&
            a.Name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
            (a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
             a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));
    }

    private static GitHubAsset? FindMacAsset(GitHubRelease release)
    {
        if (release.Assets == null) return null;
        // Prefer DMG, fall back to zip
        return release.Assets.FirstOrDefault(a =>
                   a.Name != null && a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
               ?? release.Assets.FirstOrDefault(a =>
                   a.Name != null && a.Name.Contains("mac", StringComparison.OrdinalIgnoreCase) &&
                   a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    private static GitHubAsset? FindLinuxAsset(GitHubRelease release)
    {
        if (release.Assets == null) return null;
        // Prefer AppImage, fall back to tar.gz
        return release.Assets.FirstOrDefault(a =>
                   a.Name != null && a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
               ?? release.Assets.FirstOrDefault(a =>
                   a.Name != null && a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                   a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));
    }

    // ── Release notes for a specific version ──────────────────────────────────

    /// <summary>
    /// Fetches release notes for the given version tag from GitHub, e.g. "1.0.9".
    /// Returns null if the tag does not exist or the request fails.
    /// </summary>
    public async Task<UpdateInfo?> FetchReleaseNotesAsync(string version, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/v{version}";
            var release = await _http.GetFromJsonAsync<GitHubRelease>(url, ct).ConfigureAwait(false);
            if (release is null) return null;
            return new UpdateInfo(
                version,
                release.Name ?? $"Pikura v{version}",
                release.Body ?? string.Empty,
                ReleasesPageUrl,
                null);
        }
        catch { return null; }
    }

    /// <summary>
    /// Fetches the full published release history from GitHub (newest first), for the About
    /// page's "View Changelog" button — unlike <see cref="FetchReleaseNotesAsync"/>, which only
    /// returns the single current-version release. GitHub's list endpoint paginates at 30 per
    /// page by default; <paramref name="maxReleases"/> caps how many pages we walk.
    /// </summary>
    public async Task<List<UpdateInfo>> FetchAllReleasesAsync(int maxReleases = 100, CancellationToken ct = default)
    {
        var result = new List<UpdateInfo>();
        try
        {
            const int perPage = 100;
            var page = 1;
            while (result.Count < maxReleases)
            {
                var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page={perPage}&page={page}";
                var releases = await _http.GetFromJsonAsync<GitHubRelease[]>(url, ct).ConfigureAwait(false);
                if (releases is null || releases.Length == 0) break;

                foreach (var r in releases)
                {
                    if (string.IsNullOrWhiteSpace(r.TagName)) continue;
                    var version = r.TagName.TrimStart('v');
                    result.Add(new UpdateInfo(
                        version,
                        r.Name ?? $"Pikura v{version}",
                        r.Body ?? string.Empty,
                        ReleasesPageUrl,
                        null));
                    if (result.Count >= maxReleases) break;
                }

                if (releases.Length < perPage) break; // last page
                page++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FetchAllReleases failed (non-fatal)");
        }
        return result;
    }

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the update asset to a temp file, reporting progress 0–100.
    /// Returns the local path of the downloaded file.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(
        UpdateInfo info,
        IProgress<int> progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            throw new InvalidOperationException("No download URL available for this release.");

        var ext      = Path.GetExtension(info.DownloadUrl.Split('?')[0]);
        var destPath = Path.Combine(Path.GetTempPath(), $"Pikura-update-{info.Version}{ext}");

        using var response = await _http
            .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total  = response.Content.Headers.ContentLength ?? -1L;
        var buffer = new byte[81920];
        long read  = 0;

        await using var src  = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dest = File.Create(destPath);

        int bytesRead;
        while ((bytesRead = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            read += bytesRead;
            if (total > 0) progress.Report((int)(read * 100 / total));
        }
        progress.Report(100);

        _logger.LogInformation("Update downloaded to {Path}", destPath);
        return destPath;
    }

    // ── Install & restart ─────────────────────────────────────────────────────

    /// <summary>
    /// Writes a batch/shell script that waits for this process to exit,
    /// replaces the executable, then relaunches it. Then exits the app.
    /// </summary>
    public async Task InstallAndRestartAsync(string downloadedPath)
    {
        // When running as an AppImage the process is the extracted squashfs binary,
        // not the .AppImage file itself.  The AppImage runtime always sets $APPIMAGE
        // to the real .AppImage path, so prefer that on Linux.
        string rawPath;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            rawPath = Environment.GetEnvironmentVariable("APPIMAGE")
                      ?? Environment.ProcessPath
                      ?? Path.Combine(AppContext.BaseDirectory, "Pikura");
        }
        else
        {
            rawPath = Environment.ProcessPath
                      ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Pikura.exe" : "Pikura");
        }

        if (string.IsNullOrEmpty(rawPath))
            throw new InvalidOperationException("Cannot determine current executable path.");

        // Environment.ProcessPath can return a \??\ kernel-style prefix on Windows
        // when running as a single-file executable — strip it so cmd.exe can use it.
        var exePath = rawPath.StartsWith(@"\??\", StringComparison.Ordinal)
            ? rawPath[4..]
            : rawPath;

        if (OperatingSystem.IsWindows())
            await LaunchWindowsUpdaterAsync(downloadedPath, exePath, _logger);
        else
        {
            LaunchUnixUpdater(downloadedPath, exePath);
            // Exit the app — the script takes over from here
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
    }

    private static async Task LaunchWindowsUpdaterAsync(string src, string dest, ILogger logger)
    {
        var srcName = Path.GetFileName(src).ToLowerInvariant();

        // If the downloaded file is an Inno Setup installer, run it silently.
        // /VERYSILENT = no UI at all, /CLOSEAPPLICATIONS = close running instances,
        // /RESTARTAPPLICATIONS = relaunch after install,
        // /DIR = install over the current exe's directory so the right copy is replaced.
        if (srcName.Contains("setup") || srcName.Contains("install") || srcName.Contains("update"))
        {
            var installDir = Path.GetDirectoryName(dest) ?? AppContext.BaseDirectory;
            logger.LogInformation("Launching installer: {Src} /DIR={InstallDir}", src, installDir);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName        = src,
                Arguments       = $"/VERYSILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /DIR=\"{installDir}\"",
                UseShellExecute = true,
                Verb            = "runas",
            };
            System.Diagnostics.Process? proc = null;
            try { proc = System.Diagnostics.Process.Start(psi); }
            catch (Exception ex) { logger.LogError(ex, "Process.Start failed for installer"); throw; }
            logger.LogInformation("Installer process started: PID={Pid}", proc?.Id);
            // Wait long enough for Inno's CloseApplications to enumerate and close our
            // process before we exit. Inno marks processes it closes for RestartApplications;
            // if we self-kill first it never records us and won't relaunch after install.
            await System.Threading.Tasks.Task.Delay(3000);
            logger.LogInformation("Exiting for installer takeover");
            Environment.Exit(0);
            return;
        }

        // Portable .exe — wait for process to exit then copy over and relaunch.
        var script = Path.Combine(Path.GetTempPath(), "pikura_update.bat");
        File.WriteAllText(script, $"""
            @echo off
            echo Waiting for Pikura to exit...
            timeout /t 2 /nobreak >nul
            :waitloop
            tasklist /fi "imagename eq Pikura.exe" 2>nul | find /i "Pikura.exe" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto waitloop
            )
            echo Installing update...
            copy /y "{src}" "{dest}"
            echo Restarting Pikura...
            start "" "{dest}"
            del "%~f0"
            """);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "cmd.exe",
            Arguments       = $"/c \"{script}\"",
            CreateNoWindow  = true,
            UseShellExecute = false,
        });
    }

    private static void LaunchUnixUpdater(string src, string dest)
    {
        var script      = Path.Combine(Path.GetTempPath(), "pikura_update.sh");
        var srcLower    = src.ToLowerInvariant();
        string installBlock;

        if (srcLower.EndsWith(".dmg"))
        {
            // macOS DMG: mount, copy .app bundle to /Applications, unmount
            // $$""" allows both C# interpolation ({src}) and literal shell $ signs ($$MOUNT, $$NF)
            installBlock = $$"""
                MOUNT=$(hdiutil attach "{{src}}" -nobrowse -noverify | awk 'END{print $NF}')
                if [ -d "$MOUNT/Pikura.app" ]; then
                  rm -rf /Applications/Pikura.app
                  cp -R "$MOUNT/Pikura.app" /Applications/
                  RELAUNCH=/Applications/Pikura.app/Contents/MacOS/Pikura
                else
                  RELAUNCH="{{dest}}"
                fi
                hdiutil detach "$MOUNT" -quiet
                """;
        }
        else if (srcLower.EndsWith(".appimage"))
        {
            // Linux AppImage: replace in-place
            installBlock = $"""
                cp -f "{src}" "{dest}"
                chmod +x "{dest}"
                RELAUNCH="{dest}"
                """;
        }
        else if (srcLower.EndsWith(".tar.gz") || srcLower.EndsWith(".tgz"))
        {
            // Linux tar.gz: extract into the same directory as the current binary
            var destDir = Path.GetDirectoryName(dest) ?? "/tmp";
            installBlock = $"""
                tar -xzf "{src}" -C "{destDir}"
                chmod +x "{dest}"
                RELAUNCH="{dest}"
                """;
        }
        else if (srcLower.EndsWith(".zip"))
        {
            // macOS zip fallback: unzip and copy .app
            var tmpDir = Path.Combine(Path.GetTempPath(), "pikura_update_extract");
            installBlock = $"""
                mkdir -p "{tmpDir}"
                unzip -o "{src}" -d "{tmpDir}"
                if [ -d "{tmpDir}/Pikura.app" ]; then
                  rm -rf /Applications/Pikura.app
                  cp -R "{tmpDir}/Pikura.app" /Applications/
                  RELAUNCH=/Applications/Pikura.app/Contents/MacOS/Pikura
                else
                  cp -f "{tmpDir}/Pikura" "{dest}" 2>/dev/null || true
                  chmod +x "{dest}"
                  RELAUNCH="{dest}"
                fi
                rm -rf "{tmpDir}"
                """;
        }
        else
        {
            // Generic fallback: treat as raw binary
            installBlock = $"""
                cp -f "{src}" "{dest}"
                chmod +x "{dest}"
                RELAUNCH="{dest}"
                """;
        }

        File.WriteAllText(script, $"""
            #!/bin/bash
            sleep 2
            while pgrep -x Pikura > /dev/null; do sleep 1; done
            {installBlock}
            "$RELAUNCH" &
            rm -- "$0"
            """);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/chmod", Arguments = $"+x \"{script}\"", UseShellExecute = false
        })?.WaitForExit();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "/bin/bash",
            Arguments       = $"\"{script}\"",
            UseShellExecute = false,
        });
    }

    // ── Private types ─────────────────────────────────────────────────────────

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]   public string?        TagName    { get; init; }
        [JsonPropertyName("name")]       public string?        Name       { get; init; }
        [JsonPropertyName("body")]       public string?        Body       { get; init; }
        [JsonPropertyName("assets")]     public GitHubAsset[]? Assets     { get; init; }
        [JsonPropertyName("prerelease")] public bool           Prerelease { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]                 public string? Name                { get; init; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl  { get; init; }
    }
}

/// <summary>Information about an available update.</summary>
public sealed record UpdateInfo(
    string  Version,
    string  Title,
    string  ReleaseNotes,
    string  ReleasePageUrl,
    string? DownloadUrl);
