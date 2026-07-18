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
using Pikura.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pikura.Avalonia.Views.Bookmarks;

public partial class BookmarksView : UserControl
{
    private BookmarksViewModel? VM => DataContext as BookmarksViewModel;

    public BookmarksView()
    {
        InitializeComponent();
        BookmarksInlineViewer.ToggleBrowse += OnViewerToggleBrowse;
        BookmarksInlineViewer.ExpandViewer  += OnExpandViewer;
        BookmarksInlineViewer.ViewerClosed  += OnViewerClosed;
        BookmarksFullViewer.ToggleBrowse   += OnViewerToggleBrowse;
        BookmarksFullViewer.ExpandViewer   += OnExpandViewer;
        BookmarksFullViewer.ViewerClosed   += OnViewerClosed;
        DataContextChanged += OnDataContextChanged;
        LayoutUpdated += OnLayoutUpdated;
        SortComboBox.SelectionChanged += OnSortChanged;
        AttachedToVisualTree += (_, _) =>
        {
            if (VM is not { } vm) return;
            if (vm.GalleryVm.HasTabs && !vm.ShowPreview)
                vm.ShowPreview = true;
            // Force the viewer to reload the current card — without this, navigating
            // back to Bookmarks when a tab is already open leaves the image blank because
            // IsVisible never changed (HasTabs was already true) so the re-trigger in
            // OnPropertyChanged never fired.
            var gvm = vm.GalleryVm;
            BookmarksInlineViewer.DataContext = null;
            BookmarksInlineViewer.DataContext = gvm;
            BookmarksFullViewer.DataContext = null;
            BookmarksFullViewer.DataContext = gvm;
        };
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (VM is not { } vm) return;
        var grid = this.FindControl<Grid>("BookmarksBodyGrid");
        var available = grid?.Bounds.Width ?? 0;
        if (available > 400)
        {
            var maxAllowed = available - 320;
            if (vm.BrowsePanelWidth > maxAllowed)
                vm.BrowsePanelWidth = maxAllowed;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (VM is { } vm)
            vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // When height mode changes, just nudge the masonry panels so they remeasure children.
        // Card heights are now driven directly by a converter, so no IsVisible toggling tricks needed.
        if (e.PropertyName is nameof(BookmarksViewModel.IsFixedHeight)
                         or nameof(BookmarksViewModel.IsNaturalHeight))
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateMasonryPanels, global::Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void InvalidateMasonryPanels()
    {
        foreach (var panel in this.GetVisualDescendants().OfType<global::Pikura.Avalonia.Views.Gallery.MasonryPanel>())
        {
            foreach (var child in panel.Children)
                child.InvalidateMeasure();
            panel.InvalidateMeasure();
        }
    }

    // ── Inline viewer events ────────────────────────────────────────────────
    private void OnViewerToggleBrowse(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM != null) VM.ShowPreview = !VM.ShowPreview;
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

    // ── Tab pills ───────────────────────────────────────────────────────────
    private void OnTabClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.Tag is string s && int.TryParse(s, out var idx) && VM != null)
        {
            foreach (var c in VM.FilteredPublic.Concat(VM.FilteredPrivate).Concat(VM.FilteredFavorites))
                c.IsSelected = false;
            VM.SelectedTabIndex = idx;
            VM.NotifySelectionChanged();
        }
    }

    // ── Card click → unblur or open viewer ───────────────────────────────────
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

    private void OnCardTapped(object? sender, TappedEventArgs e)
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

