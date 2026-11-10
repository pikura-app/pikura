using Pikura.Core.Services;
using Xunit;

namespace Pikura.Core.Tests;

public class SauceNaoWebParseTests
{
    // Trimmed from a real saucenao.com results page (2026): a Danbooru hit citing a pixiv source,
    // then a lower-similarity pixiv hit.
    private const string Html =
        """<div class="result"><table class="resulttable"><tr><td class="resulttablecontent"><div class="resultmatchinfo"><div class="resultsimilarityinfo">96.45%</div></div><div class="resultcontent"><div class="resulttitle"><strong>Creator: </strong>tidsean<br /></div><div class="resultcontentcolumn"><strong>Source: </strong><a href="https://www.pixiv.net/member_illust.php?mode=medium&illust_id=134439552">pixiv #134439552</a><br /></div></div></td></tr></table></div>"""
      + """<div class="result"><table class="resulttable"><tr><td class="resulttablecontent"><div class="resultmatchinfo"><div class="resultsimilarityinfo">55.64%</div></div><div class="resultcontent"><div class="resulttitle"><strong>feifらくがき詰め</strong><br /></div><div class="resultcontentcolumn"><strong>pixiv ID: </strong><a href="https://www.pixiv.net/member_illust.php?mode=medium&illust_id=60494173" class="linkify">60494173</a><br /><strong>Member: </strong><a href="https://www.pixiv.net/users/1">feif</a></div></div></td></tr></table></div>""";

    [Fact]
    public void Picks_Best_Confident_Result_With_A_Pixiv_Id()
    {
        var hit = ImageLookupService.ParseWebResults(Html);
        Assert.NotNull(hit);
        Assert.Equal("134439552", hit!.PixivId);
        Assert.Equal(96.45, hit.Similarity);
        Assert.Null(hit.SourceTitle); // "Creator:" is a booru label, not a title
        Assert.Equal("https://www.pixiv.net/artworks/134439552", hit.SourceUrl);
    }

    [Fact]
    public void Low_Similarity_Only_Is_No_Match()
        => Assert.Null(ImageLookupService.ParseWebResults(Html.Replace("96.45%", "40.10%")));

    [Fact]
    public void Pixiv_Result_Keeps_Title_And_Member()
    {
        var hit = ImageLookupService.ParseWebResults(Html.Replace("96.45%", "10%").Replace("55.64%", "91.2%"));
        Assert.Equal("60494173", hit!.PixivId);
        Assert.Equal("feifらくがき詰め", hit.SourceTitle);
        Assert.Equal("feif", hit.ArtistTags);
    }
}
