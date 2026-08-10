using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Pikura.Avalonia.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

public partial class DownloadBookmarksViewModel : ViewModelBase
{
    // ── Dependencies ───────────────────────────────────────────────────────
    private readonly PixivClient _client;
    private readonly DownloadCoordinator _coordinator;
    private readonly DialogService _dialogService;
    private readonly LocalFavoritesService _favoritesService;
    private readonly SettingsService _settingsService;

    [ObservableProperty] private bool _isBookmarkedImages = true;
    [ObservableProperty] private bool _isPublicOnly = true;
    [ObservableProperty] private bool _isPrivateOnly;
    [ObservableProperty] private bool _isBothVisibility;
    [ObservableProperty] private bool _includeR18 = true;
    [ObservableProperty] private bool _includeAiGenerated = true;
    [ObservableProperty] private bool _includeManga = true;

    [ObservableProperty] private bool _hasActiveJob;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusMessage = "Ready";

    [ObservableProperty] private string _selectedFolder = string.Empty;
    [ObservableProperty] private string _customDownloadPath = string.Empty;
    [ObservableProperty] private bool _useCustomDownloadPath = false;
    public ObservableCollection<string> AvailableFolders { get; } = [];
    public bool HasFolders => AvailableFolders.Count > 0;

    public DownloadBookmarksViewModel(
        PixivClient client,
        DownloadCoordinator coordinator,
        DialogService dialogService,
        LocalFavoritesService favoritesService,
        SettingsService settingsService)
    {
        _client = client;
        _coordinator = coordinator;
        _dialogService = dialogService;
        _favoritesService = favoritesService;
        _settingsService = settingsService;

        // Load folders
        LoadFolders();
    }

    private void LoadFolders()
    {
        AvailableFolders.Clear();
        foreach (var folder in _favoritesService.GetAllFolders())
        {
            AvailableFolders.Add(folder);
        }
        OnPropertyChanged(nameof(HasFolders));
    }

