using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Core.Services;

/// <summary>Result from a reverse image / tag lookup.</summary>
public class ImageLookupResult
{
    public string? CharacterTags { get; set; }
    public string? CopyrightTags { get; set; }
    public string? GeneralTags { get; set; }
    public string? ArtistTags { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceTitle { get; set; }
    public string? PixivId { get; set; }
    public double Similarity { get; set; }
    public string Provider { get; set; } = string.Empty;
}

/// <summary>
/// Provides character/tag identification via SauceNAO (reverse image search)
/// and Danbooru (tag lookup by Pixiv ID). Both are free with no API key required
/// for basic usage.
/// </summary>
public class ImageLookupService
{
    private readonly ILogger<ImageLookupService> _logger;
    private readonly HttpClient _http;

    private const string SauceNaoUrl   = "https://saucenao.com/search.php";
    private const string DanbooruUrl   = "https://danbooru.donmai.us";
    private const string GelbooruUrl   = "https://gelbooru.com/index.php";

    public ImageLookupService(ILogger<ImageLookupService> logger)
    {
        _logger = logger;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <summary>
    /// Looks up an image by Pixiv artwork ID using Danbooru's tag database.
    /// Returns character, copyright, and general tags without needing image bytes.
    /// </summary>
    public async Task<ImageLookupResult?> LookupByPixivIdAsync(string pixivId, CancellationToken ct = default)
    {
        // Try Danbooru first
        var result = await TryDanbooruAsync(pixivId, ct);
        if (result != null) return result;

        // Fall back to Gelbooru (no Cloudflare, public API)
        return await TryGelbooruAsync(pixivId, ct);
    }

    private async Task<ImageLookupResult?> TryDanbooruAsync(string pixivId, CancellationToken ct)
    {
        try
        {
            var url = $"{DanbooruUrl}/posts.json?tags=pixiv:{pixivId}&limit=1";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            if (json.Contains("Just a moment") || json.Contains("cf_chl")) return null; // Cloudflare block

            var posts = JsonSerializer.Deserialize<List<DanbooruPost>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var post = posts?.FirstOrDefault();
            if (post == null) return null;

            return new ImageLookupResult
            {
                Provider      = "Danbooru",
                PixivId       = pixivId,
                CharacterTags = FormatTagList(post.TagStringCharacter),
                CopyrightTags = FormatTagList(post.TagStringCopyright),
                GeneralTags   = FormatTagList(post.TagStringGeneral, 20),
                ArtistTags    = FormatTagList(post.TagStringArtist),
                SourceUrl     = post.Source,
                Similarity    = 1.0
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Danbooru lookup failed for Pixiv ID {PixivId}", pixivId);
            return null;
        }
    }

    private async Task<ImageLookupResult?> TryGelbooruAsync(string pixivId, CancellationToken ct)
    {
        try
        {
            // Gelbooru public API — search by Pixiv source URL
            var url = $"{GelbooruUrl}?page=dapi&s=post&q=index&json=1&tags=pixiv_id:{pixivId}&limit=1";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);

            JsonElement postEl;
            if (doc.RootElement.TryGetProperty("post", out var postsArr) && postsArr.GetArrayLength() > 0)
                postEl = postsArr[0];
            else
                return null;

            var tags       = postEl.TryGetProperty("tags", out var te) ? te.GetString() : null;
            var sourceUrl  = postEl.TryGetProperty("source", out var se) ? se.GetString() : null;

            if (string.IsNullOrEmpty(tags)) return null;

            return new ImageLookupResult
            {
                Provider    = "Gelbooru",
                PixivId     = pixivId,
                GeneralTags = FormatTagList(tags, 25),
                SourceUrl   = sourceUrl,
                Similarity  = 1.0
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gelbooru lookup failed for Pixiv ID {PixivId}", pixivId);
            return null;
        }
    }

    /// <summary>Why the last <see cref="LookupByImageBytesAsync"/> returned null because of an error
    /// (null when it simply found no confident match).</summary>
    public string? LastError { get; private set; }

    /// <summary>saucenao.com's results page from the last web search (null after an API search),
    /// so callers can show the user the same results in a browser.</summary>
    public string? LastResultsHtml { get; private set; }

    /// <summary>
    /// Performs a reverse image search via SauceNAO using raw image bytes.
    /// Works best with Pixiv-sourced images.
    /// Optionally pass a SauceNAO API key for higher rate limits (4 searches/30s free, 200/day with key).
    /// </summary>
    public async Task<ImageLookupResult?> LookupByImageBytesAsync(
        byte[] imageBytes,
        string? apiKey = null,
        CancellationToken ct = default,
        bool withTags = true)
    {
        // SauceNAO's JSON API refuses anonymous use; its normal web search doesn't, so without a
        // key (or with a key that lacks API access) search the way the website does.
        LastResultsHtml = null;
        var hit = string.IsNullOrEmpty(apiKey) ? null : await LookupViaApiAsync(imageBytes, apiKey, ct);
        if (string.IsNullOrEmpty(apiKey) || (hit == null && LastError?.StartsWith("SauceNAO 403", StringComparison.Ordinal) == true))
            hit = await LookupViaWebSearchAsync(imageBytes, ct);
        _logger.LogInformation("SauceNAO lookup: pixiv={PixivId} similarity={Similarity} error={Error}",
            hit?.PixivId, hit?.Similarity, LastError);
        // Danbooru tags are an extra request (often several seconds); callers that only need the pixiv id skip it.
        return hit != null && withTags ? await EnrichAsync(hit, ct) : hit;
    }

    private async Task<ImageLookupResult?> LookupViaApiAsync(byte[] imageBytes, string apiKey, CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(imageBytes), "file", "image.jpg");
            form.Add(new StringContent("2"),  "output_type"); // JSON
            form.Add(new StringContent("1"),  "numres");      // top 1 result
            if (!string.IsNullOrEmpty(apiKey))
                form.Add(new StringContent(apiKey), "api_key");

            LastError = null;
            var response = await _http.PostAsync(SauceNaoUrl, form, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // SauceNAO explains refusals (no key, quota) in header.message — keep it for the UI.
                string? msg = null;
                try { msg = JsonDocument.Parse(json).RootElement.GetProperty("header").GetProperty("message").GetString(); } catch { }
                LastError = $"SauceNAO {(int)response.StatusCode}: {msg ?? response.ReasonPhrase}";
                return null;
            }

            var doc  = JsonDocument.Parse(json);
            var results = doc.RootElement.GetProperty("results");

            if (results.GetArrayLength() == 0) return null;

            var top    = results[0];
            var header = top.GetProperty("header");
            var data   = top.GetProperty("data");

            var similarity = double.Parse(header.GetProperty("similarity").GetString() ?? "0");
            if (similarity < 70) return null; // Too low confidence

            var pixivId = data.TryGetProperty("pixiv_id", out var pidEl) ? pidEl.ToString() : null;
            var title   = data.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            var member  = data.TryGetProperty("member_name", out var memEl) ? memEl.GetString() : null;
            var extUrl  = data.TryGetProperty("ext_urls", out var urlsEl) && urlsEl.GetArrayLength() > 0
                ? urlsEl[0].GetString() : null;

            return new ImageLookupResult
            {
                Provider    = "SauceNAO",
                Similarity  = similarity,
                PixivId     = pixivId,
                SourceTitle = title,
                ArtistTags  = member,
                SourceUrl   = extUrl
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SauceNAO lookup failed");
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>If we got a Pixiv ID, enrich with Danbooru character tags.</summary>
    private async Task<ImageLookupResult> EnrichAsync(ImageLookupResult result, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(result.PixivId)) return result;
        var danbooru = await LookupByPixivIdAsync(result.PixivId, ct);
        if (danbooru != null)
        {
            result.CharacterTags = danbooru.CharacterTags;
            result.CopyrightTags = danbooru.CopyrightTags;
            result.GeneralTags   = danbooru.GeneralTags;
        }
        return result;
    }

    /// <summary>Same upload as saucenao.com's search form; parses the results page.</summary>
    private async Task<ImageLookupResult?> LookupViaWebSearchAsync(byte[] imageBytes, CancellationToken ct)
    {
        try
        {
            LastError = null;
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(imageBytes), "file", "image.jpg");
            using var req = new HttpRequestMessage(HttpMethod.Post, SauceNaoUrl) { Content = form };
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            using var response = await _http.SendAsync(req, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            // Anonymous searches are rate limited (per 30 s and per day); SauceNAO says so in the page.
            if (!response.IsSuccessStatusCode || html.Contains("Search Rate Too High", StringComparison.OrdinalIgnoreCase)
                || html.Contains("Daily Search Limit Exceeded", StringComparison.OrdinalIgnoreCase))
            {
                LastError = (int)response.StatusCode == 429 || html.Contains("Limit", StringComparison.Ordinal)
                    ? "SauceNAO: search limit reached — try again later"
                    : $"SauceNAO {(int)response.StatusCode}: {response.ReasonPhrase}";
                return null;
            }
            LastResultsHtml = html;
            return ParseWebResults(html);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SauceNAO web search failed");
            LastError = ex.Message;
            return null;
        }
    }

    private static readonly Regex WebResultRx = new(
        """<div class="result"><table class="resulttable">(?<body>.*?)</table></div>""", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WebSimilarityRx = new(
        """class="resultsimilarityinfo">(?<s>[\d.]+)%""", RegexOptions.Compiled);
    private static readonly Regex WebPixivIdRx = new(
        """pixiv\.net/[^"]*?(?:illust_id=|artworks/)(?<id>\d+)""", RegexOptions.Compiled);
    private static readonly Regex WebTitleRx = new(
        """<div class="resulttitle"><strong>(?<t>[^<]*)</strong>""", RegexOptions.Compiled);
    private static readonly Regex WebMemberRx = new(
        """<strong>Member: </strong><a[^>]*>(?<m>[^<]*)</a>""", RegexOptions.Compiled);

    /// <summary>Best result on a saucenao.com results page that links a pixiv artwork, at the same
    /// ≥70% confidence the API path uses. Booru results count when they cite a pixiv source.</summary>
    internal static ImageLookupResult? ParseWebResults(string html)
    {
        ImageLookupResult? best = null;
        foreach (Match m in WebResultRx.Matches(html))
        {
            var body = m.Groups["body"].Value;
            var sim = WebSimilarityRx.Match(body);
            var pid = WebPixivIdRx.Match(body);
            if (!sim.Success || !pid.Success) continue;
            if (!double.TryParse(sim.Groups["s"].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var similarity) || similarity < 70) continue;
            if (best != null && best.Similarity >= similarity) continue;
            var title = WebTitleRx.Match(body) is { Success: true } t ? System.Net.WebUtility.HtmlDecode(t.Groups["t"].Value.Trim()) : null;
            best = new ImageLookupResult
            {
                Provider    = "SauceNAO",
                Similarity  = similarity,
                PixivId     = pid.Groups["id"].Value,
                // Booru results' "title" is "Creator: …", not the artwork title.
                SourceTitle = string.IsNullOrEmpty(title) || title.EndsWith(':') ? null : title,
                ArtistTags  = WebMemberRx.Match(body) is { Success: true } mm ? System.Net.WebUtility.HtmlDecode(mm.Groups["m"].Value) : null,
                SourceUrl   = $"https://www.pixiv.net/artworks/{pid.Groups["id"].Value}",
            };
        }
        return best;
    }

    private static string? FormatTagList(string? raw, int maxTags = 50)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var tags = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                      .Take(maxTags)
                      .Select(t => t.Replace('_', ' '));
        return string.Join(", ", tags);
    }

    // ── Danbooru JSON models ──────────────────────────────────────────────────

    private class DanbooruPost
    {
        [JsonPropertyName("tag_string_character")]
        public string? TagStringCharacter { get; set; }

        [JsonPropertyName("tag_string_copyright")]
        public string? TagStringCopyright { get; set; }

        [JsonPropertyName("tag_string_general")]
        public string? TagStringGeneral { get; set; }

        [JsonPropertyName("tag_string_artist")]
        public string? TagStringArtist { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("rating")]
        public string? Rating { get; set; }
    }
}
