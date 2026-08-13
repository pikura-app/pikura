using System.Text.Json.Serialization;
using Pikura.Core.Utilities;

namespace Pikura.Core.Models;

/// <summary>Response from /ajax/user/{id}/illusts/bookmarks endpoint.</summary>
public sealed class BookmarkedArtworksResponse
{
    [JsonPropertyName("works")] public List<BookmarkedArtwork> Works { get; set; } = new();
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("bookmarkRanges")] public List<BookmarkRange> BookmarkRanges { get; set; } = new();
}

public sealed class BookmarkedArtwork
{
    [JsonPropertyName("id"), JsonConverter(typeof(FlexibleStringConverter))] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("isBookmarkable")] public bool IsBookmarkable { get; set; }
    [JsonPropertyName("bookmarkData")] public BookmarkData? BookmarkData { get; set; }
    [JsonPropertyName("userId"), JsonConverter(typeof(FlexibleStringConverter))] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;
    [JsonPropertyName("pageCount")] public int PageCount { get; set; } = 1;
    [JsonPropertyName("xRestrict")] public int XRestrict { get; set; }
    [JsonPropertyName("illustType")] public int IllustType { get; set; }
    [JsonPropertyName("aiType")] public int AiType { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    public ArtworkPreview ToArtworkPreview() => new()
    {
        Id = Id,
        Title = Title,
        ThumbnailUrl = Url,
        UserId = UserId,
        UserName = UserName,
        PageCount = PageCount > 0 ? PageCount : 1,
        XRestrict = XRestrict,
        IllustType = IllustType,
        AiType = AiType,
        Tags = Tags,
        Width = Width,
        Height = Height,
    };
}

public sealed class BookmarkData
{
    [JsonPropertyName("id"), JsonConverter(typeof(FlexibleStringConverter))] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("private")] public bool IsPrivate { get; set; }
}

public sealed class BookmarkRange
{
    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; }
}

/// <summary>Response body from GET /ajax/illust/{id}/bookmarks/add — current bookmark state.</summary>
public sealed class ArtworkBookmarkState
{
    [JsonPropertyName("bookmarkData")] public BookmarkData? BookmarkData { get; set; }
    [JsonPropertyName("isBookmarkable")] public bool IsBookmarkable { get; set; }

    public bool IsBookmarked => BookmarkData != null;
    public bool IsPrivate => BookmarkData?.IsPrivate ?? false;
    public string? BookmarkId => BookmarkData?.Id;
}

/// <summary>Response body from POST /ajax/illusts/bookmarks/add.</summary>
public sealed class AddBookmarkBody
{
    [JsonPropertyName("last_bookmark_id")] public string? LastBookmarkId { get; set; }
}

/// <summary>Response body from POST /ajax/illusts/like.</summary>
public sealed class LikeResponse
{
    [JsonPropertyName("is_liked")] public bool IsLiked { get; set; }
}

/// <summary>OAuth token response from Pixiv authentication endpoint.</summary>
public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")] public string? Access_token { get; set; }
    [JsonPropertyName("token_type")] public string? Token_type { get; set; }
    [JsonPropertyName("expires_in")] public int Expires_in { get; set; }
    [JsonPropertyName("refresh_token")] public string? Refresh_token { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
}

/// <summary>Response from App API bookmark add endpoint.</summary>
public sealed class AppApiBookmarkResponse
{
    [JsonPropertyName("bookmark_id")] public string? Bookmark_id { get; set; }
}

/// <summary>Response from /bookmark.php or /ajax/user/{id}/following endpoint for bookmarked users.</summary>
public sealed class BookmarkedUsersResponse
{
    [JsonPropertyName("users")] public List<BookmarkedUser> Users { get; set; } = new();
    [JsonPropertyName("total")] public int Total { get; set; }
}

public sealed class BookmarkedUser
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;
    [JsonPropertyName("userAccount")] public string UserAccount { get; set; } = string.Empty;
    [JsonPropertyName("profileImageUrl")] public string ProfileImageUrl { get; set; } = string.Empty;
    [JsonPropertyName("isFollowed")] public bool IsFollowed { get; set; }
    [JsonPropertyName("illusts")] public List<ArtworkPreview> Illustrations { get; set; } = new();
}
