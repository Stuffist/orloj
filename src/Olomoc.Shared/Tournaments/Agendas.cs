namespace Olomoc.Tournaments;

/// <summary>
/// Scénario d'une ronde : un agenda du recueil officiel (<see cref="Agendas.All"/>), ou
/// <see cref="Agendas.Other"/> pour un scénario maison, nommé par l'organisateur ; et, si
/// précisés, les decks Artefact et Ministère joués (<see cref="Decks"/>).
/// </summary>
public sealed record Scenario(string Id, string? Name = null, string? Artifact = null, string? Ministry = null)
{
    public bool IsOther => Id == Agendas.Other;
}

/// <summary>
/// Decks Artefact et Ministère, dans l'ordre de leur numéro (Artefact 1, 2…). Noms affichés :
/// clés « deck.artifact.&lt;id&gt; » et « deck.ministry.&lt;id&gt; » des textes.
/// </summary>
public static class Decks
{
    public static readonly IReadOnlyList<string> Artifacts = ["ROGUE", "PARASITE"];
    public static readonly IReadOnlyList<string> Ministries = ["DISINFORMATION", "INDOCTRINATION"];

    public static bool IsArtifact(string? id) => id is not null && Artifacts.Contains(id);
    public static bool IsMinistry(string? id) => id is not null && Ministries.Contains(id);
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
    /// l'organisateur). Recueil épuisé : on recommence. Chacune de ces rondes (scénarios maison
    /// compris) reçoit aussi un deck Artefact et un deck Ministère au hasard.
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
        for (var i = pairedRounds; i < roundCount; i++)
            result[i] = result[i]! with
            {
                Artifact = Decks.Artifacts[random.Next(Decks.Artifacts.Count)],
                Ministry = Decks.Ministries[random.Next(Decks.Ministries.Count)],
            };
        return result;
    }
}

/// <summary>Joueur de la table qui a joué en premier (J1).</summary>
public enum TableSide { A, B }

/// <summary>Cause de la fin de partie : Fiel à 3, fin du 4ᵉ cycle, niveau de traque de la PoliSec à 3.</summary>
public enum GameEnd { Bile, FourthCycle, PoliSec }
