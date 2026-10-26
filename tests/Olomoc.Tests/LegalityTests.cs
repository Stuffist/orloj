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

    // ---- Harmonium (Maisel): one cog of the Axis, of rank 1 or 2, out of faction ----

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
    public async Task A_single_Harmonium_covers_only_one_cog() =>
        AssertIssue(await Check("MAISEL", ("3R07", "D01" /* Carapace */), ("2R08", "D03" /* Stigmates */), ("1R01", "E11" /* Harmonium */)), "outsideFaction");

    [Fact]
    public async Task Harmonium_refuses_a_rank_3()
    {
        var issue = AssertIssue(await Check("MAISEL", ("3R06", "E03" /* Phéromones */), ("2R02", "E11" /* Harmonium */), ("1R09", "D01" /* Carapace */)), "outsideFaction");
        Assert.Equal(new[] { "1R09" }, issue.Args[1].Values);
    }

    // ---- Asservisseur mental (Others): its wearer, of rank 1 or 2 ----

    [Fact]
    public async Task Asservisseur_allows_its_wearer() =>
        AssertLegal(await Check("OTHERS", ("3R17", "E26" /* Dose de Mélisse */), ("2R02", "E13" /* Asservisseur mental */), ("1R09", "D01" /* Carapace */)));

    [Fact]
    public async Task Asservisseur_carried_by_another_does_not_cover()
    {
        var issue = AssertIssue(await Check("OTHERS", ("3R17", "E26" /* Dose de Mélisse */), ("2R02", "D01" /* Carapace */), ("1R09", "E13" /* Asservisseur mental */)), "outsideFaction");
        Assert.Equal(new[] { "2R02" }, issue.Args[1].Values);
    }

    [Fact]
    public async Task Asservisseur_refuses_a_rank_3_wearer() =>
        AssertIssue(await Check("OTHERS", ("3R17", "E26" /* Dose de Mélisse */), ("2R08", "D01" /* Carapace */), ("1R01", "E13" /* Asservisseur mental */)), "outsideFaction");

    // ---- ALL_MINISTRIES: the wearer counts as affiliated with every Ministry ----

    [Fact]
    public async Task All_ministries_makes_the_Fringe_available() =>
        AssertLegal(await Check("MAISEL", ("3R06", "E26" /* Dose de Mélisse */), ("2R04", "E03" /* Phéromones */), ("1R01", AllMinistries)));

    [Fact]
    public async Task All_ministries_does_not_make_itself_available()
    {
        var issue = AssertIssue(await Check("MAISEL", ("3R06", "E03" /* Phéromones */), ("2R02", "E01" /* Manuel du Progrès */), ("1R01", AllMinistries)), "ministryAbsent");
        Assert.Equal(new[] { "COORDINATION" }, issue.Args[1].Values);
    }

    [Fact]
    public async Task All_ministries_takes_its_wearer_slot() =>
        AssertIssue(await Check("MAISEL", ("3R06", "E26" /* Dose de Mélisse */), ("2R04", AllMinistries), ("1R01", "E08" /* Lumen */)), "ministryOverflow");

    [Fact]
    public async Task Without_all_ministries_the_Fringe_is_missing() =>
        AssertIssue(await Check("MAISEL", ("3R06", "E26" /* Dose de Mélisse */), ("2R04", "E03" /* Phéromones */), ("1R01", "E08" /* Lumen */)), "ministryAbsent");

    [Fact]
    public async Task All_ministries_counts_as_the_Axis_for_Harmonium() =>
        AssertLegal(await Check("MAISEL", ("3R20", AllMinistries), ("2R04", "E11" /* Harmonium */), ("1R01", "E08" /* Lumen */)));

    [Fact]
    public async Task Passe_droit_du_Census_no_longer_grants_all_ministries()
    {
        var d = await AppData.GetAsync();
        Assert.False(d.Equipment("E29" /* Passe-droit du Census */).GrantsAllMinistries);
    }

    // ---- Availability ----

    [Fact]
    public async Task Lists_offered_according_to_the_recruitment_rules()
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
    public async Task Factions_compatible_with_the_cogs_already_chosen()
    {
        var d = await AppData.GetAsync();
        var all = d.Catalog.AllFactions.Select(f => f.Code).ToList();
        Assert.Equal(all, d.Catalog.CompatibleFactions(new Olomoc.Models.Cog?[3]));

        // A single cog: its factions, each one welcoming it; a cog specific to one faction.
        var indra = d.Catalog.CompatibleFactions([null, null, d.Cog("1R16")]);
        Assert.Equal(["URKARAS"], indra);
        var petra = d.Catalog.CompatibleFactions([d.Cog("1R01")]);
        Assert.Contains("MAISEL", petra);
        Assert.All(petra, f => Assert.True(d.Catalog.Fits(d.Cog("1R01"), f)));

        // Several cogs: the intersection, in the catalog's order.
        var both = d.Catalog.CompatibleFactions([d.Cog("1R01"), d.Cog("1R12")]);
        Assert.Equal(petra.Intersect(d.Catalog.CompatibleFactions([d.Cog("1R12")])), both);
    }

    [Fact]
    public async Task Factions_of_a_cog_member_or_recruited_through_a_card()
    {
        var d = await AppData.GetAsync();
        string Show(string id) => string.Join(" ", d.Catalog.FactionsOf(d.Cog(id))
            .Select(f => f.Member ? f.Faction : $"{f.Faction}<{d.L.ReferenceName(f.Via!)}>"));

        // Rank 1: its three factions, and Les Autres through the card that recruits its wearer.
        Assert.Equal("OTHERS<Asservisseur mental> BLOCKHAUS DYKOVIAN MAISEL", Show("3R05"));
        // Axis cog: Les Autres, and the Maisel League through the Harmonium.
        Assert.Equal("OTHERS MAISEL<Harmonium>", Show("2R08"));
        // Rank 3: no card recruits it outside its faction.
        Assert.Equal("URKARAS", Show("1R16"));
        // Always consistent with Fits.
        foreach (var cog in d.Catalog.Catalog.Cogs)
            Assert.Equal(d.Catalog.AllFactions.Where(f => d.Catalog.Fits(cog, f.Code)).Select(f => f.Code),
                         d.Catalog.FactionsOf(cog).Select(f => f.Faction));
    }

    [Fact]
    public async Task Without_a_faction_offered_cogs_compatible_with_those_already_chosen()
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
        // Without a list of factions: unchanged (all the cogs of the rank).
        Assert.Equal(d.Catalog.Catalog.Cogs.Count(c => c.Rank == 3), d.Catalog.AvailableCogs(3, null, null, CogQuery.Empty).Count);
    }

    [Fact]
    public async Task All_ministries_carried_elsewhere_widens_the_offered_equipment()
    {
        var d = await AppData.GetAsync();
        var cogs = new Cog?[] { d.Cog("3R06"), d.Cog("2R04"), d.Cog("1R01") };
        var equipment = new Equipment?[] { null, null, AllMinistriesCard };
        Assert.Contains(d.Catalog.AvailableEquipment(0, cogs, equipment, "MAISEL"), e => e.Ministry == "FRINGE");
        Assert.DoesNotContain(d.Catalog.AvailableEquipment(2, cogs, equipment, "MAISEL"), e => e.Ministry == "FRINGE");
    }

    // ---- Greyed-out choices: already "taken" by the rest of the cabal ----

    private static async Task<(AppData D, Cog?[] Cogs)> Cogs(params string?[] ids)
    {
        var d = await AppData.GetAsync();
        return (d, ids.Select(id => id is null ? null : (Cog?)d.Cog(id)).ToArray());
    }

    [Fact]
    public async Task Harmonium_already_used_greys_out_another_Axis_cog()
    {
        var (d, cogs) = await Cogs("3R07", null, null);                // Axis, out of faction: through Harmonium
        Assert.False(d.Catalog.CanJoin(1, d.Cog("2R08"), cogs, "MAISEL"));
        Assert.True(d.Catalog.CanJoin(1, d.Cog("2R11"), cogs, "MAISEL"));   // member of Maisel
        Assert.True(d.Catalog.CanJoin(0, d.Cog("3R07"), cogs, "MAISEL"));   // replacing it with itself
    }

    [Fact]
    public async Task Without_a_faction_greys_out_what_no_faction_covers_together()
    {
        var (d, cogs) = await Cogs("3R07", null, "1R01");              // possible for Maisel only
        Assert.False(d.Catalog.CanJoin(1, d.Cog("2R08"), cogs, null));
        Assert.True(d.Catalog.CanJoin(1, d.Cog("2R11"), cogs, null));
    }

    [Fact]
    public async Task Asservisseur_already_used_greys_out_another_out_of_faction_cog()
    {
        var (d, cogs) = await Cogs(null, "2R02", null);                // out of faction for Others
        Assert.False(d.Catalog.CanJoin(0, d.Cog("3R05"), cogs, "OTHERS"));
        Assert.True(d.Catalog.CanJoin(0, d.Cog("3R07"), cogs, "OTHERS"));
    }

    [Fact]
    public async Task Cabal_already_impossible_does_not_grey_out_everything()
    {
        var (d, cogs) = await Cogs("3R07", "2R08", null);              // two Axis cogs, a single Harmonium
        Assert.True(d.Catalog.CanJoin(2, d.Cog("1R01"), cogs, "MAISEL"));
    }

    [Fact]
    public async Task Out_of_faction_cog_swaps_the_one_taking_its_card()
    {
        var (d, cogs) = await Cogs("3R07", null, "1R01");              // 3R07: Axis, through Harmonium
        Assert.Equal(new[] { 0 }, d.Catalog.CogConflict(1, d.Cog("2R08"), cogs, "MAISEL")?.Displaced);
        Assert.Equal(new[] { 0 }, d.Catalog.CogConflict(1, d.Cog("2R08"), cogs, null)?.Displaced);   // without a faction
        Assert.Null(d.Catalog.CogConflict(1, d.Cog("2R11"), cogs, "MAISEL"));                        // member: nothing to swap
    }

    [Fact]
    public async Task Recruitment_card_equipped_automatically()
    {
        var d = await AppData.GetAsync();
        var maisel = new Cog?[] { null, d.Cog("2R08"), d.Cog("1R01") };
        Assert.Equal("Harmonium", d.L.ReferenceName(d.Catalog.RecruitCard(1, maisel, new Equipment?[3], "MAISEL")!));
        // Already covered by the Harmonium another cog carries: nothing to equip.
        Assert.Null(d.Catalog.RecruitCard(1, maisel, new Equipment?[] { null, null, d.Equipment("E11" /* Harmonium */) }, "MAISEL"));
        Assert.Null(d.Catalog.RecruitCard(2, maisel, new Equipment?[3], "MAISEL"));                  // member

        var others = new Cog?[] { d.Cog("3R07"), d.Cog("2R02"), null };
        Assert.Equal("Asservisseur mental", d.L.ReferenceName(d.Catalog.RecruitCard(1, others, new Equipment?[3], "OTHERS")!));
        // Carried by a member, who does not benefit from it: offered anyway (choosing it moves it).
        var onMember = new Equipment?[] { d.Equipment("E13" /* Asservisseur mental */), null, null };
        Assert.Equal("Asservisseur mental", d.L.ReferenceName(d.Catalog.RecruitCard(1, others, onMember, "OTHERS")!));
    }

    [Fact]
    public async Task Factions_compatible_with_the_cogs_taken_together()
    {
        var (d, cogs) = await Cogs("3R07", "2R08", null);
        var factions = d.Catalog.CompatibleFactions(cogs);
        Assert.DoesNotContain("MAISEL", factions);
        Assert.Contains("OTHERS", factions);
    }

    [Fact]
    public async Task Equipment_already_carried_swapped()
    {
        var (d, cogs) = await Cogs("3R06", "2R11", "1R01");
        var equipment = new Equipment?[] { d.Equipment("E03" /* Phéromones */), null, null };
        var swap = d.Catalog.EquipmentConflict(1, d.Equipment("E03" /* Phéromones */), cogs, equipment);
        Assert.Equal("equipment.swap.duplicate", swap?.Reason);
        Assert.Equal(new[] { 0 }, swap!.Displaced);                                                 // removed from Bohdan Gross
        Assert.Null(d.Catalog.EquipmentConflict(0, d.Equipment("E03" /* Phéromones */), cogs, equipment));   // its own slot
    }

    [Fact]
    public async Task Ministry_already_used_swaps_its_equipment()
    {
        // A single Harmony cog, which already unlocks Phéromones.
        var (d, cogs) = await Cogs("3R06", "2R11", "1R01");
        var equipment = new Equipment?[] { d.Equipment("E03" /* Phéromones */), null, null };
        var swap = d.Catalog.EquipmentConflict(1, d.Equipment("E21" /* Collier de dévotion */), cogs, equipment);
        Assert.Equal("equipment.swap.ministry", swap?.Reason);
        Assert.Equal(new[] { 0 }, swap!.Displaced);
        Assert.Null(d.Catalog.EquipmentConflict(1, d.Equipment("E07" /* Ration */), cogs, equipment));      // Effort: its own
    }

    [Fact]
    public async Task All_ministries_carried_elsewhere_unlocks_one_more_Ministry()
    {
        // 1R01 carries the ALL_MINISTRIES card (unlocked by 2R04's Coordination): it can unlock Harmony.
        var (d, cogs) = await Cogs("3R06", "2R04", "1R01");
        var collier = d.Equipment("E21" /* Collier de dévotion */);
        var sans = new Equipment?[] { d.Equipment("E03" /* Phéromones */), null, null };
        var avec = new Equipment?[] { d.Equipment("E03" /* Phéromones */), null, AllMinistriesCard };
        Assert.Equal("equipment.swap.ministry", d.Catalog.EquipmentConflict(1, collier, cogs, sans)?.Reason);
        Assert.Null(d.Catalog.EquipmentConflict(1, collier, cogs, avec));
        AssertLegal(d.Catalog.CheckLegality("MAISEL", cogs, new[] { avec[0], collier, avec[2] }));
        Assert.Equal(0, d.Catalog.MinistryRemaining(1, "HARMONY", cogs, sans));
        Assert.Equal(1, d.Catalog.MinistryRemaining(1, "HARMONY", cogs, avec));
    }

    [Fact]
    public async Task Remaining_equipment_per_Ministry()
    {
        var (d, cogs) = await Cogs("3R06", "2R11", "1R01");                // Harmony, Effort, Security
        var equipment = new Equipment?[] { d.Equipment("E03" /* Phéromones */), null, null };
        Assert.Equal(0, d.Catalog.MinistryRemaining(1, "HARMONY", cogs, equipment));   // Bohdan Gross already has his
        Assert.Equal(1, d.Catalog.MinistryRemaining(0, "HARMONY", cogs, equipment));   // its own, which it would replace
        Assert.Equal(1, d.Catalog.MinistryRemaining(1, "EFFORT", cogs, equipment));
        Assert.Equal(0, d.Catalog.MinistryRemaining(1, "AXIS", cogs, equipment));      // no Axis cog
    }

    [Fact]
    public async Task Messages_are_translated_with_the_language_names()
    {
        var d = await AppData.CreateAsync();
        var r = d.Catalog.CheckLegality("MAISEL",
            new Cog?[] { d.Cog("3R06"), d.Cog("2R08"), d.Cog("1R01") },
            new Equipment?[] { d.Equipment("E03" /* Phéromones */), d.Equipment("D01" /* Carapace */), d.Equipment("E08" /* Lumen */) });
        var issue = Assert.Single(r.Issues);
        Assert.Contains("Hors faction Ligue de Maisel : Le Chasseur", d.L.Describe(issue));
        await d.L.SetLanguageAsync("en");
        Assert.StartsWith("Outside the Ligue de Maisel faction: Le Chasseur", d.L.Describe(issue));   // faction: French name assumed; cog: French fallback
        Assert.False(d.L.FactionName("MAISEL").IsFallback);
        Assert.True(d.L.CogName(d.Cog("2R08")).IsFallback);
    }
}
