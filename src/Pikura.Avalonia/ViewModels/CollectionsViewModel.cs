using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

/// <summary>
/// View-only browser for Pixiv "Collections" (the beta curation feature at pixiv.net/collection).
/// Paste a collection URL or ID to view its artwork, caption, and the same creator's other
/// collections. There's no dedicated Pixiv API for this — see
/// <see cref="PixivClient.GetCollectionAsync"/> for how the data is actually obtained (scraped
/// from the collection page's own embedded Next.js data, the same technique already used for
/// user search and CSRF tokens).
/// </summary>
public partial class CollectionsViewModel : ObservableObject
{
    private readonly PixivClient _pixivClient;
    private readonly PixivImageLoader _imageLoader;
    private readonly DownloadCoordinator _coordinator;
    private readonly SettingsService _settingsService;
    private readonly DialogService _dialogService;

    /// <summary>Full collection data for the currently-loaded collection — kept around so
    /// "Download this collection" doesn't need to re-fetch anything already on screen.</summary>
    private PixivCollection? _currentCollection;

    /// <summary>Cache of full collection data fetched while browsing, keyed by ID, so bulk
    /// download actions over the browse collage don't re-fetch a collection already loaded.</summary>
    private readonly Dictionary<string, PixivCollection> _browseCache = new();

    /// <summary>Unfiltered browse-tile lists — kept around so toggling <see cref="ShowR18"/>
    /// re-filters without re-fetching from Pixiv. <see cref="FeaturedCollections"/> and
    /// <see cref="FeaturedCollections"/> is the currently-visible (filtered) view over this;
    /// "All Collections" doesn't need an equivalent cache since it's paginated server-side.</summary>
    private readonly List<CollectionTileViewModel> _rawFeatured = [];

