using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Artwork;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Diagnostics;
using System.Linq;

namespace Pikura.Avalonia.Views.History;

public partial class ViewedHistoryView : UserControl
{
    private ViewedHistoryViewModel? VM => DataContext as ViewedHistoryViewModel;
    private double _lastSidePanelWidth = 520;

    public ViewedHistoryView()
    {
        try
        {
            var s = AppServices.Get<SettingsService>();
            if (s.Current.BrowsePanelWidth >= 200)
                _lastSidePanelWidth = s.Current.BrowsePanelWidth;
        }
        catch { }

        InitializeComponent();

        AttachedToVisualTree += (_, _) =>
        {
            HookShowPreview();
            try
            {
                if (VM != null && AppServices.Get<GalleryViewModel>().HasTabs)
                    VM.ShowPreview = true;
            }
            catch { }

            var inlineViewer = this.FindControl<Pikura.Avalonia.Views.Gallery.InlineArtworkViewer>("HistoryInlineViewer");
            if (inlineViewer != null)
            {
                inlineViewer.ToggleBrowse += OnViewerToggleBrowse;
                inlineViewer.ExpandViewer += OnExpandViewer;
                inlineViewer.ViewerClosed += OnViewerClosed;
            }
            var overlayViewer = this.FindControl<Pikura.Avalonia.Views.Gallery.InlineArtworkViewer>("HistoryOverlayViewer");
            if (overlayViewer != null)
            {
                overlayViewer.ToggleBrowse += OnViewerToggleBrowse;
                overlayViewer.ExpandViewer += OnExpandViewer;
                overlayViewer.ViewerClosed += OnViewerClosed;
            }

            try
            {
                var gvm = AppServices.Get<GalleryViewModel>();
                if (gvm.InlineViewerCard != null)
                {
                    if (inlineViewer != null) { inlineViewer.DataContext = null; inlineViewer.DataContext = gvm; }
                    if (overlayViewer != null) { overlayViewer.DataContext = null; overlayViewer.DataContext = gvm; }
                }
            }
            catch { }
        };
    }

