using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Artwork;
using Pikura.Avalonia.Views.Gallery;
using Pikura.Core.Models;
using Pikura.Core.Services;

namespace Pikura.Avalonia.Views.Collections;

public partial class CollectionsView : UserControl
{
    private CollectionsViewModel? VM => DataContext as CollectionsViewModel;

    private CollectionsViewModel? _subscribedVm;

    public CollectionsView()
    {
        InitializeComponent();

        // Wire ToggleBrowse + Expand + Close events from both viewers (side panel + full-screen
        // overlay) — same pattern as GalleryView/BookmarksView. Without this, the viewer's own
        // internal "Hide Panel"/"Expand" buttons had no effect on ShowPreview/IsViewerExpanded.
        CollectionsInlineViewer.ToggleBrowse += OnToggleBrowse;
        CollectionsInlineViewer.ExpandViewer += OnExpandViewer;
        CollectionsInlineViewer.ViewerClosed += OnViewerClosed;
        CollectionsInlineViewer.RequestFullscreen += OnRequestFullscreen;
        CollectionsFullViewer.ToggleBrowse += OnToggleBrowse;
        CollectionsFullViewer.ExpandViewer += OnExpandViewer;
        CollectionsFullViewer.ViewerClosed += OnViewerClosed;
        CollectionsFullViewer.RequestFullscreen += OnRequestFullscreen;

        DataContextChanged += OnDataContextChanged;
        LayoutUpdated += OnLayoutUpdated;
    }

    // ── Side-panel splitter drag-resize — mirrors GalleryView's identical pattern ──────────
    private bool _isDraggingSplitter;
    private double _dragStartX;
    private double _dragStartPanelWidth;

