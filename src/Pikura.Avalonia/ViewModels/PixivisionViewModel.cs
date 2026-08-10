using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

/// <summary>
/// "Pixivision" tab — browses pixivision.net's editorial article feed and lets the
/// user drill into an article to view its embedded Pixiv artworks. Scraped best-effort
/// from the public site (see <see cref="PixivisionService"/>) since there is no
/// supported public API for pixivision content. Reuses <see cref="GalleryViewModel"/>
/// for the inline viewer/Hoshi/download commands, same composition pattern as
/// GlobalSearchViewModel/ViewedHistoryViewModel.
/// </summary>
public partial class PixivisionViewModel : ViewModelBase
{
    private readonly PixivisionService _pixivision;
    private readonly PixivClient _pixivClient;
    private readonly PixivImageLoader _imageLoader;
    private readonly SettingsService _settingsService;
    private readonly PixivisionSavedArticlesService _savedArticles;

    private const int MaxConcurrentWorkFetches = 4;

    private CancellationTokenSource? _articleCts;
    private CancellationTokenSource? _articlesListCts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingArticle;
    [ObservableProperty] private string _statusMessage = "Loading pixivision articles…";

    // Page-based pagination is the default; autoload restores the original infinite-scroll behavior.
    [ObservableProperty] private int _articlePage = 1;
    [ObservableProperty] private bool _hasNextPage;
    [ObservableProperty] private bool _isAutoloadMode;
    [ObservableProperty] private string _pageInput = "";
    public bool HasPrevPage => ArticlePage > 1;
    partial void OnArticlePageChanged(int value) => OnPropertyChanged(nameof(HasPrevPage));
    partial void OnIsAutoloadModeChanged(bool value) => OnPropertyChanged(nameof(UsePagedMode));

    /// <summary>Single-switch view of the paged/autoload choice — checked means paged, mirroring
    /// Rankings' "Pages" toggle so both sections present the same control instead of two
    /// separate mutually-exclusive buttons.</summary>
    public bool UsePagedMode
    {
        get => !IsAutoloadMode;
        set
        {
            if (value == !IsAutoloadMode) return;
            if (value) _ = SetPagedMode();
            else _ = SetAutoloadMode();
        }
    }

    /// <summary>Null = no date filter (default). Non-null restricts the list to articles
    /// published on that day. pixivision has no date-archive endpoint, so this is a best-effort
    /// client-side scan of the reverse-chronological feed, bounded by <see cref="MaxDateScanPages"/>.</summary>
    [ObservableProperty] private DateTime? _selectedDate;
    public bool IsFilteredByDate => SelectedDate.HasValue;
    public string DateLabel => SelectedDate?.ToString("yyyy-MM-dd") ?? "All dates";
    private const int MaxDateScanPages = 60;

    // ── Calendar "empty day" detection ─────────────────────────────────────
    // pixivision doesn't publish every day (some categories especially — e.g. Novel — can go
    // days/weeks between articles), so the date-picker calendar grays out days it knows have no
    // articles. Since there's no date-archive endpoint, this reuses the same reverse-chronological
    // page scan as GoToDateAsync, but accumulates progress across calls/months (the feed only
    // moves in one direction, so once we've scanned past a month's start we've necessarily seen
    // everything in it) so paging the calendar back and forth doesn't re-fetch already-seen pages.
    private readonly HashSet<DateTime> _scannedArticleDates = new();
    private int _dateScanNextPage = 1;
    private DateTime? _scanOldestDate;
    private bool _dateScanExhausted;

    /// <summary>True once we've scanned far back enough to know, definitively, whether every day
    /// in <paramref name="monthStart"/> (the 1st of that month) has an article or not.</summary>
    private bool IsMonthScanCovered(DateTime monthStart) =>
        _dateScanExhausted || (_scanOldestDate is { } d && d < monthStart);

    /// <summary>Scans additional pixivision feed pages (for the active category) until
    /// <paramref name="month"/> is fully covered, the feed is exhausted, or <see cref="MaxDateScanPages"/>
    /// is reached — whichever comes first. Safe/cheap to call repeatedly (e.g. every time the
    /// calendar's displayed month changes); already-covered months return immediately.</summary>
    public async Task EnsureMonthScannedAsync(DateTime month)
    {
        var monthStart = new DateTime(month.Year, month.Month, 1);
        if (monthStart > DateTime.Today) return; // nothing to know about future months

        while (!IsMonthScanCovered(monthStart) && !_dateScanExhausted && _dateScanNextPage <= MaxDateScanPages)
        {
            PixivisionArticlePage result;
            try { result = await _pixivision.GetArticlesAsync(SelectedCategory.Slug, _dateScanNextPage); }
            catch { break; }

            if (result.Items.Count == 0) { _dateScanExhausted = true; break; }

            foreach (var a in result.Items)
            {
                if (a.PublishedDate is not { } d) continue;
                var day = d.Date;
                _scannedArticleDates.Add(day);
                if (_scanOldestDate is null || day < _scanOldestDate) _scanOldestDate = day;
            }

            _dateScanNextPage++;
            if (!result.HasNextPage) _dateScanExhausted = true;
        }
    }

