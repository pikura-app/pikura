using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Security.Authentication;

namespace Pikura.Core.Http;

/// <summary>
/// Some networks (in-flight / hotel Wi-Fi, captive portals, middleboxes) break TLS over TCP to
/// pixiv while browsers still work — browsers reach Cloudflare-fronted pixiv over HTTP/3 (QUIC,
/// UDP). When a TCP TLS handshake fails with a garbled response, retry the request over HTTP/3
/// and remember that host for the rest of the session.
/// </summary>
public sealed class Http3FallbackHandler : DelegatingHandler
{
    private static readonly ConcurrentDictionary<string, bool> Http3Hosts = new(StringComparer.OrdinalIgnoreCase);

    public Http3FallbackHandler(HttpMessageHandler inner) : base(inner) { }

    private static bool Http3Available =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            ? QuicConnection.IsSupported
            : false;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri?.Host ?? "";
        if (Http3Available && Http3Hosts.ContainsKey(host) && request.Content == null)
            return await base.SendAsync(AsHttp3(request), ct).ConfigureAwait(false);

        try
        {
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (Http3Available && request.Content == null
                                              && request.RequestUri?.Scheme == Uri.UriSchemeHttps
                                              && IsBrokenTls(ex) && !ct.IsCancellationRequested)
        {
            var response = await base.SendAsync(AsHttp3(request), ct).ConfigureAwait(false);
            Http3Hosts[host] = true; // HTTP/3 worked where TCP didn't — keep using it for this host
            return response;
        }
    }

    private static bool IsBrokenTls(HttpRequestException ex) =>
        ex.StatusCode == null && (ex.InnerException is AuthenticationException or IOException);

    /// <summary>Copy of a body-less request pinned to HTTP/3 (a sent request can't be resent).</summary>
    private static HttpRequestMessage AsHttp3(HttpRequestMessage original)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        foreach (var h in original.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        foreach (var o in original.Options) ((IDictionary<string, object?>)copy.Options)[o.Key] = o.Value;
        return copy;
    }
}
