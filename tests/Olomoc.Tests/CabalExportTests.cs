using System.Text.Json;
using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Text export (format of the bwaaaanegamesdb.fr builder) and JSON.</summary>
public class CabalExportTests
{
    // Slots in the order rank 1, rank 2, rank 3 (export order).
    private static async Task<(AppData D, CabalExporter X, Cog?[] Cogs, Equipment?[] Equipment)> Cabal(
        params (string Cog, string? Equipment)[] slots)
    {
        var d = await AppData.CreateAsync();
        return (d, new CabalExporter(d.L),
            slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(),
            slots.Select(s => s.Equipment is null ? null : (Equipment?)d.Equipment(s.Equipment)).ToArray());
    }

    // Composition taken from the reference builder (2026-10-10) and its text export, identical.
    private static readonly (string, string?)[] Reference =
        { ("3R05", "E11" /* Harmonium */), ("2R04", "E12" /* Chronographe */), ("1R01", "E13" /* Asservisseur mental */) };

    [Fact]
    public async Task Text_identical_to_the_reference_builder()
    {
        var (_, x, cogs, equipment) = await Cabal(Reference);
        Assert.Equal(
            "Ma Cabale Test\r\nDOMINIK KRALOV - HARMONIUM\r\nHYNEK NOVOTNY - CHRONOGRAPHE\r\nPETRA KANTOR - ASSERVISSEUR MENTAL",
            x.ToText("Ma Cabale Test", cogs, equipment));
    }

    [Fact]
    public async Task Text_without_a_name_omits_the_first_line()
    {
        var (_, x, cogs, equipment) = await Cabal(Reference);
        Assert.Equal(
            "DOMINIK KRALOV - HARMONIUM\r\nHYNEK NOVOTNY - CHRONOGRAPHE\r\nPETRA KANTOR - ASSERVISSEUR MENTAL",
            x.ToText("   ", cogs, equipment));
    }

    [Fact]
    public async Task Text_with_an_unequipped_cog_and_an_empty_slot()
    {
        var (_, x, cogs, equipment) = await Cabal(("3R05", null), ("2R04", "E12" /* Chronographe */), ("1R01", null));
        cogs[1] = null;   // rank 2 not recruited
        Assert.Equal("X\r\nDOMINIK KRALOV\r\nPETRA KANTOR", x.ToText("X", cogs, equipment));
    }

    [Fact]
    public async Task Text_in_uppercase_without_accents()
    {
        var (_, x, cogs, equipment) = await Cabal(("3R27", "E45" /* Thymorégulateur */), ("2R28", "E43" /* Fumigènes */), ("1R29", "E08" /* Lumen */));
        Assert.Equal("MEDIC RUZICKA - THYMOREGULATEUR\r\nOPERATEUR SIKORA - FUMIGENES\r\nCAPORAL VLACH - LUMEN",
            x.ToText(null, cogs, equipment));
    }

    [Fact]
    public async Task Json_ids_names_faction_and_ranks()
    {
        var (_, x, cogs, equipment) = await Cabal(Reference);
        var json = x.ToJson("Ma Cabale Test", "MAISEL", cogs, equipment);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("olomoc-cabal", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("fr", root.GetProperty("language").GetString());
        Assert.Equal("Ma Cabale Test", root.GetProperty("name").GetString());
        Assert.Equal("MAISEL", root.GetProperty("faction").GetProperty("code").GetString());
        Assert.Equal("Ligue de Maisel", root.GetProperty("faction").GetProperty("name").GetString());

        var slots = root.GetProperty("cogs").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 2, 3 }, slots.Select(s => s.GetProperty("rank").GetInt32()));
        Assert.Equal(new[] { "3R05", "2R04", "1R01" }, slots.Select(s => s.GetProperty("cog").GetProperty("id").GetString()));
        Assert.Equal("Dominik Kralov", slots[0].GetProperty("cog").GetProperty("name").GetString());
        Assert.Equal("E11", slots[0].GetProperty("equipment").GetProperty("id").GetString());
        Assert.Equal("Harmonium", slots[0].GetProperty("equipment").GetProperty("name").GetString());
        Assert.Contains("Asservisseur mental", json);   // readable accents and characters, not escaped
    }

    [Fact]
    public async Task Json_optional_fields_omitted()
    {
        var (_, x, cogs, equipment) = await Cabal(("3R05", null));
        using var doc = JsonDocument.Parse(x.ToJson("", null, cogs, equipment));
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("name", out _));
        Assert.False(root.TryGetProperty("faction", out _));
        var slot = Assert.Single(root.GetProperty("cogs").EnumerateArray());
        Assert.False(slot.TryGetProperty("equipment", out _));
    }

    [Fact]
    public async Task Json_in_the_current_language()
    {
        var (d, x, cogs, equipment) = await Cabal(Reference);
        await d.L.SetLanguageAsync("en");
        using var doc = JsonDocument.Parse(x.ToJson(null, "MAISEL", cogs, equipment));
        Assert.Equal("en", doc.RootElement.GetProperty("language").GetString());
        // Untranslated cards: reference name; ids unchanged.
        Assert.Equal("Dominik Kralov", doc.RootElement.GetProperty("cogs")[0].GetProperty("cog").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("Ma Cabale Test", "txt", "ma-cabale-test.txt")]
    [InlineData("« Brûlons Tous » !", "json", "brulons-tous.json")]
    [InlineData("", "txt", "cabale.txt")]
    [InlineData(null, "json", "cabale.json")]
    public void File_name(string? name, string extension, string expected) =>
        Assert.Equal(expected, CabalExporter.FileName(name, extension));

    [Fact]
    public async Task Cabal_of_a_local_game_to_open_it_online()
    {
        var (d, x, cogs, equipment) = await Cabal(("1R01", "E11" /* Harmonium */), ("2R08", "D01" /* Carapace */), ("3R06", "E03" /* Phéromones */));
        var game = new GameSession(GameSession.NewGame("Ma cabale", "MAISEL", cogs, equipment));
        game.Wound(0, 3, attack: false, ignoreProtection: false);          // the game's state does not matter

        var cabal = x.FromGame(game.State, d.Catalog);
        Assert.Equal("Ma cabale", cabal.Name);
        Assert.Equal("MAISEL", cabal.Faction!.Code);
        Assert.Equal(new[] { 1, 2, 3 }, cabal.Cogs.Select(c => c.Rank));
        Assert.True(CabalLibrary.SameCards(x.ToModel("Ma cabale", "MAISEL", cogs, equipment), cabal));
        Assert.Empty(d.Catalog.CheckLegality("MAISEL", d.Catalog.Resolve(cabal).Cogs, d.Catalog.Resolve(cabal).Equipment).Issues);
    }
}
