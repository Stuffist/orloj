using OlomocListBuilder.Models;
using OlomocListBuilder.Services;
using Xunit;

namespace OlomocListBuilder.Tests;

/// <summary>Mode partie : ligne de vie (LDR p.16), Noirceur et Fiel (p.17), sonné (p.18), capacités (p.20), cycles (p.33-35).</summary>
public class GameSessionTests
{
    // Cabale Maisel légale : rang 1 Petra Kantor (RRRRSTSSSSSS), rang 2 Le Chasseur, rang 3 Bohdan Gross.
    private static async Task<GameSession> NewGame(string rank1Equipment = "Harmonium", string rank2Equipment = "Carapace", string rank3Equipment = "Phéromones")
    {
        var d = await AppData.GetAsync();
        var state = GameSession.NewGame("Test", "MAISEL",
            new Cog?[] { d.Cog("1R01"), d.Cog("2R08"), d.Cog("3R06") },
            new Equipment?[] { d.Equipment(rank1Equipment), d.Equipment(rank2Equipment), d.Equipment(rank3Equipment) });
        return new GameSession(state);
    }

    private static int SlotOf(GameSession g, string cogId) => g.State.Cogs.FindIndex(c => c.CogId == cogId);

    [Fact]
    public async Task Nouvelle_partie_rangs_3_2_1_et_4_pions_d_action()
    {
        var g = await NewGame();
        Assert.Equal(new[] { 3, 2, 1 }, g.State.Cogs.Select(c => c.Rank));
        Assert.All(g.State.Cogs, c => Assert.Equal(GameSession.ActionsPerCycle, c.Actions));
        Assert.Equal(1, g.State.Cycle);
        Assert.False(g.CanUndo);
    }

    [Fact]
    public async Task Carapace_ajoute_deux_PV_renforces_en_tete()
    {
        var d = await AppData.GetAsync();
        var g = await NewGame();
        var chasseur = g.State.Cogs[SlotOf(g, "2R08")];
        Assert.Equal(new[] { HealthPointType.R, HealthPointType.R }.Concat(d.Cog("2R08").LifeLine), chasseur.LifeLine);
    }

    [Fact]
    public async Task Exemples_du_livre_de_regles_Petra_Kantor()
    {
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");

        // 5 blessures, un PV renforcé reste à cocher : 4 PV cochés.
        var first = g.Wound(petra, 5, attack: true, ignoreProtection: false);
        Assert.True(first.Protected);
        Assert.Equal(4, first.Marked);

        // 3 blessures : le deuxième PV à cocher est teinté, la troisième blessure est ignorée, +1 Noirceur.
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
    public async Task Blessures_hors_attaque_ni_protection_ni_arret_mais_Noirceur()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");     // SSTSSSSSSSSS
        var o = g.Wound(bohdan, 5, attack: false, ignoreProtection: false);
        Assert.Equal(5, o.Marked);
        Assert.False(o.Stopped);
        Assert.Equal(1, g.State.Cogs[bohdan].Darkness);
    }

    [Fact]
    public async Task Attaque_qui_ignore_la_protection()
    {
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");
        var o = g.Wound(petra, 3, attack: true, ignoreProtection: true);
        Assert.False(o.Protected);
        Assert.Equal(3, o.Marked);
    }

    [Fact]
    public async Task PV_standards_renforces_jusqu_a_la_fin_du_cycle()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");     // SSTSSSSSSSSS : aucun PV renforcé
        Assert.False(g.PreviewWound(bohdan, 2, attack: true, ignoreProtection: false).Protected);

        g.SetFortified(bohdan, true);
        var o = g.Wound(bohdan, 2, attack: true, ignoreProtection: false);
        Assert.True(o.Protected);
        Assert.Equal(1, o.Marked);

