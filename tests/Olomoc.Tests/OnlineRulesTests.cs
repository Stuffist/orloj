using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;
using static Olomoc.Tests.OnlineTestGame;

namespace Olomoc.Tests;

/// <summary>Online game: guided steps, seats, secret cabals, tracking of the sides (real card catalog).</summary>
public class OnlineRulesTests
{
    /// <summary>Game started, Ana P1, agenda chosen: first cycle.</summary>
    private static Task<(AppData D, OnlineGame G)> InCycle() => OnlineTestGame.InCycle(first: 0);

    private static GameState Copy(GameState s) => GameSession.FromJson(GameSession.ToJson(s))!;

    [Fact]
    public async Task Lobby_cabals_secret_then_revealed_when_both_are_ready()
    {
        var (d, a, b) = await Cabals();
        var g = NewGame();
        Ok(Do(g, 0, new(OnlineCommandType.SetName, "Ana"), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetName, "Bo"), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: a), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d));

        // Bo does not see Ana's cabal (nor her faction), Ana sees her own.
        var bo = OnlineRules.View(g, 1, new HashSet<int>());
        Assert.Null(bo.Seats[0].Cabal);
        Assert.Null(bo.Seats[0].Faction);
        Assert.NotNull(OnlineRules.View(g, 0, new HashSet<int>()).Seats[0].Cabal);

        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: b), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetReady, Flag: true), d));
        Assert.Equal(OnlineStep.Initiative, g.Step);
        Assert.NotNull(OnlineRules.View(g, 1, new HashSet<int>()).Seats[0].Cabal);
    }

    [Fact]
    public async Task Illegal_or_unregistered_cabal_refused()
    {
        var (d, a, b) = await Cabals();
        var g = NewGame();
        var illegal = a with { Cogs = a.Cogs.Take(1).ToList() };
        Assert.All(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: illegal), d).Problems, p => Assert.Equal("cabalIllegal", p.Key));
        var teams = a with { Cogs = a.Cogs.Take(2).ToList() };                                     // 2 cogs: 2 v 2 only
        Assert.Contains(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: teams), d).Problems, p => p.Key == "cabalCogs");

        // Tournament table: only the registered cabals (same cards) are allowed.
        g.Seats[1].AllowedCabals.Add(a);
        Assert.Equal("onlineCabalNotRegistered", Key(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: b), d)));
        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: a with { Name = "Renommée" }), d));
    }

    [Fact]
    public async Task Initiative_roll_again_on_a_tie()
    {
        var (d, g) = await Started();
        // Forced tie: same dice on both sides (same seed) → roll again.
        Ok(Do(g, 0, new(OnlineCommandType.RollInitiative), d, seed: 7));
        Ok(Do(g, 1, new(OnlineCommandType.RollInitiative), d, seed: 7));
        Assert.Equal(OnlineStep.Initiative, g.Step);

        for (var seed = 1; g.Step == OnlineStep.Initiative; seed += 2)
        {
            Ok(Do(g, 0, new(OnlineCommandType.RollInitiative), d, seed));
            Ok(Do(g, 1, new(OnlineCommandType.RollInitiative), d, seed + 1));
        }
        var winner = g.First!.Value;
        Assert.True(OnlineRules.Successes(g.Seats[winner].Initiative!) > OnlineRules.Successes(g.Seats[1 - winner].Initiative!));
    }

    [Fact]
    public async Task Each_player_tracks_their_side_from_the_last_revision()
    {
        var (d, g) = await InCycle();
        var revision = g.Seats[0].StateRevision;
        var session = new GameSession(Copy(g.Seats[0].State!));
        session.AdjustVictoryPoints(3);

        Ok(Do(g, 0, new(OnlineCommandType.UpdateState, State: session.State, BasedOn: revision), d));
        Assert.Equal(3, g.Seats[0].State!.VictoryPoints);
        Assert.Equal("onlineStale", Key(Do(g, 0, new(OnlineCommandType.UpdateState, State: session.State, BasedOn: revision), d)));

        // Undo: the previous state comes back, only once.
        Ok(Do(g, 0, new(OnlineCommandType.Undo), d));
        Assert.Equal(0, g.Seats[0].State!.VictoryPoints);
        Assert.Equal("onlineNothingToUndo", Key(Do(g, 0, new(OnlineCommandType.Undo), d)));
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
            return Do(g, 0, new(OnlineCommandType.UpdateState, State: next, BasedOn: s.StateRevision), d);
        }
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[0].CogId = "3R07")));
        Assert.Equal("onlineState", Key(Send(n => n.Cogs[1].Darkness = 4)));
        Assert.Equal("onlineState", Key(Send(n => n.Bile = 4)));

        // The cycle and the name stay the server's, whatever the player sends.
        Ok(Send(n => { n.Cycle = 3; n.Name = "Pirate"; }));
        Assert.Equal((1, "A"), (s.State!.Cycle, s.State.Name));
    }

    [Fact]
    public async Task End_of_cycle_when_both_confirm()
    {
        var (d, g) = await InCycle();
        var s = g.Seats[1];
        var next = Copy(s.State!);
        next.Cogs[0].Actions = 0;
        next.Cogs[0].Darkness = 3;      // corrupted: +1 Bile at the end of the cycle
        Ok(Do(g, 1, new(OnlineCommandType.UpdateState, State: next, BasedOn: s.StateRevision), d));

        Ok(Do(g, 0, new(OnlineCommandType.SetEndCycle, Flag: true), d));
        Assert.Equal(1, g.Cycle);
        Ok(Do(g, 1, new(OnlineCommandType.SetEndCycle, Flag: true), d));

        Assert.Equal(2, g.Cycle);
        Assert.Equal(GameSession.ActionsPerCycle, s.State!.Cogs[0].Actions);
        Assert.Equal(1, s.State.Bile);
    }

    [Fact]
    public async Task View_without_tokens_or_the_others_allowed_cabals()
    {
        var (_, a, _) = await Cabals();
        var g = NewGame();
        g.Seats[0].TokenHash = "secret";
        g.Seats[0].AllowedCabals.Add(a);
        var view = OnlineRules.View(g, 1, new HashSet<int> { 0 });
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(view, TournamentJson.Options));
        Assert.Empty(view.Seats[0].AllowedCabals);
    }
}
