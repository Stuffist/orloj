using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Tournaments API, in-memory server on a temporary data folder.</summary>
public sealed class TournamentApiTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _servers = new();

    public void Dispose()
    {
        _servers.ForEach(s => s.Dispose());
        TestDb.Delete(_data);
    }

    private HttpClient Server(int readsPerMinute = 1000)
    {
        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("RATE_CREATE_PER_HOUR", "1000")
            .UseSetting("RATE_READS_PER_MINUTE", readsPerMinute.ToString()));
        _servers.Add(server);
        return server.CreateClient();
    }

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options))!;

    private static HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? organizer = null, string? player = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: TournamentJson.Options);
        if (organizer is not null) request.Headers.Add(TournamentHeaders.Organizer, organizer);
        if (player is not null) request.Headers.Add(TournamentHeaders.Player, player);
        return request;
    }

    private static async Task<(CabalExport A, CabalExport B)> MaiselCabals()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(),
                slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        return (Cabal("A", ("3R06", "E03" /* Phéromones */), ("2R08", "D01" /* Carapace */), ("1R01", "E11" /* Harmonium */)),
                Cabal("B", ("3R06", "E03" /* Phéromones */), ("2R11", "E07" /* Ration */), ("1R01", "E08" /* Lumen */)));
    }

    private static async Task<TournamentCreated> Create(HttpClient http, CreateTournamentRequest? request = null)
    {
        var response = await http.PostAsJsonAsync("/api/tournaments",
            request ?? new CreateTournamentRequest("Open de Lyon", new DateOnly(2026, 11, 7), 3, 2), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Read<TournamentCreated>(response);
    }

    private static async Task<PlayerRegistered> Register(HttpClient http, string code, string name, CabalExport a, CabalExport b)
    {
        var response = await http.PostAsJsonAsync($"/api/tournaments/{code}/players",
            new RegistrationRequest(name, "MAISEL", new List<CabalExport> { a, b }), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Read<PlayerRegistered>(response);
    }

    private static async Task<TournamentView> View(HttpClient http, string code, string? organizer = null, string? player = null) =>
        await Read<TournamentView>(await http.SendAsync(Request(HttpMethod.Get, $"/api/tournaments/{code}", organizer: organizer, player: player)));

    [Fact]
    public async Task Tournament_end_to_end()
    {
        var http = Server();
        var (a, b) = await MaiselCabals();
        var t = await Create(http);
        var url = $"/api/tournaments/{t.Code}";

        // Registrations: 3 players; an illegal list is refused with its reasons.
        var ana = await Register(http, t.Code, "Ana", a, b);
        await Register(http, t.Code, "Bo", a, b);
        await Register(http, t.Code, "Cy", a, b);
        var refused = await http.PostAsJsonAsync($"{url}/players",
            new RegistrationRequest("Dan", "MAISEL", new List<CabalExport> { a, b with { Cogs = b.Cogs.Take(1).ToList() } }), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.All((await Read<ProblemList>(refused)).Problems, p => Assert.Equal(("cabalIllegal", 1), (p.Key, p.Cabal)));

        // Lists hidden from the public while registrations are open; visible to the player and the organizer.
        Assert.All((await View(http, t.Code)).Players, p => Assert.Null(p.Cabals));
        var mine = await View(http, t.Code, player: ana.PlayerToken);
        Assert.NotNull(mine.Player(ana.PlayerId)!.Cabals);
        Assert.Null(mine.Players.First(p => p.Id != ana.PlayerId).Cabals);
        Assert.All((await View(http, t.Code, organizer: t.OrganizerToken)).Players, p => Assert.Equal(2, p.Cabals!.Count));

        // Organizer actions: token required.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Post, $"{url}/start", player: ana.PlayerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Post, $"{url}/start", organizer: t.OrganizerToken))).StatusCode);
        Assert.All((await View(http, t.Code)).Players, p => Assert.NotNull(p.Cabals));

        // Round 1: 3 players, one bye; round 2 refused as long as round 1 is not complete.
        Assert.Equal(HttpStatusCode.Created, (await http.SendAsync(Request(HttpMethod.Post, $"{url}/rounds", organizer: t.OrganizerToken))).StatusCode);
        var game = Assert.Single(Assert.Single((await View(http, t.Code)).Rounds).Tables, x => !x.IsBye);
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(Request(HttpMethod.Post, $"{url}/rounds", organizer: t.OrganizerToken))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await http.SendAsync(Request(HttpMethod.Put, $"{url}/rounds/1/tables/{game.Number}", new ResultRequest(12, 5, 0, 1), t.OrganizerToken))).StatusCode);
        var leader = (await View(http, t.Code)).Standings[0];
        Assert.Equal((game.PlayerA, 3, 7), (leader.PlayerId, leader.Points, leader.Diff));

        // Drop, then round 2 between the two remaining players; finish.
        Assert.Equal(HttpStatusCode.NoContent,
            (await http.SendAsync(Request(HttpMethod.Put, $"{url}/players/{game.PlayerB}/dropped", new DroppedRequest(true), t.OrganizerToken))).StatusCode);
        await http.SendAsync(Request(HttpMethod.Post, $"{url}/rounds", organizer: t.OrganizerToken));
        var table2 = (await View(http, t.Code)).Rounds[1].Tables.Single();
        Assert.False(table2.Involves(game.PlayerB!));
        await http.SendAsync(Request(HttpMethod.Put, $"{url}/rounds/2/tables/{table2.Number}", new ResultRequest(8, 8, 1, 1), t.OrganizerToken));
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Post, $"{url}/finish", organizer: t.OrganizerToken))).StatusCode);

        // Export: complete tournament without the secrets.
        var export = await (await http.SendAsync(Request(HttpMethod.Get, $"{url}/export", organizer: t.OrganizerToken))).Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Open de Lyon\"", export);
        Assert.DoesNotContain(Olomoc.Server.Tournaments.Tokens.Hash(t.OrganizerToken), export);
        Assert.DoesNotContain(Olomoc.Server.Tournaments.Tokens.Hash(ana.PlayerToken), export);

        // Statistics: the finished tournament counts.
        var stats = (await http.GetFromJsonAsync<Olomoc.Tournaments.Stats.StatsView>("/api/stats", TournamentJson.Options))!;
        Assert.Equal((1, 3, 2), (stats.Tournaments, stats.Players, stats.Games));
    }

    [Fact]
    public async Task Player_changes_or_withdraws_only_their_own_registration()
    {
        var http = Server();
        var (a, b) = await MaiselCabals();
        var t = await Create(http);
        var url = $"/api/tournaments/{t.Code}/players";
        var ana = await Register(http, t.Code, "Ana", a, b);
        var bo = await Register(http, t.Code, "Bo", a, b);

        var rename = new RegistrationRequest("Ana Lys", "MAISEL", new List<CabalExport> { b, a });
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Put, $"{url}/{ana.PlayerId}", rename, player: bo.PlayerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Put, $"{url}/{ana.PlayerId}", rename, player: ana.PlayerToken))).StatusCode);
        Assert.Equal("Ana Lys", (await View(http, t.Code)).Player(ana.PlayerId)!.Name);

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Delete, $"{url}/{bo.PlayerId}", player: ana.PlayerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Delete, $"{url}/{bo.PlayerId}", player: bo.PlayerToken))).StatusCode);
        Assert.Single((await View(http, t.Code)).Players);
    }

    [Fact]
    public async Task Password_opens_the_administration_on_another_device()
    {
        var http = Server();
        var t = await Create(http, new CreateTournamentRequest("Avec mot de passe", null, 3, 2, "secret-du-club"));
        var url = $"/api/tournaments/{t.Code}";

        var wrong = await http.PostAsJsonAsync($"{url}/organizer/login", new PasswordRequest("devine"), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        var phone = await Read<OrganizerLogin>(await http.PostAsJsonAsync($"{url}/organizer/login", new PasswordRequest("secret-du-club"), TournamentJson.Options));
        Assert.True((await View(http, t.Code, organizer: phone.OrganizerToken)).IsOrganizer);
        var tablet = await Read<OrganizerLogin>(await http.PostAsJsonAsync($"{url}/organizer/login", new PasswordRequest("secret-du-club"), TournamentJson.Options));

        // Change from the phone: it stays open, the tablet is closed, the link stays valid.
        Assert.Equal(HttpStatusCode.NoContent,
            (await http.SendAsync(Request(HttpMethod.Put, $"{url}/organizer/password", new PasswordRequest("nouveau-secret"), phone.OrganizerToken))).StatusCode);
        Assert.True((await View(http, t.Code, organizer: phone.OrganizerToken)).IsOrganizer);
        Assert.False((await View(http, t.Code, organizer: tablet.OrganizerToken)).IsOrganizer);
        Assert.True((await View(http, t.Code, organizer: t.OrganizerToken)).IsOrganizer);

        // Export: no password hash, no sessions.
        var export = await (await http.SendAsync(Request(HttpMethod.Get, $"{url}/export", organizer: t.OrganizerToken))).Content.ReadAsStringAsync();
        Assert.Contains("\"organizerPasswordHash\":\"\"", export);
        Assert.Contains("\"organizerSessionHashes\":[]", export);
    }

    [Fact]
    public async Task Password_attempts_limited()
    {
        var http = Server();
        var t = await Create(http, new CreateTournamentRequest("Open", null, 3, 2, "secret-du-club"));
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
            statuses.Add((await http.PostAsJsonAsync($"/api/tournaments/{t.Code}/organizer/login", new PasswordRequest("devine" + i), TournamentJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task Hidden_standings_for_the_organizer_only()
    {
        var http = Server();
        var (a, b) = await MaiselCabals();
        var t = await Create(http, new CreateTournamentRequest("Rencontre", null, 0, 2, Format: EventFormat.Encounter, AutoRounds: true, HideStandings: true));
        var ana = await Register(http, t.Code, "Ana", a, b);
        await Register(http, t.Code, "Bo", a, b);
        await Register(http, t.Code, "Cy", a, b);
        Assert.Equal(2, (await View(http, t.Code)).RoundCount);                                     // automatic: 3 players, 2 rounds

        var url = $"/api/tournaments/{t.Code}";
        await http.SendAsync(Request(HttpMethod.Post, $"{url}/start", organizer: t.OrganizerToken));
        await http.SendAsync(Request(HttpMethod.Post, $"{url}/rounds", organizer: t.OrganizerToken));
        Assert.Empty((await View(http, t.Code, player: ana.PlayerToken)).Standings);
        Assert.Equal(3, (await View(http, t.Code, organizer: t.OrganizerToken)).Standings.Count);
    }

    [Fact]
    public async Task Tournament_kept_after_a_restart()
    {
        var (a, b) = await MaiselCabals();
        var first = Server();
        var t = await Create(first);
        await Register(first, t.Code, "Ana", a, b);

        var view = await View(Server(), t.Code.ToLowerInvariant());      // case-insensitive code
        Assert.Equal("Ana", Assert.Single(view.Players).Name);
    }

    [Fact]
    public async Task Reads_limited_per_address()
    {
        // Trying codes in bulk: beyond the limit, refused, whether the code exists or not.
        var http = Server(readsPerMinute: 3);
        var t = await Create(http);
        var statuses = new List<HttpStatusCode>();
        foreach (var code in new[] { t.Code, "ZZZZZZ", "YYYYYY", t.Code })
            statuses.Add((await http.GetAsync($"/api/tournaments/{code}")).StatusCode);
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests }, statuses);
    }
}
