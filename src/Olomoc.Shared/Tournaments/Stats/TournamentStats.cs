namespace Olomoc.Tournaments.Stats;

/// <summary>Share of an item (faction, cog): Count out of Total.</summary>
public sealed record Share(string Key, int Count, int Total)
{
    public double Percent => Total == 0 ? 0 : 100.0 * Count / Total;
}

/// <summary>Game results of an item (faction, rank 3 cog): wins out of games.</summary>
public sealed record WinRate(string Key, int Games, int Wins, int Draws, int Losses)
{
    public double Percent => Games == 0 ? 0 : 100.0 * Wins / Games;
}

/// <summary>
/// Games played on a scenario (agenda, or <see cref="Agendas.Other"/>) or with a deck (Artifact,
/// Ministry), and P1's results when recorded.
/// </summary>
public sealed record ScenarioStat(string Key, int Games, int Total, WinRate First)
{
    public double Percent => Total == 0 ? 0 : 100.0 * Games / Total;
}

/// <summary>Cogs chosen in a faction's registered cabals.</summary>
public sealed record FactionPicks(string Faction, int Cabals, IReadOnlyList<Share> Cogs);

public sealed record StatsView(
    int Tournaments, int Players, int Games,
    IReadOnlyList<Share> Factions, IReadOnlyList<FactionPicks> Picks,
    IReadOnlyList<WinRate> FactionWins, IReadOnlyList<WinRate> Rank3Wins,
    WinRate FirstPlayer, IReadOnlyList<Share> Ends, IReadOnlyList<ScenarioStat> Scenarios,
    IReadOnlyList<ScenarioStat> Artifacts, IReadOnlyList<ScenarioStat> Ministries)
{
    /// <summary>Below this, a win rate is not very meaningful (shown, but flagged).</summary>
    public const int MinGames = 10;

    public static readonly StatsView Empty = TournamentStats.Compute(Array.Empty<ArchivedTournament>());
}

/// <summary>
/// Statistics of the archived tournaments:
/// <list type="bullet">
/// <item>factions played: share of the players;</item>
/// <item>cog choices: for each faction, share of the registered cabals that contain the cog (both
/// of a player's cabals count);</item>
/// <item>wins: each side of a game counts (a mirror match, on both sides); per faction, and per
/// rank 3 cog of the cabal played, when it is known;</item>
/// <item>games where it is recorded: P1 wins, causes of the end of the game, scenarios played (and
/// P1 wins per scenario), Artifact and Ministry decks played (likewise).</item>
/// </list>
/// </summary>
public static class TournamentStats
{
    public static StatsView Compute(IEnumerable<ArchivedTournament> tournaments)
    {
        var list = tournaments.ToList();
        var players = list.SelectMany(t => t.Players).ToList();

        var factions = players.GroupBy(p => p.Faction)
            .Select(g => new Share(g.Key, g.Count(), players.Count))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Key).ToList();

        var picks = players.GroupBy(p => p.Faction).Select(g =>
        {
            var cabals = g.SelectMany(p => p.Cabals).ToList();
            var cogs = cabals.SelectMany(c => c.Select(s => s.Cog).Distinct())
                .GroupBy(id => id)
                .Select(x => new Share(x.Key, x.Count(), cabals.Count))
                .OrderByDescending(s => s.Count).ThenBy(s => s.Key).ToList();
            return new FactionPicks(g.Key, cabals.Count, cogs);
        }).OrderByDescending(p => p.Cabals).ThenBy(p => p.Faction).ToList();

        // Each side of a game: faction, rank 3 cog of the cabal played (if known), VP for / against.
        var sides = list.SelectMany(t => t.Games.SelectMany(g => new[]
        {
            Side(t, g.A, g.CabalA, g.VpA, g.VpB),
            Side(t, g.B, g.CabalB, g.VpB, g.VpA),
        })).ToList();

        var games = list.SelectMany(t => t.Games).ToList();
        var ended = games.Where(g => g.End is not null).ToList();
        var ends = ended.GroupBy(g => g.End!.Value)
            .Select(g => new Share(g.Key.ToString(), g.Count(), ended.Count))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Key).ToList();

        return new StatsView(
            list.Count, players.Count, games.Count,
            factions, picks,
            Rates(sides, s => s.Faction),
            Rates(sides.Where(s => s.Rank3 is not null), s => s.Rank3!),
            FirstRate("J1", games), ends,
            ByKey(games, g => g.Scenario, Agendas.All),
            ByKey(games, g => g.Artifact, Decks.Artifacts),
            ByKey(games, g => g.Ministry, Decks.Ministries));
    }

    // Games where the key is recorded, grouped by key: most played first, then in the reference order.
    private static IReadOnlyList<ScenarioStat> ByKey(IEnumerable<ArchivedGame> games, Func<ArchivedGame, string?> key, IReadOnlyList<string> order)
    {
        var noted = games.Where(g => key(g) is not null).ToList();
        return noted.GroupBy(g => key(g)!)
            .Select(g => new ScenarioStat(g.Key, g.Count(), noted.Count, FirstRate(g.Key, g)))
            .OrderByDescending(s => s.Games).ThenBy(s => order.ToList().IndexOf(s.Key) is var i && i < 0 ? int.MaxValue : i).ToList();
    }

    // P1's results on the games where it is recorded: win, draw or loss of the player who started.
    private static WinRate FirstRate(string key, IEnumerable<ArchivedGame> games)
    {
        var known = games.Where(g => g.First is not null)
            .Select(g => g.First == TableSide.A ? g.VpA.CompareTo(g.VpB) : g.VpB.CompareTo(g.VpA)).ToList();
        return new WinRate(key, known.Count, known.Count(c => c > 0), known.Count(c => c == 0), known.Count(c => c < 0));
    }

    private sealed record SideResult(string Faction, string? Rank3, int For, int Against);

    private static SideResult Side(ArchivedTournament t, int player, int? cabal, int vpFor, int vpAgainst)
    {
        var p = t.Players[player];
        var played = cabal is { } i && i >= 0 && i < p.Cabals.Count ? p.Cabals[i] : null;
        return new(p.Faction, played?.FirstOrDefault(s => s.Rank == 3)?.Cog, vpFor, vpAgainst);
    }

    private static IReadOnlyList<WinRate> Rates(IEnumerable<SideResult> sides, Func<SideResult, string> key) =>
        sides.GroupBy(key)
            .Select(g => new WinRate(g.Key, g.Count(), g.Count(s => s.For > s.Against), g.Count(s => s.For == s.Against), g.Count(s => s.For < s.Against)))
            .OrderByDescending(r => r.Percent).ThenByDescending(r => r.Games).ThenBy(r => r.Key).ToList();
}
