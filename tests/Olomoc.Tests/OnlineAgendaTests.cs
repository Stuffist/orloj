using System.Text.Json;
using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>"Hors Bloc" panel, scenario: the agendas' setups on the mat, the decks of the scenario.</summary>
public class OnlineAgendaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 20, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<TableManifest> Manifest = new(() =>
        JsonSerializer.Deserialize<TableManifest>(File.ReadAllText(Path.Combine(AppData.WwwRoot, "data", "olomoc-table.json")), TableAssets.Json)!);

    // A game in its cycles, Bo (seat 1) is P1, agenda chosen.
    private static async Task<(AppData D, OnlineGame G)> InCycle(string agenda)
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(), slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        var g = new OnlineGame { Code = "AGENDA", Seats = { new Seat(), new Seat() } };
        Ok(Do(g, 0, new(OnlineCommandType.SetName, "Ana"), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetName, "Bo"), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: Cabal("A", ("3R06", "E03"), ("2R08", "D01"), ("1R01", "E11"))), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: Cabal("B", ("3R06", "E03"), ("2R11", "E07"), ("1R01", "E11"))), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetReady, Flag: true), d));
        g.Step = OnlineStep.Agenda;
        g.First = 1;
        Ok(Do(g, 1, new(OnlineCommandType.ChooseAgenda, agenda), d));
        return (d, g);
    }

    private static OnlineResult Do(OnlineGame g, int seat, OnlineCommand c, AppData d, int seed = 1) =>
        OnlineRules.Apply(g, seat, c, d.Catalog, new Random(seed), Now, Manifest.Value);

    private static void Ok(OnlineResult r) => Assert.True(r.Ok, string.Join(", ", r.Problems.Select(p => p.Key)));

    private static string Key(OnlineResult r) => Assert.Single(r.Problems).Key;

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

    [Fact]
    public async Task Placer_puts_the_agendas_elements_down_numbered_and_colored()
    {
        var (d, g) = await InCycle("LOST_HYPOGEUM");
        Ok(Do(g, 0, new(OnlineCommandType.PlaceAgenda, "rise"), d));
        var doors = g.Table!.Pieces.Where(p => p.Label == "door").ToList();
        Assert.Equal(["2", "3", "4"], doors.Select(p => p.Number));
        var centre = TableManifest.BoardSize / 2;
        Assert.Equal((centre, centre), (doors[0].X, doors[0].Y));
        Assert.True(doors[1].X > centre && doors[1].Y < centre);        // 3: up and to the right
        Assert.All(doors, p => Assert.Equal((PieceKind.Marker, "objective"), (p.Kind, p.Ref)));
        Assert.Equal("agendaPlaced", g.Log[^1].Key);

        // Placing again adds them again; the spectator sees them.
        Ok(Do(g, 1, new(OnlineCommandType.PlaceAgenda, "fall"), d));
        Assert.Equal(6, OnlineRules.View(g, null, new HashSet<int>()).Table!.Pieces.Count(p => p.Label == "door"));

        // Batteries: player 1's (Bo, seat 1) and player 2's colors.
        var (d2, quantar) = await InCycle("QUANTAR_BATTERIES");
        Ok(Do(quantar, 0, new(OnlineCommandType.PlaceAgenda, "players"), d2));
        Assert.Equal([[1], [0]], quantar.Table!.Pieces.Select(p => p.Claims.ToArray()));

        // Alone in the dark: 4 Bile zones under 4 secrets.
        var (d3, dark) = await InCycle("ALONE_IN_THE_DARK");
        Ok(Do(dark, 0, new(OnlineCommandType.PlaceAgenda, "drawing"), d3));
        Assert.Equal(4, dark.Table!.Pieces.Count(p => p.Kind == PieceKind.Terrain));
        Assert.Equal(4, dark.Table.Pieces.Count(p => p.Label == "secret"));
    }

    [Fact]
    public async Task Placer_refused_without_agenda_or_placement_and_outside_the_game()
    {
        var (d, g) = await InCycle("VISITS");
        Assert.Equal("onlineAgendaVariant", Key(Do(g, 0, new(OnlineCommandType.PlaceAgenda, "centre"), d)));     // nothing to place
        var (d2, prospection) = await InCycle("PROSPECTION");
        Assert.Equal("onlineAgendaVariant", Key(Do(prospection, 0, new(OnlineCommandType.PlaceAgenda, "moon"), d2)));
        prospection.Agenda = null;
        Assert.Equal("onlineAgendaNone", Key(Do(prospection, 0, new(OnlineCommandType.PlaceAgenda, "h-rise"), d2)));
        var (d3, finished) = await InCycle("PROSPECTION");
        finished.Step = OnlineStep.Finished;
        Assert.Equal("onlineStep", Key(Do(finished, 0, new(OnlineCommandType.PlaceAgenda, "h-rise"), d3)));
    }

    [Fact]
    public async Task La_Gargoule_is_not_offered_online()
    {
        Assert.DoesNotContain("GARGOYLE", OnlineRules.OnlineAgendas);
        Assert.Equal(Agendas.All.Count - 1, OnlineRules.OnlineAgendas.Count);
        var (d, g) = await InCycle("PROSPECTION");
        g.Step = OnlineStep.Agenda;
        Assert.Equal("scenario", Key(Do(g, 1, new(OnlineCommandType.ChooseAgenda, "GARGOYLE"), d)));
        for (var seed = 0; seed < 40; seed++)
        {
            g.Step = OnlineStep.Agenda;
            Ok(Do(g, 1, new(OnlineCommandType.DrawAgenda), d, seed));
            Assert.NotEqual("GARGOYLE", g.Agenda!.Id);
        }
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
        Ok(Do(drawn, 1, new(OnlineCommandType.DrawAgenda), d2, seed: 7));
        Assert.Contains(drawn.Agenda!.Artifact, Decks.Artifacts);
        Assert.Contains(drawn.Agenda.Ministry, Decks.Ministries);
    }
}
