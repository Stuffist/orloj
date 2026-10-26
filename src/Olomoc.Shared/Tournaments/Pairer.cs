using Olomoc.Models;

namespace Olomoc.Tournaments;

/// <summary>Paired round, and the compromises that had to be made for lack of anything better.</summary>
public sealed record PairingOutcome(Round Round, int Rematches, int SameAlignment);

/// <summary>
/// Pairing of the next round, between the active players (drops excluded).
/// <list type="bullet">
/// <item><b>Swiss</b>: round 1 at random; then by standings, each player against the closest one
/// in points still available (1 v. 2, 3 v. 4…), going down to the next points group if needed.
/// Option: for an equal points gap, prefer the opposite alignment.</item>
/// <item><b>Alignment</b>: Light against Darkness, at random; the surplus of the larger side
/// plays among itself.</item>
/// </list>
/// In both cases, no rematch (backtracking search); if that is impossible, the rematch is allowed
/// and reported. A bye if the number is odd: the lowest-ranked player who has not had one yet (in
/// Alignment mode, in the larger side).
/// </summary>
public static class Pairer
{
    private const int SearchBudget = 200_000;     // search steps; beyond that, rematches allowed

    public static PairingOutcome Pair(Tournament t, Func<string, Alignment?> alignmentOf, int seed)
    {
        var rng = new Random(seed);
        var active = t.ActivePlayers.Select(p => p.Id).ToList();
        var standing = Standings.Compute(t).ToDictionary(s => s.PlayerId);
        var alignment = t.ActivePlayers.ToDictionary(p => p.Id, p => alignmentOf(p.Faction));
        var random = active.ToDictionary(id => id, _ => rng.Next());

        var met = new HashSet<(string, string)>();
        var hadBye = new HashSet<string>();
        foreach (var table in t.Rounds.SelectMany(r => r.Tables))
        {
            if (table.IsBye) hadBye.Add(table.PlayerA);
            else { met.Add((table.PlayerA, table.PlayerB!)); met.Add((table.PlayerB!, table.PlayerA)); }
        }

        bool SameAlignment(string a, string b) => alignment[a] is { } x && alignment[b] == x;
        int CampSize(string id) => alignment[id] is { } a ? active.Count(o => alignment[o] == a) : 0;

        // From the first to the last of the standings (ties broken at random).
        var ranking = active.OrderByDescending(id => standing[id].Points).ThenByDescending(id => standing[id].Diff)
                            .ThenByDescending(id => standing[id].VpFor).ThenBy(id => random[id]).ToList();

        // Processing order (each one, in turn, chooses their opponent) and preference between opponents.
        List<string> order;
        Func<string, string, (int, int)> preference;
        if (t.Pairing == PairingMode.Swiss)
        {
            order = t.Rounds.Count == 0 ? active.OrderBy(id => random[id]).ToList() : ranking;
            preference = (p, q) => (Math.Abs(standing[p].Points - standing[q].Points),
                                    t.AvoidSameAlignment && SameAlignment(p, q) ? 1 : 0);
        }
        else
        {
            // Smaller side first: each of its players takes an opponent from the other side.
            order = active.OrderBy(id => alignment[id] is null ? int.MaxValue : CampSize(id)).ThenBy(id => random[id]).ToList();
            preference = (p, q) => (SameAlignment(p, q) ? 1 : 0, 0);
        }
        var position = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        // Bye: whoever has not had one first, (Alignment) in the larger side, from the bottom of the standings.
        var rankPosition = ranking.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var byeCandidates = active.Count % 2 == 0
            ? new List<string?> { null }
            : active.OrderBy(id => hadBye.Contains(id))
                    .ThenByDescending(id => t.Pairing == PairingMode.Alignment ? CampSize(id) : 0)
                    .ThenByDescending(id => rankPosition[id])
                    .Cast<string?>().ToList();

        foreach (var allowRematch in new[] { false, true })
        {
            var budget = SearchBudget;
            foreach (var bye in byeCandidates)
            {
                var players = order.Where(id => id != bye).ToList();
                var pairs = new List<(string A, string B)>();
                if (Search(players, new HashSet<string>(), pairs, Allowed, Key, ref budget))
                    return Outcome(t, pairs, bye, met, SameAlignment);
                if (budget < 0) break;
            }

            bool Allowed(string p, string q) => allowRematch || !met.Contains((p, q));
            (int, int, int, int) Key(string p, string q)
            {
                var (first, second) = preference(p, q);
                return (met.Contains((p, q)) ? 1 : 0, first, second, position[q]);
            }
        }
        throw new InvalidOperationException("Pairing impossible.");   // unreachable: rematches allowed
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
        HashSet<(string, string)> met, Func<string, string, bool> sameAlignment)
    {
        var round = new Round { Number = t.Rounds.Count + 1 };
        foreach (var (a, b) in pairs)
            round.Tables.Add(new Table { Number = round.Tables.Count + 1, PlayerA = a, PlayerB = b });
        if (bye is not null)
            round.Tables.Add(new Table { Number = round.Tables.Count + 1, PlayerA = bye });
        return new PairingOutcome(round, pairs.Count(x => met.Contains((x.A, x.B))), pairs.Count(x => sameAlignment(x.A, x.B)));
    }
}
