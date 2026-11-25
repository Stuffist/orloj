using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>"Hors Bloc" panel, planning (semi-automatic): programmed in secret, revealed phase by phase.</summary>
public class OnlinePlanningTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 22, 0, 0, TimeSpan.Zero);

    // In the cycles, ministries dealt (Disinformation deck), Bo (seat 1) is P1.
    private static async Task<(AppData D, OnlineGame G)> InCycle()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(), slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        var g = new OnlineGame { Code = "PLAN01", Seats = { new Seat(), new Seat() } };
        Ok(Do(g, 0, new(OnlineCommandType.SetName, "Ana"), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetName, "Bo"), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: Cabal("A", ("3R06", "E03"), ("2R08", "D01"), ("1R01", "E11"))), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: Cabal("B", ("3R06", "E03"), ("2R11", "E07"), ("1R01", "E11"))), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetReady, Flag: true), d));
        g.Step = OnlineStep.Agenda;
        g.First = 1;
        Ok(Do(g, 1, new(OnlineCommandType.ChooseAgenda, "PROSPECTION"), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["ROGUE", "DISINFORMATION"]), d));
        Ok(Do(g, 1, new(OnlineCommandType.DealMinistries), d));
        return (d, g);
    }

    private static OnlineResult Do(OnlineGame g, int seat, OnlineCommand c, AppData d) =>
        OnlineRules.Apply(g, seat, c, d.Catalog, new Random(3), Now);

    private static void Ok(OnlineResult r) => Assert.True(r.Ok, string.Join(", ", r.Problems.Select(p => p.Key)));

    private static string Key(OnlineResult r) => Assert.Single(r.Problems).Key;

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
        var (_, g) = await InCycle();
        var hand = PlanningRules.Hand(g, 0);
        Assert.Equal(3, hand.Count(c => c.StartsWith("act:")));
        Assert.Equal(["prop:1"], hand.Where(c => c.StartsWith("prop:")));
        Assert.Equal(4, hand.Count(c => c.StartsWith("min:")));
        Assert.Equal(g.Seats[0].State!.Cogs.Single(c => c.CogId == "3R06").Rank, PlanningRules.Rank(g, 0, "act:3R06"));
        Assert.Equal((1, 4), (PlanningRules.Rank(g, 0, "prop:1"), PlanningRules.Rank(g, 0, $"min:{g.Decks!.Hands[0][1]}")));

        // A cog out of action: no activation card, one more propaganda.
        var cog = g.Seats[0].State!.Cogs[0];
        for (var i = 0; i < cog.LifeLine.Count; i++) cog.SetChecked(i, true);
        hand = PlanningRules.Hand(g, 0);
        Assert.Equal((2, 2), (hand.Count(c => c.StartsWith("act:")), hand.Count(c => c.StartsWith("prop:"))));
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
