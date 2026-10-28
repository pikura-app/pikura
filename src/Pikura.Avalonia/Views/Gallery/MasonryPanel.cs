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

    public static readonly StyledProperty<bool> SqueezeToFillProperty =
        AvaloniaProperty.Register<MasonryPanel, bool>(nameof(SqueezeToFill));

    public static readonly StyledProperty<bool> StretchToFillProperty =
        AvaloniaProperty.Register<MasonryPanel, bool>(nameof(StretchToFill));

    /// <summary>When true, columns widen to fill the row (real width change — no
    /// RenderTransform). Use when card templates don't fix their own width.</summary>
    public bool StretchToFill
    {
        get => GetValue(StretchToFillProperty);
        set => SetValue(StretchToFillProperty, value);
    }

    /// <summary>Scale cards down (to ≥70%) to fit an extra column instead of leaving a wide gap.</summary>
    public bool SqueezeToFill
    {
        get => GetValue(SqueezeToFillProperty);
        set => SetValue(SqueezeToFillProperty, value);
    }

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
    private double _scale = 1.0;
    private readonly HashSet<Control> _scaledChildren = new();
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
        // StretchToFill: widen the columns so they fill the row — children are measured
        // at the real column width (no RenderTransform), so the card templates get a
        // properly-sized slot and aspect-driven heights line up.
        var actualColumnWidth = ColumnWidth;
        if (StretchToFill && !double.IsInfinity(availableSize.Width))
        {
            // Prefer one more column only when cards would still be >= 70% of ColumnWidth.
            var more = columnCount + 1;
            var wMore = (availableWidth - ItemSpacing * (more - 1)) / more;
            if (wMore >= ColumnWidth * 0.7 && wMore < ColumnWidth) columnCount = more;
            actualColumnWidth = (availableWidth - ItemSpacing * (columnCount - 1)) / columnCount;
            if (actualColumnWidth <= 0) actualColumnWidth = ColumnWidth;
        }
        // Narrower than a single card: scale cards down uniformly instead of letting the
        // right edge (and its rounded corners) get clipped. Cards carry explicit
        // Width=CardSize bindings, so a render scale is the only change that fits them all.
        _scale = columnCount == 1 && availableWidth < ColumnWidth ? availableWidth / ColumnWidth : 1.0;
        // SqueezeToFill: fit one more column by scaling every card down (to ≥70%) rather than
        // leaving most of a column's width empty.
        if (SqueezeToFill && _scale >= 1 && !double.IsInfinity(availableSize.Width))
        {
            var candidate = columnCount + 1;
            var needed = (availableWidth - ItemSpacing * (candidate - 1)) / (candidate * ColumnWidth);
            if (needed >= 0.7 && needed < 1)
            {
                columnCount = candidate;
                _scale = needed;
            }
        }

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
            columnHeights[col] += size.Height * _scale + ItemSpacing;
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
        var columnCount = _scale < 1 || double.IsInfinity(availableWidth) || availableWidth <= 0
            ? _cachedColumnCount
            : Math.Max(1, (int)Math.Floor((availableWidth + ItemSpacing) / (actualColumnWidth + ItemSpacing)));

        var columnHeights = new double[columnCount];

        // Center the columns: leftover width that can't fit another column is split evenly
        // instead of piling up as one big gap on the right (very visible with the side panel open).
        var contentWidth = columnCount * actualColumnWidth * _scale + (columnCount - 1) * ItemSpacing;
        var leftover = Math.Max(0, availableWidth - contentWidth);
        var offsetX = leftover / 2;

        for (int i = 0; i < Children.Count && i < _cachedSizes.Length; i++)
        {
            var col = PreserveOrder ? i % columnCount : FindShortestColumn(columnHeights);
            // Scaled cards occupy cw × scale, so columns step by that (not the unscaled width).
            var x = offsetX + col * (actualColumnWidth * _scale + ItemSpacing);
            var y = columnHeights[col];
            var size = _cachedSizes[i];
            var child = Children[i];

            child.Arrange(new Rect(x, y, actualColumnWidth, size.Height));
            if (_scale < 1)
            {
                child.RenderTransformOrigin = RelativePoint.TopLeft;
                child.RenderTransform = new ScaleTransform(_scale, _scale);
                _scaledChildren.Add(child);
            }
            else if (_scaledChildren.Remove(child))
            {
                child.RenderTransform = null;
            }
            columnHeights[col] += size.Height * _scale + ItemSpacing;
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
        if (change.Property == ColumnWidthProperty || change.Property == ItemSpacingProperty
            || change.Property == StretchToFillProperty)
        {
            _cachedSizes = null;
            InvalidateMeasure();
        }
    }
}
