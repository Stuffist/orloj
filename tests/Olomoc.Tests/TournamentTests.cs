using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Tournoi : classement, appariements (suisse, alignement), règles des phases et inscriptions.</summary>
public class TournamentTests
{
    // Factions de test : L = Lumières, D = Ténèbres (le référentiel n'est pas nécessaire ici).
    private static Alignment? AlignmentOf(string faction) => faction switch
    {
        "L" => Alignment.LIGHT,
        "D" => Alignment.DARK,
        _ => null,
    };

    private static Tournament NewTournament(PairingMode mode, params string[] factions)
    {
        // Une cabale par joueur : la cabale jouée est implicite (voir Cabale_jouee_exigee_avec_deux_cabales).
        var t = new Tournament { Code = "TEST", Name = "Test", RoundCount = 10, CabalsPerPlayer = 1, Pairing = mode, Phase = TournamentPhase.Started };
        for (var i = 0; i < factions.Length; i++)
            t.Players.Add(new Player { Id = $"p{i}", Name = $"Joueur {i}", Faction = factions[i] });
        return t;
    }

    private static Round Pair(Tournament t, int seed = 1) => TournamentRules.PairNext(t, AlignmentOf, seed).Round;

    // Résultats : le joueur A de chaque table gagne si win(A, B), sinon perd ; PV 10 à 4.
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

    // ---- Classement ----

    [Fact]
    public void Classement_points_puis_difference_puis_PV()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L");
        t.Rounds.Add(new Round
        {
            Number = 1,
            Tables =
            {
                new Table { Number = 1, PlayerA = "p0", PlayerB = "p1", VpA = 12, VpB = 2 },   // p0 +10
                new Table { Number = 2, PlayerA = "p2", PlayerB = "p3", VpA = 8, VpB = 8 },    // nul
                new Table { Number = 3, PlayerA = "p4" },                                     // exempt : 7-0
            },
        });
        var s = Standings.Compute(t);

