using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>"Hors Bloc" panel, decks: the ministries dealt, the Artefact and its Mécanismes, Indra.</summary>
public class OnlineDecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 21, 0, 0, TimeSpan.Zero);

    // A game in its cycles, Bo (seat 1) is P1, agenda and decks chosen; Ana (seat 0) has Indra if asked.
    private static async Task<(AppData D, OnlineGame G)> InCycle(string agenda = "PROSPECTION", string artifact = "ROGUE",
        string ministry = "DISINFORMATION", bool indra = false)
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, string faction, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, faction, slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(), slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        var g = new OnlineGame { Code = "DECKS1", Seats = { new Seat(), new Seat() } };
        Ok(Do(g, 0, new(OnlineCommandType.SetName, "Ana"), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetName, "Bo"), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: indra ? d.LegalCabal("URKARAS") : Cabal("A", "MAISEL", ("3R06", "E03"), ("2R08", "D01"), ("1R01", "E11"))), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: Cabal("B", "MAISEL", ("3R06", "E03"), ("2R11", "E07"), ("1R01", "E11"))), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetReady, Flag: true), d));
        g.Step = OnlineStep.Agenda;
        g.First = 1;
        Ok(Do(g, 1, new(OnlineCommandType.ChooseAgenda, agenda), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: [artifact, ministry]), d));
        if (indra) g.Seats[0].State!.Cogs[2].CogId = CardDecks.Indra;          // her card in the cabal
        return (d, g);
    }

    private static OnlineResult Do(OnlineGame g, int seat, OnlineCommand c, AppData d, int seed = 1) =>
        OnlineRules.Apply(g, seat, c, d.Catalog, new Random(seed), Now);

    private static void Ok(OnlineResult r) => Assert.True(r.Ok, string.Join(", ", r.Problems.Select(p => p.Key)));

    private static string Key(OnlineResult r) => Assert.Single(r.Problems).Key;

    [Fact]
    public void Decks_as_printed_two_ministry_decks_of_9_two_artefact_decks_of_12()
    {
        foreach (var deck in Decks.Ministries)
        {
            var cards = CardDecks.MinistryDeck(deck);
            Assert.Equal(9, cards.Count);
            Assert.Equal(2, cards.Count(id => CardDecks.Ministry(id)!.Axe));
            Assert.All(cards.Select(CardDecks.Ministry), m => Assert.Equal(4, m!.Rank));
            Assert.All(cards.Select(CardDecks.Ministry).Where(m => !m!.Axe), m => Assert.InRange(m!.Slot!.Value, 1, 5));
        }
        foreach (var deck in Decks.Artifacts)
        {
            var cards = CardDecks.ArtifactDeck(deck);
            Assert.Equal(12, cards.Count);
            Assert.All(cards.Select(CardDecks.Artifact), a => Assert.Contains(a!.Priority, new int?[] { 1, 2 }));
        }
        Assert.Equal(2, CardDecks.Artifact("rogue-2")!.Priority);
        // Every image named exists (imported from the module).
        var images = CardDecks.Ministries.Select(m => CardDecks.Image(m.Id))
            .Concat(CardDecks.Artifacts.Where(a => a.Source is not null).Select(a => CardDecks.Image(a.Id)))
            .Concat(CardDecks.Hostiles.SelectMany(h => new[] { CardDecks.Front(h), CardDecks.Back(h) }));
        Assert.All(images, image => Assert.True(File.Exists(Path.Combine(AppData.WwwRoot, image)), image));
    }

    [Fact]
    public async Task Ministries_dealt_an_Axe_card_and_3_each_the_last_one_fallen_hands_known_to_both()
    {
        var (d, g) = await InCycle();
        Ok(Do(g, 1, new(OnlineCommandType.DealMinistries), d));
        var decks = g.Decks!;
        Assert.All(decks.Hands, hand =>
        {
            Assert.Equal(4, hand.Count);
            Assert.Equal(1, hand.Count(id => CardDecks.Ministry(id)!.Axe));
        });
        var all = decks.Hands.SelectMany(h => h).Append(decks.Fallen!).ToList();
        Assert.Equal(CardDecks.MinistryDeck("DISINFORMATION").Order(), all.Order());
        Assert.False(CardDecks.Ministry(decks.Fallen)!.Axe);
        Assert.Equal("ministriesDealt", g.Log[^1].Key);
        Assert.Equal("onlineDealt", Key(Do(g, 0, new(OnlineCommandType.DealMinistries), d)));
        Ok(Do(g, 0, new(OnlineCommandType.DealMinistries, Flag: true), d, seed: 5));          // dealt again

        // Each one has their hand; both hands are known (to a spectator too).
        var ana = OnlineRules.View(g, 0, new HashSet<int>()).Decks!;
        Assert.Equal(g.Decks!.Hands[0], ana.MyHand);
        Assert.Equal([4, 4], ana.HandSizes);
        Assert.Equal(g.Decks.Fallen, ana.Fallen);
        Assert.Equal(g.Decks.Hands[1], ana.Hands[1]);
        var spectator = OnlineRules.View(g, null, new HashSet<int>()).Decks!;
        Assert.Empty(spectator.MyHand);
        Assert.Equal(g.Decks.Hands[0], spectator.Hands[0]);
        Assert.All(spectator.Played, p => Assert.Empty(p));
    }

    [Fact]
    public async Task Mechanism_draws_the_pile_which_is_shuffled_again_when_empty()
    {
        var (d, g) = await InCycle();
        Ok(Do(g, 0, new(OnlineCommandType.Mechanism), d));
        var first = g.Decks!.Drawn!;
        Assert.Equal((11, 0, 1), (g.Decks.Pile.Count, g.Decks.Discard.Count, g.Decks.Mechanisms));
        Assert.Equal("mechanism", g.Log[^1].Key);
        Assert.Equal(CardDecks.Artifact(first)!.Name, g.Log[^1].Args![0]);
        Ok(Do(g, 1, new(OnlineCommandType.Mechanism), d));
        Assert.Equal([first], g.Decks.Discard);
        for (var i = 0; i < 10; i++) Ok(Do(g, 0, new(OnlineCommandType.Mechanism), d));
        Assert.Empty(g.Decks.Pile);

        // The pile empty: its discard (the 12 cards, the last drawn one included) shuffled again, then drawn.
        Ok(Do(g, 0, new(OnlineCommandType.Mechanism), d));
        Assert.Equal((11, 0), (g.Decks.Pile.Count, g.Decks.Discard.Count));

        // The pile's order is never sent; "Remélanger": every card shuffled again.
        var view = OnlineRules.View(g, 0, new HashSet<int>()).Decks!;
        Assert.Equal(11, view.PileSize);
        Ok(Do(g, 1, new(OnlineCommandType.ReshuffleArtifact), d));
        Assert.Equal((12, 0, (string?)null), (g.Decks.Pile.Count, g.Decks.Discard.Count, g.Decks.Drawn));
    }

    [Fact]
    public async Task Visits_puts_the_three_Rogue_cards_on_top_of_the_pile()
    {
        var (d, g) = await InCycle("VISITS");
        Ok(Do(g, 0, new(OnlineCommandType.Mechanism), d));
        Assert.Equal("ROGUE", CardDecks.Artifact(g.Decks!.Drawn)!.Hostile);
        Assert.All(g.Decks.Pile.Take(2), id => Assert.Equal("ROGUE", CardDecks.Artifact(id)!.Hostile));
        Assert.DoesNotContain(g.Decks.Pile.Skip(2), id => CardDecks.Artifact(id)!.Hostile == "ROGUE");
    }

    [Fact]
    public async Task Indra_takes_the_three_last_cards_and_swaps_the_drawn_card_her_player_only()
    {
        var (d, g) = await InCycle(indra: true);
        Assert.Equal(0, DeckRules.IndraSeat(g));
        Assert.Equal("onlineIndra", Key(Do(g, 1, new(OnlineCommandType.IndraTake), d)));
        Ok(Do(g, 0, new(OnlineCommandType.IndraTake), d));
        var indra = g.Decks!.Indra.ToList();
        Assert.Equal((3, 9), (indra.Count, g.Decks.Pile.Count));
        Assert.Equal("onlineIndraTaken", Key(Do(g, 0, new(OnlineCommandType.IndraTake), d)));
        Assert.Equal(indra, OnlineRules.View(g, 0, new HashSet<int>()).Decks!.MyIndra);
        var other = OnlineRules.View(g, 1, new HashSet<int>()).Decks!;
        Assert.Empty(other.MyIndra);
        Assert.Equal((0, 3), (other.IndraSeat, other.IndraCount));

        Assert.Equal("onlineNoMechanism", Key(Do(g, 0, new(OnlineCommandType.IndraSwap, indra[0]), d)));
        Ok(Do(g, 1, new(OnlineCommandType.Mechanism), d));
        var drawn = g.Decks.Drawn!;
        Assert.Equal("onlineIndra", Key(Do(g, 1, new(OnlineCommandType.IndraSwap, indra[0]), d)));
        Ok(Do(g, 0, new(OnlineCommandType.IndraSwap, indra[0]), d));
        Assert.Equal((indra[0], drawn), (g.Decks.Drawn, g.Decks.Discard[^1]));
        Assert.Equal(2, g.Decks.Indra.Count);
    }

    [Fact]
    public async Task Decks_need_the_scenarios_and_start_again_when_it_changes()
    {
        var (d, g) = await InCycle();
        Ok(Do(g, 0, new(OnlineCommandType.Mechanism), d));
        Ok(Do(g, 0, new(OnlineCommandType.DealMinistries), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["PARASITE", "DISINFORMATION"]), d));
        Assert.Equal((null, (string?)null), (g.Decks!.ArtifactDeck, g.Decks.Drawn));
        Assert.NotNull(g.Decks.Fallen);                                       // the ministries stay
        Ok(Do(g, 0, new(OnlineCommandType.Mechanism), d));
        Assert.Equal("PARASITE", CardDecks.Artifact(g.Decks.Drawn)!.Deck);

        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: [null, null]), d));
        Assert.Equal("onlineDecksNone", Key(Do(g, 0, new(OnlineCommandType.Mechanism), d)));
        Assert.Equal("onlineDecksNone", Key(Do(g, 0, new(OnlineCommandType.DealMinistries), d)));
    }
}
