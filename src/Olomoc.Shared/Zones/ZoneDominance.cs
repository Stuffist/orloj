using Olomoc.Tournaments.Stats;

namespace Olomoc.Zones;

/// <summary>
/// Dominance of a zone: the leading faction (null: none yet, or a tie, <see cref="Tie"/>).
/// <see cref="Games"/>: games recorded in the zone, and <see cref="Mode"/>, to explain an absence of
/// dominance (win rate: no faction has enough games yet; <see cref="MostPlayed"/>: games of the
/// most played faction).
/// </summary>
public sealed record Dominance(string? Faction, bool Tie, int Games = 0, DominanceMode Mode = DominanceMode.Wins, int MostPlayed = 0)
{
    public static readonly Dominance None = new(null, false);

    /// <summary>Win rate mode, no faction with <see cref="StatsView.MinGames"/> games yet.</summary>
    public bool NeedsMoreGames => Faction is null && !Tie && Mode == DominanceMode.WinRate && MostPlayed is > 0 and < StatsView.MinGames;
}

/// <summary>
/// Which faction dominates a zone, according to its mode (each player counts, see <see cref="GameStats"/>):
/// <list type="bullet">
/// <item><see cref="DominanceMode.Wins"/>: the most games won;</item>
/// <item><see cref="DominanceMode.WinRate"/>: the best win rate, among those with at least
/// <see cref="StatsView.MinGames"/> games;</item>
/// <item><see cref="DominanceMode.VictoryPoints"/>: the most VP scored.</item>
/// </list>
/// </summary>
public static class ZoneDominance
{
    public static Dominance Compute(StatsView stats, DominanceMode mode)
    {
        var mostPlayed = stats.FactionWins.Select(w => w.Games).DefaultIfEmpty(0).Max();
        var (faction, tie) = Leader(stats.FactionWins, mode);
        return new(faction, tie, stats.Games, mode, mostPlayed);
    }

    /// <summary>Value compared for the mode; null: does not take part (too few games).</summary>
    public static double? Score(WinRate w, DominanceMode mode) => mode switch
    {
        DominanceMode.Wins => w.Wins,
        DominanceMode.VictoryPoints => w.Vp,
        _ => w.Games >= StatsView.MinGames ? w.Percent : null,
    };

    // Best score (strictly positive); a tie at the top: no leader.
    private static (string? Key, bool Tie) Leader(IEnumerable<WinRate> rates, DominanceMode mode)
    {
        var scored = rates.Select(w => (w.Key, Score: Score(w, mode))).Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ToList();
        if (scored.Count == 0) return (null, false);
        if (scored.Count > 1 && scored[1].Score == scored[0].Score) return (null, true);
        return (scored[0].Key, false);
    }
}
