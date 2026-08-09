using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Artwork;
using Pikura.Avalonia.Views.Dialogs;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Views.Gallery;

public partial class GalleryView : UserControl
{
    private bool _scrollHooked;

    public GalleryView()
    {
        InitializeComponent();
        LayoutUpdated += OnLayoutUpdated;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;

        // Wire ToggleBrowse + Expand + Close events from both viewers
        GallerySideViewer.ToggleBrowse += OnToggleBrowse;
        GallerySideViewer.ExpandViewer += OnExpandViewer;
        GallerySideViewer.ViewerClosed += OnViewerClosed;
        GalleryFullViewer.ToggleBrowse  += OnToggleBrowse;
        GalleryFullViewer.ExpandViewer  += OnExpandViewer;
        GalleryFullViewer.ViewerClosed  += OnViewerClosed;
    }

    /// <summary>Browse/panel button → toggle side-panel visibility only.</summary>
    private void OnToggleBrowse(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM != null) VM.ShowPreview = !VM.ShowPreview;
    }

    /// <summary>Expand button → toggle full-screen overlay.</summary>
    private void OnExpandViewer(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM == null) return;
        VM.IsViewerExpanded = !VM.IsViewerExpanded;
        if (!VM.IsViewerExpanded) VM.ShowPreview = true;
    }

    private void OnViewerClosed(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e) { }

    private GalleryViewModel? VM => DataContext as GalleryViewModel;

    private GalleryViewModel? _wiredVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wiredVm != null)
            _wiredVm.CopyToClipboardRequested -= OnCopyToClipboardRequested;

        if (VM is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.CopyToClipboardRequested += OnCopyToClipboardRequested;
            _wiredVm = vm;
        }
    }

    private void OnCopyToClipboardRequested(string text)
    {
        CopyToClipboard(text);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.IsFixedHeight)
            || e.PropertyName == nameof(GalleryViewModel.IsNaturalHeight)
            || e.PropertyName == nameof(GalleryViewModel.CardHeightMode))
        {
            Dispatcher.UIThread.Post(InvalidateArtworksPanel, global::Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void InvalidateArtworksPanel()
    {
        // The MasonryPanel lives inside an ItemsPanelTemplate; walk the visual tree to find it
        var panel = FindMasonryPanel(this);
        if (panel == null) return;
        // Invalidate each wrapper Panel AND its children (Fixed/Natural Border) so
        // the IsVisible change is reflected before MasonryPanel re-measures.
        foreach (var child in panel.Children)
        {
            if (child is global::Avalonia.Controls.Panel wrapperPanel)
                foreach (var sub in wrapperPanel.Children)
                    sub.InvalidateMeasure();
            child.InvalidateMeasure();
        }
        panel.InvalidateMeasure();
    }

    private static MasonryPanel? FindMasonryPanel(global::Avalonia.Visual root)
    {
        if (root is MasonryPanel mp) return mp;
        foreach (var v in global::Avalonia.VisualTree.VisualExtensions.GetVisualChildren(root))
        {
            var found = FindMasonryPanel(v);
            if (found != null) return found;
        }
        return null;
    }

    private bool _isDraggingSplitter;
    private double _dragStartX;
    private double _dragStartPanelWidth;

    private void OnSplitterPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not global::Avalonia.Controls.Border splitter || VM == null) return;
        var pt = e.GetPosition(this);
        _isDraggingSplitter = true;
        _dragStartX = pt.X;
        _dragStartPanelWidth = VM.BrowsePanelWidth;
        VM.IsResizingPanel = true;
        e.Pointer.Capture(splitter);
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object? sender, global::Avalonia.Input.PointerEventArgs e)
    {
        if (!_isDraggingSplitter || VM == null) return;
        var contentGrid = this.FindControl<Grid>("ContentGrid");
        if (contentGrid == null) return;
        var available = contentGrid.Bounds.Width;
        if (available <= 0) return;

        var pt = e.GetPosition(this);
        var dx = pt.X - _dragStartX;
        // Dragging left (dx < 0) grows the panel
        var newWidth = _dragStartPanelWidth - dx;
        var maxWidth = available - 320;
        if (newWidth < 320) newWidth = 320;
        if (newWidth > maxWidth) newWidth = maxWidth;
        VM.BrowsePanelWidth = newWidth;
    }

    private void OnSplitterPointerReleased(object? sender, global::Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (!_isDraggingSplitter || VM == null) return;
        _isDraggingSplitter = false;
        e.Pointer.Capture(null);
        // Re-enable persistence then nudge the value to trigger one save
        VM.IsResizingPanel = false;
        var w = VM.BrowsePanelWidth;
        VM.BrowsePanelWidth = w + 0.001;  // force change notification
        VM.BrowsePanelWidth = w;          // restore exact value (and persist)
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        // Clamp persisted BrowsePanelWidth to current container width so it can't exceed window
        if (VM is { } vm0)
        {
            var contentGrid = this.FindControl<Grid>("ContentGrid");
            var available = contentGrid?.Bounds.Width ?? 0;
            if (available > 400)
            {
                var maxAllowed = available - 320;
                if (vm0.BrowsePanelWidth > maxAllowed)
                    vm0.BrowsePanelWidth = maxAllowed;
            }
        }

        if (_scrollHooked) return;
        var gs = this.FindControl<ScrollViewer>("GridScroll");
        var ls = this.FindControl<ScrollViewer>("ListScroll");
        if (gs == null && ls == null) return;
        _scrollHooked = true;
        HookScrollViewer(gs);
        HookScrollViewer(ls);
        // Restore sort combo selection from persisted VM state
        if (VM is { } vm)
        {
            var combo = this.FindControl<ComboBox>("SortCombo");
            if (combo != null) combo.SelectedIndex = (int)vm.SortMode;
        }
        LayoutUpdated -= OnLayoutUpdated;
    }

    private void HookScrollViewer(ScrollViewer? sv)
    {
        if (sv == null) return;
        sv.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        var nearBottom = sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 400;
        if (nearBottom && VM is { } vm)
            _ = vm.TriggerAutoLoadAsync();
    }

    // Left-click → open inline viewer inside the gallery
    // If card is blurred (R-18 + blur setting), single click unblurs, double click opens viewer
    private void OnArtworkCardClicked(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Control ctrl) return;
        if (ctrl.DataContext is not ArtworkCardViewModel card) return;
        if (e.Handled) return; // Already handled by inner element (e.g., tag chip)
        if (e.Source is CheckBox || e.Source is Button) return;
        // Check if the source is a tag chip border by checking DataContext is string
        if (e.Source is Border srcBorder && srcBorder.DataContext is string) return;
        if (e.Source is TextBlock tb && tb.Parent is Border bp && bp.DataContext is string) return;

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
            // Single click: ensure side panel is open, then open the card as a tab in the viewer
            if (VM != null && !VM.ShowPreview) VM.ShowPreview = true;
            VM?.OpenInlineViewer(card);
        }
    }

    private void OnTagChipClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
            OnTagClicked(tag);
        e.Handled = true;
    }

    private void OnTagChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is Border b && b.DataContext is string tag)
        {
            // Shift+click = global Pixiv search; regular click = filter current view
            bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;
            if (isShiftPressed && VM is { } vm)
            {
                if (vm.SearchByTagCommand.CanExecute(tag))
                    _ = vm.SearchByTagCommand.ExecuteAsync(tag);
            }
            else if (VM is { } v)
            {
                v.TagIncludeFilter = tag;
                v.ShowFilters = true;
            }
        }
        e.Handled = true;
    }

    private void OnArtworkCheckboxClicked(object? sender, RoutedEventArgs e)
    {
        VM?.NotifySelectionChanged();
    }

    private void SwallowPointer(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnContextDownloadAll(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is { } card) _ = VM?.DownloadSingleAsync(card);
    }
    private void OnContextDownloadThisPage(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is { } card) _ = VM?.DownloadSinglePageAsync(card, 0);
    }
    private async void OnContextOpenPopup(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null || VM == null) return;
        var viewer = new ArtworkViewerWindow(card.Artwork, VM);
        await viewer.ShowDialog(window);
    }

    private void OnContextOpenSidePanel(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        VM.OpenInViewer(card);
        VM.ShowPreview = true;
    }

    private void OnContextOpenFullScreen(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        VM.OpenInViewer(card);
        VM.IsViewerExpanded = true;
    }

    private void OnContextOpenInNewTab(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        VM.OpenInNewTab(card);
    }

    private void OnContextOpenArtistGallery(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        _ = VM.LoadArtistByIdCommand.ExecuteAsync(card.UserId);
    }
    private void OnContextCopyId(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        CopyToClipboard(card.Id);
    }

    // "Copy artist ID" on artwork cards — answers the user request to grab an
    // unfollowed artist's ID without first having to follow them. Mirrors the
    // same routing as the inline viewer's context menu so behaviour is
    // identical no matter which surface the user clicks from.
    private void OnContextCopyArtistId(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        if (string.IsNullOrWhiteSpace(card.UserId)) return;
        CopyToClipboard(card.UserId);
        try { Services.QuickClipboardService.CopyArtist(card.UserId); } catch { /* non-fatal */ }
        if (VM != null) VM.StatusMessage = $"Copied artist ID {card.UserId} ({card.UserName})";
    }

    private void OnContextCopyImage(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        CopyBitmapToClipboard(card.Thumbnail);
    }

    private void OnContextUseAsBackground(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        if (string.IsNullOrWhiteSpace(card.ThumbnailUrl)) return;
        try { AppServices.Get<BackgroundOverlayService>().AddImage(card.ThumbnailUrl); }
        catch { /* non-fatal */ }
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
    }
    private void OnContextOpenPixiv(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        OpenUrl($"https://www.pixiv.net/artworks/{card.Id}");
    }

    private void OnContextToggleSelection(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        card.IsSelected = !card.IsSelected;
        VM?.NotifySelectionChanged();
    }

    private void OnContextToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        var favs = AppServices.Get<Pikura.Core.Services.LocalFavoritesService>();
        favs.Toggle(card.Artwork);
        card.IsLocalFavorite = favs.IsFavorite(card.Id);
    }

    private void OnHoshiButtonClicked(object? sender, RoutedEventArgs e)
    {
        var window = this.VisualRoot as Window ?? TopLevel.GetTopLevel(this) as Window;
        if (window is MainWindow mw) mw.HoshiButton_Click(sender, e);
    }

    private void OnContextToggleFollow(object? sender, RoutedEventArgs e)
    {
        // Follow/unfollow feature removed - Pixiv OAuth no longer available
        if (VM != null)
        {
            VM.StatusMessage = "Follow/unfollow is not available. Pixiv has blocked OAuth authentication.";
        }
    }

    private void OnContextUgoiraOptions(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        // Open the ugoira in Image Editor which will show the options dialog
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var ugoiraService = AppServices.Get<UgoiraService>();
                var imageLoader = AppServices.Get<PixivImageLoader>();

                // Create EditableArtwork with IllustType = 2 (ugoira)
                var editable = new EditableArtwork
                {
                    ArtworkId = card.Id,
                    Title = card.Title,
                    UserName = card.UserName,
                    PageCount = card.PageCount,
                    IllustType = 2 // Mark as ugoira
                };

                // Show the Image Editor which will detect ugoira and show options
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
                    VM.StatusMessage = $"Ugoira options failed: {ex.Message}";
                });
            }
        });
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void OnCardContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
        => ApplyContextMenuVisibility(sender as ContextMenu);

    private void OnListCardContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
        => ApplyContextMenuVisibility(sender as ContextMenu);

    private void ApplyContextMenuVisibility(ContextMenu? menu)
    {
        if (menu is null) return;
        if (menu.PlacementTarget is not Control ctrl) return;
        if (ctrl.DataContext is not ArtworkCardViewModel card) return;

        bool overlayEnabled = false;
        try { overlayEnabled = AppServices.Get<BackgroundOverlayService>().IsEnabled; }
        catch { /* non-fatal */ }

        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi || mi.Header is not string header) continue;
            if (header.Contains("Ugoira"))
                mi.IsVisible = card.IllustType == 2;
            else if (header.Contains("app background"))
                mi.IsVisible = overlayEnabled;
        }
    }

    private void OnTagClicked(string tag)
    {
        if (VM?.SearchByTagCommand is { } cmd && cmd.CanExecute(tag))
            _ = cmd.ExecuteAsync(tag);
    }

    private static ArtworkCardViewModel? GetCardFromMenu(object? sender)
    {
        // Context menu DataContext IS the ArtworkCardViewModel (set by x:DataType binding)
        if (sender is MenuItem { DataContext: ArtworkCardViewModel card }) return card;
        // Fallback: walk up to ContextMenu and use PlacementTarget
        if (sender is MenuItem mi)
        {
            var cm = mi.Parent as ContextMenu ?? mi.GetLogicalParent<ContextMenu>();
            if (cm?.PlacementTarget is Control ctrl)
                return ctrl.DataContext as ArtworkCardViewModel;
        }
        return null;
    }

    private void OnIdSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) VM?.SearchByIdCommand.Execute(null);
    }

    private void OnSortComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (VM == null || sender is not ComboBox cb) return;
        VM.SortMode = (ArtworkSortMode)cb.SelectedIndex;
    }

    private void OnClearFilters(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        VM.TagIncludeFilter = string.Empty;
        VM.TagExcludeFilter = string.Empty;
        VM.DateFrom = null;
        VM.DateTo = null;
        VM.SortMode = ArtworkSortMode.Default;
        if (this.FindControl<ComboBox>("SortCombo") is { } combo) combo.SelectedIndex = 0;
    }

    private async void OnDownloadPresetClicked(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm == null) return;

        // Priority: 1) selected artworks  2) currently viewed (inline viewer)  3) error
        var picked = vm.VisibleArtworks.Where(a => a.IsSelected).ToList();
        if (picked.Count == 0 && vm.InlineViewerCard != null)
        {
            // Use the currently viewed artwork
            picked = new List<ArtworkCardViewModel> { vm.InlineViewerCard };
        }

        if (picked.Count == 0)
        {
            vm.StatusMessage = "No artwork selected or open. Click an artwork or select multiple first.";
            return;
        }

        // Show preset window with the first artwork as preview
        var dialogService = AppServices.Get<DialogService>();
        var firstArtwork = picked[0].Artwork;
        var additionalArtworks = picked.Skip(1).Select(c => c.Artwork).ToList();

        var preset = await dialogService.ShowDownloadPresetDialogAsync(firstArtwork, additionalArtworks);
        if (preset != null)
        {
            foreach (var card in picked)
            {
                await vm.DownloadWithPresetAsync(card, preset);
            }
            vm.StatusMessage = $"Queued {picked.Count} artwork(s) for download with preset: {preset.Name}";
        }
    }

    private async void OnDownloadRangeClicked(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm?.SelectedArtist == null) return;
        var total = vm.ArtworksTotal;
        if (total <= 0) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        var dialog = new RangePickerDialog(
            title: $"Download range — {vm.SelectedArtist.Name}",
            description: $"{vm.SelectedArtist.Name} has {total} artworks (newest-first). " +
                         "Enter positions like \"1-20\", \"1,5-10,50\".",
            maxInclusive: total,
            placeholder: $"1-{Math.Min(total, 20)}");

        var ok = await dialog.ShowDialog<bool?>(window);
        if (ok == true && dialog.SelectedIndexes.Count > 0)
            await vm.DownloadArtworkRangeAsync(dialog.SelectedIndexes);
    }

    private void OnArtistIdPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        // Get UserId from Tag since DataContext might not be available directly
        var userId = tb.Tag?.ToString();
        if (string.IsNullOrEmpty(userId)) return;

        if (e.GetCurrentPoint(tb).Properties.IsLeftButtonPressed)
        {
            CopyArtistIdToClipboard(userId);

            // Visual feedback - change text temporarily
            var originalText = tb.Text;
            tb.Text = $"ID {userId} ✓ copied!";
            tb.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(34, 197, 94)); // Green

            // Reset after 2 seconds
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    tb.Text = originalText;
                    tb.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#9CA3AF"));
                });
            });

            e.Handled = true; // Prevent row selection when clicking ID
        }
    }

    private void OnArtistRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Allow right-click to show context menu
        if (e.GetCurrentPoint((Control?)sender).Properties.IsRightButtonPressed)
        {
            // Context menu will show automatically
            return;
        }
    }

    private void OnOpenArtistPageMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        var userId = menuItem.Tag?.ToString();
        if (!string.IsNullOrEmpty(userId))
        {
            var url = $"https://www.pixiv.net/en/users/{userId}";
            OpenUrl(url);
        }
    }

    private void OnCopyArtistIdMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        var userId = menuItem.Tag?.ToString();
        if (!string.IsNullOrEmpty(userId))
        {
            CopyArtistIdToClipboard(userId);
        }
    }

    private void OnUnfollowArtistMenu(object? sender, RoutedEventArgs e)
    {
        // Follow/unfollow feature removed - Pixiv OAuth no longer available
        if (VM != null)
        {
            VM.StatusMessage = "Unfollow is not available. Pixiv has blocked OAuth authentication.";
        }
    }

    private void CopyArtistIdToClipboard(string userId)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        var dt = new DataTransfer();
        dt.Add(DataTransferItem.CreateText(userId));
        _ = clipboard.SetDataAsync(dt);

        // Also save to quick clipboard
        QuickClipboardService.CopyArtist(userId);
    }
    
    private async void OnDownloadAllClicked(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm == null) return;
        await vm.DownloadAllAsync();
    }
    
    private async void OnDownloadLoadedClicked(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm == null) return;
        await vm.DownloadVisibleAsync();
    }
    
    private async void OnDownloadSelectedClicked(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm == null) return;
        await vm.DownloadSelectedAsync();
    }
}
