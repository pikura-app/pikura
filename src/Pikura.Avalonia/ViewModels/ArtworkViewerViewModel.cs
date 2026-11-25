using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Services;
using Pikura.Core.Data;
using Pikura.Core.Models;
using Pikura.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

public partial class ArtworkViewerViewModel : ObservableObject
{
    private readonly ArtworkPreview _artwork;
    private readonly GalleryViewModel _gallery;
    private readonly Window _owner;
    private readonly PixivClient _pixivClient;
    private readonly PixivImageLoader _imageLoader;
    private readonly PixivDownloadService _downloader;
    private readonly UgoiraService _ugoiraService;
    private readonly DownloadCoordinator _coordinator;
    private readonly DownloadJobRepository _jobRepository;

    private IReadOnlyList<ArtworkPage> _pages = [];

    [ObservableProperty] private Bitmap? _currentPageBitmap;
    [ObservableProperty] private int _currentPageIndex;
    [ObservableProperty] private bool _isLoadingPage;
    [ObservableProperty] private bool _isDownloading;

    // Ugoira (animated) playback
    [ObservableProperty] private string? _ugoiraPreviewPath;
    [ObservableProperty] private bool _isUgoira;
    [ObservableProperty] private bool _isPlayingUgoira = true;

    public string ArtworkId => _artwork.Id;
    public string Title => _artwork.Title;
    public string ArtistName => _artwork.UserName;
    public string TypeLabel => _artwork.TypeLabel;
    public int PageCount => _pages.Count > 0 ? _pages.Count : _artwork.PageCount;
    public bool HasMultiplePages => PageCount > 1 && !IsUgoira;
    public bool IsR18 => _artwork.IsR18;
    public string PageIndicator => $"{CurrentPageIndex + 1} / {PageCount}";
    public string TagsText => _artwork.Tags.Count > 0 ? string.Join("  ·  ", _artwork.Tags.Take(8)) : string.Empty;

    /// <summary>Numbered page list shown in the fullscreen page strip.</summary>
    public ObservableCollection<PageThumbnailItem> PageList { get; private set; } = [];

    [ObservableProperty] private bool _isPageGridVisible;


    public ArtworkViewerViewModel(ArtworkPreview artwork, GalleryViewModel gallery, Window owner)
    {
        _artwork = artwork;
        _gallery = gallery;
        _owner = owner;
        _pixivClient   = AppServices.Get<PixivClient>();
        _imageLoader   = AppServices.Get<PixivImageLoader>();
        _downloader    = AppServices.Get<PixivDownloadService>();
        _ugoiraService = AppServices.Get<UgoiraService>();
        _coordinator   = AppServices.Get<DownloadCoordinator>();
        _jobRepository = AppServices.Get<DownloadJobRepository>();
    }

    /// <summary>Pages lists by artwork id, so reopening an artwork doesn't wait on the pages API
    /// before it can even start loading the image.</summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ArtworkPage>> PagesCache = new();
    private const int MaxCachedPageLists = 500;

    private static async Task<IReadOnlyList<ArtworkPage>> GetPagesCachedAsync(PixivClient client, string artworkId)
    {
        if (PagesCache.TryGetValue(artworkId, out var cached)) return cached;
        var pages = await client.GetArtworkPagesAsync(artworkId);
        if (pages.Count > 0)
        {
            if (PagesCache.Count >= MaxCachedPageLists) PagesCache.Clear();
            PagesCache[artworkId] = pages;
        }
        return pages;
    }

    /// <summary>
    /// Warms the caches for an artwork the user is likely to open next: its pages list and the
    /// first page's Original, so arrowing onto it shows full quality straight away.
    /// </summary>
    public static async Task PrefetchAsync(ArtworkPreview artwork)
    {
        if (artwork.IllustType == 2) return;
        try
        {
            var pages = await GetPagesCachedAsync(AppServices.Get<PixivClient>(), artwork.Id);
            if (pages.Count == 0) return;
            var page = pages[Math.Clamp(artwork.PageIndex, 0, pages.Count - 1)];
            if (page.Urls.Original is not { Length: > 0 } original || TryGetDecoded(original) != null) return;
            var bytes = await AppServices.Get<PixivImageLoader>().FetchBytesAsync(original);
            if (bytes == null) return;
            if (await Task.Run(() => DecodeBytes(bytes)) is { } bmp)
                RememberDecoded(original, bmp);
        }
        catch { /* best-effort */ }
    }

