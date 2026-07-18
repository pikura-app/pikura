using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Avalonia.Services;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

public partial class EnhancedRankingsViewModel : ViewModelBase
{
    private readonly PixivClient _pixivClient;
    private readonly PixivImageLoader _imageLoader;
    private readonly Pikura.Core.Settings.SettingsService _settingsService;
    private readonly LocalFavoritesService _favoritesService;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Loading daily rankings…";
    [ObservableProperty] private string _selectedMode = "daily";
    [ObservableProperty] private string _selectedContent = "all";
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalItems;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _showR18;
    [ObservableProperty] private string _rankingDate = "";
    [ObservableProperty] private string? _prevDate;
    [ObservableProperty] private string? _nextDate;
    [ObservableProperty] private string _dateInput = "";

    // View options — mirrors GalleryViewModel
    [ObservableProperty] private int _cardSize = 180;
    [ObservableProperty] private bool _isFixedHeight = true;
    [ObservableProperty] private bool _isNaturalHeight;
    [ObservableProperty] private bool _isGridView = true;
    [ObservableProperty] private bool _isListView;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showPreview;

    [ObservableProperty] private bool _isViewerExpanded;
    partial void OnIsViewerExpandedChanged(bool v) { OnPropertyChanged(nameof(IsViewerFullScreen)); }
    public bool IsViewerFullScreen => IsViewerExpanded;
    public double FixedCardTotalHeight => CardSize;
    public bool HasSelection => SelectedCount > 0;

    // Grid view mode combined with height mode (for ScrollViewer visibility)
    public bool ShowFixedGrid => IsFixedHeight && IsGridView;
    public bool ShowNaturalGrid => IsNaturalHeight && IsGridView;

    public bool IsFavorite(string id) => _favoritesService.IsFavorite(id);
    [RelayCommand]
    public void ToggleFavorite(RankingCardViewModel card)
    {
        if (_favoritesService.IsFavorite(card.Id))
            _favoritesService.Remove(card.Id);
        else
            _favoritesService.Add(card.ToPreview());
    }
    public GalleryViewModel GalleryVm => Pikura.Avalonia.Services.AppServices.Get<GalleryViewModel>();
    public string ViewerSourceKey => $"Rankings:{SelectedMode}:{SelectedContent}:{RankingDate}:{ShowR18}:{TagFilter}:{UsePagination}:{GalleryCurrentPage}";

    // Public accessor for settings service (used by view for blur checking)
    public Pikura.Core.Settings.SettingsService SettingsService => _settingsService;

    // R-18 button visibility - hide when R-18 is disabled globally
    public bool ShowR18Buttons => _settingsService.Current.R18Mode != Pikura.Core.Settings.R18Mode.Off;
    // Effective R-18 toggle - respects global setting
    public bool IsR18Enabled => _settingsService.Current.R18Mode != Pikura.Core.Settings.R18Mode.Off;
    // AI button visible when R-18 is Off, or when in Show mode but toggle is OFF
    public bool ShowAiButton => _settingsService.Current.R18Mode == Pikura.Core.Settings.R18Mode.Off 
        || (_settingsService.Current.R18Mode == Pikura.Core.Settings.R18Mode.Show && !ShowR18);

    // Content-type specific mode visibility
    public bool ShowOverallModes => SelectedContent == "all";
    public bool ShowIllustModes => SelectedContent == "illust";
    public bool ShowUgoiraModes => SelectedContent == "ugoira";
    public bool ShowMangaModes => SelectedContent == "manga";
    public bool ShowNovelModes => SelectedContent == "novel";

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _loadMoreCts;

    public ObservableCollection<RankingCardViewModel> Items { get; } = [];
    public ObservableCollection<RankingCardViewModel> FilteredItems { get; } = [];

    // Tag filter (set when user clicks a tag chip)
    [ObservableProperty] private string _tagFilter = string.Empty;
    partial void OnTagFilterChanged(string _) => UpdateFilteredItems();

    // Pagination properties
    [ObservableProperty] private bool _usePagination;
    [ObservableProperty] private int _itemsPerPage = 50;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private bool _canGoPrevious;
    [ObservableProperty] private bool _canGoNext;
    [ObservableProperty] private int _galleryCurrentPage = 1;

