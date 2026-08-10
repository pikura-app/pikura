using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Services;
using Pikura.Core.Data;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

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

    [RelayCommand] public void SetFixedHeight() { IsFixedHeight = true; IsNaturalHeight = false; }
    [RelayCommand] public void SetNaturalHeight() { IsFixedHeight = false; IsNaturalHeight = true; }
    [RelayCommand] public void SetGridView() { IsGridView = true; IsListView = false; }
    [RelayCommand] public void SetListView() { IsGridView = false; IsListView = true; }
    partial void OnIsFixedHeightChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }
    partial void OnIsNaturalHeightChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }
    partial void OnIsGridViewChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }
    partial void OnIsListViewChanged(bool value) { OnPropertyChanged(nameof(ShowFixedGrid)); OnPropertyChanged(nameof(ShowNaturalGrid)); }

    /// <summary>Null = "All time" (default). Non-null filters to that single local calendar day.</summary>
    [ObservableProperty] private DateTime? _selectedDate;
    public bool IsFilteredByDate => SelectedDate.HasValue;
    public string DateLabel => SelectedDate?.ToString("yyyy-MM-dd") ?? "All time";

    partial void OnIsViewerExpandedChanged(bool value) => OnPropertyChanged(nameof(IsViewerFullScreen));
    public bool IsViewerFullScreen => IsViewerExpanded;
    public double FixedCardTotalHeight => CardSize;
    public bool HasSelection => SelectedCount > 0;
    public bool HasResults => Results.Count > 0;

    public GalleryViewModel GalleryVm => AppServices.Get<GalleryViewModel>();
    public string ViewerSourceKey => "ViewedHistory";
    public SettingsService SettingsService => _settingsService;

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

        _settingsService.Changed += (_, _) =>
        {
            var shared = _settingsService.Current.CardSize;
            if (CardSize != shared) CardSize = shared;
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

    [RelayCommand]
    public async Task ReloadAsync()
    {
        Results.Clear();
        SelectedCount = 0;
        IsLoading = true;
        StatusMessage = "Loading history…";

        try
        {
            List<ViewedHistoryEntry> entries;
            int total;
            if (SelectedDate is { } date)
                (entries, total) = await _repository.GetByDateAsync(date, 0, PageSize);
            else
            {
                entries = await _repository.GetRecentAsync(0, PageSize);
                total = await _repository.GetTotalCountAsync();
            }

            foreach (var entry in entries)
                AddCard(entry);

            CanLoadMore = Results.Count < total && entries.Count > 0;
            StatusMessage = Results.Count == 0
                ? (IsFilteredByDate
                    ? $"No artworks viewed on {DateLabel}."
                    : "No viewing history yet — artworks you open will show up here.")
                : $"{Results.Count} of {total:N0} viewed" + (IsFilteredByDate ? $" on {DateLabel}" : "");
        }
        catch (Exception ex) { StatusMessage = $"Failed to load history: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!CanLoadMore || IsLoading) return;
        IsLoading = true;
        try
        {
            List<ViewedHistoryEntry> entries;
            int total;
            if (SelectedDate is { } date)
                (entries, total) = await _repository.GetByDateAsync(date, Results.Count, PageSize);
            else
            {
                entries = await _repository.GetRecentAsync(Results.Count, PageSize);
                total = await _repository.GetTotalCountAsync();
            }

            foreach (var entry in entries)
                AddCard(entry);

            CanLoadMore = Results.Count < total;
            StatusMessage = $"{Results.Count} of {total:N0} viewed" + (IsFilteredByDate ? $" on {DateLabel}" : "");
            GalleryVm.SyncViewerTabs(ViewerSourceKey, Results.ToList(), Results.Count);
        }
        catch (Exception ex) { StatusMessage = $"Failed to load more: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task GoToDateAsync(DateTime date)
    {
        SelectedDate = date;
        OnPropertyChanged(nameof(IsFilteredByDate));
        OnPropertyChanged(nameof(DateLabel));
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task GoToAllTimeAsync()
    {
        SelectedDate = null;
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

        var card = new ArtworkCardViewModel(preview)
        {
            IsBlurred = _settingsService.Current.BlurR18Content && entry.XRestrict >= 1
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
