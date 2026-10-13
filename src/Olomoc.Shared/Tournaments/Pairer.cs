using Olomoc.Models;

namespace Olomoc.Tournaments;

/// <summary>Ronde appariée, et les entorses qu'il a fallu consentir faute de mieux.</summary>
public sealed record PairingOutcome(Round Round, int Rematches, int SameAlignment);

/// <summary>
/// Appariement de la ronde suivante, entre les joueurs actifs (hors abandons).
/// <list type="bullet">
/// <item><b>Suisse</b> : ronde 1 au hasard ; ensuite par classement, chaque joueur contre le
/// plus proche en points encore disponible (1 c. 2, 3 c. 4…), en descendant au groupe de
/// points suivant si besoin. Option : à écart de points égal, préférer l'alignement opposé.</item>
/// <item><b>Alignement</b> : Lumières contre Ténèbres, au hasard ; le surplus du camp le plus
/// nombreux s'affronte entre lui.</item>
/// </list>
/// Dans les deux cas, pas de revanche (recherche avec retour arrière) ; si c'est impossible,
/// la revanche est admise et signalée. Exempt (bye) si le nombre est impair : le joueur le
/// moins bien classé qui n'en a pas encore eu (en mode Alignement, dans le camp le plus nombreux).
/// </summary>
public static class Pairer
{
    private const int SearchBudget = 200_000;     // étapes de recherche ; au-delà, revanches admises

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

        // Du premier au dernier du classement (égalités départagées au hasard).
        var ranking = active.OrderByDescending(id => standing[id].Points).ThenByDescending(id => standing[id].Diff)
                            .ThenByDescending(id => standing[id].VpFor).ThenBy(id => random[id]).ToList();

        // Ordre de traitement (chacun, à son tour, choisit son adversaire) et préférence entre adversaires.
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
            // Camp le moins nombreux d'abord : chacun de ses joueurs prend un adversaire d'en face.
            order = active.OrderBy(id => alignment[id] is null ? int.MaxValue : CampSize(id)).ThenBy(id => random[id]).ToList();
            preference = (p, q) => (SameAlignment(p, q) ? 1 : 0, 0);
        }
        var position = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        // Exempt : qui n'en a pas eu d'abord, (Alignement) dans le camp le plus nombreux, du bas du classement.
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
        throw new InvalidOperationException("Appariement impossible.");   // inatteignable : revanches admises
    }

    // Retour arrière : le premier joueur libre de l'ordre essaie ses adversaires possibles,
    // du préféré au moins préféré, jusqu'à un appariement complet.
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
