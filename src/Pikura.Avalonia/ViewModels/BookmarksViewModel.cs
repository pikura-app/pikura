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
    private bool _isLoadingLiked;
    private bool _isLoadingCollections;
    private int _likedLoadGeneration;
    private int _loadedOffsetPublic;
    private int _loadedOffsetPrivate;

    // ── Tab ────────────────────────────────────────────────────────────────
    // 0 = Public  1 = Private  2 = Local Favorites  3 = Liked  4 = Collections
    [ObservableProperty] private int _selectedTabIndex = 2; 
    public bool IsPublicTab      => SelectedTabIndex == 0;
    public bool IsPrivateTab     => SelectedTabIndex == 1;
    public bool IsFavoritesTab   => SelectedTabIndex == 2;
    public bool IsLikedTab       => SelectedTabIndex == 3;
    /// <summary>Bookmarked Pixiv Collections (the collection itself, not artworks inside one) —
    /// distinct from every other tab, which lists bookmarked/liked/favorited artworks.</summary>
    public bool IsCollectionsTab => SelectedTabIndex == 4;

    // ── Status ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Select a tab to load bookmarks";
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private int _totalCount;

    // ── Collections ────────────────────────────────────────────────────────
    public ObservableCollection<ArtworkCardViewModel> PublicBookmarks { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> PrivateBookmarks { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> LocalFavorites { get; } = [];

    // Fast O(1) lookup for "is this artwork ID one of my Pixiv bookmarks" — kept in sync with
    // PublicBookmarks/PrivateBookmarks. We deliberately do NOT check bookmark status per-card
    // everywhere in the app (that would mean one API call per thumbnail, which doesn't scale),
    // but once the Public/Private tabs have been loaded at least once this session (or a
    // bookmark add/remove happens), this cache lets any newly-constructed ArtworkCardViewModel
    // — in Gallery, Discover, wherever — pick up the correct badge for free.
    private readonly HashSet<string> _bookmarkedIds = new();
    private readonly HashSet<string> _privateBookmarkedIds = new();

    /// <summary>True if the given artwork ID is known to be bookmarked (from whatever has been
    /// loaded/synced into PublicBookmarks/PrivateBookmarks so far this session).</summary>
    public bool IsKnownBookmarked(string? id, out bool isPrivate)
    {
        isPrivate = false;
        if (string.IsNullOrEmpty(id)) return false;
        if (_privateBookmarkedIds.Contains(id)) { isPrivate = true; return true; }
        return _bookmarkedIds.Contains(id);
    }

    private void RebuildBookmarkIdCache()
    {
        _bookmarkedIds.Clear();
        foreach (var c in PublicBookmarks) _bookmarkedIds.Add(c.Id);
        foreach (var c in PrivateBookmarks) _bookmarkedIds.Add(c.Id);
        _privateBookmarkedIds.Clear();
        foreach (var c in PrivateBookmarks) _privateBookmarkedIds.Add(c.Id);
    }
    /// <summary>Every artwork Liked via Pikura's Like action (from SettingsService.PixivLikedArtworkIds),
    /// regardless of whether it's also bookmarked — this is the full liked list, not a filter over bookmarks.</summary>
    public ObservableCollection<ArtworkCardViewModel> LikedArtworks { get; } = [];

    /// <summary>Pixiv Collections the user has bookmarked (the collection itself, not any
    /// artwork inside it) — see <see cref="PixivClient.GetBookmarkedCollectionsAsync"/>.</summary>
    public ObservableCollection<CollectionTileViewModel> BookmarkedCollections { get; } = [];
    [ObservableProperty] private bool _isLoadingCollectionsUi;
    partial void OnIsLoadingCollectionsUiChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyCollectionsMessage));
    public bool HasBookmarkedCollections => BookmarkedCollections.Count > 0;
    public bool ShowEmptyCollectionsMessage => !HasBookmarkedCollections && !IsLoadingCollectionsUi;

    public ObservableCollection<ArtworkCardViewModel> FilteredPublic { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> FilteredPrivate { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> FilteredFavorites { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> FilteredLiked { get; } = [];

    public bool HasPublic => PublicBookmarks.Count > 0;
    public bool HasPrivate => PrivateBookmarks.Count > 0;
    public bool HasFavorites => LocalFavorites.Count > 0;
    public bool HasLiked => LikedArtworks.Count > 0;

    // ── View options ───────────────────────────────────────────────────────
    [ObservableProperty] private int _cardSize = 180;
    [ObservableProperty] private bool _isFixedHeight = true;
    [ObservableProperty] private bool _isNaturalHeight;
    [ObservableProperty] private bool _isGridView = true;
    [ObservableProperty] private bool _isListView;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showBadges = true;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private bool _showR18;
    [ObservableProperty] private double _browsePanelWidth = 450;

    // ── Folder filter (Local Favorites) ───────────────────────────────────
    [ObservableProperty] private string _folderFilter = string.Empty;
    partial void OnFolderFilterChanged(string value) => UpdateFiltered();
    public ObservableCollection<string> AvailableFolders { get; } = [];

    public double FixedCardTotalHeight => CardSize;
    public bool ShowR18Buttons => _settingsService.Current.R18Mode != R18Mode.Off;
    public GalleryViewModel GalleryVm { get; }
    public string ViewerSourceKey => $"Bookmarks:{SelectedTabIndex}:{ShowR18}:{TagFilter}:{FolderFilter}";
    public bool HasTabs => GalleryVm.HasTabs;
    [ObservableProperty] private bool _isViewerExpanded;
    partial void OnIsViewerExpandedChanged(bool value) { OnPropertyChanged(nameof(IsViewerFullScreen)); OnPropertyChanged(nameof(ShowGridLayer)); OnPropertyChanged(nameof(PublicTabVisible)); OnPropertyChanged(nameof(PrivateTabVisible)); OnPropertyChanged(nameof(FavoritesTabVisible)); OnPropertyChanged(nameof(LikedTabVisible)); OnPropertyChanged(nameof(CollectionsTabVisible)); }
    /// <summary>True when the viewer is expanded to fill the full content area.</summary>
    public bool IsViewerFullScreen => IsViewerExpanded;
    /// <summary>True when the artwork grid should be visible.</summary>
    public bool ShowGridLayer => !IsViewerExpanded;
    public bool PublicTabVisible => IsPublicTab && ShowGridLayer;
    public bool PrivateTabVisible => IsPrivateTab && ShowGridLayer;
    public bool FavoritesTabVisible => IsFavoritesTab && ShowGridLayer;
    public bool LikedTabVisible => IsLikedTab && ShowGridLayer;
    public bool CollectionsTabVisible => IsCollectionsTab && ShowGridLayer;

    // Grid view mode combined with height mode (for ScrollViewer visibility)
    public bool ShowFixedGrid => IsFixedHeight && IsGridView;
    public bool ShowNaturalGrid => IsNaturalHeight && IsGridView;

    // ── Selection mode (all tabs) ──────────────────────────────────────────
    [ObservableProperty] private bool _isSelectionMode;

    // Unified selection helpers — works across all four tabs
    private ObservableCollection<ArtworkCardViewModel> ActiveCollection => SelectedTabIndex switch
    {
        0 => FilteredPublic,
        1 => FilteredPrivate,
        3 => FilteredLiked,
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
        OnPropertyChanged(nameof(CanViewSelectedAsCollage));
        ViewSelectedAsCollageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanViewSelectedInNewTabs));
        ViewSelectedInNewTabsCommand.NotifyCanExecuteChanged();
    }

    public bool CanViewSelectedAsCollage => GalleryVm.CanCollage(SelectedCount);

    [RelayCommand(CanExecute = nameof(CanViewSelectedAsCollage))]
    private void ViewSelectedAsCollage()
    {
        var selected = ActiveCollection.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;
        GalleryVm.AddSelectedToCollage(selected);
    }

    public bool CanViewSelectedInNewTabs => SelectedCount >= 1;

    [RelayCommand(CanExecute = nameof(CanViewSelectedInNewTabs))]
    private void ViewSelectedInNewTabs()
    {
        var selected = ActiveCollection.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;
        foreach (var card in selected)
            GalleryVm.OpenInNewTab(card, selected, selected.Count, source: ViewerSourceKey);
        ShowPreview = true;
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
    partial void OnSortModeChanged(BookmarkSortMode value) => UpdateFiltered();

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
    partial void OnTagFilterChanged(string value) => UpdateFiltered();

    // ── Constructor ────────────────────────────────────────────────────────
    public BookmarksViewModel(
        PixivClient pixivClient,
        PixivImageLoader imageLoader,
        SettingsService settingsService,
        LocalFavoritesService favoritesService,
        GalleryViewModel galleryVm,
        DownloadCoordinator downloadCoordinator,
        DialogService dialogService,
        ILogger<BookmarksViewModel>? logger = null)
        : base((ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
        _pixivClient = pixivClient;
        _imageLoader = imageLoader;
        _settingsService = settingsService;
        _favoritesService = favoritesService;
        GalleryVm = galleryVm;
        _downloadCoordinator = downloadCoordinator;
        _dialogService = dialogService;

        PublicBookmarks.CollectionChanged += (_, _) => RebuildBookmarkIdCache();
        PrivateBookmarks.CollectionChanged += (_, _) => RebuildBookmarkIdCache();

        var s = settingsService.Current;
        _isFixedHeight   = s.BookmarksCardHeightMode != "Natural";
        _isNaturalHeight = s.BookmarksCardHeightMode == "Natural";
        _isGridView      = s.BookmarksViewMode != "List";
        _isListView      = s.BookmarksViewMode == "List";
        _cardSize        = s.CardSize;
        _showTags        = s.BookmarksShowTags;
        _showInfo        = s.BookmarksShowInfo;
        _showBadges      = s.ShowBadges;
        _showR18         = s.BookmarksShowR18;
        _browsePanelWidth = s.BrowsePanelWidth >= 200 ? s.BrowsePanelWidth : 450;

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
            OnPropertyChanged(nameof(LikedTabVisible));
            if (!GalleryVm.HasTabs) IsViewerExpanded = false;
        }

        GalleryVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.HasTabs))
                NotifyViewerState();
            if (e.PropertyName is nameof(GalleryViewModel.IsCollageMode)
                               or nameof(GalleryViewModel.HasStoredCollage)
                               or nameof(GalleryViewModel.CollageItems)
                               or nameof(GalleryViewModel.CanViewSelectedAsCollage))
            {
                OnPropertyChanged(nameof(CanViewSelectedAsCollage));
                ViewSelectedAsCollageCommand.NotifyCanExecuteChanged();
            }
        };
        GalleryVm.ViewerTabs.CollectionChanged += (_, _) => NotifyViewerState();
    }

    // ── Navigation entry point ─────────────────────────────────────────────
    public void OnNavigatedTo()
    {
        // Load public bookmarks if empty, or re-fetch if all existing cards have no
        // thumbnail yet (e.g., a previous load failed or was still using bad URLs).
        if (!_isLoadingPublic && (PublicBookmarks.Count == 0 || !PublicBookmarks.Any(vm => vm.Thumbnail is not null)))
            _ = LoadTabAsync(0);
        if (LocalFavorites.Count == 0)
            ReloadLocalFavorites();
    }

    /// <summary>Loads Collections the user has bookmarked on Pixiv. Unlike the artwork tabs,
    /// there's no local/liked concept here — this is purely a listing of the real Pixiv
    /// bookmark endpoint's results, rendered as collage tiles (same visual as the Collections
    /// browse page) so clicking one can jump straight into viewing it.</summary>
    [RelayCommand]
    public async Task LoadBookmarkedCollectionsAsync()
    {
        if (_isLoadingCollections) return;
        _isLoadingCollections = true;
        IsLoadingCollectionsUi = true;
        BookmarkedCollections.Clear();
        try
        {
            var self = await _pixivClient.ResolveSelfAsync();
            if (self == null) { StatusMessage = "Not signed in."; return; }

            var collections = await _pixivClient.GetBookmarkedCollectionsAsync(self.Value.UserId);
            foreach (var c in collections)
            {
                var tile = new CollectionTileViewModel(c);
                BookmarkedCollections.Add(tile);
                _ = LoadCollectionTileThumbnailAsync(tile);
            }
            OnPropertyChanged(nameof(HasBookmarkedCollections));
            OnPropertyChanged(nameof(ShowEmptyCollectionsMessage));
            if (collections.Count == 0)
                StatusMessage = "No bookmarked collections found.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load bookmarked collections");
            StatusMessage = $"Failed to load bookmarked collections: {ex.Message}";
        }
        finally
        {
            _isLoadingCollections = false;
            IsLoadingCollectionsUi = false;
        }
    }

    /// <summary>Mirrors <c>CollectionsViewModel.LoadTileThumbnailAsync</c> — a 2x2 assortment of
    /// real work thumbnails, since the listing endpoint's own thumbnail URL
    /// (embed.pixiv.net) reliably 400s.</summary>
    private async Task LoadCollectionTileThumbnailAsync(CollectionTileViewModel tile)
    {
        try
        {
            var urls = await _pixivClient.GetCollectionThumbnailsAsync(tile.Id, maxCount: 4);
            if (urls.Count > 0) await tile.LoadCollageThumbnailsAsync(_imageLoader, urls);
        }
        catch { /* thumbnail is decorative — non-fatal */ }
    }

    /// <summary>Raised when the user clicks a bookmarked-Collection tile — the View subscribes
    /// to this (in code, not a XAML event attribute — see the comment on
    /// <see cref="OpenBookmarkedCollectionAsync"/> for why) and switches to the Collections tab.</summary>
    public event Action<CollectionTileViewModel>? RequestOpenCollection;

    /// <summary>
    /// Bound directly from the tile's Button (ancestor-DataContext Command binding) rather than
    /// a code-behind PointerPressed/Tapped event handler — the latter reliably fails to compile
    /// here with a baffling Avalonia XAML-compiler error (AVLN3000, "unable to find suitable
    /// setter... for argument System.String") that doesn't reproduce in the otherwise-identical
    /// Collections browse tiles. Root cause unconfirmed; this sidesteps it entirely.
    /// </summary>
    [RelayCommand]
    private void OpenBookmarkedCollection(CollectionTileViewModel? tile)
    {
        if (tile != null) RequestOpenCollection?.Invoke(tile);
    }

    // ── Tab switching ──────────────────────────────────────────────────────
    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPublicTab));
        OnPropertyChanged(nameof(IsPrivateTab));
        OnPropertyChanged(nameof(IsFavoritesTab));
        OnPropertyChanged(nameof(IsLikedTab));
        OnPropertyChanged(nameof(IsCollectionsTab));
        OnPropertyChanged(nameof(PublicTabVisible));
        OnPropertyChanged(nameof(PrivateTabVisible));
        OnPropertyChanged(nameof(FavoritesTabVisible));
        OnPropertyChanged(nameof(LikedTabVisible));
        OnPropertyChanged(nameof(CollectionsTabVisible));
        switch (value)
        {
            case 0 when !_isLoadingPublic && (PublicBookmarks.Count == 0 || !PublicBookmarks.Any(vm => vm.Thumbnail is not null)):
                _ = LoadTabAsync(0);
                break;
            case 0:
                UpdateFiltered();
                break;
            case 1 when !_isLoadingPrivate && (PrivateBookmarks.Count == 0 || !PrivateBookmarks.Any(vm => vm.Thumbnail is not null)):
                _ = LoadTabAsync(1);
                break;
            case 1:
                UpdateFiltered();
                break;
            case 2:
                ReloadLocalFavorites();
                break;
            case 3 when LikedArtworks.Count == 0 && !_isLoadingLiked:
                _ = LoadLikedArtworksAsync();
                break;
            case 3:
                UpdateFiltered();
                break;
            case 4 when BookmarkedCollections.Count == 0 && !_isLoadingCollections:
                _ = LoadBookmarkedCollectionsAsync();
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

        // Bookmarks sometimes arrive with no usable thumbnail URL (or a custom-thumb crop
        // that won't load). Fetch each author's profile/illusts metadata once per batch to
        // backfill a proper /img-master/ URL for those works.
        await BackfillBookmarkThumbnailUrlsAsync(response.Works, ct);

        var blurR18 = _settingsService.Current.BlurR18Content;
        var likedIds = _settingsService.Current.PixivLikedArtworkIds;
        foreach (var work in response.Works)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(work.Id)) continue;
            var preview = work.ToArtworkPreview();
            var vm = new ArtworkCardViewModel(preview)
            {
                IsBlurred = blurR18 && preview.IsR18,
                IsLocalFavorite = _favoritesService.IsFavorite(work.Id),
                IsLiked = likedIds.Contains(work.Id),
                // Trivially true here — this list IS the bookmark list being fetched.
                IsPixivBookmarked = true,
                IsPixivPrivateBookmark = hidden,
            };
            collection.Add(vm);
            _ = vm.LoadThumbnailAsync(_imageLoader, ct: ct);
        }

        StatusMessage = CanLoadMore
            ? $"Loaded {loadedOffset} / {TotalCount}"
            : $"{collection.Count} bookmarks";
        return loadedOffset;
    }

    private async Task BackfillBookmarkThumbnailUrlsAsync(List<BookmarkedArtwork> works, CancellationToken ct)
    {
        var needsBackfill = works
            .Where(w => !string.IsNullOrEmpty(w.Id) && !string.IsNullOrEmpty(w.UserId)
                && (string.IsNullOrWhiteSpace(w.Url) || w.Url.Contains("/custom-thumb/")))
            .GroupBy(w => w.UserId)
            .ToList();

        foreach (var group in needsBackfill)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ids = group.Select(w => w.Id).Distinct().ToList();
                var meta = await _pixivClient.GetArtworksMetadataAsync(group.Key, ids, ct).ConfigureAwait(false);
                foreach (var work in group)
                {
                    if (meta.TryGetValue(work.Id, out var preview)
                        && !string.IsNullOrWhiteSpace(preview.ThumbnailUrl))
                    {
                        work.Url = preview.ThumbnailUrl;
                    }
                }
            }
            catch { /* best-effort metadata lookup — keep the original URL if it fails */ }
        }
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
        var likedIds = _settingsService.Current.PixivLikedArtworkIds;
        var missingThumbnails = new List<string>();
        foreach (var entry in _favoritesService.GetAll())
        {
            var preview = entry.ToArtworkPreview();
            var vm = new ArtworkCardViewModel(preview)
            {
                IsBlurred       = blurR18 && preview.IsR18,
                IsLocalFavorite = true,
                IsLiked         = likedIds.Contains(preview.Id),
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

    // ── Liked artworks (all Pikura Likes, not just liked bookmarks) ────────
    /// <summary>
    /// Loads every artwork the user has Liked via Pikura, regardless of whether it's also
    /// bookmarked. Unlike Public/Private, there's no Pixiv listing endpoint for "things I've
    /// liked" — we only have the ID list persisted in settings — so each artwork's detail is
    /// fetched individually (bounded concurrency) to get its thumbnail/title/stats.
    /// </summary>
    [RelayCommand]
    public async Task LoadLikedArtworksAsync()
    {
        // Generation counter — belt-and-suspenders against any overlapping call ever applying
        // its results after a newer call has started (which previously showed each liked
        // artwork twice). Whichever call is still "current" when its fetch finishes is the
        // only one allowed to touch LikedArtworks.
        var myGeneration = ++_likedLoadGeneration;
        _isLoadingLiked = true;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsLoading = true;
        LikedArtworks.Clear();
        FilteredLiked.Clear();

        try
        {
            // Newest-liked-first: IDs are appended to the list as they're liked.
            // Distinct() guards against stale duplicate IDs some earlier sessions may have
            // persisted (before de-duplication was added to the Like action) — without it,
            // every duplicate ID re-fetched and re-added its own separate card.
            var likedIds = _settingsService.Current.PixivLikedArtworkIds;
            var deduped = likedIds.Distinct().ToList();
            if (deduped.Count != likedIds.Count)
            {
                likedIds.Clear();
                likedIds.AddRange(deduped);
                _settingsService.Save();
            }
            var ids = deduped.AsEnumerable().Reverse().ToList();
            if (ids.Count == 0)
            {
                StatusMessage = "No liked artworks yet — the ♥ Like button on an artwork adds it here.";
                return;
            }

            StatusMessage = $"Loading {ids.Count} liked artworks…";
            var blurR18 = _settingsService.Current.BlurR18Content;
            var results = new ArtworkCardViewModel?[ids.Count];

            using var gate = new SemaphoreSlim(8, 8);
            await Task.WhenAll(ids.Select((id, i) => Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var detailTask = _pixivClient.GetArtworkDetailAsync(id, ct);
                    var bookmarkTask = _pixivClient.GetBookmarkStateAsync(id, ct);
                    await Task.WhenAll(detailTask, bookmarkTask).ConfigureAwait(false);
                    var detail = await detailTask.ConfigureAwait(false);
                    if (detail is null) return;
                    var bookmark = await bookmarkTask.ConfigureAwait(false);
                    var preview = new ArtworkPreview
                    {
                        Id = detail.IllustId ?? id,
                        Title = detail.IllustTitle ?? id,
                        UserName = detail.UserName ?? string.Empty,
                        UserId = detail.UserId ?? string.Empty,
                        ThumbnailUrl = detail.ThumbnailUrl,
                        PageCount = detail.PageCount > 0 ? detail.PageCount : 1,
                        IllustType = detail.IllustType,
                        XRestrict = detail.XRestrict,
                        AiType = detail.AiType,
                        Width = detail.Width,
                        Height = detail.Height,
                        BookmarkCount = detail.BookmarkCount,
                        LikeCount = detail.LikeCount,
                        ViewCount = detail.ViewCount,
                        Tags = detail.Tags?.Tags?.Select(t => t.Tag ?? string.Empty).ToList() ?? []
                    };
                    results[i] = new ArtworkCardViewModel(preview)
                    {
                        IsBlurred = blurR18 && preview.IsR18,
                        IsLocalFavorite = _favoritesService.IsFavorite(id),
                        IsLiked = true,
                        // A liked artwork may also be bookmarked on Pixiv — check its live
                        // bookmark state so the bookmark badge shows here too, not just on
                        // the Public/Private tabs.
                        IsPixivBookmarked = bookmark?.IsBookmarked ?? false,
                        IsPixivPrivateBookmark = bookmark?.IsPrivate ?? false,
                        PixivBookmarkId = bookmark?.BookmarkId,
                    };
                }
                catch { /* artwork deleted/private since being liked — skip it */ }
                finally { gate.Release(); }
            }, ct))).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            Logger.LogInformation("[LikedArtworks] generation={Gen} currentGen={CurGen} idsCount={IdsCount} resultsNonNull={ResultsCount}",
                myGeneration, _likedLoadGeneration, ids.Count, results.Count(r => r != null));
            if (myGeneration != _likedLoadGeneration) return; // a newer call superseded this one

            LikedArtworks.Clear(); // in case a superseded call added anything before being superseded
            var seen = new HashSet<string>();
            foreach (var vm in results)
            {
                if (vm is null || !seen.Add(vm.Id)) continue;
                LikedArtworks.Add(vm);
                _ = vm.LoadThumbnailAsync(_imageLoader, ct: ct);
            }

            Logger.LogInformation("[LikedArtworks] after add: LikedArtworks.Count={Count} ids={Ids}",
                LikedArtworks.Count, string.Join(",", LikedArtworks.Select(a => a.Id)));
            StatusMessage = $"{LikedArtworks.Count} liked artworks";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load liked artworks");
            StatusMessage = $"Failed to load liked artworks: {ex.Message}";
        }
        finally
        {
            _isLoadingLiked = false;
            IsLoading = false;
            if (myGeneration == _likedLoadGeneration) UpdateFiltered();
            OnPropertyChanged(nameof(HasLiked));
        }
    }

    public void ToggleFavorite(ArtworkCardViewModel card)
    {
        _favoritesService.Toggle(card.Artwork);
        card.IsLocalFavorite = _favoritesService.IsFavorite(card.Id);
        // Sync IsLocalFavorite across all loaded collections
        SyncFavoriteFlag(card.Id, card.IsLocalFavorite);
    }

    /// <summary>Public alias so the artwork viewer can push a local-favorite change into every
    /// card already loaded across the Bookmarks tabs (Public/Private/Favorites/Liked).</summary>
    public void SyncFavoriteEverywhere(string id, bool value) => SyncFavoriteFlag(id, value);

    private void SyncFavoriteFlag(string id, bool value)
    {
        foreach (var c in AllLoadedCards())
            if (c.Id == id) c.IsLocalFavorite = value;
    }

    private IEnumerable<ArtworkCardViewModel> AllLoadedCards() =>
        PublicBookmarks.Concat(PrivateBookmarks).Concat(LocalFavorites).Concat(LikedArtworks);

    /// <summary>
    /// Called by the artwork viewer right after a Like/Unlike succeeds, so the Liked tab (and
    /// the heart badge on any matching card already loaded elsewhere) updates immediately
    /// instead of requiring a manual Refresh.
    /// </summary>
    public void SyncLiked(ArtworkCardViewModel card, bool liked)
    {
        if (string.IsNullOrEmpty(card.Id)) return;
        Logger.LogInformation("[SyncLiked] id={Id} liked={Liked} alreadyInLikedArtworks={Already} likedArtworksCountBefore={CountBefore}",
            card.Id, liked, LikedArtworks.Any(a => a.Id == card.Id), LikedArtworks.Count);

        foreach (var c in AllLoadedCards())
            if (c.Id == card.Id) c.IsLiked = liked;

        if (liked)
        {
            if (!LikedArtworks.Any(a => a.Id == card.Id))
            {
                var clone = new ArtworkCardViewModel(card.Artwork)
                {
                    IsBlurred = card.IsBlurred,
                    IsLocalFavorite = card.IsLocalFavorite,
                    IsPixivBookmarked = card.IsPixivBookmarked,
                    IsPixivPrivateBookmark = card.IsPixivPrivateBookmark,
                    PixivBookmarkId = card.PixivBookmarkId,
                    IsLiked = true,
                };
                LikedArtworks.Insert(0, clone); // newest-liked-first, matches full-reload order
                _ = clone.LoadThumbnailAsync(_imageLoader);
            }
        }
        else
        {
            var existing = LikedArtworks.FirstOrDefault(a => a.Id == card.Id);
            if (existing != null) LikedArtworks.Remove(existing);
        }

        Logger.LogInformation("[SyncLiked] after: likedArtworksCountAfter={CountAfter} selectedTab={Tab}", LikedArtworks.Count, SelectedTabIndex);
        UpdateFiltered();
        OnPropertyChanged(nameof(HasLiked));
    }

    /// <summary>
    /// Called by the artwork viewer right after a bookmark add/remove/privacy-change succeeds,
    /// so the Public/Private tabs (which literally ARE your bookmark lists) and the bookmark
    /// badge on any matching card elsewhere update immediately instead of requiring Refresh.
    /// </summary>
    public void SyncBookmarked(ArtworkCardViewModel card, bool bookmarked, bool isPrivate, string? bookmarkId)
    {
        if (string.IsNullOrEmpty(card.Id)) return;

        foreach (var c in AllLoadedCards())
            if (c.Id == card.Id)
            {
                c.IsPixivBookmarked = bookmarked;
                c.IsPixivPrivateBookmark = isPrivate;
                c.PixivBookmarkId = bookmarkId;
            }

        var targetList = isPrivate ? PrivateBookmarks : PublicBookmarks;
        var otherList = isPrivate ? PublicBookmarks : PrivateBookmarks;

        // Privacy may have flipped — make sure it's not left sitting in the other list too.
        var inOther = otherList.FirstOrDefault(a => a.Id == card.Id);
        if (inOther != null) otherList.Remove(inOther);

        var inTarget = targetList.FirstOrDefault(a => a.Id == card.Id);
        if (bookmarked)
        {
            if (inTarget == null)
            {
                var clone = new ArtworkCardViewModel(card.Artwork)
                {
                    IsBlurred = card.IsBlurred,
                    IsLocalFavorite = card.IsLocalFavorite,
                    IsLiked = card.IsLiked,
                    IsPixivBookmarked = true,
                    IsPixivPrivateBookmark = isPrivate,
                    PixivBookmarkId = bookmarkId,
                };
                targetList.Insert(0, clone); // newest-bookmarked-first
                _ = clone.LoadThumbnailAsync(_imageLoader);
            }
        }
        else if (inTarget != null)
        {
            targetList.Remove(inTarget);
        }

        UpdateFiltered();
        OnPropertyChanged(nameof(HasPublic));
        OnPropertyChanged(nameof(HasPrivate));
    }

    // ── Filter ─────────────────────────────────────────────────────────────
    private void UpdateFiltered()
    {
        ApplyFilter(PublicBookmarks,  FilteredPublic);
        ApplyFilter(PrivateBookmarks, FilteredPrivate);
        ApplyFilter(LocalFavorites,   FilteredFavorites, applyFolder: true);
        ApplyFilter(LikedArtworks,    FilteredLiked);
        Logger.LogInformation("[LikedArtworks] UpdateFiltered: LikedArtworks.Count={Src} FilteredLiked.Count={Dst} filteredIds={Ids}",
            LikedArtworks.Count, FilteredLiked.Count, string.Join(",", FilteredLiked.Select(a => a.Id)));

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
            case 3:
                StatusMessage = $"{FilteredLiked.Count} liked artworks";
                break;
        }
    }

    private void ApplyFilter(
        ObservableCollection<ArtworkCardViewModel> src,
        ObservableCollection<ArtworkCardViewModel> dst,
        bool applyFolder = false)
    {
        // Defensive de-dup by ID — guards against any duplicate cards that might slip into a
        // source collection (e.g. an overlapping load), so the visible grid never shows the
        // same artwork twice even if the underlying collection briefly does.
        var items = src.DistinctBy(a => a.Id).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(TagFilter))
            items = items.Where(a => a.Tags.Any(t =>
                t.Contains(TagFilter, StringComparison.OrdinalIgnoreCase)));
        if (!ShowR18)
            items = items.Where(a => !a.Artwork.IsR18);
        // Unified blocklist filter for the Bookmarks tab
        items = items.Where(a => !_settingsService.Current.IsArtworkHidden("Bookmarks", a.UserId, a.UserName, a.Title, a.Tags));
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

        SyncCollection(dst, items.ToList());
    }

    /// <summary>
    /// Updates <paramref name="dst"/> in place to match <paramref name="desired"/> (same items,
    /// same order) without ever fully clearing it. A blunt Clear()+re-Add() briefly leaves the
    /// bound ItemsControl with zero items and then rebuilds every container from scratch — if an
    /// async thumbnail load (or another filter recompute) lands in that window, the custom
    /// MasonryPanel used for "Natural" mode can end up rendering leftover containers alongside
    /// the freshly-added ones, visually duplicating cards even though the source data was never
    /// actually duplicated. Diffing in place avoids that window entirely.
    /// </summary>
    private static void SyncCollection(ObservableCollection<ArtworkCardViewModel> dst, List<ArtworkCardViewModel> desired)
    {
        var desiredSet = new HashSet<ArtworkCardViewModel>(desired);
        for (int i = dst.Count - 1; i >= 0; i--)
            if (!desiredSet.Contains(dst[i])) dst.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < dst.Count && ReferenceEquals(dst[i], desired[i])) continue;
            var existingIndex = dst.IndexOf(desired[i]);
            if (existingIndex >= 0) dst.Move(existingIndex, i);
            else dst.Insert(i, desired[i]);
        }
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

    partial void OnCardSizeChanged(int value)
    {
        OnPropertyChanged(nameof(FixedCardTotalHeight));
        if (_settingsService.Current.CardSize != value)
            _settingsService.Update(s => s.CardSize = value);
    }
    partial void OnIsFixedHeightChanged(bool value)
    {
        _settingsService.Update(s => s.BookmarksCardHeightMode = value ? "Fixed" : "Natural");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnIsNaturalHeightChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnShowPreviewChanged(bool value) { }
    partial void OnIsGridViewChanged(bool value)
    {
        _settingsService.Update(s => s.BookmarksViewMode = value ? "Grid" : "List");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnIsListViewChanged(bool value)
    {
        if (value) _settingsService.Update(s => s.BookmarksViewMode = "List");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnBrowsePanelWidthChanged(double value)
        => _settingsService.Update(s => s.BrowsePanelWidth = value);
    partial void OnShowTagsChanged(bool value)       => _settingsService.Update(s => s.BookmarksShowTags        = value);
    partial void OnShowInfoChanged(bool value)       => _settingsService.Update(s => s.BookmarksShowInfo        = value);
    partial void OnShowBadgesChanged(bool value)     => _settingsService.Update(s => s.ShowBadges                = value);
    partial void OnShowR18Changed(bool value)        { _settingsService.Update(s => s.BookmarksShowR18 = ShowR18); UpdateFiltered(); }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        switch (SelectedTabIndex)
        {
            case 0: await LoadTabAsync(0); break;
            case 1: await LoadTabAsync(1); break;
            case 2: ReloadLocalFavorites(); break;
            case 3: await LoadLikedArtworksAsync(); break;
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

        var tabName = SelectedTabIndex switch { 0 => "public", 1 => "private", 3 => "liked", _ => "favorites" };
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

        var tabName = SelectedTabIndex switch { 0 => "public", 1 => "private", 3 => "liked", _ => "favorites" };
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
