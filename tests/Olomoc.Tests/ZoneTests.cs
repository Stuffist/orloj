using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;
using Olomoc.Tournaments.Stats;
using Olomoc.Zones;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Dystopian Encounters: rules of a game entered in a zone, statistics, dominance.</summary>
public class ZoneTests
{
    // Light: RUBIN, LIBUS, URKARAS, DYKOVIAN. Darkness: MAISEL, OTHERS, BLOCKHAUS.
    private static readonly DateOnly Today = new(2026, 10, 26);

    private static Zone OpenZone => new() { Code = "ZZZZZZ", Name = "Club", StartDate = new(2026, 1, 1), EndDate = new(2026, 12, 31) };

    private static AppData Data => AppData.GetAsync().GetAwaiter().GetResult();

    /// <summary>A complete scenario: agenda and both decks.</summary>
    internal static Scenario FullScenario(string id = "SABOTAGE", string? name = null) => new(id, name, Decks.Artifacts[0], Decks.Ministries[0]);

    // A player with a legal cabal of their faction (an unknown faction: none).
    private static ZonePlayer Player(string faction, string? name = null) =>
        new(faction, name, Data.Catalog.AllFactions.Any(f => f.Code == faction) ? Data.LegalCabal(faction) : null);

    // Complete games: cabals, P1, end of the game, scenario and decks (all required).
    private static ZoneGame Duel(string a, string b, int vpA, int vpB, string? nameA = null) =>
        new(GameFormat.Duel, Today, new ZoneSide([Player(a, nameA)], vpA), new ZoneSide([Player(b)], vpB),
            TableSide.A, GameEnd.FourthCycle, FullScenario());

    private static ZoneGame Teams(string a1, string a2, string b1, string b2, int vpA, int vpB) =>
        new(GameFormat.Teams, Today, new ZoneSide([Player(a1), Player(a2)], vpA),
            new ZoneSide([Player(b1), Player(b2)], vpB), TableSide.B, GameEnd.Bile, FullScenario());

    private static async Task<string[]> Problems(ZoneGame game, Zone? zone = null, bool manager = false)
    {
        var d = await AppData.GetAsync();
        return ZoneRules.CheckGame(game, zone ?? OpenZone, d.Catalog, Today, manager).Problems.Select(p => p.Key).ToArray();
    }

    private static async Task<CabalExport> MaiselCabal()
    {
        var d = await AppData.GetAsync();
        return new CabalExporter(d.L).ToModel("Assaut", "MAISEL",
            new Cog?[] { d.Cog("1R01"), d.Cog("2R08"), d.Cog("3R06") },
            new Equipment?[] { d.Equipment("E11" /* Harmonium */), d.Equipment("D01" /* Carapace */), d.Equipment("E03" /* Phéromones */) });
    }

    // ---- Rules ----

    [Fact]
    public async Task Duel_accepted_and_cleaned()
    {
        var d = await AppData.GetAsync();
        var game = Duel("RUBIN", "MAISEL", 7, 4, nameA: "  Ana  ") with { Scenario = FullScenario(name: "ignored") };
        var (clean, problems) = ZoneRules.CheckGame(game, OpenZone, d.Catalog, Today, manager: false);
        Assert.Empty(problems);
        Assert.Equal("Ana", clean!.A.Players[0].Name);
        Assert.Null(clean.Scenario!.Name);                      // official agenda: no name kept
        Assert.Equal(TableSide.A, clean.Winner);
    }

    [Fact]
    public async Task Duel_accepts_any_factions_even_mirror()
    {
        Assert.Empty(await Problems(Duel("MAISEL", "MAISEL", 5, 5)));
        Assert.Empty(await Problems(Duel("RUBIN", "LIBUS", 5, 3)));
    }

