using System.Text.Json.Serialization;

namespace Pikura.Core.Models;

/// <summary>
/// Compact artwork record used by listing endpoints (profile/all, following preview).
/// </summary>
public sealed record ArtworkPreview
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("illustType")] public int IllustType { get; init; }
    [JsonPropertyName("aiType")] public int AiType { get; init; }
    [JsonPropertyName("url")] public string? ThumbnailUrl { get; init; }
    [JsonPropertyName("pageCount")] public int PageCount { get; init; } = 1;

    /// <summary>0-based page index used when a card represents a single page of a multi-page work.</summary>
    public int PageIndex { get; init; }

    [JsonPropertyName("userId")] public string UserId { get; init; } = string.Empty;
    [JsonPropertyName("userName")] public string UserName { get; init; } = string.Empty;
    [JsonPropertyName("xRestrict")] public int XRestrict { get; init; }
    [JsonPropertyName("bookmarkCount")] public int? BookmarkCount { get; init; }
    [JsonPropertyName("likeCount")] public int? LikeCount { get; init; }
    [JsonPropertyName("viewCount")] public int? ViewCount { get; init; }
    [JsonPropertyName("createDate")] public DateTimeOffset? CreateDate { get; init; }
    [JsonPropertyName("updateDate")] public DateTimeOffset? UpdateDate { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Native dimensions of the first page (not the thumbnail).
    /// Pixiv returns these on <c>/ajax/user/{id}/profile/illusts</c>, the
    /// follow-latest feed, and the ranking/search feeds. Used to compute the
    /// card's aspect ratio without waiting on the thumbnail bitmap (which
    /// is always a square crop).</summary>
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }

    /// <summary>Height / Width. Falls back to 1.0 for missing dimensions.</summary>
    public double AspectRatio => Width > 0 && Height > 0 ? (double)Height / Width : 1.0;

    /// <summary>True when the artwork is R-18 / R-18G.</summary>
    public bool IsR18 => XRestrict >= 1;

    /// <summary>True when the artwork is specifically R-18G (grotesque/violent).</summary>
    public bool IsR18G => XRestrict == 2;

    /// <summary>True when the artwork is AI-generated (aiType 1 or 2).</summary>
    public bool IsAiGenerated => AiType is 1 or 2;

    /// <summary>Human-readable type: 0=illust, 1=manga, 2=ugoira.</summary>
    public string TypeLabel => IllustType switch
    {
        0 => "Illust",
        1 => "Manga",
        2 => "Ugoira",
        _ => "Other",
    };

    /// <summary>Total page-count badge shown on artwork cards. For a non-paged tile this is empty.</summary>
    public string PageLabel => PageCount > 1 ? PageCount.ToString() : string.Empty;

    /// <summary>Bookmark count as a display string ("N/A" when null - API doesn't provide this in listings).</summary>
    public string BookmarkLabel => BookmarkCount.HasValue ? BookmarkCount.Value.ToString("N0") : "N/A";

    /// <summary>Like count as a display string ("N/A" when null - API doesn't provide this in listings).</summary>
    public string LikeLabel => LikeCount.HasValue ? LikeCount.Value.ToString("N0") : "N/A";

    /// <summary>View count as a display string ("N/A" when null - API doesn't provide this in listings).</summary>
    public string ViewLabel => ViewCount.HasValue ? ViewCount.Value.ToString("N0") : "N/A";

    /// <summary>Formatted creation date for display.</summary>
    public string CreateDateLabel => CreateDate?.ToString("MMMM d, yyyy h:mm tt") ?? "Unknown date";
}

/// <summary>One page of an artwork's image URLs from <c>/ajax/illust/{id}/pages</c>.</summary>
public sealed record ArtworkPage
{
    [JsonPropertyName("urls")] public ArtworkPageUrls Urls { get; init; } = new();
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
}

public sealed record ArtworkPageUrls
{
    [JsonPropertyName("thumb_mini")] public string? ThumbMini { get; init; }
    [JsonPropertyName("small")] public string? Small { get; init; }
    [JsonPropertyName("regular")] public string? Regular { get; init; }
    [JsonPropertyName("original")] public string? Original { get; init; }
}
