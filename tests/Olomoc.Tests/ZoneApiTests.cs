using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Olomoc.Accounts;
using Olomoc.Tournaments;
using Olomoc.Tournaments.Stats;
using Olomoc.Zones;
using Xunit;

namespace Olomoc.Tests;

/// <summary>
/// Zones API (/api/zones, /api/admin/zones), in-memory server on a temporary data folder. "Root" is
/// admin, "Bob" manages the zones he is given, "Carl" is a player.
/// </summary>
public sealed class ZoneApiTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-zones-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _servers = new();

    public void Dispose()
    {
        _servers.ForEach(s => s.Dispose());
        TestDb.Delete(_data);
    }

    private WebApplicationFactory<Program> Server(string admins = "", int entriesPerHour = 1000)
    {
        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("SESSION_CHECK_SECONDS", "0")
            .UseSetting("RATE_ACCOUNTS_PER_HOUR", "1000")
            .UseSetting("RATE_LOGINS_PER_15_MINUTES", "1000")
            .UseSetting("RATE_ZONE_ENTRIES_PER_HOUR", entriesPerHour.ToString())
            .UseSetting("OLOMOC_ADMINS", admins));
        _servers.Add(server);
        return server;
    }

    private static Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string url, object? body = null, string? entry = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: TournamentJson.Options);
        if (entry is not null) request.Headers.Add(ZoneHeaders.Entry, entry);
        return http.SendAsync(request);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options))!;
    }

    private static async Task<string[]> Keys(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemList>(TournamentJson.Options))!.Problems.Select(p => p.Key).ToArray();

    /// <summary>Root, Bob and Carl created, server restarted with Root as admin; clients signed in, plus an anonymous one.</summary>
    private async Task<(WebApplicationFactory<Program> Server, HttpClient Root, HttpClient Bob, HttpClient Carl, HttpClient Anonymous)> StartAsync()
    {
        var first = Server();
        foreach (var name in new[] { "Root", "Bob", "Carl" })
            Assert.Equal(HttpStatusCode.OK, (await Send(first.CreateClient(), HttpMethod.Post, "/api/account/register",
                new RegisterRequest(name, $"{name.ToLowerInvariant()}@example.org", "motdepasse", "fr"))).StatusCode);
        var server = Server(admins: "Root");
        var clients = new[] { "Root", "Bob", "Carl" }.Select(_ => server.CreateClient()).ToArray();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await Send(clients[i], HttpMethod.Post, "/api/account/login",
                new LoginRequest(new[] { "Root", "Bob", "Carl" }[i], "motdepasse"))).StatusCode);
        return (server, clients[0], clients[1], clients[2], server.CreateClient());
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static ZoneAdminRequest Settings(string name = "Club du mardi", bool hidden = false, bool closed = false, params string[] managers) =>
        new(name, "Mardi 18 h – 23 h", ZoneKind.Club, Today.AddDays(-30), null, closed, DominanceMode.Wins, hidden, managers);

    private static async Task<string> CreateZone(HttpClient admin, ZoneAdminRequest settings)
    {
        var response = await Send(admin, HttpMethod.Post, "/api/admin/zones", settings);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Read<ZoneCreated>(response)).Code;
    }

    private static AppData Data => AppData.GetAsync().GetAwaiter().GetResult();

    // A player with a legal cabal of their faction (required, like P1, the end and the scenario).
    private static ZonePlayer Player(string faction, string? name = null) => new(faction, name, Data.LegalCabal(faction));

    private static ZoneGame Duel(string a = "RUBIN", string b = "MAISEL", int vpA = 7, int vpB = 4) =>
        new(GameFormat.Duel, Today, new ZoneSide([Player(a, "Ana")], vpA), new ZoneSide([Player(b)], vpB),
            TableSide.A, GameEnd.FourthCycle, ZoneTests.FullScenario());

    private static async Task<ZoneGameCreated> Enter(HttpClient http, string zone, ZoneGame? game = null)
    {
        var response = await Send(http, HttpMethod.Post, $"/api/zones/{zone}/games", game ?? Duel());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Read<ZoneGameCreated>(response);
    }

    private static async Task<ZoneView> Zone(HttpClient http, string code) => await Read<ZoneView>(await http.GetAsync($"/api/zones/{code}"));

    private static async Task<ZoneGamesPage> Games(HttpClient http, string code, long? before = null) =>
        await Read<ZoneGamesPage>(await http.GetAsync($"/api/zones/{code}/games" + (before is { } b ? $"?before={b}" : "")));

    [Fact]
    public async Task Only_zone_admins_create_and_delete_zones()
    {
        var (_, root, bob, _, anonymous) = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous, HttpMethod.Post, "/api/admin/zones", Settings())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bob, HttpMethod.Post, "/api/admin/zones", Settings())).StatusCode);

        var bad = await Send(root, HttpMethod.Post, "/api/admin/zones", Settings(name: " ", managers: "Nobody"));
        Assert.Equal(["zoneName", "unknownAccount"], await Keys(bad));

        var code = await CreateZone(root, Settings());
        await Enter(anonymous, code);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bob, HttpMethod.Delete, $"/api/admin/zones/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Delete, $"/api/admin/zones/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/zones/{code}")).StatusCode);
        var audit = await Read<AdminAction[]>(await root.GetAsync("/api/admin/audit"));
        Assert.Equal(["deleteZone", "createZone"], audit.Select(a => a.Action).Take(2));
    }

    [Fact]
    public async Task Editors_administer_every_zone()
    {
        var (_, root, bob, carl, _) = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await Send(root, HttpMethod.Put, "/api/admin/accounts/Bob/role", new RoleRequest(AccountRole.Editor))).StatusCode);

        // Bob (editor) creates a zone with every setting, changes another's mode and visibility, deletes it.
        var mine = await CreateZone(bob, Settings("Club de Bob", managers: "Carl"));
        var other = await CreateZone(root, Settings("Club de Root"));
        var view = await Zone(bob, other);
        Assert.True(view.CanManage && view.ZoneAdmin);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bob, HttpMethod.Put, $"/api/admin/zones/{other}", Settings("Club de Root", hidden: true) with
        {
            Dominance = DominanceMode.WinRate,
        })).StatusCode);
        Assert.Equal(DominanceMode.WinRate, (await Zone(root, other)).Mode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bob, HttpMethod.Delete, $"/api/admin/zones/{other}")).StatusCode);

        // Carl, a mere manager: no zone administration.
        Assert.False((await Zone(carl, mine)).ZoneAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(carl, HttpMethod.Post, "/api/admin/zones", Settings())).StatusCode);
    }

    [Fact]
    public async Task Anyone_enters_a_game_and_changes_it_with_the_entry_token()
    {
        var (server, root, _, _, anonymous) = await StartAsync();
        var code = await CreateZone(root, Settings());
        var entry = await Enter(anonymous, code);

        var view = await Zone(anonymous, code);
        Assert.Equal((ZoneStatus.Open, 1, false), (view.Status, view.Stats.Games, view.CanManage));
        Assert.Equal((Olomoc.Models.Alignment?)Olomoc.Models.Alignment.LIGHT, view.Dominance.Alignment);
        Assert.Equal("RUBIN", view.Dominance.Faction);
        Assert.Null(view.Managers);

        var page = await Games(anonymous, code);
        var game = Assert.Single(page.Games);
        Assert.Equal(("Ana", false), (game.Game.A.Players[0].Name, game.CanEdit));   // the token is checked by the browser

        // Another device, without the token: refused; with it: accepted.
        var other = server.CreateClient();
        var changed = Duel(vpA: 2, vpB: 9);
        var refused = await Send(other, HttpMethod.Put, $"/api/zones/{code}/games/{entry.Id}", changed);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(["zoneForbidden"], await Keys(refused));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(other, HttpMethod.Put, $"/api/zones/{code}/games/{entry.Id}", changed, entry.EditToken)).StatusCode);
        Assert.Equal(Olomoc.Models.Alignment.DARK, (await Zone(anonymous, code)).Dominance.Alignment);     // statistics computed again

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(other, HttpMethod.Delete, $"/api/zones/{code}/games/{entry.Id}", entry: "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(other, HttpMethod.Delete, $"/api/zones/{code}/games/{entry.Id}", entry: entry.EditToken)).StatusCode);
        Assert.Empty((await Games(anonymous, code)).Games);
    }

    [Fact]
    public async Task Invalid_games_refused()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var code = await CreateZone(root, Settings());
        var teams = new ZoneGame(GameFormat.Teams, Today, new ZoneSide([Player("RUBIN"), Player("LIBUS")], 5),
            new ZoneSide([Player("URKARAS"), Player("DYKOVIAN")], 3), TableSide.A, GameEnd.Bile, ZoneTests.FullScenario());
        var response = await Send(anonymous, HttpMethod.Post, $"/api/zones/{code}/games", teams);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["teamsOpposed"], await Keys(response));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(anonymous, HttpMethod.Post, "/api/zones/ABCDEF/games", Duel())).StatusCode);
    }

    [Fact]
    public async Task The_account_is_enough_for_its_author_on_any_device()
    {
        var (server, root, _, carl, _) = await StartAsync();
        var code = await CreateZone(root, Settings());
        var entry = await Enter(carl, code);
        Assert.True(Assert.Single((await Games(carl, code)).Games).CanEdit);

        // Carl on another device: signed in, without the token.
        var carl2 = server.CreateClient();
        await Send(carl2, HttpMethod.Post, "/api/account/login", new LoginRequest("Carl", "motdepasse"));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(carl2, HttpMethod.Put, $"/api/zones/{code}/games/{entry.Id}", Duel(vpA: 1))).StatusCode);

        // Account deleted: the game stays, without an author.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(carl2, HttpMethod.Post, "/api/account/delete", new DeleteAccountRequest("motdepasse"))).StatusCode);
        Assert.Single((await Games(root, code)).Games);
    }

    [Fact]
    public async Task Managers_moderate_their_zone_only()
    {
        var (_, root, bob, carl, anonymous) = await StartAsync();
        var mine = await CreateZone(root, Settings("Club de Bob", managers: "Bob"));
        var other = await CreateZone(root, Settings("Autre club"));
        var entry = await Enter(anonymous, mine);
        var elsewhere = await Enter(anonymous, other);

        var view = await Zone(bob, mine);
        Assert.True(view.CanManage);
        Assert.Equal(["Bob"], view.Managers);
        Assert.True(Assert.Single((await Games(bob, mine)).Games).CanEdit);
        Assert.False((await Zone(bob, other)).CanManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bob, HttpMethod.Delete, $"/api/zones/{other}/games/{elsewhere.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bob, HttpMethod.Delete, $"/api/zones/{mine}/games/{entry.Id}")).StatusCode);

        // Settings: the manager, not another account, not anonymous.
        var settings = new ZoneSettingsRequest("Club de Bob (mardi)", "Nouvelle salle", ZoneKind.Club, Today.AddDays(-30), null, false,
            " https://example.org/bob.png ");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(carl, HttpMethod.Put, $"/api/zones/{mine}", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous, HttpMethod.Put, $"/api/zones/{mine}", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bob, HttpMethod.Put, $"/api/zones/{mine}", settings)).StatusCode);
        Assert.Equal("Club de Bob (mardi)", (await Zone(anonymous, mine)).Name);

        // Logo: a link only (https), kept trimmed, in the zone and the list.
        Assert.Equal("https://example.org/bob.png", (await Zone(anonymous, mine)).Logo);
        Assert.Equal("https://example.org/bob.png",
            (await Read<List<ZoneSummary>>(await anonymous.GetAsync("/api/zones"))).Single(z => z.Code == mine).Logo);
        var badLogo = await Send(bob, HttpMethod.Put, $"/api/zones/{mine}", settings with { Logo = "javascript:alert(1)" });
        Assert.Equal(HttpStatusCode.BadRequest, badLogo.StatusCode);
        Assert.Equal(["zoneLogo"], await Keys(badLogo));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bob, HttpMethod.Put, $"/api/zones/{mine}", settings with { Logo = "" })).StatusCode);
        Assert.Null((await Zone(anonymous, mine)).Logo);
    }

    [Fact]
    public async Task Closed_zone_refuses_entries_and_changes_except_for_its_managers()
    {
        var (_, root, bob, _, anonymous) = await StartAsync();
        var code = await CreateZone(root, Settings(managers: "Bob"));
        var entry = await Enter(anonymous, code);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bob, HttpMethod.Put, $"/api/zones/{code}",
            new ZoneSettingsRequest("Club du mardi", null, ZoneKind.Club, Today.AddDays(-30), null, Closed: true))).StatusCode);

        var view = await Zone(anonymous, code);
        Assert.Equal(ZoneStatus.Closed, view.Status);
        Assert.Equal(Olomoc.Models.Alignment.LIGHT, view.Dominance.Alignment);        // "Closed under the Light"

        var refused = await Send(anonymous, HttpMethod.Post, $"/api/zones/{code}/games", Duel());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(["zoneClosed"], await Keys(refused));
        var change = await Send(anonymous, HttpMethod.Put, $"/api/zones/{code}/games/{entry.Id}", Duel(vpA: 0), entry.EditToken);
        Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
        Assert.Equal(["zoneClosed"], await Keys(change));

        await Enter(bob, code);
        await Enter(root, code);
        Assert.Equal(3, (await Zone(anonymous, code)).Stats.Games);
    }

    [Fact]
    public async Task Hidden_zones_out_of_the_list_and_of_the_overall_statistics()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var shown = await CreateZone(root, Settings("Visible"));
        var hidden = await CreateZone(root, Settings("Essai", hidden: true));
        await Enter(anonymous, shown);
        await Enter(anonymous, hidden, Duel("LIBUS", "OTHERS"));

        var list = await Read<List<ZoneSummary>>(await anonymous.GetAsync("/api/zones"));
        Assert.Equal(["Visible"], list.Select(z => z.Name));
        Assert.Equal(1, list[0].Games);
        Assert.Equal(2, (await Read<List<ZoneSummary>>(await root.GetAsync("/api/zones"))).Count);
        Assert.Equal("Essai", (await Zone(anonymous, hidden)).Name);           // reachable by its link

        var all = await Read<StatsView>(await anonymous.GetAsync("/api/zones/stats"));
        Assert.Equal((1, 1), (all.Tournaments, all.Games));
        Assert.DoesNotContain(all.FactionWins, w => w.Key == "LIBUS");
    }

    [Fact]
    public async Task Hexagons_stay_put_when_zones_are_added_changed_or_deleted()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var first = await CreateZone(root, Settings("Un"));
        var second = await CreateZone(root, Settings("Deux"));
        var third = await CreateZone(root, Settings("Trois"));
        async Task<Dictionary<string, int?>> Slots() =>
            (await Read<List<ZoneSummary>>(await anonymous.GetAsync("/api/zones"))).ToDictionary(z => z.Code, z => z.Slot);
        var after = await Slots();
        Assert.Equal((0, 1, 2), (after[first], after[second], after[third]));      // the center, then the first ring

        // Renamed, closed: nobody moves.
        await Send(root, HttpMethod.Put, $"/api/admin/zones/{second}", Settings("Deux bis", closed: true));
        after = await Slots();
        Assert.Equal((0, 1, 2), (after[first], after[second], after[third]));

        // Deleted: its slot (the center) goes to the next zone created; the others stay.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Delete, $"/api/admin/zones/{first}")).StatusCode);
        var fourth = await CreateZone(root, Settings("Quatre"));
        after = await Slots();
        Assert.Equal((1, 2, 0), (after[second], after[third], after[fourth]));
    }

    // A tiny JPEG (its first bytes are enough for the type) and a photo upload, with the device's entry token.
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];

    private static Task<HttpResponseMessage> SendPhoto(HttpClient http, string zone, long id, byte[] bytes, string? entry)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/zones/{zone}/games/{id}/photo") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("image/jpeg");
        if (entry is not null) request.Headers.Add(ZoneHeaders.Entry, entry);
        return http.SendAsync(request);
    }

    [Fact]
    public async Task Photos_off_by_default_then_one_per_game_for_its_author()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var zone = await CreateZone(root, Settings());
        var entry = await Enter(anonymous, zone);
        var photo = $"/api/zones/{zone}/games/{entry.Id}/photo";

        // Off: refused, nothing shown.
        var off = await SendPhoto(anonymous, zone, entry.Id, Jpeg, entry.EditToken);
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Equal(["photosOff"], await Keys(off));
        Assert.False((await Zone(anonymous, zone)).Photos);

        // An admin turns them on (logged); the author adds one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous, HttpMethod.Put, "/api/admin/settings", new SiteSettings(true))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Put, "/api/admin/settings", new SiteSettings(true))).StatusCode);
        Assert.True((await Read<SiteSettings>(await root.GetAsync("/api/admin/settings"))).BlockPhotos);
        Assert.True((await Zone(anonymous, zone)).Photos);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendPhoto(anonymous, zone, entry.Id, Jpeg, null)).StatusCode);     // without its token
        Assert.Equal(["photoType"], await Keys(await SendPhoto(anonymous, zone, entry.Id, "pas une image"u8.ToArray(), entry.EditToken)));
        var tooLarge = await SendPhoto(anonymous, zone, entry.Id, [.. Jpeg, .. new byte[ZoneRules.MaxPhotoBytes]], entry.EditToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendPhoto(anonymous, zone, entry.Id, Jpeg, entry.EditToken)).StatusCode);

        Assert.True(Assert.Single((await Games(anonymous, zone)).Games).Photo);
        var shown = await anonymous.GetAsync(photo);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Equal("image/jpeg", shown.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Jpeg, await shown.Content.ReadAsByteArrayAsync());

        // Turned off: hidden (kept); deleted with its game.
        await Send(root, HttpMethod.Put, "/api/admin/settings", new SiteSettings(false));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(photo)).StatusCode);
        Assert.False(Assert.Single((await Games(anonymous, zone)).Games).Photo);
        await Send(root, HttpMethod.Put, "/api/admin/settings", new SiteSettings(true));
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(photo)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(anonymous, HttpMethod.Delete, $"/api/zones/{zone}/games/{entry.Id}", entry: entry.EditToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(photo)).StatusCode);
        Assert.Contains(await Read<AdminAction[]>(await root.GetAsync("/api/admin/audit")), a => a.Action == "settings");
    }

    [Fact]
    public async Task Games_in_pages_most_recent_first()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var code = await CreateZone(root, Settings());
        var ids = new List<long>();
        for (var i = 0; i < 55; i++) ids.Add((await Enter(anonymous, code, Duel(vpA: i))).Id);

        var first = await Games(anonymous, code);
        Assert.Equal((50, 55), (first.Games.Count, first.Total));
        Assert.Equal(ids[^1], first.Games[0].Id);
        var second = await Games(anonymous, code, first.Next);
        Assert.Equal(5, second.Games.Count);
        Assert.Null(second.Next);
        Assert.Equal(ids[0], second.Games[^1].Id);
    }

    [Fact]
    public async Task Entries_limited_per_address()
    {
        var server = Server(entriesPerHour: 2);
        var root = server.CreateClient();
        // No admin here: the zone is created directly in the store.
        var zones = server.Services.GetService(typeof(Olomoc.Server.Zones.ZoneStore)) as Olomoc.Server.Zones.ZoneStore;
        var zone = await zones!.CreateAsync(z => z.Name = "Club");
        await Enter(root, zone!.Code);
        await Enter(root, zone.Code);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(root, HttpMethod.Post, $"/api/zones/{zone.Code}/games", Duel())).StatusCode);
    }

    [Fact]
    public async Task Admin_overview_counts_the_zones()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var code = await CreateZone(root, Settings());
        await Enter(anonymous, code);
        var overview = await Read<AdminOverview>(await root.GetAsync("/api/admin/overview"));
        Assert.Equal((1, 1, 1), (overview.Zones.Total, overview.Zones.Open, overview.Zones.Games));
    }

    // ---- Tournaments bound to a zone ----

    private static Task<HttpResponseMessage> Organizer(HttpClient http, HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: TournamentJson.Options);
        request.Headers.Add(TournamentHeaders.Organizer, token);
        return http.SendAsync(request);
    }

    /// <summary>A tournament bound to the zone, two players, started, round 1 paired; its table.</summary>
    private static async Task<(TournamentCreated T, Olomoc.Tournaments.Table Table)> TournamentInZone(HttpClient http, string zone)
    {
        var created = await Send(http, HttpMethod.Post, "/api/tournaments",
            new CreateTournamentRequest("Open du club", Today, 3, 1, PairingMode.Swiss, false, Zone: zone));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var t = await Read<TournamentCreated>(created);
        var d = await AppData.GetAsync();
        var cabal = new Olomoc.Services.CabalExporter(d.L).ToModel("Assaut", "MAISEL",
            new Olomoc.Models.Cog?[] { d.Cog("1R01"), d.Cog("2R08"), d.Cog("3R06") },
            new Olomoc.Models.Equipment?[] { d.Equipment("E11"), d.Equipment("D01"), d.Equipment("E03") });
        foreach (var name in new[] { "Ana", "Bea" })
            Assert.Equal(HttpStatusCode.Created, (await Send(http, HttpMethod.Post, $"/api/tournaments/{t.Code}/players",
                new RegistrationRequest(name, "MAISEL", [cabal]))).StatusCode);
        await Organizer(http, HttpMethod.Post, $"/api/tournaments/{t.Code}/start", t.OrganizerToken);
        await Organizer(http, HttpMethod.Post, $"/api/tournaments/{t.Code}/rounds", t.OrganizerToken);
        var view = await Read<TournamentView>(await http.GetAsync($"/api/tournaments/{t.Code}"));
        Assert.Equal((zone, "Club du mardi"), (view.Zone, view.ZoneName));
        return (t, view.Rounds[0].Tables.Single());
    }

    [Fact]
    public async Task Results_of_a_bound_tournament_follow_it_into_the_zone()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var zone = await CreateZone(root, Settings());
        var (t, table) = await TournamentInZone(anonymous, zone);
        var result = $"/api/tournaments/{t.Code}/rounds/1/tables/{table.Number}";
        Assert.Empty((await Games(anonymous, zone)).Games);

        // A result: one duel in the zone, without the names, with the cabal played; not editable from the zone.
        Assert.Equal(HttpStatusCode.NoContent, (await Organizer(anonymous, HttpMethod.Put, result, t.OrganizerToken, new ResultRequest(9, 4))).StatusCode);
        var game = Assert.Single((await Games(root, zone)).Games);
        Assert.Equal((t.Code, "Open du club", false), (game.Tournament, game.TournamentName, game.CanEdit));
        Assert.Equal((9, 4, GameFormat.Duel), (game.Game.A.Vp, game.Game.B.Vp, game.Game.Format));
        Assert.All(game.Game.Players, p => Assert.Null(p.Name));
        Assert.NotNull(game.Game.A.Players[0].Cabal);
        var refused = await Send(root, HttpMethod.Delete, $"/api/zones/{zone}/games/{game.Id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(["zoneGameFromTournament"], await Keys(refused));

        // Corrected: the same game (same place in the list), updated.
        await Organizer(anonymous, HttpMethod.Put, result, t.OrganizerToken, new ResultRequest(3, 8));
        var corrected = Assert.Single((await Games(anonymous, zone)).Games);
        Assert.Equal((game.Id, 3, 8), (corrected.Id, corrected.Game.A.Vp, corrected.Game.B.Vp));

        // Excluded from the statistics: out of the zone; counted again: back.
        await Organizer(anonymous, HttpMethod.Put, $"/api/tournaments/{t.Code}/stats", t.OrganizerToken, new StatsRequest(true));
        Assert.Empty((await Games(anonymous, zone)).Games);
        await Organizer(anonymous, HttpMethod.Put, $"/api/tournaments/{t.Code}/stats", t.OrganizerToken, new StatsRequest(false));
        Assert.Single((await Games(anonymous, zone)).Games);

        // Result cleared, then the tournament deleted: nothing left in the zone.
        await Organizer(anonymous, HttpMethod.Put, result, t.OrganizerToken, new ResultRequest(null, null));
        Assert.Empty((await Games(anonymous, zone)).Games);
        await Organizer(anonymous, HttpMethod.Put, result, t.OrganizerToken, new ResultRequest(5, 5));
        Assert.Equal(1, (await Zone(anonymous, zone)).Stats.Games);
        Assert.Equal(HttpStatusCode.NoContent, (await Organizer(anonymous, HttpMethod.Delete, $"/api/tournaments/{t.Code}", t.OrganizerToken)).StatusCode);
        Assert.Equal(0, (await Zone(anonymous, zone)).Stats.Games);
    }

    [Fact]
    public async Task Statistics_of_everything_count_a_bound_tournament_game_once()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var zone = await CreateZone(root, Settings());
        var hidden = await CreateZone(root, Settings("Essai", hidden: true));
        await Enter(anonymous, zone, Duel("LIBUS", "OTHERS"));
        await Enter(anonymous, hidden, Duel("RUBIN", "URKARAS"));
        var (t, table) = await TournamentInZone(anonymous, zone);
        await Organizer(anonymous, HttpMethod.Put, $"/api/tournaments/{t.Code}/rounds/1/tables/{table.Number}", t.OrganizerToken, new ResultRequest(9, 4));

        // In progress: its game counts through the zone (no archive yet); the hidden zone is left out.
        var all = await Read<StatsView>(await anonymous.GetAsync("/api/stats/all"));
        Assert.Equal((0, 1, 2), (all.Tournaments, all.Zones, all.Games));
        Assert.DoesNotContain(all.FactionWins, w => w.Key == "RUBIN");

        // Finished: through its archive, no longer through the zone; still in each source's own figures.
        Assert.Equal(HttpStatusCode.NoContent, (await Organizer(anonymous, HttpMethod.Post, $"/api/tournaments/{t.Code}/finish", t.OrganizerToken)).StatusCode);
        all = await Read<StatsView>(await anonymous.GetAsync("/api/stats/all"));
        Assert.Equal((1, 1, 2), (all.Tournaments, all.Zones, all.Games));
        Assert.Equal(2 + 2, all.Players);                                   // 2 registered + 2 participations
        Assert.Equal(1, (await Read<StatsView>(await anonymous.GetAsync("/api/stats"))).Games);
        Assert.Equal(2, (await Read<StatsView>(await anonymous.GetAsync("/api/zones/stats"))).Games);
    }

    [Fact]
    public async Task A_closed_zone_is_neither_offered_nor_changed()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var zone = await CreateZone(root, Settings());
        var (t, table) = await TournamentInZone(anonymous, zone);
        await Organizer(anonymous, HttpMethod.Put, $"/api/tournaments/{t.Code}/rounds/1/tables/{table.Number}", t.OrganizerToken, new ResultRequest(9, 4));
        await Send(root, HttpMethod.Put, $"/api/admin/zones/{zone}", Settings(closed: true));

        // Closed: the tournament no longer changes it (its game stays as it was).
        await Organizer(anonymous, HttpMethod.Put, $"/api/tournaments/{t.Code}/rounds/1/tables/{table.Number}", t.OrganizerToken, new ResultRequest(1, 2));
        Assert.Equal(9, Assert.Single((await Games(anonymous, zone)).Games).Game.A.Vp);

        // And a new tournament cannot be bound to it.
        var refused = await Send(anonymous, HttpMethod.Post, "/api/tournaments",
            new CreateTournamentRequest("Trop tard", Today, 3, 1, PairingMode.Swiss, false, Zone: zone));
        Assert.Equal(["zoneClosed"], await Keys(refused));
        Assert.Equal(["zoneNotFound"], await Keys(await Send(anonymous, HttpMethod.Post, "/api/tournaments",
            new CreateTournamentRequest("Nulle part", Today, 3, 1, PairingMode.Swiss, false, Zone: "ABCDEF"))));
    }
}
