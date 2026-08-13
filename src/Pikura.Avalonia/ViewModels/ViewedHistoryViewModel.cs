using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Services;
using Pikura.Core.Data;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

/// <summary>Top-level layout for the Viewed tab: the classic flat feed, a "Grouped" view with per-day collage thumbnails, or a per-day "List" view. See <see cref="ViewedHistoryViewModel.ViewMode"/>.</summary>
public enum HistoryViewMode { Default, Grouped, List }

/// <summary>
/// One row of the "Grouped"/"List" top-level view: a single local calendar date with its
/// view count and up to 4 of its newest thumbnails (for the collage preview). Clicking one
/// drills into <see cref="ViewedHistoryViewModel.DrillDownDate"/> to show that day's artworks.
/// </summary>
public partial class HistoryDateGroupViewModel : ObservableObject
{
    public DateTime Date { get; }
    public int Count { get; }
    /// <summary>Short label for the Grouped collage tiles: "Today"/"Yesterday"/"yyyy-MM-dd".</summary>
    public string Label { get; }
    /// <summary>Long label for the List rows: "Today"/"Yesterday", or "Monday, August 10, 2026 (2026-08-10)".</summary>
    public string FullLabel { get; }

    [ObservableProperty] private Bitmap? _thumb1;
    [ObservableProperty] private Bitmap? _thumb2;
    [ObservableProperty] private Bitmap? _thumb3;
    [ObservableProperty] private Bitmap? _thumb4;

    private readonly List<string?> _thumbnailUrls;

    public HistoryDateGroupViewModel(DateTime date, int count, List<string?> thumbnailUrls)
    {
        Date = date;
        Count = count;
        Label = date == DateTime.Today ? "Today" : date == DateTime.Today.AddDays(-1) ? "Yesterday" : date.ToString("yyyy-MM-dd");
        FullLabel = ViewedHistoryViewModel.FormatDateLabel(date);
        _thumbnailUrls = thumbnailUrls;
    }

    public async Task LoadThumbnailsAsync(PixivImageLoader loader, CancellationToken ct = default)
    {
        var slots = new Action<Bitmap?>[] { b => Thumb1 = b, b => Thumb2 = b, b => Thumb3 = b, b => Thumb4 = b };
        for (var i = 0; i < _thumbnailUrls.Count && i < slots.Length; i++)
        {
            var url = _thumbnailUrls[i];
            if (string.IsNullOrWhiteSpace(url)) continue;
            try
            {
                var skBitmap = await loader.FetchBitmapAsync(url, ThumbnailSize.Small, ct);
                if (skBitmap is null || ct.IsCancellationRequested) continue;
                var bmp = await Task.Run(() => (Bitmap?)Pikura.Avalonia.Services.BitmapInterop.SkiaToAvalonia(skBitmap), ct);
                skBitmap.Dispose();
                if (bmp is not null) slots[i](bmp);
            }
            catch { /* non-fatal — collage tile just stays blank */ }
        }
    }
}

/// <summary>
/// "Viewed" tab — local, unrestricted browsing history. Every artwork opened in the
/// inline viewer anywhere in the app is recorded here (see
/// <see cref="GalleryViewModel.InlineViewerCard"/> change handler), unlike Pixiv's own
/// history feature which is capped for non-Premium accounts. Reuses
/// <see cref="GalleryViewModel"/> for the inline viewer/Hoshi/download commands.
/// </summary>
public partial class ViewedHistoryViewModel : ViewModelBase
{
    private readonly ViewedHistoryRepository _repository;
    private readonly PixivImageLoader _imageLoader;
    private readonly SettingsService _settingsService;

    private const int PageSize = 60;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private int _cardSize = 180;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private bool _isViewerExpanded;
    [ObservableProperty] private int _selectedCount;

