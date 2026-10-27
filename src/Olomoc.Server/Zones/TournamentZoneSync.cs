using System.Collections.Concurrent;
using Olomoc.Server.Tournaments;
using Olomoc.Zones;

namespace Olomoc.Server.Zones;

/// <summary>
/// Copies the results of a tournament bound to a zone into that zone (see <see cref="TournamentZone"/>),
/// after each change that affects them: result entered or cleared, tournament counted in the
/// statistics or not, deleted. A closed zone is no longer changed. One synchronization at a time per
/// tournament, always from its latest state.
/// </summary>
public sealed class TournamentZoneSync(TournamentStore tournaments, ZoneStore zones, ZoneGameStore games, ZoneStats stats,
    ILogger<TournamentZoneSync> log)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>The tournament's games in its zone, as they are now (nothing if it has no zone).</summary>
    public Task SyncAsync(string code) => RunAsync(code, async () =>
    {
        if (await tournaments.LoadAsync(code) is not { Zone: { } zone } t) return;
        await ApplyAsync(zone, t.Code, TournamentZone.Games(t));
    });

    /// <summary>Tournament deleted: its games leave the zone (unless the zone is closed).</summary>
    public Task RemoveAsync(string code, string? zone) => zone is null ? Task.CompletedTask
        : RunAsync(code, () => ApplyAsync(zone, code.ToUpperInvariant(), new Dictionary<int, ZoneGame>()));

    private async Task ApplyAsync(string zone, string tournament, IReadOnlyDictionary<int, ZoneGame> wanted)
    {
        if (await zones.LoadAsync(zone) is not { } z || z.StatusOn(DateOnly.FromDateTime(DateTime.UtcNow)) == ZoneStatus.Closed) return;
        if (await games.SyncTournamentAsync(z.Code, tournament, wanted)) stats.Invalidate(z.Code);
    }

    // The tournament's own result stays saved even if copying fails: logged, caught up at the next change.
    private async Task RunAsync(string code, Func<Task> work)
    {
        var gate = _locks.GetOrAdd(code.ToUpperInvariant(), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await work(); }
        catch (Exception e) { log.LogError(e, "Tournament {Code}: copy of the results into its zone failed.", code); }
        finally { gate.Release(); }
    }
}
