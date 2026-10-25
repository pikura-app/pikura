using System.Net;
using System.Net.Http.Headers;
using Pikura.Core.Services;
using Xunit;

namespace Pikura.Core.Tests;

public class ResumableFileDownloaderTests : IDisposable
{
    private readonly string _dir;
    public ResumableFileDownloaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pikura-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] JpgBytes(int len)
    {
        var b = new byte[len];
        b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0;
        for (var i = 4; i < len; i++) b[i] = (byte)(i % 251);
        return b;
    }

    private static HttpResponseMessage Full(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage Partial(byte[] bytes, long from, long total)
    {
        var c = new ByteArrayContent(bytes[(int)from..]);
        c.Headers.ContentRange = new ContentRangeHeaderValue(from, total - 1, total);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = c };
    }

    private static Task<HttpResponseMessage> SendReturning(Func<HttpResponseMessage> factory) =>
        Task.FromResult(factory());

    [Fact]
    public async Task InterruptedDownload_ResumesWithRange_AndCompletes()
    {
        var payload = JpgBytes(10000);
        var dest = Path.Combine(_dir, "img.jpg");
        var calls = new List<HttpRequestMessage>();
        ResumableFileDownloader.SendFunc send = (url, cfg, ct) =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            cfg?.Invoke(req);
            calls.Add(req);
            return SendReturning(() => Partial(payload, req.Headers.Range!.Ranges.First().From!.Value, payload.Length));
        };

        // Seed a .part as if a previous run died mid-stream.
        await File.WriteAllBytesAsync(dest + ".part", payload[..6000]);

        var outcome = await ResumableFileDownloader.DownloadAsync(send, "http://x/img", dest,
            new ResumableFileDownloader.Context(), CancellationToken.None);

        Assert.Equal(ResumableFileDownloader.Outcome.Completed, outcome);
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        Assert.False(File.Exists(dest + ".part"));
        Assert.False(File.Exists(dest + ".part.meta"));
        var rangeReq = calls[^1];
        Assert.Equal(6000, rangeReq.Headers.Range!.Ranges.First().From);
    }

    [Fact]
    public async Task ServerIgnoresRange_Returns200_RestartsFromScratch()
    {
        var payload = JpgBytes(8000);
        var dest = Path.Combine(_dir, "img.jpg");
        await File.WriteAllBytesAsync(dest + ".part", payload[..3000]);

        var outcome = await ResumableFileDownloader.DownloadAsync(
            (u, cfg, ct) => SendReturning(() => Full(payload)),
            "http://x/img", dest, new ResumableFileDownloader.Context(), CancellationToken.None);

        Assert.Equal(ResumableFileDownloader.Outcome.Completed, outcome);
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Range416_RestartsWithoutRange()
    {
        var payload = JpgBytes(8000);
        var dest = Path.Combine(_dir, "img.jpg");
        await File.WriteAllBytesAsync(dest + ".part", payload[..8000]); // server thinks range is invalid
        var sawRange = new List<bool>();
        var outcome = await ResumableFileDownloader.DownloadAsync(
            (u, cfg, ct) =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, u);
                cfg?.Invoke(req);
                sawRange.Add(req.Headers.Range != null);
                return Task.FromResult(req.Headers.Range != null
                    ? new HttpResponseMessage((HttpStatusCode)416) { Content = new ByteArrayContent([]) }
                    : Full(payload));
            },
            "http://x/img", dest, new ResumableFileDownloader.Context(), CancellationToken.None);

        Assert.Equal(ResumableFileDownloader.Outcome.Completed, outcome);
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        Assert.Equal(new[] { true, false }, sawRange);
    }

    [Fact]
    public async Task ContentRangeTotalMismatch_WithStoredMeta_DiscardsAndRedownloads()
    {
        var payload = JpgBytes(8000);
        var other = JpgBytes(9000);
        var dest = Path.Combine(_dir, "img.jpg");
        await File.WriteAllBytesAsync(dest + ".part", other[..3000]);
        await File.WriteAllTextAsync(dest + ".part.meta", "{\"total\":9000}");

        var calls = 0;
        var outcome = await ResumableFileDownloader.DownloadAsync(
            (u, cfg, ct) =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, u);
                cfg?.Invoke(req);
                calls++;
                if (req.Headers.Range != null)
                    return SendReturning(() => Partial(payload, 3000, payload.Length)); // total 8000 != stored 9000
                return SendReturning(() => Full(payload));
            },
            "http://x/img", dest, new ResumableFileDownloader.Context(), CancellationToken.None);

        Assert.Equal(ResumableFileDownloader.Outcome.Completed, outcome);
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        Assert.True(calls >= 2); // ranged attempt rejected -> re-requested without Range
    }

    [Fact]
    public async Task IntegrityFailure_DeletesPartAndMeta()
    {
        var dest = Path.Combine(_dir, "img.jpg");
        var html = System.Text.Encoding.ASCII.GetBytes("<html>404</html>");
        var outcome = ResumableFileDownloader.DownloadAsync(
            (u, cfg, ct) => SendReturning(() => Full(html)),
            "http://x/img", dest,
            new ResumableFileDownloader.Context { ValidateAsync = (p, c) => Task.FromResult(false) },
            CancellationToken.None);
        await Assert.ThrowsAsync<ResumableFileDownloader.IntegrityException>(() => outcome);
        Assert.False(File.Exists(dest + ".part"));
        Assert.False(File.Exists(dest + ".part.meta"));
    }

    [Fact]
    public async Task TransientFailure_KeepsPart_ForResume()
    {
        var dest = Path.Combine(_dir, "img.jpg");
        await File.WriteAllBytesAsync(dest + ".part", JpgBytes(4000));

        // Simulate network failure inside SendFunc mid-chain.
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            ResumableFileDownloader.DownloadAsync(
                (u, cfg, ct) => throw new HttpRequestException("boom"),
                "http://x/img", dest, new ResumableFileDownloader.Context(), CancellationToken.None));
        Assert.True(File.Exists(dest + ".part"));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(fn(request));
        }
    }

    [Fact]
    public async Task RateLimit429WithRetryAfter_ThenSucceeds_WithoutSafeMode()
    {
        var tries = 0;
        var handler = new FakeHandler(req =>
        {
            tries++;
            if (tries == 1)
            {
                var r = new HttpResponseMessage((HttpStatusCode)429);
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        var client = new HttpClient(handler);
        var waits = new List<TimeSpan>();
        using var resp = await PixivDownloadService.SendWithRateLimitCoreAsync(
            client, "http://x/api", null, null, safeMode: false,
            (info, ct) => { waits.Add(info.Wait); return Task.Delay(info.Wait, ct); },
            null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, tries);
        Assert.Single(waits);
        Assert.Equal(TimeSpan.FromMilliseconds(1), waits[0]);
    }
}
