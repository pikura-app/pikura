using System;
using System.Collections.Generic;
using System.Linq;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.Services;

/// <summary>A season or holiday: its accent ramp, three palette tones per variant (window, panels/cards,
/// controls/borders) the app is tinted toward, and the glyphs it drifts across the window. Ambient lists extra
/// animated effects (comma-separated: Lightning, Embers, PinkEmbers, Ripples, Mist, Wind, Sparkles, DragonBalls, Rise; each optionally
/// "Kind:#RRGGBB"), or "Random:" followed by "|"-separated groups, one of which plays at a time.</summary>
public sealed record SeasonalOccasion(
    string Key, string Accent, string Emoji, string[] Dark, string[] Light,
    string[] Particles, double Sway, double Spin, string[] Scenery, PixelSprite? Sprite = null,
    string? Ambient = null);

/// <summary>A small pixel-art figure drawn in a corner: rows of palette keys, '.' is transparent.</summary>
public sealed record PixelSprite(string[] Rows, IReadOnlyDictionary<char, string> Palette);

public static class SeasonalSprites
{
    /// <summary>"Neon": an original cyan music-blob mascot with headphones, for the Miku theme.</summary>
    public static readonly PixelSprite NoteBuddy = new(
    [
        "...........NN...",
        "...........N.N..",
        "...........N....",
        ".........NNN....",
        "....KKKKKKKK....",
        "...KooooooooK...",
        "..PKoCCcCCCoKP..",
        "..PoCCCCCCCCoP..",
        "..PoCKCCCCKCoP..",
        "..PoCBCCCCBCoP..",
        "...oCCCKKCCCo...",
        "...oCCCCCCCCo...",
        "....oCCCCCCo....",
        ".....oo..oo.....",
    ], new Dictionary<char, string>
    {
        ['o'] = "#13807E", ['C'] = "#7FD8DC", ['c'] = "#C8F2F3", ['K'] = "#1C1C22",
        ['P'] = "#E8285A", ['B'] = "#FF8FA3", ['N'] = "#1C1C22",
    });
    /// <summary>Ichika: a film clapperboard.</summary>
    public static readonly PixelSprite Clapper = new(
    [
        "................",
        ".KKKKKKKKKKKKK..",
        ".KWWKKWWKKWWKK..",
        ".KKKKKKKKKKKKK..",
        ".KKKKKKKKKKKKK..",
        ".KWWKKWWKKWWKK..",
        ".KKKKKKKKKKKKK..",
        ".KYYYYYYYYYYYK..",
        ".KYyyyyYYYYYYK..",
        ".KYYYYYYYYYYYK..",
        ".KYyyyyyyYYYYK..",
        ".KYYYYYYYYYYYK..",
        ".KKKKKKKKKKKKK..",
    ], new Dictionary<char, string>
    {
        ['K'] = "#1C1C22", ['W'] = "#F4F4F4", ['Y'] = "#F2C94C", ['y'] = "#8A6A10",
    });

    /// <summary>Nino: a butterfly ribbon.</summary>
    public static readonly PixelSprite Butterfly = new(
    [
        ".PP..........PP.",
        "PppP........PppP",
        "PpppP..KK..PpppP",
        "PppppP.KK.PppppP",
        "PpppppPKKPpppppP",
        ".PpppppKKpppppP.",
        "..PPPPPKKPPPPP..",
        "..PppPPKKPPppP..",
        ".PppppPKKPppppP.",
        ".PppP..KK..PppP.",
        "..PP...KK...PP..",
    ], new Dictionary<char, string>
    {
        ['P'] = "#7E3FB8", ['p'] = "#D7B4F0", ['K'] = "#2A1A3A",
    });

    /// <summary>Miku Nakano: headphones.</summary>
    public static readonly PixelSprite Headphones = new(
    [
        "....KKKKKKKK....",
        "..KKbbbbbbbbKK..",
        ".KbbKKKKKKKKbbK.",
        ".KbK........KbK.",
        "KbK..........KbK",
        "KbK..........KbK",
        "KbK..........KbK",
        "KKBBK......KBBKK",
        "KBBBBK....KBBBBK",
        "KBbbBK....KBbbBK",
        "KBbbBK....KBbbBK",
        "KBBBBK....KBBBBK",
        ".KKKK......KKKK.",
    ], new Dictionary<char, string>
    {
        ['K'] = "#1C1C22", ['B'] = "#3A6FD8", ['b'] = "#9DBCF2",
    });

    /// <summary>Yotsuba: a four-leaf clover.</summary>
    public static readonly PixelSprite Clover = new(
    [
        "...ooo....ooo...",
        "..oGgGo..oGgGo..",
        ".oGgggGooGgggGo.",
        ".oGGgGGooGGgGGo.",
        "..oGGGGooGGGGo..",
        "...oooo..oooo...",
        "..oGGGGooGGGGo..",
        ".oGGgGGooGGgGGo.",
        ".oGgggGooGgggGo.",
        "..oGgGo..oGgGo..",
        "...ooo.oo.ooo...",
        ".......oo.......",
        "........oo......",
        ".........oo.....",
    ], new Dictionary<char, string>
    {
        ['o'] = "#1E6B26", ['G'] = "#3FAF4A", ['g'] = "#9BE08F",
    });

    /// <summary>Itsuki: a star hair clip.</summary>
    public static readonly PixelSprite Star = new(
    [
        ".......RR.......",
        "......RYYR......",
        "......RYYR......",
        ".....RYyYYR.....",
        "RRRRRRYyYYRRRRRR",
        ".RYYYYYyYYYYYYR.",
        "..RYYYYYYYYYYR..",
        "...RYYYYYYYYR...",
        "....RYYYYYYR....",
        "...RYYYYYYYYR...",
        "...RYYYRRYYYR...",
        "..RYYRR..RRYYR..",
        "..RYR......RYR..",
        ".RR..........RR.",
    ], new Dictionary<char, string>
    {
        ['R'] = "#C0392B", ['Y'] = "#F2C94C", ['y'] = "#FFF0A0",
    });

    /// <summary>All five: a bouquet in the five sisters' colors.</summary>
    public static readonly PixelSprite Bouquet = new(
    [
        "....YY....PP....",
        "...YYYY..PPPP...",
        "...YYYYBBPPPP...",
        "....YYBBBBPP....",
        "..GG..BBBB..RR..",
        ".GGGG..BB..RRRR.",
        ".GGGG......RRRR.",
        "..GGs.s..s.sRR..",
        "....s.s..s.s....",
        "....WWWWWWWW....",
        ".....WWWWWW.....",
        "......WWWW......",
        ".......WW.......",
    ], new Dictionary<char, string>
    {
        ['Y'] = "#F2C94C", ['P'] = "#9B59D0", ['B'] = "#3A6FD8", ['G'] = "#3FAF4A", ['R'] = "#D04848", ['s'] = "#1E6B26", ['W'] = "#F4A6C0",
    });
    /// <summary>Tanjiro: a green and black checkered pattern.</summary>
    public static readonly PixelSprite Checker = new(
    [
        "GGKKGGKKGGKKGGKK",
        "GGKKGGKKGGKKGGKK",
        "KKGGKKGGKKGGKKGG",
        "KKGGKKGGKKGGKKGG",
        "GGKKGGKKGGKKGGKK",
        "GGKKGGKKGGKKGGKK",
        "KKGGKKGGKKGGKKGG",
        "KKGGKKGGKKGGKKGG",
        "GGKKGGKKGGKKGGKK",
        "GGKKGGKKGGKKGGKK",
        "KKGGKKGGKKGGKKGG",
        "KKGGKKGGKKGGKKGG",
    ], new Dictionary<char, string>
    {
        ['G'] = "#2E8A4E", ['K'] = "#1C1C22",
    });

    /// <summary>Nezuko: a bamboo stalk with a pink ribbon.</summary>
    public static readonly PixelSprite Bamboo = new(
    [
        "......dGGd......",
        "......GggG......",
        "......GggG...GG.",
        "......GggG.GGg..",
        "......dddd.Gg...",
        "......GggG......",
        "....PPPPPPPP....",
        "...PPPdGGdPPP...",
        "....PPGggGPP....",
        "......GggG......",
        "......dddd......",
        ".GG...GggG......",
        "..gGG.GggG......",
        "...Gg.GggG......",
        "......dGGd......",
    ], new Dictionary<char, string>
    {
        ['d'] = "#2E5A1E", ['G'] = "#5FA040", ['g'] = "#A8D88A", ['P'] = "#F08AB0",
    });

