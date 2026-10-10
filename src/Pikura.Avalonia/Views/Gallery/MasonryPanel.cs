using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Pikura.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace Pikura.Avalonia.Views.Gallery;

/// <summary>
/// Optimized masonry-style panel that arranges items in columns.
/// Uses cached measurements and efficient column tracking to prevent UI freezes.
/// </summary>
public class MasonryPanel : Panel
{
    public MasonryPanel()
    {
        // Belt-and-suspenders: explicitly invalidate layout whenever a child is added/removed/
        // moved (e.g. an ObservableCollection Insert() from an ItemsControl binding). This
        // panel caches per-child sizes/columns between Measure and Arrange (_cachedSizes); if a
        // child changes without a fresh Measure pass picking it up first — which has been
        // observed after inserting a single new item into an already-realized, non-empty
        // ItemsControl — an item can be fully generated as a control but never get arranged
        // into a visible position until *something else* forces a new layout pass (e.g.
        // toggling a filter). Force both explicitly so a bound collection change always
        // results in a visible update immediately.
        Children.CollectionChanged += OnChildrenCollectionChanged;
    }

    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(ColumnWidth), 200);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(ItemSpacing), 6);

    public static readonly StyledProperty<bool> PreserveOrderProperty =
        AvaloniaProperty.Register<MasonryPanel, bool>(nameof(PreserveOrder));

    /// <summary>Target width for each column. Panel auto-calculates actual column count.</summary>
    public double ColumnWidth
    {
        get => GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    /// <summary>Vertical and horizontal spacing between items.</summary>
    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>When true, items are assigned to columns round-robin by index (0,1,2,...,then
    /// wrapping back to column 0) instead of always dropping into the currently-shortest column.
    /// Keeps each column internally in strict source order — needed for content like rankings
    /// where position must reflect rank, while still letting each card keep its natural aspect
    /// ratio/height (unlike a plain WrapPanel, which clamps every item in a row to the same
    /// height). Default (false) keeps the original Pinterest-style shortest-column packing used
    /// by regular galleries, where visual tightness matters more than strict ordering.</summary>
    public bool PreserveOrder
    {
        get => GetValue(PreserveOrderProperty);
        set => SetValue(PreserveOrderProperty, value);
    }

    // Cache measurements between Measure and Arrange passes
    private Size[]? _cachedSizes;
    private double _cachedColumnWidth;
    private int _cachedColumnCount;
    private ScrollViewer? _parentScrollViewer;
    private readonly Dictionary<ArtworkCardViewModel, Control> _cardSubscriptions = new();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        var sv = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (sv != null)
        {
            _parentScrollViewer = sv;
            _parentScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
            _cachedSizes = null;
            InvalidateMeasure();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_parentScrollViewer != null)
        {
            _parentScrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
            _parentScrollViewer = null;
        }
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ViewportProperty ||
            e.Property == ScrollViewer.HorizontalScrollBarVisibilityProperty)
        {
            _cachedSizes = null;
            InvalidateMeasure();
        }
    }

    private void OnChildrenCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _cachedSizes = null;
        InvalidateMeasure();
        InvalidateArrange();

        if (e.NewItems != null)
        {
            foreach (Control child in e.NewItems.OfType<Control>())
            {
                if (child.DataContext is ArtworkCardViewModel card && !_cardSubscriptions.ContainsKey(card))
                {
                    _cardSubscriptions[card] = child;
                    card.PropertyChanged += OnCardPropertyChanged;
                }
            }
        }

        if (e.OldItems != null)
        {
            foreach (Control child in e.OldItems.OfType<Control>())
            {
                if (child.DataContext is ArtworkCardViewModel card && _cardSubscriptions.TryGetValue(card, out var c) && c == child)
                {
                    _cardSubscriptions.Remove(card);
                    card.PropertyChanged -= OnCardPropertyChanged;
                }
            }
        }
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArtworkCardViewModel.ClampedAspectRatio)
                         or nameof(ArtworkCardViewModel.AspectRatio)
                         or nameof(ArtworkCardViewModel.Thumbnail))
        {
            _cachedSizes = null;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return new Size(0, 0);

        var availableWidth = availableSize.Width;
        if (double.IsInfinity(availableWidth) || availableWidth <= 0 || double.IsNaN(availableWidth))
        {
            if (_parentScrollViewer?.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled)
            {
                var viewport = _parentScrollViewer.Viewport;
                availableWidth = viewport.Width > 0 ? viewport.Width : ColumnWidth;
            }
            else if (_parentScrollViewer?.HorizontalScrollBarVisibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible or ScrollBarVisibility.Hidden)
            {
                availableWidth = ColumnWidth * 4;
            }
            else
            {
                // No scroll viewer yet; use one column until we attach and get a real viewport.
                availableWidth = ColumnWidth;
            }
        }

        var columnCount = Math.Max(1, (int)Math.Floor((availableWidth + ItemSpacing) / (ColumnWidth + ItemSpacing)));
        // Use the exact ColumnWidth (don't stretch to fill). This keeps card width
        // equal to CardSize so explicit Width bindings line up with the column slot
        // — extra space falls on the right of the panel as a normal empty gutter.
        var actualColumnWidth = ColumnWidth;

        // Cache for Arrange pass
        _cachedSizes = new Size[Children.Count];
        _cachedColumnWidth = actualColumnWidth;
        _cachedColumnCount = columnCount;

        var columnHeights = new double[columnCount];
        var childSize = new Size(actualColumnWidth, double.PositiveInfinity);

        // Single-pass measurement
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];

            child.Measure(childSize);

            // If the child is a Panel wrapper containing Fixed+Natural cards, only count
            // the visible sub-child's height so height-mode toggling works correctly.
            Size size;
            if (child is Panel wrapperPanel)
            {
                var visibleChild = wrapperPanel.Children
                    .FirstOrDefault(c => c.IsVisible && c.IsHitTestVisible);
                if (visibleChild != null)
                {
                    visibleChild.Measure(childSize);
                    size = new Size(actualColumnWidth, visibleChild.DesiredSize.Height);
                }
                else
                {
                    size = new Size(actualColumnWidth, child.DesiredSize.Height);
                }
            }
            else
            {
                size = child.DesiredSize;
            }

            _cachedSizes[i] = size;

            var col = PreserveOrder ? i % columnCount : FindShortestColumn(columnHeights);
            columnHeights[col] += size.Height + ItemSpacing;
        }

        var maxHeight = columnHeights.Length > 0 ? columnHeights.Max() : 0;
        if (maxHeight > 0) maxHeight -= ItemSpacing;

        return new Size(availableWidth, maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0 || _cachedSizes == null) return finalSize;

        var availableWidth = finalSize.Width;
        var actualColumnWidth = _cachedColumnWidth;

        // Recalculate column count from the final arrange width so the panel always fits
        // inside the viewport even if Measure used a fallback/wider width.
        var columnCount = double.IsInfinity(availableWidth) || availableWidth <= 0
            ? _cachedColumnCount
            : Math.Max(1, (int)Math.Floor((availableWidth + ItemSpacing) / (actualColumnWidth + ItemSpacing)));

        var columnHeights = new double[columnCount];

        // When horizontal scrolling is disabled, the panel must stay inside the
        // viewport, so start the first column flush with the left edge. Otherwise
        // keep a slight left bias so the masonry looks anchored rather than centered.
        var contentWidth = columnCount * actualColumnWidth + (columnCount - 1) * ItemSpacing;
        var leftover = Math.Max(0, availableWidth - contentWidth);
        var offsetX = _parentScrollViewer?.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
            ? 0
            : leftover * 0.2;

        for (int i = 0; i < Children.Count && i < _cachedSizes.Length; i++)
        {
            var col = PreserveOrder ? i % columnCount : FindShortestColumn(columnHeights);
            var x = offsetX + col * (actualColumnWidth + ItemSpacing);
            var y = columnHeights[col];
            var size = _cachedSizes[i];

            Children[i].Arrange(new Rect(x, y, actualColumnWidth, size.Height));
            columnHeights[col] += size.Height + ItemSpacing;
        }

        var maxHeight = columnHeights.Length > 0 ? columnHeights.Max() : 0;
        if (maxHeight > 0) maxHeight -= ItemSpacing;

        // Return the actual content extent so the parent (e.g. ScrollViewer) knows
        // the full size and can scroll properly rather than clipping.
        return new Size(contentWidth + leftover, maxHeight);
    }

    private static int FindShortestColumn(double[] heights)
    {
        // Fast linear scan - optimal for typical column counts (2-6)
        var shortest = 0;
        var minHeight = heights[0];
        for (int i = 1; i < heights.Length; i++)
        {
            if (heights[i] < minHeight)
            {
                minHeight = heights[i];
                shortest = i;
            }
        }
        return shortest;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Invalidate cache when sizing properties change
        if (change.Property == ColumnWidthProperty || change.Property == ItemSpacingProperty)
        {
            _cachedSizes = null;
            InvalidateMeasure();
        }
    }
}
