using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Play mode: life line (rulebook p.16), Darkness and Bile (p.17), stunned (p.18), abilities (p.20), cycles (p.33-35).</summary>
public class GameSessionTests
{
    // Legal Maisel cabal: rank 1 Petra Kantor (RRRRSTSSSSSS), rank 2 Le Chasseur, rank 3 Bohdan Gross.
    private static async Task<GameSession> NewGame(string rank1Equipment = "E11" /* Harmonium */, string rank2Equipment = "D01" /* Carapace */, string rank3Equipment = "E03" /* Phéromones */)
    {
        var d = await AppData.GetAsync();
        var state = GameSession.NewGame("Test", "MAISEL",
            new Cog?[] { d.Cog("1R01"), d.Cog("2R08"), d.Cog("3R06") },
            new Equipment?[] { d.Equipment(rank1Equipment), d.Equipment(rank2Equipment), d.Equipment(rank3Equipment) });
        return new GameSession(state);
    }

    private static int SlotOf(GameSession g, string cogId) => g.State.Cogs.FindIndex(c => c.CogId == cogId);

    [Fact]
    public async Task New_game_ranks_3_2_1_and_4_action_tokens()
    {
        var g = await NewGame();
        Assert.Equal(new[] { 3, 2, 1 }, g.State.Cogs.Select(c => c.Rank));
        Assert.All(g.State.Cogs, c => Assert.Equal(GameSession.ActionsPerCycle, c.Actions));
        Assert.Equal(1, g.State.Cycle);
        Assert.False(g.CanUndo);
    }

