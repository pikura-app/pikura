using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Pikura.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

public partial class BookmarksViewModel : ViewModelBase
{
    // ── Dependencies ───────────────────────────────────────────────────────
    private readonly PixivClient _pixivClient;
    private readonly PixivImageLoader _imageLoader;
    private readonly SettingsService _settingsService;
    private readonly LocalFavoritesService _favoritesService;
    private readonly DownloadCoordinator _downloadCoordinator;
    private readonly DialogService _dialogService;

    private CancellationTokenSource? _cts;
    private bool _isLoadingPublic;
    private bool _isLoadingPrivate;
    private int _loadedOffsetPublic;
    private int _loadedOffsetPrivate;

    // ── Tab ────────────────────────────────────────────────────────────────
    // 0 = Public  1 = Private  2 = Local Favorites
    [ObservableProperty] private int _selectedTabIndex = 2; 
    public bool IsPublicTab    => SelectedTabIndex == 0;
    public bool IsPrivateTab   => SelectedTabIndex == 1;
    public bool IsFavoritesTab => SelectedTabIndex == 2;

    // ── Status ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Select a tab to load bookmarks";
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private int _totalCount;

    // ── Collections ────────────────────────────────────────────────────────
    public ObservableCollection<ArtworkCardViewModel> PublicBookmarks { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> PrivateBookmarks { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> LocalFavorites { get; } = [];

    public ObservableCollection<ArtworkCardViewModel> FilteredPublic { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> FilteredPrivate { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> FilteredFavorites { get; } = [];

    public bool HasPublic => PublicBookmarks.Count > 0;
    public bool HasPrivate => PrivateBookmarks.Count > 0;
    public bool HasFavorites => LocalFavorites.Count > 0;

    // ── View options ───────────────────────────────────────────────────────
    [ObservableProperty] private int _cardSize = 180;
    [ObservableProperty] private bool _isFixedHeight = true;
    [ObservableProperty] private bool _isNaturalHeight;
    [ObservableProperty] private bool _isGridView = true;
    [ObservableProperty] private bool _isListView;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private bool _showR18;
    [ObservableProperty] private double _browsePanelWidth = 480;

    // ── Folder filter (Local Favorites) ───────────────────────────────────
    [ObservableProperty] private string _folderFilter = string.Empty;
    partial void OnFolderFilterChanged(string _) => UpdateFiltered();
    public ObservableCollection<string> AvailableFolders { get; } = [];

    public double FixedCardTotalHeight => CardSize;
    public bool ShowR18Buttons => _settingsService.Current.R18Mode != R18Mode.Off;
    public GalleryViewModel GalleryVm { get; }
    public string ViewerSourceKey => $"Bookmarks:{SelectedTabIndex}:{ShowR18}:{TagFilter}:{FolderFilter}";
    public bool HasTabs => GalleryVm.HasTabs;
    [ObservableProperty] private bool _isViewerExpanded;
    partial void OnIsViewerExpandedChanged(bool v) { OnPropertyChanged(nameof(IsViewerFullScreen)); OnPropertyChanged(nameof(ShowGridLayer)); OnPropertyChanged(nameof(PublicTabVisible)); OnPropertyChanged(nameof(PrivateTabVisible)); OnPropertyChanged(nameof(FavoritesTabVisible)); }
    /// <summary>True when the viewer is expanded to fill the full content area.</summary>
    public bool IsViewerFullScreen => IsViewerExpanded;
    /// <summary>True when the artwork grid should be visible.</summary>
    public bool ShowGridLayer => !IsViewerExpanded;
    public bool PublicTabVisible => IsPublicTab && ShowGridLayer;
    public bool PrivateTabVisible => IsPrivateTab && ShowGridLayer;
    public bool FavoritesTabVisible => IsFavoritesTab && ShowGridLayer;

    // Grid view mode combined with height mode (for ScrollViewer visibility)
    public bool ShowFixedGrid => IsFixedHeight && IsGridView;
    public bool ShowNaturalGrid => IsNaturalHeight && IsGridView;

    // ── Selection mode (all tabs) ──────────────────────────────────────────
    [ObservableProperty] private bool _isSelectionMode;

    // Unified selection helpers — works across all three tabs
    private ObservableCollection<ArtworkCardViewModel> ActiveCollection => SelectedTabIndex switch
    {
        0 => FilteredPublic,
        1 => FilteredPrivate,
        _ => FilteredFavorites
    };

    public int SelectedCount => ActiveCollection.Count(c => c.IsSelected);
    public bool HasSelection => SelectedCount > 0;

    // Legacy alias kept for Favorites-specific XAML bindings
    public int SelectedFavoritesCount => FilteredFavorites.Count(c => c.IsSelected);

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedFavoritesCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    public void NotifyFavoritesSelectionChanged() => NotifySelectionChanged();

    [RelayCommand]
    public void SelectAllFavorites()
    {
        foreach (var c in ActiveCollection) c.IsSelected = true;
        NotifySelectionChanged();
    }

    [RelayCommand]
    public void ClearFavoritesSelection()
    {
        foreach (var c in ActiveCollection) c.IsSelected = false;
        IsSelectionMode = false;
        NotifySelectionChanged();
    }

    [RelayCommand]
    public void RemoveSelectedFavorites()
    {
        var selected = FilteredFavorites.Where(c => c.IsSelected).ToList();
        foreach (var c in selected) _favoritesService.Remove(c.Id);
        NotifySelectionChanged();
    }

    public void SetFolderForSelected(string? folder)
    {
        var selected = FilteredFavorites.Where(c => c.IsSelected).ToList();
        foreach (var c in selected) _favoritesService.SetFolder(c.Id, folder);
        UpdateFiltered();
        AvailableFolders.Clear();
        foreach (var f in _favoritesService.GetAllFolders()) AvailableFolders.Add(f);
        NotifyFavoritesSelectionChanged();
    }

    // ── Sort ───────────────────────────────────────────────────────────────
    public enum BookmarkSortMode { Default, NewestPosted, OldestPosted, TitleAZ, TitleZA, MostPages }
    [ObservableProperty] private BookmarkSortMode _sortMode = BookmarkSortMode.Default;
    partial void OnSortModeChanged(BookmarkSortMode _) => UpdateFiltered();

    public static IReadOnlyList<string> SortOptions { get; } =
    [
        "Newest Bookmarked",
        "Newest Posted",
        "Oldest Posted",
        "Title A → Z",
        "Title Z → A",
        "Most Pages",
    ];

    public static BookmarkSortMode SortModeFromIndex(int index) => index switch
    {
        1 => BookmarkSortMode.NewestPosted,
        2 => BookmarkSortMode.OldestPosted,
        3 => BookmarkSortMode.TitleAZ,
        4 => BookmarkSortMode.TitleZA,
        5 => BookmarkSortMode.MostPages,
        _ => BookmarkSortMode.Default,
    };

    // ── Tag filter ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _tagFilter = string.Empty;
    partial void OnTagFilterChanged(string _) => UpdateFiltered();

    // ── Constructor ────────────────────────────────────────────────────────
    public BookmarksViewModel(
        PixivClient pixivClient,
        PixivImageLoader imageLoader,
        SettingsService settingsService,
        LocalFavoritesService favoritesService,
        GalleryViewModel galleryVm,
        DownloadCoordinator downloadCoordinator,
        DialogService dialogService)
    {
        _pixivClient = pixivClient;
        _imageLoader = imageLoader;
        _settingsService = settingsService;
        _favoritesService = favoritesService;
        GalleryVm = galleryVm;
        _downloadCoordinator = downloadCoordinator;
        _dialogService = dialogService;

        var s = settingsService.Current;
        _isFixedHeight   = s.BookmarksCardHeightMode != "Natural";
        _isNaturalHeight = s.BookmarksCardHeightMode == "Natural";
        _isGridView      = s.BookmarksViewMode != "List";
        _isListView      = s.BookmarksViewMode == "List";
        _cardSize        = s.CardSize;
        _showTags        = s.BookmarksShowTags;
        _showInfo        = s.BookmarksShowInfo;
        _showR18         = s.BookmarksShowR18;
        _browsePanelWidth = s.BrowsePanelWidth >= 200 ? s.BrowsePanelWidth : 480;

        _favoritesService.Changed += (_, _) =>
        {
            if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                ReloadLocalFavorites();
            else
                global::Avalonia.Threading.Dispatcher.UIThread.Post(ReloadLocalFavorites);
        };

        _settingsService.Changed += (_, _) =>
        {
            var shared = _settingsService.Current.CardSize;
            if (CardSize != shared) CardSize = shared;
        };

        void NotifyViewerState()
        {
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(PublicTabVisible));
            OnPropertyChanged(nameof(PrivateTabVisible));
            OnPropertyChanged(nameof(FavoritesTabVisible));
            if (!GalleryVm.HasTabs) IsViewerExpanded = false;
        }

        GalleryVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.HasTabs))
                NotifyViewerState();
        };
        GalleryVm.ViewerTabs.CollectionChanged += (_, _) => NotifyViewerState();
    }

