using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Desktop notification service for showing native OS notifications.
/// </summary>
public class NotificationService
{
    private readonly ILogger<NotificationService> _logger;
    private bool _isInitialized;
    private string? _iconPath;
    private static readonly HttpClient _thumbClient = new HttpClient();

    public NotificationService(ILogger<NotificationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns a disk path to the Pikura app icon (extracted from embedded resources
    /// on first call). Returns null if extraction fails — callers should treat this
    /// as optional and fall back to platform default icons.
    /// </summary>
    private string? GetIconPath()
    {
        if (_iconPath != null) return _iconPath;

        try
        {
            // Cache under temp so we don't write into the install dir, and use a fixed
            // filename so PowerShell / notify-send / osascript can re-reference it
            // without us re-extracting on every notification.
            var dest = Path.Combine(Path.GetTempPath(), "pikura-notification-icon.png");
            if (!File.Exists(dest))
            {
                using var src = global::Avalonia.Platform.AssetLoader.Open(
                    new Uri("avares://Pikura/Assets/pikura-logo.png"));
                using var dst = File.Create(dest);
                src.CopyTo(dst);
            }
            _iconPath = dest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract notification icon (non-fatal)");
        }
        return _iconPath;
    }

    public void Initialize()
    {
        if (_isInitialized) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows toast notifications are handled via Windows APIs
                _logger.LogInformation("Notification service initialized for Windows");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _logger.LogInformation("Notification service initialized for macOS");
            }
            else
            {
                _logger.LogInformation("Notification service initialized for Linux");
            }

            _isInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize notification service");
        }
    }

    /// <summary>
    /// Shows a desktop notification.
    /// </summary>
    public void ShowNotification(string title, string message, NotificationType type = NotificationType.Info, string? thumbnailUrl = null)
    {
        if (!_isInitialized)
        {
            Initialize();
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                ShowWindowsNotification(title, message, type, thumbnailUrl);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                ShowMacOSNotification(title, message, type);
            }
            else
            {
                ShowLinuxNotification(title, message, type);
            }

            _logger.LogDebug("Notification shown: {Title} - {Message}", title, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show notification");
        }
    }

