using System.Text;
using System.Text.RegularExpressions;

namespace Pikura.Core.Services.Hoshi;

/// <summary>Pixiv artwork facts as shown in the Hoshi system prompt. All fields optional — empty lines are omitted.</summary>
public sealed record ArtworkFacts(
    string? Title,
    string? ArtworkId,
    string? UserName,
    string? UserId,
    DateTimeOffset? CreateDate,
    IReadOnlyList<(string Tag, string? Translation)>? Tags,
    long? ViewCount,
    long? LikeCount,
    long? BookmarkCount,
    int? PageCount,
    int? XRestrict,
    int? AiType,
    string? Caption);

public static class HoshiPrompt
{
    /// <summary>One-line decision used at Hoshi enable time: existing installs of the legacy
    /// models mean the user must be migrated to custom-model settings rather than get a pull.</summary>
    public static bool ShouldMigrateToLegacyModels(bool useCustom, IEnumerable<string> installedNames)
    {
        if (useCustom) return false;
        var names = installedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Has(string prefix) => names.Any(n => n == prefix || n.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase));
        return Has("llava") && Has("llama3.2") && !Has("qwen3-vl:4b");
    }

    private const string BaseTemplate = """
        You are Hoshi, the friendly assistant built into Pikura, a desktop app for browsing and downloading artwork from pixiv.
        Rules:
        - Reply in the same language the user writes in.
        - Be concise: a few sentences unless the user asks for more detail.
        - For facts about the current artwork (title, artist, upload date, tags, views, likes, bookmarks, pages, rating, AI-generated status, caption), use ONLY the "Current artwork" data provided below. Never guess these from the image.
        - "Submitted", "uploaded", "posted" and "published" all mean the upload date.
        - If the provided data does not contain the answer, say you don't know. Never invent artwork titles, artist names, IDs, dates, numbers or URLs.
        - When describing the image itself, describe only what you can actually see.
        Today's date: {0}
        """;

    private const string ToolsLine =
        "You can use tools to look up artworks, search pixiv, find similar artworks or artists, show, open or download artworks, and manage favorites. Use a tool when the user asks for one of these actions. Never claim you did something unless a tool result confirms it. Only download artworks or change favorites when the user explicitly asks.";

    private const string NoToolsLine =
        "You cannot perform actions yourself. If the user asks you to download, search, open or favorite something, tell them to use the buttons below the chat or phrase it as a command such as \"download this\" or \"find similar art\".";

    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new("\\s+", RegexOptions.Compiled);

    public static string BuildSystemPrompt(ArtworkFacts? facts, IReadOnlyList<string>? taggerTags, bool toolsEnabled, DateTimeOffset now)
    {
        var parts = new List<string> { string.Format(BaseTemplate, now.ToString("yyyy-MM-dd")) };
        parts.Add(toolsEnabled ? ToolsLine : NoToolsLine);
        if (facts != null)
        {
            var block = FormatFacts(facts, now);
            if (block.Length > 0) parts.Add(block);
        }
        if (taggerTags is { Count: > 0 })
            parts.Add("Tags detected in the image by a local tagger: " + string.Join(", ", taggerTags));
        return string.Join("\n\n", parts);
    }

    /// <summary>Per-round streaming seam: returns the newly-visible delta of
    /// <paramref name="roundText"/> after <paramref name="alreadyEmitted"/> chars were
    /// yielded this round. Each tool-call round resets the counter, so deltas are
    /// computed from an empty slate every round.</summary>
    public static string VisibleDelta(string roundText, int alreadyEmitted)
        => roundText.Length > alreadyEmitted ? roundText[alreadyEmitted..] : "";

    /// <summary>Spec §1d facts block; empty lines omitted. Empty string when nothing known.</summary>
    public static string FormatFacts(ArtworkFacts f, DateTimeOffset now)
    {
        var sb = new StringBuilder("Current artwork (from pixiv):");
        var any = false;
        void Line(string s) { sb.Append('\n').Append("- ").Append(s); any = true; }

        if (!string.IsNullOrEmpty(f.Title)) Line($"Title: {f.Title}");
        if (!string.IsNullOrEmpty(f.ArtworkId)) Line($"Artwork ID: {f.ArtworkId} (https://www.pixiv.net/artworks/{f.ArtworkId})");
        if (!string.IsNullOrEmpty(f.UserName) || !string.IsNullOrEmpty(f.UserId))
        {
            var artist = f.UserName ?? "";
            Line($"Artist: {artist} (user ID {f.UserId}, https://www.pixiv.net/users/{f.UserId})");
        }
        if (f.CreateDate is { } dt)
        {
            var utc = dt.ToUniversalTime();
            var days = (int)Math.Floor((now - utc).TotalDays);
            Line($"Uploaded: {utc:yyyy-MM-dd HH:mm} UTC ({days} days ago)");
        }
        if (f.Tags is { Count: > 0 })
        {
            var tags = f.Tags.Select(t =>
                !string.IsNullOrEmpty(t.Translation) && !string.Equals(t.Tag, t.Translation, StringComparison.OrdinalIgnoreCase)
                    ? $"{t.Tag} ({t.Translation})" : t.Tag);
            Line("Tags: " + string.Join(", ", tags));
        }
        if (f.ViewCount != null || f.LikeCount != null || f.BookmarkCount != null)
            Line($"Views: {f.ViewCount?.ToString("N0") ?? "?"}, Likes: {f.LikeCount?.ToString("N0") ?? "?"}, Bookmarks: {f.BookmarkCount?.ToString("N0") ?? "?"}");
        if (f.PageCount != null) Line($"Pages: {f.PageCount}");
        if (f.XRestrict != null)
            Line("Rating: " + (f.XRestrict == 2 ? "R-18G" : f.XRestrict == 1 ? "R-18" : "All ages"));
        if (f.AiType is 1 or 2)
            Line("AI-generated: " + (f.AiType == 2 ? "yes" : "no"));
        if (!string.IsNullOrEmpty(f.Caption))
        {
            var cap = MultiSpace.Replace(HtmlTag.Replace(f.Caption, ""), " ").Trim();
            if (cap.Length > 500) cap = cap[..500] + "…";
            if (cap.Length > 0) Line($"Caption: {cap}");
        }
        return any ? sb.ToString() : "";
    }
}