    /// <summary>Zenitsu: a lightning bolt.</summary>
    public static readonly PixelSprite Bolt = new(
    [
        ".........oooo...",
        "........oYYo....",
        ".......oYyYo....",
        "......oYyYo.....",
        ".....oYyYo......",
        "....oYyYYoooo...",
        "...oYYYYYYYYo...",
        "...ooooYyYYo....",
        "......oYyYo.....",
        ".....oYyYo......",
        "....oYyYo.......",
        "...oYYo.........",
        "..oYo...........",
        "..oo............",
    ], new Dictionary<char, string>
    {
        ['o'] = "#8A6A10", ['Y'] = "#F2C94C", ['y'] = "#FFF3B0",
    });

    /// <summary>Inosuke: a mountain pine.</summary>
    public static readonly PixelSprite Pine = new(
    [
        ".......o.......",
        "......oGo......",
        ".....oGgGo.....",
        "....oGGgGGo....",
        "....ooGGGoo....",
        ".....oGgGo.....",
        "...oGGGgGGGo...",
        "..oGGGGgGGGGo..",
        "...oooGGGooo...",
        "..oGGGGgGGGGo..",
        ".oGGGGGgGGGGGo.",
        "ooooooBBBoooooo",
        "......BBB......",
        "......BBB......",
    ], new Dictionary<char, string>
    {
        ['o'] = "#1E4A2A", ['G'] = "#3F7F4A", ['g'] = "#8FC08A", ['B'] = "#7A5230",
    });

    /// <summary>Giyu: a water drop.</summary>
    public static readonly PixelSprite Drop = new(
    [
        ".......oo.......",
        "......oBBo......",
        "......oBBo......",
        ".....oBBBBo.....",
        "....oBBBBBBo....",
        "...oBBwBBBBBo...",
        "..oBBwbBBBBBBo..",
        "..oBBwbBBBBBBo..",
        "..oBBbBBBBBBBo..",
        "..oBBBBBBBBBBo..",
        "...oBBBBBBBBo...",
        "....oBBBBBBo....",
        ".....oooooo.....",
    ], new Dictionary<char, string>
    {
        ['o'] = "#163A70", ['B'] = "#2E5FB8", ['b'] = "#8FB4EC", ['w'] = "#EAF2FF",
    });

    /// <summary>Rengoku: a flame.</summary>
    public static readonly PixelSprite Flame = new(
    [
        ".......R........",
        "......RR....R...",
        "......ROR..RR...",
        ".R...ROOR.ROR...",
        ".RR..ROOORROR...",
        ".ROR.ROYOOOOR...",
        ".ROORROYYOOOR...",
        ".ROOOOYYYYOOR...",
        "..ROOYYYYYYOR...",
        "..ROYYYYYYYOR...",
        "...ROYYYYYOR....",
        "....RROOOOR.....",
        ".....RRRRR......",
    ], new Dictionary<char, string>
    {
        ['R'] = "#C8381E", ['O'] = "#F08A24", ['Y'] = "#F6D04A",
    });

    /// <summary>Tengen: a gemstone.</summary>
    public static readonly PixelSprite Gem = new(
    [
        "...ooooooo...",
        "..oDdDdDdDo..",
        ".oDDdDDDdDDo.",
        "ooooooooooooo",
        "oDDDdDDDdDDDo",
        ".oDDDdDdDDDo.",
        "..oDDDdDDDo..",
        "...oDDdDDo...",
        "....oDdDo....",
        ".....oDo.....",
        "......o......",
    ], new Dictionary<char, string>
    {
        ['o'] = "#3A2A6A", ['D'] = "#8E6BE8", ['d'] = "#D4C4FA",
    });

    /// <summary>Mitsuri: a heart.</summary>
    public static readonly PixelSprite Heart = new(
    [
        ".ooo...ooo.",
        "oPpPo.oPPPo",
        "oPppPoPPPPo",
        "oPpPPPPPPPo",
        "oPPPPPPPPPo",
        ".oPPPPPPPo.",
        "..oPPPPPo..",
        "...oPPPo...",
        "....oPo....",
        ".....o.....",
    ], new Dictionary<char, string>
    {
        ['o'] = "#8A2050", ['P'] = "#E85A9A", ['p'] = "#F8B4D2",
    });

    /// <summary>Muichiro: a mist cloud.</summary>
    public static readonly PixelSprite Cloud = new(
    [
        "......oooo......",
        "....ooccwwoo....",
        "...ocwwwwwwco...",
        ".oocwwwwwwwwcoo.",
        "ocwwwwwwwwwwwwco",
        "ocwwwwwwwwwwwwco",
        ".occcccccccccco.",
        "..oooooooooooo..",
    ], new Dictionary<char, string>
    {
        ['o'] = "#3A7A88", ['c'] = "#A8D8E0", ['w'] = "#EEF6F8",
    });

    /// <summary>Gyomei: a ring of prayer beads.</summary>
    public static readonly PixelSprite Beads = new(
    [
        "................",
        ".....BBBbbb.....",
        "...bbBBBbbbBB...",
        "..bbbBB..bbBBB..",
        "..bbb......BBB..",
        ".BBB........bbb.",
        ".BBB........bbb.",
        ".BB..........bb.",
        ".bb..........BB.",
        ".bbb........BBB.",
        ".bbb........BBB.",
        "..BBB......bbb..",
        "..BBBbb..BBbbb..",
        "...BBbbbBBBbb...",
        ".....bbbBBB.....",
        "................",
    ], new Dictionary<char, string>
    {
        ['B'] = "#7A5230", ['b'] = "#C8A070",
    });

    /// <summary>Sanemi: gusts of wind.</summary>
    public static readonly PixelSprite Wind = new(
    [
        "....oooooooo....",
        "..oo........o...",
        "............o...",
        "..oooooooooo....",
        "................",
        "oooooooooooooo..",
        "..............o.",
        "...........oo.o.",
        "..........o..o..",
        "...........oo...",
        "................",
        "...ggggggggg....",
        ".gg.........g...",
        "............g...",
        "..........gg....",
    ], new Dictionary<char, string>
    {
        ['o'] = "#4A7A54", ['g'] = "#A8CCA4",
    });

    /// <summary>Obanai: black and white stripes.</summary>
    public static readonly PixelSprite Stripes = new(
    [
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
        "KKWWKKWWKKWWKK",
    ], new Dictionary<char, string>
    {
        ['K'] = "#2A2A32", ['W'] = "#EEEEF2",
    });

    /// <summary>All characters: a sword with a red guard.</summary>
    public static readonly PixelSprite Katana = new(
    [
        "..............sS",
        ".............sS.",
        "............sS..",
        "...........sS...",
        "..........sS....",
        ".........sS.....",
        "........sS......",
        ".......sS.......",
        "......sS........",
        "....RRS.........",
        "...RRRR.........",
        "...RRRR.........",
        "..KKRR..........",
        ".WK.............",
        "KK..............",
        "W...............",
    ], new Dictionary<char, string>
    {
        ['S'] = "#D8DDE4", ['s'] = "#8A939E", ['R'] = "#C0392B", ['K'] = "#1C1C22", ['W'] = "#EEEEF2",
    });

    public static readonly PixelSprite KanaoButterfly = Butterfly with
    {
        Palette = new Dictionary<char, string> { ['P'] = "#D04A8A", ['p'] = "#F8C4DC", ['K'] = "#3A1A2A" },
    };

    public static readonly PixelSprite ShinobuButterfly = Butterfly with
    {
        Palette = new Dictionary<char, string> { ['P'] = "#6A3AA8", ['p'] = "#A8E0D4", ['K'] = "#2A1A3A" },
    };
    /// <summary>Dragon Ball: an orange orb with red stars.</summary>
    public static readonly PixelSprite Orb = new(
    [
        "....oooooo....",
        "..ooOOOOOOoo..",
        ".oOOhhOOOOOOo.",
        ".oOhhOOOOOOOo.",
        "oOOhOOORROOOOo",
        "oOOOOORRRROOOo",
        "oOOORRRRRRRROo",
        "oOOOORRRRRROOo",
        "oOOOORRRRRROOo",
        "oOOOORROORROOo",
        ".oOOOOOOOOOOo.",
        ".oOOOOOOOOOOo.",
        "..ooOOOOOOoo..",
        "....oooooo....",
    ], new Dictionary<char, string> { ['o'] = "#B05A10", ['O'] = "#F5A030", ['h'] = "#FFE0A0", ['R'] = "#D02020" });

    /// <summary>A swirl.</summary>
    public static readonly PixelSprite Spiral = new(
    [
        "....SSSSSS....",
        "..SS......SS..",
        ".S..SSSSSS..S.",
        "S..S......S..S",
        "S.S..SSSS..S.S",
        "S.S.S....S.S.S",
        "S.S.S.SS.S.S.S",
        "S.S.S..S.S.S.S",
        "S.S..SS..S.S.S",
        "S..S....S..S.S",
        ".S..SSSS..S..S",
        "..SS.....S..S.",
        "....SSSSS..S..",
        "..........S...",
    ], new Dictionary<char, string> { ['S'] = "#F08A24" });

