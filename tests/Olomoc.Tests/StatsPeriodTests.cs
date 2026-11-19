using Olomoc.Tournaments.Stats;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Period of the statistics (/stats/?from=&amp;to=) and their reset.</summary>
public class StatsPeriodTests
{
    [Fact]
    public void Bounds_included_and_optional()
    {
        var june = new StatsPeriod(new(2026, 6, 1), new(2026, 6, 30));
        Assert.True(june.Contains(new DateOnly(2026, 6, 1)) && june.Contains(new DateOnly(2026, 6, 30)));
        Assert.False(june.Contains(new DateOnly(2026, 7, 1)) || june.Contains(new DateOnly(2026, 5, 31)));
        Assert.True(StatsPeriod.All.Contains(new DateOnly(2030, 1, 1)));
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
