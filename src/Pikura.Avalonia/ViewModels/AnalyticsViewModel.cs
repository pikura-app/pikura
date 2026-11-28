using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Localization;
using Pikura.Core.Data;
using Pikura.Core.Models;
using Pikura.Core.Services;

namespace Pikura.Avalonia.ViewModels;

/// <summary>One labeled bar row (share of the largest or of the total).</summary>
public sealed class StatRow
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public double Percent { get; init; }
    public string ValueText { get; init; } = "";
    public bool IsTop { get; init; }
}

public partial class AnalyticsViewModel : ViewModelBase
{
    private readonly DownloadJobRepository _jobRepository;
    private readonly ViewedHistoryRepository _historyRepository;
    private ActivityStats? _stats;
    private bool _loading;

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _totalText = "0";
    [ObservableProperty] private string _totalSub = "";
    [ObservableProperty] private double[] _totalSegments = [];
    [ObservableProperty] private string _viewsLegend = "";
    [ObservableProperty] private string _r18Legend = "";
    [ObservableProperty] private string _downloadsLegend = "";
    [ObservableProperty] private string _todayText = "0";
    [ObservableProperty] private string _todaySub = "";
    [ObservableProperty] private string _past7Text = "0";
    [ObservableProperty] private string _past7Sub = "";
    [ObservableProperty] private string _streakText = "";
    [ObservableProperty] private string _streakSub = "";
    [ObservableProperty] private string _busiestText = "—";
    [ObservableProperty] private string _busiestSub = "";
    [ObservableProperty] private IReadOnlyDictionary<DateOnly, int>? _daily;
    [ObservableProperty] private DateOnly? _firstDay;
    [ObservableProperty] private string _activeDaysText = "";
    [ObservableProperty] private string _hourMode = "all";
    [ObservableProperty] private int[] _hours = new int[24];
    [ObservableProperty] private string _busiestHourText = "";
    [ObservableProperty] private string _busiestWeekdayText = "";

    public ObservableCollection<StatRow> Weekdays { get; } = [];
    public ObservableCollection<StatRow> MostViewedArtists { get; } = [];
    public ObservableCollection<StatRow> TopTags { get; } = [];
    public ObservableCollection<StatRow> ContentTypes { get; } = [];
    public ObservableCollection<StatRow> MostDownloadedArtists { get; } = [];
    public ObservableCollection<StatRow> DownloadJobs { get; } = [];

    public AnalyticsViewModel(DownloadJobRepository jobRepository, ViewedHistoryRepository historyRepository)
    {
        _jobRepository = jobRepository;
        _historyRepository = historyRepository;
        _ = LoadAnalyticsAsync();
    }

    public Task ReloadAsync() => LoadAnalyticsAsync();
    public Task LoadMostViewedArtistsAsync() => LoadAnalyticsAsync();

