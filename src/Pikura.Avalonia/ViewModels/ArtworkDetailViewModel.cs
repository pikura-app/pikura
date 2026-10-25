using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Pikura.Avalonia.ViewModels;

public partial class ArtworkDetailViewModel : ViewModelBase
{
    private readonly PixivClient _pixivClient;
    private readonly PixivImageLoader _imageLoader;
    private readonly PixivDownloadService _downloadService;
    private readonly NavigationService _navigationService;
    private readonly DialogService _dialogService;

    [ObservableProperty]
    private ArtworkPreview? _artwork;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _currentPageIndex;

    [ObservableProperty]
    private int _totalPages;

    [ObservableProperty]
    private bool _canGoToPreviousPage;

    [ObservableProperty]
    private bool _canGoToNextPage;

    // Interaction state
    [ObservableProperty]
    private bool _isLiked;

    [ObservableProperty]
    private bool _isBookmarked;

    [ObservableProperty]
    private bool _isFollowing;

    [ObservableProperty]
    private bool _isActionLoading;

    [ObservableProperty]
    private string? _bookmarkId;

    [ObservableProperty]
    private int? _bookmarkCount;

    [ObservableProperty]
    private int? _likeCount;

    [ObservableProperty]
    private int? _viewCount;

    public IReadOnlyList<string> Tags => Artwork?.Tags ?? Array.Empty<string>();

    public string? ArtistName => Artwork?.UserName;
    public string? ArtistId => Artwork?.UserId;

    public ArtworkDetailViewModel(
        PixivClient pixivClient,
        PixivImageLoader imageLoader,
        PixivDownloadService downloadService,
        NavigationService navigationService,
        DialogService dialogService)
    {
        _pixivClient = pixivClient;
        _imageLoader = imageLoader;
        _downloadService = downloadService;
        _navigationService = navigationService;
        _dialogService = dialogService;
    }

    public void Initialize(ArtworkPreview artwork)
    {
        Artwork = artwork;
        CurrentPageIndex = 0;
        _ = LoadArtworkPagesAsync();
        _ = LoadArtworkStateAsync();
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigationService.NavigateTo<GalleryViewModel>();
    }

