using System.Net.Http.Json;
using System.Linq;
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

        try
        {
            var isPremium = await GetIsPremiumAsync(ct).ConfigureAwait(false);
            if (isPremium.HasValue)
                _settings.Update(s => s.IsPremium = isPremium.Value);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Premium status check failed (non-fatal)"); }

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
        // Pixiv's web client always sends tag= and lang= on this endpoint; omitting
        // lang= has been observed to cap responses (issue #22: only the first ~96/144
        // bookmarks load regardless of offset, because the server stops paginating).
        var url = $"{BaseUrl}/ajax/user/{userId}/illusts/bookmarks?tag={Uri.EscapeDataString(tag ?? "")}&offset={offset}&limit={limit}&rest={rest}&lang={_settings.Current.Locale}";
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
        var url = $"{BaseUrl}/ajax/user/{userId}/novels/bookmarks?tag={Uri.EscapeDataString(tag ?? "")}&offset={offset}&limit={limit}&rest={rest}&lang={_settings.Current.Locale}";
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
        var url = $"{BaseUrl}/ajax/illusts/bookmark/backup?offset={offset}&limit={limit}&rest={rest}&lang={_settings.Current.Locale}";
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
                        Name = info.Name,
                        ImageUrl = info.ImageUrl,
                        Comment = info.Comment,
                    },
                };
            }
        }

        // The old /ajax/search/users/{keyword} endpoint 404s — reuse the HTML-scraping
        // implementation in SearchUsersAsync instead (see its docs for why).
        var result = await SearchUsersAsync(keyword, page: 1, ct).ConfigureAwait(false);
        _logger.LogDebug("Searching artists {Keyword}: {Count} users", keyword, result?.Users?.Count ?? 0);
        return result?.Users ?? [];
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

    /// <summary>
    /// GET /novel/ranking.php?format=json — separate endpoint from the illust/manga/ugoira
    /// ranking (novels are not a <c>content=</c> value on <c>/ranking.php</c>).
    /// </summary>
    /// <param name="mode">daily, weekly, monthly, rookie, weekly_original, weekly_ai,
    /// male, female (append "_r18" for the R-18 variant, mirroring the illust ranking modes).</param>
    /// <param name="date">Optional YYYYMMDD (null = latest available).</param>
    /// <param name="page">1-based page index.</param>
    public async Task<NovelRankingResponse> GetNovelRankingsAsync(
        string mode = "daily",
        string? date = null,
        int page = 1,
        CancellationToken ct = default)
    {
        var qs = new List<string> { $"mode={Uri.EscapeDataString(mode)}", "format=json", $"p={page}" };
        if (!string.IsNullOrEmpty(date)) qs.Add($"date={date}");

        var url = $"{BaseUrl}/novel/ranking.php?{string.Join("&", qs)}";
        var client = _httpFactory.GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Novel ranking {Url} -> {Code}", url, resp.StatusCode);
            return new NovelRankingResponse();
        }
        try
        {
            var body = await resp.Content.ReadFromJsonAsync<NovelRankingResponse>(JsonOpts, ct).ConfigureAwait(false);
            return body ?? new NovelRankingResponse();
        }
        catch (Exception ex)
        {
            // Best-effort field mapping (see NovelRankingEntry) — degrade to empty
            // rather than crash the Rankings tab if Pixiv's actual shape differs.
            _logger.LogWarning(ex, "Novel ranking {Url} JSON parse failed", url);
            return new NovelRankingResponse();
        }
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
    /// Full-text search over artwork titles and tags. Routes to whichever of pixiv's ajax search
    /// endpoints actually honors the requested work type — reverse-engineered from a live
    /// authenticated browser session's DevTools Network tab (2026-08-08), since this isn't
    /// documented anywhere:
    /// <list type="bullet">
    /// <item><c>GET /ajax/search/artworks/{keyword}</c> — the combined illust+manga feed. Its own
    /// <c>type=</c> param is silently ignored server-side (confirmed live: requesting
    /// <c>type=illust_and_ugoira</c> still returned manga-type results, and the reported
    /// <c>total</c> never changes regardless of <c>type=</c>) — used here only when no specific
    /// work type is requested (i.e. <paramref name="options"/> is null, as with Download-by-Search).</item>
    /// <item><c>GET /ajax/search/illustrations/{keyword}</c> — used whenever <see cref="ArtworkSearchOptions.WorkType"/>
    /// is illust/ugoira/illust_and_ugoira. Its <c>type=</c> *does* filter server-side (confirmed live:
    /// <c>type=ugoira</c> returned 60/60 illustType=2 results with an accurate <c>total</c>).</item>
    /// <item><c>GET /ajax/search/manga/{keyword}</c> — used for the Manga work type. Same shape/behavior
    /// as the illustrations endpoint, minus the <c>type=</c> param (manga has no sub-types to filter).</item>
    /// </list>
    /// Both dedicated endpoints return their section keyed as <c>"illust"</c>/<c>"manga"</c> rather than
    /// <c>"illustManga"</c> — repackaged into <see cref="ArtworkSearchResult.IllustManga"/> below so every
    /// existing caller keeps working unchanged.
    /// </summary>
    public async Task<ArtworkSearchResult?> SearchArtworksAsync(
        string keyword,
        string order = "date_d",     // date_d/date = newest; popular_d/popular, popular_male_d/popular_male,
                                      // popular_female_d/popular_female = popularity sorts (Premium only)
        string mode = "safe",        // safe | r18 | all
        int page = 1,
        ArtworkSearchOptions? options = null,
        CancellationToken ct = default)
    {
        keyword = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(keyword)) return null;

        var endpoint = options?.WorkType switch
        {
            "manga" => "manga",
            "illust" or "ugoira" or "illust_and_ugoira" => "illustrations",
            _ => "artworks",
        };

        var sb = new StringBuilder($"{BaseUrl}/ajax/search/{endpoint}/{Uri.EscapeDataString(keyword)}")
            .Append($"?word={Uri.EscapeDataString(keyword)}")
            .Append($"&order={Uri.EscapeDataString(order)}")
            .Append($"&mode={Uri.EscapeDataString(mode)}")
            .Append($"&p={page}")
            .Append($"&s_mode={Uri.EscapeDataString(options?.TargetMode ?? "s_tag")}");

        // Only the combined/illustrations endpoints take a `type=` param — manga has no sub-types.
        if (endpoint != "manga")
            sb.Append($"&type={Uri.EscapeDataString(options?.WorkType ?? "all")}");

        sb.Append($"&lang={_settings.Current.Locale}");

        if (options is not null)
        {
            sb.Append($"&ratio={(options.Ratio is { } ratio ? ratio.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}");
            if (!string.IsNullOrWhiteSpace(options.Tool)) sb.Append($"&tool={Uri.EscapeDataString(options.Tool)}");
            if (options.PostedAfter is { } scd) sb.Append($"&scd={scd:yyyy-MM-dd}");
            if (options.PostedBefore is { } ecd) sb.Append($"&ecd={ecd:yyyy-MM-dd}");
            if (options.MinWidth is { } wlt) sb.Append($"&wlt={wlt}");
            if (options.MaxWidth is { } wgt) sb.Append($"&wgt={wgt}");
            if (options.MinHeight is { } hlt) sb.Append($"&hlt={hlt}");
            if (options.MaxHeight is { } hgt) sb.Append($"&hgt={hgt}");
            if (options.MinBookmarks is { } blt) sb.Append($"&blt={blt}");
            if (options.MaxBookmarks is { } bgt) sb.Append($"&bgt={bgt}");
            // The dedicated endpoints require ai_type/csw to be present (0 = show AI / don't bundle)
            // rather than omitted, matching pixiv's own frontend requests.
            if (endpoint != "artworks")
            {
                sb.Append($"&ai_type={options.AiType ?? 0}");
                sb.Append("&csw=0"); // "Bundle works by the same creator" — not exposed in our UI.
            }
            else if (options.AiType is { } aiType) sb.Append($"&ai_type={aiType}");
        }

        var result = await GetAjaxAsync<ArtworkSearchResult>(sb.ToString(), ct).ConfigureAwait(false);
        if (result is null) return null;
        if (endpoint == "artworks") return result;

        var section = endpoint == "manga" ? result.Manga : result.Illust;
        return result with { IllustManga = section ?? new ArtworkSearchSection() };
    }

    /// <summary>
    /// GET /search/users?s_mode=s_usr&amp;nick={keyword}&amp;i=1&amp;comment=&amp;p={page} — search pixiv
    /// user accounts by name. There is no ajax/JSON endpoint for this anymore (the old
    /// /ajax/search/users/{keyword} 404s) — the HTML search page embeds its data as a
    /// Next.js <c>&lt;script id="__NEXT_DATA__"&gt;</c> JSON blob, so we scrape that instead.
    /// See <see cref="PixivUserSearchNextData"/> for the blob's shape.
    /// </summary>
    public async Task<UserSearchResult?> SearchUsersAsync(
        string keyword, int page = 1, CancellationToken ct = default)
    {
        keyword = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(keyword)) return null;

        var url = $"{BaseUrl}/search/users?s_mode=s_usr&nick={Uri.EscapeDataString(keyword)}&i=1&comment=&p={page}";
        var client = _httpFactory.GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("SearchUsersAsync {Url} -> {Code}", url, resp.StatusCode);
            return null;
        }

        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var m = NextDataRegex().Match(html);
        if (!m.Success)
        {
            _logger.LogWarning("SearchUsersAsync {Url}: __NEXT_DATA__ not found in response", url);
            return null;
        }

        PixivUserSearchNextData? nextData;
        try { nextData = System.Text.Json.JsonSerializer.Deserialize<PixivUserSearchNextData>(m.Groups[1].Value, JsonOpts); }
        catch (Exception ex) { _logger.LogWarning(ex, "SearchUsersAsync {Url} JSON parse failed", url); return null; }

        var pageProps = nextData?.Props?.PageProps;
        var userIds = pageProps?.UserIds ?? [];
        var workIds = pageProps?.WorkIds ?? new();

        // Pixiv's current page shape puts the user map directly under pageProps.userData.
        // Older shapes nest it inside a JSON string at serverSerializedPreloadedState.
        var usersMap = pageProps?.UserData?.Users;
        if (usersMap is null or { Count: 0 } && !string.IsNullOrEmpty(pageProps?.ServerSerializedPreloadedState))
        {
            try
            {
                var preloaded = System.Text.Json.JsonSerializer.Deserialize<PixivUserSearchPreloadedState>(
                    pageProps.ServerSerializedPreloadedState, JsonOpts);
                if (preloaded?.UserData?.Users is { } preloadedUsers) usersMap = preloadedUsers;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SearchUsersAsync {Url}: failed to parse serverSerializedPreloadedState", url);
            }
        }

        usersMap ??= new Dictionary<string, PixivUserSearchUserInfo>();

        var entries = new List<UserSearchEntry>();
        foreach (var id in userIds)
        {
            var key = id.ToString();
            var recentWorks = workIds.TryGetValue(key, out var works) ? works : [];
            if (usersMap.TryGetValue(key, out var info) && !string.IsNullOrWhiteSpace(info.Name))
            {
                entries.Add(new UserSearchEntry
                {
                    UserId = string.IsNullOrEmpty(info.Id) ? key : info.Id,
                    Name = info.Name,
                    ImageUrl = info.BestAvatarUrl,
                    Comment = info.Comment,
                    RecentWorkIds = recentWorks,
                });
            }
            else
            {
                entries.Add(new UserSearchEntry { UserId = key, Name = $"User {key}", RecentWorkIds = recentWorks });
            }
        }

        return new UserSearchResult { Users = entries, Total = entries.Count };
    }

    [GeneratedRegex("<script id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();

    /// <summary>
    /// GET /collections/{id} — a Pixiv "Collection". There is no ajax/JSON endpoint for reading
    /// one at all; the collection's metadata, its artwork tiles, full thumbnail data for every
    /// work in it, AND the list of the same creator's other collection IDs are all embedded in
    /// the page's own <c>__NEXT_DATA__</c> JSON (confirmed from a captured live page load), so
    /// this scrapes that the same way <see cref="SearchUsersAsync"/> and CSRF token extraction
    /// already do.
    /// </summary>
    /// <summary>
    /// GET /ajax/collection/{collectionId} — a lightweight JSON endpoint (confirmed from a
    /// captured live request) that, unlike the <c>embed.pixiv.net</c> collage-thumbnail
    /// generator (which reliably 400s for reasons still unconfirmed — an app-level rejection,
    /// not a Cloudflare block), returns real per-work thumbnails on the always-working
    /// <c>i.pximg.net</c> CDN under <c>body.thumbnails.illust[]</c>. Used to build a 2x2
    /// collage preview for browse tiles (an assortment of the collection's works, similar to
    /// Pixiv's own collage look) instead of a single image. Also carries the collage's actual
    /// tile layout under <c>body.data.detail.tiles[]</c> (position/size per work) for a future
    /// true-mosaic rendering, though only the thumbnail list is used today.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCollectionThumbnailsAsync(
        string collectionId, int maxCount = 4, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/collection/{collectionId}?lang={_settings.Current.Locale}";
        var json = await GetAjaxRawAsync(url, $"{BaseUrl}/collections/{collectionId}", ct).ConfigureAwait(false);
        if (json is null) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("body", out var body)) return [];
            if (!body.TryGetProperty("thumbnails", out var thumbs) || thumbs.ValueKind != JsonValueKind.Object) return [];
            if (!thumbs.TryGetProperty("illust", out var illusts) || illusts.ValueKind != JsonValueKind.Array)
                return [];

            var urls = new List<string>();
            foreach (var item in illusts.EnumerateArray())
            {
                if (urls.Count >= maxCount) break;
                if (item.TryGetProperty("url", out var urlEl) && urlEl.GetString() is { } u)
                    urls.Add(u);
            }
            return urls;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetCollectionThumbnails {Id}: parse failed", collectionId);
            return [];
        }
    }

    public async Task<PixivCollection?> GetCollectionAsync(string collectionId, CancellationToken ct = default)
    {
        // No locale prefix — confirmed from a captured live request. Unlike /en/artworks/{id},
        // /en/collections/{id} isn't a valid route (it 404s), only the bare path is.
        var url = $"{BaseUrl}/collections/{collectionId}";
        var client = _httpFactory.GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("GetCollection {Id} -> {Code}", collectionId, resp.StatusCode);
            await WriteDiagAsync(url, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}", ct);
            return null;
        }

        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var m = NextDataRegex().Match(html);
        if (!m.Success)
        {
            _logger.LogWarning("GetCollection {Id}: __NEXT_DATA__ not found in response", collectionId);
            var dumpPath = Path.Combine(Path.GetTempPath(), $"pikura_collection_dump_{collectionId}.html");
            try { await File.WriteAllTextAsync(dumpPath, html, ct).ConfigureAwait(false); } catch { /* best-effort */ }
            await WriteDiagAsync(url, $"__NEXT_DATA__ not found (htmlLen={html.Length}). Full HTML at {dumpPath}", ct);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            var pageProps = doc.RootElement.GetProperty("props").GetProperty("pageProps");
            if (!pageProps.TryGetProperty("collection", out var c) || c.ValueKind != JsonValueKind.Object)
                return null;

            var result = new PixivCollection
            {
                Id = c.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? collectionId : collectionId,
                Title = c.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "",
                UserId = c.TryGetProperty("userId", out var uidEl) ? uidEl.GetString() ?? "" : "",
                UserName = c.TryGetProperty("userName", out var unameEl) ? unameEl.GetString() ?? "" : "",
                UserProfileImageUrl = c.TryGetProperty("profileImageUrl", out var puEl) ? puEl.GetString() : null,
                Caption = c.TryGetProperty("caption", out var capEl) ? capEl.GetString() : null,
                BookmarkCount = c.TryGetProperty("bookmarkCount", out var bcEl) && bcEl.ValueKind == JsonValueKind.Number ? bcEl.GetInt32() : 0,
                ViewCount = c.TryGetProperty("viewCount", out var vcEl) && vcEl.ValueKind == JsonValueKind.Number ? vcEl.GetInt32() : 0,
                IsBookmarked = c.TryGetProperty("bookmarkData", out var bdEl) && bdEl.ValueKind == JsonValueKind.Object,
            };

            if (c.TryGetProperty("tags", out var tagsEl))
            {
                var tagList = new List<string>();
                if (tagsEl.ValueKind == JsonValueKind.Object && tagsEl.TryGetProperty("tags", out var tagArrEl) && tagArrEl.ValueKind == JsonValueKind.Array)
                    foreach (var t in tagArrEl.EnumerateArray())
                        if (t.TryGetProperty("tag", out var tEl) && tEl.GetString() is { } tagStr) tagList.Add(tagStr);
                result.Tags = tagList;
            }

            var siblingIds = new List<string>();
            if (pageProps.TryGetProperty("userCollectionIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                foreach (var idEl2 in idsEl.EnumerateArray())
                    if (idEl2.GetString() is { } sid) siblingIds.Add(sid);
            result.SiblingCollectionIds = siblingIds;

            // Full ArtworkPreview data for every work, AND full metadata (title/thumbnail/counts)
            // for every sibling collection, are both embedded separately (as a JSON string that
            // needs its own parse pass) rather than inline — this gives sibling collections
            // proper collage tiles for free instead of bare IDs.
            var thumbMap = new Dictionary<string, ArtworkPreview>();
            if (pageProps.TryGetProperty("serverSerializedPreloadedState", out var preEl) && preEl.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var preDoc = JsonDocument.Parse(preEl.GetString() ?? "{}");
                    if (preDoc.RootElement.TryGetProperty("thumbnail", out var thumbEl) &&
                        thumbEl.TryGetProperty("illust", out var illustMapEl) &&
                        illustMapEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in illustMapEl.EnumerateObject())
                        {
                            var preview = System.Text.Json.JsonSerializer.Deserialize<ArtworkPreview>(prop.Value.GetRawText(), JsonOpts);
                            if (preview != null) thumbMap[prop.Name] = preview;
                        }
                    }

                    if (preDoc.RootElement.TryGetProperty("work", out var workEl) &&
                        workEl.TryGetProperty("collection", out var collMapEl) &&
                        collMapEl.ValueKind == JsonValueKind.Object)
                    {
                        var siblings = new List<PixivCollectionSummary>();
                        foreach (var prop in collMapEl.EnumerateObject())
                        {
                            var cv = prop.Value;
                            siblings.Add(new PixivCollectionSummary
                            {
                                Id = cv.TryGetProperty("id", out var sIdEl) ? sIdEl.GetString() ?? prop.Name : prop.Name,
                                Title = cv.TryGetProperty("title", out var sTitleEl) ? sTitleEl.GetString() ?? "" : "",
                                UserId = cv.TryGetProperty("userId", out var sUidEl) ? sUidEl.GetString() ?? "" : "",
                                UserName = cv.TryGetProperty("userName", out var sUnameEl) ? sUnameEl.GetString() ?? "" : "",
                                ThumbnailImageUrl = cv.TryGetProperty("thumbnailImageUrl", out var sThumbEl) ? sThumbEl.GetString() : null,
                                BookmarkCount = cv.TryGetProperty("bookmarkCount", out var sBcEl) && sBcEl.ValueKind == JsonValueKind.Number ? sBcEl.GetInt32() : 0,
                                ViewCount = cv.TryGetProperty("viewCount", out var sVcEl) && sVcEl.ValueKind == JsonValueKind.Number ? sVcEl.GetInt32() : 0,
                            });
                        }
                        result.SiblingCollections = siblings;
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "GetCollection {Id}: preloaded-state parse failed", collectionId); }
            }

            var works = new List<ArtworkPreview>();
            if (c.TryGetProperty("tiles", out var tilesEl) && tilesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tile in tilesEl.EnumerateArray())
                {
                    if (!tile.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "Work") continue;
                    if (!tile.TryGetProperty("workId", out var workIdEl)) continue;
                    var workId = workIdEl.GetString();
                    if (workId != null && thumbMap.TryGetValue(workId, out var preview))
                        works.Add(preview);
                }
            }
            result.Works = works;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetCollection {Id} parse failed", collectionId);
            return null;
        }
    }

    /// <summary>
    /// GET /ajax/top/collection — the Collections landing page's actual data source (confirmed
    /// from Pixiv's own JS bundle: <c>e.get("/ajax/top/collection", {})</c>, module 90351). Its
    /// error-fallback shape — <c>body.page.{everyoneCollectionIds,myCollectionIds,
    /// recommendCollectionIds,tagRecommendCollectionIds}</c> — is confirmed from the bundle; the
    /// success shape is assumed to mirror it (populated arrays instead of empty ones), since
    /// Pixiv's ajax endpoints consistently return the same shape whether or not there's an
    /// error. Each ID list is then hydrated into full <see cref="PixivCollectionSummary"/> tiles
    /// via <see cref="ResolveCollectionSummariesAsync"/>. "Featured" surfaces the recommendation
    /// lists; "All" surfaces the "everyone" feed. If the endpoint's shape doesn't match, this
    /// dumps the raw JSON to a temp file for diagnosis and returns empty lists rather than
    /// throwing.
    /// </summary>
    public async Task<(IReadOnlyList<PixivCollectionSummary> Featured, IReadOnlyList<PixivCollectionSummary> All)>
        GetFeaturedCollectionsAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/top/collection";
        var json = await GetAjaxRawAsync(url, BaseUrl + "/collection", ct).ConfigureAwait(false);
        if (json is null)
        {
            _logger.LogWarning("GetFeaturedCollections: /ajax/top/collection request failed");
            return ([], []);
        }

        List<string> everyoneIds, myIds, recommendIds, tagRecommendIds;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            {
                await DumpTopCollectionJsonAsync(json, ct);
                _logger.LogWarning("GetFeaturedCollections: /ajax/top/collection response missing body");
                return ([], []);
            }
            // The bundle's fallback nests the lists under body.page; be tolerant of them living
            // directly on body too, in case the real success shape differs from the fallback.
            var page = body.TryGetProperty("page", out var pageEl) && pageEl.ValueKind == JsonValueKind.Object
                ? pageEl : body;

            everyoneIds = ReadStringArray(page, "everyoneCollectionIds");
            myIds = ReadStringArray(page, "myCollectionIds");
            recommendIds = ReadStringArray(page, "recommendCollectionIds");
            tagRecommendIds = ReadStringArray(page, "tagRecommendCollectionIds");

            if (everyoneIds.Count == 0 && myIds.Count == 0 && recommendIds.Count == 0 && tagRecommendIds.Count == 0)
            {
                await DumpTopCollectionJsonAsync(json, ct);
                _logger.LogWarning("GetFeaturedCollections: no collection IDs found in /ajax/top/collection response");
            }
        }
        catch (Exception ex)
        {
            await DumpTopCollectionJsonAsync(json, ct);
            _logger.LogWarning(ex, "GetFeaturedCollections: /ajax/top/collection parse failed");
            return ([], []);
        }

        var featuredIds = recommendIds.Count > 0 ? recommendIds : tagRecommendIds;
        var featured = await ResolveCollectionSummariesAsync(featuredIds, ct).ConfigureAwait(false);
        var all = await ResolveCollectionSummariesAsync(everyoneIds, ct).ConfigureAwait(false);
        return (featured, all);
    }

    /// <summary>
    /// GET /ajax/collections/search — the real paginated "All collections" listing (confirmed
    /// from a captured live request: <c>?mode=safe&amp;limit=20&amp;offset=20&amp;lang=en</c>).
    /// Unlike <see cref="GetFeaturedCollectionsAsync"/>'s <c>everyoneCollectionIds</c> (a fixed
    /// ~10-item sample), this genuinely paginates — the same capture reported
    /// <c>body.data.total</c> in the thousands. Response shape: <c>body.data.ids[]</c> (ordering)
    /// + <c>body.thumbnails.collection[]</c> (full summary objects, same shape as
    /// <see cref="ResolveCollectionSummariesAsync"/> already parses).
    /// </summary>
    /// <param name="mode">"safe" (all-ages only) or "all" (safe + R-18) — confirmed values from
    /// the capture; mirrors the site's own "All ages ▾" filter dropdown.</param>
    public async Task<(IReadOnlyList<PixivCollectionSummary> Items, int Total)> SearchCollectionsAsync(
        string mode = "safe", int limit = 40, int offset = 0, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/collections/search?mode={mode}&limit={limit}&offset={offset}&lang={_settings.Current.Locale}";
        var json = await GetAjaxRawAsync(url, $"{BaseUrl}/collections", ct).ConfigureAwait(false);
        if (json is null) return ([], 0);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("body", out var body)) return ([], 0);

            var total = body.TryGetProperty("data", out var dataEl) && dataEl.TryGetProperty("total", out var totalEl)
                && totalEl.ValueKind == JsonValueKind.Number ? totalEl.GetInt32() : 0;

            var orderedIds = new List<string>();
            if (dataEl.ValueKind == JsonValueKind.Object && dataEl.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                foreach (var idEl in idsEl.EnumerateArray())
                    if (idEl.GetString() is { } s) orderedIds.Add(s);

            var byId = new Dictionary<string, PixivCollectionSummary>();
            if (body.TryGetProperty("thumbnails", out var thumbs) && thumbs.TryGetProperty("collection", out var collArr)
                && collArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in collArr.EnumerateArray())
                    if (ParseCollectionSummary(item) is { } sum) byId[sum.Id] = sum;
            }

            // Preserve the server's own ordering; fall back to whatever we parsed if the id
            // list didn't line up (shouldn't happen, but don't drop data over it).
            var items = orderedIds.Count > 0
                ? orderedIds.Select(id => byId.TryGetValue(id, out var s) ? s : null).OfType<PixivCollectionSummary>().ToList()
                : byId.Values.ToList();

            if (items.Count == 0 && total == 0)
            {
                var dumpPath = Path.Combine(Path.GetTempPath(), "pikura_collections_search_dump.json");
                try { await File.WriteAllTextAsync(dumpPath, json, ct).ConfigureAwait(false); } catch { }
                _logger.LogWarning("SearchCollections: no items parsed. Raw JSON at {Path}", dumpPath);
            }

            return (items, total);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SearchCollections: parse failed");
            return ([], 0);
        }
    }

    private static Task DumpTopCollectionJsonAsync(string json, CancellationToken ct)
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), "pikura_top_collection_dump.json");
        return File.WriteAllTextAsync(dumpPath, json, ct);
    }

    private static List<string> ReadStringArray(JsonElement obj, string propertyName)
    {
        var result = new List<string>();
        if (!obj.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var el in arr.EnumerateArray())
        {
            var s = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrEmpty(s)) result.Add(s);
        }
        return result;
    }

    /// <summary>
    /// GET /ajax/collection/recommend/collections?ids=... — hydrates a list of bare collection
    /// IDs (as returned by <see cref="GetFeaturedCollectionsAsync"/>) into full tile metadata.
    /// Confirmed to exist from the JS bundle (module ~82930s) but its response shape is
    /// unconfirmed, so this tries several plausible envelope shapes (a bare array, a wrapper
    /// object with a "collections"/"collection"/"list" array, or an id-keyed object) before
    /// giving up and dumping the raw JSON for diagnosis.
    /// </summary>
    private async Task<IReadOnlyList<PixivCollectionSummary>> ResolveCollectionSummariesAsync(
        IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        // Pixiv's frontend serializes array params with axios/qs using arrayFormat "brackets"
        // (confirmed from the JS bundle), i.e. ids[]=1&ids[]=2 rather than ids=1&ids=2 — the
        // latter was observed to fail with HTTP 400 "Invalid request."
        var query = string.Join("&", ids.Select(id => $"ids%5B%5D={Uri.EscapeDataString(id)}"));
        var url = $"{BaseUrl}/ajax/collection/recommend/collections?{query}";
        var json = await GetAjaxRawAsync(url, BaseUrl + "/collection", ct).ConfigureAwait(false);
        if (json is null)
        {
            _logger.LogWarning("ResolveCollectionSummaries: request failed for {Count} ids", ids.Count);
            return [];
        }

        // TEMPORARY: always dump the raw response while the exact thumbnail/field names are
        // still being confirmed against a live capture (tiles are populating with titles, but
        // thumbnails are coming back blank) — remove once ParseCollectionSummary's property
        // guesses are confirmed correct.
        await DumpRecommendCollectionsJsonAsync(json, ct);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("body", out var body))
            {
                await DumpRecommendCollectionsJsonAsync(json, ct);
                return [];
            }

            var result = new List<PixivCollectionSummary>();
            if (body.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in body.EnumerateArray())
                    if (ParseCollectionSummary(item) is { } s) result.Add(s);
            }
            else if (body.ValueKind == JsonValueKind.Object)
            {
                if ((body.TryGetProperty("collections", out var listEl)
                     || body.TryGetProperty("collection", out listEl)
                     || body.TryGetProperty("list", out listEl))
                    && listEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in listEl.EnumerateArray())
                        if (ParseCollectionSummary(item) is { } s) result.Add(s);
                }
                else
                {
                    // Maybe body is itself keyed by collection ID -> collection object.
                    foreach (var prop in body.EnumerateObject())
                        if (ParseCollectionSummary(prop.Value, prop.Name) is { } s) result.Add(s);
                }
            }

            if (result.Count == 0)
            {
                await DumpRecommendCollectionsJsonAsync(json, ct);
                _logger.LogWarning("ResolveCollectionSummaries: no summaries parsed for {Count} ids", ids.Count);
            }

            return result;
        }
        catch (Exception ex)
        {
            await DumpRecommendCollectionsJsonAsync(json, ct);
            _logger.LogWarning(ex, "ResolveCollectionSummaries: parse failed for {Count} ids", ids.Count);
            return [];
        }
    }

    private static Task DumpRecommendCollectionsJsonAsync(string json, CancellationToken ct)
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), "pikura_collection_recommend_dump.json");
        return File.WriteAllTextAsync(dumpPath, json, ct);
    }

    private static PixivCollectionSummary? ParseCollectionSummary(JsonElement item, string? fallbackId = null)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string? id = item.TryGetProperty("id", out var idEl)
            ? (idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText())
            : fallbackId;
        if (string.IsNullOrEmpty(id)) return null;

        return new PixivCollectionSummary
        {
            Id = id,
            Title = item.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "",
            UserId = item.TryGetProperty("userId", out var uEl)
                ? (uEl.ValueKind == JsonValueKind.String ? uEl.GetString() ?? "" : uEl.GetRawText()) : "",
            UserName = item.TryGetProperty("userName", out var unEl) ? unEl.GetString() ?? "" : "",
            ThumbnailImageUrl = item.TryGetProperty("thumbnailImageUrl", out var thEl) ? thEl.GetString()
                : item.TryGetProperty("thumbnail", out var th2El) ? th2El.GetString()
                : item.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null,
            BookmarkCount = item.TryGetProperty("bookmarkCount", out var bEl) && bEl.ValueKind == JsonValueKind.Number ? bEl.GetInt32() : 0,
            ViewCount = item.TryGetProperty("viewCount", out var vEl) && vEl.ValueKind == JsonValueKind.Number ? vEl.GetInt32() : 0,
            IsBookmarked = item.TryGetProperty("bookmarkData", out var bdEl) && bdEl.ValueKind == JsonValueKind.Object,
            BookmarkId = item.TryGetProperty("bookmarkData", out var bd2El) && bd2El.ValueKind == JsonValueKind.Object
                && bd2El.TryGetProperty("id", out var bdIdEl)
                ? (bdIdEl.ValueKind == JsonValueKind.String ? bdIdEl.GetString() : bdIdEl.GetRawText())
                : null,
            XRestrict = item.TryGetProperty("xRestrict", out var xrEl) && xrEl.ValueKind == JsonValueKind.Number ? xrEl.GetInt32() : 0,
        };
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
    /// GET /ajax/search/novels/{keyword} — search novels by keyword. Mirrors
    /// <see cref="SearchArtworksAsync"/>'s parameters (mode/s_mode/order) — omitting mode
    /// and s_mode (as the previous implementation did) causes Pixiv to return an empty result set.
    /// </summary>
    public async Task<NovelSearchResult?> SearchNovelsAsync(
        string keyword,
        string order = "date_d",
        string mode = "safe",       // safe | r18 | all
        string sMode = "s_tag",     // s_tag | s_tag_full | s_tc
        int page = 1,
        CancellationToken ct = default)
    {
        keyword = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(keyword)) return null;
        var url = $"{BaseUrl}/ajax/search/novels/{Uri.EscapeDataString(keyword)}" +
                  $"?word={Uri.EscapeDataString(keyword)}&order={Uri.EscapeDataString(order)}" +
                  $"&mode={Uri.EscapeDataString(mode)}&s_mode={Uri.EscapeDataString(sMode)}" +
                  $"&p={page}&lang={_settings.Current.Locale}";
        var raw = await GetAjaxRawAsync(url, referer: null, ct).ConfigureAwait(false);
        try { await File.WriteAllTextAsync(System.IO.Path.Combine(Path.GetTempPath(), "pikura_novelsearch_dump.json"), raw ?? "(null)", ct); } catch { }
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
    /// <summary>Same 429/503 backoff-and-retry behavior as <see cref="GetAjaxAsync{T}"/> (SafeMode),
    /// which this previously lacked — every caller of this raw-JSON variant (Collections search,
    /// featured/recommend resolution, comments, etc.) was treating a bare "HTTP 429
    /// TooManyRequests" error body as if it were the actual JSON payload, immediately failing to
    /// parse and silently returning empty results instead of backing off and retrying like the
    /// rest of the app does.</summary>
    private async Task<string?> GetAjaxRawAsync(string url, string? referer, CancellationToken ct)
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

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
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

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return $"HTTP {(int)resp.StatusCode} {resp.StatusCode}\n{body}";
            return body;
        }
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
    /// POST helper for Pixiv's web /ajax endpoints. Requires the PHPSESSID cookie and a valid
    /// CSRF token. Returns the parsed body on success, or <c>default</c> if the request fails.
    /// Does not retry on non-rate-limit failures to avoid duplicate write side-effects.
    /// </summary>
    private async Task<bool> PostAjaxSuccessAsync<TRequest>(
        string url, TRequest payload, string? referer, string? illustId, CancellationToken ct)
    {
        var csrf = await GetCsrfTokenAsync(illustId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(csrf)) return false;

        var client = _httpFactory.GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Referer", referer ?? BaseUrl + "/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Origin", BaseUrl);
        req.Headers.TryAddWithoutValidation("x-csrf-token", csrf);
        if (!string.IsNullOrWhiteSpace(_settings.Current.UserId))
            req.Headers.TryAddWithoutValidation("x-user-id", _settings.Current.UserId);

        var json = System.Text.Json.JsonSerializer.Serialize(payload, JsonOpts);
        req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            await WriteDiagAsync(url, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}\nBody={body?[..Math.Min(500, body.Length)]}", ct);
            return false;
        }

        PixivAjaxResponse<object>? envelope;
        try { envelope = System.Text.Json.JsonSerializer.Deserialize<PixivAjaxResponse<object>>(body, JsonOpts); }
        catch { return false; }

        if (envelope is null || envelope.Error)
        {
            await WriteDiagAsync(url, $"error=true msg={envelope?.Message}\nBody={body?[..Math.Min(500, body.Length)]}", ct);

            // Pixiv sometimes returns an error for "already liked / bookmarked" —
            // treat those as success since the desired end-state already exists.
            var msg = envelope?.Message ?? "";
            if (msg.Contains("already", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("exist", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("before", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("\u65e2\u306b", StringComparison.OrdinalIgnoreCase)) // 既に
            {
                return true;
            }
            return false;
        }
        return true;
    }

    private async Task<TResponse?> PostAjaxAsync<TRequest, TResponse>(
        string url, TRequest payload, string? referer, string? illustId, CancellationToken ct)
    {
        var csrf = await GetCsrfTokenAsync(illustId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(csrf))
        {
            _logger.LogWarning("PostAjax {Url}: no CSRF token", url);
            return default;
        }

        var client = _httpFactory.GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Referer", referer ?? BaseUrl + "/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Origin", BaseUrl);
        req.Headers.TryAddWithoutValidation("x-csrf-token", csrf);
        if (!string.IsNullOrWhiteSpace(_settings.Current.UserId))
            req.Headers.TryAddWithoutValidation("x-user-id", _settings.Current.UserId);

        var json = System.Text.Json.JsonSerializer.Serialize(payload, JsonOpts);
        req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("PostAjax {Url} -> {Code}: {Body}", url, resp.StatusCode, body?[..Math.Min(500, body.Length)] ?? "(null)");
            await WriteDiagAsync(url, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}\nBody={body?[..Math.Min(500, body.Length)]}", ct);
            return default;
        }

        PixivAjaxResponse<TResponse>? envelope;
        try { envelope = System.Text.Json.JsonSerializer.Deserialize<PixivAjaxResponse<TResponse>>(body, JsonOpts); }
        catch (Exception ex) { _logger.LogWarning(ex, "PostAjax {Url} JSON parse failed", url); return default; }
        if (envelope is null || envelope.Error)
        {
            _logger.LogWarning("PostAjax {Url} error: {Msg}", url, envelope?.Message);
            await WriteDiagAsync(url, $"error=true msg={envelope?.Message}\nBody={body?[..Math.Min(500, body.Length)]}", ct);
            return default;
        }
        return envelope.Body;
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

    // ─── Web AJAX write actions (PHPSESSID + CSRF token) ────────────────────

    /// <summary>
    /// GET /ajax/illusts/comments/roots — top-level comments on an artwork. Documented publicly
    /// (unlike posting a comment) — see PixivComment for field-shape sourcing.
    /// </summary>
    public async Task<PixivCommentsRootsResponse?> GetCommentsAsync(
        string illustId, int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/illusts/comments/roots?illust_id={illustId}&offset={offset}&limit={limit}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<PixivCommentsRootsResponse>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /ajax/collections/comments/roots — top-level comments on a Collection *itself*
    /// (distinct from any individual artwork's own comments — Pixiv Collections have their own
    /// comment thread, shown below the collage). Confirmed from a captured live request:
    /// <c>?collection_id={id}&amp;offset=0&amp;limit=3&amp;lang=en</c>. Reuses
    /// <see cref="PixivCommentsRootsResponse"/> since Pixiv's comment UI/data shape is expected
    /// to be shared across illust and collection comments; if that assumption is wrong for some
    /// field, <see cref="GetAjaxAsync{T}"/>'s existing diagnostic dump-on-parse-failure will
    /// surface it.
    /// </summary>
    public async Task<PixivCommentsRootsResponse?> GetCollectionCommentsAsync(
        string collectionId, int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/collections/comments/roots?collection_id={collectionId}&offset={offset}&limit={limit}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<PixivCommentsRootsResponse>(url, ct, referer: $"{BaseUrl}/collections/{collectionId}").ConfigureAwait(false);
    }

    /// <summary>
    /// POST /ajax/comments/collection/post — post a text comment on a Collection. Confirmed
    /// from a captured live request: JSON body <c>{"workId":"{collectionId}","comment":"text"}</c>
    /// (no <c>isStamp</c> field for plain text).
    /// </summary>
    public async Task<bool> PostCollectionCommentAsync(string collectionId, string commentText, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/comments/collection/post";
        var referer = $"{BaseUrl}/collections/{collectionId}";
        var payload = new { workId = collectionId, comment = commentText };
        var body = await PostAjaxAsync<object, object>(url, payload, referer, null, ct).ConfigureAwait(false);
        return body != null;
    }

    /// <summary>
    /// POST /ajax/comments/collection/post — post a sticker on a Collection. Confirmed from a
    /// captured live request: JSON body <c>{"workId":"{collectionId}","isStamp":1,"comment":306}</c>
    /// — note <c>comment</c> is the numeric stamp ID here, not text.
    /// </summary>
    public async Task<bool> PostCollectionStickerAsync(string collectionId, int stampId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/comments/collection/post";
        var referer = $"{BaseUrl}/collections/{collectionId}";
        var payload = new { workId = collectionId, isStamp = 1, comment = stampId };
        var body = await PostAjaxAsync<object, object>(url, payload, referer, null, ct).ConfigureAwait(false);
        return body != null;
    }

    /// <summary>
    /// POST /ajax/comments/collection/delete — delete a comment you posted on a Collection.
    /// Confirmed from a captured live request: JSON body
    /// <c>{"commentId":"...","workId":"{collectionId}"}</c>.
    /// </summary>
    public async Task<bool> DeleteCollectionCommentAsync(string collectionId, string commentId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/comments/collection/delete";
        var referer = $"{BaseUrl}/collections/{collectionId}";
        var payload = new { commentId, workId = collectionId };
        var body = await PostAjaxAsync<object, object>(url, payload, referer, null, ct).ConfigureAwait(false);
        return body != null;
    }

    /// <summary>GET /ajax/illusts/comments/replies — replies to a specific top-level comment.</summary>
    public async Task<PixivCommentsRepliesResponse?> GetCommentRepliesAsync(
        string commentId, int page = 1, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/illusts/comments/replies?comment_id={commentId}&page={page}&lang={_settings.Current.Locale}";
        return await GetAjaxAsync<PixivCommentsRepliesResponse>(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// POST /rpc/post_comment.php — post a comment/emoji (type=comment) or a sticker
    /// (type=stamp), or a reply (if <paramref name="parentCommentId"/> is supplied), on an
    /// artwork using the web session cookie and CSRF token.
    /// <para>
    /// This is a legacy PHP-RPC endpoint (not under <c>/ajax/</c> like every other write action
    /// in this client) using <c>application/x-www-form-urlencoded</c> instead of JSON — confirmed
    /// from captured live browser requests rather than guessed. A sticker post is a genuinely
    /// different shape, not just an extra field on a text comment: <c>type=stamp</c> with
    /// <c>stamp_id</c> and NO <c>comment</c> field at all, vs. <c>type=comment</c> with
    /// <c>comment</c> and no <c>stamp_id</c>. Emoji (from Pixiv's "Emoji" tab, which uses the
    /// open-source emoji-mart picker) are just plain Unicode emoji characters inserted into the
    /// comment text — no special handling needed, they go through the ordinary <c>comment</c> field.
    /// </para>
    /// </summary>
    public async Task<AddCommentResponse?> PostCommentAsync(
        string illustId,
        string authorUserId,
        string commentText,
        string? stampId = null,
        string? parentCommentId = null,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/rpc/post_comment.php";
        var referer = $"{BaseUrl}/en/artworks/{illustId}";

        var csrf = await GetCsrfTokenAsync(illustId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(csrf))
        {
            _logger.LogWarning("PostComment {Id}: no CSRF token", illustId);
            return null;
        }

        var isStamp = !string.IsNullOrEmpty(stampId);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("type", isStamp ? "stamp" : "comment"),
            new("illust_id", illustId),
            new("author_user_id", authorUserId),
        };
        if (isStamp) fields.Add(new("stamp_id", stampId!));
        else fields.Add(new("comment", commentText));
        if (!string.IsNullOrEmpty(parentCommentId)) fields.Add(new("parent_id", parentCommentId));

        var client = _httpFactory.GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Referer", referer);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Origin", BaseUrl);
        req.Headers.TryAddWithoutValidation("x-csrf-token", csrf);
        req.Content = new FormUrlEncodedContent(fields);

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("PostComment {Id} -> {Code}: {Body}", illustId, resp.StatusCode, body[..Math.Min(500, body.Length)]);
            await WriteDiagAsync(url, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}\nBody={body[..Math.Min(500, body.Length)]}", ct);
            return null;
        }

        PixivAjaxResponse<AddCommentResponse>? envelope;
        try { envelope = System.Text.Json.JsonSerializer.Deserialize<PixivAjaxResponse<AddCommentResponse>>(body, JsonOpts); }
        catch (Exception ex) { _logger.LogWarning(ex, "PostComment {Id} JSON parse failed", illustId); return null; }

        if (envelope is null || envelope.Error)
        {
            _logger.LogWarning("PostComment {Id} error: {Msg}", illustId, envelope?.Message);
            await WriteDiagAsync(url, $"error=true msg={envelope?.Message}\nBody={body[..Math.Min(500, body.Length)]}", ct);
            return null;
        }

        _logger.LogDebug("PostComment {Id} succeeded", illustId);
        return envelope.Body;
    }

    /// <summary>POST /rpc_delete_comment.php — delete a comment you posted. Field names
    /// (<c>i_id</c> = illust ID, <c>del_id</c> = comment ID) confirmed from a captured live
    /// request.</summary>
    public async Task<bool> DeleteCommentAsync(string illustId, string commentId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/rpc_delete_comment.php";
        var referer = $"{BaseUrl}/en/artworks/{illustId}";
        var csrf = await GetCsrfTokenAsync(illustId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(csrf)) return false;

        var fields = new List<KeyValuePair<string, string>>
        {
            new("i_id", illustId),
            new("del_id", commentId),
        };

        var client = _httpFactory.GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Referer", referer);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Origin", BaseUrl);
        req.Headers.TryAddWithoutValidation("x-csrf-token", csrf);
        req.Content = new FormUrlEncodedContent(fields);

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var envelope = System.Text.Json.JsonSerializer.Deserialize<PixivAjaxResponse<object>>(body, JsonOpts);
            return envelope is { Error: false };
        }
        catch { return false; }
    }

    /// <summary>
    /// POST /ajax/illusts/like — like an artwork using the web session cookie and CSRF token.
    /// This is the same endpoint the pixiv.net website uses. Returns true when the server
    /// reports the artwork is now liked.
    /// </summary>
    public async Task<bool> LikeIllustAsync(string illustId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/illusts/like";
        var referer = $"{BaseUrl}/en/artworks/{illustId}";
        var payload = new { illust_id = illustId };

        var ok = await PostAjaxSuccessAsync(url, payload, referer, illustId, ct).ConfigureAwait(false);
        if (!ok) _logger.LogWarning("Like {Id} failed", illustId);
        else _logger.LogDebug("Like {Id} succeeded", illustId);
        return ok;
    }

    /// <summary>
    /// POST /ajax/illusts/bookmarks/add — add an artwork to Pixiv bookmarks using the web
    /// session cookie and CSRF token. This matches the website's own bookmark button and
    /// does not require an App API refresh token. Returns the bookmark id on success.
    /// </summary>
    public async Task<string?> AddWebBookmarkAsync(
        string illustId,
        bool isPrivate = false,
        IEnumerable<string>? tags = null,
        string? comment = null,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/illusts/bookmarks/add";
        var referer = $"{BaseUrl}/en/artworks/{illustId}";
        var payload = new
        {
            illust_id = illustId,
            restrict = isPrivate ? 1 : 0,
            comment = comment ?? string.Empty,
            tags = tags?.ToArray() ?? Array.Empty<string>(),
        };

        var body = await PostAjaxAsync<object, AddBookmarkBody>(url, payload, referer, illustId, ct).ConfigureAwait(false);
        if (body == null)
        {
            _logger.LogWarning("AddWebBookmark {Id} failed", illustId);
            return null;
        }

        var bookmarkId = body.LastBookmarkId ?? illustId;
        _logger.LogDebug("AddWebBookmark {Id} succeeded with bookmark_id={BookmarkId}", illustId, bookmarkId);
        return bookmarkId;
    }

    /// <summary>
    /// POST /ajax/illusts/bookmarks/remove — remove bookmarks by their bookmark ids using
    /// the web session cookie and CSRF token. Returns true on success.
    /// </summary>
    public async Task<bool> RemoveWebBookmarkAsync(
        IEnumerable<string> bookmarkIds,
        CancellationToken ct = default)
    {
        var ids = bookmarkIds?.ToList() ?? [];
        if (ids.Count == 0) return true;

        var url = $"{BaseUrl}/ajax/illusts/bookmarks/remove";
        var payload = new { bookmarkIds = ids };

        var body = await PostAjaxAsync<object, object>(url, payload, BaseUrl + "/", null, ct).ConfigureAwait(false);
        if (body == null)
        {
            _logger.LogWarning("RemoveWebBookmark failed for {Count} ids", ids.Count);
            return false;
        }

        _logger.LogDebug("RemoveWebBookmark succeeded for {Count} ids", ids.Count);
        return true;
    }

    /// <summary>
    /// POST /ajax/collections/bookmarks/add — bookmark a Collection itself (distinct from
    /// bookmarking any individual artwork in it). Confirmed from the Pixiv JS bundle:
    /// <c>e.post("/ajax/collections/bookmarks/add", {}, {collectionId, restrict})</c> where
    /// <c>restrict</c> is 0 (public) or 1 (private) — same JSON-body convention as the illust
    /// bookmark endpoints. Returns true on success.
    /// </summary>
    public async Task<bool> AddCollectionBookmarkAsync(string collectionId, bool isPrivate = false, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/collections/bookmarks/add";
        var referer = $"{BaseUrl}/collections/{collectionId}";
        var payload = new { collectionId, restrict = isPrivate ? 1 : 0 };

        var ok = await PostAjaxSuccessAsync(url, payload, referer, null, ct).ConfigureAwait(false);
        if (!ok) _logger.LogWarning("AddCollectionBookmark {Id} failed", collectionId);
        else _logger.LogDebug("AddCollectionBookmark {Id} succeeded", collectionId);
        return ok;
    }

    /// <summary>
    /// POST /ajax/collections/bookmarks/remove — remove a Collection bookmark. Confirmed from
    /// the bundle: <c>e.post("/ajax/collections/bookmarks/remove", {}, {bookmarkIds:[id]})</c>.
    /// </summary>
    public async Task<bool> RemoveCollectionBookmarkAsync(string bookmarkId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/collections/bookmarks/remove";
        var payload = new { bookmarkIds = new[] { bookmarkId } };

        var body = await PostAjaxAsync<object, object>(url, payload, BaseUrl + "/", null, ct).ConfigureAwait(false);
        if (body == null)
        {
            _logger.LogWarning("RemoveCollectionBookmark {Id} failed", bookmarkId);
            return false;
        }
        _logger.LogDebug("RemoveCollectionBookmark {Id} succeeded", bookmarkId);
        return true;
    }

    /// <summary>
    /// GET /ajax/collection/{collectionId}/bookmarkData — current bookmark status for a
    /// Collection. Confirmed from the bundle:
    /// <c>e.get("/ajax/collection/:collectionId/bookmarkData", {collectionId})</c>.
    /// </summary>
    public async Task<CollectionBookmarkData?> GetCollectionBookmarkDataAsync(string collectionId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/ajax/collection/{collectionId}/bookmarkData?lang={_settings.Current.Locale}";
        return await GetAjaxAsync<CollectionBookmarkData>(url, ct, referer: $"{BaseUrl}/collections/{collectionId}").ConfigureAwait(false);
    }

    /// <summary>
    /// GET /ajax/user/{userId}/collections/bookmarks — the current user's bookmarked
    /// Collections. Confirmed from the bundle:
    /// <c>e.get("/ajax/user/:userId/collections/bookmarks", {userId}, {offset, limit, rest})</c>.
    /// </summary>
    public async Task<IReadOnlyList<PixivCollectionSummary>> GetBookmarkedCollectionsAsync(
        string userId, int offset = 0, int limit = 50, bool hidden = false, CancellationToken ct = default)
    {
        var rest = hidden ? "hide" : "show";
        var url = $"{BaseUrl}/ajax/user/{userId}/collections/bookmarks?offset={offset}&limit={limit}&rest={rest}&lang={_settings.Current.Locale}";
        var json = await GetAjaxRawAsync(url, BaseUrl + "/", ct).ConfigureAwait(false);
        if (json is null) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("body", out var body)) return [];

            // Unconfirmed exact shape — try the most likely candidates (mirrors the recommend
            // endpoint's "collections" array, or could be "works"/a bare array) and fall back to
            // dumping raw JSON for diagnosis if none match.
            JsonElement listEl = default;
            var found = body.TryGetProperty("collections", out listEl) || body.TryGetProperty("works", out listEl);
            if (!found && body.ValueKind == JsonValueKind.Array) listEl = body;

            var result = new List<PixivCollectionSummary>();
            if (listEl.ValueKind == JsonValueKind.Array)
                foreach (var item in listEl.EnumerateArray())
                    if (ParseCollectionSummary(item) is { } s) result.Add(s);

            if (result.Count == 0)
            {
                var dumpPath = Path.Combine(Path.GetTempPath(), "pikura_bookmarked_collections_dump.json");
                try { await File.WriteAllTextAsync(dumpPath, json, ct).ConfigureAwait(false); } catch { }
                _logger.LogWarning("GetBookmarkedCollections: no collections parsed. Raw JSON at {Path}", dumpPath);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetBookmarkedCollections: parse failed");
            return [];
        }
    }

    // ─── User follow actions ────────────────────────────────────────────────

    /// <summary>
    /// Follow a user via the web session (PHPSESSID + CSRF), the same mobile touch API
    /// pixiv.net's own follow button uses. No App API refresh token needed.
    /// </summary>
    public Task<bool> FollowUserAsync(
        string userId,
        bool isPrivate = false,
        CancellationToken ct = default)
        => FollowUserWebAsync(userId, isPrivate, ct);

    /// <summary>Unfollow a user via the web session.</summary>
    public Task<bool> UnfollowUserAsync(
        string userId,
        CancellationToken ct = default)
        => UnfollowUserWebAsync(userId, ct);

    /// <summary>
    /// POST https://www.pixiv.net/touch/ajax_api/ajax_api.php — Pixiv's mobile-web "touch" API.
    /// This is what pixiv's own mobile site uses for follow/unfollow and is confirmed working
    /// (unlike the desktop bookmark_add.php form, which no longer accepts plain GET/tt tokens,
    /// and unlike /ajax/following/user/add, which 404s from this API path). Uses the PHPSESSID
    /// cookie plus a CSRF token, sent as multipart/form-data.
    /// </summary>
    private async Task<bool> FollowUserWebAsync(
        string userId,
        bool isPrivate = false,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["mode"] = "add_bookmark_user",
            ["user_id"] = userId,
            ["restrict"] = isPrivate ? "1" : "0",
        };

        var ok = await PostTouchApiSuccessAsync(form, ct).ConfigureAwait(false);
        if (ok) _logger.LogDebug("Web FollowUser {Id} succeeded", userId);
        else _logger.LogWarning("Web FollowUser {Id} failed", userId);
        return ok;
    }

    /// <summary>
    /// POST https://www.pixiv.net/touch/ajax_api/ajax_api.php — unfollow via the mobile touch API.
    /// </summary>
    private async Task<bool> UnfollowUserWebAsync(
        string userId,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["mode"] = "delete_bookmark_user",
            ["user_id"] = userId,
        };

        var ok = await PostTouchApiSuccessAsync(form, ct).ConfigureAwait(false);
        if (ok) _logger.LogDebug("Web UnfollowUser {Id} succeeded", userId);
        else _logger.LogWarning("Web UnfollowUser {Id} failed", userId);
        return ok;
    }

    /// <summary>
    /// POST helper for Pixiv's mobile touch API (multipart/form-data + PHPSESSID + CSRF token).
    /// </summary>
    private async Task<bool> PostTouchApiSuccessAsync(
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        var url = $"{BaseUrl}/touch/ajax_api/ajax_api.php";
        try
        {
            var csrf = await GetCsrfTokenAsync(null, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(csrf))
            {
                _logger.LogWarning("PostTouchApi {Url}: no CSRF token", url);
                return false;
            }

            var client = _httpFactory.GetClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("x-csrf-token", csrf);

            using var content = new MultipartFormDataContent();
            foreach (var kv in form)
                content.Add(new StringContent(kv.Value), kv.Key);
            req.Content = content;

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                await WriteDiagAsync(url, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}\nBody={body?[..Math.Min(500, body.Length)]}", ct);
                return false;
            }

            PixivAjaxResponse<object>? envelope;
            try { envelope = System.Text.Json.JsonSerializer.Deserialize<PixivAjaxResponse<object>>(body, JsonOpts); }
            catch
            {
                await WriteDiagAsync(url, $"parse failed\nBody={body?[..Math.Min(500, body.Length)]}", ct);
                return false;
            }

            if (envelope is null || envelope.Error)
            {
                await WriteDiagAsync(url, $"error=true msg={envelope?.Message}\nBody={body?[..Math.Min(500, body.Length)]}", ct);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PostTouchApi failed for {Url}", url);
            return false;
        }
    }

    private static Task WriteDiagAsync(string context, string detail, CancellationToken ct = default)
    {
        var path = System.IO.Path.Combine(Path.GetTempPath(), "pikura_api_diag.txt");
        return File.AppendAllTextAsync(path, $"[{DateTime.Now}] {context}\n{detail}\n\n", ct);
    }

    // Cache the CSRF token so we only fetch it once per session (it's stable until re-login).
    private string? _cachedCsrfToken;

    // Cache premium status so we only fetch it once per session.
    private bool? _cachedIsPremium;

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
            // 1) __NEXT_DATA__ -> props.pageProps.serverSerializedPreloadedState -> api.token
            var nextData = ExtractNextDataToken(html);
            if (!string.IsNullOrWhiteSpace(nextData)) return nextData;
            // 2) meta-global-data content blob
            var metaM = MetaGlobalDataRegex().Match(html);
            if (metaM.Success)
            {
                var inner = CsrfTokenRegex().Match(metaM.Groups[1].Value);
                if (inner.Success) return inner.Groups[1].Value;
            }
            // 3) "token":"..." anywhere
            var tokM = CsrfTokenRegex().Match(html);
            if (tokM.Success) return tokM.Groups[1].Value;
            // 4) "tt":"..." anywhere (Next.js pageProps)
            var ttM = TtTokenRegex().Match(html);
            if (ttM.Success) return ttM.Groups[1].Value;
            // Dump the full HTML to disk so we can inspect the actual token format
            var dumpPath = System.IO.Path.Combine(Path.GetTempPath(), $"pikura_page_dump_{Guid.NewGuid():N}.html");
            await File.WriteAllTextAsync(dumpPath, html, ct).ConfigureAwait(false);
            await WriteDiagAsync(url, $"CSRF: no pattern matched (htmlLen={html.Length}). Full HTML at {dumpPath}", ct);
            return null;

            string? ExtractNextDataToken(string page)
            {
                try
                {
                    var nextM = NextDataRegex().Match(page);
                    if (!nextM.Success) return null;
                    var nextJson = nextM.Groups[1].Value;
                    using var nextDoc = JsonDocument.Parse(nextJson);
                    if (!nextDoc.RootElement.TryGetProperty("props", out var props) ||
                        !props.TryGetProperty("pageProps", out var pageProps) ||
                        !pageProps.TryGetProperty("serverSerializedPreloadedState", out var preloaded) ||
                        preloaded.ValueKind != JsonValueKind.String)
                        return null;

                    using var preDoc = JsonDocument.Parse(preloaded.GetString() ?? "{}");
                    if (preDoc.RootElement.TryGetProperty("api", out var api) &&
                        api.TryGetProperty("token", out var token) &&
                        token.ValueKind == JsonValueKind.String)
                    {
                        return token.GetString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "__NEXT_DATA__ CSRF parse failed for {Url}", url);
                }
                return null;
            }
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

    // Pixiv's root page embeds a GA data-layer script containing the account's
    // premium flag, e.g. `var dataLayer = [{ ... premium: 'yes', ... }];`.
    [GeneratedRegex(@"var dataLayer\s*=.*?premium:\s*'(\w+)'", RegexOptions.Singleline)]
    private static partial Regex PremiumDataLayerRegex();

    /// <summary>
    /// Best-effort detection of whether the signed-in account has a Pixiv Premium
    /// subscription. Scrapes the <c>dataLayer</c> marker off the root page HTML —
    /// there is no dedicated cookie-authenticated endpoint for this, and the App
    /// API's <c>is_premium</c> field requires an OAuth refresh token most users
    /// never configure. Returns null if the marker can't be found or the request
    /// fails; result is cached for the lifetime of this client instance.
    /// </summary>
    public async Task<bool?> GetIsPremiumAsync(CancellationToken ct = default)
    {
        if (_cachedIsPremium.HasValue) return _cachedIsPremium;
        try
        {
            var client = _httpFactory.GetClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/");
            req.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var m = PremiumDataLayerRegex().Match(html);
            if (!m.Success) return null;

            _cachedIsPremium = string.Equals(m.Groups[1].Value, "yes", StringComparison.OrdinalIgnoreCase);
            return _cachedIsPremium;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetIsPremiumAsync failed (non-fatal)");
            return null;
        }
    }

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
