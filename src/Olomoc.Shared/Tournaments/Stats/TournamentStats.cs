using Olomoc.Models;

namespace Olomoc.Tournaments.Stats;

/// <summary>Share of an item (faction, cog): Count out of Total.</summary>
public sealed record Share(string Key, int Count, int Total)
{
    public double Percent => Total == 0 ? 0 : 100.0 * Count / Total;
}

/// <summary>Game results of an item (faction, rank 3 cog, alignment): wins out of games, VP scored.</summary>
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
/// <see cref="Players"/> registered), or of zones (<see cref="Tournaments"/> = number of zones,
/// <see cref="Players"/> = participations in the games).
/// </summary>
public sealed record StatsView(
    int Tournaments, int Players, int Games,
    IReadOnlyList<Share> Factions, IReadOnlyList<FactionPicks> Picks,
    IReadOnlyList<WinRate> FactionWins, IReadOnlyList<WinRate> Rank3Wins, IReadOnlyList<WinRate> AlignmentWins,
    WinRate FirstPlayer, IReadOnlyList<Share> Ends, IReadOnlyList<ScenarioStat> Scenarios,
    IReadOnlyList<ScenarioStat> Artifacts, IReadOnlyList<ScenarioStat> Ministries)
{
    /// <summary>Below this, a win rate is not very meaningful (shown, but flagged).</summary>
    public const int MinGames = 10;

    public static readonly StatsView Empty = TournamentStats.Compute(Array.Empty<ArchivedTournament>());

    /// <summary>
    /// Statistics of games played outside a tournament (zones): factions and cog choices counted
    /// on the participations and on the cabals played.
    /// </summary>
    public static StatsView OfGames(int zones, IReadOnlyCollection<StatGame> games, Func<string, Alignment?>? alignmentOf)
    {
        var players = games.SelectMany(g => g.A.Players.Concat(g.B.Players)).ToList();
        var factions = players.GroupBy(p => p.Faction)
            .Select(g => new Share(g.Key, g.Count(), players.Count))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Key).ToList();
        var picks = players.Where(p => p.Cabal is not null).GroupBy(p => p.Faction)
            .Select(g => PicksOf(g.Key, g.Select(p => p.Cabal!).ToList()))
            .OrderByDescending(p => p.Cabals).ThenBy(p => p.Faction).ToList();
        var f = GameStats.Compute(games, alignmentOf);
        return new StatsView(zones, players.Count, games.Count, factions, picks,
            f.FactionWins, f.Rank3Wins, f.AlignmentWins, f.FirstPlayer, f.Ends, f.Scenarios, f.Artifacts, f.Ministries);
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
/// cabal played when it is known, per alignment (with <paramref name="alignmentOf"/>); P1, ends,
/// scenarios, decks.</item>
/// </list>
/// </summary>
public static class TournamentStats
{
    public static StatsView Compute(IEnumerable<ArchivedTournament> tournaments, Func<string, Alignment?>? alignmentOf = null)
    {
        var list = tournaments.ToList();
        var players = list.SelectMany(t => t.Players).ToList();

        var factions = players.GroupBy(p => p.Faction)
            .Select(g => new Share(g.Key, g.Count(), players.Count))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Key).ToList();

        var picks = players.GroupBy(p => p.Faction)
            .Select(g => StatsView.PicksOf(g.Key, g.SelectMany(p => p.Cabals).ToList()))
            .OrderByDescending(p => p.Cabals).ThenBy(p => p.Faction).ToList();

        var games = list.SelectMany(t => t.Games.Select(g => new StatGame(
            Side(t, g.A, g.CabalA, g.VpA), Side(t, g.B, g.CabalB, g.VpB), g.First, g.End, g.Scenario, g.Artifact, g.Ministry))).ToList();
        var f = GameStats.Compute(games, alignmentOf);

        return new StatsView(
            list.Count, players.Count, games.Count,
            factions, picks,
            f.FactionWins, f.Rank3Wins, f.AlignmentWins,
            f.FirstPlayer, f.Ends, f.Scenarios, f.Artifacts, f.Ministries);
    }

    // A side of a tournament game: the player, with the cabal played (if known).
    private static StatSide Side(ArchivedTournament t, int player, int? cabal, int vp)
    {
        var p = t.Players[player];
        var played = cabal is { } i && i >= 0 && i < p.Cabals.Count ? p.Cabals[i] : null;
        return new StatSide([new StatPlayer(p.Faction, played)], vp);
    }
}
