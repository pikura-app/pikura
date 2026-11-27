using Pikura.Avalonia.Services;
using Xunit;

namespace Pikura.Avalonia.Tests;

public class PixivEffectsTests
{
    [Theory]
    [InlineData("pixivHalloweenEffect", "🎃")]
    [InlineData("pixivFallEffect", "🍁")]
    [InlineData("pixivSnow2024", "❄️")]
    [InlineData("pixivSakuraEffect", "🌸")]
    [InlineData("pixivGlowEffect", "✨")]
    [InlineData("pixivSomethingNewEffect", "✨")]
    public void Effect_Tag_Maps_To_Matching_Glyphs(string tag, string firstGlyph)
    {
        var glyphs = PixivEffects.GlyphsFor(["オリジナル", tag]);
        Assert.NotNull(glyphs);
        Assert.Equal(firstGlyph, glyphs![0]);
        Assert.Equal(tag, PixivEffects.EffectTag(["オリジナル", tag]));
    }

    [Theory]
    [InlineData("オリジナル")]
    [InlineData("pixiv")]
    [InlineData("pixivision")]
    [InlineData("halloween")]
    [InlineData("pixiv100users")]
    public void Artworks_Without_Effect_Tag_Get_No_Effect(string tag) =>
        Assert.Null(PixivEffects.GlyphsFor([tag]));
}
