namespace Olomoc.Tournaments;

/// <summary>Paired round, and the compromises that had to be made for lack of anything better.</summary>
public sealed record PairingOutcome(Round Round, int Rematches, int Mirrors);

/// <summary>
/// Pairing of the next round of an event, between the active players (drops excluded):
/// <list type="bullet">
/// <item><b>Tournament</b> (Swiss rounds): at random among the players with the same points (round 1:
/// everyone), from the highest points group down; a player left over plays the closest points
/// still available. No rematch; mirror matches allowed.</item>
/// <item><b>Dystopian Encounter</b>: at random every round, whatever the points; no rematch and no
/// mirror match (two players of the same faction).</item>
/// </list>
/// <para>If the rules cannot all be kept (backtracking search), the mirror match is allowed first,
/// then the rematch, and the compromises are reported.</para>
/// A bye if the number is odd: a player who has not had one yet, the lowest-ranked (tournament) or
/// drawn at random (Encounter).
/// </summary>
public static class Pairer
{
    private const int SearchBudget = 200_000;     // search steps per attempt; beyond that, the next compromise

    public static PairingOutcome Pair(Tournament t, int seed)
    {
        var rng = new Random(seed);
        var active = t.ActivePlayers.Select(p => p.Id).ToList();
        var standing = Standings.Compute(t).ToDictionary(s => s.PlayerId);
        var faction = t.ActivePlayers.ToDictionary(p => p.Id, p => p.Faction);
        var random = active.ToDictionary(id => id, _ => rng.Next());

        var met = new HashSet<(string, string)>();
        var hadBye = new HashSet<string>();
        foreach (var table in t.Rounds.SelectMany(r => r.Tables))
        {
            if (table.IsBye) hadBye.Add(table.PlayerA);
            else { met.Add((table.PlayerA, table.PlayerB!)); met.Add((table.PlayerB!, table.PlayerA)); }
        }

        bool Mirror(string a, string b) => faction[a] == faction[b];
        bool Rematch(string a, string b) => met.Contains((a, b));

        // From the first to the last of the standings (ties broken at random): for the bye.
        var ranking = active.OrderByDescending(id => standing[id].Points).ThenByDescending(id => standing[id].Diff)
                            .ThenByDescending(id => standing[id].VpFor).ThenBy(id => random[id]).ToList();

        // Processing order: each one, in turn, chooses their opponent (the first one possible in this
        // order, after the rules). Swiss: by points groups, at random within a group; Encounter: at random.
        var swiss = t.Format == EventFormat.Tournament;
        var order = swiss
            ? active.OrderByDescending(id => standing[id].Points).ThenBy(id => random[id]).ToList()
            : active.OrderBy(id => random[id]).ToList();
        var position = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        // Bye: whoever has not had one first, from the bottom of the standings (Encounter: at random).
        var rankPosition = ranking.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var byeCandidates = active.Count % 2 == 0
            ? new List<string?> { null }
            : active.OrderBy(id => hadBye.Contains(id)).ThenByDescending(id => swiss ? rankPosition[id] : random[id]).Cast<string?>().ToList();

        (int, int, int, int) Key(string p, string q) =>
            (Rematch(p, q) ? 1 : 0, !swiss && Mirror(p, q) ? 1 : 0, swiss ? Math.Abs(standing[p].Points - standing[q].Points) : 0, position[q]);

        // Successive compromises: everything kept, then mirror matches (Encounter), then rematches.
        var attempts = swiss ? new[] { (true, false), (true, true) } : new[] { (false, false), (true, false), (true, true) };
        foreach (var (allowMirror, allowRematch) in attempts)
        {
            bool Allowed(string p, string q) => (allowRematch || !Rematch(p, q)) && (allowMirror || !Mirror(p, q));
            var budget = SearchBudget;
            foreach (var bye in byeCandidates)
            {
                var players = order.Where(id => id != bye).ToList();
                var pairs = new List<(string A, string B)>();
                if (Search(players, new HashSet<string>(), pairs, Allowed, Key, ref budget))
                    return Outcome(t, pairs, bye, Rematch, (a, b) => !swiss && Mirror(a, b));
                if (budget < 0) break;
            }
        }
        throw new InvalidOperationException("Pairing impossible.");   // unreachable: everything allowed
    }

    // Backtracking: the first free player in the order tries their possible opponents, from the
    // preferred to the least preferred, until a complete pairing.
    private static bool Search(List<string> order, HashSet<string> used, List<(string, string)> pairs,
        Func<string, string, bool> allowed, Func<string, string, (int, int, int, int)> key, ref int budget)
    {
        var p = order.FirstOrDefault(id => !used.Contains(id));
        if (p is null) return true;
        if (--budget < 0) return false;
        used.Add(p);
        foreach (var q in order.Where(q => !used.Contains(q) && allowed(p, q)).OrderBy(q => key(p, q)).ToList())
        {
            used.Add(q);
            pairs.Add((p, q));
            if (Search(order, used, pairs, allowed, key, ref budget)) return true;
            pairs.RemoveAt(pairs.Count - 1);
            used.Remove(q);
            if (budget < 0) break;
        }
        used.Remove(p);
        return false;
    }

    private static PairingOutcome Outcome(Tournament t, List<(string A, string B)> pairs, string? bye,
        Func<string, string, bool> rematch, Func<string, string, bool> mirror)
    {
        var round = new Round { Number = t.Rounds.Count + 1 };
        foreach (var (a, b) in pairs)
            round.Tables.Add(new Table { Number = round.Tables.Count + 1, PlayerA = a, PlayerB = b });
        if (bye is not null)
            round.Tables.Add(new Table { Number = round.Tables.Count + 1, PlayerA = bye });
        return new PairingOutcome(round, pairs.Count(x => rematch(x.A, x.B)), pairs.Count(x => mirror(x.A, x.B)));
    }
}
