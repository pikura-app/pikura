using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;

namespace Pikura.Avalonia.ViewModels;

/// <summary>
/// Card for a novel search result (GlobalSearchViewModel's "Novels" category).
/// Pixiv novels have no illust-style viewer in this app yet, so cards just link out to
/// pixiv.net — same treatment as Pixivision article cards.
/// </summary>
public partial class NovelCardViewModel : ObservableObject
{
    [ObservableProperty] private Bitmap? _thumbnail;

    public string Id { get; }
    public string Title { get; }
    public string UserId { get; }
    public string UserName { get; }
    public string? ThumbnailUrl { get; }
    public List<string> Tags { get; }
    public bool HasTags => Tags.Count > 0;
    public int BookmarkCount { get; }
    public int TextLength { get; }
    public bool IsR18 { get; }

    public NovelCardViewModel(NovelPreview novel)
    {
        Id = novel.Id;
        Title = novel.Title;
        UserId = novel.UserId;
        UserName = novel.UserName;
        ThumbnailUrl = novel.EffectiveCoverUrl;
        Tags = novel.Tags?.ToList() ?? [];
        BookmarkCount = novel.BookmarkCount;
        TextLength = novel.EffectiveTextLength;
        IsR18 = novel.IsR18;
    }

    public async Task LoadThumbnailAsync(PixivImageLoader loader, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl)) return;
        try
        {
            var skBitmap = await loader.FetchBitmapAsync(ThumbnailUrl, ThumbnailSize.Small, ct);
            if (skBitmap is null || ct.IsCancellationRequested) return;
            var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap), ct);
            skBitmap.Dispose();
            if (bmp is not null && !ct.IsCancellationRequested) Thumbnail = bmp;
        }
        catch { /* best-effort thumbnail */ }
    }
}

/// <summary>
/// Card for a user search result (GlobalSearchViewModel's "User" category). Clicking one loads
/// that artist's gallery via the existing <c>GalleryViewModel.LoadArtistByIdCommand</c> flow.
/// </summary>
public partial class UserSearchCardViewModel : ObservableObject
{
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private bool _isLoadingThumbnails;

    public string UserId { get; }
    public string Name { get; }
    public string? AvatarUrl { get; }
    public List<PixivUserSearchWorkRef> RecentWorkIds { get; }
    public int RecentWorkCount => RecentWorkIds.Count;
    public bool HasRecentWorks => RecentWorkIds.Count > 0;
    public ObservableCollection<Bitmap?> RecentThumbnails { get; } = new();
    public bool HasRecentThumbnails => RecentThumbnails.Count > 0;

    private bool _thumbnailsRequested;

    public UserSearchCardViewModel(UserSearchEntry entry)
    {
        UserId = entry.UserId;
        Name = entry.Name;
        AvatarUrl = entry.ImageUrl;
        RecentWorkIds = entry.RecentWorkIds?.Take(4).ToList() ?? [];
    }

    public async Task LoadAvatarAsync(PixivImageLoader loader, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(AvatarUrl)) return;
        try
        {
            var skBitmap = await loader.FetchBitmapAsync(AvatarUrl, ThumbnailSize.Small, ct);
            if (skBitmap is null || ct.IsCancellationRequested) return;
            var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap), ct);
            skBitmap.Dispose();
            if (bmp is not null && !ct.IsCancellationRequested) Avatar = bmp;
        }
        catch { /* best-effort avatar */ }
    }

    /// <summary>
    /// Resolves the thumbnail bitmaps for the up to 4 recent works shown on Pixiv's user
    /// search page. This is lazy: call it when the card is actually displayed in the
    /// thumbnail view so we don't hammer the API for hidden results.
    /// </summary>
    public async Task LoadRecentThumbnailsAsync(PixivClient client, PixivImageLoader loader, CancellationToken ct = default)
    {
        if (_thumbnailsRequested || RecentWorkIds.Count == 0) return;
        _thumbnailsRequested = true;
        IsLoadingThumbnails = true;
        var loaded = new List<Bitmap?>();
        try
        {
            var ids = RecentWorkIds.Select(w => w.Id).Distinct().ToList();
            var previews = await client.GetArtworksMetadataAsync(UserId, ids, ct).ConfigureAwait(false);
            foreach (var id in ids)
            {
                if (ct.IsCancellationRequested) break;
                if (!previews.TryGetValue(id, out var preview) || string.IsNullOrWhiteSpace(preview.ThumbnailUrl))
                {
                    loaded.Add(null);
                    continue;
                }
                try
                {
                    var skBitmap = await loader.FetchBitmapAsync(preview.ThumbnailUrl, ThumbnailSize.Small, ct).ConfigureAwait(false);
                    if (skBitmap is null || ct.IsCancellationRequested)
                    {
                        loaded.Add(null);
                        continue;
                    }
                    var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap), ct).ConfigureAwait(false);
                    skBitmap.Dispose();
                    loaded.Add(bmp);
                }
                catch
                {
                    loaded.Add(null);
                }
            }
        }
        catch { /* best-effort thumbnails */ }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var thumb in loaded)
                    RecentThumbnails.Add(thumb);
                IsLoadingThumbnails = false;
                OnPropertyChanged(nameof(HasRecentThumbnails));
            });
        }
    }
}
