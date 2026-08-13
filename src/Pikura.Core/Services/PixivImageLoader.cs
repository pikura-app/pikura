using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Pikura.Core.Http;
using SkiaSharp;

namespace Pikura.Core.Services;

/// <summary>
/// Thumbnail size hint for optimizing download speed vs quality.
/// </summary>
public enum ThumbnailSize
{
    /// <summary>250px square crop (fastest, good for dense grids).</summary>
    Small,
    /// <summary>540px on long edge (default, keeps aspect ratio).</summary>
    Medium,
    /// <summary>Full original (slowest, best quality).</summary>
    Original
}

/// <summary>
/// Downloads Pixiv CDN image bytes with the mandatory
/// <c>Referer: https://www.pixiv.net/</c> header. Uses a 4-tier cache:
/// (1) in-memory decoded bitmap cache (instant for already-decoded images),
/// (2) in-memory deduplicated tasks (instant for in-flight + recent fetches),
/// (3) on-disk byte cache under <c>%LOCALAPPDATA%\PixivUtil\imgcache</c>
/// so thumbnails survive app restarts, and (4) a network fallback when
/// none of the caches hit. A semaphore caps concurrent network fetches so
/// scrolling a large gallery doesn't open hundreds of sockets at once.
/// </summary>
public sealed class PixivImageLoader : IDisposable
{
    private const string Referer = "https://www.pixiv.net/";
    // 14 days on disk before re-fetching (Pixiv URLs are content-hashed so
    // anything we cached under a given URL is still valid as long as the
    // URL is).
    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromDays(14);

    private static readonly string DiskCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pikura", "imgcache");

    private static readonly string FailureLogPath = Path.Combine(
        Path.GetTempPath(), "pikura_image_failures.txt");
    private static readonly object FailureLogLock = new();

    private readonly PixivHttpClientFactory _factory;
    private readonly ILogger<PixivImageLoader> _logger;
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _memoryCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SKBitmap> _bitmapCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate;
    private readonly SemaphoreSlim _bitmapGate = new(1, 1); // Protects bitmap cache eviction

    // Device-aware limits — scaled by DeviceCapabilityService instead of hardcoded constants,
    // so a low-end machine doesn't open as many concurrent sockets or hold as many decoded
    // bitmaps in memory as a high-end desktop would. Pixiv's i.pximg.net is HTTP/2 multiplexed
    // and tolerates plenty of parallel requests on capable hardware (the original tuning here
    // bumped 24 → 48 concurrent fetches because the bottleneck was semaphore wait, not the
    // network), but that same concurrency is wasted/costly on a constrained machine.
    private readonly int _maxMemoryEntries;
    private readonly int _maxBitmapEntries;

    public PixivImageLoader(PixivHttpClientFactory factory, ILogger<PixivImageLoader> logger, DeviceCapabilityService? deviceCapability = null)
    {
        _factory = factory;
        _logger = logger;
        var caps = deviceCapability ?? new DeviceCapabilityService();
        _maxMemoryEntries = caps.MaxByteCacheEntries;
        _maxBitmapEntries = caps.MaxBitmapCacheEntries;
        _gate = new SemaphoreSlim(caps.MaxImageFetchConcurrency, caps.MaxImageFetchConcurrency);
        try { Directory.CreateDirectory(DiskCacheRoot); } catch { /* best-effort */ }
    }

    public async Task<byte[]?> FetchBytesAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // Hard cap on the in-memory map so it can't grow without bound.
        if (_memoryCache.Count > _maxMemoryEntries)
        {
            foreach (var kv in _memoryCache.Take(_memoryCache.Count - _maxMemoryEntries / 2))
                _memoryCache.TryRemove(kv.Key, out _);
        }

        // GetOrAdd is not atomic: if the existing task is faulted or cancelled,
        // replace it so the next caller gets a fresh attempt.
        if (_memoryCache.TryGetValue(url, out var existing))
        {
            if (existing.IsCompletedSuccessfully)
            {
                // Don't cache a failed/null fetch forever — transient CDN errors
                // or earlier HTTP/2 blocks should be retryable.
                if (existing.Result is not null) return existing.Result;
                _memoryCache.TryRemove(new KeyValuePair<string, Task<byte[]?>>(url, existing));
            }
            if (existing.IsFaulted || existing.IsCanceled)
                _memoryCache.TryRemove(new KeyValuePair<string, Task<byte[]?>>(url, existing));
        }

