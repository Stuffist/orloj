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
    public async Task Invalid_games_refused()
    {
        Assert.Equal(["zonePlayers"], await Problems(Duel("RUBIN", "MAISEL", 5, 3) with { Format = GameFormat.Teams }));
        Assert.Equal(["zoneFaction"], await Problems(Duel("NOPE", "MAISEL", 5, 3)));
        Assert.Equal(["zoneVictoryPoints"], await Problems(Duel("RUBIN", "MAISEL", -1, 3)));
        var game = Duel("RUBIN", "MAISEL", 5, 3);
        Assert.Equal(["zoneScenario", "zoneFirst", "zoneEnd"], await Problems(game with { Scenario = null, First = null, End = null }));
        Assert.Equal(["zoneDecks"], await Problems(game with { Scenario = new Scenario("SABOTAGE") }));
        Assert.Equal(["playedOnFuture"], await Problems(game with { PlayedOn = Today.AddDays(2) }));
        Assert.Equal(["playedOnOutside"], await Problems(game with { PlayedOn = new(2025, 12, 31) }));
    }

    [Fact]
    public async Task Cabal_must_be_legal_and_of_the_player_faction()
    {
        var cabal = await MaiselCabal();
        var game = Duel("RUBIN", "MAISEL", 5, 3);
        Assert.Empty(await Problems(game with { B = new ZoneSide([new ZonePlayer("MAISEL", null, cabal)], 3) }));
        Assert.Equal(["cabalFaction"], await Problems(game with { A = new ZoneSide([new ZonePlayer("RUBIN", null, cabal)], 5) }));
        Assert.Equal(["zoneCabal"], await Problems(game with { B = new ZoneSide([new ZonePlayer("MAISEL")], 3) }));
    }

    [Fact]
    public async Task Teams_cabals_have_two_cogs()
    {
        var full = await MaiselCabal();
        var two = full with { Cogs = full.Cogs.Where(s => s.Rank != 2).ToList() };     // ranks 3 and 1, equipped
        ZoneSide Side(CabalExport cabal, int vp) => new([new ZonePlayer("MAISEL", null, cabal), new ZonePlayer("MAISEL", null, cabal)], vp);
        var teams = new ZoneGame(GameFormat.Teams, Today, Side(two, 5), Side(two, 3), TableSide.A, GameEnd.FourthCycle, FullScenario());
        Assert.Empty(await Problems(teams));
        Assert.Equal(["cabalCogs", "cabalCogs"], await Problems(teams with { B = Side(full, 3) }));
        Assert.Equal(["cabalCogs"], await Problems(Duel("RUBIN", "MAISEL", 5, 3) with { B = new ZoneSide([new ZonePlayer("MAISEL", null, two)], 3) }));
    }

    [Fact]
    public async Task Closed_zone_refuses_except_for_managers()
    {
        var game = Duel("RUBIN", "MAISEL", 5, 3);
        var closed = OpenZone;
        closed.Closed = true;
        Assert.Equal(["zoneClosed"], await Problems(game, closed));
        Assert.Empty(await Problems(game, closed, manager: true));
    }

    [Fact]
    public void Photo_type_read_from_the_first_bytes()
    {
        Assert.Equal("image/jpeg", ZoneRules.PhotoType([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/png", ZoneRules.PhotoType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]));
        Assert.Null(ZoneRules.PhotoType("<svg onload=alert(1)>"u8));
    }

    [Theory]
    [InlineData("https://example.org/logo.png", true)]
    [InlineData("http://example.org/logo.png", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:pass@example.org/logo.png", false)]
    public void Logo_is_an_https_link(string? logo, bool valid)
    {
        Assert.Equal(valid ? Array.Empty<string>() : ["zoneLogo"], ZoneRules.CheckSettings("Club", null, null, null, logo).Select(p => p.Key));
    }

    [Fact]
    public void Map_slots_kept_and_free_ones_given_center_first_to_the_oldest()
    {
        var t0 = DateTimeOffset.UtcNow;
        var placed = new Zone { Code = "AAAAAA", Slot = 0, CreatedAt = t0 };
        var newer = new Zone { Code = "BBBBBB", CreatedAt = t0.AddDays(2) };
        var older = new Zone { Code = "CCCCCC", CreatedAt = t0.AddDays(1) };
        Assert.Equal([older, newer], ZoneMapLayout.Assign([placed, newer, older]));
        Assert.Equal((0, 1, 2), (placed.Slot, older.Slot, newer.Slot));
    }

    // ---- Statistics and dominance ----

    private static StatsView Stats(params ZoneGame[] games) => StatsView.OfGames(1, games.Select(g => g.ToStatGame()).ToList());

    [Fact]
    public void Teams_both_players_count()
    {
        var wins = Stats(Teams("RUBIN", "LIBUS", "MAISEL", "OTHERS", 8, 3)).FactionWins.ToDictionary(w => w.Key);
        Assert.Equal((1, 1), (wins["RUBIN"].Games, wins["RUBIN"].Wins));
        Assert.Equal((1, 1), (wins["LIBUS"].Games, wins["LIBUS"].Wins));
        Assert.Equal((1, 0, 3), (wins["MAISEL"].Games, wins["MAISEL"].Wins, wins["MAISEL"].Vp));
    }

    [Fact]
    public void Dominance_depends_on_the_mode()
    {
        // Rubin wins two close games, Maisel one by a wide margin.
        var s = Stats(Duel("RUBIN", "MAISEL", 6, 5), Duel("RUBIN", "OTHERS", 6, 5), Duel("LIBUS", "MAISEL", 0, 20));
        Assert.Equal("RUBIN", ZoneDominance.Compute(s, DominanceMode.Wins).Faction);
        Assert.Equal("MAISEL", ZoneDominance.Compute(s, DominanceMode.VictoryPoints).Faction);      // 25 VP against Rubin's 12

        // Win rate: fewer than StatsView.MinGames games for every faction, nobody takes part yet.
        var rate = ZoneDominance.Compute(s, DominanceMode.WinRate);
        Assert.Null(rate.Faction);
        Assert.True(rate.NeedsMoreGames);

        // Tie at the top: no dominance.
        Assert.True(ZoneDominance.Compute(Stats(Duel("RUBIN", "MAISEL", 5, 3), Duel("LIBUS", "OTHERS", 5, 3)), DominanceMode.Wins).Tie);
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
        };

        var g = Assert.Single(TournamentZone.Games(t)).Value;
        Assert.Equal((GameFormat.Duel, new DateOnly(2026, 11, 7), 7, 3), (g.Format, g.PlayedOn, g.A.Vp, g.B.Vp));
        Assert.All(g.Players, p => Assert.Null(p.Name));                      // never the names
        Assert.Null(g.B.Players[0].Cabal);                                     // two cabals, the one played unknown

        t.ExcludeFromStats = true;
        Assert.Empty(TournamentZone.Games(t));
    }
}
