using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pikura.Core.Http;
using Pikura.Core.Models;
using Pikura.Core.Settings;

namespace Pikura.Core.Services;

/// <summary>
/// Strongly-typed wrapper around Pixiv's APIs.
/// Uses Web API (cookie-based) for read operations and App API (OAuth 2.0) for write operations.
/// </summary>
public sealed partial class PixivClient
{
    private const string BaseUrl = "https://www.pixiv.net";
    private const string AppApiUrl = "https://app-api.pixiv.net";
    private const string OAuthClientId = "MOBrBDS8blbauCxckCKZ";
    private const string OAuthClientSecret = "hpACdFZglqyq9z2u";

    private string? _cachedAccessToken;
    private DateTime _accessTokenExpiry;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly PixivHttpClientFactory _httpFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<PixivClient> _logger;

    public PixivClient(
        PixivHttpClientFactory httpFactory,
        SettingsService settings,
        ILogger<PixivClient> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the signed-in user's id and name. The Pixiv session cookie has the
    /// shape <c>{userId}_{token}</c>, so we can pull the user id straight from
    /// <see cref="AppSettings.PhpSessId"/> without any HTTP round-trip. We then
    /// best-effort fetch a display name from the touch ajax endpoint; if that
    /// fails we just use the user id as the display name.
    /// </summary>
    public async Task<(string UserId, string UserName)?> ResolveSelfAsync(CancellationToken ct = default)
    {
        var sid = _settings.Current.PhpSessId;
        if (string.IsNullOrWhiteSpace(sid)) return null;

        // 1) Parse user id from the cookie itself. Format: "{userId}_{random_token}".
        var underscore = sid.IndexOf('_');
        if (underscore <= 0 || !sid[..underscore].All(char.IsDigit))
        {
            _logger.LogWarning("PHPSESSID has unexpected shape (length={Len})", sid.Length);
            return null;
        }
        var userId = sid[..underscore];

        // 2) Best-effort display-name lookup. Any failure is non-fatal.
        string? userName = null;
        try
        {
            var touch = await GetAjaxAsync<TouchSelfStatus>(
                $"{BaseUrl}/touch/ajax/user/self/status?lang={_settings.Current.Locale}", ct).ConfigureAwait(false);
            userName = touch?.UserStatus?.UserName;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "touch/user/self/status failed (non-fatal)"); }

        if (string.IsNullOrWhiteSpace(userName))
        {
            try
            {
                var info = await GetArtistAsync(userId, ct).ConfigureAwait(false);
                userName = info?.Name;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "GetArtist for self failed (non-fatal)"); }
        }

