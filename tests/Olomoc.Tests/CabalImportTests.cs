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
    private static CabalExport Maisel(AppData d, CabalExporter x, string? name = "Assaut") => x.ToModel(name, "MAISEL",
        new Cog?[] { d.Cog("1R01"), d.Cog("2R08"), d.Cog("3R06") },
        new Equipment?[] { d.Equipment("E11" /* Harmonium */), d.Equipment("D01" /* Carapace */), d.Equipment("E03" /* Phéromones */) });

    [Fact]
    public async Task Text_export_read_back_identically()
    {
        var (d, x, import) = await Services();
        var cabal = Maisel(d, x);
        var text = x.ToText(cabal.Name, Cogs(d, cabal), Equipment(d, cabal));

        var result = import.Parse(text, faction: null);
        Assert.Empty(result.Issues);
        Assert.Equal("Assaut", result.Cabal!.Name);
        Assert.Equal("MAISEL", result.Cabal.Faction?.Code);          // deduced: legal only in Maisel
        Assert.True(CabalLibrary.SameCards(cabal, result.Cabal));
    }

    [Fact]
    public async Task Tolerant_hand_typing()
    {
        var (d, x, import) = await Services();
        // Lowercase, no accents, long or attached dash, extra spaces, no name, any order, CRLF.
        var text = "  phéromones est absent ici\r\nPetra Kantor –  harmonium\r\n\r\nle chasseur-carapace\r\nBOHDAN   GROSS - PHEROMONES ";
        var result = import.Parse(text, faction: "MAISEL");

        Assert.Equal("phéromones est absent ici", result.Cabal!.Name);   // first line not recognized: the name
        Assert.Empty(result.Issues);
        Assert.True(CabalLibrary.SameCards(Maisel(d, x), result.Cabal));
    }

    [Fact]
    public async Task English_names_and_hyphen_inside_a_name()
    {
        var (d, x, import) = await Services();
        await d.L.SetLanguageAsync("en");
        var cabal = x.ToModel("B", "MAISEL",
            new Cog?[] { d.Cog("1R01"), d.Cog("2R04"), d.Cog("3R06") },
            new Equipment?[] { d.Equipment("E29" /* Passe-droit du Census */), d.Equipment("E03" /* Phéromones */), d.Equipment("E26" /* Dose de Mélisse */) });
        var result = import.Parse(x.ToText("B", Cogs(d, cabal), Equipment(d, cabal)), "MAISEL");
        Assert.Empty(result.Issues);
        Assert.True(CabalLibrary.SameCards(cabal, result.Cabal!));
    }

    [Fact]
    public async Task Text_valid_in_several_factions_to_be_chosen()
    {
        var (_, _, import) = await Services();
        const string text = "JANA ALENA NOVAK - CATALYSEUR QUANTIQUE\nKUBIK OKTOBAR - CARTE DU SYNDICAT TETRA\nBOHDAN GROSS - MANUEL DE L'HARMONIE";

        var open = import.Parse(text, faction: null);
        Assert.Contains("DYKOVIAN", open.Factions);
        Assert.Contains("MAISEL", open.Factions);
        Assert.Equal(open.Factions[0], open.Cabal!.Faction?.Code);

        var chosen = import.Parse(text, faction: "MAISEL");
        Assert.Equal(new[] { "MAISEL" }, chosen.Factions);
        Assert.Equal("MAISEL", chosen.Cabal!.Faction?.Code);
    }

    [Fact]
    public async Task Unknown_lines_and_duplicate_rank_reported()
    {
        var (_, _, import) = await Services();
        var result = import.Parse("Ma liste\nDOMINIK KRALOV - HARMONIUM\nGANDALF - BATON\nBOHDAN GROSS", "MAISEL");

        Assert.Equal(new[] { ("unknownLine", "3"), ("sameRank", "4") }, result.Issues.Select(i => (i.Key, i.Args[0])));
        Assert.Equal("3R05", Assert.Single(result.Cabal!.Cogs).Cog.Id);      // Dominik Kralov, rank 1; Bohdan Gross too
    }

    [Fact]
    public async Task JSON_accepted()
    {
        var (d, x, import) = await Services();
        var cabal = Maisel(d, x);
        var json = x.ToJson(cabal.Name, "MAISEL", Cogs(d, cabal), Equipment(d, cabal));
        var result = import.Parse("\n  " + json, faction: "RUBIN");      // the JSON's faction prevails
        Assert.Empty(result.Issues);
        Assert.Equal("MAISEL", result.Cabal!.Faction?.Code);
        Assert.Equal("json", Assert.Single(import.Parse("{ cassé", null).Issues).Key);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   \n ", "empty")]
    [InlineData("rien de connu\nnon plus", "noCog")]
    public async Task Nothing_to_read(string text, string key)
    {
        var (_, _, import) = await Services();
        var result = import.Parse(text, null);
        Assert.Null(result.Cabal);
        Assert.Equal(key, result.Issues[0].Key);
    }

    private static Cog?[] Cogs(AppData d, CabalExport c) =>
        Enumerable.Range(1, 3).Select(r => c.Cogs.FirstOrDefault(s => s.Rank == r) is { } s ? (Cog?)d.Cog(s.Cog.Id) : null).ToArray();

    private static Equipment?[] Equipment(AppData d, CabalExport c) =>
        Enumerable.Range(1, 3).Select(r => c.Cogs.FirstOrDefault(s => s.Rank == r)?.Equipment is { } e ? d.Catalog.EquipmentById(e.Id) : null).ToArray();
}
