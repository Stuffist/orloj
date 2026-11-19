using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Equipment and boon catalog: groups, filter, search.</summary>
public class EquipmentBrowserTests
{
    [Fact]
    public async Task Equippable_by_the_cabal()
    {
        var d = await AppData.GetAsync();
        var none = new Equipment?[3];

        // Harmony and Effort: their Ministries, the faction; not Security, the Axis or another faction.
        var cogs = new Cog?[] { d.Cog("3R06"), d.Cog("2R11"), null };
        var equipable = d.Catalog.EquipableByCabal(cogs, none, "MAISEL")!;
        foreach (var id in new[] { "E03" /* Phéromones */, "E07" /* Ration */, "E11" /* Harmonium */ }) Assert.Contains(id, equipable);
        foreach (var id in new[] { "E08" /* Lumen */, "D01" /* Carapace */, "E13" /* Asservisseur mental */ }) Assert.DoesNotContain(id, equipable);
    }

    [Fact]
    public async Task By_Ministry_in_catalog_order_then_factions()
    {
        var d = await AppData.GetAsync();
        var groups = EquipmentBrowser.Browse(d.Catalog, d.L, null, "", EquipmentSort.Ministry);
        Assert.Equal(d.Catalog.Catalog.Equipment.Count, groups.Sum(g => g.Items.Count));
        string[] ministries = ["COORDINATION", "INTEGRITY", "EFFORT", "PROGRESS", "HARMONY", "EDIFICATION", "SECURITY", "FRINGE", "AXIS"];
        Assert.Equal(ministries, groups.Take(ministries.Length).Select(g => g.Key));
        Assert.All(groups.Skip(ministries.Length), g => Assert.StartsWith(EquipmentBrowser.FactionPrefix, g.Key));
    }

    [Theory]
    [InlineData("phero", "E03")]                // name, without accent
    [InlineData("rouages HOSTILES", "E03")]     // effect, case-insensitive, words combined
    public async Task Search_in_names_and_effects(string search, string id)
    {
        var d = await AppData.GetAsync();
        var found = EquipmentBrowser.Browse(d.Catalog, d.L, null, search, EquipmentSort.Ministry).SelectMany(g => g.Items);
        Assert.Contains(found, e => e.Id == id);
    }
}
