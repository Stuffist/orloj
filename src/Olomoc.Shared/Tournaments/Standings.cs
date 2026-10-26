namespace Olomoc.Tournaments;

/// <summary>A player's line in the standings.</summary>
public sealed record Standing(
    string PlayerId, int Rank, int Points, int Diff, int VpFor,
    int Played, int Wins, int Draws, int Losses, int Byes);

/// <summary>
/// Standings (round sheet): tournament points (win 3, draw 1, loss 0), then cumulative VP
/// difference, then VP scored. A bye is worth a 7-0 win: 3 points, +7 difference, 7 VP scored.
/// Only tables whose result is entered count. Players who dropped come after all those still in,
/// ranked among themselves the same way.
/// </summary>
public static class Standings
{
    public const int WinPoints = 3, DrawPoints = 1, ByePoints = 3, ByeVictoryPoints = 7;

    public static IReadOnlyList<Standing> Compute(Tournament t)
    {
        var lines = t.Players.ToDictionary(p => p.Id, p => new Line(p.Id, p.Dropped));
        foreach (var table in t.Rounds.SelectMany(r => r.Tables).Where(x => x.HasResult))
        {
            if (table.IsBye)
            {
                if (lines.TryGetValue(table.PlayerA, out var alone)) alone.Bye();
                continue;
            }
            if (lines.TryGetValue(table.PlayerA, out var a)) a.Game(table.VpA!.Value, table.VpB!.Value);
            if (lines.TryGetValue(table.PlayerB!, out var b)) b.Game(table.VpB!.Value, table.VpA!.Value);
        }

        var order = t.Players.Select((p, i) => (lines[p.Id], i))
            .OrderBy(x => x.Item1.Dropped).ThenByDescending(x => x.Item1.Points).ThenByDescending(x => x.Item1.Diff)
            .ThenByDescending(x => x.Item1.VpFor).ThenBy(x => x.i)       // perfect tie: registration order
            .Select(x => x.Item1).ToList();

        var result = new List<Standing>(order.Count);
        for (var i = 0; i < order.Count; i++)
        {
            var l = order[i];
            // Tie on the three criteria (between players still in, or between drops): same rank (1, 2, 2, 4).
            var rank = i > 0 && l.SameScore(order[i - 1]) ? result[i - 1].Rank : i + 1;
            result.Add(new Standing(l.Id, rank, l.Points, l.Diff, l.VpFor, l.Played, l.Wins, l.Draws, l.Losses, l.Byes));
        }
        return result;
    }

    private sealed class Line(string id, bool dropped)
    {
        public string Id => id;
        public bool Dropped => dropped;
        public int Points, Diff, VpFor, Played, Wins, Draws, Losses, Byes;

        public void Bye() { Byes++; Wins++; Played++; Points += ByePoints; VpFor += ByeVictoryPoints; Diff += ByeVictoryPoints; }

        public void Game(int vpFor, int vpAgainst)
        {
            Played++;
            VpFor += vpFor;
            Diff += vpFor - vpAgainst;
            if (vpFor > vpAgainst) { Wins++; Points += WinPoints; }
            else if (vpFor == vpAgainst) { Draws++; Points += DrawPoints; }
            else Losses++;
        }

        public bool SameScore(Line o) => Dropped == o.Dropped && Points == o.Points && Diff == o.Diff && VpFor == o.VpFor;
    }
}
