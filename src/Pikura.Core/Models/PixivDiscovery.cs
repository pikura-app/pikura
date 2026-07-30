using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pikura.Core.Models;

/// <summary>Response from /ajax/illust/{id}/recommend/init endpoint.</summary>
public sealed record RecommendIllustsResponse
{
    // Pixiv's `details` map values vary wildly (objects, strings, numbers) and we don't
    // consume them, so keep the whole thing as raw JsonElements to avoid deserialization
    // failures when the shape changes between artworks.
    [JsonPropertyName("details")] public Dictionary<string, JsonElement>? Details { get; init; }
    [JsonPropertyName("illusts")] public List<RecommendIllustEntry>? Illusts { get; init; }
    [JsonPropertyName("nextIds")] public List<string>? NextIds { get; init; }
}

public sealed record RecommendIllustEntry
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("url")] public string? ThumbnailUrl { get; init; }
    [JsonPropertyName("userId")] public string? UserId { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonPropertyName("xRestrict")] public int XRestrict { get; init; }
    [JsonPropertyName("aiType")] public int AiType { get; init; }
    [JsonPropertyName("illustType")] public int IllustType { get; init; }
    [JsonPropertyName("pageCount")] public int PageCount { get; init; } = 1;
    [JsonPropertyName("bookmarkCount")] public int? BookmarkCount { get; init; }
    [JsonPropertyName("likeCount")] public int? LikeCount { get; init; }
    [JsonPropertyName("viewCount")] public int? ViewCount { get; init; }
    [JsonPropertyName("createDate")] public DateTimeOffset? CreateDate { get; init; }
    // Pixiv's /recommend/init endpoint returns tags as plain strings (same shape as
    // /ajax/discovery/artworks' DiscoveryIllust.Tags below) — not {name, translatedName}
    // objects. The previous List<RecommendTag> typing threw a JsonException on every
    // response that had any tags at all.
    [JsonPropertyName("tags")] public List<string>? Tags { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }

    public bool IsR18 => XRestrict >= 1;
    public bool IsR18G => XRestrict == 2;
    public bool IsAiGenerated => AiType is 1 or 2;

    /// <summary>Convert to ArtworkPreview for use in existing UI components.</summary>
    public ArtworkPreview ToArtworkPreview() => new()
    {
        Id = Id ?? "",
        Title = Title ?? "",
        ThumbnailUrl = ThumbnailUrl,
        UserId = UserId ?? "",
        UserName = UserName ?? "",
        XRestrict = XRestrict,
        AiType = AiType,
        IllustType = IllustType,
        PageCount = PageCount,
        BookmarkCount = BookmarkCount,
        LikeCount = LikeCount,
        ViewCount = ViewCount,
        CreateDate = CreateDate,
        Tags = Tags ?? [],
        Width = Width,
        Height = Height
    };
}

/// <summary>Response from /ajax/discovery/artworks endpoint.</summary>
public sealed record DiscoveryArtworksResponse
{
    [JsonPropertyName("thumbnails")] public DiscoveryThumbnails? Thumbnails { get; init; }
    [JsonPropertyName("recommendedIllusts")] public List<RecommendedIllustRef>? RecommendedIllusts { get; init; }
}

public sealed record RecommendedIllustRef
{
    [JsonPropertyName("illustId")] public string? IllustId { get; init; }
}

public sealed record DiscoveryThumbnails
{
    [JsonPropertyName("illust")] public List<DiscoveryIllust>? Illust { get; init; }
}

public sealed record DiscoveryIllust
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("url")] public string? ThumbnailUrl { get; init; }
    [JsonPropertyName("userId")] public string? UserId { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonPropertyName("xRestrict")] public int XRestrict { get; init; }
    [JsonPropertyName("aiType")] public int AiType { get; init; }
    [JsonPropertyName("illustType")] public int IllustType { get; init; }
    [JsonPropertyName("pageCount")] public int PageCount { get; init; } = 1;
    [JsonPropertyName("bookmarkCount")] public int? BookmarkCount { get; init; }
    [JsonPropertyName("likeCount")] public int? LikeCount { get; init; }
    [JsonPropertyName("viewCount")] public int? ViewCount { get; init; }
    [JsonPropertyName("createDate")] public DateTimeOffset? CreateDate { get; init; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }

    public bool IsR18 => XRestrict >= 1;
    public bool IsR18G => XRestrict == 2;
    public bool IsAiGenerated => AiType is 1 or 2;

    /// <summary>Convert to ArtworkPreview for use in existing UI components.</summary>
    public ArtworkPreview ToArtworkPreview() => new()
    {
        Id = Id ?? "",
        Title = Title ?? "",
        ThumbnailUrl = ThumbnailUrl,
        UserId = UserId ?? "",
        UserName = UserName ?? "",
        XRestrict = XRestrict,
        AiType = AiType,
        IllustType = IllustType,
        PageCount = PageCount,
        BookmarkCount = BookmarkCount,
        LikeCount = LikeCount,
        ViewCount = ViewCount,
        CreateDate = CreateDate,
        Tags = Tags ?? [],
        Width = Width,
        Height = Height
    };
}

public sealed record DiscoveryPage
{
    [JsonPropertyName("ids")] public List<string>? Ids { get; init; }
    [JsonPropertyName("isLastPage")] public bool IsLastPage { get; init; }
}

/// <summary>Response from /ajax/discovery/users endpoint.</summary>
public sealed record DiscoveryUsersResponse
{
    [JsonPropertyName("users")] public List<DiscoveryUser>? Users { get; init; }
    [JsonPropertyName("recommendedUsers")] public List<RecommendedUserRef>? RecommendedUsers { get; init; }
}

public sealed record RecommendedUserRef
{
    [JsonPropertyName("userId")] public string? UserId { get; init; }
}

public sealed record DiscoveryUser
{
    [JsonPropertyName("userId")] public string? UserId { get; init; }
    [JsonPropertyName("name")] public string? UserName { get; init; }
    [JsonPropertyName("image")] public string? ProfileImageUrl { get; init; }
    [JsonPropertyName("isFollowed")] public bool IsFollowed { get; init; }
    [JsonPropertyName("premium")] public bool Premium { get; init; }
}
