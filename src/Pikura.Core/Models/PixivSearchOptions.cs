using System.Text.Json.Serialization;

namespace Pikura.Core.Models;

/// <summary>
/// Extra filters for <see cref="Pikura.Core.Services.PixivClient.SearchArtworksAsync"/>, mirroring
/// the "Search option" dialog on pixiv.net's own search UI. Every field is optional/nullable —
/// an unset field means "don't filter on this" and is simply omitted from the query string.
/// Query param names/values verified against pixiv's own ajax/search/artworks endpoint
/// (see daydreamer-json/pixiv-ajax-api-docs): s_mode, type, ratio, tool, scd/ecd, wlt/wgt/hlt/hgt,
/// blt/bgt. Fields NOT included here ("Visible to", AI-generated toggle, "Bundle by creator") are
/// not reliably documented for this endpoint and were intentionally omitted rather than guessed.
/// </summary>
public sealed class ArtworkSearchOptions
{
    /// <summary>s_mode: s_tag (partial tag match, default), s_tag_full (exact tag match), s_tc (title/caption).</summary>
    public string? TargetMode { get; set; }

    /// <summary>type: all (default), illust_and_ugoira, illust, manga, ugoira.</summary>
    public string? WorkType { get; set; }

    /// <summary>ratio: -0.5 = portrait, 0 = square, 0.5 = landscape. Null = no filter.</summary>
    public double? Ratio { get; set; }

    /// <summary>tool: creation tool name, e.g. "Photoshop". Free text, matches pixiv's own list.</summary>
    public string? Tool { get; set; }

    /// <summary>scd: only show artwork posted on/after this date.</summary>
    public DateOnly? PostedAfter { get; set; }

    /// <summary>ecd: only show artwork posted on/before this date.</summary>
    public DateOnly? PostedBefore { get; set; }

    public int? MinWidth { get; set; }
    public int? MaxWidth { get; set; }
    public int? MinHeight { get; set; }
    public int? MaxHeight { get; set; }

    /// <summary>blt/bgt — bookmark count filter. Premium-account only on pixiv's end.</summary>
    public int? MinBookmarks { get; set; }
    public int? MaxBookmarks { get; set; }

    /// <summary>ai_type: 0 = display AI-generated work (default), 1 = hide it. Null = don't send
    /// the param at all (equivalent to "display").</summary>
    public int? AiType { get; set; }

    public bool IsEmpty =>
        string.IsNullOrEmpty(TargetMode) && string.IsNullOrEmpty(WorkType) && Ratio is null &&
        string.IsNullOrEmpty(Tool) && PostedAfter is null && PostedBefore is null &&
        MinWidth is null && MaxWidth is null && MinHeight is null && MaxHeight is null &&
        MinBookmarks is null && MaxBookmarks is null && AiType is null;
}

/// <summary>
/// Response from <c>GET /ajax/search/users/{keyword}</c>. Field names are best-effort
/// (community-reverse-engineered, e.g. pixiv.ts's <c>pixiv.search.users()</c>) since pixiv
/// publishes no schema for this endpoint — all fields degrade gracefully (default/empty) if
/// pixiv renames something, rather than throwing during deserialization.
/// </summary>
public sealed class UserSearchResult
{
    [JsonPropertyName("users")] public List<UserSearchEntry> Users { get; set; } = new();
    [JsonPropertyName("total")] public int Total { get; set; }
}

public sealed class UserSearchEntry
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("image")] public string? ImageUrl { get; set; }
    [JsonPropertyName("illusts")] public List<ArtworkPreview> Illusts { get; set; } = new();
    [JsonPropertyName("isFollowed")] public bool IsFollowed { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }

    /// <summary>Up to 4 of this user's most recent work IDs (artwork id + illust type), as
    /// returned by <c>/search/users</c>'s <c>workIds</c> field. No thumbnail URL is included by
    /// pixiv — callers must resolve thumbnails lazily (e.g. via <c>GetArtworkDetailAsync</c>)
    /// for the "avatar + 4 recent thumbnails" user card layout.</summary>
    public List<PixivUserSearchWorkRef> RecentWorkIds { get; set; } = new();

    /// <summary>Alias of <see cref="Name"/> kept for callers written against pixiv's older/other field naming.</summary>
    public string UserName => Name;

    /// <summary>Alias of <see cref="ImageUrl"/> kept for callers written against pixiv's older/other field naming.</summary>
    public string? ProfileImageUrl => ImageUrl;
}

