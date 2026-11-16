using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Lets a view model keep the user's place when it swaps what a list shows — e.g. switching
/// between "Pages" and autoload/infinite scroll. The view model calls <see cref="Capture"/>
/// before the swap to get the topmost visible item, works out where that item lives in the new
/// layout, then calls <see cref="ScrollTo(Func{object?})"/> to bring it back to the top of the
/// viewport. The owning view wires it up once with <see cref="Attach"/>.
/// </summary>
public sealed class ScrollAnchor
{
    private Func<object?>? _capture;
    private Action<Func<object?>>? _scroll;

    /// <summary>The first item (in list order) at least partly visible, or null.</summary>
    public object? Capture() => _capture?.Invoke();

    /// <summary>Scrolls so <paramref name="item"/> is at the top once the list has laid out.</summary>
    public void ScrollTo(object? item)
    {
        if (item != null) _scroll?.Invoke(() => item);
    }

    /// <summary>Like <see cref="ScrollTo(object?)"/>, but resolves the item after layout — for
    /// lists that are rebuilt with new item instances.</summary>
    public void ScrollTo(Func<object?> resolve) => _scroll?.Invoke(resolve);

    /// <summary>Connects <paramref name="anchor"/> to the visible <see cref="ItemsControl"/>s
    /// inside <paramref name="view"/> whose ItemsSource is <paramref name="items"/>.</summary>
    public static void Attach(Control view, ScrollAnchor anchor, IEnumerable items)
    {
        IEnumerable<(ItemsControl List, ScrollViewer Scroll)> Hosts() =>
            view.GetVisualDescendants()
                .OfType<ItemsControl>()
                .Where(ic => ReferenceEquals(ic.ItemsSource, items) && ic.IsEffectivelyVisible)
                .Select(ic => (ic, ic.FindAncestorOfType<ScrollViewer>()))
                .Where(h => h.Item2 != null)
                .Select(h => (h.ic, h.Item2!));

        anchor._capture = () =>
        {
            foreach (var (list, scroll) in Hosts())
            {
                object? best = null;
                var bestIndex = int.MaxValue;
                foreach (var container in list.GetRealizedContainers())
                {
                    if (container.TranslatePoint(default, scroll) is not { } p) continue;
                    if (p.Y + container.Bounds.Height <= 1 || p.Y >= scroll.Viewport.Height) continue;
                    var index = list.IndexFromContainer(container);
                    if (index < 0 || index >= bestIndex) continue;
                    bestIndex = index;
                    best = list.ItemFromContainer(container);
                }
                if (best != null) return best;
            }
            return null;
        };

        anchor._scroll = resolve =>
        {
            // Cards keep resizing for a moment as thumbnails arrive (masonry layout), so settle
            // the position a few times — unless the user has started scrolling themselves.
            var lastSet = new Dictionary<ScrollViewer, double>();
            void Apply(int attempt)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (resolve() is not { } item) return;
                    foreach (var (list, scroll) in Hosts())
                    {
                        if (lastSet.TryGetValue(scroll, out var previous) && Math.Abs(scroll.Offset.Y - previous) > 1)
                            return; // user scrolled since our last correction
                        if (list.ContainerFromItem(item) is not { } container
                            || container.TranslatePoint(default, scroll) is not { } p) continue;
                        var y = Math.Max(0, scroll.Offset.Y + p.Y - 8);
                        scroll.Offset = new Vector(scroll.Offset.X, y);
                        lastSet[scroll] = scroll.Offset.Y;
                    }
                    if (attempt < 3)
                        DispatcherTimer.RunOnce(() => Apply(attempt + 1), TimeSpan.FromMilliseconds(150));
                }, DispatcherPriority.Background);
            }
            Apply(0);
        };
    }
}
