using System.Text.Json;
using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Text export (format of the bwaaaanegamesdb.fr builder) and JSON.</summary>
public class CabalExportTests
{
    // Composition taken from the reference builder (2026-10-10) and its text export, identical.
    // Slots in the order rank 1, rank 2, rank 3 (export order).
    private static async Task<(CabalExporter X, Cog?[] Cogs, Equipment?[] Equipment)> Reference()
    {
        var d = await AppData.GetAsync();
        return (new CabalExporter(d.L),
            new Cog?[] { d.Cog("3R05"), d.Cog("2R04"), d.Cog("1R01") },
            new Equipment?[] { d.Equipment("E11" /* Harmonium */), d.Equipment("E12" /* Chronographe */), d.Equipment("E13" /* Asservisseur mental */) });
    }

    [Fact]
    public async Task Text_identical_to_the_reference_builder()
    {
        var (x, cogs, equipment) = await Reference();
        Assert.Equal(
            "Ma Cabale Test\r\nDOMINIK KRALOV - HARMONIUM\r\nHYNEK NOVOTNY - CHRONOGRAPHE\r\nPETRA KANTOR - ASSERVISSEUR MENTAL",
            x.ToText("Ma Cabale Test", cogs, equipment));
    }

    [Fact]
    public async Task Json_ids_names_faction_and_ranks()
    {
        var (x, cogs, equipment) = await Reference();
        using var doc = JsonDocument.Parse(x.ToJson("Ma Cabale Test", "MAISEL", cogs, equipment));
        var root = doc.RootElement;

        Assert.Equal("olomoc-cabal", root.GetProperty("format").GetString());
        Assert.Equal("MAISEL", root.GetProperty("faction").GetProperty("code").GetString());
        var slots = root.GetProperty("cogs").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 2, 3 }, slots.Select(s => s.GetProperty("rank").GetInt32()));
        Assert.Equal(new[] { "3R05", "2R04", "1R01" }, slots.Select(s => s.GetProperty("cog").GetProperty("id").GetString()));
        Assert.Equal("E11", slots[0].GetProperty("equipment").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("« Brûlons Tous » !", "json", "brulons-tous.json")]
    [InlineData(null, "txt", "cabale.txt")]
    public void File_name(string? name, string extension, string expected) =>
        Assert.Equal(expected, CabalExporter.FileName(name, extension));
}