    private void OnCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled) return;
        var card = CardFromTap(e);
        if (card == null) return;
        e.Handled = true;
        card.IsBlurred = false;
        OpenInlineViewer(card);       // double click: always open side panel
    }

    private void OpenInlineViewer(ArtworkCardViewModel card)
    {
        if (VM == null) return;
        var list = VM.SelectedTabIndex switch
        {
            0 => VM.FilteredPublic.ToList(),
            1 => VM.FilteredPrivate.ToList(),
            _ => VM.FilteredFavorites.ToList(),
        };
        var target = list.FirstOrDefault(c => c.Id == card.Id) ?? card;
        Func<System.Threading.Tasks.Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMore = null;
        if (VM.SelectedTabIndex != 2)
        {
            loadMore = async () =>
            {
                await VM.LoadMoreAsync();
                return VM.SelectedTabIndex == 0 ? VM.FilteredPublic.ToList() : VM.FilteredPrivate.ToList();
            };
        }
        var total = VM.SelectedTabIndex == 2 ? list.Count : Math.Max(VM.TotalCount, list.Count);
        VM.GalleryVm.OpenInViewer(target, list, total, loadMore, VM.ViewerSourceKey);
        VM.ShowPreview = true;
    }

    // ── Context menu helpers ────────────────────────────────────────────────
    private static ArtworkCardViewModel? GetCard(object? sender)
    {
        if (sender is not MenuItem mi) return null;
        if (mi.DataContext is ArtworkCardViewModel c) return c;
        var cm = mi.Parent as ContextMenu ?? mi.GetLogicalParent<ContextMenu>();
        return cm?.PlacementTarget is Control ctrl ? ctrl.DataContext as ArtworkCardViewModel : null;
    }

    // ── Context menu handlers ───────────────────────────────────────────────
    private void OnContextPreview(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is { } card) OpenInlineViewer(card);  // sets ShowPreview = true
    }

    private void OnContextOpenFullScreen(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        OpenInlineViewer(card);
        VM.IsViewerExpanded = true;
    }

    private void OnContextOpenInNewTab(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        var list = VM.SelectedTabIndex switch
        {
            0 => VM.FilteredPublic.ToList(),
            1 => VM.FilteredPrivate.ToList(),
            _ => VM.FilteredFavorites.ToList(),
        };
        var target = list.FirstOrDefault(c => c.Id == card.Id) ?? card;
        Func<System.Threading.Tasks.Task<IReadOnlyList<ArtworkCardViewModel>>>? loadMore = null;
        if (VM.SelectedTabIndex != 2)
        {
            loadMore = async () =>
            {
                await VM.LoadMoreAsync();
                return VM.SelectedTabIndex == 0 ? VM.FilteredPublic.ToList() : VM.FilteredPrivate.ToList();
            };
        }
        var total = VM.SelectedTabIndex == 2 ? list.Count : Math.Max(VM.TotalCount, list.Count);
        VM.GalleryVm.OpenInNewTab(target, list, total, loadMore, VM.ViewerSourceKey);
    }

    private async void OnContextOpenPopup(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var viewer = new ArtworkViewerWindow(card.Artwork, VM.GalleryVm);
        await viewer.ShowDialog(window);
    }

    private void OnContextDownload(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        _ = VM.GalleryVm.DownloadSingleAsync(card);
    }

    private async void OnContextDownloadPreset(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;

        var dialogService = AppServices.Get<DialogService>();
        var result = await dialogService.ShowDownloadPresetDialogAsync(card.Artwork);

        if (result != null)
        {
            _ = VM.GalleryVm.DownloadWithPresetAsync(card, result);
        }
    }

    private void OnContextOpenArtistGallery(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card) return;
        if (TopLevel.GetTopLevel(this) is not MainWindow main || VM == null) return;
        main.LoadGalleryView();
        _ = VM.GalleryVm.LoadArtistByIdCommand.ExecuteAsync(card.UserId);
    }

    private void OnContextOpenPixiv(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card) return;
        OpenUrl($"https://www.pixiv.net/artworks/{card.Id}");
    }

    private void OnContextAddFavorite(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        VM.ToggleFavorite(card);
    }

    private void OnContextRemoveFavorite(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        VM.ToggleFavorite(card);
    }

    private void OnContextSelectFavorite(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        card.IsSelected = true;
        VM.NotifySelectionChanged();
    }

    private void OnFavCardCheckboxClicked(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        VM.NotifySelectionChanged();
        e.Handled = true; // prevent card click from firing
    }

    private async void OnSelectionSetFolder(object? sender, RoutedEventArgs e)
    {
        if (VM == null || !VM.HasSelection) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        // Show existing folders as quick-pick buttons plus a text input
        var dialog = new Window
        {
            Title = $"Move {VM.SelectedFavoritesCount} items to folder",
            Width = 380, Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var tb = new TextBox { Watermark = "New folder name (or pick below)", Margin = new global::Avalonia.Thickness(12, 12, 12, 6) };
        var existingPanel = new WrapPanel { Margin = new global::Avalonia.Thickness(12, 0, 12, 6) };
        foreach (var f in VM.AvailableFolders)
        {
            var btn = new Button { Content = f, Margin = new global::Avalonia.Thickness(0, 0, 4, 4), Padding = new global::Avalonia.Thickness(8, 4), CornerRadius = new global::Avalonia.CornerRadius(4), FontSize = 11 };
            btn.Click += (_, _) => dialog.Close(f);
            existingPanel.Children.Add(btn);
        }
        var btnRow = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Margin = new global::Avalonia.Thickness(12, 0, 12, 12), Spacing = 6 };
        var ok = new Button { Content = "Move" };
        var clear = new Button { Content = "Remove from folder" };
        ok.Click += (_, _) => dialog.Close(tb.Text?.Trim());
        clear.Click += (_, _) => dialog.Close(string.Empty);
        tb.KeyDown += (_, ke) => { if (ke.Key == global::Avalonia.Input.Key.Return) dialog.Close(tb.Text?.Trim()); };
        btnRow.Children.Add(clear);
        btnRow.Children.Add(ok);
        dialog.Content = new StackPanel { Children = { tb, existingPanel, btnRow } };

        var result = await dialog.ShowDialog<string?>(window);
        if (result == null) return; // cancelled
        VM.SetFolderForSelected(string.IsNullOrEmpty(result) ? null : result);
    }

    private async void OnContextSetFolder(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        var current = VM.GetFolderForCard(card.Id) ?? string.Empty;

        // Build a simple input dialog using a TextBox in a Window
        var dialog = new Window
        {
            Title = "Set Folder",
            Width = 340, Height = 140,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var tb = new TextBox { Text = current, Watermark = "Folder name (empty = none)", Margin = new global::Avalonia.Thickness(16, 16, 16, 8), VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center };
        var ok = new Button { Content = "OK", HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Margin = new global::Avalonia.Thickness(0, 0, 16, 12) };
        ok.Click += (_, _) => dialog.Close(tb.Text?.Trim());
        tb.KeyDown += (_, ke) => { if (ke.Key == global::Avalonia.Input.Key.Return) dialog.Close(tb.Text?.Trim()); };
        dialog.Content = new StackPanel { Children = { tb, ok } };

        var result = await dialog.ShowDialog<string?>(window);
        VM.SetFolderForCard(card, result);
    }

    private void OnContextCopyId(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card) return;
        CopyToClipboard(card.Id);
    }

    private void OnContextCopyImage(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null && card.Thumbnail != null)
            _ = clipboard.SetBitmapAsync(card.Thumbnail);
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

    // ── Custom splitter for inline viewer side panel ────────────────────────
    private bool _isDraggingSplitter;
    private double _dragStartX;
    private double _dragStartPanelWidth;

    private void OnSplitterPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not global::Avalonia.Controls.Border splitter || VM == null) return;
        _isDraggingSplitter = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartPanelWidth = VM.BrowsePanelWidth;
        e.Pointer.Capture(splitter);
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object? sender, global::Avalonia.Input.PointerEventArgs e)
    {
        if (!_isDraggingSplitter || VM == null) return;
        var grid = this.FindControl<Grid>("BookmarksBodyGrid");
        if (grid == null) return;
        var available = grid.Bounds.Width;
        if (available <= 0) return;

        var pt = e.GetPosition(this);
        var dx = pt.X - _dragStartX;
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
        var w = VM.BrowsePanelWidth;
        VM.BrowsePanelWidth = w + 0.001;
        VM.BrowsePanelWidth = w;
    }

    private async void OnDownloadPresetClicked(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;

        // Priority: 1) current selection  2) artwork open in side panel viewer  3) nothing
        var artworksToDownload = VM.SelectedTabIndex switch
        {
            0 => VM.FilteredPublic.Where(c => c.IsSelected).Select(c => c.Artwork).ToList(),
            1 => VM.FilteredPrivate.Where(c => c.IsSelected).Select(c => c.Artwork).ToList(),
            _ => VM.FilteredFavorites.Where(c => c.IsSelected).Select(c => c.Artwork).ToList(),
        };

        // If no selection but something is open in the viewer, use that
        if (artworksToDownload.Count == 0 && VM.GalleryVm.InlineViewerCard != null)
        {
            artworksToDownload = new List<ArtworkPreview> { VM.GalleryVm.InlineViewerCard.Artwork };
        }

        if (artworksToDownload.Count == 0)
        {
            VM.StatusMessage = "Select bookmarks first or open one in the side panel.";
            return;
        }

        var dialogService = AppServices.Get<DialogService>();
        var firstArtwork = artworksToDownload.First();
        var result = await dialogService.ShowDownloadPresetDialogAsync(firstArtwork, artworksToDownload.Skip(1).ToList());

        if (result != null)
        {
            // Download all artworks with the selected preset using the GalleryViewModel's method
            foreach (var artwork in artworksToDownload)
            {
                var card = new ArtworkCardViewModel(artwork);
                _ = VM.GalleryVm.DownloadWithPresetAsync(card, result);
            }
        }
    }

    private void OnSortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (VM is not { } vm) return;
        vm.SortMode = BookmarksViewModel.SortModeFromIndex(SortComboBox.SelectedIndex);
    }
}
