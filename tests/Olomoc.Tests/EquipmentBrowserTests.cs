using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Equipment and boon catalog: groups, filter, search, sort.</summary>
public class EquipmentBrowserTests
{
    [Fact]
    public async Task Equippable_by_the_cabal()
    {
        var d = await AppData.GetAsync();
        var none = new Olomoc.Models.Equipment?[3];
        Assert.Null(d.Catalog.EquipableByCabal(new Olomoc.Models.Cog?[3], none, null));       // nothing to compare

        // Faction alone: its cards.
        var faction = d.Catalog.EquipableByCabal(new Olomoc.Models.Cog?[3], none, "MAISEL")!;
        Assert.Contains("E11" /* Harmonium */, faction);
        Assert.DoesNotContain("E03" /* Phéromones */, faction);

        // Harmony and Effort: their Ministries, the faction; not Security, the Axis or another faction.
        var cogs = new Olomoc.Models.Cog?[] { d.Cog("3R06"), d.Cog("2R11"), null };
        var equipable = d.Catalog.EquipableByCabal(cogs, none, "MAISEL")!;
        foreach (var id in new[] { "E03" /* Phéromones */, "E07" /* Ration */, "E11" /* Harmonium */ }) Assert.Contains(id, equipable);
        foreach (var id in new[] { "E08" /* Lumen */, "D01" /* Carapace */, "E13" /* Asservisseur mental */ }) Assert.DoesNotContain(id, equipable);

        var groups = EquipmentBrowser.Browse(d.Catalog, d.L, null, "", EquipmentSort.Ministry, equipable);
        Assert.Equal(equipable.Count, groups.Sum(g => g.Items.Count));
        Assert.Equal(["EFFORT", "HARMONY"], groups.Select(g => g.Key).Where(k => EquipmentBrowser.GroupMinistry(k) is not null));
    }

    [Fact]
    public async Task Cabal_shared_with_the_catalog_window()
    {
        var d = await AppData.GetAsync();
        var cogs = new Olomoc.Models.Cog?[] { d.Cog("3R06"), null, d.Cog("1R01") };
        var equipment = new Olomoc.Models.Equipment?[] { d.Equipment("E03" /* Phéromones */), null, null };
        var (faction, readCogs, readEquipment) = CabalDraft.Read(CabalDraft.Share("MAISEL", cogs, equipment), d.Catalog);
        Assert.Equal("MAISEL", faction);
        Assert.Equal(cogs.Select(c => c?.Id), readCogs.Select(c => c?.Id));
        Assert.Equal(equipment.Select(e => e?.Id), readEquipment.Select(e => e?.Id));

        Assert.Null(CabalDraft.Read(null, d.Catalog).Faction);                                // nothing published
        Assert.Null(CabalDraft.Read("|,,", d.Catalog).Faction);                               // empty cabal
    }

    [Fact]
    public async Task By_Ministry_in_catalog_order_then_factions()
    {
        var d = await AppData.GetAsync();
        var groups = EquipmentBrowser.Browse(d.Catalog, d.L, null, "", EquipmentSort.Ministry);
        Assert.Equal(d.Catalog.Catalog.Equipment.Count, groups.Sum(g => g.Items.Count));
        Assert.Equal(53, groups.Sum(g => g.Items.Count));
        string[] ministries = ["COORDINATION", "INTEGRITY", "EFFORT", "PROGRESS", "HARMONY", "EDIFICATION", "SECURITY", "FRINGE", "AXIS"];
        Assert.Equal(ministries, groups.Take(ministries.Length).Select(g => g.Key));
        Assert.All(groups.Single(g => g.Key == "AXIS").Items, e => Assert.Equal(Olomoc.Models.EquipmentKind.BOON, e.Kind));

        // Then one group per faction, in the order of the faction reference.
        var factionGroups = groups.Skip(ministries.Length).ToList();
        var order = d.Catalog.AllFactions.Select(f => EquipmentBrowser.FactionPrefix + f.Code).ToList();
        Assert.All(factionGroups, g => Assert.Contains(g.Key, order));
        Assert.Equal(factionGroups.Select(g => g.Key).OrderBy(order.IndexOf), factionGroups.Select(g => g.Key));
        Assert.All(factionGroups, g => Assert.All(g.Items, e => Assert.Equal(g.Key, EquipmentBrowser.FactionPrefix + e.Faction)));
        Assert.Equal(13, factionGroups.Sum(g => g.Items.Count));

        // Within each group: A to Z.
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
    public async Task Filter_by_Ministry(string group, int count)
    {
        var d = await AppData.GetAsync();
        var groups = EquipmentBrowser.Browse(d.Catalog, d.L, group, null, EquipmentSort.Ministry);
        Assert.Equal(group, Assert.Single(groups).Key);
        Assert.Equal(count, groups[0].Items.Count);
    }

    [Fact]
    public async Task Filter_by_faction()
    {
        var d = await AppData.GetAsync();
        var all = EquipmentBrowser.Browse(d.Catalog, d.L, EquipmentBrowser.Factions, null, EquipmentSort.Ministry);
        Assert.Equal(13, all.Sum(g => g.Items.Count));
        Assert.True(all.Count > 1);                     // one group per faction

        var first = all[0];
        var one = EquipmentBrowser.Browse(d.Catalog, d.L, first.Key, null, EquipmentSort.Ministry);
        Assert.Equal(first.Items, Assert.Single(one).Items);
    }

    [Fact]
    public async Task Equipment_choice_sorted_by_group_then_by_name()
    {
        var d = await AppData.GetAsync();
        var shuffled = d.Catalog.Catalog.Equipment.OrderByDescending(e => e.Id).ToList();
        var ordered = EquipmentBrowser.Ordered(shuffled, d.Catalog, d.L);
        var browsed = EquipmentBrowser.Browse(d.Catalog, d.L, null, null, EquipmentSort.Ministry).SelectMany(g => g.Items);
        Assert.Equal(browsed.Select(e => e.Id), ordered.Select(e => e.Id));
    }

    [Theory]
    [InlineData("phero", "E03")]                // name, without accent
    [InlineData("rouages HOSTILES", "E03")]     // effect, case-insensitive, words combined
    [InlineData("esquive", "E38")]              // effect only
    public async Task Search_in_names_and_effects(string search, string id)
    {
        var d = await AppData.GetAsync();
        var found = EquipmentBrowser.Browse(d.Catalog, d.L, null, search, EquipmentSort.Ministry).SelectMany(g => g.Items);
        Assert.Contains(found, e => e.Id == id);
        Assert.Empty(EquipmentBrowser.Browse(d.Catalog, d.L, "AXIS", "phero", EquipmentSort.Ministry));
    }

    [Fact]
    public async Task By_name_a_single_alphabetical_group()
    {
        var d = await AppData.GetAsync();
        var group = Assert.Single(EquipmentBrowser.Browse(d.Catalog, d.L, null, "", EquipmentSort.Name));
        var names = group.Items.Select(e => CogQuery.Fold(d.L.EquipmentName(e).Text)).ToList();
        Assert.Equal(53, names.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Empty(EquipmentBrowser.Browse(d.Catalog, d.L, null, "introuvable", EquipmentSort.Name));
    }
}