        // Use CancellationToken.None for the cached task so one caller
        // cancelling doesn't poison the shared result for others.
        var task = _memoryCache.GetOrAdd(url, u => FetchInternalAsync(u, CancellationToken.None));
        var bytes = await task.ConfigureAwait(false);
        if (bytes is null) _memoryCache.TryRemove(url, out _);
        return bytes;
    }

    private async Task<byte[]?> FetchInternalAsync(string url, CancellationToken ct)
    {
        // Try the on-disk cache first — avoids hitting Pixiv's CDN at all
        // for thumbnails the user has seen before.
        var diskPath = GetDiskPath(url);
        try
        {
            if (File.Exists(diskPath))
            {
                var fi = new FileInfo(diskPath);
                if (fi.Length > 0 && DateTime.UtcNow - fi.LastWriteTimeUtc <= DiskCacheTtl)
                {
                    return await File.ReadAllBytesAsync(diskPath, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disk-cache read failed for {Url}", url);
        }

        // Throttle network fetches so we don't open dozens of sockets at once.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        byte[]? bytes;
        try
        {
            bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
        }
        finally
        {
            // Release immediately after download so the slot is free for the next fetch
            // while we do the (potentially slow) disk write below.
            _gate.Release();
        }
        if (bytes is not null && bytes.Length > 0)
        {
            try
            {
                var dir = Path.GetDirectoryName(diskPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(diskPath, bytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Disk-cache write failed for {Url}", url); }
        }
        return bytes;
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _factory.GetClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", Referer);
            // i.pximg.net does not reliably serve images over HTTP/2 in all regions;
            // force HTTP/1.1 for the CDN requests to match browser/curl behavior.
            req.Version = HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Thumbnail {Url} -> {Code}", url, resp.StatusCode);
                LogFailure(url, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}");
                return null;
            }
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail fetch failed for {Url}", url);
            LogFailure(url, ex.Message);
            return null;
        }
    }

    private static string GetDiskPath(string url)
    {
        // SHA-1 over the URL gives us a deterministic, filesystem-safe key
        // and avoids issues with URLs containing chars Windows rejects.
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(Encoding.UTF8.GetBytes(url), hash);
        var name = Convert.ToHexString(hash);
        // Two-char shard prevents any single dir from exploding to 100k+ files.
        return Path.Combine(DiskCacheRoot, name[..2], name);
    }

    /// <summary>
    /// Fetches and decodes an image into an SKBitmap, with a decoded-bitmap memory cache.
    /// This is useful when the same image is shown in multiple views (e.g., Gallery + History).
    /// The bitmap cache is separate from the byte cache - it stores decoded SKBitmap objects
    /// to avoid repeated JPEG/PNG decoding overhead.
    /// </summary>
    /// <param name="url">Image URL to fetch.</param>
    /// <param name="size">Thumbnail size hint (modifies Pixiv URLs to request smaller/larger versions).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SKBitmap?> FetchBitmapAsync(string url, ThumbnailSize size = ThumbnailSize.Medium, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // Apply thumbnail size transformation
        var sizedUrl = ConvertUrlForThumbnailSize(url, size);

        // Check bitmap cache first (fastest - already decoded). Must hold _bitmapGate for this
        // read too, not just the eviction/write path below — otherwise a concurrent eviction can
        // Dispose() the very SKBitmap this thread is mid-Copy() on (both run on high-concurrency
        // fire-and-forget thumbnail loads), causing a use-after-free that crashes natively in
        // SkiaSharp (0xC0000005) instead of throwing a catchable .NET exception.
        await _bitmapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bitmapCache.TryGetValue(sizedUrl, out var cachedBitmap))
            {
                // Return a copy so callers can dispose without affecting cache
                return cachedBitmap.Copy();
            }
        }
        finally { _bitmapGate.Release(); }

        // Not in bitmap cache - fetch bytes and decode
        var bytes = await FetchBytesAsync(sizedUrl, ct).ConfigureAwait(false);
        if (bytes is null || ct.IsCancellationRequested) return null;

        // Decode off-thread to avoid blocking
        var bitmap = await Task.Run(() =>
        {
            try { return SKBitmap.Decode(bytes); }
            catch { return null; }
        }, ct).ConfigureAwait(false);

        if (bitmap is null) return null;

        // Add to bitmap cache (with eviction if needed)
        await _bitmapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bitmapCache.Count >= _maxBitmapEntries)
            {
                // Evict oldest entries (simple strategy: remove first 32)
                var toRemove = _bitmapCache.Take(32).ToList();
                foreach (var kv in toRemove)
                {
                    _bitmapCache.TryRemove(kv.Key, out var oldBmp);
                    oldBmp?.Dispose();
                }
            }
            _bitmapCache.TryAdd(sizedUrl, bitmap.Copy());
        }
        finally { _bitmapGate.Release(); }

        return bitmap;
    }

    /// <summary>
    /// Converts a Pixiv thumbnail URL to request a specific size.
    /// Pixiv uses URL patterns to control thumbnail size:
    /// - square1200: 250x250 crop (small)
    /// - master1200: 540px on long edge (medium)
    /// - original: full resolution
    /// </summary>
    public static string ConvertUrlForThumbnailSize(string url, ThumbnailSize size)
    {
        if (string.IsNullOrEmpty(url)) return url;

        return size switch
        {
            ThumbnailSize.Small => url
                .Replace("_master1200", "_square1200")
                .Replace("/540x540_70/", "/250x250_80_a2/"),
            ThumbnailSize.Medium => url
                .Replace("_square1200", "_master1200")
                .Replace("/250x250_80_a2/", "/540x540_70/"),
            ThumbnailSize.Original => ConvertToOriginalUrl(url),
            _ => url
        };
    }

    private static string ConvertToOriginalUrl(string url)
    {
        // Pixiv original URLs are not always derivable; this handles the common
        // i.pximg.net master/square thumbnail patterns by stripping the crop and
        // master suffix. The result is a .jpg guess; callers should fall back to
        // the source URL if the guess fails.
        if (!url.Contains("pximg.net", StringComparison.OrdinalIgnoreCase))
            return url;

        var original = System.Text.RegularExpressions.Regex.Replace(url, @"/c/[^/]+/", "/");
        original = original.Replace("/img-master/", "/img-original/", StringComparison.OrdinalIgnoreCase);
        original = System.Text.RegularExpressions.Regex.Replace(original, @"_(master|square)1200(\.[a-zA-Z0-9]+)$", "$2");
        return original;
    }

    private static void LogFailure(string url, string reason)
    {
        try
        {
            var line = $"[{DateTime.Now:O}] {url} | {reason}{Environment.NewLine}";
            lock (FailureLogLock)
                File.AppendAllText(FailureLogPath, line);
        }
        catch { /* logging must not break image loading */ }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _bitmapGate.Dispose();
        // Dispose all cached bitmaps
        foreach (var kv in _bitmapCache)
            kv.Value?.Dispose();
        _bitmapCache.Clear();
    }
}
