using System.Collections.Concurrent;
using Olomoc.Server.Tournaments;
using Olomoc.Tournaments.Stats;
using Olomoc.Zones;

namespace Olomoc.Server.Zones;

/// <summary>
/// Statistics of each zone, and of all the zones together (hidden zones excluded): computed on
/// demand, kept in memory, computed again after each change of a zone's games (or of a zone).
/// </summary>
public sealed class ZoneStats(ZoneGameStore games, ZoneStore zones, ServerCatalog catalog)
{
    private readonly ConcurrentDictionary<string, StatsView> _zones = new();
    private StatsView? _all;
    private int _version;       // a computation that overlapped a change is not kept

    public async Task<StatsView> OfZoneAsync(string code)
    {
        if (_zones.TryGetValue(code, out var cached)) return cached;
        var version = _version;
        var cat = await catalog.GetAsync();
        var list = (await games.AllAsync(code)).Select(g => g.Game.ToStatGame()).ToList();
        var stats = StatsView.OfGames(1, list, cat.AlignmentOf);
        if (version == _version) _zones[code] = stats;
        return stats;
    }

    public async Task<StatsView> AllAsync()
    {
        if (_all is { } cached) return cached;
        var version = _version;
        var cat = await catalog.GetAsync();
        var counted = (await zones.ListAsync()).Where(z => !z.Hidden).Select(z => z.Code).ToHashSet();
        var list = (await games.AllWithZoneAsync()).Where(x => counted.Contains(x.Zone)).ToList();
        var stats = StatsView.OfGames(list.Select(x => x.Zone).Distinct().Count(), list.Select(x => x.Game.Game.ToStatGame()).ToList(), cat.AlignmentOf);
        if (version == _version) _all = stats;
        return stats;
    }

    /// <summary>
    /// For the statistics of everything: the games of the visible zones, without those copied from
    /// a tournament in <paramref name="archived"/> (counted in its archive), and the number of
    /// zones that have games. Kept while neither the zones' games nor the archives change.
    /// </summary>
    public async Task<(int Zones, IReadOnlyList<StatGame> Games)> OutsideArchivesAsync(IReadOnlySet<string> archived)
    {
        if (_outside is { } cached && ReferenceEquals(cached.Archived, archived)) return (cached.Zones, cached.Games);
        var version = _version;
        var counted = (await zones.ListAsync()).Where(z => !z.Hidden).Select(z => z.Code).ToHashSet();
        var list = (await games.AllWithZoneAsync())
            .Where(x => counted.Contains(x.Zone) && (x.Game.TournamentCode is not { } code || !archived.Contains(code)))
            .ToList();
        var result = new Outside(archived, list.Select(x => x.Zone).Distinct().Count(), list.Select(x => x.Game.Game.ToStatGame()).ToList());
        if (version == _version) _outside = result;
        return (result.Zones, result.Games);
    }

    private sealed record Outside(IReadOnlySet<string> Archived, int Zones, IReadOnlyList<StatGame> Games);

    private Outside? _outside;

    public async Task<Dominance> DominanceAsync(Zone zone)
    {
        var cat = await catalog.GetAsync();
        return ZoneDominance.Compute(await OfZoneAsync(zone.Code), zone.Dominance, cat.AlignmentOf);
    }

    /// <summary>A zone's games (or the zone itself) changed.</summary>
    public void Invalidate(string code)
    {
        Interlocked.Increment(ref _version);
        _zones.TryRemove(code, out _);
        _all = null;
        _outside = null;
    }
}
