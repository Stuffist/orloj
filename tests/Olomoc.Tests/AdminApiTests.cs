using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Olomoc.Accounts;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>
/// Admin panel (/api/admin and /api/admin/accounts), in-memory server on a temporary data folder.
/// "Root" is admin (OLOMOC_ADMINS, at startup), "Bob" a player.
/// </summary>
public sealed class AdminApiTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-admin-" + Guid.NewGuid().ToString("N"));
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
            .UseSetting("RATE_CREATE_PER_HOUR", "1000")
            .UseSetting("OLOMOC_ADMINS", admins));
        _servers.Add(server);
        return server;
    }

    private static Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: TournamentJson.Options);
        return http.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string url, object? body = null) => Send(http, HttpMethod.Post, url, body);

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options))!;
    }

    private static async Task<string[]> Keys(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemList>(TournamentJson.Options))!.Problems.Select(p => p.Key).ToArray();

    private static Task<HttpResponseMessage> Login(HttpClient http, string name, string password = "motdepasse") =>
        Post(http, "/api/account/login", new LoginRequest(name, password));

    /// <summary>Root and Bob accounts created, then server restarted with Root as admin; clients signed in.</summary>
    private async Task<(WebApplicationFactory<Program> Server, HttpClient Root, HttpClient Bob)> StartAsync()
    {
        var first = Server();
        foreach (var name in new[] { "Root", "Bob" })
            Assert.Equal(HttpStatusCode.OK, (await Post(first.CreateClient(), "/api/account/register",
                new RegisterRequest(name, $"{name.ToLowerInvariant()}@example.org", "motdepasse", "fr"))).StatusCode);
        var server = Server(admins: "Root");
        var root = server.CreateClient();
        var bob = server.CreateClient();
        await Login(root, "Root");
        await Login(bob, "Bob");
        return (server, root, bob);
    }

    [Fact]
    public async Task Restricted_to_admins()
    {
        var (server, root, bob) = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateClient().GetAsync("/api/admin/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/admin/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bob, HttpMethod.Delete, "/api/admin/accounts/Root")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await root.GetAsync("/api/admin/overview")).StatusCode);
    }

    [Fact]
    public async Task New_admin_link_and_deletion_of_a_tournament()
    {
        var (_, root, bob) = await StartAsync();
        var created = await Read<TournamentCreated>(await bob.PostAsJsonAsync("/api/tournaments",
            new CreateTournamentRequest("Open de Lyon", new DateOnly(2026, 11, 7), 3, 1), TournamentJson.Options));
        var code = created.Code;

        var link = await Read<TournamentCreated>(await Post(root, $"/api/admin/tournaments/{code}/organizer-link"));
        async Task<bool> IsOrganizer(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tournaments/{code}");
            request.Headers.Add(TournamentHeaders.Organizer, token);
            return (await Read<TournamentView>(await bob.SendAsync(request))).IsOrganizer;
        }
        Assert.False(await IsOrganizer(created.OrganizerToken));     // old link: refused
        Assert.True(await IsOrganizer(link.OrganizerToken));

        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Delete, $"/api/admin/tournaments/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/tournaments/{code}")).StatusCode);
    }

    [Fact]
    public async Task Accounts_unlock_and_deletion()
    {
        var (server, root, _) = await StartAsync();

        // Locked after 5 attempts, unlocked by the admin.
        var other = server.CreateClient();
        for (var i = 0; i < 5; i++) await Login(other, "Bob", "mauvais!!");
        Assert.Equal(["lockedOut"], await Keys(await Login(other, "Bob")));
        Assert.Equal(HttpStatusCode.NoContent, (await Post(root, "/api/admin/accounts/bob/unlock")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(other, "Bob")).StatusCode);

        // Deletion: never oneself.
        Assert.Equal(["ownAccount"], await Keys(await Send(root, HttpMethod.Delete, "/api/admin/accounts/Root")));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Delete, "/api/admin/accounts/Bob")).StatusCode);
        Assert.Equal(["credentials"], await Keys(await Login(other, "Bob")));
    }
}
