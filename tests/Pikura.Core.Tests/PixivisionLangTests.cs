using Pikura.Core.Services;
using Xunit;

namespace Pikura.Core.Tests;

public class PixivisionLangTests
{
    // Real markup shape (attrs are adjacent, no space between them):
    //   href="/<lang>/a/{id}"data-gtm-action="ClickImage"…>…background-image:url(thumb)
    //   …<h2 class="aec|arc__title"><a href="/<lang>/a/{id}"data-gtm-action="ClickTitle"…>{title}</a>
    private static string Card(string lang, long id, string title, string thumb) =>
        $"""<a href="/{lang}/a/{id}"data-gtm-action="ClickImage" class="arc__cover"><div style="background-image:url({thumb})"></div></a><h2 class="arc__title"><a href="/{lang}/a/{id}"data-gtm-action="ClickTitle">{title}</a></h2>""";

    [Fact]
    public void List_Regex_Matches_En_And_Ja()
    {
        var en = Card("en", 111, "English title", "https://i.pixivision.net/en-t.jpg");
        var ja = Card("ja", 222, "日本語タイトル", "https://i.pixivision.net/ja-t.jpg");
        var html = en + ja + Card("ko", 333, "한국어", "https://i.pixivision.net/ko-t.jpg");

        var m = PixivisionService.ArticleListItemRegex().Matches(html);
        Assert.Equal(3, m.Count);
        Assert.Equal("111", m[0].Groups["id"].Value);
        Assert.Equal("222", m[1].Groups["id"].Value);
        Assert.Equal("日本語タイトル", m[1].Groups["title"].Value);
        Assert.Contains("ja-t.jpg", m[1].Groups["thumb"].Value);
        Assert.Equal("333", m[2].Groups["id"].Value);
    }

    [Fact]
    public void List_Regex_Ignores_Other_Locales()
    {
        // /fr/ (unsupported) must not match — keeps foreign links out of results.
        var html = Card("fr", 999, "fr", "https://x/fr.jpg") + Card("zh", 888, "中文", "https://x/zh.jpg");
        var m = PixivisionService.ArticleListItemRegex().Matches(html);
        Assert.Single(m);
        Assert.Equal("888", m[0].Groups["id"].Value);
    }
}
