using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Core.Services;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using System.Threading.Tasks;
using System;

namespace Pikura.Avalonia.ViewModels;

public partial class DownloadsViewModel : ViewModelBase
{
    private readonly PixivDownloadService _downloadService;
    private readonly DialogService _dialogService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public DownloadsViewModel(PixivDownloadService downloadService, DialogService dialogService)
    {
        _downloadService = downloadService;
        _dialogService = dialogService;
    }

    [RelayCommand]
    private async Task RefreshDownloadsAsync()
    {
        IsLoading = true;
        StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Jobs_RefreshingDownloads"];

        try
        {
            await Task.Delay(500); // Simulate loading
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Jobs_DownloadsRefreshed"];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to refresh downloads");
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Jobs_FailedToRefreshDownloads"];
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Jobs_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Jobs_FailedToRefreshDownloads_2"]);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
