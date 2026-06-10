using System.Text.Json.Serialization;

namespace Pikura.Core.Models;

/// <summary>Body shape returned by <c>GET /ajax/user/{id}</c>.</summary>
public sealed record PixivUserInfo
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("image")] public string? ImageUrl { get; init; }
    [JsonPropertyName("imageBig")] public string? ImageBigUrl { get; init; }
    [JsonPropertyName("isFollowed")] public bool IsFollowed { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    /// <summary>Profile background/banner image URL (only present with ?full=1).</summary>
    [JsonPropertyName("background")] public PixivUserBackground? Background { get; init; }
}

public sealed record PixivUserBackground
{
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("isPrivate")] public bool IsPrivate { get; init; }
}

/// <summary>
/// Body shape returned by <c>GET /ajax/search/users/{keyword}</c>. Pixiv has
/// changed this endpoint historically; we treat it best-effort.
/// </summary>
public sealed record UserSearchResult
{
    [JsonPropertyName("users")] public IReadOnlyList<UserSearchEntry> Users { get; init; } = [];
    [JsonPropertyName("total")] public int Total { get; init; }
}

public sealed record UserSearchEntry
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = string.Empty;
    [JsonPropertyName("userName")] public string UserName { get; init; } = string.Empty;
    [JsonPropertyName("profileImageUrl")] public string? ProfileImageUrl { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
}

/// <summary>Body shape for <c>GET /touch/ajax/user/self/status</c>.</summary>
public sealed record TouchSelfStatus
{
    [JsonPropertyName("user_status")] public TouchSelfUserStatus? UserStatus { get; init; }
}

public sealed record TouchSelfUserStatus
{
    [JsonPropertyName("user_id")] public string UserId { get; init; } = string.Empty;
    [JsonPropertyName("user_name")] public string UserName { get; init; } = string.Empty;
    [JsonPropertyName("logged_in")] public bool LoggedIn { get; init; }
}
