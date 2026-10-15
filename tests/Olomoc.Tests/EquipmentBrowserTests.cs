using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Catalogue des équipements et dons : groupes, filtre, recherche, tri.</summary>
public class EquipmentBrowserTests
{
    [Fact]
    public async Task Par_ministere_ordre_du_referentiel_puis_factions()
    {
        var d = await AppData.GetAsync();
        var groups = EquipmentBrowser.Browse(d.Catalog, d.L, null, "", EquipmentSort.Ministry);
        Assert.Equal(d.Catalog.Catalog.Equipment.Count, groups.Sum(g => g.Items.Count));
        Assert.Equal(53, groups.Sum(g => g.Items.Count));
        string[] ministries = ["COORDINATION", "INTEGRITY", "EFFORT", "PROGRESS", "HARMONY", "EDIFICATION", "SECURITY", "FRINGE", "AXIS"];
        Assert.Equal(ministries, groups.Take(ministries.Length).Select(g => g.Key));
        Assert.All(groups.Single(g => g.Key == "AXIS").Items, e => Assert.Equal(Olomoc.Models.EquipmentKind.BOON, e.Kind));

        // Puis un groupe par faction, dans l'ordre du référentiel des factions.
        var factionGroups = groups.Skip(ministries.Length).ToList();
        var order = d.Catalog.AllFactions.Select(f => EquipmentBrowser.FactionPrefix + f.Code).ToList();
        Assert.All(factionGroups, g => Assert.Contains(g.Key, order));
        Assert.Equal(factionGroups.Select(g => g.Key).OrderBy(order.IndexOf), factionGroups.Select(g => g.Key));
        Assert.All(factionGroups, g => Assert.All(g.Items, e => Assert.Equal(g.Key, EquipmentBrowser.FactionPrefix + e.Faction)));
        Assert.Equal(13, factionGroups.Sum(g => g.Items.Count));

        // Dans chaque groupe : de A à Z.
        Assert.All(groups, g =>
        {
            var names = g.Items.Select(e => CogQuery.Fold(d.L.EquipmentName(e).Text)).ToList();
            Assert.Equal(names.Order(StringComparer.Ordinal), names);
        });
        Assert.Equal(d.L.FactionName(factionGroups[0].Key[EquipmentBrowser.FactionPrefix.Length..]).Text,
            EquipmentBrowser.GroupTitle(factionGroups[0].Key, d.L));
        Assert.Equal(d.L.MinistryName("HARMONY").Text, EquipmentBrowser.GroupTitle("HARMONY", d.L));
    }

    [Theory]
    [InlineData("HARMONY", 5)]
    [InlineData("AXIS", 8)]
    public async Task Filtre_par_ministere(string group, int count)
    {
        var d = await AppData.GetAsync();
        var groups = EquipmentBrowser.Browse(d.Catalog, d.L, group, null, EquipmentSort.Ministry);
        Assert.Equal(group, Assert.Single(groups).Key);
        Assert.Equal(count, groups[0].Items.Count);
    }

    [Fact]
    public async Task Filtre_par_faction()
    {
        var d = await AppData.GetAsync();
        var all = EquipmentBrowser.Browse(d.Catalog, d.L, EquipmentBrowser.Factions, null, EquipmentSort.Ministry);
        Assert.Equal(13, all.Sum(g => g.Items.Count));
        Assert.True(all.Count > 1);                     // un groupe par faction

        var first = all[0];
        var one = EquipmentBrowser.Browse(d.Catalog, d.L, first.Key, null, EquipmentSort.Ministry);
        Assert.Equal(first.Items, Assert.Single(one).Items);
    }

    [Fact]
    public async Task Choix_d_un_equipement_range_par_groupe_puis_par_nom()
    {
        var d = await AppData.GetAsync();
        var shuffled = d.Catalog.Catalog.Equipment.OrderByDescending(e => e.Id).ToList();
        var ordered = EquipmentBrowser.Ordered(shuffled, d.Catalog, d.L);
        var browsed = EquipmentBrowser.Browse(d.Catalog, d.L, null, null, EquipmentSort.Ministry).SelectMany(g => g.Items);
        Assert.Equal(browsed.Select(e => e.Id), ordered.Select(e => e.Id));
    }

    [Theory]
    [InlineData("phero", "E03")]                // nom, sans accent
    [InlineData("rouages HOSTILES", "E03")]     // effet, casse indifférente, mots cumulés
    [InlineData("esquive", "E38")]              // effet seulement
    public async Task Recherche_dans_les_noms_et_les_effets(string search, string id)
    {
        var d = await AppData.GetAsync();
        var found = EquipmentBrowser.Browse(d.Catalog, d.L, null, search, EquipmentSort.Ministry).SelectMany(g => g.Items);
        Assert.Contains(found, e => e.Id == id);
        Assert.Empty(EquipmentBrowser.Browse(d.Catalog, d.L, "AXIS", "phero", EquipmentSort.Ministry));
    }

    [Fact]
    public async Task Par_nom_un_seul_groupe_alphabetique()
    {
        var d = await AppData.GetAsync();
        var group = Assert.Single(EquipmentBrowser.Browse(d.Catalog, d.L, null, "", EquipmentSort.Name));
        var names = group.Items.Select(e => CogQuery.Fold(d.L.EquipmentName(e).Text)).ToList();
        Assert.Equal(53, names.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Empty(EquipmentBrowser.Browse(d.Catalog, d.L, null, "introuvable", EquipmentSort.Name));
    }
}