    [Fact]
    public async Task Players_per_side_follow_the_format()
    {
        var teamsAsDuel = Teams("RUBIN", "LIBUS", "MAISEL", "OTHERS", 5, 3) with { Format = GameFormat.Duel };
        Assert.Equal(["zonePlayers"], await Problems(teamsAsDuel));
        var duelAsTeams = Duel("RUBIN", "MAISEL", 5, 3) with { Format = GameFormat.Teams };
        Assert.Equal(["zonePlayers"], await Problems(duelAsTeams));
    }

    [Fact]
    public async Task Teams_are_a_Light_team_against_a_Darkness_team()
    {
        Assert.Empty(await Problems(Teams("RUBIN", "LIBUS", "MAISEL", "OTHERS", 5, 3)));
        Assert.Equal(["teamAlignment"], await Problems(Teams("RUBIN", "MAISEL", "LIBUS", "OTHERS", 5, 3)));
        Assert.Equal(["teamsOpposed"], await Problems(Teams("RUBIN", "LIBUS", "URKARAS", "DYKOVIAN", 5, 3)));
    }

    [Fact]
    public async Task Unknown_faction_VP_and_name_refused()
    {
        Assert.Equal(["zoneFaction"], await Problems(Duel("NOPE", "MAISEL", 5, 3)));
        Assert.Equal(["zoneVictoryPoints"], await Problems(Duel("RUBIN", "MAISEL", -1, 3)));
        Assert.Equal(["zonePlayerName"], await Problems(Duel("RUBIN", "MAISEL", 5, 3, nameA: new string('x', ZoneRules.MaxNameLength + 1))));
    }

    [Fact]
    public async Task Cabal_must_be_legal_and_of_the_player_faction()
    {
        var cabal = await MaiselCabal();
        var game = Duel("RUBIN", "MAISEL", 5, 3);
        var ok = game with { B = new ZoneSide([new ZonePlayer("MAISEL", null, cabal)], 3) };
        Assert.Empty(await Problems(ok));

        var wrong = game with { A = new ZoneSide([new ZonePlayer("RUBIN", null, cabal)], 5) };
        var d = await AppData.GetAsync();
        var problem = Assert.Single(ZoneRules.CheckGame(wrong, OpenZone, d.Catalog, Today, false).Problems);
        Assert.Equal(("cabalFaction", 0), (problem.Key, problem.Cabal));     // "Cabal 1": the first player
    }

    [Fact]
    public async Task Everything_but_the_names_is_required()
    {
        var game = Duel("RUBIN", "MAISEL", 5, 3);
        Assert.Empty(await Problems(game));                                   // names left out: accepted
        var noCabal = game with { B = new ZoneSide([new ZonePlayer("MAISEL")], 3) };
        var missing = ZoneRules.CheckGame(noCabal, OpenZone, Data.Catalog, Today, false).Problems;
        Assert.Equal(("zoneCabal", "2"), (missing.Single().Key, missing.Single().Args![0]));    // player 2
        Assert.Equal(["zoneScenario", "zoneFirst", "zoneEnd"], await Problems(game with { Scenario = null, First = null, End = null }));
        Assert.Equal(["zoneDecks"], await Problems(game with { Scenario = new Scenario("SABOTAGE", null, Decks.Artifacts[0], null) }));
        Assert.Equal(["zoneDecks"], await Problems(game with { Scenario = new Scenario("SABOTAGE") }));
    }

    [Fact]
    public void Map_slots_kept_and_free_ones_given_center_first_to_the_oldest()
    {
        var t0 = DateTimeOffset.UtcNow;
        var placed = new Zone { Code = "AAAAAA", Slot = 0, CreatedAt = t0 };
        var newer = new Zone { Code = "BBBBBB", CreatedAt = t0.AddDays(2) };
        var older = new Zone { Code = "CCCCCC", CreatedAt = t0.AddDays(1) };
        var changed = ZoneMapLayout.Assign([placed, newer, older]);
        Assert.Equal([older, newer], changed);
        Assert.Equal((0, 1, 2), (placed.Slot, older.Slot, newer.Slot));

        // Full map: no slot (the zone is in the list only).
        var full = Enumerable.Range(0, ZoneMapLayout.Slots.Count).Select(i => new Zone { Code = $"Z{i:00000}", Slot = i }).ToList();
        var extra = new Zone { Code = "EXTRAA" };
        Assert.Empty(ZoneMapLayout.Assign([.. full, extra]));
        Assert.Null(extra.Slot);
    }

