using Olomoc.Models;

namespace Olomoc.Tournaments.Stats;

/// <summary>Share of an item (faction, cog): Count out of Total.</summary>
public sealed record Share(string Key, int Count, int Total)
{
    public double Percent => Total == 0 ? 0 : 100.0 * Count / Total;
}

/// <summary>Game results of an item (faction, rank 3 cog): wins out of games, VP scored.</summary>
public sealed record WinRate(string Key, int Games, int Wins, int Draws, int Losses, int Vp = 0)
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

/// <summary>Cogs chosen in a faction's cabals (registered in a tournament, played in a zone).</summary>
public sealed record FactionPicks(string Faction, int Cabals, IReadOnlyList<Share> Cogs);

/// <summary>
/// Statistics shown on a page: of the tournaments (<see cref="Tournaments"/> archives,
/// <see cref="Players"/> registered), of zones (<see cref="Tournaments"/> = number of zones,
/// <see cref="Players"/> = participations in the games), or of both together
/// (<see cref="Combine"/>: <see cref="Tournaments"/> archives and <see cref="Zones"/> zones,
/// <see cref="Players"/> = registered + participations). <see cref="Since"/>: the statistics were
/// reset at that moment (<see cref="StatsReset"/>), only what was recorded afterwards counts.
/// </summary>
public sealed record StatsView(
    int Tournaments, int Players, int Games,
    IReadOnlyList<Share> Factions, IReadOnlyList<FactionPicks> Picks,
    IReadOnlyList<WinRate> FactionWins, IReadOnlyList<WinRate> Rank3Wins,
    WinRate FirstPlayer, IReadOnlyList<Share> Ends, IReadOnlyList<ScenarioStat> Scenarios,
    IReadOnlyList<ScenarioStat> Artifacts, IReadOnlyList<ScenarioStat> Ministries, int Zones = 0, DateTimeOffset? Since = null)
{
    /// <summary>Below this, a win rate is not very meaningful (shown, but flagged).</summary>
    public const int MinGames = 10;

    public static readonly StatsView Empty = TournamentStats.Compute(Array.Empty<ArchivedTournament>());

    /// <summary>
    /// Statistics of games played outside a tournament (zones): factions and cog choices counted
    /// on the participations and on the cabals played.
    /// </summary>
    public static StatsView OfGames(int zones, IReadOnlyCollection<StatGame> games)
    {
        var players = games.SelectMany(g => g.A.Players.Concat(g.B.Players)).ToList();
        var factions = players.GroupBy(p => p.Faction)
            .Select(g => new Share(g.Key, g.Count(), players.Count))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Key).ToList();
        var picks = players.Where(p => p.Cabal is not null).GroupBy(p => p.Faction)
            .Select(g => PicksOf(g.Key, g.Select(p => p.Cabal!).ToList()))
            .OrderByDescending(p => p.Cabals).ThenBy(p => p.Faction).ToList();
        var f = GameStats.Compute(games);
        return new StatsView(zones, players.Count, games.Count, factions, picks,
            f.FactionWins, f.Rank3Wins, f.FirstPlayer, f.Ends, f.Scenarios, f.Artifacts, f.Ministries);
    }

    /// <summary>
    /// The tournaments and the games outside them (zones) together: factions counted on the
    /// registered players and the participations, cog choices on the registered cabals and the
    /// cabals played, the figures of the games on all of them. The caller leaves out the zone games
    /// copied from an archived tournament (counted once, in the tournament).
    /// </summary>
    public static StatsView Combine(IReadOnlyCollection<ArchivedTournament> tournaments, IReadOnlyCollection<StatGame> zoneGames, int zones)
    {
        var registered = tournaments.SelectMany(t => t.Players).ToList();
        var participations = zoneGames.SelectMany(g => g.A.Players.Concat(g.B.Players)).ToList();
        var factionsPlayed = registered.Select(p => p.Faction).Concat(participations.Select(p => p.Faction)).ToList();
        var factions = factionsPlayed.GroupBy(f => f)
            .Select(g => new Share(g.Key, g.Count(), factionsPlayed.Count))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Key).ToList();
        var cabals = registered.SelectMany(p => p.Cabals.Select(c => (p.Faction, Cabal: c)))
            .Concat(participations.Where(p => p.Cabal is not null).Select(p => (p.Faction, Cabal: p.Cabal!)));
        var picks = cabals.GroupBy(c => c.Faction)
            .Select(g => PicksOf(g.Key, g.Select(c => c.Cabal).ToList()))
            .OrderByDescending(p => p.Cabals).ThenBy(p => p.Faction).ToList();
        var games = TournamentStats.Games(tournaments).Concat(zoneGames).ToList();
        var f = GameStats.Compute(games);
        return new StatsView(tournaments.Count, factionsPlayed.Count, games.Count, factions, picks,
            f.FactionWins, f.Rank3Wins, f.FirstPlayer, f.Ends, f.Scenarios, f.Artifacts, f.Ministries, zones);
    }

    /// <summary>Share of the cabals that contain each cog.</summary>
    public static FactionPicks PicksOf(string faction, IReadOnlyList<IReadOnlyList<ArchivedSlot>> cabals) =>
        new(faction, cabals.Count, cabals.SelectMany(c => c.Select(s => s.Cog).Distinct())
            .GroupBy(id => id)
            .Select(x => new Share(x.Key, x.Count(), cabals.Count))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Key).ToList());
}

/// <summary>
/// Statistics of the archived tournaments:
/// <list type="bullet">
/// <item>factions played: share of the players;</item>
/// <item>cog choices: for each faction, share of the registered cabals that contain the cog (both
/// of a player's cabals count);</item>
/// <item>the figures of the games (<see cref="GameStats"/>): wins per faction, per rank 3 cog of the
/// cabal played when it is known; P1, ends, scenarios, decks.</item>
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

        var picks = players.GroupBy(p => p.Faction)
            .Select(g => StatsView.PicksOf(g.Key, g.SelectMany(p => p.Cabals).ToList()))
            .OrderByDescending(p => p.Cabals).ThenBy(p => p.Faction).ToList();

        var games = Games(list);
        var f = GameStats.Compute(games);

        return new StatsView(
            list.Count, players.Count, games.Count,
            factions, picks,
            f.FactionWins, f.Rank3Wins,
            f.FirstPlayer, f.Ends, f.Scenarios, f.Artifacts, f.Ministries);
    }

    /// <summary>The archived games, in the neutral form of the statistics.</summary>
    public static List<StatGame> Games(IEnumerable<ArchivedTournament> tournaments) =>
        tournaments.SelectMany(t => t.Games.Select(g => new StatGame(
            Side(t, g.A, g.CabalA, g.VpA), Side(t, g.B, g.CabalB, g.VpB), g.First, g.End, g.Scenario, g.Artifact, g.Ministry))).ToList();

    // A side of a tournament game: the player, with the cabal played (if known).
    private static StatSide Side(ArchivedTournament t, int player, int? cabal, int vp)
    {
        var p = t.Players[player];
        var played = cabal is { } i && i >= 0 && i < p.Cabals.Count ? p.Cabals[i] : null;
        return new StatSide([new StatPlayer(p.Faction, played)], vp);
    }
}
