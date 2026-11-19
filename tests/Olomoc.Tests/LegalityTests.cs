using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Cabal composition (rulebook p.32) and rules carried by the cards (buildRules).</summary>
public class LegalityTests
{
    // Fictitious Coordination card carrying the ALL_MINISTRIES rule: no card of the catalog carries
    // it (Passe-droit du Census lost it), but the editor can give it to a card.
    private const string AllMinistries = "(Tous les Ministères)";
    private static readonly Equipment AllMinistriesCard = new()
    {
        Id = "TEST-ALL-MINISTRIES",
        Kind = EquipmentKind.EQUIPMENT,
        Ministry = "COORDINATION",
        BuildRules = { new BuildRule { Type = BuildRuleType.ALL_MINISTRIES } },
        Published = true
    };

    // Slots in the order rank 1, rank 2, rank 3: (cog id, equipment/boon name).
    private static async Task<LegalityResult> Check(string faction, params (string Cog, string Equipment)[] slots)
    {
        var d = await AppData.GetAsync();
        return d.Catalog.CheckLegality(faction,
            slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(),
            slots.Select(s => (Equipment?)(s.Equipment == AllMinistries ? AllMinistriesCard : d.Equipment(s.Equipment))).ToArray());
    }

    private static void AssertLegal(LegalityResult r) =>
        Assert.True(r.IsLegal, "illegal: " + string.Join(" | ", r.Issues.Select(i => i.Key)));

    private static LegalityIssue AssertIssue(LegalityResult r, string key)
    {
        Assert.False(r.IsLegal);
        return Assert.Single(r.Issues, i => i.Key == key);
    }

    private static async Task<(AppData D, Cog?[] Cogs)> Cogs(params string?[] ids)
    {
        var d = await AppData.GetAsync();
        return (d, ids.Select(id => id is null ? null : (Cog?)d.Cog(id)).ToArray());
    }

    // ---- Composition ----

    [Fact]
    public async Task Harmonium_allows_a_cog_of_the_Axis() =>
        AssertLegal(await Check("MAISEL", ("3R06", "E03" /* Phéromones */), ("2R08", "D01" /* Carapace */), ("1R01", "E11" /* Harmonium */)));

    [Fact]
    public async Task Without_Harmonium_the_Axis_cog_is_out_of_faction()
    {
        var issue = AssertIssue(await Check("MAISEL", ("3R06", "E03" /* Phéromones */), ("2R08", "D01" /* Carapace */), ("1R01", "E08" /* Lumen */)), "outsideFaction");
        Assert.Equal(new[] { "2R08" }, issue.Args[1].Values);
    }

    [Fact]
    public async Task Asservisseur_carried_by_another_does_not_cover()
    {
        AssertLegal(await Check("OTHERS", ("3R17", "E26" /* Dose de Mélisse */), ("2R02", "E13" /* Asservisseur mental */), ("1R09", "D01" /* Carapace */)));
        var issue = AssertIssue(await Check("OTHERS", ("3R17", "E26" /* Dose de Mélisse */), ("2R02", "D01" /* Carapace */), ("1R09", "E13" /* Asservisseur mental */)), "outsideFaction");
        Assert.Equal(new[] { "2R02" }, issue.Args[1].Values);
    }

    [Fact]
    public async Task All_ministries_makes_the_Fringe_available()
    {
        AssertLegal(await Check("MAISEL", ("3R06", "E26" /* Dose de Mélisse */), ("2R04", "E03" /* Phéromones */), ("1R01", AllMinistries)));
        AssertIssue(await Check("MAISEL", ("3R06", "E26" /* Dose de Mélisse */), ("2R04", "E03" /* Phéromones */), ("1R01", "E08" /* Lumen */)), "ministryAbsent");
    }

    // ---- Availability ----

