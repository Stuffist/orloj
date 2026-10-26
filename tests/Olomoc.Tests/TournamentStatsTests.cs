using System.Text.Json;
using Olomoc.Models;
using Olomoc.Tournaments;
using Olomoc.Tournaments.Stats;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Statistics of the archived tournaments, and anonymous archive of a tournament.</summary>
public class TournamentStatsTests
{
    private static IReadOnlyList<ArchivedSlot> Cabal(string r3, string r2, string r1) =>
        new[] { new ArchivedSlot(1, r1, null), new ArchivedSlot(2, r2, null), new ArchivedSlot(3, r3, "E1") };

    // Maisel: p0 (two cabals, rank 3 A3 then B3), p1 (one cabal, A3); Rubin: p2, p3.
    private static readonly ArchivedTournament Sample = new(1, "AAAAAA", new DateOnly(2026, 10, 1), DateTimeOffset.UtcNow, 2,
        new[]
        {
            new ArchivedPlayer(0, "MAISEL", new[] { Cabal("A3", "A2", "A1"), Cabal("B3", "A2", "B1") }),
            new ArchivedPlayer(1, "MAISEL", new[] { Cabal("A3", "C2", "C1") }),
            new ArchivedPlayer(2, "RUBIN", new[] { Cabal("R3", "R2", "R1") }),
            new ArchivedPlayer(3, "RUBIN", new[] { Cabal("S3", "R2", "S1") }),
        },
        new[]
        {
            new ArchivedGame(0, 2, 0, 0, 10, 5),         // Maisel (A3) beats Rubin (R3)
            new ArchivedGame(1, 0, 0, 1, 6, 6),          // Maisel v. Maisel, draw: A3 and B3
            new ArchivedGame(3, 1, null, 0, 2, 8),       // Rubin (unknown cabal) loses to Maisel (A3)
        });

    [Fact]
    public void Totals_and_factions_played()
    {
        var s = TournamentStats.Compute(new[] { Sample });
        Assert.Equal((1, 4, 3), (s.Tournaments, s.Players, s.Games));
        Assert.Equal(new[] { ("MAISEL", 50.0), ("RUBIN", 50.0) }, s.Factions.Select(f => (f.Key, f.Percent)));
    }

    [Fact]
    public void Cog_choices_per_faction_on_the_registered_cabals()
    {
        var maisel = TournamentStats.Compute(new[] { Sample }).Picks.Single(p => p.Faction == "MAISEL");
        Assert.Equal(3, maisel.Cabals);                                  // both of p0's cabals count
        var share = maisel.Cogs.ToDictionary(c => c.Key, c => c.Count);
        Assert.Equal(2, share["A3"]);
        Assert.Equal(2, share["A2"]);
        Assert.Equal(1, share["B3"]);
        Assert.Equal(66.7, Math.Round(maisel.Cogs.Single(c => c.Key == "A3").Percent, 1));
        Assert.DoesNotContain(maisel.Cogs, c => c.Key == "R3");
    }

    [Fact]
    public void Wins_per_faction_each_side_counts()
    {
        var wins = TournamentStats.Compute(new[] { Sample }).FactionWins.ToDictionary(w => w.Key);
        Assert.Equal((4, 2, 2, 0), (wins["MAISEL"].Games, wins["MAISEL"].Wins, wins["MAISEL"].Draws, wins["MAISEL"].Losses));
        Assert.Equal(50.0, wins["MAISEL"].Percent);
        Assert.Equal((2, 0, 2), (wins["RUBIN"].Games, wins["RUBIN"].Wins, wins["RUBIN"].Losses));
    }

    [Fact]
    public void Wins_per_rank_3_cog_of_the_cabal_played()
    {
        var wins = TournamentStats.Compute(new[] { Sample }).Rank3Wins.ToDictionary(w => w.Key);
        Assert.Equal((3, 2, 1), (wins["A3"].Games, wins["A3"].Wins, wins["A3"].Draws));
        Assert.Equal((1, 0, 1), (wins["B3"].Games, wins["B3"].Wins, wins["B3"].Draws));
        Assert.Equal((1, 0, 1), (wins["R3"].Games, wins["R3"].Wins, wins["R3"].Losses));
        Assert.False(wins.ContainsKey("S3"));                            // unknown cabal played: not counted
        Assert.Equal("A3", TournamentStats.Compute(new[] { Sample }).Rank3Wins[0].Key);   // sorted by rate
    }

    [Fact]
    public void No_data()
    {
        var s = StatsView.Empty;
        Assert.Equal((0, 0, 0), (s.Tournaments, s.Players, s.Games));
        Assert.Empty(s.Factions);
        Assert.Equal(0, s.FirstPlayer.Games);
        Assert.Empty(s.Ends);
        Assert.Empty(s.Scenarios);
        Assert.Empty(s.Artifacts);
        Assert.Empty(s.Ministries);
    }

