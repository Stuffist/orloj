using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Cabale collée : texte au format de l'export (builder de référence) ou JSON.</summary>
public class CabalImportTests
{
    private static async Task<(AppData D, CabalExporter X, CabalImport I)> Services()
    {
        var d = await AppData.CreateAsync();
        var x = new CabalExporter(d.L);
        return (d, x, new CabalImport(d.Catalog, d.L, x));
    }

    // Composition légale en Ligue de Maisel (rang 1, 2, 3).
    private static CabalExport Maisel(AppData d, CabalExporter x, string? name = "Assaut") => x.ToModel(name, "MAISEL",
        new Cog?[] { d.Cog("1R01"), d.Cog("2R08"), d.Cog("3R06") },
        new Equipment?[] { d.Equipment("E11" /* Harmonium */), d.Equipment("D01" /* Carapace */), d.Equipment("E03" /* Phéromones */) });

    [Fact]
    public async Task Export_texte_relu_a_l_identique()
    {
        var (d, x, import) = await Services();
        var cabal = Maisel(d, x);
        var text = x.ToText(cabal.Name, Cogs(d, cabal), Equipment(d, cabal));

        var result = import.Parse(text, faction: null);
        Assert.Empty(result.Issues);
        Assert.Equal("Assaut", result.Cabal!.Name);
        Assert.Equal("MAISEL", result.Cabal.Faction?.Code);          // déduite : légale seulement en Maisel
        Assert.True(CabalLibrary.SameCards(cabal, result.Cabal));
    }

    [Fact]
    public async Task Saisie_a_la_main_tolerante()
    {
        var (d, x, import) = await Services();
        // Minuscules, sans accents, tiret long ou collé, espaces en trop, sans nom, ordre quelconque, CRLF.
        var text = "  phéromones est absent ici\r\nPetra Kantor –  harmonium\r\n\r\nle chasseur-carapace\r\nBOHDAN   GROSS - PHEROMONES ";
        var result = import.Parse(text, faction: "MAISEL");

        Assert.Equal("phéromones est absent ici", result.Cabal!.Name);   // première ligne non reconnue : le nom
        Assert.Empty(result.Issues);
        Assert.True(CabalLibrary.SameCards(Maisel(d, x), result.Cabal));
    }

    [Fact]
    public async Task Noms_anglais_et_trait_d_union_dans_un_nom()
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
    public async Task Texte_valable_dans_plusieurs_factions_a_faire_choisir()
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
    public async Task Lignes_inconnues_et_rang_en_double_signales()
    {
        var (_, _, import) = await Services();
        var result = import.Parse("Ma liste\nDOMINIK KRALOV - HARMONIUM\nGANDALF - BATON\nBOHDAN GROSS", "MAISEL");

        Assert.Equal(new[] { ("unknownLine", "3"), ("sameRank", "4") }, result.Issues.Select(i => (i.Key, i.Args[0])));
        Assert.Equal("3R05", Assert.Single(result.Cabal!.Cogs).Cog.Id);      // Dominik Kralov, rang 1 ; Bohdan Gross aussi
    }

    [Fact]
    public async Task JSON_accepte()
    {
        var (d, x, import) = await Services();
        var cabal = Maisel(d, x);
        var json = x.ToJson(cabal.Name, "MAISEL", Cogs(d, cabal), Equipment(d, cabal));
        var result = import.Parse("\n  " + json, faction: "RUBIN");      // la faction du JSON fait foi
        Assert.Empty(result.Issues);
        Assert.Equal("MAISEL", result.Cabal!.Faction?.Code);
        Assert.Equal("json", Assert.Single(import.Parse("{ cassé", null).Issues).Key);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   \n ", "empty")]
    [InlineData("rien de connu\nnon plus", "noCog")]
    public async Task Rien_a_lire(string text, string key)
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
