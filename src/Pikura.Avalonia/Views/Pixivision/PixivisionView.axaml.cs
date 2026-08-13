using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Artwork;
using Pikura.Avalonia.Views.Dialogs;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.Views.Pixivision;

public partial class PixivisionView : UserControl
{
    private PixivisionViewModel? VM => DataContext as PixivisionViewModel;
    private double _lastSidePanelWidth = 520;

    public PixivisionView()
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
                // The tab list is global across every section (Gallery, Search, Viewed, etc.) —
                // if another section already has tabs open, surface the side panel here too so
                // switching into Pixivision doesn't hide them.
                if (VM != null && AppServices.Get<GalleryViewModel>().HasTabs)
                    VM.ShowPreview = true;
            }
            catch { }

            var inlineViewer = this.FindControl<Pikura.Avalonia.Views.Gallery.InlineArtworkViewer>("PixivisionInlineViewer");
            if (inlineViewer != null)
            {
                inlineViewer.ToggleBrowse += OnViewerToggleBrowse;
                inlineViewer.ExpandViewer += OnExpandViewer;
                inlineViewer.ViewerClosed += OnViewerClosed;
            }
            var overlayViewer = this.FindControl<Pikura.Avalonia.Views.Gallery.InlineArtworkViewer>("PixivisionOverlayViewer");
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
                if (e.PropertyName == nameof(PixivisionViewModel.ShowPreview))
                    ApplyShowPreview(vm.ShowPreview);
                else if (e.PropertyName == nameof(PixivisionViewModel.ArticleEyecatch))
                    UpdateEyecatchHeight();
            };
            ApplyShowPreview(vm.ShowPreview);

            var border = this.FindControl<Border>("EyecatchBorder");
            if (border != null)
                border.SizeChanged += (_, _) => UpdateEyecatchHeight();
            UpdateEyecatchHeight();
        }
    }

    // Sizes the eyecatch banner from the actual bitmap's aspect ratio and the border's rendered
    // width, so UniformToFill never has to crop away much of the image (unlike a fixed height).
    private void UpdateEyecatchHeight()
    {
        var border = this.FindControl<Border>("EyecatchBorder");
        if (border == null || VM?.ArticleEyecatch is not { } bmp || bmp.PixelSize.Width <= 0) return;
        var width = border.Bounds.Width;
        if (width <= 0) return;
        var ratio = (double)bmp.PixelSize.Height / bmp.PixelSize.Width;
        border.Height = Math.Clamp(width * ratio, 160, 520);
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

    private void OnCategoryTabClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control ctrl) return;
        if (ctrl.Tag is not Pikura.Core.Models.PixivisionCategory category) return;
        VM?.SelectCategoryCommand.Execute(category);
    }

    private void OnArticleScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (VM is not { IsAutoloadMode: true, HasNextPage: true, IsLoading: false, IsFilteredByDate: false }) return;
        if (sender is not ScrollViewer sv) return;
        if (sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height < 300)
            _ = VM.LoadMoreArticlesAsync();
    }

    private async void OnCalendarOpened(object? sender, EventArgs e)
    {
        var cal = this.FindControl<Calendar>("DatePopupCalendar");
        if (cal == null || VM == null) return;
        cal.DisplayDateEnd = DateTime.Today;
        if (VM.SelectedDate is { } d) cal.SelectedDate = d;
        await RefreshCalendarBlackoutsAsync(cal);
    }

    private void OnCalendarDisplayDateChanged(object? sender, CalendarDateChangedEventArgs e)
    {
        if (sender is Calendar cal) _ = RefreshCalendarBlackoutsAsync(cal);
    }

    /// <summary>Grays out (blacks out) days pixivision has no articles for in the calendar's
    /// currently displayed month — see <see cref="PixivisionViewModel.EnsureMonthScannedAsync"/>.</summary>
    private async Task RefreshCalendarBlackoutsAsync(Calendar cal)
    {
        if (VM == null) return;
        var month = cal.DisplayDate;
        await VM.EnsureMonthScannedAsync(month);
        var emptyDays = VM.GetEmptyDaysInMonth(month);

        cal.BlackoutDates.Clear();
        foreach (var day in emptyDays)
        {
            // Avalonia's Calendar throws if a blackout range contains the currently selected date.
            if (cal.SelectedDate?.Date == day.Date) continue;
            try { cal.BlackoutDates.Add(new CalendarDateRange(day)); } catch { }
        }
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

    // ── Custom date range — mirrors ViewedHistoryView's identical pattern ──────
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

    private void OnPixivisionPageInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            VM?.GoToPageInputCommand.Execute(null);
        }
    }

    private void OnParagraphLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void OnTocEntryClicked(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        if (sender is not Button { Tag: int ordinal }) return;

        var headings = VM.ArticleParagraphs.Where(p => p.IsHeading).ToList();
        if (ordinal < 0 || ordinal >= headings.Count) return;
        var target = headings[ordinal];

        var container = ArticleParagraphsItemsControl
            .GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => ReferenceEquals(c.DataContext, target));
        if (container == null) return;

        // BringIntoView() only scrolls the minimum amount needed, which lands the heading right
        // at the bottom edge of the viewport. Scroll it near the top instead, with a little
        // breathing room above it.
        const double topPadding = 20;
        var offsetInScrollViewer = container.TranslatePoint(new Point(0, 0), ArticleDetailScrollViewer) ?? default;
        var targetY = ArticleDetailScrollViewer.Offset.Y + offsetInScrollViewer.Y - topPadding;
        ArticleDetailScrollViewer.Offset = new Vector(ArticleDetailScrollViewer.Offset.X, Math.Max(0, targetY));
    }

    private void OnContextOpenArticlePixivision(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        PixivisionArticleCardViewModel? article = mi.DataContext as PixivisionArticleCardViewModel;
        if (article == null)
        {
            var cm = mi.Parent as ContextMenu ?? mi.GetLogicalParent<ContextMenu>();
            article = (cm?.PlacementTarget as Control)?.DataContext as PixivisionArticleCardViewModel;
        }
        if (article == null) return;
        VM?.OpenArticleCardInBrowserCommand.Execute(article);
    }

    private void OnContextToggleSaveArticle(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        PixivisionArticleCardViewModel? article = mi.DataContext as PixivisionArticleCardViewModel;
        if (article == null)
        {
            var cm = mi.Parent as ContextMenu ?? mi.GetLogicalParent<ContextMenu>();
            article = (cm?.PlacementTarget as Control)?.DataContext as PixivisionArticleCardViewModel;
        }
        if (article == null) return;
        VM?.ToggleSaveCardCommand.Execute(article);
    }

    private void OnBookmarkIconClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PixivisionArticleCardViewModel article }) return;
        VM?.ToggleSaveCardCommand.Execute(article);
    }

    private void OnArticleCardClicked(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (e.Source is Button) return;
        if (sender is not Control ctrl) return;
        if (ctrl.DataContext is not PixivisionArticleCardViewModel article) return;
        e.Handled = true;
        VM?.OpenArticleCommand.Execute(article);
    }

    private void OnArticleOpenInBrowserClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control ctrl) return;
        if (ctrl.DataContext is not PixivisionArticleCardViewModel article) return;
        VM?.OpenArticleCardInBrowserCommand.Execute(article);
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
            card.IsBlurred = false;
        else
            VM.OpenCard(card);
    }

    private void OnCardCheckboxClicked(object? sender, RoutedEventArgs e) => VM?.NotifySelectionChanged();

    // ── Context menu ──────────────────────────────────────────────────────────

    private static ArtworkCardViewModel? CardFrom(object? sender)
    {
        if (sender is MenuItem { DataContext: ArtworkCardViewModel card }) return card;
        if (sender is MenuItem mi)
        {
            var cm = mi.Parent as ContextMenu ?? mi.GetLogicalParent<ContextMenu>();
            if (cm?.PlacementTarget is Control ctrl)
                return ctrl.DataContext as ArtworkCardViewModel;
        }
        return null;
    }

    private void OnCardContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (menu.PlacementTarget is not Control ctrl) return;
        if (ctrl.DataContext is not ArtworkCardViewModel card) return;

        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi || mi.Header is not string header) continue;
            if (header.Contains("Ugoira"))
                mi.IsVisible = card.IllustType == 2;
        }
    }

    private void OnContextPreview(object? sender, RoutedEventArgs e) => OnContextOpenSidePanel(sender, e);

    private void OnContextToggleSelection(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card)
        {
            card.IsSelected = !card.IsSelected;
            VM?.NotifySelectionChanged();
        }
    }

    private void OnContextDownloadAll(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card) _ = VM?.GalleryVm.DownloadSingleAsync(card);
    }

    private void OnContextDownloadThisPage(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card) _ = VM?.GalleryVm.DownloadSinglePageAsync(card, 0);
    }

    private void OnContextOpenSidePanel(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is { } card) VM?.OpenCard(card);
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
        VM.GalleryVm.OpenInNewTab(card, VM.FeaturedWorks.ToList(), VM.FeaturedWorks.Count, null, VM.ViewerSourceKey);
        VM.ShowPreview = true;
    }

    private async void OnContextOpenPopup(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card || VM == null) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;
        var viewer = new ArtworkViewerWindow(card.Artwork, VM.GalleryVm);
        await viewer.ShowDialog(window);
    }

    private void OnContextOpenPixiv(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card) return;
        try { Process.Start(new ProcessStartInfo($"https://www.pixiv.net/artworks/{card.Id}") { UseShellExecute = true }); }
        catch { }
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

    private void OnContextUgoiraOptions(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var editable = new EditableArtwork
                {
                    ArtworkId = card.Id,
                    Title = card.Title,
                    UserName = card.UserName,
                    PageCount = card.PageCount,
                    IllustType = 2
                };

                var editor = new ImageEditorWindow(
                    AppServices.Get<ImageResizeService>(),
                    new List<EditableArtwork> { editable },
                    initialArtworkIndex: 0,
                    initialPageIndex: 0);

                await Dispatcher.UIThread.InvokeAsync(async () => await editor.ShowDialog(window));
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (VM != null) VM.StatusMessage = $"Ugoira options failed: {ex.Message}";
                });
            }
        });
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
        if (VM != null) VM.StatusMessage = $"Copied artist ID {card.UserId} ({card.UserName})";
    }

    private void OnContextCopyImage(object? sender, RoutedEventArgs e)
    {
        if (CardFrom(sender) is not { } card) return;
        var bmp = card.Thumbnail;
        if (bmp == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        _ = clipboard.SetBitmapAsync(bmp);
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

    private async void OnDownloadPresetClicked(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm == null) return;

        var picked = vm.FeaturedWorks.Where(c => c.IsSelected).ToList();
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
}
