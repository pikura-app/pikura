using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Pikura.Avalonia.Views.Dialogs;

/// <summary>
/// Custom "from – to" date range picker content, used as the content of an anchored Popup
/// (rather than a separate modal Window) — matches the common "pick a date range inline under
/// the trigger" pattern (e.g. airline booking sites) instead of popping up a whole new window.
/// Dates can be typed directly (MM-DD-YYYY) or picked from the calendars, kept in sync both ways.
/// </summary>
public partial class DateRangePickerPanel : UserControl
{
    private static readonly string[] AcceptedFormats =
    [
        "MM-dd-yyyy", "M-d-yyyy", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd",
    ];

    public DateTime? RangeStart { get; private set; }
    public DateTime? RangeEnd { get; private set; }

    /// <summary>Raised when the user clicks "Apply range" with two valid dates.</summary>
    public event EventHandler? Applied;
    /// <summary>Raised when the user clicks "Cancel".</summary>
    public event EventHandler? Cancelled;

    private bool _suppressSync;

    public DateRangePickerPanel()
    {
        InitializeComponent();
        StartCalendar.DisplayDateEnd = DateTime.Today;
        EndCalendar.DisplayDateEnd = DateTime.Today;
    }

    public void SetInitialRange(DateTime? start, DateTime? end)
    {
        if (start is { } s) SetStart(s);
        if (end is { } en) SetEnd(en);
    }

    private void SetStart(DateTime date)
    {
        _suppressSync = true;
        StartDateBox.Text = date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        StartCalendar.SelectedDate = date;
        StartCalendar.DisplayDate = date;
        _suppressSync = false;
    }

    private void SetEnd(DateTime date)
    {
        _suppressSync = true;
        EndDateBox.Text = date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        EndCalendar.SelectedDate = date;
        EndCalendar.DisplayDate = date;
        _suppressSync = false;
    }

    private static bool TryParse(string? text, out DateTime date) =>
        DateTime.TryParseExact((text ?? string.Empty).Trim(), AcceptedFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private void OnStartTextChanged(object? sender, global::Avalonia.Controls.TextChangedEventArgs e)
    {
        if (_suppressSync) return;
        ErrorText.IsVisible = false;
        if (TryParse(StartDateBox.Text, out var d))
        {
            _suppressSync = true;
            StartCalendar.SelectedDate = d;
            StartCalendar.DisplayDate = d;
            _suppressSync = false;
        }
    }

    private void OnEndTextChanged(object? sender, global::Avalonia.Controls.TextChangedEventArgs e)
    {
        if (_suppressSync) return;
        ErrorText.IsVisible = false;
        if (TryParse(EndDateBox.Text, out var d))
        {
            _suppressSync = true;
            EndCalendar.SelectedDate = d;
            EndCalendar.DisplayDate = d;
            _suppressSync = false;
        }
    }

    private void OnStartCalendarChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSync) return;
        if (StartCalendar.SelectedDate is DateTime dt) SetStart(dt.Date);
    }

    private void OnEndCalendarChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSync) return;
        if (EndCalendar.SelectedDate is DateTime dt) SetEnd(dt.Date);
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (!TryParse(StartDateBox.Text, out var start) || !TryParse(EndDateBox.Text, out var end))
        {
            ErrorText.Text = "Enter both dates as MM-DD-YYYY (e.g. 10-14-2015).";
            ErrorText.IsVisible = true;
            return;
        }
        RangeStart = start;
        RangeEnd = end;
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);
}
