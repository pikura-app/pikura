using System.Net;
using Pikura.Core.Settings;

namespace Pikura.Core.Http;

/// <summary>
/// Builds <see cref="HttpClient"/> instances pre-configured with Pixiv cookies,
/// User-Agent and Referer. We use a per-call factory so cookie changes (after
/// login) take effect on the next request without restarting the app.
/// </summary>
public sealed class PixivHttpClientFactory : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _client;

    private string _appliedUserAgent = string.Empty;

    public PixivHttpClientFactory(SettingsService settings)
    {
        _settings = settings;
        _handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Allow more concurrent connections to Pixiv's CDN.
            MaxConnectionsPerServer = 32,
            // Prefer HTTP/2 so multiple image fetches multiplex over fewer TCP connections.
            EnableMultipleHttp2Connections = true,
        };
        _client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        _client.DefaultRequestVersion = new Version(2, 0);
        ApplyCookies();
    }

    /// <summary>Returns the shared client. Cookies/UA are only mutated when the setting changes.</summary>
    public HttpClient GetClient()
    {
        ApplyCookies();
        var ua = _settings.Current.UserAgent ?? string.Empty;
        if (ua != _appliedUserAgent)
        {
            _client.DefaultRequestHeaders.UserAgent.Clear();
            if (!string.IsNullOrWhiteSpace(ua))
                _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
            _appliedUserAgent = ua;
        }
        return _client;
    }

    private void ApplyCookies()
    {
        var sid = _settings.Current.PhpSessId;
        if (!string.IsNullOrWhiteSpace(sid))
        {
            // Replace any existing PHPSESSID by setting a new cookie with same name on the same domain.
            _handler.CookieContainer.Add(new Cookie("PHPSESSID", sid, "/", ".pixiv.net"));
        }

        // cf_clearance is required by some pixiv.net subdomains' Cloudflare bot-management
        // (confirmed: embed.pixiv.net, used for Collection collage thumbnails) even when
        // PHPSESSID alone is valid. Obtained separately — see CloudflareSessionService.
        var cf = _settings.Current.CfClearance;
        if (!string.IsNullOrWhiteSpace(cf))
        {
            _handler.CookieContainer.Add(new Cookie("cf_clearance", cf, "/", ".pixiv.net"));
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }
}
