using Microsoft.Extensions.Logging;
using Pikura.Core.Http;
using Pikura.Core.Models;
using Pikura.Core.Settings;
using System.Diagnostics;
using System.Text;

namespace Pikura.Core.Services;

/// <summary>Per-file/per-artwork download progress callbacks.</summary>
public sealed record DownloadProgress(string ArtworkId, int PageIndex, int TotalPages, long BytesSoFar, long? TotalBytes);

/// <summary>
/// Streams original-size images from i.pximg.net into a local folder.
/// Honors the mandatory <c>Referer: https://www.pixiv.net/</c> header.
/// </summary>
public sealed class PixivDownloadService
{
    private const string PixivReferer = "https://www.pixiv.net/";
    private const int BufferSize = 81920;

    // Backoff schedule (seconds) used by SendWithRateLimitAsync when SafeMode is on
    // and the server doesn't supply a Retry-After header. Exponential growth keeps us
    // patient as the rate limit holds. Stored as an array (not stackalloc Span) so it
    // can survive across the await boundaries inside the rate-limit loop.
    private static readonly int[] RateLimitBackoffSeconds = { 5, 10, 20, 60 };

    private readonly PixivClient _client;
    private readonly PixivHttpClientFactory _httpFactory;
    private readonly SettingsService _settings;
    private readonly ImageResizeService _resizeService;
    private readonly UgoiraService _ugoiraService;
    private readonly FfmpegService _ffmpegService;
    private readonly ILogger<PixivDownloadService> _logger;

    // Semaphore to limit concurrent downloads (respects MaxConcurrentDownloads setting).
    // Swapped out at runtime when the limit changes; guarded by _semaphoreLock.
    private SemaphoreSlim _downloadSemaphore = new(1, 1);
    private int _currentSemaphoreLimit = 1;
    private readonly object _semaphoreLock = new();

    private static readonly string DiagLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pikura", "download.log");