        Assert.Equal(new[] { "p0", "p4", "p2", "p3", "p1" }, s.Select(x => x.PlayerId));
        Assert.Equal(new[] { 3, 3, 1, 1, 0 }, s.Select(x => x.Points));
        Assert.Equal(new[] { 10, 7, 0, 0, -10 }, s.Select(x => x.Diff));
        Assert.Equal(new[] { 12, 7, 8, 8, 2 }, s.Select(x => x.VpFor));
        Assert.Equal(new[] { 1, 2, 3, 3, 5 }, s.Select(x => x.Rank));       // p2 et p3 à égalité parfaite
        Assert.Equal((1, 1), (s.Single(x => x.PlayerId == "p4").Byes, s.Single(x => x.PlayerId == "p4").Wins));
    }

    [Fact]
    public void Abandons_en_fin_de_classement_classes_entre_eux()
    {
        // p0 et p2 abandonnent avec plus de points que p1 et p3 : ils passent derrière, p0 devant p2.
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
        Assert.Equal(new[] { 3, 0, 0, 3, 3 }, s.Select(x => x.Points));      // les points restent affichés
    }

    [Fact]
    public void Abandon_a_egalite_avec_un_joueur_en_lice_n_a_pas_son_rang()
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
        Assert.Equal(new[] { 1, 1, 3, 3 }, s.Select(x => x.Rank));          // égalités : entre en lice, entre abandons
    }

    [Fact]
    public void Exempt_vaut_une_victoire_au_Fiel_7_0()
    {
        // Victoire 6-2 (+4) contre exempt (+7) : l'exempt passe devant au départage.
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
    public void Table_sans_resultat_ne_compte_pas()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D");
        t.Rounds.Add(new Round { Number = 1, Tables = { new Table { Number = 1, PlayerA = "p0", PlayerB = "p1", VpA = 5 } } });
        Assert.All(Standings.Compute(t), s => Assert.Equal(0, s.Played));
    }

    // ---- Suisse ----

    [Fact]
    public void Suisse_sans_revanche_sur_toutes_les_rondes_possibles()
    {
        // 8 joueurs, 7 rondes : chacun rencontre tous les autres exactement une fois.
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
    public void Gros_tournoi_rapide_et_sans_revanche(PairingMode mode)
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
    public void Suisse_apparie_par_groupe_de_points()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L", "D", "L", "D");
        Play(Pair(t), (a, b) => Num(a) < Num(b));
        Play(Pair(t), (a, b) => Num(a) < Num(b));

        // Après 2 rondes : 2 joueurs à 6 pts, 4 à 3, 2 à 0 ; chaque table oppose des joueurs à points égaux.
        var standing = Standings.Compute(t).ToDictionary(s => s.PlayerId, s => s.Points);
        Assert.Equal(new[] { 0, 0, 3, 3, 3, 3, 6, 6 }, standing.Values.OrderBy(x => x));
        var round3 = Pair(t);
        Assert.All(round3.Tables, x => Assert.Equal(standing[x.PlayerA], standing[x.PlayerB!]));
        Assert.Equal(6, standing[round3.Tables[0].PlayerA]);         // table 1 : les deux premiers
    }

    [Fact]
    public void Exempt_au_dernier_classe_sans_exempt_puis_tournant()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L");
        var exempts = new List<string>();
        for (var r = 0; r < 5; r++)
        {
            var round = Pair(t, seed: r);
            AssertEveryoneOnce(t, round);
            var bye = Assert.Single(round.Tables, x => x.IsBye);
            Assert.Equal(round.Tables.Count, bye.Number);                // l'exempt en dernière table
            if (r > 0)
            {
                // Le moins bien classé parmi ceux qui n'ont pas encore été exemptés.
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
    public void Revanche_admise_et_signalee_quand_elle_est_inevitable()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D");
        for (var r = 0; r < 3; r++) Play(Pair(t, seed: r), (a, b) => Num(a) < Num(b));
        AssertNoRematch(t);

        var outcome = TournamentRules.PairNext(t, AlignmentOf, seed: 9);  // 4e ronde à 4 joueurs
        Assert.Equal(2, outcome.Rematches);
        AssertEveryoneOnce(t, outcome.Round);
    }

    [Fact]
    public void Suisse_option_alignement_a_points_egaux()
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
    public void Abandon_n_est_plus_apparie()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L");
        Assert.Null(TournamentRules.SetDropped(t, "p4", true));
        var round = Pair(t);
        AssertEveryoneOnce(t, round);
        Assert.DoesNotContain(round.Tables, x => x.Involves("p4"));
        Assert.DoesNotContain(round.Tables, x => x.IsBye);
    }

    // ---- Alignement ----

    [Theory]
    [InlineData(3, 3, 0, false)]
    [InlineData(4, 2, 1, false)]     // surplus de Lumières : une table Lumières c. Lumières
    [InlineData(4, 3, 0, true)]      // impair : exempt dans le camp le plus nombreux
    [InlineData(5, 0, 2, true)]
    public void Alignement_lumieres_contre_tenebres(int light, int dark, int sameAlignment, bool bye)
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
    public void Alignement_sans_revanche_au_fil_des_rondes()
    {
        var t = NewTournament(PairingMode.Alignment, "L", "L", "L", "D", "D", "D");
        for (var r = 0; r < 3; r++)
        {
            var outcome = TournamentRules.PairNext(t, AlignmentOf, seed: r);
            Assert.Equal(0, outcome.SameAlignment);
            Play(outcome.Round, (a, b) => true);
        }
        AssertNoRematch(t);     // 3 rondes : chaque Lumière a rencontré chaque Ténèbre
    }

    // ---- Règles des phases et des résultats ----

    [Fact]
    public void Ronde_suivante_seulement_quand_la_precedente_est_complete()
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
    public void Resultat_invalide_refuse()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L");
        Pair(t);
        var bye = t.Rounds[0].Tables.Single(x => x.IsBye).Number;
        Assert.Equal("unknownTable", TournamentRules.SetResult(t, 1, bye, 1, 2)?.Key);
        Assert.Equal("unknownTable", TournamentRules.SetResult(t, 2, 1, 1, 2)?.Key);
        Assert.Equal("victoryPoints", TournamentRules.SetResult(t, 1, 1, 5, null)?.Key);
        Assert.Equal("victoryPoints", TournamentRules.SetResult(t, 1, 1, -1, 2)?.Key);
        Assert.Null(TournamentRules.SetResult(t, 1, 1, null, null));                  // effacer
    }

    [Fact]
    public void Cabale_jouee_facultative_avec_deux_cabales()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D");
        t.CabalsPerPlayer = 2;
        Pair(t);
        Assert.Equal("cabalPlayed", TournamentRules.SetResult(t, 1, 1, 7, 3, 0, 2)?.Key);     // hors bornes
        Assert.Equal("cabalPlayed", TournamentRules.SetResult(t, 1, 1, 7, 3, -1, null)?.Key);
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3));                                // inconnues
        Assert.Equal((null, null), (t.Rounds[0].Tables[0].CabalA, t.Rounds[0].Tables[0].CabalB));
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3, null, 1));                       // une seule connue
        Assert.Equal((null, 1), (t.Rounds[0].Tables[0].CabalA, t.Rounds[0].Tables[0].CabalB));
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3, 1, 0));
        Assert.Equal((1, 0), (t.Rounds[0].Tables[0].CabalA, t.Rounds[0].Tables[0].CabalB));
        Assert.Null(TournamentRules.SetResult(t, 1, 1, null, null, 1, 0));                    // effacer : cabales aussi
        Assert.Equal((null, null), (t.Rounds[0].Tables[0].CabalA, t.Rounds[0].Tables[0].CabalB));

        t.CabalsPerPlayer = 1;                                                                // une seule : implicite
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 4, 4));
        Assert.Equal((0, 0), (t.Rounds[0].Tables[0].CabalA, t.Rounds[0].Tables[0].CabalB));
    }

    [Fact]
    public void Annuler_la_derniere_ronde_tant_qu_aucun_resultat()
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
    public void Echanger_deux_joueurs_de_la_ronde_en_cours()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D", "L");
        var round = Pair(t);
        var t1 = round.Tables[0];
        var t2 = round.Tables[1];
        var bye = round.Tables.Single(x => x.IsBye);
        var (a, b, c, d, e) = (t1.PlayerA, t1.PlayerB!, t2.PlayerA, t2.PlayerB!, bye.PlayerA);

        // A et D échangent leur place : A rejoint la table 2, D la table 1.
        Assert.Null(TournamentRules.SwapPlayers(t, 1, a, d));
        Assert.Equal((d, b), (t1.PlayerA, t1.PlayerB));
        Assert.Equal((c, a), (t2.PlayerA, t2.PlayerB));

        // Avec l'exempt : l'exemption change de joueur.
        Assert.Null(TournamentRules.SwapPlayers(t, 1, e, b));
        Assert.Equal(b, bye.PlayerA);
        Assert.True(bye.IsBye);
        Assert.Equal((d, e), (t1.PlayerA, t1.PlayerB));
        AssertEveryoneOnce(t, round);

        Assert.Equal("sameTable", TournamentRules.SwapPlayers(t, 1, d, e)?.Key);
        Assert.Equal("unknownPlayer", TournamentRules.SwapPlayers(t, 1, d, "absent")?.Key);
        Assert.Equal("notCurrentRound", TournamentRules.SwapPlayers(t, 2, d, c)?.Key);

        // Table avec un résultat (même partiel) : intouchable ; effacé, l'échange redevient possible.
        Assert.Null(TournamentRules.SetResult(t, 1, t1.Number, 6, 2));
        Assert.Equal("tableHasResult", TournamentRules.SwapPlayers(t, 1, d, c)?.Key);
        Assert.Equal("tableHasResult", TournamentRules.SwapPlayers(t, 1, b, e)?.Key);
        Assert.Null(TournamentRules.SetResult(t, 1, t1.Number, null, null));
        Assert.Null(TournamentRules.SwapPlayers(t, 1, d, c));

        // Ronde précédente : figée.
        Play(round, (_, _) => true);
        Pair(t);
        Assert.Equal("notCurrentRound", TournamentRules.SwapPlayers(t, 1, a, c)?.Key);

        t.Phase = TournamentPhase.Finished;
        Assert.Equal("finished", TournamentRules.SwapPlayers(t, 2, a, c)?.Key);
    }

    [Fact]
    public void Revanche_reperee_dans_les_rondes_precedentes()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D", "L", "D");
        var first = Pair(t);
        Play(first, (_, _) => true);
        var (a, b) = (first.Tables[0].PlayerA, first.Tables[0].PlayerB!);
        Pair(t);
        Assert.Equal(1, TournamentRules.MetBefore(t.Rounds, 2, a, b));
        Assert.Equal(1, TournamentRules.MetBefore(t.Rounds, 2, b, a));
        Assert.Null(TournamentRules.MetBefore(t.Rounds, 1, a, b));                    // pas avant la ronde 1
        Assert.Null(TournamentRules.MetBefore(t.Rounds, 2, a, first.Tables[1].PlayerA));
    }

    [Fact]
    public void Scenarios_des_rondes_prevus_a_l_avance()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D");
        t.RoundCount = 3;
        Assert.Null(TournamentRules.SetScenarios(t, [new Scenario("SABOTAGE"), null, new Scenario(Agendas.Other, "  Le bal  ")]));
        Assert.Equal("SABOTAGE", t.ScenarioOf(1)!.Id);
        Assert.Null(t.ScenarioOf(2));
        Assert.Equal(new Scenario(Agendas.Other, "Le bal"), t.ScenarioOf(3));              // nom nettoyé
        Assert.Null(t.ScenarioOf(4));

        Assert.Null(TournamentRules.SetScenarios(t, [new Scenario("VISITS", "ignoré"), null, null]));
        Assert.Equal([new Scenario("VISITS")], t.Scenarios);                                 // nom d'un agenda ignoré, fin vide retirée

        Assert.Equal("scenario", TournamentRules.SetScenarios(t, [new Scenario("INCONNU")])?.Key);
        Assert.Equal("scenarioName", TournamentRules.SetScenarios(t, [new Scenario(Agendas.Other, " ")])?.Key);
        Assert.Equal("scenarioCount", TournamentRules.SetScenarios(t, [null, null, null, new Scenario("VISITS")])?.Key);
        Assert.Equal([new Scenario("VISITS")], t.Scenarios);                                 // refus : rien de changé

        t.Phase = TournamentPhase.Finished;
        Assert.Equal("finished", TournamentRules.SetScenarios(t, [])?.Key);
    }

    [Fact]
    public void Tirage_des_scenarios_sans_repetition_ni_scenario_maison()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var all = Agendas.Draw([], 10, 0, new Random(seed));
            Assert.Equal(Agendas.All.Order(), all.Select(s => s!.Id).Order());               // 10 rondes : chaque agenda une fois

            // Rondes 1 et 2 déjà appariées : gardées, et leurs agendas exclus du tirage ; scénario
            // maison de la ronde 4 gardé aussi ; agenda officiel de la ronde 5 retiré au sort.
            Scenario?[] current = [new Scenario("SABOTAGE"), new Scenario(Agendas.Other, "Maison"), null, new Scenario(Agendas.Other, "Bal"), new Scenario("VISITS")];
            var drawn = Agendas.Draw(current, 6, 2, new Random(seed));
            Assert.Equal(6, drawn.Count);
            Assert.Equal(current[0], drawn[0]);
            Assert.Equal(current[1], drawn[1]);
            Assert.Equal(current[3], drawn[3]);
            var later = new[] { drawn[2], drawn[4], drawn[5] }.Select(s => s!.Id).ToList();
            Assert.Equal(later.Distinct(), later);
            Assert.All(later, id => Assert.Contains(id, Agendas.All));
            Assert.DoesNotContain("SABOTAGE", later);
        }
    }

    [Fact]
    public void J1_et_fin_de_partie_notes_avec_le_resultat()
    {
        var t = NewTournament(PairingMode.Swiss, "L", "D");
        Pair(t);
        var table = t.Rounds[0].Tables[0];
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3, first: TableSide.B, end: GameEnd.PoliSec));
        Assert.Equal((TableSide.B, GameEnd.PoliSec), (table.First, table.End));
        Assert.Null(TournamentRules.SetResult(t, 1, 1, 7, 3));                                // facultatifs : retirés
        Assert.Equal((null, null), (table.First, table.End));
        Assert.Null(TournamentRules.SetResult(t, 1, 1, null, null, first: TableSide.A, end: GameEnd.Bile));
        Assert.Equal((null, null), (table.First, table.End));                                // résultat effacé : rien
    }

    [Fact]
    public void Lancer_exige_deux_joueurs()
    {
        var t = NewTournament(PairingMode.Swiss, "L");
        t.Phase = TournamentPhase.Registration;
        Assert.Equal("notEnoughPlayers", TournamentRules.Start(t)?.Key);
    }

    [Theory]
    [InlineData("", 3, 2, "tournamentName")]
    [InlineData("Open", 0, 2, "roundCount")]
    [InlineData("Open", 3, 3, "cabalsPerPlayer")]
    public void Reglages_invalides(string name, int rounds, int cabals, string key) =>
        Assert.Equal(key, Assert.Single(TournamentRules.CheckSettings(name, rounds, cabals)).Key);

    // ---- Inscriptions (référentiel réel) ----

    private static async Task<(AppData D, CabalExport A, CabalExport B)> MaiselCabals()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(),
                slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        return (d,
            Cabal("A", ("3R06", "Phéromones"), ("2R08", "Carapace"), ("1R01", "Harmonium")),
            Cabal("B", ("3R06", "Dose de Mélisse"), ("2R04", "Phéromones"), ("1R01", "Passe-droit du Census")));
    }

    [Fact]
    public async Task Inscription_legale_acceptee()
    {
        var (d, a, b) = await MaiselCabals();
        var t = new Tournament();
        Assert.Empty(TournamentRules.CheckRegistration(t, d.Catalog, "Ana", "MAISEL", new[] { a, b }));
    }

    [Fact]
    public async Task Inscription_refusee_avec_ses_raisons()
    {
        var (d, a, b) = await MaiselCabals();
        var t = new Tournament();
        t.Players.Add(new Player { Id = "x", Name = "Ana", Faction = "MAISEL" });

        var illegal = b with { Cogs = b.Cogs.Take(2).ToList() };                                   // un rouage manquant
        var other = a with { Faction = new CodeName("RUBIN", "Réseau Rubin") };
        var problems = TournamentRules.CheckRegistration(t, d.Catalog, " ana ", "MAISEL", new[] { other, illegal });

        Assert.Contains(problems, p => p.Key == "nameTaken");
        Assert.Contains(problems, p => p is { Key: "cabalFaction", Cabal: 0 });
        Assert.Contains(problems, p => p is { Key: "cabalIllegal", Cabal: 1, Legality.Key: "composition" });

        Assert.Equal("cabalCount", Assert.Single(TournamentRules.CheckRegistration(t, d.Catalog, "Bo", "MAISEL", new[] { a })).Key);
        Assert.Equal("faction", Assert.Single(TournamentRules.CheckRegistration(t, d.Catalog, "Bo", "NOPE", new[] { a, b })).Key);

        // Modification de sa propre inscription : son nom n'est pas « déjà pris ».
        Assert.Empty(TournamentRules.CheckRegistration(t, d.Catalog, "Ana", "MAISEL", new[] { a, b }, playerId: "x"));

        t.Phase = TournamentPhase.Started;
        Assert.Equal("registrationClosed", Assert.Single(TournamentRules.CheckRegistration(t, d.Catalog, "Bo", "MAISEL", new[] { a, b })).Key);
    }

    [Fact]
    public async Task Alignements_du_referentiel()
    {
        var d = await AppData.GetAsync();
        Assert.Equal(Alignment.LIGHT, d.Catalog.AlignmentOf("DYKOVIAN"));
        Assert.Equal(Alignment.DARK, d.Catalog.AlignmentOf("MAISEL"));
        Assert.Equal(new[] { "DYKOVIAN", "LIBUS", "RUBIN", "URKARAS" },
            d.Catalog.AllFactions.Where(f => f.Alignment == Alignment.LIGHT).Select(f => f.Code).OrderBy(c => c));
        Assert.Null(d.Catalog.AlignmentOf("NOPE"));
    }
}
