using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Artwork;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pikura.Avalonia.Views.Rankings;

public partial class EnhancedRankingsView : UserControl
{
    private EnhancedRankingsViewModel? VM => DataContext as EnhancedRankingsViewModel;

    private double _lastSidePanelWidth = 520; // remembered between toggles

    private string RankingViewerSource => VM?.ViewerSourceKey ?? "Rankings";

    private static ArtworkCardViewModel ToViewerCard(RankingCardViewModel card)
        => new(card.ToPreview()) { ViewerPosition = card.Rank };

    private async System.Threading.Tasks.Task<IReadOnlyList<ArtworkCardViewModel>> LoadMoreRankingCardsAsync()
    {
        if (VM == null) return [];
        await VM.LoadMoreAsync();
        return VM.FilteredItems.Select(ToViewerCard).ToList();
    }

    public EnhancedRankingsView()
    {
        try
        {
            var s = AppServices.Get<SettingsService>();
            if (s.Current.BrowsePanelWidth >= 200)
                _lastSidePanelWidth = s.Current.BrowsePanelWidth;
        }
        catch { }

        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) =>
        {
            HookGalleryShowPreview();
            // Tabs are global — auto-open the preview panel if any tabs exist
            try
            {
                if (VM != null && AppServices.Get<GalleryViewModel>().HasTabs)
                    VM.ShowPreview = true;
            }
            catch { }

            // Wire Browse/Close events on both viewers (must be after visual tree is built)
            var inlineViewer = this.FindControl<Pikura.Avalonia.Views.Gallery.InlineArtworkViewer>("RankingsInlineViewer");
            if (inlineViewer != null)
            {
                inlineViewer.ToggleBrowse += OnViewerToggleBrowse;
                inlineViewer.ExpandViewer  += OnExpandViewer;
                inlineViewer.ViewerClosed += OnViewerClosed;
            }
            var overlayViewer = this.FindControl<Pikura.Avalonia.Views.Gallery.InlineArtworkViewer>("RankingsOverlayViewer");
            if (overlayViewer != null)
            {
                overlayViewer.ToggleBrowse += OnViewerToggleBrowse;
                overlayViewer.ExpandViewer  += OnExpandViewer;
                overlayViewer.ViewerClosed += OnViewerClosed;
            }

            // Force viewers to reload the current card when navigating back to Rankings
            // with an existing open tab — same fix applied to Bookmarks and Discover.
            try
            {
                var gvm = AppServices.Get<GalleryViewModel>();
                if (gvm.InlineViewerCard != null)
                {
                    if (inlineViewer != null)  { inlineViewer.DataContext  = null; inlineViewer.DataContext  = gvm; }
                    if (overlayViewer != null) { overlayViewer.DataContext = null; overlayViewer.DataContext = gvm; }
                }
            }
            catch { }
        };
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

    private void HookGalleryShowPreview()
    {
        // Hook Rankings VM's own ShowPreview to drive the column width
        if (VM is { } vm)
        {
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EnhancedRankingsViewModel.ShowPreview))
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
            // restore (or initialise) the panel width
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