    // Recently shown / prefetched Originals, already decoded. Decoding a multi-megapixel
    // Original takes a noticeable moment even from disk, so going back and forth between
    // recent artworks reuses these instead. Evicted entries aren't disposed — one may still be
    // on screen — the GC frees them once nothing references them.
    private static readonly object DecodedLock = new();
    private static readonly LinkedList<(string Url, Bitmap Bitmap)> Decoded = new();
    private static int? _maxDecoded;

    private static int MaxDecoded => _maxDecoded ??= GetDeviceTier() switch
    {
        DeviceTier.High => 6,
        DeviceTier.Low => 1,
        _ => 3,
    };

    private static DeviceTier GetDeviceTier()
    {
        try { return AppServices.Get<DeviceCapabilityService>().Tier; }
        catch { return DeviceTier.Medium; }
    }

    private static Bitmap? TryGetDecoded(string url)
    {
        lock (DecodedLock)
        {
            for (var node = Decoded.First; node != null; node = node.Next)
            {
                if (node.Value.Url != url) continue;
                Decoded.Remove(node);
                Decoded.AddFirst(node);
                return node.Value.Bitmap;
            }
        }
        return null;
    }

    private static void RememberDecoded(string url, Bitmap bitmap)
    {
        lock (DecodedLock)
        {
            for (var node = Decoded.First; node != null; node = node.Next)
            {
                if (node.Value.Url != url) continue;
                Decoded.Remove(node);
                break;
            }
            Decoded.AddFirst((url, bitmap));
            while (Decoded.Count > MaxDecoded) Decoded.RemoveLast();
        }
    }

    private static Bitmap? DecodeBytes(byte[] bytes)
    {
        try { using var ms = new MemoryStream(bytes); return new Bitmap(ms); }
        catch { return null; }
    }

