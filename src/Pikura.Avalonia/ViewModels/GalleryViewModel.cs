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

public partial class ViewerTab : ObservableObject
{
    [ObservableProperty] private ArtworkCardViewModel _card;
    [ObservableProperty] private string _header;

    /// <summary>The ordered list this tab navigates through (artist gallery, ranking page, etc.).</summary>
    public List<ArtworkCardViewModel> NavList { get; init; } = [];

    /// <summary>True total from the source (e.g. artist's full catalogue count), may exceed NavList.Count.</summary>
    public int TotalCount { get; set; }

    /// <summary>Optional callback to load more cards into NavList when navigating past the loaded edge.</summary>
    public Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? LoadMoreAsync { get; set; }

    /// <summary>Which section opened this tab ("Gallery", "Discover", "Rankings", etc.). Informational only — tabs are global across all sections.</summary>
    public string Source { get; init; } = "Gallery";

    public ViewerTab(ArtworkCardViewModel card, IReadOnlyList<ArtworkCardViewModel>? navList = null,
        int totalCount = 0, Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMoreAsync = null,
        string source = "Gallery")
    {
        _card = card;
        _header = card.Title.Length > 24 ? card.Title[..24] + "…" : card.Title;
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

    private int _loadingArtistsGuard;
    private bool _suppressArtistChanged;
    private List<string> _currentArtistAllIds = [];
    private int _currentArtistLoadedCount;
    private CancellationTokenSource? _artworkLoadCts;
    // Cache: artistUserId -> loaded card list (avoids re-fetching on back navigation)
    private readonly Dictionary<string, (List<ArtworkCardViewModel> Cards, List<string> AllIds, int TotalIds, int LoadedCount, bool CanMore)> _artworkCache = [];
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
    [ObservableProperty] private bool _showFilters;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _isRecentFeedActive;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private double _browsePanelWidth = 380;
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

    /// <summary>The single global tab collection — every section shows the same tabs.</summary>
    public ObservableCollection<ViewerTab> ViewerTabs { get; } = [];
    [ObservableProperty] private ViewerTab? _selectedViewerTab;

    private void OnViewerTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsInlineViewerOpen));
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(HasMultipleTabs));
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
        _showPreview = s.ShowPreview;
        _browsePanelWidth = s.BrowsePanelWidth >= 200 ? s.BrowsePanelWidth : 380;
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

    partial void OnInlineViewerCardChanged(ArtworkCardViewModel? value)
    {
        OnPropertyChanged(nameof(IsInlineViewerOpen));
    }

    partial void OnSelectedViewerTabChanged(ViewerTab? value)
    {
        InlineViewerCard = value?.Card;
        // Keep InlineViewerCardList in sync so the counter works for non-tab viewers too
        InlineViewerCardList = value?.NavList.Count > 0 ? value.NavList : null;
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
            if (string.IsNullOrEmpty(q))
            {
                // Filter cleared — full rebuild to ensure no stale filtered subset remains.
                FilteredArtists.Clear();
                foreach (var a in Artists)
                    FilteredArtists.Add(a);
            }
            else
            {
                // Filter text changed — full rebuild is necessary.
                FilteredArtists.Clear();
                foreach (var a in Artists)
                    if (a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || a.UserId.Contains(q, StringComparison.OrdinalIgnoreCase))
                        FilteredArtists.Add(a);
            }

            // Restore selection without re-triggering artwork load
            if (saved != null && FilteredArtists.Contains(saved))
                SelectedArtist = saved;
        }
        finally
        {
            _suppressArtistChanged = false;
        }
    }

    public void RebuildFilteredArtworks()
    {
        var inc = TagIncludeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exc = TagExcludeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Global excluded tags from settings
        var globalExc = _settingsService.Current.ExcludedTags;
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
        // Global excluded tags from settings - always active
        if (globalExc.Count > 0)
            src = src.Where(a => !globalExc.Any(t => a.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase))));
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

        // Perform global Pixiv search for this tag
        StatusMessage = $"Searching for tag: {tag}...";
        IsLoading = true;
        IsRecentFeedActive = false;

        try
        {
            var results = await _pixivClient.SearchArtworksAsync(
                tag,
                mode: ShowR18 ? "all" : "safe",
                ct: cts.Token);

            var artworks = results?.IllustManga.Data;
            if (artworks is null || artworks.Count == 0)
            {
                StatusMessage = $"No results found for tag: {tag}";
                return;
            }

            // Clear current view
            VisibleArtworks.Clear();
            SelectedArtist = null; // No specific artist for tag search
            _currentArtistAllIds = [];
            _currentArtistLoadedCount = 0;
            CanLoadMore = false;
            ArtworksTotal = artworks.Count;

            // Prepare batch of artwork cards
            var batch = new List<ArtworkCardViewModel>();
            foreach (var preview in artworks)
            {
                if (!ShowR18 && preview.IsR18) continue;
                batch.Add(new ArtworkCardViewModel(preview)
                {
                    IsFollowed = IsArtistFollowed(preview.UserId),
                    IsBlurred = _settingsService.Current.BlurR18Content && preview.IsR18
                });
            }

            // Add batch to UI
            AddArtworkCardsBatch(batch);

            // Show info bar with search context
            ShowSearchInfo = true;
            SearchInfoText = $"Tag: {tag} • {VisibleArtworks.Count} results";

            StatusMessage = $"Found {VisibleArtworks.Count} artworks for tag: {tag}";
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

    [RelayCommand]
    private async Task SearchByIdAsync()
    {
        var raw = IdSearchQuery.Trim();
        if (string.IsNullOrEmpty(raw)) return;

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
            // otherwise = tag search
            else
            {
                await SearchByTagAsync(raw);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Search failed: " + ex.Message;
        }
        finally { IsLoading = false; }
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
        // First, check if already in followed artists list — if so, just select
        var existing = Artists.FirstOrDefault(a => a.UserId == userId);
        if (existing != null)
        {
            SelectedArtist = existing;
            StatusMessage = $"Loaded artist {existing.Name}";
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
    }

    private async Task LoadArtworkByIdAsync(string artworkId)
    {
        var b = await _pixivClient.GetArtworkDetailAsync(artworkId);
        if (b == null) { StatusMessage = $"Artwork {artworkId} not found."; return; }
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
        OpenInlineViewer(vm);
        StatusMessage = $"Viewing artwork {artworkId}";
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
                        RebuildFilteredArtists();
                    });
                }
            }
            Logger.LogInformation("[FollowedArtists] After first pages: realTotal={RealTotal} seen={Seen}", realTotal, seen.Count);

            // Step 2: Fetch remaining pages for public and private.
            await FetchRemainingFollowedPagesAsync(userId, firstPages, limit, seen, seenLock);

            ArtistsTotal = realTotal > 0 ? realTotal : Artists.Count;
            Logger.LogInformation("[FollowedArtists] Load complete: Artists.Count={Count} ArtistsTotal={Total}", Artists.Count, ArtistsTotal);
            ArtistsLoaded = true;
            if (!IsLoading)
                StatusMessage = $"{Artists.Count} followed artists";
            RebuildFilteredArtists();
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

            ArtistsTotal = realTotal > 0 ? realTotal : Artists.Count;
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
                RebuildFilteredArtists();
                
                // Start avatar loading after UI update
                foreach (var vm in batch)
                {
                    _ = vm.LoadAvatarAsync(_imageLoader);
                }
            });
        }

        var tasks = new List<Task>();
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
                    Logger.LogInformation("[FollowedArtists] Parallel fetch hidden={Hidden}: {PageCount} pages for total={Total}",
                        hiddenCapture, offsets.Count, totalBound);
                    tasks.Add(Task.WhenAll(offsets.Select(offset => Task.Run(async () =>
                    {
                        try
                        {
                            var page = await _pixivClient.GetFollowedArtistsAsync(userId, offset, limit, hiddenCapture);
                            if (page?.Users?.Count > 0)
                                await AddBatchAsync(page.Users);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex, "Followed-artists fetch failed at offset {Off}", offset);
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
                            catch (Exception ex) { Logger.LogDebug(ex, "Followed-artists fetch failed at offset {Off}", offset); break; }
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

    // ─── Follow / unfollow ──────────────────────────────────────────────

    /// <summary>True when the given user id is in our followed-artists list.</summary>
    public bool IsArtistFollowed(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        return Artists.Any(a => a.UserId == userId);
    }

    private void UpdateCache(ArtistCardViewModel artist)
    {
        _artworkCache[artist.UserId] = (
            VisibleArtworks.ToList(),
            _currentArtistAllIds.ToList(),
            ArtworksTotal,
            _currentArtistLoadedCount,
            CanLoadMore);
    }

    /// <summary>
    /// After loading more artworks, sync the active viewer tab's NavList and TotalCount
    /// so the "X / Y" counter and prev/next navigation reflect the new cards.
    /// </summary>
    private void SyncViewerTabNavList()
    {
        if (SelectedViewerTab is not { } tab) return;
        if (tab.Source != "Gallery") return;
        var current = FilteredArtworks.ToList();
        tab.NavList.Clear();
        foreach (var c in current) tab.NavList.Add(c);
        tab.TotalCount = ArtworksTotal > current.Count ? ArtworksTotal : current.Count;
        _navListVersion++;
        OnPropertyChanged(nameof(NavListVersion));
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var a in VisibleArtworks) a.IsSelected = false;
        SelectedCount = 0;
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
        var list = navList ?? FilteredArtworks.ToList();
        // Total is the actual count of navigable items (not the artist's announced catalogue size)
        int total = totalCount > 0 ? totalCount : list.Count;
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

        // Plain click: always replace the current tab in-place (if any), otherwise create one.
        ViewerSource = source;
        if (SelectedViewerTab is { } active)
        {
            active.NavList.Clear();
            foreach (var c in list) active.NavList.Add(c);
            active.TotalCount = total;          // refresh counter to match new nav list
            active.LoadMoreAsync = loadMore;    // refresh load-more callback for new context
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

    public void OpenInNewTab(ArtworkCardViewModel card, IReadOnlyList<ArtworkCardViewModel>? navList = null,
        int totalCount = 0, Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMoreAsync = null,
        string source = "Gallery")
    {
        ViewerSource = source;
        // Snapshot filtered artworks so navigating to another artist doesn't mutate this tab's list
        var list = navList ?? FilteredArtworks.ToList();

        // Total is the actual count of navigable items (not the artist's announced catalogue size)
        int total = totalCount > 0 ? totalCount : list.Count;
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

        // "Open in new tab" always creates a new global tab
        var tab = new ViewerTab(card, list, total, loadMore, source);
        ViewerTabs.Add(tab);
        SelectedViewerTab = tab;
        InlineViewerCard = card;
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
            _ = LoadArtistArtworksAsync(value);
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
            var profile = await _pixivClient.GetUserProfileAllAsync(artist.UserId);
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

            // Continue loading page 2 in the background. The cache is updated only
            // after both pages finish so that revisits restore the full 96-card view.
            if (CanLoadMore)
            {
                await LoadArtworkPageAsync(artist, append: true, ct);
                ct.ThrowIfCancellationRequested();
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
                IsLoading = false;
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

        if (approved.Count == 0) { StatusMessage = "All artworks skipped."; return; }

        var total = approved.Count;
        var done = 0;
        var failed = 0;
        var maxConcurrent = Math.Max(1, acctOverride.MaxConcurrentDownloads ?? _settingsService.Current.MaxConcurrentDownloads);
        using var gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        string? outputFolder = null;

        var targets = approved.Select(c => new DownloadTarget
        {
            TargetId = c.Id, Name = c.Title, ThumbnailUrl = c.ThumbnailUrl, UserName = c.UserName, UserId = c.UserId, Type = TargetType.Artwork, Status = TargetStatus.Pending
        }).ToList();

        var artistPrefix = SelectedArtist != null ? $"{SelectedArtist.Name}: " : "";
        var jobName = approved.Count == 1 ? $"{artistPrefix}{approved[0].Title}" : $"{artistPrefix}{approved.Count} artworks (with preset)";
        var activeJob = await _coordinator.CreateJobAsync(
            DownloadJobType.ImageId, jobName, targets,
            settingsOverride: acctOverride, startImmediately: false,
            initialStatusOverride: JobStatus.Running);

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
                var files = await _downloader.DownloadArtworkAsync(card.Artwork, progress, ct, overrideSettings: acctOverride);
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

    private async Task DownloadCoreAsync(IReadOnlyList<ArtworkCardViewModel> cards)
    {
        if (cards.Count == 0) return;

        var acctOverride = BuildAccountSettingsOverride();

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

        var artistPrefix = SelectedArtist != null ? $"{SelectedArtist.Name}: " : "";
        var jobName = approved.Count == 1 ? $"{artistPrefix}{approved[0].Title}" : $"{artistPrefix}{approved.Count} artworks";
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
                var files = await _downloader.DownloadArtworkAsync(card.Artwork, progress, ct, overrideSettings: acctOverride);
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
    public string? ProfileImageUrl { get; }
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
    public string TypeLabel { get; }
    public int PageCount { get; }
    public int IllustType { get; }
    public bool IsMultiPage => PageCount > 1;
    [ObservableProperty] private double _aspectRatio;
    partial void OnAspectRatioChanged(double v) => OnPropertyChanged(nameof(ClampedAspectRatio));
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
        // We want the master1200 variant for natural-aspect display. If the URL
        // points at /custom-thumb/, both the prefix AND the filename suffix have
        // to be rewritten — a partial rewrite produces a 404.
        var upgraded = url;
        if (upgraded.Contains("/custom-thumb/"))
        {
            upgraded = upgraded.Replace("/custom-thumb/", "/img-master/")
                               .Replace("_custom1200", "_master1200");
        }
        upgraded = upgraded.Replace("_square1200", "_master1200");

        // Strip any "c/<WxH>_..." crop/resize prefix so we get the raw
        // /img-master/ master1200 file (aspect-preserved, max 1200 long edge).
        upgraded = System.Text.RegularExpressions.Regex.Replace(
            upgraded, @"/c/\d+x\d+[^/]*/", "/");
        return upgraded;
    }

    public async Task LoadThumbnailAsync(PixivImageLoader loader, ThumbnailSize size = ThumbnailSize.Medium, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl)) return;
        try
        {
            // Default to Medium (_master1200, ≤540px long edge, preserves aspect ratio)
            // so natural-height cards match the real image proportions and fixed-height
            // cards still get a high-quality center-crop. Small (_square1200, 250×250)
            // is reserved for callers that explicitly want the tiny square crop.
            var effectiveSize = size;

            var skBitmap = await loader.FetchBitmapAsync(ThumbnailUrl, effectiveSize, ct);
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
