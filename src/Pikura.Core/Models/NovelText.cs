using System.Text.RegularExpressions;

namespace Pikura.Core.Models;

/// <summary>A novel's text and metadata from <c>GET /ajax/novel/{id}</c>.</summary>
public sealed record NovelText
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    public string? CoverUrl { get; init; }
    public string Description { get; init; } = "";
    public string Content { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public int XRestrict { get; init; }
    public int BookmarkCount { get; init; }
    public int LikeCount { get; init; }
    public int ViewCount { get; init; }
    public int CharacterCount { get; init; }
    public int ReadingTimeSeconds { get; init; }
    public string CreateDate { get; init; } = "";
    public string? SeriesId { get; init; }
    public string? SeriesTitle { get; init; }
    public string? PrevId { get; init; }
    public string? PrevTitle { get; init; }
    public string? NextId { get; init; }
    public string? NextTitle { get; init; }
    /// <summary>Images uploaded into the text ([uploadedimage:id]) → URL.</summary>
    public IReadOnlyDictionary<string, string> EmbeddedImages { get; init; } = new Dictionary<string, string>();
}

public enum NovelBlockKind { Text, Chapter, PixivImage, UploadedImage, Jump }

/// <summary>One piece of a novel page. <see cref="Spans"/> holds text with ruby readings.</summary>
public sealed record NovelBlock(NovelBlockKind Kind, IReadOnlyList<NovelSpan> Spans, string Value = "");

/// <summary>Text, a link (<see cref="Url"/>) or a base with its <see cref="Ruby"/> reading.</summary>
public sealed record NovelSpan(string Text, string? Ruby = null, string? Url = null);

/// <summary>Parses pixiv's novel markup: [newpage], [chapter:], [[rb:漢字 > かんじ]],
/// [pixivimage:id-page], [uploadedimage:id], [jump:n] and [[jumpuri:text > url]].</summary>
public static partial class NovelMarkup
{
    [GeneratedRegex(@"\[newpage\]")]
    private static partial Regex NewPage();

    [GeneratedRegex(@"\[chapter:(.*?)\]|\[pixivimage:([0-9]+(?:-[0-9]+)?)\]|\[uploadedimage:([0-9]+)\]|\[jump:([0-9]+)\]")]
    private static partial Regex BlockTag();

    [GeneratedRegex(@"\[\[rb:(.*?)>(.*?)\]\]|\[\[jumpuri:(.*?)>(.*?)\]\]")]
    private static partial Regex InlineTag();

    public static IReadOnlyList<IReadOnlyList<NovelBlock>> Parse(string content)
    {
        var pages = new List<IReadOnlyList<NovelBlock>>();
        foreach (var page in NewPage().Split(content.Replace("\r\n", "\n")))
        {
            var blocks = new List<NovelBlock>();
            var last = 0;
            foreach (Match m in BlockTag().Matches(page))
            {
                AddText(blocks, page[last..m.Index]);
                if (m.Groups[1].Success) blocks.Add(new NovelBlock(NovelBlockKind.Chapter, Spans(m.Groups[1].Value)));
                else if (m.Groups[2].Success) blocks.Add(new NovelBlock(NovelBlockKind.PixivImage, [], m.Groups[2].Value));
                else if (m.Groups[3].Success) blocks.Add(new NovelBlock(NovelBlockKind.UploadedImage, [], m.Groups[3].Value));
                else blocks.Add(new NovelBlock(NovelBlockKind.Jump, [], m.Groups[4].Value));
                last = m.Index + m.Length;
            }
            AddText(blocks, page[last..]);
            pages.Add(blocks);
        }
        return pages;
    }

    private static void AddText(List<NovelBlock> blocks, string text)
    {
        text = text.Trim('\n');
        if (text.Length > 0) blocks.Add(new NovelBlock(NovelBlockKind.Text, Spans(text)));
    }

    public static IReadOnlyList<NovelSpan> Spans(string text)
    {
        var spans = new List<NovelSpan>();
        var last = 0;
        foreach (Match m in InlineTag().Matches(text))
        {
            if (m.Index > last) spans.Add(new NovelSpan(text[last..m.Index]));
            if (m.Groups[1].Success) spans.Add(new NovelSpan(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
            else spans.Add(new NovelSpan(m.Groups[3].Value.Trim(), Url: m.Groups[4].Value.Trim()));
            last = m.Index + m.Length;
        }
        if (last < text.Length) spans.Add(new NovelSpan(text[last..]));
        return spans;
    }
}

/// <summary>A novel series from <c>GET /ajax/novel/series/{id}</c>.</summary>
public sealed record NovelSeries(string Id, string Title, string UserName, string Caption, int Total, int XRestrict);

/// <summary>One chapter of a series from <c>GET /ajax/novel/series_content/{id}</c>.</summary>
public sealed record NovelSeriesEntry(string Id, string Title, int Order, int CharacterCount, int XRestrict);