    [Fact]
    public void Comment_optional_trimmed_and_limited()
    {
        var game = Duel("RUBIN", "MAISEL", 5, 3);
        ZoneGame? Clean(string? comment) => ZoneRules.CheckGame(game with { Comment = comment }, OpenZone, Data.Catalog, Today, false).Clean;
        Assert.Null(Clean(null)!.Comment);
        Assert.Null(Clean("   ")!.Comment);
        Assert.Equal("Retournement au cycle 4 !", Clean("  Retournement au cycle 4 !  ")!.Comment);
        Assert.Null(Clean(new string('x', ZoneRules.MaxCommentLength + 1)));
    }

    [Fact]
    public async Task Dates_of_the_game_within_the_zone_and_not_in_the_future()
    {
        var game = Duel("RUBIN", "MAISEL", 5, 3);
        Assert.Equal(["playedOnFuture"], await Problems(game with { PlayedOn = Today.AddDays(2) }));
        Assert.Empty(await Problems(game with { PlayedOn = Today.AddDays(1) }));         // time zone margin
        Assert.Equal(["playedOnOutside"], await Problems(game with { PlayedOn = new(2025, 12, 31) }));
    }

    [Fact]
    public async Task Closed_or_upcoming_zone_refuses_except_for_managers()
    {
        var game = Duel("RUBIN", "MAISEL", 5, 3);
        var closed = OpenZone;
        closed.Closed = true;
        Assert.Equal(["zoneClosed"], await Problems(game, closed));
        Assert.Empty(await Problems(game, closed, manager: true));

        var ended = OpenZone;
        ended.EndDate = Today.AddDays(-1);
        Assert.Equal(ZoneStatus.Closed, ended.StatusOn(Today));

        var upcoming = OpenZone;
        upcoming.StartDate = Today.AddDays(3);
        Assert.Equal(["zoneUpcoming"], await Problems(game, upcoming));
    }

