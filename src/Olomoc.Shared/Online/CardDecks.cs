namespace Olomoc.Online;

// The Ministry and Artefact decks of Olomòc (two of each: Decks.Ministries / Decks.Artifacts) and
// the hostiles' profile cards, as printed on the cards of Occicat's Vassal module (rank, planning
// constraint, artefact number read on the images). Images copied by tools/VassalImport into
// img/offblock/{id}.jpg (Source: the module's file). Names as printed (French).

/// <summary>
/// A ministry card: rank, planning constraint (the slot, 1 to 5, where it must be planned; null:
/// any, the Axe's Animosité), Axe or not.
/// </summary>
public sealed record MinistryCard(string Id, string Deck, string Name, int Rank, int? Slot, bool Axe, string Source);

/// <summary>
/// An artefact card: its number (the first digit: the player who gets the priority; null: not known)
/// and the hostile it brings, if any (its profile).
/// </summary>
public sealed record ArtifactCard(string Id, string Deck, string Name, string? Number, string? Hostile, string? Source)
{
    /// <summary>Player who gets the priority when it is drawn: 1 or 2 (null: unknown).</summary>
    public int? Priority => Number is { Length: > 0 } n && n[0] is '1' or '2' ? n[0] - '0' : null;
}

/// <summary>Profile card of a hostile (front: characteristics and abilities; back: its instinct).</summary>
public sealed record HostileCard(string Id, string Name, string Front, string Back);

public static class CardDecks
{
    public const string Images = "img/offblock/";
    public const string Indra = "1R16";
    /// <summary>Cards of each player: 1 of the Axe, then 3 ministries; the one left: the fallen ministry.</summary>
    public const int MinistriesPerPlayer = 3;

    public static readonly IReadOnlyList<MinistryCard> Ministries =
    [
        new("communion", "DISINFORMATION", "Communion", 4, 4, false, "M01.jpg"),
        new("desinformation", "DISINFORMATION", "Désinformation", 4, 1, false, "M02.jpg"),
        new("sterilisation", "DISINFORMATION", "Stérilisation", 4, 2, false, "M03.jpg"),
        new("mise-sous-tension", "DISINFORMATION", "Mise sous tension", 4, 5, false, "M04.jpg"),
        new("panne", "DISINFORMATION", "Panne", 4, 1, false, "M05-V2.jpg"),
        new("cadence", "DISINFORMATION", "Cadence", 4, 2, false, "M06-V2.jpg"),
        new("couvre-feu", "DISINFORMATION", "Couvre-feu", 4, 3, false, "M07-V2.jpg"),
        new("animosite", "DISINFORMATION", "Animosité", 4, null, true, "M08.jpg"),
        new("contemplation", "INDOCTRINATION", "Contemplation", 4, 3, false, "M09.jpg"),
        new("endoctrinement", "INDOCTRINATION", "Endoctrinement", 4, 1, false, "M10.jpg"),
        new("virus", "INDOCTRINATION", "Virus", 4, 4, false, "M11.jpg"),
        new("trame-noire", "INDOCTRINATION", "Trame noire", 4, 2, false, "M12.jpg"),
        new("obsolescence", "INDOCTRINATION", "Obsolescence", 4, 2, false, "M13.jpg"),
        new("rendement", "INDOCTRINATION", "Rendement", 4, 3, false, "M14.jpg"),
        new("surveillance", "INDOCTRINATION", "Surveillance", 4, 5, false, "M15.jpg"),
        new("animosite-2", "INDOCTRINATION", "Animosité", 4, null, true, "M16.jpg"),
    ];

