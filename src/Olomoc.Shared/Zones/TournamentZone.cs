using Olomoc.Tournaments;

namespace Olomoc.Zones;

/// <summary>
/// Results of a tournament bound to a zone, as games of that zone: one duel per table with a result
/// (byes excluded), dated on the tournament's day (or its creation), with the factions, the cabal
/// played (if known: chosen with two cabals per player, the only one otherwise), P1, the end of the
/// game and the round's scenario. Without the players' names: the zone keeps its games after the
/// tournament is purged. None if the tournament is excluded from the statistics.
/// </summary>
public static class TournamentZone
{
    /// <summary>Key of a table: round × 1000 + table.</summary>
    public static int Key(int round, int table) => round * 1000 + table;

    public static Dictionary<int, ZoneGame> Games(Tournament t)
    {
        var games = new Dictionary<int, ZoneGame>();
        if (t.ExcludeFromStats) return games;
        var date = t.Date ?? DateOnly.FromDateTime(t.CreatedAt.UtcDateTime);
        foreach (var round in t.Rounds)
            foreach (var table in round.Tables)
            {
                if (table.IsBye || table is not { VpA: { } vpA, VpB: { } vpB }) continue;
                if (t.PlayerById(table.PlayerA) is not { } a || t.PlayerById(table.PlayerB!) is not { } b) continue;
                games[Key(round.Number, table.Number)] = new ZoneGame(GameFormat.Duel, date,
                    new ZoneSide([Player(a, table.CabalA)], vpA), new ZoneSide([Player(b, table.CabalB)], vpB),
                    table.First, table.End, t.ScenarioOf(round.Number));
            }
        return games;
    }

    private static ZonePlayer Player(Player p, int? cabal) =>
        new(p.Faction, null, cabal is { } i && i >= 0 && i < p.Cabals.Count ? p.Cabals[i] : p.Cabals.Count == 1 ? p.Cabals[0] : null);
}