    private readonly TaskCompletionSource _fullQualityShown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the first page has finished loading (at full quality when
    /// possible), so background prefetching can wait until it no longer competes with it.</summary>
    public Task FirstPageLoaded => _fullQualityShown.Task;

    /// <summary>Navigate directly to a specific page index (0-based).</summary>
    public async Task GoToPageAsync(int index)
    {
        await LoadPageAsync(index);
    }

    /// <summary>Fetch a small thumbnail for every page so the "all pages" grid can show them.
    /// This runs in the background after the first page is loaded.</summary>
    private async Task LoadPageThumbnailsAsync()
    {
        // Several at a time, starting from the page being opened: one-by-one from page 1 left a
        // long submission's grid filling in slowly, with the pages around the current one last.
        var pages = _pages;
        var start = Math.Clamp(_artwork.PageIndex, 0, Math.Max(0, pages.Count - 1));
        var order = Enumerable.Range(0, pages.Count).OrderBy(i => Math.Abs(i - start)).ToList();
        using var gate = new System.Threading.SemaphoreSlim(4);
        await Task.WhenAll(order.Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                if (!ReferenceEquals(_pages, pages) || (i < PageList.Count && PageList[i].Thumbnail != null)) return;
                var url = pages[i].Urls.Small
                       ?? pages[i].Urls.Regular
                       ?? pages[i].Urls.ThumbMini;
                if (string.IsNullOrEmpty(url)) return;

                using var skBitmap = await _imageLoader.FetchBitmapAsync(url, ThumbnailSize.Medium);
                if (skBitmap is null) return;

                var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap));
                if (bmp is null) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_pages, pages) && i < PageList.Count
                        && !IsAtLeastAsSharp(PageList[i].Thumbnail, bmp))
                        PageList[i].Thumbnail = bmp;
                });
            }
            catch { /* best-effort page thumbnails */ }
            finally { gate.Release(); }
        }));
    }

    /// <summary>Ensure all property-changed notifications are raised on the UI thread.
    /// This matters for bindings like <c>IsVisible</c> and the page overlay in the
    /// fullscreen window, which are updated from background load operations.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            base.OnPropertyChanged(e);
        }
        else
        {
            Dispatcher.UIThread.Post(() => base.OnPropertyChanged(e));
        }
    }

    public async Task LoadFirstPageAsync(Bitmap? seed = null)
    {
        IsLoadingPage = true;
        try
        {
            // Ugoira — animated, fetch preview via ffmpeg
            if (_artwork.IllustType == 2)
            {
                IsUgoira = true;
                PageList = new ObservableCollection<PageThumbnailItem>();
                OnPropertyChanged(nameof(PageList));
                OnPropertyChanged(nameof(HasMultiplePages));
                var path = await _ugoiraService.GetOrCreatePreviewAsync(_artwork.Id);
                UgoiraPreviewPath = path;
                IsPlayingUgoira = true;
            }
            else
            {
                IsUgoira = false;
                _pages = await GetPagesCachedAsync(_pixivClient, _artwork.Id);
                PageList = new ObservableCollection<PageThumbnailItem>(
                    _pages.Select((_, i) => new PageThumbnailItem(i, i + 1)));
                OnPropertyChanged(nameof(PageCount));
                OnPropertyChanged(nameof(HasMultiplePages));
                OnPropertyChanged(nameof(PageIndicator));
                OnPropertyChanged(nameof(PageList));
                _ = LoadPageThumbnailsAsync();
                var startIndex = Math.Clamp(_artwork.PageIndex, 0, _pages.Count - 1);
                await LoadPageAsync(startIndex, seed);
            }
        }
        catch (Exception ex)
        {
            // Don't swallow silently — a failure here leaves the viewer completely blank, which is
            // impossible to diagnose without a trace of why.
            System.Diagnostics.Debug.WriteLine($"LoadFirstPage failed for {_artwork.Id}: {ex}");
        }
        finally
        {
            IsLoadingPage = false;
            _fullQualityShown.TrySetResult();
        }
    }

    /// <summary>Incremented on every page load so a slow Original download belonging to a page the
    /// user has already navigated away from can't overwrite the current image.</summary>
    private int _pageLoadToken;

    private async Task LoadPageAsync(int index, Bitmap? seed = null)
    {
        if (_pages.Count == 0 || index < 0 || index >= _pages.Count) return;
        IsLoadingPage = true;

        // Update the indicator up front — it describes which page we're *on*, and waiting for the
        // download left the counter stale while the image loaded.
        CurrentPageIndex = index;
        OnPropertyChanged(nameof(PageIndicator));
        foreach (var item in PageList) item.IsCurrent = item.Index == index;

        var urls = _pages[index].Urls;
        var original = urls.Original;
        var preview = urls.Regular ?? urls.Small ?? urls.ThumbMini;

        var token = ++_pageLoadToken;

        try
        {
            // Two-stage load: the small Regular first so the viewer is never blank, then the
            // full-resolution Original. The Original download starts straight away, in parallel,
            // and the Regular step is skipped entirely when it would only be a downgrade — the
            // Original is already cached (an artwork opened before), or the caller's seed bitmap
            // is already at least Regular quality.
            var hasOriginal = !string.IsNullOrEmpty(original);
            var originalTask = hasOriginal ? DecodeAsync(original!, keepDecoded: true) : null;
            var seedIsSharp = seed is { } s && Math.Max(s.PixelSize.Width, s.PixelSize.Height) >= 1200;
            var skipPreview = string.IsNullOrEmpty(preview)
                || (hasOriginal && (original == preview || TryGetDecoded(original!) != null
                    || _imageLoader.IsCached(original!) || seedIsSharp));

            if (!skipPreview)
            {
                var regular = await DecodeAsync(preview!);
                if (originalTask is not { IsCompletedSuccessfully: true, Result: not null })
                    await ApplyPageBitmapAsync(regular, token);
            }

            if (originalTask != null)
                await ApplyPageBitmapAsync(await originalTask, token);

            // Warm the next page so turning to it is instant too.
            if (token == _pageLoadToken && index + 1 < _pages.Count
                && _pages[index + 1].Urls.Original is { Length: > 0 } next && !_imageLoader.IsCached(next))
                _ = _imageLoader.FetchBytesAsync(next);
        }
        finally
        {
            if (token == _pageLoadToken) IsLoadingPage = false;
        }
    }

    /// <summary>Fetches and decodes <paramref name="url"/> off the UI thread — originals can be
    /// several megabytes, and decoding them on the UI thread stalled the window.</summary>
    private async Task<Bitmap?> DecodeAsync(string url, bool keepDecoded = false)
    {
        if (TryGetDecoded(url) is { } decoded) return decoded;
        // priority: foreground viewer image — must not queue behind background thumbnail fetches.
        try
        {
            var bytes = await _imageLoader.FetchBytesAsync(url, default, priority: true);
            if (bytes == null) return null;
            var bmp = await Task.Run(() => DecodeBytes(bytes));
            if (bmp != null && keepDecoded) RememberDecoded(url, bmp);
            return bmp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Viewer image load failed for {url}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Shows <paramref name="bitmap"/> only if this load is still the newest one.</summary>
    private async Task ApplyPageBitmapAsync(Bitmap? bitmap, int token)
    {
        if (bitmap == null || token != _pageLoadToken) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token == _pageLoadToken) CurrentPageBitmap = bitmap;
        });
    }

    [RelayCommand]
    private async Task PrevPage()
    {
        if (CurrentPageIndex > 0)
            await LoadPageAsync(CurrentPageIndex - 1);
    }

    [RelayCommand]
    private async Task NextPage()
    {
        if (CurrentPageIndex < PageCount - 1)
            await LoadPageAsync(CurrentPageIndex + 1);
    }

    [RelayCommand]
    private async Task DownloadCurrentPage()
        => await DownloadWithJobAsync([CurrentPageIndex], $"{_artwork.Title} (p{CurrentPageIndex + 1})");

    [RelayCommand]
    private async Task DownloadAllPages()
        => await DownloadWithJobAsync(null, _artwork.Title);

    [RelayCommand]
    private async Task DownloadRange()
    {
        if (PageCount <= 1) { await DownloadAllPages(); return; }
        var dialog = new Views.Dialogs.RangePickerDialog(
            title: $"Page range — {_artwork.Title}",
            description: $"This artwork has {PageCount} pages (0-based indexes). " +
                         "Examples: \"0\" (first), \"0-2\" (first 3), \"0,2,4\".",
            maxInclusive: PageCount - 1,
            placeholder: $"0-{PageCount - 1}");
        dialog.ShowInTaskbar = false;
        await dialog.ShowDialog(_owner);
        if (dialog.SelectedIndexes.Count == 0) return;
        await DownloadWithJobAsync(dialog.SelectedIndexes, $"{_artwork.Title} (range)");
    }

    private async Task DownloadWithJobAsync(IReadOnlyCollection<int>? pages, string jobName)
    {
        IsDownloading = true;
        var target = new DownloadTarget
        {
            TargetId = _artwork.Id, Name = _artwork.Title,
            ThumbnailUrl = _artwork.ThumbnailUrl, UserName = _artwork.UserName,
            UserId = _artwork.UserId, Type = TargetType.Artwork, Status = TargetStatus.Running
        };
        var job = new DownloadJob
        {
            Name = jobName, Type = DownloadJobType.ImageId,
            Status = JobStatus.Running, StartedAt = DateTime.UtcNow,
            Targets = [target]
        };
        await _jobRepository.SaveJobAsync(job);
        _coordinator.NotifyJobStarted(job);
        try
        {
            var files = await _downloader.DownloadArtworkPagesAsync(_artwork, pages);
            target.Status = TargetStatus.Completed;
            target.DownloadedItems = files.Count;
            job.Status = JobStatus.Completed;
            job.OutputFolder = files.Count > 0 ? Path.GetDirectoryName(files[0]) : null;
        }
        catch (Exception ex)
        {
            target.Status = TargetStatus.Failed;
            target.ErrorMessage = ex.Message;
            job.Status = JobStatus.Failed;
        }
        finally
        {
            job.CompletedAt = DateTime.UtcNow;
            IsDownloading = false;
            _ = Task.Run(async () => { await _jobRepository.SaveJobAsync(job); _coordinator.NotifyJobSaved(job); });
        }
    }

    [RelayCommand]
    private void Close() => _owner.Close();

    [RelayCommand]
    private void TogglePageGrid() => IsPageGridVisible = !IsPageGridVisible;

    partial void OnIsPageGridVisibleChanged(bool value)
    {
        if (value) _ = UpgradePageThumbnailsAsync();
    }

    private static bool IsAtLeastAsSharp(Bitmap? existing, Bitmap candidate)
        => existing != null && existing.PixelSize.Width >= candidate.PixelSize.Width;

    private bool _pageThumbnailsUpgraded;

    /// <summary>
    /// The grid's tiles are 540 wide — on a high-DPI screen that's over 1000 physical pixels,
    /// so the 540px strip thumbnails look soft. Swap in the 1200px master for each page, the
    /// page on screen first (reusing the image already shown), once per artwork.
    /// </summary>
    private async Task UpgradePageThumbnailsAsync()
    {
        if (_pageThumbnailsUpgraded || _pages.Count == 0) return;
        _pageThumbnailsUpgraded = true;
        var pages = _pages;

        var current = Math.Clamp(CurrentPageIndex, 0, pages.Count - 1);
        if (CurrentPageBitmap is { } shown && current < PageList.Count
            && !IsAtLeastAsSharp(PageList[current].Thumbnail, shown))
            PageList[current].Thumbnail = shown;

        var order = Enumerable.Range(0, pages.Count)
            .OrderBy(i => i < current ? i + pages.Count : i) // current page onwards, then wrap
            .ToList();
        using var gate = new System.Threading.SemaphoreSlim(3);
        await Task.WhenAll(order.Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                if (!ReferenceEquals(_pages, pages)) return;
                var url = pages[i].Urls.Regular ?? pages[i].Urls.Small;
                if (string.IsNullOrEmpty(url)) return;
                using var sk = await _imageLoader.FetchBitmapAsync(url, ThumbnailSize.Large);
                if (sk is null) return;
                var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(sk));
                if (bmp is null) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_pages, pages) && i < PageList.Count
                        && !IsAtLeastAsSharp(PageList[i].Thumbnail, bmp))
                        PageList[i].Thumbnail = bmp;
                });
            }
            catch { /* keep the smaller tile */ }
            finally { gate.Release(); }
        }));
    }

    [RelayCommand]
    private async Task SelectPageFromGrid(PageThumbnailItem? item)
    {
        if (item is null) return;
        IsPageGridVisible = false;
        await GoToPageAsync(item.Index);
    }
}

/// <summary>Page tile shown in the fullscreen "all pages" grid and page strip.</summary>
public partial class PageThumbnailItem : ObservableObject
{
    public int Index { get; }
    public int Number { get; }

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private double _aspectRatio;
    [ObservableProperty] private bool _isCurrent;

    partial void OnThumbnailChanged(Bitmap? value)
    {
        if (value is null)
        {
            AspectRatio = 1.0;
            return;
        }
        // height / width — the all-pages grid computes its tile height as
        // width × AspectRatio (see AspectRatioToHeightConverter), so this must be
        // height-over-width. It used to be width/height, which inverted every tile:
        // portrait pages came out squat and got cropped, landscape ones came out
        // over-tall. Matches ArtworkCardViewModel.AspectRatio's convention.
        var ps = value.PixelSize;
        AspectRatio = ps.Width > 0 ? (double)ps.Height / ps.Width : 1.0;
    }

    public PageThumbnailItem(int index, int number)
    {
        Index = index;
        Number = number;
        AspectRatio = 1.0;
    }
}
