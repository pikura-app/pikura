using Avalonia.Controls;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.ViewModels;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace Pikura.Avalonia.Views.Dialogs;

/// <summary>
/// Lets the user pick which open collage(s) a set of artworks should be added to, instead of always
/// appending to whichever collage tab happens to be selected. Multiple collages can be checked, and
/// a brand-new collage can be created in the same pass.
/// Closes with a <see cref="CollagePickerResult"/>, or null when cancelled.
/// </summary>
public partial class CollagePickerDialog : Window
{
    private readonly List<CollageTargetViewModel> _rows = [];
    private readonly int _itemCount;

    // Parameterless ctor so the XAML previewer / designer can still load the window.
    public CollagePickerDialog() : this([], 0, null) { }

    public CollagePickerDialog(IReadOnlyList<ViewerTab> collages, int itemCount, ViewerTab? preselect)
    {
        InitializeComponent();

        _itemCount = itemCount;
        _rows = collages.Select(t => new CollageTargetViewModel
        {
            Tab = t,
            // Default to the collage the user would have hit with a plain "Add to Collage" click,
            // so the common case is one click away. Never pre-check a full collage.
            IsSelected = t == preselect && t.CollageItems.Count < GalleryViewModel.MaxCollageItems,
        }).ToList();

        foreach (var row in _rows)
            row.PropertyChanged += OnRowPropertyChanged;

        CollageList.ItemsSource = _rows;
        EmptyText.IsVisible = _rows.Count == 0;

        TitleText.Text = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Instance["CollagePicker_Title"],
            itemCount);

        // No collages yet — the only useful action is creating one, so start there.
        if (_rows.Count == 0) NewCollageCheck.IsChecked = true;

        NewCollageCheck.IsCheckedChanged += (_, _) =>
        {
            NewCollageName.IsEnabled = NewCollageCheck.IsChecked == true;
            UpdateSummary();
        };
        CancelButton.Click += (_, _) => Close(null);
        AddButton.Click += (_, _) => Close(BuildResult());

        UpdateSummary();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollageTargetViewModel.IsSelected))
            UpdateSummary();
    }

    private CollagePickerResult BuildResult()
    {
        var targets = _rows.Where(r => r.IsSelected && !r.IsFull).Select(r => r.Tab).ToList();
        var createNew = NewCollageCheck.IsChecked == true;
        return new CollagePickerResult(targets, createNew, createNew ? NewCollageName.Text : null);
    }

    private void UpdateSummary()
    {
        var checkedCount = _rows.Count(r => r.IsSelected && !r.IsFull);
        var createNew = NewCollageCheck.IsChecked == true;
        var total = checkedCount + (createNew ? 1 : 0);

        AddButton.IsEnabled = total > 0;
        SummaryText.Text = total == 0
            ? LocalizationService.Instance["CollagePicker_SelectAtLeastOne"]
            : string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.Instance["CollagePicker_Summary"],
                _itemCount, total);
    }

    protected override void OnClosed(System.EventArgs e)
    {
        base.OnClosed(e);
        foreach (var row in _rows)
            row.PropertyChanged -= OnRowPropertyChanged;
    }
}
