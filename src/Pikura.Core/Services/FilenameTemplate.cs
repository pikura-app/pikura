using System.Text.RegularExpressions;
using Pikura.Core.Models;

namespace Pikura.Core.Services;

/// <summary>Context for resolving filename template tokens.</summary>
public sealed record FilenameContext
{
    public ArtworkPreview Artwork { get; init; } = null!;
    public int PageIndex { get; init; }
    public int PageCount { get; init; }
    public string OriginalUrl { get; init; } = string.Empty;
    public string? SearchTags { get; init; }
    public int BookmarkCount { get; init; }
    public int ImageResponseCount { get; init; }
    public int? MangaSeriesOrder { get; init; }
    public string? MangaSeriesId { get; init; }
    public string? MangaSeriesTitle { get; init; }
    public bool IsBookmarkMode { get; init; }
    public string? OriginalMemberId { get; init; }
    public string? OriginalMemberToken { get; init; }
    public string? OriginalArtist { get; init; }
    /// <summary>Top AI-tagged character / series — only set by Library rename.</summary>
    public string? Character { get; init; }
    public string? Series { get; init; }

    /// <summary>Computed: number of digits for page number padding.</summary>
    public int PageDigits => PageCount <= 1 ? 1 : (int)Math.Floor(Math.Log10(PageCount)) + 1;
}

/// <summary>
/// Resolves template strings with token substitution. Tokens are wrapped in % signs:
/// <c>%image_id%</c>, <c>%artist%</c>, <c>%page_index%</c>, etc.
/// </summary>
public sealed class FilenameTemplate
{
    private static readonly Regex TokenRx = new(@"%([a-zA-Z_]+)(?:\{([^}]*)\})?%", RegexOptions.Compiled);

    private readonly string _dateFormat;

    public FilenameTemplate(string dateFormat = "yyyy-MM-dd")
    {
        _dateFormat = dateFormat;
    }

    /// <summary>Resolves all tokens in the template and sanitises the result.</summary>
    public string Resolve(string template, FilenameContext ctx)
    {
        var result = TokenRx.Replace(template, m =>
        {
            var token = m.Groups[1].Value;
            var arg = m.Groups[2].Success ? m.Groups[2].Value : null;
            return ResolveToken(token, arg, ctx);
        });

        // Sanitise: strip illegal path chars except \ and / (separators)
        // and control characters. Preserve Unicode (Japanese, emoji, etc.).
        var invalid = Path.GetInvalidFileNameChars();
        var chars = result.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '\\' || chars[i] == '/') continue; // preserve separators
            if (chars[i] < 0x20 || Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }
        // Windows silently strips trailing dots and spaces from path segments
        // (e.g. "foo..." becomes "foo" when the folder is created). This
        // causes "path not found" errors when subsequent writes use the
        // un-stripped name. Trim each segment defensively. Also cap segment
        // length to 120 chars to avoid MAX_PATH issues on long Pixiv titles.
        const int MaxSegment = 120;
        var segments = new string(chars).Split('\\', '/');
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i].TrimEnd('.', ' ');
            if (seg.Length > MaxSegment) seg = seg[..MaxSegment].TrimEnd('.', ' ');
            segments[i] = seg;
        }
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>
    /// Strips path-separator characters (/ and \) from a token value so that
    /// titles or artist names containing "/" (e.g. "5/15 ストッキングの日") don't
    /// accidentally create phantom subdirectories in the resolved filename.
    /// </summary>
    private static string S(string? value)
        => (value ?? string.Empty).Replace('/', '_').Replace('\\', '_');

    private string ResolveToken(string token, string? arg, FilenameContext ctx)
    {
        return token switch
        {
            // Basic
            "image_id" => ctx.Artwork.Id,
            "member_id" => ctx.Artwork.UserId,
            "artist" => S(ctx.Artwork.UserName),
            "title" => S(ctx.Artwork.Title),
            "tags" => S(string.Join(",", ctx.Artwork.Tags)),
            "urlFilename" => Path.GetFileNameWithoutExtension(ctx.OriginalUrl),

            // Page
            "page_index" => ctx.PageIndex.ToString($"D{ctx.PageDigits}"),
            "page_number" => (ctx.PageIndex + 1).ToString($"D{ctx.PageDigits}"),
            "page_big" => ctx.PageCount > 1 ? "_big" : string.Empty,

            // Date
            "date" => DateTime.Now.ToString("yyyyMMdd"),
            "date_fmt" => arg is not null ? DateTime.Now.ToString(arg) : DateTime.Now.ToString(_dateFormat),
            "works_date" => ctx.Artwork.CreateDate?.ToString(_dateFormat) ?? string.Empty,
            "works_date_only" => ctx.Artwork.CreateDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            "works_date_fmt" => arg is not null
                ? (ctx.Artwork.CreateDate?.ToString(arg) ?? string.Empty)
                : (ctx.Artwork.CreateDate?.ToString(_dateFormat) ?? string.Empty),

            // Metadata
            "works_res" => ctx.PageCount > 1
                ? $"{ctx.PageCount} pages"
                : string.Empty,
            "bookmark_count" => ctx.BookmarkCount.ToString(),
            "image_response_count" => ctx.ImageResponseCount.ToString(),

            // R-18 / AI
            "R-18" => ctx.Artwork.IsR18 ? "R-18" : string.Empty,
            "AI" => string.Empty, // Not available on ArtworkPreview

            // Bookmark mode
            "bookmark" => ctx.IsBookmarkMode ? "Bookmarks" : string.Empty,
            "original_member_id" => ctx.OriginalMemberId ?? string.Empty,
            "original_member_token" => ctx.OriginalMemberToken ?? string.Empty,
            "original_artist" => ctx.OriginalArtist ?? string.Empty,
            "searchTags" => ctx.SearchTags ?? string.Empty,

            // Manga series
            "manga_series_order" => ctx.MangaSeriesOrder?.ToString() ?? string.Empty,
            "manga_series_id" => ctx.MangaSeriesId ?? string.Empty,
            "manga_series_title" => ctx.MangaSeriesTitle ?? string.Empty,

            // AI tags (Library rename)
            "character" => S(ctx.Character),
            "series" => S(ctx.Series),

            // Extension (for info files etc)
            "image_ext" => Path.GetExtension(ctx.OriginalUrl).TrimStart('.'),

            // Unknown token: leave literal
            _ => $"%{token}{(arg is not null ? $"{{{arg}}}" : string.Empty)}%",
        };
    }
}