    public static readonly IReadOnlyList<ArtifactCard> Artifacts =
    [
        new("rogue-1", "ROGUE", "Rogue", "1.470", "ROGUE", "A01-4.jpg"),
        new("rogue-2", "ROGUE", "Rogue", "2.527", "ROGUE", "A02-4.jpg"),
        new("rogue-3", "ROGUE", "Rogue", "2.847", "ROGUE", "A03-4.jpg"),
        new("contraction-temporelle", "ROGUE", "Contraction temporelle", "2.000", null, "A04-2.jpg"),
        new("pluie-de-fiel-2", "ROGUE", "Pluie de Fiel", "1.997", null, "A05-2.jpg"),
        new("pluie-de-fiel-3", "ROGUE", "Pluie de Fiel", "1.656", null, "A06-2.jpg"),
        new("pluie-de-fiel-1", "ROGUE", "Pluie de Fiel", "2.336", null, "A07-2.jpg"),
        new("faisceau-lumineux", "ROGUE", "Faisceau lumineux", "1.778", null, "A08-2.jpg"),
        new("directive-supreme-1", "ROGUE", "Directive suprême", "2.286", null, "A09-2.jpg"),
        new("directive-supreme-2", "ROGUE", "Directive suprême", "1.070", null, "A10-2.jpg"),
        new("faille-quantique-moins", "ROGUE", "Faille quantique −", "2.222", null, "A11-2.jpg"),
        new("faille-quantique-plus", "ROGUE", "Faille quantique +", "1.111", null, "A12-2.jpg"),
        new("parasite-1", "PARASITE", "Parasite", "2.527", "PARASITE", "A13-2.jpg"),
        new("parasite-2", "PARASITE", "Parasite", "1.656", "PARASITE", "A14.jpg"),
        new("parasite-3", "PARASITE", "Parasite", "2.997", "PARASITE", "A15.jpg"),
        new("parasite-4", "PARASITE", "Parasite", "1.847", "PARASITE", "A16.jpg"),
        new("parasite-5", "PARASITE", "Parasite", "2.070", "PARASITE", "A17.jpg"),
        new("parasite-6", "PARASITE", "Parasite", "1.336", "PARASITE", "A18.jpg"),
        new("signal-1", "PARASITE", "Signal", "2.000", null, "A19.jpg"),
        new("signal-2", "PARASITE", "Signal", "1.778", null, "A20.jpg"),
        new("feux-etranges", "PARASITE", "Feux étranges", "1.470", null, "A21.jpg"),
        new("le-souffle", "PARASITE", "Le souffle", "2.286", null, "A22.jpg"),
        new("reconfiguration", "PARASITE", "Reconfiguration", "2.222", null, "A23.jpg"),
        // Its image is not in the module (it points to an activation card's): number to complete.
        new("decharge", "PARASITE", "Décharge", null, null, null),
    ];

    public static readonly IReadOnlyList<HostileCard> Hostiles =
    [
        new("ROGUE", "Rogue", "H01_recto.jpg", "H01_verso.jpg"),
        new("GARGOYLE", "La Gargoule", "H02_recto.jpg", "H02_verso.jpg"),
        new("PARASITE", "Parasite", "H03_recto.jpg", "H03_verso.jpg"),
    ];

    public static MinistryCard? Ministry(string? id) => Ministries.FirstOrDefault(c => c.Id == id);
    public static ArtifactCard? Artifact(string? id) => Artifacts.FirstOrDefault(c => c.Id == id);
    public static HostileCard? Hostile(string? id) => Hostiles.FirstOrDefault(c => c.Id == id);

    /// <summary>The deck's 9 ministry cards: its Axe card twice, its 7 ministries.</summary>
    public static IReadOnlyList<string> MinistryDeck(string deck)
    {
        var cards = Ministries.Where(c => c.Deck == deck).ToList();
        var axe = cards.Single(c => c.Axe).Id;
        return [axe, axe, .. cards.Where(c => !c.Axe).Select(c => c.Id)];
    }

    public static IReadOnlyList<string> ArtifactDeck(string deck) => Artifacts.Where(c => c.Deck == deck).Select(c => c.Id).ToList();

    /// <summary>Hostiles of a game: the artefact deck's, and the Gargoyle with its agenda.</summary>
    public static IEnumerable<HostileCard> HostilesOf(string? artifactDeck, string? agenda) =>
        Hostiles.Where(h => Artifacts.Any(a => a.Deck == artifactDeck && a.Hostile == h.Id) || h.Id == "GARGOYLE" && agenda == "GARGOYLE");

    public static string Image(string id) => $"{Images}{id}.jpg";
    public static string Activation(string cogId) => $"{Images}activation-{cogId}.jpg";
    public static string Propaganda(int n) => $"{Images}propaganda-{n}.jpg";
    /// <summary>Propaganda cards printed (their images: 1 to this).</summary>
    public const int PropagandaImages = 4;
    public static string Front(HostileCard h) => $"{Images}hostile-{h.Id.ToLowerInvariant()}-front.jpg";
    public static string Back(HostileCard h) => $"{Images}hostile-{h.Id.ToLowerInvariant()}-back.jpg";
}
