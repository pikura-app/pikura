using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Pikura.Avalonia.Services;

/// <summary>pixiv's effect tags (e.g. #pixivHalloweenEffect, #pixivSnow2024) make pixiv animate an
/// effect over the artwork. pixiv's data only carries the tag, so Pikura draws a matching effect.</summary>
public static partial class PixivEffects
{
    private static readonly (string Match, string[] Glyphs)[] Known =
    [
        ("Halloween", ["🎃", "🦇", "👻", "🍬"]),
        ("Fall", ["🍁", "🍂"]),
        ("Autumn", ["🍁", "🍂"]),
        ("Momiji", ["🍁", "🍂"]),
        ("Christmas", ["❄️", "🎄", "⭐"]),
        ("Snow", ["❄️", "❅", "❆"]),
        ("Sakura", ["🌸"]),
        ("Spring", ["🌸", "🌷"]),
        ("Rain", ["💧"]),
        ("Glow", ["✨", "⭐"]),
        ("Firework", ["🎆", "✨"]),
        ("Star", ["⭐", "✨"]),
        ("Valentine", ["💖", "💕"]),
        ("Summer", ["☀️", "🌊"]),
    ];

    [GeneratedRegex(@"^pixiv[A-Z][A-Za-z]*(Effect|\d{4})$")]
    private static partial Regex EffectTagPattern();

    public static string? EffectTag(IEnumerable<string>? tags) =>
        tags?.FirstOrDefault(t => EffectTagPattern().IsMatch(t));

    /// <summary>Glyphs for the artwork's effect tag, or null when it has none.</summary>
    public static string[]? GlyphsFor(IEnumerable<string>? tags)
    {
        if (EffectTag(tags) is not { } tag) return null;
        foreach (var (match, glyphs) in Known)
            if (tag.Contains(match, StringComparison.OrdinalIgnoreCase)) return glyphs;
        return ["✨"];
    }
}