    /// <summary>A straw hat with a red band.</summary>
    public static readonly PixelSprite StrawHat = new(
    [
        ".....YYYYYY.....",
        "....YyYYYYyY....",
        "....YYYYYYYY....",
        "....RRRRRRRR....",
        "....RRRRRRRR....",
        ".YYYYYYYYYYYYYY.",
        "YyYYYYYYYYYYYyYY",
        ".YYYYYYYYYYYYYY.",
        "..oooooooooooo..",
    ], new Dictionary<char, string> { ['Y'] = "#E8C05A", ['y'] = "#FFF0B0", ['R'] = "#C8281E", ['o'] = "#9A7A30" });

    /// <summary>A skull and crossbones.</summary>
    public static readonly PixelSprite Skull = new(
    [
        "....WWWWWW....",
        "..WWWWWWWWWW..",
        ".WWWWWWWWWWWW.",
        ".WWKKWWWWKKWW.",
        ".WKKKKWWKKKKW.",
        ".WKKKKWWKKKKW.",
        ".WWKKWWWWKKWW.",
        "..WWWWKKWWWW..",
        "...WWWWWWWW...",
        "...WKWKWKWW...",
        "....WWWWWW....",
        "bb..........bb",
        "..bb......bb..",
        "....bbbbbb....",
    ], new Dictionary<char, string> { ['W'] = "#F2F2EE", ['K'] = "#1C1C22", ['b'] = "#D8D4C8" });

    /// <summary>A throwing knife with a ring.</summary>
    public static readonly PixelSprite Kunai = new(
    [
        "...............S",
        ".............sS.",
        "............sSs.",
        "...........sSs..",
        "..........sSs...",
        ".........sSs....",
        ".........Ss.....",
        "........K.......",
        ".......W........",
        "......K.........",
        ".....W..........",
        "....K...........",
        "..RR............",
        ".R..R...........",
        ".R..R...........",
        "..RR............",
    ], new Dictionary<char, string> { ['S'] = "#D8DDE4", ['s'] = "#8A939E", ['K'] = "#1C1C22", ['W'] = "#EEEEF2", ['R'] = "#8A939E" });

    /// <summary>A red eye with three black marks.</summary>
    public static readonly PixelSprite Eye = new(
    [
        "....oooooo....",
        "..ooRRRRRRoo..",
        ".oRRRRKRRRRRo.",
        ".oRRRRKKRRRRo.",
        "oRKKRRRRRRRRRo",
        "oRKRRRooRRRRRo",
        "oRRRRoKKoRRRRo",
        "oRRRRoKKoRRKRo",
        "oRRRRRooRRKKRo",
        "oRRRRRRRRRRRRo",
        ".oRRRKKRRRRRo.",
        ".oRRRRKRRRRRo.",
        "..ooRRRRRRoo..",
        "....oooooo....",
    ], new Dictionary<char, string> { ['o'] = "#1C1C22", ['R'] = "#D02828", ['K'] = "#1C1C22" });

    /// <summary>A leaf.</summary>
    public static readonly PixelSprite Leaf = new(
    [
        "..........oo..",
        ".......ooGGo..",
        ".....ooGGGgo..",
        "....oGGGGgGo..",
        "...oGGGGgGGo..",
        "..oGGGGgGGGo..",
        "..oGGGgGGGo...",
        ".oGGGgGGGGo...",
        ".oGGgGGGGo....",
        ".oGgGGGGo.....",
        ".ogGGGoo......",
        "o.ooo.........",
    ], new Dictionary<char, string> { ['o'] = "#1E5A26", ['G'] = "#4FA050", ['g'] = "#A8E09A" });

    /// <summary>A rice ball.</summary>
    public static readonly PixelSprite Onigiri = new(
    [
        "......WW......",
        ".....WWWW.....",
        "....WWWWWW....",
        "...WWWWWWWW...",
        "...WWWWWWWW...",
        "..WWWWWWWWWW..",
        "..WWWWWWWWWW..",
        ".WWWWKKKKWWWW.",
        ".WWWKKKKKKWWW.",
        "WWWWKKKKKKWWWW",
        "WWWWKKKKKKWWWW",
        ".WWWKKKKKKWWW.",
    ], new Dictionary<char, string> { ['W'] = "#F4F4EE", ['K'] = "#1E3A28" });

    /// <summary>A paw print.</summary>
    public static readonly PixelSprite Paw = new(
    [
        "...PP....PP...",
        "..PPPP..PPPP..",
        "..PPPP..PPPP..",
        "...PP....PP...",
        "PP..........PP",
        "PPP........PPP",
        ".PP..PPPP..PP.",
        "....PPPPPP....",
        "...PPPPPPPP...",
        "...PPPPPPPP...",
        "...PPPPPPPP...",
        "....PPPPPP....",
    ], new Dictionary<char, string> { ['P'] = "#2A2A32" });

    /// <summary>A hammer.</summary>
    public static readonly PixelSprite Hammer = new(
    [
        "..ssssssssss..",
        ".sSSSSSSSSSSs.",
        ".sSSSSSSSSSSs.",
        "..ssssssssss..",
        "......BB......",
        "......BB......",
        "......BB......",
        "......BB......",
        "......BB......",
        "......bb......",
        "......BB......",
        "......bb......",
        "......BB......",
    ], new Dictionary<char, string> { ['s'] = "#5A606A", ['S'] = "#A8B0BA", ['B'] = "#8A5A30", ['b'] = "#C8382E" });

    /// <summary>A capsule.</summary>
    public static readonly PixelSprite Capsule = new(
    [
        "...oooooooo...",
        ".ooWWWWRRRRoo.",
        "oWWWwWWRRRRRRo",
        "oWWWWWWRRRRRRo",
        "oWWWWWWRRRRRRo",
        "oWWWWWWRRRRRRo",
        ".ooWWWWRRRRoo.",
        "...oooooooo...",
    ], new Dictionary<char, string> { ['o'] = "#2A3A4A", ['W'] = "#F2F4F6", ['w'] = "#FFFFFF", ['R'] = "#2E8AC8" });

    public static readonly PixelSprite JJKEye = Eye with { Palette = new Dictionary<char, string> { ['o'] = "#1C1C22", ['R'] = "#6A4AC8", ['K'] = "#1C1C22" } };

    public static readonly PixelSprite GojoEye = Eye with { Palette = new Dictionary<char, string> { ['o'] = "#1C1C22", ['R'] = "#3AA8F0", ['K'] = "#E8F6FF" } };

    public static readonly PixelSprite YujiStar = Star with { Palette = new Dictionary<char, string> { ['R'] = "#1C1C22", ['Y'] = "#D82E2E", ['y'] = "#FF8A8A" } };

    public static readonly PixelSprite MegumiPaw = Paw with { Palette = new Dictionary<char, string> { ['P'] = "#3A4270" } };

    public static readonly PixelSprite MakiKatana = Katana with { Palette = new Dictionary<char, string> { ['S'] = "#D8DDE4", ['s'] = "#8A939E", ['R'] = "#2E8A4E", ['K'] = "#1C1C22", ['W'] = "#EEEEF2" } };

    public static readonly PixelSprite YutaKatana = Katana with { Palette = new Dictionary<char, string> { ['S'] = "#D8DDE4", ['s'] = "#8A939E", ['R'] = "#3A6AC8", ['K'] = "#F2F2F6", ['W'] = "#EEEEF2" } };

    public static readonly PixelSprite SukunaFlame = Flame with { Palette = new Dictionary<char, string> { ['R'] = "#5A0A10", ['O'] = "#B01820", ['Y'] = "#F05040" } };

    public static readonly PixelSprite NarutoSpiral = Spiral with { Palette = new Dictionary<char, string> { ['S'] = "#F08A24" } };

    public static readonly PixelSprite ItachiEye = Eye with { Palette = new Dictionary<char, string> { ['o'] = "#1C1C22", ['R'] = "#A81820", ['K'] = "#1C1C22" } };

    public static readonly PixelSprite SakuraClover = Clover with { Palette = new Dictionary<char, string> { ['o'] = "#B04A78", ['G'] = "#F08AB8", ['g'] = "#FFD4E6" } };

    public static readonly PixelSprite KakashiBolt = Bolt with { Palette = new Dictionary<char, string> { ['o'] = "#3A5A8A", ['Y'] = "#9AC0F0", ['y'] = "#EAF4FF" } };

    public static readonly PixelSprite HinataDrop = Drop with { Palette = new Dictionary<char, string> { ['o'] = "#4A3A7A", ['B'] = "#9A88D8", ['b'] = "#D4CCF4", ['w'] = "#EAF2FF" } };

