using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Pikura.Avalonia.Localization;

namespace Pikura.Avalonia.Controls;

internal static class StatPalette
{
    public static readonly Color R18 = Color.Parse("#E0245E");
    public static readonly Color Download = Color.Parse("#1E9E6A");

    /// <summary>The theme's accent (follows seasonal palettes and Light/Dark/High Contrast).</summary>
    public static Color Accent(Control c) =>
        c.TryFindResource("ControlBackgroundBrushSolidAccent", c.ActualThemeVariant, out var r) && r is ISolidColorBrush b ? b.Color : Color.Parse("#2B8BE6");

    public static IBrush Muted(Control c) =>
        c.TryFindResource("DefaultForegroundBrushTertiary", c.ActualThemeVariant, out var r) && r is IBrush b ? b : Brushes.Gray;

    public static FormattedText Text(Control c, string s, double size, IBrush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);
}

/// <summary>Redraws when the theme variant or palette resources change.</summary>
public abstract class StatChart : Control
{
    protected StatChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual();
    }
}

/// <summary>GitHub-style daily activity grid for the last year (weeks as columns, Sunday on top).</summary>
public sealed class ActivityHeatmap : StatChart
{
    public static readonly StyledProperty<IReadOnlyDictionary<DateOnly, int>?> DataProperty =
        AvaloniaProperty.Register<ActivityHeatmap, IReadOnlyDictionary<DateOnly, int>?>(nameof(Data));
    public static readonly StyledProperty<DateOnly?> FirstDayProperty =
        AvaloniaProperty.Register<ActivityHeatmap, DateOnly?>(nameof(FirstDay));

    private const int Weeks = 53;
    private const double LeftLabels = 36, TopLabels = 20, Gap = 3;

    static ActivityHeatmap()
    {
        AffectsRender<ActivityHeatmap>(DataProperty, FirstDayProperty);
        AffectsMeasure<ActivityHeatmap>(DataProperty);
    }

    public IReadOnlyDictionary<DateOnly, int>? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public DateOnly? FirstDay { get => GetValue(FirstDayProperty); set => SetValue(FirstDayProperty, value); }

    private static double Cell(double width) => Math.Clamp((width - LeftLabels) / Weeks - Gap, 6, 20);

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? Weeks * 16 + LeftLabels : available.Width;
        return new Size(w, TopLabels + 7 * (Cell(w) + Gap));
    }

    public override void Render(DrawingContext ctx)
    {
        var cell = Cell(Bounds.Width);
        var step = cell + Gap;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = today.AddDays(-(int)today.DayOfWeek - 7 * (Weeks - 1));
        var data = Data ?? new Dictionary<DateOnly, int>();
        var max = data.Count == 0 ? 1 : Math.Max(1, data.Values.Max());
        var muted = StatPalette.Muted(this);
        var accent = StatPalette.Accent(this);
        var empty = new SolidColorBrush(Colors.Gray, 0.16);
        var outline = new Pen(new SolidColorBrush(Colors.Gray, 0.22), 1);
        var weekdayNames = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        foreach (var row in new[] { 0, 2, 4, 6 })
        {
            var t = StatPalette.Text(this, weekdayNames[row], 11, muted);
            ctx.DrawText(t, new Point(0, TopLabels + row * step + (cell - t.Height) / 2));
        }
        var lastMonth = -1;
        for (var w = 0; w < Weeks; w++)
        {
            var weekStart = start.AddDays(w * 7);
            var x = LeftLabels + w * step;
            if (weekStart.Month != lastMonth && w < Weeks - 2)
            {
                ctx.DrawText(StatPalette.Text(this, CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(weekStart.Month), 11, muted), new Point(x, 0));
                lastMonth = weekStart.Month;
            }
            for (var d = 0; d < 7; d++)
            {
                var day = weekStart.AddDays(d);
                if (day > today) continue;
                var rect = new Rect(x, TopLabels + d * step, cell, cell);
                var v = data.GetValueOrDefault(day);
                if (v == 0)
                {
                    if (FirstDay is { } f && day >= f) ctx.DrawRectangle(empty, null, rect, 2, 2);
                    else ctx.DrawRectangle(null, outline, rect.Deflate(0.5), 2, 2);
                    continue;
                }
                var level = (double)v / max;
                var opacity = level > 0.75 ? 1 : level > 0.5 ? 0.8 : level > 0.25 ? 0.55 : 0.35;
                ctx.DrawRectangle(new SolidColorBrush(accent, opacity), null, rect, 2, 2);
            }
        }
    }
}

