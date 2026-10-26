using Olomoc.Models;

namespace Olomoc.Tournaments.Stats;

// Games in a neutral form, for the figures shared by the tournaments and the zones: a side is one
// player (duel) or a team of two (2 v 2), with its VP.

/// <summary>A player of a side: faction, cards of the cabal played (if known).</summary>
public sealed record StatPlayer(string Faction, IReadOnlyList<ArchivedSlot>? Cabal);

public sealed record StatSide(IReadOnlyList<StatPlayer> Players, int Vp);

/// <summary>A game: both sides; if recorded: P1, cause of the end, scenario, decks.</summary>
public sealed record StatGame(StatSide A, StatSide B, TableSide? First = null, GameEnd? End = null,
    string? Scenario = null, string? Artifact = null, string? Ministry = null);

/// <summary>Figures computed on games, whatever their origin.</summary>
public sealed record GameFigures(
    IReadOnlyList<WinRate> FactionWins, IReadOnlyList<WinRate> Rank3Wins, IReadOnlyList<WinRate> AlignmentWins,
    WinRate FirstPlayer, IReadOnlyList<Share> Ends, IReadOnlyList<ScenarioStat> Scenarios,
    IReadOnlyList<ScenarioStat> Artifacts, IReadOnlyList<ScenarioStat> Ministries);

/// <summary>
/// Figures of a set of games:
/// <list type="bullet">
/// <item>wins per faction and per rank 3 cog: each player counts (a mirror match, on both sides; in
/// 2 v 2, both players of the winning team win);</item>
/// <item>wins per alignment: Light against Darkness games only, once per game (a game between two
/// factions of the same alignment says nothing about it);</item>
/// <item>games where it is recorded: P1 wins, causes of the end, scenarios and decks played.</item>
/// </list>
/// </summary>
public static class GameStats
{
    public static GameFigures Compute(IReadOnlyCollection<StatGame> games, Func<string, Alignment?>? alignmentOf = null)
    {
        var players = games.SelectMany(g => new[] { (Side: g.A, Other: g.B), (Side: g.B, Other: g.A) })
            .SelectMany(x => x.Side.Players.Select(p => new SideResult(p.Faction, p.Cabal?.FirstOrDefault(s => s.Rank == 3)?.Cog, x.Side.Vp, x.Other.Vp)))
            .ToList();

        var alignments = new List<SideResult>();
        if (alignmentOf is not null)
            foreach (var g in games)
            {
                if (AlignmentOf(g.A, alignmentOf) is not { } a || AlignmentOf(g.B, alignmentOf) is not { } b || a == b) continue;
                alignments.Add(new(a.ToString(), null, g.A.Vp, g.B.Vp));
                alignments.Add(new(b.ToString(), null, g.B.Vp, g.A.Vp));
            }

        var ended = games.Where(g => g.End is not null).ToList();
        var ends = ended.GroupBy(g => g.End!.Value)
            .Select(g => new Share(g.Key.ToString(), g.Count(), ended.Count))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Key).ToList();

        return new GameFigures(
            Rates(players, s => s.Key),
            Rates(players.Where(s => s.Rank3 is not null), s => s.Rank3!),
            Rates(alignments, s => s.Key),
            FirstRate("J1", games), ends,
            ByKey(games, g => g.Scenario, Agendas.All),
            ByKey(games, g => g.Artifact, Decks.Artifacts),
            ByKey(games, g => g.Ministry, Decks.Ministries));
    }

    /// <summary>Alignment of a side: that of its players, if they all share a known one.</summary>
    public static Alignment? AlignmentOf(StatSide side, Func<string, Alignment?> alignmentOf)
    {
        var all = side.Players.Select(p => alignmentOf(p.Faction)).Distinct().ToList();
        return all is [{ } single] ? single : null;
    }

    // Games where the key is recorded, grouped by key: most played first, then in the reference order.
    private static IReadOnlyList<ScenarioStat> ByKey(IEnumerable<StatGame> games, Func<StatGame, string?> key, IReadOnlyList<string> order)
    {
        var noted = games.Where(g => key(g) is not null).ToList();
        return noted.GroupBy(g => key(g)!)
            .Select(g => new ScenarioStat(g.Key, g.Count(), noted.Count, FirstRate(g.Key, g)))
            .OrderByDescending(s => s.Games).ThenBy(s => order.ToList().IndexOf(s.Key) is var i && i < 0 ? int.MaxValue : i).ToList();
    }

    // P1's results on the games where it is recorded: win, draw or loss of the side that started.
    private static WinRate FirstRate(string key, IEnumerable<StatGame> games)
    {
        var known = games.Where(g => g.First is not null)
            .Select(g => g.First == TableSide.A ? g.A.Vp.CompareTo(g.B.Vp) : g.B.Vp.CompareTo(g.A.Vp)).ToList();
        return new WinRate(key, known.Count, known.Count(c => c > 0), known.Count(c => c == 0), known.Count(c => c < 0));
    }

    // Key: faction, or alignment.
    private sealed record SideResult(string Key, string? Rank3, int For, int Against);

    private static IReadOnlyList<WinRate> Rates(IEnumerable<SideResult> sides, Func<SideResult, string> key) =>
        sides.GroupBy(key)
            .Select(g => new WinRate(g.Key, g.Count(), g.Count(s => s.For > s.Against), g.Count(s => s.For == s.Against),
                g.Count(s => s.For < s.Against), g.Sum(s => s.For)))
            .OrderByDescending(r => r.Percent).ThenByDescending(r => r.Games).ThenBy(r => r.Key).ToList();
}
