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

/// <summary>Artwork tile in Recently viewed / Most viewed.</summary>
public sealed class AnalyticsArtwork(ArtworkCardViewModel card, string caption)
{
    public ArtworkCardViewModel Card { get; } = card;
    public string Caption { get; } = caption;
}

/// <summary>Dic or pixivision article row.</summary>
public sealed class AnalyticsArticle
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string Caption { get; init; } = "";
    public DicLibraryEntry? Dic { get; init; }
    public PixivisionArticleSummary? Pixivision { get; init; }
}

public partial class AnalyticsViewModel : ViewModelBase
{
    private readonly PixivImageLoader _imageLoader;
    private readonly Pikura.Core.Settings.SettingsService _settings;
    private readonly DicLibraryService _dicLibrary;
    private readonly PixivisionHistoryService _pixivisionHistory;
    public ObservableCollection<AnalyticsArtwork> RecentArtworks { get; } = [];
    public ObservableCollection<AnalyticsArtwork> MostViewedArtworks { get; } = [];
    public ObservableCollection<AnalyticsArticle> RecentArticles { get; } = [];
    [ObservableProperty] private string _articlesLegend = "";
    [ObservableProperty] private bool _viewCountsAreNew;
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
    [ObservableProperty] private string _period = "30d";
    [ObservableProperty] private string _totalTitle = "";
    [ObservableProperty] private int[] _timeline = [];
    [ObservableProperty] private string[] _timelineLabels = [];
    [ObservableProperty] private string _timelineUnitText = "";
    private List<ViewedHistoryRepository.ViewActivity> _views = [];
    private List<DownloadJob> _jobs = [];
    private List<DateTime> _articleTimes = [];
    private int _periodVersion;
    [ObservableProperty] private int[] _hours = new int[24];
    [ObservableProperty] private string _busiestHourText = "";
    [ObservableProperty] private string _busiestWeekdayText = "";

    public SectionPeriod TimelinePeriod { get; } = new();
    public SectionPeriod HoursPeriod { get; } = new();
    public SectionPeriod WeekdayPeriod { get; } = new();
    public SectionPeriod ArtistsPeriod { get; } = new();
    public SectionPeriod TagsPeriod { get; } = new();
    public SectionPeriod TypesPeriod { get; } = new();
    public SectionPeriod DownloadedPeriod { get; } = new();
    public SectionPeriod JobsPeriod { get; } = new();

    public ObservableCollection<StatRow> Weekdays { get; } = [];
    public ObservableCollection<StatRow> MostViewedArtists { get; } = [];
    public ObservableCollection<StatRow> TopTags { get; } = [];
    public ObservableCollection<StatRow> ContentTypes { get; } = [];
    public ObservableCollection<StatRow> MostDownloadedArtists { get; } = [];
    public ObservableCollection<StatRow> DownloadJobs { get; } = [];

