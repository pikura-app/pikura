using Pikura.Core.Models;
using Pikura.Core.Services;
using Xunit;

namespace Pikura.Core.Tests;

public class PixivisionParseTests
{
    // Same shape as pixivision's 2026 search/tag/feed cards: absolute links, no spaces between attributes.
    private const string Html =
        """<ul class="main-column-container"><li class="article-card-container"><article class="_article-card inspiration"><div class="arc__thumbnail-container"><a href="https://www.pixivision.net/en/a/12085"data-gtm-action="ClickImage"data-gtm-label="12085"><div class="_thumbnail" style="background-image:  url(https://i.pximg.net/c/a/img/1.jpg)"></div></a><a href="https://www.pixivision.net/en/c/news"data-gtm-action="ClickCategory"data-gtm-label="news"><span class="arc__thumbnail-label">News</span></a></div><div class="arc__title-container"><h2 class="arc__title"><a href="https://www.pixivision.net/en/a/12085"data-gtm-action="ClickTitle"data-gtm-label="12085">Trending &amp; Cats</a></h2></div><div class="arc__footer-container"><ul class="_tag-list"><li class="tls__list-item-container"><a href="https://www.pixivision.net/en/t/1611"data-gtm-action="ClickTag"data-gtm-label="pixiv Encyclopedia"><div class="tls__list-item small">pixiv Encyclopedia</div></a></li></ul><div class="arc__footer-date-pr"><time class="_date small light-gray" datetime="2026-09-19">2026.09.19</time></div></div></article></li>"""
      + """<li class="article-card-container"><article class="_article-card spotlight"><div class="arc__thumbnail-container"><a href="https://www.pixivision.net/en/a/12053"data-gtm-action="ClickImage"data-gtm-label="12053"><div class="_thumbnail" style="background-image:  url(https://i.pximg.net/c/a/img/2.jpg)"></div></a><a href="https://www.pixivision.net/en/c/illustration"data-gtm-action="ClickCategory"data-gtm-label="illustration"><span>Illustration</span></a></div><div class="arc__title-container"><h2 class="arc__title"><a href="https://www.pixivision.net/en/a/12053"data-gtm-action="ClickTitle"data-gtm-label="12053">Drawings of Cats</a></h2></div><div class="arc__footer-container"><ul class="_tag-list"><li><a href="https://www.pixivision.net/en/t/5"data-gtm-action="ClickTag"data-gtm-label="Cat"><div>Cat</div></a></li><li><a href="https://www.pixivision.net/en/t/9"data-gtm-action="ClickTag"data-gtm-label="Animals"><div>Animals</div></a></li></ul><time class="_date" datetime="2026-08-01">2026.08.01</time></div></article></li></ul><a href="/en/s/?q=cat&amp;p=2" class="next">next</a>""";

    [Fact]
    public void ParseArticleList_Reads_Cards_With_Category_And_Tag_Ids()
    {
        var page = PixivisionService.ParseArticleList(Html, 3);

        Assert.Equal(3, page.Page);
        Assert.True(page.HasNextPage);
        Assert.Equal(2, page.Items.Count);

        var first = page.Items[0];
        Assert.Equal(12085, first.Id);
        Assert.Equal("Trending & Cats", first.Title);
        Assert.Equal("https://i.pximg.net/c/a/img/1.jpg", first.ThumbnailUrl);
        Assert.Equal("news", first.CategorySlug);
        Assert.Equal(["pixiv Encyclopedia"], first.Tags);
        Assert.Equal([new PixivisionTagRef(1611, "pixiv Encyclopedia")], first.TagRefs);
        Assert.Equal(new DateTime(2026, 9, 19), first.PublishedDate);

        var second = page.Items[1];
        Assert.Equal(12053, second.Id);
        Assert.Equal("illustration", second.CategorySlug);
        Assert.Equal([new PixivisionTagRef(5, "Cat"), new PixivisionTagRef(9, "Animals")], second.TagRefs);
        Assert.Equal(new DateTime(2026, 8, 1), second.PublishedDate);
    }

    [Fact]
    public void ParseArticleList_Without_Next_Link_Has_No_Next_Page()
        => Assert.False(PixivisionService.ParseArticleList(Html.Replace("class=\"next\"", ""), 1).HasNextPage);
}

public sealed class PixivisionHistoryServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pikura-pv-history-{Guid.NewGuid():N}.json");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static PixivisionArticleSummary Article(long id) => new()
    {
        Id = id, Title = $"Article {id}", Tags = ["tag"], TagRefs = [new PixivisionTagRef(7, "tag")], CategorySlug = "manga",
    };

    [Fact]
    public void Record_Dedupes_And_Moves_To_Top()
    {
        var svc = new PixivisionHistoryService(_path);
        svc.Record(Article(1));
        svc.Record(Article(2));
        svc.Record(Article(1));

        Assert.Equal([1L, 2L], svc.GetAll().Select(e => e.Id));
    }

    [Fact]
    public void Record_Caps_At_MaxEntries_Dropping_Oldest()
    {
        var svc = new PixivisionHistoryService(_path);
        for (var i = 1; i <= PixivisionHistoryService.MaxEntries + 5; i++) svc.Record(Article(i));

        var all = svc.GetAll();
        Assert.Equal(PixivisionHistoryService.MaxEntries, all.Count);
        Assert.Equal(PixivisionHistoryService.MaxEntries + 5, all[0].Id);
        Assert.DoesNotContain(all, e => e.Id <= 5);
    }

    [Fact]
    public void Remove_And_Clear()
    {
        var svc = new PixivisionHistoryService(_path);
        svc.Record(Article(1));
        svc.Record(Article(2));
        svc.Remove(1);
        Assert.Equal([2L], svc.GetAll().Select(e => e.Id));
        svc.Clear();
        Assert.Empty(svc.GetAll());
    }

    [Fact]
    public void Persists_Across_Instances()
    {
        new PixivisionHistoryService(_path).Record(Article(42));

        var entry = Assert.Single(new PixivisionHistoryService(_path).GetAll());
        Assert.Equal(42, entry.Id);
        Assert.Equal("manga", entry.CategorySlug);
        Assert.Equal([new PixivisionTagRef(7, "tag")], entry.ToSummary().TagRefs);
    }
}