    // View options — mirrors Gallery/Rankings/Search
    [ObservableProperty] private bool _isFixedHeight = true;
    [ObservableProperty] private bool _isNaturalHeight;
    [ObservableProperty] private bool _isGridView = true;
    [ObservableProperty] private bool _isListView;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showTags = true;
    public bool ShowFixedGrid => IsFixedHeight && IsGridView;
    public bool ShowNaturalGrid => IsNaturalHeight && IsGridView;

    // ── Pagination — mirrors Rankings'/Gallery's Pages/Autoload toggle. Default (flat) mode
    // only; Grouped/List already show one date's worth at a time. ─────────────────────────────
    [ObservableProperty] private bool _usePagination;
    [ObservableProperty] private int _itemsPerPage = 50;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private bool _canGoPrevious;
    [ObservableProperty] private bool _canGoNext;
    [ObservableProperty] private string _pageInput = "";
    public int[] ItemsPerPageOptions { get; } = { 10, 20, 50, 100 };

    /// <summary>The slice of <see cref="Results"/> actually shown by the grid/list — the full
    /// set when <see cref="UsePagination"/> is off, or just the current page's worth when it's on.</summary>
    public ObservableCollection<ArtworkCardViewModel> DisplayedResults { get; } = [];

    private void UpdateDisplayedResults()
    {
        IEnumerable<ArtworkCardViewModel> src = Results;
        if (UsePagination)
        {
            TotalPages = Math.Max(1, (int)Math.Ceiling(Results.Count / (double)ItemsPerPage));
            CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);
            CanGoPrevious = CurrentPage > 1;
            CanGoNext = CurrentPage < TotalPages || CanLoadMore;
            src = Results.Skip((CurrentPage - 1) * ItemsPerPage).Take(ItemsPerPage);
        }
        DisplayedResults.Clear();
        foreach (var c in src) DisplayedResults.Add(c);
    }

    [RelayCommand] private void TogglePagination() => UsePagination = !UsePagination;

    partial void OnUsePaginationChanged(bool value)
    {
        _settingsService.Update(s => s.HistoryUsePagination = value);
        UpdateDisplayedResults();
    }

    partial void OnItemsPerPageChanged(int value)
    {
        _settingsService.Update(s => s.HistoryItemsPerPage = value);
        if (UsePagination) { CurrentPage = 1; UpdateDisplayedResults(); }
    }

    [RelayCommand]
    private void FirstPage()
    {
        if (CurrentPage <= 1) return;
        CurrentPage = 1;
        UpdateDisplayedResults();
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        UpdateDisplayedResults();
    }

    [RelayCommand]
    private async Task NextPage()
    {
        var neededItemCount = (CurrentPage + 1) * ItemsPerPage;
        while (Results.Count < neededItemCount && CanLoadMore)
            await LoadMoreAsync();
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            UpdateDisplayedResults();
        }
    }

    [RelayCommand]
    private async Task LastPage()
    {
        while (CanLoadMore) await LoadMoreAsync();
        CurrentPage = TotalPages;
        UpdateDisplayedResults();
    }

    [RelayCommand]
    private async Task GoToPageAsync(int page)
    {
        if (page < 1) return;
        var neededItemCount = page * ItemsPerPage;
        while (Results.Count < neededItemCount && CanLoadMore)
            await LoadMoreAsync();
        CurrentPage = page;
        UpdateDisplayedResults();
    }

    [RelayCommand]
    private Task GoToPageInputAsync()
    {
        if (int.TryParse(PageInput, out var page) && page >= 1)
            return GoToPageAsync(page);
        return Task.CompletedTask;
    }

    [RelayCommand] public void SetFixedHeight() { IsFixedHeight = true; IsNaturalHeight = false; }
    [RelayCommand] public void SetNaturalHeight() { IsFixedHeight = false; IsNaturalHeight = true; }
    [RelayCommand] public void SetGridView() { IsGridView = true; IsListView = false; }
    [RelayCommand] public void SetListView() { IsGridView = false; IsListView = true; }
    partial void OnIsFixedHeightChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }
    partial void OnIsNaturalHeightChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }
    partial void OnIsGridViewChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); OnPropertyChanged(nameof(ShowResultsGrid)); OnPropertyChanged(nameof(ShowResultsList)); }
    partial void OnIsListViewChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); OnPropertyChanged(nameof(ShowResultsGrid)); OnPropertyChanged(nameof(ShowResultsList)); }

    /// <summary>
    /// Which of the two artwork item templates (grid cards vs. list rows) to render for a
    /// single day's/all's artworks. In Default mode this follows the Grid/List toggle above;
    /// in Grouped/List top-level mode it follows the top-level mode itself. Never shown while
    /// <see cref="ShowDateGroups"/> is true (the date-group overview is shown instead).
    /// </summary>
    public bool ShowResultsGrid => !ShowDateGroups && (IsDefaultMode ? IsGridView : IsGroupedMode);
    public bool ShowResultsList => !ShowDateGroups && (IsDefaultMode ? IsListView : IsListMode);

    /// <summary>Null = "All time" (default). Non-null filters to that single local calendar day (via the calendar picker).</summary>
    [ObservableProperty] private DateTime? _selectedDate;
    partial void OnSelectedDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(IsSingleDayView));
        OnPropertyChanged(nameof(ShowDateGroups));
        OnPropertyChanged(nameof(ShowResultsGrid));
        OnPropertyChanged(nameof(ShowResultsList));
    }

    /// <summary>Quick range picker: "all", "day" (past 24h), "week", "month", "year", or "custom". Mutually exclusive with <see cref="SelectedDate"/>.</summary>
    [ObservableProperty] private string _quickRange = "all";
    [ObservableProperty] private DateTime? _customRangeStart;
    [ObservableProperty] private DateTime? _customRangeEnd;

    /// <summary>
    /// Top-level layout: flat feed (Default), per-day collage cards (Grouped), or a
    /// per-day list (List). Grouped/List show a list of date groups when the active
    /// filter spans multiple days, or drill straight into that day's artworks when it
    /// doesn't (single date, or after clicking into a group — see <see cref="DrillDownDate"/>).
    /// </summary>
    [ObservableProperty] private HistoryViewMode _viewMode = HistoryViewMode.Default;
    public bool IsDefaultMode => ViewMode == HistoryViewMode.Default;
    public bool IsGroupedMode => ViewMode == HistoryViewMode.Grouped;
    public bool IsListMode => ViewMode == HistoryViewMode.List;

    /// <summary>Set when the user drills into one date group from the Grouped/List overview. Cleared by <see cref="BackToGroupsAsync"/>.</summary>
    [ObservableProperty] private DateTime? _drillDownDate;

    /// <summary>True when a single specific day is being shown (calendar pick, drill-down, or a range that collapses to one day) rather than a multi-day list/grid.</summary>
    public bool IsSingleDayView => SelectedDate.HasValue || DrillDownDate.HasValue;
    /// <summary>True when Grouped/List mode should show the date-group overview instead of a single day's artworks.</summary>
    public bool ShowDateGroups => !IsDefaultMode && !IsSingleDayView;

    public ObservableCollection<HistoryDateGroupViewModel> DateGroups { get; } = [];

    public bool IsFilteredByDate => SelectedDate.HasValue || QuickRange != "all";

    /// <summary>Formats a date as "Today"/"Yesterday", or otherwise "Monday, August 10, 2026 (2026-08-10)".</summary>
    public static string FormatDateLabel(DateTime date) =>
        date == DateTime.Today ? "Today"
        : date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : $"{date:dddd, MMMM d, yyyy} ({date:yyyy-MM-dd})";

    public string DateLabel => SelectedDate?.ToString("yyyy-MM-dd")
        ?? DrillDownDate?.ToString("yyyy-MM-dd")
        ?? QuickRange switch
        {
            "day" => "Past day",
            "week" => "Past week",
            "month" => "Past month",
            "year" => "Past year",
            "custom" when CustomRangeStart.HasValue && CustomRangeEnd.HasValue
                => $"{CustomRangeStart:yyyy-MM-dd} – {CustomRangeEnd:yyyy-MM-dd}",
            _ => "All time",
        };

    /// <summary>Resolves the current quick/custom range to a UTC instant bound (null = unbounded). Ignored while <see cref="SelectedDate"/> or <see cref="DrillDownDate"/> is set — those use the exact-day repository call instead.</summary>
    private (DateTime? StartUtc, DateTime? EndUtc) GetEffectiveRangeUtc()
    {
        var now = DateTime.UtcNow;
        return QuickRange switch
        {
            "day" => (now.AddDays(-1), null),
            "week" => (now.AddDays(-7), null),
            "month" => (now.AddMonths(-1), null),
            "year" => (now.AddYears(-1), null),
            "custom" => (CustomRangeStart?.Date.ToUniversalTime(), CustomRangeEnd?.Date.AddDays(1).ToUniversalTime()),
            _ => (null, null),
        };
    }

    [RelayCommand]
    public Task SetViewModeAsync(string mode)
    {
        ViewMode = mode switch { "grouped" => HistoryViewMode.Grouped, "list" => HistoryViewMode.List, _ => HistoryViewMode.Default };
        return ReloadAsync();
    }

    partial void OnViewModeChanged(HistoryViewMode value)
    {
        OnPropertyChanged(nameof(IsDefaultMode));
        OnPropertyChanged(nameof(IsGroupedMode));
        OnPropertyChanged(nameof(IsListMode));
        OnPropertyChanged(nameof(ShowDateGroups));
        OnPropertyChanged(nameof(ShowResultsGrid));
        OnPropertyChanged(nameof(ShowResultsList));
        if (value == HistoryViewMode.Default) DrillDownDate = null;

        var stored = value.ToString();
        if (_settingsService.Current.HistoryViewMode != stored)
            _settingsService.Update(s => s.HistoryViewMode = stored);
    }

    partial void OnDrillDownDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(IsSingleDayView));
        OnPropertyChanged(nameof(ShowDateGroups));
        OnPropertyChanged(nameof(ShowResultsGrid));
        OnPropertyChanged(nameof(ShowResultsList));
        OnPropertyChanged(nameof(DateLabel));
    }

    /// <summary>Opens a date group's artworks (Grouped/List mode only).</summary>
    [RelayCommand]
    public Task OpenDateGroupAsync(HistoryDateGroupViewModel group)
    {
        DrillDownDate = group.Date;
        return ReloadAsync();
    }

    /// <summary>Returns from a single day's artworks back to the date-group overview (Grouped/List mode only).</summary>
    [RelayCommand]
    public Task BackToGroupsAsync()
    {
        DrillDownDate = null;
        return ReloadAsync();
    }

    /// <summary>Quick range picker: "today" (single day), "day"/"week"/"month"/"year" (relative range), or "all".</summary>
    [RelayCommand]
    public Task SetQuickRangeAsync(string range)
    {
        if (range == "today") return GoToDateAsync(DateTime.Today);

        SelectedDate = null;
        DrillDownDate = null;
        QuickRange = range;
        OnPropertyChanged(nameof(IsFilteredByDate));
        OnPropertyChanged(nameof(DateLabel));
        return ReloadAsync();
    }

    /// <summary>Applies a custom "from – to" date range (inclusive, local calendar dates).</summary>
    [RelayCommand]
    public Task ApplyCustomRangeAsync()
    {
        if (CustomRangeStart is null || CustomRangeEnd is null) return Task.CompletedTask;
        if (CustomRangeStart > CustomRangeEnd) (CustomRangeStart, CustomRangeEnd) = (CustomRangeEnd, CustomRangeStart);
        SelectedDate = null;
        DrillDownDate = null;
        QuickRange = "custom";
        OnPropertyChanged(nameof(IsFilteredByDate));
        OnPropertyChanged(nameof(DateLabel));
        return ReloadAsync();
    }

    partial void OnIsViewerExpandedChanged(bool value) => OnPropertyChanged(nameof(IsViewerFullScreen));
    public bool IsViewerFullScreen => IsViewerExpanded;
    public double FixedCardTotalHeight => CardSize;
    public bool HasSelection => SelectedCount > 0;
    public bool HasResults => ShowDateGroups ? DateGroups.Count > 0 : Results.Count > 0;

    public GalleryViewModel GalleryVm => AppServices.Get<GalleryViewModel>();
    public string ViewerSourceKey => "ViewedHistory";
    public SettingsService SettingsService => _settingsService;

    /// <summary>
    /// Incognito mode — while enabled, artworks opened anywhere in the app are not
    /// recorded to this history (see <see cref="GalleryViewModel.OnInlineViewerCardChanged"/>).
    /// Toggling this only affects the current session (<see cref="SettingsService.ActiveIncognitoEnabled"/>)
    /// — unlike Settings → Advanced's "Incognito mode" checkbox, it is never persisted, so it
    /// always resets back to that persisted setting the next time the app launches.
    /// </summary>
    [ObservableProperty] private bool _incognitoModeEnabled;
    [RelayCommand] public void ToggleIncognitoMode() => IncognitoModeEnabled = !IncognitoModeEnabled;
    partial void OnIncognitoModeEnabledChanged(bool value)
    {
        if (_settingsService.ActiveIncognitoEnabled != value)
            _settingsService.ActiveIncognitoEnabled = value;
    }

    public ObservableCollection<ArtworkCardViewModel> Results { get; } = [];

    [RelayCommand] public void TogglePreview() => ShowPreview = !ShowPreview;
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnCardSizeChanged(int value)
    {
        OnPropertyChanged(nameof(FixedCardTotalHeight));
        if (_settingsService.Current.CardSize != value)
            _settingsService.Update(s => s.CardSize = value);
    }

    public ViewedHistoryViewModel(
        ViewedHistoryRepository repository,
        PixivImageLoader imageLoader,
        SettingsService settingsService)
    {
        _repository = repository;
        _imageLoader = imageLoader;
        _settingsService = settingsService;
        _cardSize = _settingsService.Current.CardSize;
        _incognitoModeEnabled = _settingsService.ActiveIncognitoEnabled;
        _viewMode = Enum.TryParse<HistoryViewMode>(_settingsService.Current.HistoryViewMode, out var storedMode)
            ? storedMode
            : HistoryViewMode.Default;
        _usePagination = _settingsService.Current.HistoryUsePagination;
        _itemsPerPage = _settingsService.Current.HistoryItemsPerPage;

        _settingsService.Changed += (_, _) =>
        {
            var shared = _settingsService.Current.CardSize;
            if (CardSize != shared) CardSize = shared;
        };
        _settingsService.ActiveIncognitoChanged += (_, _) =>
        {
            var incognito = _settingsService.ActiveIncognitoEnabled;
            if (IncognitoModeEnabled != incognito) IncognitoModeEnabled = incognito;
        };

        GalleryVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.HasTabs) && !GalleryVm.HasTabs)
            { ShowPreview = false; IsViewerExpanded = false; }
        };

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await AutoClearExpiredAsync();
        await ReloadAsync();
    }

    /// <summary>
    /// Called every time the user navigates to the Viewed tab so the newest entries
    /// appear without a manual refresh. Skipped while a load is already in flight.
    /// </summary>
    public async Task RefreshOnActivateAsync()
    {
        if (IsLoading) return;
        await AutoClearExpiredAsync();
        await ReloadAsync();
    }

    /// <summary>Deletes entries older than the user's retention window (Settings → Advanced → auto-clear history). No-op when disabled.</summary>
    public async Task AutoClearExpiredAsync()
    {
        try
        {
            var s = _settingsService.Current;
            if ((s.AutoClearViewedHistoryEnabled || s.AutoClearViewedHistoryWhileRunning)
                && s.GetViewedHistoryRetentionCutoffUtc(DateTime.UtcNow) is { } cutoff)
                await _repository.ClearOlderThanAsync(cutoff);
        }
        catch { /* retention cleanup must never break the tab */ }
    }

    /// <summary>Single day currently in view, whether via the calendar picker or a group drill-down — both fetch that day's artworks the same way.</summary>
    private DateTime? EffectiveSingleDay => SelectedDate ?? DrillDownDate;

    private async Task<(List<ViewedHistoryEntry> Entries, int Total)> FetchPageAsync(int offset, int limit)
    {
        if (EffectiveSingleDay is { } date)
            return await _repository.GetByDateAsync(date, offset, limit);

        if (QuickRange != "all")
        {
            var (startUtc, endUtc) = GetEffectiveRangeUtc();
            return await _repository.GetByRangeAsync(startUtc, endUtc, offset, limit);
        }

        var entries = await _repository.GetRecentAsync(offset, limit);
        var total = await _repository.GetTotalCountAsync();
        return (entries, total);
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (ShowDateGroups) { await ReloadDateGroupsAsync(); return; }

        Results.Clear();
        DateGroups.Clear();
        SelectedCount = 0;
        CurrentPage = 1;
        IsLoading = true;
        StatusMessage = "Loading history…";

        try
        {
            var (entries, total) = await FetchPageAsync(0, PageSize);

            foreach (var entry in entries)
                AddCard(entry);

            CanLoadMore = Results.Count < total && entries.Count > 0;
            StatusMessage = Results.Count == 0
                ? (IsFilteredByDate
                    ? $"No artworks viewed on {DateLabel}."
                    : "No viewing history yet — artworks you open will show up here.")
                : $"{Results.Count} of {total:N0} viewed" + (IsFilteredByDate ? $" on {DateLabel}" : "");
            OnPropertyChanged(nameof(HasResults));
            UpdateDisplayedResults();
        }
        catch (Exception ex) { StatusMessage = $"Failed to load history: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!CanLoadMore || IsLoading || ShowDateGroups) return;
        IsLoading = true;
        try
        {
            var (entries, total) = await FetchPageAsync(Results.Count, PageSize);

            foreach (var entry in entries)
                AddCard(entry);

            CanLoadMore = Results.Count < total;
            StatusMessage = $"{Results.Count} of {total:N0} viewed" + (IsFilteredByDate ? $" on {DateLabel}" : "");
            GalleryVm.SyncViewerTabs(ViewerSourceKey, Results.ToList(), Results.Count);
            if (!UsePagination) UpdateDisplayedResults();
        }
        catch (Exception ex) { StatusMessage = $"Failed to load more: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>Loads the date-group overview (Grouped/List mode, multi-day range or all time).</summary>
    private async Task ReloadDateGroupsAsync()
    {
        Results.Clear();
        DateGroups.Clear();
        SelectedCount = 0;
        IsLoading = true;
        StatusMessage = "Loading history…";

        try
        {
            var (startUtc, endUtc) = QuickRange == "all" ? (null, (DateTime?)null) : GetEffectiveRangeUtc();
            var groups = await _repository.GetDateGroupsAsync(startUtc, endUtc);

            foreach (var g in groups)
            {
                var vm = new HistoryDateGroupViewModel(g.Date, g.Count, g.Thumbnails);
                DateGroups.Add(vm);
                _ = vm.LoadThumbnailsAsync(_imageLoader);
            }

            CanLoadMore = false;
            var totalViews = groups.Sum(g => g.Count);
            StatusMessage = DateGroups.Count == 0
                ? $"No artworks viewed{(IsFilteredByDate ? $" {DateLabel.ToLowerInvariant()}" : " yet")}."
                : $"{DateGroups.Count:N0} day(s), {totalViews:N0} viewed" + (IsFilteredByDate ? $" ({DateLabel})" : "");
            OnPropertyChanged(nameof(HasResults));
        }
        catch (Exception ex) { StatusMessage = $"Failed to load history: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task GoToDateAsync(DateTime date)
    {
        SelectedDate = date;
        QuickRange = "all";
        DrillDownDate = null;
        OnPropertyChanged(nameof(IsFilteredByDate));
        OnPropertyChanged(nameof(DateLabel));
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task GoToAllTimeAsync()
    {
        SelectedDate = null;
        QuickRange = "all";
        DrillDownDate = null;
        OnPropertyChanged(nameof(IsFilteredByDate));
        OnPropertyChanged(nameof(DateLabel));
        await ReloadAsync();
    }

    /// <summary>Dates that have at least one recorded view — used to highlight active days in the calendar picker.</summary>
    public async Task<HashSet<DateTime>> GetActiveDatesAsync() => await _repository.GetActiveDatesAsync();

    private void AddCard(ViewedHistoryEntry entry)
    {
        var preview = new ArtworkPreview
        {
            Id = entry.ArtworkId,
            Title = entry.Title,
            UserId = entry.UserId,
            UserName = entry.UserName,
            ThumbnailUrl = entry.ThumbnailUrl,
            IllustType = entry.IllustType,
            XRestrict = entry.XRestrict,
            PageCount = entry.PageCount,
            Tags = entry.Tags,
        };
        if (_settingsService.Current.IsArtworkHidden("Viewed", preview.UserId, preview.UserName, preview.Title, preview.Tags))
            return;

        var viewedLocal = entry.ViewedAt.Kind == DateTimeKind.Utc ? entry.ViewedAt.ToLocalTime() : entry.ViewedAt;
        var card = new ArtworkCardViewModel(preview)
        {
            IsBlurred = _settingsService.Current.BlurR18Content && entry.XRestrict >= 1,
            ViewedAtLabel = EffectiveSingleDay.HasValue
                ? viewedLocal.ToString("h:mm tt")
                : viewedLocal.ToString("MM/dd/yyyy 'at' h:mm tt"),
        };
        Results.Add(card);
        _ = card.LoadThumbnailAsync(_imageLoader);
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
    public async Task RemoveAsync(ArtworkCardViewModel card)
    {
        Results.Remove(card);
        await _repository.RemoveAsync(card.Id);
        OnPropertyChanged(nameof(HasResults));
    }

    [RelayCommand]
    public async Task ClearAllAsync()
    {
        await _repository.ClearAllAsync();
        Results.Clear();
        DateGroups.Clear();
        StatusMessage = "History cleared.";
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>
    /// Clears history from a recent time window: "hour", "day", "week", "month",
    /// "year" (entries viewed within that window), or "all" for everything.
    /// </summary>
    [RelayCommand]
    public async Task ClearHistoryRangeAsync(string range)
    {
        var description = range == "all" ? "your entire viewing history" : $"viewing history from the past {range}";
        var confirmed = await AppServices.Get<DialogService>().ShowConfirmationAsync(
            "Clear History",
            $"Delete {description}? This cannot be undone.");
        if (!confirmed) return;

        var now = DateTime.UtcNow;
        DateTime? cutoff = range switch
        {
            "hour"  => now.AddHours(-1),
            "day"   => now.AddDays(-1),
            "week"  => now.AddDays(-7),
            "month" => now.AddMonths(-1),
            "year"  => now.AddYears(-1),
            _       => null, // "all"
        };

        if (cutoff is { } c)
        {
            var removed = await _repository.ClearSinceAsync(c);
            StatusMessage = $"Cleared {removed:N0} entr{(removed == 1 ? "y" : "ies")} from the past {range}.";
        }
        else
        {
            await _repository.ClearAllAsync();
            StatusMessage = "History cleared.";
        }

        await ReloadAsync();
    }

    public void NotifySelectionChanged() => SelectedCount = Results.Count(c => c.IsSelected);
    [RelayCommand] public void SelectAll() { foreach (var c in Results) c.IsSelected = true; NotifySelectionChanged(); }
    [RelayCommand] public void ClearSelection() { foreach (var c in Results) c.IsSelected = false; SelectedCount = 0; }

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
}