    // ── Navigation entry point ─────────────────────────────────────────────
    public void OnNavigatedTo()
    {
        if (PublicBookmarks.Count == 0 && !_isLoadingPublic)
            _ = LoadTabAsync(0);
        if (LocalFavorites.Count == 0)
            ReloadLocalFavorites();
    }

    // ── Tab switching ──────────────────────────────────────────────────────
    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPublicTab));
        OnPropertyChanged(nameof(IsPrivateTab));
        OnPropertyChanged(nameof(IsFavoritesTab));
        OnPropertyChanged(nameof(PublicTabVisible));
        OnPropertyChanged(nameof(PrivateTabVisible));
        OnPropertyChanged(nameof(FavoritesTabVisible));
        switch (value)
        {
            case 0 when PublicBookmarks.Count == 0 && !_isLoadingPublic:
                _ = LoadTabAsync(0);
                break;
            case 0:
                UpdateFiltered();
                break;
            case 1 when PrivateBookmarks.Count == 0 && !_isLoadingPrivate:
                _ = LoadTabAsync(1);
                break;
            case 1:
                UpdateFiltered();
                break;
            case 2:
                ReloadLocalFavorites();
                break;
        }
    }

    // ── Load public / private ──────────────────────────────────────────────
    [RelayCommand]
    public async Task LoadTabAsync(int tabIndex)
    {
        var isPrivate = tabIndex == 1;
        if (isPrivate) { if (_isLoadingPrivate) return; _isLoadingPrivate = true; }
        else           { if (_isLoadingPublic)  return; _isLoadingPublic  = true; }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsLoading = true;
        StatusMessage = $"Loading {(isPrivate ? "private" : "public")} bookmarks…";

        var collection = isPrivate ? PrivateBookmarks : PublicBookmarks;
        var filtered   = isPrivate ? FilteredPrivate  : FilteredPublic;
        collection.Clear();
        filtered.Clear();
        if (isPrivate) _loadedOffsetPrivate = 0;
        else           _loadedOffsetPublic  = 0;
        TotalCount = 0;
        CanLoadMore = false;

        try
        {
            var self = await _pixivClient.ResolveSelfAsync(ct);
            if (self == null)
            {
                StatusMessage = "Not signed in.";
                return;
            }

            if (isPrivate) _loadedOffsetPrivate = await FetchBatchAsync(self.Value.UserId, isPrivate, collection, _loadedOffsetPrivate, ct);
            else           _loadedOffsetPublic  = await FetchBatchAsync(self.Value.UserId, isPrivate, collection, _loadedOffsetPublic,  ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load bookmarks");
            StatusMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            if (isPrivate) _isLoadingPrivate = false;
            else           _isLoadingPublic  = false;
            IsLoading = false;
            UpdateFiltered();
            OnPropertyChanged(isPrivate ? nameof(HasPrivate) : nameof(HasPublic));
        }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!CanLoadMore || IsLoading) return;
        var source = ViewerSourceKey;
        var isPrivate = SelectedTabIndex == 1;

        var self = await _pixivClient.ResolveSelfAsync();
        if (self == null) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsLoading = true;

        try
        {
            var collection = isPrivate ? PrivateBookmarks : PublicBookmarks;
            if (isPrivate) _loadedOffsetPrivate = await FetchBatchAsync(self.Value.UserId, isPrivate, collection, _loadedOffsetPrivate, ct);
            else           _loadedOffsetPublic  = await FetchBatchAsync(self.Value.UserId, isPrivate, collection, _loadedOffsetPublic,  ct);
        }
        finally
        {
            IsLoading = false;
            UpdateFiltered();
            var list = isPrivate ? FilteredPrivate.ToList() : FilteredPublic.ToList();
            GalleryVm.SyncViewerTabs(source, list, TotalCount);
        }
    }

    private async Task<int> FetchBatchAsync(
        string userId, bool hidden,
        ObservableCollection<ArtworkCardViewModel> collection,
        int loadedOffset,
        CancellationToken ct)
    {
        const int batchSize = 48;
        var response = await _pixivClient.GetBookmarkedArtworksAsync(
            userId, null, hidden, loadedOffset, batchSize, ct);

        if (response.Total == 0 && response.Works.Count == 0 && loadedOffset == 0)
            StatusMessage = $"No {(hidden ? "private" : "public")} bookmarks found. Check %TEMP%\\pikura_api_diag.txt if unexpected.";

        TotalCount = response.Total;
        loadedOffset += response.Works.Count;
        CanLoadMore = loadedOffset < TotalCount;

        var blurR18 = _settingsService.Current.BlurR18Content;
        foreach (var work in response.Works)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(work.Id)) continue;
            var preview = work.ToArtworkPreview();
            var vm = new ArtworkCardViewModel(preview)
            {
                IsBlurred = blurR18 && preview.IsR18,
                IsLocalFavorite = _favoritesService.IsFavorite(work.Id),
            };
            collection.Add(vm);
            _ = vm.LoadThumbnailAsync(_imageLoader, ct: ct);
        }

        StatusMessage = CanLoadMore
            ? $"Loaded {loadedOffset} / {TotalCount}"
            : $"{collection.Count} bookmarks";
        return loadedOffset;
    }

    // ── Local favorites ────────────────────────────────────────────────────
    public void ReloadLocalFavoritesPublic() => ReloadLocalFavorites();
    private void ReloadLocalFavorites()
    {
        LocalFavorites.Clear();
        FilteredFavorites.Clear();

        // Rebuild folder list
        AvailableFolders.Clear();
        foreach (var f in _favoritesService.GetAllFolders())
            AvailableFolders.Add(f);

        var blurR18 = _settingsService.Current.BlurR18Content;
        var missingThumbnails = new List<string>();
        foreach (var entry in _favoritesService.GetAll())
        {
            var preview = entry.ToArtworkPreview();
            var vm = new ArtworkCardViewModel(preview)
            {
                IsBlurred       = blurR18 && preview.IsR18,
                IsLocalFavorite = true,
            };
            LocalFavorites.Add(vm);
            if (string.IsNullOrWhiteSpace(preview.ThumbnailUrl))
                missingThumbnails.Add(vm.Id);
            else
                _ = vm.LoadThumbnailAsync(_imageLoader);
        }

        StatusMessage = LocalFavorites.Count > 0
            ? $"{LocalFavorites.Count} local favorites"
            : "No local favorites yet — right-click any artwork and choose ★ Add to favorites";
        UpdateFiltered();
        OnPropertyChanged(nameof(HasFavorites));

        // Repair favorites that were saved with no thumbnail (a past bug in the single-artwork
        // detail lookup left ThumbnailUrl null for artworks opened via a Hoshi "Open" action).
        // Re-fetch each one's detail once and backfill the URL so it's fixed permanently —
        // LocalFavoritesService.Changed fires per repair and re-runs this method, which then
        // picks up the newly-populated URL and loads the thumbnail normally.
        if (missingThumbnails.Count > 0)
            _ = RepairMissingThumbnailsAsync(missingThumbnails);
    }

    private async Task RepairMissingThumbnailsAsync(List<string> artworkIds)
    {
        foreach (var id in artworkIds)
        {
            try
            {
                var detail = await _pixivClient.GetArtworkDetailAsync(id);
                if (!string.IsNullOrEmpty(detail?.ThumbnailUrl))
                    _favoritesService.UpdateThumbnailUrl(id, detail.ThumbnailUrl);
            }
            catch { /* best-effort repair — leave it blank if the artwork is gone/private now */ }
        }
    }

    public void ToggleFavorite(ArtworkCardViewModel card)
    {
        _favoritesService.Toggle(card.Artwork);
        card.IsLocalFavorite = _favoritesService.IsFavorite(card.Id);
        // Sync IsLocalFavorite across all loaded collections
        SyncFavoriteFlag(card.Id, card.IsLocalFavorite);
    }

    private void SyncFavoriteFlag(string id, bool value)
    {
        foreach (var c in PublicBookmarks.Concat(PrivateBookmarks))
            if (c.Id == id) c.IsLocalFavorite = value;
    }

    // ── Filter ─────────────────────────────────────────────────────────────
    private void UpdateFiltered()
    {
        ApplyFilter(PublicBookmarks,  FilteredPublic);
        ApplyFilter(PrivateBookmarks, FilteredPrivate);
        ApplyFilter(LocalFavorites,   FilteredFavorites, applyFolder: true);

        // Tab-aware status message
        switch (SelectedTabIndex)
        {
            case 0:
                var pubHidden = !ShowR18 ? PublicBookmarks.Count(a => a.Artwork.IsR18) : 0;
                StatusMessage = pubHidden > 0
                    ? $"{FilteredPublic.Count} public bookmarks  ·  {pubHidden} R-18 hidden — click R-18 to show"
                    : $"{FilteredPublic.Count} public bookmarks";
                break;
            case 1:
                var privHidden = !ShowR18 ? PrivateBookmarks.Count(a => a.Artwork.IsR18) : 0;
                StatusMessage = privHidden > 0
                    ? $"{FilteredPrivate.Count} private bookmarks  ·  {privHidden} R-18 hidden — click R-18 to show"
                    : $"{FilteredPrivate.Count} private bookmarks";
                break;
            case 2:
                StatusMessage = $"{FilteredFavorites.Count} local favorites";
                break;
        }
    }

    private void ApplyFilter(
        ObservableCollection<ArtworkCardViewModel> src,
        ObservableCollection<ArtworkCardViewModel> dst,
        bool applyFolder = false)
    {
        var items = src.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(TagFilter))
            items = items.Where(a => a.Tags.Any(t =>
                t.Contains(TagFilter, StringComparison.OrdinalIgnoreCase)));
        if (!ShowR18)
            items = items.Where(a => !a.Artwork.IsR18);
        if (applyFolder && !string.IsNullOrWhiteSpace(FolderFilter))
            items = items.Where(a => _favoritesService.GetFolder(a.Id) == FolderFilter);

        items = SortMode switch
        {
            BookmarkSortMode.NewestPosted => items.OrderByDescending(a => a.Id),
            BookmarkSortMode.OldestPosted => items.OrderBy(a => a.Id),
            BookmarkSortMode.TitleAZ      => items.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase),
            BookmarkSortMode.TitleZA      => items.OrderByDescending(a => a.Title, StringComparer.OrdinalIgnoreCase),
            BookmarkSortMode.MostPages    => items.OrderByDescending(a => a.PageCount),
            _                             => items, // Default = API order (newest bookmarked first)
        };

        dst.Clear();
        foreach (var a in items) dst.Add(a);
    }

    // ── View-option commands ───────────────────────────────────────────────
    [RelayCommand] public void SetFixedHeight()   { IsFixedHeight = true;  IsNaturalHeight = false; }
    [RelayCommand] public void SetNaturalHeight() { IsFixedHeight = false; IsNaturalHeight = true;  }
    [RelayCommand] public void SetGridView()      { IsGridView = true;  IsListView = false; }
    [RelayCommand] public void SetListView()      { IsGridView = false; IsListView = true;  }

    // ── Folder commands ────────────────────────────────────────────────────
    [RelayCommand] public void SetFolder(string? folder) => FolderFilter = folder ?? string.Empty;

    public void SetFolderForCard(ArtworkCardViewModel card, string? folder)
    {
        _favoritesService.SetFolder(card.Id, folder);
        UpdateFiltered();
        // Rebuild folder list
        AvailableFolders.Clear();
        foreach (var f in _favoritesService.GetAllFolders()) AvailableFolders.Add(f);
    }

    public string? GetFolderForCard(string id) => _favoritesService.GetFolder(id);

    partial void OnCardSizeChanged(int v)
    {
        OnPropertyChanged(nameof(FixedCardTotalHeight));
        if (_settingsService.Current.CardSize != v)
            _settingsService.Update(s => s.CardSize = v);
    }
    partial void OnIsFixedHeightChanged(bool v)
    {
        _settingsService.Update(s => s.BookmarksCardHeightMode = v ? "Fixed" : "Natural");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnIsNaturalHeightChanged(bool v)
    {
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnShowPreviewChanged(bool _) { }
    partial void OnIsGridViewChanged(bool v)
    {
        _settingsService.Update(s => s.BookmarksViewMode = v ? "Grid" : "List");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnIsListViewChanged(bool v)
    {
        if (v) _settingsService.Update(s => s.BookmarksViewMode = "List");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnBrowsePanelWidthChanged(double v)
        => _settingsService.Update(s => s.BrowsePanelWidth = v);
    partial void OnShowTagsChanged(bool v)       => _settingsService.Update(s => s.BookmarksShowTags        = v);
    partial void OnShowInfoChanged(bool v)       => _settingsService.Update(s => s.BookmarksShowInfo        = v);
    partial void OnShowR18Changed(bool _)        { _settingsService.Update(s => s.BookmarksShowR18 = ShowR18); UpdateFiltered(); }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        switch (SelectedTabIndex)
        {
            case 0: await LoadTabAsync(0); break;
            case 1: await LoadTabAsync(1); break;
            case 2: ReloadLocalFavorites(); break;
        }
    }

    // ── Download commands ─────────────────────────────────────────────────────
    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var selected = ActiveCollection.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            await _dialogService.ShowMessageAsync("No Selection", "Select one or more bookmarks first.");
            return;
        }

        var tabName = SelectedTabIndex switch { 0 => "public", 1 => "private", _ => "favorites" };
        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Download Selected",
            $"Download {selected.Count} selected {tabName} bookmarks?");
        if (!confirmed) return;

        await QueueArtworksAsync(selected, $"Selected {selected.Count} {tabName} bookmarks");
    }

    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        var list = ActiveCollection;
        if (list.Count == 0)
        {
            await _dialogService.ShowMessageAsync("No Items", "No bookmarks to download in the current tab.");
            return;
        }

        var tabName = SelectedTabIndex switch { 0 => "public", 1 => "private", _ => "favorites" };
        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Download All",
            $"Download all {list.Count} {tabName} bookmarks?");
        if (!confirmed) return;

        await QueueArtworksAsync(list.ToList(), $"All {tabName} bookmarks ({list.Count})");
    }

    [RelayCommand]
    private async Task DownloadFolderAsync()
    {
        if (SelectedTabIndex != 2)
        {
            await _dialogService.ShowMessageAsync("Not Available", "Folder download is only available on the Local Favorites tab.");
            return;
        }
        if (AvailableFolders.Count == 0)
        {
            await _dialogService.ShowMessageAsync("No Folders", "No custom folders found in local favorites.");
            return;
        }

        var folder = string.IsNullOrWhiteSpace(FolderFilter) ? null : FolderFilter;
        if (folder == null)
        {
            await _dialogService.ShowMessageAsync("No Folder Selected", "Use the folder sidebar to select a folder first.");
            return;
        }

        var items = FilteredFavorites
            .Where(c => string.Equals(_favoritesService.GetFolder(c.Id), folder, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (items.Count == 0)
        {
            await _dialogService.ShowMessageAsync("No Items", $"No favorites found in folder '{folder}'.");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Download Folder",
            $"Download {items.Count} favorites from folder '{folder}'?");
        if (!confirmed) return;

        await QueueArtworksAsync(items, $"Favorites — {folder}");
    }

    private async Task QueueArtworksAsync(List<ArtworkCardViewModel> cards, string jobName)
    {
        try
        {
            var targets = cards.Select(c => new DownloadTarget
            {
                TargetId     = c.Id,
                Name         = c.Title,
                ThumbnailUrl = c.ThumbnailUrl,
                UserName     = c.UserName,
                UserId       = c.UserId,
                Type         = TargetType.Artwork,
            }).ToList();

            await _downloadCoordinator.CreateJobAsync(
                DownloadJobType.BookmarkImage,
                jobName,
                targets,
                settingsOverride: null,
                startImmediately: true);

            StatusMessage = $"Queued {cards.Count} artworks — check History for progress.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to queue bookmark download");
            await _dialogService.ShowMessageAsync("Error", $"Failed to start download: {ex.Message}");
        }
    }
}
