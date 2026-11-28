using System.Text.Json;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Xunit;

namespace Pikura.Avalonia.Tests;

public class NovelTextTests
{
    [Fact]
    public void Markup_Splits_Pages_And_Recognises_Tags()
    {
        var pages = NovelMarkup.Parse("[chapter:序章]\n本文[[rb:漢字 > かんじ]]です\n[pixivimage:123-2]\n[newpage]\n[[jumpuri:link > https://example.com]] [jump:1]\n[uploadedimage:9]");
        Assert.Equal(2, pages.Count);
        Assert.Equal(NovelBlockKind.Chapter, pages[0][0].Kind);
        var text = pages[0][1];
        Assert.Equal(NovelBlockKind.Text, text.Kind);
        Assert.Contains(text.Spans, s => s.Text == "漢字" && s.Ruby == "かんじ");
        Assert.Equal("123-2", pages[0][2].Value);
        Assert.Contains(pages[1][0].Spans, s => s.Url == "https://example.com");
        Assert.Contains(pages[1], b => b.Kind == NovelBlockKind.Jump && b.Value == "1");
        Assert.Contains(pages[1], b => b.Kind == NovelBlockKind.UploadedImage && b.Value == "9");
    }

    [Fact]
    public void Parses_Novel_Body()
    {
        using var doc = JsonDocument.Parse("""
            {"id":"1","title":"T","userId":"2","userName":"U","content":"a[newpage]b","xRestrict":1,
             "tags":{"tags":[{"tag":"R-18"},{"tag":"x"}]},"characterCount":10,"readingTime":120,
             "seriesNavData":{"title":"S","prev":{"id":"0","title":"P"},"next":null},
             "textEmbeddedImages":{"9":{"urls":{"original":"https://i.pximg.net/o.jpg"}}}}
            """);
        var n = PixivClient.ParseNovelText(doc.RootElement);
        Assert.Equal("T", n.Title);
        Assert.Equal(1, n.XRestrict);
        Assert.Equal(["R-18", "x"], n.Tags);
        Assert.Equal("S", n.SeriesTitle);
        Assert.Equal("0", n.PrevId);
        Assert.Null(n.NextId);
        Assert.Equal("https://i.pximg.net/o.jpg", n.EmbeddedImages["9"]);
    }
}
