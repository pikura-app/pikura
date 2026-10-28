using Avalonia;
using Avalonia.Controls;
using ShapePath = global::Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using AvaloniaAnimation = global::Avalonia.Animation;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Artwork;
using Pikura.Avalonia.Views.Dialogs;
using Pikura.Core.Data;
using Pikura.Core.Http;
using Pikura.Core.Models;
using Pikura.Core.Services;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Views.Gallery;

public partial class InlineArtworkViewer : UserControl
{
    /// <summary>Bubbling event raised when the Browse button is clicked. Hosts can handle this to toggle their own panel.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ToggleBrowseEvent =
        RoutedEvent.Register<InlineArtworkViewer, RoutedEventArgs>(nameof(ToggleBrowse), RoutingStrategies.Bubble);
    public event EventHandler<RoutedEventArgs> ToggleBrowse
    {
        add => AddHandler(ToggleBrowseEvent, value);
        remove => RemoveHandler(ToggleBrowseEvent, value);
    }

    /// <summary>Bubbling event raised when the Expand button is clicked inside the viewer. Hosts should go full-screen (hide side panel).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ExpandViewerEvent =
        RoutedEvent.Register<InlineArtworkViewer, RoutedEventArgs>(nameof(ExpandViewer), RoutingStrategies.Bubble);
    public event EventHandler<RoutedEventArgs> ExpandViewer
    {
        add => AddHandler(ExpandViewerEvent, value);
        remove => RemoveHandler(ExpandViewerEvent, value);
    }

    /// <summary>Bubbling event raised when "Fullscreen" is clicked while Collage mode is
    /// active — unlike ExpandViewer (which TOGGLES), hosts should unconditionally go full-screen
    /// here, since Fullscreen should never accidentally collapse back to the side panel if it's
    /// clicked while already expanded.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> RequestFullscreenEvent =
        RoutedEvent.Register<InlineArtworkViewer, RoutedEventArgs>(nameof(RequestFullscreen), RoutingStrategies.Bubble);
    public event EventHandler<RoutedEventArgs> RequestFullscreen
    {
        add => AddHandler(RequestFullscreenEvent, value);
        remove => RemoveHandler(RequestFullscreenEvent, value);
    }

    /// <summary>Bubbling event raised after Close All is executed. Hosts can handle this to close their own panel.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ViewerClosedEvent =
        RoutedEvent.Register<InlineArtworkViewer, RoutedEventArgs>(nameof(ViewerClosed), RoutingStrategies.Bubble);
    public event EventHandler<RoutedEventArgs> ViewerClosed
    {
        add => AddHandler(ViewerClosedEvent, value);
        remove => RemoveHandler(ViewerClosedEvent, value);
    }

