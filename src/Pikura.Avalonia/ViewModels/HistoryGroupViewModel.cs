using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Pikura.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace Pikura.Avalonia.ViewModels;

/// <summary>
/// A collapsible date-group header used in the History tabs. Holds the jobs
/// that completed/failed/cancelled on a particular calendar day.
/// </summary>
public partial class HistoryGroupViewModel : ObservableObject
{
    /// <summary>Local calendar date this group represents (time component is midnight).</summary>
    public DateTime Date { get; }

    /// <summary>Human-friendly label: "Today", "Yesterday", a weekday, or a full date.</summary>
    public string Label { get; }

    /// <summary>Jobs belonging to this date, in display order (newest first).</summary>
    public ObservableCollection<DownloadJobViewModel> Jobs { get; } = new();

    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>Invoked when the user toggles expansion (not raised by silent sets).</summary>
    public Action<HistoryGroupViewModel>? OnToggleChanged;

    private bool _silent;

    public HistoryGroupViewModel(DateTime date)
    {
        Date = date.Date;
        Label = MakeLabel(Date);
    }

    public int Count => Jobs.Count;
    public string CountText => $"{Count} download{(Count == 1 ? "" : "s")}";
    public string Chevron => IsExpanded ? "\u25BE" : "\u25B8"; // ▾ / ▸

    /// <summary>Sets expansion without firing the toggle callback (used during regrouping).</summary>
    public void SetExpandedSilently(bool value)
    {
        _silent = true;
        IsExpanded = value;
        _silent = false;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Chevron));
        if (!_silent) OnToggleChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    private static string MakeLabel(DateTime date)
    {
        var today = DateTime.Now.Date;
        if (date == today) return "Today";
        if (date == today.AddDays(-1)) return "Yesterday";
        if (date > today.AddDays(-7)) return date.ToString("dddd, MMMM d"); // e.g. "Tuesday, May 27"
        if (date.Year == today.Year) return date.ToString("MMMM d");        // e.g. "May 20"
        return date.ToString("MMMM d, yyyy");
    }
}

/// <summary>
/// Derives a flat, virtualization-friendly view (group headers interleaved with
/// their jobs) from a flat source collection of jobs, grouped by calendar date.
///
/// The flat <see cref="View"/> contains a mix of <see cref="HistoryGroupViewModel"/>
/// (headers) and <see cref="DownloadJobViewModel"/> (rows). A single virtualizing
/// ListBox binds to it, so only on-screen rows are realized — this scales to
/// thousands of history entries. Collapsing a group simply removes its job rows
/// from the flat list, keeping it small.
/// </summary>
public sealed class HistoryTabGrouping
{
    public ObservableCollection<object> View { get; } = new();

    private readonly ObservableCollection<DownloadJobViewModel> _source;
    private readonly bool _useCompletedDate;
    private readonly HashSet<DateTime> _collapsed = new();
    private List<HistoryGroupViewModel> _groups = new();

    /// <param name="source">Flat per-tab job collection (already sorted newest-first).</param>
    /// <param name="useCompletedDate">
    /// When true, groups by CompletedAt (archival tabs); otherwise by CreatedAt.
    /// </param>
    public HistoryTabGrouping(ObservableCollection<DownloadJobViewModel> source, bool useCompletedDate)
    {
        _source = source;
        _useCompletedDate = useCompletedDate;
    }

    /// <summary>Rebuilds the date groups from the source, preserving collapse state by date.</summary>
    public void Regroup()
    {
        _groups = new List<HistoryGroupViewModel>();
        HistoryGroupViewModel? current = null;

        // Sort by grouping date descending so jobs with the same date are contiguous
        // (runtime insertions at position 0 can otherwise create duplicate groups).
        var sorted = _source.OrderByDescending(job =>
        {
            var dt = _useCompletedDate ? (job.Job.CompletedAt ?? job.Job.CreatedAt) : job.Job.CreatedAt;
            return dt == default ? DateTime.Now : dt;
        });

        foreach (var job in sorted)
        {
            var dt = _useCompletedDate ? (job.Job.CompletedAt ?? job.Job.CreatedAt) : job.Job.CreatedAt;
            var local = (dt == default ? DateTime.Now : dt.ToLocalTime()).Date;

            if (current == null || current.Date != local)
            {
                current = new HistoryGroupViewModel(local) { OnToggleChanged = OnGroupToggled };
                current.SetExpandedSilently(!_collapsed.Contains(local));
                _groups.Add(current);
            }
            current.Jobs.Add(job);
        }

        RebuildFlat();
    }

