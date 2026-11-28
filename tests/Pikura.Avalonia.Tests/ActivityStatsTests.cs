using Pikura.Core.Services;
using Xunit;

namespace Pikura.Avalonia.Tests;

public class ActivityStatsTests
{
    [Fact]
    public void Computes_Streaks_Ranges_And_Distributions()
    {
        var today = new DateOnly(2026, 11, 28);
        DateTime At(int daysAgo, int hour) => today.AddDays(-daysAgo).ToDateTime(new TimeOnly(hour, 0));
        var events = new List<ActivityEvent>
        {
            new(At(0, 18), ActivityKind.View), new(At(0, 18), ActivityKind.Download),
            new(At(1, 3), ActivityKind.ViewR18),
            new(At(2, 12), ActivityKind.View), new(At(2, 12), ActivityKind.View), new(At(2, 13), ActivityKind.View),
            new(At(10, 9), ActivityKind.View),
            new(At(11, 9), ActivityKind.View),
            new(At(12, 9), ActivityKind.View),
            new(At(13, 9), ActivityKind.View),
            new(At(0, 18), ActivityKind.Article),
        };
        var s = ActivityStats.Compute(events, today);
        Assert.Equal(11, s.Total);
        Assert.Equal(1, s.Articles);
        Assert.Equal(1, s.TodayArticles);
        Assert.Equal(1, s.TodayViews);
        Assert.Equal(1, s.ViewsR18);
        Assert.Equal(1, s.Downloads);
        Assert.Equal(3, s.Today);
        Assert.Equal(1, s.TodayDownloads);
        Assert.Equal(7, s.Past7);
        Assert.Equal(4, s.Prev7);
        Assert.Equal(3, s.CurrentStreak);
        Assert.Equal(4, s.LongestStreak);
        Assert.Equal(today, s.BusiestDay);
        Assert.Equal(3, s.BusiestDayCount);
        Assert.Equal(14, s.DaysTracked);
        Assert.Equal(7, s.ActiveDays);
        Assert.Equal(3, s.HoursAll[18]);
        Assert.Equal(1, s.HoursDownloads[18]);
        Assert.Equal(1, s.HoursViews[18]);
        Assert.Equal(3, s.Weekdays[(int)today.DayOfWeek]);
    }

    [Fact]
    public void Streak_Counts_From_Yesterday_When_Nothing_Today()
    {
        var today = new DateOnly(2026, 11, 28);
        var s = ActivityStats.Compute([new(today.AddDays(-1).ToDateTime(TimeOnly.MinValue), ActivityKind.View)], today);
        Assert.Equal(1, s.CurrentStreak);
        Assert.Equal(0, s.Today);
    }
}
