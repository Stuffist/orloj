using Olomoc.Server.Zones;
using Olomoc.Tournaments.Stats;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Statistics of everything (/api/stats/all): the finished and counted tournaments (archives) and the
/// games of the visible zones, a zone game copied from an archived tournament counted once (in the
/// tournament); since the reset of the statistics, if any. Kept while neither the archives nor the
/// zones' games change; those of a period (<see cref="StatsPeriod"/>) computed on demand.
/// </summary>
public sealed class AllStats(StatsArchive archive, ZoneStats zones)
{
    private sealed record Cached(object Archives, object Games, StatsView Stats);

    private Cached? _cached;            // an object: replaced in one write

    public async Task<StatsView> GetAsync(StatsPeriod? period = null)
    {
        var archives = archive.CountedArchives();
        if (period is { IsAll: false } p)
        {
            var (count, inPeriod) = await zones.OutsideArchivesAsync(archives.Codes, p);
            return StatsView.Combine(archives.Tournaments.Where(p.Contains).ToList(), inPeriod.ToList(), count) with { Since = archives.Since };
        }
        var (zoneCount, games) = await zones.OutsideArchivesAsync(archives.Codes);
        if (_cached is { } c && ReferenceEquals(c.Archives, archives) && ReferenceEquals(c.Games, games)) return c.Stats;
        var stats = StatsView.Combine(archives.Tournaments, games.ToList(), zoneCount) with { Since = archives.Since };
        _cached = new Cached(archives, games, stats);
        return stats;
    }
}