    private void OnSplitterDragCompleted(object? sender, global::Avalonia.Input.VectorEventArgs e)
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

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (VM is not { CanLoadMore: true, IsLoading: false, UsePagination: false }) return;
        if (sender is not ScrollViewer sv) return;
        // Trigger load when within 300px of the bottom
        if (sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height < 300)
            _ = VM.LoadMoreAsync();
    }

    private void OnDateInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            VM?.GoToDateInputCommand.Execute(null);
        }
    }

    // ── Calendar popup ─────────────────────────────────────────────────────
    // Constrain to today max and preselect the currently loaded date.
    private void OnCalendarOpened(object? sender, System.EventArgs e)
    {
        var cal = this.FindControl<Calendar>("DatePopupCalendar");
        if (cal == null) return;
        cal.DisplayDateEnd = System.DateTime.Today;
        if (VM is { RankingDate: { Length: 8 } d } &&
            System.DateTime.TryParseExact(d, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var dt))
        {
            cal.SelectedDate = dt;
            cal.DisplayDate = dt;
        }
    }

    // When the user picks a date in the Calendar, set RankingDate (YYYYMMDD)
    // and trigger reload. Then close the parent flyout.
    private void OnCalendarSelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (VM == null) return;
        if (sender is not Calendar cal) return;
        if (cal.SelectedDate is not System.DateTime dt) return;
        var newDate = dt.ToString("yyyyMMdd");
        if (VM.RankingDate == newDate) return;
        VM.RankingDate = newDate;
        _ = VM.ReloadAsync();
        // Hide the flyout this calendar lives in
        global::Avalonia.Controls.Primitives.FlyoutBase
            .GetAttachedFlyout(this.FindControl<Button>("CalendarFlyoutButton")!)?.Hide();
    }

    // Left-click on a card → toggle blur if blurred, else open inline viewer
    // Single click unblurs, double click opens viewer when blurred
    private void OnCardClicked(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (e.Handled) return;
        if (e.Source is CheckBox or Button) return;
        if (sender is not Control ctrl) return;
        if (ctrl.DataContext is not RankingCardViewModel card) return;

        // Check if blur is enabled and card is R-18
        var blurEnabled = VM?.SettingsService.Current.BlurR18Content ?? false;
        var shouldBlur = blurEnabled && card.IsR18;

        e.Handled = true;

        if (shouldBlur && card.IsBlurred)
        {
            // Single click on blurred card: unblur it
            card.IsBlurred = false;
        }
        else
        {
            // Single click on non-blurred card OR double-click on blurred: open viewer
            OpenInlineViewer(card);
        }
    }

    // Per-card preview button — same behaviour as a click
    private void OnPreviewButtonClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control ctrl) return;
        if (ctrl.DataContext is not RankingCardViewModel card) return;
        OpenInlineViewer(card);
    }

    // Cache the latest ranking → ArtworkCard mapping so we can reuse converted cards
    // across prev/next navigation in the viewer.
    private System.Collections.Generic.List<ArtworkCardViewModel>? _navCache;

    // Use the Gallery VM as the inline-viewer host; the InlineArtworkViewer
    // control in this view is bound to that VM and reacts to InlineViewerCard.
    private void OpenInlineViewer(RankingCardViewModel card)
    {
        if (VM == null) return;
        try
        {
            var galleryVm = AppServices.Get<GalleryViewModel>();
            // Build (or rebuild) a navigation list spanning the currently displayed ranking entries.
            _navCache = new System.Collections.Generic.List<ArtworkCardViewModel>(VM.FilteredItems.Count);
            ArtworkCardViewModel? selected = null;
            foreach (var c in VM.FilteredItems)
            {
                var vmCard = ToViewerCard(c);
                _navCache.Add(vmCard);
                if (c.Id == card.Id) selected = vmCard;
            }
            galleryVm.InlineViewerCardList = _navCache;
            var total = VM.UsePagination ? _navCache.Count : Math.Max(VM.TotalItems, _navCache.Count);
            galleryVm.OpenInViewer(selected ?? _navCache[0], _navCache, total,
                VM.UsePagination ? null : LoadMoreRankingCardsAsync, RankingViewerSource);
            // Open the side panel (same as Gallery/Discover single-click behaviour)
            VM.ShowPreview = true;
        }
        catch { /* non-fatal */ }
    }

    // Optional "Preview in popup window" — kept for context menu / power users.
    private async System.Threading.Tasks.Task OpenPopupAsync(RankingCardViewModel card)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;
        try
        {
            var galleryVm = AppServices.Get<GalleryViewModel>();
            var viewer = new ArtworkViewerWindow(card.ToPreview(), galleryVm);
            await viewer.ShowDialog(window);
        }
        catch { /* non-fatal */ }
    }

    private void OnContextOpenFullScreen(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        OpenInlineViewer(card);
        VM.IsViewerExpanded = true;
    }

    private void OnContextOpenInNewTab(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        try
        {
            var galleryVm = AppServices.Get<GalleryViewModel>();
            // Build nav list from all loaded ranking items so prev/next works within the ranking
            var navList = VM?.FilteredItems
                .Select(ToViewerCard)
                .ToList() as IReadOnlyList<ArtworkCardViewModel>;
            var vmCard = navList?.FirstOrDefault(c => c.Id == card.Id)
                         ?? ToViewerCard(card);
            var total = VM?.UsePagination == true ? navList?.Count ?? 0 : Math.Max(VM?.TotalItems ?? 0, navList?.Count ?? 0);
            galleryVm.OpenInNewTab(vmCard, navList, total,
                VM?.UsePagination == true ? null : LoadMoreRankingCardsAsync, RankingViewerSource);
        }
        catch { /* non-fatal */ }
    }

    private void OnContextOpenPopup(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        _ = OpenPopupAsync(card);
    }

    private void OnContextToggleSelection(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        card.IsSelected = !card.IsSelected;
    }

    private void OnContextPreview(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        OpenInlineViewer(card);
    }

    // "Open artist gallery" — switch to Gallery tab and load the artist
    private void OnContextOpenArtistGallery(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        if (TopLevel.GetTopLevel(this) is not MainWindow main) return;
        try
        {
            main.LoadGalleryView();
            var galleryVm = AppServices.Get<GalleryViewModel>();
            _ = galleryVm.LoadArtistByIdCommand.ExecuteAsync(card.UserId);
        }
        catch { /* non-fatal */ }
    }

    private void OnContextOpenPixiv(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        OpenUrl($"https://www.pixiv.net/artworks/{card.Id}");
    }

    private void OnContextOpenArtist(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        OpenUrl($"https://www.pixiv.net/users/{card.UserId}");
    }

    private void OnContextAddFavorite(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        VM.ToggleFavorite(card);
    }

    private void OnContextRemoveFavorite(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        VM.ToggleFavorite(card);
    }

    private void OnContextCopyId(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        CopyToClipboard(card.Id);
    }

    private void OnContextCopyUrl(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        CopyToClipboard($"https://www.pixiv.net/artworks/{card.Id}");
    }

    private void OnContextCopyImage(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        CopyBitmapToClipboard(card.Thumbnail);
    }

    private void CopyBitmapToClipboard(global::Avalonia.Media.Imaging.Bitmap? bmp)
    {
        if (bmp == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        _ = clipboard.SetBitmapAsync(bmp);
    }

    private void CopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        var dt = new global::Avalonia.Input.DataTransfer();
        dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(text));
        _ = clipboard.SetDataAsync(dt);

        // Also save to quick clipboard for easy pasting in batch download
        QuickClipboardService.CopyArtwork(text);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void OnTagChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Border b || b.DataContext is not string tag) return;
        e.Handled = true;
        if ((e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift)
        {
            // Shift+click: open Pixiv tag search in Gallery
            var galleryVm = AppServices.Get<GalleryViewModel>();
            if (galleryVm.SearchByTagCommand.CanExecute(tag))
                _ = galleryVm.SearchByTagCommand.ExecuteAsync(tag);
            if (TopLevel.GetTopLevel(this) is MainWindow main)
                main.LoadGalleryView();
        }
        else
        {
            // Regular click: filter current rankings view
            if (VM != null)
                VM.TagFilter = VM.TagFilter == tag ? string.Empty : tag;
        }
    }

    private bool _showPreviewHooked;
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (VM is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            if (!_showPreviewHooked)
            {
                HookGalleryShowPreview();
                _showPreviewHooked = true;
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Invalidate layout when view modes change to force ScrollViewer/ItemsControl re-measure
        if (e.PropertyName is nameof(EnhancedRankingsViewModel.IsFixedHeight)
                         or nameof(EnhancedRankingsViewModel.IsNaturalHeight)
                         or nameof(EnhancedRankingsViewModel.IsGridView)
                         or nameof(EnhancedRankingsViewModel.IsListView))
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateMasonryPanels, global::Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void InvalidateMasonryPanels()
    {
        // Find all ScrollViewers and force them to re-measure
        foreach (var sv in this.GetVisualDescendants().OfType<ScrollViewer>())
            sv.InvalidateMeasure();

        // Force MasonryPanel cache clear by temporarily adjusting ColumnWidth
        foreach (var panel in this.GetVisualDescendants().OfType<global::Pikura.Avalonia.Views.Gallery.MasonryPanel>())
        {
            var originalWidth = panel.ColumnWidth;
            panel.ColumnWidth = originalWidth + 1;
            panel.ColumnWidth = originalWidth;
        }

        // Also invalidate all ItemsControls
        foreach (var ic in this.GetVisualDescendants().OfType<ItemsControl>())
            ic.InvalidateMeasure();
    }

    private static RankingCardViewModel? GetCardFromMenu(object? sender)
    {
        if (sender is MenuItem { DataContext: RankingCardViewModel card }) return card;
        if (sender is MenuItem mi)
        {
            var cm = mi.Parent as ContextMenu ?? mi.GetLogicalParent<ContextMenu>();
            if (cm?.PlacementTarget is Control ctrl)
                return ctrl.DataContext as RankingCardViewModel;
        }
        return null;
    }

    private async void OnDownloadPresetClicked(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;

        // Priority: 1) selected artworks  2) currently viewed (inline viewer)  3) error
        var previews = VM.Items.Where(c => c.IsSelected).Select(c => c.ToPreview()).ToList();
        if (previews.Count == 0 && VM.GalleryVm.InlineViewerCard != null)
        {
            // Use the currently viewed artwork
            previews.Add(VM.GalleryVm.InlineViewerCard.Artwork);
        }

        if (previews.Count == 0)
        {
            VM.StatusMessage = "No artwork selected or open. Click an artwork or select multiple first.";
            return;
        }

        // Use DialogService to show preset dialog
        var dialogService = AppServices.Get<DialogService>();
        var firstPreview = previews[0];
        var additionalPreviews = previews.Skip(1).ToList();

        var result = await dialogService.ShowDownloadPresetDialogAsync(firstPreview, additionalPreviews);

        if (result != null)
        {
            // Download all selected artworks with the preset
            await VM.GalleryVm.DownloadWithPresetAsync(previews, result);
            VM.StatusMessage = $"Queued {previews.Count} artwork(s) for download with preset: {result.Name}";
        }
    }

    private void OnCardCheckboxClicked(object? sender, RoutedEventArgs e)
    {
        VM?.NotifySelectionChanged();
    }
}