    public AnalyticsViewModel(DownloadJobRepository jobRepository, ViewedHistoryRepository historyRepository, PixivImageLoader imageLoader,
        Pikura.Core.Settings.SettingsService settings, DicLibraryService dicLibrary, PixivisionHistoryService pixivisionHistory)
    {
        _jobRepository = jobRepository;
        _historyRepository = historyRepository;
        _imageLoader = imageLoader;
        _settings = settings;
        _dicLibrary = dicLibrary;
        _pixivisionHistory = pixivisionHistory;
        foreach (var section in new[] { TimelinePeriod, HoursPeriod, WeekdayPeriod, ArtistsPeriod, TagsPeriod, TypesPeriod, DownloadedPeriod, JobsPeriod })
            section.Changed += () => _ = ApplyPeriodAsync();
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
    [RelayCommand] private void SetPeriod(string period) => Period = period;
    partial void OnPeriodChanged(string value) => _ = ApplyPeriodAsync();

    private static DateTime? Cutoff(string period, DateOnly today) => period switch
    {
        "7d" => today.AddDays(-6).ToDateTime(TimeOnly.MinValue),
        "30d" => today.AddDays(-29).ToDateTime(TimeOnly.MinValue),
        "6m" => today.AddMonths(-6).AddDays(1).ToDateTime(TimeOnly.MinValue),
        "1y" => today.AddYears(-1).AddDays(1).ToDateTime(TimeOnly.MinValue),
        "5y" => today.AddYears(-5).AddDays(1).ToDateTime(TimeOnly.MinValue),
        _ => null,
    };

    private static string PeriodName(string period) => L(period switch
    {
        "7d" => "Analytics_Period7d", "30d" => "Analytics_Period30d", "6m" => "Analytics_Period6m",
        "1y" => "Analytics_Period1y", "5y" => "Analytics_Period5y", _ => "Analytics_PeriodAll",
    });

    private string Effective(SectionPeriod section) => section.Override ?? Period;

    private static (TimelineUnit Unit, int Buckets) TimelineShape(string period, DateOnly today, DateOnly? first) => period switch
    {
        "7d" => (TimelineUnit.Day, 7),
        "30d" => (TimelineUnit.Day, 30),
        "6m" => (TimelineUnit.Week, 26),
        "1y" => (TimelineUnit.Week, 52),
        "5y" => (TimelineUnit.Month, 60),
        _ => (TimelineUnit.Month, first is { } f ? Math.Clamp((today.Year - f.Year) * 12 + today.Month - f.Month + 1, 1, 240) : 12),
    };

    /// <summary>Recomputes the page period and every section's own period. Each distinct period is
    /// computed once, off the UI thread, however many sections share it.</summary>
    private async Task ApplyPeriodAsync()
    {
        var version = ++_periodVersion;
        var period = Period;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var (views, jobs, articles, first) = (_views, _jobs, _articleTimes, FirstDay);
        var timelinePeriod = Effective(TimelinePeriod);
        var (unit, buckets) = TimelineShape(timelinePeriod, today, first);
        var needed = new[] { period, timelinePeriod, Effective(HoursPeriod), Effective(WeekdayPeriod), Effective(ArtistsPeriod),
            Effective(TagsPeriod), Effective(TypesPeriod), Effective(DownloadedPeriod), Effective(JobsPeriod) }.Distinct().ToList();
        var (results, timeline) = await Task.Run(() =>
        {
            var r = needed.ToDictionary(p => p, p => Compute(views, jobs, articles, today, Cutoff(p, today)));
            return (r, ActivityStats.Timeline(r[timelinePeriod].Item3, today, unit, buckets));
        });
        if (version != _periodVersion) return;
        _stats = results[period].Item1;
        ApplyPeriodStats(_stats, period);
        _hoursStats = results[Effective(HoursPeriod)].Item1;
        ApplyHours();
        ApplyWeekdays(results[Effective(WeekdayPeriod)].Item1);
        var fmt = unit == TimelineUnit.Month ? "MMM yyyy" : "MMM d";
        TimelineLabels = timeline.Select(t => t.Start.ToString(fmt, CultureInfo.CurrentCulture)).ToArray();
        Timeline = timeline.Select(t => t.Count).ToArray();
        TimelineUnitText = L(unit switch { TimelineUnit.Day => "Analytics_ByDay", TimelineUnit.Week => "Analytics_ByWeek", _ => "Analytics_ByMonth" });
        Fill(MostViewedArtists, results[Effective(ArtistsPeriod)].Item2.Artists, n => N(n));
        Fill(TopTags, results[Effective(TagsPeriod)].Item2.Tags, n => N(n));
        var types = results[Effective(TypesPeriod)];
        Fill(ContentTypes, types.Item2.Types, null, types.Item1.Views + types.Item1.ViewsR18);
        Fill(MostDownloadedArtists, results[Effective(DownloadedPeriod)].Item2.Downloaded, n => N(n));
        var jobTable = results[Effective(JobsPeriod)].Item2.Jobs;
        Fill(DownloadJobs, jobTable, null, jobTable.Sum(j => j.Item2));
    }

    private ActivityStats? _hoursStats;

    private void ApplyHours()
    {
        if ((_hoursStats ?? _stats) is not { } hs) return;
        Hours = HourMode switch { "views" => hs.HoursViews, "downloads" => hs.HoursDownloads, _ => hs.HoursAll };
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
            var dicHistory = _dicLibrary.History;
            var pvHistory = _pixivisionHistory.GetAll();
            List<ViewedHistoryEntry> recent;
            List<ViewedHistoryRepository.MostViewedArtwork> most;
            try { recent = await _historyRepository.GetRecentAsync(0, 12); } catch { recent = []; }
            try { most = await _historyRepository.GetMostViewedAsync(12); } catch { most = []; }
            var articleTimes = dicHistory.Select(d => d.Time.LocalDateTime).Concat(pvHistory.Select(p => p.ViewedAt.LocalDateTime)).ToList();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var (global, _, _) = await Task.Run(() => Compute(views, jobs, articleTimes, today, null));
            (_views, _jobs, _articleTimes) = (views, jobs, articleTimes);
            ApplyGlobal(global);
            await ApplyPeriodAsync();
            FillArtworks(RecentArtworks, recent.Select(e => (e, RelativeTime(e.ViewedAt))));
            ViewCountsAreNew = most.Count == 0 || most.Max(m => m.Views) <= 1;
            FillArtworks(MostViewedArtworks, most.Where(m => m.Views > 1).Select(m => (m.Entry, F("Analytics_ViewsCount", N(m.Views)))));
            RecentArticles.Clear();
            foreach (var a in dicHistory.Select(d => (Time: d.Time, Item: new AnalyticsArticle { Title = d.Title, Source = "Dic", Dic = d, Caption = RelativeTime(d.Time.UtcDateTime) }))
                         .Concat(pvHistory.Select(p => (Time: p.ViewedAt, Item: new AnalyticsArticle { Title = p.Title, Source = "pixivision", Pixivision = p.ToSummary(), Caption = RelativeTime(p.ViewedAt.UtcDateTime) })))
                         .OrderByDescending(x => x.Time).Take(8))
                RecentArticles.Add(a.Item);
        }
        finally
        {
            _loading = false;
        }
    }

