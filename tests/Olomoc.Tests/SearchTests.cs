using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Search: free words + conditions on the stats, keywords of every language.</summary>
public class SearchTests
{
    [Theory]
    [InlineData("kantor tir>4 esq<=3", "Ranged>4 Dodge<=3 | kantor")]
    [InlineData("rng>4 dod<=3", "Ranged>4 Dodge<=3 | ")]                 // English keywords
    [InlineData("petra tir> esq<3", "Dodge<3 | petra")]                  // condition being typed
    [InlineData("Pétra GARDE", " | petra,garde")]                        // accents and case
    public async Task Parsing_the_input(string input, string expected)
    {
        var d = await AppData.GetAsync();
        var q = CogQuery.Parse(input, d.L.StatKeywords());
        Assert.Equal(expected, string.Join(" ", q.Conditions.Select(c => $"{c.Stat}{c.Op}{c.Value}")) + " | " + string.Join(",", q.Words));
    }

    [Fact]
    public async Task Combined_conditions()
    {
        var d = await AppData.GetAsync();
        var q = CogQuery.Parse("tir>4 esq<=3", d.L.StatKeywords());
        var count = Enumerable.Range(1, 3).Sum(rank => d.Catalog.AvailableCogs(rank, null, null, q).Count);
        Assert.Equal(d.Catalog.Catalog.Cogs.Count(c => c.Stats.Ranged > 4 && c.Stats.Dodge <= 3), count);
    }
}
