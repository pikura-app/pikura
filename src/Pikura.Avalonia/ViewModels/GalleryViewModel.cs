using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Core.Data;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Pikura.Avalonia.Services;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Linq;
using System.Collections.Generic;

namespace Pikura.Avalonia.ViewModels;

public enum GalleryViewMode { Grid, List }
public enum ArtworkSortMode { Default, TitleAsc, TitleDesc, NewestFirst, OldestFirst, PagesDesc }
public enum CardHeightMode { Fixed, Natural }
public enum GallerySearchScope { CurrentArtist, AllFollowedArtists }

public partial class ViewerTab : ObservableObject
{
    [ObservableProperty] private ArtworkCardViewModel? _card;
    [ObservableProperty] private string _header;

    /// <summary>The ordered list this tab navigates through (artist gallery, ranking page, etc.).</summary>
    public List<ArtworkCardViewModel> NavList { get; init; } = [];

    /// <summary>True total from the source (e.g. artist's full catalogue count), may exceed NavList.Count.</summary>
    public int TotalCount { get; set; }

    /// <summary>Optional callback to load more cards into NavList when navigating past the loaded edge.</summary>
    public Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? LoadMoreAsync { get; set; }

    /// <summary>Which section opened this tab ("Gallery", "Discover", "Rankings", etc.). Informational only — tabs are global across all sections.</summary>
    public string Source { get; set; } = "Gallery";

    /// <summary>True if this is the special collage tab (no single card, displays a grid).</summary>
    public bool IsCollage { get; set; }

    /// <summary>The artworks displayed in this tab when <see cref="IsCollage"/> is true.</summary>
    public ObservableCollection<ArtworkCardViewModel> CollageItems { get; } = [];

    public ViewerTab(ArtworkCardViewModel? card, IReadOnlyList<ArtworkCardViewModel>? navList = null,
        int totalCount = 0, Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMoreAsync = null,
        string source = "Gallery", string? header = null, bool isCollage = false)
    {
        _card = card;
        _header = header ?? (card is { Title.Length: > 0 }
            ? (card.Title.Length > 24 ? card.Title[..24] + "…" : card.Title)
            : "Untitled");
        IsCollage = isCollage;
        NavList = navList != null ? new List<ArtworkCardViewModel>(navList) : [];
        TotalCount = totalCount > 0 ? totalCount : NavList.Count;
        LoadMoreAsync = loadMoreAsync;
        Source = source;
    }

    /// <summary>Move to a different card in this tab's nav list and update the header.</summary>
    public void NavigateTo(ArtworkCardViewModel card)
    {
        Card = card;
        Header = card.Title.Length > 24 ? card.Title[..24] + "…" : card.Title;
    }
}

public partial class GalleryViewModel : ViewModelBase
{
    private readonly PixivClient _pixivClient;
    private readonly PixivImageLoader _imageLoader;
    private readonly PixivDownloadService _downloader;
    private readonly SettingsService _settingsService;
    private readonly NavigationService _navigationService;
    private readonly DialogService _dialogService;
    private readonly DownloadJobRepository _jobRepository;
    private readonly DownloadCoordinator _coordinator;
    private readonly AccountService? _accountService;
    private readonly Pikura.Core.Services.DeviceCapabilityService _deviceCapability;
    private readonly Pikura.Core.Services.FollowedArtistsIndexService? _indexService;
    private readonly Pikura.Core.Data.ViewedHistoryRepository? _historyRepository;

    private int _loadingArtistsGuard;
    private bool _suppressArtistChanged;
    private List<string> _currentArtistAllIds = [];
    private int _currentArtistLoadedCount;
    private CancellationTokenSource? _artworkLoadCts;
    private List<ArtworkCardViewModel>? _searchBackup;
    private ArtistCardViewModel? _searchPreviousArtist;
    private bool _searchWasRecentFeedActive;
    private Task _artistLoadTask = Task.CompletedTask;
    // Cache: artistUserId -> loaded card list (avoids re-fetching on back navigation).
    // Bounded to _artworkCacheCapacity most-recently-used artists — each entry holds up to
    // 96 decoded thumbnail Bitmaps, so leaving this unbounded let memory grow without limit
    // as a session visited more and more artists (e.g. browsing through Discover/Search).
    // _artworkCacheOrder tracks MRU order for eviction; evicted entries have their bitmaps
    // disposed immediately rather than waiting on the GC to reclaim native Skia memory.
    private readonly Dictionary<string, (List<ArtworkCardViewModel> Cards, List<string> AllIds, int TotalIds, int LoadedCount, bool CanMore)> _artworkCache = [];
    private readonly List<string> _artworkCacheOrder = [];
    private const int ArtworkCacheCapacity = 15;
    // In-flight artwork-ID-list fetches started eagerly by LoadArtistByIdAsync so
    // LoadArtistArtworksAsync can reuse them instead of re-issuing the same request —
    // this lets the artist-profile fetch and the artwork-ID-list fetch run in parallel
    // instead of one blocking the other, shaving a full network round trip off the
    // time-to-first-card when opening an artist's gallery from Discover/Search/etc.
    private readonly Dictionary<string, Task<UserProfileAll>> _profilePrefetch = [];
    private const int PageSize = 48;
    private const int InitialPages = 2; // load 96 works immediately

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingArtists;
    [ObservableProperty] private bool _isBulkDownloading;
    [ObservableProperty] private string _statusMessage = "Ready";
    
    // Download queue system
    // _bulkClaims tracks slots claimed by ANY in-flight bulk operation
    // (including the metadata-fetch phase before DownloadCoreAsync starts),
    // so concurrency is enforced atomically at click time.
    private int _bulkClaims;
    [ObservableProperty] private bool _isDownloadInProgress;
    [ObservableProperty] private int _queuedDownloadCount;
    public bool HasQueuedDownloads => QueuedDownloadCount > 0;
    partial void OnQueuedDownloadCountChanged(int value) => OnPropertyChanged(nameof(HasQueuedDownloads));

    /// <summary>
    /// Atomically attempts to claim a concurrent-job slot.
    /// Returns true and increments the claim count if a slot was available;
    /// false otherwise.
    /// </summary>
    private bool TryClaimBulkSlot()
    {
        var max = MaxConcurrentBulkJobs;
        while (true)
        {
            var current = Volatile.Read(ref _bulkClaims);
            if (current >= max) return false;
            if (Interlocked.CompareExchange(ref _bulkClaims, current + 1, current) == current)
            {
                if (!IsBulkDownloading) IsBulkDownloading = true;
                return true;
            }
        }
    }

    /// <summary>Releases a slot previously claimed by <see cref="TryClaimBulkSlot"/>.</summary>
    private void ReleaseBulkSlot()
    {
        var remaining = Interlocked.Decrement(ref _bulkClaims);
        if (remaining <= 0)
        {
            _bulkClaims = 0; // clamp
            IsBulkDownloading = false;
        }
        // Pump queue to fill freed slot
        if (_queueEntries.Count > 0)
        {
            PumpQueue();
        }
    }

    // Backwards-compat shims for DownloadPagesAsync (single-artwork page downloads).
    // These don't enforce slot limits — single-page downloads are quick.
    private void BeginBulkDownload()
    {
        Interlocked.Increment(ref _bulkClaims);
        if (!IsBulkDownloading) IsBulkDownloading = true;
    }

    private void EndBulkDownload() => ReleaseBulkSlot();

