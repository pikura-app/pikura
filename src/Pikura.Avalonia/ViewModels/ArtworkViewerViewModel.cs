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

    /// <summary>Navigate directly to a specific page index (0-based).</summary>
    public async Task GoToPageAsync(int index)
    {
        await LoadPageAsync(index);
    }

    /// <summary>Fetch a small thumbnail for every page so the "all pages" grid can show them.
    /// This runs in the background after the first page is loaded.</summary>
    private async Task LoadPageThumbnailsAsync()
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            try
            {
                var url = _pages[i].Urls.Small
                       ?? _pages[i].Urls.Regular
                       ?? _pages[i].Urls.ThumbMini;
                if (string.IsNullOrEmpty(url)) continue;

                var skBitmap = await _imageLoader.FetchBitmapAsync(url, ThumbnailSize.Medium);
                if (skBitmap is null) continue;

                var bmp = await Task.Run(() =>
                    (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap));
                skBitmap.Dispose();

                if (bmp is not null && i < PageList.Count)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (i < PageList.Count)
                            PageList[i].Thumbnail = bmp;
                    });
                }
            }
            catch { /* best-effort page thumbnails */ }
        }
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

    public async Task LoadFirstPageAsync()
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
                _pages = await _pixivClient.GetArtworkPagesAsync(_artwork.Id);
                PageList = new ObservableCollection<PageThumbnailItem>(
                    _pages.Select((_, i) => new PageThumbnailItem(i, i + 1)));
                OnPropertyChanged(nameof(PageCount));
                OnPropertyChanged(nameof(HasMultiplePages));
                OnPropertyChanged(nameof(PageIndicator));
                OnPropertyChanged(nameof(PageList));
                _ = LoadPageThumbnailsAsync();
                var startIndex = Math.Clamp(_artwork.PageIndex, 0, _pages.Count - 1);
                await LoadPageAsync(startIndex);
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
        }
    }

    /// <summary>Incremented on every page load so a slow Original download belonging to a page the
    /// user has already navigated away from can't overwrite the current image.</summary>
    private int _pageLoadToken;

    private async Task LoadPageAsync(int index)
    {
        if (_pages.Count == 0 || index < 0 || index >= _pages.Count) return;
        IsLoadingPage = true;

        // Update the indicator up front — it describes which page we're *on*, and waiting for the
        // download left the counter stale while the image loaded.
        CurrentPageIndex = index;
        OnPropertyChanged(nameof(PageIndicator));

        var urls = _pages[index].Urls;
        var original = urls.Original;
        var preview = urls.Regular ?? urls.Small ?? urls.ThumbMini;

        var token = ++_pageLoadToken;

        try
        {
            // Two-stage load. Previously this fetched Original first and only assigned the bitmap
            // once the whole multi-megabyte file had arrived, so the viewer sat completely blank
            // until then — very obvious in fullscreen. Show the much smaller Regular immediately,
            // then transparently swap in the full-resolution Original.
            if (!string.IsNullOrEmpty(preview))
                await SetPageBitmapAsync(preview, token);

            if (!string.IsNullOrEmpty(original) && original != preview)
                await SetPageBitmapAsync(original, token);
        }
        finally
        {
            if (token == _pageLoadToken) IsLoadingPage = false;
        }
    }

    /// <summary>Fetches and decodes <paramref name="url"/>, assigning it as the current page image
    /// only if this load is still the newest one.</summary>
    private async Task SetPageBitmapAsync(string url, int token)
    {
        // priority: foreground viewer image — must not queue behind background thumbnail fetches.
        var bytes = await _imageLoader.FetchBytesAsync(url, default, priority: true);
        if (bytes == null || token != _pageLoadToken) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token != _pageLoadToken) return;
            using var ms = new MemoryStream(bytes);
            CurrentPageBitmap = new Bitmap(ms);
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
