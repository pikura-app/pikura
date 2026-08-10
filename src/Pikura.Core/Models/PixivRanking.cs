using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pikura.Core.Models;

/// <summary>
/// Accepts both real booleans (<c>true</c>/<c>false</c>) and pixiv's numeric
/// flavor (<c>0</c>/<c>1</c>). Pixiv's <c>/ranking.php?format=json</c> returns
/// <c>illust_content_type</c> fields as 0/1 ints, which the default System.Text.Json
/// bool reader rejects.
/// </summary>
public sealed class JsonFlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.TryGetInt64(out var n) ? n != 0 : reader.GetDouble() != 0,
            JsonTokenType.String =>
                bool.TryParse(reader.GetString(), out var b) ? b
                : int.TryParse(reader.GetString(), out var i) && i != 0,
            JsonTokenType.Null => false,
            _ => false,
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}

/// <summary>
/// Response shape of <c>GET /ranking.php?format=json</c>. The endpoint is
/// a legacy page-API, not an /ajax call, so it does NOT use the
/// <c>{ error, body }</c> envelope.
/// </summary>
public sealed record RankingResponse
{
    [JsonPropertyName("contents")] public IReadOnlyList<RankingEntry> Contents { get; init; } = [];
    [JsonPropertyName("mode")] public string Mode { get; init; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; init; } = string.Empty;
    [JsonPropertyName("page")] public int Page { get; init; }
    [JsonPropertyName("prev")] public object? Prev { get; init; }
    [JsonPropertyName("next")] public object? Next { get; init; }
    [JsonPropertyName("date")] public string Date { get; init; } = string.Empty;
    [JsonPropertyName("prev_date")] public object? PrevDate { get; init; }
    [JsonPropertyName("next_date")] public object? NextDate { get; init; }
    [JsonPropertyName("rank_total")] public int RankTotal { get; init; }
}

/// <summary>One entry in the ranking feed. Fields overlap heavily with
/// <see cref="ArtworkPreview"/> but the JSON shape differs, so we map to a
/// dedicated type and convert when we want to display it in the gallery grid.</summary>
public sealed record RankingEntry
{
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("date")] public string Date { get; init; } = string.Empty;
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("url")] public string? ThumbnailUrl { get; init; }
    [JsonPropertyName("illust_type")] public string IllustType { get; init; } = string.Empty;  // "0"/"1"/"2"
    [JsonPropertyName("illust_book_style")] public string IllustBookStyle { get; init; } = string.Empty;
    [JsonPropertyName("illust_page_count")] public string IllustPageCount { get; init; } = "1";
    [JsonPropertyName("user_id")] public long UserId { get; init; }
    [JsonPropertyName("user_name")] public string UserName { get; init; } = string.Empty;
    [JsonPropertyName("profile_img")] public string? ProfileImg { get; init; }
    [JsonPropertyName("illust_content_type")] public IllustContentType ContentType { get; init; } = new();
    [JsonPropertyName("illust_series")] public object? IllustSeries { get; init; }
    [JsonPropertyName("illust_id")] public long IllustId { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
    [JsonPropertyName("yes_rank")] public int YesRank { get; init; }
    [JsonPropertyName("rank")] public int Rank { get; init; }
    [JsonPropertyName("rating_count")] public int RatingCount { get; init; }
    [JsonPropertyName("view_count")] public int ViewCount { get; init; }

    /// <summary>Projects this ranking entry onto the shared <see cref="ArtworkPreview"/>
    /// shape so it renders in the same grid as other feeds.</summary>
    /// <param name="isR18Source">True if this entry came from one of pixiv's "_r18" ranking
    /// endpoints — the only reliable R-18 signal the legacy ranking.php API exposes.
    /// <c>ContentType.Sexual</c> is a mild content-warning flag, not an age rating, so it must
    /// not be used to decide R-18 status here either, or the inline viewer mislabels legitimate
    /// all-ages art as R-18 even after the ranking grid itself gets it right.</param>
    public ArtworkPreview ToPreview(bool isR18Source = false)
    {
        var hasR18Tag = Tags.Any(t => t.Contains("R-18", StringComparison.OrdinalIgnoreCase));
        var hasR18GTag = Tags.Any(t => t.Contains("R-18G", StringComparison.OrdinalIgnoreCase));
        var xRestrict = isR18Source || hasR18Tag || hasR18GTag ? 1 : 0;
        if (hasR18GTag) xRestrict = 2; // R-18G is xRestrict=2

        return new()
        {
            Id = IllustId.ToString(),
            Title = Title,
            IllustType = int.TryParse(IllustType, out var t) ? t : 0,
            ThumbnailUrl = ThumbnailUrl,
            PageCount = int.TryParse(IllustPageCount, out var p) ? p : 1,
            UserId = UserId.ToString(),
            UserName = UserName,
            XRestrict = xRestrict,
            BookmarkCount = RatingCount,
            Tags = Tags,
        };
    }
}