    public static readonly PixelSprite GaaraCloud = Cloud with { Palette = new Dictionary<char, string> { ['o'] = "#8A6A3A", ['c'] = "#D8B878", ['w'] = "#F4E4C0" } };

    public static readonly PixelSprite JiraiyaHeart = Heart with { Palette = new Dictionary<char, string> { ['o'] = "#6A1414", ['P'] = "#C82828", ['p'] = "#F08A8A" } };

    public static readonly PixelSprite ZoroKatana = Katana with { Palette = new Dictionary<char, string> { ['S'] = "#D8DDE4", ['s'] = "#8A939E", ['R'] = "#2E8A4E", ['K'] = "#E8E8EE", ['W'] = "#EEEEF2" } };

    public static readonly PixelSprite NamiOrb = Orb with { Palette = new Dictionary<char, string> { ['o'] = "#B05A10", ['O'] = "#F5A030", ['h'] = "#FFE0A0", ['R'] = "#F5A030" } };

    public static readonly PixelSprite UsoppStar = Star with { Palette = new Dictionary<char, string> { ['R'] = "#7A5230", ['Y'] = "#E8B850", ['y'] = "#FFF0A0" } };

    public static readonly PixelSprite SanjiFlame = Flame with { Palette = new Dictionary<char, string> { ['R'] = "#B0281A", ['O'] = "#F05A30", ['Y'] = "#FFB070" } };

    public static readonly PixelSprite ChopperHat = StrawHat with { Palette = new Dictionary<char, string> { ['Y'] = "#F08AB0", ['y'] = "#FFD0E2", ['R'] = "#F2F2F6", ['o'] = "#A04A70" } };

    public static readonly PixelSprite RobinClover = Clover with { Palette = new Dictionary<char, string> { ['o'] = "#5A3A7A", ['G'] = "#A878D8", ['g'] = "#E4D0F8" } };

    public static readonly PixelSprite FrankyStar = Star with { Palette = new Dictionary<char, string> { ['R'] = "#1E5A9A", ['Y'] = "#4AB0F0", ['y'] = "#C8ECFF" } };

    public static readonly PixelSprite BrookSkull = Skull with { Palette = new Dictionary<char, string> { ['W'] = "#F2F2EE", ['K'] = "#1C1C22", ['b'] = "#8A6AA8" } };

    public static readonly PixelSprite ShanksKatana = Katana with { Palette = new Dictionary<char, string> { ['S'] = "#D8DDE4", ['s'] = "#8A939E", ['R'] = "#C82828", ['K'] = "#3A1418", ['W'] = "#EEEEF2" } };

    public static readonly PixelSprite VegetaGem = Gem with { Palette = new Dictionary<char, string> { ['o'] = "#1A2A6A", ['D'] = "#3A6AE0", ['d'] = "#C8D8FF" } };

    public static readonly PixelSprite GohanBolt = Bolt with { Palette = new Dictionary<char, string> { ['o'] = "#8A6A10", ['Y'] = "#F2C94C", ['y'] = "#FFF3B0" } };

    public static readonly PixelSprite PiccoloSpiral = Spiral with { Palette = new Dictionary<char, string> { ['S'] = "#E8D84A" } };

    public static readonly PixelSprite TrunksKatana = Katana with { Palette = new Dictionary<char, string> { ['S'] = "#D8DDE4", ['s'] = "#8A939E", ['R'] = "#7A4AC8", ['K'] = "#1C1C22", ['W'] = "#EEEEF2" } };

    public static readonly PixelSprite FriezaGem = Gem with { Palette = new Dictionary<char, string> { ['o'] = "#4A1A5A", ['D'] = "#C070E0", ['d'] = "#F4E0FA" } };

    public static readonly PixelSprite CellGem = Gem with { Palette = new Dictionary<char, string> { ['o'] = "#1A4A24", ['D'] = "#4AB060", ['d'] = "#C8F0C8" } };

    public static readonly PixelSprite BuuCloud = Cloud with { Palette = new Dictionary<char, string> { ['o'] = "#B04A80", ['c'] = "#F59AC8", ['w'] = "#FFE0F0" } };

    public static readonly PixelSprite KrillinOrb = Orb with { Palette = new Dictionary<char, string> { ['o'] = "#B05A10", ['O'] = "#F5A030", ['h'] = "#FFE0A0", ['R'] = "#F5A030" } };
}

/// <summary>Optional season and holiday theming. Each is "Off", "Auto" (follows the calendar) or
/// pinned to one occasion; an active holiday takes precedence over the season.</summary>
public static class SeasonalTheme
{
    public static readonly string[] SeasonOptions = ["Off", "Auto", "Spring", "Summer", "Fall", "Winter"];
    public static readonly string[] AnimeOptions = ["Off", "Miku", "Quintuplets", "Ichika", "Nino", "MikuNakano", "Yotsuba", "Itsuki", "DemonSlayer", "Tanjiro", "Nezuko", "Zenitsu", "Inosuke", "Kanao", "Giyu", "Shinobu", "Rengoku", "Tengen", "Mitsuri", "Muichiro", "Gyomei", "Sanemi", "Obanai", "JJK", "Yuji", "Megumi", "Nobara", "Gojo", "Maki", "Inumaki", "Panda", "Yuta", "Sukuna", "Naruto", "NarutoUzumaki", "Sasuke", "SakuraHaruno", "Kakashi", "Itachi", "Hinata", "Gaara", "Shikamaru", "Jiraiya", "OnePiece", "Luffy", "Zoro", "Nami", "Usopp", "Sanji", "Chopper", "Robin", "Franky", "Brook", "Jinbe", "Ace", "Shanks", "DBZ", "Goku", "Vegeta", "Gohan", "Piccolo", "Trunks", "Frieza", "Cell", "Buu", "Bulma", "Krillin"];
    public static readonly string[] HolidayOptions = ["Off", "Auto", "NewYear", "Valentines", "Halloween", "Thanksgiving", "Christmas"];

