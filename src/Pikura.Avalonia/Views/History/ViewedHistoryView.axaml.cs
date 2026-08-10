using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
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
        if (VM is not { CanLoadMore: true, IsLoading: false }) return;
        if (sender is not ScrollViewer sv) return;
        if (sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height < 300)
            _ = VM.LoadMoreAsync();
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

    private async void OnClearAllClicked(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        await VM.ClearAllAsync();
    }

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
}
