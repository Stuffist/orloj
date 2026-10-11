using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Recherche : mots libres + conditions sur les stats, mots-clés de toutes les langues.</summary>
public class SearchTests
{
    private static async Task<string> Parsed(string input)
    {
        var d = await AppData.GetAsync();
        var q = CogQuery.Parse(input, d.L.StatKeywords());
        return string.Join(" ", q.Conditions.Select(c => $"{c.Stat}{c.Op}{c.Value}")) + " | " + string.Join(",", q.Words);
    }

    [Theory]
    [InlineData("tir>4", "Ranged>4 | ")]
    [InlineData("kantor tir>4 esq<=3", "Ranged>4 Dodge<=3 | kantor")]
    [InlineData("TEC >= 3  cbt = 2", "Tech>=3 Melee=2 | ")]
    [InlineData("tir > 4", "Ranged>4 | ")]
    [InlineData("technique≥4 esquive≤2", "Tech>=4 Dodge<=2 | ")]
    [InlineData("rng>4 dod<=3", "Ranged>4 Dodge<=3 | ")]                 // mots-clés anglais
    [InlineData("melee>3 tech<2", "Melee>3 Tech<2 | ")]
    [InlineData("tir>", " | ")]                                          // condition en cours de frappe
    [InlineData("petra tir> esq<3", "Dodge<3 | petra")]
    [InlineData("Pétra GARDE", " | petra,garde")]                        // accents et casse
    [InlineData("satir>4", " | satir>4")]                                // pas une condition
    public async Task Analyse_de_la_saisie(string input, string expected) =>
        Assert.Equal(expected, await Parsed(input));

    private static async Task<int> Count(string search)
    {
        var d = await AppData.GetAsync();
        var q = CogQuery.Parse(search, d.L.StatKeywords());
        return Enumerable.Range(1, 3).Sum(rank => d.Catalog.AvailableCogs(rank, null, null, q).Count);
    }

    [Fact]
    public async Task Conditions_cumulees()
    {
        var d = await AppData.GetAsync();
        Assert.Equal(d.Catalog.Catalog.Cogs.Count(c => c.Stats.Ranged > 4 && c.Stats.Dodge <= 3), await Count("tir>4 esq<=3"));
        Assert.Equal(await Count("tir>4 esq<=3"), await Count("rng>4 dod<=3"));
    }

    [Fact]
    public async Task Mots_sur_le_nom_et_le_titre_sans_accents()
    {
        Assert.Equal(1, await Count("petra garde"));
        Assert.Equal(0, await Count("pétra tir>7"));
    }

    [Fact]
    public async Task Retirer_une_condition_de_la_saisie()
    {
        var d = await AppData.GetAsync();
        var q = CogQuery.Parse("kantor tir>4 esq<=3", d.L.StatKeywords());
        Assert.Equal("kantor esq<=3", CogQuery.Without("kantor tir>4 esq<=3", q.Conditions[0]));
    }
}
