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

    private WebApplicationFactory<Program> Server(string admins = "")
    {
        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("SESSION_CHECK_SECONDS", "0")
            .UseSetting("RATE_ACCOUNTS_PER_HOUR", "1000")
            .UseSetting("RATE_LOGINS_PER_15_MINUTES", "1000")
            .UseSetting("RATE_ZONE_ENTRIES_PER_HOUR", "1000")
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

    private static async Task<ZoneGamesPage> Games(HttpClient http, string code) =>
        await Read<ZoneGamesPage>(await http.GetAsync($"/api/zones/{code}/games"));

    [Fact]
    public async Task Only_zone_admins_create_and_delete_zones()
    {
        var (_, root, bob, _, anonymous) = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous, HttpMethod.Post, "/api/admin/zones", Settings())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bob, HttpMethod.Post, "/api/admin/zones", Settings())).StatusCode);

        var code = await CreateZone(root, Settings());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bob, HttpMethod.Delete, $"/api/admin/zones/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Delete, $"/api/admin/zones/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/zones/{code}")).StatusCode);
    }

    [Fact]
    public async Task Anyone_enters_a_game_and_changes_it_with_the_entry_token()
    {
        var (server, root, _, _, anonymous) = await StartAsync();
        var code = await CreateZone(root, Settings());
        var entry = await Enter(anonymous, code);
        Assert.Equal("RUBIN", (await Zone(anonymous, code)).Dominance.Faction);

        // Another device, without the token: refused; with it: accepted.
        var other = server.CreateClient();
        var changed = Duel(vpA: 2, vpB: 9);
        Assert.Equal(["zoneForbidden"], await Keys(await Send(other, HttpMethod.Put, $"/api/zones/{code}/games/{entry.Id}", changed)));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(other, HttpMethod.Put, $"/api/zones/{code}/games/{entry.Id}", changed, entry.EditToken)).StatusCode);
        Assert.Equal("MAISEL", (await Zone(anonymous, code)).Dominance.Faction);     // statistics computed again

        Assert.Equal(HttpStatusCode.NoContent, (await Send(other, HttpMethod.Delete, $"/api/zones/{code}/games/{entry.Id}", entry: entry.EditToken)).StatusCode);
        Assert.Empty((await Games(anonymous, code)).Games);
    }

    [Fact]
    public async Task Managers_moderate_their_zone_only()
    {
        var (_, root, bob, carl, anonymous) = await StartAsync();
        var mine = await CreateZone(root, Settings("Club de Bob", managers: "Bob"));
        var other = await CreateZone(root, Settings("Autre club"));
        var entry = await Enter(anonymous, mine);
        var elsewhere = await Enter(anonymous, other);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bob, HttpMethod.Delete, $"/api/zones/{other}/games/{elsewhere.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bob, HttpMethod.Delete, $"/api/zones/{mine}/games/{entry.Id}")).StatusCode);

        // Settings: the manager, not another account.
        var settings = new ZoneSettingsRequest("Club de Bob (mardi)", "Nouvelle salle", ZoneKind.Club, Today.AddDays(-30), null, false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(carl, HttpMethod.Put, $"/api/zones/{mine}", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bob, HttpMethod.Put, $"/api/zones/{mine}", settings)).StatusCode);
        Assert.Equal("Club de Bob (mardi)", (await Zone(anonymous, mine)).Name);
    }

    [Fact]
    public async Task Closed_zone_refuses_entries_except_for_its_managers()
    {
        var (_, root, bob, _, anonymous) = await StartAsync();
        var code = await CreateZone(root, Settings(managers: "Bob", closed: true));
        Assert.Equal(["zoneClosed"], await Keys(await Send(anonymous, HttpMethod.Post, $"/api/zones/{code}/games", Duel())));
        await Enter(bob, code);
    }

    [Fact]
    public async Task Hidden_zones_out_of_the_list_and_of_the_overall_statistics()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var shown = await CreateZone(root, Settings("Visible"));
        var hidden = await CreateZone(root, Settings("Essai", hidden: true));
        await Enter(anonymous, shown);
        await Enter(anonymous, hidden, Duel("LIBUS", "OTHERS"));

        Assert.Equal(["Visible"], (await Read<List<ZoneSummary>>(await anonymous.GetAsync("/api/zones"))).Select(z => z.Name));
        Assert.Equal("Essai", (await Zone(anonymous, hidden)).Name);           // reachable by its link
        var all = await Read<StatsView>(await anonymous.GetAsync("/api/zones/stats"));
        Assert.Equal(1, all.Games);
        Assert.DoesNotContain(all.FactionWins, w => w.Key == "LIBUS");
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
        Assert.Equal(["photosOff"], await Keys(await SendPhoto(anonymous, zone, entry.Id, Jpeg, entry.EditToken)));

        // An admin turns them on; the author adds one.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Put, "/api/admin/settings", new SiteSettings(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendPhoto(anonymous, zone, entry.Id, Jpeg, null)).StatusCode);     // without its token
        Assert.Equal(["photoType"], await Keys(await SendPhoto(anonymous, zone, entry.Id, "pas une image"u8.ToArray(), entry.EditToken)));
        var tooLarge = await SendPhoto(anonymous, zone, entry.Id, [.. Jpeg, .. new byte[ZoneRules.MaxPhotoBytes]], entry.EditToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendPhoto(anonymous, zone, entry.Id, Jpeg, entry.EditToken)).StatusCode);

        var shown = await anonymous.GetAsync(photo);
        Assert.Equal("image/jpeg", shown.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Jpeg, await shown.Content.ReadAsByteArrayAsync());

        // Deleted with its game.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(anonymous, HttpMethod.Delete, $"/api/zones/{zone}/games/{entry.Id}", entry: entry.EditToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(photo)).StatusCode);
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
            new CreateTournamentRequest("Open du club", Today, 3, 1, Zone: zone));
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
        return (t, view.Rounds[0].Tables.Single());
    }

    [Fact]
    public async Task Results_of_a_bound_tournament_follow_it_into_the_zone()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var zone = await CreateZone(root, Settings());
        var (t, table) = await TournamentInZone(anonymous, zone);
        var result = $"/api/tournaments/{t.Code}/rounds/1/tables/{table.Number}";

        // A result: one duel in the zone, without the names; not editable from the zone.
        Assert.Equal(HttpStatusCode.NoContent, (await Organizer(anonymous, HttpMethod.Put, result, t.OrganizerToken, new ResultRequest(9, 4))).StatusCode);
        var game = Assert.Single((await Games(root, zone)).Games);
        Assert.Equal((t.Code, 9, 4), (game.Tournament, game.Game.A.Vp, game.Game.B.Vp));
        Assert.All(game.Game.Players, p => Assert.Null(p.Name));
        Assert.Equal(["zoneGameFromTournament"], await Keys(await Send(root, HttpMethod.Delete, $"/api/zones/{zone}/games/{game.Id}")));

        // Corrected: the same game, updated.
        await Organizer(anonymous, HttpMethod.Put, result, t.OrganizerToken, new ResultRequest(3, 8));
        var corrected = Assert.Single((await Games(anonymous, zone)).Games);
        Assert.Equal((game.Id, 3), (corrected.Id, corrected.Game.A.Vp));

        // Tournament deleted: nothing left in the zone.
        Assert.Equal(HttpStatusCode.NoContent, (await Organizer(anonymous, HttpMethod.Delete, $"/api/tournaments/{t.Code}", t.OrganizerToken)).StatusCode);
        Assert.Empty((await Games(anonymous, zone)).Games);
    }

    [Fact]
    public async Task Statistics_of_everything_count_a_bound_tournament_game_once()
    {
        var (_, root, _, _, anonymous) = await StartAsync();
        var zone = await CreateZone(root, Settings());
        await Enter(anonymous, zone, Duel("LIBUS", "OTHERS"));
        var (t, table) = await TournamentInZone(anonymous, zone);
        await Organizer(anonymous, HttpMethod.Put, $"/api/tournaments/{t.Code}/rounds/1/tables/{table.Number}", t.OrganizerToken, new ResultRequest(9, 4));

        // In progress: its game counts through the zone (no archive yet).
        var all = await Read<StatsView>(await anonymous.GetAsync("/api/stats/all"));
        Assert.Equal((0, 2), (all.Tournaments, all.Games));

        // Finished: through its archive, no longer through the zone.
        Assert.Equal(HttpStatusCode.NoContent, (await Organizer(anonymous, HttpMethod.Post, $"/api/tournaments/{t.Code}/finish", t.OrganizerToken)).StatusCode);
        all = await Read<StatsView>(await anonymous.GetAsync("/api/stats/all"));
        Assert.Equal((1, 2), (all.Tournaments, all.Games));
    }

    [Fact]
    public async Task Statistics_reset_by_an_admin_count_only_what_comes_after_until_cancelled()
    {
        var (_, root, _, carl, anonymous) = await StartAsync();
        var zone = await CreateZone(root, Settings());
        await Enter(anonymous, zone, Duel());
        async Task<StatsView> All() => await Read<StatsView>(await anonymous.GetAsync("/api/stats/all"));

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(carl, HttpMethod.Post, "/api/admin/stats/reset")).StatusCode);
        Assert.NotNull((await Read<SiteSettings>(await Send(root, HttpMethod.Post, "/api/admin/stats/reset"))).StatsSince);

        // Reset: nothing counts any more, the zone keeps its games.
        Assert.Equal(0, (await All()).Games);
        Assert.Single((await Games(anonymous, zone)).Games);
        await Enter(anonymous, zone, Duel("LIBUS", "OTHERS"));
        Assert.Equal(1, (await All()).Games);

        // Cancelled: everything counts again.
        Assert.Null((await Read<SiteSettings>(await Send(root, HttpMethod.Delete, "/api/admin/stats/reset"))).StatsSince);
        Assert.Equal(2, (await All()).Games);
    }

    // ---- Data export ----

    [Fact]
    public async Task Editors_export_the_data_behind_the_statistics_without_names()
    {
        var (_, root, bob, carl, anonymous) = await StartAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await carl.GetAsync("/api/admin/export")).StatusCode);
        await Send(root, HttpMethod.Put, "/api/admin/accounts/Bob/role", new RoleRequest(AccountRole.Editor));

        var zone = await CreateZone(root, Settings());
        await Enter(anonymous, zone, Duel("LIBUS", "OTHERS"));

        var response = await bob.GetAsync("/api/admin/export");
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(["cards.csv", "data.json", "games.csv", "players.csv", "tournaments.csv", "zones.csv"], archive.Entries.Select(e => e.FullName).Order());
        using var reader = new StreamReader(archive.GetEntry("data.json")!.Open());
        Assert.DoesNotContain("Ana", await reader.ReadToEndAsync());
    }
}
