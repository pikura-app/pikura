using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Pikura.Avalonia.Services;

namespace Pikura.Avalonia.ViewModels;

public partial class DownloadBySearchViewModel : ViewModelBase
{
    private readonly PixivClient _client;
    private readonly DownloadCoordinator _coordinator;
    private readonly DialogService _dialogService;
    private readonly PixivImageLoader _imageLoader;
    private readonly SettingsService _settingsService;

    [ObservableProperty] private string _searchKeyword = string.Empty;
    [ObservableProperty] private int _maxResults = 100;
    [ObservableProperty] private string _searchMode = "safe"; // safe, r18, all
    [ObservableProperty] private string _sortOrder = "date_d"; // date_d, popular_d
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private int _resultCount = 0;
    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();
    public bool HasResults => SearchResults.Count > 0;

    /// <summary>
    /// Pixiv's "popularity" search sort (order=popular_d) is a Premium-only feature —
    /// non-Premium accounts get it silently ignored/errored server-side. Gate the
    /// option in the UI rather than letting free accounts pick something that won't work.
    /// </summary>
    public bool IsPremiumAccount => _settingsService.Current.IsPremium;

    public DownloadBySearchViewModel(
        PixivClient client,
        DownloadCoordinator coordinator,
        DialogService dialogService,
        PixivImageLoader imageLoader,
        SettingsService settingsService)
    {
        _client = client;
        _coordinator = coordinator;
        _dialogService = dialogService;
        _imageLoader = imageLoader;
        _settingsService = settingsService;

        _settingsService.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsPremiumAccount));
            // If Premium lapsed (or this is a stale non-Premium session) while
            // popularity sort was selected, fall back to date order so the next
            // search doesn't silently misbehave.
            if (!IsPremiumAccount && SortOrder == "popular_d")
                SortOrder = "date_d";
        };

        // Guard against a stale/leftover selection from a previous session or a non-Premium
        // account whose SortOrder somehow ended up "popular_d" — don't wait for a settings
        // Changed event, correct it immediately so the ComboBox can't display/keep it selected.
        if (!IsPremiumAccount && SortOrder == "popular_d")
            SortOrder = "date_d";
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchKeyword))
        {
            await _dialogService.ShowMessageAsync("Error", "Please enter a search keyword.");
            return;
        }

        try
        {
            IsSearching = true;
            SearchResults.Clear();

            // Pixiv returns 60 items per page; loop pages until MaxResults is hit
            // or the API runs out of results.
            var collected = new List<Pikura.Core.Models.ArtworkPreview>();
            for (int page = 1; collected.Count < MaxResults; page++)
            {
                var result = await _client.SearchArtworksAsync(
                    SearchKeyword,
                    SortOrder,
                    SearchMode,
                    page);

                var data = result?.IllustManga?.Data;
                if (data == null || data.Count == 0) break;
                collected.AddRange(data);
                if (data.Count < 60) break; // last page
            }

            if (collected.Count > 0)
            {
                var artworks = collected.Take(MaxResults).ToList();
                foreach (var artwork in artworks)
                {
                    var item = new SearchResultItem
                    {
                        Artwork = artwork,
                        Title = artwork.Title,
                        ArtistName = artwork.UserName,
                        PageCount = artwork.PageCount,
                        IsR18 = artwork.IsR18,
                        IsAiGenerated = artwork.IsAiGenerated
                    };

                    // Load thumbnail asynchronously
                    if (!string.IsNullOrEmpty(artwork.ThumbnailUrl))
                    {
                        _ = LoadThumbnailAsync(item, artwork.ThumbnailUrl);
                    }

                    SearchResults.Add(item);
                }
                ResultCount = SearchResults.Count;
            }
            else
            {
                await _dialogService.ShowMessageAsync("No Results", "No artworks found for this search.");
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync("Error", $"Search failed: {ex.Message}");
        }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(HasResults));
        }
    }

    private async Task LoadThumbnailAsync(SearchResultItem item, string thumbnailUrl)
    {
        try
        {
            var bytes = await _imageLoader.FetchBytesAsync(thumbnailUrl);
            if (bytes != null)
            {
                // Decode on background thread to avoid UI-thread jank
                var bmp = await Task.Run(() => new Bitmap(new System.IO.MemoryStream(bytes)));
                await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => item.Thumbnail = bmp);
            }
        }
        catch
        {
            // Thumbnail load failed - continue without it
        }
    }

    [RelayCommand]
    private void ClearResults()
    {
        SearchResults.Clear();
        SearchKeyword = string.Empty;
        ResultCount = 0;
        OnPropertyChanged(nameof(HasResults));
    }

    [RelayCommand]
    private async Task StartDownloadAsync()
    {
        if (!HasResults)
        {
            await _dialogService.ShowMessageAsync("No Items", "No search results to download. Please search first.");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Start Download",
            $"Download {ResultCount} artworks from search results?");
        
        if (!confirmed) return;

        try
        {
            // Create download targets
            var targets = new List<DownloadTarget>();
            foreach (var item in SearchResults)
            {
                targets.Add(new DownloadTarget
                {
                    TargetId = item.Artwork.Id,
                    Name = $"{item.Artwork.Title} by {item.Artwork.UserName}",
                    Type = TargetType.Artwork
                });
            }

            // Create download job
            var job = await _coordinator.CreateJobAsync(
                DownloadJobType.Search,
                $"Search: {SearchKeyword}",
                targets);

            // Start the job
            await _coordinator.StartJobAsync(job.Id);

            await _dialogService.ShowMessageAsync("Download Started", 
                $"Queued {ResultCount} artworks for download.\n\nCheck the History tab for progress.");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync("Error", $"Failed to start download: {ex.Message}");
        }
    }
}

public class SearchResultItem : ObservableObject
{
    public ArtworkPreview Artwork { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public bool IsR18 { get; set; }
    public bool IsAiGenerated { get; set; }
    
    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }
}