    private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border splitter || VM == null) return;
        var pt = e.GetPosition(this);
        _isDraggingSplitter = true;
        _dragStartX = pt.X;
        _dragStartPanelWidth = VM.PanelWidth;
        VM.IsResizingPanel = true;
        e.Pointer.Capture(splitter);
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingSplitter || VM == null) return;
        var contentGrid = this.FindControl<Grid>("CollectionsContentGrid");
        if (contentGrid == null) return;
        var available = contentGrid.Bounds.Width;
        if (available <= 0) return;

        var pt = e.GetPosition(this);
        var dx = pt.X - _dragStartX;
        // Dragging left (dx < 0) grows the panel
        var newWidth = _dragStartPanelWidth - dx;
        var maxWidth = available - 350;
        if (newWidth < 350) newWidth = 350;
        if (newWidth > maxWidth) newWidth = maxWidth;
        VM.PanelWidth = newWidth;
    }

    private void OnSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSplitter || VM == null) return;
        _isDraggingSplitter = false;
        e.Pointer.Capture(null);
        // Re-enable persistence then nudge the value to trigger one save
        VM.IsResizingPanel = false;
        var w = VM.PanelWidth;
        VM.PanelWidth = w + 0.001;  // force change notification
        VM.PanelWidth = w;          // restore exact value (and persist)
    }

    private void OnLayoutUpdated(object? sender, System.EventArgs e)
    {
        // Clamp persisted PanelWidth to current container width so it can't exceed window
        if (VM is { } vm0)
        {
            var contentGrid = this.FindControl<Grid>("CollectionsContentGrid");
            var available = contentGrid?.Bounds.Width ?? 0;
            if (available > 450)
            {
                var maxAllowed = available - 350;
                if (vm0.PanelWidth > maxAllowed)
                    vm0.PanelWidth = maxAllowed;
            }
        }
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.CollectionComments.CollectionChanged -= OnCollectionCommentsChanged;
        if (VM is { } vm)
            vm.CollectionComments.CollectionChanged += OnCollectionCommentsChanged;
        _subscribedVm = VM;
        RenderCollectionComments();
    }

    private void OnCollectionCommentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RenderCollectionComments();

    /// <summary>Builds each comment row in code, so stickers and emoji shortcodes ("(normal)",
    /// "(shock3)", etc.) render as actual images instead of literal text — mirrors
    /// InlineArtworkViewer's BuildCommentRow/BuildCommentTextWithEmoji.</summary>
    private void RenderCollectionComments()
    {
        CollectionCommentsPanel.Children.Clear();
        if (VM == null) return;
        var imageLoader = AppServices.Get<PixivImageLoader>();
        foreach (var c in VM.CollectionComments)
            CollectionCommentsPanel.Children.Add(BuildCollectionCommentRow(imageLoader, c));
    }

    private Control BuildCollectionCommentRow(PixivImageLoader imageLoader, Pikura.Core.Models.PixivComment c)
    {
        var avatar = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16),
            ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0),
        };
        if (!string.IsNullOrEmpty(c.UserImageUrl))
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            avatar.Child = img;
            _ = LoadPickerImageIntoAsync(imageLoader, img, c.UserImageUrl);
        }

        var body = new StackPanel { Spacing = 2 };
        var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        metaRow.Children.Add(new TextBlock { Text = c.UserName, FontSize = 12, FontWeight = FontWeight.SemiBold });
        metaRow.Children.Add(new TextBlock
        {
            Text = c.CommentDate ?? string.Empty, FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (c.Editable)
        {
            var deleteBtn = new Button { Content = "Delete", FontSize = 10, Padding = new Thickness(6, 2), CornerRadius = new CornerRadius(4) };
            deleteBtn.Click += (_, _) => VM?.DeleteCollectionCommentCommand.Execute(c);
            metaRow.Children.Add(deleteBtn);
        }
        body.Children.Add(metaRow);

        if (c.HasStamp && string.IsNullOrEmpty(c.Comment))
        {
            var stampImg = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            body.Children.Add(stampImg);
            _ = LoadPickerImageIntoAsync(imageLoader, stampImg, $"https://source.pixiv.net/common/images/stamp/generated-stamps/{c.StampId}_s.jpg?20180605");
        }
        else
        {
            body.Children.Add(BuildCollectionCommentTextWithEmoji(imageLoader, c.Comment ?? string.Empty));
        }

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(avatar, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(avatar);
        grid.Children.Add(body);
        return grid;
    }

    private static readonly System.Lazy<System.Text.RegularExpressions.Regex> CollectionEmojiShortcodeRegexLazy = new(() =>
        new System.Text.RegularExpressions.Regex(@"\((" + string.Join("|", InlineArtworkViewer.EmojiCatalog.Select(e => System.Text.RegularExpressions.Regex.Escape(e.Shortcode))) + @")\)"));

    private Control BuildCollectionCommentTextWithEmoji(PixivImageLoader imageLoader, string text)
    {
        var panel = new WrapPanel();
        var lastEnd = 0;
        foreach (System.Text.RegularExpressions.Match m in CollectionEmojiShortcodeRegexLazy.Value.Matches(text))
        {
            if (m.Index > lastEnd)
                panel.Children.Add(new TextBlock { Text = text[lastEnd..m.Index], FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            var id = InlineArtworkViewer.EmojiCatalog.FirstOrDefault(e => e.Shortcode == m.Groups[1].Value).Id;
            var emojiImg = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform, Margin = new Thickness(1, 0) };
            _ = LoadPickerImageIntoAsync(imageLoader, emojiImg, $"https://source.pixiv.net/common/images/emoji/{id}.png");
            panel.Children.Add(emojiImg);
            lastEnd = m.Index + m.Length;
        }
        if (lastEnd < text.Length)
            panel.Children.Add(new TextBlock { Text = text[lastEnd..], FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    /// <summary>Browse/panel button → toggle side-panel visibility only.</summary>
    private void OnToggleBrowse(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM != null) VM.ShowPreview = !VM.ShowPreview;
    }

    /// <summary>Expand button → toggle full-screen overlay. Collage mode (IsCollageMode/
    /// CollageItems on the shared GalleryViewModel) is bound directly in XAML, so it's already
    /// reflected on whichever viewer instance becomes visible — no manual sync needed here.</summary>
    private void OnExpandViewer(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM == null) return;
        VM.IsViewerExpanded = !VM.IsViewerExpanded;
        if (!VM.IsViewerExpanded) VM.ShowPreview = true;
    }

    /// <summary>"Fullscreen" while in Collage mode — unconditionally goes full-screen (never
    /// toggles off), unlike OnExpandViewer.</summary>
    private void OnRequestFullscreen(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM == null) return;
        VM.IsViewerExpanded = true;
    }

    private void OnViewerClosed(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM == null) return;
        VM.ShowPreview = false;
        VM.IsViewerExpanded = false;
    }

    private void OnUrlBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            VM?.LoadCommand.Execute(null);
        }
    }

    private void OnCardClicked(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if ((sender as Control)?.DataContext is not ArtworkCardViewModel card) return;
        if (card.IsBlurred)
            card.IsBlurred = false;   // single click: unblur R-18 content first, same as Gallery/Bookmarks
        else
            VM?.OpenArtworkCommand.Execute(card);
    }

    private void OnSiblingTileClicked(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if ((sender as Control)?.DataContext is CollectionTileViewModel tile)
            VM?.LoadSiblingCommand.Execute(tile.Id);
    }

    private void OnBrowseTileClicked(object? sender, PointerPressedEventArgs e)
    {
        // Left-click only — right-click should open the ContextMenu (attached to the tile's
        // outer Border), not also trigger opening the collection.
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if ((sender as Control)?.DataContext is not CollectionTileViewModel tile) return;
        if (tile.IsBlurred)
            tile.IsBlurred = false;   // single click: unblur R-18 collections first, same as artworks
        else
            VM?.OpenCollectionTileCommand.Execute(tile);
    }

    // ── Browse-tile context menu (copy actions need the DataContext resolved via the menu,
    // not the sender, since these are plain Click handlers rather than bound Commands) ──────

    private static CollectionTileViewModel? GetTileFromMenu(object? sender)
    {
        if (sender is not MenuItem mi) return null;
        if (mi.DataContext is CollectionTileViewModel t) return t;
        var cm = mi.Parent as ContextMenu ?? mi.GetLogicalParent<ContextMenu>();
        return cm?.PlacementTarget is Control ctrl ? ctrl.DataContext as CollectionTileViewModel : null;
    }

    private void OnContextCopyCollectionId(object? sender, RoutedEventArgs e)
    {
        if (GetTileFromMenu(sender) is { } tile) CopyToClipboard(tile.Id);
    }

    private void OnContextCopyCollectionLink(object? sender, RoutedEventArgs e)
    {
        if (GetTileFromMenu(sender) is { } tile) CopyToClipboard($"https://www.pixiv.net/collections/{tile.Id}");
    }

    private void OnWorkCheckboxClicked(object? sender, RoutedEventArgs e)
    {
        VM?.NotifyWorksSelectionChanged();
        e.Handled = true; // prevent the card's PointerPressed (open viewer) from also firing
    }

    private void OnBrowseTileCheckboxClicked(object? sender, RoutedEventArgs e)
    {
        VM?.NotifySelectionChanged();
    }

    private void OnAllCollectionsPageInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            VM?.JumpToAllCollectionsPageCommand.Execute(null);
        }
    }

    private void OnCommentBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;
            VM?.PostCollectionCommentCommand.Execute(null);
        }
    }

    // ── Collection comment sticker/emoji picker — same Emoji/Stickers split as
    // InlineArtworkViewer's own picker, just posting through the Collection comment endpoints
    // instead of the artwork ones. ───────────────────────────────────────────
    private bool _collectionStickerGridBuilt;
    private bool _collectionEmojiGridBuilt;

    private void OnCollectionStickerPickerClicked(object? sender, RoutedEventArgs e)
    {
        CollectionStickerPickerPopup.IsOpen = !CollectionStickerPickerPopup.IsOpen;
        if (!CollectionStickerPickerPopup.IsOpen) return;
        if (!_collectionEmojiGridBuilt && CollectionEmojiTabBtn.IsChecked == true) BuildCollectionEmojiGrid();
    }

    private void OnCollectionEmojiTabClicked(object? sender, RoutedEventArgs e)
    {
        CollectionEmojiTabBtn.IsChecked = true;
        CollectionStickersTabBtn.IsChecked = false;
        CollectionEmojiScrollViewer.IsVisible = true;
        CollectionStickerScrollViewer.IsVisible = false;
        CollectionStickerLoadingPanel.IsVisible = false;
        if (!_collectionEmojiGridBuilt) BuildCollectionEmojiGrid();
    }

    private void OnCollectionStickersTabClicked(object? sender, RoutedEventArgs e)
    {
        CollectionEmojiTabBtn.IsChecked = false;
        CollectionStickersTabBtn.IsChecked = true;
        CollectionEmojiScrollViewer.IsVisible = false;
        CollectionStickerScrollViewer.IsVisible = true;
        if (!_collectionStickerGridBuilt) BuildCollectionStickerGrid();
    }

    private void BuildCollectionEmojiGrid()
    {
        _collectionEmojiGridBuilt = true;
        var imageLoader = AppServices.Get<PixivImageLoader>();
        foreach (var (id, shortcode) in InlineArtworkViewer.EmojiCatalog)
        {
            var shortcodeText = $"({shortcode})";
            var tile = new Button
            {
                Width = 36, Height = 36, Padding = new Thickness(4), Margin = new Thickness(1),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(tile, shortcodeText);
            var img = new Image { Stretch = Stretch.Uniform };
            tile.Content = img;
            _ = LoadPickerImageIntoAsync(imageLoader, img, $"https://source.pixiv.net/common/images/emoji/{id}.png");
            tile.Click += (_, _) =>
            {
                var box = NewCollectionCommentBox;
                var caret = box.CaretIndex;
                var text = box.Text ?? string.Empty;
                box.Text = text[..caret] + shortcodeText + text[caret..];
                box.CaretIndex = caret + shortcodeText.Length;
                box.Focus();
            };
            CollectionEmojiGrid.Children.Add(tile);
        }
    }

    private void BuildCollectionStickerGrid()
    {
        _collectionStickerGridBuilt = true;
        CollectionStickerLoadingPanel.IsVisible = true;
        var imageLoader = AppServices.Get<PixivImageLoader>();

        const int maxId = 600;
        var remaining = maxId;
        var foundAny = false;

        void OnOneFinished(bool found)
        {
            if (found) foundAny = true;
            if (System.Threading.Interlocked.Decrement(ref remaining) == 0)
            {
                CollectionStickerLoadingPanel.IsVisible = false;
                if (!foundAny)
                {
                    CollectionStickerLoadingPanel.IsVisible = true;
                    ((TextBlock)CollectionStickerLoadingPanel.Children[1]).Text = "No stickers could be loaded.";
                    CollectionStickerLoadingPanel.Children[0].IsVisible = false;
                }
            }
        }

        for (int id = 1; id <= maxId; id++)
        {
            var stampId = id;
            var tile = new Button
            {
                Width = 40, Height = 40, Padding = new Thickness(2), Margin = new Thickness(2),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                IsVisible = false,
            };
            var img = new Image { Stretch = Stretch.Uniform };
            tile.Content = img;
            tile.Click += async (_, _) =>
            {
                CollectionStickerPickerPopup.IsOpen = false;
                if (VM != null) await VM.PostCollectionStickerCommand.ExecuteAsync(stampId);
            };
            CollectionStickerGrid.Children.Add(tile);
            _ = LoadCollectionStickerTileAsync(imageLoader, img, tile, stampId.ToString(), OnOneFinished);
        }
    }

    private async Task LoadPickerImageIntoAsync(PixivImageLoader imageLoader, Image img, string url)
    {
        try
        {
            var skBitmap = await imageLoader.FetchBitmapAsync(url, ThumbnailSize.Small, System.Threading.CancellationToken.None);
            if (skBitmap == null) return;
            var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap));
            skBitmap.Dispose();
            if (bmp != null) img.Source = bmp;
        }
        catch { /* decorative — non-fatal */ }
    }

    private async Task LoadCollectionStickerTileAsync(PixivImageLoader imageLoader, Image img, Button tile, string stampId, System.Action<bool> onFinished)
    {
        try
        {
            var skBitmap = await imageLoader.FetchBitmapAsync(
                $"https://source.pixiv.net/common/images/stamp/generated-stamps/{stampId}_s.jpg?20180605",
                ThumbnailSize.Small, System.Threading.CancellationToken.None);
            if (skBitmap == null) { onFinished(false); return; }
            var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap));
            skBitmap.Dispose();
            if (bmp == null) { onFinished(false); return; }
            img.Source = bmp;
            tile.IsVisible = true;
            onFinished(true);
        }
        catch { onFinished(false); }
    }

    // ── Context menu (same actions as Gallery/Bookmarks, reusing GalleryVm) ────

    private static ArtworkCardViewModel? GetCard(object? sender)
    {
        if (sender is not MenuItem mi) return null;
        if (mi.DataContext is ArtworkCardViewModel c) return c;
        var cm = mi.Parent as ContextMenu ?? mi.GetLogicalParent<ContextMenu>();
        return cm?.PlacementTarget is Control ctrl ? ctrl.DataContext as ArtworkCardViewModel : null;
    }

    private void OnContextToggleSelection(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        card.IsSelected = !card.IsSelected;
        VM.NotifyWorksSelectionChanged();
    }

    private void OnContextOpenSidePanel(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is { } card) VM?.OpenArtworkCommand.Execute(card);
    }

    private void OnContextOpenFullScreen(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        VM.OpenArtworkCommand.Execute(card);
        VM.IsViewerExpanded = true;
    }

    private void OnContextOpenInNewTab(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        VM.GalleryVm.OpenInNewTab(card, VM.Works.ToList(), VM.Works.Count, null, VM.ViewerSourceKey);
        VM.ShowPreview = true;
    }

    private async void OnContextOpenPopup(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var viewer = new ArtworkViewerWindow(card.Artwork, VM.GalleryVm);
        await viewer.ShowDialog(window);
    }

    private void OnContextDownloadAll(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is { } card) _ = VM?.GalleryVm.DownloadSingleAsync(card);
    }

    private void OnContextDownloadThisPage(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is { } card) _ = VM?.GalleryVm.DownloadSinglePageAsync(card, 0);
    }

    private void OnContextOpenArtistGallery(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        if (TopLevel.GetTopLevel(this) is MainWindow main) main.LoadGalleryView();
        _ = VM.GalleryVm.LoadArtistByIdCommand.ExecuteAsync(card.UserId);
    }

    private async void OnContextToggleFollow(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || VM == null) return;
        if (string.IsNullOrEmpty(card.UserId)) return;
        var pixivClient = AppServices.Get<PixivClient>();
        var ok = card.IsFollowed
            ? await pixivClient.UnfollowUserAsync(card.UserId)
            : await pixivClient.FollowUserAsync(card.UserId);
        if (!ok) return;
        var followed = !card.IsFollowed;
        card.IsFollowed = followed;
        VM.GalleryVm.SetArtistFollowed(card.UserId, card.UserName, followed);
        VM.StatusMessage = followed ? $"Following {card.UserName}" : $"Unfollowed {card.UserName}";
    }

    private void OnContextToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card) return;
        var favs = AppServices.Get<LocalFavoritesService>();
        favs.Toggle(card.Artwork);
        card.IsLocalFavorite = favs.IsFavorite(card.Id);
    }

    private void OnContextCopyId(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is { } card) CopyToClipboard(card.Id);
    }

    private void OnContextCopyArtistId(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card || string.IsNullOrWhiteSpace(card.UserId)) return;
        CopyToClipboard(card.UserId);
        try { QuickClipboardService.CopyArtist(card.UserId); } catch { /* non-fatal */ }
        if (VM != null) VM.StatusMessage = $"Copied artist ID {card.UserId} ({card.UserName})";
    }

    private void OnContextCopyImage(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is not { } card) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null && card.Thumbnail != null)
            _ = clipboard.SetBitmapAsync(card.Thumbnail);
    }

    private async void OnContextUseAsBackground(object? sender, RoutedEventArgs e)
    {
        try { if (!AppServices.Get<BackgroundOverlayService>().IsEnabled) return; }
        catch { return; }
        if (GetCard(sender) is not { } card || string.IsNullOrWhiteSpace(card.ThumbnailUrl)) return;
        try
        {
            var overlay = AppServices.Get<BackgroundOverlayService>();
            var bytes = await overlay.FetchImageBytesAsync(card.ThumbnailUrl);
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window == null) { overlay.AddImage(card.ThumbnailUrl); return; }

            var seedEntry = new Pikura.Core.Settings.OverlayImageEntry
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

    private void OnContextOpenPixiv(object? sender, RoutedEventArgs e)
    {
        if (GetCard(sender) is { } card) OpenUrl($"https://www.pixiv.net/artworks/{card.Id}");
    }

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
        catch { /* non-fatal */ }
    }
}