    [RelayCommand]
    private async Task DownloadCurrentPageAsync()
    {
        if (Artwork == null) return;

        try
        {
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_DownloadingCurrentPage"];
            await Task.Delay(1000); // Simulate download
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_DownloadCompletedSimulated"];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Download failed");
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Settings_NotifyFailed"];
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_DownloadFailedPleaseTryAgain"]);
        }
    }

    [RelayCommand]
    private async Task DownloadAllPagesAsync()
    {
        if (Artwork == null) return;

        try
        {
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_DownloadingAllPages"];
            await Task.Delay(2000); // Simulate download
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_DownloadCompletedSimulated"];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Download failed");
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Settings_NotifyFailed"];
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_DownloadFailedPleaseTryAgain"]);
        }
    }

    [RelayCommand]
    private void GoToPreviousPage()
    {
        if (CanGoToPreviousPage)
        {
            CurrentPageIndex--;
            UpdatePageNavigation();
        }
    }

    [RelayCommand]
    private void GoToNextPage()
    {
        if (CanGoToNextPage)
        {
            CurrentPageIndex++;
            UpdatePageNavigation();
        }
    }

    private async Task LoadArtworkPagesAsync()
    {
        if (Artwork == null) return;

        IsLoading = true;
        StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_LoadingArtworkPages"];

        try
        {
            var pages = await _pixivClient.GetArtworkPagesAsync(Artwork.Id);
            TotalPages = pages.Count;
            UpdatePageNavigation();
            StatusMessage = $"Loaded {TotalPages} pages";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load artwork pages");
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_FailedToLoadArtworkPages"];
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdatePageNavigation()
    {
        CanGoToPreviousPage = CurrentPageIndex > 0;
        CanGoToNextPage = CurrentPageIndex < TotalPages - 1;
    }

    private async Task LoadArtworkStateAsync()
    {
        if (Artwork == null) return;

        try
        {
            var detailTask = _pixivClient.GetArtworkDetailAsync(Artwork.Id);
            var bookmarkTask = _pixivClient.GetBookmarkStateAsync(Artwork.Id);
            var artistTask = _pixivClient.GetArtistAsync(Artwork.UserId);

            await Task.WhenAll(detailTask, bookmarkTask, artistTask);

            var detail = await detailTask;
            var bookmark = await bookmarkTask;
            var artist = await artistTask;

            if (detail != null)
            {
                BookmarkCount = detail.BookmarkCount;
                LikeCount = detail.LikeCount;
                ViewCount = detail.ViewCount;
            }

            if (bookmark != null)
            {
                IsBookmarked = bookmark.IsBookmarked;
                BookmarkId = bookmark.BookmarkId;
            }

            if (artist != null)
            {
                IsFollowing = artist.IsFollowed;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load artwork state for {Id}", Artwork?.Id);
        }
    }

    [RelayCommand]
    private async Task LikeAsync()
    {
        if (Artwork == null || IsLiked || IsActionLoading) return;

        IsActionLoading = true;
        try
        {
            var ok = await _pixivClient.LikeIllustAsync(Artwork.Id);
            if (ok)
            {
                IsLiked = true;
                LikeCount = (LikeCount ?? 0) + 1;
                StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_Liked"];
            }
            else
            {
                StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_CouldNotLikeArtwork"];
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Like failed for {Id}", Artwork.Id);
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_LikeFailed"];
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_CouldNotLikeArtwork_2"]);
        }
        finally
        {
            IsActionLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleBookmarkAsync()
    {
        if (Artwork == null || IsActionLoading) return;

        IsActionLoading = true;
        try
        {
            if (IsBookmarked)
            {
                if (BookmarkId != null)
                {
                    var ok = await _pixivClient.RemoveWebBookmarkAsync(new[] { BookmarkId });
                    if (ok)
                    {
                        IsBookmarked = false;
                        BookmarkId = null;
                        BookmarkCount = (BookmarkCount ?? 1) - 1;
                        StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_RemovedBookmark"];
                    }
                    else
                    {
                        StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_CouldNotRemoveBookmark"];
                    }
                }
            }
            else
            {
                var newBookmarkId = await _pixivClient.AddWebBookmarkAsync(Artwork.Id);
                if (newBookmarkId != null)
                {
                    IsBookmarked = true;
                    BookmarkId = newBookmarkId;
                    BookmarkCount = (BookmarkCount ?? 0) + 1;
                    StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Collections_Bookmarked"];
                }
                else
                {
                    StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_CouldNotBookmark"];
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Toggle bookmark failed for {Id}", Artwork.Id);
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_BookmarkActionFailed"];
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_CouldNotUpdateBookmark"]);
        }
        finally
        {
            IsActionLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleFollowAsync()
    {
        if (Artwork?.UserId == null || IsActionLoading) return;

        IsActionLoading = true;
        try
        {
            bool ok;
            if (IsFollowing)
            {
                ok = await _pixivClient.UnfollowUserAsync(Artwork.UserId);
                if (ok)
                {
                    IsFollowing = false;
                    StatusMessage = $"Unfollowed {Artwork.UserName}";
                }
            }
            else
            {
                ok = await _pixivClient.FollowUserAsync(Artwork.UserId);
                if (ok)
                {
                    IsFollowing = true;
                    StatusMessage = $"Following {Artwork.UserName}";
                }
            }

            if (!ok)
            {
                StatusMessage = IsFollowing ? Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_CouldNotUnfollow"] : Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_CouldNotFollow"];
                await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_CouldNotUpdateFollowStateMake"]);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Toggle follow failed for {UserId}", Artwork.UserId);
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_FollowActionFailed"];
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_CouldNotUpdateFollowState"]);
        }
        finally
        {
            IsActionLoading = false;
        }
    }

    [RelayCommand]
    private async Task SearchByTagAsync(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;

        // Navigate back to gallery and trigger global search
        _navigationService.NavigateTo<GalleryViewModel>();

        // Wait for navigation
        await Task.Delay(100);

        // Get the GalleryViewModel and trigger search
        var galleryVm = AppServices.Get<GalleryViewModel>();
        if (galleryVm is not null)
        {
            await galleryVm.SearchByTagAsync(tag);
        }
    }

    [RelayCommand]
    private async Task SearchByArtistAsync()
    {
        if (Artwork?.UserId is null) return;

        _navigationService.NavigateTo<GalleryViewModel>();

        await Task.Delay(100);
        var galleryVm = AppServices.Get<GalleryViewModel>();
        if (galleryVm is not null)
        {
            await galleryVm.LoadArtistByIdAsync(Artwork.UserId);
        }
    }
}