    private void HookShowPreview()
    {
        if (VM is { } vm)
        {
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewedHistoryViewModel.ShowPreview))
                    ApplyShowPreview(vm.ShowPreview);
            };
            ApplyShowPreview(vm.ShowPreview);
        }
    }

    private void ApplyShowPreview(bool show)
    {
        var grid = this.FindControl<Grid>("ContentGrid");
        if (grid == null || grid.ColumnDefinitions.Count < 3) return;
        var col = grid.ColumnDefinitions[2];
        if (show)
        {
            col.Width = new GridLength(_lastSidePanelWidth);
            col.MinWidth = 320;
        }
        else
        {
            if (col.ActualWidth > 0)
            {
                _lastSidePanelWidth = col.ActualWidth;
                SavePanelWidth();
            }
            col.MinWidth = 0;
            col.Width = new GridLength(0);
        }
    }

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        var grid = this.FindControl<Grid>("ContentGrid");
        if (grid?.ColumnDefinitions.Count >= 3)
        {
            var w = grid.ColumnDefinitions[2].ActualWidth;
            if (w >= 200) { _lastSidePanelWidth = w; SavePanelWidth(); }
        }
    }

    private void SavePanelWidth()
    {
        try
        {
            var s = AppServices.Get<SettingsService>();
            s.Update(x => x.BrowsePanelWidth = _lastSidePanelWidth);
        }
        catch { }
    }

    private void OnViewerToggleBrowse(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM == null) return;
        VM.ShowPreview = !VM.ShowPreview;
    }

    private void OnExpandViewer(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM == null) return;
        VM.IsViewerExpanded = !VM.IsViewerExpanded;
        if (!VM.IsViewerExpanded) VM.ShowPreview = true;
    }

    private void OnViewerClosed(object? sender, RoutedEventArgs e)
    {
        if (VM != null) VM.ShowPreview = false;
    }

    private async void OnCalendarOpened(object? sender, EventArgs e)
    {
        var cal = this.FindControl<Calendar>("DatePopupCalendar");
        if (cal == null || VM == null) return;
        cal.DisplayDateEnd = DateTime.Today;
        if (VM.SelectedDate is { } d)
        {
            cal.SelectedDate = d;
            cal.DisplayDate = d;
        }
        try
        {
            // Reserved for a future visual enhancement (highlighting active days) —
            // Avalonia's Calendar doesn't support arbitrary date-marking without a
            // custom template, so for now the flyout just lets you pick any date.
            _ = await VM.GetActiveDatesAsync();
        }
        catch { /* non-fatal */ }
    }

    private void OnCalendarSelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (VM == null) return;
        if (sender is not Calendar cal) return;
        if (cal.SelectedDate is not DateTime dt) return;
        _ = VM.GoToDateCommand.ExecuteAsync(dt.Date);
        global::Avalonia.Controls.Primitives.FlyoutBase
            .GetAttachedFlyout(this.FindControl<Button>("CalendarFlyoutButton")!)?.Hide();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (VM is not { CanLoadMore: true, IsLoading: false, UsePagination: false }) return;
        if (sender is not ScrollViewer sv) return;
        if (sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height < 300)
            _ = VM.LoadMoreAsync();
    }

    /// <summary>Opens the custom date-range picker as an anchored Popup near the ⏱ button —
    /// a nested Flyout-inside-a-MenuFlyout (the previous approach) is a known Avalonia timing
    /// trap: showing a second flyout synchronously while the first is still closing loses the
    /// race and silently shows nothing, even when deferred with Dispatcher.Post. The Popup here
    /// is opened deferred for the same underlying reason (this handler runs while the
    /// MenuFlyout hosting "Custom range…" is still closing).</summary>
    private void OnCustomRangeMenuClick(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (VM == null) return;
            CustomRangePanel.SetInitialRange(VM.CustomRangeStart, VM.CustomRangeEnd);
            CustomRangePopup.IsOpen = true;
        }, DispatcherPriority.Background);
    }

    private void OnCustomRangeApplied(object? sender, EventArgs e)
    {
        if (VM == null || sender is not Dialogs.DateRangePickerPanel panel) return;
        CustomRangePopup.IsOpen = false;
        if (panel.RangeStart is null || panel.RangeEnd is null) return;
        VM.CustomRangeStart = panel.RangeStart;
        VM.CustomRangeEnd = panel.RangeEnd;
        _ = VM.ApplyCustomRangeCommand.ExecuteAsync(null);
    }

    private void OnCustomRangeCancelled(object? sender, EventArgs e) => CustomRangePopup.IsOpen = false;

    private void OnHistoryPageInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            VM?.GoToPageInputCommand.Execute(null);
        }
    }

    private void OnCardClicked(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (e.Handled) return;
        if (e.Source is CheckBox or Button) return;
        if (sender is not Control ctrl) return;
        if (ctrl.DataContext is not ArtworkCardViewModel card) return;
        if (VM == null) return;

        var blurEnabled = VM.SettingsService.Current.BlurR18Content;
        if (blurEnabled && card.IsR18 && card.IsBlurred)
        {
            card.IsBlurred = false;
        }
        else
        {
            e.Handled = true;
            VM.OpenCard(card);
        }
    }

    private void OnCardCheckboxClicked(object? sender, RoutedEventArgs e) => VM?.NotifySelectionChanged();

    private static ArtworkCardViewModel? CardFrom(object? sender) =>
        (sender as MenuItem)?.DataContext as ArtworkCardViewModel;

    private void OnContextPreview(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card) VM?.OpenCard(card);
    }

    private async void OnDownloadPresetClicked(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm == null) return;

        var picked = vm.Results.Where(c => c.IsSelected).ToList();
        if (picked.Count == 0 && vm.GalleryVm.InlineViewerCard != null)
            picked = [vm.GalleryVm.InlineViewerCard];

        if (picked.Count == 0)
        {
            vm.StatusMessage = "No artwork selected or open. Click an artwork or select multiple first.";
            return;
        }

        var dialogService = AppServices.Get<DialogService>();
        var firstArtwork = picked[0].Artwork;
        var additionalArtworks = picked.Skip(1).Select(c => c.Artwork).ToList();

        var preset = await dialogService.ShowDownloadPresetDialogAsync(firstArtwork, additionalArtworks);
        if (preset != null)
        {
            foreach (var card in picked)
                await vm.GalleryVm.DownloadWithPresetAsync(card, preset);
            vm.StatusMessage = $"Queued {picked.Count} artwork(s) for download with preset: {preset.Name}";
        }
    }

    private void OnContextOpenPixiv(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card) return;
        try { Process.Start(new ProcessStartInfo($"https://www.pixiv.net/artworks/{card.Id}") { UseShellExecute = true }); }
        catch { }
    }

    private void OnContextToggleSelection(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card)
        {
            card.IsSelected = !card.IsSelected;
            VM?.NotifySelectionChanged();
        }
    }

    private async void OnContextRemove(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card || VM == null) return;
        await VM.RemoveAsync(card);
    }

    private void OnContextDownloadAll(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card) _ = VM?.GalleryVm.DownloadSingleAsync(card);
    }

    private void OnContextDownloadThisPage(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card) _ = VM?.GalleryVm.DownloadSinglePageAsync(card, 0);
    }

    private void OnContextOpenFullScreen(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card || VM == null) return;
        VM.OpenCard(card);
        VM.IsViewerExpanded = true;
    }

    private void OnContextOpenInNewTab(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card || VM == null) return;
        try
        {
            AppServices.Get<GalleryViewModel>().OpenInNewTab(card, VM.Results.ToList().AsReadOnly(), VM.Results.Count, null, VM.ViewerSourceKey);
            VM.ShowPreview = true;
        }
        catch { }
    }

    private async void OnContextOpenPopup(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card || VM == null) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;
        var viewer = new ArtworkViewerWindow(card.Artwork, VM.GalleryVm);
        await viewer.ShowDialog(window);
    }

    private void OnContextOpenArtistGallery(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card) return;
        try
        {
            var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
            mainWindow?.LoadGalleryView();
            var galleryVm = AppServices.Get<GalleryViewModel>();
            _ = galleryVm.LoadArtistByIdCommand.ExecuteAsync(card.UserId);
        }
        catch { }
    }

    private void OnContextToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card) return;
        var favs = AppServices.Get<LocalFavoritesService>();
        favs.Toggle(card.Artwork);
        card.IsLocalFavorite = favs.IsFavorite(card.Id);
    }

    private void OnContextCopyId(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card) return;
        CopyTextToClipboard(card.Id);
    }

    private void OnContextCopyArtistId(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card) return;
        if (string.IsNullOrWhiteSpace(card.UserId)) return;
        CopyTextToClipboard(card.UserId);
        try { QuickClipboardService.CopyArtist(card.UserId); } catch { }
        if (VM != null) VM.StatusMessage = $"Copied artist ID {card.UserId} ({card.UserName})";
    }

    private void OnContextCopyImage(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card || card.Thumbnail == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        _ = clipboard.SetBitmapAsync(card.Thumbnail);
    }

    private async void OnContextUseAsBackground(object? sender, RoutedEventArgs e)
    {
        try { if (!AppServices.Get<BackgroundOverlayService>().IsEnabled) return; }
        catch { return; }
        if (CardFrom(sender) is not { } card) return;
        if (string.IsNullOrWhiteSpace(card.ThumbnailUrl)) return;
        try
        {
            var overlay = AppServices.Get<BackgroundOverlayService>();
            var bytes = await overlay.FetchImageBytesAsync(card.ThumbnailUrl);
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window == null) { overlay.AddImage(card.ThumbnailUrl); return; }

            var seedEntry = new OverlayImageEntry
            {
                Path = card.ThumbnailUrl,
                Title = card.Title,
                UserName = card.UserName,
                UserId = card.UserId,
                IllustId = card.Id,
            };
            var preview = new Pikura.Avalonia.Views.Dialogs.BackgroundPreviewWindow(card.ThumbnailUrl, bytes, seedEntry);
            await preview.ShowDialog(window);

            if (preview.Result is { } result)
            {
                result.Title = card.Title;
                result.UserName = card.UserName;
                result.UserId = card.UserId;
                result.IllustId = card.Id;
                overlay.AddImage(card.ThumbnailUrl, result);
            }
        }
        catch { /* non-fatal */ }
    }

    private void CopyTextToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        var dt = new DataTransfer();
        dt.Add(DataTransferItem.CreateText(text));
        _ = clipboard.SetDataAsync(dt);
    }
}
