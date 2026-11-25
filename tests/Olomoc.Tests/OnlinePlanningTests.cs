using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;
using static Olomoc.Tests.OnlineTestGame;

namespace Olomoc.Tests;

/// <summary>"Hors Bloc" panel, planning (semi-automatic): programmed in secret, revealed phase by phase.</summary>
public class OnlinePlanningTests
{
    // In the cycles, ministries dealt (Disinformation deck), Bo (seat 1) is P1.
    private static async Task<(AppData D, OnlineGame G)> InCycle()
    {
        var (d, g) = await OnlineTestGame.InCycle("PROSPECTION");
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["ROGUE", "DISINFORMATION"]), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetUp, "h-rise"), d));
        return (d, g);
    }

    // A full planning: the 3 activations, the propaganda, a ministry of the hand on its slot (the Axe: slot 5).
    private static List<string?> Full(OnlineGame g, int seat, string? ministry = null)
    {
        var hand = PlanningRules.Hand(g, seat);
        var acts = hand.Where(c => c.StartsWith("act:")).ToList();
        var min = ministry ?? hand.First(c => c.StartsWith("min:") && CardDecks.Ministry(c[4..])!.Axe);
        var slot = CardDecks.Ministry(min[4..])!.Slot ?? 5;
        var others = new Queue<string>([.. acts, "prop:1"]);
        return Enumerable.Range(1, 5).Select(i => i == slot ? min : others.Dequeue()).ToList();
    }

    [Fact]
    public async Task Hand_activations_of_cogs_in_play_propaganda_and_ministries()
    {
        var (d, g) = await InCycle();
        var hand = PlanningRules.Hand(g, 0);
        Assert.Equal(3, hand.Count(c => c.StartsWith("act:")));
        Assert.Equal(["prop:1"], hand.Where(c => c.StartsWith("prop:")));
        Assert.Equal(4, hand.Count(c => c.StartsWith("min:")));
        Assert.Equal(g.Seats[0].State!.Cogs.Single(c => c.CogId == "3R06").Rank, PlanningRules.Rank(g, 0, "act:3R06"));
        Assert.Equal((1, 4), (PlanningRules.Rank(g, 0, "prop:1"), PlanningRules.Rank(g, 0, $"min:{g.Decks!.Hands[0][1]}")));

        // A cog out of action: its activation card until the end of the cycle, then a propaganda instead.
        var cog = g.Seats[0].State!.Cogs[0];
        for (var i = 0; i < cog.LifeLine.Count; i++) cog.SetChecked(i, true);
        hand = PlanningRules.Hand(g, 0);
        Assert.Equal((3, 1), (hand.Count(c => c.StartsWith("act:")), hand.Count(c => c.StartsWith("prop:"))));
        Ok(Do(g, 0, new(OnlineCommandType.SetEndCycle, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetEndCycle, Flag: true), d));
        hand = PlanningRules.Hand(g, 0);
        Assert.Equal((2, 2), (hand.Count(c => c.StartsWith("act:")), hand.Count(c => c.StartsWith("prop:"))));
        Assert.DoesNotContain($"act:{cog.CogId}", hand);
    }

    [Fact]
    public async Task Planning_checked_ministry_on_its_slot_cards_of_the_hand_once()
    {
        var (d, g) = await InCycle();
        var ministry = g.Decks!.Hands[0].Select(id => $"min:{id}").First(c => !CardDecks.Ministry(c[4..])!.Axe);
        var full = Full(g, 0, ministry);
        Ok(Do(g, 0, new(OnlineCommandType.SetPlanning, Decks: full), d));
        Assert.True(PlanningRules.Complete(g, 0));

        // The ministry elsewhere than its slot; two ministries; a card not in the hand; twice the same.
        var slot = CardDecks.Ministry(ministry[4..])!.Slot!.Value;
        var moved = full.ToList();
        (moved[slot - 1], moved[slot % 5]) = (moved[slot % 5], moved[slot - 1]);
        Assert.Equal("onlinePlanningSlot", Key(Do(g, 0, new(OnlineCommandType.SetPlanning, Decks: moved), d)));
        var axe = $"min:{g.Decks.Hands[0].First(id => CardDecks.Ministry(id)!.Axe)}";
        var two = full.Select(c => c == "prop:1" ? axe : c).ToList();
        Assert.Equal("onlinePlanningMinistries", Key(Do(g, 0, new(OnlineCommandType.SetPlanning, Decks: two), d)));
        Assert.Equal("onlinePlanning", Key(Do(g, 0, new(OnlineCommandType.SetPlanning, Decks: full.Select(c => c == "prop:1" ? "prop:2" : c).ToList()), d)));
        Assert.Equal("onlinePlanning", Key(Do(g, 0, new(OnlineCommandType.SetPlanning, Decks: full.Select(c => c == "prop:1" ? full[0] : c).ToList()), d)));
        Assert.Equal("onlinePlanning", Key(Do(g, 0, new(OnlineCommandType.SetPlanning, Decks: full.Take(4).ToList()), d)));

        // Incomplete: allowed, but not ready.
        Ok(Do(g, 0, new(OnlineCommandType.SetPlanning, Decks: [full[0], null, null, null, null]), d));
        Assert.False(PlanningRules.Complete(g, 0));
    }

    [Fact]
    public async Task Phases_revealed_once_both_are_complete_secret_before_then_ministry_discarded_at_the_end()
    {
        var (d, g) = await InCycle();
        Assert.Equal("onlinePlanningIncomplete", Key(Do(g, 0, new(OnlineCommandType.RevealPhase), d)));
        var ana = Full(g, 0);
        Ok(Do(g, 0, new(OnlineCommandType.SetPlanning, Decks: ana), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetPlanning, Decks: Full(g, 1)), d));

        // Bo does not see Ana's cards before they are revealed.
        var bo = OnlineRules.View(g, 1, new HashSet<int>()).Planning!;
        Assert.Equal((0, true), (bo.Phase, bo.Complete.All(c => c)));
        Assert.All(bo.Revealed, r => Assert.Empty(r));
        Assert.Equal(ana, OnlineRules.View(g, 0, new HashSet<int>()).Planning!.MySlots);

        Ok(Do(g, 1, new(OnlineCommandType.RevealPhase), d));
        bo = OnlineRules.View(g, 1, new HashSet<int>()).Planning!;
        Assert.Equal((1, ana[0]), (bo.Phase, bo.Revealed[0].Single()));
        Assert.Equal(PlanningRules.Rank(g, 0, ana[0]!), bo.Ranks[0][0]);
        Assert.Equal("phaseRevealed", g.Log[^1].Key);
        Assert.Equal("onlinePlanningRevealed", Key(Do(g, 0, new(OnlineCommandType.SetPlanning, Decks: ana), d)));
        for (var i = 1; i < 5; i++) Ok(Do(g, 0, new(OnlineCommandType.RevealPhase), d));
        Assert.Equal("onlinePlanningDone", Key(Do(g, 0, new(OnlineCommandType.RevealPhase), d)));

        // End of the cycle: a new planning; the ministry played leaves the hand.
        var played = ana.Single(c => c!.StartsWith("min:"))![4..];
        Ok(Do(g, 0, new(OnlineCommandType.SetEndCycle, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetEndCycle, Flag: true), d));
        Assert.Equal(0, g.Planning!.Revealed);
        Assert.All(g.Planning.Slots, s => Assert.All(s, c => Assert.Null(c)));
        Assert.DoesNotContain(played, g.Decks!.Hands[0]);
        Assert.Contains(played, g.Decks.Played[0]);
    }
}