    /// <summary>Set by the host to drive the Expand/Restore button label. Independent of GalleryViewModel.IsViewerExpanded.</summary>
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<InlineArtworkViewer, bool>(nameof(IsExpanded));
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Set by the host to drive the Show/Hide Panel button label. Each section (Gallery,
    /// Rankings, Search, Pixivision, Viewed) has its own ShowPreview flag on its own ViewModel —
    /// binding this control's DataContext to the shared GalleryViewModel means it can't read that
    /// directly, so hosts pass their own ShowPreview in here instead.</summary>
    public static readonly StyledProperty<bool> IsPanelOpenProperty =
        AvaloniaProperty.Register<InlineArtworkViewer, bool>(nameof(IsPanelOpen));
    public bool IsPanelOpen
    {
        get => GetValue(IsPanelOpenProperty);
        set => SetValue(IsPanelOpenProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty)
            ApplyExpandedState((bool)change.NewValue!);
        if (change.Property == IsPanelOpenProperty)
            ApplyPanelOpenState((bool)change.NewValue!);

        // When own IsVisible flips true (e.g. GalleryFullViewer shown via IsViewerExpanded binding)
        // and also when IsExpanded changes (side↔full switch), reload the card.
        // Post to UI dispatcher to debounce rapid layout passes that fire multiple notifications.
        if ((change.Property == IsVisibleProperty || change.Property == IsExpandedProperty)
            && change.NewValue is true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsEffectivelyVisible) return;
                _loadedCardId = null;
                _ = LoadCardAsync(VM?.InlineViewerCard);
            });
        }
        if (change.Property == IsVisibleProperty && change.NewValue is false)
            _loadedCardId = null;
    }

    private void ApplyExpandedState(bool expanded)
    {
        if (this.FindControl<Button>("BrowsePanelButton") is { } browse)
            browse.IsVisible = !expanded;
        if (this.FindControl<TextBlock>("ExpandLabel") is { } expandLbl)
            expandLbl.IsVisible = !expanded;
        if (this.FindControl<TextBlock>("RestoreLabel") is { } restoreLbl)
            restoreLbl.IsVisible = expanded;
    }

    private void ApplyPanelOpenState(bool open)
    {
        if (this.FindControl<ShapePath>("ShowPanelIcon") is { } showIcon)
            showIcon.IsVisible = !open;
        if (this.FindControl<ShapePath>("HidePanelIcon") is { } hideIcon)
            hideIcon.IsVisible = open;
        if (this.FindControl<TextBlock>("ShowPanelLabel") is { } showLbl)
            showLbl.IsVisible = !open;
        if (this.FindControl<TextBlock>("HidePanelLabel") is { } hideLbl)
            hideLbl.IsVisible = open;
    }

    private readonly PixivClient _pixivClient;
    private readonly PixivImageLoader _imageLoader;
    private readonly PixivDownloadService _downloader;
    private readonly UgoiraService _ugoiraService;
    private readonly Pikura.Core.Services.LocalFavoritesService _favorites;
    private readonly AiViewModel _aiVm;

    private IReadOnlyList<ArtworkPage> _pages = [];
    private int _currentPageIndex;
    private ArtworkCardViewModel? _currentCard;
    private string? _loadedCardId; // ID of the card that was successfully loaded (for retry dedup)
    private CancellationTokenSource? _loadCts;
    private string? _contextMenuTag; // Tag from the tag chip that opened the context menu

    // Zoom / pan state
    private double _scale = 1.0;
    private double _translateX;
    private double _translateY;
    private bool _isPanning;
    private bool _panDragging;
    private const double PanThreshold = 4.0;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;

    // Full-res loading
    private const double FullResThreshold = 1.5;
    private bool _fullResLoaded;
    private string? _currentOriginalUrl;

    // Per-card page cache: every image+page the viewer has opened is remembered so
    // switching between collage items, tabs, pages, or even different view instances
    // never re-fetches the same bytes.
    private static readonly ConditionalWeakTable<ArtworkCardViewModel, CardPageCache> _cardPageCache = new();

    private class CardPageCache
    {
        public IReadOnlyList<ArtworkPage> Pages { get; set; } = [];
        public Dictionary<int, Bitmap> PageBitmaps { get; set; } = new();
        public HashSet<int> PageFullResLoaded { get; set; } = new();
        public int CurrentPageIndex { get; set; }
        public string? CurrentOriginalUrl { get; set; }
        public bool FullResLoaded { get; set; }
    }

    // Active card's cache; these aliases are swapped by LoadCardAsync.
    private Dictionary<int, Bitmap> _pageBitmaps = new();
    private HashSet<int> _pageFullResLoaded = new();

    // Pixiv action state
    private bool _isLiked;
    private bool _isBookmarked;
    private bool _isPrivateBookmark;
    private bool _isFollowing;
    private string? _bookmarkId;
    private int _bookmarkCount;
    private int _likeCount;
    private int _viewCount;
    private bool _statsLoaded;
    // Caches real like/bookmark/view counts per artwork ID once fetched, so navigating back
    // to an artwork already seen this session shows the real numbers immediately instead of
    // flashing "0" again while LoadArtworkStateAsync's network fetch is in flight — the
    // gallery-listing endpoint doesn't return these counts, so the card starts at 0 for
    // artworks that haven't had their detail fetched yet.
    private readonly Dictionary<string, (int Likes, int Bookmarks, int Views)> _statsCache = [];
    // Tab drag-reorder state
    private ViewerTab? _dragTab;
    private Control? _draggedContainer;
    private Panel? _tabPanel;
    private Point _dragStart;
    private int _dragFromIndex = -1;
    private bool _isDragging;
    private double _slotWidth;
    private readonly List<AvaloniaAnimation.Transitions?> _savedTabTransitions = [];
    private readonly Dictionary<Control, double> _preMoveX = [];
    private const double DragThreshold = 6.0;
    private static readonly AvaloniaAnimation.Transitions _tabMoveTransitions = new()
    {
        new AvaloniaAnimation.TransformOperationsTransition
        {
            Property = Visual.RenderTransformProperty,
            Duration = TimeSpan.FromSeconds(0.2),
            Easing = new AvaloniaAnimation.Easings.CubicEaseOut()
        }
    };

    public InlineArtworkViewer()
    {
        InitializeComponent();

        _pixivClient = AppServices.Get<PixivClient>();
        _imageLoader = AppServices.Get<PixivImageLoader>();
        _downloader = AppServices.Get<PixivDownloadService>();
        _ugoiraService = AppServices.Get<UgoiraService>();
        _favorites  = AppServices.Get<Pikura.Core.Services.LocalFavoritesService>();
        _aiVm       = AppServices.Get<AiViewModel>();

        // Bind message list once — never reset to avoid streaming race crashes
        Loaded += (_, _) =>
        {
            if (AiMessagesList != null && AiMessagesList.ItemsSource == null)
                AiMessagesList.ItemsSource = _aiVm.Messages;
            // Hook extent changes so scroll fires after layout whenever content grows
            if (AiScrollViewer != null)
                AiScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;

            // Restore the persisted Show/Hide stats toggle state (previously always reset to
            // visible on every card/restart, ignoring whatever the user last chose).
            if (ShowStatsBtn != null && StatsStackPanel != null)
            {
                var showStats = AppServices.Get<Pikura.Core.Settings.SettingsService>().Current.ShowPixivStats;
                ShowStatsBtn.IsChecked = showStats;
                StatsStackPanel.IsVisible = showStats;
            }
        };

        _aiVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiViewModel.StatusText) && AiStatusLabel != null)
                Dispatcher.UIThread.Post(() => AiStatusLabel.Text = _aiVm.StatusText);
            if (e.PropertyName is nameof(AiViewModel.StatusText) or nameof(AiViewModel.IsThinking)
                               or nameof(AiViewModel.IsGenerating)
                               or nameof(AiViewModel.IsModelReady) or nameof(AiViewModel.IsEnabled))
                Dispatcher.UIThread.Post(UpdateAiStatusDot);
            if (e.PropertyName == nameof(AiViewModel.IsThinking))
            {
                RefreshAiMessages();
                Dispatcher.UIThread.Post(UpdateThinkingIndicator);
            }
            if (e.PropertyName == nameof(AiViewModel.IsCurrentSubmissionMultiPage))
                Dispatcher.UIThread.Post(UpdateDescribeButtonVisibility);
            // Any action that creates the chat (Describe, Send, Fav…) replaces the start button.
            if (e.PropertyName == nameof(AiViewModel.CurrentSession))
                Dispatcher.UIThread.Post(() =>
                {
                    if (AiStartChatBtn != null && _aiVm.CurrentSession != null) AiStartChatBtn.IsVisible = false;
                });
        };
        UpdateDescribeButtonVisibility();
        UpdateThinkingIndicator();
        // On new message: hook content streaming for auto-scroll, then refresh
        _aiVm.Messages.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is AiChatMessage msg)
                        msg.PropertyChanged += (_, _) => ScrollToBottomDeferred();
                }
            }
            RefreshAiMessages();
        };

        DataContextChanged += OnDataContextChanged;
        TabStrip.Loaded += (_, _) => RefreshDragHandlers();
    }

    /// <summary>
    /// Fired when this control (re-)enters the visual tree. Reload the current card so
    /// switching back to a gallery section that hosts this viewer shows content.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeToAncestorVisibility();
        if (IsEffectivelyVisible)
            _ = LoadCardAsync(VM?.InlineViewerCard);
        if (TopLevel.GetTopLevel(this) is { } tl)
            tl.AddHandler(KeyDownEvent, OnViewerKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } tl)
            tl.RemoveHandler(KeyDownEvent, OnViewerKeyDown);
        UnsubscribeAncestorVisibility();
        _loadedCardId = null;
    }

    private void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        // Only the visible instance should handle keys
        if (!IsEffectivelyVisible) return;
        if (VM?.InlineViewerCard == null) return;
        if (e.Key != Key.Left && e.Key != Key.Right) return;
        if (!AppServices.Get<Pikura.Core.Settings.SettingsService>().Current.GalleryKeyboardNavEnabled) return;
        // Don't intercept when a text input has focus
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox or NumericUpDown) return;

        if (e.Key == Key.Left)
            NavigatePrevWithPages();
        else
            NavigateNextWithPages();
        e.Handled = true;
    }

    private readonly List<(AvaloniaObject obj, EventHandler<AvaloniaPropertyChangedEventArgs> handler)> _ancestorSubs = [];

    private void SubscribeToAncestorVisibility()
    {
        UnsubscribeAncestorVisibility();
        Visual? current = this.GetVisualParent();
        while (current is not null)
        {
            var captured = current;
            EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, e) =>
            {
                if (e.Property != IsVisibleProperty) return;
                if (e.NewValue is true)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!IsEffectivelyVisible) return;
                        _loadedCardId = null;
                        _ = LoadCardAsync(VM?.InlineViewerCard);
                    });
                else if (e.NewValue is false)
                    _loadedCardId = null;
            };
            captured.PropertyChanged += handler;
            _ancestorSubs.Add((captured, handler));
            current = current.GetVisualParent();
        }
    }

    private void UnsubscribeAncestorVisibility()
    {
        foreach (var (obj, handler) in _ancestorSubs)
            obj.PropertyChanged -= handler;
        _ancestorSubs.Clear();
    }

    private GalleryViewModel? VM => DataContext as GalleryViewModel;

    /// <summary>The tab collection shown in the strip — always the global ViewerTabs.</summary>
    private IEnumerable<ViewerTab> ActiveTabs => VM?.ViewerTabs ?? Enumerable.Empty<ViewerTab>();

    private GalleryViewModel? _subscribedVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from previous VM if any
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm.ViewerTabs.CollectionChanged -= OnViewerTabsCollectionChanged;
            _subscribedVm = null;
        }

        if (VM is not { } vm) return;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.ViewerTabs.CollectionChanged += OnViewerTabsCollectionChanged;
        _subscribedVm = vm;

        // Always force reload on re-attach — _currentCard may match a stale instance
        _currentCard = null;
        _currentOriginalUrl = null;

        if (vm.InlineViewerCard != null)
        {
            _ = LoadCardAsync(vm.InlineViewerCard);
            UpdateTabHighlight();
        }
    }

    private void OnViewerTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isDragging) return;
        Dispatcher.UIThread.Post(RefreshDragHandlers);
    }

    private void RefreshDragHandlers()
    {
        var panel = TabStrip.ItemsPanelRoot as Panel;
        if (panel == null) return;
        foreach (var child in panel.Children)
        {
            child.RemoveHandler(PointerPressedEvent, OnTabPointerPressed);
            child.RemoveHandler(PointerMovedEvent, OnTabPointerMoved);
            child.RemoveHandler(PointerReleasedEvent, OnTabPointerReleased);
            child.RemoveHandler(PointerCaptureLostEvent, OnTabPointerCaptureLost);
            child.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);
            child.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel);
            child.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Direct);
            child.AddHandler(PointerCaptureLostEvent, OnTabPointerCaptureLost, RoutingStrategies.Direct);
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.InlineViewerCard))
        {
            _ = LoadCardAsync(VM?.InlineViewerCard);
            UpdateArtistButtonVisibility();
            _ = UpdateLibraryInfoAsync();
        }
        if (e.PropertyName == nameof(GalleryViewModel.SelectedViewerTab))
        {
            _ = LoadCardAsync(VM?.InlineViewerCard);
            UpdateTabHighlight();
            UpdateArtistButtonVisibility();
            _ = UpdateLibraryInfoAsync();
        }
        if (e.PropertyName == nameof(GalleryViewModel.SelectedArtist))
            UpdateArtistButtonVisibility();
        if (e.PropertyName == nameof(GalleryViewModel.NavListVersion))
            UpdateArtworkCounter();
        if (e.PropertyName == nameof(GalleryViewModel.IsCollageGridVisible) && VM?.IsCollageGridVisible == true)
            _ = PreloadCollageThumbnailsAsync(VM.CollageItems ?? [], false);
    }

    /// <summary>Keep the toolbar "👤 Artist" button hidden when already viewing this artist.</summary>
    private void UpdateArtistButtonVisibility()
    {
        if (GoToArtistBtn != null)
            GoToArtistBtn.IsVisible = !IsViewingCurrentArtist();
    }

    private void UpdateTabHighlight()
    {
        if (VM == null) return;
        var strip = this.FindControl<ItemsControl>("TabStrip");
        if (strip == null) return;
        var active = VM.SelectedViewerTab;
        foreach (var border in strip.GetVisualDescendants().OfType<Border>()
                     .Where(b => b.Name == "TabItem"))
        {
            bool isActive = border.DataContext == active;
            border.Opacity = isActive ? 1.0 : 0.55;
        }
    }

    private void OnTabListClick(object? sender, RoutedEventArgs e)
    {
        if (VM == null || sender is not Button btn) return;
        var menu = new ContextMenu();
        foreach (var tab in ActiveTabs)
        {
            var item = new MenuItem { Header = tab.Header };
            var captured = tab;
            item.Click += (_, _) => VM.SelectedViewerTab = captured;
            if (tab == VM.SelectedViewerTab)
                item.Classes.Add("accent");
            menu.Items.Add(item);
        }
        menu.Open(btn);
    }

    private void OnTabHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ViewerTab tab || VM == null) return;
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed) return;

        var menu = new ContextMenu();
        var rename = new MenuItem { Header = Pikura.Avalonia.Localization.LocalizationService.Instance["Common_Rename"] };
        rename.Click += (_, _) => _ = RenameTabAsync(tab);
        menu.Items.Add(rename);
        menu.Open(border);
        e.Handled = true;

        async Task RenameTabAsync(ViewerTab t)
        {
            await VM.RenameTabAsync(t);
        }
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ContentPresenter cp || cp.DataContext is not ViewerTab tab || VM == null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        _tabPanel = TabStrip.ItemsPanelRoot as Panel;
        if (_tabPanel == null) return;

        _draggedContainer = cp;
        VM.SelectedViewerTab = tab;
        _dragTab = tab;
        _dragFromIndex = _tabPanel.Children.IndexOf(cp);
        _dragStart = e.GetPosition(_tabPanel);
        _isDragging = false;
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab == null || _draggedContainer == null || _tabPanel == null) return;
        var pos = e.GetPosition(_tabPanel);
        if (!_isDragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < DragThreshold &&
                Math.Abs(pos.Y - _dragStart.Y) < DragThreshold) return;

            _isDragging = true;
            _draggedContainer.Classes.Add("dragging");
            _draggedContainer.ZIndex = 100;
            _draggedContainer.Cursor = new Cursor(StandardCursorType.DragMove);
            if (_draggedContainer is IInputElement ie)
                e.Pointer.Capture(ie);

            // Disable transitions during the drag so siblings snap to new slots immediately
            // and the dragged container follows the cursor without lag.
            _savedTabTransitions.Clear();
            foreach (var child in _tabPanel.Children)
            {
                if (child is Control c)
                {
                    _savedTabTransitions.Add(c.Transitions);
                    c.Transitions = null;
                }
                else _savedTabTransitions.Add(null);
            }

            if (_tabPanel.Children.Count > 1)
                _slotWidth = Math.Abs(_tabPanel.Children[1].Bounds.X - _tabPanel.Children[0].Bounds.X);
            else
                _slotWidth = _draggedContainer.Bounds.Width + 2; // 2 is the StackPanel Spacing
        }

        ApplyDragTransform(pos.X - _dragStart.X);

        int toIndex = -1;
        for (int i = 0; i < _tabPanel.Children.Count; i++)
        {
            var child = _tabPanel.Children[i];
            var bounds = child.Bounds;
            if (pos.X < bounds.X + bounds.Width / 2 && toIndex < 0)
                toIndex = i;
        }
        if (toIndex < 0) toIndex = _tabPanel.Children.Count - 1;
        if (toIndex == _dragFromIndex) return;

        var tabs = VM?.ViewerTabs;
        if (tabs == null || _dragFromIndex >= tabs.Count || toIndex >= tabs.Count) return;

        var shift = (toIndex - _dragFromIndex) * _slotWidth;
        _preMoveX.Clear();
        foreach (var child in _tabPanel.Children)
        {
            if (child is Control c && c != _draggedContainer)
                _preMoveX[c] = child.Bounds.X;
        }

        tabs.Move(_dragFromIndex, toIndex);
        _dragFromIndex = _tabPanel.Children.IndexOf(_draggedContainer);
        _dragStart = new Point(_dragStart.X + shift, _dragStart.Y);
        ApplyDragTransform(pos.X - _dragStart.X);

        // FLIP: move siblings visually back to their pre-Move positions, then let
        // transitions animate them to the new layout slot.
        int t = 0;
        foreach (var child in _tabPanel.Children)
        {
            if (child is Control c && c != _draggedContainer && _preMoveX.TryGetValue(c, out var oldX))
            {
                var newX = child.Bounds.X;
                var delta = oldX - newX;
                if (Math.Abs(delta) > 0.5)
                {
                    c.RenderTransform = TransformOperations.Parse($"translateX({delta:F2}px)");
                    var saved = t < _savedTabTransitions.Count ? _savedTabTransitions[t] : null;
                    c.Transitions = _tabMoveTransitions;
                    Dispatcher.UIThread.Post(() =>
                    {
                        c.RenderTransform = null;
                        c.Transitions = saved;
                    });
                }
            }
            t++;
        }
    }

    private void ApplyDragTransform(double deltaX)
    {
        if (_draggedContainer == null) return;
        var x = deltaX.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        _draggedContainer.RenderTransform = TransformOperations.Parse($"translateX({x}px) scale(1.03)");
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        EndDrag();
    }

    private void OnTabPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndDrag();
    }

    private void EndDrag()
    {
        if (_draggedContainer != null)
        {
            _draggedContainer.Classes.Remove("dragging");
            _draggedContainer.ZIndex = 0;
            _draggedContainer.RenderTransform = null;
            _draggedContainer.Cursor = Cursor.Default;
            _draggedContainer = null;
        }

        if (_tabPanel != null)
        {
            int t = 0;
            foreach (var child in _tabPanel.Children)
            {
                if (child is Control c)
                {
                    c.RenderTransform = null;
                    c.ZIndex = 0;
                    c.Opacity = 1;
                    c.Classes.Remove("dragging");
                    c.Cursor = Cursor.Default;
                    if (t < _savedTabTransitions.Count)
                        c.Transitions = _savedTabTransitions[t];
                }
                t++;
            }
        }

        _savedTabTransitions.Clear();
        _preMoveX.Clear();
        _isDragging = false;
        _dragTab = null;
        _dragFromIndex = -1;
        _tabPanel = null;
    }

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var border = btn.FindAncestorOfType<Border>();
            if (border?.DataContext is ViewerTab tab && VM != null)
                VM.CloseViewerTabCommand.Execute(tab);
        }
        e.Handled = true;
    }

    private void ClearViewer()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _currentCard = null;
        _currentOriginalUrl = null;
        _pages = [];
        _currentPageIndex = 0;
        _fullResLoaded = false;
        // Drop the working alias so the viewer isn't keeping a live cache when empty.
        _pageBitmaps = new Dictionary<int, Bitmap>();
        _pageFullResLoaded = new HashSet<int>();
        VM?.InlinePageList.Clear();
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ViewerImage != null) { ViewerImage.Source = null; ViewerImage.IsVisible = false; }
            if (UgoiraImage != null) { UgoiraImage.SourcePath = null; UgoiraImage.IsVisible = false; }
            if (LoadingPanel != null) LoadingPanel.IsVisible = false;
            if (ErrorPanel != null) ErrorPanel.IsVisible = false;
        });
    }

    private async Task LoadCardAsync(ArtworkCardViewModel? card)
    {
        // Skip if this viewer instance is not actually displayed.
        // Multiple InlineArtworkViewer instances exist across pages (Gallery, Discover, Rankings, etc.)
        // and they all subscribe to the same VM. Only the visible one should load.
        // Detached viewers (pages not currently shown) can still report IsEffectivelyVisible —
        // without the visual-root check each click loaded the image twice.
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) == null) return;

        if (card == null || card.Id == null) { ClearViewer(); return; }
        // Captured once, non-null — member narrowing of card.Id doesn't survive the awaits
        // further down in this method, so the string parameter calls below use this instead.
        var cardId = card.Id;

        // Skip only if the same card instance is already fully loaded successfully.
        // We use reference equality because expanded multi-page collages can contain
        // multiple cards with the same artwork Id (one per page).
        if (ReferenceEquals(_currentCard, card))
        {
            if (_loadedCardId == card.Id
                && (ViewerImage?.Source != null || UgoiraImage?.SourcePath != null))
                return;
            if (_loadCts is { IsCancellationRequested: false })
                return;
        }

        // ── Per-card page cache: save outgoing, restore incoming ────────
        // Keep a separate cache for every card the viewer has seen so collage/tile
        // navigation, tab switching, and page flipping never re-fetch the same image.
        var vm = VM;
        if (_currentCard != null)
        {
            var outCache = _cardPageCache.GetValue(_currentCard, _ => new CardPageCache());
            outCache.Pages = _pages;
            outCache.PageBitmaps = _pageBitmaps;
            outCache.PageFullResLoaded = _pageFullResLoaded;
            outCache.CurrentPageIndex = _currentPageIndex;
            outCache.CurrentOriginalUrl = _currentOriginalUrl;
            outCache.FullResLoaded = _fullResLoaded;

            // Also snapshot the current bitmap onto the outgoing ViewerTab so plain
            // tab switching can still restore instantly.
            if (_currentCard?.Id != card.Id && vm != null)
            {
                var outgoing = vm.ViewerTabs.FirstOrDefault(t => ReferenceEquals(t.Card, _currentCard));
                if (outgoing != null && ViewerImage?.Source is Bitmap outBmp)
                {
                    outgoing.CachedBitmap = outBmp;
                    outgoing.CachedPageIndex = _currentPageIndex;
                    outgoing.CachedOriginalUrl = _currentOriginalUrl;
                    outgoing.CachedFullResLoaded = _fullResLoaded;
                }
            }
        }

        // Restore (or create) the incoming card's cache and point the working aliases at it.
        var cache = _cardPageCache.GetValue(card, _ => new CardPageCache());
        _pages = cache.Pages;
        _pageBitmaps = cache.PageBitmaps;
        _pageFullResLoaded = cache.PageFullResLoaded;
        _currentPageIndex = cache.CurrentPageIndex;
        if (_currentPageIndex < 0 || _currentPageIndex >= Math.Max(1, card.PageCount))
            _currentPageIndex = card.PageIndex;
        if (_currentPageIndex < 0) _currentPageIndex = 0;
        _currentOriginalUrl = cache.CurrentOriginalUrl;
        _fullResLoaded = cache.FullResLoaded;

        // If the selected ViewerTab happens to hold this exact card, use its cached
        // bitmap too (covers tab-switching where the weak-table cache may have been GC'd).
        var incomingTab = vm?.SelectedViewerTab;
        if (incomingTab?.CachedBitmap != null && ReferenceEquals(incomingTab.Card, card)
            && !_pageBitmaps.ContainsKey(_currentPageIndex))
        {
            _pageBitmaps[_currentPageIndex] = incomingTab.CachedBitmap;
            if (incomingTab.CachedFullResLoaded) _pageFullResLoaded.Add(_currentPageIndex);
            _fullResLoaded = incomingTab.CachedFullResLoaded;
        }

        _currentCard = card;

        // A different artwork: close the "All pages" grid left open by the previous one.
        if (VM != null && VM.IsInlinePageGridVisible) VM.IsInlinePageGridVisible = false;

        // Cancel any in-flight load and start fresh
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        // Clear previous content — we're loading a genuinely new card with no cache.
        if (UgoiraImage != null)
        {
            UgoiraImage.SourcePath = null;
            UgoiraImage.IsPlaying = false;
        }
        if (ViewerImage != null) ViewerImage.Source = null;

        _currentCard = card;
        _pageBitmaps.Clear();
        _pageFullResLoaded.Clear();
        // Re-apply the persisted Show/Hide stats toggle on every artwork navigation, not just
        // once at control-load time — the ToggleButton's hardcoded XAML default (IsChecked="True")
        // was winning back over the user's choice as soon as they moved to another submission.
        if (ShowStatsBtn != null && StatsStackPanel != null)
        {
            var showStats = AppServices.Get<Pikura.Core.Settings.SettingsService>().Current.ShowPixivStats;
            ShowStatsBtn.IsChecked = showStats;
            StatsStackPanel.IsVisible = showStats;
        }
        // Capture the resolved session by value — if the user switches to another
        // artwork before the fetches below complete, late-arriving bytes must still
        // land on THIS session (not whatever session happens to be "current" later),
        // otherwise the image silently never gets persisted for this artwork.
        _aiVm.SetViewerContext(card);
        var session = _aiVm.SessionForArtwork(card.Id);
        // Re-show the "start chat" button when this artwork has no chat (fixes the
        // button not reappearing after the first artwork switch).
        if (AiStartChatBtn != null)
            AiStartChatBtn.IsVisible = session == null && _aiVm.CurrentCard != null && AiPanel?.IsVisible == true;

        // Seed Hoshi's vision bytes with the *thumbnail* immediately so the user
        // can hit "Describe"/"Tags" the moment the card opens — without this seed
        // the buttons fire before RenderPageAsync's Regular fetch completes and
        // the model receives a text-only prompt, replying "I don't have the
        // ability to see the image." The Regular fetch in RenderPageAsync will
        // upgrade these bytes when it lands.
        var thumbUrl = card.ThumbnailUrl;
        if (!string.IsNullOrEmpty(thumbUrl) && session != null && AiPanel?.IsVisible == true)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var thumbBytes = await _imageLoader.FetchBytesAsync(thumbUrl, ct);
                    if (thumbBytes is null) return;
                    // Don't clobber a higher-res Regular that already arrived for this session.
                    if (session.ImageBytes is { Length: > 0 }) return;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (session.ImageBytes is not { Length: > 0 })
                            _aiVm.SetSessionImageBytes(session, thumbBytes);
                    });
                }
                catch (OperationCanceledException) { /* card switched */ }
                catch { /* non-fatal — Regular fetch will still set bytes */ }
            }, ct);
        }
        _currentPageIndex = Math.Clamp(_currentPageIndex, 0, Math.Max(0, card.PageCount - 1));
        _fullResLoaded = false;
        // If pages are already cached, the page strip will be rebuilt below.
        if (_pages.Count == 0) VM?.InlinePageList.Clear();

        _isLiked = card.Id is not null &&
                   (VM?.SettingsService?.Current?.PixivLikedArtworkIds.Contains(card.Id) ?? false);
        card.IsLiked = _isLiked; // defensive: keep the card's own badge in sync with the authoritative check
        SyncCardFlagsEverywhere(isLiked: _isLiked, targetCard: card);
        _isBookmarked = card.IsPixivBookmarked;
        _isPrivateBookmark = card.IsPixivPrivateBookmark;
        _isFollowing = card.IsFollowed;
        _bookmarkId = card.PixivBookmarkId;
        if (card.Id is not null && _statsCache.TryGetValue(card.Id, out var cachedStats))
        {
            _likeCount = cachedStats.Likes;
            _bookmarkCount = cachedStats.Bookmarks;
            _viewCount = cachedStats.Views;
            _statsLoaded = true;
        }
        else
        {
            _bookmarkCount = card.BookmarkCount;
            _likeCount = card.LikeCount;
            _viewCount = card.ViewCount;
            _statsLoaded = false;
        }
        UpdateStatsLabel();
        UpdateFollowButton();
        UpdateLikeButton();
        UpdateBookmarkButtons();
        UpdateFavoriteButton(card);
        _currentOriginalUrl = null;

        // Library cards hide like/bookmark/follow, and the Library fills in title/artist itself —
        // skipping these 3 requests per click is what keeps browsing the library under pixiv's rate limit.
        if (card.HasPixivId && card.LocalPath == null) _ = LoadArtworkStateAsync(card);

        UpdatePageIndicator();
        UpdateArtworkCounter();
        SetLoading(true);
        ResetZoom();

        // Mark unloaded — only set _loadedCardId once content actually arrives.
        _loadedCardId = null;
        bool succeeded = false;

        try
        {
            if (card.LocalPath is { } localPath && File.Exists(localPath))
            {
                // Library card: show the exact file on disk (right page, works offline).
                // Thumbnail first so the click feels instant; the full file replaces it below.
                if (card.Thumbnail != null && ViewerImage != null) ViewerImage.Source = card.Thumbnail;
                SetLoading(false);
                // Decode only at the size the panel shows (a scaled JPEG decode is several times
                // faster than the full 8+ MP image); the original loads when the user zooms in.
                var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                var targetWidth = (int)Math.Clamp((ViewerImage?.Bounds.Width ?? 1000) * scaling * 1.25, 800, 2400);
                // Dedicated thread, not the shared pool: background thumbnail decodes can fill the
                // pool and leave the image the user just clicked queued for seconds.
                var bmp = await Task.Factory.StartNew(() =>
                {
                    using var fs = File.OpenRead(localPath);
                    return Bitmap.DecodeToWidth(fs, targetWidth, BitmapInterpolationMode.HighQuality);
                }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                if (ct.IsCancellationRequested || !ReferenceEquals(_currentCard, card)) return;
                _pages = [];
                VM?.InlinePageList.Clear();
                if (ViewerImage != null) ViewerImage.Source = bmp;
                _fullResLoaded = false;
                UpdatePageIndicator();
                succeeded = true;
                // Hoshi gets a 1024px copy (what it sends to the model anyway), not the full file.
                if (session != null)
                    _ = Task.Run(() => LocalImageForHoshi(localPath), ct).ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully && t.Result is { } hb)
                            Dispatcher.UIThread.Post(() => _aiVm.SetSessionImageBytes(session, hb));
                    }, TaskScheduler.Default);
            }
            else if (!card.HasPixivId)
            {
                SetError(LocalizationService.Instance["Library_FileMissing"]);
            }
            else if (card.IllustType == 2)
            {
                succeeded = await LoadUgoiraAsync(cardId, ct);
            }
            else if (_pages.Count > 0)
            {
                // Pages already cached — just rebuild the strip and render the current page.
                _currentPageIndex = Math.Clamp(_currentPageIndex, 0, Math.Max(0, _pages.Count - 1));
                if (VM != null)
                {
                    VM.InlinePageList.Clear();
                    for (int i = 0; i < _pages.Count; i++)
                        VM.InlinePageList.Add(new PageThumbnailItem(i, i + 1));
                    _ = LoadInlinePageThumbnailsAsync(_pages);
                }
                UpdatePageIndicator();
                succeeded = await RenderPageAsync(_currentPageIndex, ct, session);
            }
            else
            {
                var pages = await _pixivClient.GetArtworkPagesAsync(cardId);
                if (ct.IsCancellationRequested) return;
                _pages = pages;
                cache.Pages = pages;
                if (VM != null)
                {
                    VM.InlinePageList.Clear();
                    for (int i = 0; i < pages.Count; i++)
                        VM.InlinePageList.Add(new PageThumbnailItem(i, i + 1));
                    _ = LoadInlinePageThumbnailsAsync(pages);
                }
                _currentPageIndex = Math.Clamp(_currentPageIndex, 0, Math.Max(0, _pages.Count - 1));
                UpdatePageIndicator();
                succeeded = await RenderPageAsync(_currentPageIndex, ct, session);
            }
        }
        catch (OperationCanceledException) { /* expected on rapid switch */ }
        catch (System.Net.Http.HttpRequestException httpEx)
            when ((int?)httpEx.StatusCode == 429)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[InlineArtworkViewer] LoadCardAsync({card.Id}) rate-limited (429)");
            if (_currentCard?.Id == card.Id)
                SetError("Rate limited by Pixiv (HTTP 429).\nWait a moment then click Retry,\nor enable Safe Mode in Settings.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[InlineArtworkViewer] LoadCardAsync({card.Id}) failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            if (_currentCard?.Id == card.Id)
                SetError(ex is System.Net.Http.HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException }
                    // Not pixiv's fault: something on the network (captive portal, in-flight or hotel
                    // Wi-Fi, firewall) answered the secure connection with non-TLS data.
                    ? LocalizationService.Instance["Viewer_NetworkIntercepted"]
                    : $"Failed to load image.\n{ex.Message}");
        }
        finally
        {
            // Clear loading state only if we're still the current card.
            // If a newer load started (different card OR null), it owns the loading state.
            if (ReferenceEquals(_loadCts, cts))
            {
                if (succeeded) _loadedCardId = card.Id;
                else _loadedCardId = null;
                _loadCts = null;
                cts.Dispose();
                SetLoading(false);
            }
        }
    }

    private async Task<bool> RenderPageAsync(int index, CancellationToken ct = default, HoshiSession? session = null)
    {
        session ??= _aiVm.CurrentSession;
        if (_pages.Count == 0 || index < 0 || index >= _pages.Count) return false;
        SetLoading(true);
        var displayed = false;

        // Keep the per-card cache in sync with the page we are about to show.
        if (_currentCard != null)
        {
            var cache = _cardPageCache.GetValue(_currentCard, _ => new CardPageCache());
            cache.CurrentPageIndex = index;
        }

        _currentOriginalUrl = _pages[index].Urls.Original;
        if (_currentCard != null)
        {
            var cache = _cardPageCache.GetValue(_currentCard, _ => new CardPageCache());
            cache.CurrentOriginalUrl = _currentOriginalUrl;
        }

        // If we already decoded this page, restore it instantly instead of re-fetching.
        if (_pageBitmaps.TryGetValue(index, out var cached))
        {
            _fullResLoaded = _pageFullResLoaded.Contains(index);
            if (_currentCard != null)
            {
                var cache = _cardPageCache.GetValue(_currentCard, _ => new CardPageCache());
                cache.CurrentOriginalUrl = _currentOriginalUrl;
                cache.FullResLoaded = _fullResLoaded;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                ViewerImage.Source = cached;
                ViewerImage.IsVisible = true;
                if (ErrorPanel != null) ErrorPanel.IsVisible = false;
                displayed = true;
                if (LoadingPanel != null) LoadingPanel.IsVisible = false;
                ResetZoom();
                if (VM?.SelectedViewerTab is { } activeTab && activeTab.Card?.Id == _currentCard?.Id)
                {
                    activeTab.CachedBitmap = cached;
                    activeTab.CachedPageIndex = index;
                    activeTab.CachedOriginalUrl = _currentOriginalUrl;
                    activeTab.CachedFullResLoaded = _fullResLoaded;
                }
                SetLoading(false);
            });
            if (!_fullResLoaded && !string.IsNullOrEmpty(_currentOriginalUrl))
                _ = LoadFullResAsync(_currentOriginalUrl, index);
            return true;
        }

        _fullResLoaded = false;

        // Instant feedback: paint the card's already-loaded thumbnail (or the
        // first frame from any earlier ugoira load) so the user sees something
        // immediately while the higher-res Regular image streams in.
        if (index == 0 && _currentCard?.Thumbnail is { } thumb)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                ViewerImage.Source = thumb;
                ViewerImage.IsVisible = true;
                displayed = true;
                if (LoadingPanel != null) LoadingPanel.IsVisible = false;
                ResetZoom();
            });
        }

        var url = _pages[index].Urls.Regular ?? _pages[index].Urls.Small
               ?? _pages[index].Urls.Original ?? _pages[index].Urls.ThumbMini;

        // Start the full-res Original download *in parallel* with the Regular fetch instead of
        // waiting for Regular to land first. Original is what the viewer ultimately shows, so
        // running the two sequentially delayed final quality by the entire Regular transfer.
        // Whichever arrives first gets painted; the Regular apply below stands down if the
        // Original beat it. Skipped when the "preview" URL *is* the Original (no Regular exists),
        // so we never fetch the same bytes twice.
        if (!string.IsNullOrEmpty(_currentOriginalUrl) && _currentOriginalUrl != url && !_fullResLoaded)
            _ = LoadFullResAsync(_currentOriginalUrl!, index);

        if (!string.IsNullOrEmpty(url))
        {
            var bytes = await _imageLoader.FetchBytesAsync(url, ct, priority: true);
            if (bytes != null)
            {
                // Upgrade the session's vision bytes to this higher-res Regular image
                // (the thumbnail seed in LoadCardAsync only covers the moment before
                // this fetch lands). Persist directly to the captured session so a
                // late-arriving fetch can't corrupt whatever session is "current" now.
                if (session != null)
                    _aiVm.SetSessionImageBytes(session, bytes);

                if (ct.IsCancellationRequested) return false;

                // Decode off the UI thread — large Regular images can take 50–200 ms
                // to decode and would otherwise freeze scrolling/typing during the swap.
                var bmp = await Task.Run(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream(bytes);
                        return new Bitmap(ms);
                    }
                    catch { return null; }
                }, ct);

                if (bmp == null || ct.IsCancellationRequested) return displayed;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) { bmp.Dispose(); return; }
                    // The parallel Original download already landed for this page — applying the
                    // lower-res Regular now would visibly downgrade what the user is looking at.
                    if (_pageFullResLoaded.Contains(index))
                    {
                        bmp.Dispose();
                        displayed = true;
                        SetLoading(false);
                        return;
                    }
                    _pageBitmaps[index] = bmp;
                    _pageFullResLoaded.Remove(index);
                    ViewerImage.Source = bmp;
                    ViewerImage.IsVisible = true;
                    if (ErrorPanel != null) ErrorPanel.IsVisible = false;
                    displayed = true;
                    ResetZoom();
                    // Cache the decoded bitmap on the active tab and the per-card cache.
                    if (VM?.SelectedViewerTab is { } activeTab && activeTab.Card?.Id == _currentCard?.Id)
                    {
                        activeTab.CachedBitmap = bmp;
                        activeTab.CachedPageIndex = index;
                        activeTab.CachedOriginalUrl = _currentOriginalUrl;
                        activeTab.CachedFullResLoaded = false;
                    }
                    if (_currentCard != null)
                    {
                        var cache = _cardPageCache.GetValue(_currentCard, _ => new CardPageCache());
                        cache.CurrentOriginalUrl = _currentOriginalUrl;
                        cache.FullResLoaded = false;
                    }
                    // Clear loading state now that the bitmap is applied — avoids the
                    // blank-frame race where SetLoading(false) ran before Source was set.
                    SetLoading(false);
                });

                // The Original upgrade was already started in parallel above, before the Regular
                // fetch — nothing left to kick off here.
                return true; // SetLoading already called above
            }
        }
        SetLoading(false);
        if (!displayed && !ct.IsCancellationRequested)
            SetError("Failed to load image.\nClick Retry to try again.");
        return displayed;
    }

    private void SetLoading(bool loading)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var hasCard = VM?.InlineViewerCard != null;
            if (LoadingPanel != null) LoadingPanel.IsVisible = loading && hasCard;
            if (ErrorPanel != null && loading) ErrorPanel.IsVisible = false;
            var isUgoira = _currentCard?.IllustType == 2;
            if (ViewerImage != null) ViewerImage.IsVisible = !loading && hasCard && !isUgoira;
            if (UgoiraImage != null) UgoiraImage.IsVisible = !loading && hasCard && isUgoira;
        });
    }

    private void SetError(string message)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (LoadingPanel != null) LoadingPanel.IsVisible = false;
            if (ErrorPanel != null)
            {
                ErrorPanel.IsVisible = true;
                if (ErrorText != null) ErrorText.Text = message;
            }
        });
    }

    private void OnRetryLoad(object? sender, RoutedEventArgs e)
    {
        if (ErrorPanel != null) ErrorPanel.IsVisible = false;
        _loadedCardId = null;
        _ = LoadCardAsync(VM?.InlineViewerCard);
    }

    private async Task<bool> LoadUgoiraAsync(string artworkId, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested) return false;

            // Paint the first extracted frame as a still placeholder the moment it
            // becomes available — gives the user immediate feedback while ffmpeg
            // continues encoding the animated WebP in the background.
            var firstFrameProgress = new Progress<string>(framePath =>
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    using var s = File.OpenRead(framePath);
                    var bmp = new global::Avalonia.Media.Imaging.Bitmap(s);
                    Dispatcher.UIThread.Post(() =>
                    {
                        // If the load was cancelled or another card took over, drop it.
                        if (ct.IsCancellationRequested) { bmp.Dispose(); return; }
                        if (ViewerImage != null)
                        {
                            ViewerImage.Source    = bmp;
                            ViewerImage.IsVisible = true;
                        }
                        // Keep LoadingPanel visible — encoding still in progress.
                    });
                }
                catch { /* still placeholder is best-effort */ }
            });

            var previewPath = await _ugoiraService
                .GetOrCreatePreviewAsync(artworkId, firstFrameProgress, ct)
                .ConfigureAwait(false);
            if (ct.IsCancellationRequested) return false;

            if (previewPath != null)
            {
                bool applied = false;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    // Hide the static placeholder and swap to the animated player.
                    if (ViewerImage != null) { ViewerImage.Source = null; ViewerImage.IsVisible = false; }
                    UgoiraImage.SourcePath = null;
                    UgoiraImage.SourcePath = previewPath;
                    UgoiraImage.IsVisible  = true;
                    UgoiraImage.IsPlaying  = true;
                    ResetZoom();
                    applied = true;
                });
                return applied;
            }
            return false;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ugoira load failed: {ex.Message}");
            return false;
        }
    }

    private void UpdatePageIndicator()
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var total = _pages.Count > 0 ? _pages.Count : _currentCard?.PageCount ?? 1;
            var current = _currentPageIndex + 1;
            var isMulti = total > 1;

            if (PageIndicatorText != null)
                PageIndicatorText.Text = $"{current} / {total}";

            // Update context menu labels with real page counts
            if (CtxDlPageHeader != null)
                CtxDlPageHeader.Text = isMulti
                    ? $"↓ Download page {current} of {total}"
                    : "↓ Download this image";
            if (CtxDlAll != null)
                CtxDlAll.IsVisible = isMulti;
            if (CtxDlAllHeader != null)
                CtxDlAllHeader.Text = $"↓ Download all {total} pages";
            if (CtxDlRange != null)
                CtxDlRange.IsVisible = isMulti;
            if (CtxDlRangeHeader != null)
                CtxDlRangeHeader.Text = $"↓ Download page range… (1–{total})";

            // Drive the visible action-bar buttons from the real page count too —
            // pre-load metadata can be wrong, so override the XAML IsVisible binding.
            var prev = this.FindControl<Button>("PrevPageBtn");
            var next = this.FindControl<Button>("NextPageBtn");
            var dlAll = this.FindControl<Button>("DlAllBtn");
            var dlRange = this.FindControl<Button>("DlRangeBtn");
            if (prev != null) prev.IsVisible = isMulti;
            if (next != null) next.IsVisible = isMulti;
            if (dlAll != null) dlAll.IsVisible = isMulti;
            if (dlRange != null) dlRange.IsVisible = isMulti;
        });
    }

    /// <summary>Library tabs show files the user already has, so downloading and comments are hidden.</summary>
    private bool IsLibraryTab => VM?.SelectedViewerTab?.Source == "Library";

    // ── Library tab actions ─────────────────────────────────────────────────
    // Per card, not per tab: prev/next inside a Library tab moves between files.
    private string? LibraryPath => _currentCard?.LocalPath ?? VM?.SelectedViewerTab?.LocalPath;
    private long LibraryFileId => _currentCard is { LibraryFileId: > 0 } c ? c.LibraryFileId : VM?.SelectedViewerTab?.LibraryFileId ?? 0;

    private async Task UpdateLibraryInfoAsync()
    {
        if (LibraryInfoLabel == null) return;
        var id = LibraryFileId;
        if (!IsLibraryTab || id == 0) { LibraryInfoLabel.Text = ""; return; }
        try
        {
            var f = await AppServices.Get<LibraryViewModel>().GetFileAsync(id);
            if (f == null || LibraryFileId != id) return;
            var date = DateTime.TryParse(f.Mtime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime().ToString("g") : "";
            LibraryInfoLabel.Text = $"{f.Width}×{f.Height} · {DuplicateFileRow.FormatSize(f.Size)} · {date}";
        }
        catch { LibraryInfoLabel.Text = ""; }
    }

    private void OnLibrarySourceClicked(object? sender, RoutedEventArgs e)
    {
        var tab = VM?.SelectedViewerTab;
        if (tab?.Source != "Library") return;
        _ = Task.Run(async () =>
        {
            var f = await AppServices.Get<LibraryViewModel>().GetFileAsync(tab.LibraryFileId);
            var src = f?.SourceUrl ?? (f?.ArtworkId != null ? $"https://www.pixiv.net/artworks/{f.ArtworkId}" : null);
            if (src != null)
                await Dispatcher.UIThread.InvokeAsync(() =>
                    global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(src) { UseShellExecute = true }));
        });
    }

    private void OnLibraryOpenFile(object? sender, RoutedEventArgs e)
    {
        if (LibraryPath is { } p && File.Exists(p) && OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
    }

    private void OnLibraryShowInFolder(object? sender, RoutedEventArgs e)
    {
        if (LibraryPath is { } p && File.Exists(p) && OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p}\"");
    }

    private void OnLibraryCopyPath(object? sender, RoutedEventArgs e)
    {
        if (LibraryPath is not { } p || TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
        var dt = new global::Avalonia.Input.DataTransfer();
        dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(p));
        _ = cb.SetDataAsync(dt);
    }

    private async void OnLibraryFindOriginal(object? sender, RoutedEventArgs e)
    {
        try { if (LibraryFileId is > 0 and var id) await AppServices.Get<LibraryViewModel>().ViewerReverseSearchAsync(id); }
        catch (Exception ex) { if (VM != null) VM.StatusMessage = ex.Message; }
    }

    private async void OnLibraryTagWithAi(object? sender, RoutedEventArgs e)
    {
        try { if (LibraryFileId is > 0 and var id) await AppServices.Get<LibraryViewModel>().ViewerTagWithAiAsync(id); }
        catch (Exception ex) { if (VM != null) VM.StatusMessage = ex.Message; }
    }

    private async void OnLibraryDelete(object? sender, RoutedEventArgs e)
    {
        try { if (LibraryFileId is > 0 and var id) await AppServices.Get<LibraryViewModel>().ViewerDeleteAsync(id); }
        catch (Exception ex) { if (VM != null) VM.StatusMessage = ex.Message; }
    }

    private void ApplyTransform()
    {
        void ApplyTo(Image img)
        {
            if (img.RenderTransform is TransformGroup tg)
            {
                if (tg.Children[0] is ScaleTransform s) { s.ScaleX = _scale; s.ScaleY = _scale; }
                if (tg.Children[1] is TranslateTransform t) { t.X = _translateX; t.Y = _translateY; }
            }
        }
        ApplyTo(ViewerImage);
        ApplyTo(UgoiraImage);
        if (ZoomLabel != null) ZoomLabel.Text = $"{_scale * 100:0}%";
        if (_scale >= FullResThreshold && !_fullResLoaded && _currentCard?.LocalPath is { } local && File.Exists(local))
            _ = LoadLocalFullResAsync(_currentCard, local);
        else if (_scale >= FullResThreshold && !_fullResLoaded && !string.IsNullOrEmpty(_currentOriginalUrl))
            _ = LoadFullResAsync(_currentOriginalUrl!, _currentPageIndex);
    }

    /// <summary>Library zoom: swap in the original-resolution file.</summary>
    private async Task LoadLocalFullResAsync(ArtworkCardViewModel card, string path)
    {
        _fullResLoaded = true; // claim it so repeated zoom steps don't start parallel decodes
        try
        {
            var bmp = await Task.Factory.StartNew(() => { using var fs = File.OpenRead(path); return new Bitmap(fs); },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (ReferenceEquals(_currentCard, card) && ViewerImage != null) ViewerImage.Source = bmp;
        }
        catch { _fullResLoaded = false; }
    }

    private static byte[]? LocalImageForHoshi(string path) => AiViewModel.LoadLocalImageForVision(path);

    private async Task LoadFullResAsync(string originalUrl, int pageIndex)
    {
        // priority: this is the image the user is looking at — it must not queue behind, or split
        // bandwidth with, the gallery's background thumbnail fetches.
        var bytes = await _imageLoader.FetchBytesAsync(originalUrl, default, priority: true);
        if (bytes == null || _currentOriginalUrl != originalUrl) return;

        // Decode off the UI thread — originals can be large (4–8 MB JPEG)
        var bmp = await Task.Run(() =>
        {
            try { using var ms = new MemoryStream(bytes); return new Bitmap(ms); }
            catch { return null; }
        });
        if (bmp == null || _currentOriginalUrl != originalUrl) { bmp?.Dispose(); return; }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_currentOriginalUrl != originalUrl) { bmp.Dispose(); return; }
            _pageBitmaps[pageIndex] = bmp;
            _pageFullResLoaded.Add(pageIndex);
            if (_currentPageIndex == pageIndex)
            {
                _fullResLoaded = true;
                ViewerImage.Source = bmp;
                // Update the tab cache with the higher-res bitmap.
                if (VM?.SelectedViewerTab is { } activeTab && activeTab.Card?.Id == _currentCard?.Id)
                {
                    activeTab.CachedBitmap = bmp;
                    activeTab.CachedOriginalUrl = originalUrl;
                    activeTab.CachedFullResLoaded = true;
                }
                if (_currentCard != null)
                {
                    var cache = _cardPageCache.GetValue(_currentCard, _ => new CardPageCache());
                    cache.CurrentOriginalUrl = originalUrl;
                    cache.FullResLoaded = true;
                }
            }
        });
    }

    private void ResetZoom()
    {
        _scale = 1.0; _translateX = 0; _translateY = 0;
        ApplyTransform();
    }

    /// <summary>True when the event started inside the All-pages grid or the page strip — those
    /// sit on top of the image canvas, and scrolling/dragging them must not zoom or pan the image.</summary>
    private bool IsFromPageOverlay(RoutedEventArgs e)
    {
        for (var v = e.Source as Visual; v != null && !ReferenceEquals(v, ImageCanvas); v = v.GetVisualParent())
            if (v is Control { Name: "PageGridOverlay" or "PageStrip" }) return true;
        return false;
    }

    private void OnImageWheel(object? sender, PointerWheelEventArgs e)
    {
        if (IsFromPageOverlay(e)) return;
        var delta = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        ZoomAroundCenter(delta);
        e.Handled = true;
    }

    // Zoom toward a canvas-coordinate point (cursor for wheel, canvas center for +/- buttons).
    // RenderTransformOrigin="0.5,0.5" means scale pivots around the image's own center.
    // The image's center in canvas coords = (canvasW/2 + _translateX, canvasH/2 + _translateY).
    // To keep canvas point P fixed: translateX_new = translateX + (P.x - canvasW/2 - translateX) * (1 - factor)
    private void ZoomToward(Point pivot, double factor)
    {
        if (ImageCanvas == null) return;
        var cx = ImageCanvas.Bounds.Width  / 2.0;
        var cy = ImageCanvas.Bounds.Height / 2.0;
        // Offset of pivot from image center
        var dx = pivot.X - cx - _translateX;
        var dy = pivot.Y - cy - _translateY;
        _translateX += dx * (1.0 - factor);
        _translateY += dy * (1.0 - factor);
        _scale = Math.Clamp(_scale * factor, 0.1, 10.0);
        ApplyTransform();
    }

    private bool _isPinching;
    private double _pinchStartScale = 1.0;
    private double _pinchStartDist = -1;
    private readonly Dictionary<int, Point> _touchPts = new(); // active non-mouse pointers
    private DateTime _touchDownAt;
    private Point _touchStartPos;
    private int _lastPointerType = -1;

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _lastPointerType = (int)e.Pointer.Type;
        if (e.Pointer.Type != PointerType.Mouse)
        {
            // touch/pen: track for pinch + swipe; only pan when zoomed in
            _touchPts[e.Pointer.Id] = e.GetPosition(ImageCanvas);
            if (_touchPts.Count == 1) _touchStartPos = e.GetPosition(ImageCanvas);
            if (_touchPts.Count == 2)
            {
                // second finger down → pinch starts; cancel any pan
                _isPanning = false; _isPinching = true;
                _pinchStartScale = _scale;
                var pts = _touchPts.Values.ToList();
                _pinchStartDist = Math.Sqrt(Math.Pow(pts[0].X - pts[1].X, 2) + Math.Pow(pts[0].Y - pts[1].Y, 2));
            }
            if (ImageCanvas is null || IsFromPageOverlay(e) || _touchPts.Count > 1) return;
            // One finger always moves the image freely (like the mouse); a quick sideways flick
            // at normal zoom still changes page — see OnImagePointerReleased.
            _touchDownAt = DateTime.UtcNow;
            _isPanning = true;
            _panDragging = false;
            _panStart = e.GetPosition(ImageCanvas);
            _panStartX = _translateX;
            _panStartY = _translateY;
            e.Pointer.Capture(ImageCanvas);
            e.Handled = true;
            return;
        }
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (ImageCanvas is null || IsFromPageOverlay(e)) return;
        _isPanning = true;
        _panDragging = false;
        _panStart = e.GetPosition(ImageCanvas);
        _panStartX = _translateX;
        _panStartY = _translateY;
        e.Pointer.Capture(ImageCanvas);
        e.Handled = true;
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse && _touchPts.ContainsKey(e.Pointer.Id))
        {
            _touchPts[e.Pointer.Id] = e.GetPosition(ImageCanvas);
            if (_isPinching && _touchPts.Count == 2 && _pinchStartDist > 0)
            {
                var pts = _touchPts.Values.ToList();
                var dist = Math.Sqrt(Math.Pow(pts[0].X - pts[1].X, 2) + Math.Pow(pts[0].Y - pts[1].Y, 2));
                _scale = Math.Clamp(_pinchStartScale * (dist / _pinchStartDist), 0.1, 10.0);
                ApplyTransform();
                e.Handled = true;
                return;
            }
        }
        if (_isPinching) return;
        if (!_isPanning) return;
        var pos = e.GetPosition(ImageCanvas);
        var dx = pos.X - _panStart.X;
        var dy = pos.Y - _panStart.Y;

        if (!_panDragging)
        {
            if (Math.Abs(dx) < PanThreshold && Math.Abs(dy) < PanThreshold) return;
            _panDragging = true;
        }

        _translateX = _panStartX + dx;
        _translateY = _panStartY + dy;
        ApplyTransform();
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _touchPts.Remove(e.Pointer.Id);
        if (_touchPts.Count < 2) { _isPinching = false; _pinchStartDist = -1; }
        if (_touchPts.Count == 0) _lastPointerType = -1;
        // Touch swipe (only when not zoomed): >80px horizontal, >2x vertical → page/artwork nav.
        if (e.Pointer.Type != PointerType.Mouse && _scale <= 1.05 && ImageCanvas != null)
        {
            var pos = e.GetPosition(ImageCanvas);
            var dx = pos.X - _touchStartPos.X;
            var dy = pos.Y - _touchStartPos.Y;
            // Flick = fast (<350 ms), long (>80px) and mostly horizontal; slower drags just move the image.
            var quick = (DateTime.UtcNow - _touchDownAt).TotalMilliseconds < 350;
            if (quick && _pages.Count > 1 && Math.Abs(dx) > 80 && Math.Abs(dx) > Math.Abs(dy) * 2)
            {
                _translateX = _panStartX; _translateY = _panStartY; // undo the drag before flipping
                ApplyTransform();
                if (dx < 0) OnNextPage(sender, new RoutedEventArgs());
                else OnPrevPage(sender, new RoutedEventArgs());
            }
        }
        _isPanning = false;
        _panDragging = false;
        e.Pointer.Capture(null);
    }

    private void OnImagePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPanning = false;
        _panDragging = false;
    }

    private void OnImagePointerExited(object? sender, PointerEventArgs e)
    {
        _isPanning = false;
        _panDragging = false;
        e.Pointer.Capture(null);
    }

    // ── Controls ─────────────────────────────────────────────────────────────

    private void ZoomAroundCenter(double factor)
    {
        // Scale in place: translate stays the same, image grows/shrinks from its current center.
        // This produces the "zoom from all four sides equally" effect.
        _scale = Math.Clamp(_scale * factor, 0.1, 10.0);
        ApplyTransform();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e)  => ZoomAroundCenter(1.25);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => ZoomAroundCenter(1.0 / 1.25);
    private void OnZoomFit(object? sender, RoutedEventArgs e) => ResetZoom();

    private void OnPrevPage(object? sender, RoutedEventArgs e)
    {
        if (_currentPageIndex > 0)
        {
            _currentPageIndex--;
            UpdatePageIndicator();
            _ = RenderPageAsync(_currentPageIndex);
        }
    }

    private void OnNextPage(object? sender, RoutedEventArgs e)
    {
        if (_currentPageIndex < _pages.Count - 1)
        {
            _currentPageIndex++;
            UpdatePageIndicator();
            _ = RenderPageAsync(_currentPageIndex);
        }
    }

    private async Task LoadPageListForCachedCardAsync(string cardId)
    {
        try
        {
            var pages = await _pixivClient.GetArtworkPagesAsync(cardId);
            _pages = pages;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                VM?.InlinePageList.Clear();
                for (int i = 0; i < pages.Count; i++)
                    VM?.InlinePageList.Add(new PageThumbnailItem(i, i + 1));
                UpdatePageIndicator();
            });
            _ = LoadInlinePageThumbnailsAsync(pages);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load page list for cached card: {ex.Message}");
        }
    }

    private async Task LoadInlinePageThumbnailsAsync(IReadOnlyList<ArtworkPage> pages)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            try
            {
                var url = pages[i].Urls.Small
                       ?? pages[i].Urls.Regular
                       ?? pages[i].Urls.ThumbMini;
                if (string.IsNullOrEmpty(url)) continue;

                var skBitmap = await _imageLoader.FetchBitmapAsync(url, ThumbnailSize.Medium);
                if (skBitmap is null) continue;

                var bmp = await Task.Run(() =>
                    (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap));
                skBitmap.Dispose();

                if (bmp is not null && i < (VM?.InlinePageList.Count ?? 0))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (i < (VM?.InlinePageList.Count ?? 0))
                            VM!.InlinePageList[i].Thumbnail = bmp;
                    });
                }
            }
            catch { /* best-effort page thumbnails */ }
        }
    }

    private void OnPageStripItemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PageThumbnailItem item) return;
        if (item.Index < 0 || item.Index >= _pages.Count) return;
        _currentPageIndex = item.Index;
        UpdatePageIndicator();
        _ = RenderPageAsync(_currentPageIndex);
    }

    private void OnInlinePageGridItemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PageThumbnailItem item) return;
        if (VM is not null) VM.IsInlinePageGridVisible = false;
        if (item.Index < 0 || item.Index >= _pages.Count) return;
        _currentPageIndex = item.Index;
        UpdatePageIndicator();
        _ = RenderPageAsync(_currentPageIndex);
    }

    private void OnToggleInlinePageGrid(object? sender, RoutedEventArgs e)
    {
        if (VM is null) return;
        VM.ToggleInlinePageGridCommand.Execute(null);
        // The strip's ~540px square crops look blurry at grid-tile size; load sharp versions.
        if (VM.IsInlinePageGridVisible) _ = UpgradeInlinePageThumbnailsAsync(_pages);
    }

    private IReadOnlyList<ArtworkPage>? _upgradedPages;

    /// <summary>Replaces each page tile with the master1200 image (aspect kept), once per page list.</summary>
    private async Task UpgradeInlinePageThumbnailsAsync(IReadOnlyList<ArtworkPage> pages)
    {
        if (pages.Count == 0 || ReferenceEquals(_upgradedPages, pages)) return;
        _upgradedPages = pages;
        for (int i = 0; i < pages.Count; i++)
        {
            if (!ReferenceEquals(_pages, pages)) return; // switched artwork meanwhile
            try
            {
                var url = pages[i].Urls.Regular ?? pages[i].Urls.Small;
                if (string.IsNullOrEmpty(url)) continue;
                using var sk = await _imageLoader.FetchBitmapAsync(url, ThumbnailSize.Large);
                if (sk is null) continue;
                var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(sk));
                var index = i;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_pages, pages) && bmp != null && index < (VM?.InlinePageList.Count ?? 0))
                        VM!.InlinePageList[index].Thumbnail = bmp;
                });
            }
            catch { /* keep the low-res tile */ }
        }
    }

    private void OnDownloadCurrentPage(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null || VM == null) return;
        _ = VM.DownloadSinglePageAsync(_currentCard, _currentPageIndex);
    }

    private void OnDownloadAllPages(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null || VM == null) return;
        _ = VM.DownloadSingleAsync(_currentCard);
    }

    private async void OnDownloadPageRange(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null || VM == null) return;
        if (_pages.Count <= 1) { OnDownloadCurrentPage(sender, e); return; }
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        var dialog = new RangePickerDialog(
            title: $"Page range — {_currentCard.Title}",
            description: $"Artwork has {_pages.Count} pages (0-based). Examples: \"0-2\", \"0,3,5\".",
            maxInclusive: _pages.Count - 1,
            placeholder: $"0-{_pages.Count - 1}");
        var ok = await dialog.ShowDialog<bool?>(window);
        if (ok == true && dialog.SelectedIndexes.Count > 0)
            _ = VM.DownloadPagesAsync(_currentCard, dialog.SelectedIndexes);
    }

    private async void OnDownloadWithPreset(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null || VM == null) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

