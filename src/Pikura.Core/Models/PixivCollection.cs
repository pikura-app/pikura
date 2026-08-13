namespace Pikura.Core.Models;

/// <summary>
/// A Pixiv "Collection" (the beta curation feature at pixiv.net/collection) — a user-curated
/// mosaic of artwork with an optional caption and tags. Unlike every other endpoint in
/// <see cref="Services.PixivClient"/>, there is no dedicated ajax endpoint for reading a
/// collection's contents; the data is embedded server-side in the collection page's own
/// Next.js <c>__NEXT_DATA__</c> JSON, so <see cref="Services.PixivClient.GetCollectionAsync"/>
/// scrapes that instead (same technique already used for user search and CSRF tokens).
/// </summary>
public sealed class PixivCollection
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? UserProfileImageUrl { get; set; }
    public string? Caption { get; set; }
    public int BookmarkCount { get; set; }
    public int ViewCount { get; set; }
    public bool IsBookmarked { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>The collection's artwork, in the same order as the mosaic layout.</summary>
    public IReadOnlyList<ArtworkPreview> Works { get; set; } = [];

    /// <summary>IDs of this same creator's other collections (including this one) — Pixiv
    /// conveniently returns the whole set on every single collection page.</summary>
    public IReadOnlyList<string> SiblingCollectionIds { get; set; } = [];

    /// <summary>Full metadata (title/thumbnail/counts) for every ID in
    /// <see cref="SiblingCollectionIds"/> — also embedded on the same collection page, under
    /// serverSerializedPreloadedState.work.collection, so no extra requests are needed to show
    /// them as proper collage tiles instead of bare IDs.</summary>
    public IReadOnlyList<PixivCollectionSummary> SiblingCollections { get; set; } = [];
}

/// <summary>Lightweight collection listing entry — used for a creator's sibling collections and
/// for the featured/"All collections" browse page, neither of which need the full artwork list.</summary>
public sealed class PixivCollectionSummary
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? ThumbnailImageUrl { get; set; }
    public int BookmarkCount { get; set; }
    public int ViewCount { get; set; }
    public bool IsBookmarked { get; set; }
    public string? BookmarkId { get; set; }
    /// <summary>Pixiv's content-rating flag for the collection: 0 = all-ages/safe, 1 = R-18,
    /// 2 = R-18G. Confirmed present on every collection-summary object returned by
    /// <c>/ajax/collection/recommend/collections</c> — drives the browse collage's
    /// Safe/R-18/All filter.</summary>
    public int XRestrict { get; set; }
    public bool IsR18 => XRestrict > 0;
}

/// <summary>Response body from GET /ajax/collection/{id}/bookmarkData — mirrors the shape of an
/// artwork's own bookmarkData (confirmed convention from the JS bundle, which reuses the same
/// generic bookmark plumbing for illust/novel/collection).</summary>
public sealed class CollectionBookmarkData
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    [System.Text.Json.Serialization.JsonConverter(typeof(Pikura.Core.Utilities.FlexibleStringConverter))]
    public string? Id { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("private")] public bool IsPrivate { get; set; }
}
