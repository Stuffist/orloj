using System.Collections.Concurrent;
using Olomoc.Server.Hosting;
using Olomoc.Tournaments.Stats;
using Olomoc.Zones;

namespace Olomoc.Server.Zones;

/// <summary>
/// Statistics of each zone, and of all the zones together (hidden zones excluded): computed on
/// demand, kept in memory, computed again after each change of a zone's games (or of a zone). After
/// a reset of the statistics (<see cref="StatsReset"/>), only the games entered afterwards count,
/// dominances included (the zone keeps its games). Those of a period (<see cref="StatsPeriod"/>):
/// on demand.
/// </summary>
public sealed class ZoneStats(ZoneGameStore games, ZoneStore zones, SiteSettingsStore settings)
{
    private readonly ConcurrentDictionary<string, StatsView> _zones = new();
    private StatsView? _all;
    private int _version;       // a computation that overlapped a change is not kept
    private DateTimeOffset? _since;     // reset taken into account by the cached figures

    private DateTimeOffset? Since
    {
        get
        {
            var since = settings.Current.StatsSince;
            if (since != _since) InvalidateAll(since);
            return since;
        }
    }

    public async Task<StatsView> OfZoneAsync(string code)
    {
        var since = Since;
        if (_zones.TryGetValue(code, out var cached)) return cached;
        var version = _version;
        var list = (await games.AllAsync(code)).Where(g => StatsReset.Counts(g.CreatedAt, since)).Select(g => g.Game.ToStatGame()).ToList();
        var stats = StatsView.OfGames(1, list) with { Since = since };
        if (version == _version) _zones[code] = stats;
        return stats;
    }

    public async Task<StatsView> AllAsync(StatsPeriod? period = null)
    {
        var since = Since;
        var all = period is null or { IsAll: true };
        if (all && _all is { } cached) return cached;
        var version = _version;
        var list = (await CountedAsync(since)).Where(x => all || period!.Contains(x.Game.Game.PlayedOn)).ToList();
        var stats = StatsView.OfGames(list.Select(x => x.Zone).Distinct().Count(), list.Select(x => x.Game.Game.ToStatGame()).ToList())
            with { Since = since };
        if (all && version == _version) _all = stats;
        return stats;
    }

    /// <summary>
    /// For the statistics of everything: the games of the visible zones (since the reset, in the
    /// period), without those copied from a tournament in <paramref name="archived"/> (counted in its
    /// archive), and the number of zones that have games. Kept while neither the zones' games nor the
    /// archives change (a period: computed on demand).
    /// </summary>
    public async Task<(int Zones, IReadOnlyList<StatGame> Games)> OutsideArchivesAsync(IReadOnlySet<string> archived, StatsPeriod? period = null)
    {
        var since = Since;
        var all = period is null or { IsAll: true };
        if (all && _outside is { } cached && ReferenceEquals(cached.Archived, archived)) return (cached.Zones, cached.Games);
        var version = _version;
        var list = (await CountedAsync(since))
            .Where(x => (all || period!.Contains(x.Game.Game.PlayedOn)) && (x.Game.TournamentCode is not { } code || !archived.Contains(code)))
            .ToList();
        var result = new Outside(archived, list.Select(x => x.Zone).Distinct().Count(), list.Select(x => x.Game.Game.ToStatGame()).ToList());
        if (all && version == _version) _outside = result;
        return (result.Zones, result.Games);
    }

    private sealed record Outside(IReadOnlySet<string> Archived, int Zones, IReadOnlyList<StatGame> Games);

    private Outside? _outside;

    // The games of the visible zones entered since the reset.
    private async Task<List<(string Zone, StoredZoneGame Game)>> CountedAsync(DateTimeOffset? since)
    {
        var counted = (await zones.ListAsync()).Where(z => !z.Hidden).Select(z => z.Code).ToHashSet();
        return (await games.AllWithZoneAsync()).Where(x => counted.Contains(x.Zone) && StatsReset.Counts(x.Game.CreatedAt, since)).ToList();
    }

    public async Task<Dominance> DominanceAsync(Zone zone)
    {
        return ZoneDominance.Compute(await OfZoneAsync(zone.Code), zone.Dominance);
    }

    /// <summary>A zone's games (or the zone itself) changed.</summary>
    public void Invalidate(string code)
    {
        Interlocked.Increment(ref _version);
        _zones.TryRemove(code, out _);
        _all = null;
        _outside = null;
    }

    // The statistics were reset (or the reset cancelled): everything computed again.
    private void InvalidateAll(DateTimeOffset? since)
    {
        Interlocked.Increment(ref _version);
        _since = since;
        _zones.Clear();
        _all = null;
        _outside = null;
    }
}
