using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Core.Models;

namespace Pikura.Avalonia.ViewModels;

public partial class EnhancedGalleryViewModel : ViewModelBase
{
    private string _searchQuery = string.Empty;
    private bool _isLoading = false;
    private string _statusMessage = "Ready";
    private int _selectedCount = 0;
    private FollowedArtist? _selectedArtist;

    public ObservableCollection<FollowedArtist> Artists { get; }
    public ObservableCollection<EnhancedArtworkCardViewModel> VisibleArtworks { get; }
    public ObservableCollection<string> ActiveFilters { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public int SelectedCount
    {
        get => _selectedCount;
        set => SetProperty(ref _selectedCount, value);
    }

    public FollowedArtist? SelectedArtist
    {
        get => _selectedArtist;
        set => SetProperty(ref _selectedArtist, value);
    }

    public IRelayCommand SearchCommand { get; }
    public IRelayCommand LoadFollowedArtistsCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    public IRelayCommand ApplyFilterCommand { get; }
    public IRelayCommand RemoveFilterCommand { get; }

    public EnhancedGalleryViewModel()
    {
        Artists = new ObservableCollection<FollowedArtist>();
        VisibleArtworks = new ObservableCollection<EnhancedArtworkCardViewModel>();
        ActiveFilters = new ObservableCollection<string>();

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        LoadFollowedArtistsCommand = new AsyncRelayCommand(LoadFollowedArtistsAsync);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        ApplyFilterCommand = new RelayCommand<string>(ApplyFilter);
        RemoveFilterCommand = new RelayCommand<string>(RemoveFilter);

        // Initialize with sample data
        InitializeSampleData();
    }

    private void InitializeSampleData()
    {
        // Sample followed artists
        Artists.Add(new FollowedArtist
        {
            UserId = "12345",
            UserName = "DigitalArtist",
            ProfileImageUrl = null,
            UserComment = "Creating amazing digital art",
            Following = true,
            Illustrations = new List<ArtworkPreview>
            {
                new() { Id = "1", Title = "Sunset Dreams", UserName = "DigitalArtist" },
                new() { Id = "2", Title = "Ocean Waves", UserName = "DigitalArtist" },
                new() { Id = "3", Title = "Mountain Peak", UserName = "DigitalArtist" }
            }
        });

        Artists.Add(new FollowedArtist
        {
            UserId = "67890",
            UserName = "CreativeMind",
            ProfileImageUrl = null,
            UserComment = "Exploring new artistic horizons",
            Following = true,
            Illustrations = new List<ArtworkPreview>
            {
                new() { Id = "4", Title = "Abstract Thoughts", UserName = "CreativeMind" },
                new() { Id = "5", Title = "Color Explosion", UserName = "CreativeMind" }
            }
        });

        // Sample artworks
        var sampleArtworks = new List<EnhancedArtworkCardViewModel>
        {
            new(new ArtworkPreview 
            { 
                Id = "1", 
                Title = "Ethereal Landscape", 
                UserName = "DigitalArtist",
                BookmarkCount = 25300,
                LikeCount = 156000,
                Tags = new List<string> { "landscape", "digital", "nature" }
            }),
            new(new ArtworkPreview 
            { 
                Id = "2", 
                Title = "Character Portrait", 
                UserName = "CreativeMind",
                BookmarkCount = 18700,
                LikeCount = 124000,
                Tags = new List<string> { "character", "portrait", "anime" }
            }),
            new(new ArtworkPreview 
            { 
                Id = "3", 
                Title = "Cyberpunk City", 
                UserName = "FutureArtist",
                BookmarkCount = 15200,
                LikeCount = 98000,
                Tags = new List<string> { "cyberpunk", "city", "sci-fi" }
            }),
            new(new ArtworkPreview 
            { 
                Id = "4", 
                Title = "Fantasy Warrior", 
                UserName = "GameArtist",
                BookmarkCount = 12800,
                LikeCount = 87000,
                Tags = new List<string> { "fantasy", "warrior", "game" }
            }),
            new(new ArtworkPreview 
            { 
                Id = "5", 
                Title = "Peaceful Garden", 
                UserName = "NatureArtist",
                BookmarkCount = 11200,
                LikeCount = 76000,
                Tags = new List<string> { "nature", "garden", "peaceful" }
            }),
            new(new ArtworkPreview 
            { 
                Id = "6", 
                Title = "Space Exploration", 
                UserName = "SciFiArtist",
                BookmarkCount = 9800,
                LikeCount = 65000,
                Tags = new List<string> { "space", "sci-fi", "exploration" }
            })
        };

        foreach (var artwork in sampleArtworks)
        {
            VisibleArtworks.Add(artwork);
        }

        StatusMessage = $"Loaded {VisibleArtworks.Count} artworks";
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
            return;

        IsLoading = true;
        StatusMessage = "Searching...";

        try
        {
            await Task.Delay(1000); // Simulate API call

            // Filter artworks based on search query
            var filtered = VisibleArtworks.Where(a => 
                a.Title.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                a.UserName.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                a.Tags.Any(tag => tag.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            VisibleArtworks.Clear();
            foreach (var artwork in filtered)
            {
                VisibleArtworks.Add(artwork);
            }

            StatusMessage = $"Found {VisibleArtworks.Count} results for '{SearchQuery}'";
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

    private async Task LoadFollowedArtistsAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading followed artists...";

        try
        {
            await Task.Delay(800); // Simulate API call
            StatusMessage = $"Loaded {Artists.Count} followed artists";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load artists: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ClearSelection()
    {
        foreach (var artwork in VisibleArtworks)
        {
            artwork.IsSelected = false;
        }
        SelectedCount = 0;
        StatusMessage = "Selection cleared";
    }

    private void ApplyFilter(string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return;
        if (!ActiveFilters.Contains(filter))
        {
            ActiveFilters.Add(filter);
            // Apply filter logic here
            StatusMessage = $"Applied filter: {filter}";
        }
    }

    private void RemoveFilter(string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return;
        ActiveFilters.Remove(filter);
        // Remove filter logic here
        StatusMessage = $"Removed filter: {filter}";
    }
}

public partial class EnhancedArtworkCardViewModel : ObservableObject
{
    private bool _isSelected = false;

    public string Id { get; }
    public string Title { get; }
    public string UserName { get; }
    public int BookmarkCount { get; }
    public int LikeCount { get; }
    public List<string> Tags { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public EnhancedArtworkCardViewModel(ArtworkPreview artwork)
    {
        Id = artwork.Id;
        Title = artwork.Title;
        UserName = artwork.UserName;
        BookmarkCount = artwork.BookmarkCount ?? 0;
        LikeCount = artwork.LikeCount ?? 0;
        Tags = artwork.Tags?.ToList() ?? new List<string>();
    }
}