    [Fact]
    public void Settings_checked()
    {
        Assert.Empty(ZoneRules.CheckSettings("Club", null, null, null));
        Assert.Equal(["zoneName"], ZoneRules.CheckSettings(" ", null, null, null).Select(p => p.Key));
        Assert.Equal(["zoneDates"], ZoneRules.CheckSettings("Club", "", new(2026, 3, 1), new(2026, 2, 1)).Select(p => p.Key));
        Assert.Equal(["zoneDescription"], ZoneRules.CheckSettings("Club", new string('x', 1001), null, null).Select(p => p.Key));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("  ", true)]
    [InlineData("https://example.org/logo.png", true)]
    [InlineData("http://example.org/logo.png", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    [InlineData("https://user:pass@example.org/logo.png", false)]
    [InlineData("/img/logo.png", false)]
    public void Logo_is_an_https_link(string? logo, bool valid)
    {
        Assert.Equal(valid ? Array.Empty<string>() : ["zoneLogo"], ZoneRules.CheckSettings("Club", null, null, null, logo).Select(p => p.Key));
    }

    [Fact]
    public void Logo_trimmed_and_limited()
    {
        Assert.Equal("https://example.org/l.png", ZoneRules.CleanLogo("  https://example.org/l.png "));
        Assert.Null(ZoneRules.CleanLogo("https://example.org/" + new string('a', ZoneRules.MaxLogoLength)));
    }

    // ---- Statistics and dominance ----

    private static async Task<StatsView> Stats(params ZoneGame[] games)
    {
        var d = await AppData.GetAsync();
        return StatsView.OfGames(1, games.Select(g => g.ToStatGame()).ToList(), d.Catalog.AlignmentOf);
    }

    [Fact]
    public async Task Teams_both_players_count_alignment_once_per_game()
    {
        var s = await Stats(Teams("RUBIN", "LIBUS", "MAISEL", "OTHERS", 8, 3));
        var wins = s.FactionWins.ToDictionary(w => w.Key);
        Assert.Equal((1, 1), (wins["RUBIN"].Games, wins["RUBIN"].Wins));
        Assert.Equal((1, 1), (wins["LIBUS"].Games, wins["LIBUS"].Wins));
        Assert.Equal((1, 0, 3), (wins["MAISEL"].Games, wins["MAISEL"].Wins, wins["MAISEL"].Vp));
        var alignments = s.AlignmentWins.ToDictionary(w => w.Key);
        Assert.Equal((1, 1, 8), (alignments["LIGHT"].Games, alignments["LIGHT"].Wins, alignments["LIGHT"].Vp));
        Assert.Equal((1, 0), (alignments["DARK"].Games, alignments["DARK"].Wins));
        Assert.Equal((1, 4, 4), (s.Tournaments, s.Players, s.Factions.Sum(f => f.Count)));
    }

    [Fact]
    public async Task Same_alignment_games_say_nothing_about_alignments()
    {
        var s = await Stats(Duel("RUBIN", "LIBUS", 5, 3), Duel("MAISEL", "MAISEL", 4, 4));
        Assert.Empty(s.AlignmentWins);
        Assert.Equal(4, s.FactionWins.Sum(w => w.Games));
    }

    [Fact]
    public async Task Rank_3_and_picks_from_the_cabals_played()
    {
        var cabal = await MaiselCabal();
        // Rubin without a cabal (a game copied from a tournament can lack it): only Maisel's counts.
        var game = Duel("RUBIN", "MAISEL", 2, 6) with
        {
            A = new ZoneSide([new ZonePlayer("RUBIN")], 2),
            B = new ZoneSide([new ZonePlayer("MAISEL", null, cabal)], 6),
        };
        var s = await Stats(game);
        var rank3 = Assert.Single(s.Rank3Wins);
        Assert.Equal(("1R01", 1), (rank3.Key, rank3.Wins));            // the rank 3 cog of the cabal
        var picks = Assert.Single(s.Picks);
        Assert.Equal(("MAISEL", 1, 3), (picks.Faction, picks.Cabals, picks.Cogs.Count));
    }

    [Fact]
    public async Task Dominance_depends_on_the_mode()
    {
        // Light wins two close games, Darkness one by a wide margin.
        var s = await Stats(Duel("RUBIN", "MAISEL", 6, 5), Duel("RUBIN", "OTHERS", 6, 5), Duel("LIBUS", "MAISEL", 0, 20));
        var d = await AppData.GetAsync();

        var wins = ZoneDominance.Compute(s, DominanceMode.Wins, d.Catalog.AlignmentOf);
        Assert.Equal((Alignment.LIGHT, "RUBIN", false), (wins.Alignment, wins.Faction, wins.Tie));

        var vp = ZoneDominance.Compute(s, DominanceMode.VictoryPoints, d.Catalog.AlignmentOf);
        Assert.Equal((Alignment.DARK, "MAISEL"), (vp.Alignment, vp.Faction));        // 30 VP against 12; Maisel 25, The Others 5

        // Win rate: fewer than StatsView.MinGames games, nobody takes part yet.
        var rate = ZoneDominance.Compute(s, DominanceMode.WinRate, d.Catalog.AlignmentOf);
        Assert.Null(rate.Alignment);
        Assert.Equal(3, rate.Games);
        Assert.True(rate.NeedsMoreGames);                                           // "3 games out of the 10 needed"
        Assert.False(wins.NeedsMoreGames);
    }

    [Fact]
    public async Task Win_rate_dominance_once_there_are_enough_games()
    {
        var games = Enumerable.Range(0, StatsView.MinGames).Select(i => Duel("RUBIN", "MAISEL", i < 6 ? 5 : 1, 3)).ToArray();
        var d = await AppData.GetAsync();
        var dominance = ZoneDominance.Compute(await Stats(games), DominanceMode.WinRate, d.Catalog.AlignmentOf);
        Assert.Equal((Alignment.LIGHT, "RUBIN"), (dominance.Alignment, dominance.Faction));
    }

    [Fact]
    public async Task Tie_at_the_top_means_no_dominance()
    {
        var d = await AppData.GetAsync();
        var s = await Stats(Duel("RUBIN", "MAISEL", 5, 3), Duel("LIBUS", "OTHERS", 1, 3));
        Assert.Equal(new Dominance(null, null, true, 2), ZoneDominance.Compute(s, DominanceMode.Wins, d.Catalog.AlignmentOf));

        // Alignment leading, its factions tied: no leading faction.
        var s2 = await Stats(Duel("RUBIN", "MAISEL", 5, 3), Duel("LIBUS", "OTHERS", 5, 3));
        Assert.Equal(new Dominance(Alignment.LIGHT, null, false, 2), ZoneDominance.Compute(s2, DominanceMode.Wins, d.Catalog.AlignmentOf));
    }

    [Fact]
    public void No_games_no_dominance()
    {
        var s = StatsView.OfGames(1, [], _ => null);
        Assert.Equal(Dominance.None, ZoneDominance.Compute(s, DominanceMode.Wins, _ => null));
    }

    // ---- Tournament bound to a zone ----

    [Fact]
    public async Task Tournament_results_as_zone_games()
    {
        var cabal = await MaiselCabal();
        var t = new Tournament
        {
            Code = "TTTTTT", Date = new(2026, 11, 7), CabalsPerPlayer = 2,
            Players =
            [
                new() { Id = "a", Name = "Ana", Faction = "MAISEL", Cabals = [cabal, cabal] },
                new() { Id = "b", Name = "Bea", Faction = "RUBIN", Cabals = [cabal, cabal] },
                new() { Id = "c", Name = "Cid", Faction = "LIBUS", Cabals = [cabal, cabal] },
            ],
            Rounds =
            [
                new()
                {
                    Number = 2,
                    Tables =
                    [
                        new() { Number = 1, PlayerA = "a", PlayerB = "b", VpA = 7, VpB = 3, CabalA = 1, First = TableSide.B, End = GameEnd.Bile },
                        new() { Number = 2, PlayerA = "c" },                      // bye
                        new() { Number = 3, PlayerA = "c", PlayerB = "b" },       // no result yet
                    ],
                },
            ],
            Scenarios = [null, new Scenario("SABOTAGE")],
        };

        var game = Assert.Single(TournamentZone.Games(t));
        Assert.Equal(TournamentZone.Key(2, 1), game.Key);
        var g = game.Value;
        Assert.Equal((GameFormat.Duel, new DateOnly(2026, 11, 7), 7, 3), (g.Format, g.PlayedOn, g.A.Vp, g.B.Vp));
        Assert.Equal(("MAISEL", "RUBIN"), (g.A.Players[0].Faction, g.B.Players[0].Faction));
        Assert.All(g.Players, p => Assert.Null(p.Name));                      // never the names
        Assert.NotNull(g.A.Players[0].Cabal);
        Assert.Null(g.B.Players[0].Cabal);                                     // two cabals, the one played unknown
        Assert.Equal((TableSide.B, GameEnd.Bile, "SABOTAGE"), (g.First, g.End, g.Scenario!.Id));

        t.ExcludeFromStats = true;
        Assert.Empty(TournamentZone.Games(t));
    }
}