    private void OnGroupToggled(HistoryGroupViewModel g)
    {
        if (g.IsExpanded) _collapsed.Remove(g.Date);
        else _collapsed.Add(g.Date);
        RebuildFlat();
    }

    private void RebuildFlat()
    {
        View.Clear();
        foreach (var g in _groups)
        {
            View.Add(g);
            if (g.IsExpanded)
                foreach (var j in g.Jobs)
                    View.Add(j);
        }
    }
}

/// <summary>A status-group header for the Active tab (Running, Paused, Queued).</summary>
public partial class ActiveGroupViewModel : ObservableObject
{
    public string Label { get; }
    public string StatusKey { get; }
    public ObservableCollection<DownloadJobViewModel> Jobs { get; } = new();

    [ObservableProperty] private bool _isExpanded = true;

    public Action<ActiveGroupViewModel>? OnToggleChanged;
    private bool _silent;

    public ActiveGroupViewModel(string label, string statusKey)
    {
        Label = label;
        StatusKey = statusKey;
    }

    public int Count => Jobs.Count;
    public string CountText => $"{Count} job{(Count == 1 ? "" : "s")}";
    public string Chevron => IsExpanded ? "\u25BE" : "\u25B8";

    public void SetExpandedSilently(bool value)
    {
        _silent = true;
        IsExpanded = value;
        _silent = false;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Chevron));
        if (!_silent) OnToggleChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>Derives a flat view from active jobs grouped by status (Running, Paused, Pending).</summary>
public sealed class ActiveTabGrouping
{
    public ObservableCollection<object> View { get; } = new();

    private readonly ObservableCollection<DownloadJobViewModel> _source;
    private readonly HashSet<string> _collapsed = new();
    private List<ActiveGroupViewModel> _groups = new();

    public ActiveTabGrouping(ObservableCollection<DownloadJobViewModel> source)
    {
        _source = source;
    }

    public void Regroup()
    {
        _groups = new List<ActiveGroupViewModel>();
        // Order: Running (0), Paused (1), Pending (2)
        var ordered = _source.OrderBy(j => j.Job.Status switch
        {
            JobStatus.Running => 0,
            JobStatus.Paused => 1,
            _ => 2
        }).ThenBy(j => j.QueuePosition);

        ActiveGroupViewModel? current = null;
        foreach (var job in ordered)
        {
            var key = job.Job.Status switch
            {
                JobStatus.Running => "running",
                JobStatus.Paused => "paused",
                _ => "pending"
            };
            var label = job.Job.Status switch
            {
                JobStatus.Running => "▶ Running",
                JobStatus.Paused => "⏸ Paused",
                _ => "⏳ Queued"
            };

            if (current == null || current.StatusKey != key)
            {
                current = new ActiveGroupViewModel(label, key) { OnToggleChanged = OnGroupToggled };
                current.SetExpandedSilently(!_collapsed.Contains(key));
                _groups.Add(current);
            }
            current.Jobs.Add(job);
        }

        RebuildFlat();
    }

    private void OnGroupToggled(ActiveGroupViewModel g)
    {
        if (g.IsExpanded) _collapsed.Remove(g.StatusKey);
        else _collapsed.Add(g.StatusKey);
        RebuildFlat();
    }

    private void RebuildFlat()
    {
        View.Clear();
        foreach (var g in _groups)
        {
            View.Add(g);
            if (g.IsExpanded)
                foreach (var j in g.Jobs)
                    View.Add(j);
        }
    }
}