    private static string L(string key) => LocalizationService.Instance[key];
    private static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, L(key), args);
    private static string N(double v) => v.ToString("N0", CultureInfo.CurrentCulture);
    private static string Pct(double v) => v.ToString(v > 0 && v < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + "%";

    partial void OnHourModeChanged(string value) => ApplyHours();

    [RelayCommand] private void SetHourMode(string mode) => HourMode = mode;

    private void ApplyHours()
    {
        if (_stats is null) return;
        Hours = HourMode switch { "views" => _stats.HoursViews, "downloads" => _stats.HoursDownloads, _ => _stats.HoursAll };
        var peak = Hours.Max() == 0 ? -1 : Array.IndexOf(Hours, Hours.Max());
        BusiestHourText = peak < 0 ? "" : F("Analytics_BusiestHour", $"{peak:00}:00–{(peak + 1) % 24:00}:00");
    }

    [RelayCommand]
    private async Task LoadAnalyticsAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            List<ViewedHistoryRepository.ViewActivity> views;
            List<DownloadJob> jobs;
            try { views = await _historyRepository.GetActivityAsync(); } catch { views = []; }
            try { jobs = await _jobRepository.GetJobsAsync(); } catch { jobs = []; }
            var today = DateOnly.FromDateTime(DateTime.Now);
            var (stats, tables) = await Task.Run(() => Compute(views, jobs, today));
            _stats = stats;
            Apply(stats, today);
            Fill(MostViewedArtists, tables.Artists, n => N(n));
            Fill(TopTags, tables.Tags, n => N(n));
            Fill(ContentTypes, tables.Types, null, stats.Views + stats.ViewsR18);
            Fill(MostDownloadedArtists, tables.Downloaded, n => N(n));
            Fill(DownloadJobs, tables.Jobs, null, jobs.Count);
        }
        finally
        {
            _loading = false;
        }
    }

    private sealed record Tables(
        List<(string, int)> Artists, List<(string, int)> Tags, List<(string, int)> Types,
        List<(string, int)> Downloaded, List<(string, int)> Jobs);

    private static (ActivityStats, Tables) Compute(List<ViewedHistoryRepository.ViewActivity> views, List<DownloadJob> jobs, DateOnly today)
    {
        var events = new List<ActivityEvent>(views.Count);
        foreach (var v in views)
            events.Add(new ActivityEvent(v.ViewedAtUtc.ToLocalTime(), v.XRestrict >= 1 ? ActivityKind.ViewR18 : ActivityKind.View));
        foreach (var job in jobs)
            foreach (var t in job.Targets.Where(t => t.Status == TargetStatus.Completed))
            {
                var at = t.ProcessedAt ?? job.CompletedAt ?? job.CreatedAt;
                var count = Math.Max(1, t.DownloadedItems);
                for (var i = 0; i < count; i++) events.Add(new ActivityEvent(at.ToLocalTime(), ActivityKind.Download));
            }
        var stats = ActivityStats.Compute(events, today);

        List<(string, int)> Top(IEnumerable<string> keys, int n = 8) => keys.Where(k => !string.IsNullOrWhiteSpace(k))
            .GroupBy(k => k).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).Take(n).ToList();

        var types = new List<(string, int)>
        {
            (L("Analytics_TypeIllust"), views.Count(v => v.IllustType == 0)),
            (L("Analytics_TypeManga"), views.Count(v => v.IllustType == 1)),
            (L("Analytics_TypeUgoira"), views.Count(v => v.IllustType == 2)),
            ("R-18", views.Count(v => v.XRestrict >= 1)),
        };
        var downloaded = jobs.SelectMany(j => j.Targets.Where(t => t.Status == TargetStatus.Completed)
                .Select(t => (Name: !string.IsNullOrWhiteSpace(t.UserName) ? t.UserName!.Trim() : t.Type == TargetType.Artist ? ArtistName(t.Name) : "",
                    Items: Math.Max(1, t.DownloadedItems))))
            .Where(t => t.Name.Length > 0)
            .GroupBy(t => t.Name).Select(g => (g.Key, g.Sum(t => t.Items)))
            .OrderByDescending(t => t.Item2).Take(8).ToList();
        var jobRows = jobs.GroupBy(j => j.Status).Select(g => (StatusName(g.Key), g.Count())).OrderByDescending(x => x.Item2).ToList();
        return (stats, new Tables(Top(views.Select(v => v.UserName)), Top(views.SelectMany(v => v.Tags)), types, downloaded, jobRows));
    }

    private static string StatusName(JobStatus s) => s switch
    {
        JobStatus.Completed => L("Analytics_Completed"),
        JobStatus.Failed => L("Analytics_Failed"),
        _ => s.ToString(),
    };

    private static string ArtistName(string targetName)
    {
        var i = targetName.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? targetName[(i + 4)..].Trim() : targetName.Trim();
    }

    private static void Fill(ObservableCollection<StatRow> target, List<(string Name, int Count)> rows, Func<int, string>? value, int total = 0)
    {
        target.Clear();
        var max = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(r => r.Count));
        foreach (var (name, count) in rows)
        {
            var pct = total > 0 ? count * 100.0 / total : count * 100.0 / max;
            target.Add(new StatRow
            {
                Name = name,
                Count = count,
                Percent = total > 0 ? pct : pct,
                ValueText = value?.Invoke(count) ?? $"{N(count)}  ·  {Pct(pct)}",
                IsTop = count == max && count > 0,
            });
        }
    }

    private void Apply(ActivityStats s, DateOnly today)
    {
        Subtitle = s.FirstDay is { } f ? F("Analytics_CountingSince", f.ToString("MMM d, yyyy", CultureInfo.CurrentCulture), N(s.DaysTracked)) : L("Analytics_NoDataYet");
        TotalText = N(s.Total);
        var weeks = Math.Max(1, s.DaysTracked / 7.0);
        TotalSub = s.Total == 0 ? "" : F("Analytics_PerWeek", N(Math.Round(s.Total / weeks)));
        TotalSegments = [s.Views, s.ViewsR18, s.Downloads];
        var total = Math.Max(1, s.Total);
        ViewsLegend = $"{L("Analytics_Views")}  {N(s.Views)}  {Pct(s.Views * 100.0 / total)}";
        R18Legend = $"{L("Analytics_ViewsR18")}  {N(s.ViewsR18)}  {Pct(s.ViewsR18 * 100.0 / total)}";
        DownloadsLegend = $"{L("Analytics_Downloads")}  {N(s.Downloads)}  {Pct(s.Downloads * 100.0 / total)}";
        TodayText = N(s.Today);
        TodaySub = F("Analytics_TodaySub", N(s.TodayViews), N(s.TodayDownloads));
        Past7Text = N(s.Past7);
        Past7Sub = s.Prev7 == 0 ? L("Analytics_NoneBefore") : F("Analytics_Before", N(s.Prev7),
            (s.Past7 >= s.Prev7 ? "▲ " : "▼ ") + Pct(Math.Abs(s.Past7 - s.Prev7) * 100.0 / s.Prev7));
        StreakText = F("Analytics_Days", N(s.CurrentStreak));
        StreakSub = s.CurrentStreak > 0 && s.CurrentStreak >= s.LongestStreak ? L("Analytics_LongestYet") : F("Analytics_Longest", N(s.LongestStreak));
        BusiestText = s.BusiestDay is null ? "—" : N(s.BusiestDayCount);
        BusiestSub = s.BusiestDay?.ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture) ?? "";
        Daily = s.Daily;
        FirstDay = s.FirstDay;
        ActiveDaysText = F("Analytics_ActiveDays", N(s.ActiveDays), N(Math.Max(s.DaysTracked, 0)));
        ApplyHours();

        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        var sum = Math.Max(1, s.Weekdays.Sum());
        var maxDay = s.Weekdays.Max();
        Weekdays.Clear();
        for (var d = 0; d < 7; d++)
            Weekdays.Add(new StatRow { Name = names[d], Count = s.Weekdays[d], Percent = s.Weekdays[d] * 100.0 / sum, ValueText = Pct(s.Weekdays[d] * 100.0 / sum), IsTop = maxDay > 0 && s.Weekdays[d] == maxDay });
        BusiestWeekdayText = maxDay == 0 ? "" : F("Analytics_BusiestOn", CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)Array.IndexOf(s.Weekdays, maxDay)));
    }
}