    private static readonly Dictionary<string, SeasonalOccasion> Occasions = new()
    {
        ["Spring"] = new("Spring", "Green", "🌸", ["#2E4E34", "#2A4430", "#6A3A52"], ["#D4EFD0", "#E6F5E2", "#F8D0E0"],
            ["🌸", "🌷", "🦋", "🌼"], 0.8, 1.0,
            ["🌷", "🌸", "🌼", "🌱", "🦋"]),
        ["Summer"] = new("Summer", "Teal", "☀️", ["#0E4A55", "#0F3F4A", "#5A4A18"], ["#BDE9F0", "#D8F2F5", "#FFE6A8"],
            ["☀️", "🍉", "🐚", "🌴", "🌊"], 0.5, 0.4,
            ["🌴", "🐚", "🍉", "🌺", "🌊"]),
        ["Fall"] = new("Fall", "Orange", "🍁", ["#6B3A1C", "#7E3420", "#94561E"], ["#E9A15A", "#F5C98E", "#E58E4A"],
            ["🍁", "🍂", "🌰", "🍄", "🍁"], 1.0, 1.2,
            ["🎃", "🍁", "🍂", "🌾", "🍄", "🌰"]),
        ["Winter"] = new("Winter", "Blue", "⛄", ["#1E3550", "#24405E", "#2E5478"], ["#CFE2F6", "#E6F0FB", "#B7D3EE"],
            ["❄️", "❄️", "☃️", "🧣"], 0.4, 0.3,
            ["⛄", "🌲", "❄️", "🧣", "🌲"]),
        ["NewYear"] = new("NewYear", "Purple", "🎆", ["#1A1636", "#221C44", "#5A4A1C"], ["#DCD0F4", "#ECE4FA", "#F2DFA8"],
            ["🎆", "🎉", "✨", "🥂", "🎊"], 0.3, 0.4,
            ["🎆", "🎉", "🎊", "🥂", "✨"]),
        ["Valentines"] = new("Valentines", "Pink", "💝", ["#5A1E36", "#6E2440", "#8A3050"], ["#F6C8D8", "#FBE0EA", "#F0A8C0"],
            ["💖", "💝", "🌹", "💕"], 0.4, 0.3,
            ["💐", "💖", "🌹", "💝", "🌷"]),
        ["Halloween"] = new("Halloween", "Orange", "🎃", ["#5A2A0E", "#3E1F4E", "#8A4310"], ["#F2A05A", "#F7C99A", "#C9A0E0"],
            ["🎃", "👻", "🦇", "🍬", "🕸️"], 0.6, 0.6,
            ["🎃", "👻", "🕸️", "🦇", "🍬", "🎃"]),
        ["Thanksgiving"] = new("Thanksgiving", "Orange", "🦃", ["#6E3B17", "#7A2E1C", "#9A5B1C"], ["#E39A52", "#F3C68C", "#D9793E"],
            ["🦃", "🥧", "🌽", "🍂", "🍁", "🌰", "🍗"], 0.9, 1.0,
            ["🎃", "🦃", "🌽", "🥧", "🍂", "🍁", "🌾"]),
        ["Miku"] = new("Miku", "Cyan", "🎤", ["#05313D", "#073C4A", "#6A1E48"], ["#C2EDF5", "#DCF6FB", "#F6C4DA"],
            ["🎵", "🎶", "🥬", "💙", "🎤"], 0.5, 0.6,
            ["🎵", "🎶", "🥬", "🎤", "🎧", "💙"], SeasonalSprites.NoteBuddy),
        // The Quintessential Quintuplets: each sister's image color and motifs; "Quintuplets" mixes all five.
        ["Quintuplets"] = new("Quintuplets", "Pink", "💐", ["#2E1E36", "#36243F", "#4A2A3E"], ["#F4DCE6", "#FAEAF0", "#E6DCF4"],
            ["🎬", "🦋", "🎧", "🍀", "⭐"], 0.6, 0.6,
            ["🎬", "🦋", "🎧", "🍀", "⭐", "💐"], SeasonalSprites.Bouquet),
        ["Ichika"] = new("Ichika", "Amber", "🎬", ["#3A3210", "#463C14", "#5A2A3A"], ["#F6EBB8", "#FBF4D6", "#F4C8D4"],
            ["🎬", "⭐", "🌟", "🎭"], 0.5, 0.5,
            ["🎬", "🎭", "⭐", "🌟", "🎥"], SeasonalSprites.Clapper),
        ["Nino"] = new("Nino", "Purple", "🦋", ["#2E1A42", "#3A2050", "#5A1E3A"], ["#E2D2F4", "#EEE4FA", "#F4C8DA"],
            ["🦋", "🎀", "🍰", "🧁"], 0.8, 0.6,
            ["🦋", "🎀", "🍰", "🧁", "🍳"], SeasonalSprites.Butterfly),
        ["MikuNakano"] = new("MikuNakano", "Blue", "🎧", ["#14284A", "#1A3258", "#2E3E5A"], ["#CFDDF4", "#E2EBFA", "#C8D8EC"],
            ["🎧", "🍵", "🍞", "💙"], 0.4, 0.4,
            ["🎧", "🍵", "⚔️", "🏯", "🍞"], SeasonalSprites.Headphones),
        ["Yotsuba"] = new("Yotsuba", "Green", "🍀", ["#1C3A1E", "#224626", "#4A4A14"], ["#D2EFC8", "#E6F7E0", "#F2EBB0"],
            ["🍀", "🎀", "✨", "🍀"], 0.9, 1.0,
            ["🍀", "🎀", "🏅", "⚽", "🍀"], SeasonalSprites.Clover),
        ["Itsuki"] = new("Itsuki", "Red", "⭐", ["#42161C", "#501C24", "#5A3A14"], ["#F4CCCC", "#FAE0E0", "#F6E0B0"],
            ["⭐", "🍙", "🍜", "🍛"], 0.5, 0.6,
            ["⭐", "🍙", "🍜", "🍛", "📚"], SeasonalSprites.Star),
        // Demon Slayer: each character's colors and motifs; "DemonSlayer" mixes the cast.
        ["DemonSlayer"] = new("DemonSlayer", "Red", "🗡️", ["#2A1416", "#341A1C", "#1A2A20"], ["#F0D4D4", "#F8E6E6", "#D4E8D8"],
            ["🗡️", "🎴", "🔥", "🌊", "⚡", "🦋"], 0.5, 0.6,
            ["🗡️", "🎴", "🌸", "🔥", "🌊", "⚡"], SeasonalSprites.Katana, "Random:Ripples,Embers|PinkEmbers|Lightning|Wind|Ripples|Embers|Sparkles|Mist|Wind"),
        ["Tanjiro"] = new("Tanjiro", "Green", "🎴", ["#12291C", "#1A3524", "#3A1418"], ["#D2ECD8", "#E6F5EA", "#F0D0D0"],
            ["💧", "🔥", "☀️", "🌊"], 0.5, 0.5,
            ["🎴", "🌊", "🔥", "☀️", "🗡️"], SeasonalSprites.Checker, "Ripples,Embers"),
        ["Nezuko"] = new("Nezuko", "Pink", "🎋", ["#3A1626", "#461C30", "#3A2A10"], ["#F6D0DE", "#FBE4EC", "#F2E0B8"],
            ["🌸", "🎀", "🎋", "💗"], 0.8, 0.6,
            ["🎋", "🌸", "🎀", "📦", "💗"], SeasonalSprites.Bamboo, "PinkEmbers"),
        ["Zenitsu"] = new("Zenitsu", "Amber", "⚡", ["#3A3010", "#46391A", "#4A2A10"], ["#F7E8A8", "#FBF2CC", "#F6D6A0"],
            ["⚡", "💛", "🌩️", "🐦"], 0.3, 1.2,
            ["⚡", "🌩️", "🐦", "💛"], SeasonalSprites.Bolt, "Lightning"),
        ["Inosuke"] = new("Inosuke", "Teal", "🐗", ["#1E2A3A", "#263446", "#3A2E1E"], ["#D6DEEA", "#E6ECF4", "#E8DCC8"],
            ["🐗", "🍃", "🌲"], 1.0, 1.0,
            ["🐗", "🌲", "🍄", "🍃", "🗡️"], SeasonalSprites.Pine, "Wind"),
        ["Kanao"] = new("Kanao", "Pink", "🦋", ["#3A1A30", "#46203A", "#24303A"], ["#F4D4E8", "#FAE6F2", "#DCE6F0"],
            ["🦋", "🌸", "🪙"], 0.8, 0.5,
            ["🦋", "🌸", "🪙", "🌷"], SeasonalSprites.KanaoButterfly, "Rise"),
        ["Giyu"] = new("Giyu", "Blue", "🌊", ["#10243E", "#16304C", "#3A1A1A"], ["#CCDDF2", "#E2ECF8", "#EED4CC"],
            ["💧", "🌊", "💧"], 0.4, 0.3,
            ["🌊", "💧", "🗡️", "🐚"], SeasonalSprites.Drop, "Ripples"),
        ["Shinobu"] = new("Shinobu", "Purple", "🦋", ["#2A1A3E", "#34204A", "#1A3A34"], ["#E2D4F2", "#EEE6FA", "#CCEDE4"],
            ["🦋", "💜", "🧪"], 0.8, 0.5,
            ["🦋", "🧪", "🌸", "💜"], SeasonalSprites.ShinobuButterfly, "Rise"),
        ["Rengoku"] = new("Rengoku", "Orange", "🔥", ["#4A1E0E", "#5A2810", "#5A4210"], ["#F6C8A0", "#FADCC0", "#F6E0A0"],
            ["🔥", "🍠", "✨"], 0.6, 0.8,
            ["🔥", "🍠", "🍱", "☀️"], SeasonalSprites.Flame, "Embers"),
        ["Tengen"] = new("Tengen", "Indigo", "💎", ["#24183A", "#2E1E48", "#4A3A10"], ["#DCD2F4", "#ECE6FA", "#F6E6A8"],
            ["💎", "💥", "🎆", "✨"], 0.5, 0.8,
            ["💎", "💥", "🎆", "🎵"], SeasonalSprites.Gem, "Sparkles"),
        ["Mitsuri"] = new("Mitsuri", "Pink", "💗", ["#3E1A2E", "#4A1E36", "#1E3A1E"], ["#F6CCE0", "#FBE2EE", "#D4EECC"],
            ["💗", "🍡", "🌸"], 0.8, 0.6,
            ["💗", "🍡", "🌸", "🍰"], SeasonalSprites.Heart, "Rise"),
        ["Muichiro"] = new("Muichiro", "Teal", "☁️", ["#14303A", "#1A3A44", "#2A2A3A"], ["#CCE8EC", "#E2F2F4", "#DCDCEC"],
            ["☁️", "🌫️", "🍃"], 0.9, 0.3,
            ["☁️", "🌫️", "🍃", "🐦"], SeasonalSprites.Cloud, "Mist"),
        ["Gyomei"] = new("Gyomei", "Amber", "📿", ["#2A2620", "#34302A", "#3A2E1A"], ["#E2DCD0", "#EEEAE0", "#E6D6B8"],
            ["🪨", "📿", "🐱"], 0.3, 0.3,
            ["🪨", "📿", "🐱", "⛓️"], SeasonalSprites.Beads),
        ["Sanemi"] = new("Sanemi", "Lime", "🌪️", ["#1E2E24", "#26382C", "#3A2A1A"], ["#DCEAE0", "#EAF4EC", "#EAD8C8"],
            ["🌪️", "💨", "🍃"], 1.2, 1.2,
            ["🌪️", "💨", "🍃", "🍵"], SeasonalSprites.Wind, "Wind"),
        ["Obanai"] = new("Obanai", "Indigo", "🐍", ["#22202E", "#2C2838", "#1A3A2E"], ["#E0DCEC", "#ECEAF4", "#CCEAD8"],
            ["🐍", "🤍", "🌿"], 0.7, 0.5,
            ["🐍", "🤍", "🌿"], SeasonalSprites.Stripes),
        // Jujutsu Kaisen: each character's colors and motifs; "JJK" mixes the cast.
        ["JJK"] = new("JJK", "Indigo", "🤞", ["#161A2E", "#1E2238", "#3A1420"], ["#D8DCEE", "#E8EAF6", "#F0D0D6"],
            ["🤞", "👁️", "💀", "⛩️", "🔥"], 0.6, 0.6,
            ["⛩️", "👁️", "🤞", "📿", "💀"], SeasonalSprites.JJKEye, "Random:Lightning:#FF3B3B|Mist:#8A90B0|Sparkles:#FF9A60|Ripples:#7FB8FF,Sparkles:#A8D8FF|Wind:#A8E0B0|Ripples:#C8B4F0|Sparkles:#E6ECFF|Embers:#C01818,Wind:#E05050"),
        ["Yuji"] = new("Yuji", "Red", "👊", ["#2E1416", "#3A1A1C", "#1A1A2E"], ["#F0D2D2", "#F8E4E4", "#D8DAEE"],
            ["👊", "💥", "🩸"], 0.6, 0.6,
            ["👊", "💥", "🍙", "🎬"], SeasonalSprites.YujiStar, "Lightning:#FF3B3B"),
        ["Megumi"] = new("Megumi", "Indigo", "🐺", ["#14182A", "#1A2034", "#22222E"], ["#D4D8EA", "#E6E8F4", "#DADAE4"],
            ["🐺", "🐸", "🐇", "🌑"], 0.6, 0.6,
            ["🐺", "🦅", "🐍", "🌑"], SeasonalSprites.MegumiPaw, "Mist:#8A90B0"),
        ["Nobara"] = new("Nobara", "Orange", "🔨", ["#2E1A12", "#3A2016", "#2A1428"], ["#F2DACA", "#F8E8DE", "#ECD4E6"],
            ["🔨", "📌", "💅", "✨"], 0.6, 0.6,
            ["🔨", "📌", "🛍️", "💄"], SeasonalSprites.Hammer, "Sparkles:#FF9A60"),
        ["Gojo"] = new("Gojo", "Cyan", "♾️", ["#0E2438", "#122C44", "#1E1E3A"], ["#CCE6F6", "#E2F2FB", "#DCDCF4"],
            ["♾️", "💠", "✨", "🔵"], 0.6, 0.6,
            ["♾️", "🕶️", "🍡", "💠"], SeasonalSprites.GojoEye, "Ripples:#7FB8FF,Sparkles:#A8D8FF"),
        ["Maki"] = new("Maki", "Green", "🗡️", ["#142A1E", "#1A3426", "#2E2A14"], ["#D2EAD8", "#E6F4EA", "#EEE6C8"],
            ["🗡️", "👓", "🍃"], 0.6, 0.6,
            ["🗡️", "👓", "🥋", "🍃"], SeasonalSprites.MakiKatana, "Wind:#A8E0B0"),
        ["Inumaki"] = new("Inumaki", "Purple", "🍙", ["#221A34", "#2A2040", "#1E2A30"], ["#E0D8F2", "#EEEAF8", "#D6E6EC"],
            ["🍙", "🐟", "🗯️"], 0.6, 0.6,
            ["🍙", "🐟", "🗯️", "🎤"], SeasonalSprites.Onigiri, "Ripples:#C8B4F0"),
        ["Panda"] = new("Panda", "Teal", "🐼", ["#1C1E22", "#24272C", "#1E2E24"], ["#E4E6EA", "#F0F1F4", "#D8EAD8"],
            ["🐼", "🎋", "🐾"], 0.6, 0.6,
            ["🐼", "🎋", "🐾"], SeasonalSprites.Paw, "Rise"),
        ["Yuta"] = new("Yuta", "Blue", "💍", ["#141E30", "#1A263A", "#2E2E3A"], ["#D6DEEE", "#E8EDF6", "#E4E4EC"],
            ["💍", "🗡️", "🤍"], 0.6, 0.6,
            ["💍", "🗡️", "🤍", "✨"], SeasonalSprites.YutaKatana, "Sparkles:#E6ECFF"),
        ["Sukuna"] = new("Sukuna", "Red", "👹", ["#2A0E10", "#360F14", "#1A1018"], ["#ECC8CA", "#F6DCDE", "#DCD0DA"],
            ["👹", "🩸", "🔥"], 0.6, 0.6,
            ["👹", "🔥", "⛩️", "🩸"], SeasonalSprites.SukunaFlame, "Embers:#C01818,Wind:#E05050"),
        // Naruto: each character's colors and motifs; "Naruto" mixes the cast.
        ["Naruto"] = new("Naruto", "Orange", "🍥", ["#2E1C10", "#3A2414", "#1A2436"], ["#F4DCC4", "#FAEADC", "#D4DEEE"],
            ["🍥", "🍃", "🍜", "🦊", "🌀"], 0.6, 0.6,
            ["🍥", "🍜", "🦊", "📜", "🍃"], SeasonalSprites.Kunai, "Random:Embers:#FF9A2A,Wind:#FFD0A0|Lightning:#9AB8FF|Sparkles:#FFA0C8|Lightning:#BFD8FF|Embers:#C82828,Mist:#B08890|Ripples:#C8B8F0|Wind:#D8B878|Mist:#9AA88A|Ripples:#9AE0A0"),
        ["NarutoUzumaki"] = new("NarutoUzumaki", "Orange", "🦊", ["#33200E", "#402812", "#1A2436"], ["#F8D8B4", "#FCE8D4", "#D0DCF0"],
            ["🍥", "🍜", "🦊", "🌀"], 0.6, 0.6,
            ["🍥", "🍜", "🦊", "🐸"], SeasonalSprites.NarutoSpiral, "Embers:#FF9A2A,Wind:#FFD0A0"),
        ["Sasuke"] = new("Sasuke", "Indigo", "⚡", ["#16162A", "#1E1E34", "#2E1418"], ["#D8D8EC", "#E8E8F4", "#ECD0D2"],
            ["⚡", "🐍", "🦅"], 0.6, 0.6,
            ["⚡", "🐍", "🦅", "🗡️"], SeasonalSprites.Eye, "Lightning:#9AB8FF"),
        ["SakuraHaruno"] = new("SakuraHaruno", "Pink", "🌸", ["#341A26", "#40202E", "#1E2E22"], ["#F6D4E2", "#FBE6EE", "#D6ECD8"],
            ["🌸", "💮", "💪"], 0.6, 0.6,
            ["🌸", "💮", "🍡", "🩹"], SeasonalSprites.SakuraClover, "Sparkles:#FFA0C8"),
        ["Kakashi"] = new("Kakashi", "Teal", "📙", ["#1A2228", "#222C34", "#2A2A34"], ["#DAE2E8", "#EAF0F4", "#E2E2EA"],
            ["⚡", "📙", "🐕"], 0.6, 0.6,
            ["📙", "⚡", "🐕", "🍆"], SeasonalSprites.KakashiBolt, "Lightning:#BFD8FF"),
        ["Itachi"] = new("Itachi", "Red", "🌙", ["#1E1014", "#28141A", "#1A1A24"], ["#E6D0D4", "#F2E2E4", "#D8D8E2"],
            ["🐦", "🌙", "☁️"], 0.6, 0.6,
            ["☁️", "🌙", "🐦", "🍡"], SeasonalSprites.ItachiEye, "Embers:#C82828,Mist:#B08890"),
        ["Hinata"] = new("Hinata", "Purple", "💜", ["#221E34", "#2A2640", "#20283A"], ["#E2DEF4", "#EFECFA", "#DCE2F2"],
            ["💜", "🌸", "🦁"], 0.6, 0.6,
            ["💜", "🦁", "🌸", "🍥"], SeasonalSprites.HinataDrop, "Ripples:#C8B8F0"),
        ["Gaara"] = new("Gaara", "Amber", "🏺", ["#2E2414", "#3A2E1A", "#2E1414"], ["#EEE0C4", "#F6ECDA", "#EED0CC"],
            ["🏺", "🍂", "❤️"], 0.6, 0.6,
            ["🏺", "🏜️", "🌵", "❤️"], SeasonalSprites.GaaraCloud, "Wind:#D8B878"),
        ["Shikamaru"] = new("Shikamaru", "Green", "🦌", ["#1A2618", "#22301E", "#2A2A1E"], ["#D8E8D2", "#E8F2E4", "#E6E4CE"],
            ["☁️", "🍃", "🦌"], 0.6, 0.6,
            ["🦌", "☁️", "♟️", "🍃"], SeasonalSprites.Leaf, "Mist:#9AA88A"),
        ["Jiraiya"] = new("Jiraiya", "Red", "🐸", ["#2A1616", "#341C1C", "#1E2A24"], ["#EED6D2", "#F6E6E2", "#D6E8DC"],
            ["🐸", "📜", "📖"], 0.6, 0.6,
            ["🐸", "📜", "📖", "🍶"], SeasonalSprites.JiraiyaHeart, "Ripples:#9AE0A0"),
        // One Piece: each character's colors and motifs; "OnePiece" mixes the cast.
        ["OnePiece"] = new("OnePiece", "Red", "☠️", ["#14223A", "#1A2C46", "#3A1818"], ["#D0E0F2", "#E4EEF8", "#F0D2D0"],
            ["☠️", "👒", "🍖", "⚓", "🌊"], 0.6, 0.6,
            ["☠️", "👒", "⚓", "🗺️", "🍖"], SeasonalSprites.Skull, "Random:Ripples:#9FD0FF,Sparkles:#FFFFFF|Wind:#9FE0A0|Lightning:#9FD0FF|Sparkles:#FFD070|Embers:#FF6A30|Sparkles:#FFC0D8|Sparkles:#7FD0FF|Mist:#C8C0D8|Ripples|Embers|Lightning:#FF4040"),
        ["Luffy"] = new("Luffy", "Red", "👒", ["#301416", "#3C1A1C", "#2E2A10"], ["#F2D0D0", "#F8E2E2", "#F2E6B8"],
            ["👒", "🍖", "☀️"], 0.6, 0.6,
            ["👒", "🍖", "☀️", "⚓"], SeasonalSprites.StrawHat, "Ripples:#9FD0FF,Sparkles:#FFFFFF"),
        ["Zoro"] = new("Zoro", "Green", "⚔️", ["#142A1A", "#1A3422", "#1E1E1E"], ["#D0EAD6", "#E4F4E8", "#DCDCDC"],
            ["⚔️", "🍶", "🍃"], 0.6, 0.6,
            ["⚔️", "🍶", "🧭", "🍃"], SeasonalSprites.ZoroKatana, "Wind:#9FE0A0"),
        ["Nami"] = new("Nami", "Orange", "🍊", ["#2E1E0E", "#3A2612", "#142A3A"], ["#F8DEC0", "#FCECDA", "#CCE4F4"],
            ["🍊", "💰", "⚡", "☁️"], 0.6, 0.6,
            ["🍊", "🗺️", "💰", "🌤️"], SeasonalSprites.NamiOrb, "Lightning:#9FD0FF"),
        ["Usopp"] = new("Usopp", "Amber", "🎯", ["#2A2010", "#342816", "#1E2A1A"], ["#EEE2C8", "#F6EEDC", "#DCE8D4"],
            ["🎯", "🌱", "🥚"], 0.6, 0.6,
            ["🎯", "🌱", "🎭", "🥚"], SeasonalSprites.UsoppStar, "Sparkles:#FFD070"),
        ["Sanji"] = new("Sanji", "Yellow", "🍳", ["#2A2614", "#34301A", "#1A1E2A"], ["#F2ECC8", "#F8F4DE", "#D8DCEA"],
            ["🍳", "🔥", "💛", "🍖"], 0.6, 0.6,
            ["🍳", "🍽️", "🔥", "🌹"], SeasonalSprites.SanjiFlame, "Embers:#FF6A30"),
        ["Chopper"] = new("Chopper", "Pink", "🦌", ["#34182A", "#402032", "#1E2A3A"], ["#F6D2E4", "#FBE4EE", "#D6E2F0"],
            ["🌸", "🍬", "❄️"], 0.6, 0.6,
            ["🦌", "🎩", "🍬", "💊"], SeasonalSprites.ChopperHat, "Sparkles:#FFC0D8"),
        ["Robin"] = new("Robin", "Purple", "🌺", ["#24182E", "#2C1E38", "#1A2A2E"], ["#E4D6EE", "#F0E6F6", "#D4E8EA"],
            ["🌺", "🌸", "📚"], 0.6, 0.6,
            ["🌺", "📚", "🏛️", "☕"], SeasonalSprites.RobinClover, "Rise"),
        ["Franky"] = new("Franky", "Blue", "🤖", ["#0E2438", "#142C44", "#2E2414"], ["#CCE2F6", "#E0EEFA", "#F0E6CC"],
            ["⭐", "🔧", "🥤"], 0.6, 0.6,
            ["🤖", "🔧", "🥤", "⭐"], SeasonalSprites.FrankyStar, "Sparkles:#7FD0FF"),
        ["Brook"] = new("Brook", "Purple", "🎻", ["#1A1A22", "#22222C", "#2A1E2E"], ["#E0E0E8", "#EDEDF2", "#E6DCEA"],
            ["🎵", "🎻", "💀"], 0.6, 0.6,
            ["🎻", "🎩", "☕", "🎵"], SeasonalSprites.BrookSkull, "Mist:#C8C0D8"),
        ["Jinbe"] = new("Jinbe", "Blue", "🐋", ["#0E2234", "#122A40", "#2A1A14"], ["#CCDEF0", "#E0ECF8", "#ECDCD0"],
            ["🌊", "💧", "🐟"], 0.6, 0.6,
            ["🐋", "🌊", "⚓", "🐟"], SeasonalSprites.Drop, "Ripples"),
        ["Ace"] = new("Ace", "Orange", "🔥", ["#341A0C", "#40200E", "#2A1A10"], ["#F8D4B8", "#FCE4D2", "#F0DCC8"],
            ["🔥", "✨", "🍖"], 0.6, 0.6,
            ["🔥", "🤠", "🍖", "☠️"], SeasonalSprites.Flame, "Embers"),
        ["Shanks"] = new("Shanks", "Red", "🍶", ["#2E1012", "#3A1418", "#1A1A1E"], ["#F0CCCE", "#F8E0E2", "#DCDCE0"],
            ["⚡", "🍶", "🗡️"], 0.6, 0.6,
            ["🍶", "👒", "🗡️", "☠️"], SeasonalSprites.ShanksKatana, "Lightning:#FF4040"),
        // Dragon Ball Z: each character's colors and motifs; "DBZ" mixes the cast.
        ["DBZ"] = new("DBZ", "Orange", "🐉", ["#2E1E0E", "#3A2612", "#142438"], ["#F8DEC0", "#FCECDA", "#CCDDF2"],
            ["🐉", "⭐", "💥", "☁️"], 0.6, 0.6,
            ["🐉", "⭐", "☁️", "💥"], SeasonalSprites.Orb, "Random:DragonBalls|Embers:#FFD84A,DragonBalls|Embers:#7FA8FF|Lightning:#FFE070|Sparkles:#F0E070|Wind:#D0C8F0|Sparkles:#D080FF|Embers:#9AE08A|Mist:#FFB0D8|Sparkles:#7FE0E0|Ripples:#FFE080"),
        ["Goku"] = new("Goku", "Orange", "🥋", ["#341E0C", "#40260E", "#14243A"], ["#F8D8B4", "#FCE8D4", "#CCDCF2"],
            ["🐉", "💥", "⭐", "☁️"], 0.6, 0.6,
            ["🐉", "🥋", "☁️", "🍖"], SeasonalSprites.Orb, "Embers:#FFD84A,DragonBalls"),
        ["Vegeta"] = new("Vegeta", "Blue", "👑", ["#121E3A", "#182646", "#2E2A14"], ["#D0DAF2", "#E2E8F8", "#F0E8C8"],
            ["💥", "👑", "⚡"], 0.6, 0.6,
            ["👑", "💥", "🥊", "⚡"], SeasonalSprites.VegetaGem, "Embers:#7FA8FF"),
        ["Gohan"] = new("Gohan", "Purple", "📚", ["#1E1834", "#261E40", "#2E2A14"], ["#DCD6F2", "#EAE6F8", "#F0E8C8"],
            ["⚡", "📚", "✨"], 0.6, 0.6,
            ["📚", "⚡", "🎓", "🦸"], SeasonalSprites.GohanBolt, "Lightning:#FFE070"),
        ["Piccolo"] = new("Piccolo", "Green", "🧘", ["#142A18", "#1A3420", "#2A1A34"], ["#D0EAD4", "#E4F4E6", "#E4D6EE"],
            ["🌀", "✨", "🍃"], 0.6, 0.6,
            ["🧘", "🌀", "🧣", "💧"], SeasonalSprites.PiccoloSpiral, "Sparkles:#F0E070"),
        ["Trunks"] = new("Trunks", "Purple", "⚔️", ["#221A34", "#2A2040", "#1E2430"], ["#E0D8F2", "#EEE8F8", "#D8DEEA"],
            ["⚔️", "💥", "✨"], 0.6, 0.6,
            ["⚔️", "⏳", "🧥", "💥"], SeasonalSprites.TrunksKatana, "Wind:#D0C8F0"),
        ["Frieza"] = new("Frieza", "Purple", "👽", ["#24142E", "#2E1A3A", "#1E1E2A"], ["#EAD8F2", "#F4E8F8", "#E2E2EC"],
            ["🟣", "✨", "👽"], 0.6, 0.6,
            ["👽", "🛸", "🟣", "🍷"], SeasonalSprites.FriezaGem, "Sparkles:#D080FF"),
        ["Cell"] = new("Cell", "Green", "🦗", ["#142A1A", "#1A3420", "#22222A"], ["#D2EAD4", "#E4F4E6", "#DCDCE4"],
            ["🦗", "⚫", "✨"], 0.6, 0.6,
            ["🦗", "⚫", "🧬", "💥"], SeasonalSprites.CellGem, "Embers:#9AE08A"),
        ["Buu"] = new("Buu", "Pink", "🍬", ["#341A2C", "#402034", "#2A1A34"], ["#F6D2E8", "#FBE4F2", "#E4D6EE"],
            ["🍬", "🍫", "🍭", "💨"], 0.6, 0.6,
            ["🍬", "🍫", "🍭", "🍰"], SeasonalSprites.BuuCloud, "Mist:#FFB0D8"),
        ["Bulma"] = new("Bulma", "Teal", "🔧", ["#0E2A2C", "#123436", "#2A1A2A"], ["#CCECEC", "#E0F6F6", "#EED6E6"],
            ["🔧", "💊", "📡"], 0.6, 0.6,
            ["🔧", "💊", "🛵", "📡"], SeasonalSprites.Capsule, "Sparkles:#7FE0E0"),
        ["Krillin"] = new("Krillin", "Orange", "💿", ["#2E1E10", "#3A2614", "#1A2436"], ["#F4DEC6", "#FAEADC", "#D4DEEE"],
            ["💿", "✨", "⭐"], 0.6, 0.6,
            ["💿", "🥋", "🐢", "⭐"], SeasonalSprites.KrillinOrb, "Ripples:#FFE080"),
        ["Christmas"] = new("Christmas", "Red", "🎄", ["#12301E", "#163A26", "#5A2026"], ["#CFE8D2", "#E4F2E5", "#F0C8C4"],
            ["❄️", "🎄", "🎁", "⭐", "🔔", "⛄"], 0.4, 0.4,
            ["🎄", "🎁", "⛄", "🕯️", "🍬", "⭐"]),
    };