// Get required services and presets
var imageResizeService = AppServices.Get<ImageResizeService>();
var dialogService = AppServices.Get<DialogService>();
var imageLoader = AppServices.Get<PixivImageLoader>();
var pixivClient = AppServices.Get<PixivClient>();
var userPresetsRepo = AppServices.Get<UserPresetsRepository>();
var customPresets = await userPresetsRepo.GetAllAsync();

// Create artwork preview list - map ArtworkPreview to Dialogs.ArtworkPreview
var artwork = _currentCard.Artwork;

var artworks = new List<global::Pikura.Avalonia.Views.Dialogs.ArtworkPreview>
{
    new()
    {
        ArtworkId = artwork.Id ?? "",
        Title = artwork.Title ?? "",
        ArtistName = artwork.UserName ?? "",
        ThumbnailUrl = artwork.ThumbnailUrl,
        PageCount = artwork.PageCount,
        IllustType = artwork.IllustType
    }
};

// Show the download preset window
var presetWindow = new DownloadPresetWindow(
    imageResizeService,
    dialogService,
    imageLoader,
    pixivClient,
    artworks,
    customPresets?.ToList());
var result = await presetWindow.ShowDialog<ImageEditPreset?>(window);

// Only download if user didn't cancel (result != null) and clicked Download button
if (result != null && presetWindow.DownloadClicked)
{
    // Download with the selected preset via ViewModel
    _ = VM.DownloadWithPresetAsync(_currentCard, result);
}
    }

    private async Task PreloadCollageThumbnailsAsync(IEnumerable<ArtworkCardViewModel> items, bool upgradeToLarge = false)
    {
        if (_imageLoader is null) return;
        var list = items.ToList();

        // Ensure every tile has at least a medium thumbnail.
        var loadTasks = list
            .Where(c => c.Thumbnail is null)
            .Select(c => c.LoadThumbnailAsync(_imageLoader))
            .ToList();
        if (loadTasks.Count > 0)
            await Task.WhenAll(loadTasks);

        if (!upgradeToLarge) return;
        await UpgradeCollageThumbnailsAsync(list);
    }

    /// <summary>
    /// Upgrades every collage tile to the 1200px master so zooming stays sharp.
    /// <para>
    /// Deliberately kicked off <i>after</i> the fullscreen window is shown rather than awaited
    /// before it. A collage can hold up to 100 tiles, and awaiting 100 large downloads+decodes
    /// made "Fullscreen" look frozen for seconds with no feedback. Tiles now sharpen in place,
    /// which is a far better trade than a blocked UI.
    /// </para>
    /// </summary>
    private async Task UpgradeCollageThumbnailsAsync(IEnumerable<ArtworkCardViewModel> items)
    {
        if (_imageLoader is null) return;
        try { await Task.WhenAll(items.Select(c => c.UpgradeThumbnailAsync(_imageLoader, ThumbnailSize.Large))); }
        catch { /* non-fatal — tiles just stay at medium resolution */ }
    }

    /// <summary>
    /// The artworks belonging to the currently selected tab, used to scope fullscreen navigation
    /// and the fullscreen artwork strip. A collage tab contributes its tiles; any other tab
    /// contributes its own nav list. Returns null when the tab has nothing specific, letting the
    /// fullscreen viewer fall back to the gallery-derived list.
    /// </summary>
    private IReadOnlyList<ArtworkCardViewModel>? CurrentTabNavList()
    {
        if (VM?.SelectedViewerTab is not { } tab) return null;
        if (tab is { IsCollage: true, CollageItems.Count: > 0 }) return tab.CollageItems.ToList();
        return tab.NavList.Count > 0 ? tab.NavList.ToList() : null;
    }

    private async void OnOpenPopup(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null || VM == null) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;
        var viewer = new ArtworkViewerWindow(_currentCard.Artwork, VM);
        await viewer.ShowDialog(window);
    }

    private async void OnFullscreen(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null || VM == null) return;

        if (VM.IsCollageGridVisible && VM.CollageItems is { Count: > 0 } collageItems)
        {
            // Only guarantee a medium thumbnail (usually already cached, so near-instant). The
            // 1200px upgrade runs in the background once the window is up — see
            // UpgradeCollageThumbnailsAsync.
            await PreloadCollageThumbnailsAsync(collageItems, upgradeToLarge: false);
            var collage = new CollageFullscreenWindow(collageItems);
            _ = UpgradeCollageThumbnailsAsync(collageItems);
            await collage.ShowDialog(window);
            return;
        }

        if (_currentCard == null) return;

        // Hand the already-decoded bitmap to the fullscreen window so it paints instantly instead
        // of showing an empty canvas while it re-fetches the pages list and image from scratch.
        var seed = ViewerImage?.Source as Bitmap;

        // Scope fullscreen navigation to the *current tab's own* artworks, whatever kind of tab it
        // is: a collage uses its tiles, any other tab uses its own nav list. Without this the
        // arrow keys and the artwork strip wander into the whole surrounding gallery rather than
        // staying within the set the user actually opened.
        var navList = CurrentTabNavList();

        var viewer = new FullscreenViewerWindow(_currentCard.Artwork, VM, seed, navList);
        await viewer.ShowDialog(window);
    }

    private void OnOpenInPixiv(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        var url = $"https://www.pixiv.net/artworks/{_currentCard.Id}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OnOpenImageInBrowser(object? sender, RoutedEventArgs e)
    {
        var url = _currentOriginalUrl
               ?? (_pages.Count > 0 ? _pages[_currentPageIndex].Urls.Regular : null);
        if (string.IsNullOrEmpty(url)) return;
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb == null) return;
        var dt = new global::Avalonia.Input.DataTransfer();
        dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(url));
        _ = cb.SetDataAsync(dt);
        if (VM != null) VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_ImageURLCopiedPasteIntoA"];
    }

    // Use the active tab's nav list, then InlineViewerCardList, then FilteredArtworks.
    private System.Collections.Generic.IReadOnlyList<ArtworkCardViewModel> NavList()
    {
        if (VM?.SelectedViewerTab is { } tab && tab.NavList.Count > 0)
            return tab.NavList;
        if (VM?.InlineViewerCardList is { } ext)
            return ext;
        return VM?.FilteredArtworks
            ?? (System.Collections.Generic.IReadOnlyList<ArtworkCardViewModel>)System.Array.Empty<ArtworkCardViewModel>();
    }

    private void OnCloseAllClicked(object? sender, RoutedEventArgs e)
    {
        VM?.CloseInlineViewerCommand.Execute(null);
        RaiseEvent(new RoutedEventArgs(ViewerClosedEvent, this));
    }

    private void OnBrowseButtonClicked(object? sender, RoutedEventArgs e)
    {
        var args = new RoutedEventArgs(ToggleBrowseEvent, this);
        RaiseEvent(args);
        if (!args.Handled)
            VM?.TogglePreviewCommand.Execute(null);
    }

    private void OnExpandButtonClicked(object? sender, RoutedEventArgs e)
    {
        var args = new RoutedEventArgs(ExpandViewerEvent, this);
        RaiseEvent(args);
        if (!args.Handled && VM != null)
            VM.IsViewerExpanded = !VM.IsViewerExpanded;
    }

    public void NavigatePrev() => OnPrevArtwork(null, new RoutedEventArgs());
    public void NavigateNext() => OnNextArtwork(null, new RoutedEventArgs());

    /// <summary>
    /// Keyboard-aware ← navigation: steps back one page within a multi-page artwork first;
    /// only moves to the previous artwork in the list when already on page 0.
    /// Falls back to card.PageCount when _pages hasn't finished loading yet.
    /// </summary>
    public void NavigatePrevWithPages()
    {
        var pageCount = _pages.Count > 0 ? _pages.Count : (_currentCard?.PageCount ?? 1);
        if (pageCount > 1 && _currentPageIndex > 0)
            OnPrevPage(null, new RoutedEventArgs());
        else
            OnPrevArtwork(null, new RoutedEventArgs());
    }

    /// <summary>
    /// Keyboard-aware → navigation: steps forward one page within a multi-page artwork first;
    /// only moves to the next artwork in the list when already on the last page.
    /// Falls back to card.PageCount when _pages hasn't finished loading yet.
    /// </summary>
    public void NavigateNextWithPages()
    {
        var pageCount = _pages.Count > 0 ? _pages.Count : (_currentCard?.PageCount ?? 1);
        if (pageCount > 1 && _currentPageIndex < pageCount - 1)
            OnNextPage(null, new RoutedEventArgs());
        else
            OnNextArtwork(null, new RoutedEventArgs());
    }

    private void OnPrevArtwork(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        var list = NavList();
        var idx = IndexOfById(list, _currentCard.Id);
        if (idx > 0) NavigateToArtwork(list[idx - 1]);
    }

    private async void OnNextArtwork(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        var list = NavList();
        var idx = IndexOfById(list, _currentCard.Id);
        if (idx < 0) return;

        if (idx >= list.Count - 1 && VM?.SelectedViewerTab is { LoadMoreAsync: not null } loadTab)
        {
            await LoadMoreIntoTabAsync(loadTab);
            list = NavList();
            idx = IndexOfById(list, _currentCard.Id);
        }

        if (idx >= 0 && idx < list.Count - 1)
            NavigateToArtwork(list[idx + 1]);
    }

    private void OnFirstArtwork(object? sender, RoutedEventArgs e)
    {
        var list = NavList();
        if (list.Count > 0) NavigateToArtwork(list[0]);
    }

    private async void OnLastArtwork(object? sender, RoutedEventArgs e)
    {
        if (VM?.SelectedViewerTab is { LoadMoreAsync: not null } tab)
            await LoadToPositionAsync(tab, tab.TotalCount);

        var list = NavList();
        if (list.Count > 0) NavigateToArtwork(list[^1]);
    }

    private async void OnArtworkJumpKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        e.Handled = true;
        if (!int.TryParse(box.Text, out var position) || position < 1) return;

        if (VM?.SelectedViewerTab is { LoadMoreAsync: not null } tab)
            await LoadToPositionAsync(tab, position);

        var list = NavList();
        var target = list.FirstOrDefault(c => c.ViewerPosition == position)
                     ?? (position <= list.Count ? list[position - 1] : null);
        if (target != null)
        {
            NavigateToArtwork(target);
            box.Text = string.Empty;
        }
    }

    private async Task LoadToPositionAsync(ViewerTab tab, int position)
    {
        var target = tab.TotalCount > 0 ? Math.Min(position, tab.TotalCount) : position;
        bool HasReachedTarget() => tab.NavList.Any(c => c.ViewerPosition == target)
            || (tab.NavList.All(c => c.ViewerPosition == null) && tab.NavList.Count >= target)
            || tab.NavList.Max(c => c.ViewerPosition ?? 0) >= target;
        while (!HasReachedTarget())
        {
            var before = tab.NavList.Count;
            await LoadMoreIntoTabAsync(tab);
            if (tab.NavList.Count == before) break;
        }
    }

    private void NavigateToArtwork(ArtworkCardViewModel card)
    {
        if (VM == null) return;
        if (VM.SelectedViewerTab is { } tab)
            tab.NavigateTo(card);
        _currentCard = null;
        VM.InlineViewerCard = card;
    }

    private async Task LoadMoreIntoTabAsync(ViewerTab tab)
    {
        if (tab.LoadMoreAsync == null) return;
        if (NextArtworkBtn != null) NextArtworkBtn.IsEnabled = false;
        try
        {
            var newCards = await tab.LoadMoreAsync();
            var existingIds = new System.Collections.Generic.HashSet<string>(tab.NavList.Select(c => c.Id));
            foreach (var c in newCards)
                if (existingIds.Add(c.Id)) tab.NavList.Add(c);
            tab.TotalCount = Math.Max(tab.TotalCount, tab.NavList.Count);
            UpdateArtworkCounter();
        }
        catch { }
    }

    private static int IndexOfById(System.Collections.Generic.IReadOnlyList<ArtworkCardViewModel> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == id) return i;
        return -1;
    }

    private void UpdateArtworkCounter()
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (VM == null || _currentCard == null || ArtworkCounterLabel == null) return;
            var list = NavList();
            var idx = IndexOfById(list, _currentCard.Id);
            if (idx < 0)
            {
                ArtworkCounterLabel.Text = "";
                if (FirstArtworkBtn != null) FirstArtworkBtn.IsEnabled = false;
                if (PrevArtworkBtn != null) PrevArtworkBtn.IsEnabled = false;
                if (NextArtworkBtn != null) NextArtworkBtn.IsEnabled = false;
                if (LastArtworkBtn != null) LastArtworkBtn.IsEnabled = false;
                return;
            }
            // Use the tab's true total (full artist catalogue) if available
            var tab = VM.SelectedViewerTab;
            var total = (tab != null && tab.TotalCount > list.Count) ? tab.TotalCount : list.Count;
            var position = _currentCard.ViewerPosition ?? idx + 1;
            ArtworkCounterLabel.Text = $"{position} / {total}";
            if (FirstArtworkBtn != null) FirstArtworkBtn.IsEnabled = idx > 0;
            if (PrevArtworkBtn != null) PrevArtworkBtn.IsEnabled = idx > 0;
            // Can go next if not at loaded end, or if more can be loaded from source
            var canGoNext = idx < list.Count - 1 || (tab?.LoadMoreAsync != null && idx + 1 < total);
            if (NextArtworkBtn != null) NextArtworkBtn.IsEnabled = canGoNext;
            if (LastArtworkBtn != null) LastArtworkBtn.IsEnabled = idx + 1 < total;
        });
    }

    private void OnGoToArtist(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        NavigateToArtistGallery(_currentCard.UserId);
    }

    private async Task RefreshFollowStateAsync(ArtworkCardViewModel card)
    {
        try
        {
            var info = await _pixivClient.GetArtistAsync(card.UserId);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (info == null || _currentCard?.Id != card.Id) return;
                _isFollowing = info.IsFollowed;
                card.IsFollowed = info.IsFollowed;
                UpdateFollowButton();
            });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[InlineArtworkViewer] follow check failed: {ex.Message}"); }
    }

    private async void NavigateToArtistGallery(string userId)
    {
        var galleryVm = AppServices.Get<GalleryViewModel>();
        try
        {
            // Cards built from partial data have no artist id yet — resolve it from the artwork.
            if (string.IsNullOrWhiteSpace(userId) && _currentCard?.Id is { } artId)
                userId = (await _pixivClient.GetArtworkDetailAsync(artId))?.UserId ?? "";
            if (string.IsNullOrWhiteSpace(userId))
            {
                galleryVm.StatusMessage = LocalizationService.Instance["Viewer_ArtistUnknown"];
                return;
            }
            // Navigate to Gallery tab and load the artist — keep existing tabs open
            var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
            // Close the inline viewer only if no tabs are pinned
            if (galleryVm.ViewerTabs.Count == 0)
                galleryVm.CloseInlineViewer();
            mainWindow?.LoadGalleryView();
            await galleryVm.LoadArtistByIdCommand.ExecuteAsync(userId);
        }
        catch (Exception ex)
        {
            galleryVm.StatusMessage = LocalizationService.Instance["Viewer_ArtistUnknown"] + " " + ex.Message;
        }
    }

    private async void OnFollowToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null || VM == null) return;

        bool ok;
        if (_isFollowing)
            ok = await _pixivClient.UnfollowUserAsync(_currentCard.UserId);
        else
            ok = await _pixivClient.FollowUserAsync(_currentCard.UserId);

        if (ok)
        {
            _isFollowing = !_isFollowing;
            _currentCard.IsFollowed = _isFollowing;
            VM.SetArtistFollowed(_currentCard.UserId, _currentCard.UserName, _isFollowing);
            UpdateFollowButton();
            VM.StatusMessage = _isFollowing ? $"Following {_currentCard.UserName}" : $"Unfollowed {_currentCard.UserName}";
        }
        else
        {
            VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotUpdateFollowFollowUnfollow"];
        }
    }

    private void UpdateFollowButton()
    {
        if (FollowToggleBtn == null || FollowToggleLabel == null) return;
        FollowToggleBtn.IsVisible = _currentCard != null;
        FollowToggleLabel.Text = _isFollowing ? Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_Following"] : Pikura.Avalonia.Localization.LocalizationService.Instance["Viewer_Follow"];
        if (_isFollowing) FollowToggleBtn.Classes.Add("accent");
        else FollowToggleBtn.Classes.Remove("accent");
    }

    private async Task LoadArtworkStateAsync(ArtworkCardViewModel card)
    {
        try
        {
            var detailTask = _pixivClient.GetArtworkDetailAsync(card.Id);
            var bookmarkTask = _pixivClient.GetBookmarkStateAsync(card.Id);
            // The local followed-artists cache can be incomplete (Pixiv's paginated list can
            // shift while we're fetching hundreds of pages), so re-check the live follow state
            // for this artist directly rather than trusting only the local Artists collection.
            var followTask = string.IsNullOrWhiteSpace(card.UserId)
                ? Task.FromResult<Core.Models.PixivUserInfo?>(null)
                : _pixivClient.GetArtistAsync(card.UserId);

            await Task.WhenAll(detailTask, bookmarkTask, followTask);

            var detail = await detailTask;
            var bookmark = await bookmarkTask;
            var followInfo = await followTask;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_currentCard?.Id != card.Id) return;

                // Cards built from partial data (Library files) lack artist/title — fill them in.
                if (detail != null && string.IsNullOrWhiteSpace(card.UserId) && !string.IsNullOrWhiteSpace(detail.UserId))
                {
                    card.UserId = detail.UserId!;
                    card.UserName = detail.UserName ?? "";
                    if (!string.IsNullOrWhiteSpace(detail.IllustTitle)) card.Title = detail.IllustTitle!;
                    UpdateArtistButtonVisibility();
                    if (followInfo == null) _ = RefreshFollowStateAsync(card);
                }

                _bookmarkCount = detail?.BookmarkCount ?? card.BookmarkCount;
                _likeCount = detail?.LikeCount ?? card.LikeCount;
                _viewCount = detail?.ViewCount ?? card.ViewCount;
                _statsLoaded = true;
                if (card.Id is not null)
                    _statsCache[card.Id] = (_likeCount, _bookmarkCount, _viewCount);
                _isBookmarked = bookmark?.IsBookmarked ?? false;
                _isPrivateBookmark = bookmark?.IsPrivate ?? false;
                _bookmarkId = bookmark?.BookmarkId;
                // This live check is the ONLY place that discovers "this artwork is already
                // bookmarked" when simply opening it (as opposed to clicking Bookmark, which
                // already updates the card directly) — push it onto the card too, otherwise its
                // badge in the Gallery/Discover/Bookmarks grid stays stale until some other
                // action happens to touch it.
                card.IsPixivBookmarked = _isBookmarked;
                card.IsPixivPrivateBookmark = _isPrivateBookmark;
                card.PixivBookmarkId = _bookmarkId;
                if (card.Id is not null)
                {
                    try { AppServices.Get<BookmarksViewModel>().SyncBookmarked(card, _isBookmarked, _isPrivateBookmark, _bookmarkId); }
                    catch { /* Bookmarks view not initialized yet */ }
                    SyncCardFlagsEverywhere(isPixivBookmarked: _isBookmarked, isPixivPrivateBookmark: _isPrivateBookmark,
                        pixivBookmarkId: _bookmarkId, bookmarkIdProvided: true, targetCard: card);
                }

                if (followInfo != null)
                {
                    _isFollowing = followInfo.IsFollowed;
                    card.IsFollowed = followInfo.IsFollowed;
                    if (followInfo.IsFollowed)
                        VM?.SetArtistFollowed(card.UserId, card.UserName, true);
                }

                UpdateStatsLabel();
                UpdateLikeButton();
                UpdateBookmarkButtons();
                UpdateFollowButton();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InlineArtworkViewer] LoadArtworkStateAsync({card.Id}) failed: {ex.Message}");
        }
    }

    private void UpdateStatsLabel()
    {
        // Until the real detail fetch resolves, the card's counts default to 0 (the
        // gallery-listing endpoint doesn't return them) — show "…" instead of a
        // momentary, misleading "0 / 0 / 0".
        if (!_statsLoaded)
        {
            if (LikeCountLabel != null) LikeCountLabel.Text = "…";
            if (BookmarkCountLabel != null) BookmarkCountLabel.Text = "…";
            if (ViewCountLabel != null) ViewCountLabel.Text = "…";
            return;
        }
        if (LikeCountLabel != null) LikeCountLabel.Text = _likeCount.ToString(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_N0"], CultureInfo.InvariantCulture);
        if (BookmarkCountLabel != null) BookmarkCountLabel.Text = _bookmarkCount.ToString(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_N0"], CultureInfo.InvariantCulture);
        if (ViewCountLabel != null) ViewCountLabel.Text = _viewCount.ToString(Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_N0"], CultureInfo.InvariantCulture);
    }

    private void OnShowStatsToggled(object? sender, RoutedEventArgs e)
    {
        if (ShowStatsBtn == null || StatsStackPanel == null) return;
        var isVisible = ShowStatsBtn.IsChecked ?? true;
        StatsStackPanel.IsVisible = isVisible;
        try { AppServices.Get<Pikura.Core.Settings.SettingsService>().Update(s => s.ShowPixivStats = isVisible); }
        catch { /* non-fatal */ }
    }

    private void UpdateLikeButton()
    {
        if (LikeBtn == null || LikeBtnLabel == null) return;
        LikeBtnLabel.Text = _isLiked ? Pikura.Avalonia.Localization.LocalizationService.Instance["Artwork_Liked"] : Pikura.Avalonia.Localization.LocalizationService.Instance["Viewer_Like"];
        if (_isLiked) LikeBtn.Classes.Add("accent");
        else LikeBtn.Classes.Remove("accent");
    }

    private void UpdateBookmarkButtons()
    {
        if (BookmarkSplitBtn == null || BookmarkBtnLabel == null) return;

        if (_isBookmarked)
        {
            BookmarkBtnLabel.Text = _isPrivateBookmark ? Pikura.Avalonia.Localization.LocalizationService.Instance["Bookmarks_Private"] : Pikura.Avalonia.Localization.LocalizationService.Instance["Bookmarks_Public"];
        }
        else
        {
            BookmarkBtnLabel.Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Viewer_Bookmark"];
        }

        if (_isBookmarked) BookmarkSplitBtn.Classes.Add("accent");
        else BookmarkSplitBtn.Classes.Remove("accent");
    }

    private async void OnLikeClicked(object? sender, RoutedEventArgs e)
    {
        // Capture into a local — nullable flow analysis doesn't narrow fields across the
        // await below (another card switch could reassign _currentCard mid-flight), so
        // reading _currentCard directly after the guard still produces null-ref warnings.
        var card = _currentCard;
        var vm = VM;
        if (card == null || _isLiked || vm == null) return;
        LikeBtn!.IsEnabled = false;
        try
        {
            var ok = await _pixivClient.LikeIllustAsync(card.Id);
            if (ok)
            {
                _isLiked = true;
                _likeCount++;
                card.IsLiked = true;
                UpdateLikeButton();
                UpdateStatsLabel();
                try
                {
                    var service = vm.SettingsService;
                    if (service != null && card.Id != null && !service.Current.PixivLikedArtworkIds.Contains(card.Id))
                    {
                        service.Current.PixivLikedArtworkIds.Add(card.Id);
                        service.Save();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InlineArtworkViewer] Could not persist liked artwork: {ex.Message}");
                }
                try { AppServices.Get<BookmarksViewModel>().SyncLiked(card, true); }
                catch { /* Bookmarks view not initialized yet — nothing to sync into */ }
                SyncCardFlagsEverywhere(isLiked: true);
                vm.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_LikedOnPixiv"];
            }
            else
            {
                vm.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotLikeArtwork"];
            }
        }
        finally { LikeBtn.IsEnabled = true; }
    }

    // ── Comments ─────────────────────────────────────────────────────────────
    private readonly List<Pikura.Core.Models.PixivComment> _loadedComments = [];
    private int _commentsOffset;
    private bool _commentsHasNext;
    private bool _commentsLoading;
    private string? _commentsLoadedForArtworkId;
    /// <summary>Set while replying to a specific comment — that comment's own ID becomes
    /// <c>parent_comment_id</c> on the next post. Pixiv only supports one level of replies (a
    /// reply's own commentRootId is the top-level comment it belongs to), so replying to a
    /// reply targets the same root rather than nesting further.</summary>
    private (string Id, string UserName)? _replyingTo;
    /// <summary>Root comment ID → its (currently expanded) replies panel, so a just-posted reply
    /// can be inserted into the right thread instead of always landing at the top of the list.</summary>
    private readonly Dictionary<string, StackPanel> _repliesHosts = [];

    private DispatcherTimer? _commentsPollTimer;

    private async void OnCommentsClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        CommentsPopup.IsOpen = !CommentsPopup.IsOpen;
        if (CommentsPopup.IsOpen)
        {
            if (_commentsLoadedForArtworkId != _currentCard.Id)
                await LoadCommentsAsync(reset: true);
            StartCommentsPolling();
        }
        else
        {
            StopCommentsPolling();
        }
    }

    /// <summary>
    /// Pixiv has no push/websocket notification for new comments, so the only way to reflect a
    /// comment posted, replied to, or deleted from the website (or another device) while this
    /// panel is open is to periodically re-check. Keeps things simple and low-risk: only adds
    /// genuinely new top-level comments and removes ones that vanished from the first page —
    /// it never touches already-expanded reply threads or in-progress typing.
    /// </summary>
    private void StartCommentsPolling()
    {
        StopCommentsPolling();
        _commentsPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _commentsPollTimer.Tick += async (_, _) => await PollCommentsForChangesAsync();
        _commentsPollTimer.Start();
    }

    private void StopCommentsPolling()
    {
        _commentsPollTimer?.Stop();
        _commentsPollTimer = null;
    }

    private void OnCommentsPopupClosed(object? sender, EventArgs e) => StopCommentsPolling();

    private async Task PollCommentsForChangesAsync()
    {
        if (_currentCard?.Id == null || _commentsLoading) return;
        try
        {
            var result = await _pixivClient.GetCommentsAsync(_currentCard.Id, 0, 20);
            if (result == null) return;

            var freshIds = result.Comments.Select(c => c.Id).ToHashSet();
            // Snapshot BEFORE any mutation — only the comments we already knew about that fall
            // within this same first page are eligible to be flagged as "deleted elsewhere";
            // anything loaded further down via scroll-to-load-more is left untouched to avoid
            // any risk of misjudging it as removed just because it's outside this shallow check.
            var previouslyKnownFirstPage = _loadedComments.Take(result.Comments.Count).ToList();
            var knownIds = _loadedComments.Select(c => c.Id).ToHashSet();

            // Comments the website now has that we don't yet — insert at the top, newest first.
            var newOnes = result.Comments.Where(c => !knownIds.Contains(c.Id)).Reverse().ToList();
            foreach (var c in newOnes)
            {
                _loadedComments.Insert(0, c);
                CommentsListPanel.Children.Insert(0, BuildCommentThread(c));
            }

            var removed = previouslyKnownFirstPage.Where(c => !freshIds.Contains(c.Id)).ToList();
            if (removed.Count > 0)
            {
                foreach (var c in removed) _loadedComments.Remove(c);
                RerenderCommentsList();
            }
            else if (newOnes.Count > 0)
            {
                CommentsCountLabel.Text = result.TotalComments > 0 ? $"{result.TotalComments} comment(s)" : string.Empty;
                CommentsEmptyPanel.IsVisible = false;
            }
        }
        catch { /* transient poll failure — just try again next tick */ }
    }

    private async Task LoadCommentsAsync(bool reset)
    {
        if (_currentCard?.Id == null || _commentsLoading) return;
        _commentsLoading = true;
        try
        {
            if (reset)
            {
                _loadedComments.Clear();
                _repliesHosts.Clear();
                _commentsOffset = 0;
                _commentsLoadedForArtworkId = _currentCard.Id;
                CommentsListPanel.Children.Clear();
                CommentsEmptyPanel.IsVisible = true;
                CommentsLoadingBar.IsVisible = true;
                CommentsEmptyLabel.Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Viewer_LoadingComments"];
                CommentsCountLabel.Text = string.Empty;
            }

            var result = await _pixivClient.GetCommentsAsync(_currentCard.Id, _commentsOffset, 20);
            if (result == null)
            {
                CommentsLoadingBar.IsVisible = false;
                CommentsEmptyLabel.Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotLoadComments"];
                return;
            }

            _commentsHasNext = result.HasNext;
            _loadedComments.AddRange(result.Comments);
            _commentsOffset += result.Comments.Count;
            CommentsCountLabel.Text = result.TotalComments > 0 ? $"{result.TotalComments} comment(s)" : string.Empty;

            foreach (var c in result.Comments)
                CommentsListPanel.Children.Add(BuildCommentThread(c));

            CommentsEmptyPanel.IsVisible = _loadedComments.Count == 0;
            if (_loadedComments.Count == 0) CommentsEmptyLabel.Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_NoCommentsYetBeTheFirst"];
            CommentsLoadingBar.IsVisible = false;
        }
        catch (Exception ex)
        {
            CommentsLoadingBar.IsVisible = false;
            CommentsEmptyLabel.Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotLoadComments"];
            System.Diagnostics.Debug.WriteLine($"[InlineArtworkViewer] LoadComments failed: {ex}");
        }
        finally { _commentsLoading = false; }
    }

    private Control BuildCommentRow(Pikura.Core.Models.PixivComment c)
    {
        var avatar = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Background = Brushes.Transparent,
        };
        if (!string.IsNullOrEmpty(c.UserImageUrl))
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            avatar.Child = img;
            _ = LoadAvatarIntoAsync(img, c.UserImageUrl);
        }

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock
        {
            Text = c.UserName, FontSize = 11, FontWeight = FontWeight.SemiBold,
        });
        if (c.HasStamp && string.IsNullOrEmpty(c.Comment))
        {
            // Confirmed from inspecting a real rendered comment's CSS background-image — the
            // correct CDN domain is source.pixiv.net (not s.pximg.net, which 404s on this path
            // despite otherwise looking like a normal Pixiv image host).
            var stampImg = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left };
            body.Children.Add(stampImg);
            _ = LoadAvatarIntoAsync(stampImg, $"https://source.pixiv.net/common/images/stamp/generated-stamps/{c.StampId}_s.jpg?20180605");
        }
        else
        {
            body.Children.Add(BuildCommentTextWithEmoji(c.Comment));
        }
        var metaRow = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        metaRow.Children.Add(new TextBlock
        {
            Text = c.CommentDate ?? string.Empty, FontSize = 10,
            // ActiPro's theme resources aren't directly castable to IBrush via a plain
            // Application.FindResource lookup from code-behind (that's what
            // "actipro:ThemeResource" markup extension in XAML does) — a hardcoded gray is a
            // perfectly fine, low-risk choice for this small piece of secondary metadata text.
            Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        });
        // Pixiv only supports one level of nesting — replying to a reply still targets that
        // reply's own root comment, not the reply itself, so this is available on every comment.
        var replyButton = new Button
        {
            Content = "Reply", FontSize = 10, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
        };
        replyButton.Click += (_, _) => BeginReply(c);
        metaRow.Children.Add(replyButton);

        // Editable is set by Pixiv itself on comments the roots/replies endpoints return that
        // belong to you — plus we always set it on comments we just posted locally this session.
        if (c.Editable)
        {
            var deleteButton = new Button
            {
                Content = "Delete", FontSize = 10, Padding = new Thickness(0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.Parse("#F87171")),
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            };
            var confirmButton = new Button
            {
                Content = "Confirm?", FontSize = 10, Padding = new Thickness(0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.Parse("#F87171")), FontWeight = FontWeight.SemiBold,
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
                IsVisible = false,
            };
            var cancelButton = new Button
            {
                Content = "Cancel", FontSize = 10, Padding = new Thickness(0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
                IsVisible = false,
            };
            deleteButton.Click += (_, _) =>
            {
                deleteButton.IsVisible = false;
                confirmButton.IsVisible = true;
                cancelButton.IsVisible = true;
            };
            cancelButton.Click += (_, _) =>
            {
                deleteButton.IsVisible = true;
                confirmButton.IsVisible = false;
                cancelButton.IsVisible = false;
            };
            confirmButton.Click += async (_, _) => await DeleteCommentAsync(c);
            metaRow.Children.Add(deleteButton);
            metaRow.Children.Add(confirmButton);
            metaRow.Children.Add(cancelButton);
        }
        body.Children.Add(metaRow);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(avatar, 0);
        Grid.SetColumn(body, 1);
        body.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(avatar);
        row.Children.Add(body);
        return row;
    }

    /// <summary>
    /// Comment text from Pixiv contains literal shortcodes like "(shock3)" for any emoji the
    /// poster inserted — Pixiv's own renderer swaps these for inline images, so plain text
    /// display would otherwise show that raw parenthesized text instead of the emoji.
    /// </summary>
    private Control BuildCommentTextWithEmoji(string text)
    {
        var panel = new WrapPanel();
        var lastEnd = 0;
        foreach (System.Text.RegularExpressions.Match m in EmojiShortcodeRegex.Matches(text))
        {
            if (m.Index > lastEnd)
                panel.Children.Add(new TextBlock { Text = text[lastEnd..m.Index], FontSize = 12, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center });

            var id = EmojiCatalog.FirstOrDefault(e => e.Shortcode == m.Groups[1].Value).Id;
            var emojiImg = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform, Margin = new Thickness(1, 0) };
            _ = LoadAvatarIntoAsync(emojiImg, $"https://source.pixiv.net/common/images/emoji/{id}.png");
            panel.Children.Add(emojiImg);
            lastEnd = m.Index + m.Length;
        }
        if (lastEnd < text.Length)
            panel.Children.Add(new TextBlock { Text = text[lastEnd..], FontSize = 12, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center });
        return panel;
    }

    // EmojiCatalog is declared later in this file; the null-forgiving operator below is safe
    // because this Lazy factory only runs on first .Value access (well after all static field
    // initializers have completed), but the compiler's forward-reference analysis can't see that.
    private static readonly Lazy<System.Text.RegularExpressions.Regex> EmojiShortcodeRegexLazy = new(() =>
        new System.Text.RegularExpressions.Regex(@"\((" + string.Join("|", EmojiCatalog!.Select(e => System.Text.RegularExpressions.Regex.Escape(e.Shortcode))) + @")\)"));
    private static System.Text.RegularExpressions.Regex EmojiShortcodeRegex => EmojiShortcodeRegexLazy.Value;

    private void BeginReply(Pikura.Core.Models.PixivComment c)
    {
        // A reply's parent should be the THREAD ROOT, not the specific reply being answered —
        // Pixiv's comment model is only one level deep, so replying to a reply still posts under
        // the same root comment as a sibling reply.
        var rootId = c.CommentRootId ?? c.Id;
        _replyingTo = (rootId, c.UserName);
        ReplyingToLabel.Text = $"Replying to {c.UserName}";
        ReplyingToRow.IsVisible = true;
        NewCommentBox.Focus();
    }

    private async Task DeleteCommentAsync(Pikura.Core.Models.PixivComment c)
    {
        if (_currentCard?.Id == null) return;
        var ok = await _pixivClient.DeleteCommentAsync(_currentCard.Id, c.Id);
        if (!ok)
        {
            if (VM != null) VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotDeleteComment"];
            return;
        }
        _loadedComments.Remove(c);
        RerenderCommentsList();
        if (VM != null) VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CommentDeleted"];
    }

    private void RerenderCommentsList()
    {
        CommentsListPanel.Children.Clear();
        foreach (var c in _loadedComments)
            CommentsListPanel.Children.Add(BuildCommentThread(c));
        CommentsEmptyPanel.IsVisible = _loadedComments.Count == 0;
        if (_loadedComments.Count == 0) CommentsEmptyLabel.Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_NoCommentsYetBeTheFirst"];
    }

    /// <summary>A top-level comment's row plus, if it has replies, a "View N replies" toggle
    /// that lazily fetches and shows them indented underneath — GetCommentsAsync only returns
    /// root comments, so without this a whole thread just silently vanishes.</summary>
    private Control BuildCommentThread(Pikura.Core.Models.PixivComment c)
    {
        var container = new StackPanel { Spacing = 6 };
        container.Children.Add(BuildCommentRow(c));

        // Always create the replies host and register it — even for comments with no replies
        // yet — so a reply posted to THIS comment later lands nested under it instead of
        // falling back to the top of the whole list for lack of anywhere else to put it.
        var repliesHost = new StackPanel { Spacing = 6, Margin = new Thickness(38, 0, 0, 0), IsVisible = false };
        _repliesHosts[c.Id] = repliesHost;

        if (c.HasReplies)
        {
            var toggle = new Button
            {
                Content = "▾ View replies", FontSize = 10, Padding = new Thickness(0),
                Margin = new Thickness(38, 0, 0, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.Parse("#60A5FA")),
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            };
            var loaded = false;
            toggle.Click += async (_, _) =>
            {
                if (loaded) { repliesHost.IsVisible = !repliesHost.IsVisible; return; }
                loaded = true;
                repliesHost.IsVisible = true;
                toggle.Content = Pikura.Avalonia.Localization.LocalizationService.Instance["Common_Loading_2"];
                try
                {
                    var replies = await _pixivClient.GetCommentRepliesAsync(c.Id);
                    foreach (var r in replies?.Comments ?? [])
                        repliesHost.Children.Add(BuildCommentRow(r));
                    toggle.Content = replies?.Comments.Count > 0 ? Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_HideReplies"] : Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_NoRepliesFound"];
                }
                catch { toggle.Content = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotLoadReplies"]; }
            };
            container.Children.Add(toggle);
        }
        container.Children.Add(repliesHost);

        return container;
    }

    private void OnCancelReplyClicked(object? sender, RoutedEventArgs e)
    {
        _replyingTo = null;
        ReplyingToRow.IsVisible = false;
    }

    private async Task LoadAvatarIntoAsync(Image img, string url)
    {
        try
        {
            var skBitmap = await _imageLoader.FetchBitmapAsync(url, ThumbnailSize.Small, CancellationToken.None);
            if (skBitmap == null) return;
            var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap));
            skBitmap.Dispose();
            if (bmp != null) img.Source = bmp;
        }
        catch { /* avatar is decorative — non-fatal */ }
    }

    private void OnCommentsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_commentsLoading || !_commentsHasNext) return;
        if (sender is not ScrollViewer sv) return;
        if (sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height < 100)
            _ = LoadCommentsAsync(reset: false);
    }

    private void OnNewCommentKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = PostCommentAsync(); }
    }

    private async void OnPostCommentClicked(object? sender, RoutedEventArgs e) => await PostCommentAsync();

    private bool _stickerGridBuilt;
    private bool _emojiGridBuilt;

    // Pixiv's actual 38-emoji "Custom" category from its emoji-mart picker — confirmed by
    // inspecting the picker's own rendered DOM (aria-label = the shortcode text that gets typed
    // into the comment box, e.g. "(shock3)"; background-image = the matching PNG at
    // source.pixiv.net/common/images/emoji/{id}.png). Unlike the earlier Unicode-character
    // approach, these EXACT shortcodes are what Pixiv's own comment renderer recognizes.
    public static readonly (string Id, string Shortcode)[] EmojiCatalog =
    [
        ("101", "normal"), ("102", "surprise"), ("103", "serious"), ("104", "heaven"),
        ("105", "happy"), ("106", "excited"), ("107", "sing"), ("108", "cry"),
        ("201", "normal2"), ("202", "shame2"), ("203", "love2"), ("204", "interesting2"),
        ("205", "blush2"), ("206", "fire2"), ("207", "angry2"), ("208", "shine2"), ("209", "panic2"),
        ("301", "normal3"), ("302", "satisfaction3"), ("303", "surprise3"), ("304", "smile3"),
        ("305", "shock3"), ("306", "gaze3"), ("307", "wink3"), ("308", "happy3"),
        ("309", "excited3"), ("310", "love3"),
        ("401", "normal4"), ("402", "surprise4"), ("403", "serious4"), ("404", "love4"),
        ("405", "shine4"), ("406", "sweat4"), ("407", "shame4"), ("408", "sleep4"),
        ("501", "heart"), ("502", "teardrop"), ("503", "star"),
    ];

    private void BuildEmojiGrid()
    {
        _emojiGridBuilt = true;
        foreach (var (id, shortcode) in EmojiCatalog)
        {
            var shortcodeText = $"({shortcode})";
            var tile = new Button
            {
                Width = 36, Height = 36, Padding = new Thickness(4), Margin = new Thickness(1),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            };
            ToolTip.SetTip(tile, shortcodeText);
            var img = new Image { Stretch = Stretch.Uniform };
            tile.Content = img;
            _ = LoadAvatarIntoAsync(img, $"https://source.pixiv.net/common/images/emoji/{id}.png");
            tile.Click += (_, _) =>
            {
                // Insert at the caret rather than always appending, so you can drop one mid-sentence
                // the same way Pixiv's own picker does.
                var box = NewCommentBox;
                var caret = box.CaretIndex;
                var text = box.Text ?? string.Empty;
                box.Text = text[..caret] + shortcodeText + text[caret..];
                box.CaretIndex = caret + shortcodeText.Length;
                box.Focus();
            };
            EmojiGrid.Children.Add(tile);
        }
    }

    private void OnEmojiTabClicked(object? sender, RoutedEventArgs e)
    {
        EmojiTabBtn.IsChecked = true;
        StickersTabBtn.IsChecked = false;
        EmojiScrollViewer.IsVisible = true;
        StickerScrollViewer.IsVisible = false;
        StickerLoadingPanel.IsVisible = false;
        if (!_emojiGridBuilt) BuildEmojiGrid();
    }

    private void OnStickersTabClicked(object? sender, RoutedEventArgs e)
    {
        EmojiTabBtn.IsChecked = false;
        StickersTabBtn.IsChecked = true;
        EmojiScrollViewer.IsVisible = false;
        StickerScrollViewer.IsVisible = true;
        if (!_stickerGridBuilt) BuildStickerGrid();
    }

    /// <summary>
    /// Pixiv's stamp catalog has no public listing endpoint we can call without the OAuth
    /// App API (which this app deliberately avoids) — only the image URL PATTERN is confirmed
    /// (https://source.pixiv.net/common/images/stamp/generated-stamps/{id}_s.jpg?20180605,
    /// sourced from inspecting a real rendered comment's CSS background-image). This tries a
    /// broad range of candidate IDs and silently skips any that fail to load, rather than
    /// guessing at a curated "known good" list.
    /// </summary>
    private void OnStickerPickerClicked(object? sender, RoutedEventArgs e)
    {
        StickerPickerPopup.IsOpen = !StickerPickerPopup.IsOpen;
        if (!StickerPickerPopup.IsOpen) return;
        if (!_emojiGridBuilt && EmojiTabBtn.IsChecked == true) BuildEmojiGrid();
    }

    private void BuildStickerGrid()
    {
        _stickerGridBuilt = true;
        StickerLoadingPanel.IsVisible = true;

        // Pixiv's own "we plan to add more types" history means the catalog spans well past
        // the original ~38-stamp set (confirmed IDs seen so far include both 101-119ish and
        // 300s) — scanning further to surface newer categories too.
        const int maxId = 600;
        var remaining = maxId;
        var foundAny = false;

        void OnOneFinished(bool found)
        {
            if (found) foundAny = true;
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                StickerLoadingPanel.IsVisible = false;
                if (!foundAny)
                {
                    StickerLoadingPanel.IsVisible = true;
                    ((TextBlock)StickerLoadingPanel.Children[1]).Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_NoStickersCouldBeLoaded"];
                    StickerLoadingPanel.Children[0].IsVisible = false; // hide spinner, keep message
                }
            }
        }

        for (int id = 1; id <= maxId; id++)
        {
            var stampId = id.ToString();
            var tile = new Button
            {
                Width = 40, Height = 40, Padding = new Thickness(2), Margin = new Thickness(2),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
                IsVisible = false, // only shown once its image actually loads
            };
            var img = new Image { Stretch = Stretch.Uniform };
            tile.Content = img;
            tile.Click += async (_, _) =>
            {
                StickerPickerPopup.IsOpen = false;
                await PostCommentOrStampAsync("", stampId);
            };
            StickerGrid.Children.Add(tile);
            _ = LoadStickerTileAsync(img, tile, stampId, OnOneFinished);
        }
    }

    private async Task LoadStickerTileAsync(Image img, Button tile, string stampId, Action<bool> onFinished)
    {
        try
        {
            var skBitmap = await _imageLoader.FetchBitmapAsync(
                $"https://source.pixiv.net/common/images/stamp/generated-stamps/{stampId}_s.jpg?20180605",
                ThumbnailSize.Small, CancellationToken.None);
            if (skBitmap == null) { onFinished(false); return; }
            var bmp = await Task.Run(() => (Bitmap?)BitmapInterop.SkiaToAvalonia(skBitmap));
            skBitmap.Dispose();
            if (bmp == null) { onFinished(false); return; }
            img.Source = bmp;
            tile.IsVisible = true;
            onFinished(true);
        }
        catch { onFinished(false); /* candidate stamp ID doesn't exist — expected for many, just skip it */ }
    }

    private async Task PostCommentAsync() => await PostCommentOrStampAsync(NewCommentBox.Text?.Trim() ?? "", null);

    private async Task PostCommentOrStampAsync(string text, string? stampId)
    {
        if (_currentCard?.Id == null) return;
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(stampId)) return;

        PostCommentBtn.IsEnabled = false;
        NewCommentBox.IsEnabled = false;
        var replyingTo = _replyingTo;
        try
        {
            var result = await _pixivClient.PostCommentAsync(
                _currentCard.Id, _currentCard.UserId, text, stampId, replyingTo?.Id);
            if (result != null)
            {
                NewCommentBox.Text = string.Empty;
                _replyingTo = null;
                ReplyingToRow.IsVisible = false;
                // Locally-construct the new comment/reply immediately rather than re-fetching —
                // scroll position would otherwise reset on a full reload.
                var mine = new Pikura.Core.Models.PixivComment
                {
                    Id = result.CommentId ?? Guid.NewGuid().ToString(),
                    Comment = result.Comment ?? text,
                    UserName = result.UserName ?? "You",
                    StampId = result.StampId ?? stampId,
                    CommentDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    CommentRootId = replyingTo?.Id,
                    CommentParentId = replyingTo?.Id,
                    Editable = true,
                };

                if (replyingTo != null && _repliesHosts.TryGetValue(replyingTo.Value.Id, out var host))
                {
                    // Nest under the comment being replied to instead of the top-level list.
                    host.IsVisible = true;
                    host.Children.Add(BuildCommentRow(mine));
                }
                else
                {
                    _loadedComments.Insert(0, mine);
                    CommentsListPanel.Children.Insert(0, BuildCommentThread(mine));
                }
                CommentsEmptyPanel.IsVisible = false;
                if (VM != null) VM.StatusMessage = replyingTo != null ? $"Reply posted to {replyingTo.Value.UserName}" : Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CommentPosted"];
            }
            else if (VM != null)
            {
                VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotPostComment"];
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InlineArtworkViewer] PostComment failed: {ex.Message}");
            if (VM != null) VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotPostComment_2"];
        }
        finally
        {
            PostCommentBtn.IsEnabled = true;
            NewCommentBox.IsEnabled = true;
        }
    }

    private bool _isBookmarkToggling;

    private async void OnBookmarkMainClicked(object? sender, RoutedEventArgs e)
        => await OnBookmarkToggleAsync(_isPrivateBookmark);

    private async void OnBookmarkPublicClicked(object? sender, RoutedEventArgs e)
        => await OnBookmarkToggleAsync(false);

    private async void OnBookmarkPrivateClicked(object? sender, RoutedEventArgs e)
        => await OnBookmarkToggleAsync(true);

    private async Task OnBookmarkToggleAsync(bool isPrivate)
    {
        if (_currentCard == null || VM == null || _isBookmarkToggling) return;

        _isBookmarkToggling = true;
        BookmarkSplitBtn!.IsEnabled = false;
        try
        {
            if (_isBookmarked)
            {
                if (_isPrivateBookmark == isPrivate)
                {
                    // Remove existing bookmark
                    if (_bookmarkId != null)
                    {
                        var ok = await _pixivClient.RemoveWebBookmarkAsync(new[] { _bookmarkId });
                        if (ok)
                        {
                            _isBookmarked = false;
                            _isPrivateBookmark = false;
                            _bookmarkId = null;
                            _bookmarkCount = Math.Max(0, _bookmarkCount - 1);
                            _currentCard.IsPixivBookmarked = false;
                            _currentCard.IsPixivPrivateBookmark = false;
                            _currentCard.PixivBookmarkId = null;
                            try { AppServices.Get<BookmarksViewModel>().SyncBookmarked(_currentCard, false, false, null); }
                            catch { /* Bookmarks view not initialized yet */ }
                            SyncCardFlagsEverywhere(isPixivBookmarked: false, isPixivPrivateBookmark: false, pixivBookmarkId: null, bookmarkIdProvided: true);
                            VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_RemovedPixivBookmark"];
                        }
                        else
                        {
                            VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotRemoveBookmark"];
                        }
                    }
                }
                else
                {
                    // Switch privacy: remove current, add with new privacy
                    if (_bookmarkId != null)
                    {
                        var removed = await _pixivClient.RemoveWebBookmarkAsync(new[] { _bookmarkId });
                        if (removed)
                        {
                            var newId = await _pixivClient.AddWebBookmarkAsync(_currentCard.Id, isPrivate: isPrivate);
                            if (newId != null)
                            {
                                _isPrivateBookmark = isPrivate;
                                _bookmarkId = newId;
                                _currentCard.IsPixivPrivateBookmark = isPrivate;
                                _currentCard.PixivBookmarkId = newId;
                                try { AppServices.Get<BookmarksViewModel>().SyncBookmarked(_currentCard, true, isPrivate, newId); }
                                catch { /* Bookmarks view not initialized yet */ }
                                SyncCardFlagsEverywhere(isPixivBookmarked: true, isPixivPrivateBookmark: isPrivate, pixivBookmarkId: newId, bookmarkIdProvided: true);
                                VM.StatusMessage = isPrivate ? Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_SwitchedToPrivateBookmark"] : Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_SwitchedToPublicBookmark"];
                            }
                            else
                            {
                                // If re-adding failed, we lost the bookmark. Refresh state.
                                _ = LoadArtworkStateAsync(_currentCard);
                                VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotReAddBookmark"];
                            }
                        }
                        else
                        {
                            VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotUpdateBookmarkPrivacy"];
                        }
                    }
                }
            }
            else
            {
                var newId = await _pixivClient.AddWebBookmarkAsync(_currentCard.Id, isPrivate: isPrivate);
                if (newId != null)
                {
                    _isBookmarked = true;
                    _isPrivateBookmark = isPrivate;
                    _bookmarkId = newId;
                    _bookmarkCount++;
                    _currentCard.IsPixivBookmarked = true;
                    _currentCard.IsPixivPrivateBookmark = isPrivate;
                    _currentCard.PixivBookmarkId = newId;
                    try { AppServices.Get<BookmarksViewModel>().SyncBookmarked(_currentCard, true, isPrivate, newId); }
                    catch { /* Bookmarks view not initialized yet */ }
                    SyncCardFlagsEverywhere(isPixivBookmarked: true, isPixivPrivateBookmark: isPrivate, pixivBookmarkId: newId, bookmarkIdProvided: true);
                    VM.StatusMessage = isPrivate ? Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_AddedPrivateBookmark"] : Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_BookmarkedOnPixiv"];
                }
                else
                {
                    VM.StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotBookmark"];
                }
            }
            UpdateBookmarkButtons();
            UpdateStatsLabel();
        }
        finally
        {
            _isBookmarkToggling = false;
            BookmarkSplitBtn.IsEnabled = true;
        }
    }

    private void OnOpenArtistInPixiv(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        var url = $"https://www.pixiv.net/users/{_currentCard.UserId}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OnCopyId(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb == null) return;
        var dt = new global::Avalonia.Input.DataTransfer();
        dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(_currentCard.Id));
        _ = cb.SetDataAsync(dt);
    }

    /// <summary>
    /// Copies the current artwork's *artist* ID to both the OS clipboard and
    /// the in-app <see cref="QuickClipboardService"/> queue. This is the only
    /// place a user can grab an unfollowed artist's ID without first having
    /// to follow them — same shortcut backs the artist-name single-click in
    /// the info row above the image.
    /// </summary>
    private void OnCopyArtistId(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        CopyArtistIdToClipboard(_currentCard.UserId, _currentCard.UserName);
    }

    /// <summary>
    /// Click handler for the artist name TextBlock in the info row. Same
    /// behaviour as the context-menu item but reachable with a single click —
    /// the most common operation a user wants on an artist they don't follow.
    /// </summary>
    private void OnArtistNamePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_currentCard == null) return;
        var props = e.GetCurrentPoint(null).Properties;
        if (!props.IsLeftButtonPressed) return; // right-click should fall through to anything else
        e.Handled = true;
        CopyArtistIdToClipboard(_currentCard.UserId, _currentCard.UserName);
    }

    private void CopyArtistIdToClipboard(string userId, string? userName)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb != null)
        {
            var dt = new global::Avalonia.Input.DataTransfer();
            dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(userId));
            _ = cb.SetDataAsync(dt);
        }
        // Mirror to the in-app queue so a later "paste artists" picks it up.
        try { QuickClipboardService.CopyArtist(userId); } catch { /* non-fatal */ }
        // Visible status bar feedback — same pattern as the gallery card handler,
        // and always reachable even when the AI panel is collapsed.
        if (VM != null)
            VM.StatusMessage = string.Format(LocalizationService.Instance["Common_Status_CopiedArtistIdShort"], userId) + (string.IsNullOrEmpty(userName) ? "" : $" ({userName})");
    }

    private void OnCopyImage(object? sender, RoutedEventArgs e)
    {
        if (ViewerImage.Source is not global::Avalonia.Media.Imaging.Bitmap bmp) return;
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb == null) return;
        _ = cb.SetBitmapAsync(bmp);
    }

    private async void OnUseAsBackground(object? sender, RoutedEventArgs e)
    {
        if (_currentCard is not { } card) return;

        var preferred = _currentOriginalUrl
            ?? (_pages.Count > 0 && _currentPageIndex >= 0 && _currentPageIndex < _pages.Count
                ? _pages[_currentPageIndex].Urls.Original
                : null);

        try
        {
            await AppServices.Get<BackgroundOverlayService>()
                .UseAsBackgroundFromCardAsync(card, preferred, TopLevel.GetTopLevel(this) as Window);
        }
        catch { /* non-fatal */ }
    }

    private void OnTagPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.DataContext is not string tag) return;
        var props = e.GetCurrentPoint(null).Properties;

        // Right-click → show programmatic context menu with tag captured in closure
        if (props.IsRightButtonPressed)
        {
            e.Handled = true;
            ShowTagContextMenu(border, tag);
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        var vm = VM ?? AppServices.Get<GalleryViewModel>();
        if (vm == null) return;

        e.Handled = true;
        // The inline viewer may be hosted inside Discover/Rankings — ensure we
        // switch to the Gallery tab so the search results are actually visible.
        if (TopLevel.GetTopLevel(this) is Pikura.Avalonia.Views.MainWindow main)
            main.LoadGalleryView();

        // Collapse the fullscreen viewer so the filtered/searched grid underneath
        // is actually visible — otherwise the filter is applied but hidden behind
        // the viewer overlay and the click appears to do nothing.
        vm.IsViewerExpanded = false;

        // Shift+click = global Pixiv search; regular click = filter within current artist
        bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;

        if (isShiftPressed)
        {
            // Global Pixiv search
            if (vm.SearchByTagCommand.CanExecute(tag))
                _ = vm.SearchByTagCommand.ExecuteAsync(tag);
        }
        else
        {
            // Filter within current artist's gallery
            vm.TagIncludeFilter = tag;
            vm.ShowFilters = true;
        }
    }

    /// <summary>
    /// Build a context menu programmatically with the tag captured in closures.
    /// This avoids fragile DataContext / PlacementTarget plumbing that doesn't
    /// reliably propagate through DataTemplate-instantiated controls.
    /// </summary>
    private void ShowTagContextMenu(Control target, string tag)
    {
        var menu = new ContextMenu();
        var hide = IsViewingCurrentArtist();

        if (!hide)
        {
            var openArtist = new MenuItem { Header = "\U0001F464 Open artist gallery" };
            openArtist.Click += (_, _) =>
            {
                if (_currentCard != null) NavigateToArtistGallery(_currentCard.UserId);
            };
            menu.Items.Add(openArtist);
            menu.Items.Add(new Separator());
        }

        var searchGallery = new MenuItem { Header = "\U0001F50D Search tag in Gallery" };
        searchGallery.Click += (_, _) =>
        {
            var vm = VM ?? AppServices.Get<GalleryViewModel>();
            if (vm == null) return;
            if (TopLevel.GetTopLevel(this) is Pikura.Avalonia.Views.MainWindow main)
                main.LoadGalleryView();
            vm.IsViewerExpanded = false;
            vm.TagIncludeFilter = tag;
            vm.ShowFilters = true;
        };
        menu.Items.Add(searchGallery);

        var searchGlobal = new MenuItem { Header = "\U0001F310 Global tag search" };
        searchGlobal.Click += (_, _) =>
        {
            var vm = VM ?? AppServices.Get<GalleryViewModel>();
            if (vm == null) return;
            if (TopLevel.GetTopLevel(this) is Pikura.Avalonia.Views.MainWindow main)
                main.LoadGalleryView();
            vm.IsViewerExpanded = false;
            if (vm.SearchByTagCommand.CanExecute(tag))
                _ = vm.SearchByTagCommand.ExecuteAsync(tag);
        };
        menu.Items.Add(searchGlobal);

        menu.Items.Add(new Separator());

        var openOnPixiv = new MenuItem { Header = "\U0001F517 Open tag on pixiv.net" };
        openOnPixiv.Click += (_, _) =>
        {
            var url = $"https://www.pixiv.net/tags/{Uri.EscapeDataString(tag)}/artworks";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };
        menu.Items.Add(openOnPixiv);

        menu.Open(target);
    }

    private void OnTagOpenArtistGallery(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        NavigateToArtistGallery(_currentCard.UserId);
    }

    /// <summary>
    /// True when the gallery already has this card's artist selected — used to
    /// hide the "open artist gallery" affordances since they would be a no-op.
    /// </summary>
    private bool IsViewingCurrentArtist()
    {
        if (_currentCard == null) return false;
        var galleryVm = DataContext as GalleryViewModel ?? AppServices.Get<GalleryViewModel>();
        return galleryVm.SelectedArtist?.UserId == _currentCard.UserId;
    }

    private void OnImageContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var hide = IsViewingCurrentArtist();
        if (GoToArtistMenuItem != null) GoToArtistMenuItem.IsVisible = !hide;
        // Toolbar button mirrors the menu item — keep them in sync
        if (GoToArtistBtn != null) GoToArtistBtn.IsVisible = !hide;

    }

    private void OnTagContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        
        // Get the tag from the element that opened the context menu and store it
        _contextMenuTag = menu.PlacementTarget?.DataContext as string;
        
        var hide = IsViewingCurrentArtist();
        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi) continue;
            
            // Show/hide "Open artist gallery" based on context
            if ((mi.Tag as string) == "OpenArtistGallery")
                mi.IsVisible = !hide;
        }
    }

    private void OnTagSearchGallery(object? sender, RoutedEventArgs e)
    {
        if (_contextMenuTag is not { } tag) return;
        
        var vm = VM ?? AppServices.Get<GalleryViewModel>();
        if (vm == null) return;

        vm.TagIncludeFilter = tag;
        vm.ShowFilters = true;
    }

    private void OnTagSearchPixiv(object? sender, RoutedEventArgs e)
    {
        if (_contextMenuTag is not { } tag) return;

        var url = $"https://www.pixiv.net/tags/{Uri.EscapeDataString(tag)}/artworks";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ── Local favorite ──────────────────────────────────────────────────────

    private void UpdateFavoriteButton(ArtworkCardViewModel? card)
    {
        if (LocalFavBtn == null || LocalFavLabel == null) return;
        var isFav = card != null && _favorites.IsFavorite(card.Id);
        LocalFavLabel.Text = isFav ? Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Favorited"] : Pikura.Avalonia.Localization.LocalizationService.Instance["Viewer_Favorite"];
        if (card != null)
        {
            card.IsLocalFavorite = isFav;
            try { AppServices.Get<BookmarksViewModel>().SyncFavoriteEverywhere(card.Id, isFav); }
            catch { /* Bookmarks view not initialized yet */ }
            SyncCardFlagsEverywhere(isLocalFavorite: isFav, targetCard: card);
        }
    }

    /// <summary>
    /// Pushes updated flags onto every currently-loaded card with a matching artwork ID across
    /// Gallery and Discover (the two other browsing surfaces besides Bookmarks), so the
    /// heart/bookmark/star badges update live wherever that artwork is already on screen.
    /// </summary>
    private void SyncCardFlagsEverywhere(
        bool? isLiked = null,
        bool? isPixivBookmarked = null,
        bool? isPixivPrivateBookmark = null,
        string? pixivBookmarkId = null,
        bool bookmarkIdProvided = false,
        bool? isLocalFavorite = null,
        ArtworkCardViewModel? targetCard = null)
    {
        var id = (targetCard ?? _currentCard)?.Id;
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            AppServices.Get<GalleryViewModel>().SyncArtworkFlags(
                id, isLiked, isPixivBookmarked, isPixivPrivateBookmark, pixivBookmarkId, bookmarkIdProvided, isLocalFavorite);
        }
        catch { /* Gallery view not initialized yet */ }
        try
        {
            AppServices.Get<DiscoverViewModel>().SyncArtworkFlags(
                id, isLiked, isPixivBookmarked, isPixivPrivateBookmark, pixivBookmarkId, bookmarkIdProvided, isLocalFavorite);
        }
        catch { /* Discover view not initialized yet */ }
        try
        {
            AppServices.Get<EnhancedRankingsViewModel>().SyncArtworkFlags(
                id, isLiked, isPixivBookmarked, isPixivPrivateBookmark, pixivBookmarkId, bookmarkIdProvided, isLocalFavorite);
        }
        catch { /* Rankings view not initialized yet */ }
    }

    private void OnToggleLocalFavorite(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        _favorites.Toggle(_currentCard.Artwork);
        UpdateFavoriteButton(_currentCard);
    }

    // ── Collage mode — up to 5 selected images, tiled ────────────────────────────────────────
    // IsCollageMode/CollageItems live on the shared GalleryViewModel (this control's DataContext
    // in every tab it's used in) and are bound directly in XAML (CollagePanel.IsVisible /
    // CollageGrid.ItemsSource) — so the side-panel and full-screen viewer instances automatically
    // stay in sync with no manual cross-instance code needed here. There is no manual toggle for
    // this — it's only entered via a "View as Collage" button in a tab's multi-select toolbar.

    /// <summary>Explicit "Exit Collage" toolbar button — closes the collage tab.</summary>
    private void OnExitCollageClicked(object? sender, RoutedEventArgs e) => VM?.CloseCollage();

    /// <summary>Left-clicking a tile opens that image in-place inside the collage tab so the
    /// user can view it, use prev/next to move through the collage, and press "Return to Collage"
    /// to go back to the grid. Right-click is left alone so a context menu can be used without
    /// immediately losing the collage.</summary>
    private void OnCollageTileReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Handled || e.InitialPressMouseButton != MouseButton.Left) return;
        if ((sender as Control)?.DataContext is not ArtworkCardViewModel card) return;
        e.Handled = true;
        var list = VM?.SelectedViewerTab?.CollageItems ?? VM?.CollageItems;
        VM?.OpenInViewer(card, list, source: "Collage");
    }

    private void OnRemoveCollageItemClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not ArtworkCardViewModel card) return;
        VM?.RemoveFromCollage(card);
    }

    private void OnRemoveCollageItemTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
    }

    private bool _collagePanning;
    private Point _collagePanStartPoint;
    private Vector _collagePanStartOffset;

    private void OnCollageWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (VM is null) return;

        var delta = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        var newWidth = VM.CollageGridColumnWidth * delta;
        VM.CollageGridColumnWidth = Math.Clamp(newWidth, 120, 600);
        e.Handled = true;
    }

    private void OnCollagePanPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || e.Pointer.IsPrimary == false || e.GetCurrentPoint(this).Properties.IsRightButtonPressed == false) return;
        if (CollageScroll is null) return;
        _collagePanning = true;
        _collagePanStartPoint = e.GetPosition(this);
        _collagePanStartOffset = CollageScroll.Offset;
        e.Pointer.Capture(CollageScroll);
        e.Handled = true;
    }

    private void OnCollagePanMoved(object? sender, PointerEventArgs e)
    {
        if (!_collagePanning || CollageScroll is null) return;
        var pos = e.GetPosition(this);
        var delta = pos - _collagePanStartPoint;
        CollageScroll.Offset = _collagePanStartOffset - new Vector(delta.X, delta.Y);
        e.Handled = true;
    }

    private void OnCollagePanReleased(object? sender, PointerReleasedEventArgs e)
    {
        _collagePanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnCollagePanCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _collagePanning = false;
    }

    // ── Hoshi (星) AI assistant ───────────────────────────────────────────

    private async void OnAiToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (AiPanel == null) return;

        if (!_aiVm.IsEnabled)
        {
            // First enable: start Ollama + pull model
            if (AiToggleBtnLabel != null) AiToggleBtnLabel.Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_Hoshi"];
            if (AiToggleBtn != null)      AiToggleBtn.IsEnabled = false;

            await _aiVm.ToggleEnabledAsync();

            if (AiToggleBtn != null) AiToggleBtn.IsEnabled = true;
            UpdateAiToggleButton();

            if (_aiVm.IsEnabled)
                AiPanel.IsVisible = true;
        }
        else
        {
            // Toggle panel open/close
            AiPanel.IsVisible = !AiPanel.IsVisible;
            if (AiPanel.IsVisible)
            {
                // Open the artwork's existing session, or show the start-chat empty state —
                // no session is auto-created on navigation anymore.
                _ = _aiVm.SessionsService.RefreshHeadersAsync();
                var sess = _aiVm.CurrentCard is { } cc ? _aiVm.SessionForArtwork(cc.Id) : null;
                if (sess != null) _aiVm.LoadSession(sess);
                if (AiStartChatBtn != null)
                    AiStartChatBtn.IsVisible = _aiVm.CurrentSession == null && _aiVm.CurrentCard != null;
                AiInputBox?.Focus();
            }
        }
        UpdateAiPanelRowSize();
        UpdateAiToggleButton();
        if (AiPanel.IsVisible) ScrollToBottomDeferred();
    }

    private void UpdateAiPanelRowSize()
    {
        // Row 5 is the AiPanel row — expand to * when visible, collapse to Auto when hidden
        if (RootGrid == null || AiPanel == null) return;
        RootGrid.RowDefinitions[5].Height = AiPanel.IsVisible
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;
    }

    private void UpdateAiToggleButton()
    {
        if (AiToggleBtnLabel == null) return;
        AiToggleBtnLabel.Text = _aiVm.IsEnabled
            ? (AiPanel?.IsVisible == true ? "Hoshi ▲" : "Hoshi ▼")
            : "Hoshi";
        if (AiStatusLabel != null)
            AiStatusLabel.Text = _aiVm.StatusText;
        UpdateAiStatusDot();
    }

    private static readonly IBrush AiDotReady = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush AiDotBusy  = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush AiDotOff   = new SolidColorBrush(Color.Parse("#6B7280"));

    /// <summary>Green = model ready, amber = starting up or answering, grey = Hoshi off.</summary>
    private void UpdateAiStatusDot()
    {
        if (AiStatusDot == null) return;
        AiStatusDot.Fill = !_aiVm.IsEnabled ? AiDotOff
            : _aiVm.IsModelReady && !_aiVm.IsThinking ? AiDotReady
            : AiDotBusy;
        ToolTip.SetTip(AiStatusDot, _aiVm.StatusText);
    }

    private void RefreshAiMessages()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Ensure binding is set if Loaded fired before _aiVm was ready
            if (AiMessagesList != null && AiMessagesList.ItemsSource == null)
                AiMessagesList.ItemsSource = _aiVm.Messages;

            // Show thinking indicator in send button
            if (AiSendBtn != null)
                AiSendBtn.Content = _aiVm.IsGenerating
                ? Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_Stop"]
                : Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_Send"];
        });
        ScrollToBottomDeferred();
    }

    private bool _autoScroll = true;

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ExtentProperty && _autoScroll)
            AiScrollViewer?.ScrollToEnd();
        // If user scrolls up, disable auto-scroll; re-enable when they reach the bottom
        if (e.Property == ScrollViewer.OffsetProperty && AiScrollViewer != null)
        {
            var offset = AiScrollViewer.Offset.Y;
            var atBottom = AiScrollViewer.Extent.Height - offset - AiScrollViewer.Viewport.Height < 40;
            _autoScroll = atBottom;
        }
    }

    /// <summary>Scrolls the AI chat view to the bottom and re-enables auto-scroll.</summary>
    private void ScrollToBottomDeferred()
    {
        _autoScroll = true;
        Dispatcher.UIThread.Post(() => AiScrollViewer?.ScrollToEnd(),
            global::Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnAiStopClicked(object? sender, RoutedEventArgs e) => _aiVm.Stop();

    private async void OnAiSendClicked(object? sender, RoutedEventArgs e)
    {
        if (_aiVm.IsGenerating) { _aiVm.Stop(); return; }
        if (AiInputBox == null || string.IsNullOrWhiteSpace(AiInputBox.Text)) return;
        _aiVm.InputText = AiInputBox.Text;
        AiInputBox.Text = string.Empty;
        await _aiVm.SendAsync();
    }

    private async void OnAiInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            await OnAiSendAsync();
        }
    }

    private async Task OnAiSendAsync()
    {
        if (AiInputBox == null || string.IsNullOrWhiteSpace(AiInputBox.Text)) return;
        _aiVm.InputText = AiInputBox.Text;
        AiInputBox.Text = string.Empty;
        await _aiVm.SendAsync();
    }

    private void OnAiFullViewClicked(object? sender, RoutedEventArgs e)
    {
        // AiViewModel is a singleton shared with the standalone Hoshi tab, so switching there
        // just re-hosts the exact same session/messages in the bigger view — nothing to copy.
        var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
        mainWindow?.LoadHoshiView();
    }

    /// <summary>
    /// Swaps between the plain "Describe" button and the "Describe" dropdown (with the
    /// "all pages" option) based on whether the open submission actually has more than one
    /// page. This control's DataContext is GalleryViewModel, not AiViewModel, so it can't bind
    /// to AiViewModel.IsCurrentSubmissionMultiPage directly — toggled here instead.
    /// </summary>
    private void UpdateDescribeButtonVisibility()
    {
        var isMultiPage = _aiVm.IsCurrentSubmissionMultiPage;
        if (AiDescribeBtn != null) AiDescribeBtn.IsVisible = !isMultiPage;
        if (AiDescribeSplitBtn != null) AiDescribeSplitBtn.IsVisible = isMultiPage;
    }

    /// <summary>
    /// Shows/hides the "Hoshi is thinking…" bubble so it's obvious a request (Describe, Tags,
    /// Similar Art, etc.) is actually in flight instead of the panel just looking stuck —
    /// this control's DataContext is GalleryViewModel, not AiViewModel, so it can't bind to
    /// AiViewModel.IsThinking directly.
    /// </summary>
    private void UpdateThinkingIndicator()
    {
        if (AiThinkingIndicator == null) return;
        AiThinkingIndicator.IsVisible = _aiVm.IsThinking;
        if (_aiVm.IsThinking)
            ScrollToBottomDeferred();
    }

    private async void OnAiDescribeClicked(object? sender, RoutedEventArgs e)
        => await _aiVm.DescribeImageAsync();

    private async void OnAiTagsClicked(object? sender, RoutedEventArgs e)
        => await _aiVm.SuggestTagsAsync();

    private async void OnAiAllPagesClicked(object? sender, RoutedEventArgs e)
        => await _aiVm.DescribeAllPagesCommand.ExecuteAsync(null);

    private async void OnAiSimilarArtClicked(object? sender, RoutedEventArgs e)
        => await _aiVm.FindSimilarArtworksCommand.ExecuteAsync(null);

    private async void OnAiSimilarArtistsClicked(object? sender, RoutedEventArgs e)
        => await _aiVm.FindSimilarArtistsCommand.ExecuteAsync(null);

    private async void OnAiFavClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentCard is not { } card) return;
        await _aiVm.EnsureSessionAsync(); // like every Hoshi action, this starts the chat
        _favorites.Toggle(card.Artwork);
        UpdateFavoriteButton(card);
        var msg = _favorites.IsFavorite(card.Id)
            ? $"Added \"{card.Title}\" to local favorites ★"
            : $"Removed \"{card.Title}\" from favorites.";
        _aiVm.Messages.Add(new AiChatMessage { Role = "assistant", Content = msg });
        await _aiVm.SaveCurrentSessionAsync();
        RefreshAiMessages();
    }

    private async void OnAiDlClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentCard is not { } card) return;
        await _aiVm.EnsureSessionAsync();
        await _aiVm.DownloadArtworkWithJobAsync(card);
        await _aiVm.SaveCurrentSessionAsync();
        RefreshAiMessages();
    }

    private void OnAiClearClicked(object? sender, RoutedEventArgs e)
        => _aiVm.ClearChat();

    private async void OnOpenArtworkFromChat(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AiChatMessage msg } || string.IsNullOrEmpty(msg.ArtworkId)) return;
        try
        {
            var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
            var galleryVm  = AppServices.Get<GalleryViewModel>();
            mainWindow?.LoadGalleryView();
            await galleryVm.OpenArtworkByIdInNewTabAsync(msg.ArtworkId);
        }
        catch (Exception ex)
        {
            _aiVm.Messages.Add(new AiChatMessage { Role = "system", Content = $"✗ Could not open artwork: {ex.Message}" });
            RefreshAiMessages();
        }
    }

    private async void OnOpenUrlFromChat(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AiChatMessage msg } || string.IsNullOrEmpty(msg.PixivUrl)) return;
        try
        {
            var url = new Uri(msg.PixivUrl);
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher != null)
            {
                await launcher.LaunchUriAsync(url);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(msg.PixivUrl) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _aiVm.Messages.Add(new AiChatMessage { Role = "system", Content = $"✗ Could not open URL: {ex.Message}" });
            RefreshAiMessages();
        }
    }

    private async void OnOpenArtistFromChat(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AiChatMessage msg } || string.IsNullOrEmpty(msg.ArtistId)) return;
        try
        {
            var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
            var galleryVm  = AppServices.Get<GalleryViewModel>();
            mainWindow?.LoadGalleryView();
            await galleryVm.LoadArtistByIdCommand.ExecuteAsync(msg.ArtistId);
        }
        catch (Exception ex)
        {
            _aiVm.Messages.Add(new AiChatMessage { Role = "system", Content = $"✗ Could not open artist gallery: {ex.Message}" });
            RefreshAiMessages();
        }
    }

    /// <summary>"Start chat about this artwork" — the ONLY place a session is created
    /// near navigation; creates/reuses the artwork's chat explicitly.</summary>
    private void OnAiStartChat(object? sender, RoutedEventArgs e)
    {
        if (_aiVm.CurrentCard is { } card)
        {
            _aiVm.StartArtworkChat(card);
            if (AiStartChatBtn != null) AiStartChatBtn.IsVisible = false;
            AiInputBox?.Focus();
        }
    }
}
