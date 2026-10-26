using Avalonia;
using Avalonia.Controls;
using System;

namespace Pikura.Avalonia.Views.Gallery;

/// <summary>
/// Grid of equal-width cells that always fills the row: squeezes in an extra column when
/// cards can shrink a little to fit it, otherwise widens the cells, so the row spans the full
/// width (no big empty gutter on the right, like the Gallery). Items keep source order, row by row;
/// each row is as tall as its tallest item. Children should not set an explicit Width.
/// </summary>
public class FillWrapPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<FillWrapPanel, double>(nameof(MinItemWidth), 200);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<FillWrapPanel, double>(nameof(ItemSpacing), 6);

    static FillWrapPanel() => AffectsMeasure<FillWrapPanel>(MinItemWidthProperty, ItemSpacingProperty);

    /// <summary>Smallest cell width (normally the tab's card-size slider).</summary>
    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }

    /// <summary>Gap between cells, horizontally and vertically.</summary>
    public double ItemSpacing { get => GetValue(ItemSpacingProperty); set => SetValue(ItemSpacingProperty, value); }

    /// <summary>How far a card may shrink below <see cref="MinItemWidth"/> to fit another column.</summary>
    private const double MinShrink = 0.7;

    private int _columns = 1;
    private double _cellWidth;

    private (int Columns, double CellWidth) Layout(double width)
    {
        var min = Math.Max(1, MinItemWidth);
        var gap = ItemSpacing;
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0) return (1, min);
        // Prefer squeezing in one more column (cards shrink, at most to MinShrink × size) over
        // widening fewer columns — e.g. 2.6 cards' worth of room shows 3 slightly smaller cards.
        var fit = (width + gap) / (min + gap);
        var cols = Math.Max(1, (int)Math.Ceiling(fit - 0.001));
        if ((width - gap * (cols - 1)) / cols < min * MinShrink)
            cols = Math.Max(1, (int)Math.Floor(fit));
        var cell = (width - gap * (cols - 1)) / cols;
        // Never stretch a lone column past the set size; centre it instead.
        if (cols == 1) cell = Math.Min(cell, min);
        return (cols, Math.Max(1, cell));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        (_columns, _cellWidth) = Layout(availableSize.Width);
        double total = 0, rowHeight = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(_cellWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (i % _columns == _columns - 1 || i == Children.Count - 1)
            {
                total += rowHeight + ItemSpacing;
                rowHeight = 0;
            }
        }
        if (total > 0) total -= ItemSpacing;
        var width = double.IsInfinity(availableSize.Width)
            ? _columns * _cellWidth + (_columns - 1) * ItemSpacing
            : availableSize.Width;
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        (_columns, _cellWidth) = Layout(finalSize.Width);
        var content = _columns * _cellWidth + (_columns - 1) * ItemSpacing;
        var x0 = Math.Max(0, (finalSize.Width - content) / 2);
        double y = 0;
        for (int start = 0; start < Children.Count; start += _columns)
        {
            var end = Math.Min(start + _columns, Children.Count);
            double rowHeight = 0;
            for (int i = start; i < end; i++) rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            for (int i = start; i < end; i++)
                Children[i].Arrange(new Rect(x0 + (i - start) * (_cellWidth + ItemSpacing), y, _cellWidth, rowHeight));
            y += rowHeight + ItemSpacing;
        }
        return finalSize;
    }
}
