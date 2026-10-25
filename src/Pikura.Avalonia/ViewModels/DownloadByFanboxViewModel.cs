using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

public partial class DownloadByFanboxViewModel : ViewModelBase
{
    private readonly FanboxClient _fanboxClient;
    private readonly DownloadCoordinator _coordinator;
    private readonly DialogService _dialogService;
    private readonly SettingsService _settingsService;

    [ObservableProperty] private string _postId = string.Empty;
    [ObservableProperty] private string _creatorId = string.Empty;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _searchResult = string.Empty;

    public ObservableCollection<FanboxPostItem> Posts { get; } = new();

    public DownloadByFanboxViewModel(
        FanboxClient fanboxClient,
        DownloadCoordinator coordinator,
        DialogService dialogService,
        SettingsService settingsService)
    {
        _fanboxClient = fanboxClient;
        _coordinator = coordinator;
        _dialogService = dialogService;
        _settingsService = settingsService;
    }

    [RelayCommand]
    private async Task SearchByPostIdAsync()
    {
        if (string.IsNullOrWhiteSpace(PostId))
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_PleaseEnterAPostID"]);
            return;
        }

        try
        {
            IsSearching = true;
            Posts.Clear();
            SearchResult = string.Empty;

            var post = await _fanboxClient.GetPostAsync(PostId);
            if (post != null)
            {
                var item = CreatePostItem(post);
                Posts.Add(item);
                SearchResult = $"Found: {post.Title} by {post.User?.Name ?? post.CreatorId}";
            }
            else
            {
                SearchResult = "Post not found or access denied.";
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Error"], $"Search failed: {ex.Message}");
            SearchResult = "Search failed.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task SearchByCreatorIdAsync()
    {
        if (string.IsNullOrWhiteSpace(CreatorId))
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_PleaseEnterACreatorID"]);
            return;
        }

        try
        {
            IsSearching = true;
            Posts.Clear();
            SearchResult = string.Empty;

            var posts = await _fanboxClient.GetCreatorPostsAsync(CreatorId);
            if (posts != null && posts.Count > 0)
            {
                foreach (var post in posts)
                {
                    var item = CreatePostItem(post);
                    Posts.Add(item);
                }
                SearchResult = $"Found {posts.Count} posts.";
            }
            else
            {
                SearchResult = "No posts found or access denied.";
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Error"], $"Search failed: {ex.Message}");
            SearchResult = "Search failed.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private FanboxPostItem CreatePostItem(FanboxPost post)
    {
        var settings = _settingsService.Current;
        var artistName = post.User?.Name ?? post.CreatorId;
        var artistId = post.UserId ?? post.CreatorId;
        
        var coverFilename = ApplyTemplate(settings.FilenameFanboxCover, post, post.User);
        var infoFilename = ApplyTemplate(settings.FilenameFanboxInfo, post, post.User, "txt");
        
        var imageFilenames = new List<string>();
        foreach (var image in post.Images)
        {
            var imageFilename = ApplyTemplate(settings.FilenameFanboxContent, post, post.User, image.Extension);
            imageFilenames.Add(imageFilename);
        }

        return new FanboxPostItem
        {
            Post = post,
            CoverFilename = coverFilename,
            InfoFilename = infoFilename,
            ImageFilenames = imageFilenames
        };
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (Posts.Count == 0)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_NoItems"], Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_NoPostsToDownloadPleaseSearch"]);
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Start Download",
            $"Download {Posts.Count} FANBOX post(s)?");
        
        if (!confirmed) return;

        try
        {
            var targets = new List<DownloadTarget>();
            foreach (var item in Posts)
            {
                targets.Add(new DownloadTarget
                {
                    TargetId = item.Post.Id,
                    Name = $"{item.Post.Title} by {item.Post.User?.Name ?? item.Post.CreatorId}",
                    Type = TargetType.Post
                });
            }

            var job = await _coordinator.CreateJobAsync(
                DownloadJobType.Fanbox,
                "FANBOX Download",
                targets);

            await _coordinator.StartJobAsync(job.Id);

            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_DownloadStarted"], 
                $"Queued {Posts.Count} FANBOX post(s) for download.\n\nCheck the History tab for progress.");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Error"], $"Failed to start download: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearResults()
    {
        Posts.Clear();
        PostId = string.Empty;
        CreatorId = string.Empty;
        SearchResult = string.Empty;
    }

    private string ApplyTemplate(string template, FanboxPost post, FanboxUser? user, string? extension = null)
    {
        var result = template
            .Replace("%artist%", user?.Name ?? post.CreatorId)
            .Replace("%member_id%", user?.UserId ?? post.CreatorId)
            .Replace("%creator_id%", post.CreatorId)
            .Replace("%image_id%", post.Id)
            .Replace("%title%", post.Title)
            .Replace("%urlFilename%", post.Id)
            .Replace("%image_ext%", extension ?? "jpg");

        return result;
    }
}

public class FanboxPostItem
{
    public FanboxPost Post { get; set; } = null!;
    public string CoverFilename { get; set; } = string.Empty;
    public string InfoFilename { get; set; } = string.Empty;
    public List<string> ImageFilenames { get; set; } = new();
}
