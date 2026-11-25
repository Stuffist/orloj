using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Shared dice roller of an online game: action and reaction, open to both players.</summary>
public class OnlineDiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);

    private static async Task<(AppData D, OnlineGame G)> InCycle()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(), slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        var g = new OnlineGame { Code = "DICE01", Seats = { new Seat(), new Seat() } };
        Ok(Do(g, 0, new(OnlineCommandType.SetName, "Ana"), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetName, "Bo"), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: Cabal("A", ("3R06", "E03"), ("2R08", "D01"), ("1R01", "E11"))), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: Cabal("B", ("3R06", "E03"), ("2R11", "E07"), ("1R01", "E11"))), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetReady, Flag: true), d));
        return (d, g);
    }

    private static OnlineResult Do(OnlineGame g, int seat, OnlineCommand c, AppData d, int seed = 7) =>
        OnlineRules.Apply(g, seat, c, d.Catalog, new Random(seed), Now);

    private static void Ok(OnlineResult r) => Assert.True(r.Ok, string.Join(", ", r.Problems.Select(p => p.Key)));

    private static string Key(OnlineResult r) => Assert.Single(r.Problems).Key;

    private static List<int> Expected(int seed, int count)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => random.Next(1, 7)).ToList();
    }

    [Fact]
    public void Successes_counted_at_each_threshold()
    {
        int[] faces = [6, 5, 4, 3, 2, 1];
        Assert.Equal([2, 3, 4, 5], new[] { 5, 4, 3, 2 }.Select(t => DiceRules.Successes(faces, t)));
    }

    [Fact]
    public async Task Either_player_rolls_rereads_and_rerolls_either_slot_seen_by_everyone()
    {
        var (d, g) = await InCycle();
        Ok(Do(g, 0, new(OnlineCommandType.RollDice, DiceRules.Action, Count: 4), d, seed: 3));
        var action = g.Dice![DiceRules.Action];
        Assert.Equal(Expected(3, 4), action.Faces);
        Assert.Equal((DiceRules.DefaultThreshold, 0, 0), (action.Threshold, action.Seat, action.Rerolls));
        Assert.Equal("dice", g.Log[^1].Key);
        Assert.Equal(DiceRules.Action, g.Log[^1].Args![0]);

        // Read at 3+ (support and charge), by the other player: the faces stay.
        Ok(Do(g, 1, new(OnlineCommandType.SetDiceThreshold, DiceRules.Action, Threshold: 3), d));
        Assert.Equal(Expected(3, 4), action.Faces);
        Assert.Equal(3, action.Threshold);

        // Reroll of the failures (trying fate): the successes at 3+ stay, the others are new; counted.
        var before = action.Faces.ToList();
        Ok(Do(g, 1, new(OnlineCommandType.RerollDice, DiceRules.Action), d, seed: 11));
        var fresh = new Random(11);
        Assert.Equal(before.Select(f => f >= 3 ? f : fresh.Next(1, 7)), action.Faces);
        Assert.Equal((1, 1, 3), (action.Rerolls, action.Seat, action.Threshold));
        Assert.Equal("diceReroll", g.Log[^1].Key);

        // The reaction under it, by the other player; the spectator sees both.
        Ok(Do(g, 1, new(OnlineCommandType.RollDice, DiceRules.Reaction, Count: 2, Threshold: 4), d));
        var view = OnlineRules.View(g, null, new HashSet<int>()).Dice!;
        Assert.Equal((4, 2, 4), (view[DiceRules.Action].Faces.Count, view[DiceRules.Reaction].Faces.Count, view[DiceRules.Reaction].Threshold));

        // A new roll starts again: no reroll yet, the slot's threshold kept.
        Ok(Do(g, 0, new(OnlineCommandType.RollDice, DiceRules.Action, Count: 1), d));
        Assert.Equal((1, 0, 3), (g.Dice[DiceRules.Action].Faces.Count, g.Dice[DiceRules.Action].Rerolls, g.Dice[DiceRules.Action].Threshold));
    }

    [Fact]
    public async Task Without_failures_a_reroll_changes_nothing()
    {
        var (d, g) = await InCycle();
        Ok(Do(g, 0, new(OnlineCommandType.RollDice, DiceRules.Action, Count: 5, Threshold: 2), d));
        g.Dice![DiceRules.Action].Faces = [6, 5, 4, 3, 2];
        Ok(Do(g, 1, new(OnlineCommandType.RerollDice, DiceRules.Action), d));
        Assert.Equal([6, 5, 4, 3, 2], g.Dice[DiceRules.Action].Faces);
        Assert.Equal(0, g.Dice[DiceRules.Action].Rerolls);
    }

    [Fact]
    public async Task Rolls_checked_and_only_during_the_game()
    {
        var (d, g) = await InCycle();
        Assert.Equal("onlineDiceCount", Key(Do(g, 0, new(OnlineCommandType.RollDice, DiceRules.Action, Count: 0), d)));
        Assert.Equal("onlineDiceCount", Key(Do(g, 0, new(OnlineCommandType.RollDice, DiceRules.Action, Count: DiceRules.MaxDice + 1), d)));
        Assert.Equal("onlineDiceCount", Key(Do(g, 0, new(OnlineCommandType.RollDice, DiceRules.Action), d)));
        Assert.Equal("onlineDiceThreshold", Key(Do(g, 0, new(OnlineCommandType.RollDice, DiceRules.Action, Count: 3, Threshold: 6), d)));
        Assert.Equal("onlineDiceThreshold", Key(Do(g, 0, new(OnlineCommandType.RollDice, DiceRules.Action, Count: 3, Threshold: 1), d)));
        Assert.Equal("onlineDice", Key(Do(g, 0, new(OnlineCommandType.RollDice, "bonus", Count: 3), d)));
        Assert.Equal("onlineDice", Key(Do(g, 0, new(OnlineCommandType.RerollDice, DiceRules.Reaction), d)));        // nothing rolled yet
        Assert.Equal("onlineDice", Key(Do(g, 0, new(OnlineCommandType.SetDiceThreshold, DiceRules.Reaction, Threshold: 4), d)));

        g.Step = OnlineStep.Finished;
        Assert.Equal("onlineStep", Key(Do(g, 0, new(OnlineCommandType.RollDice, DiceRules.Action, Count: 3), d)));
        var lobby = new OnlineGame { Code = "LOBBY1", Seats = { new Seat(), new Seat() } };
        Assert.Equal("onlineStep", Key(Do(lobby, 0, new(OnlineCommandType.RollDice, DiceRules.Action, Count: 3), d)));
    }
}
