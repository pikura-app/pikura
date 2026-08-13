using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Artwork;
using Pikura.Avalonia.Views.Dialogs;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Diagnostics;
using System.Linq;

namespace Pikura.Avalonia.Views.Search;

public partial class GlobalSearchView : UserControl
{
    private GlobalSearchViewModel? VM => DataContext as GlobalSearchViewModel;
    private double _lastSidePanelWidth = 520;

    public GlobalSearchView()
    {
        try
        {
            var s = AppServices.Get<SettingsService>();
            if (s.Current.BrowsePanelWidth >= 200)
                _lastSidePanelWidth = s.Current.BrowsePanelWidth;
        }
        catch { }

        InitializeComponent();

        // TextBox's own internal pointer handling (caret placement/selection) marks
        // PointerPressed Handled during the tunnel pass, so a plain XAML-attached handler on it
        // never actually fires — handledEventsToo:true is required to still see the click and
        // reopen the search-history dropdown after light-dismiss has closed it.
        SearchBox.AddHandler(PointerPressedEvent, OnSearchBoxPointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        AttachedToVisualTree += (_, _) =>
        {
            HookShowPreview();
            try
            {
                if (VM != null && AppServices.Get<GalleryViewModel>().HasTabs)
                    VM.ShowPreview = true;
            }
            catch { }

            var inlineViewer = this.FindControl<Pikura.Avalonia.Views.Gallery.InlineArtworkViewer>("SearchInlineViewer");
            if (inlineViewer != null)
            {
                inlineViewer.ToggleBrowse += OnViewerToggleBrowse;
                inlineViewer.ExpandViewer += OnExpandViewer;
                inlineViewer.ViewerClosed += OnViewerClosed;
            }
            var overlayViewer = this.FindControl<Pikura.Avalonia.Views.Gallery.InlineArtworkViewer>("SearchOverlayViewer");
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
                if (e.PropertyName == nameof(GlobalSearchViewModel.ShowPreview))
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

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (VM is not { CanLoadMore: true, IsLoading: false, UsePagination: false }) return;
        if (sender is not ScrollViewer sv) return;
        if (sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height < 300)
            _ = VM.LoadMoreAsync();
    }

    private void OnSearchPageInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            VM?.GoToPageInputCommand.Execute(null);
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SearchHistoryPopup.IsOpen = false;
            VM?.SearchCommand.Execute(null);
        }
    }

    // ── Search history dropdown ──────────────────────────────────────────────
    private void OnSearchBoxGotFocus(object? sender, RoutedEventArgs e)
    {
        if (VM is { HasSearchHistory: true }) SearchHistoryPopup.IsOpen = true;
    }

