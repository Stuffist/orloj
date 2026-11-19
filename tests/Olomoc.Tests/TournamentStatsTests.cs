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
    public void Totals_and_wins_per_faction()
    {
        var s = TournamentStats.Compute(new[] { Sample });
        Assert.Equal((1, 4, 3), (s.Tournaments, s.Players, s.Games));
        var wins = s.FactionWins.ToDictionary(w => w.Key);
        Assert.Equal((4, 2, 2, 0), (wins["MAISEL"].Games, wins["MAISEL"].Wins, wins["MAISEL"].Draws, wins["MAISEL"].Losses));
        Assert.Equal((2, 0, 2), (wins["RUBIN"].Games, wins["RUBIN"].Wins, wins["RUBIN"].Losses));
    }

    [Fact]
    public void Wins_per_rank_3_cog_of_the_cabal_played()
    {
        var wins = TournamentStats.Compute(new[] { Sample }).Rank3Wins.ToDictionary(w => w.Key);
        Assert.Equal((3, 2, 1), (wins["A3"].Games, wins["A3"].Wins, wins["A3"].Draws));
        Assert.False(wins.ContainsKey("S3"));                            // unknown cabal played: not counted
    }

    [Fact]
    public async Task Anonymous_archive_of_a_tournament()
    {
        var d = await AppData.GetAsync();
        var x = new Olomoc.Services.CabalExporter(d.L);
        var cabal = x.ToModel("Secrète", "MAISEL",
            new Cog?[] { d.Cog("3R06"), d.Cog("2R08"), d.Cog("1R01") },
            new Equipment?[] { d.Equipment("E03" /* Phéromones */), d.Equipment("D01" /* Carapace */), d.Equipment("E11" /* Harmonium */) });
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
                new Round { Number = 1, Tables = { new Table { Number = 1, PlayerA = "id-ana", PlayerB = "id-bo", VpA = 9, VpB = 4, CabalA = 0, CabalB = 0 }, new Table { Number = 2, PlayerA = "id-cy" } } },
                new Round { Number = 2, Tables = { new Table { Number = 1, PlayerA = "id-cy", PlayerB = "id-ana" } } },   // without a result
            },
            Scenarios = { new Scenario(Agendas.Other, "Bal masqué chez Ana") },
        };

        var archive = ArchivedTournament.From(t, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(archive, TournamentJson.Options);
        foreach (var secret in new[] { "Ana Lys", "Bo\"", "HASH-", "Secrète", "id-", "Bal masqué" })
            Assert.DoesNotContain(secret, json);
        var game = Assert.Single(archive.Games);                         // bye and game without a result excluded
        Assert.Equal((0, 1, 9, 4), (game.A, game.B, game.VpA, game.VpB));
    }
}
