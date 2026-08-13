using System.Text.Json.Serialization;
using Pikura.Core.Utilities;

namespace Pikura.Core.Models;

/// <summary>
/// One comment on an artwork, from GET /ajax/illusts/comments/roots (top-level comments) or
/// GET /ajax/illusts/comments/replies (a thread's replies). Field names verified against
/// community documentation (vixipy/pixiv-api-docs, daydreamer-json/pixiv-ajax-api-docs).
/// </summary>
public sealed class PixivComment
{
    [JsonPropertyName("id"), JsonConverter(typeof(FlexibleStringConverter))] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("comment")] public string Comment { get; set; } = string.Empty;
    [JsonPropertyName("userId"), JsonConverter(typeof(FlexibleStringConverter))] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;
    [JsonPropertyName("img")] public string? UserImageUrl { get; set; }
    [JsonPropertyName("stampId")] public string? StampId { get; set; }
    [JsonPropertyName("stampLink")] public string? StampLink { get; set; }
    [JsonPropertyName("commentDate")] public string? CommentDate { get; set; }
    [JsonPropertyName("commentRootId"), JsonConverter(typeof(FlexibleStringConverter))] public string? CommentRootId { get; set; }
    [JsonPropertyName("commentParentId"), JsonConverter(typeof(FlexibleStringConverter))] public string? CommentParentId { get; set; }
    [JsonPropertyName("hasReplies")] public bool HasReplies { get; set; }
    [JsonPropertyName("editable")] public bool Editable { get; set; }

    /// <summary>Non-null/non-empty when the comment includes an emoji "stamp" instead of (or
    /// alongside) text — the image itself lives at CDN path derivable from StampId, but since
    /// Pixiv doesn't return a full URL reliably across endpoints, this is best-effort display
    /// text only ("[stamp]") unless StampLink is populated.</summary>
    public bool HasStamp => !string.IsNullOrEmpty(StampId);
}

/// <summary>Response body from GET /ajax/illusts/comments/roots.</summary>
public sealed class PixivCommentsRootsResponse
{
    [JsonPropertyName("comments")] public List<PixivComment> Comments { get; set; } = new();
    [JsonPropertyName("hasNext")] public bool HasNext { get; set; }
    [JsonPropertyName("totalComments")] public int TotalComments { get; set; }
}

/// <summary>Response body from GET /ajax/illusts/comments/replies.</summary>
public sealed class PixivCommentsRepliesResponse
{
    [JsonPropertyName("comments")] public List<PixivComment> Comments { get; set; } = new();
    [JsonPropertyName("hasNext")] public bool HasNext { get; set; }
}

/// <summary>Response body from POST /rpc/post_comment.php (confirmed from a captured live
/// request — see PixivClient.PostCommentAsync).</summary>
public sealed class AddCommentResponse
{
    [JsonPropertyName("comment_id"), JsonConverter(typeof(FlexibleStringConverter))] public string? CommentId { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }
    [JsonPropertyName("user_id"), JsonConverter(typeof(FlexibleStringConverter))] public string? UserId { get; set; }
    [JsonPropertyName("user_name")] public string? UserName { get; set; }
    [JsonPropertyName("stamp_id"), JsonConverter(typeof(FlexibleStringConverter))] public string? StampId { get; set; }
}
