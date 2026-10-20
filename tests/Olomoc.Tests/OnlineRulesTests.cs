using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Partie en ligne : étapes guidées, sièges, cabales secrètes, suivi des camps (référentiel réel).</summary>
public class OnlineRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 20, 0, 0, TimeSpan.Zero);

    private static async Task<(AppData D, CabalExport A, CabalExport B)> Cabals()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(),
                slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        return (d,
            Cabal("A", ("3R06", "Phéromones"), ("2R08", "Carapace"), ("1R01", "Harmonium")),
            Cabal("B", ("3R06", "Dose de Mélisse"), ("2R04", "Phéromones"), ("1R01", "Passe-droit du Census")));
    }

    private static OnlineGame NewGame() => new() { Code = "ABCDEF", Seats = { new Seat(), new Seat() } };

    private static OnlineResult Do(OnlineGame g, int seat, OnlineCommand c, CatalogService catalog, int seed = 1) =>
        OnlineRules.Apply(g, seat, c, catalog, new Random(seed), Now);

    private static void Ok(OnlineResult r) => Assert.True(r.Ok, string.Join(", ", r.Problems.Select(p => p.Key)));

    private static string Key(OnlineResult r) => Assert.Single(r.Problems).Key;

    /// <summary>Deux joueurs nommés, cabales choisies et prêts : la partie est à l'initiative.</summary>
    private static async Task<(AppData D, OnlineGame G)> Started()
    {
        var (d, a, b) = await Cabals();
        var g = NewGame();
        Ok(Do(g, 0, new(OnlineCommandType.SetName, "Ana"), d.Catalog));
        Ok(Do(g, 1, new(OnlineCommandType.SetName, "Bo"), d.Catalog));
        Ok(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: a), d.Catalog));
        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: b), d.Catalog));
        Ok(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d.Catalog));
        Ok(Do(g, 1, new(OnlineCommandType.SetReady, Flag: true), d.Catalog));
        return (d, g);
    }

    /// <summary>Partie lancée, J1 tiré, agenda choisi : premier cycle.</summary>
    private static async Task<(AppData D, OnlineGame G)> InCycle()
    {
        var (d, g) = await Started();
        g.Step = OnlineStep.Agenda;
        g.First = 0;
        Ok(Do(g, 0, new(OnlineCommandType.ChooseAgenda, "VISITS"), d.Catalog));
        return (d, g);
    }

    private static GameState Copy(GameState s) => GameSession.FromJson(GameSession.ToJson(s))!;

    [Fact]
    public async Task Salon_cabales_secretes_puis_revelees_quand_les_deux_sont_prets()
    {
        var (d, a, b) = await Cabals();
        var g = NewGame();
        Ok(Do(g, 0, new(OnlineCommandType.SetName, "Ana"), d.Catalog));
        Assert.Equal("nameTaken", Key(Do(g, 1, new(OnlineCommandType.SetName, " ana "), d.Catalog)));
        Ok(Do(g, 1, new(OnlineCommandType.SetName, "Bo"), d.Catalog));
        Assert.Equal("onlineNotReady", Key(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d.Catalog)));
        Ok(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: a), d.Catalog));
        Ok(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d.Catalog));
        Assert.Equal("onlineReady", Key(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: b), d.Catalog)));

        // Bo ne voit pas la cabale d'Ana (ni sa faction), Ana voit la sienne ; un spectateur aucune.
        var bo = OnlineRules.View(g, 1, new HashSet<int>());
        Assert.True(bo.Seats[0].HasCabal);
        Assert.Null(bo.Seats[0].Cabal);
        Assert.Null(bo.Seats[0].Faction);
        Assert.NotNull(OnlineRules.View(g, 0, new HashSet<int>()).Seats[0].Cabal);
        Assert.Null(OnlineRules.View(g, null, new HashSet<int>()).Seats[0].Cabal);

        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: b), d.Catalog));
        Ok(Do(g, 1, new(OnlineCommandType.SetReady, Flag: true), d.Catalog));
        Assert.Equal(OnlineStep.Initiative, g.Step);
        Assert.All(g.Seats, s => Assert.Equal(3, s.State!.Cogs.Count));
        Assert.Equal("A", g.Seats[0].State!.Name);
        Assert.NotNull(OnlineRules.View(g, 1, new HashSet<int>()).Seats[0].Cabal);
        Assert.NotNull(OnlineRules.View(g, null, new HashSet<int>()).Seats[1].State);
    }

    [Fact]
    public async Task Cabale_illegale_ou_non_inscrite_refusee()
    {
        var (d, a, b) = await Cabals();
        var g = NewGame();
        var illegal = a with { Cogs = a.Cogs.Take(2).ToList() };
        var r = Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: illegal), d.Catalog);
        Assert.NotEmpty(r.Problems);
        Assert.All(r.Problems, p => Assert.Equal("cabalIllegal", p.Key));
        Assert.Null(g.Seats[0].Cabal);

        // Table de tournoi : seules les cabales inscrites (mêmes cartes) sont permises.
        g.Seats[1].AllowedCabals.Add(a);
        Assert.Equal("onlineCabalNotRegistered", Key(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: b), d.Catalog)));
        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: a with { Name = "Renommée" }), d.Catalog));
    }

    [Fact]
    public async Task Commandes_hors_de_leur_etape_refusees()
    {
        var (d, g) = await Started();
        Assert.Equal("onlineStep", Key(Do(g, 0, new(OnlineCommandType.SetName, "Autre"), d.Catalog)));
        Assert.Equal("onlineStep", Key(Do(g, 0, new(OnlineCommandType.ChooseAgenda, "VISITS"), d.Catalog)));
        Assert.Equal("onlineStep", Key(Do(g, 0, new(OnlineCommandType.SetEndCycle, Flag: true), d.Catalog)));
        var revision = g.Revision;
        Assert.Equal("onlineStep", Key(Do(g, 0, new(OnlineCommandType.UpdateState, State: g.Seats[0].State, BasedOn: g.Seats[0].StateRevision), d.Catalog)));
        Assert.Equal(revision, g.Revision);
    }

    [Fact]
    public async Task Initiative_cinq_des_a_5_plus_relance_en_cas_d_egalite()
    {
        var (d, g) = await Started();
        // Égalité forcée : mêmes dés des deux côtés (même graine) → relance.
        Ok(Do(g, 0, new(OnlineCommandType.RollInitiative), d.Catalog, seed: 7));
        Assert.Equal("onlineRolled", Key(Do(g, 0, new(OnlineCommandType.RollInitiative), d.Catalog)));
        Ok(Do(g, 1, new(OnlineCommandType.RollInitiative), d.Catalog, seed: 7));
        Assert.Equal(OnlineStep.Initiative, g.Step);
        Assert.All(g.Seats, s => Assert.Null(s.Initiative));
        Assert.Contains(g.Log, e => e.Key == "initiativeTie");

        // Graines différentes jusqu'à départager.
        for (var seed = 1; g.Step == OnlineStep.Initiative; seed += 2)
        {
            Ok(Do(g, 0, new(OnlineCommandType.RollInitiative), d.Catalog, seed));
            Ok(Do(g, 1, new(OnlineCommandType.RollInitiative), d.Catalog, seed + 1));
        }
        Assert.Equal(OnlineStep.Agenda, g.Step);
        var winner = g.First!.Value;
        Assert.True(OnlineRules.Successes(g.Seats[winner].Initiative!) > OnlineRules.Successes(g.Seats[1 - winner].Initiative!));
        Assert.All(g.Seats[0].Initiative!, die => Assert.InRange(die, 1, 6));
    }

    [Fact]
    public async Task Agenda_choisi_ou_tire_par_J1_seulement()
    {
        var (d, g) = await Started();
        g.Step = OnlineStep.Agenda;
        g.First = 1;
        Assert.Equal("onlineNotFirst", Key(Do(g, 0, new(OnlineCommandType.DrawAgenda), d.Catalog)));
        Assert.Equal("scenario", Key(Do(g, 1, new(OnlineCommandType.ChooseAgenda, "OTHER"), d.Catalog)));
        Ok(Do(g, 1, new(OnlineCommandType.DrawAgenda), d.Catalog));
        Assert.Contains(g.Agenda!.Id, Agendas.All);
        Assert.Equal(OnlineStep.Cycle, g.Step);
    }

    [Fact]
    public async Task Chaque_joueur_suit_son_camp_a_partir_de_la_derniere_revision()
    {
        var (d, g) = await InCycle();
        var revision = g.Seats[0].StateRevision;
        var next = Copy(g.Seats[0].State!);
        var session = new GameSession(next);
        session.Wound(0, 2, attack: true, ignoreProtection: false);
        session.AdjustVictoryPoints(3);

        var r = Do(g, 0, new(OnlineCommandType.UpdateState, State: session.State, BasedOn: revision, Note: "Bohdan : 2 blessures"), d.Catalog);
        Ok(r);
        Assert.Equal(revision + 1, r.StateRevision);
        Assert.Equal(3, g.Seats[0].State!.VictoryPoints);
        Assert.Equal("Bohdan : 2 blessures", g.Log[^1].Args![0]);
        Assert.Equal(0, g.Seats[1].State!.VictoryPoints);

        // Mise à jour partie d'une révision dépassée : refusée.
        Assert.Equal("onlineStale", Key(Do(g, 0, new(OnlineCommandType.UpdateState, State: session.State, BasedOn: revision), d.Catalog)));

        // Annulation : l'état précédent revient, une seule fois.
        Ok(Do(g, 0, new(OnlineCommandType.Undo), d.Catalog));
        Assert.Equal(0, g.Seats[0].State!.VictoryPoints);
        Assert.Equal("onlineNothingToUndo", Key(Do(g, 0, new(OnlineCommandType.Undo), d.Catalog)));
        Assert.Equal("onlineNothingToUndo", Key(Do(g, 1, new(OnlineCommandType.Undo), d.Catalog)));
    }

    [Fact]
    public async Task Etat_incoherent_refuse()
    {
        var (d, g) = await InCycle();
        var s = g.Seats[0];
        OnlineResult Send(Action<GameState> change)
        {
            var next = Copy(s.State!);
            change(next);
            return Do(g, 0, new(OnlineCommandType.UpdateState, State: next, BasedOn: s.StateRevision), d.Catalog);
        }
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[0].CogId = "3R07")));
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[0].EquipmentId = "E01")));
        Assert.Equal("onlineState", Key(Send(n => n.Cogs.RemoveAt(2))));
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[1].LifeLine.Add(HealthPointType.S))));
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[1].Darkness = 4)));
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[1].Actions = 5)));
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[1].Checked = n.Cogs[1].LifeLine.Select(_ => true).Append(true).ToList())));   // plus de cases que de PV
        Assert.Equal("onlineState", Key(Send(n => n.Bile = 4)));
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[0].ExtraEquipmentId = "E02")));

        // Le cycle et le nom restent ceux du serveur, quoi qu'envoie le joueur.
        Ok(Send(n => { n.Cycle = 3; n.Name = "Pirate"; }));
        Assert.Equal(1, s.State!.Cycle);
        Assert.Equal("A", s.State.Name);
    }

    [Fact]
    public async Task Fin_de_cycle_quand_les_deux_confirment_puis_fin_de_partie()
    {
        var (d, g) = await InCycle();
        var s = g.Seats[1];
        var next = Copy(s.State!);
        next.Cogs[0].Actions = 0;
        next.Cogs[0].Darkness = 3;      // corrompu : +1 Fiel en fin de cycle
        Ok(Do(g, 1, new(OnlineCommandType.UpdateState, State: next, BasedOn: s.StateRevision), d.Catalog));

        Ok(Do(g, 0, new(OnlineCommandType.SetEndCycle, Flag: true), d.Catalog));
        Assert.Equal(1, g.Cycle);
        Ok(Do(g, 0, new(OnlineCommandType.SetEndCycle, Flag: false), d.Catalog));
        Ok(Do(g, 1, new(OnlineCommandType.SetEndCycle, Flag: true), d.Catalog));
        Assert.Equal(1, g.Cycle);
        Ok(Do(g, 0, new(OnlineCommandType.SetEndCycle, Flag: true), d.Catalog));

        Assert.Equal(2, g.Cycle);
        Assert.All(g.Seats, x => Assert.Equal(2, x.State!.Cycle));
        Assert.Equal(GameSession.ActionsPerCycle, s.State!.Cogs[0].Actions);
        Assert.Equal(1, s.State.Bile);
        Assert.All(g.Seats, x => Assert.False(x.EndCycleReady));
        Assert.Equal("onlineNothingToUndo", Key(Do(g, 1, new(OnlineCommandType.Undo), d.Catalog)));

        // Plus de rouage corrompu : le Fiel n'atteint pas 3, les cycles vont jusqu'au bout.
        next = Copy(s.State);
        next.Cogs[0].Darkness = 0;
        Ok(Do(g, 1, new(OnlineCommandType.UpdateState, State: next, BasedOn: s.StateRevision), d.Catalog));
        for (var cycle = 2; cycle <= GameSession.Cycles; cycle++)
        {
            Ok(Do(g, 0, new(OnlineCommandType.SetEndCycle, Flag: true), d.Catalog));
            Ok(Do(g, 1, new(OnlineCommandType.SetEndCycle, Flag: true), d.Catalog));
        }
        Assert.Equal(OnlineStep.Finished, g.Step);
        Assert.True(OnlineRules.View(g, 0, new HashSet<int>()).Over);
    }

    [Fact]
    public async Task Fiel_a_3_partie_terminee_plus_de_fin_de_cycle()
    {
        var (d, g) = await InCycle();
        var s = g.Seats[0];
        var next = Copy(s.State!);
        next.Bile = GameSession.MaxBile;
        Ok(Do(g, 0, new(OnlineCommandType.UpdateState, State: next, BasedOn: s.StateRevision), d.Catalog));
        Assert.True(OnlineRules.View(g, 1, new HashSet<int>()).Over);
        Assert.Equal("onlineOver", Key(Do(g, 1, new(OnlineCommandType.SetEndCycle, Flag: true), d.Catalog)));
    }

    [Fact]
    public async Task Vue_sans_jeton_ni_cabales_permises_des_autres()
    {
        var (d, a, _) = await Cabals();
        var g = NewGame();
        g.Seats[0].TokenHash = "secret";
        g.Seats[0].AllowedCabals.Add(a);
        var json = System.Text.Json.JsonSerializer.Serialize(OnlineRules.View(g, 1, new HashSet<int> { 0 }), TournamentJson.Options);
        Assert.DoesNotContain("secret", json);
        var view = OnlineRules.View(g, 1, new HashSet<int> { 0 });
        Assert.Empty(view.Seats[0].AllowedCabals);
        Assert.True(view.Seats[0].Connected);
        Assert.False(view.Seats[1].Connected);
        Assert.Single(OnlineRules.View(g, 0, new HashSet<int>()).Seats[0].AllowedCabals);
    }
}