    public static readonly string[] IntensityOptions = ["Subtle", "Medium", "Festive"];

    /// <summary>How far the palette moves toward the occasion's tones.</summary>
    public static double IntensityScale(string? intensity) => intensity switch
    {
        "Subtle" => 0.3,
        "Festive" => 1.0,
        _ => 0.6,
    };

    public static SeasonalOccasion? CurrentOccasion { get; private set; }
    public static bool EffectsEnabled { get; private set; }
    public static bool SceneryEnabled { get; private set; }
    public static string Intensity { get; private set; } = "Medium";

    /// <summary>Raised (on the caller's thread) when the active occasion or the effects switch changes.</summary>
    public static event Action? Changed;

    public static readonly string[] ColorsFromOptions = ["Auto", "Anime", "Holiday", "Season"];

    /// <summary>The theme that sets the colors right now (see <see cref="AppSettings.ThemeColorsFrom"/>).</summary>
    public static SeasonalOccasion? For(AppSettings settings) => For(settings, DateTime.Now);

    public static SeasonalOccasion? For(AppSettings settings, DateTime now)
    {
        var active = Active(settings, now);
        return active.FirstOrDefault(a => a.Layer == settings.ThemeColorsFrom).Occasion
            ?? active.FirstOrDefault().Occasion;
    }