    [ObservableProperty] private string _collectionUrlOrId = string.Empty;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private bool _isViewerExpanded;
    /// <summary>Side-panel width — shares the same persisted setting as Gallery/Bookmarks'
    /// resizable panels (rather than a fixed 420px) so the panel can be widened when the
    /// viewer's header row (title/username/Follow button) is too cramped.</summary>
    [ObservableProperty] private double _panelWidth;
    public bool IsResizingPanel { get; set; }
    partial void OnPanelWidthChanged(double value)
    {
        if (IsResizingPanel) return;
        _settingsService.Update(s => s.BrowsePanelWidth = value);
    }
    [ObservableProperty] private bool _isDownloading;
    /// <summary>When true, every work downloads into one folder named after the collection.
    /// When false, downloads go through the normal global folder/filename template instead,
    /// same as any other artwork download.</summary>
    [ObservableProperty] private bool _useCollectionFolder = true;
    [ObservableProperty] private bool _isLoading;
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasNoWorks));
    [ObservableProperty] private string _statusMessage = "Paste a pixiv.net/collections/{id} URL (or just the ID) to view it, or browse below.";
    [ObservableProperty] private bool _hasLoaded;
    partial void OnHasLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCollectionDetail));
        OnPropertyChanged(nameof(HasNoWorks));
    }

    /// <summary>True while showing the Featured/All collage; false while viewing one specific
    /// collection's artwork.</summary>
    [ObservableProperty] private bool _isBrowsing = true;
    partial void OnIsBrowsingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCollectionDetail));
        OnPropertyChanged(nameof(HasNoWorks));
    }
    [ObservableProperty] private bool _isBrowseLoading;

    /// <summary>True once a specific collection has finished loading and turned out to have no
    /// artwork at all — shows a friendly empty state instead of leaving a blank gap above the
    /// comments section.</summary>
    public bool HasNoWorks => HasLoaded && !IsLoading && !IsBrowsing && Works.Count == 0;

    /// <summary>Content-rating filter for the browse collage — Collections carry Pixiv's own
    /// <c>xRestrict</c> flag so this filters the already-fetched tile lists without extra
    /// requests. Persisted separately from Gallery/Bookmarks' own R-18 toggles.</summary>
    [ObservableProperty] private bool _showR18;
    partial void OnShowR18Changed(bool value)
    {
        _settingsService.Update(s => s.CollectionsShowR18 = value);
        ApplyFeaturedFilter();
        // "All Collections" is filtered server-side (mode=safe/all) rather than client-side,
        // since "safe" mode doesn't return R-18 items at all — needs a fresh page 1, not a
        // re-filter of what's already loaded.
        if (UsePagination)
        {
            _ = GoToAllCollectionsPageAsync(1);
        }
        else
        {
            _allCollectionsOffset = 0;
            AllCollections.Clear();
            _ = LoadMoreAllCollectionsAsync();
        }
    }

    /// <summary>Gates the detail info bar (title/tags/counts/sibling collections) — true only
    /// once a specific collection has finished loading AND we're not back on the browse collage.
    /// Prevents the previous collection's header from lingering after "← Back to Browse".</summary>
    public bool ShowCollectionDetail => HasLoaded && !IsBrowsing;

    /// <summary>Whether the "Caption, tags & more from this creator" section is expanded.
    /// Replaces a plain Expander control, which rendered with clipped corners and low-contrast
    /// chrome in light mode; this drives a manual ToggleButton + collapsible panel instead.</summary>
    [ObservableProperty] private bool _showCaptionSection;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string? _caption;
    [ObservableProperty] private int _bookmarkCount;
    [ObservableProperty] private int _viewCount;
    [ObservableProperty] private string _tagsLabel = string.Empty;
    /// <summary>Whether the *currently open* collection itself is bookmarked on Pixiv (distinct
    /// from bookmarking any individual artwork inside it) — see
    /// <see cref="PixivClient.AddCollectionBookmarkAsync"/>.</summary>
    [ObservableProperty] private bool _isCollectionBookmarked;
    [ObservableProperty] private string? _collectionBookmarkId;
    [ObservableProperty] private bool _isTogglingBookmark;
    public ObservableCollection<string> TagsList { get; } = [];

    public ObservableCollection<ArtworkCardViewModel> Works { get; } = [];
    public ObservableCollection<CollectionTileViewModel> SiblingCollections { get; } = [];
    public ObservableCollection<CollectionTileViewModel> FeaturedCollections { get; } = [];
    public bool ShowFeaturedCollections => FeaturedCollections.Count > 0 && (!UsePagination || AllCollectionsPage == 1);
    public ObservableCollection<CollectionTileViewModel> AllCollections { get; } = [];

    /// <summary>Top-level comments on the Collection *itself* — distinct from any individual
    /// artwork's own comments. See <see cref="PixivClient.GetCollectionCommentsAsync"/>.</summary>
    public ObservableCollection<PixivComment> CollectionComments { get; } = [];
    [ObservableProperty] private bool _isLoadingComments;
    [ObservableProperty] private bool _isPostingComment;
    [ObservableProperty] private string _newCollectionComment = string.Empty;
    [ObservableProperty] private int _collectionCommentCount;

    public int SelectedCount => AllCollections.Count(c => c.IsSelected) + FeaturedCollections.Count(c => c.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>Selection state over <see cref="Works"/> — for downloading a subset of the
    /// currently-open collection's artwork, same idea as Gallery/Bookmarks' own selection.</summary>
    public int SelectedWorksCount => Works.Count(w => w.IsSelected);
    public bool HasWorksSelection => SelectedWorksCount > 0;
    public void NotifyWorksSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedWorksCount));
        OnPropertyChanged(nameof(HasWorksSelection));
        OnPropertyChanged(nameof(CanViewSelectedWorksAsCollage));
        ViewSelectedWorksAsCollageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanViewSelectedWorksInNewTabs));
        ViewSelectedWorksInNewTabsCommand.NotifyCanExecuteChanged();
    }

    // ── Works grid display options — mirrors Gallery/Bookmarks' Fixed/Natural, Tags, Info,
    // Badges, Size, Grid/List controls, applied to a collection's own artwork grid. ──────────
    [ObservableProperty] private int _cardSize = 180;
    [ObservableProperty] private bool _isFixedHeight = true;
    [ObservableProperty] private bool _isNaturalHeight;
    [ObservableProperty] private bool _isGridView = true;
    [ObservableProperty] private bool _isListView;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showBadges = true;
    public double FixedCardTotalHeight => CardSize;
    public bool ShowFixedGrid => IsFixedHeight && IsGridView;
    public bool ShowNaturalGrid => IsNaturalHeight && IsGridView;

    [RelayCommand] public void SetFixedHeight()   { IsFixedHeight = true;  IsNaturalHeight = false; }
    [RelayCommand] public void SetNaturalHeight() { IsFixedHeight = false; IsNaturalHeight = true;  }
    [RelayCommand] public void SetGridView()      { IsGridView = true;  IsListView = false; }
    [RelayCommand] public void SetListView()      { IsGridView = false; IsListView = true;  }

    partial void OnCardSizeChanged(int value)
    {
        OnPropertyChanged(nameof(FixedCardTotalHeight));
        if (_settingsService.Current.CardSize != value)
            _settingsService.Update(s => s.CardSize = value);
    }
    partial void OnIsFixedHeightChanged(bool value)
    {
        _settingsService.Update(s => s.CollectionsCardHeightMode = value ? "Fixed" : "Natural");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnIsNaturalHeightChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnIsGridViewChanged(bool value)
    {
        _settingsService.Update(s => s.CollectionsViewMode = value ? "Grid" : "List");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnIsListViewChanged(bool value)
    {
        if (value) _settingsService.Update(s => s.CollectionsViewMode = "List");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }
    partial void OnShowTagsChanged(bool value)   => _settingsService.Update(s => s.CollectionsShowTags = value);
    partial void OnShowInfoChanged(bool value)   => _settingsService.Update(s => s.CollectionsShowInfo = value);
    partial void OnShowBadgesChanged(bool value) => _settingsService.Update(s => s.ShowBadges = value);

    public GalleryViewModel GalleryVm => AppServices.Get<GalleryViewModel>();
    public string ViewerSourceKey => $"Collections:{_currentCollection?.Id}";

    public CollectionsViewModel(
        PixivClient pixivClient,
        PixivImageLoader imageLoader,
        DownloadCoordinator coordinator,
        SettingsService settingsService,
        DialogService dialogService)
    {
        _pixivClient = pixivClient;
        _imageLoader = imageLoader;
        _coordinator = coordinator;
        _settingsService = settingsService;
        _dialogService = dialogService;

        _panelWidth = settingsService.Current.BrowsePanelWidth >= 350 ? settingsService.Current.BrowsePanelWidth : 420;
        _showR18 = settingsService.Current.CollectionsShowR18;

        var s = settingsService.Current;
        _cardSize = s.CardSize;
        _isFixedHeight = s.CollectionsCardHeightMode != "Natural";
        _isNaturalHeight = s.CollectionsCardHeightMode == "Natural";
        _isGridView = s.CollectionsViewMode != "List";
        _isListView = s.CollectionsViewMode == "List";
        _showTags = s.CollectionsShowTags;
        _showInfo = s.CollectionsShowInfo;
        _showBadges = s.ShowBadges;

        GalleryVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.HasTabs) && !GalleryVm.HasTabs)
            { ShowPreview = false; IsViewerExpanded = false; }
            if (e.PropertyName is nameof(GalleryViewModel.IsCollageMode)
                               or nameof(GalleryViewModel.HasStoredCollage)
                               or nameof(GalleryViewModel.CanViewSelectedAsCollage)
                               or nameof(GalleryViewModel.CollageItems))
            {
                OnPropertyChanged(nameof(CanViewSelectedWorksAsCollage));
                ViewSelectedWorksAsCollageCommand.NotifyCanExecuteChanged();
            }
        };

        Works.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoWorks));

        _ = LoadBrowseAsync();
    }

    /// <summary>Loads the Featured/All collections collage. UNVERIFIED against a confirmed
    /// capture of the landing page's data shape — see
    /// <see cref="PixivClient.GetFeaturedCollectionsAsync"/> for details and the diagnostic
    /// dump it writes if nothing comes back.</summary>
    [RelayCommand]
    private async Task LoadBrowseAsync()
    {
        IsBrowseLoading = true;
        try
        {
            var (featured, _) = await _pixivClient.GetFeaturedCollectionsAsync();
            _rawFeatured.Clear();
            foreach (var s in featured)
            {
                var tile = new CollectionTileViewModel(s);
                _rawFeatured.Add(tile);
                _ = LoadTileThumbnailAsync(tile);
            }
            ApplyFeaturedFilter();

            // "All Collections" now uses the real paginated search endpoint (confirmed from a
            // captured live request: GET /ajax/collections/search?mode=safe&limit=20&offset=20)
            // instead of the small fixed ~10-item "everyoneCollectionIds" sample.
            _allCollectionsOffset = 0;
            AllCollections.Clear();
            if (UsePagination) await GoToAllCollectionsPageAsync(1);
            else await LoadMoreAllCollectionsAsync();

            if (featured.Count == 0 && AllCollections.Count == 0)
                StatusMessage = "Couldn't load collections — you can still paste a specific collection URL/ID above.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load featured collections: {ex.Message}";
        }
        finally { IsBrowseLoading = false; }
    }

    /// <summary>Server-side page size for "All Collections" — Pixiv's own capture used 20, but
    /// a slightly larger batch reduces round-trips for our wider tile grid. The user can change
    /// this from the bottom pager bar; switching sizes always reloads from page 1 to keep the
    /// offset math consistent.</summary>
    [ObservableProperty] private int _allCollectionsPageSize = 40;
    private int _allCollectionsOffset;
    [ObservableProperty] private int _allCollectionsTotal;
    partial void OnAllCollectionsTotalChanged(int value)
    {
        OnPropertyChanged(nameof(AllCollectionsTotalPages));
        OnPropertyChanged(nameof(CanGoPreviousAllCollectionsPage));
        OnPropertyChanged(nameof(CanGoNextAllCollectionsPage));
        OnPropertyChanged(nameof(CanLoadMoreAllCollections));
        PreviousAllCollectionsPageCommand.NotifyCanExecuteChanged();
        NextAllCollectionsPageCommand.NotifyCanExecuteChanged();
        LoadMoreAllCollectionsCommand.NotifyCanExecuteChanged();
    }

    partial void OnAllCollectionsPageChanged(int value)
    {
        OnPropertyChanged(nameof(AllCollectionsTotalPages));
        OnPropertyChanged(nameof(CanGoPreviousAllCollectionsPage));
        OnPropertyChanged(nameof(CanGoNextAllCollectionsPage));
        OnPropertyChanged(nameof(ShowFeaturedCollections));
        PreviousAllCollectionsPageCommand.NotifyCanExecuteChanged();
        NextAllCollectionsPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnAllCollectionsPageSizeChanged(int value)
    {
        value = Math.Clamp(value, 20, 120);
        if (value == _allCollectionsPageSize) return;
        _allCollectionsPageSize = value;
        OnPropertyChanged(nameof(AllCollectionsPageSize));
        OnPropertyChanged(nameof(AllCollectionsTotalPages));
        OnPropertyChanged(nameof(CanGoPreviousAllCollectionsPage));
        OnPropertyChanged(nameof(CanGoNextAllCollectionsPage));
        PreviousAllCollectionsPageCommand.NotifyCanExecuteChanged();
        NextAllCollectionsPageCommand.NotifyCanExecuteChanged();
        LoadMoreAllCollectionsCommand.NotifyCanExecuteChanged();
        if (UsePagination)
        {
            var target = Math.Clamp(AllCollectionsPage, 1, Math.Max(1, AllCollectionsTotalPages));
            _ = GoToAllCollectionsPageAsync(target);
        }
        else
        {
            _allCollectionsOffset = 0;
            AllCollections.Clear();
            _ = LoadMoreAllCollectionsAsync();
        }
    }
    [ObservableProperty] private bool _isLoadingMoreAllCollections;
    partial void OnIsLoadingMoreAllCollectionsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoPreviousAllCollectionsPage));
        OnPropertyChanged(nameof(CanGoNextAllCollectionsPage));
        OnPropertyChanged(nameof(CanLoadMoreAllCollections));
        PreviousAllCollectionsPageCommand.NotifyCanExecuteChanged();
        NextAllCollectionsPageCommand.NotifyCanExecuteChanged();
        LoadMoreAllCollectionsCommand.NotifyCanExecuteChanged();
    }
    public bool CanLoadMoreAllCollections => !IsLoadingMoreAllCollections && !UsePagination && AllCollections.Count < AllCollectionsTotal;

    // ── Numbered-pages mode — mirrors Rankings/Pixivision's own "Pages" toggle, as an
    // alternative to "Load more" (infinite accumulation). ─────────────────────────────────────
    [ObservableProperty] private bool _usePagination;
    [ObservableProperty] private int _allCollectionsPage = 1;
    [ObservableProperty] private string _allCollectionsPageInput = "";
    public int AllCollectionsTotalPages => AllCollectionsTotal <= 0 ? 1 : (int)Math.Ceiling(AllCollectionsTotal / (double)AllCollectionsPageSize);
    public bool CanGoPreviousAllCollectionsPage => !IsLoadingMoreAllCollections && AllCollectionsPage > 1;
    public bool CanGoNextAllCollectionsPage => !IsLoadingMoreAllCollections && AllCollectionsPage < AllCollectionsTotalPages;

    /// <summary>Switching modes always starts from a clean, consistent state — otherwise
    /// stale/out-of-sync offsets between the two independent fetch paths (page-based vs.
    /// append-based) could leave "Load more" visible at the same time as the page controls, or
    /// leave the list looking empty.</summary>
    partial void OnUsePaginationChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoadMoreAllCollections));
        OnPropertyChanged(nameof(ShowFeaturedCollections));
        LoadMoreAllCollectionsCommand.NotifyCanExecuteChanged();
        if (value)
        {
            _ = GoToAllCollectionsPageAsync(1);
        }
        else
        {
            _allCollectionsOffset = 0;
            AllCollections.Clear();
            _ = LoadMoreAllCollectionsAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPreviousAllCollectionsPage))]
    private Task PreviousAllCollectionsPageAsync() => GoToAllCollectionsPageAsync(AllCollectionsPage - 1);

    [RelayCommand(CanExecute = nameof(CanGoNextAllCollectionsPage))]
    private Task NextAllCollectionsPageAsync() => GoToAllCollectionsPageAsync(AllCollectionsPage + 1);

    [RelayCommand]
    private void TogglePagination() => UsePagination = !UsePagination;

    [RelayCommand]
    private Task JumpToAllCollectionsPageAsync()
    {
        if (!int.TryParse(AllCollectionsPageInput, out var page)) return Task.CompletedTask;
        AllCollectionsPageInput = "";
        return GoToAllCollectionsPageAsync(Math.Clamp(page, 1, AllCollectionsTotalPages));
    }

    /// <summary>Jumps straight to a specific page — replaces (rather than appends to)
    /// <see cref="AllCollections"/>, unlike <see cref="LoadMoreAllCollectionsAsync"/>. Retries
    /// once on an empty/failed response before giving up, and — unlike before — never clears
    /// the currently-displayed page over a transient failure; a flaky request just leaves the
    /// previous page on screen instead of wiping it out to a blank "couldn't load" state.</summary>
    private async Task GoToAllCollectionsPageAsync(int page)
    {
        if (page < 1 || IsLoadingMoreAllCollections) return;
        IsLoadingMoreAllCollections = true;
        try
        {
            var mode = ShowR18 ? "all" : "safe";
            var offset = (page - 1) * AllCollectionsPageSize;
            var (items, total) = await _pixivClient.SearchCollectionsAsync(mode, AllCollectionsPageSize, offset);

            // A genuinely empty result set (0 items AND 0 total) for a page we know should have
            // content (we're paging within AllCollectionsTotalPages, or this is the very first
            // load) usually means a transient/rate-limited response rather than truly zero
            // collections — retry once before accepting it.
            if (items.Count == 0 && total == 0 && AllCollectionsTotal > 0)
            {
                await Task.Delay(400);
                (items, total) = await _pixivClient.SearchCollectionsAsync(mode, AllCollectionsPageSize, offset);
            }

            if (items.Count == 0 && total == 0)
            {
                StatusMessage = $"Couldn't load page {page} — Pixiv may be rate-limiting requests. Try again in a moment.";
                return;
            }

            AllCollectionsTotal = total;
            AllCollectionsPage = page;
            AllCollections.Clear();
            foreach (var s in items)
            {
                var tile = new CollectionTileViewModel(s);
                AllCollections.Add(tile);
                _ = LoadTileThumbnailAsync(tile);
            }
            OnPropertyChanged(nameof(CanLoadMoreAllCollections));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load page {page}: {ex.Message}";
        }
        finally { IsLoadingMoreAllCollections = false; }
    }

    /// <summary>Fetches the next page of "All Collections" from the real paginated endpoint and
    /// appends it. <see cref="ShowR18"/> maps directly to the endpoint's own "safe"/"all" mode
    /// filter rather than a client-side filter, since R-18 items aren't even returned in "safe"
    /// mode.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMoreAllCollections))]
    private async Task LoadMoreAllCollectionsAsync()
    {
        if (IsLoadingMoreAllCollections || UsePagination) return;
        IsLoadingMoreAllCollections = true;
        try
        {
            var mode = ShowR18 ? "all" : "safe";
            var (items, total) = await _pixivClient.SearchCollectionsAsync(mode, AllCollectionsPageSize, _allCollectionsOffset);
            AllCollectionsTotal = total;
            _allCollectionsOffset += items.Count;

            foreach (var s in items)
            {
                var tile = new CollectionTileViewModel(s);
                AllCollections.Add(tile);
                _ = LoadTileThumbnailAsync(tile);
            }
            OnAllCollectionsTotalChanged(AllCollectionsTotal);
            OnPropertyChanged(nameof(CanLoadMoreAllCollections));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load more collections: {ex.Message}";
        }
        finally { IsLoadingMoreAllCollections = false; }
    }

    /// <summary>Re-populates <see cref="FeaturedCollections"/> from the cached raw list,
    /// filtering out R-18 collections unless <see cref="ShowR18"/> is on. "All Collections" is
    /// filtered server-side instead (see <see cref="LoadMoreAllCollectionsAsync"/>) since the
    /// "safe" mode there doesn't return R-18 items at all.</summary>
    private void ApplyFeaturedFilter()
    {
        var selectedIds = FeaturedCollections.Where(t => t.IsSelected).Select(t => t.Id).ToHashSet();

        FeaturedCollections.Clear();
        foreach (var t in _rawFeatured.Where(t => ShowR18 || !t.IsR18))
        {
            t.IsSelected = selectedIds.Contains(t.Id);
            FeaturedCollections.Add(t);
        }

        NotifySelectionChanged();
        OnPropertyChanged(nameof(ShowFeaturedCollections));
    }

    /// <summary>
    /// Backfills a browse tile's 2x2 collage preview from real work images on
    /// <c>i.pximg.net</c> (via <see cref="PixivClient.GetCollectionThumbnailsAsync"/>) instead
    /// of the <c>embed.pixiv.net</c> collage-thumbnail URL the listing endpoint returns — that
    /// endpoint reliably 400s (confirmed app-level rejection, not a Cloudflare/auth issue) for
    /// reasons still unconfirmed, most likely because the collage image is only generated
    /// on-demand after a real browser visits the collection page.
    /// </summary>
    private async Task LoadTileThumbnailAsync(CollectionTileViewModel tile)
    {
        try
        {
            var urls = await _pixivClient.GetCollectionThumbnailsAsync(tile.Id, maxCount: 4);
            if (urls.Count > 0)
            {
                await tile.LoadCollageThumbnailsAsync(_imageLoader, urls);
                // Every bitmap fetch failed (e.g. transient rate-limit) — retry once before
                // giving up, rather than leaving a permanently blank tile.
                if (!tile.HasCollage)
                {
                    await Task.Delay(400);
                    await tile.LoadCollageThumbnailsAsync(_imageLoader, urls);
                }
            }
        }
        catch { /* thumbnail is decorative — non-fatal */ }
        finally
        {
            // A collection with no works at all (or one whose thumbnails still failed to load
            // after the retry) renders as an empty tile — hide it entirely instead of showing a
            // blank card the user can't do anything useful with.
            if (!tile.HasCollage) RemoveBlankTile(tile);
        }
    }

    /// <summary>Removes a tile with no loadable thumbnails from whichever browse list it's
    /// currently shown in.</summary>
    private void RemoveBlankTile(CollectionTileViewModel tile)
    {
        _rawFeatured.Remove(tile);
        FeaturedCollections.Remove(tile);
        AllCollections.Remove(tile);
        OnPropertyChanged(nameof(ShowFeaturedCollections));
        OnPropertyChanged(nameof(CanLoadMoreAllCollections));
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var t in AllCollections) t.IsSelected = true;
        foreach (var t in FeaturedCollections) t.IsSelected = true;
        NotifySelectionChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var t in AllCollections) t.IsSelected = false;
        foreach (var t in FeaturedCollections) t.IsSelected = false;
        NotifySelectionChanged();
    }

    [RelayCommand]
    private void ToggleTileSelection(CollectionTileViewModel? tile)
    {
        if (tile == null) return;
        tile.IsSelected = !tile.IsSelected;
        NotifySelectionChanged();
    }

    [RelayCommand]
    private void BackToBrowse()
    {
        IsBrowsing = true;
        ShowPreview = false;

        // Fully reset detail-view state so the next collection opened doesn't briefly show
        // stale title/tags/comments/works from whatever was previously loaded, and so the info
        // bar (gated on HasLoaded) doesn't linger over the browse collage.
        HasLoaded = false;
        _currentCollection = null;
        CollectionUrlOrId = string.Empty;
        Title = string.Empty;
        UserName = string.Empty;
        Caption = null;
        BookmarkCount = 0;
        ViewCount = 0;
        TagsLabel = string.Empty;
        TagsList.Clear();
        Works.Clear();
        NotifyWorksSelectionChanged();
        SiblingCollections.Clear();
        CollectionComments.Clear();
        CollectionCommentCount = 0;
        NewCollectionComment = string.Empty;
        StatusMessage = "Paste a pixiv.net/collections/{id} URL (or just the ID) to view it, or browse below.";
    }

    /// <summary>Fetches (and caches) full collection data for a browse tile, needed before
    /// downloading it.</summary>
    private async Task<PixivCollection?> ResolveCollectionAsync(string id)
    {
        if (_browseCache.TryGetValue(id, out var cached)) return cached;
        var collection = await _pixivClient.GetCollectionAsync(id);
        if (collection != null) _browseCache[id] = collection;
        return collection;
    }

    [RelayCommand]
    private async Task DownloadSelectedCollectionsAsync()
    {
        var selected = AllCollections.Concat(FeaturedCollections).Where(t => t.IsSelected).DistinctBy(t => t.Id).ToList();
        await DownloadCollectionTilesAsync(selected, "selected");
    }

    [RelayCommand]
    private async Task DownloadLoadedCollectionsAsync()
    {
        var loaded = AllCollections.Concat(FeaturedCollections).DistinctBy(t => t.Id).ToList();
        await DownloadCollectionTilesAsync(loaded, "loaded");
    }

    [RelayCommand]
    private async Task DownloadSelectedWithPresetAsync()
    {
        var selected = AllCollections.Concat(FeaturedCollections).Where(t => t.IsSelected).DistinctBy(t => t.Id).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Select one or more collections first.";
            return;
        }

        IsDownloading = true;
        try
        {
            StatusMessage = $"Loading {selected.Count} collection(s)…";
            var resolved = new List<PixivCollection>();
            foreach (var tile in selected)
            {
                var c = await ResolveCollectionAsync(tile.Id);
                if (c != null && c.Works.Count > 0) resolved.Add(c);
            }
            if (resolved.Count == 0) { StatusMessage = "None of the selected collections could be loaded."; return; }

            var firstWork = resolved[0].Works[0];
            var additional = resolved.SelectMany(c => c.Works).Skip(1).ToList();
            var preset = await _dialogService.ShowDownloadPresetDialogAsync(firstWork, additional);
            if (preset == null) { StatusMessage = "Download cancelled."; return; }

            var started = 0;
            foreach (var c in resolved)
            {
                var (targets, _) = CollectionDownloadHelper.BuildTargets(c, UseCollectionFolder, _settingsService.Current.DownloadRoot);
                foreach (var t in targets)
                    t.CustomSettings!.ImagePreset = preset;
                await _coordinator.CreateJobAsync(DownloadJobType.ImageId, $"Collection: {c.Title}", targets, settingsOverride: null, startImmediately: true);
                started++;
            }
            StatusMessage = $"Started {started} collection download job(s) with preset '{preset.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not start download: {ex.Message}";
        }
        finally { IsDownloading = false; }
    }

    private async Task DownloadCollectionTilesAsync(List<CollectionTileViewModel> tiles, string label)
    {
        if (tiles.Count == 0)
        {
            StatusMessage = $"No {label} collections to download.";
            return;
        }

        IsDownloading = true;
        var started = 0;
        var failed = new List<string>();
        try
        {
            foreach (var tile in tiles)
            {
                StatusMessage = $"Loading {tile.Title}…";
                var collection = await ResolveCollectionAsync(tile.Id);
                if (collection == null || collection.Works.Count == 0) { failed.Add(tile.Title); continue; }

                var (targets, _) = CollectionDownloadHelper.BuildTargets(
                    collection, UseCollectionFolder, _settingsService.Current.DownloadRoot);
                await _coordinator.CreateJobAsync(
                    DownloadJobType.ImageId, $"Collection: {collection.Title}", targets, settingsOverride: null, startImmediately: true);
                started++;
            }
            StatusMessage = failed.Count == 0
                ? $"Started {started} {label} collection download job(s)."
                : $"Started {started} job(s); couldn't load: {string.Join(", ", failed)}";
        }
        finally { IsDownloading = false; }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var id = ExtractCollectionId(CollectionUrlOrId);
        if (string.IsNullOrEmpty(id))
        {
            StatusMessage = "That doesn't look like a collection URL or ID.";
            return;
        }
        await LoadCollectionAsync(id);
    }

    [RelayCommand]
    private async Task LoadSiblingAsync(string id)
    {
        CollectionUrlOrId = id;
        await LoadCollectionAsync(id);
    }

    /// <summary>Also used to open a collection clicked from the browse collage.</summary>
    [RelayCommand]
    private async Task OpenCollectionTileAsync(CollectionTileViewModel? tile)
    {
        if (tile == null) return;
        CollectionUrlOrId = tile.Id;
        await LoadCollectionAsync(tile.Id);
    }

    /// <summary>Context-menu "Download Collection" for a single browse tile, without needing
    /// to check its box first.</summary>
    [RelayCommand]
    private async Task DownloadSingleTileAsync(CollectionTileViewModel? tile)
    {
        if (tile == null) return;
        await DownloadCollectionTilesAsync([tile], tile.Title);
    }

    /// <summary>Context-menu "Load into side panel" — opens the collection (same as clicking
    /// it) and immediately opens its first work in the inline viewer side panel, so browsing a
    /// collection's images doesn't need an extra click after loading. Loads the FULL work list
    /// as the viewer's navigation, not just the one clicked.</summary>
    [RelayCommand]
    private async Task LoadTileIntoSidePanelAsync(CollectionTileViewModel? tile)
    {
        if (tile == null) return;
        CollectionUrlOrId = tile.Id;
        await LoadCollectionAsync(tile.Id);
        if (Works.Count > 0)
        {
            GalleryVm.OpenInViewer(Works[0], Works.ToList(), Works.Count, source: ViewerSourceKey);
            ShowPreview = true;
        }
    }

    /// <summary>Whether "View as Collage" should be enabled. Needs at least 2 checked artworks
    /// to start a new collage, or 1 checked artwork to add to an existing/stored collage (capped at
    /// <see cref="GalleryViewModel.MaxCollageItems"/>).</summary>
    public bool CanViewSelectedWorksAsCollage => GalleryVm.CanCollage(SelectedWorksCount);

    /// <summary>"View as Collage" for the currently-checked artworks in an open collection.
    /// Opens the artworks into a dedicated collage tab. If a collage tab already exists,
    /// the newly-selected artworks are appended to it.</summary>
    [RelayCommand(CanExecute = nameof(CanViewSelectedWorksAsCollage))]
    private void ViewSelectedWorksAsCollage()
    {
        var selected = Works.Where(w => w.IsSelected).ToList();
        if (selected.Count == 0) return;
        if (GalleryVm.HasStoredCollage || GalleryVm.IsCollageMode)
        {
            GalleryVm.AddToCollage(selected);
            return;
        }
        GalleryVm.ShowCollage(selected.Take(GalleryViewModel.MaxCollageItems));
    }

    public bool CanViewSelectedWorksInNewTabs => SelectedWorksCount >= 1;

    [RelayCommand(CanExecute = nameof(CanViewSelectedWorksInNewTabs))]
    private void ViewSelectedWorksInNewTabs()
    {
        var selected = Works.Where(w => w.IsSelected).ToList();
        if (selected.Count == 0) return;
        foreach (var card in selected)
            GalleryVm.OpenInNewTab(card, selected, selected.Count, source: ViewerSourceKey);
        ShowPreview = true;
    }

    /// <summary>Context-menu bookmark toggle for a browse tile — bookmarks/unbookmarks the
    /// collection without opening it first.</summary>
    [RelayCommand]
    private async Task ToggleTileBookmarkAsync(CollectionTileViewModel? tile)
    {
        if (tile == null) return;
        try
        {
            if (tile.IsBookmarked)
            {
                if (tile.BookmarkId == null) return;
                if (await _pixivClient.RemoveCollectionBookmarkAsync(tile.BookmarkId))
                {
                    tile.IsBookmarked = false;
                    tile.BookmarkId = null;
                }
            }
            else if (await _pixivClient.AddCollectionBookmarkAsync(tile.Id))
            {
                tile.IsBookmarked = true;
            }
        }
        catch { /* non-fatal */ }
    }

    private async Task LoadCollectionAsync(string id)
    {
        IsLoading = true;
        IsBrowsing = false;
        StatusMessage = "Loading collection…";
        Works.Clear();
        NotifyWorksSelectionChanged();
        ShowPreview = false;
        try
        {
            var collection = await _pixivClient.GetCollectionAsync(id);
            if (collection == null)
            {
                StatusMessage = "Could not load that collection — it may be private, deleted, or the ID is wrong.";
                HasLoaded = false;
                _currentCollection = null;
                return;
            }

            _currentCollection = collection;
            Title = collection.Title;
            UserName = collection.UserName;
            Caption = collection.Caption;
            BookmarkCount = collection.BookmarkCount;
            ViewCount = collection.ViewCount;
            TagsLabel = string.Join("  ", collection.Tags.Select(t => $"#{t}"));
            TagsList.Clear();
            foreach (var t in collection.Tags) TagsList.Add($"#{t}");

            foreach (var preview in collection.Works)
                Works.Add(new ArtworkCardViewModel(preview)
                {
                    IsBlurred = _settingsService.Current.BlurR18Content && preview.IsR18,
                });
            foreach (var card in Works)
                _ = card.LoadThumbnailAsync(_imageLoader);

            SiblingCollections.Clear();
            // Exclude the collection currently being viewed from its own "more by this creator" list.
            foreach (var s in collection.SiblingCollections.Where(s => s.Id != collection.Id))
            {
                var tile = new CollectionTileViewModel(s);
                SiblingCollections.Add(tile);
                if (!string.IsNullOrEmpty(s.ThumbnailImageUrl))
                    _ = tile.LoadThumbnailAsync(_imageLoader, s.ThumbnailImageUrl!);
            }

            HasLoaded = true;
            StatusMessage = $"{Works.Count} work(s) in this collection";

            _ = LoadCollectionCommentsAsync(id);
            _ = LoadCollectionBookmarkStateAsync(id);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load collection: {ex.Message}";
            HasLoaded = false;
            _currentCollection = null;
        }
        finally { IsLoading = false; }
    }

    /// <summary>Loads the Collection's own top-level comments (distinct from any individual
    /// artwork's comments — see <see cref="PixivClient.GetCollectionCommentsAsync"/>).</summary>
    private async Task LoadCollectionCommentsAsync(string collectionId)
    {
        IsLoadingComments = true;
        CollectionComments.Clear();
        try
        {
            var response = await _pixivClient.GetCollectionCommentsAsync(collectionId, limit: 50);
            if (response == null) return;
            // Guard against a stale response landing after the user has already navigated away.
            if (_currentCollection?.Id != collectionId) return;

            foreach (var c in response.Comments) CollectionComments.Add(c);
            CollectionCommentCount = response.TotalComments;
        }
        catch { /* comments are supplementary — non-fatal */ }
        finally { IsLoadingComments = false; }
    }

    private async Task LoadCollectionBookmarkStateAsync(string collectionId)
    {
        IsCollectionBookmarked = false;
        CollectionBookmarkId = null;
        try
        {
            var data = await _pixivClient.GetCollectionBookmarkDataAsync(collectionId);
            if (_currentCollection?.Id != collectionId) return; // navigated away meanwhile
            if (data?.Id != null)
            {
                IsCollectionBookmarked = true;
                CollectionBookmarkId = data.Id;
            }
        }
        catch { /* non-fatal — bookmark button just starts unchecked */ }
    }

    /// <summary>Bookmarks/unbookmarks the *collection itself* on Pixiv (distinct from the local
    /// "Keep as one collection folder" download setting, and from bookmarking any individual
    /// artwork inside it).</summary>
    [RelayCommand]
    private async Task ToggleCollectionBookmarkAsync()
    {
        if (_currentCollection == null || IsTogglingBookmark) return;
        IsTogglingBookmark = true;
        try
        {
            if (IsCollectionBookmarked)
            {
                if (CollectionBookmarkId == null) return;
                var ok = await _pixivClient.RemoveCollectionBookmarkAsync(CollectionBookmarkId);
                if (ok) { IsCollectionBookmarked = false; CollectionBookmarkId = null; StatusMessage = "Removed collection bookmark."; }
                else StatusMessage = "Failed to remove collection bookmark.";
            }
            else
            {
                var ok = await _pixivClient.AddCollectionBookmarkAsync(_currentCollection.Id);
                if (ok)
                {
                    IsCollectionBookmarked = true;
                    StatusMessage = "Bookmarked this collection.";
                    _ = LoadCollectionBookmarkStateAsync(_currentCollection.Id); // pick up the real bookmark id
                }
                else StatusMessage = "Failed to bookmark collection.";
            }
        }
        finally { IsTogglingBookmark = false; }
    }

    [RelayCommand]
    private async Task PostCollectionCommentAsync()
    {
        var text = NewCollectionComment.Trim();
        if (string.IsNullOrEmpty(text) || _currentCollection == null || IsPostingComment) return;

        var collectionId = _currentCollection.Id;
        IsPostingComment = true;
        try
        {
            var ok = await _pixivClient.PostCollectionCommentAsync(collectionId, text);
            if (ok)
            {
                NewCollectionComment = string.Empty;
                await LoadCollectionCommentsAsync(collectionId);
            }
            else
            {
                StatusMessage = "Failed to post comment.";
            }
        }
        finally { IsPostingComment = false; }
    }

    /// <summary>Sends a sticker on the Collection's own comment thread — mirrors the artwork
    /// viewer's sticker picker (<see cref="Pikura.Avalonia.Views.Gallery.InlineArtworkViewer"/>),
    /// via <see cref="PixivClient.PostCollectionStickerAsync"/>.</summary>
    [RelayCommand]
    private async Task PostCollectionStickerAsync(int stampId)
    {
        if (_currentCollection == null || IsPostingComment) return;
        var collectionId = _currentCollection.Id;
        IsPostingComment = true;
        try
        {
            var ok = await _pixivClient.PostCollectionStickerAsync(collectionId, stampId);
            if (ok) await LoadCollectionCommentsAsync(collectionId);
            else StatusMessage = "Failed to post sticker.";
        }
        finally { IsPostingComment = false; }
    }

    [RelayCommand]
    private async Task DeleteCollectionCommentAsync(PixivComment? comment)
    {
        if (comment == null || _currentCollection == null) return;
        var collectionId = _currentCollection.Id;
        var confirmed = await _dialogService.ShowConfirmationAsync("Delete Comment", "Delete this comment? This cannot be undone.");
        if (!confirmed) return;

        var ok = await _pixivClient.DeleteCollectionCommentAsync(collectionId, comment.Id);
        if (ok) CollectionComments.Remove(comment);
        else StatusMessage = "Failed to delete comment.";
    }

    /// <summary>Opens the artwork in the same inline viewer (with Like/Bookmark/Comments/etc.)
    /// used everywhere else in the app, rather than the system browser.</summary>
    [RelayCommand]
    private void OpenArtwork(ArtworkCardViewModel? card)
    {
        if (card == null) return;
        GalleryVm.OpenInViewer(card, Works.ToList(), Works.Count, source: ViewerSourceKey);
        ShowPreview = true;
    }

    [RelayCommand] private void TogglePreview() => ShowPreview = !ShowPreview;

    [RelayCommand]
    private void ToggleWorkSelection(ArtworkCardViewModel? card)
    {
        if (card == null) return;
        card.IsSelected = !card.IsSelected;
        NotifyWorksSelectionChanged();
    }

    [RelayCommand]
    private void SelectAllWorks()
    {
        foreach (var w in Works) w.IsSelected = true;
        NotifyWorksSelectionChanged();
    }

    [RelayCommand]
    private void ClearWorksSelection()
    {
        foreach (var w in Works) w.IsSelected = false;
        NotifyWorksSelectionChanged();
    }

    /// <summary>Downloads only the checked artworks from the currently-open collection, honoring
    /// the same "Collections" folder behavior as "Download Collection".</summary>
    [RelayCommand]
    private async Task DownloadSelectedWorksAsync()
    {
        var picked = Works.Where(w => w.IsSelected).ToList();
        if (picked.Count == 0 || _currentCollection == null)
        {
            StatusMessage = "Select one or more artworks first.";
            return;
        }

        IsDownloading = true;
        try
        {
            var (targets, folder) = CollectionDownloadHelper.BuildTargets(
                picked.Select(w => w.Artwork).ToList(), _currentCollection.Title, UseCollectionFolder, _settingsService.Current.DownloadRoot);

            await _coordinator.CreateJobAsync(
                DownloadJobType.ImageId,
                $"Collection: {_currentCollection.Title} (selected)",
                targets,
                settingsOverride: null,
                startImmediately: true);

            StatusMessage = $"Download started for {targets.Count} selected work(s) → {folder}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not start download: {ex.Message}";
        }
        finally { IsDownloading = false; }
    }

    [RelayCommand]
    private async Task DownloadSelectedWorksWithPresetAsync()
    {
        var picked = Works.Where(w => w.IsSelected).ToList();
        if (picked.Count == 0) picked = Works.ToList();
        if (picked.Count == 0 || _currentCollection == null)
        {
            StatusMessage = "No artworks to download.";
            return;
        }

        var preset = await _dialogService.ShowDownloadPresetDialogAsync(
            picked[0].Artwork, picked.Skip(1).Select(w => w.Artwork).ToList());
        if (preset == null) { StatusMessage = "Download cancelled."; return; }

        IsDownloading = true;
        try
        {
            var (targets, folder) = CollectionDownloadHelper.BuildTargets(
                picked.Select(w => w.Artwork).ToList(), _currentCollection.Title, UseCollectionFolder, _settingsService.Current.DownloadRoot);
            foreach (var t in targets)
                t.CustomSettings!.ImagePreset = preset;

            await _coordinator.CreateJobAsync(
                DownloadJobType.ImageId,
                $"Collection: {_currentCollection.Title} (preset)",
                targets,
                settingsOverride: null,
                startImmediately: true);

            StatusMessage = $"Download started for {targets.Count} work(s) with preset '{preset.Name}' → {folder}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not start download: {ex.Message}";
        }
        finally { IsDownloading = false; }
    }

    /// <summary>
    /// Downloads every artwork currently loaded into one folder named after the collection.
    /// Reuses the existing ImageId job type/download pipeline rather than adding a whole new
    /// execution path in DownloadCoordinator — a Collection is, for download purposes, just a
    /// named batch of specific artwork IDs.
    /// </summary>
    [RelayCommand]
    private async Task DownloadCollectionAsync()
    {
        if (_currentCollection == null || Works.Count == 0) return;
        IsDownloading = true;
        try
        {
            var (targets, folder) = CollectionDownloadHelper.BuildTargets(
                _currentCollection, UseCollectionFolder, _settingsService.Current.DownloadRoot);

            await _coordinator.CreateJobAsync(
                DownloadJobType.ImageId,
                $"Collection: {_currentCollection.Title}",
                targets,
                settingsOverride: null,
                startImmediately: true);

            StatusMessage = $"Download started for {targets.Count} work(s) → {folder}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not start download: {ex.Message}";
        }
        finally { IsDownloading = false; }
    }

    /// <summary>Accepts either a bare numeric ID or a full pixiv.net/collections/{id} URL.</summary>
    private static string? ExtractCollectionId(string input)
    {
        input = (input ?? string.Empty).Trim();
        if (input.Length == 0) return null;
        var m = Regex.Match(input, @"collections/(\d+)");
        if (m.Success) return m.Groups[1].Value;
        return Regex.IsMatch(input, @"^\d+$") ? input : null;
    }
}

