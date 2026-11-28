namespace Pikura.Core.Services;

public enum ActivityKind { View, ViewR18, Download }

public readonly record struct ActivityEvent(DateTime Local, ActivityKind Kind);

/// <summary>Totals, streaks and day/hour distributions for the Analytics page.</summary>
public sealed class ActivityStats
{
    public int Total { get; private init; }
    public int Views { get; private init; }
    public int ViewsR18 { get; private init; }
    public int Downloads { get; private init; }
    public int Today { get; private init; }
    public int TodayViews { get; private init; }
    public int TodayDownloads { get; private init; }
    public int Past7 { get; private init; }
    public int Prev7 { get; private init; }
    public int CurrentStreak { get; private init; }
    public int LongestStreak { get; private init; }
    public DateOnly? BusiestDay { get; private init; }
    public int BusiestDayCount { get; private init; }
    public DateOnly? FirstDay { get; private init; }
    public int DaysTracked { get; private init; }
    public int ActiveDays { get; private init; }
    public IReadOnlyDictionary<DateOnly, int> Daily { get; private init; } = new Dictionary<DateOnly, int>();
    public int[] HoursAll { get; private init; } = new int[24];
    public int[] HoursViews { get; private init; } = new int[24];
    public int[] HoursDownloads { get; private init; } = new int[24];
    public int[] Weekdays { get; private init; } = new int[7];

    public static ActivityStats Compute(IEnumerable<ActivityEvent> events, DateOnly today)
    {
        var list = events.ToList();
        var daily = new Dictionary<DateOnly, int>();
        int[] all = new int[24], views = new int[24], downloads = new int[24], weekdays = new int[7];
        foreach (var e in list)
        {
            var d = DateOnly.FromDateTime(e.Local);
            daily[d] = daily.GetValueOrDefault(d) + 1;
            all[e.Local.Hour]++;
            (e.Kind == ActivityKind.Download ? downloads : views)[e.Local.Hour]++;
            weekdays[(int)e.Local.DayOfWeek]++;
        }

        var longest = 0;
        var run = 0;
        DateOnly? prev = null;
        foreach (var d in daily.Keys.Order())
        {
            run = prev is { } p && p.AddDays(1) == d ? run + 1 : 1;
            longest = Math.Max(longest, run);
            prev = d;
        }
        var current = 0;
        var cursor = daily.ContainsKey(today) ? today : today.AddDays(-1);
        while (daily.ContainsKey(cursor)) { current++; cursor = cursor.AddDays(-1); }

        int Range(int from, int to) => daily.Where(kv => kv.Key > today.AddDays(-to) && kv.Key <= today.AddDays(-from)).Sum(kv => kv.Value);
        var busiest = daily.Count == 0 ? (KeyValuePair<DateOnly, int>?)null : daily.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First();
        DateOnly? first = daily.Count == 0 ? null : daily.Keys.Min();
        var todayEvents = list.Where(e => DateOnly.FromDateTime(e.Local) == today).ToList();

        return new ActivityStats
        {
            Total = list.Count,
            Views = list.Count(e => e.Kind == ActivityKind.View),
            ViewsR18 = list.Count(e => e.Kind == ActivityKind.ViewR18),
            Downloads = list.Count(e => e.Kind == ActivityKind.Download),
            Today = todayEvents.Count,
            TodayViews = todayEvents.Count(e => e.Kind != ActivityKind.Download),
            TodayDownloads = todayEvents.Count(e => e.Kind == ActivityKind.Download),
            Past7 = Range(0, 7),
            Prev7 = Range(7, 14),
            CurrentStreak = current,
            LongestStreak = longest,
            BusiestDay = busiest?.Key,
            BusiestDayCount = busiest?.Value ?? 0,
            FirstDay = first,
            DaysTracked = first is { } f ? today.DayNumber - f.DayNumber + 1 : 0,
            ActiveDays = daily.Count,
            Daily = daily,
            HoursAll = all,
            HoursViews = views,
            HoursDownloads = downloads,
            Weekdays = weekdays,
        };
    }
}
