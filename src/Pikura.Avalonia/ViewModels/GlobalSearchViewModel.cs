using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

/// <summary>
/// "Search" tab — a true global search against Pixiv's live search API
/// (same endpoint as Download-by-Search), not restricted to followed artists.
/// Reuses <see cref="GalleryViewModel"/> (via <see cref="GalleryVm"/>) for the
/// inline viewer, Hoshi panel, and download commands — same composition
/// pattern as EnhancedRankingsViewModel/BookmarksViewModel.
/// </summary>
public partial class GlobalSearchViewModel : ViewModelBase
{
    private readonly PixivClient _pixivClient;
    private readonly PixivImageLoader _imageLoader;
    private readonly SettingsService _settingsService;

    private CancellationTokenSource? _cts;
    private int _currentPage = 1;

    /// <summary>Real result count reported by pixiv's dedicated per-work-type search endpoints
    /// (see <see cref="PixivClient.SearchArtworksAsync"/>) — null until the first page of the
    /// current search has loaded. Used to show an accurate "N of Total" and page count instead of
    /// only "however many we've loaded so far".</summary>
    private int? _knownTotal;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _includeAnyKeywords = string.Empty;
    [ObservableProperty] private string _excludeKeywords = string.Empty;
    [ObservableProperty] private bool _showKeywordOptions;
    [ObservableProperty] private string _sortOrder = "date_d"; // date_d, popular_d/popular_male_d/popular_female_d (Premium only)
    [ObservableProperty] private string _searchMode = "safe";  // safe, r18, all
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasSearched;
    [ObservableProperty] private string _statusMessage = "Search all of Pixiv";
    [ObservableProperty] private bool _canLoadMore;

    /// <summary>Pixiv's popularity search sort is Premium-only — hidden entirely for non-Premium accounts.</summary>
    public bool IsPremiumAccount => _settingsService.Current.IsPremium;