    [Fact]
    public async Task Lists_offered_according_to_the_recruitment_rules()
    {
        var d = await AppData.GetAsync();
        Assert.Contains(d.Catalog.AvailableCogs(2, "MAISEL", null, CogQuery.Empty), c => c.Id == "2R08");
        Assert.Equal("Harmonium", d.L.ReferenceName(d.Catalog.RecruitVia(d.Cog("2R08"), "MAISEL")!));
        Assert.Equal(d.Catalog.Catalog.Cogs.Count(c => c.Rank == 2), d.Catalog.AvailableCogs(2, "OTHERS", null, CogQuery.Empty).Count);
    }

    [Fact]
    public async Task Factions_compatible_with_the_cogs_already_chosen()
    {
        var d = await AppData.GetAsync();
        Assert.Equal(["URKARAS"], d.Catalog.CompatibleFactions([null, null, d.Cog("1R16")]));
        var (_, cogs) = await Cogs("3R07", "2R08", null);                // two Axis cogs, a single Harmonium possible
        Assert.DoesNotContain("MAISEL", d.Catalog.CompatibleFactions(cogs));
        Assert.Contains("OTHERS", d.Catalog.CompatibleFactions(cogs));
    }

    [Fact]
    public async Task Factions_of_a_cog_consistent_with_Fits()
    {
        var d = await AppData.GetAsync();
        foreach (var cog in d.Catalog.Catalog.Cogs)
            Assert.Equal(d.Catalog.AllFactions.Where(f => d.Catalog.Fits(cog, f.Code)).Select(f => f.Code),
                         d.Catalog.FactionsOf(cog).Select(f => f.Faction));
    }

    // ---- Greyed-out choices and swaps ----

    [Fact]
    public async Task Harmonium_already_used_greys_out_another_Axis_cog()
    {
        var (d, cogs) = await Cogs("3R07", null, "1R01");                // Axis, out of faction: through Harmonium
        Assert.False(d.Catalog.CanJoin(1, d.Cog("2R08"), cogs, "MAISEL"));
        Assert.True(d.Catalog.CanJoin(1, d.Cog("2R11"), cogs, "MAISEL"));   // member of Maisel
        Assert.Equal(new[] { 0 }, d.Catalog.CogConflict(1, d.Cog("2R08"), cogs, "MAISEL")?.Displaced);
    }

    [Fact]
    public async Task Recruitment_card_equipped_automatically()
    {
        var d = await AppData.GetAsync();
        var maisel = new Cog?[] { null, d.Cog("2R08"), d.Cog("1R01") };
        Assert.Equal("Harmonium", d.L.ReferenceName(d.Catalog.RecruitCard(1, maisel, new Equipment?[3], "MAISEL")!));
        Assert.Null(d.Catalog.RecruitCard(1, maisel, new Equipment?[] { null, null, d.Equipment("E11" /* Harmonium */) }, "MAISEL"));
    }

    [Fact]
    public async Task Equipment_already_carried_or_Ministry_already_used_swapped()
    {
        var (d, cogs) = await Cogs("3R06", "2R11", "1R01");                // Harmony, Effort, Security
        var equipment = new Equipment?[] { d.Equipment("E03" /* Phéromones */), null, null };
        var duplicate = d.Catalog.EquipmentConflict(1, d.Equipment("E03" /* Phéromones */), cogs, equipment);
        Assert.Equal("equipment.swap.duplicate", duplicate?.Reason);
        Assert.Equal(new[] { 0 }, duplicate!.Displaced);                                             // removed from Bohdan Gross
        var ministry = d.Catalog.EquipmentConflict(1, d.Equipment("E21" /* Collier de dévotion */), cogs, equipment);
        Assert.Equal("equipment.swap.ministry", ministry?.Reason);
        Assert.Equal(new[] { 0 }, ministry!.Displaced);
        Assert.Null(d.Catalog.EquipmentConflict(1, d.Equipment("E07" /* Ration */), cogs, equipment));      // Effort: its own
    }
}
