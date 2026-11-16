using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Pikura.Avalonia.Controls;

/// <summary>
/// Drop-in replacement for a string <see cref="ComboBox"/> whose dropdown has a
/// type-to-filter search box. Index 0 is treated as the "any" entry and is hidden
/// while a filter is typed.
/// </summary>
public sealed class SearchableComboBox : UserControl
{
    public static readonly StyledProperty<IList<string>?> ItemsSourceProperty =
        AvaloniaProperty.Register<SearchableComboBox, IList<string>?>(nameof(ItemsSource));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<SearchableComboBox, int>(nameof(SelectedIndex), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> SearchPlaceholderProperty =
        AvaloniaProperty.Register<SearchableComboBox, string?>(nameof(SearchPlaceholder));

    public IList<string>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public string? SearchPlaceholder
    {
        get => GetValue(SearchPlaceholderProperty);
        set => SetValue(SearchPlaceholderProperty, value);
    }

    private sealed record Entry(int Index, string Label);

    private readonly TextBlock _label;
    private readonly Flyout _flyout;
    private readonly StackPanel _panel;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly ObservableCollection<Entry> _filtered = [];
    private INotifyCollectionChanged? _observed;

    public SearchableComboBox()
    {
        _label = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chevron = new TextBlock
        {
            Text = "▾",
            Opacity = 0.7,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(chevron, 1);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        content.Children.Add(_label);
        content.Children.Add(chevron);

        _search = new TextBox();
        _search.TextChanged += (_, _) => ApplyFilter();
        _search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);

        _list = new ListBox
        {
            ItemsSource = _filtered,
            MaxHeight = 320,
            ItemTemplate = new FuncDataTemplate<Entry>((e, _) => new TextBlock
            {
                Text = e?.Label,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }),
        };
        _list.Tapped += (_, _) => Commit(_list.SelectedItem as Entry);
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);

        _panel = new StackPanel { Spacing = 6, Children = { _search, _list } };
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = _panel,
        };
        _flyout.Opened += OnFlyoutOpened;

        Content = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = content,
            Flyout = _flyout,
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (_observed != null) _observed.CollectionChanged -= OnItemsChanged;
            _observed = ItemsSource as INotifyCollectionChanged;
            if (_observed != null) _observed.CollectionChanged += OnItemsChanged;
            UpdateLabel();
        }
        else if (change.Property == SelectedIndexProperty)
        {
            UpdateLabel();
        }
        else if (change.Property == SearchPlaceholderProperty)
        {
            _search.PlaceholderText = SearchPlaceholder;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateLabel();

    private void UpdateLabel()
    {
        var items = ItemsSource;
        _label.Text = items == null || items.Count == 0 ? ""
            : SelectedIndex >= 0 && SelectedIndex < items.Count ? items[SelectedIndex]
            : items[0];
    }

    private void OnFlyoutOpened(object? sender, EventArgs e)
    {
        _panel.Width = Math.Max(Bounds.Width, 280);
        _search.Text = "";
        ApplyFilter();
        Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Input);
    }

    private void ApplyFilter()
    {
        var query = _search.Text?.Trim() ?? "";
        _filtered.Clear();
        var items = ItemsSource;
        if (items == null) return;

        for (var i = 0; i < items.Count; i++)
        {
            if (query.Length == 0
                || (i > 0 && items[i].Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                _filtered.Add(new Entry(i, items[i]));
        }

        Entry? highlight = null;
        if (query.Length == 0)
        {
            foreach (var entry in _filtered)
                if (entry.Index == SelectedIndex) { highlight = entry; break; }
        }
        highlight ??= _filtered.Count > 0 ? _filtered[0] : null;
        _list.SelectedItem = highlight;
        if (highlight != null) _list.ScrollIntoView(highlight);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _filtered.Count > 0:
                var next = Math.Min(_list.SelectedIndex + 1, _filtered.Count - 1);
                _list.SelectedIndex = next;
                _list.ScrollIntoView(_filtered[next]);
                e.Handled = true;
                break;
            case Key.Up when _filtered.Count > 0:
                var prev = Math.Max(_list.SelectedIndex - 1, 0);
                _list.SelectedIndex = prev;
                _list.ScrollIntoView(_filtered[prev]);
                e.Handled = true;
                break;
            case Key.Enter:
                Commit(_list.SelectedItem as Entry);
                e.Handled = true;
                break;
            case Key.Escape:
                _flyout.Hide();
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit(_list.SelectedItem as Entry);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _flyout.Hide();
            e.Handled = true;
        }
    }

    private void Commit(Entry? entry)
    {
        if (entry == null) return;
        _flyout.Hide();
        SetCurrentValue(SelectedIndexProperty, entry.Index);
    }
}
