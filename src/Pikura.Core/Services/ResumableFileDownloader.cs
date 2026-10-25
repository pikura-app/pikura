using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pikura.Core.Services;

/// <summary>
/// Downloads a URL to <c>{dest}.part</c> then moves it to <c>{dest}</c>, with
/// byte-range resume support: on transient failures the partial file is kept and
/// the next attempt sends <c>Range: bytes=N-</c> (plus <c>If-Range</c> when a
/// validator was persisted in <c>{dest}.part.meta</c>). Integrity failures and
/// size-filter skips delete the partial file as before.
/// </summary>
internal sealed class ResumableFileDownloader
{
    /// <summary>Transient failures keep the .part file; integrity failures throw this so callers delete it.</summary>
    internal sealed class IntegrityException : IOException
    {
        public IntegrityException(string message) : base(message) { }
    }

    internal sealed class PartialMeta
    {
        [JsonPropertyName("etag")] public string? Etag { get; set; }
        [JsonPropertyName("lastModified")] public string? LastModified { get; set; }
        [JsonPropertyName("total")] public long? Total { get; set; }
    }

    /// <summary>Sends a GET; <paramref name="configure"/> may add Range/If-Range headers. Returns the live response.</summary>
    internal delegate Task<HttpResponseMessage> SendFunc(string url, Action<HttpRequestMessage>? configure, CancellationToken ct);

    internal sealed class Context
    {
        public int BufferSize { get; init; } = 81920;
        /// <summary>Inactivity timeout for the stream read (0 = none).</summary>
        public int TimeoutSec { get; init; }
        public int MinFileSizeKB { get; init; }
        public int MaxFileSizeKB { get; init; }
        /// <summary>Optional content validator (magic-byte check). Throw-free; return false to fail integrity.</summary>
        public Func<string, CancellationToken, Task<bool>>? ValidateAsync { get; init; }
        /// <summary>Progress callback: (bytesSoFar including resumed prefix, expectedTotal).</summary>
        public Action<long, long?>? Progress { get; init; }
        /// <summary>Called once when a ranged resume begins: (alreadyHave, expectedTotal).</summary>
        public Action<long, long?>? Resuming { get; init; }
        public Action<string>? Diag { get; init; }
    }