    // Light-dismiss closes the popup on any outside click, but a click outside that lands on a
    // non-focusable area (e.g. the artwork grid background) never moves focus away from the
    // TextBox — so it stays focused and GotFocus never fires again to reopen the dropdown on the
    // next click. Reopening on every press (not just focus changes) covers that case too.
    private void OnSearchBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VM is { HasSearchHistory: true } && !SearchHistoryPopup.IsOpen) SearchHistoryPopup.IsOpen = true;
    }

    private void OnSearchBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        // Deferred so a click on a history row (which momentarily steals focus from the
        // TextBox) doesn't close the popup before that click's own handler runs.
        Dispatcher.UIThread.Post(() =>
        {
            if (!SearchBox.IsFocused) SearchHistoryPopup.IsOpen = false;
        }, DispatcherPriority.Background);
    }

    private void OnHistoryEntryClicked(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not Pikura.Core.Settings.SearchHistoryEntry entry) return;
        SearchHistoryPopup.IsOpen = false;
        _ = VM?.ApplyHistoryEntryCommand.ExecuteAsync(entry);
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
        var shouldBlur = blurEnabled && card.IsR18;

        e.Handled = true;

        if (shouldBlur && card.IsBlurred)
        {
            card.IsBlurred = false;
        }
        else
        {
            VM.OpenCard(card);
        }
    }

    private void OnCardCheckboxClicked(object? sender, RoutedEventArgs e) => VM?.NotifySelectionChanged();

    // ── Category tabs (mutually exclusive) ──────────────────────────────────

    private void OnIllustrationsCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        VM.SearchCategory = "illustrations";
    }

    private void OnMangaCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        VM.SearchCategory = "manga";
    }

    private void OnNovelsCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        VM.SearchCategory = "novels";
    }

    private void OnUsersCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        VM.SearchCategory = "users";
    }

    // ── Novel / User cards ───────────────────────────────────────────────────

    private void OnNovelCardClicked(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Control ctrl) return;
        if (ctrl.DataContext is not NovelCardViewModel card) return;
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo($"https://www.pixiv.net/novel/show.php?id={card.Id}") { UseShellExecute = true }); }
        catch { }
    }

    private void OnUserCardClicked(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Control ctrl) return;
        if (ctrl.DataContext is not UserSearchCardViewModel card) return;
        e.Handled = true;
        try
        {
            var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
            mainWindow?.LoadGalleryView();
            var galleryVm = AppServices.Get<GalleryViewModel>();
            _ = galleryVm.LoadArtistByIdCommand.ExecuteAsync(card.UserId);
        }
        catch { }
    }

    // ── Advanced filter flyout ───────────────────────────────────────────────

    private void OnAdvancedFiltersOpening(object? sender, EventArgs e)
    {
        // Load the currently applied filters into the flyout's edit copy so
        // the user can tweak them without touching the live search state.
        VM?.RefreshAdvancedFilterEdit();
    }

    private void OnApplyAdvancedFiltersClick(object? sender, RoutedEventArgs e)
    {
        // The Apply command commits the edit copy to the live filters. Close the flyout.
        if (sender is Button btn) btn.Flyout?.Hide();
    }

    private void OnResetAdvancedFiltersClick(object? sender, RoutedEventArgs e)
    {
        // The Reset command only clears the edit copy; close the flyout so the
        // user can reopen it and apply the cleared defaults if they want.
        if (sender is Button btn) btn.Flyout?.Hide();
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private static ArtworkCardViewModel? CardFrom(object? sender) =>
        (sender as MenuItem)?.DataContext as ArtworkCardViewModel;

    private void OnContextPreview(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card) VM?.OpenCard(card);
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

    private void OnContextToggleSelection(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card)
        {
            card.IsSelected = !card.IsSelected;
            VM?.NotifySelectionChanged();
        }
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

    private void OnContextCopyId(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card) return;
        CopyTextToClipboard(card.Id);
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

    private async void OnContextOpenPopup(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card || VM == null) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;
        var viewer = new ArtworkViewerWindow(card.Artwork, VM.GalleryVm);
        await viewer.ShowDialog(window);
    }

    private void OnContextToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card) return;
        var favs = AppServices.Get<LocalFavoritesService>();
        favs.Toggle(card.Artwork);
        card.IsLocalFavorite = favs.IsFavorite(card.Id);
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

    private void OnResetSearchClicked(object? sender, RoutedEventArgs e)
    {
        VM?.ResetSearchCommand.Execute(null);
        // Avalonia doesn't always re-measure the empty-state ScrollViewer's extent after its
        // content goes from 0 items to a full page of popular-tag chips in the same visibility
        // toggle — confirmed by the fact that dragging the (unrelated) card-size slider "wakes
        // it up" afterwards, since that triggers a real cascading layout invalidation elsewhere
        // in the tree. Invalidating just the ScrollViewer wasn't enough; force the same kind of
        // full top-down relayout by invalidating measure on the whole view.
        Dispatcher.UIThread.Post(() =>
        {
            InvalidateMeasure();
            EmptyStateScrollViewer.InvalidateMeasure();
            EmptyStateScrollViewer.UpdateLayout();
        }, DispatcherPriority.Loaded);
    }

    // ── Popular / related tag chips ──────────────────────────────────────────
    // Using a Click handler here (rather than a Command binding) — the Command binding
    // approach wasn't firing reliably for these chips.
    private void OnTagChipClicked(object? sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.DataContext switch
        {
            PopularTagInfo p => p.Tag,
            string s => s,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(tag)) return;
        _ = VM?.SearchRelatedTagCommand.ExecuteAsync(tag);
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
            var preview = new BackgroundPreviewWindow(card.ThumbnailUrl, bytes, seedEntry);
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