    private void ShowWindowsNotification(string title, string message, NotificationType type, string? thumbnailUrl = null)
    {
        try
        {
            // Use PowerShell + BurntToast (pre-installed on Windows 10/11) for native toast.
            // Falls back silently if the module is absent.
            var escaped_title   = title.Replace("'", "''").Replace("\"", "\\\"");
            var escaped_message = message.Replace("'", "''").Replace("\"", "\\\"");

            // App icon: prefer the extracted Pikura logo on disk; the raw WinRT path
            // requires the file:/// URI scheme on Windows so the toast XML parser
            // accepts it as an <image> src.
            var iconPath = GetIconPath();
            var hasIcon  = !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath);
            var iconUri  = hasIcon ? new Uri(iconPath!).AbsoluteUri : null;
            var iconPathEscaped = iconPath?.Replace("'", "''");

            // Use thumbnail as hero image if provided (shows artist/artwork image in toast body).
            // pximg.net requires Referer header — download to temp file so the toast XML
            // image loader (which sends no headers) can read it via file:/// URI.
            var thumbUri = !string.IsNullOrEmpty(thumbnailUrl)
                ? DownloadThumbnailToTemp(thumbnailUrl)
                : null;

            var script = $@"
                try {{
                    if (Get-Module -ListAvailable -Name BurntToast) {{
                        Import-Module BurntToast -ErrorAction Stop
                        {(hasIcon
                            ? $"New-BurntToastNotification -Text '{escaped_title}','{escaped_message}' -AppLogo '{iconPathEscaped}'"
                            : $"New-BurntToastNotification -Text '{escaped_title}','{escaped_message}'")}
                    }} else {{
                        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
                        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
                        {BuildWinRtToastXmlScript(hasIcon, iconUri, thumbUri, escaped_title, escaped_message)}
                        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
                        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('pikura-app.Pikura').Show($toast)
                    }}
                }} catch {{ }}";

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName  = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script.Replace("\"", "\\\"")}\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                }
            };
            process.Start();
            // Fire-and-forget — don't block the UI thread waiting for the shell
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show Windows notification");
        }
    }

    private static string UpgradeThumbnailUrl(string url)
    {
        // Profile avatars: _170.jpg and _50.jpg are the only API-provided sizes.
        // Stripping the size suffix serves the original upload (up to ~400–1000px).
        if (url.Contains("/user-profile/"))
            return System.Text.RegularExpressions.Regex.Replace(url, @"_\d+\.jpg$", ".jpg");

        // Artwork master thumbnails: swap the /c/NxN_... resize proxy for a larger one.
        // e.g. /c/250x250_80_a2/ or /c/128x128/ → /c/600x600_80/
        if (url.Contains("i.pximg.net/c/"))
            return System.Text.RegularExpressions.Regex.Replace(url, @"/c/\d+x\d+[^/]*/", "/c/600x600_80/");

        return url;
    }

    private static string? DownloadThumbnailToTemp(string url)
    {
        try
        {
            var upgraded = UpgradeThumbnailUrl(url);
            var ext      = Path.GetExtension(new Uri(upgraded).AbsolutePath);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var tmpPath  = Path.Combine(Path.GetTempPath(), $"pikura-thumb-{Math.Abs(upgraded.GetHashCode())}{ext}");
            if (File.Exists(tmpPath)) return new Uri(tmpPath).AbsoluteUri;

            using var req  = new HttpRequestMessage(HttpMethod.Get, upgraded);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.pixiv.net/");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var resp = _thumbClient.Send(req);
            if (!resp.IsSuccessStatusCode)
            {
                // Fall back to original URL if upgraded version 404s
                if (upgraded != url)
                {
                    using var req2  = new HttpRequestMessage(HttpMethod.Get, url);
                    req2.Headers.TryAddWithoutValidation("Referer", "https://www.pixiv.net/");
                    req2.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                    using var resp2 = _thumbClient.Send(req2);
                    if (!resp2.IsSuccessStatusCode) return null;
                    using var fs2 = File.Create(tmpPath);
                    resp2.Content.ReadAsStream().CopyTo(fs2);
                    return new Uri(tmpPath).AbsoluteUri;
                }
                return null;
            }
            using var fs = File.Create(tmpPath);
            resp.Content.ReadAsStream().CopyTo(fs);
            return new Uri(tmpPath).AbsoluteUri;
        }
        catch { return null; }
    }

    private static string BuildWinRtToastXmlScript(bool hasIcon, string? iconUri, string? thumbUri, string escapedTitle, string escapedMessage)
    {
        // Build the toast XML binding content
        var appLogo = hasIcon ? $@"<image placement=""appLogoOverride"" src=""{iconUri}""/>" : string.Empty;
        var hero    = !string.IsNullOrEmpty(thumbUri) ? $@"<image placement=""hero"" src=""{thumbUri}""/>" : string.Empty;
        var xml = $@"<toast><visual><binding template=""ToastGeneric""><text id=""1""></text><text id=""2""></text>{appLogo}{hero}</binding></visual></toast>";

        return $@"$xml = [Windows.Data.Xml.Dom.XmlDocument]::new()
                        $xml.LoadXml('{xml}')
                        $xml.SelectSingleNode('//text[@id=""1""]').InnerText = '{escapedTitle}'
                        $xml.SelectSingleNode('//text[@id=""2""]').InnerText = '{escapedMessage}'";
    }

    private void ShowMacOSNotification(string title, string message, NotificationType type)
    {
        try
        {
            // Use osascript to show native macOS notifications
            var script = $@"display notification ""{message.Replace("\"", "\\\"")}"" with title ""{title.Replace("\"", "\\\"")}""";
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e '{script}'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show macOS notification");
        }
    }

    private void ShowLinuxNotification(string title, string message, NotificationType type)
    {
        try
        {
            // Use notify-send for Linux desktop notifications
            var urgency = type switch
            {
                NotificationType.Error => "critical",
                NotificationType.Warning => "normal",
                _ => "low"
            };

            // Prefer the Pikura app icon for info/success; keep stock dialog-* glyphs
            // for warning/error so the OS still conveys severity colouring.
            var iconPath = GetIconPath();
            var icon = type switch
            {
                NotificationType.Warning => "dialog-warning",
                NotificationType.Error   => "dialog-error",
                _ => (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                        ? iconPath!
                        : "dialog-information"
            };

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "notify-send",
                    Arguments = $"--urgency={urgency} --icon=\"{icon}\" \"{title}\" \"{message}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show Linux notification");
        }
    }

    /// <summary>
    /// Shows a notification for new artist submissions with artwork ID for click handling.
    /// </summary>
    public void ShowNewSubmissionNotification(string artistName, int count, string? firstArtworkTitle, string? artworkId = null)
    {
        var title = $"🎨 {artistName} posted {count} new artwork{(count > 1 ? "s" : "")}";
        var message = firstArtworkTitle ?? "Click to view";
        var url = artworkId != null ? $"https://www.pixiv.net/en/artworks/{artworkId}" : null;

        // Store for click handling
        _lastNotification = new NotificationClickedEventArgs(title, message, artworkId, url, NotificationType.Info);

        ShowNotification(title, message, NotificationType.Info);
    }

    /// <summary>
    /// Shows a notification when a download job starts.
    /// </summary>
    public void ShowJobStartedNotification(string jobName, int targetCount, string? thumbnailUrl = null)
    {
        var title   = "⬇️ Download started";
        var message = $"{jobName} — {targetCount} item{(targetCount == 1 ? "" : "s")}";
        _lastNotification = new NotificationClickedEventArgs(title, message, null, null, NotificationType.Info);
        ShowNotification(title, message, NotificationType.Info, thumbnailUrl);
    }

    /// <summary>
    /// Shows a notification when a download job fails.
    /// </summary>
    public void ShowJobFailedNotification(string jobName, string? errorMessage, string? thumbnailUrl = null)
    {
        var title   = "❌ Download failed";
        var message = string.IsNullOrEmpty(errorMessage) ? jobName : $"{jobName}\n{errorMessage}";
        _lastNotification = new NotificationClickedEventArgs(title, message, null, null, NotificationType.Error);
        ShowNotification(title, message, NotificationType.Error, thumbnailUrl);
    }

    public void ShowJobPausedNotification(string jobName, int completed, int total, string? thumbnailUrl = null)
    {
        var title   = "⏸ Download paused";
        var message = total > 0
            ? $"{jobName}\n{completed}/{total} artwork{(total == 1 ? "" : "s")} downloaded so far"
            : $"{jobName}\nPaused";
        _lastNotification = new NotificationClickedEventArgs(title, message, null, null, NotificationType.Info);
        ShowNotification(title, message, NotificationType.Info, thumbnailUrl);
    }

    public void ShowJobResumedNotification(string jobName, int completed, int total, string? thumbnailUrl = null)
    {
        var title   = "▶ Download resumed";
        var message = total > 0
            ? $"{jobName}\nResuming from {completed}/{total} artwork{(total == 1 ? "" : "s")}"
            : $"{jobName}\nResuming";
        _lastNotification = new NotificationClickedEventArgs(title, message, null, null, NotificationType.Info);
        ShowNotification(title, message, NotificationType.Info, thumbnailUrl);
    }

    /// <summary>
    /// Shows a notification when a download job completes.
    /// </summary>
    public void ShowJobCompletedNotification(string jobName, int succeeded, int failed, string? firstArtworkId = null, string? thumbnailUrl = null)
    {
        var title = failed > 0 ? "⚠️ Download completed with errors" : "✅ Download complete";
        var message = failed > 0
            ? $"{jobName}\n{succeeded} artwork{(succeeded == 1 ? "" : "s")} · {failed} failed"
            : $"{jobName}\n{succeeded} artwork{(succeeded == 1 ? "" : "s")} downloaded";
        var type = failed > 0 ? NotificationType.Warning : NotificationType.Success;
        var url = firstArtworkId != null ? $"https://www.pixiv.net/en/artworks/{firstArtworkId}" : null;

        _lastNotification = new NotificationClickedEventArgs(title, message, firstArtworkId, url, type);

        ShowNotification(title, message, type, thumbnailUrl);
    }

    private NotificationClickedEventArgs? _lastNotification;

    /// <summary>
    /// Gets the last notification that was shown (for click handling).
    /// </summary>
    public NotificationClickedEventArgs? GetLastNotification() => _lastNotification;

    /// <summary>
    /// Event raised when a notification is clicked.
    /// </summary>
    public event EventHandler<NotificationClickedEventArgs>? NotificationClicked;
}

/// <summary>
/// Event args for notification click events.
/// </summary>
public class NotificationClickedEventArgs : EventArgs
{
    public string Title { get; }
    public string Message { get; }
    public string? ArtworkId { get; }
    public string? Url { get; }
    public NotificationType Type { get; }

    public NotificationClickedEventArgs(string title, string message, string? artworkId = null, string? url = null, NotificationType type = NotificationType.Info)
    {
        Title = title;
        Message = message;
        ArtworkId = artworkId;
        Url = url;
        Type = type;
    }
}

public enum NotificationType
{
    Info,
    Success,
    Warning,
    Error
}
