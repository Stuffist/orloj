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

    private static ZoneGame Duel(string a = "RUBIN", string b = "MAISEL", int vpA = 7, int vpB = 4) =>
        new(GameFormat.Duel, Today, new ZoneSide([new ZonePlayer(a, "Ana")], vpA), new ZoneSide([new ZonePlayer(b)], vpB));

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
    public async Task Only_admins_create_and_delete_zones()
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
        var teams = new ZoneGame(GameFormat.Teams, Today, new ZoneSide([new("RUBIN"), new("LIBUS")], 5),
            new ZoneSide([new("URKARAS"), new("DYKOVIAN")], 3));
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
        var settings = new ZoneSettingsRequest("Club de Bob (mardi)", "Nouvelle salle", ZoneKind.Club, Today.AddDays(-30), null, false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(carl, HttpMethod.Put, $"/api/zones/{mine}", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous, HttpMethod.Put, $"/api/zones/{mine}", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(bob, HttpMethod.Put, $"/api/zones/{mine}", settings)).StatusCode);
        Assert.Equal("Club de Bob (mardi)", (await Zone(anonymous, mine)).Name);
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
}
