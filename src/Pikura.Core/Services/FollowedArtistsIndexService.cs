using Microsoft.Extensions.Logging;
using Pikura.Core.Data;
using Pikura.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Core.Services;

/// <summary>
/// Builds and maintains a local SQLite index of every followed artist's artwork
/// catalogue (title + tags), so the Search tab can query across all followed
/// artists instantly instead of fetching every artist's works on every search.
///
/// The crawl is deliberately throttled (small delay between artists, and between
/// each paginated batch within an artist) to stay well clear of Pixiv's rate
/// limiter — this mirrors the delay used in <see cref="ArtistMonitorService"/>.
/// </summary>
public sealed class FollowedArtistsIndexService : IDisposable
{
    private readonly PixivClient _client;
    private readonly ArtworkIndexRepository _repository;
    private readonly ILogger<FollowedArtistsIndexService> _logger;

    private const int BatchSize = 48;
    private const int MaxConcurrentArtists = 3;
    private const int MaxRetriesPerArtist = 4;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(3);
    private static readonly TimeSpan ArtistDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(300);

    private int _isRunning; // 0/1 guard, Interlocked
    private readonly Timer _recrawlTimer;
    private TimeSpan _recrawlInterval = TimeSpan.FromHours(6);
    private bool _isTimerRunning;

    /// <summary>Raised as the background crawl progresses: (current, total, currentArtistName).</summary>
    public event EventHandler<IndexProgressEventArgs>? ProgressChanged;