    /// <summary>Every theme in effect right now, most specific first: anime, holiday, season.</summary>
    public static List<(string Layer, SeasonalOccasion Occasion)> Active(AppSettings settings, DateTime now)
    {
        var list = new List<(string, SeasonalOccasion)>();
        if (Pick(settings.AnimeTheme, () => null) is { } anime) list.Add(("Anime", anime));
        if (Pick(settings.HolidayTheme, () => ResolveHoliday(now)) is { } holiday) list.Add(("Holiday", holiday));
        if (Pick(settings.SeasonalTheme, () => ResolveSeason(now)) is { } season) list.Add(("Season", season));
        return list;
    }

    /// <summary>The first active theme other than the color theme, for the accent blend.</summary>
    public static SeasonalOccasion? Secondary(AppSettings settings, DateTime now)
    {
        var primary = For(settings, now);
        return Active(settings, now).Select(a => a.Occasion).FirstOrDefault(o => !ReferenceEquals(o, primary));
    }

    private static readonly Dictionary<string, SeasonalOccasion> MixCache = [];

    /// <summary>The decorations to show: the color theme's alone, or with mixing on, a blend where the
    /// color theme's glyphs make up about 60% and the other active themes share the rest.</summary>
    public static SeasonalOccasion? Decorations(AppSettings settings, DateTime now)
    {
        var primary = For(settings, now);
        if (primary == null || !settings.ThemeMixDecorations) return primary;
        var others = Active(settings, now).Select(a => a.Occasion).Where(o => !ReferenceEquals(o, primary)).ToList();
        if (others.Count == 0) return primary;
        var key = string.Join("+", others.Select(o => o.Key).Prepend(primary.Key));
        if (MixCache.TryGetValue(key, out var cached)) return cached;
        var mixed = primary with
        {
            Key = key,
            Particles = Mix(primary.Particles, others.Select(o => o.Particles)),
            Scenery = Mix(primary.Scenery, others.Select(o => o.Scenery)),
        };
        return MixCache[key] = mixed;

        static string[] Mix(string[] main, IEnumerable<string[]> rest)
        {
            var extra = rest.SelectMany(r => r).Distinct().Except(main).ToArray();
            if (main.Length == 0 || extra.Length == 0) return [.. main, .. extra];
            var copies = Math.Max(1, (int)Math.Round(1.5 * extra.Length / main.Length));
            return [.. Enumerable.Repeat(main, copies).SelectMany(m => m), .. extra];
        }
    }

