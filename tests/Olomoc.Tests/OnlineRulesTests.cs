using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Online game: guided steps, seats, secret cabals, tracking of the sides (real card catalog).</summary>
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
            Cabal("A", ("3R06", "E03" /* Phéromones */), ("2R08", "D01" /* Carapace */), ("1R01", "E11" /* Harmonium */)),
            Cabal("B", ("3R06", "E03" /* Phéromones */), ("2R11", "E07" /* Ration */), ("1R01", "E11" /* Harmonium */)));
    }

    private static OnlineGame NewGame() => new() { Code = "ABCDEF", Seats = { new Seat(), new Seat() } };

    private static OnlineResult Do(OnlineGame g, int seat, OnlineCommand c, CatalogService catalog, int seed = 1) =>
        OnlineRules.Apply(g, seat, c, catalog, new Random(seed), Now);

    private static void Ok(OnlineResult r) => Assert.True(r.Ok, string.Join(", ", r.Problems.Select(p => p.Key)));

    private static string Key(OnlineResult r) => Assert.Single(r.Problems).Key;

    /// <summary>Two named players, cabals chosen and ready: the game is at the initiative.</summary>
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

    /// <summary>Game started, P1 drawn, agenda chosen: first cycle.</summary>
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
    public async Task Lobby_cabals_secret_then_revealed_when_both_are_ready()
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

        // Bo does not see Ana's cabal (nor her faction), Ana sees her own; a spectator none.
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
    public async Task Illegal_or_unregistered_cabal_refused()
    {
        var (d, a, b) = await Cabals();
        var g = NewGame();
        var illegal = a with { Cogs = a.Cogs.Take(2).ToList() };
        var r = Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: illegal), d.Catalog);
        Assert.NotEmpty(r.Problems);
        Assert.All(r.Problems, p => Assert.Equal("cabalIllegal", p.Key));
        Assert.Null(g.Seats[0].Cabal);

        // Tournament table: only the registered cabals (same cards) are allowed.
        g.Seats[1].AllowedCabals.Add(a);
        Assert.Equal("onlineCabalNotRegistered", Key(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: b), d.Catalog)));
        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: a with { Name = "Renommée" }), d.Catalog));
    }

    [Fact]
    public async Task Commands_outside_their_step_refused()
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
    public async Task Initiative_five_dice_at_5_plus_roll_again_on_a_tie()
    {
        var (d, g) = await Started();
        // Forced tie: same dice on both sides (same seed) → roll again.
        Ok(Do(g, 0, new(OnlineCommandType.RollInitiative), d.Catalog, seed: 7));
        Assert.Equal("onlineRolled", Key(Do(g, 0, new(OnlineCommandType.RollInitiative), d.Catalog)));
        Ok(Do(g, 1, new(OnlineCommandType.RollInitiative), d.Catalog, seed: 7));
        Assert.Equal(OnlineStep.Initiative, g.Step);
        Assert.All(g.Seats, s => Assert.Null(s.Initiative));
        Assert.Contains(g.Log, e => e.Key == "initiativeTie");

        // Different seeds until there is a winner.
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
    public async Task Agenda_chosen_or_drawn_by_P1_only()
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
    public async Task Each_player_tracks_their_side_from_the_last_revision()
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

        // Update starting from an outdated revision: refused.
        Assert.Equal("onlineStale", Key(Do(g, 0, new(OnlineCommandType.UpdateState, State: session.State, BasedOn: revision), d.Catalog)));

        // Undo: the previous state comes back, only once.
        Ok(Do(g, 0, new(OnlineCommandType.Undo), d.Catalog));
        Assert.Equal(0, g.Seats[0].State!.VictoryPoints);
        Assert.Equal("onlineNothingToUndo", Key(Do(g, 0, new(OnlineCommandType.Undo), d.Catalog)));
        Assert.Equal("onlineNothingToUndo", Key(Do(g, 1, new(OnlineCommandType.Undo), d.Catalog)));
    }

    [Fact]
    public async Task Inconsistent_state_refused()
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
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[1].Checked = n.Cogs[1].LifeLine.Select(_ => true).Append(true).ToList())));   // more boxes than HP
        Assert.Equal("onlineState", Key(Send(n => n.Bile = 4)));
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[0].ExtraEquipmentId = "E02")));

        // The cycle and the name stay the server's, whatever the player sends.
        Ok(Send(n => { n.Cycle = 3; n.Name = "Pirate"; }));
        Assert.Equal(1, s.State!.Cycle);
        Assert.Equal("A", s.State.Name);
    }

    [Fact]
    public async Task End_of_cycle_when_both_confirm_then_end_of_game()
    {
        var (d, g) = await InCycle();
        var s = g.Seats[1];
        var next = Copy(s.State!);
        next.Cogs[0].Actions = 0;
        next.Cogs[0].Darkness = 3;      // corrupted: +1 Bile at the end of the cycle
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

        // No corrupted cog left: the Bile does not reach 3, the cycles go to the end.
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
    public async Task Bile_at_3_game_over_no_more_end_of_cycle()
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
    public async Task Hunt_shared_by_both_players_end_at_3()
    {
        var (d, g) = await InCycle();
        void Hunt(int seat, int hunt)
        {
            var s = g.Seats[seat];
            var next = Copy(s.State!);
            next.Hunt = hunt;
            Ok(Do(g, seat, new(OnlineCommandType.UpdateState, State: next, BasedOn: s.StateRevision), d.Catalog));
        }

        Hunt(0, 2);
        Assert.Equal(2, OnlineRules.View(g, 1, new HashSet<int>()).Hunt);
        Assert.False(OnlineRules.View(g, 1, new HashSet<int>()).Over);
        Hunt(1, 1);                                         // 2 + 1: hunt at 3, end for both
        var view = OnlineRules.View(g, null, new HashSet<int>());
        Assert.Equal(GameSession.MaxHunt, view.Hunt);
        Assert.True(view.Over);
        Assert.All(view.Seats, s => Assert.False(s.Over));  // nobody lost (Bile)
        Assert.Equal("onlineOver", Key(Do(g, 0, new(OnlineCommandType.SetEndCycle, Flag: true), d.Catalog)));

        Hunt(1, 0);                                         // correction: the game resumes
        Assert.False(OnlineRules.View(g, 0, new HashSet<int>()).Over);
    }

    [Fact]
    public async Task View_without_tokens_or_the_others_allowed_cabals()
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
