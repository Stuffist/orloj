using Olomoc.Tournaments;

namespace Olomoc.Online;

/// <summary>
/// The decks beside the Block in an online game, from the scenario's decks:
/// <list type="bullet">
/// <item>ministries (rulebook p.31), dealt by « Placer + distribuer »: 1 Axe card to each player,
/// the other 7 shuffled, 3 to each player (hands known to both), the last one face up: the fallen
/// ministry;</item>
/// <item>the Artefact (p.27): a pile face down (secret order), built at its first Mécanisme
/// (Visites: the 3 Rogue cards on top); "Mécanisme" draws its top card (the previous one to the
/// discard; an empty pile: the discard shuffled again); "Remélanger": every card shuffled again;</item>
/// <item>Indra: her player takes the pile's 3 last cards (secret) and may swap the card drawn by a
/// Mécanisme for one of them.</item>
/// </list>
/// Either player (a spectator never), from the initiative to the end of the cycles.
/// </summary>
public static class DeckRules
{
    public static Problem? Apply(OnlineGame g, int seat, OnlineCommand c, Random random, DateTimeOffset now)
    {
        if (g.Step is OnlineStep.Lobby or OnlineStep.Finished || g.Seats.Any(s => s.State is null)) return Problem.Of("onlineStep");
        var decks = g.Decks ??= new OnlineDecks();
        return c.Type switch
        {
            OnlineCommandType.Mechanism => Mechanism(g, decks, seat, random, now),
            OnlineCommandType.ReshuffleArtifact => Reshuffle(g, decks, seat, random, now),
            OnlineCommandType.IndraTake => IndraTake(g, decks, seat, random, now),
            OnlineCommandType.IndraSwap => IndraSwap(g, decks, seat, c.Text, now),
            _ => Problem.Of("onlineCommand"),
        };
    }

    /// <summary>The game set up: its ministries dealt; its decks can no longer change.</summary>
    public static bool Started(OnlineGame g) => g.Decks?.Fallen is not null;

    /// <summary>
    /// « Placer + distribuer » (OnlineRules.SetUp, both decks chosen, not dealt yet): the ministries
    /// dealt, the Artefact pile built (unless a Mécanisme already did).
    /// </summary>
    internal static void SetUp(OnlineGame g, int seat, Random random, DateTimeOffset now)
    {
        var decks = g.Decks ??= new OnlineDecks();
        Deal(g, decks, g.Agenda!.Ministry!, seat, random, now);
        Pile(g, decks, random);
    }

    // ---- Ministries ----

    private static void Deal(OnlineGame g, OnlineDecks decks, string deck, int seat, Random random, DateTimeOffset now)
    {
        var cards = CardDecks.MinistryDeck(deck);
        var axe = cards.First(id => CardDecks.Ministry(id)!.Axe);
        var others = Shuffle(cards.Where(id => id != axe), random);
        decks.Hands = [[axe, .. others.Take(CardDecks.MinistriesPerPlayer)], [axe, .. others.Skip(CardDecks.MinistriesPerPlayer).Take(CardDecks.MinistriesPerPlayer)]];
        decks.Fallen = others[^1];
        decks.MinistryDeck = deck;
        decks.Played = [[], []];
        OnlineRules.Log(g, seat, now, "ministriesDealt", CardDecks.Ministry(decks.Fallen)!.Name);
    }

    // ---- Artefact ----

    private static Problem? Mechanism(OnlineGame g, OnlineDecks decks, int seat, Random random, DateTimeOffset now)
    {
        if (Pile(g, decks, random) is { } problem) return problem;
        if (decks.Drawn is { } previous) decks.Discard.Add(previous);
        decks.Drawn = null;
        if (decks.Pile.Count == 0)
        {
            // An empty pile: its discard shuffled again (rulebook p.27).
            decks.Pile = Shuffle(decks.Discard, random);
            decks.Discard = [];
            if (decks.Pile.Count == 0) return Problem.Of("onlineArtifactEmpty");
            OnlineRules.Log(g, seat, now, "artifactReshuffled");
        }
        decks.Drawn = decks.Pile[0];
        decks.Pile.RemoveAt(0);
        decks.Mechanisms++;
        var card = CardDecks.Artifact(decks.Drawn)!;
        OnlineRules.Log(g, seat, now, "mechanism", card.Name, card.Number ?? "?", card.Priority?.ToString() ?? "?");
        return null;
    }

