using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pikura.Core.Models;

/// <summary>Response from /ajax/illust/{id} endpoint with full artwork details and stats.</summary>
public sealed class ArtworkDetailResponse
{
    [JsonPropertyName("error")] public bool Error { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("body")] public ArtworkDetailBody? Body { get; init; }
}

public sealed class ArtworkDetailBody
{
    [JsonPropertyName("illustId")] public string? IllustId { get; init; }
    [JsonPropertyName("illustTitle")] public string? IllustTitle { get; init; }
    [JsonPropertyName("illustComment")] public string? IllustComment { get; init; }
    [JsonPropertyName("userId")] public string? UserId { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }

    // Pixiv's /ajax/illust/{id} endpoint nests thumbnail URLs under "urls": {mini, thumb,
    // small, regular, original} — there is no flat top-level "url" string field (that shape
    // only exists on listing endpoints like /recommend/init or /discovery/artworks). Binding
    // straight to a top-level "url" here silently produced a null thumbnail for every artwork
    // opened via this endpoint (e.g. "Open" from a Hoshi similar-art/artist result, or any
    // other single-artwork lookup), even though the artwork loaded successfully otherwise.
    [JsonPropertyName("urls")] public ArtworkDetailUrls? Urls { get; init; }
    public string? ThumbnailUrl => Urls?.Regular ?? Urls?.Small ?? Urls?.Thumb ?? Urls?.Original;

    // Stats - these are the key fields we need!
    [JsonPropertyName("bookmarkCount")] public int? BookmarkCount { get; init; }
    [JsonPropertyName("likeCount")] public int? LikeCount { get; init; }
    [JsonPropertyName("viewCount")] public int? ViewCount { get; init; }
    [JsonPropertyName("commentCount")] public int? CommentCount { get; init; }

    [JsonPropertyName("createDate")] public string? CreateDate { get; init; }
    [JsonPropertyName("uploadDate")] public string? UploadDate { get; init; }
    [JsonPropertyName("illustType")] public int IllustType { get; init; }
    [JsonPropertyName("xRestrict")] public int XRestrict { get; init; }
    [JsonPropertyName("sl")] public int? Sl { get; init; }

    [JsonPropertyName("tags")] public ArtworkDetailTags? Tags { get; init; }
    [JsonPropertyName("aiType")] public int AiType { get; init; }

    // Page count info
    [JsonPropertyName("pageCount")] public int PageCount { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
}

public sealed class ArtworkDetailUrls
{
    [JsonPropertyName("mini")] public string? Mini { get; init; }
    [JsonPropertyName("thumb")] public string? Thumb { get; init; }
    [JsonPropertyName("small")] public string? Small { get; init; }
    [JsonPropertyName("regular")] public string? Regular { get; init; }
    [JsonPropertyName("original")] public string? Original { get; init; }
}

public sealed class ArtworkDetailTags
{
    [JsonPropertyName("tags")] public List<ArtworkDetailTag> Tags { get; init; } = [];
}

public sealed class ArtworkDetailTag
{
    [JsonPropertyName("tag")] public string? Tag { get; init; }
    [JsonPropertyName("locked")] public bool Locked { get; init; }
    [JsonPropertyName("deletable")] public bool Deletable { get; init; }
    [JsonPropertyName("userId")] public string? UserId { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
}