/// <summary>A collage tile for one collection — used both for a creator's sibling collections
/// and for the featured/browse listing. Loads its own thumbnails lazily. Renders as a 2x2
/// assortment of up to 4 of the collection's actual works (via
/// <see cref="PixivClient.GetCollectionThumbnailsAsync"/>) rather than a single image, echoing
/// Pixiv's own collage look; <see cref="Thumbnail"/> (the first one) is kept around for the
/// single-image case (e.g. a collection with only 1 work, or the sibling-collections mini list).</summary>
public partial class CollectionTileViewModel : ObservableObject
{
    public string Id { get; }
    public string Title { get; }
    public string UserName { get; }
    public int BookmarkCount { get; }
    public int ViewCount { get; }
    /// <summary>Pixiv's content-rating flag — drives the browse collage's R-18 filter.</summary>
    public bool IsR18 { get; }

    [ObservableProperty] private global::Avalonia.Media.Imaging.Bitmap? _thumbnail;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Whether this collection is bookmarked on Pixiv — drives a small bookmark badge
    /// on the tile. Seeded from the listing response and updated by
    /// <see cref="CollectionsViewModel.ToggleTileBookmarkAsync"/>.</summary>
    [ObservableProperty] private bool _isBookmarked;
    [ObservableProperty] private string? _bookmarkId;
    /// <summary>Whether this tile's thumbnails should render blurred — mirrors
    /// ArtworkCardViewModel.IsBlurred (global BlurR18Content setting && IsR18), so an R-18
    /// collection's thumbnails blur the same way an R-18 artwork's would.</summary>
    [ObservableProperty] private bool _isBlurred;

