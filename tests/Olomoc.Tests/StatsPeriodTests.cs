using Olomoc.Tournaments.Stats;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Period of the statistics (/stats/?from=&amp;to=) and their reset.</summary>
public class StatsPeriodTests
{
    private static readonly DateOnly Day = new(2026, 6, 15);

    [Fact]
    public void Bounds_included_and_optional()
    {
        Assert.True(StatsPeriod.All.IsAll);
        Assert.True(StatsPeriod.All.Contains(Day));
        var june = new StatsPeriod(new(2026, 6, 1), new(2026, 6, 30));
        Assert.True(june.Contains(new DateOnly(2026, 6, 1)) && june.Contains(new DateOnly(2026, 6, 30)));
        Assert.False(june.Contains(new DateOnly(2026, 7, 1)) || june.Contains(new DateOnly(2026, 5, 31)));
        Assert.True(new StatsPeriod(From: Day).Contains(new DateOnly(2030, 1, 1)));
        Assert.False(new StatsPeriod(To: Day).Contains(Day.AddDays(1)));
    }

    [Fact]
    public void Query_read_back()
    {
        Assert.Equal("", StatsPeriod.All.Query);
        Assert.Equal("from=2026-06-15", new StatsPeriod(From: Day).Query);
        Assert.Equal("from=2026-06-01&to=2026-06-15", new StatsPeriod(new(2026, 6, 1), Day).Query);
        Assert.Equal(Day, StatsPeriod.Parse("2026-06-15"));
        Assert.Null(StatsPeriod.Parse("15/06/2026"));
        Assert.Null(StatsPeriod.Parse(null));
    }

    [Fact]
    public void A_tournament_counts_on_its_date_otherwise_the_day_it_finished()
    {
        var finished = new DateTimeOffset(2026, 7, 2, 18, 0, 0, TimeSpan.Zero);
        ArchivedTournament T(DateOnly? date) => new(ArchivedTournament.CurrentVersion, "ABCDEF", date, finished, 1, [], []);
        Assert.Equal(Day, StatsPeriod.DayOf(T(Day)));
        Assert.Equal(new DateOnly(2026, 7, 2), StatsPeriod.DayOf(T(null)));
        Assert.False(new StatsPeriod(To: new(2026, 6, 30)).Contains(T(null)));
    }

    [Fact]
    public void After_a_reset_only_what_is_recorded_afterwards_counts()
    {
        var reset = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.True(StatsReset.Counts(reset.AddYears(-3), null));
        Assert.False(StatsReset.Counts(reset.AddSeconds(-1), reset));
        Assert.True(StatsReset.Counts(reset, reset));
    }
}