    [Fact]
    public void P1_game_endings_and_scenarios_when_recorded()
    {
        var noted = Sample with
        {
            Games = new[]
            {
                new ArchivedGame(0, 2, 0, 0, 10, 5, TableSide.A, GameEnd.Bile, "SABOTAGE", "PARASITE", "DISINFORMATION"),  // P1 (A) wins
                new ArchivedGame(1, 0, 0, 1, 6, 6, TableSide.B, GameEnd.FourthCycle, "SABOTAGE", "ROGUE"),  // draw, Ministry not recorded
                new ArchivedGame(3, 1, null, 0, 2, 8, TableSide.A, GameEnd.FourthCycle, "VISITS", "PARASITE", "DISINFORMATION"),  // P1 (A) loses
                new ArchivedGame(2, 3, 0, 0, 4, 1, null, null, Agendas.Other),                        // nothing recorded, custom scenario
                new ArchivedGame(2, 3, 0, 0, 4, 1),                                                   // archive v1: nothing
            },
        };
        var s = TournamentStats.Compute(new[] { noted });

        Assert.Equal((3, 1, 1, 1), (s.FirstPlayer.Games, s.FirstPlayer.Wins, s.FirstPlayer.Draws, s.FirstPlayer.Losses));
        Assert.Equal(new[] { ("FourthCycle", 2, 3), ("Bile", 1, 3) }, s.Ends.Select(e => (e.Key, e.Count, e.Total)));

        Assert.Equal(new[] { ("SABOTAGE", 2), ("VISITS", 1), (Agendas.Other, 1) }, s.Scenarios.Select(x => (x.Key, x.Games)));
        Assert.Equal(4, s.Scenarios[0].Total);
        var sabotage = s.Scenarios[0].First;
        Assert.Equal((2, 1, 1), (sabotage.Games, sabotage.Wins, sabotage.Draws));
        Assert.Equal(0, s.Scenarios.Single(x => x.Key == Agendas.Other).First.Games);

        // Decks: only the games where they are recorded (archives v3).
        Assert.Equal(new[] { ("PARASITE", 2, 3), ("ROGUE", 1, 3) }, s.Artifacts.Select(x => (x.Key, x.Games, x.Total)));
        Assert.Equal(new[] { ("DISINFORMATION", 2, 2) }, s.Ministries.Select(x => (x.Key, x.Games, x.Total)));
        Assert.Equal((2, 1, 0), (s.Ministries[0].First.Games, s.Ministries[0].First.Wins, s.Ministries[0].First.Draws));
    }

    [Fact]
    public async Task Anonymous_archive_of_a_tournament()
    {
        var d = await AppData.GetAsync();
        var x = new Olomoc.Services.CabalExporter(d.L);
        CabalExport Cabal(params (string Cog, string Equipment)[] slots) =>
            x.ToModel("Secrète", "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(), slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        var cabal = Cabal(("3R06", "E03" /* Phéromones */), ("2R08", "D01" /* Carapace */), ("1R01", "E11" /* Harmonium */));
        var t = new Tournament
        {
            Code = "BBBBBB", Name = "Open", CabalsPerPlayer = 1, Phase = TournamentPhase.Finished,
            OrganizerTokenHash = "HASH-ORGA",
            Players =
            {
                new Player { Id = "id-ana", Name = "Ana Lys", Faction = "MAISEL", TokenHash = "HASH-ANA", Cabals = { cabal } },
                new Player { Id = "id-bo", Name = "Bo", Faction = "MAISEL", TokenHash = "HASH-BO", Cabals = { cabal } },
                new Player { Id = "id-cy", Name = "Cy", Faction = "MAISEL", TokenHash = "HASH-CY", Cabals = { cabal } },
            },
            Rounds =
            {
                new Round { Number = 1, Tables = { new Table { Number = 1, PlayerA = "id-ana", PlayerB = "id-bo", VpA = 9, VpB = 4, CabalA = 0, CabalB = 0, First = TableSide.B, End = GameEnd.PoliSec }, new Table { Number = 2, PlayerA = "id-cy" } } },
                new Round { Number = 2, Tables = { new Table { Number = 1, PlayerA = "id-cy", PlayerB = "id-ana" } } },   // without a result
            },
            Scenarios = { new Scenario(Agendas.Other, "Bal masqué chez Ana", "ROGUE", "INDOCTRINATION"), new Scenario("SABOTAGE") },
        };

        var archive = ArchivedTournament.From(t, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(archive, TournamentJson.Options);
        foreach (var secret in new[] { "Ana Lys", "Bo\"", "HASH-", "Secrète", "id-", "Bal masqué" })
            Assert.DoesNotContain(secret, json);
        Assert.Equal(new[] { 0, 1, 2 }, archive.Players.Select(p => p.Number));
        var game = Assert.Single(archive.Games);                         // bye and game without a result excluded
        Assert.Equal((0, 1, 9, 4), (game.A, game.B, game.VpA, game.VpB));
        Assert.Equal((TableSide.B, GameEnd.PoliSec, Agendas.Other), (game.First, game.End, game.Scenario));  // custom scenario: without its name
        Assert.Equal(("ROGUE", "INDOCTRINATION"), (game.Artifact, game.Ministry));
        Assert.Equal("3R06", archive.Players[0].Cabals[0].Single(s => s.Rank == 1).Cog);
    }
}
