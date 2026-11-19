using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Pasted cabal: text in the export format (reference builder) or JSON.</summary>
public class CabalImportTests
{
    private static async Task<(AppData D, CabalExporter X, CabalImport I)> Services()
    {
        var d = await AppData.CreateAsync();
        var x = new CabalExporter(d.L);
        return (d, x, new CabalImport(d.Catalog, d.L, x));
    }

    // Legal composition in the Maisel League (rank 1, 2, 3).
    private static readonly string[] CogIds = { "1R01", "2R08", "3R06" };
    private static readonly string[] EquipmentIds = { "E11" /* Harmonium */, "D01" /* Carapace */, "E03" /* Phéromones */ };

    private static CabalExport Maisel(AppData d, CabalExporter x) => x.ToModel("Assaut", "MAISEL",
        CogIds.Select(id => (Cog?)d.Cog(id)).ToArray(), EquipmentIds.Select(id => (Equipment?)d.Equipment(id)).ToArray());

    [Fact]
    public async Task Text_export_read_back_identically()
    {
        var (d, x, import) = await Services();
        var text = x.ToText("Assaut", CogIds.Select(id => (Cog?)d.Cog(id)).ToArray(), EquipmentIds.Select(id => (Equipment?)d.Equipment(id)).ToArray());

        var result = import.Parse(text, faction: null);
        Assert.Empty(result.Issues);
        Assert.Equal("Assaut", result.Cabal!.Name);
        Assert.Equal("MAISEL", result.Cabal.Faction?.Code);          // deduced: legal only in Maisel
        Assert.True(CabalLibrary.SameCards(Maisel(d, x), result.Cabal));
    }

    [Fact]
    public async Task Tolerant_hand_typing()
    {
        var (d, x, import) = await Services();
        // Lowercase, no accents, long or attached dash, extra spaces, any order, CRLF.
        var text = "  Ma liste\r\nPetra Kantor –  harmonium\r\n\r\nle chasseur-carapace\r\nBOHDAN   GROSS - PHEROMONES ";
        var result = import.Parse(text, faction: "MAISEL");

        Assert.Equal("Ma liste", result.Cabal!.Name);                    // first line not recognized: the name
        Assert.Empty(result.Issues);
        Assert.True(CabalLibrary.SameCards(Maisel(d, x), result.Cabal));
    }

    [Fact]
    public async Task Unknown_lines_and_duplicate_rank_reported()
    {
        var (_, _, import) = await Services();
        var result = import.Parse("Ma liste\nDOMINIK KRALOV - HARMONIUM\nGANDALF - BATON\nBOHDAN GROSS", "MAISEL");
        Assert.Equal(new[] { ("unknownLine", "3"), ("sameRank", "4") }, result.Issues.Select(i => (i.Key, i.Args[0])));
    }

    [Fact]
    public async Task JSON_accepted()
    {
        var (d, x, import) = await Services();
        var json = x.ToJson("Assaut", "MAISEL", CogIds.Select(id => (Cog?)d.Cog(id)).ToArray(), EquipmentIds.Select(id => (Equipment?)d.Equipment(id)).ToArray());
        var result = import.Parse("\n  " + json, faction: "RUBIN");      // the JSON's faction prevails
        Assert.Empty(result.Issues);
        Assert.Equal("MAISEL", result.Cabal!.Faction?.Code);
        Assert.Equal("json", Assert.Single(import.Parse("{ cassé", null).Issues).Key);
    }
}