    public FollowedArtistsIndexService(
        PixivClient client,
        ArtworkIndexRepository repository,
        ILogger<FollowedArtistsIndexService> logger)
    {
        _client = client;
        _repository = repository;
        _logger = logger;
        _recrawlTimer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Starts periodic re-crawling so newly-followed artists are picked up and
    /// the index doesn't go stale during a long-running session without
    /// requiring an app restart. Each tick calls <see cref="BuildIndexAsync"/>,
    /// which is itself a no-op for any artist indexed within <see cref="StaleAfter"/>.
    /// </summary>
    public void Start()
    {
        if (_isTimerRunning) return;
        _isTimerRunning = true;
        _recrawlTimer.Change(_recrawlInterval, _recrawlInterval);
        _logger.LogInformation("Artwork index periodic re-crawl started with interval: {Interval}", _recrawlInterval);
    }

    public void Stop()
    {
        _isTimerRunning = false;
        _recrawlTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnTimerTick(object? state)
    {
        if (!_isTimerRunning) return;
        _ = Task.Run(async () =>
        {
            try { await BuildIndexAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Periodic artwork index re-crawl failed"); }
        });
    }

    /// <summary>
    /// Crawls every followed artist whose index is missing or older than
    /// <see cref="StaleAfter"/>, upserting their full catalogue into the local
    /// index. Safe to call repeatedly — a second call while one is already
    /// running is a no-op.
    /// </summary>
    public async Task BuildIndexAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            _logger.LogDebug("Index build already in progress — skipping");
            return;
        }

        try
        {
            var self = await _client.ResolveSelfAsync(ct).ConfigureAwait(false);
            if (self is null)
            {
                _logger.LogWarning("Not logged in — cannot build artwork index");
                return;
            }

            var followed = await FetchAllFollowedArtistsAsync(self.Value.UserId, ct).ConfigureAwait(false);
            if (followed.Count == 0) return;

            var states = (await _repository.GetAllArtistStatesAsync(ct).ConfigureAwait(false))
                .ToDictionary(s => s.ArtistUserId);

            // Prune index entries for artists no longer followed (unfollowed on
            // pixiv.net directly — Pikura has no unfollow action of its own).
            var followedIds = new HashSet<string>(followed.Select(a => a.UserId));
            var stalePrunes = states.Keys.Where(id => !followedIds.Contains(id)).ToList();
            foreach (var staleId in stalePrunes)
            {
                try { await _repository.RemoveArtistAsync(staleId, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to prune unfollowed artist {UserId} from index", staleId); }
            }
            if (stalePrunes.Count > 0)
                _logger.LogInformation("Pruned {Count} unfollowed artists from artwork index", stalePrunes.Count);

            var toIndex = followed
                .Where(a => !states.TryGetValue(a.UserId, out var s)
                            || s.LastIndexedAt is null
                            || DateTime.UtcNow - s.LastIndexedAt.Value > StaleAfter)
                .ToList();

            _logger.LogInformation("Artwork index: {Count} of {Total} followed artists need (re)indexing",
                toIndex.Count, followed.Count);

            // Bounded-concurrency crawl instead of one-at-a-time — a large follow list
            // (1000+) took far too long sequentially. Still throttled (MaxConcurrentArtists
            // at once, small delay before each fetch) to stay clear of Pixiv's rate limiter.
            var completed = 0;
            using var throttle = new SemaphoreSlim(MaxConcurrentArtists);
            var tasks = toIndex.Select(async artist =>
            {
                await throttle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(ArtistDelay, ct).ConfigureAwait(false);
                    await IndexArtistWithRetryAsync(artist.UserId, artist.UserName, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to index artist {UserId} ({UserName}) after retries", artist.UserId, artist.UserName);
                }
                finally
                {
                    throttle.Release();
                    var done = Interlocked.Increment(ref completed);
                    ProgressChanged?.Invoke(this, new IndexProgressEventArgs(done, toIndex.Count, artist.UserName));
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            ProgressChanged?.Invoke(this, new IndexProgressEventArgs(toIndex.Count, toIndex.Count, null));
            _logger.LogInformation("Artwork index build complete");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Artwork index build failed");
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    /// <summary>Re-indexes a single artist immediately (e.g. after a new-follow, or a manual refresh).</summary>
    public async Task RefreshArtistAsync(string userId, string userName, CancellationToken ct = default)
    {
        try
        {
            await IndexArtistWithRetryAsync(userId, userName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh artist {UserId}", userId);
        }
    }

    /// <summary>
    /// Wraps <see cref="IndexArtistAsync"/> with 429-specific retry/backoff, independent
    /// of the user's interactive SafeMode toggle — this is a background bulk crawl and
    /// should always be conservative about Pixiv's rate limiter regardless of that setting.
    /// </summary>
    private async Task IndexArtistWithRetryAsync(string userId, string userName, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await IndexArtistAsync(userId, userName, ct).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                                                    && attempt < MaxRetriesPerArtist)
            {
                attempt++;
                var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt) * 2); // 4s, 8s, 16s, 32s
                _logger.LogDebug("Rate-limited indexing {UserId} — retrying in {Seconds}s (attempt {Attempt}/{Max})",
                    userId, wait.TotalSeconds, attempt, MaxRetriesPerArtist);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task IndexArtistAsync(string userId, string userName, CancellationToken ct)
    {
        // GetUserIllustsAsync/GetUserMangaAsync (/ajax/user/{id}/illusts|manga) return
        // HTTP 400 for every artist — not a valid Pixiv endpoint for full pagination.
        // Use the confirmed-working two-step approach instead (same as
        // ArtistMonitorService/GalleryViewModel): fetch all IDs, then batch-fetch metadata.
        var profile = await _client.GetUserProfileAllAsync(userId, ct).ConfigureAwait(false);
        var allIds = profile?.AllArtworkIds() ?? [];
        if (allIds.Count == 0)
        {
            await _repository.ReplaceArtistArtworksAsync(userId, userName, [], ct).ConfigureAwait(false);
            return;
        }

        var all = new List<ArtworkPreview>();
        for (int i = 0; i < allIds.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batchIds = allIds.Skip(i).Take(BatchSize).ToList();
            var metadata = await _client.GetArtworksMetadataAsync(userId, batchIds, ct).ConfigureAwait(false);
            all.AddRange(metadata.Values);

            if (i + BatchSize < allIds.Count)
                await Task.Delay(PageDelay, ct).ConfigureAwait(false);
        }

        await _repository.ReplaceArtistArtworksAsync(userId, userName,
            all.Select(ToIndexed).ToList(), ct).ConfigureAwait(false);
    }

    private static IndexedArtwork ToIndexed(ArtworkPreview p) => new()
    {
        ArtworkId = p.Id,
        ArtistUserId = p.UserId,
        ArtistUserName = p.UserName,
        Title = p.Title,
        Tags = p.Tags,
        ThumbnailUrl = p.ThumbnailUrl,
        IllustType = p.IllustType,
        XRestrict = p.XRestrict,
        AiType = p.AiType,
        PageCount = p.PageCount,
        Width = p.Width,
        Height = p.Height,
        CreateDate = p.CreateDate,
    };

    /// <summary>Fetches the complete followed-artists list (public + private) across all pages.</summary>
    private async Task<List<PixivArtistSummary>> FetchAllFollowedArtistsAsync(string selfUserId, CancellationToken ct)
    {
        var result = new List<PixivArtistSummary>();
        var seenIds = new HashSet<string>();

        foreach (var hidden in new[] { false, true })
        {
            var offset = 0;
            const int limit = 96;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var resp = await _client.GetFollowedArtistsAsync(selfUserId, offset, limit, hidden, ct).ConfigureAwait(false);
                if (resp.Users.Count == 0) break;

                foreach (var u in resp.Users)
                {
                    if (seenIds.Add(u.UserId))
                        result.Add(new PixivArtistSummary(u.UserId, u.UserName));
                }

                offset += resp.Users.Count;
                if (offset >= resp.Total) break;

                await Task.Delay(PageDelay, ct).ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <summary>Total number of artworks currently indexed (for status display).</summary>
    public Task<int> GetTotalIndexedCountAsync(CancellationToken ct = default)
        => _repository.GetTotalIndexedCountAsync(ct);

    /// <summary>Searches the local index. See <see cref="ArtworkIndexRepository.SearchAsync"/>.</summary>
    public Task<(List<IndexedArtwork> Results, int Total)> SearchAsync(
        string? keyword, IReadOnlyList<string>? includeTags, IReadOnlyList<string>? excludeTags,
        int offset, int limit, CancellationToken ct = default)
        => _repository.SearchAsync(keyword, includeTags, excludeTags, offset, limit, ct);

    public void Dispose() => _recrawlTimer.Dispose();
}

/// <summary>Minimal artist identity used internally while crawling.</summary>
public sealed record PixivArtistSummary(string UserId, string UserName);

public sealed class IndexProgressEventArgs : EventArgs
{
    public int Current { get; }
    public int Total { get; }
    public string? CurrentArtistName { get; }

    public IndexProgressEventArgs(int current, int total, string? currentArtistName)
    {
        Current = current;
        Total = total;
        CurrentArtistName = currentArtistName;
    }
}
