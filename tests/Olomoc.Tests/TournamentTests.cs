using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Tournament: standings, pairings (Swiss, alignment), rules of the phases and registrations.</summary>
public class TournamentTests
{
    // Test factions: L = Light, D = Darkness (the card catalog is not needed here).
    private static Alignment? AlignmentOf(string faction) => faction switch
    {
        "L" => Alignment.LIGHT,
        "D" => Alignment.DARK,
        _ => null,
    };

    private static Tournament NewTournament(PairingMode mode, params string[] factions)
    {
        // One cabal per player: the cabal played is implicit (see Cabal_played_optional_with_two_cabals).
        var t = new Tournament { Code = "TEST", Name = "Test", RoundCount = 10, CabalsPerPlayer = 1, Pairing = mode, Phase = TournamentPhase.Started };
        for (var i = 0; i < factions.Length; i++)
            t.Players.Add(new Player { Id = $"p{i}", Name = $"Joueur {i}", Faction = factions[i] });
        return t;
    }

    private static Round Pair(Tournament t, int seed = 1) => TournamentRules.PairNext(t, AlignmentOf, seed).Round;

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
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L");
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
        Assert.Equal(new[] { 12, 7, 8, 8, 2 }, s.Select(x => x.VpFor));
        Assert.Equal(new[] { 1, 2, 3, 3, 5 }, s.Select(x => x.Rank));       // p2 and p3 perfectly tied
        Assert.Equal((1, 1), (s.Single(x => x.PlayerId == "p4").Byes, s.Single(x => x.PlayerId == "p4").Wins));
    }

    [Fact]
    public void Drops_at_the_end_of_the_standings_ranked_among_themselves()
    {
        // p0 and p2 drop with more points than p1 and p3: they go behind, p0 ahead of p2.
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L");
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
        var s = Standings.Compute(t);

        Assert.Equal(new[] { "p4", "p3", "p1", "p0", "p2" }, s.Select(x => x.PlayerId));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, s.Select(x => x.Rank));
        Assert.Equal(new[] { 3, 0, 0, 3, 3 }, s.Select(x => x.Points));      // the points stay displayed
    }

    [Fact]
    public void Drop_tied_with_a_player_still_in_does_not_get_their_rank()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D");
        t.Rounds.Add(new Round
        {
            Number = 1,
            Tables =
            {
                new Table { Number = 1, PlayerA = "p0", PlayerB = "p1", VpA = 8, VpB = 8 },
                new Table { Number = 2, PlayerA = "p2", PlayerB = "p3", VpA = 8, VpB = 8 },
            },
        });
        t.Players[1].Dropped = true;
        t.Players[3].Dropped = true;
        var s = Standings.Compute(t);

        Assert.Equal(new[] { "p0", "p2", "p1", "p3" }, s.Select(x => x.PlayerId));
        Assert.Equal(new[] { 1, 1, 3, 3 }, s.Select(x => x.Rank));          // ties: among players still in, among drops
    }

    [Fact]
    public void Bye_is_worth_a_7_0_win()
    {
        // Win 6-2 (+4) against a bye (+7): the bye goes ahead on the tiebreaker.
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L");
        t.Rounds.Add(new Round
        {
            Number = 1,
            Tables = { new Table { Number = 1, PlayerA = "p0", PlayerB = "p1", VpA = 6, VpB = 2 }, new Table { Number = 2, PlayerA = "p2" } },
        });
        var s = Standings.Compute(t);
        Assert.Equal(new[] { "p2", "p0", "p1" }, s.Select(x => x.PlayerId));
        Assert.Equal((3, 7, 7), (s[0].Points, s[0].Diff, s[0].VpFor));
    }

    [Fact]
    public void Table_without_a_result_does_not_count()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D");
        t.Rounds.Add(new Round { Number = 1, Tables = { new Table { Number = 1, PlayerA = "p0", PlayerB = "p1", VpA = 5 } } });
        Assert.All(Standings.Compute(t), s => Assert.Equal(0, s.Played));
    }

    // ---- Swiss ----

    [Fact]
    public void Swiss_without_rematch_over_all_possible_rounds()
    {
        // 8 players, 7 rounds: each one meets all the others exactly once.
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L", "D", "L", "D");
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
    [InlineData(PairingMode.Swiss)]
    [InlineData(PairingMode.Alignment)]
    public void Big_tournament_fast_and_without_rematch(PairingMode mode)
    {
        var rng = new Random(42);
        var t = NewTournament(mode, Enumerable.Range(0, 64).Select(i => i % 3 == 0 ? "D" : "L").ToArray());
        t.AvoidSameAlignment = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var r = 0; r < 7; r++)
        {
            var outcome = TournamentRules.PairNext(t, AlignmentOf, seed: r);
            Assert.Equal(0, outcome.Rematches);
            AssertEveryoneOnce(t, outcome.Round);
            Play(outcome.Round, (_, _) => rng.Next(2) == 0);
        }
        AssertNoRematch(t);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Swiss_pairs_by_points_group()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L", "D", "L", "D");
        Play(Pair(t), (a, b) => Num(a) < Num(b));
        Play(Pair(t), (a, b) => Num(a) < Num(b));

        // After 2 rounds: 2 players at 6 pts, 4 at 3, 2 at 0; each table opposes players with equal points.
        var standing = Standings.Compute(t).ToDictionary(s => s.PlayerId, s => s.Points);
        Assert.Equal(new[] { 0, 0, 3, 3, 3, 3, 6, 6 }, standing.Values.OrderBy(x => x));
        var round3 = Pair(t);
        Assert.All(round3.Tables, x => Assert.Equal(standing[x.PlayerA], standing[x.PlayerB!]));
        Assert.Equal(6, standing[round3.Tables[0].PlayerA]);         // table 1: the first two
    }

    [Fact]
    public void Bye_to_the_lowest_ranked_without_a_bye_then_rotating()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L");
        var exempts = new List<string>();
        for (var r = 0; r < 5; r++)
        {
            var round = Pair(t, seed: r);
            AssertEveryoneOnce(t, round);
            var bye = Assert.Single(round.Tables, x => x.IsBye);
            Assert.Equal(round.Tables.Count, bye.Number);                // the bye at the last table
            if (r > 0)
            {
                // The lowest ranked among those who have not had a bye yet.
                var standing = Standings.Compute(new Tournament { Players = t.Players, Rounds = t.Rounds.Take(r).ToList() });
                var expected = standing.Where(s => !exempts.Contains(s.PlayerId)).Last();
                Assert.Equal(expected.Points, standing.Single(s => s.PlayerId == bye.PlayerA).Points);
            }
            exempts.Add(bye.PlayerA);
            Play(round, (a, b) => Num(a) < Num(b));
        }
        Assert.Equal(5, exempts.Distinct().Count());
        AssertNoRematch(t);
    }

    [Fact]
    public void Rematch_allowed_and_reported_when_unavoidable()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D");
        for (var r = 0; r < 3; r++) Play(Pair(t, seed: r), (a, b) => Num(a) < Num(b));
        AssertNoRematch(t);

        var outcome = TournamentRules.PairNext(t, AlignmentOf, seed: 9);  // 4th round with 4 players
        Assert.Equal(2, outcome.Rematches);
        AssertEveryoneOnce(t, outcome.Round);
    }

    [Fact]
    public void Swiss_alignment_option_for_equal_points()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var t = NewTournament(PairingMode.Swiss, "L", "L", "L", "D", "D", "D");
            t.AvoidSameAlignment = true;
            var outcome = TournamentRules.PairNext(t, AlignmentOf, seed);
            Assert.Equal(0, outcome.SameAlignment);
        }
    }

    [Fact]
    public void Dropped_player_no_longer_paired()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L");
        Assert.Null(TournamentRules.SetDropped(t, "p4", true));
        var round = Pair(t);
        AssertEveryoneOnce(t, round);
        Assert.DoesNotContain(round.Tables, x => x.Involves("p4"));
        Assert.DoesNotContain(round.Tables, x => x.IsBye);
    }

    // ---- Alignment ----

    [Theory]
    [InlineData(3, 3, 0, false)]
    [InlineData(4, 2, 1, false)]     // surplus of Light: one Light v. Light table
    [InlineData(4, 3, 0, true)]      // odd: bye in the larger side
    [InlineData(5, 0, 2, true)]
    public void Alignment_light_against_darkness(int light, int dark, int sameAlignment, bool bye)
    {
        for (var seed = 0; seed < 10; seed++)
        {
            var t = NewTournament(PairingMode.Alignment,
                Enumerable.Repeat("L", light).Concat(Enumerable.Repeat("D", dark)).ToArray());
            var outcome = TournamentRules.PairNext(t, AlignmentOf, seed);
            AssertEveryoneOnce(t, outcome.Round);
            Assert.Equal(sameAlignment, outcome.SameAlignment);
            var exempt = outcome.Round.Tables.SingleOrDefault(x => x.IsBye);
            Assert.Equal(bye, exempt is not null);
            if (exempt is not null) Assert.Equal("L", t.PlayerById(exempt.PlayerA)!.Faction);
        }
    }

    [Fact]
    public void Alignment_without_rematch_over_the_rounds()
    {
        var t = NewTournament(PairingMode.Alignment, "L", "L", "L", "D", "D", "D");
        for (var r = 0; r < 3; r++)
        {
            var outcome = TournamentRules.PairNext(t, AlignmentOf, seed: r);
            Assert.Equal(0, outcome.SameAlignment);
            Play(outcome.Round, (a, b) => true);
        }
        AssertNoRematch(t);     // 3 rounds: each Light player has met each Darkness player
    }

    // ---- Rules of the phases and results ----

    [Fact]
    public void Next_round_only_when_the_previous_one_is_complete()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D");
        t.Phase = TournamentPhase.Registration;
        Assert.Equal("notStarted", TournamentRules.CanPair(t)?.Key);
        Assert.Null(TournamentRules.Start(t));
        Assert.Equal("alreadyStarted", TournamentRules.Start(t)?.Key);

        var round = Pair(t);
        Assert.Equal("roundIncomplete", TournamentRules.CanPair(t)?.Key);
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 7));
        Assert.Equal("roundIncomplete", TournamentRules.CanPair(t)?.Key);
        Assert.Null(TournamentRules.SetResult(t, 1, 2, 9, 3));
        Assert.Null(TournamentRules.CanPair(t));

        t.RoundCount = 1;
        Assert.Equal("noMoreRounds", TournamentRules.CanPair(t)?.Key);
        Assert.Null(TournamentRules.Finish(t));
        Assert.Equal(TournamentPhase.Finished, t.Phase);
        Assert.Equal("finished", TournamentRules.SetResult(t, 1, 1, 1, 1)?.Key);
        Assert.Single(t.Rounds);
        Assert.Same(round, t.Rounds[0]);
    }

    [Fact]
    public void Invalid_result_refused()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L");
        Pair(t);
        var bye = t.Rounds[0].Tables.Single(x => x.IsBye).Number;
        Assert.Equal("unknownTable", TournamentRules.SetResult(t, 1, bye, 1, 2)?.Key);
        Assert.Equal("unknownTable", TournamentRules.SetResult(t, 2, 1, 1, 2)?.Key);
        Assert.Equal("victoryPoints", TournamentRules.SetResult(t, 1, 1, 5, null)?.Key);
        Assert.Equal("victoryPoints", TournamentRules.SetResult(t, 1, 1, -1, 2)?.Key);
        Assert.Null(TournamentRules.SetResult(t, 1, 1, null, null));                  // clear
    }

    [Fact]
    public void Cabal_played_optional_with_two_cabals()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D");
        t.CabalsPerPlayer = 2;
        Pair(t);
        Assert.Equal("cabalPlayed", TournamentRules.SetResult(t, 1, 1, 7, 3, 0, 2)?.Key);     // out of bounds
        Assert.Equal("cabalPlayed", TournamentRules.SetResult(t, 1, 1, 7, 3, -1, null)?.Key);
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3));                                // unknown
        Assert.Equal((null, null), (t.Rounds[0].Tables[0].CabalA, t.Rounds[0].Tables[0].CabalB));
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3, null, 1));                       // only one known
        Assert.Equal((null, 1), (t.Rounds[0].Tables[0].CabalA, t.Rounds[0].Tables[0].CabalB));
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3, 1, 0));
        Assert.Equal((1, 0), (t.Rounds[0].Tables[0].CabalA, t.Rounds[0].Tables[0].CabalB));
        Assert.Null(TournamentRules.SetResult(t, 1, 1, null, null, 1, 0));                    // clear: cabals too
        Assert.Equal((null, null), (t.Rounds[0].Tables[0].CabalA, t.Rounds[0].Tables[0].CabalB));

        t.CabalsPerPlayer = 1;                                                                // only one: implicit
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 4, 4));
        Assert.Equal((0, 0), (t.Rounds[0].Tables[0].CabalA, t.Rounds[0].Tables[0].CabalB));
    }

    [Fact]
    public void Undo_the_last_round_as_long_as_there_is_no_result()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D");
        Assert.Equal("noRound", TournamentRules.UndoLastRound(t)?.Key);
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
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L");
        var round = Pair(t);
        var t1 = round.Tables[0];
        var t2 = round.Tables[1];
        var bye = round.Tables.Single(x => x.IsBye);
        var (a, b, c, d, e) = (t1.PlayerA, t1.PlayerB!, t2.PlayerA, t2.PlayerB!, bye.PlayerA);

        // A and D swap places: A joins table 2, D table 1.
        Assert.Null(TournamentRules.SwapPlayers(t, 1, a, d));
        Assert.Equal((d, b), (t1.PlayerA, t1.PlayerB));
        Assert.Equal((c, a), (t2.PlayerA, t2.PlayerB));

        // With the bye: the bye changes player.
        Assert.Null(TournamentRules.SwapPlayers(t, 1, e, b));
        Assert.Equal(b, bye.PlayerA);
        Assert.True(bye.IsBye);
        Assert.Equal((d, e), (t1.PlayerA, t1.PlayerB));
        AssertEveryoneOnce(t, round);

        Assert.Equal("sameTable", TournamentRules.SwapPlayers(t, 1, d, e)?.Key);
        Assert.Equal("unknownPlayer", TournamentRules.SwapPlayers(t, 1, d, "absent")?.Key);
        Assert.Equal("notCurrentRound", TournamentRules.SwapPlayers(t, 2, d, c)?.Key);

        // Table with a result (even partial): untouchable; cleared, the swap becomes possible again.
        Assert.Null(TournamentRules.SetResult(t, 1, t1.Number, 6, 2));
        Assert.Equal("tableHasResult", TournamentRules.SwapPlayers(t, 1, d, c)?.Key);
        Assert.Equal("tableHasResult", TournamentRules.SwapPlayers(t, 1, b, e)?.Key);
        Assert.Null(TournamentRules.SetResult(t, 1, t1.Number, null, null));
        Assert.Null(TournamentRules.SwapPlayers(t, 1, d, c));

        // Previous round: frozen.
        Play(round, (_, _) => true);
        Pair(t);
        Assert.Equal("notCurrentRound", TournamentRules.SwapPlayers(t, 1, a, c)?.Key);

        t.Phase = TournamentPhase.Finished;
        Assert.Equal("finished", TournamentRules.SwapPlayers(t, 2, a, c)?.Key);
    }

    [Fact]
    public void Rematch_found_in_the_previous_rounds()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D");
        var first = Pair(t);
        Play(first, (_, _) => true);
        var (a, b) = (first.Tables[0].PlayerA, first.Tables[0].PlayerB!);
        Pair(t);
        Assert.Equal(1, TournamentRules.MetBefore(t.Rounds, 2, a, b));
        Assert.Equal(1, TournamentRules.MetBefore(t.Rounds, 2, b, a));
        Assert.Null(TournamentRules.MetBefore(t.Rounds, 1, a, b));                    // not before round 1
        Assert.Null(TournamentRules.MetBefore(t.Rounds, 2, a, first.Tables[1].PlayerA));
    }

    [Fact]
    public void Round_scenarios_planned_in_advance()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D");
        t.RoundCount = 3;
        Assert.Null(TournamentRules.SetScenarios(t, [new Scenario("SABOTAGE"), null, new Scenario(Agendas.Other, "  Le bal  ")]));
        Assert.Equal("SABOTAGE", t.ScenarioOf(1)!.Id);
        Assert.Null(t.ScenarioOf(2));
        Assert.Equal(new Scenario(Agendas.Other, "Le bal"), t.ScenarioOf(3));              // name cleaned up
        Assert.Null(t.ScenarioOf(4));

        Assert.Null(TournamentRules.SetScenarios(t, [new Scenario("VISITS", "ignoré"), null, null]));
        Assert.Equal([new Scenario("VISITS")], t.Scenarios);                                 // name of an agenda ignored, empty end removed

        Assert.Equal("scenario", TournamentRules.SetScenarios(t, [new Scenario("INCONNU")])?.Key);
        Assert.Equal("scenarioName", TournamentRules.SetScenarios(t, [new Scenario(Agendas.Other, " ")])?.Key);
        Assert.Equal("deck", TournamentRules.SetScenarios(t, [new Scenario("VISITS", null, "INCONNU")])?.Key);
        Assert.Equal("deck", TournamentRules.SetScenarios(t, [new Scenario("VISITS", null, null, "ROGUE")])?.Key);   // not a Ministry
        Assert.Equal("scenarioCount", TournamentRules.SetScenarios(t, [null, null, null, new Scenario("VISITS")])?.Key);
        Assert.Equal([new Scenario("VISITS")], t.Scenarios);                                 // refused: nothing changed

        // Decks kept, with an agenda as with a custom scenario; each one optional.
        Assert.Null(TournamentRules.SetScenarios(t, [new Scenario("VISITS", null, "ROGUE", "INDOCTRINATION"), new Scenario(Agendas.Other, "Bal", null, "DISINFORMATION")]));
        Assert.Equal([new Scenario("VISITS", null, "ROGUE", "INDOCTRINATION"), new Scenario(Agendas.Other, "Bal", null, "DISINFORMATION")], t.Scenarios);

        t.Phase = TournamentPhase.Finished;
        Assert.Equal("finished", TournamentRules.SetScenarios(t, [])?.Key);
    }

    [Fact]
    public void Scenario_draw_without_repeats_or_custom_scenario()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var all = Agendas.Draw([], 10, 0, new Random(seed));
            Assert.Equal(Agendas.All.Order(), all.Select(s => s!.Id).Order());               // 10 rounds: each agenda once

            // Rounds 1 and 2 already paired: kept, and their agendas excluded from the draw; custom
            // scenario of round 4 kept too; official agenda of round 5 drawn again.
            Scenario?[] current = [new Scenario("SABOTAGE"), new Scenario(Agendas.Other, "Maison"), null, new Scenario(Agendas.Other, "Bal"), new Scenario("VISITS")];
            var drawn = Agendas.Draw(current, 6, 2, new Random(seed));
            Assert.Equal(6, drawn.Count);
            Assert.Equal(current[0], drawn[0]);
            Assert.Equal(current[1], drawn[1]);
            Assert.Equal(current[3], drawn[3]! with { Artifact = null, Ministry = null });       // custom one kept (decks drawn)
            var later = new[] { drawn[2], drawn[4], drawn[5] }.Select(s => s!.Id).ToList();
            Assert.Equal(later.Distinct(), later);
            Assert.All(later, id => Assert.Contains(id, Agendas.All));
            Assert.DoesNotContain("SABOTAGE", later);

            // Decks drawn for the rounds not paired yet (custom scenario included), not for the others.
            Assert.All(drawn.Skip(2), s => Assert.True(Decks.IsArtifact(s!.Artifact) && Decks.IsMinistry(s.Ministry)));
            Assert.Equal("Bal", drawn[3]!.Name);
            Assert.All(all, s => Assert.True(Decks.IsArtifact(s!.Artifact) && Decks.IsMinistry(s.Ministry)));
        }
    }

    [Fact]
    public void P1_and_end_of_game_recorded_with_the_result()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D");
        Pair(t);
        var table = t.Rounds[0].Tables[0];
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3, first: TableSide.B, end: GameEnd.PoliSec));
        Assert.Equal((TableSide.B, GameEnd.PoliSec), (table.First, table.End));
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3));                                // optional: removed
        Assert.Equal((null, null), (table.First, table.End));
        Assert.Null(TournamentRules.SetResult(t, 1, 1, null, null, first: TableSide.A, end: GameEnd.Bile));
        Assert.Equal((null, null), (table.First, table.End));                                // result cleared: nothing
    }

    [Fact]
    public void Starting_requires_two_players()
    {
        var t = NewTournament(PairingMode.Swiss, "L");
        t.Phase = TournamentPhase.Registration;
        Assert.Equal("notEnoughPlayers", TournamentRules.Start(t)?.Key);
    }

    [Theory]
    [InlineData("", 3, 2, "tournamentName")]
    [InlineData("Open", 0, 2, "roundCount")]
    [InlineData("Open", 3, 3, "cabalsPerPlayer")]
    public void Invalid_settings(string name, int rounds, int cabals, string key) =>
        Assert.Equal(key, Assert.Single(TournamentRules.CheckSettings(name, rounds, cabals)).Key);

    // ---- Registrations (real card catalog) ----

    private static async Task<(AppData D, CabalExport A, CabalExport B)> MaiselCabals()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(),
                slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        return (d,
            Cabal("A", ("3R06", "E03" /* Phéromones */), ("2R08", "D01" /* Carapace */), ("1R01", "E11" /* Harmonium */)),
            Cabal("B", ("3R06", "E03" /* Phéromones */), ("2R11", "E07" /* Ration */), ("1R01", "E08" /* Lumen */)));
    }

    [Fact]
    public async Task Legal_registration_accepted()
    {
        var (d, a, b) = await MaiselCabals();
        var t = new Tournament();
        Assert.Empty(TournamentRules.CheckRegistration(t, d.Catalog, "Ana", "MAISEL", new[] { a, b }));
    }

    [Fact]
    public async Task Registration_refused_with_its_reasons()
    {
        var (d, a, b) = await MaiselCabals();
        var t = new Tournament();
        t.Players.Add(new Player { Id = "x", Name = "Ana", Faction = "MAISEL" });

        var illegal = b with { Cogs = b.Cogs.Take(2).ToList() };                                   // one cog missing
        var other = a with { Faction = new CodeName("RUBIN", "Réseau Rubin") };
        var problems = TournamentRules.CheckRegistration(t, d.Catalog, " ana ", "MAISEL", new[] { other, illegal });

        Assert.Contains(problems, p => p.Key == "nameTaken");
        Assert.Contains(problems, p => p is { Key: "cabalFaction", Cabal: 0 });
        Assert.Contains(problems, p => p is { Key: "cabalIllegal", Cabal: 1, Legality.Key: "composition" });

        Assert.Equal("cabalCount", Assert.Single(TournamentRules.CheckRegistration(t, d.Catalog, "Bo", "MAISEL", new[] { a })).Key);
        Assert.Equal("faction", Assert.Single(TournamentRules.CheckRegistration(t, d.Catalog, "Bo", "NOPE", new[] { a, b })).Key);

        // Change of one's own registration: one's name is not "already taken".
        Assert.Empty(TournamentRules.CheckRegistration(t, d.Catalog, "Ana", "MAISEL", new[] { a, b }, playerId: "x"));

        t.Phase = TournamentPhase.Started;
        Assert.Equal("registrationClosed", Assert.Single(TournamentRules.CheckRegistration(t, d.Catalog, "Bo", "MAISEL", new[] { a, b })).Key);
    }

    [Fact]
    public async Task Catalog_alignments()
    {
        var d = await AppData.GetAsync();
        Assert.Equal(Alignment.LIGHT, d.Catalog.AlignmentOf("DYKOVIAN"));
        Assert.Equal(Alignment.DARK, d.Catalog.AlignmentOf("MAISEL"));
        Assert.Equal(new[] { "DYKOVIAN", "LIBUS", "RUBIN", "URKARAS" },
            d.Catalog.AllFactions.Where(f => f.Alignment == Alignment.LIGHT).Select(f => f.Code).OrderBy(c => c));
        Assert.Null(d.Catalog.AlignmentOf("NOPE"));
    }
}
