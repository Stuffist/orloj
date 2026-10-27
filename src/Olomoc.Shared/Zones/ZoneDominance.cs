using Olomoc.Models;
using Olomoc.Tournaments.Stats;

namespace Olomoc.Zones;

/// <summary>
/// Dominance of a zone: the leading alignment (null: none yet, or a tie, <see cref="Tie"/>), and the
/// leading faction of that alignment (null if tied, or none). <see cref="Games"/>: Light against
/// Darkness games counted, and <see cref="Mode"/>, to explain an absence of dominance (win rate:
/// not enough games yet).
/// </summary>
public sealed record Dominance(Alignment? Alignment, string? Faction, bool Tie, int Games = 0, DominanceMode Mode = DominanceMode.Wins)
{
    public static readonly Dominance None = new(null, null, false);

    /// <summary>Win rate mode below <see cref="StatsView.MinGames"/> Light against Darkness games.</summary>
    public bool NeedsMoreGames => Alignment is null && !Tie && Mode == DominanceMode.WinRate && Games is > 0 and < StatsView.MinGames;
}

/// <summary>
/// Who dominates a zone, according to its mode:
/// <list type="bullet">
/// <item><see cref="DominanceMode.Wins"/>: the most games won;</item>
/// <item><see cref="DominanceMode.WinRate"/>: the best win rate, among those with at least
/// <see cref="StatsView.MinGames"/> games;</item>
/// <item><see cref="DominanceMode.VictoryPoints"/>: the most VP scored.</item>
/// </list>
/// The alignment comes from the Light against Darkness games; the faction is then the leading one of
/// that alignment (each player counts, see <see cref="GameStats"/>).
/// </summary>
public static class ZoneDominance
{
    public static Dominance Compute(StatsView stats, DominanceMode mode, Func<string, Alignment?> alignmentOf)
    {
        var games = stats.AlignmentWins.FirstOrDefault()?.Games ?? 0;      // each side of these games: the same number
        var (alignmentKey, tie) = Leader(stats.AlignmentWins, mode);
        if (alignmentKey is null || !Enum.TryParse<Alignment>(alignmentKey, out var alignment)) return new(null, null, tie, games, mode);
        var (faction, _) = Leader(stats.FactionWins.Where(w => alignmentOf(w.Key) == alignment), mode);
        return new(alignment, faction, false, games, mode);
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
