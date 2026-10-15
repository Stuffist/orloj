namespace Olomoc.Tournaments.Stats;

/// <summary>Part d'un élément (faction, rouage) : Count sur Total.</summary>
public sealed record Share(string Key, int Count, int Total)
{
    public double Percent => Total == 0 ? 0 : 100.0 * Count / Total;
}

/// <summary>Bilan des parties d'un élément (faction, rouage de rang 3) : victoires sur parties.</summary>
public sealed record WinRate(string Key, int Games, int Wins, int Draws, int Losses)
{
    public double Percent => Games == 0 ? 0 : 100.0 * Wins / Games;
}

/// <summary>Parties jouées sur un scénario (agenda, ou <see cref="Agendas.Other"/>), et bilan de J1 quand il est noté.</summary>
public sealed record ScenarioStat(string Key, int Games, int Total, WinRate First)
{
    public double Percent => Total == 0 ? 0 : 100.0 * Games / Total;
}

/// <summary>Rouages choisis dans les cabales inscrites d'une faction.</summary>
public sealed record FactionPicks(string Faction, int Cabals, IReadOnlyList<Share> Cogs);

public sealed record StatsView(
    int Tournaments, int Players, int Games,
    IReadOnlyList<Share> Factions, IReadOnlyList<FactionPicks> Picks,
    IReadOnlyList<WinRate> FactionWins, IReadOnlyList<WinRate> Rank3Wins,
    WinRate FirstPlayer, IReadOnlyList<Share> Ends, IReadOnlyList<ScenarioStat> Scenarios)
{
    /// <summary>En dessous, un taux de victoire est peu significatif (affiché, mais signalé).</summary>
    public const int MinGames = 10;

    public static readonly StatsView Empty = TournamentStats.Compute(Array.Empty<ArchivedTournament>());
}

/// <summary>
/// Statistiques des tournois archivés :
/// <list type="bullet">
/// <item>factions jouées : part des joueurs ;</item>
/// <item>choix des rouages : pour chaque faction, part des cabales inscrites qui contiennent le rouage
/// (les deux cabales d'un joueur comptent) ;</item>
/// <item>victoires : chaque camp d'une partie compte (un affrontement de même faction, des deux côtés) ;
/// par faction, et par rouage de rang 3 de la cabale jouée, quand elle est connue ;</item>
/// <item>parties où c'est noté : victoires de J1, causes de fin de partie, scénarios joués (et
/// victoires de J1 par scénario).</item>
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

        // Chaque camp d'une partie : faction, rouage de rang 3 de la cabale jouée (si connue), PV pour / contre.
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
        var withScenario = games.Where(g => g.Scenario is not null).ToList();
        var scenarios = withScenario.GroupBy(g => g.Scenario!)
            .Select(g => new ScenarioStat(g.Key, g.Count(), withScenario.Count, FirstRate(g.Key, g)))
            .OrderByDescending(s => s.Games).ThenBy(s => Agendas.All.ToList().IndexOf(s.Key) is var i && i < 0 ? int.MaxValue : i).ToList();

        return new StatsView(
            list.Count, players.Count, games.Count,
            factions, picks,
            Rates(sides, s => s.Faction),
            Rates(sides.Where(s => s.Rank3 is not null), s => s.Rank3!),
            FirstRate("J1", games), ends, scenarios);
    }

    // Bilan de J1 sur les parties où il est noté : victoire, nul ou défaite du joueur qui a commencé.
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