        g.EndCycle();
        Assert.False(g.State.Cogs[bohdan].Fortified);
    }

    [Fact]
    public async Task Apercu_sans_effet_et_hors_de_combat()
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
    public async Task Soin_et_correction_manuelle()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");
        g.Wound(bohdan, 5, attack: false, ignoreProtection: false);
        Assert.Equal(2, g.Heal(bohdan, 2));
        Assert.Equal(3, g.State.Cogs[bohdan].Marked);
        Assert.Equal(3, g.Heal(bohdan, 10));                 // pas plus que les PV cochés

        g.TogglePoint(bohdan, 4);                            // coche jusqu'au 5e PV
        Assert.Equal(5, g.State.Cogs[bohdan].Marked);
        g.TogglePoint(bohdan, 1);                            // décoche le 2e PV et les suivants
        Assert.Equal(1, g.State.Cogs[bohdan].Marked);
    }

    [Fact]
    public async Task Noirceur_au_dela_de_3_vers_le_Fiel_et_defaite_a_3()
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
        Assert.Equal(0, g.EndCycle());                       // partie finie : plus de fin de cycle
    }

    [Fact]
    public async Task Fin_de_cycle_Fiel_par_rouage_corrompu_et_pions_d_action()
    {
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");
        var bohdan = SlotOf(g, "3R06");
        g.AdjustDarkness(petra, 3);
        g.AdjustDarkness(bohdan, 3);
        g.TogglePoint(bohdan, 11);                           // hors de combat : ne compte plus
        g.AdjustActions(petra, -3);

        Assert.Equal(1, g.EndCycle());
        Assert.Equal(1, g.State.Bile);
        Assert.Equal(2, g.State.Cycle);
        Assert.Equal(GameSession.ActionsPerCycle, g.State.Cogs[petra].Actions);
        Assert.Equal(3, g.State.Cogs[petra].Darkness);      // la Noirceur reste d'un cycle à l'autre
    }

    [Fact]
    public async Task Traque_plus_1_par_rouage_hors_de_combat_et_fin_a_3()
    {
        var g = await NewGame();
        var bohdan = SlotOf(g, "3R06");
        var o = g.Wound(bohdan, 50, attack: false, ignoreProtection: false);
        Assert.True(o.HuntGained);
        Assert.Equal(1, g.State.Hunt);
        Assert.False(g.PreviewWound(bohdan, 1, attack: false, ignoreProtection: false).HuntGained);   // déjà hors de combat

        g.TogglePoint(bohdan, 0);                             // correction : de retour en jeu, pion retiré
        Assert.Equal(0, g.State.Hunt);
        g.TogglePoint(bohdan, 11);
        Assert.Equal(1, g.State.Hunt);
        g.Heal(bohdan, 1);
        Assert.Equal(0, g.State.Hunt);

        g.AdjustHunt(2);                                      // rouages adverses : à la main
        Assert.False(g.IsOver);
        g.TogglePoint(bohdan, 11);
        Assert.True(g.IsHunted);
        Assert.True(g.IsOver);
        Assert.Equal(0, g.EndCycle());
        g.AdjustHunt(1);
        Assert.Equal(GameSession.MaxHunt, g.State.Hunt);
    }

    [Fact]
    public async Task Points_de_victoire()
    {
        var g = await NewGame();
        g.AdjustVictoryPoints(-1);
        Assert.False(g.CanUndo);                              // jamais négatifs : rien à annuler
        g.AdjustVictoryPoints(3);
        g.AdjustVictoryPoints(-1);
        Assert.Equal(2, g.State.VictoryPoints);
        Assert.Equal(2, GameSession.FromJson(GameSession.ToJson(g.State))!.VictoryPoints);
        g.Undo();
        Assert.Equal(3, g.State.VictoryPoints);
    }

    [Fact]
    public async Task Partie_terminee_apres_4_cycles()
    {
        var g = await NewGame();
        for (var i = 0; i < GameSession.Cycles; i++) g.EndCycle();
        Assert.True(g.IsFinished);
        Assert.Equal(GameSession.Cycles + 1, g.State.Cycle);
    }

    [Fact]
    public async Task Capacite_limitee_par_partie_Pilules_stabilisantes()
    {
        var d = await AppData.GetAsync();
        var g = await NewGame(rank3Equipment: "Pilules stabilisantes");
        var bohdan = SlotOf(g, "3R06");
        var pills = d.Equipment("Pilules stabilisantes");
        var key = GameSession.AbilityKey(pills.Id, 0);
        var ability = pills.Abilities[0];

        Assert.True(GameSession.IsTracked(ability));
        Assert.Equal(3, GameSession.GameLimit(ability));
        for (var i = 0; i < 3; i++) Assert.True(g.Use(bohdan, key, ability));
        Assert.Equal(0, g.UsesLeft(bohdan, key, ability));
        Assert.Equal(AbilityBlock.Exhausted, g.CanUse(bohdan, key, ability));
        Assert.False(g.Use(bohdan, key, ability));
        Assert.Equal(GameSession.ActionsPerCycle, g.State.Cogs[bohdan].Actions);   // sans coût

        g.EndCycle();                                         // limite par partie : pas remise à zéro
        Assert.Equal(0, g.UsesLeft(bohdan, key, ability));
    }

    [Fact]
    public async Task Capacite_payee_en_pions_d_action_et_de_Noirceur()
    {
        var d = await AppData.GetAsync();
        var g = await NewGame();
        var petra = SlotOf(g, "1R01");
        var veteran = d.Cog("1R01").Abilities[0];            // Vétéran brisée : active, 1 pion de Noirceur
        var key = GameSession.AbilityKey("1R01", 0);

        Assert.True(g.Use(petra, key, veteran));
        Assert.Equal(1, g.State.Cogs[petra].Darkness);
        Assert.True(g.Use(petra, key, veteran));             // limite par cycle : non suivie

        g.SetStunned(petra, true);
        Assert.Equal(AbilityBlock.Stunned, g.CanUse(petra, key, veteran));
        g.SetStunned(petra, false);

        var demolition = d.Equipment("Avis de démolition").Abilities[0];   // 1 pion d'action
        var dKey = GameSession.AbilityKey("E18", 0);
        g.AdjustActions(petra, -GameSession.ActionsPerCycle);
        Assert.Equal(AbilityBlock.NoActions, g.CanUse(petra, dKey, demolition));
    }

    [Fact]
    public async Task Medic_Ruzicka_equipement_supplementaire_de_l_Integrite_au_premier_cycle()
    {
        var d = await AppData.GetAsync();
        var ruzicka = d.Cog("3R27");
        var kit = d.Equipment("Kit d'Intégrité");
        var hazmat = d.Equipment("Combinaison Hazmat");
        var g = new GameSession(GameSession.NewGame(null, "BLAUKAUST",
            new Cog?[] { ruzicka, d.Cog("2R08"), d.Cog("1R01") },
            new Equipment?[] { kit, d.Equipment("Carapace"), d.Equipment("Harmonium") }));
        var slot = SlotOf(g, "3R27");

        // Équipements de l'Intégrité seulement, sans doublon avec ceux de la cabale.
        var options = d.Catalog.ExtraEquipmentOptions(ruzicka, g.State.Cogs.Select(c => c.EquipmentId!));
        Assert.Contains(options, e => e.Id == hazmat.Id);
        Assert.DoesNotContain(options, e => e.Id == kit.Id);
        Assert.All(options, e => Assert.True(e.Ministry == "INTEGRITY" && e.Kind == EquipmentKind.EQUIPMENT));
        Assert.Empty(d.Catalog.ExtraEquipmentOptions(d.Cog("1R01"), Array.Empty<string>()));

        Assert.False(g.SetExtraEquipment(slot, ruzicka, kit, d.Equipment("Carapace")));     // hors Intégrité
        Assert.True(g.SetExtraEquipment(slot, ruzicka, kit, hazmat));
        Assert.Equal(hazmat.Id, g.State.Cogs[slot].ExtraEquipmentId);
        Assert.Equal(ruzicka.LifeLine, g.State.Cogs[slot].LifeLine);

        g.Undo();
        Assert.Null(g.State.Cogs[slot].ExtraEquipmentId);
        g.SetExtraEquipment(slot, ruzicka, kit, hazmat);

        g.EndCycle();                                         // « au début du premier cycle » seulement
        Assert.False(g.CanChooseExtraEquipment(ruzicka));
        Assert.False(g.SetExtraEquipment(slot, ruzicka, kit, null));
        Assert.Equal(hazmat.Id, g.State.Cogs[slot].ExtraEquipmentId);
        Assert.False(g.CanChooseExtraEquipment(d.Cog("1R01")));
    }

    [Fact]
    public async Task Annuler_restaure_l_etat_precedent()
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
    public async Task Sauvegarde_JSON_aller_retour()
    {
        var g = await NewGame(rank3Equipment: "Pilules stabilisantes");
        g.Wound(0, 3, attack: true, ignoreProtection: false);
        g.SetStunned(1, true);
        g.Use(0, GameSession.AbilityKey("E15", 0), (await AppData.GetAsync()).Equipment("Pilules stabilisantes").Abilities[0]);

        var json = GameSession.ToJson(g.State);
        var back = GameSession.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(json, GameSession.ToJson(back!));
        Assert.Contains("\"lifeLine\":[\"S\"", json);         // énumérations en clair

        Assert.Null(GameSession.FromJson("{ pas du json"));
        Assert.Null(GameSession.FromJson("{\"version\":99,\"cogs\":[]}"));
    }
}
