using System.Text.Json;
using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>
/// Online games for the rules tests (real card catalog): Ana (seat 0) and Bo (seat 1), two Maisel
/// cabals; commands applied with a fixed random and the table's manifest.
/// </summary>
internal static class OnlineTestGame
{
    public static readonly DateTimeOffset Now = new(2026, 11, 25, 20, 0, 0, TimeSpan.Zero);

    public static readonly Lazy<TableManifest> Manifest = new(() =>
        JsonSerializer.Deserialize<TableManifest>(File.ReadAllText(Path.Combine(AppData.WwwRoot, "data", "olomoc-table.json")), TableAssets.Json)!);

    /// <summary>A: Phéromones, Carapace, Harmonium; B: Phéromones, Ration, Harmonium.</summary>
    public static async Task<(AppData D, CabalExport A, CabalExport B)> Cabals()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(), slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        return (d, Cabal("A", ("3R06", "E03"), ("2R08", "D01"), ("1R01", "E11")), Cabal("B", ("3R06", "E03"), ("2R11", "E07"), ("1R01", "E11")));
    }

    public static OnlineGame NewGame() => new() { Code = "ABCDEF", Seats = { new Seat(), new Seat() } };

    /// <summary>Both players named, cabals chosen (Ana's: <paramref name="a"/> if given) and ready: the game is at the initiative.</summary>
    public static async Task<(AppData D, OnlineGame G)> Started(CabalExport? a = null)
    {
        var (d, cabalA, cabalB) = await Cabals();
        var g = NewGame();
        Ok(Do(g, 0, new(OnlineCommandType.SetName, "Ana"), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetName, "Bo"), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: a ?? cabalA), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: cabalB), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetReady, Flag: true), d));
        return (d, g);
    }

    /// <summary>Started, P1 given (Bo by default), agenda chosen: first cycle.</summary>
    public static async Task<(AppData D, OnlineGame G)> InCycle(string agenda = "VISITS", int first = 1, CabalExport? a = null)
    {
        var (d, g) = await Started(a);
        g.Step = OnlineStep.Agenda;
        g.First = first;
        Ok(Do(g, first, new(OnlineCommandType.ChooseAgenda, agenda), d));
        return (d, g);
    }

    public static OnlineResult Do(OnlineGame g, int seat, OnlineCommand c, AppData d, int seed = 1) =>
        OnlineRules.Apply(g, seat, c, d.Catalog, new Random(seed), Now, Manifest.Value);

    public static void Ok(OnlineResult r) => Assert.True(r.Ok, string.Join(", ", r.Problems.Select(p => p.Key)));

    /// <summary>The single problem of a refused command.</summary>
    public static string Key(OnlineResult r) => Assert.Single(r.Problems).Key;
}
