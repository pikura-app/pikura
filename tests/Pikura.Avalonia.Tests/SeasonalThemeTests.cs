using System.Linq;
using Pikura.Avalonia.Services;
using Pikura.Core.Settings;
using Xunit;

namespace Pikura.Avalonia.Tests;

public class SeasonalThemeTests
{
    [Theory]
    [InlineData(2026, 26)]
    [InlineData(2025, 27)]
    [InlineData(2024, 28)]
    public void Thanksgiving_Is_Fourth_Thursday_Of_November(int year, int day) =>
        Assert.Equal(new DateTime(year, 11, day), SeasonalTheme.ThanksgivingDate(year));

    [Theory]
    [InlineData("2026-01-01", "NewYear")]
    [InlineData("2026-01-15", "Winter")]
    [InlineData("2026-02-14", "Valentines")]
    [InlineData("2026-04-10", "Spring")]
    [InlineData("2026-07-04", "Summer")]
    [InlineData("2026-09-20", "Fall")]
    [InlineData("2026-10-31", "Halloween")]
    [InlineData("2026-11-20", "Thanksgiving")]
    [InlineData("2026-11-27", "Thanksgiving")]
    [InlineData("2026-11-10", "Fall")]
    [InlineData("2026-12-01", "Christmas")]
    [InlineData("2026-12-28", "Winter")]
    [InlineData("2026-12-31", "NewYear")]
    public void Auto_Follows_The_Calendar(string date, string expected) =>
        Assert.Equal(expected, SeasonalTheme.For(
            new AppSettings { SeasonalTheme = "Auto", HolidayTheme = "Auto" }, DateTime.Parse(date))!.Key);

    [Fact]
    public void Season_Only_Ignores_Holidays()
    {
        var settings = new AppSettings { SeasonalTheme = "Auto", HolidayTheme = "Off" };
        Assert.Equal("Fall", SeasonalTheme.For(settings, new DateTime(2026, 11, 26))!.Key);
        Assert.Null(SeasonalTheme.For(new AppSettings { HolidayTheme = "Auto" }, new DateTime(2026, 7, 4)));
    }

    [Fact]
    public void Off_And_Pinned_Settings()
    {
        Assert.Null(SeasonalTheme.For(new AppSettings { SeasonalTheme = "Off" }));
        Assert.Equal("Christmas", SeasonalTheme.For(new AppSettings { HolidayTheme = "Christmas", SeasonalTheme = "Summer" })!.Key);
    }

    [Fact]
    public void Anime_Theme_Wins_Over_Holiday_And_Season() =>
        Assert.Equal("Miku", SeasonalTheme.For(new AppSettings
            { AnimeTheme = "Miku", HolidayTheme = "Christmas", SeasonalTheme = "Winter" })!.Key);

    [Fact]
    public void Colors_From_Picks_The_Chosen_Active_Layer()
    {
        var s = new AppSettings { AnimeTheme = "Miku", HolidayTheme = "Thanksgiving", SeasonalTheme = "Fall", ThemeColorsFrom = "Holiday" };
        Assert.Equal("Thanksgiving", SeasonalTheme.For(s)!.Key);
        Assert.Equal("Miku", SeasonalTheme.Secondary(s, DateTime.Now)!.Key);
        s.HolidayTheme = "Off";
        Assert.Equal("Miku", SeasonalTheme.For(s)!.Key);
    }

    [Fact]
    public void Mixed_Decorations_Favor_The_Color_Theme()
    {
        var s = new AppSettings { AnimeTheme = "Miku", HolidayTheme = "Thanksgiving" };
        var mixed = SeasonalTheme.Decorations(s, DateTime.Now)!;
        Assert.Equal("Miku+Thanksgiving", mixed.Key);
        Assert.Contains("🦃", mixed.Particles);
        var share = mixed.Particles.Count(p => SeasonalTheme.For(s)!.Particles.Contains(p)) / (double)mixed.Particles.Length;
        Assert.InRange(share, 0.5, 0.75);
        Assert.Same(mixed, SeasonalTheme.Decorations(s, DateTime.Now));
        s.ThemeMixDecorations = false;
        Assert.Equal("Miku", SeasonalTheme.Decorations(s, DateTime.Now)!.Key);
    }

    [Theory]
    [InlineData("Quintuplets")]
    [InlineData("Ichika")]
    [InlineData("Nino")]
    [InlineData("MikuNakano")]
    [InlineData("Yotsuba")]
    [InlineData("Itsuki")]
    public void Quintuplet_Themes_Resolve_With_Light_And_Dark_Tones(string key)
    {
        Assert.Contains(key, SeasonalTheme.AnimeOptions);
        var o = SeasonalTheme.For(new AppSettings { AnimeTheme = key, HolidayTheme = "Off", SeasonalTheme = "Off" })!;
        Assert.Equal(key, o.Key);
        Assert.Equal(3, o.Dark.Length);
        Assert.Equal(3, o.Light.Length);
        Assert.NotEmpty(o.Particles);
    }

    [Fact]
    public void Every_Anime_Theme_Has_A_Well_Formed_Corner_Sprite()
    {
        foreach (var key in SeasonalTheme.AnimeOptions.Where(k => k != "Off"))
        {
            var sprite = SeasonalTheme.For(new AppSettings { AnimeTheme = key })!.Sprite;
            Assert.NotNull(sprite);
            Assert.Single(sprite!.Rows.Select(r => r.Length).Distinct());
            Assert.All(sprite.Rows.SelectMany(r => r).Where(c => c != '.'), c => Assert.True(sprite.Palette.ContainsKey(c), $"{key}: '{c}'"));
        }
    }

    [Theory]
    [InlineData("Zenitsu", "Lightning")]
    [InlineData("Rengoku", "Embers")]
    [InlineData("Giyu", "Ripples")]
    [InlineData("DemonSlayer", "Random:")]
    [InlineData("Shanks", "Lightning:#FF4040")]
    [InlineData("Goku", "Embers:#FFD84A")]
    [InlineData("Goku", "DragonBalls")]
    [InlineData("Sasuke", "Lightning")]
    [InlineData("JJK", "Random:")]
    [InlineData("OnePiece", "Random:")]
    public void Anime_Characters_Have_Signature_Effects(string key, string effect)
    {
        var o = SeasonalTheme.For(new AppSettings { AnimeTheme = key })!;
        Assert.Contains(effect, o.Ambient!);
    }
}
