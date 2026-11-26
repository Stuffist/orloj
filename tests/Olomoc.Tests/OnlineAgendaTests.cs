using System.Text.Json;
using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;
using static Olomoc.Tests.OnlineTestGame;

namespace Olomoc.Tests;

/// <summary>"Hors Bloc" panel, scenario: the agendas' setups on the mat, the decks of the scenario.</summary>
public class OnlineAgendaTests
{
    [Fact]
    public void Every_agenda_has_its_setup_inside_the_mat_with_known_pieces_and_texts()
    {
        Assert.Equal(Agendas.All.Order(), AgendaSetups.All.Select(a => a.Id).Order());
        var half = TableManifest.BoardSize / 2;
        var texts = new[] { "fr", "en" }.Select(lang =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(AppData.WwwRoot, "i18n", lang, "ui.json"))).RootElement).ToList();
        foreach (var setup in AgendaSetups.All)
        {
            Assert.All(texts, ui => Assert.True(ui.TryGetProperty($"online.agenda.note.{setup.Id}", out _), setup.Id));
            foreach (var variant in setup.Variants)
            {
                Assert.NotEmpty(variant.Elements);
                Assert.All(texts, ui => Assert.True(ui.TryGetProperty($"online.agenda.variant.{variant.Id}", out _), variant.Id));
                foreach (var e in variant.Elements)
                {
                    var (x, y) = AgendaSetups.OnMat(e);
                    Assert.InRange(x, 0, TableManifest.BoardSize);
                    Assert.InRange(y, 0, TableManifest.BoardSize);
                    Assert.True(e.Piece == PieceKind.Terrain ? Manifest.Value.TerrainOf(e.Ref) is not null : Manifest.Value.Marker(e.Ref) is not null, $"{setup.Id}: {e.Ref}");
                    Assert.All(texts, ui => Assert.True(ui.TryGetProperty($"online.agenda.element.{e.Kind}", out _), e.Kind));
                }
            }
            // An agenda's choices are different placements.
            Assert.Equal(setup.Variants.Count, setup.Variants.Select(v => v.Id).Distinct().Count());
        }
        Assert.Equal((0.0, 0.0), (AgendaSetups.OnMat(new("secret", 0, 0)).X - half, AgendaSetups.OnMat(new("secret", 0, 0)).Y - half));
    }

    // « Placer + distribuer » by P1 (Bo), the decks chosen.
    private static async Task<OnlineGame> SetUp(string agenda, string? variant)
    {
        var (d, g) = await InCycle(agenda);
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["ROGUE", "DISINFORMATION"]), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetUp, variant), d));
        return g;
    }

    [Fact]
    public async Task Placer_puts_the_agendas_elements_down_numbered_and_colored()
    {
        var g = await SetUp("LOST_HYPOGEUM", "rise");
        var doors = g.Table!.Pieces.Where(p => p.Label == "door").ToList();
        Assert.Equal(["2", "3", "4"], doors.Select(p => p.Number));
        var centre = TableManifest.BoardSize / 2;
        Assert.Equal((centre, centre), (doors[0].X, doors[0].Y));
        Assert.True(doors[1].X > centre && doors[1].Y < centre);        // 3: up and to the right
        Assert.All(doors, p => Assert.Equal((PieceKind.Marker, "objective"), (p.Kind, p.Ref)));
        Assert.Contains(g.Log, e => e.Key == "agendaPlaced");

        // Batteries: player 1's (Bo, seat 1) and player 2's colors.
        var quantar = await SetUp("QUANTAR_BATTERIES", "players");
        Assert.Equal([[1], [0]], quantar.Table!.Pieces.Select(p => p.Claims.ToArray()));

        // Alone in the dark: 4 Bile zones under 4 secrets.
        var dark = await SetUp("ALONE_IN_THE_DARK", "drawing");
        Assert.Equal(4, dark.Table!.Pieces.Count(p => p.Kind == PieceKind.Terrain));
        Assert.Equal(4, dark.Table.Pieces.Count(p => p.Label == "secret"));

        // An agenda with nothing on the mat: only the cards.
        var visits = await SetUp("VISITS", null);
        Assert.Empty(visits.Table!.Pieces);
        Assert.NotNull(visits.Decks!.Fallen);
    }

    [Fact]
    public async Task La_Gargoule_is_not_offered_online()
    {
        Assert.DoesNotContain("GARGOYLE", OnlineRules.OnlineAgendas);
        Assert.Equal(Agendas.All.Count - 1, OnlineRules.OnlineAgendas.Count);
        var (d, g) = await InCycle("PROSPECTION");
        g.Step = OnlineStep.Agenda;
        Assert.Equal("scenario", Key(Do(g, 1, new(OnlineCommandType.ChooseAgenda, "GARGOYLE"), d)));
    }

    [Fact]
    public async Task Decks_of_the_scenario_chosen_or_drawn_by_P1_and_drawn_with_a_drawn_scenario()
    {
        var (d, g) = await InCycle("PROSPECTION");
        Assert.Equal("onlineNotFirst", Key(Do(g, 0, new(OnlineCommandType.SetDecks, Decks: ["PARASITE", "INDOCTRINATION"]), d)));
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["PARASITE", "INDOCTRINATION"]), d));
        Assert.Equal(("PROSPECTION", "PARASITE", "INDOCTRINATION"), (g.Agenda!.Id, g.Agenda.Artifact, g.Agenda.Ministry));
        Assert.Equal("decks", g.Log[^1].Key);
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: [null, "DISINFORMATION"]), d));
        Assert.Equal((null, "DISINFORMATION"), (g.Agenda!.Artifact, g.Agenda.Ministry));
        Assert.Equal("onlineDecks", Key(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["DISINFORMATION", null]), d)));

        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Flag: true), d));
        Assert.Contains(g.Agenda!.Artifact, Decks.Artifacts);
        Assert.Contains(g.Agenda.Ministry, Decks.Ministries);
        Assert.Equal(g.Agenda, OnlineRules.View(g, null, new HashSet<int>()).Agenda);

        // A scenario drawn: its decks drawn with it.
        var (d2, drawn) = await InCycle("PROSPECTION");
        drawn.Step = OnlineStep.Agenda;
        Ok(Do(drawn, 1, new(OnlineCommandType.ChooseAgenda, "PROSPECTION", Flag: true), d2, seed: 7));
        Assert.Contains(drawn.Agenda!.Artifact, Decks.Artifacts);
        Assert.Contains(drawn.Agenda.Ministry, Decks.Ministries);
    }

    [Fact]
    public async Task Placer_et_distribuer_by_P1_once_then_the_decks_are_locked()
    {
        var (d, g) = await InCycle("LOST_HYPOGEUM");
        Assert.Equal("onlineDecksNone", Key(Do(g, 1, new(OnlineCommandType.SetUp, "rise"), d)));
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["ROGUE", "DISINFORMATION"]), d));
        Assert.Equal("onlineNotFirst", Key(Do(g, 0, new(OnlineCommandType.SetUp, "rise"), d)));
        Assert.Equal("onlineAgendaVariant", Key(Do(g, 1, new(OnlineCommandType.SetUp, "moon"), d)));
        Assert.Null(g.Decks);                                                  // refused: nothing changed

        Ok(Do(g, 1, new(OnlineCommandType.SetUp, "rise"), d));
        Assert.NotNull(g.Decks!.Fallen);
        Assert.Equal(("ROGUE", 12), (g.Decks.ArtifactDeck, g.Decks.Pile.Count));
        Assert.Equal("onlineDealt", Key(Do(g, 1, new(OnlineCommandType.SetUp, "rise"), d)));
        Assert.Equal("onlineDecksLocked", Key(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["PARASITE", "DISINFORMATION"]), d)));
    }

    [Fact]
    public async Task Le_Cartographe_Kadlec_joins_P1_his_activation_instead_of_a_propaganda()
    {
        var g = await SetUp("CARTOGRAPHER", null);
        var bo = g.Seats[1].State!;                                              // Bo is P1
        Assert.Equal((4, 3), (bo.Cogs.Count, bo.Cogs.Single(c => c.CogId == CardDecks.Kadlec).Rank));
        Assert.Equal(1, g.Escort);
        var hand = PlanningRules.Hand(g, 1);
        Assert.Contains($"act:{CardDecks.Kadlec}", hand);
        Assert.DoesNotContain(hand, c => c.StartsWith("prop:"));
        Assert.Equal(["prop:1"], PlanningRules.Hand(g, 0).Where(c => c.StartsWith("prop:")));
        Assert.Equal("kadlecJoins", g.Log[^1].Key);
    }

    [Fact]
    public async Task Visites_always_with_the_Rogue_deck()
    {
        var (d, g) = await InCycle("VISITS");
        Assert.Equal("ROGUE", g.Agenda!.Artifact);
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["PARASITE", "DISINFORMATION"]), d));
        Assert.Equal("ROGUE", g.Agenda.Artifact);
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Flag: true), d));
        Assert.Equal("ROGUE", g.Agenda.Artifact);
    }

    [Fact]
    public async Task Restart_goes_back_to_the_lobby_names_and_cabals_kept()
    {
        var (d, g) = await InCycle("LOST_HYPOGEUM");
        Ok(Do(g, 1, new(OnlineCommandType.SetDecks, Decks: ["ROGUE", "DISINFORMATION"]), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetUp, "rise"), d));
        var revision = g.Seats[0].StateRevision;

        Ok(Do(g, 0, new(OnlineCommandType.Restart), d));
        Assert.Equal((OnlineStep.Lobby, 1, (int?)null, (Scenario?)null), (g.Step, g.Cycle, g.First, g.Agenda));
        Assert.Equal((null, null, null, null, (int?)null), (g.Table, g.Decks, g.Planning, g.Dice, g.Escort));
        Assert.All(g.Seats, s => Assert.Equal((false, null, null), (s.Ready, s.Initiative, s.State)));
        Assert.Equal(["Ana", "Bo"], g.Seats.Select(s => s.Name));
        Assert.All(g.Seats, s => Assert.NotNull(s.Cabal));
        Assert.True(g.Seats[0].StateRevision > revision);
        Assert.Equal("restart", g.Log[^1].Key);
        Assert.Equal("onlineStep", Key(Do(g, 0, new(OnlineCommandType.Restart), d)));

        // Ready again: the game starts again.
        Ok(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetReady, Flag: true), d));
        Assert.Equal(OnlineStep.Initiative, g.Step);
        Assert.All(g.Seats, s => Assert.NotNull(s.State));

        // A confirmed result (in the statistics): no restart.
        g.Result = new([1, 2], GameEnd.FourthCycle, 0, Now);
        Assert.Equal("onlineRestartResult", Key(Do(g, 0, new(OnlineCommandType.Restart), d)));
    }
}
