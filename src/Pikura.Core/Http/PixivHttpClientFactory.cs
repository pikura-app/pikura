using System.Net;
using System.Security.Cryptography.X509Certificates;
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
        ApplyNetworkSettings();
        // Http3FallbackHandler: retry over HTTP/3 (QUIC) when a network breaks TLS-over-TCP
        // (e.g. in-flight Wi-Fi) — the same path browsers use to reach pixiv there.
        _client = new HttpClient(new Http3FallbackHandler(_handler), disposeHandler: false)
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

    private void ApplyNetworkSettings()
    {
        // Proxy: when the user enables "Use proxy", honor it. An empty address means
        // "use the system proxy" (e.g. VPN/proxy software); a provided address uses that
        // explicit WebProxy. When disabled, do not use any proxy.
        if (_settings.Current.UseProxy)
        {
            _handler.UseProxy = true;
            _handler.Proxy = string.IsNullOrWhiteSpace(_settings.Current.ProxyAddress)
                ? null
                : new WebProxy(_settings.Current.ProxyAddress);
        }
        else
        {
            _handler.UseProxy = false;
            _handler.Proxy = null;
        }

        // SSL certificate verification
        _handler.SslOptions.RemoteCertificateValidationCallback =
            _settings.Current.EnableSSLVerification
                ? null
                : (sender, cert, chain, errors) => true;

        // Keep a sensible TLS version range.
        _handler.SslOptions.EnabledSslProtocols =
            System.Security.Authentication.SslProtocols.Tls12 |
            System.Security.Authentication.SslProtocols.Tls13;
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }
}
