using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
using System.IO;
using System.Linq;
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty)
            ApplyExpandedState((bool)change.NewValue!);

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
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;

    // Full-res loading
    private const double FullResThreshold = 1.5;
    private bool _fullResLoaded;
    private string? _currentOriginalUrl;

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
        };

        _aiVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiViewModel.StatusText) && AiStatusLabel != null)
                Dispatcher.UIThread.Post(() => AiStatusLabel.Text = _aiVm.StatusText);
            if (e.PropertyName == nameof(AiViewModel.IsThinking))
            {
                RefreshAiMessages();
                Dispatcher.UIThread.Post(UpdateThinkingIndicator);
            }
            if (e.PropertyName == nameof(AiViewModel.IsCurrentSubmissionMultiPage))
                Dispatcher.UIThread.Post(UpdateDescribeButtonVisibility);
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
            _subscribedVm = null;
        }

        if (VM is not { } vm) return;
        vm.PropertyChanged += OnVmPropertyChanged;
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

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.InlineViewerCard))
        {
            _ = LoadCardAsync(VM?.InlineViewerCard);
            UpdateArtistButtonVisibility();
        }
        if (e.PropertyName == nameof(GalleryViewModel.SelectedViewerTab))
        {
            _ = LoadCardAsync(VM?.InlineViewerCard);
            UpdateTabHighlight();
            UpdateArtistButtonVisibility();
        }
        if (e.PropertyName == nameof(GalleryViewModel.SelectedArtist))
            UpdateArtistButtonVisibility();
        if (e.PropertyName == nameof(GalleryViewModel.NavListVersion))
            UpdateArtworkCounter();
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

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border b && b.DataContext is ViewerTab tab && VM != null)
            VM.SelectedViewerTab = tab;
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
        if (!IsEffectivelyVisible) return;

        if (card == null) { ClearViewer(); return; }

        // Skip only if the same card is already fully loaded successfully.
        // We must NOT dedupe on _currentCard alone — _currentCard is set
        // immediately when a load starts but a cancelled load leaves it set
        // without ever producing content, blocking legitimate retries.
        if (_currentCard?.Id == card.Id)
        {
            if (_loadedCardId == card.Id
                && (ViewerImage?.Source != null || UgoiraImage?.SourcePath != null))
                return;
            if (_loadCts is { IsCancellationRequested: false })
                return;
        }

        // Cancel any in-flight load and start fresh
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        // Immediately clear previous content so user doesn't see stale animation/image
        // while we fetch the new card's data over the network.
        if (UgoiraImage != null)
        {
            UgoiraImage.SourcePath = null;
            UgoiraImage.IsPlaying = false;
        }
        if (ViewerImage != null) ViewerImage.Source = null;

        _currentCard = card;
        _aiVm.CurrentImageBytes = null;
        // Capture the resolved session by value — if the user switches to another
        // artwork before the fetches below complete, late-arriving bytes must still
        // land on THIS session (not whatever session happens to be "current" later),
        // otherwise the image silently never gets persisted for this artwork.
        var session = _aiVm.SwitchToArtworkSession(card);

        // Seed Hoshi's vision bytes with the *thumbnail* immediately so the user
        // can hit "Describe"/"Tags" the moment the card opens — without this seed
        // the buttons fire before RenderPageAsync's Regular fetch completes and
        // the model receives a text-only prompt, replying "I don't have the
        // ability to see the image." The Regular fetch in RenderPageAsync will
        // upgrade these bytes when it lands.
        var thumbUrl = card.ThumbnailUrl;
        if (!string.IsNullOrEmpty(thumbUrl) && session != null)
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
        _currentPageIndex = 0;
        _pages = [];
        _fullResLoaded = false;
        UpdateFollowButton();
        UpdateFavoriteButton(card);
        _currentOriginalUrl = null;

        UpdatePageIndicator();
        UpdateArtworkCounter();
        SetLoading(true);
        ResetZoom();

        // Mark unloaded — only set _loadedCardId once content actually arrives.
        _loadedCardId = null;
        bool succeeded = false;

        try
        {
            if (card.IllustType == 2)
            {
                succeeded = await LoadUgoiraAsync(card.Id, ct);
            }
            else
            {
                var pages = await _pixivClient.GetArtworkPagesAsync(card.Id);
                if (ct.IsCancellationRequested) return;
                _pages = pages;
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
                SetError($"Failed to load image.\n{ex.Message}");
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

        _fullResLoaded = false;
        _currentOriginalUrl = _pages[index].Urls.Original;

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

        if (!string.IsNullOrEmpty(url))
        {
            var bytes = await _imageLoader.FetchBytesAsync(url, ct);
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
                    ViewerImage.Source = bmp;
                    ViewerImage.IsVisible = true;
                    if (ErrorPanel != null) ErrorPanel.IsVisible = false;
                    displayed = true;
                    ResetZoom();
                    // Clear loading state now that the bitmap is applied — avoids the
                    // blank-frame race where SetLoading(false) ran before Source was set.
                    SetLoading(false);
                });

                // Eagerly upgrade to full-res Original in the background so the viewer
                // always displays the highest quality image, not just when zoomed in.
                if (!string.IsNullOrEmpty(_currentOriginalUrl) && !_fullResLoaded)
                    _ = LoadFullResAsync(_currentOriginalUrl!);

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

    // ── Zoom/pan ────────────────────────────────────────────────────────────

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
        if (_scale >= FullResThreshold && !_fullResLoaded && !string.IsNullOrEmpty(_currentOriginalUrl))
            _ = LoadFullResAsync(_currentOriginalUrl!);
    }

    private async Task LoadFullResAsync(string originalUrl)
    {
        _fullResLoaded = true;
        var bytes = await _imageLoader.FetchBytesAsync(originalUrl);
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
            var old = ViewerImage.Source as Bitmap;
            ViewerImage.Source = bmp;
            old?.Dispose();
        });
    }

    private void ResetZoom()
    {
        _scale = 1.0; _translateX = 0; _translateY = 0;
        ApplyTransform();
    }

    private void OnImageWheel(object? sender, PointerWheelEventArgs e)
    {
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

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        _isPanning = true;
        _panStart = e.GetPosition(ImageCanvas);
        _panStartX = _translateX;
        _panStartY = _translateY;
        e.Handled = true;
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(ImageCanvas);
        _translateX = _panStartX + (pos.X - _panStart.X);
        _translateY = _panStartY + (pos.Y - _panStart.Y);
        ApplyTransform();
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isPanning = false;
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
        if (_currentCard == null || VM == null) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;
        var viewer = new FullscreenViewerWindow(_currentCard.Artwork, VM);
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
        if (VM != null) VM.StatusMessage = "Image URL copied — paste into a download manager (browser will 403; Pixiv CDN requires Referer header).";
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

    private async void NavigateToArtistGallery(string userId)
    {
        // Navigate to Gallery tab and load the artist — keep existing tabs open
        var mainWindow = TopLevel.GetTopLevel(this) as Pikura.Avalonia.Views.MainWindow;
        var galleryVm = AppServices.Get<GalleryViewModel>();
        // Close the inline viewer only if no tabs are pinned
        if (galleryVm.ViewerTabs.Count == 0)
            galleryVm.CloseInlineViewer();
        mainWindow?.LoadGalleryView();
        await galleryVm.LoadArtistByIdCommand.ExecuteAsync(userId);
    }

    private void OnFollowToggleClicked(object? sender, RoutedEventArgs e)
    {
        // Follow/unfollow feature removed - Pixiv OAuth no longer available
        if (VM != null)
        {
            VM.StatusMessage = "Follow/unfollow is not available. Pixiv has blocked OAuth authentication.";
        }
    }

    private void UpdateFollowButton()
    {
        // Follow button hidden - feature unavailable due to Pixiv OAuth restrictions
        if (FollowToggleBtn != null)
            FollowToggleBtn.IsVisible = false;
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
            VM.StatusMessage = $"Copied artist ID {userId}" + (string.IsNullOrEmpty(userName) ? "" : $" ({userName})");
    }

    private void OnCopyImage(object? sender, RoutedEventArgs e)
    {
        if (ViewerImage.Source is not global::Avalonia.Media.Imaging.Bitmap bmp) return;
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb == null) return;
        _ = cb.SetBitmapAsync(bmp);
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
        LocalFavLabel.Text = isFav ? "★ Favorited" : "☆ Favorite";
        if (card != null) card.IsLocalFavorite = isFav;
    }

    private void OnToggleLocalFavorite(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        _favorites.Toggle(_currentCard.Artwork);
        UpdateFavoriteButton(_currentCard);
    }

    // ── Hoshi (星) AI assistant ───────────────────────────────────────────

    private async void OnAiToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (AiPanel == null) return;

        if (!_aiVm.IsEnabled)
        {
            // First enable: start Ollama + pull model
            if (AiToggleBtnLabel != null) AiToggleBtnLabel.Text = "Hoshi…";
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
            if (AiPanel.IsVisible && AiInputBox != null)
                AiInputBox.Focus();
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
                AiSendBtn.Content = _aiVm.IsThinking ? "…" : "Send";
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

    private async void OnAiSendClicked(object? sender, RoutedEventArgs e)
    {
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

    private void OnAiFavClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        _favorites.Toggle(_currentCard.Artwork);
        UpdateFavoriteButton(_currentCard);
        var msg = _favorites.IsFavorite(_currentCard.Id)
            ? $"Added \"{_currentCard.Title}\" to local favorites ★"
            : $"Removed \"{_currentCard.Title}\" from favorites.";
        _aiVm.Messages.Add(new AiChatMessage { Role = "assistant", Content = msg });
        RefreshAiMessages();
    }

    private async void OnAiDlClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentCard == null) return;
        await _aiVm.DownloadArtworkWithJobAsync(_currentCard);
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
}