/// <summary>
/// Pixiv's <c>/ajax/search/users/{keyword}</c> endpoint no longer exists — user search now only
/// works via the HTML page at <c>/search/users?s_mode=s_usr&amp;nick={keyword}&amp;i=1&amp;comment=&amp;p={page}</c>,
/// which embeds its data as a <c>&lt;script id="__NEXT_DATA__"&gt;</c> JSON blob (Next.js). These
/// models mirror that blob's shape (confirmed by fetching a live page and dumping the raw JSON —
/// see <c>SearchUsersAsync</c>). The user's name/avatar/comment are NOT directly under
/// <c>pageProps</c> — they live inside <see cref="PixivUserSearchPageProps.ServerSerializedPreloadedState"/>,
/// a *second, nested JSON string* that must be parsed separately (see
/// <see cref="PixivUserSearchPreloadedState"/>).
/// </summary>
public sealed class PixivUserSearchNextData
{
    [JsonPropertyName("props")] public PixivUserSearchNextDataProps? Props { get; set; }
}

public sealed class PixivUserSearchNextDataProps
{
    [JsonPropertyName("pageProps")] public PixivUserSearchPageProps? PageProps { get; set; }
}

public sealed class PixivUserSearchPageProps
{
    [JsonPropertyName("userIds")] public List<long> UserIds { get; set; } = new();

    /// <summary>userId (as string) -> up to 4 of that user's most recent works (id/type/created_at only,
    /// no thumbnail URL). Mirrors pixiv's own search-users UI, which shows an avatar + 4 recent thumbnails
    /// per user row.</summary>
    [JsonPropertyName("workIds")] public Dictionary<string, List<PixivUserSearchWorkRef>> WorkIds { get; set; } = new();

    /// <summary>Direct user profile map. Current Pixiv returns this here instead of inside
    /// <see cref="ServerSerializedPreloadedState"/>.</summary>
    [JsonPropertyName("userData")] public PixivUserSearchUserData? UserData { get; set; }

    /// <summary>A JSON-encoded string (not a nested object!) containing the actual user profile data.
    /// Must be deserialized a second time into <see cref="PixivUserSearchPreloadedState"/>.
    /// Kept as a fallback for older or future page shapes.</summary>
    [JsonPropertyName("serverSerializedPreloadedState")] public string? ServerSerializedPreloadedState { get; set; }
}

public sealed class PixivUserSearchWorkRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
}

public sealed class PixivUserSearchPreloadedState
{
    [JsonPropertyName("userData")] public PixivUserSearchUserData? UserData { get; set; }
}

public sealed class PixivUserSearchUserData
{
    [JsonPropertyName("users")] public Dictionary<string, PixivUserSearchUserInfo> Users { get; set; } = new();
}

public sealed class PixivUserProfileImageUrls
{
    [JsonPropertyName("medium")] public string? Medium { get; set; }
    [JsonPropertyName("px_170x170")] public string? Px170x170 { get; set; }
    [JsonPropertyName("px170x170")] public string? Px170 { get; set; }
    [JsonPropertyName("big")] public string? Big { get; set; }
    [JsonPropertyName("small")] public string? Small { get; set; }

    public string? BestUrl => Medium ?? Px170x170 ?? Px170 ?? Big ?? Small;
}

public sealed class PixivUserSearchUserInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    // Legacy web-search keys.
    [JsonPropertyName("profileImg")] public string? ProfileImg { get; set; }
    [JsonPropertyName("profileImgBig")] public string? ProfileImgBig { get; set; }

    // Newer shape observed in __NEXT_DATA__.
    [JsonPropertyName("profileImageUrls")] public PixivUserProfileImageUrls? ProfileImageUrls { get; set; }

    [JsonPropertyName("comment")] public string? Comment { get; set; }

    public string? BestAvatarUrl => ProfileImageUrls?.BestUrl ?? ProfileImgBig ?? ProfileImg;
}