    /// <summary>Days in <paramref name="month"/> confirmed to have zero articles — empty unless
    /// <see cref="EnsureMonthScannedAsync"/> has already covered that month (never guesses).</summary>
    public IReadOnlyList<DateTime> GetEmptyDaysInMonth(DateTime month)
    {
        var monthStart = new DateTime(month.Year, month.Month, 1);
        if (!IsMonthScanCovered(monthStart)) return [];

        var empty = new List<DateTime>();
        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(month.Year, month.Month, day);
            if (date > DateTime.Today) continue; // future days are already handled by DisplayDateEnd
            if (!_scannedArticleDates.Contains(date)) empty.Add(date);
        }
        return empty;
    }

    partial void OnSelectedCategoryChanged(PixivisionCategory value)
    {
        // The feed (and thus which days have articles) differs per category, so a category
        // switch invalidates all scan progress.
        _scannedArticleDates.Clear();
        _dateScanNextPage = 1;
        _scanOldestDate = null;
        _dateScanExhausted = false;
    }

    public IReadOnlyList<PixivisionCategoryGroup> CategoryGroups => PixivisionService.CategoryGroups;
    [ObservableProperty] private PixivisionCategory _selectedCategory = PixivisionService.Categories[0];

    // Article list Grid/List view toggle — mirrors Gallery.
    [ObservableProperty] private bool _isArticleGridView = true;
    [ObservableProperty] private bool _isArticleListView;
    [ObservableProperty] private int _articleCardSize = 220;
    // Hides the "Monthly Ranking" / "Featured" sidebar next to the article list.
    [ObservableProperty] private bool _showSidebarWidgets = true;

    [ObservableProperty] private bool _isViewingArticle;
    [ObservableProperty] private long _currentArticleId;
    [ObservableProperty] private string? _articleTitle;
    [ObservableProperty] private Bitmap? _articleEyecatch;

    // "Read later" — set to the summary passed into OpenArticleAsync so ToggleSaveArticle has
    // enough (Id/Title/ThumbnailUrl/Tags) to persist without needing a second network round-trip.
    private PixivisionArticleCardViewModel? _currentArticleSummary;
    public bool IsCurrentArticleSaved => CurrentArticleId > 0 && _savedArticles.IsSaved(CurrentArticleId);
    public string SaveArticleButtonLabel => IsCurrentArticleSaved ? "🔖 Saved" : "🔖 Save for later";
    public ObservableCollection<PixivisionArticleCardViewModel> SavedArticles { get; } = [];
    public bool HasSavedArticles => SavedArticles.Count > 0;
    public string SavedArticlesButtonLabel => $"🔖 Saved ({SavedArticles.Count})";
    [ObservableProperty] private bool _isViewingSavedArticles;

    // Interview-article ("Artist's Spotlight" etc.) extras — absent (null/empty) on regular articles.
    [ObservableProperty] private PixivisionProfile? _articleProfile;
    [ObservableProperty] private Bitmap? _articleProfileAvatar;
    public bool HasArticleProfile => ArticleProfile != null;
    partial void OnArticleProfileChanged(PixivisionProfile? value) => OnPropertyChanged(nameof(HasArticleProfile));
    public ObservableCollection<PixivisionTocEntryViewModel> ArticleTableOfContents { get; } = [];
    public bool HasArticleToc => ArticleTableOfContents.Count > 0;

    // Toggle between showing embedded artworks inline (where they appear in the article's
    // reading flow) vs. in the separate "Featured Artworks" gallery at the bottom (default).
    [ObservableProperty] private bool _isInlineArtworkLayout;
    partial void OnIsInlineArtworkLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFeaturedWorksGallery));
        OnPropertyChanged(nameof(InlineArtworkLayoutLabel));
    }
    public bool ShowFeaturedWorksGallery => HasFeaturedWorks && !IsInlineArtworkLayout;
    /// <summary>Label reflecting the toggle's current state, so it reads as a mode switch
    /// ("Inline" vs "Gallery") rather than a plain on/off checkbox.</summary>
    public string InlineArtworkLayoutLabel => IsInlineArtworkLayout ? "Inline" : "Gallery";

    [ObservableProperty] private int _cardSize = 180;
    // Featured-artwork Fixed/Natural height toggle — mirrors Gallery/Search.
    [ObservableProperty] private bool _isFixedHeight = true;
    [ObservableProperty] private bool _isNaturalHeight;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private bool _isViewerExpanded;
    [ObservableProperty] private int _selectedCount;
    public string SidePanelLabel => ShowPreview ? "Hide Panel" : "Side Panel";
    partial void OnShowPreviewChanged(bool value) => OnPropertyChanged(nameof(SidePanelLabel));

    public ObservableCollection<PixivisionArticleCardViewModel> Articles { get; } = [];
    public ObservableCollection<PixivisionParagraphViewModel> ArticleParagraphs { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> FeaturedWorks { get; } = [];

    // "Newest articles tagged X" / "If you liked X, you will also love..." — pixivision's own
    // per-article related-content widgets.
    public ObservableCollection<PixivisionArticleCardViewModel> RelatedLatest { get; } = [];
    public ObservableCollection<PixivisionArticleCardViewModel> RelatedPopular { get; } = [];
    public bool HasRelatedLatest => RelatedLatest.Count > 0;
    public bool HasRelatedPopular => RelatedPopular.Count > 0;
    [ObservableProperty] private string? _relatedTagName;
    public string RelatedLatestHeading => string.IsNullOrEmpty(RelatedTagName)
        ? "Newest related articles"
        : $"Newest articles tagged {RelatedTagName}";
    public string RelatedPopularHeading => string.IsNullOrEmpty(RelatedTagName)
        ? "You may also like…"
        : $"If you liked {RelatedTagName}, you will also love…";
    partial void OnRelatedTagNameChanged(string? value) { OnPropertyChanged(nameof(RelatedLatestHeading)); OnPropertyChanged(nameof(RelatedPopularHeading)); }

    // pixivision's own sidebar widgets — refreshed on category/page changes (see PopulateSidebarWidgets).
    public ObservableCollection<PixivisionArticleCardViewModel> MonthlyRanking { get; } = [];
    public ObservableCollection<PixivisionArticleCardViewModel> Featured { get; } = [];
    public bool HasMonthlyRanking => MonthlyRanking.Count > 0;
    public bool HasFeatured => Featured.Count > 0;
    public bool HasSidebarWidgets => HasMonthlyRanking || HasFeatured;
    public bool ShowSidebarPanel => ShowSidebarWidgets && HasSidebarWidgets;
    partial void OnShowSidebarWidgetsChanged(bool value) => OnPropertyChanged(nameof(ShowSidebarPanel));

    public bool HasArticles => Articles.Count > 0;
    public bool HasFeaturedWorks => FeaturedWorks.Count > 0;
    public bool HasSelection => SelectedCount > 0;
    public double FixedCardTotalHeight => CardSize;
    public bool ShowFixedGrid => IsFixedHeight;
    public bool ShowNaturalGrid => IsNaturalHeight;
    partial void OnIsFixedHeightChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }
    partial void OnIsNaturalHeightChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }

    partial void OnIsViewerExpandedChanged(bool value) => OnPropertyChanged(nameof(IsViewerFullScreen));
    public bool IsViewerFullScreen => IsViewerExpanded;
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));

    public GalleryViewModel GalleryVm => AppServices.Get<GalleryViewModel>();
    public string ViewerSourceKey => $"Pixivision:{CurrentArticleId}";
    public SettingsService SettingsService => _settingsService;

    [RelayCommand] public void TogglePreview() => ShowPreview = !ShowPreview;
    [RelayCommand] public void SetFixedHeight() { IsFixedHeight = true; IsNaturalHeight = false; }
    [RelayCommand] public void SetNaturalHeight() { IsFixedHeight = false; IsNaturalHeight = true; }
    [RelayCommand] public void SetArticleGridView() { IsArticleGridView = true; IsArticleListView = false; }
    [RelayCommand] public void SetArticleListView() { IsArticleGridView = false; IsArticleListView = true; }
    // Autoload accumulates every scrolled-through page into one long list; switching back to
    // paged mode has to collapse that back down to a single page's worth of articles, or the
    // paged view would show a confusing mix of everything loaded so far. Reload the page the
    // user last scrolled to (not page 1) so the switch feels seamless rather than a "reset".
    [RelayCommand] public Task SetPagedMode() { IsAutoloadMode = false; return LoadArticlesPageAsync(Math.Max(1, ArticlePage), append: false); }
    [RelayCommand] public Task SetAutoloadMode() { IsAutoloadMode = true; return GoToPageAsync(1); }

    public PixivisionViewModel(
        PixivisionService pixivision,
        PixivClient pixivClient,
        PixivImageLoader imageLoader,
        SettingsService settingsService,
        PixivisionSavedArticlesService savedArticles)
    {
        _pixivision = pixivision;
        _pixivClient = pixivClient;
        _imageLoader = imageLoader;
        _settingsService = settingsService;
        _savedArticles = savedArticles;
        _cardSize = _settingsService.Current.CardSize;

        GalleryVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.HasTabs) && !GalleryVm.HasTabs)
            { ShowPreview = false; IsViewerExpanded = false; }
        };

        _savedArticles.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsCurrentArticleSaved));
            OnPropertyChanged(nameof(SaveArticleButtonLabel));
            RefreshSavedArticles();
            RefreshCardSavedStates();
        };
        RefreshSavedArticles();

        _ = LoadArticlesAsync();
    }

    [RelayCommand]
    public Task LoadArticlesAsync() => GoToPageAsync(1);

    [RelayCommand]
    public Task GoToPageAsync(int page)
    {
        SelectedDate = null;
        OnPropertyChanged(nameof(IsFilteredByDate));
        OnPropertyChanged(nameof(DateLabel));
        return LoadArticlesPageAsync(page, append: false);
    }

    [RelayCommand]
    public Task LoadMoreArticlesAsync()
    {
        if (!HasNextPage || IsLoading || IsFilteredByDate) return Task.CompletedTask;
        return LoadArticlesPageAsync(ArticlePage + 1, append: true);
    }

    private async Task LoadArticlesPageAsync(int page, bool append)
    {
        if (page < 1) return;

        // Switching modes (paged <-> autoload) mid-fetch used to let two loads mutate the shared
        // Articles collection at once — cancel any in-flight fetch first so only one is ever
        // touching it, avoiding a crash from concurrent collection mutation during layout.
        _articlesListCts?.Cancel();
        var cts = new CancellationTokenSource();
        _articlesListCts = cts;

        if (!append) { Articles.Clear(); SelectedCount = 0; }
        IsLoading = true;
        StatusMessage = "Loading pixivision articles…";

        try
        {
            var result = await _pixivision.GetArticlesAsync(SelectedCategory.Slug, page, cts.Token);
            if (cts.IsCancellationRequested) return;

            ArticlePage = result.Page;
            HasNextPage = result.HasNextPage;
            foreach (var a in result.Items)
            {
                if (cts.IsCancellationRequested) return;
                var card = new PixivisionArticleCardViewModel(a);
                Articles.Add(card);
                _ = card.LoadThumbnailAsync(_imageLoader);
            }
            StatusMessage = Articles.Count == 0
                ? "Couldn't load pixivision articles — check your connection."
                : IsAutoloadMode
                    ? $"{Articles.Count} article(s) loaded"
                    : $"{Articles.Count} article(s) — page {ArticlePage}";
            OnPropertyChanged(nameof(HasArticles));
            // The sidebar widgets are category-scoped on pixivision's own site, so refresh them
            // on every fresh page load (category switch, page jump) — but not on autoload's
            // incremental appends, since the widgets don't change between pages of one category.
            if (!append) PopulateSidebarWidgets(result);
            RefreshCardSavedStates();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"Failed to load pixivision: {ex.Message}"; }
        finally { if (!cts.IsCancellationRequested) IsLoading = false; }
    }

    private void PopulateSidebarWidgets(PixivisionArticlePage result)
    {
        if (result.MonthlyRanking.Count == 0 && result.Featured.Count == 0) return;

        MonthlyRanking.Clear();
        Featured.Clear();
        for (var i = 0; i < result.MonthlyRanking.Count; i++)
        {
            var card = new PixivisionArticleCardViewModel(result.MonthlyRanking[i]) { Rank = i + 1 };
            MonthlyRanking.Add(card);
            _ = card.LoadThumbnailAsync(_imageLoader);
        }
        foreach (var a in result.Featured)
        {
            var card = new PixivisionArticleCardViewModel(a);
            Featured.Add(card);
            _ = card.LoadThumbnailAsync(_imageLoader);
        }
        OnPropertyChanged(nameof(HasMonthlyRanking));
        OnPropertyChanged(nameof(HasFeatured));
        OnPropertyChanged(nameof(HasSidebarWidgets));
        OnPropertyChanged(nameof(ShowSidebarPanel));
    }

    [RelayCommand] public Task NextPageAsync() => HasNextPage ? GoToPageAsync(ArticlePage + 1) : Task.CompletedTask;
    [RelayCommand] public Task PrevPageAsync() => HasPrevPage ? GoToPageAsync(ArticlePage - 1) : Task.CompletedTask;

    [RelayCommand]
    public Task GoToPageInputAsync()
    {
        var input = PageInput;
        PageInput = "";
        return int.TryParse(input.Trim(), out var page) && page >= 1
            ? GoToPageAsync(page)
            : Task.CompletedTask;
    }

    [RelayCommand]
    public Task SelectCategoryAsync(PixivisionCategory category)
    {
        if (SelectedCategory == category && !IsFilteredByDate) return Task.CompletedTask;
        SelectedCategory = category;
        return GoToPageAsync(1);
    }

    /// <summary>Best-effort client-side date filter — pixivision has no date-archive endpoint,
    /// so this pages through the reverse-chronological feed collecting matches and stops early
    /// once it scans past the target date, bounded by <see cref="MaxDateScanPages"/>.</summary>
    [RelayCommand]
    public async Task GoToDateAsync(DateTime date)
    {
        var target = date.Date;
        SelectedDate = target;
        OnPropertyChanged(nameof(IsFilteredByDate));
        OnPropertyChanged(nameof(DateLabel));

        Articles.Clear();
        SelectedCount = 0;
        HasNextPage = false;
        IsLoading = true;
        StatusMessage = $"Searching pixivision for articles on {target:yyyy-MM-dd}…";

        try
        {
            var page = 1;
            var scannedPages = 0;
            while (page <= MaxDateScanPages)
            {
                var result = await _pixivision.GetArticlesAsync(SelectedCategory.Slug, page);
                scannedPages++;
                if (result.Items.Count == 0) break;

                var passedTarget = false;
                foreach (var a in result.Items)
                {
                    if (a.PublishedDate is not { } d) continue;
                    if (d.Date == target)
                    {
                        var card = new PixivisionArticleCardViewModel(a);
                        Articles.Add(card);
                        _ = card.LoadThumbnailAsync(_imageLoader);
                    }
                    else if (d.Date < target)
                    {
                        passedTarget = true;
                    }
                }

                if (passedTarget || !result.HasNextPage) break;
                page++;
            }

            StatusMessage = Articles.Count == 0
                ? $"No articles found on {target:yyyy-MM-dd} (searched {scannedPages} page(s))."
                : $"{Articles.Count} article(s) on {target:yyyy-MM-dd}";
            OnPropertyChanged(nameof(HasArticles));
            RefreshCardSavedStates();
        }
        catch (Exception ex) { StatusMessage = $"Date search failed: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public Task GoToAllTimeAsync()
    {
        SelectedDate = null;
        OnPropertyChanged(nameof(IsFilteredByDate));
        OnPropertyChanged(nameof(DateLabel));
        return GoToPageAsync(1);
    }

    [RelayCommand]
    public async Task OpenArticleAsync(PixivisionArticleCardViewModel summary)
    {
        _articleCts?.Cancel();
        var cts = new CancellationTokenSource();
        _articleCts = cts;

        IsViewingArticle = true;
        IsViewingSavedArticles = false;
        _currentArticleSummary = summary;
        CurrentArticleId = summary.Id;
        ArticleTitle = summary.Title;
        ArticleEyecatch = summary.Thumbnail;
        OnPropertyChanged(nameof(IsCurrentArticleSaved));
        OnPropertyChanged(nameof(SaveArticleButtonLabel));
        ArticleParagraphs.Clear();
        FeaturedWorks.Clear();
        RelatedLatest.Clear();
        RelatedPopular.Clear();
        RelatedTagName = null;
        SelectedCount = 0;
        ArticleProfile = null;
        ArticleProfileAvatar = null;
        ArticleTableOfContents.Clear();
        OnPropertyChanged(nameof(HasFeaturedWorks));
        OnPropertyChanged(nameof(ShowFeaturedWorksGallery));
        OnPropertyChanged(nameof(HasRelatedLatest));
        OnPropertyChanged(nameof(HasRelatedPopular));
        OnPropertyChanged(nameof(HasArticleToc));
        IsLoadingArticle = true;

        try
        {
            var detail = await _pixivision.GetArticleAsync(summary.Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (detail == null)
            {
                StatusMessage = "Failed to load article.";
                return;
            }

            ArticleTitle = detail.Title;

            ArticleProfile = detail.Profile;
            if (detail.Profile?.HasAvatar == true)
                _ = LoadProfileAvatarAsync(detail.Profile.AvatarUrl!, cts.Token);

            for (var i = 0; i < detail.TableOfContents.Count; i++)
                ArticleTableOfContents.Add(new PixivisionTocEntryViewModel(detail.TableOfContents[i], i));
            OnPropertyChanged(nameof(HasArticleToc));

            var avatarCache = new Dictionary<string, List<PixivisionParagraphViewModel>>();
            var artworkPvmByIllustId = new Dictionary<string, PixivisionParagraphViewModel>();
            foreach (var p in detail.Paragraphs)
            {
                var pvm = new PixivisionParagraphViewModel(p);
                ArticleParagraphs.Add(pvm);
                if (p.HasAvatar)
                {
                    // Interview answers typically all share the same interviewee avatar — fetch
                    // each distinct URL once and fan the bitmap out to every paragraph using it.
                    if (avatarCache.TryGetValue(p.AvatarUrl!, out var waiters))
                        waiters.Add(pvm);
                    else
                        avatarCache[p.AvatarUrl!] = [pvm];
                }
                if (p.IsArtwork && !string.IsNullOrEmpty(p.IllustId))
                    artworkPvmByIllustId[p.IllustId] = pvm;
                if (p.HasRelatedCards)
                {
                    foreach (var a in p.RelatedCards!)
                    {
                        var card = new PixivisionArticleCardViewModel(a);
                        pvm.RelatedCards.Add(card);
                        _ = card.LoadThumbnailAsync(_imageLoader);
                    }
                }
            }
            foreach (var (url, waiters) in avatarCache)
                _ = LoadAvatarAsync(url, waiters, cts.Token);
            if (!string.IsNullOrEmpty(detail.EyecatchUrl))
                _ = LoadEyecatchAsync(detail.EyecatchUrl, cts.Token);

            RelatedTagName = detail.RelatedTagName;
            foreach (var a in detail.RelatedLatest)
            {
                var card = new PixivisionArticleCardViewModel(a);
                RelatedLatest.Add(card);
                _ = card.LoadThumbnailAsync(_imageLoader);
            }
            foreach (var a in detail.RelatedPopular)
            {
                var card = new PixivisionArticleCardViewModel(a);
                RelatedPopular.Add(card);
                _ = card.LoadThumbnailAsync(_imageLoader);
            }
            OnPropertyChanged(nameof(HasRelatedLatest));
            OnPropertyChanged(nameof(HasRelatedPopular));
            RefreshCardSavedStates();

            // Each work needs its own GetArtworkDetailAsync round-trip, so with a dozen-plus
            // featured works even 4-way concurrency takes a couple of seconds — waiting for
            // Task.WhenAll before adding ANY of them made the whole gallery appear to pop in at
            // once after that full delay. Instead, flush cards into FeaturedWorks in order as
            // soon as each contiguous run finishes, so early cards appear immediately while later
            // ones are still loading.
            using var gate = new SemaphoreSlim(MaxConcurrentWorkFetches);
            var cards = new ArtworkCardViewModel?[detail.Works.Count];
            var isDone = new bool[detail.Works.Count];
            var nextToFlush = 0;

            void FlushReadyCards()
            {
                while (nextToFlush < cards.Length && isDone[nextToFlush])
                {
                    var card = cards[nextToFlush];
                    if (card != null && !_settingsService.Current.IsArtworkHidden("Pixivision", card.UserId, card.UserName, card.Title, card.Tags))
                    {
                        FeaturedWorks.Add(card);
                        _ = card.LoadThumbnailAsync(_imageLoader);
                        if (artworkPvmByIllustId.TryGetValue(detail.Works[nextToFlush].IllustId, out var artworkPvm))
                            artworkPvm.Artwork = card;
                    }
                    nextToFlush++;
                }
                OnPropertyChanged(nameof(HasFeaturedWorks));
                OnPropertyChanged(nameof(ShowFeaturedWorksGallery));
                StatusMessage = $"{FeaturedWorks.Count} featured artwork(s)";
            }

            var tasks = detail.Works.Select(async (work, i) =>
            {
                await gate.WaitAsync(cts.Token);
                try { cards[i] = await BuildCardAsync(work, cts.Token); }
                finally
                {
                    isDone[i] = true;
                    gate.Release();
                    if (!cts.IsCancellationRequested) FlushReadyCards();
                }
            });
            await Task.WhenAll(tasks);
            if (cts.IsCancellationRequested) return;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"Failed to load article: {ex.Message}"; }
        finally { if (!cts.IsCancellationRequested) IsLoadingArticle = false; }
    }

    private async Task<ArtworkCardViewModel?> BuildCardAsync(PixivisionFeaturedWork work, CancellationToken ct)
    {
        ArtworkPreview preview;
        try
        {
            var b = await _pixivClient.GetArtworkDetailAsync(work.IllustId, ct);
            preview = b != null
                ? new ArtworkPreview
                {
                    Id = b.IllustId ?? work.IllustId,
                    Title = b.IllustTitle ?? work.Title,
                    UserName = b.UserName ?? work.UserName,
                    UserId = b.UserId ?? work.UserId,
                    ThumbnailUrl = b.ThumbnailUrl ?? work.ThumbnailUrl,
                    PageCount = b.PageCount > 0 ? b.PageCount : 1,
                    IllustType = b.IllustType,
                    XRestrict = b.XRestrict,
                    AiType = b.AiType,
                    Width = b.Width,
                    Height = b.Height,
                    BookmarkCount = b.BookmarkCount,
                    LikeCount = b.LikeCount,
                    ViewCount = b.ViewCount,
                    Tags = b.Tags?.Tags?.Select(t => t.Tag ?? string.Empty).ToList() ?? []
                }
                : FallbackPreview(work);
        }
        catch { preview = FallbackPreview(work); }

        return new ArtworkCardViewModel(preview)
        {
            IsBlurred = _settingsService.Current.BlurR18Content && preview.IsR18,
            Caption = work.Caption
        };
    }

    private async Task LoadEyecatchAsync(string url, CancellationToken ct)
    {
        try
        {
            var skBitmap = await _imageLoader.FetchBitmapAsync(url, ThumbnailSize.Medium, ct);
            if (skBitmap is null || ct.IsCancellationRequested) return;
            var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap), ct);
            skBitmap.Dispose();
            if (bmp is not null && !ct.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(() => ArticleEyecatch = bmp);
        }
        catch { /* non-fatal */ }
    }

    private async Task LoadProfileAvatarAsync(string url, CancellationToken ct)
    {
        try
        {
            var skBitmap = await _imageLoader.FetchBitmapAsync(url, ThumbnailSize.Small, ct);
            if (skBitmap is null || ct.IsCancellationRequested) return;
            var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap), ct);
            skBitmap.Dispose();
            if (bmp is not null && !ct.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(() => ArticleProfileAvatar = bmp);
        }
        catch { /* non-fatal */ }
    }

    private async Task LoadAvatarAsync(string url, List<PixivisionParagraphViewModel> waiters, CancellationToken ct)
    {
        try
        {
            var skBitmap = await _imageLoader.FetchBitmapAsync(url, ThumbnailSize.Small, ct);
            if (skBitmap is null || ct.IsCancellationRequested) return;
            var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap), ct);
            skBitmap.Dispose();
            if (bmp is null || ct.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var w in waiters) w.Avatar = bmp;
            });
        }
        catch { /* non-fatal */ }
    }

    private static ArtworkPreview FallbackPreview(PixivisionFeaturedWork work) => new()
    {
        Id = work.IllustId,
        Title = work.Title,
        UserId = work.UserId,
        UserName = work.UserName,
        ThumbnailUrl = work.ThumbnailUrl,
        PageCount = 1
    };

    [RelayCommand]
    public void BackToList()
    {
        _articleCts?.Cancel();
        IsViewingArticle = false;
        ShowPreview = false;
        FeaturedWorks.Clear();
        ArticleParagraphs.Clear();
        RelatedLatest.Clear();
        RelatedPopular.Clear();
        ArticleProfile = null;
        ArticleProfileAvatar = null;
        ArticleTableOfContents.Clear();
        OnPropertyChanged(nameof(HasFeaturedWorks));
        OnPropertyChanged(nameof(ShowFeaturedWorksGallery));
        OnPropertyChanged(nameof(HasRelatedLatest));
        OnPropertyChanged(nameof(HasRelatedPopular));
        OnPropertyChanged(nameof(HasArticleToc));
    }

    [RelayCommand]
    public void OpenArticleInBrowser()
    {
        if (CurrentArticleId <= 0) return;
        OpenArticleUrlInBrowser(CurrentArticleId);
    }

    [RelayCommand]
    public void OpenArticleCardInBrowser(PixivisionArticleCardViewModel card) => OpenArticleUrlInBrowser(card.Id);

    private static void OpenArticleUrlInBrowser(long articleId)
    {
        try { Process.Start(new ProcessStartInfo($"https://www.pixivision.net/en/a/{articleId}") { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>Saves/un-saves the article currently open in detail view for reading later.</summary>
    [RelayCommand]
    public void ToggleSaveArticle()
    {
        if (_currentArticleSummary == null || CurrentArticleId <= 0) return;
        _savedArticles.Toggle(_currentArticleSummary.ToSummary());
        // _savedArticles.Changed already raises IsCurrentArticleSaved/RefreshSavedArticles, but
        // fire it here too so the button flips instantly rather than waiting on the event.
        OnPropertyChanged(nameof(IsCurrentArticleSaved));
        OnPropertyChanged(nameof(SaveArticleButtonLabel));
    }

    /// <summary>Saves/un-saves any article card — used by the card's bookmark icon and right-click
    /// context menu (article grid/list, related widgets, TOC recommendations, etc).</summary>
    [RelayCommand]
    public void ToggleSaveCard(PixivisionArticleCardViewModel card) => _savedArticles.Toggle(card.ToSummary());

    /// <summary>Un-saves an article directly from the "Saved" list (e.g. its card's context menu).</summary>
    [RelayCommand]
    public void RemoveSavedArticle(PixivisionArticleCardViewModel card) => _savedArticles.Remove(card.Id);

    /// <summary>Syncs every currently-loaded card's <see cref="PixivisionArticleCardViewModel.IsSaved"/>
    /// flag with the persisted saved list — called on load and whenever the list changes.</summary>
    private void RefreshCardSavedStates()
    {
        foreach (var card in Articles.Concat(RelatedLatest).Concat(RelatedPopular)
                     .Concat(MonthlyRanking).Concat(Featured).Concat(SavedArticles)
                     .Concat(ArticleParagraphs.SelectMany(p => p.RelatedCards)))
        {
            card.IsSaved = _savedArticles.IsSaved(card.Id);
        }
    }

    [RelayCommand]
    public void ShowSavedArticles() => IsViewingSavedArticles = true;

    [RelayCommand]
    public void HideSavedArticles() => IsViewingSavedArticles = false;

    private void RefreshSavedArticles()
    {
        SavedArticles.Clear();
        foreach (var entry in _savedArticles.GetAll())
        {
            var card = new PixivisionArticleCardViewModel(entry.ToSummary()) { IsSaved = true };
            SavedArticles.Add(card);
            _ = card.LoadThumbnailAsync(_imageLoader);
        }
        OnPropertyChanged(nameof(HasSavedArticles));
        OnPropertyChanged(nameof(SavedArticlesButtonLabel));
    }

    public void OpenCard(ArtworkCardViewModel card)
    {
        var navList = FeaturedWorks.ToList();
        GalleryVm.OpenInViewer(card, navList, FeaturedWorks.Count, null, ViewerSourceKey);
        ShowPreview = true;
    }

    public void NotifySelectionChanged() => SelectedCount = FeaturedWorks.Count(c => c.IsSelected);
    [RelayCommand] public void SelectAll() { foreach (var c in FeaturedWorks) c.IsSelected = true; NotifySelectionChanged(); }
    [RelayCommand] public void ClearSelection() { foreach (var c in FeaturedWorks) c.IsSelected = false; SelectedCount = 0; }

    [RelayCommand]
    public Task DownloadSelectedAsync()
    {
        var previews = FeaturedWorks.Where(c => c.IsSelected).Select(c => c.Artwork).ToList();
        if (previews.Count == 0) return Task.CompletedTask;
        return GalleryVm.DownloadPreviewsAsync(previews);
    }

    [RelayCommand]
    public Task DownloadAllVisibleAsync()
    {
        var previews = FeaturedWorks.Select(c => c.Artwork).ToList();
        if (previews.Count == 0) return Task.CompletedTask;
        return GalleryVm.DownloadPreviewsAsync(previews);
    }
}

/// <summary>One entry in an interview article's "Index" table-of-contents widget.
/// <see cref="HeadingOrdinal"/> is this entry's 0-based position among the article's
/// <see cref="PixivisionParagraphKind.Heading"/> blocks, used to jump to the matching
/// section when clicked (see <see cref="PixivisionView.OnTocEntryClicked"/>).</summary>
public sealed class PixivisionTocEntryViewModel(string text, int headingOrdinal)
{
    public string Text { get; } = text;
    public int HeadingOrdinal { get; } = headingOrdinal;
}

/// <summary>Thin, bindable wrapper around <see cref="PixivisionParagraph"/> that adds an
/// async-loaded avatar bitmap for interview-article answer blocks (see
/// <see cref="PixivisionParagraphKind.Answer"/>), mirroring ArtworkCardViewModel's pattern.</summary>
public partial class PixivisionParagraphViewModel : ObservableObject
{
    private readonly PixivisionParagraph _model;

    public PixivisionParagraphViewModel(PixivisionParagraph model) => _model = model;

    public string Text => _model.Text;
    public List<PixivisionParagraphLink> Links => _model.Links;
    public bool HasText => _model.HasText;
    public bool HasLinks => _model.HasLinks;
    public bool IsHeading => _model.IsHeading;
    public bool IsQuestion => _model.IsQuestion;
    public bool IsAnswer => _model.IsAnswer;
    public bool IsArtwork => _model.IsArtwork;
    public string? IllustId => _model.IllustId;
    public bool IsPlainText => _model.Kind == PixivisionParagraphKind.Text;
    public bool HasRelatedCards => _model.HasRelatedCards;

    [ObservableProperty] private Bitmap? _avatar;
    /// <summary>Only set for <see cref="IsArtwork"/> blocks, once the referenced artwork has
    /// finished loading (see <see cref="PixivisionViewModel.OpenArticleAsync"/>).</summary>
    [ObservableProperty] private ArtworkCardViewModel? _artwork;
    /// <summary>Only populated for <see cref="IsHeading"/> blocks that embed a recommended-article
    /// card grid (see <see cref="PixivisionParagraph.RelatedCards"/>).</summary>
    public ObservableCollection<PixivisionArticleCardViewModel> RelatedCards { get; } = [];
}

/// <summary>Thin, bindable wrapper around <see cref="PixivisionArticleSummary"/> that adds an
/// async-loaded thumbnail bitmap for the article grid, mirroring ArtworkCardViewModel's pattern.</summary>
public partial class PixivisionArticleCardViewModel : ObservableObject
{
    public long Id { get; }
    public string Title { get; }
    public string? ThumbnailUrl { get; }
    public IReadOnlyList<string> Tags { get; }
    public DateTime? PublishedDate { get; }
    public string DateLabel => PublishedDate?.ToString("yyyy.MM.dd") ?? string.Empty;
    public bool HasDate => PublishedDate.HasValue;
    public bool HasTags => Tags.Count > 0;

    /// <summary>1-based rank, only set for items in the "Monthly Ranking" sidebar widget.</summary>
    public int Rank { get; init; }
    public bool HasRank => Rank > 0;

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isSaved;

    public PixivisionArticleCardViewModel(PixivisionArticleSummary summary)
    {
        Id = summary.Id;
        Title = summary.Title;
        ThumbnailUrl = summary.ThumbnailUrl;
        Tags = summary.Tags;
        PublishedDate = summary.PublishedDate;
    }

    public PixivisionArticleSummary ToSummary() => new()
    {
        Id = Id,
        Title = Title,
        ThumbnailUrl = ThumbnailUrl,
        Tags = Tags.ToList(),
        PublishedDate = PublishedDate,
    };

    public async Task LoadThumbnailAsync(PixivImageLoader loader, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl)) return;
        try
        {
            var skBitmap = await loader.FetchBitmapAsync(ThumbnailUrl, ThumbnailSize.Small, ct);
            if (skBitmap is null || ct.IsCancellationRequested) return;
            var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap), ct);
            skBitmap.Dispose();
            if (bmp is not null && !ct.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(() => Thumbnail = bmp);
        }
        catch (OperationCanceledException) { }
        catch { /* non-fatal */ }
    }
}