    /// <summary>Up to 4 thumbnails for the 2x2 collage grid. Populated by
    /// <see cref="LoadCollageThumbnailsAsync"/>; empty until then (or if the collection has no
    /// works). <see cref="Thumbnail"/> always mirrors the first entry for callers that only
    /// want a single image.</summary>
    public ObservableCollection<global::Avalonia.Media.Imaging.Bitmap> CollageThumbnails { get; } = [];
    public bool HasCollage => CollageThumbnails.Count > 0;
    /// <summary>True once there's more than one thumbnail — switches the tile from a single
    /// full-bleed image to the 2x2 assortment grid.</summary>
    public bool HasMultipleThumbnails => CollageThumbnails.Count > 1;

    public CollectionTileViewModel(PixivCollectionSummary summary)
    {
        Id = summary.Id;
        Title = summary.Title;
        UserName = summary.UserName;
        BookmarkCount = summary.BookmarkCount;
        ViewCount = summary.ViewCount;
        IsR18 = summary.IsR18;
        _isBookmarked = summary.IsBookmarked;
        _bookmarkId = summary.BookmarkId;
        _isBlurred = AppServices.Get<SettingsService>().Current.BlurR18Content && IsR18;
    }

    public async Task LoadThumbnailAsync(PixivImageLoader loader, string url)
    {
        var bmp = await FetchBitmapAsync(loader, url);
        if (bmp != null) Thumbnail = bmp;
    }

