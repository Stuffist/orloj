using Olomoc.Tournaments;

namespace Olomoc.Online;

/// <summary>
/// The planning of an online game, semi-automatic (rulebook p.33): each player programs 5 cards in
/// secret, left to right: the activation cards of their cogs, their propaganda cards (1, plus 1 per
/// cog out of action at the end of an earlier cycle, its activation card replaced: a cog out of
/// action during the cycle keeps it until then), one of their ministries, which must go on the slot
/// of its planning constraint (the Axe: any). Once both plannings are complete, "Révéler la phase"
/// shows the next card of each; the players work out the rest (the active player: the higher rank;
/// equal ranks: a Mécanisme). At the end of the cycle, the planning starts again and the ministry
/// played is discarded. Cards: "act:{cog}", "prop:{n}", "min:{ministry}".
/// </summary>
public static class PlanningRules
{
    public const int Slots = 5;

    public static Problem? Apply(OnlineGame g, int seat, OnlineCommand c, DateTimeOffset now)
    {
        if (g.Step != OnlineStep.Cycle || g.Seats.Any(s => s.State is null)) return Problem.Of("onlineStep");
        var planning = g.Planning ??= new OnlinePlanning();
        return c.Type switch
        {
            OnlineCommandType.SetPlanning => Set(g, planning, seat, c.Decks),
            OnlineCommandType.RevealPhase => Reveal(g, planning, seat, now),
            _ => Problem.Of("onlineCommand"),
        };
    }

    /// <summary>
    /// The cards a seat can plan this cycle: a cog out of action during the cycle keeps its activation
    /// card until the end of the cycle, when it becomes a propaganda.
    /// </summary>
    public static IReadOnlyList<string> Hand(OnlineGame g, int seat)
    {
        var cogs = g.Seats[seat].State?.Cogs ?? [];
        var lost = g.Planning?.Lost.ElementAtOrDefault(seat) ?? [];
        var propaganda = 1 + lost.Count;
        var ministries = g.Decks?.Hands.ElementAtOrDefault(seat) ?? [];
        return
        [
            .. cogs.Where(c => !lost.Contains(c.CogId)).OrderByDescending(c => c.Rank).Select(c => $"act:{c.CogId}"),
            .. Enumerable.Range(1, propaganda).Select(n => $"prop:{n}"),
            .. ministries.Distinct().Select(id => $"min:{id}"),
        ];
    }

    /// <summary>Rank of a planned card: its cog's, 1 for a propaganda, the ministry card's.</summary>
    public static int Rank(OnlineGame g, int seat, string card) => card.Split(':', 2) switch
    {
        ["act", var cog] => g.Seats[seat].State?.Cogs.FirstOrDefault(c => c.CogId == cog)?.Rank ?? 0,
        ["min", var id] => CardDecks.Ministry(id)?.Rank ?? 0,
        _ => 1,
    };

    /// <summary>Complete: as many cards as the hand allows (5 at most).</summary>
    public static bool Complete(OnlineGame g, int seat) =>
        g.Planning?.Slots.ElementAtOrDefault(seat) is { } slots && slots.Count(s => s is not null) == Math.Min(Slots, Hand(g, seat).Count);

    private static Problem? Set(OnlineGame g, OnlinePlanning planning, int seat, IReadOnlyList<string?>? slots)
    {
        if (planning.Revealed > 0) return Problem.Of("onlinePlanningRevealed");
        if (slots is null || slots.Count != Slots) return Problem.Of("onlinePlanning");
        var hand = Hand(g, seat);
        var cards = slots.OfType<string>().ToList();
        if (cards.Any(c => !hand.Contains(c)) || cards.Distinct().Count() != cards.Count) return Problem.Of("onlinePlanning");
        if (cards.Count(c => c.StartsWith("min:")) > 1) return Problem.Of("onlinePlanningMinistries");
        for (var i = 0; i < Slots; i++)
            if (slots[i] is { } card && card.StartsWith("min:") && CardDecks.Ministry(card[4..]) is { Slot: { } slot } && slot != i + 1)
                return Problem.Of("onlinePlanningSlot", CardDecks.Ministry(card[4..])!.Name, slot.ToString());
        planning.Slots[seat] = slots.ToList();
        return null;
    }

    private static Problem? Reveal(OnlineGame g, OnlinePlanning planning, int seat, DateTimeOffset now)
    {
        if (planning.Revealed >= Slots) return Problem.Of("onlinePlanningDone");
        if (!Complete(g, 0) || !Complete(g, 1)) return Problem.Of("onlinePlanningIncomplete");
        planning.Revealed++;
        var phase = planning.Revealed - 1;
        OnlineRules.Log(g, seat, now, "phaseRevealed", planning.Revealed.ToString(),
            planning.Slots[0].ElementAtOrDefault(phase) ?? "", planning.Slots[1].ElementAtOrDefault(phase) ?? "");
        return null;
    }

    /// <summary>
    /// End of the cycle: the ministry played goes from its player's hand to their discard; the activation
    /// card of a cog out of action becomes a propaganda; a new planning.
    /// </summary>
    public static void EndCycle(OnlineGame g)
    {
        var planning = g.Planning ?? new OnlinePlanning();
        for (var seat = 0; seat < planning.Slots.Count; seat++)
            foreach (var card in planning.Slots[seat].OfType<string>().Where(c => c.StartsWith("min:")))
                if (g.Decks is { } decks && decks.Hands.ElementAtOrDefault(seat) is { } hand && hand.Remove(card[4..]))
                    decks.Played[seat].Add(card[4..]);
        var lost = planning.Lost.Select((cogs, seat) => cogs
            .Union(g.Seats[seat].State?.Cogs.Where(c => c.OutOfAction).Select(c => c.CogId) ?? []).ToList()).ToList();
        g.Planning = new OnlinePlanning { Lost = lost };
    }

    /// <summary>The planning as a connection sees it: its own in full and its hand; of each seat, the revealed cards.</summary>
    public static OnlinePlanningView? View(OnlineGame g, int? me)
    {
        if (g.Step is OnlineStep.Lobby || g.Seats.Any(s => s.State is null)) return null;
        var planning = g.Planning ?? new OnlinePlanning();
        return new(
            me is { } i ? Hand(g, i) : [],
            me is { } j ? planning.Slots[j] : [],
            planning.Slots.Select(s => (IReadOnlyList<string?>)s.Take(planning.Revealed).ToList()).ToList(),
            planning.Revealed,
            [Complete(g, 0), Complete(g, 1)],
            planning.Slots.Select((s, seat) => s.Take(planning.Revealed).Select(c => c is null ? 0 : Rank(g, seat, c)).ToList()).Cast<IReadOnlyList<int>>().ToList());
    }
}