    private static SeasonalOccasion? Pick(string? option, Func<SeasonalOccasion?> auto) => option switch
    {
        null or "" or "Off" => null,
        "Auto" => auto(),
        var key => Occasions.GetValueOrDefault(key),
    };

    public static SeasonalOccasion? ResolveHoliday(DateTime now)
    {
        var d = now.Date;
        var thanksgiving = ThanksgivingDate(d.Year);
        string? key =
            (d.Month == 12 && d.Day == 31) || (d.Month == 1 && d.Day <= 2) ? "NewYear"
            : d.Month == 2 && d.Day is >= 10 and <= 14 ? "Valentines"
            : d.Month == 10 && d.Day >= 20 ? "Halloween"
            : d >= thanksgiving.AddDays(-6) && d <= thanksgiving.AddDays(1) ? "Thanksgiving"
            : d.Month == 12 && d.Day <= 26 ? "Christmas"
            : null;
        return key == null ? null : Occasions[key];
    }

    public static SeasonalOccasion ResolveSeason(DateTime now) => Occasions[now.Month switch
    {
        12 or 1 or 2 => "Winter",
        >= 3 and <= 5 => "Spring",
        >= 6 and <= 8 => "Summer",
        _ => "Fall",
    }];

    /// <summary>US Thanksgiving: the fourth Thursday of November.</summary>
    public static DateTime ThanksgivingDate(int year)
    {
        var first = new DateTime(year, 11, 1);
        var toThursday = ((int)DayOfWeek.Thursday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(toThursday + 21);
    }

    internal static void Update(SeasonalOccasion? occasion, bool effects, bool scenery, string? intensity)
    {
        intensity = IntensityOptions.Contains(intensity) ? intensity! : "Medium";
        if (ReferenceEquals(occasion, CurrentOccasion) && effects == EffectsEnabled
            && scenery == SceneryEnabled && intensity == Intensity) return;
        CurrentOccasion = occasion;
        EffectsEnabled = effects;
        SceneryEnabled = scenery;
        Intensity = intensity;
        Changed?.Invoke();
    }
}
