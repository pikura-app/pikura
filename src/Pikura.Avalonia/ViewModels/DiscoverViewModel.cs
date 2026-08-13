using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

public partial class DiscoverViewModel : ViewModelBase
{
    private readonly PixivClient _pixivClient;
    private readonly PixivImageLoader _imageLoader;
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _usersCts;
    private CancellationTokenSource? _artistWorksCts;
    private DateTime _lastLoaded = DateTime.MinValue;
    private bool _isLoadingWorks;
    private bool _isLoadingUsers;
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(30);

    // ── Status ──────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingArtistWorks;
    [ObservableProperty] private bool _isBulkDownloading;
    partial void OnIsLoadingArtistWorksChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoArtistPlaceholder));
        OnPropertyChanged(nameof(ShowNoWorksPlaceholder));
    }
    [ObservableProperty] private string _statusMessage = "Discover new artwork and artists";
    [ObservableProperty] private int _queuedArtistCount;

    // ── Tab state ────────────────────────────────────────────────────────────

    [ObservableProperty] private int _selectedTabIndex;

    public bool IsWorksTab => SelectedTabIndex == 0;
    public bool IsUsersTab => SelectedTabIndex == 1;

    // ── Works ────────────────────────────────────────────────────────────────

    [ObservableProperty] public ObservableCollection<ArtworkCardViewModel> _recommendedWorks = new();
    [ObservableProperty] private bool _canLoadMoreRecommended;
    [ObservableProperty] private int _recommendedOffset;

    public bool HasRecommendedWorks => RecommendedWorks.Count > 0;

    // Selection tracking for multi-select download
    [ObservableProperty] private int _selectedCount;
    public bool HasSelection => SelectedCount > 0;
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));

    // Filtered view (tag filter applied)
    public ObservableCollection<ArtworkCardViewModel> FilteredWorks { get; } = [];

    [ObservableProperty] private string _tagFilter = string.Empty;
    partial void OnTagFilterChanged(string value) => UpdateFilteredWorks();

    public void UpdateFilteredWorks()
    {
        // Rebuild recommended works filter
        var src = RecommendedWorks.AsEnumerable();
        if (!string.IsNullOrEmpty(TagFilter))
            src = src.Where(a => a.Tags.Any(t => t.Contains(TagFilter, StringComparison.OrdinalIgnoreCase)));
        if (!ShowR18)
            src = src.Where(a => !a.Artwork.IsR18);
        src = src.Where(a => !_settingsService.Current.IsArtworkHidden("Discover", a.UserId, a.UserName, a.Title, a.Tags));
        FilteredWorks.Clear();
        foreach (var a in src) FilteredWorks.Add(a);

        // Rebuild artist works filter
        var artistSrc = ArtistWorks.AsEnumerable();
        if (!ShowR18)
            artistSrc = artistSrc.Where(a => !a.Artwork.IsR18);
        artistSrc = artistSrc.Where(a => !_settingsService.Current.IsArtworkHidden("Discover", a.UserId, a.UserName, a.Title, a.Tags));
        FilteredArtistWorks.Clear();
        foreach (var a in artistSrc) FilteredArtistWorks.Add(a);
        OnPropertyChanged(nameof(HasArtistWorks));
        OnPropertyChanged(nameof(ShowNoArtistPlaceholder));
        OnPropertyChanged(nameof(ShowNoWorksPlaceholder));
    }

    /// <summary>
    /// Pushes an updated Liked/Bookmarked/Local-favorite flag onto every currently-loaded card
    /// with a matching artwork ID (in both RecommendedWorks and ArtistWorks — FilteredWorks/
    /// FilteredArtistWorks share the same card instances, so they pick up the change too).
    /// Called by the artwork viewer right after a Like/Bookmark/Favorite action succeeds.
    /// </summary>
    /// <summary>
    /// Re-checks Liked/Bookmarked/Local-favorite status for every currently-loaded card (see
    /// GalleryViewModel.RefreshLikedBookmarkedFavoriteFlags for the full rationale — the live
    /// push in SyncArtworkFlags only reaches a card that's already loaded at the moment the
    /// action happens). Call this whenever navigating back to Discover.
    /// </summary>
    public void RefreshLikedBookmarkedFavoriteFlags()
    {
        Pikura.Core.Settings.SettingsService settings;
        Pikura.Core.Services.LocalFavoritesService favorites;
        BookmarksViewModel? bookmarksVm = null;
        try { settings = AppServices.Get<Pikura.Core.Settings.SettingsService>(); } catch { return; }
        try { favorites = AppServices.Get<Pikura.Core.Services.LocalFavoritesService>(); } catch { return; }
        try { bookmarksVm = AppServices.Get<BookmarksViewModel>(); } catch { /* not initialized yet */ }

        foreach (var c in RecommendedWorks.Concat(ArtistWorks))
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
        foreach (var c in RecommendedWorks.Concat(ArtistWorks))
        {
            if (c.Id != id) continue;
            if (isLiked.HasValue) c.IsLiked = isLiked.Value;
            if (isPixivBookmarked.HasValue) c.IsPixivBookmarked = isPixivBookmarked.Value;
            if (isPixivPrivateBookmark.HasValue) c.IsPixivPrivateBookmark = isPixivPrivateBookmark.Value;
            if (bookmarkIdProvided) c.PixivBookmarkId = pixivBookmarkId;
            if (isLocalFavorite.HasValue) c.IsLocalFavorite = isLocalFavorite.Value;
        }
    }

    // ── Users ────────────────────────────────────────────────────────────────

    [ObservableProperty] public ObservableCollection<DiscoveryUserCardViewModel> _recommendedUsers = new();
    [ObservableProperty] private bool _canLoadMoreUsers;
    [ObservableProperty] private int _usersOffset;

    public bool HasRecommendedUsers => RecommendedUsers.Count > 0;

    // Two-column user layout: selected user + their works
    [ObservableProperty] private DiscoveryUserCardViewModel? _selectedUser;
    [ObservableProperty] public ObservableCollection<ArtworkCardViewModel> _artistWorks = new();
    public ObservableCollection<ArtworkCardViewModel> FilteredArtistWorks { get; } = [];
    public bool HasArtistWorks => FilteredArtistWorks.Count > 0;
    public bool HasSelectedUser => SelectedUser != null;
    public bool ShowNoArtistPlaceholder => !HasSelectedUser && !IsLoadingArtistWorks;
    public bool ShowNoWorksPlaceholder => HasSelectedUser && !IsLoadingArtistWorks && !HasArtistWorks;

    partial void OnSelectedUserChanged(DiscoveryUserCardViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedUser));
        OnPropertyChanged(nameof(ShowNoArtistPlaceholder));
        OnPropertyChanged(nameof(ShowNoWorksPlaceholder));
        if (value != null)
            _ = LoadArtistWorksAsync(value.UserId);
    }

    // ── View options ─────────────────────────────────────────────────────────

    [ObservableProperty] private int _cardSize = 180;
    [ObservableProperty] private bool _isFixedHeight = true;
    [ObservableProperty] private bool _isNaturalHeight;
    [ObservableProperty] private bool _isGridView = true;
    [ObservableProperty] private bool _isListView;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showBadges = true;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private bool _showR18 = true;
    [ObservableProperty] private double _browsePanelWidth = 390;

    public bool ShowR18Buttons => _settingsService?.Current.R18Mode != Pikura.Core.Settings.R18Mode.Off;

    public bool IsResizingPanel { get; set; }
    public bool IsDiscoverViewerOpen => GalleryVm.HasTabs;
    [ObservableProperty] private bool _isViewerExpanded;
    partial void OnIsViewerExpandedChanged(bool value) { OnPropertyChanged(nameof(IsViewerFullScreen)); }
    public bool IsViewerFullScreen => IsViewerExpanded;
    public double FixedCardTotalHeight => CardSize;

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    public void CopyAllArtistIds()
    {
        var flushed = QuickClipboardService.FlushArtistIds();
        if (flushed == null)
        {
            StatusMessage = "No artist IDs queued — click individual artist IDs first.";
            return;
        }
        var count = flushed.Split(',').Length;
        CopyAllArtistIdsRequested?.Invoke(flushed);
        StatusMessage = $"Copied {count} queued artist ID{(count == 1 ? "" : "s")} to clipboard";
    }

    public event Action<string>? CopyAllArtistIdsRequested;

    [RelayCommand] public void SetFixedHeight()   { IsFixedHeight = true;  IsNaturalHeight = false; }
    [RelayCommand] public void SetNaturalHeight() { IsFixedHeight = false; IsNaturalHeight = true; }
    [RelayCommand] public void SetGridView()  { IsGridView = true;  IsListView = false; }
    [RelayCommand] public void SetListView()  { IsGridView = false; IsListView = true; }

    // ── Selection & Download ───────────────────────────────────────────────

    public void NotifySelectionChanged()
    {
        SelectedCount = RecommendedWorks.Count(c => c.IsSelected) + ArtistWorks.Count(c => c.IsSelected);
        OnPropertyChanged(nameof(CanViewSelectedAsCollage));
        ViewSelectedAsCollageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanViewSelectedInNewTabs));
        ViewSelectedInNewTabsCommand.NotifyCanExecuteChanged();
    }

    public bool CanViewSelectedAsCollage => GalleryVm.CanCollage(SelectedCount);

    [RelayCommand(CanExecute = nameof(CanViewSelectedAsCollage))]
    private void ViewSelectedAsCollage()
    {
        var works = IsWorksTab ? RecommendedWorks : ArtistWorks;
        var selected = works.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;
        GalleryVm.AddSelectedToCollage(selected);
    }

    public bool CanViewSelectedInNewTabs => SelectedCount >= 1;

    [RelayCommand(CanExecute = nameof(CanViewSelectedInNewTabs))]
    private void ViewSelectedInNewTabs()
    {
        var works = IsWorksTab ? RecommendedWorks : ArtistWorks;
        var selected = works.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;
        foreach (var card in selected)
            GalleryVm.OpenInNewTab(card, selected, selected.Count, source: "Discover");
        ShowPreview = true;
    }

    [RelayCommand]
    public void SelectAll()
    {
        var works = IsWorksTab ? RecommendedWorks : ArtistWorks;
        foreach (var c in works) c.IsSelected = true;
        NotifySelectionChanged();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        var works = IsWorksTab ? RecommendedWorks : ArtistWorks;
        foreach (var c in works) c.IsSelected = false;
        SelectedCount = 0;
    }

    [RelayCommand]
    public Task DownloadSelectedAsync()
    {
        var works = IsWorksTab ? RecommendedWorks : ArtistWorks;
        var previews = works.Where(c => c.IsSelected).Select(c => c.Artwork).ToList();
        if (previews.Count == 0) return Task.CompletedTask;
        return GalleryVm.DownloadPreviewsAsync(previews);
    }

    [RelayCommand]
    public Task DownloadVisibleAsync()
    {
        var previews = IsWorksTab
            ? FilteredWorks.Select(c => c.Artwork).ToList()
            : FilteredArtistWorks.Select(c => c.Artwork).ToList();
        if (previews.Count == 0) return Task.CompletedTask;
        return GalleryVm.DownloadPreviewsAsync(previews);
    }

    [RelayCommand]
    public async Task DownloadAllAsync()
    {
        if (SelectedUser == null || IsBulkDownloading) return;
        
        IsBulkDownloading = true;
        try
        {
            // Get all artwork IDs for the selected user (same as LoadArtistWorksAsync)
            var profile = await _pixivClient.GetUserProfileAllAsync(SelectedUser.UserId);
            var allIds = profile.AllArtworkIds();
            if (allIds.Count == 0)
            {
                StatusMessage = "No artworks found for this artist.";
                return;
            }

            // Fetch all artwork metadata in batches
            var allPreviews = new List<ArtworkPreview>();
            const int batchSize = 48;
            
            for (int i = 0; i < allIds.Count; i += batchSize)
            {
                var batchIds = allIds.Skip(i).Take(batchSize).ToList();
                var batch = await _pixivClient.GetArtworksMetadataAsync(SelectedUser.UserId, batchIds);
                if (batch != null)
                {
                    allPreviews.AddRange(batch.Values);
                }
                
                // Update status to show progress
                StatusMessage = $"Fetching artwork metadata... {Math.Min(i + batchSize, allIds.Count)}/{allIds.Count}";
            }

            // Download all previews
            if (allPreviews.Count > 0)
            {
                await GalleryVm.DownloadPreviewsAsync(allPreviews);
                StatusMessage = $"Queued {allPreviews.Count} artworks for download.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to fetch all artworks: {ex.Message}";
        }
        finally
        {
            IsBulkDownloading = false;
        }
    }

    partial void OnCardSizeChanged(int value)
    {
        OnPropertyChanged(nameof(FixedCardTotalHeight));
        if (_settingsService != null && _settingsService.Current.CardSize != value)
            _settingsService.Update(s => s.CardSize = value);
    }
    partial void OnIsFixedHeightChanged(bool value) => _settingsService?.Update(s => s.DiscoverCardHeightMode = value ? "Fixed" : "Natural");
    partial void OnIsGridViewChanged(bool value)    => _settingsService?.Update(s => s.DiscoverViewMode = value ? "Grid" : "List");
    partial void OnShowTagsChanged(bool value)      => _settingsService?.Update(s => s.DiscoverShowTags = value);
    partial void OnShowInfoChanged(bool value)      => _settingsService?.Update(s => s.DiscoverShowInfo = value);
    partial void OnShowBadgesChanged(bool value)    => _settingsService?.Update(s => s.ShowBadges = value);
    partial void OnShowPreviewChanged(bool value)   { _settingsService?.Update(s => s.DiscoverShowPreview = value); }
    partial void OnShowR18Changed(bool value)       { _settingsService?.Update(s => s.DiscoverShowR18 = value); UpdateFilteredWorks(); }

    // ── GalleryViewModel bridge ──────────────────────────────────────────────

    public GalleryViewModel GalleryVm { get; }

    // ── Constructor ──────────────────────────────────────────────────────────

    public DiscoverViewModel(PixivClient pixivClient, PixivImageLoader imageLoader, SettingsService settingsService, GalleryViewModel galleryVm)
    {
        _pixivClient = pixivClient;
        _imageLoader = imageLoader;
        _settingsService = settingsService;
        GalleryVm = galleryVm;
        GalleryVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GalleryViewModel.IsInlineViewerOpen) or nameof(GalleryViewModel.HasTabs))
            {
                OnPropertyChanged(nameof(IsDiscoverViewerOpen));
                if (!GalleryVm.HasTabs) IsViewerExpanded = false;
            }
            if (e.PropertyName is nameof(GalleryViewModel.IsCollageMode)
                               or nameof(GalleryViewModel.HasStoredCollage)
                               or nameof(GalleryViewModel.CollageItems)
                               or nameof(GalleryViewModel.CanViewSelectedAsCollage))
            {
                OnPropertyChanged(nameof(CanViewSelectedAsCollage));
                ViewSelectedAsCollageCommand.NotifyCanExecuteChanged();
            }
        };
        GalleryVm.ViewerTabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsDiscoverViewerOpen));
            if (!GalleryVm.HasTabs) IsViewerExpanded = false;
        };

        var s = settingsService.Current;
        _isFixedHeight    = s.DiscoverCardHeightMode != "Natural";
        _isNaturalHeight  = s.DiscoverCardHeightMode == "Natural";
        _isGridView       = s.DiscoverViewMode != "List";
        _isListView       = s.DiscoverViewMode == "List";
        _cardSize         = s.CardSize;
        _settingsService.Changed += (_, _) =>
        {
            var shared = _settingsService.Current.CardSize;
            if (CardSize != shared) CardSize = shared;
        };
        _showTags         = s.DiscoverShowTags;
        _showInfo         = s.DiscoverShowInfo;
        _showBadges       = s.ShowBadges;
        _showPreview      = s.DiscoverShowPreview;
        _showR18          = s.DiscoverShowR18;
        _browsePanelWidth = s.BrowsePanelWidth >= 200 ? s.BrowsePanelWidth : 390;

        QuickClipboardService.ClipboardChanged += () =>
        {
            QueuedArtistCount = QuickClipboardService.QueuedArtistCount;
        };
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    public void OnNavigatedTo()
    {
        // Raise tab/works visibility bindings so the view refreshes on re-entry
        OnPropertyChanged(nameof(IsWorksTab));
        OnPropertyChanged(nameof(IsUsersTab));
        OnPropertyChanged(nameof(HasRecommendedWorks));
        OnPropertyChanged(nameof(HasRecommendedUsers));
        OnPropertyChanged(nameof(HasArtistWorks));

        // Always rebuild FilteredWorks so the grid isn't blank on re-entry
        UpdateFilteredWorks();

        // Restore browse panel if a viewer tab is still open
        if (GalleryVm.HasTabs && !ShowPreview) ShowPreview = true;

        var isStale = _lastLoaded == DateTime.MinValue
            || DateTime.UtcNow - _lastLoaded > StaleThreshold
            || DateTime.UtcNow.Date > _lastLoaded.Date;
        if (!_isLoadingWorks && (RecommendedWorks.Count == 0 || isStale))
            _ = LoadRecommendedWorksAsync();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsWorksTab));
        OnPropertyChanged(nameof(IsUsersTab));
        switch (value)
        {
            case 0:
                if (RecommendedWorks.Count == 0 && !_isLoadingWorks)
                    _ = LoadRecommendedWorksAsync();
                break;
            case 1:
                if (RecommendedUsers.Count == 0 && !_isLoadingUsers)
                    _ = LoadRecommendedUsersAsync();
                break;
        }
    }

    [RelayCommand]
    private void SetTabIndex(object? param)
    {
        if (param is int i) SelectedTabIndex = i;
        else if (param is string s && int.TryParse(s, out var n)) SelectedTabIndex = n;
    }

    // ── Load recommended works ───────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadRecommendedWorksAsync()
    {
        if (_isLoadingWorks) return;
        _isLoadingWorks = true;
        IsLoading = true;
        StatusMessage = "Loading recommended works…";
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        RecommendedWorks.Clear();
        RecommendedOffset = 0;
        FilteredWorks.Clear();

        try
        {
            var response = await _pixivClient.GetDiscoveryArtworksAsync(RecommendedOffset, 48, ct);
            if (response?.Thumbnails?.Illust == null || response.Thumbnails.Illust.Count == 0)
            {
                StatusMessage = "No recommended works found.";
                return;
            }

            foreach (var illust in response.Thumbnails.Illust)
            {
                if (ct.IsCancellationRequested) break;
                var artwork = illust.ToArtworkPreview();
                var vm = new ArtworkCardViewModel(artwork)
                {
                    IsBlurred = _settingsService.Current.BlurR18Content && artwork.IsR18
                };
                RecommendedWorks.Add(vm);
                _ = vm.LoadThumbnailAsync(_imageLoader, ct: ct);
            }

            RecommendedOffset += response.Thumbnails.Illust.Count;
            // Endpoint returns fresh randomized recommendations per call regardless of
            // batch size, so always allow load-more.
            CanLoadMoreRecommended = true;
            StatusMessage = $"{RecommendedWorks.Count} recommended works";
            _lastLoaded = DateTime.UtcNow;
            OnPropertyChanged(nameof(HasRecommendedWorks));
            UpdateFilteredWorks();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load recommended works");
            StatusMessage = "Failed to load: " + ex.Message;
        }
        finally
        {
            _isLoadingWorks = false;
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task LoadMoreWorksAsync()
    {
        if (_isLoadingWorks || !CanLoadMoreRecommended) return;
        _isLoadingWorks = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var response = await _pixivClient.GetDiscoveryArtworksAsync(RecommendedOffset, 48, ct);
            if (response?.Thumbnails?.Illust == null || response.Thumbnails.Illust.Count == 0)
            {
                // Pixiv exhausted this round of recommendations; allow user to retry later
                // but do not permanently disable load-more — the endpoint produces fresh
                // recommendations on subsequent visits.
                StatusMessage = "No more new works right now — try refreshing.";
                return;
            }

            // Pixiv's /ajax/discovery/artworks returns fresh randomized recommendations
            // on each call (the offset parameter is ignored server-side). Track seen IDs
            // so we only append genuinely new items and keep load-more available.
            var existingIds = new HashSet<string>(
                RecommendedWorks.Select(w => w.Artwork.Id),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (var illust in response.Thumbnails.Illust)
            {
                if (ct.IsCancellationRequested) break;
                var artwork = illust.ToArtworkPreview();
                if (!existingIds.Add(artwork.Id)) continue; // duplicate
                var vm = new ArtworkCardViewModel(artwork)
                {
                    IsBlurred = _settingsService.Current.BlurR18Content && artwork.IsR18
                };
                RecommendedWorks.Add(vm);
                _ = vm.LoadThumbnailAsync(_imageLoader, ct: ct);
                added++;
            }

            RecommendedOffset += response.Thumbnails.Illust.Count;
            // Keep allowing load-more as long as the API responds with anything — Pixiv
            // returns more recommendations over time even after returning < 48 items.
            CanLoadMoreRecommended = true;
            StatusMessage = added > 0
                ? $"{RecommendedWorks.Count} recommended works"
                : "No new recommendations this round — try again.";
            OnPropertyChanged(nameof(HasRecommendedWorks));
            UpdateFilteredWorks();
            GalleryVm.SyncViewerTabs("Discover:Works", FilteredWorks.ToList());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.LogError(ex, "Failed to load more works"); }
        finally { _isLoadingWorks = false; }
    }

    // ── Load recommended users ───────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadRecommendedUsersAsync()
    {
        if (_isLoadingUsers) return;
        _isLoadingUsers = true;
        StatusMessage = "Loading recommended users…";
        _usersCts?.Cancel();
        _usersCts = new CancellationTokenSource();
        var ct = _usersCts.Token;

        RecommendedUsers.Clear();
        UsersOffset = 0;
        SelectedUser = null;
        ArtistWorks.Clear();

        try
        {
            var response = await _pixivClient.GetDiscoveryUsersAsync(UsersOffset, 48, ct);
            if (response?.Users == null || response.Users.Count == 0)
            {
                StatusMessage = "No recommended users found.";
                return;
            }

            foreach (var user in response.Users)
            {
                if (ct.IsCancellationRequested) break;
                var card = new DiscoveryUserCardViewModel(user);
                RecommendedUsers.Add(card);
                _ = card.LoadProfileImageAsync(_imageLoader, ct);
            }

            UsersOffset += response.Users.Count;
            CanLoadMoreUsers = response.Users.Count >= 48;
            StatusMessage = $"{RecommendedUsers.Count} recommended users";
            OnPropertyChanged(nameof(HasRecommendedUsers));

            // Auto-select first user to populate right panel
            if (RecommendedUsers.Count > 0)
                SelectedUser = RecommendedUsers[0];
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load recommended users");
            StatusMessage = "Failed to load recommended users: " + ex.Message;
        }
        finally { _isLoadingUsers = false; }
    }

    // ── Load artist works (Users tab right panel) ────────────────────────────

    private async Task LoadArtistWorksAsync(string userId)
    {
        _artistWorksCts?.Cancel();
        _artistWorksCts = new CancellationTokenSource();
        var ct = _artistWorksCts.Token;

        IsLoadingArtistWorks = true;
        ArtistWorks.Clear();
        FilteredArtistWorks.Clear();
        OnPropertyChanged(nameof(HasArtistWorks));

        try
        {
            // Step 1: get all illust+manga IDs via profile/all (same approach as Gallery)
            var profile = await _pixivClient.GetUserProfileAllAsync(userId, ct);
            ct.ThrowIfCancellationRequested();

            var allIds = profile.AllArtworkIds();
            if (allIds.Count == 0)
            {
                OnPropertyChanged(nameof(HasArtistWorks));
                return;
            }

            // Step 2: fetch metadata for first 48 IDs
            var batch = allIds.Take(48).ToList();
            var works = await _pixivClient.GetArtworksMetadataAsync(userId, batch, ct);
            ct.ThrowIfCancellationRequested();

            foreach (var id in batch)
            {
                if (ct.IsCancellationRequested) break;
                if (!works.TryGetValue(id, out var preview)) continue;
                var vm = new ArtworkCardViewModel(preview)
                {
                    IsBlurred = _settingsService.Current.BlurR18Content && preview.IsR18
                };
                ArtistWorks.Add(vm);
                _ = vm.LoadThumbnailAsync(_imageLoader, ct: ct);
            }

            UpdateFilteredWorks();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.LogError(ex, "Failed to load artist works for {UserId}", userId); }
        finally { IsLoadingArtistWorks = false; }
    }

    // ── Download with Preset ───────────────────────────────────────────────

    [RelayCommand]
    private async Task DownloadWithPresetAsync()
    {
        var artworks = ArtistWorks.Where(a => !string.IsNullOrEmpty(a.ThumbnailUrl)).ToList();
        if (artworks.Count == 0)
        {
            StatusMessage = "No artworks available to download.";
            return;
        }

        // Get first artwork for preview
        var first = artworks.First();
        var dialogService = Pikura.Avalonia.Services.AppServices.Get<DialogService>();
        var preset = await dialogService.ShowDownloadPresetDialogAsync(first.Artwork);

        if (preset == null)
        {
            StatusMessage = "Download with preset cancelled.";
            return;
        }

        // Use GalleryVm to download artworks with preset
        foreach (var artwork in artworks)
        {
            await GalleryVm.DownloadWithPresetAsync(artwork, preset);
        }

        StatusMessage = $"Queued {artworks.Count} artworks for download with preset: {preset.Name}";
    }

    // ── Refresh ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshAsync()
    {
        switch (SelectedTabIndex)
        {
            case 0: await LoadRecommendedWorksAsync(); break;
            case 1: await LoadRecommendedUsersAsync(); break;
        }
    }
}

/// <summary>
/// ViewModel wrapper for discovery users with follow functionality.
/// </summary>
public partial class DiscoveryUserCardViewModel : ObservableObject
{
    [ObservableProperty]
    private DiscoveryUser _user;

    [ObservableProperty]
    private Bitmap? _profileImage;

    public string UserId => User.UserId ?? "";
    public string UserName => User.UserName ?? "";
    public string? ProfileImageUrl => User.ProfileImageUrl;
    public bool IsFollowed => User.IsFollowed;

    public DiscoveryUserCardViewModel(DiscoveryUser user)
    {
        User = user;
    }

    public async Task LoadProfileImageAsync(PixivImageLoader loader, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ProfileImageUrl)) return;
        try
        {
            var bytes = await loader.FetchBytesAsync(ProfileImageUrl, ct);
            if (bytes is null || ct.IsCancellationRequested) return;
            var bmp = await Task.Run(() => { using var ms = new MemoryStream(bytes); return new Bitmap(ms); }, ct);
            if (!ct.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(() => ProfileImage = bmp);
        }
        catch { }
    }
}
