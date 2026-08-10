using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pikura.Core.Models;

/// <summary>Response from /ajax/user/{userId}/novels endpoint.</summary>
public sealed class UserNovelsResponse
{
    [JsonPropertyName("novels")] public List<NovelPreview> Novels { get; set; } = new();
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("lastIndex")] public int LastIndex { get; set; }
}

/// <summary>Nested container returned by /ajax/search/novels/{keyword}.</summary>
public sealed class NovelSearchData
{
    [JsonPropertyName("data")] public List<NovelPreview> Data { get; set; } = new();
    [JsonPropertyName("total")] public int Total { get; set; }
}

/// <summary>Response from /ajax/search/novels/{keyword} endpoint.</summary>
/// <remarks>
/// Pixiv's current shape nests the list under <c>body.novel.data</c> rather than
/// <c>body.novels</c>. We read the nested property and fall back to the legacy
/// top-level array if Pixiv ever changes it back.
/// </remarks>
public sealed class NovelSearchResult
{
    [JsonPropertyName("novel")] public NovelSearchData? Novel { get; set; }
    [JsonPropertyName("novels")] public List<NovelPreview>? NovelsLegacy { get; set; }

    [JsonIgnore] public IReadOnlyList<NovelPreview> Novels => Novel?.Data ?? NovelsLegacy ?? new List<NovelPreview>();
    [JsonIgnore] public int Total => Novel?.Total ?? 0;
}

/// <summary>Response from /ajax/novel/{id} endpoint.</summary>
public sealed class NovelDetailResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("coverUrl")] public string? CoverUrl { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("bookmarkCount")] public int BookmarkCount { get; set; }
    [JsonPropertyName("viewCount")] public int ViewCount { get; set; }
    [JsonPropertyName("likeCount")] public int LikeCount { get; set; }
    [JsonPropertyName("commentCount")] public int CommentCount { get; set; }
    [JsonPropertyName("textLength")] public int TextLength { get; set; }
    [JsonPropertyName("seriesId")] public string? SeriesId { get; set; }
    [JsonPropertyName("seriesTitle")] public string? SeriesTitle { get; set; }
    [JsonPropertyName("isOriginal")] public bool IsOriginal { get; set; }
    [JsonPropertyName("isR18")] public bool IsR18 { get; set; }
    [JsonPropertyName("createDate")] public string CreateDate { get; set; } = string.Empty;
}

/// <summary>Preview of a novel in search/list results.</summary>
public sealed class NovelPreview
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;

    // Pixiv's /ajax/search/novels endpoint uses "url" for the cover image.
    [JsonPropertyName("coverUrl")] public string? CoverUrl { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    public string? EffectiveCoverUrl => CoverUrl ?? Url;

    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("bookmarkCount")] public int BookmarkCount { get; set; }
    [JsonPropertyName("viewCount")] public int ViewCount { get; set; }

    // Some endpoints return "textLength", the search endpoint returns "textCount".
    [JsonPropertyName("textLength")] public int TextLength { get; set; }
    [JsonPropertyName("textCount")] public int TextCount { get; set; }
    public int EffectiveTextLength => TextLength > 0 ? TextLength : TextCount;

    [JsonPropertyName("isR18")] public bool IsR18 { get; set; }
    [JsonPropertyName("createDate")] public string CreateDate { get; set; } = string.Empty;
}