    private int MaxConcurrentBulkJobs
    {
        get
        {
            var n = _settingsService?.Current?.MaxConcurrentJobs ?? 1;
            return n <= 0 ? 1 : n;
        }
    }
    [ObservableProperty] private bool _showR18;
    [ObservableProperty] private ArtistCardViewModel? _selectedArtist;
    [ObservableProperty] private int _selectedCount;
    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(CanViewSelectedAsCollage));
        ViewSelectedAsCollageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanViewSelectedInNewTabs));
        ViewSelectedInNewTabsCommand.NotifyCanExecuteChanged();
    }
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private int _artworksTotal;
    [ObservableProperty] private string _artistFilter = string.Empty;
    [ObservableProperty] private GalleryViewMode _viewMode = GalleryViewMode.Grid;
    [ObservableProperty] private int _cardSize = 180;
    [ObservableProperty] private int _artistsTotal;
    [ObservableProperty] private bool _artistsLoaded;
    [ObservableProperty] private int _queuedArtistCount;
    [ObservableProperty] private ArtworkCardViewModel? _inlineViewerCard;
    [ObservableProperty] private ArtworkSortMode _sortMode = ArtworkSortMode.Default;
    [ObservableProperty] private CardHeightMode _cardHeightMode = CardHeightMode.Fixed;
    [ObservableProperty] private bool _isFixedHeight = true;
    [ObservableProperty] private bool _isNaturalHeight;
    [ObservableProperty] private string _tagIncludeFilter = string.Empty;
    [ObservableProperty] private string _tagExcludeFilter = string.Empty;
    [ObservableProperty] private DateTime? _dateFrom;
    [ObservableProperty] private DateTime? _dateTo;
    [ObservableProperty] private string _idSearchQuery = string.Empty;
    [ObservableProperty] private bool _isIdSearchMode;
    [ObservableProperty] private GallerySearchScope _searchScope = GallerySearchScope.CurrentArtist;
    [ObservableProperty] private int _searchScopeIndex;
    [ObservableProperty] private bool _isSearchActive;

    partial void OnSearchScopeChanged(GallerySearchScope value) => SearchScopeIndex = (int)value;
    partial void OnSearchScopeIndexChanged(int value) => SearchScope = (GallerySearchScope)value;
    [ObservableProperty] private bool _showFilters;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showBadges = true;
    [ObservableProperty] private bool _isRecentFeedActive;
    [ObservableProperty] private bool _showPreview;

    // ── Collage mode — a single dedicated "Collage" tab in the global tab list. This lets
    // the user switch between the collage and individual artwork tabs without destroying either.
    public const int MaxCollageItems = 10;

    private void OnCollageItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CollageItems));
        OnPropertyChanged(nameof(HasStoredCollage));
        OnPropertyChanged(nameof(CanReturnToCollage));
        OnPropertyChanged(nameof(CanViewSelectedAsCollage));
        ViewSelectedAsCollageCommand.NotifyCanExecuteChanged();
        ReturnToCollageCommand.NotifyCanExecuteChanged();
    }

    private ViewerTab GetOrCreateCollageTab()
    {
        var tab = ViewerTabs.FirstOrDefault(t => t.IsCollage);
        if (tab == null)
        {
            tab = new ViewerTab(null, [], 0, null, "Collage", header: "Collage", isCollage: true);
            tab.CollageItems.CollectionChanged += OnCollageItemsChanged;
            ViewerTabs.Add(tab);
        }
        return tab;
    }

    /// <summary>True when the currently selected tab is the collage tab.</summary>
    public bool IsCollageMode => SelectedViewerTab is { IsCollage: true };

    /// <summary>The collage items for the active collage tab (null when no collage is selected).</summary>
    public ObservableCollection<ArtworkCardViewModel>? CollageItems => SelectedViewerTab is { IsCollage: true } ? SelectedViewerTab.CollageItems : null;

    /// <summary>True when there is an existing collage tab (whether currently selected or not).</summary>
    public bool HasStoredCollage => ViewerTabs.Any(t => t.IsCollage);

    /// <summary>True when the viewer has a stored collage tab that can be switched to.</summary>
    public bool CanReturnToCollage => !IsCollageMode && HasStoredCollage;

    /// <summary>Replace the contents of the collage tab with the given items.</summary>
    public void ShowCollage(IEnumerable<ArtworkCardViewModel> items)
    {
        var tab = GetOrCreateCollageTab();
        tab.CollageItems.Clear();
        AddToCollage(items, tab);
        SelectedViewerTab = tab;
        ShowPreview = true;
    }

    /// <summary>Collage a cross-tab selection: append to existing collage if one exists,
    /// otherwise start a new one.</summary>
    public void AddSelectedToCollage(IEnumerable<ArtworkCardViewModel> selected)
    {
        var list = selected.ToList();
        if (list.Count == 0) return;
        if (HasStoredCollage || IsCollageMode)
        {
            AddToCollage(list);
            return;
        }
        ShowCollage(list.Take(MaxCollageItems));
    }

    [RelayCommand]
    public async Task DownloadCollageAsync()
    {
        if (CollageItems is not { Count: > 0 } items) return;

        var baseRoot = _accountService?.GetEffectiveDownloadRoot() ?? _settingsService.Current.DownloadRoot;
        int number;
        string folder;
        do
        {
            number = Random.Shared.Next(100000, 999999);
            folder = Path.Combine(baseRoot, $"Collage_{number}");
        } while (Directory.Exists(folder));
        Directory.CreateDirectory(folder);

        var acctOverride = BuildAccountSettingsOverride() ?? new SettingsOverride();
        acctOverride.UseGlobalSettings = false;
        acctOverride.DownloadRoot = folder;

        var folderName = Path.GetFileName(folder);
        StatusMessage = $"Downloading {items.Count} collage artworks to {folderName}…";
        await DownloadCoreAsync(items.ToList(), acctOverride, jobName: folderName);
    }

    /// <summary>Add items to the existing collage tab (creating it if necessary).</summary>
    public void AddToCollage(IEnumerable<ArtworkCardViewModel> items, ViewerTab? tab = null)
    {
        tab ??= GetOrCreateCollageTab();
        var existing = tab.CollageItems.Select(c => c.Id).ToHashSet();
        foreach (var item in items)
        {
            if (tab.CollageItems.Count >= MaxCollageItems) break;
            if (existing.Add(item.Id)) tab.CollageItems.Add(item);
        }
        if (SelectedViewerTab?.IsCollage != true)
            SelectedViewerTab = tab;
        ShowPreview = true;
    }

    /// <summary>Remove an item from the active collage tab, closing the tab if it becomes empty.</summary>
    public void RemoveFromCollage(ArtworkCardViewModel? card)
    {
        if (card == null) return;
        if (SelectedViewerTab is not { IsCollage: true } collage) return;
        collage.CollageItems.Remove(card);
        if (collage.CollageItems.Count == 0)
            CloseViewerTab(collage);
    }

    /// <summary>Close the collage tab.</summary>
    public void CloseCollage()
    {
        if (SelectedViewerTab is { IsCollage: true } collage)
            CloseViewerTab(collage);
        else if (ViewerTabs.FirstOrDefault(t => t.IsCollage) is { } c)
            CloseViewerTab(c);
    }

    [RelayCommand]
    private void ReturnToCollage()
    {
        var tab = ViewerTabs.FirstOrDefault(t => t.IsCollage);
        if (tab != null) SelectedViewerTab = tab;
    }

    [ObservableProperty] private double _browsePanelWidth = 350;
    [ObservableProperty] private bool _showSearchInfo;

    // Pagination properties
    [ObservableProperty] private bool _usePagination;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _itemsPerPage = 50;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private bool _canGoPrevious;
    [ObservableProperty] private bool _canGoNext;
    [ObservableProperty] private string _searchInfoText = string.Empty;
    [ObservableProperty] private bool _showSidebar = true;

    [RelayCommand] private void ToggleFilters() => ShowFilters = !ShowFilters;
    [RelayCommand] private void TogglePreview() => ShowPreview = !ShowPreview;
    [RelayCommand] private void ToggleSidebar() => ShowSidebar = !ShowSidebar;

    public bool IsGridView => ViewMode == GalleryViewMode.Grid;
    public bool IsListView => ViewMode == GalleryViewMode.List;
    /// <summary>True when the global viewer has any open tabs or an active card.</summary>
    public bool IsInlineViewerOpen => InlineViewerCard != null || ViewerTabs.Count > 0;
    /// <summary>True when the global tab list has any tabs.</summary>
    public bool HasTabs => ViewerTabs.Count > 0;
    public bool HasMultipleTabs => ViewerTabs.Count > 1;
    public bool HasArtworks => FilteredArtworks.Count > 0;
    /// <summary>Incremented whenever the active tab's NavList is synced after loading more artworks.</summary>
    private int _navListVersion;
    public int NavListVersion => _navListVersion;

    // R-18 button visibility - hide when R-18 is disabled
    public bool ShowR18Buttons => _settingsService.Current.R18Mode != R18Mode.Off;

    /// <summary>Access to settings service for code-behind (e.g., blur checking).</summary>
    public SettingsService SettingsService => _settingsService;
    /// <summary>Total fixed card height: image only, info is an overlay.</summary>
    public double FixedCardTotalHeight => CardSize;
    [ObservableProperty] private bool _isViewerExpanded;
    partial void OnIsViewerExpandedChanged(bool value) { OnPropertyChanged(nameof(IsViewerFullScreen)); OnPropertyChanged(nameof(ShowGridLayer)); }
    public bool IsViewerFullScreen => IsViewerExpanded;
    public bool ShowGridLayer => !IsViewerExpanded;

    /// <summary>Tracks which section last opened the inline viewer so other sections don't show stale tabs.</summary>
    public string ViewerSource { get; private set; } = string.Empty;

    private string CurrentGalleryViewerSource => SelectedArtist == null
        ? "Gallery"
        : $"Gallery:{SelectedArtist.UserId}:{ShowR18}:{TagIncludeFilter}:{TagExcludeFilter}:{DateFrom:O}:{DateTo:O}:{SortMode}";

    /// <summary>The single global tab collection — every section shows the same tabs.</summary>
    public ObservableCollection<ViewerTab> ViewerTabs { get; } = [];
    [ObservableProperty] private ViewerTab? _selectedViewerTab;

    private void OnViewerTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsInlineViewerOpen));
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(HasMultipleTabs));
        OnPropertyChanged(nameof(HasStoredCollage));
        OnPropertyChanged(nameof(CanReturnToCollage));
        OnPropertyChanged(nameof(CanViewSelectedAsCollage));
        ViewSelectedAsCollageCommand.NotifyCanExecuteChanged();
        ReturnToCollageCommand.NotifyCanExecuteChanged();
        if (!GalleryVm_HasTabs()) { IsViewerExpanded = false; }
    }
    private bool GalleryVm_HasTabs() => ViewerTabs.Count > 0;

    public ObservableCollection<ArtistCardViewModel> Artists { get; } = [];
    public ObservableCollection<ArtistCardViewModel> FilteredArtists { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> VisibleArtworks { get; } = [];
    public ObservableCollection<ArtworkCardViewModel> FilteredArtworks { get; } = [];

    private void AddArtworkCard(ArtworkCardViewModel vm, string? currentArtistId = null)
    {
        var artistId = currentArtistId ?? SelectedArtist?.UserId;
        vm.IsCurrentArtist = artistId != null && artistId == vm.UserId;
        VisibleArtworks.Add(vm);
    }
    
    partial void OnIsBulkDownloadingChanged(bool value)
    {
        // When a slot frees, kick the queue
        if (!value && _queueEntries.Count > 0)
        {
            PumpQueue();
        }
    }

    // Download queue management
    // Each queued entry pairs the actual download lambda with an optional
    // placeholder DownloadJob (status=Queued) registered in the coordinator so
    // it is visible in History → Active while waiting.
    private readonly Queue<(Func<Task> Task, Guid? PlaceholderJobId)> _queueEntries = new();
    private readonly object _queueLock = new();

    /// <summary>
    /// Pulls entries from the queue and runs each in its own claimed slot.
    /// Safe to call repeatedly; only spawns runners for available slots.
    /// </summary>
    private void PumpQueue()
    {
        while (true)
        {
            // Peek before claiming — avoid claim/release churn when queue is empty
            lock (_queueLock)
            {
                if (_queueEntries.Count == 0) return;
            }
            if (!TryClaimBulkSlot()) return;

            (Func<Task> Task, Guid? PlaceholderJobId) entry;
            lock (_queueLock)
            {
                if (_queueEntries.Count == 0)
                {
                    ReleaseBulkSlot(); // unused claim, give it back
                    return;
                }
                entry = _queueEntries.Dequeue();
                QueuedDownloadCount = _queueEntries.Count;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (entry.PlaceholderJobId is { } pid)
                    {
                        try
                        {
                            // Mark Cancelled first so the History UI removes it
                            // from Active without requiring a manual refresh.
                            var jobs = await _coordinator.GetJobsAsync();
                            var ph = jobs.FirstOrDefault(x => x.Id == pid);
                            if (ph != null)
                            {
                                ph.Status = JobStatus.Cancelled;
                                ph.CompletedAt = DateTime.UtcNow;
                                _coordinator.NotifyJobSaved(ph);
                            }
                            await _coordinator.DeleteJobAsync(pid);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Failed to delete placeholder {Id}", pid);
                        }
                    }
                    await entry.Task();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Queued download failed");
                    StatusMessage = $"Queued download failed: {ex.Message}";
                }
                finally
                {
                    ReleaseBulkSlot();
                }
            });
        }
    }

    private Task ProcessQueueAsync()
    {
        PumpQueue();
        return Task.CompletedTask;
    }

    private async Task QueueDownloadAsync(Func<Task> downloadTask, string description, IReadOnlyList<ArtworkCardViewModel>? previewCards = null)
    {
        // Snapshot the target list synchronously (cheap, just object construction),
        // then create the placeholder job WITHOUT blocking the UI thread.
        List<DownloadTarget>? targets = null;
        if (previewCards != null && previewCards.Count > 0)
        {
            targets = previewCards.Select(c => new DownloadTarget
            {
                TargetId = c.Id,
                Name = c.Title,
                ThumbnailUrl = c.ThumbnailUrl,
                UserName = c.UserName,
                UserId = c.UserId,
                Type = TargetType.Artwork,
                Status = TargetStatus.Pending
            }).ToList();
        }

        // Enqueue first so the queued entry exists even if placeholder creation is slow.
        Guid? placeholderId = null;
        lock (_queueLock)
        {
            _queueEntries.Enqueue((downloadTask, placeholderId));
            QueuedDownloadCount = _queueEntries.Count;
        }
        StatusMessage = $"Queued: {description} ({QueuedDownloadCount} waiting)";
        Logger.LogInformation("[Queue] Enqueued: {Desc}. Queue size: {Count}", description, QueuedDownloadCount);

        // Create the History placeholder asynchronously (DB write off the UI thread).
        if (targets != null)
        {
            var acctOverride = BuildAccountSettingsOverride();
            try
            {
                var job = await Task.Run(() => _coordinator.CreateJobAsync(
                    DownloadJobType.ImageId, $"(Queued) {description}", targets,
                    settingsOverride: acctOverride,
                    startImmediately: false));
                placeholderId = job.Id;
                // Attach the placeholder id to the queued entry (it may already be
                // dequeued/running; if so, this is a harmless no-op).
                lock (_queueLock)
                {
                    var items = _queueEntries.ToArray();
                    var idx = Array.FindIndex(items, e => ReferenceEquals(e.Task, downloadTask));
                    if (idx >= 0)
                    {
                        items[idx] = (downloadTask, job.Id);
                        _queueEntries.Clear();
                        foreach (var it in items) _queueEntries.Enqueue(it);
                    }
                    else
                    {
                        // Already dequeued — delete the now-orphan placeholder.
                        _ = Task.Run(async () => { try { await _coordinator.DeleteJobAsync(job.Id); } catch { } });
                        job = null!;
                    }
                }
                if (job != null) _coordinator.NotifyJobSaved(job);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not create placeholder queue job");
            }
        }

        PumpQueue();
    }

    /// <summary>
    /// Adds multiple artwork cards in a batch to reduce UI thread pressure.
    /// </summary>
    private void AddArtworkCardsBatch(IEnumerable<ArtworkCardViewModel> vms, string? currentArtistId = null)
    {
        var artistId = currentArtistId ?? SelectedArtist?.UserId;
        var batch = new List<ArtworkCardViewModel>();
        var thumbnailLoads = new List<Task>();

        foreach (var vm in vms)
        {
            vm.IsCurrentArtist = artistId != null && artistId == vm.UserId;
            batch.Add(vm);
            // Queue thumbnail loading but don't await yet
            thumbnailLoads.Add(vm.LoadThumbnailAsync(_imageLoader));
        }

        // Add all to collection at once (ObservableCollection will batch notifications)
        foreach (var vm in batch)
        {
            VisibleArtworks.Add(vm);
        }

        // Fire-and-forget thumbnail loads
        _ = Task.WhenAll(thumbnailLoads);
    }

    public GalleryViewModel(
        PixivClient pixivClient,
        PixivImageLoader imageLoader,
        PixivDownloadService downloader,
        SettingsService settingsService,
        NavigationService navigationService,
        DialogService dialogService,
        DownloadJobRepository jobRepository,
        DownloadCoordinator coordinator,
        AccountService? accountService = null,
        Pikura.Core.Services.DeviceCapabilityService? deviceCapability = null,
        Pikura.Core.Services.FollowedArtistsIndexService? indexService = null,
        Pikura.Core.Data.ViewedHistoryRepository? historyRepository = null,
        ILogger<GalleryViewModel>? logger = null) : base((ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
        _pixivClient = pixivClient;
        _imageLoader = imageLoader;
        _downloader = downloader;
        _settingsService = settingsService;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _jobRepository = jobRepository;
        _coordinator = coordinator;
        _accountService = accountService;
        _deviceCapability = deviceCapability ?? new Pikura.Core.Services.DeviceCapabilityService();
        _indexService = indexService;
        _historyRepository = historyRepository;

        // Restore persisted gallery UI state
        var s = settingsService.Current;
        _viewMode = s.GalleryViewMode == "List" ? GalleryViewMode.List : GalleryViewMode.Grid;
        _cardHeightMode = s.CardHeightMode == "Natural" ? CardHeightMode.Natural : CardHeightMode.Fixed;
        _isFixedHeight = _cardHeightMode == CardHeightMode.Fixed;
        _usePagination = s.GalleryUsePagination;
        _itemsPerPage = s.GalleryItemsPerPage;
        _isNaturalHeight = _cardHeightMode == CardHeightMode.Natural;
        _cardSize = s.CardSize;
        _sortMode = (ArtworkSortMode)Math.Clamp(s.SortModeIndex, 0, 5);
        _showTags = s.ShowTags;
        _showInfo = s.ShowInfo;
        _showBadges = s.ShowBadges;
        _showPreview = s.ShowPreview;
        _browsePanelWidth = s.BrowsePanelWidth >= 200 ? s.BrowsePanelWidth : 350;
        _showR18 = s.GalleryShowR18;

        // Clean up stale active jobs from a previous session.
        // On a fresh app launch, nothing is actually running, so any job left in
        // Queued/Pending/Running status from before is dead state we should clear.
        _ = Task.Run(async () =>
        {
            try
            {
                var jobs = await _coordinator.GetJobsAsync();
                foreach (var j in jobs)
                {
                    // Placeholder queue jobs: delete outright
                    if (j.Name != null && j.Name.StartsWith("(Queued) ")
                        && j.Status == JobStatus.Pending)
                    {
                        try { await _coordinator.DeleteJobAsync(j.Id); } catch { }
                        continue;
                    }
                    // Real jobs left in active state from a prior session — mark
                    // as Cancelled so the user can see what was interrupted but
                    // they no longer occupy concurrency slots.
                    if (j.Status is JobStatus.Pending or JobStatus.Running)
                    {
                        try
                        {
                            j.Status = JobStatus.Cancelled;
                            j.CompletedAt = DateTime.UtcNow;
                            j.ErrorMessage ??= "Interrupted by app restart";
                            await _jobRepository.SaveJobAsync(j);
                            _coordinator.NotifyJobSaved(j);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Failed to clean stale job {Id}", j.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to clean orphan jobs");
            }
        });

        // Only load on first construction - singleton means this only fires once
        _ = LoadFollowedArtistsAsync();
        VisibleArtworks.CollectionChanged += (_, __) => RebuildFilteredArtworks();
        FilteredArtworks.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(HasArtworks));
            UpdateArtworkCountStatus();
        };
        ViewerTabs.CollectionChanged += OnViewerTabsChanged;

        // Keep queued artist count in sync for Copy All button label
        QuickClipboardService.ClipboardChanged += () =>
        {
            QueuedArtistCount = QuickClipboardService.QueuedArtistCount;
        };

        // Rebuild filters when settings change (excluded tags, R18Mode, blur setting)
        _settingsService.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowR18Buttons));
            // Apply blur setting to existing R-18 cards
            ApplyBlurSetting(_settingsService.Current.BlurR18Content);
            RebuildFilteredArtworks();
            // Sync shared CardSize from settings (updated by other tabs)
            var shared = _settingsService.Current.CardSize;
            if (CardSize != shared) CardSize = shared;
        };
    }

    partial void OnViewModeChanged(GalleryViewMode value)
    {
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(IsListView));
        _settingsService.Update(s => s.GalleryViewMode = value == GalleryViewMode.List ? "List" : "Grid");
    }

    partial void OnShowTagsChanged(bool value)
        => _settingsService.Update(s => s.ShowTags = value);

    partial void OnShowInfoChanged(bool value)
        => _settingsService.Update(s => s.ShowInfo = value);

    partial void OnShowBadgesChanged(bool value)
        => _settingsService.Update(s => s.ShowBadges = value);

    partial void OnInlineViewerCardChanged(ArtworkCardViewModel? value)
    {
        OnPropertyChanged(nameof(IsInlineViewerOpen));
        if (value != null && _historyRepository != null && !_settingsService.ActiveIncognitoEnabled)
        {
            var entry = new Pikura.Core.Data.ViewedHistoryEntry
            {
                ArtworkId = value.Id,
                Title = value.Title,
                UserId = value.UserId,
                UserName = value.UserName,
                ThumbnailUrl = value.ThumbnailUrl,
                IllustType = value.IllustType,
                XRestrict = value.IsR18G ? 2 : value.IsR18 ? 1 : 0,
                PageCount = value.PageCount,
                Tags = value.Tags,
                ViewedAt = DateTime.UtcNow,
            };
            _ = _historyRepository.RecordViewAsync(entry);
        }
    }

    partial void OnSelectedViewerTabChanged(ViewerTab? value)
    {
        InlineViewerCard = value?.Card;
        // Keep InlineViewerCardList in sync so the counter works for non-tab viewers too
        InlineViewerCardList = value?.NavList.Count > 0 ? value.NavList : null;
        OnPropertyChanged(nameof(IsCollageMode));
        OnPropertyChanged(nameof(CollageItems));
        OnPropertyChanged(nameof(HasStoredCollage));
        OnPropertyChanged(nameof(CanReturnToCollage));
        OnPropertyChanged(nameof(CanViewSelectedAsCollage));
        ViewSelectedAsCollageCommand.NotifyCanExecuteChanged();
        ReturnToCollageCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowPreviewChanged(bool value)
    {
        _settingsService.Update(s => s.ShowPreview = value);
    }

    /// <summary>When true (during splitter drag), property changes won't persist to disk.</summary>
    public bool IsResizingPanel { get; set; }

    partial void OnBrowsePanelWidthChanged(double value)
    {
        if (IsResizingPanel) return;
        _settingsService.Update(s => s.BrowsePanelWidth = value);
    }

    partial void OnShowR18Changed(bool value)
    {
        _settingsService.Update(s => s.GalleryShowR18 = value);
        // Invalidate cache for current artist so R-18 works are included/excluded on next load
        if (SelectedArtist != null)
            _artworkCache.Remove(SelectedArtist.UserId);
        // Rebuild the filtered view — R-18 filter is applied in RebuildFilteredArtworks
        RebuildFilteredArtworks();
    }

    partial void OnCardHeightModeChanged(CardHeightMode value)
    {
        IsFixedHeight = value == CardHeightMode.Fixed;
        IsNaturalHeight = value == CardHeightMode.Natural;
        SetFixedHeightCommand.NotifyCanExecuteChanged();
        SetNaturalHeightCommand.NotifyCanExecuteChanged();
        _settingsService.Update(s => s.CardHeightMode = value == CardHeightMode.Natural ? "Natural" : "Fixed");
    }

    partial void OnCardSizeChanged(int value)
    {
        OnPropertyChanged(nameof(FixedCardTotalHeight));
        _settingsService.Update(s => s.CardSize = value);
    }

    partial void OnArtistFilterChanged(string value) => RebuildFilteredArtists();

    partial void OnSortModeChanged(ArtworkSortMode value)
    {
        RebuildFilteredArtworks();
        _settingsService.Update(s => s.SortModeIndex = (int)value);
    }
    partial void OnTagIncludeFilterChanged(string value) => RebuildFilteredArtworks();
    partial void OnTagExcludeFilterChanged(string value) => RebuildFilteredArtworks();
    partial void OnDateFromChanged(DateTime? value) => RebuildFilteredArtworks();
    partial void OnDateToChanged(DateTime? value) => RebuildFilteredArtworks();

    private void RebuildFilteredArtists()
    {
        var q = ArtistFilter.Trim();
        var saved = SelectedArtist;

        _suppressArtistChanged = true;
        try
        {
            // Incremental diff instead of Clear()+re-Add() — this is called once per incoming
            // page batch while ~1600 followed artists paginate in (17+ times per load), and a
            // blunt Clear() briefly empties the sidebar ListBox on every single call, causing
            // a visible flicker/scroll-reset each time instead of just growing smoothly.
            List<ArtistCardViewModel> desired = string.IsNullOrEmpty(q)
                ? Artists.ToList()
                : Artists.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                      || a.UserId.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            SyncArtistCollection(FilteredArtists, desired);

            // Restore selection without re-triggering artwork load
            if (saved != null && FilteredArtists.Contains(saved))
                SelectedArtist = saved;
        }
        finally
        {
            _suppressArtistChanged = false;
        }
    }

    /// <summary>Updates <paramref name="dst"/> in place to match <paramref name="desired"/>
    /// (same items, same order) without ever fully clearing it — see the identical helper in
    /// BookmarksViewModel for the full rationale.</summary>
    private static void SyncArtistCollection(ObservableCollection<ArtistCardViewModel> dst, List<ArtistCardViewModel> desired)
    {
        var desiredSet = new HashSet<ArtistCardViewModel>(desired);
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

    public void RebuildFilteredArtworks()
    {
        var inc = TagIncludeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exc = TagExcludeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<ArtworkCardViewModel> src = VisibleArtworks;

        // R-18 filter logic based on global R18Mode and view toggle
        var r18Mode = _settingsService.Current.R18Mode;
        var r18Type = _settingsService.Current.R18Type;

        // R-18 filtering logic:
        // - R18Mode.Off: Always hide R-18 content regardless of toggle
        // - R18Mode.Show: Show all content (no filtering)
        // - R18Mode.Only: Show ONLY R-18 content when toggle is ON
        // - ShowR18 toggle: When OFF in Show mode, hide R-18; When ON in Show mode, show all
        if (r18Mode == R18Mode.Off)
        {
            // Always hide R-18 content in Off mode
            src = src.Where(a => !a.IsR18);
        }
        else if (r18Mode == R18Mode.Only && ShowR18)
        {
            // Only mode + toggle ON: Show ONLY R-18 content (filtered by R18Type)
            src = r18Type switch
            {
                R18TypeFilter.Both => src.Where(a => a.IsR18),
                R18TypeFilter.R18 => src.Where(a => a.IsR18 && !a.IsR18G),
                R18TypeFilter.R18G => src.Where(a => a.IsR18G),
                _ => src.Where(a => a.IsR18)
            };
        }
        else if (r18Mode == R18Mode.Show && !ShowR18)
        {
            // Show mode but toggle OFF: Hide R-18 content (show only safe)
            src = src.Where(a => !a.IsR18);
        }
        // R18Mode.Show with toggle ON shows all content (no filtering needed)
        
        // AI-generated content filtering
        if (_settingsService.Current.FilterAiGenerated)
        {
            src = src.Where(a => !a.IsAi);
        }
        
        if (inc.Length > 0)
            src = src.Where(a => inc.All(t => a.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase))));
        // Local exclude filter
        if (exc.Length > 0)
            src = src.Where(a => !exc.Any(t => a.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase))));
        // Unified blocklist filter for the Gallery tab
        src = src.Where(a => !_settingsService.Current.IsArtworkHidden("Gallery", a.UserId, a.UserName, a.Title, a.Tags));
        if (DateFrom.HasValue)
            src = src.Where(a => a.DateCreated >= DateFrom.Value);
        if (DateTo.HasValue)
            src = src.Where(a => a.DateCreated <= DateTo.Value.AddDays(1));
        src = SortMode switch
        {
            ArtworkSortMode.TitleAsc      => src.OrderBy(a => a.Title),
            ArtworkSortMode.TitleDesc     => src.OrderByDescending(a => a.Title),
            ArtworkSortMode.NewestFirst   => src.OrderByDescending(a => a.DateCreated),
            ArtworkSortMode.OldestFirst   => src.OrderBy(a => a.DateCreated),
            ArtworkSortMode.PagesDesc     => src.OrderByDescending(a => a.PageCount),
            _                             => src,
        };

        // Apply pagination if enabled
        if (UsePagination)
        {
            var totalItems = src.Count();
            TotalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)ItemsPerPage));
            CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);
            CanGoPrevious = CurrentPage > 1;
            CanGoNext = CurrentPage < TotalPages;

            src = src.Skip((CurrentPage - 1) * ItemsPerPage).Take(ItemsPerPage);
        }

        FilteredArtworks.Clear();
        foreach (var a in src) FilteredArtworks.Add(a);
    }

    [RelayCommand] private void SetFixedHeight() => CardHeightMode = CardHeightMode.Fixed;
    [RelayCommand] private void SetNaturalHeight() => CardHeightMode = CardHeightMode.Natural;

    [RelayCommand]
    public async Task SearchByTagAsync(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;

        // Cancel any in-progress artist/tag load, then take ownership of the CTS
        // so that a concurrent LoadArtistArtworksAsync can't cancel our search request.
        _artworkLoadCts?.Cancel();
        _artworkLoadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _artworkLoadCts = cts;

        // Search is restricted to followed artists via the local index — the
        // dedicated "Search" tab (GlobalSearchViewModel) covers global Pixiv search.
        StatusMessage = $"Searching your followed artists for: {tag}...";
        IsLoading = true;
        IsRecentFeedActive = false;

        try
        {
            if (_indexService is null)
            {
                StatusMessage = "Search index unavailable.";
                return;
            }

            // Remember what the user was viewing so clearing the search bar can restore it.
            BackupSearchState();

            var incTags = TagIncludeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var excTags = TagExcludeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var (rows, total) = await _indexService.SearchAsync(tag, incTags, excTags, 0, 96, cts.Token);

            // Clear current view
            VisibleArtworks.Clear();
            SelectedArtist = null; // No specific artist for tag search
            _currentArtistAllIds = [];
            _currentArtistLoadedCount = 0;
            CanLoadMore = false;
            ArtworksTotal = total;

            if (rows.Count == 0)
            {
                ShowSearchInfo = true;
                SearchInfoText = $"Tag: {tag} (followed artists) • 0 results";
                StatusMessage = $"No results among your followed artists for: {tag}";
                IsSearchActive = true;
                return;
            }

            // Prepare batch of artwork cards
            var batch = new List<ArtworkCardViewModel>();
            foreach (var row in rows)
            {
                if (!ShowR18 && row.XRestrict >= 1) continue;
                var preview = IndexedArtworkToPreview(row);
                batch.Add(new ArtworkCardViewModel(preview)
                {
                    IsFollowed = true, // index only contains followed artists
                    IsBlurred = _settingsService.Current.BlurR18Content && row.XRestrict >= 1
                });
            }

            // Add batch to UI
            AddArtworkCardsBatch(batch);

            // Show info bar with search context
            ShowSearchInfo = true;
            SearchInfoText = $"Tag: {tag} (followed artists) • {VisibleArtworks.Count} results";
            IsSearchActive = true;

            StatusMessage = $"Found {VisibleArtworks.Count} artworks among your followed artists for: {tag}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static ArtworkPreview IndexedArtworkToPreview(Pikura.Core.Data.IndexedArtwork row) => new()
    {
        Id = row.ArtworkId,
        Title = row.Title,
        UserId = row.ArtistUserId,
        UserName = row.ArtistUserName,
        ThumbnailUrl = row.ThumbnailUrl,
        IllustType = row.IllustType,
        XRestrict = row.XRestrict,
        AiType = row.AiType,
        PageCount = row.PageCount,
        Width = row.Width,
        Height = row.Height,
        Tags = row.Tags,
        CreateDate = row.CreateDate,
    };

    [RelayCommand]
    private async Task SearchByIdAsync()
    {
        var raw = IdSearchQuery.Trim();

        // Clearing the search bar returns the user to the gallery they were viewing.
        if (string.IsNullOrEmpty(raw))
        {
            await ClearSearchAndReturnAsync();
            return;
        }

        StatusMessage = $"Searching for '{raw}'…";
        IsLoading = true;

        try
        {
            // u: prefix = artist ID
            if (raw.StartsWith("u:", StringComparison.OrdinalIgnoreCase))
            {
                var userId = raw[2..].Trim();
                await LoadArtistByIdAsync(userId);
            }
            // a: prefix = artist name search
            else if (raw.StartsWith("a:", StringComparison.OrdinalIgnoreCase))
            {
                var artistName = raw[2..].Trim();
                await SearchArtistByNameAsync(artistName);
            }
            // all digits = artwork ID
            else if (raw.All(char.IsDigit))
            {
                await LoadArtworkByIdAsync(raw);
            }
            // otherwise = tag/title search
            else
            {
                if (SearchScope == GallerySearchScope.CurrentArtist)
                    await SearchCurrentArtistAsync(raw);
                else
                    await SearchByTagAsync(raw);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Search failed: " + ex.Message;
        }
        finally { IsLoading = false; }
    }

    /// <summary>Remembers the current artwork list and context so a later clear can restore it.</summary>
    private void BackupSearchState()
    {
        if (_searchBackup != null) return;
        _searchBackup = VisibleArtworks.ToList();
        _searchPreviousArtist = SelectedArtist;
        _searchWasRecentFeedActive = IsRecentFeedActive;
    }

    /// <summary>Performs a local keyword search over the currently displayed artworks.</summary>
    private async Task SearchCurrentArtistAsync(string query)
    {
        IsRecentFeedActive = false;
        CanLoadMore = false;
        BackupSearchState();

        var q = query;
        var filtered = _searchBackup!.Where(a =>
            a.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            a.UserName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            a.Tags.Any(tag => tag.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        VisibleArtworks.Clear();
        AddArtworkCardsBatch(filtered);

        ShowSearchInfo = true;
        SearchInfoText = $"'{query}' in current artist • {VisibleArtworks.Count} results";
        IsSearchActive = true;
        StatusMessage = $"Found {VisibleArtworks.Count} results for '{query}' in current artist";

        await Task.CompletedTask;
    }

    /// <summary>Clears an active search and restores the previous gallery view.</summary>
    [RelayCommand]
    private async Task ClearSearchAndReturnAsync()
    {
        if (_searchBackup != null)
        {
            VisibleArtworks.Clear();
            AddArtworkCardsBatch(_searchBackup);
            _searchBackup = null;
        }
        else if (SelectedArtist != null)
        {
            await LoadArtistArtworksAsync(SelectedArtist);
        }
        else if (IsRecentFeedActive)
        {
            await LoadRecentWorksAsync();
        }
        else
        {
            VisibleArtworks.Clear();
        }

        // Restore the artist that was active before the search.
        if (_searchPreviousArtist != null)
        {
            _suppressArtistChanged = true;
            try { SelectedArtist = _searchPreviousArtist; }
            finally { _suppressArtistChanged = false; }
        }

        IsRecentFeedActive = _searchWasRecentFeedActive;
        IsSearchActive = false;
        ShowSearchInfo = false;
        IdSearchQuery = string.Empty;
        StatusMessage = "Search cleared";
    }

    public async Task SearchArtistByNameAsync(string artistName)
    {
        StatusMessage = $"Searching for artist: {artistName}...";

        try
        {
            var users = await _pixivClient.SearchArtistsAsync(artistName, ct: _artworkLoadCts?.Token ?? CancellationToken.None);

            if (users is null || users.Count == 0)
            {
                StatusMessage = $"No artists found for: {artistName}";
                return;
            }

            // Just select the first result as a transient artist (don't add to followed list)
            var firstUser = users[0];
            var existing = Artists.FirstOrDefault(a => a.UserId == firstUser.UserId);
            if (existing != null)
            {
                SelectedArtist = existing;
            }
            else
            {
                var transient = new ArtistCardViewModel(new FollowedArtist
                {
                    UserId = firstUser.UserId,
                    UserName = firstUser.UserName,
                    ProfileImageUrl = firstUser.ProfileImageUrl,
                    Following = false
                });
                SelectedArtist = transient;
                ShowSearchInfo = true;
                SearchInfoText = $"Viewing: {transient.Name} (not followed) • {users.Count} matches for '{artistName}'";
            }

            StatusMessage = $"Found {users.Count} artists matching '{artistName}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Artist search failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task LoadArtistByIdAsync(string userId)
    {
        IsIdSearchMode = false;
        IsRecentFeedActive = false;
        // Kick off the artwork-ID-list fetch immediately, in parallel with whatever else
        // this method does before the gallery load actually starts (checking the followed
        // list, and — for unfollowed artists — fetching the artist profile below). Without
        // this, those steps ran strictly before LoadArtistArtworksAsync's own fetch of the
        // same data, adding a full extra network round trip to the time-to-first-card.
        if (!_profilePrefetch.ContainsKey(userId))
            _profilePrefetch[userId] = _pixivClient.GetUserProfileAllAsync(userId);

        // First, check if already in followed artists list — if so, just select
        var existing = Artists.FirstOrDefault(a => a.UserId == userId);
        if (existing != null)
        {
            if (SelectedArtist?.UserId != existing.UserId)
                SelectedArtist = existing;
            else
                _artistLoadTask = LoadArtistArtworksAsync(existing);
            await _artistLoadTask;
            return;
        }

        // Not in followed list: load as transient artist (don't add to Artists list)
        var info = await _pixivClient.GetArtistAsync(userId);
        if (info == null) { StatusMessage = $"Artist {userId} not found."; return; }
        var transient = new ArtistCardViewModel(new FollowedArtist
        {
            UserId = info.UserId,
            UserName = info.Name,
            ProfileImageUrl = info.ImageUrl,
            Following = info.IsFollowed
        });
        // Setting SelectedArtist triggers OnSelectedArtistChanged which loads artworks
        // (which in turn updates StatusMessage to "Name — X / Y works" when complete).
        ShowSearchInfo = true;
        SearchInfoText = $"Viewing: {transient.Name} (not followed)";
        SelectedArtist = transient;
        await _artistLoadTask;
    }

    [RelayCommand]
    public async Task LoadArtworkByIdAsync(string artworkId)
    {
        var card = await BuildCardForArtworkAsync(artworkId);
        if (card == null) return;

        OpenInlineViewer(card);
        // This command is the entry point for "show this specific artwork" requests that can
        // originate from anywhere (Hoshi chat's "Open" button, an AI-recommended result, etc.),
        // including tabs other than Gallery — those callers first switch MainContentControl to
        // the Gallery view, then call this. But GalleryView's side viewer visibility is gated by
        // ShowPreview, which was never flipped on here, so the artwork loaded into
        // InlineViewerCard with nothing on screen to display it if the panel wasn't already
        // open (e.g. arriving fresh from Discover/Bookmarks/Rankings).
        ShowPreview = true;
        StatusMessage = $"Viewing artwork {artworkId}";
    }

    /// <summary>
    /// Fetches an artwork by ID and opens it in a new viewer tab. Used by Hoshi chat
    /// quick actions so each artwork opens in its own tab rather than replacing the
    /// current one.
    /// </summary>
    public async Task OpenArtworkByIdInNewTabAsync(string artworkId)
    {
        var card = await BuildCardForArtworkAsync(artworkId);
        if (card == null) return;

        var list = new List<ArtworkCardViewModel> { card };
        OpenInNewTab(card, list, list.Count, null, "Hoshi");
        ShowPreview = true;
        StatusMessage = $"Opened artwork {artworkId} in a new tab";
    }

    private async Task<ArtworkCardViewModel?> BuildCardForArtworkAsync(string artworkId)
    {
        var b = await _pixivClient.GetArtworkDetailAsync(artworkId);
        if (b == null) { StatusMessage = $"Artwork {artworkId} not found."; return null; }
        var preview = new ArtworkPreview
        {
            Id = b.IllustId ?? artworkId,
            Title = b.IllustTitle ?? artworkId,
            UserName = b.UserName ?? string.Empty,
            UserId = b.UserId ?? string.Empty,
            ThumbnailUrl = b.ThumbnailUrl,
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
        };
        var vm = new ArtworkCardViewModel(preview) { IsFollowed = IsArtistFollowed(preview.UserId) };
        _ = vm.LoadThumbnailAsync(_imageLoader);
        return vm;
    }

    /// <summary>
    /// Applies or removes blur from all R-18 cards based on the BlurR18Content setting.
    /// Called when the setting changes.
    /// </summary>
    public void ApplyBlurSetting(bool shouldBlur)
    {
        foreach (var card in VisibleArtworks)
        {
            if (card.IsR18)
            {
                card.IsBlurred = shouldBlur;
            }
        }
    }

    partial void OnUsePaginationChanged(bool value)
    {
        CurrentPage = 1; // Reset to first page when toggling
        _settingsService.Update(s => s.GalleryUsePagination = value);
        UpdatePagination();
    }

    partial void OnItemsPerPageChanged(int value)
    {
        _settingsService.Update(s => s.GalleryItemsPerPage = value);
        if (UsePagination)
        {
            CurrentPage = 1; // Reset to first page when changing items per page
            UpdatePagination();
        }
    }

    [RelayCommand] private void SetGridView() => ViewMode = GalleryViewMode.Grid;
    [RelayCommand] private void SetListView() => ViewMode = GalleryViewMode.List;

    // Pagination commands
    [RelayCommand] private void TogglePagination() => UsePagination = !UsePagination;

    [RelayCommand]
    private void FirstPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage = 1;
            UpdatePagination();
        }
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            UpdatePagination();
        }
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            UpdatePagination();
        }
    }

    [RelayCommand]
    private void LastPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage = TotalPages;
            UpdatePagination();
        }
    }

    [RelayCommand]
    private void GoToPage(int page)
    {
        if (page >= 1 && page <= TotalPages)
        {
            CurrentPage = page;
            UpdatePagination();
        }
    }

    [RelayCommand]
    private void SetItemsPerPage(int count)
    {
        ItemsPerPage = count;
        CurrentPage = 1; // Reset to first page
        UpdatePagination();
    }

    partial void OnCurrentPageChanged(int value)
    {
        // Validate page bounds and update pagination when user enters a page number
        if (UsePagination)
        {
            var clampedValue = Math.Clamp(value, 1, Math.Max(1, TotalPages));
            if (clampedValue != value)
            {
                CurrentPage = clampedValue; // Fix out-of-bounds
                return; // OnPropertyChanged will trigger again
            }
            UpdatePagination();
        }
    }

    private void UpdatePagination()
    {
        if (!UsePagination)
        {
            // Show all artworks when pagination is off
            RebuildFilteredArtworks();
            return;
        }

        // Calculate total pages
        var totalItems = VisibleArtworks.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)ItemsPerPage));

        // Ensure current page is valid
        CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);

        // Update navigation buttons
        CanGoPrevious = CurrentPage > 1;
        CanGoNext = CurrentPage < TotalPages;

        RebuildFilteredArtworks();
    }

    // Items per page options
    public int[] ItemsPerPageOptions { get; } = { 10, 20, 50, 100 };

    /// <summary>
    /// Loads followed artists from Pixiv. Only adds new artists not already in the list.
    /// </summary>
    [RelayCommand]
    private async Task LoadFollowedArtistsAsync()
    {
        if (Interlocked.Exchange(ref _loadingArtistsGuard, 1) == 1) return;

        var isInitialLoad = Artists.Count == 0;
        IsLoadingArtists = true;
        if (isInitialLoad)
            StatusMessage = "Loading followed artists…";

        try
        {
            if (string.IsNullOrWhiteSpace(_settingsService.Current.UserId))
            {
                if (isInitialLoad) StatusMessage = "Validating session…";
                await _pixivClient.ValidateSessionAsync();
            }

            var userId = _settingsService.Current.UserId;
            if (string.IsNullOrWhiteSpace(userId))
            {
                StatusMessage = "Sign in to see followed artists.";
                return;
            }

            if (isInitialLoad) ArtistsLoaded = false;
            const int limit = 96; // Larger page size to reduce API calls
            var seenLock = new object();
            // Seed 'seen' inside the lock so the initial read of Artists is
            // consistent with the locked writes done by parallel page tasks.
            var seen = new HashSet<string>();
            lock (seenLock)
            {
                foreach (var a in Artists)
                    seen.Add(a.UserId);
            }
            var realTotal = 0;

            // Step 1: Fetch first page of public AND private in parallel to get totals
            var firstPagesTask = Task.WhenAll(
                _pixivClient.GetFollowedArtistsAsync(userId, 0, limit, hidden: false),
                _pixivClient.GetFollowedArtistsAsync(userId, 0, limit, hidden: true));
            var firstPages = await firstPagesTask;

            // Add first page results immediately so UI shows something
            foreach (var page in firstPages)
            {
                if (page?.Users == null) continue;
                Logger.LogInformation("[FollowedArtists] First page: Total={Total} Users={Count} hidden={Hidden}",
                    page.Total, page.Users.Count, page == firstPages[1]);
                if (page.Total > 0) realTotal += page.Total;
                var batch = page.Users
                    .Where(u => { lock (seenLock) return seen.Add(u.UserId); })
                    .Select(u => new ArtistCardViewModel(u))
                    .ToList();
                if (batch.Count > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var vm in batch)
                        {
                            Artists.Add(vm);
                            _ = vm.LoadAvatarAsync(_imageLoader);
                        }
                        ArtistsTotal = Artists.Count;
                        RebuildFilteredArtists();
                    });
                }
            }
            Logger.LogInformation("[FollowedArtists] After first pages: realTotal={RealTotal} seen={Seen}", realTotal, seen.Count);

            // Step 2: Fetch remaining pages in the background so startup isn't gated on all pages.
            ArtistsLoaded = true;
            if (!IsLoading)
                StatusMessage = $"{Artists.Count} followed artists (loading more…)";
            RebuildFilteredArtists();

            _ = Task.Run(async () =>
            {
                try
                {
                    await FetchRemainingFollowedPagesAsync(userId, firstPages, limit, seen, seenLock);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ArtistsTotal = Artists.Count;
                        Logger.LogInformation("[FollowedArtists] Load complete: Artists.Count={Count} PixivTotalEstimate={Estimated}", Artists.Count, realTotal);
                        if (!IsLoading)
                            StatusMessage = $"{Artists.Count} followed artists";
                        RebuildFilteredArtists();
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to load remaining followed artists");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load followed artists");
            StatusMessage = "Failed to load followed artists: " + ex.Message;
        }
        finally
        {
            IsLoadingArtists = false;
            Interlocked.Exchange(ref _loadingArtistsGuard, 0);
        }
    }

    /// <summary>
    /// Clears all per-account state and reloads followed artists for the newly active account.
    /// Call this after SwitchTo() or after a new login.
    /// </summary>
    public async Task SwitchAccountAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _artworkCache.Clear();
            _artworkCacheOrder.Clear();
            _currentArtistAllIds.Clear();
            _currentArtistLoadedCount = 0;
            SelectedArtist = null;
            VisibleArtworks.Clear();
            FilteredArtworks.Clear();
            ViewerTabs.Clear();
            InlineViewerCard = null;
        });

        // Reset Discover and Bookmarks so they reload for the new account
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var discover = AppServices.Get<DiscoverViewModel>();
                discover.RecommendedWorks.Clear();
                discover.RecommendedUsers.Clear();
                discover.FilteredWorks.Clear();

                var bookmarks = AppServices.Get<BookmarksViewModel>();
                bookmarks.PublicBookmarks.Clear();
                bookmarks.PrivateBookmarks.Clear();
                bookmarks.FilteredPublic.Clear();
                bookmarks.FilteredPrivate.Clear();
                bookmarks.ReloadLocalFavoritesPublic();
            });
        }
        catch { /* non-fatal */ }

        await RefreshFollowedArtistsAsync();
    }

    /// <summary>
    /// Refreshes the followed artists list by clearing local cache and fetching fresh data from Pixiv.
    /// </summary>
    [RelayCommand]
    private async Task RefreshFollowedArtistsAsync()
    {
        if (Interlocked.Exchange(ref _loadingArtistsGuard, 1) == 1) return;

        IsLoadingArtists = true;
        StatusMessage = "Refreshing followed artists…";

        // Preserve the currently-selected artist so clearing the list (which
        // resets the bound ListBox selection to null) doesn't drop it — otherwise
        // a refresh while viewing a gallery makes Download All report
        // "select an artist first".
        var prevSelected = SelectedArtist;
        var prevSelectedId = prevSelected?.UserId;

        try
        {
            if (string.IsNullOrWhiteSpace(_settingsService.Current.UserId))
            {
                StatusMessage = "Validating session…";
                await _pixivClient.ValidateSessionAsync();
            }

            // Clear existing list first so stale entries from a previous
            // account don't remain visible (especially after sign-out).
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Artists.Clear();
                FilteredArtists.Clear();
                ArtistsLoaded = false;
            });

            var userId = _settingsService.Current.UserId;
            if (string.IsNullOrWhiteSpace(userId))
            {
                StatusMessage = "Sign in to see followed artists.";
                return;
            }
            
            var seen = new HashSet<string>();
            var seenLock = new object();
            const int limit = 96;
            var realTotal = 0;

            // Parallel: load first page of public + private to get totals
            var firstPages = await Task.WhenAll(
                _pixivClient.GetFollowedArtistsAsync(userId, 0, limit, hidden: false),
                _pixivClient.GetFollowedArtistsAsync(userId, 0, limit, hidden: true));

            foreach (var page in firstPages)
            {
                if (page?.Users == null) continue;
                if (page.Total > 0) realTotal += page.Total;
                var batch = page.Users
                    .Where(u => { lock (seenLock) return seen.Add(u.UserId); })
                    .Select(u => new ArtistCardViewModel(u))
                    .ToList();
                if (batch.Count > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var vm in batch)
                        {
                            Artists.Add(vm);
                            _ = vm.LoadAvatarAsync(_imageLoader);
                        }
                        RebuildFilteredArtists();
                    });
                }
            }

            // Load remaining pages (uses the same paginate-or-discover helper as initial load).
            await FetchRemainingFollowedPagesAsync(userId, firstPages, limit, seen, seenLock);

            ArtistsTotal = Artists.Count;
            ArtistsLoaded = true;
            if (!IsLoading)
                StatusMessage = $"{Artists.Count} followed artists (refreshed)";
            RebuildFilteredArtists();

            // Restore the previously-selected artist (matched by id to the freshly
            // created instance, falling back to the original for transient artists)
            // without re-triggering a gallery reload — the gallery is already showing it.
            if (prevSelectedId != null)
            {
                var restore = Artists.FirstOrDefault(a => a.UserId == prevSelectedId) ?? prevSelected;
                if (restore != null && !ReferenceEquals(restore, SelectedArtist))
                {
                    _suppressArtistChanged = true;
                    try { SelectedArtist = restore; }
                    finally { _suppressArtistChanged = false; }
                    foreach (var card in VisibleArtworks)
                        card.IsCurrentArtist = card.UserId == restore.UserId;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to refresh followed artists");
            StatusMessage = "Failed to refresh: " + ex.Message;
        }
        finally
        {
            IsLoadingArtists = false;
            Interlocked.Exchange(ref _loadingArtistsGuard, 0);
        }
    }

    /// <summary>
    /// Fetches pages 2..N of the followed-artists list (one branch per public/private)
    /// and streams each batch onto <see cref="Artists"/> as it arrives.
    ///
    /// Two pagination strategies:
    /// <list type="bullet">
    ///   <item><b>Total known</b> (<c>first.Total &gt; 0</c>) — issue all remaining
    ///   pages in parallel, capped at a safety bound of 5000.</item>
    ///   <item><b>Total missing/zero</b> — fall back to sequential discovery,
    ///   walking <c>offset += limit</c> until a short or empty page arrives.
    ///   This guards against the regression in issue #18 where Pixiv occasionally
    ///   omits <c>total</c> for the <c>/following</c> endpoint, which used to
    ///   cap us at exactly the first page (e.g. 48 of 225 loaded).</item>
    /// </list>
    /// </summary>
    private async Task FetchRemainingFollowedPagesAsync(
        string userId,
        FollowingResponseBody[] firstPages,
        int limit,
        HashSet<string> seen,
        object seenLock)
    {
        async Task AddBatchAsync(IReadOnlyList<FollowedArtist> users)
        {
            var batch = users
                .Where(u => { lock (seenLock) return seen.Add(u.UserId); })
                .Select(u => new ArtistCardViewModel(u))
                .ToList();
            if (batch.Count == 0) return;
            
            // Batch add to UI to reduce thread switches
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Add all items first, then rebuild filter once
                foreach (var vm in batch)
                {
                    Artists.Add(vm);
                }
                ArtistsTotal = Artists.Count;
                RebuildFilteredArtists();
                
                // Start avatar loading after UI update
                foreach (var vm in batch)
                {
                    _ = vm.LoadAvatarAsync(_imageLoader);
                }
            });
        }

        var tasks = new List<Task>();
        // Shared across both the public and hidden branches below — throttles how many page
        // requests run at once, scaled to the machine's capability instead of firing every
        // page unconditionally (previously up to ~50 concurrent requests for a large follow list).
        using var pageGate = new SemaphoreSlim(_deviceCapability.MaxParallelPageFetches, _deviceCapability.MaxParallelPageFetches);
        for (int idx = 0; idx < firstPages.Length; idx++)
        {
            var first = firstPages[idx];
            var hidden = idx == 1;
            Logger.LogInformation("[FollowedArtists] FetchRemaining: hidden={Hidden} first.Total={Total} first.Users.Count={Count} limit={Limit}",
                hidden, first?.Total ?? -1, first?.Users?.Count ?? -1, limit);
            // A short first page (or no page at all) means we already have everything —
            // but only when Total confirms it. If Total says there are more users than
            // what the first page returned, we must still paginate (the API can return
            // a slightly-short first page even when more pages exist, e.g. 47 of 232).
            if (first?.Users == null || first.Users.Count == 0)
            {
                Logger.LogInformation("[FollowedArtists] Skipping remaining pages for hidden={Hidden} (empty first page)", hidden);
                continue;
            }
            if (first.Users.Count < limit && (first.Total <= 0 || first.Total <= first.Users.Count))
            {
                Logger.LogInformation("[FollowedArtists] Skipping remaining pages for hidden={Hidden} (short/empty first page)", hidden);
                continue;
            }

            {
                var hiddenCapture = hidden;
                if (first.Total > 0)
                {
                    // Total is known — issue all remaining pages in parallel for maximum speed.
                    // The shared 'seen' set handles any duplicates from list drift.
                    var totalBound = Math.Min(first.Total, 5000);
                    var offsets = Enumerable.Range(1, (int)Math.Ceiling((totalBound - limit) / (double)limit) + 1)
                        .Select(i => i * limit)
                        .Where(o => o < totalBound + limit)
                        .ToList();
                    Logger.LogInformation("[FollowedArtists] Parallel fetch hidden={Hidden}: {PageCount} pages for total={Total} (max {MaxParallel} at once)",
                        hiddenCapture, offsets.Count, totalBound, _deviceCapability.MaxParallelPageFetches);
                    tasks.Add(Task.WhenAll(offsets.Select(offset => Task.Run(async () =>
                    {
                        await pageGate.WaitAsync();
                        try
                        {
                            // Retry once on transient failure (e.g. rate-limit) so a single
                            // flaky request doesn't silently drop a whole page of artists.
                            FollowingResponseBody? page = null;
                            for (int attempt = 0; attempt < 2; attempt++)
                            {
                                try
                                {
                                    page = await _pixivClient.GetFollowedArtistsAsync(userId, offset, limit, hiddenCapture);
                                    break;
                                }
                                catch (Exception ex) when (attempt == 0)
                                {
                                    Logger.LogWarning(ex, "Followed-artists fetch failed at offset {Off} (retrying)", offset);
                                    await Task.Delay(500);
                                }
                            }
                            if (page?.Users?.Count > 0)
                                await AddBatchAsync(page.Users);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Followed-artists fetch failed at offset {Off} (giving up)", offset);
                        }
                        finally
                        {
                            pageGate.Release();
                        }
                    }))));
                }
                else
                {
                    // Total unknown — sequential discovery walk until empty page.
                    tasks.Add(Task.Run(async () =>
                    {
                        int offset = limit;
                        int consecutiveEmpty = 0;
                        while (offset < 5000)
                        {
                            FollowingResponseBody? page;
                            try { page = await _pixivClient.GetFollowedArtistsAsync(userId, offset, limit, hiddenCapture); }
                            catch (Exception ex) { Logger.LogWarning(ex, "Followed-artists fetch failed at offset {Off}", offset); break; }
                            if (page?.Users == null || page.Users.Count == 0)
                            {
                                if (++consecutiveEmpty >= 2) break;
                            }
                            else
                            {
                                consecutiveEmpty = 0;
                                await AddBatchAsync(page.Users);
                            }
                            offset += limit;
                        }
                    }));
                }
            }
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Opens the Discover page to show recommended users to follow.
    /// </summary>
    [RelayCommand]
    private async Task DiscoverArtistsAsync()
    {
        // Navigate to Discover page and select the Users tab
        try
        {
            var vm = AppServices.Get<Pikura.Avalonia.ViewModels.DiscoverViewModel>();
            vm.SelectedTabIndex = 1; // Users tab
            await vm.LoadRecommendedUsersAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to open Discover page");
            StatusMessage = "Discover feature coming soon!";
        }
    }

    [RelayCommand]
    private async Task LoadRecentWorksAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        IsRecentFeedActive = true;
        SelectedArtist = null;
        VisibleArtworks.Clear();
        StatusMessage = "Loading recent works from followed artists…";
        try
        {
            var allIds = new List<ArtworkPreview>();
            for (int p = 1; p <= 3; p++)
            {
                // Always fetch all content and filter client-side (API only supports mode=all or mode=r18)
                var feed = await _pixivClient.GetNewWorksFromFollowedAsync(p, r18Only: false);
                if (feed.Thumbnails.Illusts.Count == 0) break;
                allIds.AddRange(feed.Thumbnails.Illusts);
            }
            foreach (var preview in allIds)
            {
                if (!ShowR18 && preview.IsR18) continue;
                var vm = new ArtworkCardViewModel(preview)
                {
                    IsFollowed = IsArtistFollowed(preview.UserId),
                    IsBlurred = _settingsService.Current.BlurR18Content && preview.IsR18
                };
                AddArtworkCard(vm);
                _ = vm.LoadThumbnailAsync(_imageLoader);
            }
            StatusMessage = $"{VisibleArtworks.Count} recent works loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to load recent works: " + ex.Message;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task LoadMoreArtworksAsync()
    {
        if (SelectedArtist == null || !CanLoadMore) return;
        var ct = _artworkLoadCts?.Token ?? CancellationToken.None;
        await LoadArtworkPageAsync(SelectedArtist, append: true, ct);
        UpdateCache(SelectedArtist);
        SyncViewerTabNavList();
    }

    [RelayCommand]
    private async Task LoadAllArtworksAsync()
    {
        if (SelectedArtist == null || _currentArtistAllIds.Count == 0) return;
        var ct = _artworkLoadCts?.Token ?? CancellationToken.None;
        while (CanLoadMore && !ct.IsCancellationRequested)
        {
            if (IsLoading) { await Task.Delay(100); continue; }
            await LoadArtworkPageAsync(SelectedArtist, append: true, ct);
        }
        UpdateCache(SelectedArtist);
        SyncViewerTabNavList();
    }

    private int _autoLoadGuard;
    /// <summary>Called by the view's scroll handler when user approaches the bottom.</summary>
    public async Task TriggerAutoLoadAsync()
    {
        if (SelectedArtist == null || !CanLoadMore || IsLoading || IsBulkDownloading) return;
        // Prevent re-entrance: scroll events can fire faster than the network load completes,
        // and concurrent LoadArtworkPageAsync calls would read the same _currentArtistLoadedCount
        // offset, double-incrementing it and stalling load-more prematurely.
        if (Interlocked.CompareExchange(ref _autoLoadGuard, 1, 0) != 0) return;
        try
        {
            var ct = _artworkLoadCts?.Token ?? CancellationToken.None;
            await LoadArtworkPageAsync(SelectedArtist, append: true, ct);
            UpdateCache(SelectedArtist);
            SyncViewerTabNavList();
        }
        finally { _autoLoadGuard = 0; }
    }

    /// <summary>
    /// Pushes an updated Liked/Bookmarked/Local-favorite flag onto every currently-loaded card
    /// with a matching artwork ID — called by the artwork viewer right after a Like/Bookmark/
    /// Favorite action succeeds, so a card already on screen in this Gallery view (e.g. the same
    /// artist's gallery, or a "recent works" feed) reflects it immediately without needing a
    /// reload. Only non-null parameters are applied.
    /// </summary>
    /// <summary>
    /// Re-checks Liked/Bookmarked/Local-favorite status for every currently-loaded card against
    /// the authoritative sources (settings, local favorites, Bookmarks cache). The live push in
    /// SyncArtworkFlags only reaches a card if it's already loaded into VisibleArtworks at the
    /// moment the action happens — if you like/bookmark something while it isn't loaded here yet
    /// (e.g. you're looking at it from Bookmarks/Local Favorites instead), an already-realized
    /// card sitting in this view never gets that update on its own. Call this whenever navigating
    /// back to the Gallery so it's always correct, not just "correct if you got lucky with load
    /// order".
    /// </summary>
    public void RefreshLikedBookmarkedFavoriteFlags()
    {
        SettingsService settings;
        Pikura.Core.Services.LocalFavoritesService favorites;
        BookmarksViewModel? bookmarksVm = null;
        try { settings = AppServices.Get<SettingsService>(); } catch { return; }
        try { favorites = AppServices.Get<Pikura.Core.Services.LocalFavoritesService>(); } catch { return; }
        try { bookmarksVm = AppServices.Get<BookmarksViewModel>(); } catch { /* not initialized yet */ }

        Logger.LogInformation("[RefreshFlags/Gallery] VisibleArtworks.Count={Count} ids={Ids}",
            VisibleArtworks.Count, string.Join(",", VisibleArtworks.Select(a => a.Id).Take(50)));
        foreach (var c in VisibleArtworks)
        {
            if (string.IsNullOrEmpty(c.Id)) continue;
            c.IsLiked = settings.Current.PixivLikedArtworkIds.Contains(c.Id);
            c.IsLocalFavorite = favorites.IsFavorite(c.Id);
            if (bookmarksVm != null)
            {
                c.IsPixivBookmarked = bookmarksVm.IsKnownBookmarked(c.Id, out var isPrivate);
                c.IsPixivPrivateBookmark = isPrivate;
            }
        }
    }

    public void SyncArtworkFlags(
        string? id,
        bool? isLiked = null,
        bool? isPixivBookmarked = null,
        bool? isPixivPrivateBookmark = null,
        string? pixivBookmarkId = null,
        bool bookmarkIdProvided = false,
        bool? isLocalFavorite = null)
    {
        if (string.IsNullOrEmpty(id)) return;
        foreach (var c in VisibleArtworks)
        {
            if (c.Id != id) continue;
            if (isLiked.HasValue) c.IsLiked = isLiked.Value;
            if (isPixivBookmarked.HasValue) c.IsPixivBookmarked = isPixivBookmarked.Value;
            if (isPixivPrivateBookmark.HasValue) c.IsPixivPrivateBookmark = isPixivPrivateBookmark.Value;
            if (bookmarkIdProvided) c.PixivBookmarkId = pixivBookmarkId;
            if (isLocalFavorite.HasValue) c.IsLocalFavorite = isLocalFavorite.Value;
        }
    }

    // ─── Follow / unfollow ──────────────────────────────────────────────

    /// <summary>True when the given user id is in our followed-artists list.</summary>
    public bool IsArtistFollowed(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        return Artists.Any(a => a.UserId == userId);
    }

    /// <summary>Updates the local followed-artists list and all visible cards after a follow/unfollow.</summary>
    public void SetArtistFollowed(string userId, string userName, bool followed)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        var existing = Artists.FirstOrDefault(a => a.UserId == userId);
        if (followed)
        {
            if (existing == null)
            {
                var artist = new ArtistCardViewModel(new FollowedArtist
                {
                    UserId = userId,
                    UserName = userName,
                    ProfileImageUrl = null,
                    Following = true
                });
                // Insert at the top — Pixiv's own followed-list sorts most-recently-followed
                // first, and a full refresh would put it there too. Adding at the end made
                // new follows invisible until the list was scrolled or manually refreshed.
                Artists.Insert(0, artist);
                // We don't have an avatar URL yet (the follow action only gives us id/name),
                // so fetch the artist's profile in the background to populate it — otherwise
                // the avatar stayed blank until a full refresh re-fetched the followed list.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var info = await _pixivClient.GetArtistAsync(userId);
                        if (info?.ImageUrl is not { Length: > 0 } url) return;
                        artist.ProfileImageUrl = url;
                        await artist.LoadAvatarAsync(_imageLoader);
                    }
                    catch { /* non-fatal */ }
                });
            }
            else
            {
                existing.IsFollowed = true;
            }
        }
        else
        {
            if (existing != null)
                Artists.Remove(existing);
        }

        foreach (var card in VisibleArtworks)
            if (card.UserId == userId)
                card.IsFollowed = followed;

        ArtistsTotal = Artists.Count;
        RebuildFilteredArtists();
    }

    private void UpdateCache(ArtistCardViewModel artist)
    {
        _artworkCache[artist.UserId] = (
            VisibleArtworks.ToList(),
            _currentArtistAllIds.ToList(),
            ArtworksTotal,
            _currentArtistLoadedCount,
            CanLoadMore);

        // Refresh MRU order and evict least-recently-used entries beyond capacity. We don't
        // dispose the evicted cards' Bitmaps here — a pinned viewer tab can still be holding
        // a reference to one of them — just drop the cache's own reference so the GC can
        // reclaim them once nothing else needs them.
        _artworkCacheOrder.Remove(artist.UserId);
        _artworkCacheOrder.Add(artist.UserId);
        while (_artworkCacheOrder.Count > ArtworkCacheCapacity)
        {
            var evictId = _artworkCacheOrder[0];
            _artworkCacheOrder.RemoveAt(0);
            _artworkCache.Remove(evictId);
        }
    }

    /// <summary>
    /// After loading more artworks, sync the active viewer tab's NavList and TotalCount
    /// so the "X / Y" counter and prev/next navigation reflect the new cards.
    /// </summary>
    private void SyncViewerTabNavList()
    {
        if (SelectedArtist == null) return;
        var current = FilteredArtworks.ToList();
        SyncViewerTabs(CurrentGalleryViewerSource, current, ArtworksTotal);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var a in VisibleArtworks) a.IsSelected = false;
        SelectedCount = 0;
    }

    /// <summary>Whether the given number of selected artworks can be collaged.
    /// At least 1 starts a new collage; adding to an existing one is only allowed
    /// if it has room (capped at <see cref="MaxCollageItems"/>).</summary>
    public bool CanCollage(int selectedCount)
    {
        if (selectedCount == 0) return false;
        if (!HasStoredCollage && !IsCollageMode) return true;
        var collageTab = ViewerTabs.FirstOrDefault(t => t.IsCollage);
        return collageTab is { CollageItems.Count: < MaxCollageItems };
    }

    public bool CanViewSelectedAsCollage => CanCollage(SelectedCount);

    /// <summary>"View as Collage" for the currently-checked artworks. Opens them into a
    /// dedicated collage tab. If a collage tab already exists, the selected artworks are
    /// appended (up to the max).</summary>
    [RelayCommand(CanExecute = nameof(CanViewSelectedAsCollage))]
    private void ViewSelectedAsCollage()
    {
        var selected = VisibleArtworks.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0) return;
        if (HasStoredCollage || IsCollageMode)
        {
            AddToCollage(selected);
            return;
        }
        ShowCollage(selected.Take(MaxCollageItems));
    }

    public bool CanViewSelectedInNewTabs => SelectedCount >= 1;

    /// <summary>Open every selected artwork in its own new viewer tab.</summary>
    [RelayCommand(CanExecute = nameof(CanViewSelectedInNewTabs))]
    private void ViewSelectedInNewTabs()
    {
        var selected = VisibleArtworks.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0) return;
        foreach (var card in selected)
            OpenInNewTab(card, selected, selected.Count, source: CurrentGalleryViewerSource);
        ShowPreview = true;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var a in VisibleArtworks) a.IsSelected = true;
        SelectedCount = VisibleArtworks.Count;
    }

    /// <summary>
    /// Flushes the accumulated artist ID queue to the system clipboard.
    /// Each time you click an artist ID it is added to the queue; pressing this copies them all at once.
    /// </summary>
    [RelayCommand]
    private void CopyAllArtistIds()
    {
        var flushed = QuickClipboardService.FlushArtistIds();
        if (flushed == null)
        {
            StatusMessage = "No artist IDs queued — click individual artist IDs first to add them.";
            return;
        }

        var count = flushed.Split(',').Length;
        // Raise event so the view can write to the system clipboard
        CopyToClipboardRequested?.Invoke(flushed);
        StatusMessage = $"Copied {count} queued artist ID{(count == 1 ? "" : "s")} to clipboard";
    }

    /// <summary>Raised when the ViewModel wants to write text to the system clipboard.</summary>
    public event Action<string>? CopyToClipboardRequested;

    public void NotifySelectionChanged()
    {
        SelectedCount = VisibleArtworks.Count(a => a.IsSelected);
        OnPropertyChanged(nameof(CanViewSelectedAsCollage));
        ViewSelectedAsCollageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Optional navigation list for the inline viewer. When non-null, the viewer
    /// uses this list (instead of <see cref="FilteredArtworks"/>) for prev/next
    /// navigation. Cleared automatically on <see cref="CloseInlineViewer"/>.
    /// </summary>
    public IReadOnlyList<ArtworkCardViewModel>? InlineViewerCardList { get; set; }

    public void OpenInlineViewer(ArtworkCardViewModel card)
    {
        // Plain click: replace the active tab rather than stacking new ones
        OpenInViewer(card);
    }

    /// <summary>
    /// Open a card via a plain click. Replaces the currently selected tab in-place
    /// (or opens the first tab if none exist). Use <see cref="OpenInNewTab"/> only
    /// for the explicit "Open in new tab" context-menu action.
    /// </summary>
    public void OpenInViewer(ArtworkCardViewModel card, IReadOnlyList<ArtworkCardViewModel>? navList = null,
        int totalCount = 0, Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMoreAsync = null,
        string source = "Gallery")
    {
        if (source == "Gallery" && SelectedArtist != null)
            source = CurrentGalleryViewerSource;
        var list = navList ?? FilteredArtworks.ToList();
        // Total is the actual count of navigable items (not the artist's announced catalogue size)
        int total = totalCount > 0
            ? totalCount
            : navList == null && source.StartsWith("Gallery:", StringComparison.Ordinal) && ArtworksTotal > list.Count
                ? ArtworksTotal
                : list.Count;
        Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMore = loadMoreAsync;
        if (loadMore == null && navList == null && CanLoadMore)
        {
            var artist = SelectedArtist;
            loadMore = async () =>
            {
                if (artist == null || !CanLoadMore) return [];
                await LoadMoreArtworksCommand.ExecuteAsync(null);
                return FilteredArtworks.ToList();
            };
        }

        // Plain click: replace the current image tab in-place, but never overwrite the collage tab.
        ViewerSource = source;
        if (SelectedViewerTab is { IsCollage: false } active)
        {
            active.NavList.Clear();
            foreach (var c in list) active.NavList.Add(c);
            active.TotalCount = total;          // refresh counter to match new nav list
            active.LoadMoreAsync = loadMore;    // refresh load-more callback for new context
            active.Source = source;
            active.NavigateTo(card);
            InlineViewerCard = card;
        }
        else
        {
            var tab = new ViewerTab(card, list, total, loadMore, source);
            ViewerTabs.Add(tab);
            SelectedViewerTab = tab;
            InlineViewerCard = card;
        }
    }

    /// <summary>Returns true if any open tab was opened from the given source section.</summary>
    public bool HasTabsFromSource(string source) => ViewerTabs.Any(t => t.Source == source);

    public void SyncViewerTabs(string source, IReadOnlyList<ArtworkCardViewModel> cards, int totalCount = 0)
    {
        foreach (var tab in ViewerTabs.Where(t => t.Source == source))
        {
            var existingIds = new HashSet<string>(tab.NavList.Select(c => c.Id));
            foreach (var card in cards)
                if (existingIds.Add(card.Id)) tab.NavList.Add(card);
            tab.TotalCount = Math.Max(tab.TotalCount, Math.Max(totalCount, tab.NavList.Count));
        }
        _navListVersion++;
        OnPropertyChanged(nameof(NavListVersion));
    }

    public void OpenInNewTab(ArtworkCardViewModel card, IReadOnlyList<ArtworkCardViewModel>? navList = null,
        int totalCount = 0, Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMoreAsync = null,
        string source = "Gallery")
    {
        if (source == "Gallery" && SelectedArtist != null)
            source = CurrentGalleryViewerSource;
        ViewerSource = source;
        // Snapshot filtered artworks so navigating to another artist doesn't mutate this tab's list
        var list = navList ?? FilteredArtworks.ToList();

        // Total is the actual count of navigable items (not the artist's announced catalogue size)
        int total = totalCount > 0
            ? totalCount
            : navList == null && source.StartsWith("Gallery:", StringComparison.Ordinal) && ArtworksTotal > list.Count
                ? ArtworksTotal
                : list.Count;
        Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMore = loadMoreAsync;
        if (loadMore == null && navList == null && CanLoadMore)
        {
            // Capture current artist so the callback is stable
            var artist = SelectedArtist;
            loadMore = async () =>
            {
                if (artist == null || !CanLoadMore) return [];
                await LoadMoreArtworksCommand.ExecuteAsync(null);
                return FilteredArtworks.ToList();
            };
        }

        // "Open in new tab" always creates a new global tab. If the collage is currently visible,
        // keep it visible and add the image tab in the background; the user can switch to it when ready.
        var tab = new ViewerTab(card, list, total, loadMore, source);
        ViewerTabs.Add(tab);
        if (SelectedViewerTab?.IsCollage != true)
        {
            SelectedViewerTab = tab;
            InlineViewerCard = card;
        }
    }

    [RelayCommand]
    public void CloseViewerTab(ViewerTab? tab)
    {
        if (tab == null) return;
        var idx = ViewerTabs.IndexOf(tab);
        ViewerTabs.Remove(tab);
        if (ViewerTabs.Count == 0)
        {
            SelectedViewerTab = null;
            InlineViewerCard = null;
            InlineViewerCardList = null;
            ShowPreview = false; // Close side panel when last tab closes
        }
        else
        {
            SelectedViewerTab = ViewerTabs[Math.Max(0, idx - 1)];
        }
    }

    [RelayCommand]
    public void CloseInlineViewer()
    {
        ViewerTabs.Clear();
        SelectedViewerTab = null;
        InlineViewerCard = null;
        InlineViewerCardList = null;
    }

    /// <summary>
    /// Snapshots the currently open viewer tabs (artwork IDs + collage contents) into settings
    /// so they can be reopened next launch. Called on app shutdown.
    /// </summary>
    public void SaveViewerTabsState()
    {
        try
        {
            var entries = new List<Pikura.Core.Settings.PersistedViewerTab>();
            foreach (var tab in ViewerTabs)
            {
                if (tab.IsCollage)
                {
                    if (tab.CollageItems.Count == 0) continue;
                    entries.Add(new Pikura.Core.Settings.PersistedViewerTab
                    {
                        IsCollage = true,
                        Source = tab.Source,
                        Header = tab.Header,
                        CollageArtworkIds = tab.CollageItems.Select(c => c.Id).ToList()
                    });
                }
                else if (tab.Card != null)
                {
                    entries.Add(new Pikura.Core.Settings.PersistedViewerTab
                    {
                        IsCollage = false,
                        Source = tab.Source,
                        Header = tab.Header,
                        ArtworkId = tab.Card.Id
                    });
                }
            }

            var selectedIndex = SelectedViewerTab != null ? ViewerTabs.IndexOf(SelectedViewerTab) : -1;
            _settingsService.Update(s =>
            {
                s.PersistedViewerTabs = entries;
                s.PersistedSelectedTabIndex = selectedIndex;
            });
        }
        catch { /* non-fatal — worst case tabs just don't restore next launch */ }
    }

    /// <summary>
    /// Reopens the viewer tabs that were open when the app last closed, re-fetching each
    /// artwork from Pixiv. Called once at startup, after the main window is ready.
    /// </summary>
    public async Task RestoreViewerTabsAsync()
    {
        try
        {
            var entries = _settingsService.Current.PersistedViewerTabs;
            if (entries == null || entries.Count == 0) return;

            foreach (var entry in entries)
            {
                if (entry.IsCollage)
                {
                    if (entry.CollageArtworkIds is not { Count: > 0 }) continue;
                    var cards = new List<ArtworkCardViewModel>();
                    foreach (var id in entry.CollageArtworkIds)
                    {
                        var c = await BuildCardForArtworkAsync(id);
                        if (c != null) cards.Add(c);
                    }
                    if (cards.Count == 0) continue;
                    AddToCollage(cards);
                }
                else
                {
                    if (string.IsNullOrEmpty(entry.ArtworkId)) continue;
                    var card = await BuildCardForArtworkAsync(entry.ArtworkId);
                    if (card == null) continue;
                    var list = new List<ArtworkCardViewModel> { card };
                    OpenInNewTab(card, list, list.Count, null, entry.Source ?? "Gallery");
                }
            }

            var selIdx = _settingsService.Current.PersistedSelectedTabIndex;
            if (selIdx >= 0 && selIdx < ViewerTabs.Count)
                SelectedViewerTab = ViewerTabs[selIdx];

            if (ViewerTabs.Count > 0)
                ShowPreview = true;
        }
        catch { /* non-fatal — worst case some/all tabs fail to restore */ }
    }

    public Task DownloadSingleAsync(ArtworkCardViewModel card)
        => DownloadCoreAsync([card]);

    public Task DownloadSinglePageAsync(ArtworkCardViewModel card, int pageIndex)
        => DownloadPagesAsync(card, new[] { pageIndex });

    public async Task DownloadPagesAsync(ArtworkCardViewModel card, IReadOnlyCollection<int> pageIndexes)
    {
        BeginBulkDownload();
        var pageLabel = pageIndexes.Count == 1
            ? $"p{pageIndexes.First() + 1}"
            : $"{pageIndexes.Count} pages";
        var job = new DownloadJob
        {
            Name = $"{card.Title} ({pageLabel})",
            Type = DownloadJobType.ImageId,
            Status = JobStatus.Running,
            StartedAt = DateTime.UtcNow,
            Targets = [new DownloadTarget { TargetId = card.Id, Name = card.Title, ThumbnailUrl = card.ThumbnailUrl, UserName = card.UserName, UserId = card.UserId, Type = TargetType.Artwork, Status = TargetStatus.Running }]
        };
        try
        {
            var files = await _downloader.DownloadArtworkPagesAsync(card.Artwork, pageIndexes);
            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.OutputFolder = files.Count > 0 ? Path.GetDirectoryName(files[0]) : null;
            job.Targets[0].Status = TargetStatus.Completed;
            job.Targets[0].DownloadedItems = files.Count;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Download pages failed for {Id}", card.Id);
            job.Status = JobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorMessage = ex.Message;
            job.Targets[0].Status = TargetStatus.Failed;
            job.Targets[0].ErrorMessage = ex.Message;
        }
        finally
        {
            EndBulkDownload();
            _ = Task.Run(async () => { await _jobRepository.SaveJobAsync(job); _coordinator.NotifyJobSaved(job); });
        }
    }

    partial void OnSelectedArtistChanged(ArtistCardViewModel? value)
    {
        if (_suppressArtistChanged) return;

        // Selecting a different artist drops any active search so the new gallery loads cleanly.
        _searchBackup = null;
        _searchPreviousArtist = null;
        _searchWasRecentFeedActive = false;
        IsSearchActive = false;
        ShowSearchInfo = false;

        // Debug: Log when SelectedArtist changes
        System.Diagnostics.Debug.WriteLine($"SelectedArtist changed to: {value?.Name ?? "null"}");
        OnPropertyChanged(nameof(SelectedArtist));

        // Sync IsCurrentArtist on all visible cards
        var selectedId = value?.UserId;
        foreach (var card in VisibleArtworks)
            card.IsCurrentArtist = selectedId != null && card.UserId == selectedId;

        if (value != null)
        {
            IsRecentFeedActive = false;
            _artistLoadTask = LoadArtistArtworksAsync(value);
        }
    }

    /// <summary>
    /// Update the status message to reflect the current artwork counts.
    /// Shows filtered count when filters are reducing visibility.
    /// Format: "Artist — N shown (M loaded / T total)" when filtered, else "Artist — M / T works".
    /// </summary>
    private void UpdateArtworkCountStatus()
    {
        if (SelectedArtist == null) return;
        // Don't override status during non-artist views
        if (IsRecentFeedActive || IsIdSearchMode) return;

        var artist = SelectedArtist;
        var loaded = VisibleArtworks.Count;
        var filtered = FilteredArtworks.Count;
        var total = ArtworksTotal;

        if (filtered < loaded)
        {
            // Filters are hiding some loaded artworks
            StatusMessage = $"{artist.Name} — {filtered} shown ({loaded} loaded / {total} total)";
        }
        else
        {
            StatusMessage = $"{artist.Name} — {loaded} / {total} works";
        }
    }

    private async Task LoadArtistArtworksAsync(ArtistCardViewModel artist)
    {
        // Cancel any in-progress load for a previous artist
        _artworkLoadCts?.Cancel();
        _artworkLoadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _artworkLoadCts = cts;
        var ct = cts.Token;

        // Restore from cache instantly — no spinner, no network call
        if (_artworkCache.TryGetValue(artist.UserId, out var cached))
        {
            VisibleArtworks.Clear();
            foreach (var c in cached.Cards) AddArtworkCard(c, artist.UserId);
            _currentArtistAllIds = cached.AllIds;   // full list so Load More works
            _currentArtistLoadedCount = cached.LoadedCount;
            ArtworksTotal = cached.TotalIds;
            CanLoadMore = cached.CanMore;
            IsLoading = false; // ensure spinner clears even if a prior load was in-flight
            UpdateArtworkCountStatus();
            // Cached cards may have been constructed (or last touched) before a Like/Bookmark/
            // Favorite happened elsewhere in the app — re-check them now that they're actually
            // the visible list, rather than relying on a live push having reached them.
            RefreshLikedBookmarkedFavoriteFlags();
            return;
        }

        IsLoading = true;
        VisibleArtworks.Clear();
        _currentArtistAllIds = [];
        _currentArtistLoadedCount = 0;
        CanLoadMore = false;
        ArtworksTotal = 0;
        StatusMessage = $"Loading {artist.Name}…";

        try
        {
            Task<UserProfileAll> profileTask;
            if (_profilePrefetch.TryGetValue(artist.UserId, out var pending))
            {
                _profilePrefetch.Remove(artist.UserId);
                profileTask = pending;
            }
            else
            {
                profileTask = _pixivClient.GetUserProfileAllAsync(artist.UserId);
            }
            var profile = await profileTask;
            ct.ThrowIfCancellationRequested();

            // Deduplicate: Pixiv API can return the same ID in multiple buckets (illusts + manga)
            _currentArtistAllIds = profile.AllArtworkIds().Distinct().ToList();
            ArtworksTotal = _currentArtistAllIds.Count;

            if (_currentArtistAllIds.Count == 0)
            {
                StatusMessage = $"{artist.Name} — no artworks";
                return;
            }

            // Render the first page immediately so the user sees cards without waiting
            // for the second page's API call. Drop IsLoading the moment page 1 is on
            // screen — perceived load time matches Rankings now.
            await LoadArtworkPageAsync(artist, append: false, ct);
            ct.ThrowIfCancellationRequested();
            IsLoading = false;
            UpdateArtworkCountStatus();
            // Correct the Liked/Bookmarked/Favorite badges as soon as page 1 is visible —
            // previously this only ran after page 2 finished too, so cards visibly sat with no
            // badges for the full ~1-2s of both network round-trips before flipping correct.
            RefreshLikedBookmarkedFavoriteFlags();

            // Continue loading page 2 in the background. The cache is updated only
            // after both pages finish so that revisits restore the full 96-card view.
            if (CanLoadMore)
            {
                await LoadArtworkPageAsync(artist, append: true, ct);
                ct.ThrowIfCancellationRequested();
                RefreshLikedBookmarkedFavoriteFlags();
            }

            if (!ct.IsCancellationRequested)
                UpdateCache(artist);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load artworks for {Artist}", artist.Name);
            StatusMessage = "Failed to load artworks: " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
                if (SelectedArtist?.UserId == artist.UserId
                    && StatusMessage.StartsWith("Loading ", StringComparison.Ordinal))
                    UpdateArtworkCountStatus();
            }
        }
    }

    private async Task LoadArtworkPageAsync(ArtistCardViewModel artist, bool append, CancellationToken ct = default)
    {
        if (!append)
        {
            VisibleArtworks.Clear();
            _currentArtistLoadedCount = 0;
        }

        var batch = _currentArtistAllIds
            .Skip(_currentArtistLoadedCount)
            .Take(PageSize)
            .ToList();

        if (batch.Count == 0) { CanLoadMore = false; return; }

        var works = await _pixivClient.GetArtworksMetadataAsync(artist.UserId, batch);
        ct.ThrowIfCancellationRequested();

        var existingIds = VisibleArtworks.Select(v => v.Id).ToHashSet();
        var artistFollowed = IsArtistFollowed(artist.UserId);
        foreach (var id in batch)
        {
            if (!works.TryGetValue(id, out var artwork)) continue;
            if (!existingIds.Add(id)) continue;  // skip duplicates
            var vm = new ArtworkCardViewModel(artwork)
            {
                IsFollowed = artistFollowed,
                IsBlurred = _settingsService.Current.BlurR18Content && artwork.IsR18
            };
            AddArtworkCard(vm, artist.UserId);
            _ = vm.LoadThumbnailAsync(_imageLoader, ct: ct);
        }
        _currentArtistLoadedCount += batch.Count;
        CanLoadMore = _currentArtistLoadedCount < _currentArtistAllIds.Count;
        UpdateArtworkCountStatus();
    }

    // ── Download commands ──────────────────────────────────────────────────

    [RelayCommand]
    public async Task DownloadSelectedAsync()
    {
        var picked = VisibleArtworks.Where(a => a.IsSelected).ToList();
        if (picked.Count == 0)
        {
            StatusMessage = "No artworks selected.";
            return;
        }

        Func<Task> downloadTask = () => DownloadCoreAsync(picked);

        if (_queueEntries.Count > 0 || !TryClaimBulkSlot())
        {
            await QueueDownloadAsync(downloadTask, $"Download selected ({picked.Count} artworks)", picked);
        }
        else
        {
            try { await downloadTask(); }
            finally { ReleaseBulkSlot(); }
        }
    }

    [RelayCommand]
    public async Task DownloadVisibleAsync()
    {
        var snapshot = VisibleArtworks.ToList();
        if (snapshot.Count == 0) { StatusMessage = "No artworks loaded."; return; }

        Func<Task> downloadTask = () => DownloadCoreAsync(snapshot);

        if (_queueEntries.Count > 0 || !TryClaimBulkSlot())
        {
            await QueueDownloadAsync(downloadTask, $"Download loaded ({snapshot.Count} artworks)", snapshot);
        }
        else
        {
            try { await downloadTask(); }
            finally { ReleaseBulkSlot(); }
        }
    }

    [RelayCommand]
    public async Task DownloadAllAsync()
    {
        if (SelectedArtist == null)
        {
            StatusMessage = "Select an artist first.";
            return;
        }

        // Snapshot artist + id list so queued execution uses correct values
        var artistSnapshot = SelectedArtist;
        var artistName = artistSnapshot.Name;
        var artistUserId = artistSnapshot.UserId;

        // If IDs not yet populated (e.g. just navigated from Rankings), wait a bit
        if (_currentArtistAllIds.Count == 0)
        {
            StatusMessage = $"Waiting for {artistName}'s artwork list to load…";
            var waitStart = DateTime.UtcNow;
            while (_currentArtistAllIds.Count == 0 && (DateTime.UtcNow - waitStart).TotalSeconds < 15)
            {
                await Task.Delay(200);
                if (SelectedArtist?.UserId != artistUserId)
                {
                    StatusMessage = "Artist changed; download cancelled.";
                    return;
                }
            }
            if (_currentArtistAllIds.Count == 0)
            {
                StatusMessage = $"Could not load artwork list for {artistName}.";
                return;
            }
        }

        var allIdsSnapshot = _currentArtistAllIds.ToList();

        Func<Task> downloadTask = async () =>
        {
            // Self-contained: fetch metadata for the snapshotted IDs directly so
            // we don't share state with VisibleArtworks (which may be showing a
            // different artist by the time this queued task runs).
            var cards = new List<ArtworkCardViewModel>();
            const int batchSize = 48;
            for (int i = 0; i < allIdsSnapshot.Count; i += batchSize)
            {
                var batch = allIdsSnapshot.Skip(i).Take(batchSize).ToList();
                StatusMessage = $"Loading {artistName} metadata… {Math.Min(i + batchSize, allIdsSnapshot.Count)}/{allIdsSnapshot.Count}";
                try
                {
                    var metadata = await _pixivClient.GetArtworksMetadataAsync(artistUserId, batch);
                    foreach (var id in batch)
                    {
                        if (metadata.TryGetValue(id, out var preview))
                            cards.Add(new ArtworkCardViewModel(preview));
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed metadata batch for {Artist}", artistName);
                }
                await Task.Yield();
            }

            if (cards.Count == 0)
            {
                StatusMessage = $"Could not load any artworks for {artistName}.";
                return;
            }

            StatusMessage = $"Downloading {cards.Count} artworks from {artistName}…";
            await DownloadCoreAsync(cards);
        };

        // Try to claim a slot; if all taken (or queue non-empty), enqueue for fairness
        if (_queueEntries.Count > 0 || !TryClaimBulkSlot())
        {
            await QueueDownloadAsync(downloadTask, $"Download all from {artistName}", VisibleArtworks.ToList());
        }
        else
        {
            try { await downloadTask(); }
            finally { ReleaseBulkSlot(); }
        }
    }

    private bool CanDownload()
    {
        return SelectedArtist != null;
    }

    [RelayCommand]
    public async Task DownloadWithPresetAsync()
    {
        var picked = VisibleArtworks.Where(a => a.IsSelected).ToList();
        if (picked.Count == 0)
        {
            // If no selection, use all visible artworks
            picked = VisibleArtworks.ToList();
        }

        if (picked.Count == 0)
        {
            StatusMessage = "No artworks to download.";
            return;
        }

        // Show preset window with the first artwork as preview
        var dialogService = _dialogService;
        var firstArtwork = picked.First().Artwork;

        var preset = await dialogService.ShowDownloadPresetDialogAsync(firstArtwork);
        if (preset != null)
        {
            await DownloadWithPresetCoreAsync(picked, preset);
        }
    }

    public async Task DownloadWithPresetAsync(ArtworkCardViewModel card, ImageEditPreset preset)
        => await DownloadWithPresetCoreAsync([card], preset);

    public async Task DownloadWithPresetAsync(IReadOnlyList<ArtworkPreview> previews, ImageEditPreset preset)
    {
        if (previews.Count == 0) return;
        var cards = previews.Select(p =>
            VisibleArtworks.FirstOrDefault(c => c.Id == p.Id) ?? new ArtworkCardViewModel(p)).ToList();
        await DownloadWithPresetCoreAsync(cards, preset);
    }

    private async Task DownloadWithPresetCoreAsync(IReadOnlyList<ArtworkCardViewModel> cards, ImageEditPreset preset)
    {
        if (cards.Count == 0) return;

        var acctOverride = BuildAccountSettingsOverride();

        // Add the preset to the settings override
        // CRITICAL: must set UseGlobalSettings=false so the downloader honors our overrides
        if (acctOverride == null)
        {
            acctOverride = new SettingsOverride
            {
                UseGlobalSettings = false,
                MaxConcurrentDownloads = _settingsService.Current.MaxConcurrentDownloads,
                ImagePreset = preset
            };
        }
        else
        {
            acctOverride.UseGlobalSettings = false;
            acctOverride.ImagePreset = preset;
        }

        if (!string.IsNullOrEmpty(preset?.CustomOutputFolder))
        {
            acctOverride.CustomOutputFolder = preset.CustomOutputFolder;
        }

        // ── Re-download confirmation ──────────────────────────────────────────
        var approved = new List<ArtworkCardViewModel>(cards.Count);
        var ownerWindow = _dialogService.OwnerWindow;
        bool? bulkDecision = null;

        // Suppress redownload warnings whenever a download preset is supplied. The
        // user picked a preset from the dialog (Discover / Rankings / Bookmarks /
        // Gallery) so they've already opted in to (re)processing the artwork.
        var skipAllWarnings = preset != null;
        
        // Determine what type of file to check for based on save mode:
        // - SaveAsNew = true → checking for processed files (_processed suffix)
        // - SaveAsNew = false (Overwrite) → checking for unprocessed files (original)
        var fileTypeFilter = preset?.SaveAsNew == true ? "processed" : "unprocessed";

        foreach (var card in cards)
        {
            // Skip file check entirely if AlsoDownloadUnprocessed is enabled
            if (!skipAllWarnings && _downloader.HasExistingFiles(card.Id, acctOverride, fileTypeFilter))
            {
                if (bulkDecision == false) { continue; } // No-to-all: skip
                if (bulkDecision == true)  { approved.Add(card); continue; } // Yes-to-all: approve

                // Show dialog if we have a window, otherwise auto-approve (user already clicked download)
                var choice = ownerWindow != null
                    ? await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => RedownloadConfirmDialog.ShowAsync(ownerWindow, card.Title, card.Thumbnail))
                    : RedownloadChoice.Yes; // No UI available, default to Yes

                if (choice == RedownloadChoice.NoToAll)  { bulkDecision = false; continue; }
                if (choice == RedownloadChoice.No)       { continue; }
                if (choice == RedownloadChoice.YesToAll) { bulkDecision = true; }
                approved.Add(card); // Yes or YesToAll
            }
            else
            {
                approved.Add(card);
            }
        }

        // Apply artwork-level selection filter from the preset dialog while preserving
        // original indices so per-page preset overrides line up correctly.
        var artworkFilter = preset?.DownloadAllArtworks == false && preset?.SelectedArtworkIndices is { Count: > 0 }
            ? approved.Select((c, i) => (card: c, originalIdx: i))
                .Where(x => preset.SelectedArtworkIndices.Contains(x.originalIdx))
                .ToList()
            : approved.Select((c, i) => (card: c, originalIdx: i)).ToList();

        if (artworkFilter.Count == 0) { StatusMessage = "No artworks selected for download."; return; }

        var total = artworkFilter.Count;
        var done = 0;
        var failed = 0;
        var maxConcurrent = Math.Max(1, acctOverride.MaxConcurrentDownloads ?? _settingsService.Current.MaxConcurrentDownloads);
        using var gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        string? outputFolder = null;

        var targets = artworkFilter.Select(x => new DownloadTarget
        {
            TargetId = x.card.Id, Name = x.card.Title, ThumbnailUrl = x.card.ThumbnailUrl, UserName = x.card.UserName, UserId = x.card.UserId, Type = TargetType.Artwork, Status = TargetStatus.Pending
        }).ToList();

        var artistPrefix = SelectedArtist != null ? $"{SelectedArtist.Name}: " : "";
        var jobName = artworkFilter.Count == 1 ? $"{artistPrefix}{artworkFilter[0].card.Title}" : $"{artistPrefix}{artworkFilter.Count} artworks (with preset)";
        var activeJob = await _coordinator.CreateJobAsync(
            DownloadJobType.ImageId, jobName, targets,
            settingsOverride: acctOverride, startImmediately: false,
            initialStatusOverride: JobStatus.Running);

        using var cts = new System.Threading.CancellationTokenSource();
        _coordinator.RegisterExternalJob(activeJob.Id, cts);
        await _coordinator.NotifyJobRunningAsync(activeJob);
        await Task.Delay(50);

        var ct = cts.Token;
        var tasks = artworkFilter.Select(async (x, idx) =>
        {
            var card = x.card;
            var originalIdx = x.originalIdx;
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                card.IsDownloading = true;
                targets[idx].Status = TargetStatus.Running;
                var localDone = done;
                _coordinator.ReportJobProgress(activeJob.Id, new JobProgress(
                    activeJob.Id, JobStatus.Running, localDone, total,
                    total > 0 ? localDone * 100.0 / total : 0,
                    card.Title, $"Downloading {card.Title}…",
                    CurrentArtworkId: card.Id,
                    CurrentThumbnailUrl: card.ThumbnailUrl));
                var progress = new Progress<DownloadProgress>(p =>
                {
                    var pct = p.TotalBytes > 0 ? (int)(100 * p.BytesSoFar / p.TotalBytes.Value) : 0;
                    StatusMessage = $"Downloading {p.ArtworkId} p{p.PageIndex + 1}/{p.TotalPages} ({pct}%) — {done}/{total}";
                    _coordinator.ReportJobProgress(activeJob.Id, new JobProgress(
                        activeJob.Id, JobStatus.Running, done, total,
                        total > 0 ? done * 100.0 / total : 0,
                        card.Title, null,
                        CurrentArtworkId: p.ArtworkId,
                        CurrentThumbnailUrl: card.ThumbnailUrl,
                        CurrentPageIndex: p.PageIndex,
                        CurrentPageTotal: p.TotalPages,
                        CurrentBytesSoFar: p.BytesSoFar,
                        CurrentTotalBytes: p.TotalBytes));
                });
                var files = await _downloader.DownloadArtworkAsync(card.Artwork, progress, ct, overrideSettings: acctOverride, batchArtworkIndex: originalIdx);
                Interlocked.Increment(ref done);
                targets[idx].Status = TargetStatus.Completed;
                targets[idx].DownloadedItems = files.Count;
                if (outputFolder == null && files.Count > 0)
                    outputFolder = Path.GetDirectoryName(files[0]);
                // Persist completion immediately so a later pause/cancel/crash lets
                // resume skip this artwork instead of re-fetching its metadata.
                _ = _jobRepository.UpdateTargetStatusAsync(targets[idx].Id, TargetStatus.Completed, 1, files.Count);
            }
            catch (OperationCanceledException)
            {
                targets[idx].Status = TargetStatus.Cancelled;
                card.IsDownloading = false;
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                targets[idx].Status = TargetStatus.Failed;
                targets[idx].ErrorMessage = ex.Message;
                _ = _jobRepository.UpdateTargetStatusAsync(targets[idx].Id, TargetStatus.Failed, errorMessage: ex.Message);
                Logger.LogError(ex, "Download failed for {Id}", card.Id);
            }
            finally
            {
                card.IsDownloading = false;
                gate.Release();
            }
        }).ToList();

        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }

        var currentStatus = (await _coordinator.GetJobsAsync()).FirstOrDefault(j => j.Id == activeJob.Id)?.Status;
        if (currentStatus == JobStatus.Paused) { StatusMessage = "Download paused."; _coordinator.UnregisterExternalJob(activeJob.Id); return; }

        StatusMessage = failed == 0 ? $"Downloaded {done}/{total} artworks with preset." : $"Done: {done} ok, {failed} failed.";
        activeJob.Status      = ct.IsCancellationRequested ? JobStatus.Cancelled : failed == 0 ? JobStatus.Completed : JobStatus.Failed;
        activeJob.CompletedAt = DateTime.UtcNow;
        activeJob.OutputFolder = outputFolder;
        _coordinator.UnregisterExternalJob(activeJob.Id);
        _ = Task.Run(async () => { await _jobRepository.SaveJobAsync(activeJob); _coordinator.NotifyJobSaved(activeJob); });
    }

    public async Task DownloadArtworkAsync(ArtworkPreview artwork)
    {
        var card = VisibleArtworks.FirstOrDefault(c => c.Id == artwork.Id)
                   ?? new ArtworkCardViewModel(artwork);
        await DownloadCoreAsync([card]);
    }

    public Task DownloadPreviewsAsync(IReadOnlyList<ArtworkPreview> previews)
    {
        if (previews.Count == 0) return Task.CompletedTask;
        var cards = previews.Select(p =>
            VisibleArtworks.FirstOrDefault(c => c.Id == p.Id) ?? new ArtworkCardViewModel(p)).ToList();
        return DownloadCoreAsync(cards);
    }

    public async Task DownloadArtworkRangeAsync(IReadOnlyList<int> oneBasedPositions)
    {
        if (SelectedArtist == null || _currentArtistAllIds.Count == 0 || oneBasedPositions.Count == 0) return;

        var selectedIds = oneBasedPositions
            .Where(i => i >= 1 && i <= _currentArtistAllIds.Count)
            .Select(i => _currentArtistAllIds[i - 1])
            .Distinct().ToList();

        StatusMessage = $"Fetching metadata for {selectedIds.Count} artworks…";
        var works = await _pixivClient.GetArtworksMetadataAsync(SelectedArtist.UserId, selectedIds);
        var cards = works.Values.Select(p => new ArtworkCardViewModel(p)).ToList();
        await DownloadCoreAsync(cards);
    }

    private SettingsOverride? BuildAccountSettingsOverride()
    {
        var acct = _accountService?.ActiveProfile;
        if (acct?.Settings is not { UseAccountSettings: true } s) return null;
        return new SettingsOverride
        {
            UseGlobalSettings = false,
            DownloadRoot           = string.IsNullOrWhiteSpace(s.DownloadRoot)      ? null : s.DownloadRoot,
            FolderTemplate         = string.IsNullOrWhiteSpace(s.FolderTemplate)    ? null : s.FolderTemplate,
            FilenameTemplate       = string.IsNullOrWhiteSpace(s.FilenameTemplate)  ? null : s.FilenameTemplate,
            MaxConcurrentDownloads = s.MaxConcurrentDownloads,
            FilterAiGenerated      = s.FilterAiGenerated,
            SkipR18                = s.SkipR18,
            SkipR18G               = s.SkipR18G,
            SeparateR18Folder      = s.SeparateR18Folder,
            AllowRedownload        = s.AllowRedownload,
        };
    }

    private async Task DownloadCoreAsync(IReadOnlyList<ArtworkCardViewModel> cards, SettingsOverride? settingsOverride = null, string? jobName = null)
    {
        if (cards.Count == 0) return;

        var acctOverride = settingsOverride ?? BuildAccountSettingsOverride();

        // Existing-file handling is governed universally by the Overwrite behavior
        // setting (Skip / Overwrite / Backup) inside PixivDownloadService — no
        // per-card disk scan or confirmation dialog here (that froze the UI on
        // large jobs and bypassed the configured behavior).
        var approved = cards.ToList();
        if (approved.Count == 0) { StatusMessage = "All artworks skipped."; return; }

        var total = approved.Count;
        var done = 0;
        var failed = 0;
        var maxConcurrent = Math.Max(1, acctOverride?.MaxConcurrentDownloads ?? _settingsService.Current.MaxConcurrentDownloads);
        using var gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        string? outputFolder = null;

        var targets = approved.Select(c => new DownloadTarget
        {
            TargetId = c.Id, Name = c.Title, ThumbnailUrl = c.ThumbnailUrl, UserName = c.UserName, UserId = c.UserId, Type = TargetType.Artwork, Status = TargetStatus.Pending
        }).ToList();

        if (string.IsNullOrWhiteSpace(jobName))
        {
            var artistPrefix = SelectedArtist != null ? $"{SelectedArtist.Name}: " : "";
            jobName = approved.Count == 1 ? $"{artistPrefix}{approved[0].Title}" : $"{artistPrefix}{approved.Count} artworks";
        }
        var activeJob = await _coordinator.CreateJobAsync(
            DownloadJobType.ImageId, jobName, targets,
            settingsOverride: acctOverride, startImmediately: false,
            initialStatusOverride: JobStatus.Running);

        // Register a semaphore-based executor so pause/cancel tokens flow through.
        using var cts = new System.Threading.CancellationTokenSource();
        _coordinator.RegisterExternalJob(activeJob.Id, cts);

        await _coordinator.NotifyJobRunningAsync(activeJob);
        await Task.Delay(50);

        var ct = cts.Token;
        var tasks = approved.Select(async (card, idx) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                card.IsDownloading = true;
                targets[idx].Status = TargetStatus.Running;
                var localDone = done;
                _coordinator.ReportJobProgress(activeJob.Id, new JobProgress(
                    activeJob.Id, JobStatus.Running, localDone, total,
                    total > 0 ? localDone * 100.0 / total : 0,
                    card.Title, $"Downloading {card.Title}…",
                    CurrentArtworkId: card.Id,
                    CurrentThumbnailUrl: card.ThumbnailUrl));
                var progress = new Progress<DownloadProgress>(p =>
                {
                    var pct = p.TotalBytes > 0 ? (int)(100 * p.BytesSoFar / p.TotalBytes.Value) : 0;
                    StatusMessage = $"Downloading {p.ArtworkId} p{p.PageIndex + 1}/{p.TotalPages} ({pct}%) — {done}/{total}";
                    _coordinator.ReportJobProgress(activeJob.Id, new JobProgress(
                        activeJob.Id, JobStatus.Running, done, total,
                        total > 0 ? done * 100.0 / total : 0,
                        card.Title, null,
                        CurrentArtworkId: p.ArtworkId,
                        CurrentThumbnailUrl: card.ThumbnailUrl,
                        CurrentPageIndex: p.PageIndex,
                        CurrentPageTotal: p.TotalPages,
                        CurrentBytesSoFar: p.BytesSoFar,
                        CurrentTotalBytes: p.TotalBytes));
                });
                var files = await _downloader.DownloadArtworkAsync(card.Artwork, progress, ct, overrideSettings: acctOverride, batchArtworkIndex: idx);
                Interlocked.Increment(ref done);
                targets[idx].Status = TargetStatus.Completed;
                targets[idx].DownloadedItems = files.Count;
                if (outputFolder == null && files.Count > 0)
                    outputFolder = Path.GetDirectoryName(files[0]);
                // Persist completion immediately so a later pause/cancel/crash lets
                // resume skip this artwork instead of re-fetching its metadata.
                _ = _jobRepository.UpdateTargetStatusAsync(targets[idx].Id, TargetStatus.Completed, 1, files.Count);
            }
            catch (OperationCanceledException)
            {
                targets[idx].Status = TargetStatus.Cancelled;
                card.IsDownloading = false;
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                targets[idx].Status = TargetStatus.Failed;
                targets[idx].ErrorMessage = ex.Message;
                _ = _jobRepository.UpdateTargetStatusAsync(targets[idx].Id, TargetStatus.Failed, errorMessage: ex.Message);
                Logger.LogError(ex, "Download failed for {Id}", card.Id);
            }
            finally
            {
                card.IsDownloading = false;
                gate.Release();
            }
        }).ToList();

        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }

        // Check if paused — coordinator already updated DB status via PauseJobAsync
        var currentStatus = (await _coordinator.GetJobsAsync()).FirstOrDefault(j => j.Id == activeJob.Id)?.Status;
        if (currentStatus == JobStatus.Paused)
        {
            StatusMessage = "Download paused.";
            _coordinator.UnregisterExternalJob(activeJob.Id);
            return;
        }

        StatusMessage = failed == 0
            ? $"Downloaded {done}/{total} artworks."
            : $"Done: {done} ok, {failed} failed.";

        activeJob.Status      = ct.IsCancellationRequested && currentStatus != JobStatus.Paused
            ? JobStatus.Cancelled
            : failed == 0 ? JobStatus.Completed : JobStatus.Failed;
        activeJob.CompletedAt = DateTime.UtcNow;
        activeJob.OutputFolder = outputFolder;
        _coordinator.UnregisterExternalJob(activeJob.Id);
        _ = Task.Run(async () => { await _jobRepository.SaveJobAsync(activeJob); _coordinator.NotifyJobSaved(activeJob); });
    }

    /// <summary>
    /// Queues selected artworks for download with an image edit preset.
    /// Fetches original URLs and enqueues each page for processing.
    /// </summary>
    public async Task QueueDownloadWithPresetAsync(List<ArtworkCardViewModel> cards, ImageEditPreset preset)
    {
        if (cards.Count == 0) return;

        foreach (var card in cards)
        {
            try
            {
                // Fetch artwork pages to get original URLs
                var pages = await _pixivClient.GetArtworkPagesAsync(card.Id);

                for (int i = 0; i < pages.Count; i++)
                {
                    var page = pages[i];
                    var target = new DownloadTarget(
                        card.Id,
                        card.Title,
                        card.UserName,
                        card.UserId,
                        page.Urls.Original,
                        i,
                        pages.Count);

                    _coordinator.QueueDownloadWithPreset(target, preset);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to queue preset download for artwork {ArtworkId}", card.Id);
            }
        }

        StatusMessage = $"Queued {cards.Count} artworks for download with preset: {preset.Name}";
    }

    public bool HasArtists => Artists.Count > 0;
}

