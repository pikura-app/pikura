using Pikura.Core.Services.Hoshi;
using Xunit;

namespace Pikura.Core.Tests;

public class HoshiPromptTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Base_Only_Has_NoTools_Line_And_Date()
    {
        var p = HoshiPrompt.BuildSystemPrompt(null, null, toolsEnabled: false, Now);
        Assert.Contains("You cannot perform actions yourself.", p);
        Assert.Contains("Today's date: 2025-06-15", p);
        Assert.DoesNotContain("You can use tools", p);
        Assert.DoesNotContain("Current artwork (from pixiv)", p);
    }

    [Fact]
    public void Tools_Line_Replaces_NoTools()
    {
        var p = HoshiPrompt.BuildSystemPrompt(null, null, toolsEnabled: true, Now);
        Assert.Contains("You can use tools to look up artworks", p);
        Assert.DoesNotContain("You cannot perform actions yourself.", p);
    }

    [Fact]
    public void Facts_Block_Formats_All_Fields()
    {
        var f = new ArtworkFacts(
            Title: "Sunset", ArtworkId: "123", UserName: "alice", UserId: "42",
            CreateDate: Now.AddDays(-3),
            Tags: [(Tag: "夕日", Translation: "sunset"), (Tag: "女の子", Translation: "girl"), (Tag: "solo", Translation: "solo")],
            ViewCount: 1234, LikeCount: 56, BookmarkCount: 7,
            PageCount: 2, XRestrict: 1, AiType: 2, Caption: "<b>hi</b>  there");
        var t = HoshiPrompt.FormatFacts(f, Now);
        Assert.Contains("- Title: Sunset", t);
        Assert.Contains("- Artwork ID: 123 (https://www.pixiv.net/artworks/123)", t);
        Assert.Contains("- Artist: alice (user ID 42, https://www.pixiv.net/users/42)", t);
        Assert.Contains("UTC (3 days ago)", t);
        Assert.Contains("夕日 (sunset)", t);
        Assert.DoesNotContain("(solo)", t); // same translation → no parens
        Assert.Contains("- Views: 1,234, Likes: 56, Bookmarks: 7", t);
        Assert.Contains("- Pages: 2", t);
        Assert.Contains("- Rating: R-18", t);
        Assert.Contains("- AI-generated: yes", t);
        Assert.Contains("- Caption: hi there", t);
    }

    [Fact]
    public void Facts_Omits_Empty_Lines()
    {
        var t = HoshiPrompt.FormatFacts(new ArtworkFacts(
            Title: "only title", ArtworkId: null, UserName: null, UserId: null, CreateDate: null, Tags: null, ViewCount: null, LikeCount: null, BookmarkCount: null, PageCount: null, XRestrict: null, AiType: null, Caption: null), Now);
        Assert.Equal("Current artwork (from pixiv):\n- Title: only title", t);
    }

    [Fact]
    public void Rating_And_AiType_Mapping()
    {
        ArtworkFacts F(int? xr, int? ai) => new(null, null, null, null, null, null, null, null, null, null, xr, ai, Caption: null);
        Assert.Contains("All ages", HoshiPrompt.FormatFacts(F(0, null), Now));
        Assert.Contains("R-18G", HoshiPrompt.FormatFacts(F(2, null), Now));
        Assert.Contains("AI-generated: no", HoshiPrompt.FormatFacts(F(null, 1), Now));
        Assert.DoesNotContain("AI-generated", HoshiPrompt.FormatFacts(F(null, 5), Now));
    }

    [Fact]
    public void Caption_Truncates_At_500()
    {
        var f = new ArtworkFacts(null, null, null, null, null, null, null, null, null, null, null, null, new string('x', 600));
        var t = HoshiPrompt.FormatFacts(f, Now);
        Assert.Contains(new string('x', 500) + "…", t);
        Assert.DoesNotContain(new string('x', 501), t);
    }

    [Fact]
    public void Tagger_Line_Only_When_Tags_Given()
    {
        Assert.Contains("Tags detected in the image by a local tagger: cat, dog",
            HoshiPrompt.BuildSystemPrompt(null, ["cat", "dog"], false, Now));
        Assert.DoesNotContain("local tagger",
            HoshiPrompt.BuildSystemPrompt(null, null, false, Now));
    }

    [Fact]
    public void VisibleDelta_PerRound_Streaming()
    {
        // Round boundary: counter resets, so a new round emits from position 0 — the old
        // shared-buffer bug suppressed round-2 text until it exceeded round-1's length.
        Assert.Equal("abc", HoshiPrompt.VisibleDelta("abc", 0));
        Assert.Equal("bc", HoshiPrompt.VisibleDelta("abc", 1));
        Assert.Equal("", HoshiPrompt.VisibleDelta("abc", 3));
        Assert.Equal("", HoshiPrompt.VisibleDelta("ab", 5));
    }

    [Fact]
    public void Legacy_Migration_Decision()
    {
        Assert.False(HoshiPrompt.ShouldMigrateToLegacyModels(true, ["llava", "llama3.2"])); // already custom
        Assert.False(HoshiPrompt.ShouldMigrateToLegacyModels(false, ["qwen3-vl:4b", "llava", "llama3.2"])); // new model there
        Assert.False(HoshiPrompt.ShouldMigrateToLegacyModels(false, ["llava"])); // only one legacy
        Assert.False(HoshiPrompt.ShouldMigrateToLegacyModels(false, [])); // fresh install
        Assert.True(HoshiPrompt.ShouldMigrateToLegacyModels(false, ["llava:latest", "llama3.2:latest"])); // the target case
    }
}
