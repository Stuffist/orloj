using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Tournament: standings, pairings (Swiss, no rematch nor mirror), rules of the phases and registrations.</summary>
public class TournamentTests
{
    // Players of all different factions: no mirror match possible (the card catalog is not needed here).
    private static Tournament NewTournament(int players) =>
        WithFactions(Enumerable.Range(0, players).Select(i => $"F{i}").ToArray());

    private static Tournament WithFactions(params string[] factions)
    {
        // One cabal per player: the cabal played is implicit.
        var t = new Tournament { Code = "TEST", Name = "Test", RoundCount = 10, CabalsPerPlayer = 1, Phase = TournamentPhase.Started };
        for (var i = 0; i < factions.Length; i++)
            t.Players.Add(new Player { Id = $"p{i}", Name = $"Joueur {i}", Faction = factions[i] });
        return t;
    }

    private static Round Pair(Tournament t, int seed = 1) => TournamentRules.PairNext(t, seed).Round;

    private static bool IsMirror(Tournament t, Table x) => t.PlayerById(x.PlayerA)!.Faction == t.PlayerById(x.PlayerB!)!.Faction;

    // Results: player A of each table wins if win(A, B), otherwise loses; VP 10 to 4.
    private static void Play(Round round, Func<string, string, bool> aWins)
    {
        foreach (var table in round.Tables.Where(x => !x.IsBye))
        {
            var a = aWins(table.PlayerA, table.PlayerB!);
            table.VpA = a ? 10 : 4;
            table.VpB = a ? 4 : 10;
        }
    }

    private static int Num(string id) => int.Parse(id[1..]);

    private static IEnumerable<(string, string)> Pairs(Tournament t) =>
        t.Rounds.SelectMany(r => r.Tables).Where(x => !x.IsBye).Select(x => (x.PlayerA, x.PlayerB!));

