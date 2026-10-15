namespace Olomoc.Tournaments;

/// <summary>
/// Scénario d'une ronde : un agenda du recueil officiel (<see cref="Agendas.All"/>), ou
/// <see cref="Agendas.Other"/> pour un scénario maison, nommé par l'organisateur.
/// </summary>
public sealed record Scenario(string Id, string? Name = null)
{
    public bool IsOther => Id == Agendas.Other;
}

/// <summary>
/// Agendas du recueil officiel (« Agendas », Occicat, janvier 2026 ; version anglaise de
/// septembre 2026), dans l'ordre du sommaire. Noms affichés : clés « agenda.&lt;id&gt; » des textes.
/// </summary>
public static class Agendas
{
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All =
    [
        "PROSPECTION", "QUANTUM_ANOMALY", "VISITS", "LOST_HYPOGEUM", "QUANTAR_BATTERIES", "SABOTAGE",
        "GARGOYLE",
        "PNEUMATIC_NETWORK", "CARTOGRAPHER", "ALONE_IN_THE_DARK",
    ];

    public static bool IsKnown(string? id) => id is not null && (id == Other || All.Contains(id));

    /// <summary>
    /// Tirage au sort des scénarios des rondes pas encore appariées (au-delà de
    /// <paramref name="pairedRounds"/>) : agendas officiels seulement, sans répétition, ni avec
    /// ceux des rondes gardées telles quelles (déjà appariées, ou scénario maison choisi par
    /// l'organisateur). Recueil épuisé : on recommence.
    /// </summary>
    public static List<Scenario?> Draw(IReadOnlyList<Scenario?> current, int roundCount, int pairedRounds, Random random)
    {
        bool Kept(int i) => i < pairedRounds || current.ElementAtOrDefault(i)?.IsOther == true;
        var result = Enumerable.Range(0, roundCount).Select(i => Kept(i) ? current.ElementAtOrDefault(i) : null).ToList();
        var pool = new List<string>();
        for (var i = pairedRounds; i < roundCount; i++)
        {
            if (Kept(i)) continue;
            if (pool.Count == 0)
            {
                pool = All.Where(id => !result.Any(s => s?.Id == id)).ToList();
                if (pool.Count == 0) pool = All.ToList();
            }
            var pick = pool[random.Next(pool.Count)];
            pool.Remove(pick);
            result[i] = new Scenario(pick);
        }
        return result;
    }
}

/// <summary>Joueur de la table qui a joué en premier (J1).</summary>
public enum TableSide { A, B }

/// <summary>Cause de la fin de partie : Fiel à 3, fin du 4ᵉ cycle, niveau de traque de la PoliSec à 3.</summary>
public enum GameEnd { Bile, FourthCycle, PoliSec }
