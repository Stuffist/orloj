using Olomoc.Online;
using Olomoc.Server.Tournaments;
using Olomoc.Server.Zones;
using Olomoc.Tournaments;
using Olomoc.Zones;

namespace Olomoc.Server.Online;

/// <summary>
/// Results of the online games, into the statistics: after a confirmation (hub), or when a finished
/// game nobody confirmed is purged (<see cref="OnlinePurge"/>).
/// <list type="bullet">
/// <item>free game: a duel in the zone of the Block that receives the online games (open; none: nothing
/// recorded, tried again at the next confirmation or at the purge), replaced at each confirmation;</item>
/// <item>tournament game: VP, P1 and end written on its table, if the organizer did not enter a result
/// (it then counts through the tournament's archive, never twice).</item>
/// </list>
/// </summary>
public sealed class OnlineResults(OnlineStore store, ZoneStore zones, ZoneGameStore games, ZoneStats stats,
    TournamentStore tournaments, TournamentZoneSync sync, ILogger<OnlineResults> log)
{
    /// <summary>Records the confirmed result of the game, and marks it recorded.</summary>
    public async Task RecordAsync(string code)
    {
        if (await store.LoadAsync(code) is not { Result: { } result } game) return;
        if (!await RecordAsync(game, result)) return;
        await store.UpdateAsync(code, g =>
        {
            if (g.Result is not { } r || r.ConfirmedAt != result.ConfirmedAt || r.Recorded) return (false, g);
            g.Result = r with { Recorded = true };
            return (true, g);
        });
    }

    /// <summary>
    /// Before the purge deletes it: a finished game whose result is not recorded yet (confirmed or
    /// not: then from the sheets). The game is not changed (it is about to be deleted).
    /// </summary>
    public async Task RecordBeforePurgeAsync(OnlineGame game, DateTimeOffset now)
    {
        if (game.Result is { Recorded: true } || !OnlineRules.Ended(game)) return;
        await RecordAsync(game, game.Result ?? OnlineRules.ResultFromSheets(game, now));
    }

    // Whether the result is now where it belongs (nothing more to do).
    private async Task<bool> RecordAsync(OnlineGame game, OnlineGameResult result)
    {
        if (game.Tournament is { } tr) return await ToTournamentAsync(game, tr, result);
        if (OnlineRules.ZoneGameOf(game, result) is not { } zoneGame) return true;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if ((await zones.ListAsync()).FirstOrDefault(z => z.ReceivesOnline && z.StatusOn(today) == ZoneStatus.Open) is not { } zone)
        {
            log.LogInformation("Online game {Code}: no open zone receives the online games, result not recorded.", game.Code);
            return false;
        }
        foreach (var changed in await games.SyncOnlineAsync(game.Code, zone.Code, zoneGame)) stats.Invalidate(changed);
        return true;
    }

    private async Task<bool> ToTournamentAsync(OnlineGame game, OnlineTournamentRef tr, OnlineGameResult result)
    {
        var written = await tournaments.UpdateAsync(tr.Code, t =>
        {
            var table = t.Rounds.FirstOrDefault(r => r.Number == tr.Round)?.Tables.FirstOrDefault(x => x.Number == tr.Table);
            if (table is null || table.IsBye || table.HasResult || t.Phase == TournamentPhase.Finished) return (false, "kept");
            var a = game.Seats.FindIndex(s => s.TournamentPlayerId == table.PlayerA);
            var b = game.Seats.FindIndex(s => s.TournamentPlayerId == table.PlayerB);
            if (a < 0 || b < 0) return (false, "kept");
            TableSide? first = game.First == a ? TableSide.A : game.First == b ? TableSide.B : null;
            var problem = TournamentRules.SetResult(t, tr.Round, tr.Table, result.Vp[a], result.Vp[b], table.CabalA, table.CabalB, first, result.End);
            return problem is null ? (true, "written") : (false, "kept");
        });
        if (written == "written") await sync.SyncAsync(tr.Code);
        return true;
    }
}