    private static void AssertNoRematch(Tournament t)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var (a, b) in Pairs(t))
            Assert.True(seen.Add(string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a)), $"revanche {a} c. {b}");
    }

    private static void AssertEveryoneOnce(Tournament t, Round round)
    {
        var ids = round.Tables.SelectMany(x => new[] { x.PlayerA, x.PlayerB }).OfType<string>().ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(t.ActivePlayers.Select(p => p.Id).OrderBy(x => x), ids.OrderBy(x => x));
    }

    // ---- Standings ----

    [Fact]
    public void Standings_points_then_difference_then_VP()
    {
        var t = NewTournament(5);
        t.Rounds.Add(new Round
        {
            Number = 1,
            Tables =
            {
                new Table { Number = 1, PlayerA = "p0", PlayerB = "p1", VpA = 12, VpB = 2 },   // p0 +10
                new Table { Number = 2, PlayerA = "p2", PlayerB = "p3", VpA = 8, VpB = 8 },    // draw
                new Table { Number = 3, PlayerA = "p4" },                                     // bye: 7-0
            },
        });
        var s = Standings.Compute(t);

        Assert.Equal(new[] { "p0", "p4", "p2", "p3", "p1" }, s.Select(x => x.PlayerId));
        Assert.Equal(new[] { 3, 3, 1, 1, 0 }, s.Select(x => x.Points));
        Assert.Equal(new[] { 10, 7, 0, 0, -10 }, s.Select(x => x.Diff));
        Assert.Equal(new[] { 1, 2, 3, 3, 5 }, s.Select(x => x.Rank));       // p2 and p3 perfectly tied
    }

    [Fact]
    public void Drops_at_the_end_of_the_standings_ranked_among_themselves()
    {
        // p0 and p2 drop with more points than p1 and p3: they go behind, p0 ahead of p2.
        var t = NewTournament(5);
        t.Rounds.Add(new Round
        {
            Number = 1,
            Tables =
            {
                new Table { Number = 1, PlayerA = "p0", PlayerB = "p1", VpA = 12, VpB = 2 },
                new Table { Number = 2, PlayerA = "p2", PlayerB = "p3", VpA = 9, VpB = 5 },
                new Table { Number = 3, PlayerA = "p4", PlayerB = null },
            },
        });
        t.Players[0].Dropped = true;
        t.Players[2].Dropped = true;
        Assert.Equal(new[] { "p4", "p3", "p1", "p0", "p2" }, Standings.Compute(t).Select(x => x.PlayerId));
    }

    // ---- Pairings ----

    [Fact]
    public void Swiss_without_rematch_over_all_possible_rounds()
    {
        // 8 players, 7 rounds: each one meets all the others exactly once.
        var t = NewTournament(8);
        t.RoundCount = 7;
        for (var r = 0; r < 7; r++)
        {
            var round = Pair(t, seed: r);
            AssertEveryoneOnce(t, round);
            Play(round, (a, b) => Num(a) < Num(b));
        }
        AssertNoRematch(t);
        Assert.Equal(28, Pairs(t).Count());
    }

    [Theory]
    [InlineData(EventFormat.Tournament)]
    [InlineData(EventFormat.Encounter)]
    public void Big_event_fast_without_rematch(EventFormat format)
    {
        var rng = new Random(42);
        var t = WithFactions(Enumerable.Range(0, 64).Select(i => $"F{i % 8}").ToArray());     // 8 players per faction
        t.Format = format;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var r = 0; r < 7; r++)
        {
            var outcome = TournamentRules.PairNext(t, seed: r);
            Assert.Equal((0, 0), (outcome.Rematches, outcome.Mirrors));
            if (format == EventFormat.Encounter) Assert.DoesNotContain(outcome.Round.Tables, x => !x.IsBye && IsMirror(t, x));
            AssertEveryoneOnce(t, outcome.Round);
            Play(outcome.Round, (_, _) => rng.Next(2) == 0);
        }
        AssertNoRematch(t);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Swiss_pairs_by_points_group()
    {
        var t = NewTournament(8);
        Play(Pair(t), (a, b) => Num(a) < Num(b));
        Play(Pair(t), (a, b) => Num(a) < Num(b));

        var standing = Standings.Compute(t).ToDictionary(s => s.PlayerId, s => s.Points);
        Assert.All(Pair(t).Tables, x => Assert.Equal(standing[x.PlayerA], standing[x.PlayerB!]));
    }

    [Fact]
    public void Bye_to_a_different_player_each_round()
    {
        var t = NewTournament(5);
        var byes = new List<string>();
        for (var r = 0; r < 5; r++)
        {
            var round = Pair(t, seed: r);
            byes.Add(Assert.Single(round.Tables, x => x.IsBye).PlayerA);
            Play(round, (a, b) => Num(a) < Num(b));
        }
        Assert.Equal(5, byes.Distinct().Count());
    }

    [Fact]
    public void Rematch_allowed_and_reported_when_unavoidable()
    {
        var t = NewTournament(4);
        for (var r = 0; r < 3; r++) Play(Pair(t, seed: r), (a, b) => Num(a) < Num(b));
        var outcome = TournamentRules.PairNext(t, seed: 9);  // 4th round with 4 players
        Assert.Equal(2, outcome.Rematches);
        AssertEveryoneOnce(t, outcome.Round);
    }

    [Fact]
    public void Encounter_mirror_allowed_before_a_rematch_and_reported()
    {
        // Two A, two B: after A–B twice (both pairings), only the mirror matches are not rematches.
        var t = WithFactions("A", "A", "B", "B");
        t.Format = EventFormat.Encounter;
        for (var r = 0; r < 2; r++)
        {
            var outcome = TournamentRules.PairNext(t, seed: r);
            Assert.Equal(0, outcome.Mirrors);
            Play(outcome.Round, (a, b) => Num(a) < Num(b));
        }
        var third = TournamentRules.PairNext(t, seed: 2);
        Assert.Equal((0, 2), (third.Rematches, third.Mirrors));
    }

    [Fact]
    public void Dropped_player_no_longer_paired()
    {
        var t = NewTournament(5);
        Assert.Null(TournamentRules.SetDropped(t, "p4", true));
        var round = Pair(t);
        AssertEveryoneOnce(t, round);
        Assert.DoesNotContain(round.Tables, x => x.IsBye);
    }

    // ---- Rules of the phases and results ----

    [Fact]
    public void Next_round_only_when_the_previous_one_is_complete()
    {
        var t = NewTournament(4);
        t.Phase = TournamentPhase.Registration;
        Assert.Equal("notStarted", TournamentRules.CanPair(t)?.Key);
        Assert.Null(TournamentRules.Start(t));

        Pair(t);
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 7));
        Assert.Equal("roundIncomplete", TournamentRules.CanPair(t)?.Key);
        Assert.Null(TournamentRules.SetResult(t, 1, 2, 9, 3));
        Assert.Null(TournamentRules.CanPair(t));

        t.RoundCount = 1;
        Assert.Equal("noMoreRounds", TournamentRules.CanPair(t)?.Key);
        Assert.Null(TournamentRules.Finish(t));
        Assert.Equal("finished", TournamentRules.SetResult(t, 1, 1, 1, 1)?.Key);
    }

    [Fact]
    public void Cabal_played_optional_with_two_cabals()
    {
        var t = NewTournament(2);
        t.CabalsPerPlayer = 2;
        Pair(t);
        var table = t.Rounds[0].Tables[0];
        Assert.Equal("cabalPlayed", TournamentRules.SetResult(t, 1, 1, 7, 3, 0, 2)?.Key);     // out of bounds
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3, null, 1));                       // only one known
        Assert.Equal((null, 1), (table.CabalA, table.CabalB));

        t.CabalsPerPlayer = 1;                                                                // only one: implicit
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 4, 4));
        Assert.Equal((0, 0), (table.CabalA, table.CabalB));
    }

    [Fact]
    public void Undo_the_last_round_as_long_as_there_is_no_result()
    {
        var t = NewTournament(4);
        Pair(t);
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 3, 2));
        Assert.Equal("roundHasResults", TournamentRules.UndoLastRound(t)?.Key);
        Assert.Null(TournamentRules.SetResult(t, 1, 1, null, null));
        Assert.Null(TournamentRules.UndoLastRound(t));
        Assert.Empty(t.Rounds);
    }

    [Fact]
    public void Swap_two_players_of_the_current_round()
    {
        var t = NewTournament(5);
        var round = Pair(t);
        var t1 = round.Tables[0];
        var t2 = round.Tables[1];
        var bye = round.Tables.Single(x => x.IsBye);
        var (a, b, c, d, e) = (t1.PlayerA, t1.PlayerB!, t2.PlayerA, t2.PlayerB!, bye.PlayerA);

        Assert.Null(TournamentRules.SwapPlayers(t, 1, a, d));
        Assert.Equal((d, b), (t1.PlayerA, t1.PlayerB));
        Assert.Equal((c, a), (t2.PlayerA, t2.PlayerB));

        Assert.Null(TournamentRules.SwapPlayers(t, 1, e, b));                                 // with the bye
        Assert.Equal(b, bye.PlayerA);
        AssertEveryoneOnce(t, round);

        Assert.Null(TournamentRules.SetResult(t, 1, t1.Number, 6, 2));
        Assert.Equal("tableHasResult", TournamentRules.SwapPlayers(t, 1, d, c)?.Key);
    }

    [Fact]
    public void Round_scenarios_planned_in_advance()
    {
        var t = NewTournament(2);
        t.RoundCount = 3;
        Assert.Null(TournamentRules.SetScenarios(t, [new Scenario("SABOTAGE"), null, new Scenario(Agendas.Other, "  Le bal  ")]));
        Assert.Equal("SABOTAGE", t.ScenarioOf(1)!.Id);
        Assert.Null(t.ScenarioOf(2));
        Assert.Equal(new Scenario(Agendas.Other, "Le bal"), t.ScenarioOf(3));              // name cleaned up

        Assert.Equal("scenario", TournamentRules.SetScenarios(t, [new Scenario("INCONNU")])?.Key);
        Assert.Equal("scenarioCount", TournamentRules.SetScenarios(t, [null, null, null, new Scenario("VISITS")])?.Key);
    }

    [Fact]
    public void Scenario_draw_without_repeats_or_custom_scenario()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var all = Agendas.Draw([], 10, 0, new Random(seed));
            Assert.Equal(Agendas.All.Order(), all.Select(s => s!.Id).Order());               // 10 rounds: each agenda once

            // Rounds 1 and 2 already paired: kept, and their agendas excluded from the draw; custom
            // scenario of round 4 kept too.
            Scenario?[] current = [new Scenario("SABOTAGE"), new Scenario(Agendas.Other, "Maison"), null, new Scenario(Agendas.Other, "Bal"), new Scenario("VISITS")];
            var drawn = Agendas.Draw(current, 6, 2, new Random(seed));
            Assert.Equal(current[0], drawn[0]);
            Assert.Equal("Bal", drawn[3]!.Name);
            var later = new[] { drawn[2], drawn[4], drawn[5] }.Select(s => s!.Id).ToList();
            Assert.Equal(later.Distinct(), later);
            Assert.DoesNotContain("SABOTAGE", later);
        }
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(5, 3)]
    [InlineData(9, 4)]
    public void Automatic_rounds_2_power_n_at_least_the_players(int players, int rounds) =>
        Assert.Equal(rounds, TournamentRules.AutoRoundCount(players));

    // ---- Registrations (real card catalog) ----

    [Fact]
    public async Task Registration_checked()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(),
                slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        var a = Cabal("A", ("3R06", "E03" /* Phéromones */), ("2R08", "D01" /* Carapace */), ("1R01", "E11" /* Harmonium */));
        var b = Cabal("B", ("3R06", "E03" /* Phéromones */), ("2R11", "E07" /* Ration */), ("1R01", "E08" /* Lumen */));
        var t = new Tournament();
        Assert.Empty(TournamentRules.CheckRegistration(t, d.Catalog, "Ana", "MAISEL", new[] { a, b }));

        t.Players.Add(new Player { Id = "x", Name = "Ana", Faction = "MAISEL" });
        var illegal = b with { Cogs = b.Cogs.Take(2).ToList() };                                   // one cog missing
        var other = a with { Faction = new CodeName("RUBIN", "Réseau Rubin") };
        var problems = TournamentRules.CheckRegistration(t, d.Catalog, " ana ", "MAISEL", new[] { other, illegal });
        Assert.Contains(problems, p => p.Key == "nameTaken");
        Assert.Contains(problems, p => p is { Key: "cabalFaction", Cabal: 0 });
        Assert.Contains(problems, p => p is { Key: "cabalIllegal", Cabal: 1, Legality.Key: "composition" });

        t.Phase = TournamentPhase.Started;
        Assert.Equal("registrationClosed", Assert.Single(TournamentRules.CheckRegistration(t, d.Catalog, "Bo", "MAISEL", new[] { a, b })).Key);
    }
}
