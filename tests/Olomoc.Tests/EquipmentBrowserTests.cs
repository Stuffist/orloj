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
        Assert.Equal(new[] { "COORDINATION", "INTEGRITY", "EFFORT", "PROGRESS", "HARMONY", "EDIFICATION", "SECURITY", "FRINGE", "AXIS", EquipmentBrowser.Factions },
            groups.Select(g => g.Key));
        Assert.All(groups.Single(g => g.Key == "AXIS").Items, e => Assert.Equal(Olomoc.Models.EquipmentKind.BOON, e.Kind));

        // Cartes de faction : regroupées par faction, dans l'ordre du référentiel de faction.
        var factions = groups.Last().Items.Select(e => e.Faction!).ToList();
        var order = d.Catalog.AllFactions.Select(f => f.Code).ToList();
        Assert.Equal(factions.OrderBy(order.IndexOf), factions);
        Assert.Equal(13, factions.Count);
    }

    [Theory]
    [InlineData("HARMONY", 5)]
    [InlineData("AXIS", 8)]
    [InlineData(EquipmentBrowser.Factions, 13)]
    public async Task Filtre_par_groupe(string group, int count)
    {
        var d = await AppData.GetAsync();
        var groups = EquipmentBrowser.Browse(d.Catalog, d.L, group, null, EquipmentSort.Ministry);
        Assert.Equal(group, Assert.Single(groups).Key);
        Assert.Equal(count, groups[0].Items.Count);
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
