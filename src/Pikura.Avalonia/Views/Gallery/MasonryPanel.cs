using Avalonia;
using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pikura.Avalonia.Views.Gallery;

/// <summary>
/// Optimized masonry-style panel that arranges items in columns.
/// Uses cached measurements and efficient column tracking to prevent UI freezes.
/// </summary>
public class MasonryPanel : Panel
{
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

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return new Size(0, 0);

        var availableWidth = availableSize.Width;
        if (double.IsInfinity(availableWidth) || availableWidth <= 0)
            availableWidth = ColumnWidth * 4;

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
        var columnCount = _cachedColumnCount;
        var actualColumnWidth = _cachedColumnWidth;

        // Recalculate if width changed significantly
        if (Math.Abs(availableWidth - finalSize.Width) > 1)
        {
            columnCount = Math.Max(1, (int)Math.Floor((availableWidth + ItemSpacing) / (ColumnWidth + ItemSpacing)));
            actualColumnWidth = ColumnWidth;
        }

        var columnHeights = new double[columnCount];

        // Bias cards toward the left margin: give the left gutter ~20% of the
        // leftover space and the right gutter the remainder. This keeps cards
        // visually anchored near the panel's left edge while still leaving some
        // breathing room and preventing all leftover from piling on one side.
        var contentWidth = columnCount * actualColumnWidth + (columnCount - 1) * ItemSpacing;
        var leftover = Math.Max(0, availableWidth - contentWidth);
        var offsetX = leftover * 0.2;

        for (int i = 0; i < Children.Count && i < _cachedSizes.Length; i++)
        {
            var col = PreserveOrder ? i % columnCount : FindShortestColumn(columnHeights);
            var x = offsetX + col * (actualColumnWidth + ItemSpacing);
            var y = columnHeights[col];
            var size = _cachedSizes[i];

            Children[i].Arrange(new Rect(x, y, actualColumnWidth, size.Height));
            columnHeights[col] += size.Height + ItemSpacing;
        }

        return finalSize;
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
