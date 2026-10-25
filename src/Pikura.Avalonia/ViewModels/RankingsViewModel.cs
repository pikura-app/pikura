using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Core.Services;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using System;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

public partial class RankingsViewModel : ViewModelBase
{
    private readonly PixivClient _pixivClient;
    private readonly DialogService _dialogService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private int _cardSize = 200;

    [ObservableProperty]
    private bool _fitWholeImage;

    [ObservableProperty]
    private bool _showR18;

    public RankingsViewModel(PixivClient pixivClient, DialogService dialogService)
    {
        _pixivClient = pixivClient;
        _dialogService = dialogService;
    }

    [RelayCommand]
    private async Task LoadRankingsAsync()
    {
        IsLoading = true;
        StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Rankings_LoadingRankings"];

        try
        {
            // This would load actual rankings from Pixiv
            await Task.Delay(1000); // Simulate loading
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Rankings_RankingsLoadedSuccessfully"];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load rankings");
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Rankings_FailedToLoadRankings"];
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Rankings_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["Rankings_FailedToLoadRankingsPleaseTry"]);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
