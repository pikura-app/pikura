using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using System.Linq;
using Pikura.Avalonia.ViewModels;
using Avalonia.Platform.Storage;

namespace Pikura.Avalonia.Views.Library;

public partial class LibraryView : UserControl
{
    private bool _isDraggingSplitter;
    private double _dragStartX;
    private double _dragStartPanelWidth;
    private LibraryViewModel? VM => DataContext as LibraryViewModel;

    public LibraryView()
    {
        InitializeComponent();
        LibraryInlineViewer.ToggleBrowse += OnViewerToggleBrowse;
        LibraryInlineViewer.ExpandViewer += OnExpandViewer;
        LibraryInlineViewer.ViewerClosed += OnViewerClosed;
        AttachedToVisualTree += (_, _) =>
        {
            if (VM is not { } vm) return;
            if (vm.GalleryVm.HasTabs && !vm.ShowPreview) vm.ShowPreview = true;
            // Same reload trick as Bookmarks — forces the viewer to rebind the current card.
            var gvm = vm.GalleryVm;
            LibraryInlineViewer.DataContext = null;
            LibraryInlineViewer.DataContext = gvm;
        };
    }

    private void OnViewerToggleBrowse(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM is { } vm) vm.ShowPreview = !vm.ShowPreview;
    }

    private void OnExpandViewer(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM is { } vm)
        {
            vm.IsViewerExpanded = !vm.IsViewerExpanded;
            if (!vm.IsViewerExpanded) vm.ShowPreview = true;
        }
    }

    private void OnViewerClosed(object? sender, RoutedEventArgs e)
    { if (VM is { } vm) vm.ShowPreview = false; }

    private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border splitter || VM == null) return;
        _isDraggingSplitter = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartPanelWidth = VM.BrowsePanelWidth;
        e.Pointer.Capture(splitter);
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingSplitter || VM == null) return;
        var grid = this.FindControl<Grid>("LibraryBodyGrid");
        if (grid == null) return;
        var available = grid.Bounds.Width;
        if (available <= 0) return;
        var dx = e.GetPosition(this).X - _dragStartX;
        var w = Math.Clamp(_dragStartPanelWidth - dx, 300, Math.Max(300, available - 300));
        VM.BrowsePanelWidth = w;
    }

    private void OnSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSplitter || VM == null) return;
        _isDraggingSplitter = false;
        e.Pointer.Capture(null);
        var w = VM.BrowsePanelWidth;
        VM.BrowsePanelWidth = w + 0.001; // nudge so the persisted width change fires
        VM.BrowsePanelWidth = w;
    }

    private void BrowseTab_Click(object? sender, RoutedEventArgs e)
    { if (DataContext is LibraryViewModel vm) vm.TabIndex = 0; }

    private void DuplicatesTab_Click(object? sender, RoutedEventArgs e)
    { if (DataContext is LibraryViewModel vm) vm.TabIndex = 1; }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null || DataContext is not LibraryViewModel vm) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Pikura.Avalonia.Localization.LocalizationService.Instance["Library_PickFolderTitle"],
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrEmpty(path))
            await vm.AddFolderCommand.ExecuteAsync(path);
    }

    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse) { _tapPtr = e.Pointer.Type; return; }

        if (sender is not Border { DataContext: LibraryItemViewModel vm }) return;
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var lvm = DataContext as LibraryViewModel;
        if (ctrl || shift) { lvm?.ApplyItemClick(vm, ctrl, shift); return; }
        lvm?.ApplyItemClick(vm, false, false);
        // Plain click → open artwork in the shared side panel (reverse search when no id).
        _ = lvm?.OpenInViewerCommand.ExecuteAsync(vm);
    }

    // Built in code: a ContextMenu lives in a popup outside the visual tree, so XAML
    // $parent[...] command bindings can't reach the page and every item came out disabled.
    private void OnItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: LibraryItemViewModel item } target || VM is not { } vm) return;
        e.Handled = true;
        if (!item.IsSelected) vm.ApplyItemClick(item, false, false);
        static string T(string key) => Pikura.Avalonia.Localization.LocalizationService.Instance[key];
        static MenuItem Item(string header, Action onClick)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => onClick();
            return mi;
        }
        var hasPixiv = item.File.ArtworkId != null;
        var items = new System.Collections.Generic.List<Control>();

        if (vm.SelectedCount > 1)
        {
            items.Add(new MenuItem { Header = string.Format(T("Library_SelectedCount"), vm.SelectedCount), IsEnabled = false });
            items.Add(Item(T("Library_OpenFile"), () => _ = vm.OpenSelectedCommand.ExecuteAsync(null)));
            items.Add(Item(T("Library_OpenFoldersBtn"), () => _ = vm.ShowSelectedInFoldersCommand.ExecuteAsync(null)));
            items.Add(Item(T("Library_CopyPathsBtn"), () => _ = vm.CopySelectedPathsCommand.ExecuteAsync(null)));
            items.Add(new Separator());
            items.Add(Item(T("Library_Delete"), () => _ = vm.DeleteSelectedCommand.ExecuteAsync(null)));
        }
        else
        {
            items.Add(Item(T("Discover_OpenInSidePanel"), () => _ = vm.OpenInViewerCoreAsync(item, newTab: false)));
            items.Add(Item(T("Discover_OpenFullScreen"), async () =>
            {
                await vm.OpenInViewerCoreAsync(item, newTab: false);
                vm.IsViewerExpanded = true;
            }));
            items.Add(Item(T("Gallery_OpenInNewTab_2"), () => _ = vm.OpenInViewerCoreAsync(item, newTab: true)));
            if (hasPixiv) items.Add(Item(T("Collections_OpenInPopup"), () => OpenPopup(item)));
            items.Add(new Separator());
            items.Add(Item(T("Library_OpenFile"), () => vm.OpenFileCommand.Execute(item)));
            items.Add(Item(T("Library_ShowInFolder"), () => vm.ShowInFolderCommand.Execute(item)));
            items.Add(Item(T("Library_OpenFolderItem"), () => vm.OpenSingleFolderCommand.Execute(item)));
            items.Add(Item(T("Library_CopyPath"), () => _ = vm.CopyPathCommand.ExecuteAsync(item)));
            items.Add(Item(T("Discover_CopyImage"), () => CopyImage(item)));
            items.Add(new Separator());
            if (hasPixiv)
            {
                items.Add(Item(T("Library_GoToArtist"), () => _ = vm.GoToArtistCommand.ExecuteAsync(item)));
                items.Add(Item(T("Library_OpenOnPixiv"), () => vm.OpenOnPixivCommand.Execute(item)));
            }
            if (item.File.HasSource)
                items.Add(Item(T("Library_OpenSource"), () => vm.OpenSourceCommand.Execute(item)));
            if (item.File.OriginalUrl != null)
                items.Add(Item(T("Library_CopyOriginalUrl"), () => _ = vm.CopyOriginalUrlCommand.ExecuteAsync(item)));
            if (hasPixiv)
            {
                items.Add(Item(T("Library_FetchInfo"), () => _ = vm.FetchInfoAsync(item)));
            }
            items.Add(Item(T("Library_FindOriginal"), () => _ = vm.ReverseSearchCommand.ExecuteAsync(item)));
            var browser = new MenuItem { Header = T("Library_SearchInBrowser") };
            foreach (var (name, site) in new[] { ("SauceNAO", "saucenao"), ("ascii2d", "ascii2d"), ("Google Lens", "lens") })
                browser.Items.Add(Item(name, () => _ = vm.SearchInBrowserCommand.ExecuteAsync(site)));
            items.Add(browser);
            items.Add(new Separator());
            items.Add(Item(T("Library_TagWithAi"), () => _ = vm.TagWithAiCommand.ExecuteAsync(item)));
            items.Add(Item(T("Library_Delete"), () => _ = vm.DeleteFileCommand.ExecuteAsync(item)));
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(target);
    }

    private async void OpenPopup(LibraryItemViewModel item)
    {
        if (VM is not { } vm || item.File.ArtworkId is not { } id || TopLevel.GetTopLevel(this) is not Window window) return;
        try
        {
            var preview = new Pikura.Core.Models.ArtworkPreview
            {
                Id = id,
                Title = item.File.Title ?? System.IO.Path.GetFileName(item.File.Path),
                UserName = item.File.ArtistName ?? "",
                UserId = item.File.ArtistId ?? "",
                PageCount = 1,
            };
            await new Pikura.Avalonia.Views.Artwork.ArtworkViewerWindow(preview, vm.GalleryVm).ShowDialog(window);
        }
        catch (Exception ex) { vm.NoticeLine = ex.Message; }
    }

    private void CopyImage(LibraryItemViewModel item)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } cb || !System.IO.File.Exists(item.File.Path)) return;
        try { _ = cb.SetBitmapAsync(new global::Avalonia.Media.Imaging.Bitmap(item.File.Path)); }
        catch (Exception ex) { if (VM is { } vm) vm.NoticeLine = ex.Message; }
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
        => (DataContext as LibraryViewModel)?.OpenSelectedCommand.Execute(null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not LibraryViewModel vm) return;
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { vm.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape) { vm.ClearSelection(); e.Handled = true; }
    }

    private void OnLibraryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm || sender is not ScrollViewer sv) return;
        if (e.ExtentDelta.Y > 0 && sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 50)
            vm.OnScrolledToEnd();
    }

    // Last non-mouse pointer type — distinguishes a touch tap from a scroll swipe.
    private PointerType _tapPtr = PointerType.Mouse;
    /// <summary>Touch/pen tap (finger lifted without scrolling): same action as the card click.</summary>
    private void OnItemPressedTapped(object? sender, TappedEventArgs e)
    {
        if (_tapPtr == PointerType.Mouse) return; // mouse already handled via PointerPressed
        if (sender is not Control c || c.DataContext is not LibraryItemViewModel item) return;
        var lvm2 = DataContext as LibraryViewModel; lvm2?.ApplyItemClick(item, false, false); _ = lvm2?.OpenInViewerCommand.ExecuteAsync(item);
    }
}