    /// <summary>Loads up to 4 real work thumbnails for the 2x2 collage grid, in parallel.</summary>
    public async Task LoadCollageThumbnailsAsync(PixivImageLoader loader, IReadOnlyList<string> urls)
    {
        if (urls.Count == 0) return;
        var bitmaps = await Task.WhenAll(urls.Take(4).Select(u => FetchBitmapAsync(loader, u)));
        CollageThumbnails.Clear();
        foreach (var bmp in bitmaps)
            if (bmp != null) CollageThumbnails.Add(bmp);
        if (CollageThumbnails.Count > 0) Thumbnail = CollageThumbnails[0];
        OnPropertyChanged(nameof(HasCollage));
        OnPropertyChanged(nameof(HasMultipleThumbnails));
    }

    private static async Task<global::Avalonia.Media.Imaging.Bitmap?> FetchBitmapAsync(PixivImageLoader loader, string url)
    {
        try
        {
            var skBitmap = await loader.FetchBitmapAsync(url, ThumbnailSize.Small);
            if (skBitmap == null) return null;
            var bmp = await Task.Run(() => (global::Avalonia.Media.Imaging.Bitmap?)Pikura.Avalonia.Services.BitmapInterop.SkiaToAvalonia(skBitmap));
            skBitmap.Dispose();
            return bmp;
        }
        catch { return null; /* thumbnail is decorative — non-fatal */ }
    }
}
