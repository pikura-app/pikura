using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Services;

namespace Pikura.Avalonia.ViewModels;

/// <summary>
/// Container ViewModel for the Batch Download view with tabs.
/// </summary>
public partial class BatchDownloadViewModel : ViewModelBase
{
    [ObservableProperty] private DownloadByArtistViewModel _artistViewModel;
    [ObservableProperty] private DownloadByImageIdViewModel _imageIdViewModel;
    [ObservableProperty] private DownloadBookmarksViewModel _bookmarksViewModel;
    [ObservableProperty] private DownloadFromListViewModel _fromListViewModel;
    [ObservableProperty] private DownloadBySearchViewModel _searchViewModel;
    [ObservableProperty] private DownloadByFanboxViewModel _fanboxViewModel;
    [ObservableProperty] private DownloadByCollectionViewModel _collectionViewModel;
    [ObservableProperty] private SchedulesViewModel _schedulesViewModel;
    [ObservableProperty] private SettingsViewModel _settingsViewModel;

    public BatchDownloadViewModel(
        DownloadByArtistViewModel artistViewModel,
        DownloadByImageIdViewModel imageIdViewModel,
        DownloadBookmarksViewModel bookmarksViewModel,
        DownloadFromListViewModel fromListViewModel,
        DownloadBySearchViewModel searchViewModel,
        DownloadByFanboxViewModel fanboxViewModel,
        DownloadByCollectionViewModel collectionViewModel,
        SchedulesViewModel schedulesViewModel,
        SettingsViewModel settingsViewModel)
    {
        ArtistViewModel = artistViewModel;
        ImageIdViewModel = imageIdViewModel;
        BookmarksViewModel = bookmarksViewModel;
        FromListViewModel = fromListViewModel;
        SearchViewModel = searchViewModel;
        FanboxViewModel = fanboxViewModel;
        CollectionViewModel = collectionViewModel;
        SchedulesViewModel = schedulesViewModel;
        SettingsViewModel = settingsViewModel;
    }

    [RelayCommand]
    private void OpenQueue()
    {
        var queueView = new Views.Download.DownloadQueueView();
        var win = new global::Avalonia.Controls.Window
        {
            Title = "Download Queue",
            Width = 680,
            Height = 500,
            WindowStartupLocation = global::Avalonia.Controls.WindowStartupLocation.CenterOwner,
            Content = queueView
        };
        queueView.SetOwnerWindow(win);
        var dialogService = Services.AppServices.Get<Services.DialogService>();
        if (dialogService.OwnerWindow != null)
            _ = win.ShowDialog(dialogService.OwnerWindow);
        else
            win.Show();
    }
}
