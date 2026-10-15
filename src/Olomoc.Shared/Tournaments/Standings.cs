namespace Olomoc.Tournaments;

/// <summary>Ligne du classement d'un joueur.</summary>
public sealed record Standing(
    string PlayerId, int Rank, int Points, int Diff, int VpFor,
    int Played, int Wins, int Draws, int Losses, int Byes);

/// <summary>
/// Classement (feuille de ronde) : points de tournoi (victoire 3, nul 1, défaite 0), puis
/// différence de PV cumulée, puis PV marqués. Un exempt (bye) vaut une victoire au Fiel 7-0 :
/// 3 points, +7 de différence, 7 PV marqués. Seules les tables dont le résultat est saisi comptent.
/// </summary>
public static class Standings
{
    public const int WinPoints = 3, DrawPoints = 1, ByePoints = 3, ByeVictoryPoints = 7;

    public static IReadOnlyList<Standing> Compute(Tournament t)
    {
        var lines = t.Players.ToDictionary(p => p.Id, p => new Line(p.Id));
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
            .OrderByDescending(x => x.Item1.Points).ThenByDescending(x => x.Item1.Diff)
            .ThenByDescending(x => x.Item1.VpFor).ThenBy(x => x.i)       // égalité parfaite : ordre d'inscription
            .Select(x => x.Item1).ToList();

        var result = new List<Standing>(order.Count);
        for (var i = 0; i < order.Count; i++)
        {
            var l = order[i];
            // Égalité sur les trois critères : même rang (1, 2, 2, 4).
            var rank = i > 0 && l.SameScore(order[i - 1]) ? result[i - 1].Rank : i + 1;
            result.Add(new Standing(l.Id, rank, l.Points, l.Diff, l.VpFor, l.Played, l.Wins, l.Draws, l.Losses, l.Byes));
        }
        return result;
    }

    private sealed class Line(string id)
    {
        public string Id => id;
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

        public bool SameScore(Line o) => Points == o.Points && Diff == o.Diff && VpFor == o.VpFor;
    }
}