public sealed record IllustContentType
{
    // Pixiv returns these as 0/1 ints, so we apply the flexible bool converter everywhere.
    [JsonPropertyName("sexual")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Sexual { get; init; }
    [JsonPropertyName("lo")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Lo { get; init; }
    [JsonPropertyName("grotesque")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Grotesque { get; init; }
    [JsonPropertyName("violent")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Violent { get; init; }
    [JsonPropertyName("homosexual")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Homosexual { get; init; }
    [JsonPropertyName("drug")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Drug { get; init; }
    [JsonPropertyName("thoughts")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Thoughts { get; init; }
    [JsonPropertyName("antisocial")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Antisocial { get; init; }
    [JsonPropertyName("religion")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Religion { get; init; }
    [JsonPropertyName("original")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Original { get; init; }
    [JsonPropertyName("furry")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Furry { get; init; }
    [JsonPropertyName("bl")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Bl { get; init; }
    [JsonPropertyName("yuri")] [JsonConverter(typeof(JsonFlexibleBoolConverter))] public bool Yuri { get; init; }
}

/// <summary>Response for <c>GET /ajax/search/artworks/{keyword}</c>.</summary>
public sealed record ArtworkSearchResult
{
    [JsonPropertyName("illustManga")] public ArtworkSearchSection IllustManga { get; init; } = new();
    [JsonPropertyName("illust")] public ArtworkSearchSection? Illust { get; init; }
    [JsonPropertyName("manga")] public ArtworkSearchSection? Manga { get; init; }
    [JsonPropertyName("relatedTags")] public IReadOnlyList<string> RelatedTags { get; init; } = [];
}

public sealed record ArtworkSearchSection
{
    [JsonPropertyName("data")] public IReadOnlyList<ArtworkPreview> Data { get; init; } = [];
    [JsonPropertyName("total")] public int Total { get; init; }

    /// <summary>Only populated by the dedicated <c>/ajax/search/illustrations/</c> and
    /// <c>/ajax/search/manga/</c> endpoints (not the combined <c>/ajax/search/artworks/</c> one) —
    /// null there, since that endpoint's <c>total</c> is unreliable anyway (see <see cref="Pikura.Core.Services.PixivClient.SearchArtworksAsync"/>).</summary>
    [JsonPropertyName("lastPage")] public int? LastPage { get; init; }
}

/// <summary>
/// Response shape of <c>GET /novel/ranking.php?format=json</c> — the same
/// legacy PHP ranking engine as <see cref="RankingResponse"/> but for novels.
/// NOTE: field names below are best-effort (mirrored from the confirmed illust
/// ranking shape) — Pixiv doesn't publish a schema for the novel variant and it
/// could not be fetched raw for verification. All novel-specific numeric fields
/// are nullable so an unexpected/renamed field degrades to "stat unavailable"
/// instead of throwing during deserialization.
/// </summary>
public sealed record NovelRankingResponse
{
    [JsonPropertyName("contents")] public IReadOnlyList<NovelRankingEntry> Contents { get; init; } = [];
    [JsonPropertyName("mode")] public string Mode { get; init; } = string.Empty;
    [JsonPropertyName("page")] public int Page { get; init; }
    [JsonPropertyName("next")] public object? Next { get; init; }
    [JsonPropertyName("date")] public string Date { get; init; } = string.Empty;
    [JsonPropertyName("prev_date")] public object? PrevDate { get; init; }
    [JsonPropertyName("next_date")] public object? NextDate { get; init; }
    [JsonPropertyName("rank_total")] public int RankTotal { get; init; }
}

public sealed record NovelRankingEntry
{
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("url")] public string? ThumbnailUrl { get; init; }
    [JsonPropertyName("novel_id")] public long NovelId { get; init; }
    [JsonPropertyName("user_id")] public long UserId { get; init; }
    [JsonPropertyName("user_name")] public string UserName { get; init; } = string.Empty;
    [JsonPropertyName("profile_img")] public string? ProfileImg { get; init; }
    [JsonPropertyName("rank")] public int Rank { get; init; }
    [JsonPropertyName("yes_rank")] public int YesRank { get; init; }
    [JsonPropertyName("rating_count")] public int RatingCount { get; init; }
    [JsonPropertyName("view_count")] public int? ViewCount { get; init; }
    [JsonPropertyName("novel_text_count")] public int? TextCount { get; init; }
    [JsonPropertyName("novel_reading_time")] public int? ReadingTimeMinutes { get; init; }
    [JsonPropertyName("illust_content_type")] public IllustContentType ContentType { get; init; } = new();
}
