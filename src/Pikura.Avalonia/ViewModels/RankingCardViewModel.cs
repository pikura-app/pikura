using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

public partial class RankingCardViewModel : ObservableObject
{
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isBlurred;
    /// <summary>Liked/bookmarked/local-favorite badges — same centralized-check approach as
    /// ArtworkCardViewModel (see GalleryViewModel.cs for the full rationale).</summary>
    [ObservableProperty] private bool _isLiked;
    [ObservableProperty] private bool _isPixivBookmarked;
    [ObservableProperty] private bool _isLocalFavorite;

    private void InitLikedBookmarkedFavorite()
    {
        try { IsLiked = AppServices.Get<SettingsService>().Current.PixivLikedArtworkIds.Contains(Id); }
        catch { /* AppServices not initialized yet, e.g. design-time */ }
        try { IsLocalFavorite = AppServices.Get<Pikura.Core.Services.LocalFavoritesService>().IsFavorite(Id); }
        catch { /* AppServices not initialized yet */ }
        try { IsPixivBookmarked = AppServices.Get<BookmarksViewModel>().IsKnownBookmarked(Id, out _); }
        catch { /* AppServices/BookmarksViewModel not initialized yet */ }
    }

    public int Rank { get; }
    public string Id { get; }
    public string Title { get; }
    public string UserName { get; }
    public string UserId { get; }
    public int PageCount { get; }
    public bool IsMultiPage => PageCount > 1;
    public bool IsR18 { get; }
    public bool IsR18G { get; }
    public int YesterdayRank { get; }
    public int RatingCount { get; }
    public int ViewCount { get; }
    public string? ThumbnailUrl { get; }
    public string Date { get; }
    public double ClampedAspectRatio { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<string> TopTags => Tags.Count > 3 ? Tags.Take(3).ToList() : Tags;

    public string RankChangeDisplay => (YesterdayRank == 0) ? "New"
        : (YesterdayRank - Rank) switch
        {
            > 0 => $"▲{YesterdayRank - Rank}",
            < 0 => $"▼{Rank - YesterdayRank}",
            _ => "—"
        };

    public string RankChangeForeground => (YesterdayRank == 0) ? "#818CF8"
        : (YesterdayRank - Rank) switch
        {
            > 0 => "#4ADE80",
            < 0 => "#F87171",
            _ => "#6B7280"
        };

    public RankingEntry? Entry { get; }
    public NovelRankingEntry? NovelEntry { get; }
    public bool IsNovel { get; }
    private readonly bool _isR18Source;

    /// <summary>Novel text length in characters, or null if unavailable/not a novel.</summary>
    public int? CharCount { get; }
    /// <summary>Estimated reading time in minutes, or null if unavailable/not a novel.</summary>
    public int? ReadingTimeMinutes { get; }
    public string StatsLabel => IsNovel
        ? (CharCount.HasValue ? $"{CharCount:N0} character(s)" : "")
          + (ReadingTimeMinutes.HasValue ? $" · {ReadingTimeMinutes} min" : "")
        : "";

    /// <summary>
    /// Not valid for novel entries — this app has no novel-download pipeline, so
    /// novels are never routed through the artwork viewer/download commands.
    /// Callers must check <see cref="IsNovel"/> first.
    /// </summary>
    public ArtworkPreview ToPreview() => Entry?.ToPreview(_isR18Source)
        ?? throw new InvalidOperationException("Novel ranking entries cannot be converted to ArtworkPreview.");

    /// <param name="isR18Source">True if this entry came from one of pixiv's "_r18" ranking
    /// endpoints. That's the only reliable R-18 signal the legacy ranking.php API exposes —
    /// <c>ContentType.Sexual</c> is a mild content-warning flag, not an age rating, and using it
    /// to decide R-18 status incorrectly hides/mislabels legitimate all-ages art (e.g. artwork
    /// with fanservice-adjacent tags that pixiv itself still ranks under "All ages").</param>
    public RankingCardViewModel(RankingEntry entry, bool isR18Source = false)
    {
        Entry = entry;
        _isR18Source = isR18Source;
        Rank = entry.Rank;
        Id = entry.IllustId.ToString();
        Title = entry.Title;
        UserName = entry.UserName;
        UserId = entry.UserId.ToString();
        PageCount = int.TryParse(entry.IllustPageCount, out var p) ? p : 1;
        var hasR18Tag = entry.Tags.Any(t => t.Contains("R-18", StringComparison.OrdinalIgnoreCase));
        var hasR18GTag = entry.Tags.Any(t => t.Contains("R-18G", StringComparison.OrdinalIgnoreCase));
        IsR18 = isR18Source || hasR18Tag || hasR18GTag;
        IsR18G = (isR18Source && (entry.ContentType.Grotesque || entry.ContentType.Violent)) || hasR18GTag;
        YesterdayRank = entry.YesRank;
        RatingCount = entry.RatingCount;
        ViewCount = entry.ViewCount;
        ThumbnailUrl = UpgradeThumbnailUrl(entry.ThumbnailUrl);
        Date = entry.Date.Length == 8
            ? $"{entry.Date[..4]}-{entry.Date[4..6]}-{entry.Date[6..8]}"
            : entry.Date;
        var rawAspect = entry.Height > 0 && entry.Width > 0
            ? (double)entry.Height / entry.Width : 1.0;
        ClampedAspectRatio = Math.Min(Math.Max(rawAspect, 0.5), 2.5);
        Tags = entry.Tags;
        InitLikedBookmarkedFavorite();
    }

    public RankingCardViewModel(NovelRankingEntry entry, bool isR18Source = false)
    {
        NovelEntry = entry;
        IsNovel = true;
        Rank = entry.Rank;
        Id = entry.NovelId.ToString();
        Title = entry.Title;
        UserName = entry.UserName;
        UserId = entry.UserId.ToString();
        PageCount = 1;
        var hasR18Tag = entry.Tags.Any(t => t.Contains("R-18", StringComparison.OrdinalIgnoreCase));
        var hasR18GTag = entry.Tags.Any(t => t.Contains("R-18G", StringComparison.OrdinalIgnoreCase));
        IsR18 = isR18Source || hasR18Tag || hasR18GTag;
        IsR18G = (isR18Source && (entry.ContentType.Grotesque || entry.ContentType.Violent)) || hasR18GTag;
        YesterdayRank = entry.YesRank;
        RatingCount = entry.RatingCount;
        ViewCount = entry.ViewCount ?? 0;
        ThumbnailUrl = UpgradeThumbnailUrl(entry.ThumbnailUrl);
        Date = string.Empty;
        ClampedAspectRatio = 1.4; // novel covers are typically portrait-ish; fixed ratio, no real dims from this endpoint
        Tags = entry.Tags;
        CharCount = entry.TextCount;
        ReadingTimeMinutes = entry.ReadingTimeMinutes;
        InitLikedBookmarkedFavorite();
    }

    private static string? UpgradeThumbnailUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        // /custom-thumb/ hosts only square _custom1200 files. Rewriting just the
        // suffix to _master1200 yields a 404 — the path prefix has to move to
        // /img-master/ as well.
        var upgraded = url;
        if (upgraded.Contains("/custom-thumb/"))
        {
            upgraded = upgraded.Replace("/custom-thumb/", "/img-master/")
                               .Replace("_custom1200", "_master1200");
        }
        return upgraded.Replace("_square1200", "_master1200")
                       .Replace("/250x250_80_a2/", "/540x540_70/");
    }

    public async Task LoadThumbnailAsync(PixivImageLoader loader, ThumbnailSize size = ThumbnailSize.Medium, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl)) return;
        try
        {
            // Use the decoded bitmap cache with size hint
            var skBitmap = await loader.FetchBitmapAsync(ThumbnailUrl, size, ct);
            if (skBitmap is null || ct.IsCancellationRequested) return;

            // Fast SKBitmap → Avalonia Bitmap conversion via direct pixel copy
            // (avoids PNG encode/decode roundtrip — ~10× faster for thumbnails).
            var bmp = await Task.Run(() =>
                (Bitmap?)Pikura.Avalonia.Services.BitmapInterop.SkiaToAvalonia(skBitmap), ct);

            skBitmap.Dispose(); // Dispose the copy we received

            if (bmp is not null && !ct.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(() => Thumbnail = bmp);
        }
        catch (OperationCanceledException) { }
        catch { }
    }
}