public partial class ArtistCardViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isFollowed;
    [ObservableProperty] private Bitmap? _avatar;

    public string UserId { get; }
    public string Name { get; }
    public string? ProfileImageUrl { get; set; }
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[0].ToString().ToUpperInvariant();

    public ArtistCardViewModel(FollowedArtist artist)
    {
        UserId = artist.UserId;
        Name = artist.UserName;
        ProfileImageUrl = artist.ProfileImageUrl;
        IsFollowed = artist.Following;
    }

    public async Task LoadAvatarAsync(PixivImageLoader loader)
    {
        if (string.IsNullOrWhiteSpace(ProfileImageUrl)) return;
        try
        {
            var bytes = await loader.FetchBytesAsync(ProfileImageUrl);
            if (bytes is null) return;
            // Decode on background thread to avoid UI-thread jank on large lists
            var bmp = await Task.Run(() =>
            {
                using var ms = new MemoryStream(bytes);
                return new Bitmap(ms);
            });
            await Dispatcher.UIThread.InvokeAsync(() => Avatar = bmp);
        }
        catch { /* non-fatal */ }
    }
}

public partial class ArtworkCardViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isFollowed;
    [ObservableProperty] private bool _isCurrentArtist;
    [ObservableProperty] private bool _isLocalFavorite;
    [ObservableProperty] private bool _isPixivBookmarked;
    [ObservableProperty] private bool _isPixivPrivateBookmark;
    [ObservableProperty] private string? _pixivBookmarkId;
    /// <summary>True when this artwork's ID is in <c>SettingsService.PixivLikedArtworkIds</c> —
    /// drives the heart badge shown on the thumbnail.</summary>
    [ObservableProperty] private bool _isLiked;

    /// <summary>
    /// When true, the thumbnail is blurred (for R-18 content when blur setting is enabled).
    /// Single click toggles this off, double click opens viewer.
    /// </summary>
    [ObservableProperty] private bool _isBlurred;

    public ArtworkPreview Artwork { get; }
    public string Id { get; }
    public string Title { get; }
    public string UserName { get; }
    public string UserId { get; }
    public string? ThumbnailUrl { get; }
    /// <summary>Optional caption shown under the card. Only set by Pixivision, where pixivision's
    /// own editorial caption for an embedded artwork gets attached here.</summary>
    public string? Caption { get; set; }
    public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);
    /// <summary>Optional "viewed at" label shown under the card. Only set by the Viewed tab.</summary>
    public string? ViewedAtLabel { get; set; }
    public bool HasViewedAt => !string.IsNullOrWhiteSpace(ViewedAtLabel);
    public string TypeLabel { get; }
    public int PageCount { get; }
    public int IllustType { get; }
    public bool IsMultiPage => PageCount > 1;
    [ObservableProperty] private double _aspectRatio;
    partial void OnAspectRatioChanged(double value) => OnPropertyChanged(nameof(ClampedAspectRatio));
    /// <summary>Aspect ratio used for natural-height layout. Lightly clamped (0.25 - 5.0)
    /// to guard against degenerate/zero values while preserving the full image proportions.</summary>
    public double ClampedAspectRatio => Math.Min(Math.Max(AspectRatio, 0.25), 5.0);
    public bool IsR18 { get; }
    public bool IsR18G { get; }
    public bool IsAi { get; }
    public List<string> Tags { get; }
    public IReadOnlyList<string> TopTags => Tags.Count > 3 ? Tags.GetRange(0, 3) : Tags;
    public DateTime DateCreated { get; }
    public bool HasDate => DateCreated != DateTime.MinValue;
    public string DateLabel => HasDate ? DateCreated.ToString("MMM d, yyyy") : string.Empty;
    public int BookmarkCount { get; }
    public int LikeCount { get; }
    public int ViewCount { get; }
    public int? ViewerPosition { get; set; }
    /// <summary>Height for natural mode: CardSize * AspectRatio.</summary>
    public double NaturalHeight(double width) => width * AspectRatio;

    public ArtworkCardViewModel(ArtworkPreview artwork)
    {
        Artwork = artwork;
        Id = artwork.Id;
        Title = artwork.Title;
        UserName = artwork.UserName;
        UserId = artwork.UserId;
        ThumbnailUrl = GetHighQualityThumbnailUrl(artwork.ThumbnailUrl);
        TypeLabel = artwork.TypeLabel;
        PageCount = artwork.PageCount;
        IllustType = artwork.IllustType;
        _aspectRatio = artwork.AspectRatio;
        IsR18 = artwork.IsR18;
        IsR18G = artwork.IsR18G;
        IsAi = artwork.IsAiGenerated;
        Tags = artwork.Tags?.ToList() ?? [];
        DateCreated = artwork.CreateDate?.DateTime ?? DateTime.MinValue;
        BookmarkCount = artwork.BookmarkCount ?? 0;
        LikeCount = artwork.LikeCount ?? 0;
        ViewCount = artwork.ViewCount ?? 0;
        // Centralized here (rather than at every call site) so the Liked heart badge shows up
        // consistently everywhere a card is built — Gallery, Discover, Rankings, Pixivision,
        // Search, Viewed History, Hoshi, etc. — without having to touch each of those call
        // sites individually. This is just a local list lookup, not a network call, so it's
        // cheap to do unconditionally.
        try { _isLiked = AppServices.Get<SettingsService>().Current.PixivLikedArtworkIds.Contains(Id); }
        catch { /* AppServices not initialized yet, e.g. design-time */ }
        // Same reasoning as IsLiked above — local favorites are a purely local, in-memory
        // JSON-backed lookup (no network call), so it's cheap to check unconditionally here
        // and get the yellow star badge showing correctly everywhere without touching every
        // place a card gets constructed.
        try { _isLocalFavorite = AppServices.Get<Pikura.Core.Services.LocalFavoritesService>().IsFavorite(Id); }
        catch { /* AppServices not initialized yet, e.g. design-time */ }
        // Bookmark status genuinely can't be cheaply centralized the same way (it's not stored
        // locally, only known via a live Pixiv check) — but once the Public/Private bookmark
        // tabs have been loaded at least once this session (or a bookmark add/remove has
        // happened), BookmarksViewModel keeps a fast lookup cache of known-bookmarked IDs that
        // any newly-constructed card can piggyback on for free.
        try
        {
            _isPixivBookmarked = AppServices.Get<BookmarksViewModel>().IsKnownBookmarked(Id, out var isPrivate);
            _isPixivPrivateBookmark = isPrivate;
        }
        catch { /* AppServices/BookmarksViewModel not initialized yet, e.g. design-time */ }
    }

    /// <summary>
    /// Upgrades a Pixiv thumbnail URL to master1200 (aspect-ratio preserving).
    /// Strips any "/c/{W}x{H}_..." size prefix so we get the raw master1200 file
    /// from /img-master/, which is at most 1200px on the long edge AND preserves
    /// the original aspect ratio — matches what RankingCardViewModel uses.
    /// </summary>
    private static string? GetHighQualityThumbnailUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        // Pixiv serves the same artwork from several path prefixes:
        //   /custom-thumb/img/...   <id>_p0_custom1200.jpg   (square social-share crop)
        //   /img-master/img/...     <id>_p0_master1200.jpg   (aspect-preserved, max 1200)
        //   /c/{W}x{H}_.../img-master/...                    (server-side resize/crop)
        // We want the img-master master1200 variant while keeping the /c/ resize
        // directive so PixivImageLoader can later swap it to the requested size.
        var upgraded = url;
        if (upgraded.Contains("/custom-thumb/"))
        {
            upgraded = upgraded.Replace("/custom-thumb/", "/img-master/")
                               .Replace("_custom1200", "_master1200");
        }
        upgraded = upgraded.Replace("_square1200", "_master1200");
        return upgraded;
    }

    public async Task LoadThumbnailAsync(PixivImageLoader loader, ThumbnailSize size = ThumbnailSize.Medium, CancellationToken ct = default)
    {
        var preferred = ThumbnailUrl;
        var fallback = Artwork.ThumbnailUrl;
        if (string.IsNullOrWhiteSpace(preferred) && string.IsNullOrWhiteSpace(fallback)) return;
        try
        {
            // Default to Medium (_master1200, ≤540px long edge, preserves aspect ratio)
            // so natural-height cards match the real image proportions and fixed-height
            // cards still get a high-quality center-crop. Small (_square1200, 250×250)
            // is reserved for callers that explicitly want the tiny square crop.
            var effectiveSize = size;

            SKBitmap? skBitmap = null;
            if (!string.IsNullOrWhiteSpace(preferred))
                skBitmap = await loader.FetchBitmapAsync(preferred, effectiveSize, ct);
            if (skBitmap is null && !string.IsNullOrWhiteSpace(fallback)
                && !string.Equals(preferred, fallback, StringComparison.OrdinalIgnoreCase))
                skBitmap = await loader.FetchBitmapAsync(fallback, effectiveSize, ct);
            if (skBitmap is null || ct.IsCancellationRequested) return;

            // Fast SKBitmap → Avalonia Bitmap conversion via direct pixel copy
            // (avoids PNG encode/decode roundtrip — ~10× faster for thumbnails).
            var bmp = await Task.Run(() =>
                (Bitmap?)Pikura.Avalonia.Services.BitmapInterop.SkiaToAvalonia(skBitmap), ct);

            skBitmap.Dispose(); // Dispose the copy we received

            if (bmp is not null && !ct.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Thumbnail = bmp;
                    // Sync AspectRatio to the loaded bitmap so the natural-height card box
                    // matches the drawn thumbnail. Only update when the difference is large
                    // enough to be visible — avoids cascading MasonryPanel re-measures for
                    // sub-pixel changes as cards load in parallel.
                    if (effectiveSize != ThumbnailSize.Small
                        && bmp.PixelSize.Width > 0 && bmp.PixelSize.Height > 0)
                    {
                        var ratio = (double)bmp.PixelSize.Height / bmp.PixelSize.Width;
                        if (ratio > 0 && Math.Abs(ratio - AspectRatio) > 0.05)
                            AspectRatio = ratio;
                    }
                });
        }
        catch (OperationCanceledException) { /* superseded by artist change */ }
        catch { /* non-fatal network/decode error */ }
    }
}
