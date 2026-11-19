using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Play mode: life line (rulebook p.16), Darkness and Bile (p.17), abilities (p.20), cycles (p.33-35).</summary>
public class GameSessionTests
{
    // Legal Maisel cabal: rank 1 Petra Kantor (RRRRSTSSSSSS), rank 2 Le Chasseur, rank 3 Bohdan Gross.
    private static async Task<GameSession> NewGame(string rank3Equipment = "E03" /* Phéromones */)
    {
        var d = await AppData.GetAsync();
        var state = GameSession.NewGame("Test", "MAISEL",
            new Cog?[] { d.Cog("1R01"), d.Cog("2R08"), d.Cog("3R06") },
            new Equipment?[] { d.Equipment("E11" /* Harmonium */), d.Equipment("D01" /* Carapace */), d.Equipment(rank3Equipment) });
        return new GameSession(state);
    }

    private static int SlotOf(GameSession g, string cogId) => g.State.Cogs.FindIndex(c => c.CogId == cogId);

    [Fact]
    public async Task Rulebook_examples_Petra_Kantor()
    {
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");

        // 5 wounds, one reinforced HP left to mark: 4 HP marked.
        var first = g.Wound(petra, 5, attack: true, ignoreProtection: false);
        Assert.True(first.Protected);
        Assert.Equal(4, first.Marked);

        // 3 wounds: the second HP to mark is tainted, the third wound is ignored, +1 Darkness.
        var second = g.Wound(petra, 3, attack: true, ignoreProtection: false);
        Assert.Equal((2, 1, true), (second.Marked, second.TaintedMarked, second.Stopped));
        Assert.Equal(6, g.State.Cogs[petra].Marked);
        Assert.Equal(1, g.State.Cogs[petra].Darkness);
    }

    [Fact]
    public async Task Healing_the_rightmost_marked_HP()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");
        g.Wound(bohdan, 5, attack: false, ignoreProtection: false);
        Assert.Equal(2, g.Heal(bohdan, 2));
        Assert.Equal(3, g.State.Cogs[bohdan].Marked);
        Assert.Equal(3, g.Heal(bohdan, 10));                 // no more than the marked HP
    }

    [Fact]
    public async Task Darkness_beyond_3_goes_to_the_Bile_and_defeat_at_3()
    {
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");
        Assert.Equal(0, g.AdjustDarkness(petra, 3));
        Assert.Equal(1, g.AdjustDarkness(petra, 1));
        Assert.Equal(1, g.State.Bile);

        g.AdjustDarkness(petra, 2);
        Assert.True(g.IsLost);
        Assert.Equal(0, g.EndCycle());                       // game over: no more end of cycle
    }

    [Fact]
    public async Task End_of_cycle_Bile_per_corrupted_cog_and_action_tokens()
    {
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");
        var bohdan = SlotOf(g, "3R06");
        g.AdjustDarkness(petra, 3);
        g.AdjustDarkness(bohdan, 3);
        g.State.Cogs[bohdan].Checked = g.State.Cogs[bohdan].LifeLine.Select(_ => true).ToList();   // out of action: no longer counts
        g.AdjustActions(petra, -3);

        Assert.Equal(1, g.EndCycle());
        Assert.Equal((1, 2), (g.State.Bile, g.State.Cycle));
        Assert.Equal(GameSession.ActionsPerCycle, g.State.Cogs[petra].Actions);
    }

    [Fact]
    public async Task Game_over_after_4_cycles()
    {
        var g = await NewGame();
        for (var i = 0; i < GameSession.Cycles; i++) g.EndCycle();
        Assert.True(g.IsFinished);
    }

    [Fact]
    public async Task Hunt_plus_1_per_cog_out_of_action_and_end_at_3()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");
        Assert.True(g.Wound(bohdan, 50, attack: false, ignoreProtection: false).HuntGained);
        Assert.Equal(1, g.State.Hunt);
        g.Heal(bohdan, 1);                                    // back in play: token removed
        Assert.Equal(0, g.State.Hunt);

        g.AdjustHunt(3);
        Assert.True(g.IsOver);
    }

    [Fact]
    public async Task Ability_limited_per_game_Pilules_stabilisantes()
    {
        var d = await AppData.GetAsync();
        var g = await NewGame(rank3Equipment: "E15" /* Pilules stabilisantes */);
        var bohdan = SlotOf(g, "3R06");
        var ability = d.Equipment("E15" /* Pilules stabilisantes */).Abilities[0];
        var key = GameSession.AbilityKey("E15", 0);

        for (var i = 0; i < 3; i++) Assert.True(g.Use(bohdan, key, ability));
        Assert.Equal(AbilityBlock.Exhausted, g.CanUse(bohdan, key, ability));
        g.EndCycle();                                         // limit per game: not reset
        Assert.Equal(0, g.UsesLeft(bohdan, key, ability));
    }

    [Fact]
    public async Task Ability_paid_with_action_and_Darkness_tokens()
    {
        var d = await AppData.GetAsync();
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");
        var veteran = d.Cog("1R01").Abilities[0];            // Vétéran brisée: active, 1 Darkness token
        Assert.True(g.Use(petra, GameSession.AbilityKey("1R01", 0), veteran));
        Assert.Equal(1, g.State.Cogs[petra].Darkness);

        var demolition = d.Equipment("E18" /* Avis de démolition */).Abilities[0];   // 1 action token
        g.AdjustActions(petra, -GameSession.ActionsPerCycle);
        Assert.Equal(AbilityBlock.NoActions, g.CanUse(petra, GameSession.AbilityKey("E18", 0), demolition));
    }

    [Fact]
    public async Task Undo_restores_the_previous_state()
    {
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");
        g.Wound(petra, 5, attack: true, ignoreProtection: false);
        g.EndCycle();

        g.Undo();
        Assert.Equal(1, g.State.Cycle);
        g.Undo();
        Assert.Equal(0, g.State.Cogs[petra].Marked);
        Assert.False(g.CanUndo);
    }

    [Fact]
    public async Task JSON_save_round_trip()
    {
        var g = await NewGame();
        g.Wound(0, 3, attack: true, ignoreProtection: false);
        g.SetStunned(1, true);

        var json = GameSession.ToJson(g.State);
        Assert.Equal(json, GameSession.ToJson(GameSession.FromJson(json)!));
        Assert.Null(GameSession.FromJson("{ pas du json"));
        Assert.Null(GameSession.FromJson("{\"version\":99,\"cogs\":[]}"));
    }
}