    [RelayCommand]
    private async Task StartDownloadAsync()
    {
        StatusMessage = "Starting bookmark download...";
        HasActiveJob = true;

        try
        {
            var self = await _client.ResolveSelfAsync();
            if (self == null)
            {
                await _dialogService.ShowMessageAsync("Error", "Not logged in. Please sign in first.");
                return;
            }

            var settings = BuildSettings();

            if (IsBookmarkedImages)
            {
                // Collect artwork IDs from public, private, or both
                var artworkIds = await CollectBookmarkedArtworkIdsAsync(self.Value.UserId);
                if (artworkIds.Count == 0)
                {
                    await _dialogService.ShowMessageAsync("No Bookmarks", "No bookmarked artworks found for the selected visibility.");
                    return;
                }

                StatusMessage = $"Queueing {artworkIds.Count} bookmarked artworks…";
                var targets = artworkIds.Select(id => new DownloadTarget
                {
                    TargetId = id,
                    Type = TargetType.Artwork,
                }).ToList();

                await _coordinator.CreateJobAsync(
                    DownloadJobType.BookmarkImage,
                    $"Bookmarks ({(IsPublicOnly ? "Public" : IsPrivateOnly ? "Private" : "Public+Private")})",
                    targets, settings, startImmediately: true);

                StatusMessage = $"Job queued — {artworkIds.Count} artworks";
            }
            else
            {
                // Bookmarked artists — queue as Artist jobs
                const int batchSize = 48;
                var visibilities = GetVisibilities();
                var artistIds = new System.Collections.Generic.List<(string Id, string Name)>();
                foreach (var hidden in visibilities)
                {
                    var offset = 0;
                    while (true)
                    {
                        var resp = await _client.GetBookmarkedUsersAsync(self.Value.UserId, hidden: hidden, offset: offset, limit: batchSize);
                        if (resp.Users.Count == 0) break;

                        foreach (var u in resp.Users)
                            if (!artistIds.Any(x => x.Id == u.UserId))
                                artistIds.Add((u.UserId, u.UserName));

                        offset += resp.Users.Count;
                        StatusMessage = $"Collecting bookmarked artists… {offset}/{resp.Total}";
                        if (offset >= resp.Total) break;
                    }
                }

                if (artistIds.Count == 0)
                {
                    await _dialogService.ShowMessageAsync("No Bookmarks", "No bookmarked artists found.");
                    return;
                }

                var targets = artistIds.Select(a => new DownloadTarget
                {
                    TargetId = a.Id,
                    Name = a.Name,
                    Type = TargetType.Artist,
                }).ToList();

                await _coordinator.CreateJobAsync(
                    DownloadJobType.BookmarkArtist,
                    $"Bookmarked Artists ({(IsPublicOnly ? "Public" : IsPrivateOnly ? "Private" : "Public+Private")})",
                    targets, settings, startImmediately: true);

                StatusMessage = $"Job queued — {artistIds.Count} artists";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start bookmark download");
            await _dialogService.ShowMessageAsync("Error", $"Failed: {ex.Message}");
        }
        finally
        {
            HasActiveJob = false;
        }
    }

    private async System.Threading.Tasks.Task<System.Collections.Generic.List<string>> CollectBookmarkedArtworkIdsAsync(string userId)
    {
        const int batchSize = 48;
        var ids = new System.Collections.Generic.List<string>();
        foreach (var hidden in GetVisibilities())
        {
            var offset = 0;
            while (true)
            {
                var resp = await _client.GetBookmarkedArtworksAsync(userId, hidden: hidden, offset: offset, limit: batchSize);
                if (resp.Works.Count == 0) break;

                foreach (var w in resp.Works)
                    if (!string.IsNullOrEmpty(w.Id) && !ids.Contains(w.Id))
                        ids.Add(w.Id);

                offset += resp.Works.Count;
                StatusMessage = $"Collecting bookmarks… {offset}/{resp.Total}";
                if (offset >= resp.Total) break;
            }
        }
        return ids;
    }

    private bool[] GetVisibilities() => IsBothVisibility
        ? [false, true]
        : [IsPrivateOnly];

    private SettingsOverride BuildSettings()
    {
        var s = SettingsOverride.FromGlobalSettings(_settingsService.Current);
        s.UseGlobalSettings = false;
        if (!IncludeR18)           s.SkipR18           = true;
        if (!IncludeAiGenerated)   s.FilterAiGenerated = true;
        if (!IncludeManga)         s.SkipManga         = true;
        if (UseCustomDownloadPath && !string.IsNullOrWhiteSpace(CustomDownloadPath))
            s.DownloadRoot = CustomDownloadPath;
        return s;
    }

    [RelayCommand]
    private async Task DownloadFromFolderAsync()
    {
        if (!HasFolders)
        {
            await _dialogService.ShowMessageAsync("No Folders", "No custom folders found in local favorites.");
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedFolder))
        {
            await _dialogService.ShowMessageAsync("No Selection", "Please select a folder to download from.");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Download from Folder",
            $"Download all favorites in folder '{SelectedFolder}'?");
        if (!confirmed) return;

        try
        {
            var favorites = _favoritesService.GetAll()
                .Where(f => string.Equals(_favoritesService.GetFolder(f.Id), SelectedFolder, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (favorites.Count == 0)
            {
                await _dialogService.ShowMessageAsync("No Items", $"No favorites in folder '{SelectedFolder}'.");
                return;
            }

            var settings = BuildSettings();
            if (!string.IsNullOrWhiteSpace(SelectedFolder))
                settings.DownloadRoot = System.IO.Path.Combine(
                    UseCustomDownloadPath && !string.IsNullOrWhiteSpace(CustomDownloadPath)
                        ? CustomDownloadPath
                        : _settingsService.Current.DownloadRoot,
                    SelectedFolder);

            var targets = favorites.Select(f => new DownloadTarget
            {
                TargetId = f.Id,
                Name = f.Title,
                Type = TargetType.Artwork,
            }).ToList();

            await _coordinator.CreateJobAsync(
                DownloadJobType.BookmarkImage,
                $"Favorites — {SelectedFolder}",
                targets, settings, startImmediately: true);

            StatusMessage = $"Job queued — {favorites.Count} artworks from '{SelectedFolder}'";
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync("Error", $"Failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DownloadAllFavoritesAsync()
    {
        var favorites = _favoritesService.GetAll().ToList();
        if (favorites.Count == 0)
        {
            await _dialogService.ShowMessageAsync("No Items", "No local favorites found.");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Download All Favorites",
            $"Download all {favorites.Count} local favorites?");
        if (!confirmed) return;

        try
        {
            var settings = BuildSettings();
            var targets = favorites.Select(f => new DownloadTarget
            {
                TargetId = f.Id,
                Name = f.Title,
                Type = TargetType.Artwork,
            }).ToList();

            await _coordinator.CreateJobAsync(
                DownloadJobType.BookmarkImage,
                "All Local Favorites",
                targets, settings, startImmediately: true);

            StatusMessage = $"Job queued — {favorites.Count} favorites";
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync("Error", $"Failed: {ex.Message}");
        }
    }
}
