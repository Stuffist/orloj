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

    private static ZoneGame Duel(string a, string b, int vpA, int vpB, string? nameA = null) =>
        new(GameFormat.Duel, Today, new ZoneSide([new ZonePlayer(a, nameA)], vpA), new ZoneSide([new ZonePlayer(b)], vpB));

    private static ZoneGame Teams(string a1, string a2, string b1, string b2, int vpA, int vpB) =>
        new(GameFormat.Teams, Today, new ZoneSide([new ZonePlayer(a1), new ZonePlayer(a2)], vpA),
            new ZoneSide([new ZonePlayer(b1), new ZonePlayer(b2)], vpB));

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
        var game = Duel("RUBIN", "MAISEL", 7, 4, nameA: "  Ana  ") with { Scenario = new Scenario("SABOTAGE", "ignored") };
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
        var game = Duel("RUBIN", "MAISEL", 2, 6) with { B = new ZoneSide([new ZonePlayer("MAISEL", null, cabal)], 6) };
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
        Assert.Equal(Dominance.None, ZoneDominance.Compute(s, DominanceMode.WinRate, d.Catalog.AlignmentOf));
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
        Assert.Equal(new Dominance(null, null, true), ZoneDominance.Compute(s, DominanceMode.Wins, d.Catalog.AlignmentOf));

        // Alignment leading, its factions tied: no leading faction.
        var s2 = await Stats(Duel("RUBIN", "MAISEL", 5, 3), Duel("LIBUS", "OTHERS", 5, 3));
        Assert.Equal(new Dominance(Alignment.LIGHT, null, false), ZoneDominance.Compute(s2, DominanceMode.Wins, d.Catalog.AlignmentOf));
    }

    [Fact]
    public void No_games_no_dominance()
    {
        var s = StatsView.OfGames(1, [], _ => null);
        Assert.Equal(Dominance.None, ZoneDominance.Compute(s, DominanceMode.Wins, _ => null));
    }
}
