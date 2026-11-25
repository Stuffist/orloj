using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;
using static Olomoc.Tests.OnlineTestGame;

namespace Olomoc.Tests;

/// <summary>"Hors Bloc" panel, decks: the ministries dealt, the Artefact and its Mécanismes, Indra.</summary>
public class OnlineDecksTests
{
    // A game in its cycles, Bo (seat 1) is P1, agenda and decks chosen; Ana (seat 0) has Indra if asked.
    private static async Task<(AppData D, OnlineGame G)> InCycle(string agenda = "PROSPECTION", string artifact = "ROGUE",
        string ministry = "DISINFORMATION", bool indra = false)
    {
        var (d, g) = await OnlineTestGame.InCycle(agenda, a: indra ? (await AppData.GetAsync()).LegalCabal("URKARAS") : null);
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: [artifact, ministry]), d));
        if (indra) g.Seats[0].State!.Cogs[2].CogId = CardDecks.Indra;          // her card in the cabal
        return (d, g);
    }

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
            Assert.All(cards, id => Assert.False(string.IsNullOrWhiteSpace(CardDecks.Text(CardDecks.Ministry(id)!)), id));
        }
        foreach (var deck in Decks.Artifacts)
        {
            var cards = CardDecks.ArtifactDeck(deck);
            Assert.Equal(12, cards.Count);
            Assert.All(cards.Select(CardDecks.Artifact), a => Assert.Contains(a!.Priority, new int?[] { 1, 2 }));
            Assert.All(cards, id => Assert.False(string.IsNullOrWhiteSpace(CardDecks.Text(CardDecks.Artifact(id)!)), id));
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
        Ok(Do(g, 1, new(OnlineCommandType.SetUp, "h-rise"), d));
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
    public async Task Before_the_setup_another_Artefact_deck_starts_its_pile_again()
    {
        var (d, g) = await InCycle();
        Ok(Do(g, 0, new(OnlineCommandType.Mechanism), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["PARASITE", "DISINFORMATION"]), d));
        Assert.Null(g.Decks);
        Ok(Do(g, 0, new(OnlineCommandType.Mechanism), d));
        Assert.Equal("PARASITE", CardDecks.Artifact(g.Decks!.Drawn)!.Deck);

        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: [null, null]), d));
        Assert.Equal("onlineDecksNone", Key(Do(g, 0, new(OnlineCommandType.Mechanism), d)));
    }
}