    /// <summary>Result of a download attempt.</summary>
    internal enum Outcome { Completed, SkippedBySizeFilter }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destPath"/>.
    /// Returns <see cref="Outcome.Completed"/> or <see cref="Outcome.SkippedBySizeFilter"/>.
    /// </summary>
    internal static async Task<Outcome> DownloadAsync(
        SendFunc send, string url, string destPath, Context ctx, CancellationToken ct)
    {
        var tmp = destPath + ".part";
        var metaPath = tmp + ".meta";

        while (true)
        {
            var existingLen = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
            var meta = LoadMeta(metaPath);
            var wantResume = existingLen > 0;

            using var resp = await send(url, wantResume
                ? req =>
                {
                    req.Headers.Range = new RangeHeaderValue(existingLen, null);
                    if (!string.IsNullOrEmpty(meta?.Etag) && EntityTagHeaderValue.TryParse(meta.Etag, out var tag))
                        req.Headers.IfRange = new RangeConditionHeaderValue(tag);
                    else if (!string.IsNullOrEmpty(meta?.LastModified) && DateTimeOffset.TryParse(meta.LastModified, out var lm))
                        req.Headers.IfRange = new RangeConditionHeaderValue(lm);
                }
                : (Action<HttpRequestMessage>?)null, ct).ConfigureAwait(false);

            long resumeFrom = 0;
            if (wantResume && resp.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = resp.Content.Headers.ContentRange;
                if (range?.From == existingLen)
                {
                    resumeFrom = existingLen;
                    // If we persisted a total and the server reports a different total, restart.
                    if (meta?.Total is { } storedTotal && range.Length != storedTotal)
                        resumeFrom = 0;
                }
            }
            else if (wantResume && (int)resp.StatusCode == 416)
            {
                // Range unsatisfiable: discard and retry once from scratch.
                resp.Dispose();
                DeletePartial(tmp, metaPath);
                continue;
            }

            if (wantResume && resumeFrom == 0)
            {
                // Stale/unusable partial: discard it.
                DeletePartial(tmp, metaPath);
                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    // The server only sent a fragment — re-request without Range.
                    resp.Dispose();
                    continue;
                }
            }

            resp.EnsureSuccessStatusCode();

            // Persist validators for a future resume attempt.
            SaveMeta(metaPath, new PartialMeta
            {
                Etag = resp.Headers.ETag?.ToString(),
                LastModified = resp.Content.Headers.LastModified?.ToString("R"),
                Total = resp.StatusCode == HttpStatusCode.PartialContent
                    ? resp.Content.Headers.ContentRange?.Length
                    : resp.Content.Headers.ContentLength,
            });

            var expectedTotal = resp.StatusCode == HttpStatusCode.PartialContent
                ? resp.Content.Headers.ContentRange?.Length
                : resp.Content.Headers.ContentLength;

            if (resumeFrom > 0)
                ctx.Resuming?.Invoke(resumeFrom, expectedTotal);

            long readTotal = resumeFrom;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (ctx.TimeoutSec > 0) timeoutCts.CancelAfter(TimeSpan.FromSeconds(ctx.TimeoutSec));
            var dlCt = timeoutCts.Token;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
                await using (var src = await resp.Content.ReadAsStreamAsync(dlCt).ConfigureAwait(false))
                await using (var dst = new FileStream(tmp,
                    resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, ctx.BufferSize, useAsync: true))
                {
                    var buffer = new byte[ctx.BufferSize];
                    int read;
                    while ((read = await src.ReadAsync(buffer.AsMemory(0, ctx.BufferSize), dlCt).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), dlCt).ConfigureAwait(false);
                        readTotal += read;
                        ctx.Progress?.Invoke(readTotal, expectedTotal);
                        if (ctx.TimeoutSec > 0) timeoutCts.CancelAfter(TimeSpan.FromSeconds(ctx.TimeoutSec));
                    }
                    await dst.FlushAsync(dlCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Inactivity timeout — transient, keep the .part for resume.
                throw new TimeoutException($"Download timed out after {ctx.TimeoutSec}s of inactivity: {url}");
            }
            // IOException / HttpRequestException / user cancel: keep .part for resume.

            // ---- Integrity checks (fail-fast: delete the .part file on any mismatch) ----
            try
            {
                if (readTotal == 0)
                    throw new IntegrityException($"Empty download for {url}");
                if (expectedTotal.HasValue && readTotal != expectedTotal.Value)
                    throw new IntegrityException($"Truncated download: got {readTotal} bytes, expected {expectedTotal.Value} for {url}");
                var fi = new FileInfo(tmp);
                if (!fi.Exists || fi.Length != readTotal)
                    throw new IntegrityException($"On-disk size mismatch for {tmp}: file={(fi.Exists ? fi.Length : -1)} counter={readTotal}");
                if (ctx.ValidateAsync != null && !await ctx.ValidateAsync(tmp, ct).ConfigureAwait(false))
                    throw new IntegrityException($"Downloaded file is not a recognized image (HTML error page?): {url}");
            }
            catch
            {
                DeletePartial(tmp, metaPath);
                throw;
            }

            if (ctx.MinFileSizeKB > 0 && readTotal < (long)ctx.MinFileSizeKB * 1024)
            {
                DeletePartial(tmp, metaPath);
                ctx.Diag?.Invoke($"  SKIP: file {readTotal} bytes is below MinFileSizeKB={ctx.MinFileSizeKB} KB");
                return Outcome.SkippedBySizeFilter;
            }
            if (ctx.MaxFileSizeKB > 0 && readTotal > (long)ctx.MaxFileSizeKB * 1024)
            {
                DeletePartial(tmp, metaPath);
                ctx.Diag?.Invoke($"  SKIP: file {readTotal} bytes exceeds MaxFileSizeKB={ctx.MaxFileSizeKB} KB");
                return Outcome.SkippedBySizeFilter;
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
            try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { /* best-effort */ }
            return Outcome.Completed;
        }
    }

    private static PartialMeta? LoadMeta(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PartialMeta>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static void SaveMeta(string path, PartialMeta meta)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(meta)); } catch { /* best-effort */ }
    }

    private static void DeletePartial(string tmp, string metaPath)
    {
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { /* best-effort */ }
    }
}
