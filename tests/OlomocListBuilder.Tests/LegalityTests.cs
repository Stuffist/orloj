using OlomocListBuilder.Models;
using OlomocListBuilder.Services;
using Xunit;

namespace OlomocListBuilder.Tests;

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
    public async Task Passe_droit_porte_ailleurs_elargit_les_equipements_proposes()
    {
        var d = await AppData.GetAsync();
        var cogs = new Cog?[] { d.Cog("3R06"), d.Cog("2R04"), d.Cog("1R01") };
        var equipment = new Equipment?[] { null, null, d.Equipment("Passe-droit du Census") };
        Assert.Contains(d.Catalog.AvailableEquipment(0, cogs, equipment, "MAISEL"), e => e.Ministry == "FRINGE");
        Assert.DoesNotContain(d.Catalog.AvailableEquipment(2, cogs, equipment, "MAISEL"), e => e.Ministry == "FRINGE");
    }

    [Fact]
    public async Task Les_messages_se_traduisent_avec_les_noms_de_la_langue()
    {
        var d = await AppData.CreateAsync();
        var r = d.Catalog.CheckLegality("MAISEL",
            new Cog?[] { d.Cog("3R06"), d.Cog("2R08"), d.Cog("1R01") },
            new Equipment?[] { d.Equipment("Phéromones"), d.Equipment("Carapace"), d.Equipment("Lumen") });
        var issue = Assert.Single(r.Issues);
        Assert.Contains("Hors faction Ligue Maisel : Le Chasseur", d.L.Describe(issue));
        await d.L.SetLanguageAsync("en");
        Assert.StartsWith("Outside the Ligue Maisel faction: Le Chasseur", d.L.Describe(issue));   // faction : nom fr assumé ; rouage : repli fr
        Assert.False(d.L.FactionName("MAISEL").IsFallback);
        Assert.True(d.L.CogName(d.Cog("2R08")).IsFallback);
    }
}
