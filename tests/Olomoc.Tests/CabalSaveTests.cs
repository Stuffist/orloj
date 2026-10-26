using Microsoft.JSInterop;
using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Reading back JSON exports and cabals saved on the device.</summary>
public class CabalSaveTests
{
    private static async Task<(AppData D, CabalExport Cabal)> Export(string? name = "Ma Cabale")
    {
        var d = await AppData.CreateAsync();
        Cog?[] cogs = { d.Cog("3R05"), d.Cog("2R04"), d.Cog("1R01") };
        Equipment?[] equipment = { d.Equipment("E11" /* Harmonium */), null, d.Equipment("E13" /* Asservisseur mental */) };
        return (d, new CabalExporter(d.L).ToModel(name, "MAISEL", cogs, equipment));
    }

    [Fact]
    public async Task Export_read_back_identically()
    {
        var (d, cabal) = await Export();
        var json = new CabalExporter(d.L).ToJson(cabal.Name, "MAISEL",
            new Cog?[] { d.Cog("3R05"), d.Cog("2R04"), d.Cog("1R01") },
            new Equipment?[] { d.Equipment("E11" /* Harmonium */), null, d.Equipment("E13" /* Asservisseur mental */) });

        var read = CabalExporter.FromJson(json);
        Assert.NotNull(read);
        Assert.Equal("Ma Cabale", read.Name);
        Assert.True(CabalLibrary.SameCards(cabal, read));

        var resolved = d.Catalog.Resolve(read);
        Assert.Equal("MAISEL", resolved.Faction);
        Assert.Equal(new[] { "3R05", "2R04", "1R01" }, resolved.Cogs.Select(c => c?.Id));
        Assert.Equal(new[] { "E11", null, "E13" }, resolved.Equipment.Select(e => e?.Id));
        Assert.Empty(resolved.Missing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pas du JSON")]
    [InlineData("{\"format\":\"autre\",\"version\":1,\"language\":\"fr\",\"cogs\":[]}")]
    [InlineData("{\"format\":\"olomoc-cabal\",\"version\":2,\"language\":\"fr\",\"cogs\":[]}")]
    [InlineData("{\"format\":\"olomoc-cabal\",\"version\":1,\"language\":\"fr\"}")]
    [InlineData("{\"format\":\"olomoc-cabal\",\"version\":1,\"language\":\"fr\",\"cogs\":[{\"rank\":1}]}")]
    public void File_that_is_not_an_export_refused(string? json) => Assert.Null(CabalExporter.FromJson(json));

    [Fact]
    public async Task Cards_not_found_set_aside_under_their_recorded_name()
    {
        var (d, cabal) = await Export();
        var slots = cabal.Cogs.ToList();
        slots[0] = slots[0] with { Cog = new IdName("XXX", "Rouage disparu"), Equipment = new IdName("E11", "Harmonium") };
        slots[2] = slots[2] with { Equipment = new IdName("E999", "Carte disparue") };
        var resolved = d.Catalog.Resolve(cabal with { Faction = new CodeName("NOPE", "Faction disparue"), Cogs = slots });

        Assert.Null(resolved.Faction);
        Assert.Null(resolved.Cogs[0]);
        Assert.Null(resolved.Equipment[0]);                     // card of the missing cog set aside with it
        Assert.Equal("1R01", resolved.Cogs[2]?.Id);
        Assert.Null(resolved.Equipment[2]);
        Assert.Equal(new[] { "Faction disparue", "Rouage disparu", "Harmonium", "Carte disparue" }, resolved.Missing);
    }

    [Fact]
    public async Task Cog_placed_by_its_rank_and_rank_already_taken_set_aside()
    {
        var (d, cabal) = await Export();
        // Wrong recorded ranks and two rank 2 cogs: the catalog's rank prevails.
        var slots = new List<CabalSlot>
        {
            new(3, new IdName("2R04", "Hynek Novotny"), null),
            new(1, new IdName("2R28", "Opérateur Sikora"), null),
        };
        var resolved = d.Catalog.Resolve(cabal with { Cogs = slots });
        Assert.Equal(new[] { null, "2R04", null }, resolved.Cogs.Select(c => c?.Id));
        Assert.Equal(new[] { "Opérateur Sikora" }, resolved.Missing);
    }

    [Fact]
    public async Task Save_replaces_the_same_name_and_survives_a_reload()
    {
        var (_, cabal) = await Export();
        var storage = new FakeStorage();
        var library = new CabalLibrary(storage);
        await library.EnsureLoadedAsync();
        Assert.Empty(library.Cabals);

        var t0 = new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
        await library.SaveAsync(cabal, t0);
        await library.SaveAsync(cabal with { Name = "Autre" }, t0.AddMinutes(1));
        var changed = cabal with { Name = "  ma cabale ", Cogs = cabal.Cogs.Take(2).ToList() };
        await library.SaveAsync(changed, t0.AddMinutes(2));

        Assert.Equal(new[] { "  ma cabale ", "Autre" }, library.Cabals.Select(c => c.Cabal.Name));
        Assert.Same(changed, library.Find("MA CABALE")?.Cabal);
        Assert.False(CabalLibrary.SameCards(cabal, changed));

        var reloaded = new CabalLibrary(storage);
        await reloaded.EnsureLoadedAsync();
        Assert.Equal(new[] { "  ma cabale ", "Autre" }, reloaded.Cabals.Select(c => c.Cabal.Name));
        Assert.True(CabalLibrary.SameCards(changed, reloaded.Cabals[0].Cabal));
        Assert.Equal(t0.AddMinutes(2), reloaded.Cabals[0].SavedAt);

        await reloaded.DeleteAsync(reloaded.Cabals[0]);
        var again = new CabalLibrary(storage);
        await again.EnsureLoadedAsync();
        Assert.Equal(new[] { "Autre" }, again.Cabals.Select(c => c.Cabal.Name));
    }

    [Fact]
    public async Task Save_without_a_name_refused()
    {
        var (_, cabal) = await Export(name: null);
        var library = new CabalLibrary(new FakeStorage());
        await Assert.ThrowsAsync<ArgumentException>(() => library.SaveAsync(cabal, DateTimeOffset.Now));
    }

    [Fact]
    public async Task Unreadable_storage_ignored()
    {
        var storage = new FakeStorage();
        storage.Items["olomoc.cabals"] = "{ cassé";
        var library = new CabalLibrary(storage);
        await library.EnsureLoadedAsync();
        Assert.Empty(library.Cabals);
    }

    /// <summary>In-memory localStorage.</summary>
    private sealed class FakeStorage : IJSRuntime
    {
        public Dictionary<string, string> Items { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            var key = (string)args![0]!;
            object? result = null;
            switch (identifier)
            {
                case "localStorage.getItem": result = Items.GetValueOrDefault(key); break;
                case "localStorage.setItem": Items[key] = (string)args[1]!; break;
                case "localStorage.removeItem": Items.Remove(key); break;
                default: throw new NotSupportedException(identifier);
            }
            return ValueTask.FromResult((TValue)result!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