/// <summary>24 hourly bars with Night/Morning/Afternoon/Evening shares under them.</summary>
public sealed class HourBarChart : StatChart
{
    public static readonly StyledProperty<int[]?> ValuesProperty = AvaloniaProperty.Register<HourBarChart, int[]?>(nameof(Values));
    static HourBarChart() => AffectsRender<HourBarChart>(ValuesProperty);
    public int[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 600 : available.Width, 250);

    public override void Render(DrawingContext ctx)
    {
        var values = Values is { Length: 24 } v ? v : new int[24];
        var max = Math.Max(1, values.Max());
        var muted = StatPalette.Muted(this);
        var fg = this.TryFindResource("DefaultForegroundBrushSecondary", ActualThemeVariant, out var fr) && fr is IBrush fb ? fb : Brushes.White;
        const double left = 26, top = 8, bottomAxis = 64;
        var w = Bounds.Width - left;
        var h = Bounds.Height - top - bottomAxis;
        var baseY = top + h;
        var grid = new Pen(new SolidColorBrush(Colors.Gray, 0.25), 1);
        ctx.DrawLine(grid, new Point(left, top), new Point(Bounds.Width, top));
        ctx.DrawLine(grid, new Point(left, baseY), new Point(Bounds.Width, baseY));
        var maxText = StatPalette.Text(this, max.ToString(CultureInfo.CurrentCulture), 11, muted);
        ctx.DrawText(maxText, new Point(left - 6 - maxText.Width, top - maxText.Height / 2));
        var zero = StatPalette.Text(this, "0", 11, muted);
        ctx.DrawText(zero, new Point(left - 6 - zero.Width, baseY - zero.Height / 2));
        var slot = w / 24;
        var peak = Array.IndexOf(values, values.Max());
        for (var i = 0; i < 24; i++)
        {
            if (values[i] > 0)
            {
                var bh = h * values[i] / max;
                ctx.DrawRectangle(new SolidColorBrush(StatPalette.Accent(this), i == peak ? 1 : 0.6), null, new Rect(left + i * slot + slot * 0.12, baseY - bh, slot * 0.76, bh), 2, 2);
            }
            if (i % 3 == 0)
                ctx.DrawText(StatPalette.Text(this, $"{i:00}:00", 11, muted), new Point(left + i * slot, baseY + 6));
        }
        var total = Math.Max(1, values.Sum());
        string[] keys = ["Analytics_Night", "Analytics_Morning", "Analytics_Afternoon", "Analytics_Evening"];
        for (var q = 0; q < 4; q++)
        {
            var share = values.Skip(q * 6).Take(6).Sum() * 100.0 / total;
            var x0 = left + q * 6 * slot + 4;
            var x1 = left + (q + 1) * 6 * slot - 4;
            ctx.DrawLine(new Pen(new SolidColorBrush(Colors.Gray, 0.35), 2), new Point(x0, baseY + 28), new Point(x1, baseY + 28));
            var label = StatPalette.Text(this, $"{LocalizationService.Instance[keys[q]]} {share.ToString(share < 10 ? "0.#" : "0", CultureInfo.CurrentCulture)}%", 12, fg);
            ctx.DrawText(label, new Point((x0 + x1 - label.Width) / 2, baseY + 36));
        }
    }
}

/// <summary>Single rounded bar split into colored segments.</summary>
public sealed class StackedBar : StatChart
{
    public static readonly StyledProperty<double[]?> ValuesProperty = AvaloniaProperty.Register<StackedBar, double[]?>(nameof(Values));
    static StackedBar() => AffectsRender<StackedBar>(ValuesProperty);
    public double[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 400 : available.Width, 14);

    public override void Render(DrawingContext ctx)
    {
        var values = Values ?? [];
        var total = values.Sum();
        if (total <= 0) { ctx.DrawRectangle(new SolidColorBrush(Colors.Gray, 0.2), null, new Rect(Bounds.Size), 7, 7); return; }
        Color[] colors = [StatPalette.Accent(this), StatPalette.R18, StatPalette.Download, Colors.MediumPurple];
        var x = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] <= 0) continue;
            var w = Bounds.Width * values[i] / total;
            ctx.DrawRectangle(new SolidColorBrush(colors[i % colors.Length]), null, new Rect(x, 0, Math.Max(0, w - 3), Bounds.Height), 7, 7);
            x += w;
        }
    }
}