        return (userId, string.IsNullOrWhiteSpace(userName) ? userId : userName);
    }

    /// <summary>Returns true when the stored PHPSESSID cookie maps to a real account.</summary>
    public async Task<bool> ValidateSessionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.Current.PhpSessId)) return false;
        var self = await ResolveSelfAsync(ct).ConfigureAwait(false);
        if (self is null) return false;
        _settings.Update(s =>
        {
            s.UserId = self.Value.UserId;
            s.UserName = self.Value.UserName;
        });
        return true;
    }

    /// <summary>
    /// GET /ajax/user/{userId}/following — paged list of accounts the user follows.
    /// Pixiv's web client always sends <c>tag=</c> and <c>lang=</c> on this endpoint;
    /// omitting them has been observed to cap responses (the server returns only the
    /// first page regardless of <c>offset</c>), which is what caused issue #18
    /// ("48 of 225 followed artists loaded").
    /// </summary>
    public async Task<FollowingResponseBody> GetFollowedArtistsAsync(
        string userId, int offset = 0, int limit = 24,
        bool hidden = false, CancellationToken ct = default)
    {
        var rest = hidden ? "hide" : "show";
        var url = $"{BaseUrl}/ajax/user/{userId}/following" +
                  $"?tag=&offset={offset}&limit={limit}&rest={rest}" +
                  $"&acceptingRequests=0&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<FollowingResponseBody>(url, ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>GET /ajax/user/{userId}/profile/all — returns all illust/manga IDs.</summary>
    public async Task<UserProfileAll> GetUserProfileAllAsync(string userId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/user/{userId}/profile/all?lang={_settings.Current.Locale}";
        return await GetAjaxAsync<UserProfileAll>(url, ct).ConfigureAwait(false) ?? new();
    }

    // ─── Bookmarks ─────────────────────────────────────────────────────────

    /// <summary>
    /// GET /ajax/user/{userId}/illusts/bookmarks — bookmarked images/artworks.
    /// </summary>
    /// <param name="tag">Optional tag filter (null = all bookmarks).</param>
    /// <param name="hidden">If true, gets private bookmarks; if false, public bookmarks.</param>
    public async Task<BookmarkedArtworksResponse> GetBookmarkedArtworksAsync(
        string userId,
        string? tag = null,
        bool hidden = false,
        int offset = 0,
        int limit = 48,
        CancellationToken ct = default)
    {
        var rest = hidden ? "hide" : "show";
        var url = $"{BaseUrl}/ajax/user/{userId}/illusts/bookmarks?tag={Uri.EscapeDataString(tag ?? "")}&offset={offset}&limit={limit}&rest={rest}";
        var referer = $"{BaseUrl}/users/{userId}/bookmarks/artworks";

        // For private bookmarks, also dump raw response to diag so failures are visible
        if (hidden)
        {
            var raw = await GetAjaxRawAsync(url, referer, ct).ConfigureAwait(false);
            await WriteDiagAsync(url, $"[private-bookmarks raw]\n{raw ?? "(null — HTTP error)"}", ct).ConfigureAwait(false);
            if (raw != null)
            {
                try
                {
                    var envelope = System.Text.Json.JsonSerializer.Deserialize<PixivAjaxResponse<BookmarkedArtworksResponse>>(raw, JsonOpts);
                    if (envelope != null && !envelope.Error && envelope.Body != null)
                        return envelope.Body;
                }
                catch { }
            }
            return new();
        }

        return await GetAjaxAsync<BookmarkedArtworksResponse>(url, ct, referer).ConfigureAwait(false) ?? new();
    }

    /// <summary>
    /// GET /ajax/user/{userId}/novels/bookmarks — bookmarked novels.
    /// </summary>
    public async Task<BookmarkedArtworksResponse> GetBookmarkedNovelsAsync(
        string userId,
        string? tag = null,
        bool hidden = false,
        int offset = 0,
        int limit = 24,
        CancellationToken ct = default)
    {
        var rest = hidden ? "hide" : "show";
        var url = $"{BaseUrl}/ajax/user/{userId}/novels/bookmarks?tag={Uri.EscapeDataString(tag ?? "")}&offset={offset}&limit={limit}&rest={rest}";
        return await GetAjaxAsync<BookmarkedArtworksResponse>(url, ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>
    /// GET /ajax/user/{userId}/following — bookmarked/followed users (same as GetFollowedArtistsAsync but explicit naming).
    /// Same param requirement as <see cref="GetFollowedArtistsAsync"/>.
    /// </summary>
    public async Task<BookmarkedUsersResponse> GetBookmarkedUsersAsync(
        string userId,
        bool hidden = false,
        int offset = 0,
        int limit = 24,
        CancellationToken ct = default)
    {
        var rest = hidden ? "hide" : "show";
        var url = $"{BaseUrl}/ajax/user/{userId}/following" +
                  $"?tag=&offset={offset}&limit={limit}&rest={rest}" +
                  $"&acceptingRequests=0&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<BookmarkedUsersResponse>(url, ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>
    /// GET /ajax/illusts/bookmark/backup — recently bookmarked artworks (newest first).
    /// </summary>
    public async Task<BookmarkedArtworksResponse> GetRecentBookmarksAsync(
        bool hidden = false,
        int offset = 0,
        int limit = 48,
        CancellationToken ct = default)
    {
        var rest = hidden ? "hide" : "show";
        var url = $"{BaseUrl}/ajax/illusts/bookmark/backup?offset={offset}&limit={limit}&rest={rest}";
        return await GetAjaxAsync<BookmarkedArtworksResponse>(url, ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>
    /// GET /ajax/user/{userId}/profile/illusts?ids[]=...&amp;work_category=illustManga
    /// — returns metadata for up to ~50 artworks per call.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ArtworkPreview>> GetArtworksMetadataAsync(
        string userId, IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return new Dictionary<string, ArtworkPreview>();

        // Pixiv accepts repeated ids[]= query params.
        var query = string.Join("&", idList.Select(id => "ids%5B%5D=" + Uri.EscapeDataString(id)));
        var url = $"{BaseUrl}/ajax/user/{userId}/profile/illusts?{query}" +
                  $"&work_category=illustManga&is_first_page=0&lang={_settings.Current.Locale}";
        var body = await GetAjaxAsync<UserProfileIllusts>(url, ct).ConfigureAwait(false);
        return body?.Works ?? new Dictionary<string, ArtworkPreview>();
    }

    /// <summary>
    /// GET /ajax/follow_latest/illust — most recent illustrations posted by anyone the user follows.
    /// </summary>
    public async Task<FollowLatestBody> GetNewWorksFromFollowedAsync(
        int page = 1,
        bool r18Only = false,
        CancellationToken ct = default)
    {
        var mode = r18Only ? "r18" : "all";
        var url = $"{BaseUrl}/ajax/follow_latest/illust?p={page}&mode={mode}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<FollowLatestBody>(url, ct).ConfigureAwait(false) ?? new();
    }

    /// <summary>GET /ajax/user/{id} — fetch a single artist's public profile.</summary>
    public async Task<PixivUserInfo?> GetArtistAsync(string userId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/user/{userId}?lang={_settings.Current.Locale}";
        return await GetAjaxAsync<PixivUserInfo>(url, ct).ConfigureAwait(false);
    }

    /// <summary>GET /ajax/user/{id}?full=1 — includes background/banner URL.</summary>
    public async Task<PixivUserInfo?> GetArtistFullAsync(string userId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/user/{userId}?full=1&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<PixivUserInfo>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort artist search via <c>/ajax/search/users/{keyword}</c>. Pixiv has
    /// changed this endpoint repeatedly; if it 404s or shape-mismatches, returns empty.
    /// As a fallback we resolve a plain numeric keyword as a direct user-id lookup.
    /// </summary>
    public async Task<IReadOnlyList<UserSearchEntry>> SearchArtistsAsync(string keyword, CancellationToken ct = default)
    {
        keyword = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(keyword)) return [];

        // Direct ID / URL shortcut: if the keyword is a numeric id (or a pixiv URL
        // containing /users/{id}), resolve straight to that user.
        if (TryExtractUserId(keyword, out var directId))
        {
            var info = await GetArtistAsync(directId, ct).ConfigureAwait(false);
            if (info is not null)
            {
                return new[]
                {
                    new UserSearchEntry
                    {
                        UserId = info.UserId,
                        UserName = info.Name,
                        ProfileImageUrl = info.ImageUrl,
                        Comment = info.Comment,
                    },
                };
            }
        }

        // Pixiv's user search endpoint
        var url = $"{BaseUrl}/ajax/search/users/{Uri.EscapeDataString(keyword)}?lang={_settings.Current.Locale}";
        _logger.LogDebug("Searching artists: {Url}", url);
        var body = await GetAjaxAsync<UserSearchResult>(url, ct).ConfigureAwait(false);
        _logger.LogDebug("Search returned {Count} users", body?.Users?.Count ?? 0);
        return body?.Users ?? [];
    }

    private static bool TryExtractUserId(string keyword, out string userId)
    {
        userId = string.Empty;
        if (keyword.All(char.IsDigit) && keyword.Length is >= 1 and <= 12)
        {
            userId = keyword;
            return true;
        }
        var m = UrlUserIdRegex().Match(keyword);
        if (m.Success)
        {
            userId = m.Groups[1].Value;
            return true;
        }
        return false;
    }

    /// <summary>GET /ajax/illust/{id}/pages — list of original image URLs for the artwork.</summary>
    public async Task<IReadOnlyList<ArtworkPage>> GetArtworkPagesAsync(string artworkId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/illust/{artworkId}/pages?lang={_settings.Current.Locale}";
        return await GetAjaxAsync<IReadOnlyList<ArtworkPage>>(url, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>GET /ajax/illust/{id} — detailed artwork info including bookmark/like/view counts.</summary>
    public async Task<ArtworkDetailBody?> GetArtworkDetailAsync(string artworkId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/illust/{artworkId}?lang={_settings.Current.Locale}";
        return await GetAjaxAsync<ArtworkDetailBody>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /ajax/illust/{id}/ugoira_meta — frame-zip URL + per-frame delays for an
    /// animated ugoira (illustType==2). Returns null when the artwork is not a ugoira.
    /// </summary>
    public async Task<UgoiraMeta?> GetUgoiraMetaAsync(string artworkId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/illust/{artworkId}/ugoira_meta?lang={_settings.Current.Locale}";
        return await GetAjaxAsync<UgoiraMeta>(url, ct).ConfigureAwait(false);
    }

    // ─── Rankings ──────────────────────────────────────────────────────────

    /// <summary>
    /// GET /ranking.php?format=json — legacy endpoint for rankings.
    /// This is the only working endpoint for rankings; the AJAX version doesn't exist.
    /// Returns 50 entries per page without the { error, body } envelope.
    /// </summary>
    /// <param name="mode">daily, weekly, monthly, rookie, original, male,
    /// female, daily_r18, weekly_r18, male_r18, female_r18, r18g, daily_ai.</param>
    /// <param name="content">all, illust, manga, ugoira.</param>
    /// <param name="date">Optional YYYYMMDD (null = latest available).</param>
    /// <param name="page">1-based page index (50 items per page).</param>
    public async Task<RankingResponse> GetRankingsAsync(
        string mode = "daily",
        string? content = null,
        string? date = null,
        int page = 1,
        CancellationToken ct = default)
    {
        var qs = new List<string> { $"mode={Uri.EscapeDataString(mode)}", "format=json", $"p={page}" };
        if (!string.IsNullOrEmpty(content) && content != "all") qs.Add($"content={Uri.EscapeDataString(content)}");
        if (!string.IsNullOrEmpty(date)) qs.Add($"date={date}");

        var url = $"{BaseUrl}/ranking.php?{string.Join("&", qs)}";
        var client = _httpFactory.GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Ranking {Url} -> {Code}", url, resp.StatusCode);
            return new RankingResponse();
        }
        var body = await resp.Content.ReadFromJsonAsync<RankingResponse>(JsonOpts, ct).ConfigureAwait(false);
        return body ?? new RankingResponse();
    }

    /// <summary>Alias for GetRankingsAsync for backward compatibility.</summary>
    public Task<RankingResponse> GetRankingAsync(
        string mode = "daily",
        string content = "all",
        int page = 1,
        CancellationToken ct = default,
        string? date = null)
        => GetRankingsAsync(mode, content, date, page, ct);

    // ─── Artwork search ────────────────────────────────────────────────────

    /// <summary>
    /// GET /ajax/search/artworks/{keyword} — full-text search over artwork
    /// titles and tags. Pixiv supports a few sibling endpoints
    /// (/search/illustrations/, /search/manga/) but the shared endpoint
    /// returns everything so we use it as the default.
    /// </summary>
    public async Task<ArtworkSearchResult?> SearchArtworksAsync(
        string keyword,
        string order = "date_d",     // date_d = newest first; popular_d = popular first (Premium only)
        string mode = "safe",        // safe | r18 | all
        int page = 1,
        CancellationToken ct = default)
    {
        keyword = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(keyword)) return null;

        var url = $"{BaseUrl}/ajax/search/artworks/{Uri.EscapeDataString(keyword)}" +
                  $"?word={Uri.EscapeDataString(keyword)}" +
                  $"&order={Uri.EscapeDataString(order)}" +
                  $"&mode={Uri.EscapeDataString(mode)}" +
                  $"&p={page}" +
                  $"&s_mode=s_tag" +
                  $"&type=all" +
                  $"&lang={_settings.Current.Locale}";

        return await GetAjaxAsync<ArtworkSearchResult>(url, ct).ConfigureAwait(false);
    }

    // ─── New modern endpoints ──────────────────────────────────────────────

    /// <summary>
    /// GET /ajax/user/{userId}/illusts — user's illustrations with pagination.
    /// </summary>
    public async Task<UserIllustsResponse?> GetUserIllustsAsync(
        string userId, int offset = 0, int limit = 48, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/user/{userId}/illusts?offset={offset}&limit={limit}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<UserIllustsResponse>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /ajax/user/{userId}/manga — user's manga with pagination.
    /// </summary>
    public async Task<UserIllustsResponse?> GetUserMangaAsync(
        string userId, int offset = 0, int limit = 48, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/user/{userId}/manga?offset={offset}&limit={limit}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<UserIllustsResponse>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /ajax/user/{userId}/novels — user's novels with pagination.
    /// </summary>
    public async Task<UserNovelsResponse?> GetUserNovelsAsync(
        string userId, int offset = 0, int limit = 24, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/user/{userId}/novels?offset={offset}&limit={limit}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<UserNovelsResponse>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /ajax/search/novels/{keyword} — search novels by keyword.
    /// </summary>
    public async Task<NovelSearchResult?> SearchNovelsAsync(
        string keyword, string order = "date_d", int page = 1, CancellationToken ct = default)
    {
        keyword = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(keyword)) return null;
        var url = $"{BaseUrl}/ajax/search/novels/{Uri.EscapeDataString(keyword)}" +
                  $"?word={Uri.EscapeDataString(keyword)}&order={order}&p={page}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<NovelSearchResult>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /ajax/novel/{id} — detailed novel info.
    /// </summary>
    public async Task<NovelDetailResponse?> GetNovelDetailAsync(string novelId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/novel/{novelId}?lang={_settings.Current.Locale}";
        return await GetAjaxAsync<NovelDetailResponse>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /ajax/tags/suggest — tag autocomplete suggestions.
    /// </summary>
    public async Task<IReadOnlyList<TagSuggestion>> GetTagSuggestionsAsync(
        string query, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/tags/suggest?word={Uri.EscapeDataString(query)}&lang={_settings.Current.Locale}";
        var result = await GetAjaxAsync<TagSuggestResponse>(url, ct).ConfigureAwait(false);
        return result?.Candidates ?? [];
    }

    /// <summary>
    /// GET /ajax/user/{userId}/following/tags — tags used to organize followed users.
    /// </summary>
    public async Task<IReadOnlyList<FollowingTag>> GetFollowingTagsAsync(
        string userId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/user/{userId}/following/tags?lang={_settings.Current.Locale}";
        var result = await GetAjaxAsync<FollowingTagsResponse>(url, ct).ConfigureAwait(false);
        return result?.Tags ?? [];
    }

    /// <summary>Fetches the raw JSON body from a Pixiv /ajax endpoint
    /// without any deserialization — used by diagnostic dumps.</summary>
    private async Task<string?> GetAjaxRawAsync(string url, string? referer, CancellationToken ct)
    {
        var client = _httpFactory.GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", referer ?? BaseUrl + "/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("x-user-id", _settings.Current.UserId ?? string.Empty);
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return $"HTTP {(int)resp.StatusCode} {resp.StatusCode}\n{body}";
        return body;
    }

    private async Task<T?> GetAjaxAsync<T>(string url, CancellationToken ct, string? referer = null)
    {
        var client = _httpFactory.GetClient();
        var backoffSeconds = new[] { 5, 10, 20, 60 };
        var jitter = Random.Shared;
        var safeMode = _settings.Current.SafeMode;
        int attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", referer ?? BaseUrl + "/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("x-user-id", _settings.Current.UserId ?? string.Empty);

            HttpResponseMessage resp;
            try
            {
                resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pixiv {Url} network request failed", url);
                throw;
            }

            using (resp)
            {
                var status = (int)resp.StatusCode;
                var isRateLimited = status == 429 || status == 503;

                if (isRateLimited && safeMode && attempt < backoffSeconds.Length)
                {
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
                        var baseSec = backoffSeconds[attempt];
                        var jitterFactor = 0.75 + (jitter.NextDouble() * 0.5);
                        wait = TimeSpan.FromSeconds(baseSec * jitterFactor);
                    }

                    if (wait > TimeSpan.FromMinutes(5)) wait = TimeSpan.FromMinutes(5);

                    _logger.LogWarning("SafeMode: HTTP {Status} from {Url} — backing off {Seconds:F1}s (attempt {Attempt}/{TotalAttempts})",
                        status, url, wait.TotalSeconds, attempt + 1, backoffSeconds.Length);

                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Pixiv {Url} -> {Code}", url, resp.StatusCode);
                    await WriteDiagAsync(url, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}", ct);
                    throw new HttpRequestException($"Pixiv API returned HTTP {(int)resp.StatusCode} {resp.StatusCode} for {url}", null, resp.StatusCode);
                }

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                PixivAjaxResponse<T>? envelope;
                try { envelope = System.Text.Json.JsonSerializer.Deserialize<PixivAjaxResponse<T>>(body, JsonOpts); }
                catch (Exception ex) { _logger.LogWarning(ex, "Pixiv {Url} JSON parse failed", url); throw; }
                if (envelope is null || envelope.Error)
                {
                    _logger.LogWarning("Pixiv {Url} error: {Msg}", url, envelope?.Message);
                    await WriteDiagAsync(url, $"error=true msg={envelope?.Message}\nBody={body[..Math.Min(500, body.Length)]}", ct);
                    throw new InvalidOperationException($"Pixiv API error: {envelope?.Message ?? "Unknown error"}");
                }
                return envelope.Body;
            }
        }
    }

    /// <summary>
    /// POST /web/v1/illust/bookmark/add — add an artwork to Pixiv bookmarks using App API.
    /// Requires OAuth authentication (refresh token). Returns the bookmark id on success, null on failure.
    /// </summary>
    public async Task<string?> AddPixivBookmarkAsync(
        string illustId,
        bool restrict = false,
        IEnumerable<string>? tags = null,
        string? comment = null,
        CancellationToken ct = default)
    {
        var accessToken = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (accessToken == null)
        {
            _logger.LogWarning("AddBookmark {Id}: could not obtain access token", illustId);
            return null;
        }

        var diagPath = System.IO.Path.Combine(Path.GetTempPath(), "pikura_bookmark_diag.txt");

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", "PixivAndroidApp/5.0.64 (Android 6.0)");

            var payload = new
            {
                illust_id = illustId,
                restrict = restrict ? "private" : "public",
                tags = tags?.ToArray() ?? Array.Empty<string>(),
                comment = comment ?? string.Empty,
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{AppApiUrl}/web/v1/illust/bookmark/add", content, ct).ConfigureAwait(false);
            var respBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AddBookmark {Id} -> {Code}: {Body}", illustId, response.StatusCode, respBody);
                await File.WriteAllTextAsync(diagPath,
                    $"[{DateTime.Now}] illust={illustId}\nHTTP {(int)response.StatusCode} {response.StatusCode}\nBody={respBody}\n", ct)
                    .ConfigureAwait(false);
                return null;
            }

            var result = System.Text.Json.JsonSerializer.Deserialize<AppApiBookmarkResponse>(respBody, JsonOpts);
            var bookmarkId = result?.Bookmark_id ?? illustId;

            await File.WriteAllTextAsync(diagPath,
                $"[{DateTime.Now}] illust={illustId}\nSUCCESS bookmark_id={bookmarkId}\n", ct)
                .ConfigureAwait(false);

            _logger.LogDebug("AddBookmark {Id} succeeded with bookmark_id={BookmarkId}", illustId, bookmarkId);
            return bookmarkId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddBookmark {Id} failed", illustId);
            await File.WriteAllTextAsync(diagPath,
                $"[{DateTime.Now}] illust={illustId}\nEXCEPTION: {ex.Message}\n", ct)
                .ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// POST /web/v1/illust/bookmark/delete — remove an artwork from Pixiv bookmarks using App API.
    /// Requires OAuth authentication. Returns true on success, false on failure.
    /// </summary>
    public async Task<bool> RemovePixivBookmarkAsync(
        string bookmarkId,
        string illustId,
        CancellationToken ct = default)
    {
        var accessToken = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (accessToken == null) return false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", "PixivAndroidApp/5.0.64 (Android 6.0)");

            var response = await client.PostAsync($"{AppApiUrl}/web/v1/illust/bookmark/delete",
                new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("bookmark_id", bookmarkId),
                }), ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("RemoveBookmark {Id} -> {Code}", bookmarkId, response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoveBookmark {Id} failed", bookmarkId);
            return false;
        }
    }

    /// <summary>
    /// GET /ajax/illust/{illustId} — returns full artwork info. We read bookmarkData from it
    /// to determine current bookmark state. There is no dedicated state endpoint anymore.
    /// </summary>
    public async Task<ArtworkBookmarkState?> GetBookmarkStateAsync(
        string illustId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/illust/{illustId}?lang={_settings.Current.Locale}";
        var info = await GetAjaxAsync<ArtworkBookmarkState>(url, ct).ConfigureAwait(false);
        return info;
    }

    private static Task WriteDiagAsync(string context, string detail, CancellationToken ct = default)
    {
        var path = System.IO.Path.Combine(Path.GetTempPath(), "pikura_api_diag.txt");
        return File.AppendAllTextAsync(path, $"[{DateTime.Now}] {context}\n{detail}\n\n", ct);
    }

    // Cache the CSRF token so we only fetch it once per session (it's stable until re-login).
    private string? _cachedCsrfToken;

    /// <summary>
    /// Extracts the tt CSRF token from a Pixiv page. Tries multiple sources and patterns
    /// because Pixiv's Next.js migration moved the token around the HTML.
    /// </summary>
    private async Task<string?> GetCsrfTokenAsync(string? illustId, CancellationToken ct)
    {
        if (_cachedCsrfToken != null) return _cachedCsrfToken;
        // Try sources in order: artwork page (returns 200 with __NEXT_DATA__) → root → settings page
        var candidates = new[]
        {
            illustId != null ? $"{BaseUrl}/en/artworks/{illustId}" : null,
            $"{BaseUrl}/",
            $"{BaseUrl}/setting_user.php",
        };
        foreach (var url in candidates)
        {
            if (url == null) continue;
            var token = await TryFetchCsrfFromAsync(url, ct).ConfigureAwait(false);
            if (token != null)
            {
                _cachedCsrfToken = token;
                _logger.LogDebug("GetCsrfToken: cached token len={Len} from {Url}", token.Length, url);
                return token;
            }
        }
        return null;
    }

    private async Task<string?> TryFetchCsrfFromAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.GetClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                await WriteDiagAsync(url, $"CSRF fetch HTTP {(int)resp.StatusCode}", ct);
                return null;
            }
            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Try patterns in order of specificity
            // 1) meta-global-data content blob
            var metaM = MetaGlobalDataRegex().Match(html);
            if (metaM.Success)
            {
                var inner = CsrfTokenRegex().Match(metaM.Groups[1].Value);
                if (inner.Success) return inner.Groups[1].Value;
            }
            // 2) "token":"..." anywhere
            var tokM = CsrfTokenRegex().Match(html);
            if (tokM.Success) return tokM.Groups[1].Value;
            // 3) "tt":"..." anywhere (Next.js pageProps)
            var ttM = TtTokenRegex().Match(html);
            if (ttM.Success) return ttM.Groups[1].Value;
            // Dump the full HTML to disk one time so we can inspect the actual token format
            var dumpPath = System.IO.Path.Combine(Path.GetTempPath(), "pikura_page_dump.html");
            if (!File.Exists(dumpPath))
                await File.WriteAllTextAsync(dumpPath, html, ct).ConfigureAwait(false);
            await WriteDiagAsync(url, $"CSRF: no pattern matched (htmlLen={html.Length}). Full HTML at {dumpPath}", ct);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CSRF fetch failed for {Url}", url);
            return null;
        }
    }

    // Extracts the content attribute of the meta-global-data tag (handles single or double quotes)
    [GeneratedRegex("meta-global-data[^>]+content=['\"]([^'\"]{20,})['\"]>", RegexOptions.Singleline)]
    private static partial Regex MetaGlobalDataRegex();

    // Extracts token value from JSON — matches both lowercase and mixed-case hex
    [GeneratedRegex("\"token\":\"([0-9a-fA-F]{8,})\"")]
    private static partial Regex CsrfTokenRegex();

    // Pixiv Next.js pageProps embed the CSRF token as "tt":"<hex>"
    [GeneratedRegex("\"tt\":\"([0-9a-fA-F]{32,})\"")]
    private static partial Regex TtTokenRegex();

    [GeneratedRegex("\"userData\":\\{\"id\":\"(\\d+)\"")]
    private static partial Regex GlobalDataIdRegex();

    [GeneratedRegex("\"userData\":\\{[^}]*?\"name\":\"([^\"]+)\"")]
    private static partial Regex GlobalDataNameRegex();

    [GeneratedRegex("\"userId\"\\s*:\\s*\"(\\d+)\"")]
    private static partial Regex AnyUserIdRegex();

    [GeneratedRegex(@"(?:pixiv\.net/(?:en/)?users?/|members?\.php\?id=)(\d+)")]
    private static partial Regex UrlUserIdRegex();

    // ─── OAuth Authentication for App API ─────────────────────────────────────

    /// <summary>
    /// Gets a valid access token for App API, refreshing if necessary.
    /// </summary>
    private async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_cachedAccessToken != null && DateTime.UtcNow < _accessTokenExpiry)
            return _cachedAccessToken;

        var refreshToken = _settings.Current.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            _logger.LogWarning("No refresh token configured for App API");
            return null;
        }

        try
        {
            using var client = new HttpClient();
            var response = await client.PostAsync(
                "https://oauth.secure.pixiv.net/auth/token",
                new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", OAuthClientId),
                    new KeyValuePair<string, string>("client_secret", OAuthClientSecret),
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("refresh_token", refreshToken),
                    new KeyValuePair<string, string>("include_policy", "true")
                }), ct).ConfigureAwait(false);

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OAuth refresh failed: {StatusCode} - {Content}", response.StatusCode, content);
                return null;
            }

            var tokenData = JsonSerializer.Deserialize<OAuthTokenResponse>(content, JsonOpts);
            if (tokenData?.Access_token == null)
            {
                _logger.LogWarning("OAuth response missing access token");
                return null;
            }

            _cachedAccessToken = tokenData.Access_token;
            _accessTokenExpiry = DateTime.UtcNow.AddSeconds(tokenData.Expires_in - 60); // Refresh 1 min before expiry

            // Update refresh token if a new one was provided
            if (!string.IsNullOrEmpty(tokenData.Refresh_token))
            {
                _settings.Update(s => s.RefreshToken = tokenData.Refresh_token);
            }

            _logger.LogDebug("OAuth access token obtained successfully");
            return _cachedAccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OAuth authentication failed");
            return null;
        }
    }

    // ─── Discovery & Recommendations ────────────────────────────────────────

    /// <summary>
    /// GET /ajax/illust/{illustId}/recommend/init — Get related/recommended artworks from a specific artwork.
    /// </summary>
    /// <param name="illustId">The illustration ID to get recommendations from.</param>
    /// <param name="limit">Maximum number of recommendations (capped at 180 by API).</param>
    public async Task<RecommendIllustsResponse?> GetRelatedWorksAsync(
        string illustId,
        int limit = 48,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/illust/{illustId}/recommend/init?limit={limit}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<RecommendIllustsResponse>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /ajax/discovery/artworks — Get discovery/recommended artworks for the logged-in user.
    /// </summary>
    public async Task<DiscoveryArtworksResponse?> GetDiscoveryArtworksAsync(
        int offset = 0,
        int limit = 48,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/discovery/artworks?mode=all&limit={limit}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<DiscoveryArtworksResponse>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /ajax/discovery/users — Get discovery/recommended users for the logged-in user.
    /// </summary>
    public async Task<DiscoveryUsersResponse?> GetDiscoveryUsersAsync(
        int offset = 0,
        int limit = 48,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/discovery/users?mode=all&limit={limit}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<DiscoveryUsersResponse>(url, ct).ConfigureAwait(false);
    }
}
