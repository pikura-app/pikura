using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Artwork;
using Pikura.Avalonia.Views.Dialogs;
using Pikura.Avalonia.Views.Gallery;
using Pikura.Core.Models;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Views.Discover;

public partial class DiscoverView : UserControl
{
    private DiscoverViewModel? VM => DataContext as DiscoverViewModel;

    public DiscoverView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DiscoverInlineViewer.ToggleBrowse += OnViewerToggleBrowse;
        DiscoverInlineViewer.ExpandViewer  += OnExpandViewer;
        DiscoverInlineViewer.ViewerClosed += OnViewerClosed;
        DiscoverSidePanelViewer.ToggleBrowse += OnViewerToggleBrowse;
        DiscoverSidePanelViewer.ExpandViewer  += OnExpandViewer;
        DiscoverSidePanelViewer.ViewerClosed += OnViewerClosed;
        ArtistWorksSidePanelViewer.ToggleBrowse += OnViewerToggleBrowse;
        ArtistWorksSidePanelViewer.ExpandViewer  += OnExpandViewer;
        ArtistWorksSidePanelViewer.ViewerClosed += OnViewerClosed;
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
        // Close the Browse panel too so we return cleanly to the grid
        if (VM != null) VM.ShowPreview = false;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (VM is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.CopyAllArtistIdsRequested += text => CopyToClipboard(text);
            WireInlineViewer(vm);
        }
    }

    private void WireInlineViewer(DiscoverViewModel vm)
    {
        var gvm = vm.GalleryVm;
        foreach (var v in AllViewers())
            if (v != null) v.DataContext = gvm;
    }

    private InlineArtworkViewer?[] AllViewers() =>
        [DiscoverInlineViewer, DiscoverSidePanelViewer, ArtistWorksSidePanelViewer];

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (VM is not { } vm) return;
        var gvm = vm.GalleryVm;
        if (gvm.InlineViewerCard == null) return;
        foreach (var v in AllViewers())
        {
            if (v == null) continue;
            v.DataContext = null;
            v.DataContext = gvm;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) { }

    private bool _isDraggingSplitter;
    private double _dragStartX;
    private double _dragStartPanelWidth;

    private void OnSplitterPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not global::Avalonia.Controls.Border splitter || VM == null) return;
        _isDraggingSplitter = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartPanelWidth = VM.BrowsePanelWidth;
        VM.IsResizingPanel = true;
        e.Pointer.Capture(splitter);
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object? sender, global::Avalonia.Input.PointerEventArgs e)
    {
        if (!_isDraggingSplitter || VM == null) return;
        var bodyGrid = VM.IsWorksTab ? WorksBodyGrid : ArtistWorksBodyGrid;
        if (bodyGrid == null) return;
        var available = bodyGrid.Bounds.Width;
        if (available <= 0) return;

        var pt = e.GetPosition(this);
        var dx = pt.X - _dragStartX;
        var newWidth = _dragStartPanelWidth - dx;
        var maxWidth = available - 300;
        if (newWidth < 300) newWidth = 300;
        if (newWidth > maxWidth) newWidth = maxWidth;
        VM.BrowsePanelWidth = newWidth;
    }

    private void OnSplitterPointerReleased(object? sender, global::Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (!_isDraggingSplitter || VM == null) return;
        _isDraggingSplitter = false;
        e.Pointer.Capture(null);
        VM.IsResizingPanel = false;
        var w = VM.BrowsePanelWidth;
        VM.BrowsePanelWidth = w + 0.001;
        VM.BrowsePanelWidth = w;
    }

    // ── Scroll auto-load ────────────────────────────────────────────────────

    private void OnWorksScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (VM is not { CanLoadMoreRecommended: true, IsLoading: false }) return;
        if (sender is not ScrollViewer sv) return;
        if (sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height < 400)
            _ = VM.LoadMoreWorksAsync();
    }

    // ── Artwork card click → unblur or open viewer ──────────────────────────

    private static ArtworkCardViewModel? CardFromTap(TappedEventArgs e)
    {
        var ctrl = e.Source as Control;
        while (ctrl != null)
        {
            if (ctrl.DataContext is ArtworkCardViewModel c) return c;
            ctrl = ctrl.Parent as Control;
        }
        return null;
    }

    private void OnScrollViewerTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled) return;
        var card = CardFromTap(e);
        if (card == null) return;
        e.Handled = true;
        if (card.IsBlurred)
            card.IsBlurred = false;   // single click: unblur
        else
            OpenInlineViewer(card);   // already unblurred: open viewer
    }

    private void OnScrollViewerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled) return;
        var card = CardFromTap(e);
        if (card == null) return;
        e.Handled = true;
        card.IsBlurred = false;       // ensure unblurred first
        OpenInlineViewer(card);       // double click: always open side panel
    }

    private void OpenInlineViewer(ArtworkCardViewModel card, bool fullScreen = false)
    {
        if (VM == null) return;
        var galleryVm = VM.GalleryVm;
        var src = VM.IsWorksTab ? VM.FilteredWorks.ToList() : VM.FilteredArtistWorks.ToList();
        var target = src.FirstOrDefault(c => c.Id == card.Id) ?? card;
        var source = VM.IsWorksTab ? "Discover:Works" : $"Discover:Artist:{VM.SelectedUser?.UserId}";
        Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMore = null;
        if (VM.IsWorksTab)
        {
            loadMore = async () =>
            {
                await VM.LoadMoreWorksAsync();
                return VM.FilteredWorks.ToList();
            };
        }

        // Ensure all viewers have the right DataContext
        foreach (var v in AllViewers())
            if (v != null && v.DataContext != galleryVm) v.DataContext = galleryVm;

        galleryVm.OpenInViewer(target, src, loadMoreAsync: loadMore, source: source);
        if (fullScreen)
            VM.IsViewerExpanded = true;
        else
            VM.ShowPreview = true;
    }

    // ── Tag chip click → filter or search ───────────────────────────────────

    private void OnTagChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Border b || b.DataContext is not string tag) return;
        e.Handled = true;
        if ((e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift)
        {
            // Shift+click: navigate to Gallery then fire a global tag search
            if (TopLevel.GetTopLevel(this) is not MainWindow main) return;
            main.LoadGalleryView();
            var galleryVm = VM?.GalleryVm ?? AppServices.Get<GalleryViewModel>();
            if (galleryVm.SearchByTagCommand.CanExecute(tag))
                _ = galleryVm.SearchByTagCommand.ExecuteAsync(tag);
        }
        else
        {
            // Regular click: toggle tag filter on current view
            if (VM != null)
                VM.TagFilter = VM.TagFilter == tag ? string.Empty : tag;
        }
    }

    // ── User card click → select artist (populate right panel) ─────────────

    private void OnUserCardClicked(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (e.Handled) return;
        if (sender is not Control ctrl) return;
        if (ctrl.DataContext is not DiscoveryUserCardViewModel user) return;
        e.Handled = true;
        if (VM != null) VM.SelectedUser = user;
    }

    // ── Artist works grid tap → open inline viewer ───────────────────────────

    private void OnArtistWorksScrollViewerTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled) return;
        var card = CardFromTap(e);
        if (card == null) return;
        e.Handled = true;
        if (card.IsBlurred)
            card.IsBlurred = false;   // single click: unblur
        else
            OpenInlineViewer(card);   // already unblurred: open viewer
    }

    private void OnArtistWorksScrollViewerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled) return;
        var card = CardFromTap(e);
        if (card == null) return;
        e.Handled = true;
        card.IsBlurred = false;       // ensure unblurred first
        OpenInlineViewer(card);       // double click: always open side panel
    }

    // ── ↗ button on each user row → open Gallery for that artist ─────────────

    private void OnUserOpenGalleryClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        // The button lives inside a DataTemplate whose DataContext is DiscoveryUserCardViewModel
        if (sender is Control ctrl && ctrl.DataContext is DiscoveryUserCardViewModel user)
            NavigateToArtistGallery(user.UserId);
    }

    // ── "Open Gallery" button in right panel header ───────────────────────────

    private void OnOpenSelectedUserGalleryClicked(object? sender, RoutedEventArgs e)
    {
        if (VM?.SelectedUser is { } user)
            NavigateToArtistGallery(user.UserId);
    }

    // Clicking the ID text copies it + shows green flash; don't bubble to card
    private void OnUserIdPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not TextBlock tb) return;
        if (tb.DataContext is not DiscoveryUserCardViewModel user) return;
        e.Handled = true;

        CopyToClipboard(user.UserId);
        try { QuickClipboardService.CopyArtist(user.UserId); } catch { }
        if (VM != null) VM.StatusMessage = $"Copied artist ID {user.UserId} ({user.UserName})";

        // Green flash: detach binding → set "✓ copied" text → reattach after delay
        var originalBrush = tb.Foreground;
        tb.ClearValue(TextBlock.TextProperty); // detach binding
        tb.Text = $"ID {user.UserId} ✓ copied!";
        tb.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(34, 197, 94));
        var uid = user.UserId;
        System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                tb.Foreground = originalBrush;
                tb.Bind(TextBlock.TextProperty,
                    new global::Avalonia.Data.Binding(nameof(DiscoveryUserCardViewModel.UserId))
                    { StringFormat = "ID {0}" });
            }));
    }

    private async void NavigateToArtistGallery(string userId)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow main) return;
        main.LoadGalleryView();
        if (VM?.GalleryVm != null)
        {
            await VM.GalleryVm.LoadArtistByIdAsync(userId);
        }
    }

    // ── Artwork context menu helpers ────────────────────────────────────────

    private static ArtworkCardViewModel? GetCardFromMenu(object? sender)
    {
        if (sender is not MenuItem mi) return null;
        if (mi.DataContext is ArtworkCardViewModel c) return c;
        var cm = mi.Parent as ContextMenu ?? mi.GetLogicalParent<ContextMenu>();
        return cm?.PlacementTarget is Control ctrl ? ctrl.DataContext as ArtworkCardViewModel : null;
    }

    private static DiscoveryUserCardViewModel? GetUserFromMenu(object? sender)
    {
        if (sender is not MenuItem mi) return null;
        if (mi.DataContext is DiscoveryUserCardViewModel u) return u;
        var cm = mi.Parent as ContextMenu ?? mi.GetLogicalParent<ContextMenu>();
        return cm?.PlacementTarget is Control ctrl ? ctrl.DataContext as DiscoveryUserCardViewModel : null;
    }

    // ── Artwork context menu handlers ───────────────────────────────────────

    private void OnContextPreview(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is { } card) OpenInlineViewer(card);  // sets ShowPreview = true
    }

    private void OnContextOpenFullScreen(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        OpenInlineViewer(card, fullScreen: true);
    }

    private void OnContextOpenInNewTab(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        var list = VM.IsWorksTab ? VM.FilteredWorks.ToList() : VM.FilteredArtistWorks.ToList();
        var target = list.FirstOrDefault(c => c.Id == card.Id) ?? card;
        var source = VM.IsWorksTab ? "Discover:Works" : $"Discover:Artist:{VM.SelectedUser?.UserId}";
        Func<Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMore = null;
        if (VM.IsWorksTab)
        {
            loadMore = async () =>
            {
                await VM.LoadMoreWorksAsync();
                return VM.FilteredWorks.ToList();
            };
        }
        VM.GalleryVm.OpenInNewTab(target, list, loadMoreAsync: loadMore, source: source);
    }

    private async void OnContextOpenPopup(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        if (TopLevel.GetTopLevel(this) is not Window window || VM == null) return;
        var viewer = new ArtworkViewerWindow(card.Artwork, VM.GalleryVm);
        await viewer.ShowDialog(window);
    }

    private void OnContextDownload(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card || VM == null) return;
        _ = VM.GalleryVm.DownloadSingleAsync(card);
    }

    private async void OnDownloadPresetClicked(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm == null) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        // Priority: 1) selected artworks  2) currently viewed (inline viewer)  3) error
        var works = vm.IsWorksTab ? vm.RecommendedWorks : vm.ArtistWorks;
        var picked = works.Where(a => a.IsSelected).ToList();
        if (picked.Count == 0 && vm.GalleryVm.InlineViewerCard != null)
        {
            // Find inline viewer card in current tab's list, else use it directly
            var inlineId = vm.GalleryVm.InlineViewerCard.Id;
            var match = works.FirstOrDefault(c => c.Id == inlineId);
            picked = new List<ArtworkCardViewModel> { match ?? vm.GalleryVm.InlineViewerCard };
        }

        if (picked.Count == 0)
        {
            vm.StatusMessage = "No artwork selected or open. Click an artwork or select multiple first.";
            return;
        }

        // Use DialogService to show preset dialog
        var dialogService = AppServices.Get<DialogService>();
        var firstArtwork = picked[0].Artwork;
        var additionalArtworks = picked.Skip(1).Select(c => c.Artwork).ToList();

        var result = await dialogService.ShowDownloadPresetDialogAsync(firstArtwork, additionalArtworks);

        if (result != null)
        {
            // Download all selected artworks with the preset using cards
            foreach (var card in picked)
            {
                await vm.GalleryVm.DownloadWithPresetAsync(card, result);
            }
            vm.StatusMessage = $"Queued {picked.Count} artwork(s) for download with preset: {result.Name}";
        }
    }

    private void OnContextOpenArtistGallery(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        NavigateToArtistGallery(card.UserId);
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

    private async void OnContextUseAsBackground(object? sender, RoutedEventArgs e)
    {
        try { if (!AppServices.Get<BackgroundOverlayService>().IsEnabled) return; }
        catch { return; }
        if (GetCardFromMenu(sender) is not { } card) return;
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

    private void OnContextToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if (GetCardFromMenu(sender) is not { } card) return;
        var favs = AppServices.Get<Pikura.Core.Services.LocalFavoritesService>();
        favs.Toggle(card.Artwork);
        card.IsLocalFavorite = favs.IsFavorite(card.Id);
    }

    private void CopyBitmapToClipboard(global::Avalonia.Media.Imaging.Bitmap? bmp)
    {
        if (bmp == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        _ = clipboard.SetBitmapAsync(bmp);
    }

    // ── User context menu handlers ──────────────────────────────────────────

    private void OnContextUserOpenGallery(object? sender, RoutedEventArgs e)
    {
        if (GetUserFromMenu(sender) is not { } user) return;
        NavigateToArtistGallery(user.UserId);
    }

    private void OnContextUserOpenPixiv(object? sender, RoutedEventArgs e)
    {
        if (GetUserFromMenu(sender) is not { } user) return;
        OpenUrl($"https://www.pixiv.net/users/{user.UserId}");
    }

    private void OnContextUserCopyId(object? sender, RoutedEventArgs e)
    {
        if (GetUserFromMenu(sender) is not { } user) return;
        CopyToClipboard(user.UserId);
    }

    private void OnCardCheckboxClicked(object? sender, RoutedEventArgs e)
    {
        // Notify ViewModel that selection changed
        VM?.NotifySelectionChanged();
    }

    private void OnArtworkCardPressed(object? sender, PointerPressedEventArgs e)
    {
        // Don't select if clicking on the checkbox directly (it handles its own toggle)
        if (e.Source is CheckBox) return;

        // Get the card DataContext
        if (sender is Border border && border.DataContext is ArtworkCardViewModel card)
        {
            // Toggle selection
            card.IsSelected = !card.IsSelected;
            VM?.NotifySelectionChanged();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void CopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        var dt = new DataTransfer();
        dt.Add(DataTransferItem.CreateText(text));
        _ = clipboard.SetDataAsync(dt);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