    [Fact]
    public async Task Carapace_adds_two_reinforced_HP_at_the_start()
    {
        var d = await AppData.GetAsync();
        var g = await NewGame();
        var chasseur = g.State.Cogs[SlotOf(g, "2R08")];
        Assert.Equal(new[] { HealthPointType.R, HealthPointType.R }.Concat(d.Cog("2R08").LifeLine), chasseur.LifeLine);
    }

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
        Assert.False(second.Protected);
        Assert.Equal(2, second.Marked);
        Assert.Equal(1, second.TaintedMarked);
        Assert.True(second.Stopped);
        Assert.Equal(6, g.State.Cogs[petra].Marked);
        Assert.Equal(1, g.State.Cogs[petra].Darkness);
        Assert.Equal(DarknessStatus.Tainted, g.State.Cogs[petra].Status);
    }

    [Fact]
    public async Task Wounds_outside_an_attack_no_protection_no_stop_but_Darkness()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");     // SSTSSSSSSSSS
        var o = g.Wound(bohdan, 5, attack: false, ignoreProtection: false);
        Assert.Equal(5, o.Marked);
        Assert.False(o.Stopped);
        Assert.Equal(1, g.State.Cogs[bohdan].Darkness);
    }

    [Fact]
    public async Task Attack_that_ignores_protection()
    {
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");
        var o = g.Wound(petra, 3, attack: true, ignoreProtection: true);
        Assert.False(o.Protected);
        Assert.Equal(3, o.Marked);
    }

    [Fact]
    public async Task Standard_HP_reinforced_until_the_end_of_the_cycle()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");     // SSTSSSSSSSSS: no reinforced HP
        Assert.False(g.PreviewWound(bohdan, 2, attack: true, ignoreProtection: false).Protected);

        g.SetFortified(bohdan, true);
        var o = g.Wound(bohdan, 2, attack: true, ignoreProtection: false);
        Assert.True(o.Protected);
        Assert.Equal(1, o.Marked);

        g.EndCycle();
        Assert.False(g.State.Cogs[bohdan].Fortified);
    }

    [Fact]
    public async Task Preview_without_effect_and_out_of_action()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");
        var preview = g.PreviewWound(bohdan, 50, attack: false, ignoreProtection: false);
        Assert.True(preview.OutOfAction);
        Assert.Equal(12, preview.Marked);
        Assert.Equal(0, g.State.Cogs[bohdan].Marked);
        Assert.False(g.CanUndo);

        g.Wound(bohdan, 50, attack: false, ignoreProtection: false);
        Assert.True(g.State.Cogs[bohdan].OutOfAction);
    }

    [Fact]
    public async Task Healing_and_manual_correction()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");
        g.Wound(bohdan, 5, attack: false, ignoreProtection: false);
        Assert.Equal(2, g.Heal(bohdan, 2));
        Assert.Equal(3, g.State.Cogs[bohdan].Marked);
        Assert.Equal(3, g.Heal(bohdan, 10));                 // no more than the marked HP

        var cog = g.State.Cogs[bohdan];
        g.TogglePoint(bohdan, 4);                            // marks the 5th HP, on its own
        Assert.Equal(1, cog.Marked);
        Assert.True(cog.IsChecked(4));
        Assert.False(cog.IsChecked(3));
        g.TogglePoint(bohdan, 4);                            // and unmarks it
        Assert.Equal(0, cog.Marked);
    }

    [Fact]
    public async Task HP_marked_alone_then_wounds_and_healing()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");
        var cog = g.State.Cogs[bohdan];
        g.TogglePoint(bohdan, 2);                            // e.g. Jakub Myasnik: a specific HP
        g.Wound(bohdan, 3, attack: false, ignoreProtection: false);
        Assert.Equal(new[] { true, true, true, true }, Enumerable.Range(0, 4).Select(cog.IsChecked));   // the 3rd HP is skipped
        Assert.False(cog.IsChecked(4));

        g.TogglePoint(bohdan, 6);
        Assert.Equal(1, g.Heal(bohdan, 1));                  // healing: the rightmost marked HP
        Assert.False(cog.IsChecked(6));
        Assert.Equal(4, cog.Marked);

        g.Undo();
        Assert.True(g.State.Cogs[bohdan].IsChecked(6));      // undo: state read back
    }

    [Fact]
    public async Task Tainted_HP_marked_by_hand_gives_a_Darkness()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");
        var cog = g.State.Cogs[bohdan];
        var t = cog.LifeLine.IndexOf(HealthPointType.T);
        var s = cog.LifeLine.IndexOf(HealthPointType.S);

        var o = g.TogglePoint(bohdan, t)!;                   // Jakub Myasnik: "mark 1 tainted HP"
        Assert.Equal(1, o.TaintedMarked);
        Assert.Equal(1, cog.Darkness);
        Assert.Null(g.TogglePoint(bohdan, t));               // unmarked: the Darkness stays
        Assert.Equal(1, cog.Darkness);
        Assert.Equal(0, g.TogglePoint(bohdan, s)!.TaintedMarked);   // standard HP: nothing more
        Assert.Equal(1, cog.Darkness);

        g.AdjustDarkness(bohdan, 2);                         // Darkness at its maximum: the token goes to the Bile
        g.TogglePoint(bohdan, t);
        Assert.Equal(3, cog.Darkness);
        Assert.Equal(1, g.State.Bile);
    }

    [Fact]
    public void Game_saved_in_the_old_format()
    {
        // Before: "marked" = number of HP marked from the left.
        var state = GameSession.FromJson("""{"version":1,"cogs":[{"rank":1,"cogId":"1R01","lifeLine":["S","S","T","S"],"marked":2}]}""")!;
        var cog = state.Cogs[0];
        Assert.Equal(new[] { true, true, false, false }, cog.Checked);
        Assert.DoesNotContain("\"marked\"", GameSession.ToJson(state));
        Assert.Equal(cog.Checked, GameSession.FromJson(GameSession.ToJson(state))!.Cogs[0].Checked);
    }

    [Fact]
    public async Task Darkness_beyond_3_goes_to_the_Bile_and_defeat_at_3()
    {
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");
        Assert.Equal(0, g.AdjustDarkness(petra, 3));
        Assert.Equal(DarknessStatus.Corrupted, g.State.Cogs[petra].Status);
        Assert.Equal(1, g.AdjustDarkness(petra, 1));
        Assert.Equal(3, g.State.Cogs[petra].Darkness);
        Assert.Equal(1, g.State.Bile);

        g.AdjustDarkness(petra, 2);
        Assert.Equal(GameSession.MaxBile, g.State.Bile);
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
        Assert.Equal(1, g.State.Bile);
        Assert.Equal(2, g.State.Cycle);
        Assert.Equal(GameSession.ActionsPerCycle, g.State.Cogs[petra].Actions);
        Assert.Equal(3, g.State.Cogs[petra].Darkness);      // Darkness stays from one cycle to the next
    }

    [Fact]
    public async Task Hunt_plus_1_per_cog_out_of_action_and_end_at_3()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");
        var o = g.Wound(bohdan, 50, attack: false, ignoreProtection: false);
        Assert.True(o.HuntGained);
        Assert.Equal(1, g.State.Hunt);
        Assert.False(g.PreviewWound(bohdan, 1, attack: false, ignoreProtection: false).HuntGained);   // already out of action

        g.TogglePoint(bohdan, 0);                             // correction (1st HP unmarked): back in play, token removed
        Assert.Equal(0, g.State.Hunt);
        g.TogglePoint(bohdan, 0);
        Assert.Equal(1, g.State.Hunt);
        g.Heal(bohdan, 1);                                    // unmarks the 12th HP
        Assert.Equal(0, g.State.Hunt);

        g.AdjustHunt(2);                                      // enemy cogs: by hand
        Assert.False(g.IsOver);
        g.TogglePoint(bohdan, 11);
        Assert.True(g.IsHunted);
        Assert.True(g.IsOver);
        Assert.Equal(0, g.EndCycle());
        g.AdjustHunt(1);
        Assert.Equal(GameSession.MaxHunt, g.State.Hunt);
    }

    [Fact]
    public async Task Victory_points()
    {
        var g = await NewGame();
        g.AdjustVictoryPoints(-1);
        Assert.False(g.CanUndo);                              // never negative: nothing to undo
        g.AdjustVictoryPoints(3);
        g.AdjustVictoryPoints(-1);
        Assert.Equal(2, g.State.VictoryPoints);
        Assert.Equal(2, GameSession.FromJson(GameSession.ToJson(g.State))!.VictoryPoints);
        g.Undo();
        Assert.Equal(3, g.State.VictoryPoints);
    }

    [Fact]
    public async Task Game_over_after_4_cycles()
    {
        var g = await NewGame();
        for (var i = 0; i < GameSession.Cycles; i++) g.EndCycle();
        Assert.True(g.IsFinished);
        Assert.Equal(GameSession.Cycles + 1, g.State.Cycle);
    }

    [Fact]
    public async Task Ability_limited_per_game_Pilules_stabilisantes()
    {
        var d = await AppData.GetAsync();
        var g = await NewGame(rank3Equipment: "E15" /* Pilules stabilisantes */);
        var bohdan = SlotOf(g, "3R06");
        var pills = d.Equipment("E15" /* Pilules stabilisantes */);
        var key = GameSession.AbilityKey(pills.Id, 0);
        var ability = pills.Abilities[0];

        Assert.True(GameSession.IsTracked(ability));
        Assert.Equal(3, GameSession.GameLimit(ability));
        for (var i = 0; i < 3; i++) Assert.True(g.Use(bohdan, key, ability));
        Assert.Equal(0, g.UsesLeft(bohdan, key, ability));
        Assert.Equal(AbilityBlock.Exhausted, g.CanUse(bohdan, key, ability));
        Assert.False(g.Use(bohdan, key, ability));
        Assert.Equal(GameSession.ActionsPerCycle, g.State.Cogs[bohdan].Actions);   // without a cost

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
        var key = GameSession.AbilityKey("1R01", 0);

        Assert.True(g.Use(petra, key, veteran));
        Assert.Equal(1, g.State.Cogs[petra].Darkness);
        Assert.True(g.Use(petra, key, veteran));             // limit per cycle: not tracked

        g.SetStunned(petra, true);
        Assert.Equal(AbilityBlock.Stunned, g.CanUse(petra, key, veteran));
        g.SetStunned(petra, false);

        var demolition = d.Equipment("E18" /* Avis de démolition */).Abilities[0];   // 1 action token
        var dKey = GameSession.AbilityKey("E18", 0);
        g.AdjustActions(petra, -GameSession.ActionsPerCycle);
        Assert.Equal(AbilityBlock.NoActions, g.CanUse(petra, dKey, demolition));
    }

    [Fact]
    public async Task Medic_Ruzicka_extra_Integrity_equipment_in_the_first_cycle()
    {
        var d = await AppData.GetAsync();
        var ruzicka = d.Cog("3R27");
        var kit = d.Equipment("E16" /* Kit d'Intégrité */);
        var hazmat = d.Equipment("E17" /* Combinaison Hazmat */);
        var g = new GameSession(GameSession.NewGame(null, "BLOCKHAUS",
            new Cog?[] { ruzicka, d.Cog("2R08"), d.Cog("1R01") },
            new Equipment?[] { kit, d.Equipment("D01" /* Carapace */), d.Equipment("E11" /* Harmonium */) }));
        var slot = SlotOf(g, "3R27");

        // Integrity equipment only, without duplicates with the cabal's.
        var options = d.Catalog.ExtraEquipmentOptions(ruzicka, g.State.Cogs.Select(c => c.EquipmentId!));
        Assert.Contains(options, e => e.Id == hazmat.Id);
        Assert.DoesNotContain(options, e => e.Id == kit.Id);
        Assert.All(options, e => Assert.True(e.Ministry == "INTEGRITY" && e.Kind == EquipmentKind.EQUIPMENT));
        Assert.Empty(d.Catalog.ExtraEquipmentOptions(d.Cog("1R01"), Array.Empty<string>()));

        Assert.False(g.SetExtraEquipment(slot, ruzicka, kit, d.Equipment("D01" /* Carapace */)));     // not Integrity
        Assert.True(g.SetExtraEquipment(slot, ruzicka, kit, hazmat));
        Assert.Equal(hazmat.Id, g.State.Cogs[slot].ExtraEquipmentId);
        Assert.Equal(ruzicka.LifeLine, g.State.Cogs[slot].LifeLine);

        g.Undo();
        Assert.Null(g.State.Cogs[slot].ExtraEquipmentId);
        g.SetExtraEquipment(slot, ruzicka, kit, hazmat);

        g.EndCycle();                                         // "at the start of the first cycle" only
        Assert.False(g.CanChooseExtraEquipment(ruzicka));
        Assert.False(g.SetExtraEquipment(slot, ruzicka, kit, null));
        Assert.Equal(hazmat.Id, g.State.Cogs[slot].ExtraEquipmentId);
        Assert.False(g.CanChooseExtraEquipment(d.Cog("1R01")));
    }

    [Fact]
    public async Task Undo_restores_the_previous_state()
    {
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");
        g.Wound(petra, 5, attack: true, ignoreProtection: false);
        g.AdjustDarkness(petra, 2);
        g.EndCycle();

        g.Undo();
        Assert.Equal(1, g.State.Cycle);
        g.Undo();
        Assert.Equal(0, g.State.Cogs[petra].Darkness);
        Assert.Equal(4, g.State.Cogs[petra].Marked);
        g.Undo();
        Assert.Equal(0, g.State.Cogs[petra].Marked);
        Assert.False(g.CanUndo);
    }

    [Fact]
    public async Task JSON_save_round_trip()
    {
        var g = await NewGame(rank3Equipment: "E15" /* Pilules stabilisantes */);
        g.Wound(0, 3, attack: true, ignoreProtection: false);
        g.SetStunned(1, true);
        g.Use(0, GameSession.AbilityKey("E15", 0), (await AppData.GetAsync()).Equipment("E15" /* Pilules stabilisantes */).Abilities[0]);

        var json = GameSession.ToJson(g.State);
        var back = GameSession.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(json, GameSession.ToJson(back!));
        Assert.Contains("\"lifeLine\":[\"S\"", json);         // enums as plain text

        Assert.Null(GameSession.FromJson("{ pas du json"));
        Assert.Null(GameSession.FromJson("{\"version\":99,\"cogs\":[]}"));
    }
}
