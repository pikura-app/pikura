using Microsoft.Extensions.Logging;
using Pikura.Core.Data;
using Pikura.Core.Models;
using Pikura.Core.Settings;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Pikura.Core.Services.Library;

/// <summary>Immutable progress snapshot (safe for Progress<T> async dispatch).</summary>
public sealed record LibraryScanProgress(int Done, int Total, string? CurrentPath, string Phase);

/// <summary>
/// Maintains the local library index: records downloads, scans roots for files,
/// parses ids from filenames, enriches metadata, tags files with the anime
/// tagger, and groups duplicates. Hashing work is queued on a bounded
/// single-consumer background channel so downloads are never blocked.
/// </summary>
public sealed class LibraryIndexService : IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif" };
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".mp4", ".webm" };
    private static readonly HashSet<string> SkipSuffixes = new(StringComparer.OrdinalIgnoreCase)
        { ".part", ".meta", ".tags.txt", ".txt" };
    private static readonly Regex PagedFileName = new(@"(?<!\d)(\d{6,10})_p(\d{1,4})(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BareIdFileName = new(@"^\d{6,10}$", RegexOptions.Compiled);

    private readonly LibraryRepository _repo;
    private readonly SettingsService _settings;
    private readonly AccountService? _accounts;
    private readonly PixivClient? _client;
    private readonly AnimeTaggerService? _tagger;
    private readonly ILogger<LibraryIndexService> _logger;

    private sealed record HashWork(LibraryFile File);
    private readonly Channel<HashWork> _hashChannel = Channel.CreateUnbounded<HashWork>();
    private readonly Task _hashWorker;
    private readonly Task _hashWorker2;
    private readonly CancellationTokenSource _disposeCts = new();

    public LibraryIndexService(
        LibraryRepository repo,
        SettingsService settings,
        ILogger<LibraryIndexService> logger,
        AccountService? accounts = null,
        PixivClient? client = null,
        AnimeTaggerService? tagger = null)
    {
        _repo = repo; _settings = settings; _logger = logger;
        _accounts = accounts; _client = client; _tagger = tagger;
        _hashWorker = Task.Run(HashWorkerLoop);
        _hashWorker2 = Task.Run(HashWorkerLoop);
    }

    public LibraryRepository Repository => _repo;

    /// <summary>Configured library folders (full paths, distinct, including missing ones):
    /// <c>LibraryFolders</c> when customised, else the default global + per-account
    /// download roots.</summary>
    public IReadOnlyList<string> GetConfiguredFolders()
    {
        if (_settings.Current.LibraryFolders is { Count: > 0 } custom)
            return custom.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var roots = new List<string>();
        var global = _settings.Current.DownloadRoot;
        if (!string.IsNullOrWhiteSpace(global)) roots.Add(global);
        if (_accounts != null)
            foreach (var p in _accounts.Profiles)
                if (!string.IsNullOrWhiteSpace(p.Settings?.DownloadRoot)) roots.Add(p.Settings?.DownloadRoot!);
        return roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True when <paramref name="child"/> is inside <paramref name="root"/>
    /// (separator-aware, so "C:\Pix" doesn't contain "C:\Pixiv2").</summary>
    internal static bool IsUnderRoot(string child, string root)
    {
        var c = Path.GetFullPath(child);
        var r = Path.GetFullPath(root);
        if (!r.EndsWith(Path.DirectorySeparatorChar) && !r.EndsWith(Path.AltDirectorySeparatorChar))
            r += Path.DirectorySeparatorChar;
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Distinct existing library roots: configured folders filtered to existing dirs,
    /// with any root nested inside another dropped so files aren't indexed twice.</summary>
    public IReadOnlyList<string> GetRoots()
    {
        var dirs = GetConfiguredFolders().Where(Directory.Exists).ToList();
        return dirs.Where(d => !dirs.Any(o => o != d && IsUnderRoot(d, o))).ToList();
    }

    // ── Record on download ───────────────────────────────────────────────────

    /// <summary>
    /// Records a freshly downloaded file (called from PixivDownloadService).
    /// Never blocks the download: hashing happens on the background channel.
    /// </summary>
    public void RecordDownloaded(ArtworkPreview artwork, string filePath, int pageIndex, string? originalUrl = null)
    {
        try
        {
            if (!_settings.Current.LibraryAutoIndexDownloads) return;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            var ext = Path.GetExtension(filePath);
            if (!MediaExtensions.Contains(ext)) return;

            var fi = new FileInfo(filePath);
            var file = new LibraryFile
            {
                Path = Path.GetFullPath(filePath),
                Root = FindRootFor(filePath) ?? Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "",
                Size = fi.Length,
                Mtime = fi.LastWriteTimeUtc.ToString("O"),
                MediaType = MediaTypeOf(ext),
                ArtworkId = artwork.Id,
                PageIndex = pageIndex,
                Title = artwork.Title,
                ArtistId = artwork.UserId,
                ArtistName = artwork.UserName,
                Tags = artwork.Tags is { Count: > 0 } ? string.Join(' ', artwork.Tags) : null,
                Description = artwork.Description,
                XRestrict = artwork.XRestrict,
                SourceUrl = $"https://www.pixiv.net/artworks/{artwork.Id}",
                OriginalUrl = originalUrl,
                SourceSite = "pixiv",
                DownloadedAt = DateTime.UtcNow.ToString("O"),
                AiType = artwork.AiType,
                CreateDate = artwork.CreateDate?.ToString("O"),
                AddedAt = DateTimeOffset.UtcNow.ToString("O"),
                MetadataSource = "download",
            };
            _hashChannel.Writer.TryWrite(new HashWork(file));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to queue library record for {Path}", filePath); }
    }

    /// <summary>Direct awaitable upsert used by the tagger hook (skips queue, hashes inline on caller's task).</summary>
    public async Task RecordAiTagsAsync(string imagePath, AnimeTagResult result, CancellationToken ct = default)
    {
        try
        {
            var f = await _repo.GetByPathAsync(imagePath, ct);
            if (f == null) return;
            await _repo.SetAiTagsAsync(f.Id, ToLibraryTags(result), ct);
            await WriteHashColumnsAsync(f.Id, imagePath, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to record AI tags for {Path}", imagePath); }
    }

    private static IReadOnlyList<LibraryAiTag> ToLibraryTags(AnimeTagResult r)
    {
        var list = new List<LibraryAiTag>();
        void Add(IReadOnlyList<ScoredTag> ts, string cat) =>
            list.AddRange(ts.Select(t => new LibraryAiTag { Tag = t.Name, Category = cat, Score = t.Confidence }));
        Add(r.General, "general"); Add(r.Character, "character");
        Add(r.Copyright, "copyright"); Add(r.Artist, "artist"); Add(r.Meta, "meta");
        return list;
    }

    private string? FindRootFor(string path) => GetRoots()
        .FirstOrDefault(r => IsUnderRoot(path, r));

    private static string? MediaTypeOf(string ext) =>
        ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            ? "video"
            : ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                ? "animation" : "image";

    // ── Background hashing ───────────────────────────────────────────────────

    private async Task HashWorkerLoop()
    {
        var ct = _disposeCts.Token;
        await foreach (var work in _hashChannel.Reader.ReadAllAsync(ct))
        {
            try { await HashAndUpsertAsync(work.File, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Library hash failed for {Path}", work.File.Path); }
        }
    }

    private async Task HashAndUpsertAsync(LibraryFile f, CancellationToken ct)
    {
        // Insert the row first so a crash/exit can't lose the record; hashing fills in later.
        f.Id = await _repo.UpsertFileAsync(f, ct);
        if (!File.Exists(f.Path)) return;
        var info = await Task.Run(() =>
            ImageProbe.IndexFile(f.Path, wantPhash: f.MediaType != "video"), ct);
        f.Sha256 = info.Sha256; f.Phash = info.Phash;
        f.Width = info.Width; f.Height = info.Height;
        f.PhashVersion = info.PhashVersion;
        f.Id = await _repo.UpsertFileAsync(f, ct);
    }

    private async Task WriteHashColumnsAsync(long fileId, string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return;
        var info = await Task.Run(() => ImageProbe.IndexFile(path), ct);
        var f = await _repo.GetByIdAsync(fileId, ct);
        if (f == null) return;
        f.Sha256 = info.Sha256; f.Phash = info.Phash;
        f.PhashVersion = info.PhashVersion;
        await _repo.UpsertFileAsync(f, ct);
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    /// <summary>Incremental scan of all roots. Cancellable, resumable (stamp-based).</summary>
    public async Task ScanAsync(IProgress<LibraryScanProgress>? progress, CancellationToken ct)
    {
        var roots = GetRoots();
        var done = 0; var phase = "scan";
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var known = await _repo.GetAllPathsWithStampAsync(root, ct);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<(string Path, long Size, string Mtime)>();

            foreach (var file in EnumerateMediaFiles(root))
            {
                seen.Add(Path.GetFullPath(file));
                var fi = new FileInfo(file);
                var mtime = fi.LastWriteTimeUtc.ToString("O");
                if (known.TryGetValue(file, out var stamp)
                    && stamp.Size == fi.Length && stamp.Mtime == mtime)
                    continue; // unchanged — skip re-hash
                candidates.Add((Path.GetFullPath(file), fi.Length, mtime));
            }

            // Compute hashes in parallel (half the cores — UI stays responsive);
            // a single writer batches the DB upserts (SQLite is single-writer).
            var write = Channel.CreateBounded<(LibraryFile File, List<LibraryAiTag>? Tags)>(512);
            var writer = Task.Run(async () =>
            {
                var batch = new List<(LibraryFile File, List<LibraryAiTag>? Tags)>(128);
                await foreach (var item in write.Reader.ReadAllAsync(ct))
                {
                    batch.Add(item);
                    if (batch.Count >= 100) { await FlushBatch(batch, ct); batch.Clear(); }
                }
                if (batch.Count > 0) await FlushBatch(batch, ct);
            }, ct);

            var dop = Math.Max(1, Environment.ProcessorCount / 2);
            await Parallel.ForEachAsync(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
                async (cand, c) =>
                {
                    var (path, size, mtime) = cand;
                    var n = Interlocked.Increment(ref done);
                    progress?.Report(new LibraryScanProgress(n, candidates.Count, path, phase));

                    var ext = Path.GetExtension(path);
                    var (artworkId, pageIndex) = ParseArtworkId(Path.GetFileNameWithoutExtension(path));
                    var file = new LibraryFile
                    {
                        Path = path, Root = root, Size = size, Mtime = mtime,
                        MediaType = MediaTypeOf(ext),
                        ArtworkId = artworkId, PageIndex = pageIndex,
                        AddedAt = DateTimeOffset.UtcNow.ToString("O"),
                        MetadataSource = artworkId != null ? "filename" : null,
                    };
                    var info = ImageProbe.IndexFile(path, wantPhash: file.MediaType != "video");
                    file.Sha256 = info.Sha256; file.Phash = info.Phash;
                    file.Width = info.Width; file.Height = info.Height;
                    file.PhashVersion = info.PhashVersion;

                    List<LibraryAiTag>? tags = null;
                    var sidecar = path + ".tags.txt";
                    if (File.Exists(sidecar))
                    {
                        try
                        {
                            var t = ParseSidecarTags(await File.ReadAllTextAsync(sidecar, c));
                            if (t.Count > 0) { tags = t; file.AiTaggedAt = DateTimeOffset.UtcNow.ToString("O"); }
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to import sidecar {Path}", sidecar); }
                    }
                    await write.Writer.WriteAsync((file, tags), c);
                });
            write.Writer.Complete();
            await writer;

            // Drop rows for files that no longer exist under this root.
            progress?.Report(new LibraryScanProgress(done, candidates.Count, null, "cleanup"));
            await _repo.DeleteRowsNotInAsync(root, seen, ct);
        }
        ScanCompleted?.Invoke(this, EventArgs.Empty);
    }

    private async Task FlushBatch(List<(LibraryFile File, List<LibraryAiTag>? Tags)> batch, CancellationToken ct)
    {
        var ids = await _repo.UpsertFilesAsync(batch.Select(b => b.File).ToList(), ct);
        for (var i = 0; i < batch.Count; i++)
            if (batch[i].Tags is { Count: > 0 } t && ids[i] > 0)
                await _repo.SetAiTagsAsync(ids[i], t, ct);
    }

    private async Task<long> HashAndUpsertReturnId(LibraryFile f, CancellationToken ct)
    {
        await HashAndUpsertAsync(f, ct);
        return (await _repo.GetByPathAsync(f.Path, ct))?.Id ?? 0;
    }

    private IEnumerable<string> EnumerateMediaFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (SkipSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
                if (MediaExtensions.Contains(Path.GetExtension(f))) yield return f;
            }
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.')) continue;
                FileAttributes attrs;
                // Access-denied/vanished dirs must skip, not abort the whole scan.
                try { attrs = File.GetAttributes(d); }
                catch { continue; }
                if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                stack.Push(d);
            }
        }
    }

    /// <summary>%image_id%_p%page_index% or bare digits → (id, page) or nulls.</summary>
    internal static (string? ArtworkId, int? PageIndex) ParseArtworkId(string stem)
    {
        var m = PagedFileName.Match(stem);
        if (m.Success)
            return (m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        if (BareIdFileName.IsMatch(stem))
            return (stem, 0);
        return (null, null);
    }

    /// <summary>Parses "Character: a, b" style sidecar files written by TryAutoTagImageAsync.</summary>
    internal static List<LibraryAiTag> ParseSidecarTags(string text)
    {
        var tags = new List<LibraryAiTag>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var cat = line[..colon].Trim().ToLowerInvariant() switch
            {
                "character" => "character", "copyright" => "copyright",
                "artist" => "artist", "meta" => "meta", _ => "general",
            };
            foreach (var t in line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                tags.Add(new LibraryAiTag { Tag = t, Category = cat, Score = 1.0 });
        }
        return tags;
    }

    /// <summary>Runs the one-time initial scan flag-checked by the app host.</summary>
    public async Task EnsureInitialScanAsync(IProgress<LibraryScanProgress>? progress, CancellationToken ct)
    {
        if (_settings.Current.LibraryInitialScanCompleted) return;
        try
        {
            await ScanAsync(progress, ct);
            var s = _settings.Current;
            s.LibraryInitialScanCompleted = true;
            _settings.Save();
        }
        catch (OperationCanceledException) { /* resumable next launch — flag stays false */ }
    }

    // ── Enrichment ───────────────────────────────────────────────────────────

    /// <summary>Fetches Pixiv metadata for filename-only rows. One request at a time, ≥1.5s spacing.</summary>
    public async Task EnrichMissingMetadataAsync(IProgress<LibraryScanProgress>? progress, CancellationToken ct)
    {
        if (_client == null) return;
        var rows = await _repo.GetFilesMissingMetadataAsync(ct);
        var total = rows.Count; var done = 0;
        foreach (var f in rows)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new LibraryScanProgress(done, total, f.Path, "enrich"));
            try { await EnrichFileAsync(f, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Rate limited: back off for a minute, then retry this file once.
                _logger.LogInformation("Enrich rate-limited at {Id}; backing off 60s", f.ArtworkId);
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                try { await EnrichFileAsync(f, ct); }
                catch (Exception retryEx) { _logger.LogWarning(retryEx, "Enrich failed for {Id}", f.ArtworkId); }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Enrich failed for {Id}", f.ArtworkId); }
            var spacing = Math.Max(1500, _settings.Current.SafeMode ? 2500 : 0);
            await Task.Delay(spacing, ct);
        }
    }

    /// <summary>Fetches Pixiv metadata for one file (needs ArtworkId), saves it and updates
    /// <paramref name="f"/> in place. Returns false when nothing could be fetched.</summary>
    public async Task<bool> EnrichFileAsync(LibraryFile f, CancellationToken ct = default)
    {
        if (_client == null || string.IsNullOrEmpty(f.ArtworkId)) return false;
        var d = await _client.GetArtworkDetailAsync(f.ArtworkId, ct);
        if (d == null) return false;
        var tags = d.Tags?.Tags?.Select(t => t.Tag).ToList();
        var tagText = tags is { Count: > 0 } ? string.Join(' ', tags) : null;
        await _repo.UpdateMetadataAsync(f.Id, d.IllustTitle, d.UserId, d.UserName, tagText,
            d.IllustComment, d.XRestrict, d.AiType, d.CreateDate, "pixiv", ct);
        f.Title = d.IllustTitle; f.ArtistId = d.UserId; f.ArtistName = d.UserName; f.Tags = tagText;
        return true;
    }

    // ── AI tagging ───────────────────────────────────────────────────────────

    /// <summary>Whether the configured tagger model is installed.</summary>
    public bool IsTaggerModelInstalled(out TaggerModelInfo model)
    {
        model = KnownAnimeTaggerModels.GetByKey(_settings.Current.HoshiAnimeTaggerModel)
            ?? KnownAnimeTaggerModels.WdSwinV2TaggerV3;
        return _tagger != null && AnimeTaggerService.IsModelInstalled(model);
    }

    public Task<bool> EnsureTaggerModelAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var model = KnownAnimeTaggerModels.GetByKey(_settings.Current.HoshiAnimeTaggerModel)
            ?? KnownAnimeTaggerModels.WdSwinV2TaggerV3;
        return _tagger?.EnsureModelInstalledAsync(model, progress, ct) ?? Task.FromResult(false);
    }

    /// <summary>Raised after a library scan finishes (not when it is cancelled).</summary>
    public event EventHandler? ScanCompleted;

    /// <summary>Tags every untagged image in the index. Cancellable.
    /// <paramref name="beforeEach"/> runs before every image (background pass: waits while paused);
    /// <paramref name="writeSidecars"/> = false keeps background passes from writing .tags.txt files
    /// into the user's folders.</summary>
    public async Task TagUntaggedAsync(IProgress<LibraryScanProgress>? progress, CancellationToken ct,
        Func<CancellationToken, Task>? beforeEach = null, bool writeSidecars = true)
    {
        if (_tagger == null) return;
        if (!IsTaggerModelInstalled(out var model)) return;
        var rows = await _repo.GetFilesMissingAiTagsAsync(ct);
        var total = rows.Count; var done = 0;
        var s = _settings.Current;
        foreach (var f in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (beforeEach != null) await beforeEach(ct);
            done++;
            progress?.Report(new LibraryScanProgress(done, total, f.Path, "enrich"));
            try
            {
                if (!File.Exists(f.Path)) continue;
                var bytes = await File.ReadAllBytesAsync(f.Path, ct);
                var result = await _tagger.TagImageAsync(bytes, model,
                    s.HoshiAnimeTaggerThreshold, s.HoshiAnimeTaggerMaxTags, ct);
                await _repo.SetAiTagsAsync(f.Id, ToLibraryTags(result), ct);
                var sidecar = f.Path + ".tags.txt";
                if (writeSidecars && !File.Exists(sidecar))
                    await File.WriteAllTextAsync(sidecar,
                        $"Tags for {Path.GetFileName(f.Path)}\n\nGeneral: {string.Join(", ", result.General.Select(t => t.Name))}\n", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogWarning(ex, "Tagging failed for {Path}", f.Path); }
        }
    }

    /// <summary>Tags a single file on demand (context menu).</summary>
    public async Task TagSingleFileAsync(LibraryFile file, CancellationToken ct)
    {
        if (_tagger == null || !IsTaggerModelInstalled(out var model) || !File.Exists(file.Path)) return;
        var s = _settings.Current;
        var bytes = await File.ReadAllBytesAsync(file.Path, ct);
        var result = await _tagger.TagImageAsync(bytes, model, s.HoshiAnimeTaggerThreshold, s.HoshiAnimeTaggerMaxTags, ct);
        await _repo.SetAiTagsAsync(file.Id, ToLibraryTags(result), ct);
    }

    // ── Duplicates / delete ──────────────────────────────────────────────────

    /// <summary>Deletes files (Recycle Bin on Windows when requested) and removes their index rows.</summary>
    public async Task<int> DeleteFilesAsync(IReadOnlyList<long> ids, bool toRecycleBin, CancellationToken ct = default)
    {
        var deleted = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var f = await _repo.GetByIdAsync(id, ct);
            if (f == null) continue;
            try
            {
                if (File.Exists(f.Path))
                {
                    if (toRecycleBin && OperatingSystem.IsWindows())
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            f.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else
                        File.Delete(f.Path);
                }
                var sidecar = f.Path + ".tags.txt";
                if (File.Exists(sidecar)) File.Delete(sidecar);
                await _repo.DeleteByIdAsync(id, ct);
                deleted++;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete {Path}", f.Path); }
        }
        return deleted;
    }

    /// <summary>
    /// Low-priority background hashing pass: rows recorded without hashes, then
    /// rows whose phash predates the v2 (scaled-decode) algorithm.
    /// </summary>
    public async Task HashUnhashedAsync(IProgress<LibraryScanProgress>? progress, CancellationToken ct)
    {
        var unhashed = await _repo.GetFilesWithoutHashAsync(ct);
        var stale = await _repo.GetFilesNeedingRehashAsync(ct);
        var rows = unhashed.Concat(stale).ToList();
        var total = rows.Count; var done = 0;
        var dop = Math.Max(1, Environment.ProcessorCount / 2);
        await Parallel.ForEachAsync(rows,
            new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
            async (f, c) =>
            {
                var n = Interlocked.Increment(ref done);
                progress?.Report(new LibraryScanProgress(n, total, f.Path, "hash"));
                await WriteHashColumnsAsync(f.Id, f.Path, c);
            });
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _hashChannel.Writer.TryComplete();
    }
}