    // ── Category (illustrations / manga / novels / users) ─────────────────
    [ObservableProperty] private string _searchCategory = "illustrations"; // illustrations, manga, novels, users
    public bool IsIllustrationsCategory => SearchCategory == "illustrations";
    public bool IsMangaCategory => SearchCategory == "manga";
    /// <summary>True for either artwork-style category (illustrations or manga) — used by
    /// UI sections shared between the two (mode/sort bar, results grid, advanced filters).</summary>
    public bool IsArtworksCategory => IsIllustrationsCategory || IsMangaCategory;
    public bool IsNovelsCategory => SearchCategory == "novels";
    public bool IsUsersCategory => SearchCategory == "users";
    partial void OnSearchCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsIllustrationsCategory));
        OnPropertyChanged(nameof(IsMangaCategory));
        OnPropertyChanged(nameof(IsArtworksCategory));
        OnPropertyChanged(nameof(IsNovelsCategory));
        OnPropertyChanged(nameof(IsUsersCategory));
        OnPropertyChanged(nameof(HasAdvancedFilters));
        if (HasSearched) _ = SearchAsync();
    }

    public ObservableCollection<NovelCardViewModel> NovelResults { get; } = [];
    public ObservableCollection<UserSearchCardViewModel> UserResults { get; } = [];
    public bool HasNovelResults => NovelResults.Count > 0;
    public bool HasUserResults => UserResults.Count > 0;

    // ── Advanced filters ("Search option" dialog on pixiv.net) ────────────
    // These are the live, applied filters that drive the actual search query.
    [ObservableProperty] private string _targetMode = "s_tag";       // s_tag, s_tag_full, s_tc
    // illust_and_ugoira (default, Illustrations tab), illust, ugoira — "manga" is implicit on the
    // Manga tab and never offered here (see EffectiveWorkType).
    [ObservableProperty] private string _workType = "illust_and_ugoira";
    [ObservableProperty] private string? _ratioTag;                  // "-0.5" portrait, "0" square, "0.5" landscape, null = any
    [ObservableProperty] private string? _tool;
    [ObservableProperty] private DateTimeOffset? _postedAfter;
    [ObservableProperty] private DateTimeOffset? _postedBefore;
    [ObservableProperty] private decimal? _minWidth;
    [ObservableProperty] private decimal? _maxWidth;
    [ObservableProperty] private decimal? _minHeight;
    [ObservableProperty] private decimal? _maxHeight;
    [ObservableProperty] private decimal? _minBookmarks;
    [ObservableProperty] private decimal? _maxBookmarks;
    [ObservableProperty] private string _aiFilter = "display";       // display, hide

    // The flyout edits this copy; changes are only committed to the live filters when
    // the user presses Apply. This prevents the search from running on every keystroke.
    public AdvancedFilterEditModel AdvancedFilterEdit { get; } = new();

    public void RefreshAdvancedFilterEdit()
    {
        AdvancedFilterEdit.TargetMode = TargetMode;
        AdvancedFilterEdit.WorkType = WorkType;
        AdvancedFilterEdit.RatioTag = RatioTag;
        AdvancedFilterEdit.Tool = Tool;
        AdvancedFilterEdit.PostedAfter = PostedAfter;
        AdvancedFilterEdit.PostedBefore = PostedBefore;
        AdvancedFilterEdit.MinWidth = MinWidth;
        AdvancedFilterEdit.MaxWidth = MaxWidth;
        AdvancedFilterEdit.MinHeight = MinHeight;
        AdvancedFilterEdit.MaxHeight = MaxHeight;
        AdvancedFilterEdit.MinBookmarks = MinBookmarks;
        AdvancedFilterEdit.MaxBookmarks = MaxBookmarks;
        AdvancedFilterEdit.AiFilter = AiFilter;
    }

    /// <summary>
    /// The "type" query param — also used client-side by <see cref="FilterByCategory"/> since
    /// pixiv's own <c>type=</c> filtering is unreliable. The Manga tab is always exactly "manga"
    /// (there's no Work-type filter shown for it — manga never mixes with ugoira). The
    /// Illustrations tab defaults to "illust_and_ugoira" (mixed) but can be narrowed via the
    /// Work-type filter to "illust" or "ugoira" only.
    /// </summary>
    private string? EffectiveWorkType => SearchCategory switch
    {
        "manga" => "manga",
        "illustrations" => WorkType is "illust" or "ugoira" ? WorkType : "illust_and_ugoira",
        _ => null,
    };

    public ArtworkSearchOptions AdvancedOptions => new()
    {
        TargetMode = TargetMode == "s_tag" ? null : TargetMode,
        WorkType = EffectiveWorkType,
        Ratio = double.TryParse(RatioTag, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : null,
        Tool = Tool,
        PostedAfter = PostedAfter is { } pa ? DateOnly.FromDateTime(pa.Date) : null,
        PostedBefore = PostedBefore is { } pb ? DateOnly.FromDateTime(pb.Date) : null,
        MinWidth = (int?)MinWidth,
        MaxWidth = (int?)MaxWidth,
        MinHeight = (int?)MinHeight,
        MaxHeight = (int?)MaxHeight,
        // Bookmark count filter is Premium-only on pixiv's end — don't send it for free accounts.
        MinBookmarks = IsPremiumAccount ? (int?)MinBookmarks : null,
        MaxBookmarks = IsPremiumAccount ? (int?)MaxBookmarks : null,
        AiType = AiFilter == "hide" ? 1 : null,
    };

    /// <summary>
    /// Builds the actual <c>word</c> query sent to Pixiv. The main query, the optional
    /// "include any" list (OR group), and the optional exclude list are combined using
    /// Pixiv's own search operators: space = AND, <c>OR</c> = OR, <c>-term</c> = NOT.
    /// Terms are comma- or whitespace-separated depending on what the user typed.
    /// </summary>
    private string BuildSearchWord()
    {
        var main = SearchQuery.Trim();
        var includeTerms = SplitKeywordTerms(IncludeAnyKeywords);
        var excludeTerms = SplitKeywordTerms(ExcludeKeywords);

        var includeClause = includeTerms.Count switch
        {
            0 => null,
            1 => includeTerms[0],
            _ => $"({string.Join(" OR ", includeTerms)})"
        };

        var parts = new List<string>(1 + includeTerms.Count + excludeTerms.Count);
        if (!string.IsNullOrWhiteSpace(main)) parts.Add(main);
        if (includeClause is not null) parts.Add(includeClause);
        foreach (var ex in excludeTerms) parts.Add($"-{ex}");

        return string.Join(" ", parts);
    }

    private static IReadOnlyList<string> SplitKeywordTerms(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var delimiters = raw.Contains(',') ? new[] { ',' } : new[] { ' ', '\t' };
        return raw.Split(delimiters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }

    /// <summary>
    /// Whether the user has actually customized any advanced filter — computed from the raw
    /// fields (not <see cref="AdvancedOptions"/>) since <see cref="EffectiveWorkType"/> always
    /// carries a non-null "type" value for the Illustrations/Manga tabs.
    /// </summary>
    public bool HasAdvancedFilters =>
        !string.IsNullOrEmpty(TargetMode) && TargetMode != "s_tag" ||
        (IsIllustrationsCategory && WorkType != "illust_and_ugoira") ||
        RatioTag is not null || !string.IsNullOrWhiteSpace(Tool) ||
        PostedAfter is not null || PostedBefore is not null ||
        MinWidth is not null || MaxWidth is not null ||
        MinHeight is not null || MaxHeight is not null ||
        MinBookmarks is not null || MaxBookmarks is not null ||
        AiFilter == "hide";

    partial void OnTargetModeChanged(string value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnWorkTypeChanged(string value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnRatioTagChanged(string? value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnToolChanged(string? value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnPostedAfterChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnPostedBeforeChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnMinWidthChanged(decimal? value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnMaxWidthChanged(decimal? value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnMinHeightChanged(decimal? value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnMaxHeightChanged(decimal? value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnMinBookmarksChanged(decimal? value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnMaxBookmarksChanged(decimal? value) => OnPropertyChanged(nameof(HasAdvancedFilters));
    partial void OnAiFilterChanged(string value) => OnPropertyChanged(nameof(HasAdvancedFilters));

    [RelayCommand]
    public void ResetAdvancedFilters()
    {
        AdvancedFilterEdit.TargetMode = "s_tag";
        AdvancedFilterEdit.WorkType = "illust_and_ugoira";
        AdvancedFilterEdit.RatioTag = null;
        AdvancedFilterEdit.Tool = null;
        AdvancedFilterEdit.PostedAfter = null;
        AdvancedFilterEdit.PostedBefore = null;
        AdvancedFilterEdit.MinWidth = null;
        AdvancedFilterEdit.MaxWidth = null;
        AdvancedFilterEdit.MinHeight = null;
        AdvancedFilterEdit.MaxHeight = null;
        AdvancedFilterEdit.MinBookmarks = null;
        AdvancedFilterEdit.MaxBookmarks = null;
        AdvancedFilterEdit.AiFilter = "display";
    }

    [RelayCommand]
    public void ApplyAdvancedFilters()
    {
        TargetMode = AdvancedFilterEdit.TargetMode;
        WorkType = AdvancedFilterEdit.WorkType;
        RatioTag = AdvancedFilterEdit.RatioTag;
        Tool = AdvancedFilterEdit.Tool;
        PostedAfter = AdvancedFilterEdit.PostedAfter;
        PostedBefore = AdvancedFilterEdit.PostedBefore;
        MinWidth = AdvancedFilterEdit.MinWidth;
        MaxWidth = AdvancedFilterEdit.MaxWidth;
        MinHeight = AdvancedFilterEdit.MinHeight;
        MaxHeight = AdvancedFilterEdit.MaxHeight;
        MinBookmarks = AdvancedFilterEdit.MinBookmarks;
        MaxBookmarks = AdvancedFilterEdit.MaxBookmarks;
        AiFilter = AdvancedFilterEdit.AiFilter;
        OnPropertyChanged(nameof(HasAdvancedFilters));
        if (HasSearched) _ = SearchAsync();
    }

    // Editable copy of the advanced filters used by the flyout.
    public sealed partial class AdvancedFilterEditModel : ObservableObject
    {
        [ObservableProperty] private string _targetMode = "s_tag";
        [ObservableProperty] private string _workType = "illust_and_ugoira";
        [ObservableProperty] private string? _ratioTag;
        [ObservableProperty] private string? _tool;
        [ObservableProperty] private DateTimeOffset? _postedAfter;
        [ObservableProperty] private DateTimeOffset? _postedBefore;
        [ObservableProperty] private decimal? _minWidth;
        [ObservableProperty] private decimal? _maxWidth;
        [ObservableProperty] private decimal? _minHeight;
        [ObservableProperty] private decimal? _maxHeight;
        [ObservableProperty] private decimal? _minBookmarks;
        [ObservableProperty] private decimal? _maxBookmarks;
        [ObservableProperty] private string _aiFilter = "display";
    }

    // View options — mirrors Gallery/Rankings
    [ObservableProperty] private int _cardSize = 180;
    [ObservableProperty] private bool _isFixedHeight = true;
    [ObservableProperty] private bool _isNaturalHeight;
    [ObservableProperty] private bool _isGridView = true;
    [ObservableProperty] private bool _isListView;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private int _selectedCount;

    [ObservableProperty] private bool _isViewerExpanded;
    partial void OnIsViewerExpandedChanged(bool value) => OnPropertyChanged(nameof(IsViewerFullScreen));
    public bool IsViewerFullScreen => IsViewerExpanded;
    public double FixedCardTotalHeight => CardSize;
    public bool HasSelection => SelectedCount > 0;
    public bool ShowFixedGrid => IsFixedHeight && IsGridView;
    public bool ShowNaturalGrid => IsNaturalHeight && IsGridView;
    public bool HasResults => Results.Count > 0;

    public GalleryViewModel GalleryVm => AppServices.Get<GalleryViewModel>();
    public string ViewerSourceKey => $"Search:{BuildSearchWord()}:{SortOrder}:{SearchMode}";
    public SettingsService SettingsService => _settingsService;
    public bool ShowR18Buttons => _settingsService.Current.R18Mode != R18Mode.Off;

    public ObservableCollection<ArtworkCardViewModel> Results { get; } = [];

    // ── Pagination ("Pages" toggle) — mirrors EnhancedRankingsViewModel/GalleryViewModel's
    // pattern: default is autoload-on-scroll (ScrollChanged -> LoadMoreAsync, see the View's
    // code-behind); toggling this on switches to a discrete Prev/Next/page-jump UI instead,
    // fetching more from pixiv on demand (via LoadMoreAsync) whenever the requested page needs
    // data we haven't loaded yet.
    [ObservableProperty] private bool _usePagination;
    [ObservableProperty] private int _itemsPerPage = 60;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private bool _canGoPrevious;
    [ObservableProperty] private bool _canGoNext;
    [ObservableProperty] private int _displayPage = 1;
    [ObservableProperty] private string _pageInput = "";

    public int[] ItemsPerPageOptions { get; } = { 20, 50, 100, 200 };

    /// <summary>The slice of <see cref="Results"/> actually shown by the grid/list — the full set
    /// when <see cref="UsePagination"/> is off, or just the current page's worth when it's on.</summary>
    public ObservableCollection<ArtworkCardViewModel> DisplayResults { get; } = [];

    private void UpdateDisplayedResults()
    {
        IEnumerable<ArtworkCardViewModel> src = Results;
        if (UsePagination)
        {
            // Prefer pixiv's real reported total (see _knownTotal) over "however much we've
            // loaded so far" so the page count doesn't creep up page-by-page as you load more.
            var totalForPaging = Math.Max(_knownTotal ?? 0, Results.Count);
            TotalPages = Math.Max(1, (int)Math.Ceiling(totalForPaging / (double)ItemsPerPage));
            var clamped = Math.Clamp(DisplayPage, 1, TotalPages);
            if (clamped != DisplayPage) { DisplayPage = clamped; return; } // re-entrant via OnDisplayPageChanged
            CanGoPrevious = DisplayPage > 1;
            CanGoNext = DisplayPage < TotalPages || CanLoadMore;
            src = Results.Skip((DisplayPage - 1) * ItemsPerPage).Take(ItemsPerPage);
        }

        DisplayResults.Clear();
        foreach (var c in src) DisplayResults.Add(c);
        if (!IsLoading)
            StatusMessage = BuildResultsStatusMessage();
    }

    partial void OnUsePaginationChanged(bool value)
    {
        _settingsService.Update(s => s.SearchUsePagination = value);
        DisplayPage = 1;
        UpdateDisplayedResults();
    }

    partial void OnItemsPerPageChanged(int value)
    {
        _settingsService.Update(s => s.SearchItemsPerPage = value);
        DisplayPage = 1;
        UpdateDisplayedResults();
    }

    partial void OnDisplayPageChanged(int value) => UpdateDisplayedResults();
    partial void OnCanLoadMoreChanged(bool value) { if (UsePagination) UpdateDisplayedResults(); }

    [RelayCommand] private void TogglePagination() => UsePagination = !UsePagination;
    [RelayCommand] private void FirstPage() { if (DisplayPage > 1) DisplayPage = 1; }
    [RelayCommand] private void PreviousPage() { if (DisplayPage > 1) DisplayPage--; }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        var nextPage = DisplayPage + 1;
        await EnsureLoadedForPageAsync(nextPage);
        if (Results.Count > (nextPage - 1) * ItemsPerPage) DisplayPage = nextPage;
    }

    [RelayCommand]
    private async Task LastPageAsync()
    {
        while (CanLoadMore) await LoadMoreAsync();
        DisplayPage = TotalPages;
    }

    [RelayCommand]
    private async Task GoToPageAsync(int page)
    {
        if (page < 1) return;
        await EnsureLoadedForPageAsync(page);
        var maxPage = Math.Max(1, (int)Math.Ceiling(Results.Count / (double)ItemsPerPage));
        DisplayPage = Math.Clamp(page, 1, maxPage);
    }

    [RelayCommand]
    private async Task GoToPageInputAsync()
    {
        if (int.TryParse(PageInput.Trim(), out var page)) await GoToPageAsync(page);
        PageInput = "";
    }

    /// <summary>Auto-fetches more pixiv pages (via <see cref="LoadMoreAsync"/>) until enough
    /// items are loaded to display the requested page, or there's nothing left to load.</summary>
    private async Task EnsureLoadedForPageAsync(int page)
    {
        var needed = page * ItemsPerPage;
        while (Results.Count < needed && CanLoadMore)
            await LoadMoreAsync();
    }

    [RelayCommand] public void SetFixedHeight() { IsFixedHeight = true; IsNaturalHeight = false; }
    [RelayCommand] public void SetNaturalHeight() { IsFixedHeight = false; IsNaturalHeight = true; }
    [RelayCommand] public void SetGridView() { IsGridView = true; IsListView = false; }
    [RelayCommand] public void SetListView() { IsGridView = false; IsListView = true; }
    [RelayCommand] public void TogglePreview() => ShowPreview = !ShowPreview;

    // User-search view options (compact avatar-only vs. avatar + 4 recent thumbnails)
    [ObservableProperty] private bool _isUsersCompactView = true;
    [ObservableProperty] private bool _isUsersThumbnailView;
    partial void OnIsUsersCompactViewChanged(bool value) => IsUsersThumbnailView = !value;
    partial void OnIsUsersThumbnailViewChanged(bool value)
    {
        IsUsersCompactView = !value;
        if (value) _ = EnsureUserThumbnailsLoadedAsync();
    }
    [RelayCommand] public void SetUsersCompactView() { IsUsersCompactView = true; IsUsersThumbnailView = false; }
    [RelayCommand] public void SetUsersThumbnailView() { IsUsersCompactView = false; IsUsersThumbnailView = true; }

    private async Task EnsureUserThumbnailsLoadedAsync()
    {
        foreach (var card in UserResults)
            await card.LoadRecentThumbnailsAsync(_pixivClient, _imageLoader);
    }

    public GlobalSearchViewModel(
        PixivClient pixivClient,
        PixivImageLoader imageLoader,
        SettingsService settingsService)
    {
        _pixivClient = pixivClient;
        _imageLoader = imageLoader;
        _settingsService = settingsService;

        _cardSize = _settingsService.Current.CardSize;
        _usePagination = _settingsService.Current.SearchUsePagination;
        _itemsPerPage = _settingsService.Current.SearchItemsPerPage;

        _settingsService.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowR18Buttons));
            OnPropertyChanged(nameof(IsPremiumAccount));
            var shared = _settingsService.Current.CardSize;
            if (CardSize != shared) CardSize = shared;
            // If Premium lapsed while popularity sort was selected, fall back to date order.
            if (!IsPremiumAccount && SortOrder == "popular_d") SortOrder = "date_d";
        };

        // Guard against a stale/leftover selection from a previous session or a non-Premium
        // account whose SortOrder somehow ended up "popular_d" — don't wait for a settings
        // Changed event, correct it immediately so the ComboBox can't display/keep it selected.
        if (!IsPremiumAccount && SortOrder == "popular_d") SortOrder = "date_d";

        GalleryVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.HasTabs) && !GalleryVm.HasTabs)
            { ShowPreview = false; IsViewerExpanded = false; }
        };
    }

    partial void OnCardSizeChanged(int value)
    {
        OnPropertyChanged(nameof(FixedCardTotalHeight));
        if (_settingsService.Current.CardSize != value)
            _settingsService.Update(s => s.CardSize = value);
    }

    partial void OnIsFixedHeightChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }
    partial void OnIsNaturalHeightChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }
    partial void OnIsGridViewChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }
    partial void OnIsListViewChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));

    // Re-run the search automatically when Mode (Safe/R-18/All) or Sort changes,
    // instead of requiring the user to click Search again.
    partial void OnSearchModeChanged(string value) { if (HasSearched) _ = SearchAsync(); }
    partial void OnSortOrderChanged(string value) { if (HasSearched) _ = SearchAsync(); }

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(BuildSearchWord())) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Results.Clear();
        NovelResults.Clear();
        UserResults.Clear();
        DisplayPage = 1;
        _knownTotal = null;
        SelectedCount = 0;
        HasSearched = true;
        IsLoading = true;
        _currentPage = 1;
        StatusMessage = "Searching…";

        try
        {
            switch (SearchCategory)
            {
                case "novels": await SearchNovelsInternalAsync(ct); break;
                case "users": await SearchUsersInternalAsync(ct); break;
                default: await SearchArtworksWithBackfillAsync(ct); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"Search failed: {ex.Message}"; }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasNovelResults));
            OnPropertyChanged(nameof(HasUserResults));
        }
    }

    private async Task SearchArtworksInternalAsync(CancellationToken ct)
    {
        var result = await _pixivClient.SearchArtworksAsync(BuildSearchWord(), SortOrder, SearchMode, _currentPage, AdvancedOptions, ct);
        var section = result?.IllustManga;
        var data = section?.Data;
        if (data is null || data.Count == 0) { StatusMessage = "No results found."; CanLoadMore = false; return; }

        AddResults(FilterByCategory(data));
        UpdateKnownTotalAndCanLoadMore(section, data.Count);
        _currentPage++;
        StatusMessage = BuildResultsStatusMessage();
    }

    /// <summary>Runs the initial artworks search, then backfills additional pages (if any client-side
    /// filter stripped items) so the very first page shown isn't visibly short.</summary>
    private async Task SearchArtworksWithBackfillAsync(CancellationToken ct)
    {
        await SearchArtworksInternalAsync(ct);
        await BackfillResultsAsync(ItemsPerPage, ct);
        UpdateDisplayedResults();
    }

    /// <summary>
    /// Tracks the real result count/page count pixiv reports (see <see cref="PixivClient.SearchArtworksAsync"/>'s
    /// dedicated-endpoint routing) so pagination and the status message reflect pixiv's actual
    /// totals instead of just "however much we've loaded so far".
    /// </summary>
    private void UpdateKnownTotalAndCanLoadMore(ArtworkSearchSection? section, int pageSize)
    {
        if (section?.LastPage is { } lastPage)
        {
            _knownTotal = section.Total;
            CanLoadMore = _currentPage < lastPage;
        }
        else
        {
            // Combined /ajax/search/artworks/ fallback (no options / "all" work type) doesn't
            // report a reliable total or lastPage — fall back to the old count-based heuristic.
            CanLoadMore = pageSize >= 60;
        }
    }

    private string BuildResultsStatusMessage()
    {
        if (Results.Count == 0) return "No results found.";
        var label = BuildSearchWord();
        if (string.IsNullOrWhiteSpace(label)) label = SearchQuery.Trim();
        var total = _knownTotal ?? Results.Count;
        if (UsePagination)
        {
            var start = (DisplayPage - 1) * ItemsPerPage + 1;
            var end = Math.Min(DisplayPage * ItemsPerPage, Results.Count);
            return total > Results.Count
                ? $"Showing {start:N0}–{end:N0} of {total:N0} results for \"{label}\""
                : $"Showing {start:N0}–{end:N0} of {Results.Count:N0} results for \"{label}\"";
        }
        return total > Results.Count
            ? $"{Results.Count} of {total:N0} results for \"{label}\""
            : $"{Results.Count} results for \"{label}\"";
    }

    /// <summary>
    /// Defensive client-side safety net — normally a no-op now that <see cref="PixivClient.SearchArtworksAsync"/>
    /// routes work-type-filtered searches to pixiv's dedicated <c>/ajax/search/illustrations/</c>
    /// and <c>/ajax/search/manga/</c> endpoints (which do filter server-side, unlike the combined
    /// <c>/ajax/search/artworks/</c> endpoint). Kept in case pixiv's server-side filtering ever
    /// regresses or misses an edge case.
    /// </summary>
    private IReadOnlyList<ArtworkPreview> FilterByCategory(IReadOnlyList<ArtworkPreview> data)
    {
        return EffectiveWorkType switch
        {
            "illust" => data.Where(p => p.IllustType == 0).ToList(),
            "manga" => data.Where(p => p.IllustType == 1).ToList(),
            "ugoira" => data.Where(p => p.IllustType == 2).ToList(),
            "illust_and_ugoira" => data.Where(p => p.IllustType != 1).ToList(),
            _ => data,
        };
    }

    private async Task SearchNovelsInternalAsync(CancellationToken ct)
    {
        var result = await _pixivClient.SearchNovelsAsync(BuildSearchWord(), SortOrder, SearchMode, TargetMode, _currentPage, ct);
        var data = result?.Novels;
        if (data is null || data.Count == 0) { StatusMessage = "No novels found."; CanLoadMore = false; return; }

        foreach (var novel in data)
        {
            var card = new NovelCardViewModel(novel);
            NovelResults.Add(card);
            _ = card.LoadThumbnailAsync(_imageLoader, ct);
        }
        // Pixiv returns ~24 novels per page; use a conservative threshold so a
        // page of 20-23 still lets us try the next page.
        CanLoadMore = data.Count >= 20;
        _currentPage++;
        StatusMessage = $"{NovelResults.Count} novel(s) for \"{SearchQuery}\"";
    }

    private async Task SearchUsersInternalAsync(CancellationToken ct)
    {
        var result = await _pixivClient.SearchUsersAsync(SearchQuery.Trim(), _currentPage, ct);
        var data = result?.Users;
        if (data is null || data.Count == 0) { StatusMessage = "No users found."; CanLoadMore = false; return; }

        foreach (var user in data)
        {
            var card = new UserSearchCardViewModel(user);
            UserResults.Add(card);
            _ = card.LoadAvatarAsync(_imageLoader, ct);
            if (IsUsersThumbnailView)
                _ = card.LoadRecentThumbnailsAsync(_pixivClient, _imageLoader, ct);
        }
        CanLoadMore = data.Count >= 20;
        _currentPage++;
        StatusMessage = $"{UserResults.Count} user(s) for \"{SearchQuery}\"";
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!CanLoadMore || IsLoading || string.IsNullOrWhiteSpace(SearchQuery)) return;
        IsLoading = true;
        try
        {
            switch (SearchCategory)
            {
                case "novels": await SearchNovelsInternalAsync(CancellationToken.None); break;
                case "users": await SearchUsersInternalAsync(CancellationToken.None); break;
                default:
                    var before = Results.Count;
                    var result = await _pixivClient.SearchArtworksAsync(BuildSearchWord(), SortOrder, SearchMode, _currentPage, AdvancedOptions);
                    var section = result?.IllustManga;
                    var data = section?.Data;
                    if (data is null || data.Count == 0) { CanLoadMore = false; break; }

                    AddResults(FilterByCategory(data));
                    UpdateKnownTotalAndCanLoadMore(section, data.Count);
                    _currentPage++;
                    // Backfill this "load more" batch so client-side filtering (blocklist, R-18)
                    // doesn't leave a visibly short chunk of results.
                    var targetCount = before + ItemsPerPage;
                    await BackfillResultsAsync(targetCount, CancellationToken.None);
                    StatusMessage = BuildResultsStatusMessage();
                    GalleryVm.SyncViewerTabs(ViewerSourceKey, Results.ToList(), Results.Count);
                    break;
            }
        }
        catch (Exception ex) { StatusMessage = $"Load more failed: {ex.Message}"; }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasNovelResults));
            OnPropertyChanged(nameof(HasUserResults));
        }
    }

    private void AddResults(IReadOnlyList<ArtworkPreview> data)
    {
        foreach (var preview in data)
        {
            if (_settingsService.Current.IsArtworkHidden("Search", preview.UserId, preview.UserName, preview.Title, preview.Tags))
                continue;

            var card = new ArtworkCardViewModel(preview)
            {
                IsBlurred = _settingsService.Current.BlurR18Content && preview.IsR18
            };
            Results.Add(card);
            _ = card.LoadThumbnailAsync(_imageLoader);
        }
        UpdateDisplayedResults();
    }

    /// <summary>
    /// Fetches additional pixiv result pages until <see cref="Results"/> has at least
    /// <paramref name="targetCount"/> filtered items, or there's nothing left to load.
    /// Client-side filters (blocklist, R-18) can strip a variable
    /// number of items from each raw page, which would otherwise leave a visibly short/gappy
    /// page in the grid — this keeps fetching until the page is filled or results are exhausted.
    /// </summary>
    private async Task BackfillResultsAsync(int targetCount, CancellationToken ct)
    {
        while (Results.Count < targetCount && CanLoadMore && !ct.IsCancellationRequested)
        {
            var before = Results.Count;
            await SearchArtworksInternalAsync(ct);
            if (Results.Count == before) break; // no progress — avoid an infinite loop
        }
    }

    public void OpenCard(ArtworkCardViewModel card)
    {
        var navList = Results.ToList();
        Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMore = null;
        if (CanLoadMore)
        {
            loadMore = async () =>
            {
                await LoadMoreAsync();
                return Results.ToList();
            };
        }
        GalleryVm.OpenInViewer(card, navList, Results.Count, loadMore, ViewerSourceKey);
        ShowPreview = true;
    }

    [RelayCommand]
    public Task DownloadSelectedAsync()
    {
        var previews = Results.Where(c => c.IsSelected).Select(c => c.Artwork).ToList();
        if (previews.Count == 0) return Task.CompletedTask;
        return GalleryVm.DownloadPreviewsAsync(previews);
    }

    [RelayCommand]
    public Task DownloadAllVisibleAsync()
    {
        var previews = Results.Select(c => c.Artwork).ToList();
        if (previews.Count == 0) return Task.CompletedTask;
        return GalleryVm.DownloadPreviewsAsync(previews);
    }

    public void NotifySelectionChanged() => SelectedCount = Results.Count(c => c.IsSelected);

    [RelayCommand] public void SelectAll() { foreach (var c in Results) c.IsSelected = true; NotifySelectionChanged(); }
    [RelayCommand] public void ClearSelection() { foreach (var c in Results) c.IsSelected = false; SelectedCount = 0; }
}
