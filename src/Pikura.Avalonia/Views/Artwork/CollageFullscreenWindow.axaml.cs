using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views.Artwork;

public partial class CollageFullscreenWindow : Window, INotifyPropertyChanged
{
    private double _zoom = 1.0;
    private const double ZoomStep = 1.15;
    private const double MinZoom = 0.25;
    private const double MaxZoom = 5.0;
    private IReadOnlyList<ArtworkCardViewModel> _items = [];
    private bool _isPanning;
    private Point _panStartPoint;
    private Vector _panStartOffset;

    public double Zoom
    {
        get => _zoom;
        private set
        {
            if (Math.Abs(_zoom - value) < 0.001) return;
            _zoom = Math.Clamp(value, MinZoom, MaxZoom);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZoomedColumnWidth));
            OnPropertyChanged(nameof(ZoomedItemSpacing));
        }
    }

    public double ZoomedColumnWidth => 360 * _zoom;
    public double ZoomedItemSpacing => 16 * _zoom;

    /// <summary>The collage's own tiles — the masonry's ItemsSource.</summary>
    public IReadOnlyList<ArtworkCardViewModel> Items => _items;

    /// <summary>
    /// One selectable entry in the strip's "Show" dropdown — a named set of artworks. Naming each
    /// tab and collage makes moving between them a visible choice rather than a hidden toggle.
    /// </summary>
    public sealed class StripSource
    {
        public required string Label { get; init; }
        public required IReadOnlyList<ArtworkCardViewModel> Items { get; init; }
        public override string ToString() => $"{Label}  ({Items.Count})";
    }

    private List<StripSource>? _sources;

    /// <summary>Dropdown contents: this collage, each other open tab/collage by name, then the
    /// combined sets. Rebuilt lazily so newly opened tabs show up.</summary>
    public List<StripSource> Sources => _sources ??= BuildSources();

    private StripSource? _selectedSource;

    public StripSource? SelectedSource
    {
        get => _selectedSource ??= Sources.FirstOrDefault();
        set
        {
            if (ReferenceEquals(_selectedSource, value)) return;
            _selectedSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StripItems));
            OnPropertyChanged(nameof(StripHint));
            if (ShowTileStrip) _ = LoadStripThumbnailsAsync();
        }
    }

    /// <summary>What the jump strip lists — the chosen source, defaulting to this collage.</summary>
    public IReadOnlyList<ArtworkCardViewModel> StripItems => SelectedSource?.Items ?? _items;

    public string StripHint =>
        $"{StripItems.Count} artwork(s) — click to jump, right-click to open in a new tab";

    /// <summary>Caps per-artist gallery groups — see the equivalent in FullscreenViewerWindow.</summary>
    private const int MaxArtistGroups = 12;

    /// <summary>
    /// Dropdown contents, in order: this collage, any other collage tab by name, the gallery
    /// grouped per artist (collages excluded), then everything including collages.
    /// </summary>
    private List<StripSource> BuildSources()
    {
        var sources = new List<StripSource>
        {
            new() { Label = "⊞ This collage", Items = _items },
        };

        try
        {
            var gallery = AppServices.Get<GalleryViewModel>();

            // Other collages, by name.
            foreach (var tab in gallery.ViewerTabs.Where(t => t.IsCollage))
            {
                var items = tab.CollageItems.ToList();
                if (items.Count == 0) continue;
                // Skip a collage tab that is just this same collage again.
                if (items.Count == _items.Count && items.All(_items.Contains)) continue;
                sources.Add(new StripSource { Label = "⊞ " + tab.Header, Items = items });
            }

            // Gallery per artist, collage-only artworks excluded.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pool = new List<ArtworkCardViewModel>();
            void Add(ArtworkCardViewModel? card)
            {
                if (card is null || string.IsNullOrEmpty(card.Id)) return;
                if (seen.Add(card.Id)) pool.Add(card);
            }
            foreach (var tab in gallery.ViewerTabs.Where(t => !t.IsCollage))
            {
                Add(tab.Card);
                foreach (var card in tab.NavList) Add(card);
            }
            // VisibleArtworks, not FilteredArtworks — the latter is paginated to one page (50).
            foreach (var card in gallery.VisibleArtworks) Add(card);

            static string ArtistKey(ArtworkCardViewModel c) =>
                !string.IsNullOrWhiteSpace(c.UserName) ? c.UserName
                : !string.IsNullOrWhiteSpace(c.UserId) ? c.UserId
                : "Unknown artist";

            sources.AddRange(pool
                .GroupBy(ArtistKey)
                .OrderByDescending(g => g.Count())
                .Take(MaxArtistGroups)
                .Select(g => new StripSource { Label = $"{g.Key} — all in gallery", Items = g.ToList() }));

            var everything = BuildOpenAcrossTabs(includeNavLists: true);
            if (everything.Count > _items.Count)
                sources.Add(new StripSource { Label = "Everything (incl. collages)", Items = everything });
        }
        catch { /* AppServices not ready — this collage alone is still a valid list */ }

        return sources;
    }

    /// <summary>
    /// Artworks across all tabs, de-duplicated by ID in tab order. Collage tabs contribute their
    /// tiles and ordinary tabs the artwork they're showing; with
    /// <paramref name="includeNavLists"/> each tab's full nav list and the current gallery are
    /// folded in too, so a single-artwork tab contributes the whole list it came from.
    /// </summary>
    private IReadOnlyList<ArtworkCardViewModel> BuildOpenAcrossTabs(bool includeNavLists)
    {
        try
        {
            var gallery = AppServices.Get<GalleryViewModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var all = new List<ArtworkCardViewModel>();

            void Add(ArtworkCardViewModel? card)
            {
                if (card is null || string.IsNullOrEmpty(card.Id)) return;
                if (seen.Add(card.Id)) all.Add(card);
            }

            foreach (var tab in gallery.ViewerTabs)
            {
                if (tab.IsCollage)
                    foreach (var card in tab.CollageItems) Add(card);
                else
                    Add(tab.Card);

                if (includeNavLists)
                    foreach (var card in tab.NavList) Add(card);
            }

            // VisibleArtworks, not FilteredArtworks — the latter is paginated to one page (50).
            if (includeNavLists)
                foreach (var card in gallery.VisibleArtworks) Add(card);

            return all.Count > 0 ? all : _items;
        }
        catch
        {
            return _items;   // AppServices not ready — fall back to this collage
        }
    }

    private bool _showTileStrip;

    /// <summary>Toggles the bottom thumbnail strip used to jump to a tile without panning.</summary>
    public bool ShowTileStrip
    {
        get => _showTileStrip;
        set
        {
            if (_showTileStrip == value) return;
            _showTileStrip = value;
            OnPropertyChanged();
            if (value)
            {
                // Rebuild so tabs opened since the strip was last shown appear in the dropdown.
                _sources = null;
                OnPropertyChanged(nameof(Sources));
                OnPropertyChanged(nameof(StripItems));
                OnPropertyChanged(nameof(StripHint));
                _ = LoadStripThumbnailsAsync();
            }
        }
    }

    /// <summary>
    /// Fills in any tile thumbnails that aren't decoded yet, in small batches so the strip can't
    /// saturate the shared image loader and stall the collage itself.
    /// </summary>
    private async Task LoadStripThumbnailsAsync()
    {
        var loader = AppServices.Get<Pikura.Core.Services.PixivImageLoader>();
        if (loader is null) return;

        const int batchSize = 8;
        var pending = StripItems.Where(c => c.Thumbnail is null).ToList();
        for (var i = 0; i < pending.Count; i += batchSize)
        {
            try { await Task.WhenAll(pending.Skip(i).Take(batchSize).Select(c => c.LoadThumbnailAsync(loader))); }
            catch { /* a failed thumbnail just leaves that tile blank */ }
        }
    }

    private PropertyChangedEventHandler? _propertyChanged;
    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => _propertyChanged += value;
        remove => _propertyChanged -= value;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public CollageFullscreenWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public CollageFullscreenWindow(IEnumerable<ArtworkCardViewModel> items)
    {
        InitializeComponent();
        DataContext = this;
        _items = items.ToList();
        if (CollageItemsControl != null)
            CollageItemsControl.ItemsSource = _items;
        OnPropertyChanged(nameof(Items));

        KeyDown += OnKeyDown;
        PointerWheelChanged += OnPointerWheelChanged;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;

        Zoom = e.Delta.Y > 0
            ? Zoom * ZoomStep
            : Zoom / ZoomStep;
        e.Handled = true;
    }

    private void OnTileReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Handled || e.InitialPressMouseButton != MouseButton.Left) return;
        if ((sender as Control)?.DataContext is not ArtworkCardViewModel card) return;
        e.Handled = true;
        OnTileReleasedCore(card);
    }

    /// <summary>Opens <paramref name="card"/> in the fullscreen viewer. Shared by tile clicks and
    /// by strip entries that aren't part of this collage (possible under "All tabs" scope).</summary>
    private async void OnTileReleasedCore(ArtworkCardViewModel card)
    {
        try
        {
            var gallery = AppServices.Get<GalleryViewModel>();

            // Navigation scope follows whatever the strip is currently showing, so "All tabs"
            // lets the viewer's arrow keys roam everything the user has open.
            var navList = StripItems;

            if (gallery.SelectedViewerTab is { IsCollage: true } tab && _items.Contains(card))
            {
                tab.NavList.Clear();
                tab.NavList.AddRange(navList);
                tab.TotalCount = navList.Count;
                tab.NavigateTo(card);
                tab.IsViewingCollageItem = true;
                gallery.InlineViewerCard = card;
                gallery.InlineViewerCardList = navList;
            }
            else
            {
                gallery.InlineViewerCardList = navList;
                gallery.InlineViewerCard = card;
            }

            // Seed with the tile's already-decoded thumbnail so the viewer opens showing the
            // artwork immediately rather than a blank canvas.
            var viewer = new FullscreenViewerWindow(card.Artwork, gallery, card.Thumbnail, navList);
            await viewer.ShowDialog(this);
        }
        catch { /* non-fatal — e.g. AppServices not ready */ }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Add:
            case Key.OemPlus:
                Zoom *= ZoomStep;
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                Zoom /= ZoomStep;
                e.Handled = true;
                break;
            case Key.R:
                Zoom = 1.0;
                e.Handled = true;
                break;
            case Key.Left:
                PanBy(-PanStep, 0);
                e.Handled = true;
                break;
            case Key.Right:
                PanBy(PanStep, 0);
                e.Handled = true;
                break;
            case Key.Up:
                PanBy(0, -PanStep);
                e.Handled = true;
                break;
            case Key.Down:
                PanBy(0, PanStep);
                e.Handled = true;
                break;
        }
    }

    private const double PanStep = 60.0;

    private void PanBy(double dx, double dy)
    {
        if (ScrollViewer is null) return;
        ScrollViewer.Offset = new Vector(ScrollViewer.Offset.X + dx, ScrollViewer.Offset.Y + dy);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Opens the right-clicked tile as its own viewer tab instead of taking over the collage tab,
    /// then closes fullscreen so the new tab is visible behind it. Lets a collage be fanned out
    /// into separate tabs you can switch between, rather than only being browsed in place.
    /// </summary>
    private void OnOpenTileInNewTab(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ArtworkCardViewModel card) return;
        try
        {
            var gallery = AppServices.Get<GalleryViewModel>();
            gallery.OpenInNewTab(card, _items, _items.Count, source: "Collage");
            gallery.ShowPreview = true;
            Close();
        }
        catch { /* non-fatal — e.g. AppServices not ready */ }
    }

    /// <summary>Fans the whole collage out into one viewer tab per artwork.</summary>
    private void OnOpenAllTilesInNewTabs(object? sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        try
        {
            var gallery = AppServices.Get<GalleryViewModel>();
            foreach (var card in _items)
                gallery.OpenInNewTab(card, _items, _items.Count, source: "Collage");
            gallery.ShowPreview = true;
            Close();
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Scrolls the masonry so the clicked tile is in view, rather than opening it — the
    /// strip is for navigating a large collage, and tapping a tile in the grid still opens it.</summary>
    private void OnTileStripClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ArtworkCardViewModel card) return;
        if (ScrollViewer is null || CollageItemsControl is null) return;

        // Only tiles belonging to *this* collage exist in the masonry. With "All tabs" scope the
        // strip can list artworks from elsewhere — those have nothing to scroll to, so open them
        // in the viewer instead of silently doing nothing.
        var index = _items.ToList().IndexOf(card);
        if (index < 0)
        {
            OnTileReleasedCore(card);
            return;
        }

        // The container may not be realised yet for a tile far down the collage; a layout pass
        // first gives ContainerFromIndex a chance to return something.
        CollageItemsControl.UpdateLayout();
        if (CollageItemsControl.ContainerFromIndex(index) is not Control container) return;

        // Centre the tile vertically where possible, clamped to the scrollable extent.
        var target = container.Bounds.Y - (ScrollViewer.Viewport.Height - container.Bounds.Height) / 2;
        var maxY = Math.Max(0, ScrollViewer.Extent.Height - ScrollViewer.Viewport.Height);
        ScrollViewer.Offset = new Vector(ScrollViewer.Offset.X, Math.Clamp(target, 0, maxY));
    }

    // Right-click drag to pan the zoomed collage
    private void OnPanPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || e.Pointer.IsPrimary == false || e.GetCurrentPoint(this).Properties.IsRightButtonPressed == false) return;
        if (ScrollViewer is null) return;
        _isPanning = true;
        _panStartPoint = e.GetPosition(this);
        _panStartOffset = ScrollViewer.Offset;
        e.Pointer.Capture(ScrollViewer);
        e.Handled = true;
    }

    private void OnPanPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || ScrollViewer is null) return;
        var pos = e.GetPosition(this);
        var delta = pos - _panStartPoint;
        ScrollViewer.Offset = _panStartOffset - new Vector(delta.X, delta.Y);
        e.Handled = true;
    }

    private void OnPanPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPanPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPanning = false;
    }
}
