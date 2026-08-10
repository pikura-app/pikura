using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Collections.Generic;

namespace Pikura.Avalonia.Views.Dialogs;

public partial class RangePickerDialog : Window
{
    public List<int> SelectedIndexes { get; } = [];

    private readonly int _maxInclusive;

    public RangePickerDialog()
    {
        InitializeComponent();
    }

    public RangePickerDialog(string title, string description, int maxInclusive, string placeholder = "")
    {
        InitializeComponent();
        _maxInclusive = maxInclusive;
        TitleText.Text = title;
        DescText.Text = description;
        if (!string.IsNullOrEmpty(placeholder))
            RangeInput.PlaceholderText = placeholder;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        SelectedIndexes.Clear();
        SelectedIndexes.AddRange(ParseRange(RangeInput.Text ?? string.Empty));
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private IEnumerable<int> ParseRange(string input)
    {
        var result = new SortedSet<int>();
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dashIdx = part.IndexOf('-', 1); // skip leading minus
            if (dashIdx > 0
                && int.TryParse(part[..dashIdx], out var from)
                && int.TryParse(part[(dashIdx + 1)..], out var to))
            {
                for (var i = Math.Max(1, from); i <= Math.Min(_maxInclusive, to); i++)
                    result.Add(i);
            }
            else if (int.TryParse(part, out var single) && single >= 1 && single <= _maxInclusive)
            {
                result.Add(single);
            }
        }
        return result;
    }
}