    private static void Diag(string s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagLog)!);
            File.AppendAllText(DiagLog, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\n");
        }
        catch { }
    }

    public PixivDownloadService(
        PixivClient client,
        PixivHttpClientFactory httpFactory,
        SettingsService settings,
        ImageResizeService resizeService,
        UgoiraService ugoiraService,
        FfmpegService ffmpegService,
        ILogger<PixivDownloadService> logger)
    {
        _client = client;
        _httpFactory = httpFactory;
        _settings = settings;
        _resizeService = resizeService;
        _ffmpegService = ffmpegService;
        _ugoiraService = ugoiraService;
        _logger = logger;

        // Initialize semaphore based on current settings
        UpdateSemaphoreLimit();
    }

    /// <summary>
    /// Updates the download semaphore limit based on MaxConcurrentDownloads setting.
    /// SafeMode overrides: when enabled, limits to 1 concurrent download regardless of setting.
    /// </summary>
    private void UpdateSemaphoreLimit()
    {
        var maxConcurrent = _settings.Current.MaxConcurrentDownloads;
        if (_settings.Current.SafeMode)
            maxConcurrent = 1; // SafeMode enforces sequential downloads

        maxConcurrent = Math.Max(1, maxConcurrent);

        lock (_semaphoreLock)
        {
            if (maxConcurrent == _currentSemaphoreLimit) return;
            // Swap in a new semaphore sized to the new limit. The previous instance is
            // intentionally NOT disposed: in-flight downloads still hold a reference to it
            // (captured in AcquireDownloadSlotAsync) and must be able to Release() on it.
            // SemaphoreSlim does not require disposal unless its AvailableWaitHandle was
            // accessed (we never do), so the GC reclaims the old one safely.
            _downloadSemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            _currentSemaphoreLimit = maxConcurrent;
        }
    }

    /// <summary>
    /// Waits for a download slot to become available (respects MaxConcurrentDownloads).
    /// </summary>
    private async Task<IDisposable> AcquireDownloadSlotAsync(CancellationToken ct)
    {
        UpdateSemaphoreLimit();
        // Capture the current semaphore instance so the matching Release() always targets
        // the same one even if the limit is changed (and the field swapped) mid-download.
        SemaphoreSlim semaphore;
        lock (_semaphoreLock) { semaphore = _downloadSemaphore; }
        await semaphore.WaitAsync(ct);
        return new SemaphoreRelease(semaphore);
    }

    private sealed class SemaphoreRelease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;
        public SemaphoreRelease(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose()
        {
            if (!_disposed) { _disposed = true; _semaphore.Release(); }
        }
    }

    /// <summary>
    /// Downloads every page of <paramref name="artwork"/> to
    /// <c>{DownloadRoot}/{UserName}/{ArtworkId}_pN.ext</c>.
    /// </summary>
    public Task<IReadOnlyList<string>> DownloadArtworkAsync(
        ArtworkPreview artwork,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default,
        SettingsOverride? overrideSettings = null,
        int batchArtworkIndex = -1)
    {
        var preset = overrideSettings?.ImagePreset;
        IReadOnlyCollection<int>? pageIndexes = null;

        if (batchArtworkIndex >= 0 &&
            preset?.DownloadOnlyPagesWithPresets == true &&
            preset.PerPagePresets != null)
        {
            pageIndexes = preset.PerPagePresets
                .Where(kvp => kvp.Key.ArtworkIndex == batchArtworkIndex)
                .Select(kvp => kvp.Key.PageIndex)
                .Distinct()
                .OrderBy(i => i)
                .ToList();
        }
        else if (preset?.SelectedPageIndices is { Count: > 0 } selectedIndices)
        {
            pageIndexes = selectedIndices
                .Where(i => i >= 0 && i < artwork.PageCount)
                .Distinct()
                .OrderBy(i => i)
                .ToList();
        }

        return DownloadArtworkPagesAsync(artwork, pageIndexes, progress, ct, overrideSettings, batchArtworkIndex);
    }

    /// <summary>
    /// Downloads the specified 0-based page indexes of <paramref name="artwork"/>.
    /// Passing <c>null</c> downloads every page.
    /// When <paramref name="overrideSettings"/> is supplied (and not UseGlobalSettings),
    /// its values take precedence over the corresponding global AppSettings values.
    /// </summary>
    public async Task<IReadOnlyList<string>> DownloadArtworkPagesAsync(
        ArtworkPreview artwork,
        IReadOnlyCollection<int>? pageIndexes,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default,
        SettingsOverride? overrideSettings = null,
        int batchArtworkIndex = -1)
    {
        Diag($"=== START artwork {artwork.Id} title='{artwork.Title}' previewPageCount={artwork.PageCount} requestedPages={(pageIndexes is null ? "ALL" : string.Join(",", pageIndexes))}");

        // Ugoira (animated) branch — fetch frame zip, apply preset if specified, run ffmpeg, write configured formats.
        if (artwork.IllustType == 2)
        {
            try
            {
                var dir = ResolveOutputDir(artwork, overrideSettings);
                Directory.CreateDirectory(dir);

                // Get preset first to check for ugoira format overrides
                var settingsOverride = overrideSettings != null && !overrideSettings.UseGlobalSettings ? overrideSettings : null;
                var preset = settingsOverride?.ImagePreset;

                // Check if preset has specific ugoira formats, otherwise use global settings
                var formats = preset?.UgoiraFormats;
                if (formats == null || formats.Count == 0)
                {
                    formats = ResolveUgoiraFormats(_settings.Current);
                }
                else
                {
                    Diag($"UGOIRA: using preset-specific formats: [{string.Join(",", formats)}]");
                }

                // Handle global UgoiraFramesOnly setting - if enabled and no preset, skip video formats
                var settings = _settings.Current;
                bool saveFrames = preset?.SaveUgoiraFrames ?? settings.SaveUgoiraFrames;
                bool framesOnly = preset?.UgoiraFramesOnly ?? settings.UgoiraFramesOnly;

                if (framesOnly && saveFrames)
                {
                    Diag("UGOIRA: FramesOnly mode enabled — skipping video generation, extracting frames only");
                    formats = []; // Clear formats to skip video encoding
                }

                if (formats.Count == 0 && !saveFrames)
                {
                    Diag("UGOIRA: no formats enabled in settings — defaulting to MP4.");
                    formats = [UgoiraFormat.Mp4];
                }

                Diag($"UGOIRA: formats=[{string.Join(",", formats)}] outputDir={dir} saveFrames={saveFrames} framesOnly={framesOnly}");
                bool needsProcessing = preset != null
                    && (preset.DevicePreset != DevicePreset.Original && preset.DevicePreset != DevicePreset.None
                        || preset.Adjustments?.HasAdjustments == true
                        || preset.CropRegion != null);

                IReadOnlyList<string> produced;
                if (needsProcessing)
                {
                    Diag($"UGOIRA: preset '{preset!.Name}' requires processing — extracting and processing frames");
                    produced = await ExportUgoiraWithPresetAsync(artwork.Id, formats, dir, preset, ct).ConfigureAwait(false);
                }
                else if (saveFrames && formats.Count == 0)
                {
                    // Frames-only mode without preset processing
                    Diag("UGOIRA: extracting frames without video encoding");
                    produced = await ExportUgoiraFramesOnlyAsync(artwork.Id, dir, ct).ConfigureAwait(false);
                }
                else if (saveFrames)
                {
                    // Both video and frames
                    Diag("UGOIRA: exporting video formats and extracting frames");
                    produced = await ExportUgoiraWithFramesAsync(artwork.Id, formats, dir, ct).ConfigureAwait(false);
                }
                else
                {
                    Diag("UGOIRA: no preset processing needed — direct export");
                    produced = await _ugoiraService
                        .ExportAsync(artwork.Id, formats, dir, ct)
                        .ConfigureAwait(false);
                }
                Diag($"UGOIRA: produced {produced.Count} file(s)");

                // Optionally keep the source frame zip alongside the outputs.
                if (_settings.Current.KeepUgoiraZip)
                {
                    try
                    {
                        var zip = await _ugoiraService.GetOrDownloadFrameZipAsync(artwork.Id, ct).ConfigureAwait(false);
                        var zipDest = Path.Combine(dir, artwork.Id + "_frames.zip");
                        if (!File.Exists(zipDest)) File.Copy(zip, zipDest);
                    }
                    catch (Exception ex) { Diag($"UGOIRA: keep-zip failed: {ex.Message}"); }
                }
                return produced;
            }
            catch (Exception ex)
            {
                Diag($"UGOIRA: pipeline failed: {ex.GetType().Name}: {ex.Message}");
                _logger.LogError(ex, "Ugoira pipeline failed for {Id}", artwork.Id);
                throw;
            }
        }

        var pages = await _client.GetArtworkPagesAsync(artwork.Id, ct).ConfigureAwait(false);
        Diag($"GetArtworkPagesAsync returned {pages.Count} pages");
        for (var pi = 0; pi < pages.Count; pi++)
        {
            Diag($"  page[{pi}] Original={pages[pi].Urls.Original}");
        }
        if (pages.Count == 0)
        {
            _logger.LogWarning("Artwork {Id} returned no pages", artwork.Id);
            Diag("ABORT: no pages");
            throw new InvalidOperationException($"Artwork {artwork.Id} returned no pages (possibly deleted, private, or rate-limited)");
        }

        var s = _settings.Current;
        var ovr = overrideSettings != null && !overrideSettings.UseGlobalSettings ? overrideSettings : null;

        // Resolve effective values: override wins over global when set
        var effectiveDownloadRoot = !string.IsNullOrWhiteSpace(ovr?.DownloadRoot) ? ovr!.DownloadRoot! : s.DownloadRoot;
        var effectiveFolderTemplate = ovr?.FolderTemplate ?? s.FolderTemplate;
        var effectiveFilenameTemplate = ovr?.FilenameTemplate ?? s.FilenameTemplate;
        var effectiveDateFormat = ovr?.DateFormat ?? s.DateFormat;
        var effectiveCreateSubfolder = ovr?.CreateSubfolderPerSubmission ?? s.CreateSubfolderPerSubmission;
        var effectiveSeparateR18 = ovr?.SeparateR18Folder ?? s.SeparateR18Folder;

        // Overwrite behavior is the universal control for existing files:
        // 0 = skip, 1 = overwrite, 2 = backup-then-overwrite.
        // AllowRedownload (legacy/account flag) forces overwrite when set.
        var effectiveOverwriteMode = ovr?.OverwriteMode ?? s.OverwriteMode;
        var allowRedownload = ovr?.AllowRedownload ?? false;
        if (allowRedownload && effectiveOverwriteMode == 0) effectiveOverwriteMode = 1;
        var effectiveBackup = (ovr?.BackupOldFile ?? s.BackupOldFile) || effectiveOverwriteMode == 2;

        var template = new FilenameTemplate(effectiveDateFormat);
        var ctx0 = new FilenameContext
        {
            Artwork = artwork,
            PageIndex = 0,
            PageCount = pages.Count,
            OriginalUrl = string.Empty,
        };

        var targetDir = effectiveDownloadRoot;

        // Resolve folder template
        var folderPath = template.Resolve(effectiveFolderTemplate, ctx0);
        targetDir = Path.Combine(targetDir, folderPath);

        // Optionally separate R-18 into its own subfolder
        if (effectiveSeparateR18 && artwork.IsR18)
            targetDir = Path.Combine(targetDir, "R-18");

        // Optionally put multi-page artworks into their own subfolder
        if (effectiveCreateSubfolder && pages.Count > 1)
        {
            var subfolderName = SafeName($"{artwork.Id}_{artwork.Title}");
            Diag($"SafeName('{artwork.Id}_{artwork.Title}') => '{subfolderName}'");
            targetDir = Path.Combine(targetDir, subfolderName);
        }

        // Override targetDir if CustomOutputFolder is set (from preset)
        if (!string.IsNullOrWhiteSpace(ovr?.CustomOutputFolder))
        {
            targetDir = ovr!.CustomOutputFolder!;
            Diag($"Using CustomOutputFolder: {targetDir}");
        }

        Diag($"Creating directory: {targetDir} (override={(ovr != null ? "yes" : "no")})");
        try
        {
            Directory.CreateDirectory(targetDir);
            Diag($"Directory created/verified: {targetDir}");
        }
        catch (Exception ex)
        {
            Diag($"FAILED to create directory: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        var targetIndexes = pageIndexes is null
            ? Enumerable.Range(0, pages.Count).ToList()
            : pageIndexes.Where(i => i >= 0 && i < pages.Count).Distinct().OrderBy(i => i).ToList();

        // If the user's filename template doesn't include any page-differentiating
        // token but the artwork has multiple pages, auto-append _p{N} so pages
        // don't collide and overwrite each other. Matches Nandaka PixivUtil2 behavior.
        var filenameTemplate = effectiveFilenameTemplate;
        var differentiates = filenameTemplate.Contains("%page_index%", StringComparison.OrdinalIgnoreCase)
            || filenameTemplate.Contains("%page_number%", StringComparison.OrdinalIgnoreCase)
            || filenameTemplate.Contains("%urlFilename%", StringComparison.OrdinalIgnoreCase);
        if (pages.Count > 1 && !differentiates)
        {
            filenameTemplate += "_p%page_index%";
            Diag($"Multi-page artwork without page token in template — auto-appending _p%page_index%");
        }

        Diag($"targetDir={targetDir} targetIndexes=[{string.Join(",", targetIndexes)}] folderTemplate='{effectiveFolderTemplate}' filenameTemplate='{filenameTemplate}'");
        var savedFiles = new List<string>(targetIndexes.Count);
        var batchTotal = targetIndexes.Count;
        var safeModePages = _settings.Current.SafeMode && targetIndexes.Count > 1;
        for (var batchIdx = 0; batchIdx < targetIndexes.Count; batchIdx++)
        {
            // Inter-page pacing for multi-page works under SafeMode. Small jittered
            // gap (300-800ms) prevents the 50-page-manga burst pattern that's a
            // dead-giveaway scraper fingerprint. Skipped before page 0 and for
            // single-page artworks (nothing to space out).
            if (safeModePages && batchIdx > 0)
            {
                var pageDelayMs = 300 + Random.Shared.Next(500); // 300-800ms
                await Task.Delay(pageDelayMs, ct).ConfigureAwait(false);
            }
            var i = targetIndexes[batchIdx];
            ct.ThrowIfCancellationRequested();
            var pageUrl = pages[i].Urls.Original;
            if (string.IsNullOrWhiteSpace(pageUrl))
            {
                Diag($"  page[{i}] SKIP: no original URL");
                continue;
            }

            var ext = Path.GetExtension(new Uri(pageUrl).AbsolutePath);
            var rawName = template.Resolve(filenameTemplate, new FilenameContext
            {
                Artwork = artwork,
                PageIndex = i,
                PageCount = pages.Count,
                OriginalUrl = pageUrl,
            });
            var fileName = SafeName(rawName) + ext;
            var destPath = Path.Combine(targetDir, fileName);
            Diag($"  page[{i}] destPath={destPath}");

            if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            {
                // 0 = skip existing, 1 = overwrite, 2 = backup-then-overwrite
                if (effectiveOverwriteMode == 0)
                {
                    Diag($"  page[{i}] EXISTS, skipping (size={new FileInfo(destPath).Length})");
                    savedFiles.Add(destPath);
                    progress?.Report(new DownloadProgress(artwork.Id, batchIdx, batchTotal, new FileInfo(destPath).Length, null));
                    continue;
                }

                if (effectiveBackup)
                {
                    try
                    {
                        var backupPath = destPath + ".bak";
                        if (File.Exists(backupPath))
                            backupPath = destPath + $".{DateTime.Now:yyyyMMddHHmmss}.bak";
                        File.Move(destPath, backupPath);
                        Diag($"  page[{i}] BACKED UP existing -> {backupPath}");
                    }
                    catch (Exception bex)
                    {
                        Diag($"  page[{i}] backup failed, will overwrite: {bex.Message}");
                    }
                }
                else
                {
                    Diag($"  page[{i}] EXISTS, overwriting (mode={effectiveOverwriteMode})");
                }
            }

            try
            {
                // Wait for a download slot (respects MaxConcurrentDownloads and SafeMode)
                using var slot = await AcquireDownloadSlotAsync(ct);
                var effectiveMinKB = ovr?.MinFileSizeKB ?? s.MinFileSizeKB;
                var effectiveMaxKB = ovr?.MaxFileSizeKB ?? s.MaxFileSizeKB;
                await DownloadFileAsync(pageUrl, destPath, batchIdx, batchTotal, artwork.Id, progress, ct,
                    effectiveMinKB, effectiveMaxKB)
                    .ConfigureAwait(false);
                Diag($"  page[{i}] DOWNLOADED size={(File.Exists(destPath) ? new FileInfo(destPath).Length : -1)}");

                // Apply preset post-processing if specified (resize, color adjustments, format conversion)
                var preset = ovr?.ImagePreset;
                if (batchArtworkIndex >= 0 && preset?.PerPagePresets != null)
                {
                    if (preset.PerPagePresets.TryGetValue((batchArtworkIndex, i), out var pagePreset))
                    {
                        preset = pagePreset;
                        Diag($"  page[{i}] using per-page preset '{preset.Name}' for artwork[{batchArtworkIndex}]");
                    }
                }
                bool needsResize = preset != null
                    && preset.DevicePreset != DevicePreset.Original
                    && preset.DevicePreset != DevicePreset.None;
                bool needsAdjustments = preset?.Adjustments?.HasAdjustments == true;
                bool needsCrop = preset?.CropRegion != null;
                bool needsFormatConvert = preset != null
                    && preset.ResizeSettings.OutputFormat != ResizeOutputFormat.KeepOriginal;
                if (preset != null && (needsResize || needsAdjustments || needsCrop || needsFormatConvert))
                {
                    Diag($"  page[{i}] POST-PROCESS with preset '{preset.Name}' (SaveAsNew={preset.SaveAsNew}, CustomFolder={preset.CustomOutputFolder ?? "<null>"})");
                    try
                    {
                        var processedPath = await _resizeService.ProcessAsync(destPath, preset, ct).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(processedPath) && File.Exists(processedPath))
                        {
                            Diag($"  page[{i}] PROCESSED -> {processedPath}");
                            // If processed file is in a different location, return that path so the user sees it
                            savedFiles.Add(processedPath);

                            // When SaveAsNew=true, original (unprocessed) at destPath is preserved by ProcessAsync.
                            // Delete it unless user explicitly opted in via AlsoDownloadUnprocessed.
                            if (preset.SaveAsNew
                                && !preset.AlsoDownloadUnprocessed
                                && !string.Equals(processedPath, destPath, StringComparison.OrdinalIgnoreCase)
                                && File.Exists(destPath))
                            {
                                try
                                {
                                    File.Delete(destPath);
                                    Diag($"  page[{i}] removed unprocessed copy at {destPath} (AlsoDownloadUnprocessed=false)");
                                }
                                catch (Exception delEx)
                                {
                                    Diag($"  page[{i}] failed to remove unprocessed copy: {delEx.Message}");
                                }
                            }
                        }
                        else
                        {
                            Diag($"  page[{i}] PROCESS returned no file, keeping original");
                            savedFiles.Add(destPath);
                        }
                    }
                    catch (Exception procEx)
                    {
                        Diag($"  page[{i}] PROCESS FAILED: {procEx.GetType().Name}: {procEx.Message}");
                        _logger.LogError(procEx, "Failed to apply preset to {Path}", destPath);
                        savedFiles.Add(destPath); // keep original even if processing fails
                    }
                }
                else
                {
                    savedFiles.Add(destPath);
                }
            }
            catch (Exception ex)
            {
                Diag($"  page[{i}] FAILED: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
        Diag($"=== DONE artwork {artwork.Id}: saved {savedFiles.Count}/{pages.Count}");
        return savedFiles;
    }

    /// <summary>
    /// Resolves the folder path that artworks for <paramref name="artwork"/> would be saved to,
    /// using the same template logic as <see cref="DownloadArtworkPagesAsync"/>.
    /// Used by the coordinator to determine where to save artist avatar/banner files.
    /// </summary>
    public string ResolveArtistFolder(ArtworkPreview artwork, SettingsOverride? overrideSettings = null)
    {
        var s = _settings.Current;
        var ovr = overrideSettings != null && !overrideSettings.UseGlobalSettings ? overrideSettings : null;
        var effectiveDownloadRoot = !string.IsNullOrWhiteSpace(ovr?.DownloadRoot) ? ovr!.DownloadRoot! : s.DownloadRoot;
        var effectiveFolderTemplate = ovr?.FolderTemplate ?? s.FolderTemplate;
        var effectiveDateFormat = ovr?.DateFormat ?? s.DateFormat;
        var template = new FilenameTemplate(effectiveDateFormat);
        var ctx = new FilenameContext { Artwork = artwork, PageIndex = 0, PageCount = 1, OriginalUrl = string.Empty };
        var folderPath = template.Resolve(effectiveFolderTemplate, ctx);
        return Path.Combine(effectiveDownloadRoot, folderPath);
    }

    /// <summary>
    /// Public SafeMode-aware downloader for arbitrary URLs (FANBOX content, image-editor
    /// preset queue, anything else outside the Pixiv image CDN flow). Honors the same
    /// 429/503 backoff + Retry-After logic as the main artwork pipeline so SafeMode
    /// protections cover every byte we pull, not just i.pximg.net pages.
    /// Callers pass the correct <paramref name="referer"/> for the origin
    /// (Pixiv or FANBOX); we still apply the SafeMode rate-limit shield.
    /// </summary>
    public async Task DownloadGenericFileAsync(
        string url, string destPath, string referer, CancellationToken ct = default)
    {
        var client = _httpFactory.GetClient();
        using var resp = await SendWithRateLimitAsync(client, url, ct, referer).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var tmp = destPath + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await src.CopyToAsync(dst, BufferSize, ct).ConfigureAwait(false);
                await dst.FlushAsync(ct).ConfigureAwait(false);
            }
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    private async Task DownloadFileAsync(
        string url, string destPath,
        int pageIndex, int totalPages, string artworkId,
        IProgress<DownloadProgress>? progress, CancellationToken ct,
        int minFileSizeKB = 0, int maxFileSizeKB = 0)
    {
        var client = _httpFactory.GetClient();

        // Send the request. When SafeMode is on, intercept 429/503 here and back off
        // before bubbling failure to the coordinator's retry loop — this prevents the
        // coordinator from burning through its retry budget against a rate-limit wall
        // and keeps the access pattern friendly to Pixiv's anti-scraper detection.
        using var resp = await SendWithRateLimitAsync(client, url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var expectedTotal = resp.Content.Headers.ContentLength;
        var tmp = destPath + ".part";
        long readTotal = 0;

        // Per-download stall timeout (DownloadTimeout setting). Applied only to the data
        // transfer — not the SafeMode 429/503 backoff above — and reset after each chunk so
        // it acts as an inactivity timeout: a download is aborted only if no bytes arrive
        // for the configured number of seconds. 0 = no timeout.
        var timeoutSec = _settings.Current.DownloadTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSec > 0) timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var dlCt = timeoutCts.Token;
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(dlCt).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(0, BufferSize), dlCt).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), dlCt).ConfigureAwait(false);
                    readTotal += read;
                    progress?.Report(new DownloadProgress(artworkId, pageIndex, totalPages, readTotal, expectedTotal));
                    // Reset the inactivity window: we just made progress.
                    if (timeoutSec > 0) timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                }
                await dst.FlushAsync(dlCt).ConfigureAwait(false);
            }

            // ---- Integrity checks (fail-fast: deletes the .part file on any mismatch) ----
            // 1) Empty file is always corrupt.
            if (readTotal == 0)
                throw new IOException($"Empty download for {url}");

            // 2) Byte count must match Content-Length (when the server sent one).
            //    Servers may legitimately omit it for chunked responses.
            if (expectedTotal.HasValue && readTotal != expectedTotal.Value)
                throw new IOException($"Truncated download: got {readTotal} bytes, expected {expectedTotal.Value} for {url}");

            // 3) On-disk size must equal what we counted.
            var fi = new FileInfo(tmp);
            if (!fi.Exists || fi.Length != readTotal)
                throw new IOException($"On-disk size mismatch for {tmp}: file={(fi.Exists ? fi.Length : -1)} counter={readTotal}");

            // 4) Image magic bytes — catches HTML error pages saved as .jpg, etc.
            if (!await HasValidImageHeaderAsync(tmp, ct).ConfigureAwait(false))
                throw new IOException($"Downloaded file is not a recognized image (HTML error page?): {url}");

            // 5) Min/Max file size filter (0 = disabled). Delete .part and skip without error.
            if (minFileSizeKB > 0 && readTotal < (long)minFileSizeKB * 1024)
            {
                try { File.Delete(tmp); } catch { /* best-effort */ }
                Diag($"  SKIP: file {readTotal} bytes is below MinFileSizeKB={minFileSizeKB} KB");
                return;
            }
            if (maxFileSizeKB > 0 && readTotal > (long)maxFileSizeKB * 1024)
            {
                try { File.Delete(tmp); } catch { /* best-effort */ }
                Diag($"  SKIP: file {readTotal} bytes exceeds MaxFileSizeKB={maxFileSizeKB} KB");
                return;
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The cancellation came from our inactivity-timeout, NOT a user pause/cancel.
            // Translate it into a regular failure (TimeoutException) so the coordinator
            // retries this download instead of treating the whole job as cancelled.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw new TimeoutException($"Download timed out after {timeoutSec}s of inactivity: {url}");
        }
        catch
        {
            // Don't leave a half-written .part file around — the next attempt
            // would skip it because the existence check uses Length>0 only on
            // the final path, but a stale .part can confuse retries.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>
    /// Sends a GET to <paramref name="url"/> with the Pixiv Referer header.
    /// When <see cref="AppSettings.SafeMode"/> is enabled, automatically waits out
    /// HTTP 429 / 503 responses up to a few times before bubbling failure:
    /// honors the server's <c>Retry-After</c> header when present, otherwise
    /// applies exponential backoff (5s → 10s → 20s → 60s) with ±25% jitter.
    /// These backoff waits intentionally don't count against the coordinator's
    /// configured retry budget — they're "the server told us to slow down" pauses,
    /// not "the request failed unexpectedly" retries.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRateLimitAsync(
        HttpClient client, string url, CancellationToken ct, string? referer = null)
    {
        // Schedule of jittered backoff waits, in seconds, used when SafeMode is on
        // and the server doesn't supply a Retry-After header. Each entry is one
        // attempt; the loop bails after the last one.
        var backoffSeconds = RateLimitBackoffSeconds;
        var jitter = Random.Shared;
        var safeMode = _settings.Current.SafeMode;

        HttpResponseMessage resp;
        int attempt = 0;
        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", referer ?? PixivReferer);
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // Anything that isn't a rate-limit signal: hand off to caller as-is.
            // (Caller calls EnsureSuccessStatusCode to surface real failures.)
            var status = (int)resp.StatusCode;
            var isRateLimited = status == 429 || status == 503;
            if (!isRateLimited || !safeMode || attempt >= backoffSeconds.Length)
                return resp;

            // Prefer the server's hint if it sent one — that's the polite signal.
            var retryAfter = resp.Headers.RetryAfter;
            TimeSpan wait;
            if (retryAfter?.Delta is { } delta)
            {
                wait = delta;
            }
            else if (retryAfter?.Date is { } date)
            {
                wait = date - DateTimeOffset.UtcNow;
                if (wait < TimeSpan.Zero) wait = TimeSpan.FromSeconds(backoffSeconds[attempt]);
            }
            else
            {
                // No header → exponential backoff with ±25% jitter
                var baseSec = backoffSeconds[attempt];
                var jitterFactor = 0.75 + (jitter.NextDouble() * 0.5); // 0.75x – 1.25x
                wait = TimeSpan.FromSeconds(baseSec * jitterFactor);
            }
            // Cap any single wait at 5 minutes so a misbehaving server can't park us forever.
            if (wait > TimeSpan.FromMinutes(5)) wait = TimeSpan.FromMinutes(5);

            Diag($"SafeMode: HTTP {status} from {url} — backing off {wait.TotalSeconds:F1}s (attempt {attempt + 1}/{backoffSeconds.Length})");
            resp.Dispose();
            await Task.Delay(wait, ct).ConfigureAwait(false);
            attempt++;
        }
    }

    /// <summary>
    /// Verifies the file starts with magic bytes for one of the formats Pixiv
    /// serves (JPEG / PNG / GIF / WebP). Returns false on truncated headers
    /// or anything else (e.g. a JSON/HTML error response saved as bytes).
    /// </summary>
    private static async Task<bool> HasValidImageHeaderAsync(string path, CancellationToken ct)
    {
        var head = new byte[12];
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var n = await fs.ReadAsync(head.AsMemory(0, 12), ct).ConfigureAwait(false);
        if (n < 4) return false;

        // JPEG: FF D8 FF
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return true;
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (n >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                   && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return true;
        // GIF: "GIF87a" or "GIF89a"
        if (n >= 6 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38
                   && (head[4] == 0x37 || head[4] == 0x39) && head[5] == 0x61) return true;
        // WebP: "RIFF" .... "WEBP"
        if (n >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
                    && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50) return true;
        // BMP: "BM"
        if (head[0] == 0x42 && head[1] == 0x4D) return true;
        return false;
    }

    /// <summary>
    /// Exports ugoira with preset processing applied to each frame.
    /// Extracts frames, processes each with the preset, then re-encodes to output formats.
    /// </summary>
    private async Task<IReadOnlyList<string>> ExportUgoiraWithPresetAsync(
        string artworkId,
        IEnumerable<UgoiraFormat> formats,
        string outputDir,
        ImageEditPreset preset,
        CancellationToken ct)
    {
        var produced = new List<string>();
        var workDir = Path.Combine(Path.GetTempPath(), $"pikura_ugoira_{artworkId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            // Step 1: Extract all frames
            Diag($"UGOIRA: extracting all frames for preset processing");
            var framePaths = await _ugoiraService.ExtractAllFramesAsync(artworkId, ct).ConfigureAwait(false);
            if (framePaths.Count == 0)
            {
                Diag("UGOIRA: failed to extract frames, falling back to direct export");
                return await _ugoiraService.ExportAsync(artworkId, formats, outputDir, ct).ConfigureAwait(false);
            }
            Diag($"UGOIRA: extracted {framePaths.Count} frames");

            // Step 2: Process each frame with the preset
            var processedFramesDir = Path.Combine(workDir, "processed_frames");
            Directory.CreateDirectory(processedFramesDir);
            var processedFramePaths = new List<string>();

            for (int i = 0; i < framePaths.Count; i++)
            {
                var framePath = framePaths[i];
                var processedPath = Path.Combine(processedFramesDir, $"frame_{i:D4}.png");

                try
                {
                    var result = await _resizeService.ProcessAsync(framePath, preset, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(result) && File.Exists(result))
                    {
                        // If processed file is in a different location, copy it to our work dir
                        if (!string.Equals(result, processedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(result, processedPath, overwrite: true);
                        }
                        processedFramePaths.Add(processedPath);
                        Diag($"UGOIRA: processed frame {i + 1}/{framePaths.Count}");
                    }
                    else
                    {
                        // Processing failed, use original frame
                        File.Copy(framePath, processedPath, overwrite: true);
                        processedFramePaths.Add(processedPath);
                        Diag($"UGOIRA: frame {i + 1} processing failed, using original");
                    }
                }
                catch (Exception ex)
                {
                    Diag($"UGOIRA: frame {i + 1} processing error: {ex.Message}, using original");
                    File.Copy(framePath, processedPath, overwrite: true);
                    processedFramePaths.Add(processedPath);
                }
            }

            if (processedFramePaths.Count == 0)
            {
                Diag("UGOIRA: no frames processed successfully, falling back to direct export");
                return await _ugoiraService.ExportAsync(artworkId, formats, outputDir, ct).ConfigureAwait(false);
            }

            // Step 3: Get frame delays for re-encoding
            var meta = await _client.GetUgoiraMetaAsync(artworkId, ct).ConfigureAwait(false);
            if (meta == null || meta.Frames.Count == 0)
            {
                Diag("UGOIRA: failed to get frame metadata, falling back to direct export");
                return await _ugoiraService.ExportAsync(artworkId, formats, outputDir, ct).ConfigureAwait(false);
            }

            // Step 4: Re-encode processed frames to each format (skip if Frames Only mode)
            if (!preset.UgoiraFramesOnly)
            {
                var ffmpegPath = _ffmpegService.GetExecutablePath();
                if (ffmpegPath == null)
                {
                    Diag("UGOIRA: ffmpeg not available, falling back to direct export");
                    return await _ugoiraService.ExportAsync(artworkId, formats, outputDir, ct).ConfigureAwait(false);
                }

                foreach (var fmt in formats.Distinct())
            {
                var destPath = Path.Combine(outputDir, artworkId + ExtensionFor(fmt));
                try
                {
                    string? encodedPath = null;
                    switch (fmt)
                    {
                        case UgoiraFormat.Mp4:
                            encodedPath = await EncodeProcessedFramesToMp4Async(
                                processedFramePaths, meta, destPath, ffmpegPath, ct).ConfigureAwait(false);
                            break;
                        case UgoiraFormat.WebM:
                            encodedPath = await EncodeProcessedFramesToWebmAsync(
                                processedFramePaths, meta, destPath, ffmpegPath, ct).ConfigureAwait(false);
                            break;
                        case UgoiraFormat.Gif:
                            encodedPath = await EncodeProcessedFramesToGifAsync(
                                processedFramePaths, meta, destPath, ffmpegPath, ct).ConfigureAwait(false);
                            break;
                    }

                    if (!string.IsNullOrEmpty(encodedPath) && File.Exists(encodedPath))
                    {
                        produced.Add(encodedPath);
                        Diag($"UGOIRA: produced {fmt} with preset -> {encodedPath}");
                    }
                }
                catch (Exception ex)
                {
                    Diag($"UGOIRA: failed to encode {fmt}: {ex.Message}");
                }
            }
            }

            // Step 5: Optionally save individual processed frames to subfolder
            if (preset.SaveUgoiraFrames && processedFramePaths.Count > 0)
            {
                try
                {
                    var framesDir = Path.Combine(outputDir, $"{artworkId}_frames");
                    Directory.CreateDirectory(framesDir);
                    
                    for (int i = 0; i < processedFramePaths.Count; i++)
                    {
                        var sourcePath = processedFramePaths[i];
                        var destFileName = $"frame_{i:D4}.png";
                        var destPath = Path.Combine(framesDir, destFileName);
                        File.Copy(sourcePath, destPath, overwrite: true);
                    }
                    
                    produced.Add(framesDir);
                    Diag($"UGOIRA: saved {processedFramePaths.Count} processed frames to {framesDir}");
                }
                catch (Exception ex)
                {
                    Diag($"UGOIRA: failed to save individual frames: {ex.Message}");
                }
            }
        }
        finally
        {
            // Cleanup temp directory
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }

        return produced;
    }

    /// <summary>
    /// Extracts all ugoira frames as individual PNG files without encoding video formats.
    /// Used when UgoiraFramesOnly setting is enabled.
    /// </summary>
    private async Task<IReadOnlyList<string>> ExportUgoiraFramesOnlyAsync(
        string artworkId,
        string outputDir,
        CancellationToken ct)
    {
        var produced = new List<string>();
        try
        {
            // Extract all frames as PNG
            var framePaths = await _ugoiraService.ExtractAllFramesAsync(artworkId, ct).ConfigureAwait(false);
            if (framePaths.Count == 0)
            {
                Diag("UGOIRA: failed to extract any frames");
                return produced;
            }

            // Create frames subfolder and copy frames there
            var framesDir = Path.Combine(outputDir, $"{artworkId}_frames");
            Directory.CreateDirectory(framesDir);

            for (int i = 0; i < framePaths.Count; i++)
            {
                var sourcePath = framePaths[i];
                var destFileName = $"frame_{i:D4}.png";
                var destPath = Path.Combine(framesDir, destFileName);
                File.Copy(sourcePath, destPath, overwrite: true);
            }

            produced.Add(framesDir);
            Diag($"UGOIRA: saved {framePaths.Count} frames to {framesDir}");
        }
        catch (Exception ex)
        {
            Diag($"UGOIRA: ExportUgoiraFramesOnlyAsync failed: {ex.Message}");
            _logger.LogError(ex, "Failed to extract ugoira frames only for {Id}", artworkId);
        }
        return produced;
    }

    /// <summary>
    /// Exports ugoira to video formats AND saves individual frames as PNG.
    /// Used when SaveUgoiraFrames setting is enabled alongside video formats.
    /// </summary>
    private async Task<IReadOnlyList<string>> ExportUgoiraWithFramesAsync(
        string artworkId,
        IEnumerable<UgoiraFormat> formats,
        string outputDir,
        CancellationToken ct)
    {
        var produced = new List<string>();
        try
        {
            // First, export video formats
            var videoPaths = await _ugoiraService.ExportAsync(artworkId, formats, outputDir, ct).ConfigureAwait(false);
            produced.AddRange(videoPaths);
            Diag($"UGOIRA: exported {videoPaths.Count} video file(s)");

            // Then, extract and save individual frames
            var framePaths = await _ugoiraService.ExtractAllFramesAsync(artworkId, ct).ConfigureAwait(false);
            if (framePaths.Count > 0)
            {
                var framesDir = Path.Combine(outputDir, $"{artworkId}_frames");
                Directory.CreateDirectory(framesDir);

                for (int i = 0; i < framePaths.Count; i++)
                {
                    var sourcePath = framePaths[i];
                    var destFileName = $"frame_{i:D4}.png";
                    var destPath = Path.Combine(framesDir, destFileName);
                    File.Copy(sourcePath, destPath, overwrite: true);
                }

                produced.Add(framesDir);
                Diag($"UGOIRA: saved {framePaths.Count} frames to {framesDir}");
            }
        }
        catch (Exception ex)
        {
            Diag($"UGOIRA: ExportUgoiraWithFramesAsync failed: {ex.Message}");
            _logger.LogError(ex, "Failed to export ugoira with frames for {Id}", artworkId);
        }
        return produced;
    }

    private static string ExtensionFor(UgoiraFormat fmt) => fmt switch
    {
        UgoiraFormat.Mp4 => ".mp4",
        UgoiraFormat.WebM => ".webm",
        UgoiraFormat.Gif => ".gif",
        _ => ".mp4"
    };

    private async Task<string?> EncodeProcessedFramesToMp4Async(
        List<string> framePaths, UgoiraMeta meta, string destPath, string ffmpegPath, CancellationToken ct)
    {
        // Write concat file with processed frames
        var concatPath = Path.Combine(Path.GetTempPath(), $"concat_{Guid.NewGuid():N}.txt");
        try
        {
            var sb = new StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < Math.Min(framePaths.Count, meta.Frames.Count); i++)
            {
                var delayMs = meta.Frames[i].DelayMs;
                sb.AppendLine($"file '{framePaths[i].Replace("'", "'\\''")}'");
                sb.AppendLine($"duration {(delayMs / 1000.0).ToString("F3", inv)}");
            }
            sb.AppendLine($"file '{framePaths[Math.Min(framePaths.Count - 1, meta.Frames.Count - 1)].Replace("'", "'\\''")}'");
            await File.WriteAllTextAsync(concatPath, sb.ToString(), ct).ConfigureAwait(false);

            // Encode with ffmpeg - use same args as working UgoiraService
            var args = $"-y -f concat -safe 0 -i \"{concatPath}\" -vsync vfr -pix_fmt yuv420p -c:v libx264 -preset fast -crf 23 -movflags +faststart \"{destPath}\"";
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            
            var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            
            if (p.ExitCode != 0)
            {
                Diag($"UGOIRA: ffmpeg MP4 encoding failed with exit code {p.ExitCode}: {stderr}");
                return null;
            }

            return File.Exists(destPath) ? destPath : null;
        }
        finally
        {
            try { File.Delete(concatPath); } catch { }
        }
    }

    private async Task<string?> EncodeProcessedFramesToWebmAsync(
        List<string> framePaths, UgoiraMeta meta, string destPath, string ffmpegPath, CancellationToken ct)
    {
        // Similar to MP4 but with WebM codec
        var concatPath = Path.Combine(Path.GetTempPath(), $"concat_{Guid.NewGuid():N}.txt");
        try
        {
            var sb = new StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < Math.Min(framePaths.Count, meta.Frames.Count); i++)
            {
                var delayMs = meta.Frames[i].DelayMs;
                sb.AppendLine($"file '{framePaths[i].Replace("'", "'\\''")}'");
                sb.AppendLine($"duration {(delayMs / 1000.0).ToString("F3", inv)}");
            }
            sb.AppendLine($"file '{framePaths[Math.Min(framePaths.Count - 1, meta.Frames.Count - 1)].Replace("'", "'\\''")}'");
            await File.WriteAllTextAsync(concatPath, sb.ToString(), ct).ConfigureAwait(false);

            var args = $"-y -f concat -safe 0 -i \"{concatPath}\" -c:v libvpx-vp9 -b:v 0 -crf 30 -pix_fmt yuv420p \"{destPath}\"";
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            
            var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            
            if (p.ExitCode != 0)
            {
                Diag($"UGOIRA: ffmpeg WebM encoding failed with exit code {p.ExitCode}: {stderr}");
                return null;
            }

            return File.Exists(destPath) ? destPath : null;
        }
        finally
        {
            try { File.Delete(concatPath); } catch { }
        }
    }

    private async Task<string?> EncodeProcessedFramesToGifAsync(
        List<string> framePaths, UgoiraMeta meta, string destPath, string ffmpegPath, CancellationToken ct)
    {
        // Use palettegen and paletteuse for better GIF quality
        var concatPath = Path.Combine(Path.GetTempPath(), $"concat_{Guid.NewGuid():N}.txt");
        try
        {
            var sb = new StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < Math.Min(framePaths.Count, meta.Frames.Count); i++)
            {
                var delayMs = meta.Frames[i].DelayMs;
                sb.AppendLine($"file '{framePaths[i].Replace("'", "'\\''")}'");
                sb.AppendLine($"duration {(delayMs / 1000.0).ToString("F3", inv)}");
            }
            sb.AppendLine($"file '{framePaths[Math.Min(framePaths.Count - 1, meta.Frames.Count - 1)].Replace("'", "'\\''")}'");
            await File.WriteAllTextAsync(concatPath, sb.ToString(), ct).ConfigureAwait(false);

            // GIF encoding with optimized palette
            var args = $"-y -f concat -safe 0 -i \"{concatPath}\" -vf \"fps=30,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=128[p];[s1][p]paletteuse\" -loop 0 \"{destPath}\"";
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            
            var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            
            if (p.ExitCode != 0)
            {
                Diag($"UGOIRA: ffmpeg GIF encoding failed with exit code {p.ExitCode}: {stderr}");
                return null;
            }

            return File.Exists(destPath) ? destPath : null;
        }
        finally
        {
            try { File.Delete(concatPath); } catch { }
        }
    }

    /// <summary>
    /// Returns true if any file whose name starts with <paramref name="artworkId"/> exists
    /// inside the download directory (recursive). Checks both DownloadRoot and CustomOutputFolder.
    /// Used to detect already-downloaded artworks before showing a re-download confirmation dialog.
    /// When <paramref name="fileTypeFilter"/> is "processed", only checks for files with "_processed" suffix.
    /// When "unprocessed", only checks for files without "_processed" suffix.
    /// When "all" (default), checks for any file starting with the artwork ID.
    /// </summary>
    public bool HasExistingFiles(string artworkId, SettingsOverride? overrideSettings = null, string fileTypeFilter = "all")
    {
        var s = _settings.Current;
        var ovr = overrideSettings != null && !overrideSettings.UseGlobalSettings ? overrideSettings : null;
        
        // Helper to check if filename matches the criteria
        bool MatchesPattern(string filename)
        {
            var name = Path.GetFileNameWithoutExtension(filename);
            // Must start with artwork ID
            if (!name.StartsWith(artworkId, StringComparison.OrdinalIgnoreCase))
                return false;
            
            // Apply file type filter
            return fileTypeFilter switch
            {
                "processed" => name.Contains("_processed", StringComparison.OrdinalIgnoreCase),
                "unprocessed" => !name.Contains("_processed", StringComparison.OrdinalIgnoreCase),
                _ => true // "all" - match any file starting with artwork ID
            };
        }
        
        // Check CustomOutputFolder first if set
        if (!string.IsNullOrWhiteSpace(ovr?.CustomOutputFolder))
        {
            var customFolder = ovr!.CustomOutputFolder!;
            if (Directory.Exists(customFolder))
            {
                try
                {
                    var found = Directory
                        .EnumerateFiles(customFolder, "*", SearchOption.AllDirectories)
                        .Any(MatchesPattern);
                    if (found) return true;
                }
                catch { /* continue to check DownloadRoot */ }
            }
        }
        
        // Check DownloadRoot (either from override or global)
        var root = !string.IsNullOrWhiteSpace(ovr?.DownloadRoot) ? ovr!.DownloadRoot! : s.DownloadRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
        try
        {
            return Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Any(MatchesPattern);
        }
        catch { return false; }
    }

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Sanitizes a string for use as a Windows path segment. Preserves Unicode
    /// characters (Japanese kanji/kana, emoji, etc.) since NTFS supports them,
    /// but replaces characters that Windows actually rejects, trims trailing
    /// dots/spaces (which Windows silently strips), and avoids reserved names.
    /// </summary>
    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            // Strip control chars and Windows-invalid chars; keep all printable Unicode.
            if (ch < 0x20 || Array.IndexOf(invalid, ch) >= 0)
                sb.Append('_');
            else
                sb.Append(ch);
        }

        // Windows strips trailing dots/spaces from path segments at create time,
        // which then breaks subsequent file writes. Trim both ends.
        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(cleaned)) return "unknown";

        // Avoid Windows reserved device names (CON, PRN, AUX, NUL, COM1-9, LPT1-9)
        // even when followed by an extension (CON.txt is also rejected).
        var rootName = cleaned;
        var dot = rootName.IndexOf('.');
        if (dot > 0) rootName = rootName[..dot];
        if (WindowsReserved.Contains(rootName)) cleaned = "_" + cleaned;

        // Cap individual path-segment length so we don't blow Windows MAX_PATH.
        // Pixiv titles can be very long; 120 chars per segment is plenty.
        const int MaxSegment = 120;
        if (cleaned.Length > MaxSegment)
            cleaned = cleaned[..MaxSegment].TrimEnd('.', ' ');

        return cleaned;
    }

    /// <summary>
    /// Resolves the output directory for a ugoira artwork using the same folder
    /// template / R-18 / per-submission rules as regular artwork downloads, but
    /// without page-count logic (ugoira always have one logical "page").
    /// </summary>
    private string ResolveOutputDir(ArtworkPreview artwork, SettingsOverride? overrideSettings)
    {
        var s = _settings.Current;
        var ovr = overrideSettings != null && !overrideSettings.UseGlobalSettings ? overrideSettings : null;

        var effectiveDownloadRoot = !string.IsNullOrWhiteSpace(ovr?.DownloadRoot) ? ovr!.DownloadRoot! : s.DownloadRoot;
        var effectiveFolderTemplate = ovr?.FolderTemplate ?? s.FolderTemplate;
        var effectiveDateFormat = ovr?.DateFormat ?? s.DateFormat;
        var effectiveSeparateR18 = ovr?.SeparateR18Folder ?? s.SeparateR18Folder;

        var template = new FilenameTemplate(effectiveDateFormat);
        var ctx = new FilenameContext
        {
            Artwork = artwork,
            PageIndex = 0,
            PageCount = 1,
            OriginalUrl = string.Empty,
        };
        var folderPath = template.Resolve(effectiveFolderTemplate, ctx);
        var dir = Path.Combine(effectiveDownloadRoot, folderPath);
        if (effectiveSeparateR18 && artwork.IsR18) dir = Path.Combine(dir, "R-18");
        if (!string.IsNullOrWhiteSpace(ovr?.CustomOutputFolder)) dir = ovr!.CustomOutputFolder!;
        return dir;
    }

    /// <summary>
    /// Maps the user's per-format checkboxes onto the <see cref="UgoiraFormat"/>
    /// list that <see cref="UgoiraService.ExportAsync"/> will iterate.
    /// </summary>
    private static List<UgoiraFormat> ResolveUgoiraFormats(AppSettings s)
    {
        var formats = new List<UgoiraFormat>();
        if (s.CreateUgoiraMp4)  formats.Add(UgoiraFormat.Mp4);
        if (s.CreateUgoiraWebm) formats.Add(UgoiraFormat.WebM);
        if (s.CreateUgoiraGif)  formats.Add(UgoiraFormat.Gif);
        if (s.CreateUgoiraWebp) formats.Add(UgoiraFormat.WebP);
        if (s.CreateUgoiraApng) formats.Add(UgoiraFormat.Apng);
        return formats;
    }
}