    private sealed record Tables(
        List<(string, int)> Artists, List<(string, int)> Tags, List<(string, int)> Types,
        List<(string, int)> Downloaded, List<(string, int)> Jobs);

    private static DateTime TargetTime(DownloadJob job, DownloadTarget t) => (t.ProcessedAt ?? job.CompletedAt ?? job.CreatedAt).ToLocalTime();

    private static (ActivityStats, Tables, List<ActivityEvent>) Compute(List<ViewedHistoryRepository.ViewActivity> allViews, List<DownloadJob> allJobs,
        List<DateTime> articleTimes, DateOnly today, DateTime? cutoff)
    {
        var from = cutoff ?? DateTime.MinValue;
        var views = cutoff is null ? allViews : allViews.Where(v => v.ViewedAtUtc.ToLocalTime() >= from).ToList();
        var targets = allJobs.SelectMany(j => j.Targets.Where(t => t.Status == TargetStatus.Completed).Select(t => (Job: j, Target: t, At: TargetTime(j, t))))
            .Where(x => x.At >= from).ToList();
        var jobs = cutoff is null ? allJobs : allJobs.Where(j => j.CreatedAt.ToLocalTime() >= from).ToList();
        var events = new List<ActivityEvent>(views.Count + articleTimes.Count);
        foreach (var t in articleTimes) if (t >= from) events.Add(new ActivityEvent(t, ActivityKind.Article));
        foreach (var v in views)
            events.Add(new ActivityEvent(v.ViewedAtUtc.ToLocalTime(), v.XRestrict >= 1 ? ActivityKind.ViewR18 : ActivityKind.View));
        foreach (var x in targets)
        {
            var count = Math.Max(1, x.Target.DownloadedItems);
            for (var i = 0; i < count; i++) events.Add(new ActivityEvent(x.At, ActivityKind.Download));
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
        var downloaded = targets.Select(x => x.Target)
                .Select(t => (Name: !string.IsNullOrWhiteSpace(t.UserName) ? t.UserName!.Trim() : t.Type == TargetType.Artist ? ArtistName(t.Name) : "",
                    Items: Math.Max(1, t.DownloadedItems)))
            .Where(t => t.Name.Length > 0)
            .GroupBy(t => t.Name).Select(g => (g.Key, g.Sum(t => t.Items)))
            .OrderByDescending(t => t.Item2).Take(8).ToList();
        var jobRows = jobs.GroupBy(j => j.Status).Select(g => (StatusName(g.Key), g.Count())).OrderByDescending(x => x.Item2).ToList();
        return (stats, new Tables(Top(views.Select(v => v.UserName)), Top(views.SelectMany(v => v.Tags)), types, downloaded, jobRows), events);
    }

    private void FillArtworks(ObservableCollection<AnalyticsArtwork> target, IEnumerable<(ViewedHistoryEntry Entry, string Caption)> rows)
    {
        target.Clear();
        var blur = _settings.Current.BlurR18Content;
        foreach (var (e, caption) in rows)
        {
            var preview = new ArtworkPreview
            {
                Id = e.ArtworkId, Title = e.Title, UserId = e.UserId, UserName = e.UserName, ThumbnailUrl = e.ThumbnailUrl,
                IllustType = e.IllustType, XRestrict = e.XRestrict, PageCount = e.PageCount, Tags = e.Tags,
            };
            if (_settings.Current.IsArtworkHidden("Viewed", preview.UserId, preview.UserName, preview.Title, preview.Tags)) continue;
            var card = new ArtworkCardViewModel(preview) { IsBlurred = blur && e.XRestrict >= 1 };
            target.Add(new AnalyticsArtwork(card, caption));
            if (!string.IsNullOrEmpty(e.ThumbnailUrl)) _ = card.LoadThumbnailAsync(_imageLoader);
        }
    }

    public void OpenArtwork(AnalyticsArtwork item, IEnumerable<AnalyticsArtwork> list)
    {
        if (item.Card.IsR18 && item.Card.IsBlurred) { item.Card.IsBlurred = false; return; }
        var nav = list.Select(a => a.Card).ToList();
        var gallery = Pikura.Avalonia.Services.AppServices.Get<GalleryViewModel>();
        gallery.OpenInViewer(item.Card, nav);
    }

    private static string RelativeTime(DateTime utc)
    {
        var ago = DateTime.UtcNow - (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc);
        if (ago.TotalMinutes < 1) return L("Analytics_JustNow");
        if (ago.TotalHours < 1) return F("Analytics_MinutesAgo", (int)ago.TotalMinutes);
        if (ago.TotalDays < 1) return F("Analytics_HoursAgo", (int)ago.TotalHours);
        if (ago.TotalDays < 30) return F("Analytics_DaysAgo", (int)ago.TotalDays);
        return utc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
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

    private void ApplyGlobal(ActivityStats s)
    {
        Subtitle = s.FirstDay is { } f ? F("Analytics_CountingSince", f.ToString("MMM d, yyyy", CultureInfo.CurrentCulture), N(s.DaysTracked)) : L("Analytics_NoDataYet");
        TodayText = N(s.Today);
        TodaySub = F("Analytics_TodaySub", N(s.TodayViews), N(s.TodayDownloads), N(s.TodayArticles));
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
    }

    private void ApplyPeriodStats(ActivityStats s, string period)
    {
        TotalTitle = $"{L("Analytics_Total")} · {PeriodName(period)}";
        TotalText = N(s.Total);
        var weeks = Math.Max(1, s.DaysTracked / 7.0);
        TotalSub = s.Total == 0 ? "" : F("Analytics_PerWeek", N(Math.Round(s.Total / weeks)));
        TotalSegments = [s.Views, s.ViewsR18, s.Downloads, s.Articles];
        ArticlesLegend = $"{L("Analytics_ArticlesRead")}  {N(s.Articles)}  {Pct(s.Articles * 100.0 / Math.Max(1, s.Total))}";
        var total = Math.Max(1, s.Total);
        ViewsLegend = $"{L("Analytics_Views")}  {N(s.Views)}  {Pct(s.Views * 100.0 / total)}";
        R18Legend = $"{L("Analytics_ViewsR18")}  {N(s.ViewsR18)}  {Pct(s.ViewsR18 * 100.0 / total)}";
        DownloadsLegend = $"{L("Analytics_Downloads")}  {N(s.Downloads)}  {Pct(s.Downloads * 100.0 / total)}";
    }

    private void ApplyWeekdays(ActivityStats s)
    {
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        var sum = Math.Max(1, s.Weekdays.Sum());
        var maxDay = s.Weekdays.Max();
        Weekdays.Clear();
        for (var d = 0; d < 7; d++)
            Weekdays.Add(new StatRow { Name = names[d], Count = s.Weekdays[d], Percent = s.Weekdays[d] * 100.0 / sum, ValueText = Pct(s.Weekdays[d] * 100.0 / sum), IsTop = maxDay > 0 && s.Weekdays[d] == maxDay });
        BusiestWeekdayText = maxDay == 0 ? "" : F("Analytics_BusiestOn", CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)Array.IndexOf(s.Weekdays, maxDay)));
    }
}

/// <summary>A chart or table's own period; index 0 follows the page-wide period.</summary>
public sealed partial class SectionPeriod : ObservableObject
{
    private static readonly string?[] Codes = [null, "7d", "30d", "6m", "1y", "5y", "all"];
    private static readonly string[] Keys = ["Analytics_PeriodPage", "Analytics_Period7d", "Analytics_Period30d", "Analytics_Period6m",
        "Analytics_Period1y", "Analytics_Period5y", "Analytics_PeriodAll"];
    public string[] Options => Keys.Select(k => LocalizationService.Instance[k]).ToArray();
    [ObservableProperty] private int _index;
    public string? Override => Codes[Math.Clamp(Index, 0, Codes.Length - 1)];
    public event Action? Changed;
    partial void OnIndexChanged(int value) => Changed?.Invoke();
}