    public int[] ItemsPerPageOptions { get; } = { 10, 20, 50, 100 };

    public string FormattedDate
    {
        get
        {
            var d = RankingDate;
            if (!string.IsNullOrEmpty(d) && d.Length == 8 && d.All(char.IsDigit))
                return $"{d[..4]}-{d[4..6]}-{d[6..8]}";
            return "";
        }
    }

    private static string? NormalizeDate(object? raw)
    {
        var s = raw?.ToString();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length == 8 && s.All(char.IsDigit)) return s;
        return null;
    }

    public EnhancedRankingsViewModel(PixivClient pixivClient, PixivImageLoader imageLoader, Pikura.Core.Settings.SettingsService settingsService, LocalFavoritesService favoritesService)
    {
        _pixivClient = pixivClient;
        _imageLoader = imageLoader;
        _settingsService = settingsService;
        _favoritesService = favoritesService;

        // Restore persisted settings
        var s = _settingsService.Current;
        _usePagination = s.RankingsUsePagination;
        _itemsPerPage = s.RankingsItemsPerPage;
        _showR18 = s.RankingsShowR18;

        // Restore view options — CardSize is shared across all tabs (synced via s.CardSize)
        _cardSize = s.CardSize;
        _isGridView = s.RankingsViewMode == "Grid";
        _isListView = s.RankingsViewMode == "List";
        _isFixedHeight = s.RankingsCardHeightMode == "Fixed";
        _isNaturalHeight = s.RankingsCardHeightMode == "Natural";
        _showInfo = s.RankingsShowInfo;
        _showTags = s.RankingsShowTags;

        // Listen for global settings changes (excluded tags, R18Mode, blur) — not Rankings-specific ones
        _settingsService.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowR18Buttons));
            OnPropertyChanged(nameof(IsR18Enabled));
            OnPropertyChanged(nameof(ShowAiButton));
            // Sync shared CardSize from settings (updated by other tabs)
            var shared = _settingsService.Current.CardSize;
            if (CardSize != shared) CardSize = shared;
            // Only reload for global filter changes; Rankings-specific toggles handle their own reload
            UpdateFilteredItems();
        };

        // Force initial property notifications to ensure UI reflects current settings
        OnPropertyChanged(nameof(ShowR18Buttons));
        OnPropertyChanged(nameof(IsR18Enabled));
        OnPropertyChanged(nameof(ShowAiButton));

        // When all viewer tabs are closed (global state), also collapse the side panel here.
        GalleryVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.HasTabs) && !GalleryVm.HasTabs)
            { ShowPreview = false; IsViewerExpanded = false; }
        };

        // Auto-load daily/overall on startup
        _ = ReloadAsync();
    }

    partial void OnRankingDateChanged(string value)
    {
        // Only echo a clean YYYY-MM-DD into the input — never raw garbage like 'False'
        DateInput = FormattedDate;
        OnPropertyChanged(nameof(FormattedDate));
    }

    partial void OnShowR18Changed(bool value)
    {
        // Save the R-18 toggle state
        _settingsService.Update(s => s.RankingsShowR18 = value);
        
        // If currently in AI mode and R-18 is turned on, switch back to daily
        if (value && SelectedMode == "daily_ai")
        {
            SelectedMode = "daily";
        }
        
        // Notify that AI button visibility may have changed
        OnPropertyChanged(nameof(ShowAiButton));
        // Reload to apply R-18 filtering and update FilteredItems
        GalleryCurrentPage = 1;
        _ = ReloadAsync();
    }

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));

    partial void OnCardSizeChanged(int value)
    {
        OnPropertyChanged(nameof(FixedCardTotalHeight));
        if (_settingsService.Current.CardSize != value)
            _settingsService.Update(s => s.CardSize = value);
    }

    partial void OnIsFixedHeightChanged(bool value)
    {
        if (value) _settingsService.Update(s => s.RankingsCardHeightMode = "Fixed");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }

    partial void OnIsNaturalHeightChanged(bool value)
    {
        if (value) _settingsService.Update(s => s.RankingsCardHeightMode = "Natural");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }

    partial void OnIsGridViewChanged(bool value)
    {
        if (value) _settingsService.Update(s => s.RankingsViewMode = "Grid");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }

    partial void OnIsListViewChanged(bool value)
    {
        if (value) _settingsService.Update(s => s.RankingsViewMode = "List");
        OnPropertyChanged(nameof(ShowFixedGrid));
        OnPropertyChanged(nameof(ShowNaturalGrid));
    }

    partial void OnShowInfoChanged(bool value) => _settingsService.Update(s => s.RankingsShowInfo = value);
    partial void OnShowTagsChanged(bool value) => _settingsService.Update(s => s.RankingsShowTags = value);

    partial void OnSelectedContentChanged(string value)
    {
        // Notify mode visibility properties when content type changes
        OnPropertyChanged(nameof(ShowOverallModes));
        OnPropertyChanged(nameof(ShowIllustModes));
        OnPropertyChanged(nameof(ShowUgoiraModes));
        OnPropertyChanged(nameof(ShowMangaModes));
        OnPropertyChanged(nameof(ShowNovelModes));
    }

    [RelayCommand] public void SetFixedHeight()  { IsFixedHeight = true;  IsNaturalHeight = false; }
    [RelayCommand] public void SetNaturalHeight() { IsFixedHeight = false; IsNaturalHeight = true; }
    [RelayCommand] public void SetGridView()  { IsGridView = true;  IsListView = false; }
    [RelayCommand] public void SetListView()  { IsGridView = false; IsListView = true; }

    [RelayCommand]
    public async Task GoToLatestAsync()
    {
        RankingDate = "";
        DateInput = "";
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task GoToDateInputAsync()
    {
        var input = DateInput.Trim().Replace("-", "").Replace("/", "");
        if (input.Length == 8 && input.All(char.IsDigit))
        {
            RankingDate = input;
            await ReloadAsync();
        }
    }

    [RelayCommand]
    public async Task SelectModeAsync(string mode)
    {
        if (SelectedMode == mode && Items.Count > 0) return;
        SelectedMode = mode;
        // Preserve the currently displayed date across mode changes
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task SelectContentAsync(string content)
    {
        if (SelectedContent == content && Items.Count > 0) return;
        SelectedContent = content;
        // Preserve the currently displayed date across content changes
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task GoToPrevDateAsync()
    {
        if (PrevDate is null) return;
        RankingDate = PrevDate;
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task GoToNextDateAsync()
    {
        if (NextDate is null) return;
        RankingDate = NextDate;
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        _loadMoreCts?.Cancel(); // abort any in-flight load-more
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        foreach (var c in Items) c.PropertyChanged -= OnCardPropertyChanged;
        Items.Clear();
        SelectedCount = 0;
        CurrentPage = 1;
        CanLoadMore = false;
        IsLoading = true;
        StatusMessage = $"Loading {SelectedMode} rankings…";

        // Determine effective mode based on R-18 toggle and selected mode
        var effectiveMode = SelectedMode;
        var r18Mode = _settingsService.Current.R18Mode;

        // Global R-18 Only mode: always fetch R-18 content, toggle controls visibility
        if (r18Mode == R18Mode.Only && !effectiveMode.Contains("_r18") && effectiveMode is "daily" or "weekly" or "monthly" or "rookie" or "original" or "male" or "female")
            effectiveMode += "_r18";

        // If ShowR18 is ON and we're in a safe mode, switch to R-18 variant
        // Note: daily_ai does NOT have an R-18 variant in the API
        else if (ShowR18 && !effectiveMode.Contains("_r18") && effectiveMode is "daily" or "weekly" or "monthly" or "rookie" or "original" or "male" or "female")
            effectiveMode += "_r18";

        // If ShowR18 is OFF and we're NOT in Only mode, switch to safe variant
        if (!ShowR18 && r18Mode != R18Mode.Only && effectiveMode.Contains("_r18"))
            effectiveMode = effectiveMode.Replace("_r18", "");

        // Global R-18 Off always forces safe mode regardless of toggle
        if (r18Mode == R18Mode.Off && effectiveMode.Contains("_r18"))
            effectiveMode = effectiveMode.Replace("_r18", "");

        try
        {
            var dateParam = string.IsNullOrEmpty(RankingDate) ? null : RankingDate;
            var resp = await _pixivClient.GetRankingAsync(effectiveMode, SelectedContent, 1, ct, dateParam);
            if (resp is null || resp.Contents.Count == 0)
            {
                StatusMessage = "No results — check your session or try a different mode.";
                return;
            }

            // Pixiv's rank_total only reflects the first page count (e.g. 100).
            // If more pages exist, estimate the true total from pages already seen × page size.
            // We refine this as each subsequent page is loaded.
            TotalItems = resp.Next is not null
                ? Math.Max(resp.RankTotal, resp.Contents.Count * 5) // max 5 pages = 500 entries
                : resp.RankTotal;
            RankingDate = resp.Date is { Length: 8 } d && d.All(char.IsDigit) ? d : "";
            PrevDate = NormalizeDate(resp.PrevDate);
            NextDate = NormalizeDate(resp.NextDate);

            // Global excluded tags from settings
            var excludedTags = _settingsService.Current.ExcludedTags;
            var globalR18Off = _settingsService.Current.R18Mode == Pikura.Core.Settings.R18Mode.Off;

            foreach (var entry in resp.Contents)
            {
                if (ct.IsCancellationRequested) break;

                // Skip R-18 items if globally disabled
                if (globalR18Off && entry.Tags.Any(t => t.Contains("R-18", StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Skip items with excluded tags
                if (excludedTags.Count > 0 && entry.Tags.Any(t => excludedTags.Any(e => t.Contains(e, StringComparison.OrdinalIgnoreCase))))
                    continue;

                var card = new RankingCardViewModel(entry)
                {
                    IsBlurred = _settingsService.Current.BlurR18Content && entry.ContentType.Sexual
                };
                card.PropertyChanged += OnCardPropertyChanged;
                Items.Add(card);
                _ = card.LoadThumbnailAsync(_imageLoader); // no ct — thumbnail should survive load-more
            }

            CanLoadMore = resp.Next is not null;
            CurrentPage = 2;

            // Sync to FilteredItems for pagination display
            UpdateFilteredItems();

            StatusMessage = $"#{Items[0].Rank}–#{Items[^1].Rank} of {TotalItems:N0}  ·  {effectiveMode}  ·  {RankingDate}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"Failed: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!CanLoadMore || IsLoading) return;
        // Use a separate CTS so ReloadAsync's _cts is never disposed by this method
        _loadMoreCts?.Cancel();
        _loadMoreCts?.Dispose();
        _loadMoreCts = new CancellationTokenSource();
        var ct = _loadMoreCts.Token;

        IsLoading = true;

        // Determine effective mode based on R-18 toggle and selected mode (same logic as ReloadAsync)
        // Note: daily_ai does NOT have an R-18 variant in the API
        var effectiveMode = SelectedMode;
        var r18Mode = _settingsService.Current.R18Mode;

        // Global R-18 Only mode: always fetch R-18 content
        if (r18Mode == R18Mode.Only && !effectiveMode.Contains("_r18") && effectiveMode is "daily" or "weekly" or "monthly" or "rookie" or "original" or "male" or "female")
            effectiveMode += "_r18";
        else if (ShowR18 && !effectiveMode.Contains("_r18") && effectiveMode is "daily" or "weekly" or "monthly" or "rookie" or "original" or "male" or "female")
            effectiveMode += "_r18";

        // If ShowR18 is OFF and we're NOT in Only mode, switch to safe variant
        if (!ShowR18 && r18Mode != R18Mode.Only && effectiveMode.Contains("_r18"))
            effectiveMode = effectiveMode.Replace("_r18", "");

        // Global R-18 Off always forces safe mode regardless of toggle
        if (r18Mode == R18Mode.Off && effectiveMode.Contains("_r18"))
            effectiveMode = effectiveMode.Replace("_r18", "");

        try
        {
            var resp = await _pixivClient.GetRankingAsync(effectiveMode, SelectedContent, CurrentPage, ct);
            if (resp is null) return;

            foreach (var entry in resp.Contents)
            {
                if (ct.IsCancellationRequested) break;
                var card = new RankingCardViewModel(entry)
                {
                    IsBlurred = _settingsService.Current.BlurR18Content && entry.ContentType.Sexual
                };
                card.PropertyChanged += OnCardPropertyChanged;
                Items.Add(card);
                _ = card.LoadThumbnailAsync(_imageLoader); // no ct — thumbnail should survive navigation
            }

            CanLoadMore = resp.Next is not null;
            CurrentPage++;

            // Refine TotalItems: once no more pages, use exact loaded count
            if (!CanLoadMore)
                TotalItems = Items.Count;

            // Sync to FilteredItems for pagination display
            UpdateFilteredItems();
            var viewerCards = FilteredItems
                .Select(c => new ArtworkCardViewModel(c.ToPreview()) { ViewerPosition = c.Rank })
                .ToList();
            GalleryVm.SyncViewerTabs(ViewerSourceKey, viewerCards, UsePagination ? viewerCards.Count : TotalItems);

            StatusMessage = $"#{Items[0].Rank}–#{Items[^1].Rank} of {TotalItems:N0}  ·  {effectiveMode}  ·  {RankingDate}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"Failed: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    private void OnCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RankingCardViewModel.IsSelected))
            SelectedCount = Items.Count(c => c.IsSelected);
    }

    public void NotifySelectionChanged()
        => SelectedCount = Items.Count(c => c.IsSelected);

    [RelayCommand]
    public void SelectAll() { foreach (var c in Items) c.IsSelected = true; NotifySelectionChanged(); }

    [RelayCommand]
    public void ClearSelection() { foreach (var c in Items) c.IsSelected = false; SelectedCount = 0; }

    [RelayCommand]
    public Task DownloadSelectedAsync()
    {
        var previews = Items.Where(c => c.IsSelected).Select(c => c.ToPreview()).ToList();
        if (previews.Count == 0) return Task.CompletedTask;
        return GalleryVm.DownloadPreviewsAsync(previews);
    }

    [RelayCommand]
    public Task DownloadVisibleAsync()
    {
        var previews = FilteredItems.Select(c => c.ToPreview()).ToList();
        if (previews.Count == 0) return Task.CompletedTask;
        return GalleryVm.DownloadPreviewsAsync(previews);
    }

    [RelayCommand]
    public async Task DownloadWithPresetAsync()
    {
        var previews = Items.Where(c => c.IsSelected).Select(c => c.ToPreview()).ToList();
        if (previews.Count == 0)
        {
            StatusMessage = "No artworks selected.";
            return;
        }

        // Get first artwork for preview
        var first = previews[0];
        var dialogService = Pikura.Avalonia.Services.AppServices.Get<DialogService>();
        var preset = await dialogService.ShowDownloadPresetDialogAsync(first);
        if (preset != null)
        {
            // Use GalleryVm's method to download with preset
            await GalleryVm.DownloadWithPresetAsync(previews, preset);
        }
    }

    // Pagination commands
    [RelayCommand] private void TogglePagination() => UsePagination = !UsePagination;

    [RelayCommand]
    private void FirstPage()
    {
        if (GalleryCurrentPage > 1)
        {
            GalleryCurrentPage = 1;
            UpdateFilteredItems();
        }
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (GalleryCurrentPage > 1)
        {
            GalleryCurrentPage--;
            UpdateFilteredItems();
        }
    }

    [RelayCommand]
    private async Task NextPage()
    {
        if (GalleryCurrentPage < TotalPages)
        {
            var nextPage = GalleryCurrentPage + 1;
            var neededItemCount = nextPage * ItemsPerPage;
            while (Items.Count < neededItemCount && CanLoadMore)
            {
                await LoadMoreAsync();
            }
            GalleryCurrentPage = nextPage;
            UpdateFilteredItems();
        }
    }

    [RelayCommand]
    private async Task LastPage()
    {
        if (GalleryCurrentPage < TotalPages)
        {
            // Load all remaining data to reach the last page
            while (Items.Count < TotalItems && CanLoadMore)
            {
                await LoadMoreAsync();
            }
            GalleryCurrentPage = TotalPages;
            UpdateFilteredItems();
        }
    }

    [RelayCommand]
    private async Task GoToPageAsync(int page)
    {
        if (page >= 1 && page <= TotalPages)
        {
            // Check if we need to load more data for this page
            var neededItemCount = page * ItemsPerPage;
            while (Items.Count < neededItemCount && CanLoadMore)
            {
                await LoadMoreAsync();
            }
            GalleryCurrentPage = page;
            UpdateFilteredItems();
        }
    }

    partial void OnGalleryCurrentPageChanged(int value)
    {
        // Validate page bounds and update pagination when user enters a page number
        if (UsePagination)
        {
            var clampedValue = Math.Clamp(value, 1, Math.Max(1, TotalPages));
            if (clampedValue != value)
            {
                GalleryCurrentPage = clampedValue; // Fix out-of-bounds
                return; // OnPropertyChanged will trigger again
            }
            // Load more data if needed for this page
            var neededItemCount = value * ItemsPerPage;
            if (Items.Count < neededItemCount && CanLoadMore)
            {
                _ = LoadDataForPageAsync(value);
            }
            else
            {
                UpdateFilteredItems();
            }
        }
    }

    private async Task LoadDataForPageAsync(int targetPage)
    {
        var neededItemCount = targetPage * ItemsPerPage;
        while (Items.Count < neededItemCount && CanLoadMore)
        {
            await LoadMoreAsync();
        }
        UpdateFilteredItems();
    }

    partial void OnUsePaginationChanged(bool value)
    {
        GalleryCurrentPage = 1;
        _settingsService.Update(s => s.RankingsUsePagination = value);
        UpdateFilteredItems();
    }

    partial void OnItemsPerPageChanged(int value)
    {
        _settingsService.Update(s => s.RankingsItemsPerPage = value);
        if (UsePagination)
        {
            GalleryCurrentPage = 1;
            UpdateFilteredItems();
        }
    }

    /// <summary>
    /// Updates FilteredItems based on R-18 filter and pagination settings.
    /// </summary>
    public void UpdateFilteredItems()
    {
        // Filter based on R-18 settings
        var src = Items.AsEnumerable();

        // Tag filter
        if (!string.IsNullOrEmpty(TagFilter))
            src = src.Where(a => a.TopTags.Any(t => t.Contains(TagFilter, StringComparison.OrdinalIgnoreCase)));

        // R-18 filter logic based on global R18Mode and view toggle
        var r18Mode = _settingsService.Current.R18Mode;
        var r18Type = _settingsService.Current.R18Type;

        if (r18Mode == R18Mode.Only)
        {
            // Only mode: Show nothing if toggle off, show only R-18 if toggle on
            if (!ShowR18)
            {
                src = src.Where(_ => false); // Show nothing when toggle is off
            }
            else
            {
                // Filter by R18Type: Both, R18 only, or R18G only
                src = r18Type switch
                {
                    R18TypeFilter.Both => src.Where(a => a.IsR18),
                    R18TypeFilter.R18 => src.Where(a => a.IsR18 && !a.IsR18G),
                    R18TypeFilter.R18G => src.Where(a => a.IsR18G),
                    _ => src.Where(a => a.IsR18)
                };
            }
        }
        else if (r18Mode == R18Mode.Off || !ShowR18)
        {
            // Hide all R-18/R-18G content when globally off or toggle is disabled
            src = src.Where(a => !a.IsR18);
        }
        // R18Mode.Show allows all content, no additional filtering needed

        // Apply pagination if enabled
        if (UsePagination)
        {
            var filteredCount = src.Count();

            // Use TotalItems (from API) if more data is available, otherwise use what's loaded
            var totalForPages = (CanLoadMore && TotalItems > filteredCount) ? TotalItems : filteredCount;
            TotalPages = Math.Max(1, (int)Math.Ceiling(totalForPages / (double)ItemsPerPage));
            GalleryCurrentPage = Math.Clamp(GalleryCurrentPage, 1, TotalPages);
            CanGoPrevious = GalleryCurrentPage > 1;
            CanGoNext = GalleryCurrentPage < TotalPages;

            src = src.Skip((GalleryCurrentPage - 1) * ItemsPerPage).Take(ItemsPerPage);
        }
        else
        {
            TotalPages = 1;
            CanGoPrevious = false;
            CanGoNext = false;
        }

        FilteredItems.Clear();
        foreach (var a in src.OrderBy(x => x.Rank)) FilteredItems.Add(a);
    }
}