    private static Problem? Reshuffle(OnlineGame g, OnlineDecks decks, int seat, Random random, DateTimeOffset now)
    {
        if (Pile(g, decks, random) is { } problem) return problem;
        decks.Pile = Shuffle([.. decks.Pile, .. decks.Discard, .. decks.Drawn is { } d ? [d] : Array.Empty<string>()], random);
        decks.Discard = [];
        decks.Drawn = null;
        OnlineRules.Log(g, seat, now, "artifactReshuffled");
        return null;
    }

    // The pile of the scenario's Artefact deck, built at its first use (Visites: the Rogue cards on top).
    private static Problem? Pile(OnlineGame g, OnlineDecks decks, Random random)
    {
        if (decks.ArtifactDeck is not null) return null;
        if (g.Agenda?.Artifact is not { } deck) return Problem.Of("onlineDecksNone");
        var cards = Shuffle(CardDecks.ArtifactDeck(deck), random);
        if (g.Agenda.Id == "VISITS")
            cards = [.. cards.Where(IsRogue), .. cards.Where(id => !IsRogue(id))];
        decks.ArtifactDeck = deck;
        decks.Pile = cards;
        return null;
    }

    private static bool IsRogue(string id) => CardDecks.Artifact(id)?.Hostile == "ROGUE";

    // ---- Indra ----

    /// <summary>The seat whose cabal has Indra (null: none).</summary>
    public static int? IndraSeat(OnlineGame g) =>
        g.Seats.FindIndex(s => s.State?.Cogs.Any(c => c.CogId == CardDecks.Indra) == true) is var i and >= 0 ? i : null;

    private static Problem? IndraTake(OnlineGame g, OnlineDecks decks, int seat, Random random, DateTimeOffset now)
    {
        if (IndraSeat(g) != seat) return Problem.Of("onlineIndra");
        if (decks.IndraSeat is not null) return Problem.Of("onlineIndraTaken");
        if (Pile(g, decks, random) is { } problem) return problem;
        var take = Math.Min(3, decks.Pile.Count);
        decks.Indra = decks.Pile.Skip(decks.Pile.Count - take).ToList();
        decks.Pile.RemoveRange(decks.Pile.Count - take, take);
        decks.IndraSeat = seat;
        OnlineRules.Log(g, seat, now, "indraTake", take.ToString());
        return null;
    }

    private static Problem? IndraSwap(OnlineGame g, OnlineDecks decks, int seat, string? card, DateTimeOffset now)
    {
        if (decks.IndraSeat != seat || card is null || !decks.Indra.Contains(card)) return Problem.Of("onlineIndra");
        if (decks.Drawn is not { } drawn) return Problem.Of("onlineNoMechanism");
        decks.Discard.Add(drawn);
        decks.Indra.Remove(card);
        decks.Drawn = card;
        var chosen = CardDecks.Artifact(card)!;
        OnlineRules.Log(g, seat, now, "indraSwap", CardDecks.Artifact(drawn)!.Name, chosen.Name, chosen.Number ?? "?", chosen.Priority?.ToString() ?? "?");
        return null;
    }

    private static List<string> Shuffle(IEnumerable<string> cards, Random random)
    {
        var list = cards.ToList();
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
        return list;
    }

    // ---- View ----

    /// <summary>
    /// The decks as a connection sees them: its own ministry hand and Indra's cards (its player
    /// only), the counts of the others; the pile's order never.
    /// </summary>
    public static OnlineDecksView? View(OnlineGame g, int? me) => g.Decks is not { } d ? null : new(
        d.MinistryDeck, d.ArtifactDeck,
        me is { } i && i < d.Hands.Count ? d.Hands[i] : [],
        d.Hands.Select(h => h.Count).ToList(),
        d.Fallen, d.Pile.Count, d.Discard, d.Drawn, d.Mechanisms,
        d.IndraSeat, d.IndraSeat == me ? d.Indra : [], d.Indra.Count,
        d.Hands.Select(h => (IReadOnlyList<string>)h.ToList()).ToList(), d.Played.Select(h => (IReadOnlyList<string>)h.ToList()).ToList());
}
