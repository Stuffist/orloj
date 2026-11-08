using Olomoc.Server.Zones;
using Olomoc.Tournaments.Stats;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Statistics of everything (/api/stats/all): the finished and counted tournaments (archives) and the
/// games of the visible zones, a zone game copied from an archived tournament counted once (in the
/// tournament). Kept while neither the archives nor the zones' games change.
/// </summary>
public sealed class AllStats(StatsArchive archive, ZoneStats zones, ServerCatalog catalog)
{
    private sealed record Cached(object Archives, object Games, StatsView Stats);

    private Cached? _cached;            // an object: replaced in one write

    public async Task<StatsView> GetAsync()
    {
        var archives = archive.CurrentArchives();
        var (zoneCount, games) = await zones.OutsideArchivesAsync(archives.Codes);
        if (_cached is { } c && ReferenceEquals(c.Archives, archives) && ReferenceEquals(c.Games, games)) return c.Stats;
        var cat = await catalog.GetAsync();
        var stats = StatsView.Combine(archives.Tournaments, games.ToList(), zoneCount, cat.AlignmentOf);
        _cached = new Cached(archives, games, stats);
        return stats;
    }
}
