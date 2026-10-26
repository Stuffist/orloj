namespace Olomoc.Tournaments;

/// <summary>
/// Scenario of a round: an agenda from the official collection (<see cref="Agendas.All"/>), or
/// <see cref="Agendas.Other"/> for a custom scenario, named by the organizer; and, if specified,
/// the Artifact and Ministry decks played (<see cref="Decks"/>).
/// </summary>
public sealed record Scenario(string Id, string? Name = null, string? Artifact = null, string? Ministry = null)
{
    public bool IsOther => Id == Agendas.Other;
}

/// <summary>
/// Artifact and Ministry decks, in the order of their number (Artifact 1, 2…). Displayed names:
/// keys "deck.artifact.&lt;id&gt;" and "deck.ministry.&lt;id&gt;" of the texts.
/// </summary>
public static class Decks
{
    public static readonly IReadOnlyList<string> Artifacts = ["ROGUE", "PARASITE"];
    public static readonly IReadOnlyList<string> Ministries = ["DISINFORMATION", "INDOCTRINATION"];

    public static bool IsArtifact(string? id) => id is not null && Artifacts.Contains(id);
    public static bool IsMinistry(string? id) => id is not null && Ministries.Contains(id);
}

/// <summary>
/// Agendas of the official collection ("Agendas", Occicat, January 2026; English version of
/// September 2026), in the order of the table of contents. Displayed names: keys "agenda.&lt;id&gt;" of the texts.
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
/// Random draw of the scenarios of the rounds not paired yet (beyond
/// <paramref name="pairedRounds"/>): official agendas only, without repeats, nor repeating those
/// of the rounds kept as they are (already paired, or custom scenario chosen by the organizer).
/// Collection exhausted: start again. Each of these rounds (custom scenarios included) also gets
/// a random Artifact deck and Ministry deck.
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

/// <summary>Player of the table who played first (P1).</summary>
public enum TableSide { A, B }

/// <summary>Cause of the end of the game: Bile at 3, end of the 4th cycle, PoliSec hunt level at 3.</summary>
public enum GameEnd { Bile, FourthCycle, PoliSec }
