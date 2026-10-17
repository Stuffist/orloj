using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Composition de cabale (LDR p.32) et règles portées par les cartes (buildRules).</summary>
public class LegalityTests
{
    // Emplacements dans l'ordre rang 1, rang 2, rang 3 : (id du rouage, nom de l'équipement/don).
    private static async Task<LegalityResult> Check(string faction, params (string Cog, string Equipment)[] slots)
    {
        var d = await AppData.GetAsync();
        return d.Catalog.CheckLegality(faction,
            slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(),
            slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
    }

    private static void AssertLegal(LegalityResult r) =>
        Assert.True(r.IsLegal, "illégale : " + string.Join(" | ", r.Issues.Select(i => i.Key)));

    private static LegalityIssue AssertIssue(LegalityResult r, string key)
    {
        Assert.False(r.IsLegal);
        return Assert.Single(r.Issues, i => i.Key == key);
    }

    // ---- Harmonium (Maisel) : un rouage de l'Axe, de rang 1 ou 2, hors faction ----

    [Fact]
    public async Task Harmonium_permet_un_rouage_de_l_Axe() =>
        AssertLegal(await Check("MAISEL", ("3R06", "Phéromones"), ("2R08", "Carapace"), ("1R01", "Harmonium")));

    [Fact]
    public async Task Sans_Harmonium_le_rouage_de_l_Axe_est_hors_faction()
    {
        var issue = AssertIssue(await Check("MAISEL", ("3R06", "Phéromones"), ("2R08", "Carapace"), ("1R01", "Lumen")), "outsideFaction");
        Assert.Equal(new[] { "2R08" }, issue.Args[1].Values);
    }

    [Fact]
    public async Task Un_seul_Harmonium_ne_couvre_qu_un_rouage() =>
        AssertIssue(await Check("MAISEL", ("3R07", "Carapace"), ("2R08", "Stigmates"), ("1R01", "Harmonium")), "outsideFaction");

    [Fact]
    public async Task Harmonium_refuse_un_rang_3()
    {
        var issue = AssertIssue(await Check("MAISEL", ("3R06", "Phéromones"), ("2R02", "Harmonium"), ("1R09", "Carapace")), "outsideFaction");
        Assert.Equal(new[] { "1R09" }, issue.Args[1].Values);
    }

    // ---- Asservisseur mental (Autres) : le porteur, de rang 1 ou 2 ----

    [Fact]
    public async Task Asservisseur_permet_son_porteur() =>
        AssertLegal(await Check("OTHERS", ("3R17", "Dose de Mélisse"), ("2R02", "Asservisseur mental"), ("1R09", "Carapace")));

    [Fact]
    public async Task Asservisseur_porte_par_un_autre_ne_couvre_pas()
    {
        var issue = AssertIssue(await Check("OTHERS", ("3R17", "Dose de Mélisse"), ("2R02", "Carapace"), ("1R09", "Asservisseur mental")), "outsideFaction");
        Assert.Equal(new[] { "2R02" }, issue.Args[1].Values);
    }

    [Fact]
    public async Task Asservisseur_refuse_un_porteur_de_rang_3() =>
        AssertIssue(await Check("OTHERS", ("3R17", "Dose de Mélisse"), ("2R08", "Carapace"), ("1R01", "Asservisseur mental")), "outsideFaction");

    // ---- Passe-droit du Census : le porteur compte comme affilié à tous les Ministères ----

    [Fact]
    public async Task Passe_droit_rend_la_Frange_disponible() =>
        AssertLegal(await Check("MAISEL", ("3R06", "Dose de Mélisse"), ("2R04", "Phéromones"), ("1R01", "Passe-droit du Census")));

    [Fact]
    public async Task Passe_droit_ne_se_rend_pas_disponible_lui_meme()
    {
        var issue = AssertIssue(await Check("MAISEL", ("3R06", "Phéromones"), ("2R02", "Manuel du Progrès"), ("1R01", "Passe-droit du Census")), "ministryAbsent");
        Assert.Equal(new[] { "COORDINATION" }, issue.Args[1].Values);
    }

    [Fact]
    public async Task Passe_droit_occupe_l_emplacement_de_son_porteur() =>
        AssertIssue(await Check("MAISEL", ("3R06", "Dose de Mélisse"), ("2R04", "Passe-droit du Census"), ("1R01", "Lumen")), "ministryOverflow");

    [Fact]
    public async Task Sans_Passe_droit_la_Frange_est_absente() =>
        AssertIssue(await Check("MAISEL", ("3R06", "Dose de Mélisse"), ("2R04", "Phéromones"), ("1R01", "Lumen")), "ministryAbsent");

    [Fact]
    public async Task Passe_droit_compte_comme_l_Axe_pour_Harmonium() =>
        AssertLegal(await Check("MAISEL", ("3R20", "Passe-droit du Census"), ("2R04", "Harmonium"), ("1R01", "Lumen")));

    // ---- Disponibilité ----

    [Fact]
    public async Task Listes_proposees_selon_les_regles_de_recrutement()
    {
        var d = await AppData.GetAsync();
        var maisel2 = d.Catalog.AvailableCogs(2, "MAISEL", null, CogQuery.Empty);
        Assert.Contains(maisel2, c => c.Id == "2R08");
        Assert.Equal("Harmonium", d.L.ReferenceName(d.Catalog.RecruitVia(d.Cog("2R08"), "MAISEL")!));
        Assert.DoesNotContain(d.Catalog.AvailableCogs(3, "MAISEL", null, CogQuery.Empty), c => c.Id == "1R09");
        Assert.Equal(d.Catalog.Catalog.Cogs.Count(c => c.Rank == 2), d.Catalog.AvailableCogs(2, "OTHERS", null, CogQuery.Empty).Count);
        Assert.Null(d.Catalog.RecruitVia(d.Cog("2R04"), "MAISEL"));
    }

    [Fact]
    public async Task Factions_compatibles_avec_les_rouages_deja_choisis()
    {
        var d = await AppData.GetAsync();
        var all = d.Catalog.AllFactions.Select(f => f.Code).ToList();
        Assert.Equal(all, d.Catalog.CompatibleFactions(new Olomoc.Models.Cog?[3]));

        // Un rouage seul : ses factions, chacune l'accueillant ; un rouage propre à une faction.
        var indra = d.Catalog.CompatibleFactions([null, null, d.Cog("1R16")]);
        Assert.Equal(["URKARAS"], indra);
        var petra = d.Catalog.CompatibleFactions([d.Cog("1R01")]);
        Assert.Contains("MAISEL", petra);
        Assert.All(petra, f => Assert.True(d.Catalog.Fits(d.Cog("1R01"), f)));

        // Plusieurs rouages : l'intersection, dans l'ordre du référentiel.
        var both = d.Catalog.CompatibleFactions([d.Cog("1R01"), d.Cog("1R12")]);
        Assert.Equal(petra.Intersect(d.Catalog.CompatibleFactions([d.Cog("1R12")])), both);
    }

    [Fact]
    public async Task Factions_d_un_rouage_membre_ou_recrute_par_une_carte()
    {
        var d = await AppData.GetAsync();
        string Show(string id) => string.Join(" ", d.Catalog.FactionsOf(d.Cog(id))
            .Select(f => f.Member ? f.Faction : $"{f.Faction}<{d.L.ReferenceName(f.Via!)}>"));

        // Rang 1 : ses trois factions, et Les Autres par la carte qui recrute son porteur.
        Assert.Equal("OTHERS<Asservisseur mental> BLOCKHAUS DYKOVIAN MAISEL", Show("3R05"));
        // Rouage de l'Axe : Les Autres, et la Ligue de Maisel par l'Harmonium.
        Assert.Equal("OTHERS MAISEL<Harmonium>", Show("2R08"));
        // Rang 3 : aucune carte ne le recrute hors de sa faction.
        Assert.Equal("URKARAS", Show("1R16"));
        // Toujours cohérent avec Fits.
        foreach (var cog in d.Catalog.Catalog.Cogs)
            Assert.Equal(d.Catalog.AllFactions.Where(f => d.Catalog.Fits(cog, f.Code)).Select(f => f.Code),
                         d.Catalog.FactionsOf(cog).Select(f => f.Faction));
    }

    [Fact]
    public async Task Sans_faction_rouages_proposes_compatibles_avec_ceux_deja_choisis()
    {
        var d = await AppData.GetAsync();
        var factions = d.Catalog.CompatibleFactions([d.Cog("1R01")]);
        for (var rank = 2; rank <= 3; rank++)
        {
            var limited = d.Catalog.AvailableCogs(rank, null, null, CogQuery.Empty, factions);
            Assert.NotEmpty(limited);
            Assert.All(limited, c => Assert.Contains(factions, f => d.Catalog.Fits(c, f)));
            Assert.True(limited.Count <= d.Catalog.AvailableCogs(rank, null, null, CogQuery.Empty).Count);
        }
        // Sans liste de factions : inchangé (tous les rouages du rang).
        Assert.Equal(d.Catalog.Catalog.Cogs.Count(c => c.Rank == 3), d.Catalog.AvailableCogs(3, null, null, CogQuery.Empty).Count);
    }

    [Fact]
    public async Task Passe_droit_porte_ailleurs_elargit_les_equipements_proposes()
    {
        var d = await AppData.GetAsync();
        var cogs = new Cog?[] { d.Cog("3R06"), d.Cog("2R04"), d.Cog("1R01") };
        var equipment = new Equipment?[] { null, null, d.Equipment("Passe-droit du Census") };
        Assert.Contains(d.Catalog.AvailableEquipment(0, cogs, equipment, "MAISEL"), e => e.Ministry == "FRINGE");
        Assert.DoesNotContain(d.Catalog.AvailableEquipment(2, cogs, equipment, "MAISEL"), e => e.Ministry == "FRINGE");
    }

    // ---- Choix grisés : déjà « pris » par le reste de la cabale ----

    private static async Task<(AppData D, Cog?[] Cogs)> Cogs(params string?[] ids)
    {
        var d = await AppData.GetAsync();
        return (d, ids.Select(id => id is null ? null : (Cog?)d.Cog(id)).ToArray());
    }

    [Fact]
    public async Task Harmonium_deja_utilise_grise_un_autre_rouage_de_l_Axe()
    {
        var (d, cogs) = await Cogs("3R07", null, null);                // Axe, hors faction : via Harmonium
        Assert.False(d.Catalog.CanJoin(1, d.Cog("2R08"), cogs, "MAISEL"));
        Assert.True(d.Catalog.CanJoin(1, d.Cog("2R11"), cogs, "MAISEL"));   // membre de Maisel
        Assert.True(d.Catalog.CanJoin(0, d.Cog("3R07"), cogs, "MAISEL"));   // le remplacer par lui-même
    }

    [Fact]
    public async Task Sans_faction_grise_ce_qu_aucune_faction_ne_couvre_ensemble()
    {
        var (d, cogs) = await Cogs("3R07", null, "1R01");              // possible pour Maisel seulement
        Assert.False(d.Catalog.CanJoin(1, d.Cog("2R08"), cogs, null));
        Assert.True(d.Catalog.CanJoin(1, d.Cog("2R11"), cogs, null));
    }

    [Fact]
    public async Task Asservisseur_deja_utilise_grise_un_autre_rouage_hors_faction()
    {
        var (d, cogs) = await Cogs(null, "2R02", null);                // hors faction pour Others
        Assert.False(d.Catalog.CanJoin(0, d.Cog("3R05"), cogs, "OTHERS"));
        Assert.True(d.Catalog.CanJoin(0, d.Cog("3R07"), cogs, "OTHERS"));
    }

    [Fact]
    public async Task Cabale_deja_impossible_ne_grise_pas_tout()
    {
        var (d, cogs) = await Cogs("3R07", "2R08", null);              // deux rouages de l'Axe, un seul Harmonium
        Assert.True(d.Catalog.CanJoin(2, d.Cog("1R01"), cogs, "MAISEL"));
    }

    [Fact]
    public async Task Factions_compatibles_avec_les_rouages_pris_ensemble()
    {
        var (d, cogs) = await Cogs("3R07", "2R08", null);
        var factions = d.Catalog.CompatibleFactions(cogs);
        Assert.DoesNotContain("MAISEL", factions);
        Assert.Contains("OTHERS", factions);
    }

    [Fact]
    public async Task Equipement_deja_porte_echange()
    {
        var (d, cogs) = await Cogs("3R06", "2R11", "1R01");
        var equipment = new Equipment?[] { d.Equipment("Phéromones"), null, null };
        var swap = d.Catalog.EquipmentConflict(1, d.Equipment("Phéromones"), cogs, equipment);
        Assert.Equal("equipment.swap.duplicate", swap?.Reason);
        Assert.Equal(new[] { 0 }, swap!.Displaced);                                                 // retiré à Bohdan Gross
        Assert.Null(d.Catalog.EquipmentConflict(0, d.Equipment("Phéromones"), cogs, equipment));   // son propre emplacement
    }

    [Fact]
    public async Task Ministere_deja_utilise_echange_son_equipement()
    {
        // Un seul rouage de l'Harmonie, qui débloque déjà Phéromones.
        var (d, cogs) = await Cogs("3R06", "2R11", "1R01");
        var equipment = new Equipment?[] { d.Equipment("Phéromones"), null, null };
        var swap = d.Catalog.EquipmentConflict(1, d.Equipment("Collier de dévotion"), cogs, equipment);
        Assert.Equal("equipment.swap.ministry", swap?.Reason);
        Assert.Equal(new[] { 0 }, swap!.Displaced);
        Assert.Null(d.Catalog.EquipmentConflict(1, d.Equipment("Ration"), cogs, equipment));      // Effort : le sien
    }

    [Fact]
    public async Task Passe_droit_porte_ailleurs_debloque_un_Ministere_de_plus()
    {
        // 1R01 porte le Passe-droit (débloqué par la Coordination de 2R04) : il peut débloquer l'Harmonie.
        var (d, cogs) = await Cogs("3R06", "2R04", "1R01");
        var collier = d.Equipment("Collier de dévotion");
        var sans = new Equipment?[] { d.Equipment("Phéromones"), null, null };
        var avec = new Equipment?[] { d.Equipment("Phéromones"), null, d.Equipment("Passe-droit du Census") };
        Assert.Equal("equipment.swap.ministry", d.Catalog.EquipmentConflict(1, collier, cogs, sans)?.Reason);
        Assert.Null(d.Catalog.EquipmentConflict(1, collier, cogs, avec));
        AssertLegal(d.Catalog.CheckLegality("MAISEL", cogs, new[] { avec[0], collier, avec[2] }));
        Assert.Equal(0, d.Catalog.MinistryRemaining(1, "HARMONY", cogs, sans));
        Assert.Equal(1, d.Catalog.MinistryRemaining(1, "HARMONY", cogs, avec));
    }

    [Fact]
    public async Task Equipements_restants_par_Ministere()
    {
        var (d, cogs) = await Cogs("3R06", "2R11", "1R01");                // Harmonie, Effort, Sécurité
        var equipment = new Equipment?[] { d.Equipment("Phéromones"), null, null };
        Assert.Equal(0, d.Catalog.MinistryRemaining(1, "HARMONY", cogs, equipment));   // Bohdan Gross a déjà le sien
        Assert.Equal(1, d.Catalog.MinistryRemaining(0, "HARMONY", cogs, equipment));   // le sien, qu'il remplacerait
        Assert.Equal(1, d.Catalog.MinistryRemaining(1, "EFFORT", cogs, equipment));
        Assert.Equal(0, d.Catalog.MinistryRemaining(1, "AXIS", cogs, equipment));      // aucun rouage de l'Axe
    }

    [Fact]
    public async Task Les_messages_se_traduisent_avec_les_noms_de_la_langue()
    {
        var d = await AppData.CreateAsync();
        var r = d.Catalog.CheckLegality("MAISEL",
            new Cog?[] { d.Cog("3R06"), d.Cog("2R08"), d.Cog("1R01") },
            new Equipment?[] { d.Equipment("Phéromones"), d.Equipment("Carapace"), d.Equipment("Lumen") });
        var issue = Assert.Single(r.Issues);
        Assert.Contains("Hors faction Ligue de Maisel : Le Chasseur", d.L.Describe(issue));
        await d.L.SetLanguageAsync("en");
        Assert.StartsWith("Outside the Ligue de Maisel faction: Le Chasseur", d.L.Describe(issue));   // faction : nom fr assumé ; rouage : repli fr
        Assert.False(d.L.FactionName("MAISEL").IsFallback);
        Assert.True(d.L.CogName(d.Cog("2R08")).IsFallback);
    }
}
