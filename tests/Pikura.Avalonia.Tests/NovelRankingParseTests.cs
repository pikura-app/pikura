using Pikura.Core.Services;
using Xunit;

namespace Pikura.Avalonia.Tests;

public class NovelRankingParseTests
{
    private const string Page = """
        <html><body><script id="__NEXT_DATA__" type="application/json">
        {"props":{"pageProps":{"assign":{"display_a":{
          "rank_a":[{"rank":1,"id":"29737463","title":"第二十四話","user_id":"12070158","user_name":"ren.s",
                     "x_restrict":"1","character_count":"10863","ai_type":"2","tag_a":["R-18","オリジナル"],
                     "url":"https://i.pximg.net/c/100x100/novel-cover-master/img/2026/10/25/x_master1200.jpg",
                     "series_title":"S","bookmark_count":1536,"reading_time":1303}],
          "mode":"daily_r18","page":1,"next":2,"prev_date":"20261126","next_date":null,
          "page_a":{"1":"1-50","2":"51-100"}}}}}}
        </script></body></html>
        """;

    [Fact]
    public void Parses_Entries_Paging_And_Date_From_Next_Data()
    {
        var r = PixivClient.ParseNovelRankingPage(Page);
        var e = Assert.Single(r.Contents);
        Assert.Equal(29737463, e.NovelId);
        Assert.Equal(12070158, e.UserId);
        Assert.Equal(10863, e.TextCount);
        Assert.Equal(22, e.ReadingTimeMinutes);
        Assert.Equal(1536, e.BookmarkCount);
        Assert.Equal(1, e.XRestrict);
        Assert.True(e.IsAi);
        Assert.Contains("/c/240x480_80/", e.ThumbnailUrl);
        Assert.Equal(2, Assert.IsType<int>(r.Next));
        Assert.Equal("20261127", r.Date);
        Assert.Equal(100, r.RankTotal);
    }

    [Fact]
    public void Page_Without_Next_Data_Gives_Empty_Ranking() =>
        Assert.Empty(PixivClient.ParseNovelRankingPage("<html></html>").Contents);
}
