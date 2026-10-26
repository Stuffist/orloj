using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Olomoc.Accounts;
using Olomoc.Online;
using Olomoc.Server.Data;
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

    private static async Task<AccountView?> Me(HttpClient http)
    {
        var response = await http.GetAsync("/api/account/me");
        return response.StatusCode == HttpStatusCode.NoContent ? null : await Read<AccountView>(response);
    }

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
        Assert.True((await Me(root))!.IsAdmin);
        return (server, root, bob);
    }

    private static async Task<TournamentCreated> CreateTournament(HttpClient http, string name = "Open de Lyon") =>
        await Read<TournamentCreated>(await http.PostAsJsonAsync("/api/tournaments",
            new CreateTournamentRequest(name, new DateOnly(2026, 11, 7), 3, 1, PairingMode.Swiss, false), TournamentJson.Options));

    private static async Task<AdminAction[]> Audit(HttpClient http) => await Read<AdminAction[]>(await http.GetAsync("/api/admin/audit"));

    [Fact]
    public async Task Restricted_to_admins()
    {
        var (server, _, bob) = await StartAsync();
        var anonymous = server.CreateClient();
        foreach (var url in new[] { "/api/admin/overview", "/api/admin/tournaments", "/api/admin/online", "/api/admin/audit", "/api/admin/accounts" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bob, HttpMethod.Delete, "/api/admin/tournaments/ABCDEF")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(bob, "/api/admin/maintenance/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bob, HttpMethod.Delete, "/api/admin/accounts/Root")).StatusCode);
    }

    [Fact]
    public async Task Overview()
    {
        var (_, root, bob) = await StartAsync();
        await CreateTournament(bob);
        await CreateTournament(bob, "Coupe");
        await bob.PostAsJsonAsync("/api/online", new CreateOnlineRequest("Ana"), TournamentJson.Options);

        var o = await Read<AdminOverview>(await root.GetAsync("/api/admin/overview"));
        Assert.Equal(2, o.Accounts.Total);
        Assert.Equal(1, o.Accounts.Admins);
        Assert.Equal(0, o.Accounts.Editors);
        Assert.Equal(2, o.Accounts.CreatedLast30Days);
        Assert.Equal(new TournamentMetrics(2, 1000, 2, 0, 0), o.Tournaments);
        Assert.Equal(new OnlineMetrics(1, 2000, 1), o.Online);
        Assert.False(o.MailBySmtp);
        Assert.True(o.DatabaseBytes > 0);

        var tournaments = await Read<AdminTournament[]>(await root.GetAsync("/api/admin/tournaments"));
        Assert.Equal(["Coupe", "Open de Lyon"], tournaments.Select(t => t.Name));       // last modified first
        var games = await Read<AdminOnlineGame[]>(await root.GetAsync("/api/admin/online"));
        Assert.Equal(["Ana", ""], Assert.Single(games).Seats);
    }

    [Fact]
    public async Task New_admin_link_stats_and_deletion_of_a_tournament()
    {
        var (_, root, bob) = await StartAsync();
        var created = await CreateTournament(bob);
        var code = created.Code;

        var link = await Read<TournamentCreated>(await Post(root, $"/api/admin/tournaments/{code}/organizer-link"));
        Assert.Equal(code, link.Code);
        async Task<bool> IsOrganizer(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tournaments/{code}");
            request.Headers.Add(TournamentHeaders.Organizer, token);
            return (await Read<TournamentView>(await bob.SendAsync(request))).IsOrganizer;
        }
        Assert.False(await IsOrganizer(created.OrganizerToken));     // old link: refused
        Assert.True(await IsOrganizer(link.OrganizerToken));

        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Put, $"/api/admin/tournaments/{code}/stats", new StatsRequest(true))).StatusCode);
        Assert.True(Assert.Single(await Read<AdminTournament[]>(await root.GetAsync("/api/admin/tournaments"))).ExcludeFromStats);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Delete, $"/api/admin/tournaments/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/tournaments/{code}")).StatusCode);
        Assert.Equal(["notFound"], await Keys(await Send(root, HttpMethod.Delete, $"/api/admin/tournaments/{code}")));

        Assert.Equal(["deleteTournament", "excludeStats", "organizerLink"], (await Audit(root)).Select(a => a.Action));
        Assert.All(await Audit(root), a => Assert.Equal(("Root", code), (a.Admin, a.Target)));
    }

    [Fact]
    public async Task Deletion_of_an_online_game()
    {
        var (_, root, bob) = await StartAsync();
        var game = await Read<OnlineCreated>(await bob.PostAsJsonAsync("/api/online", new CreateOnlineRequest("Ana"), TournamentJson.Options));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Delete, $"/api/admin/online/{game.Code}")).StatusCode);
        Assert.Empty(await Read<AdminOnlineGame[]>(await root.GetAsync("/api/admin/online")));
        var entry = Assert.Single(await Audit(root));
        Assert.Equal(("deleteOnline", game.Code, "Ana"), (entry.Action, entry.Target, entry.Detail));
    }

    [Fact]
    public async Task Accounts_details_unlock_sign_out_deletion()
    {
        var (server, root, bob) = await StartAsync();

        // Details: never the email.
        var listed = await root.GetAsync("/api/admin/accounts?search=bo");
        Assert.DoesNotContain("@example.org", await listed.Content.ReadAsStringAsync());
        var details = Assert.Single(await Read<AdminAccount[]>(listed));
        Assert.Equal(("Bob", AccountRole.User, false, false), (details.UserName, details.Role, details.EmailConfirmed, details.LockedOut));
        Assert.NotNull(details.LastLoginAt);
        Assert.Equal(["Bob", "Root"], (await Read<AdminAccount[]>(await root.GetAsync("/api/admin/accounts?recent=true"))).Select(a => a.UserName));

        // Locked after 5 attempts, unlocked by the admin.
        var other = server.CreateClient();
        for (var i = 0; i < 5; i++) await Login(other, "Bob", "mauvais!!");
        Assert.Equal(["lockedOut"], await Keys(await Login(other, "Bob")));
        Assert.True(Assert.Single(await Read<AdminAccount[]>(await root.GetAsync("/api/admin/accounts?search=bob"))).LockedOut);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(root, "/api/admin/accounts/bob/unlock")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(other, "Bob")).StatusCode);

        // Sign out everywhere: never oneself.
        Assert.Equal(["ownAccount"], await Keys(await Post(root, "/api/admin/accounts/Root/logout")));
        Assert.Equal(HttpStatusCode.NoContent, (await Post(root, "/api/admin/accounts/Bob/logout")).StatusCode);
        Assert.Null(await Me(bob));
        Assert.Null(await Me(other));

        // Deletion: never oneself nor an admin; the cabals go with the account.
        var db = await TestDb.OpenAsync(_data);
        using (var ctx = db.CreateDbContext())
        {
            var bobId = ctx.Users.Single(u => u.UserName == "Bob").Id;
            ctx.SavedCabals.Add(new SavedCabalRow { UserId = bobId, Name = "A", NormalizedName = "A", SavedAt = DateTime.UtcNow, Json = "{}" });
            ctx.SaveChanges();
        }
        Assert.Equal(1, Assert.Single(await Read<AdminAccount[]>(await root.GetAsync("/api/admin/accounts?search=bob"))).Cabals);
        Assert.Equal(["ownAccount"], await Keys(await Send(root, HttpMethod.Delete, "/api/admin/accounts/Root")));
        Assert.Equal(["unknownUser"], await Keys(await Send(root, HttpMethod.Delete, "/api/admin/accounts/Personne")));
        await Send(root, HttpMethod.Put, "/api/admin/accounts/Bob/role", new RoleRequest(AccountRole.Admin));
        Assert.Equal(["deleteAdmin"], await Keys(await Send(root, HttpMethod.Delete, "/api/admin/accounts/Bob")));
        await Send(root, HttpMethod.Put, "/api/admin/accounts/Bob/role", new RoleRequest(AccountRole.User));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(root, HttpMethod.Delete, "/api/admin/accounts/Bob")).StatusCode);
        Assert.Equal(["credentials"], await Keys(await Login(other, "Bob")));
        using (var ctx = db.CreateDbContext())
            Assert.Empty(ctx.SavedCabals);

        Assert.Equal(["deleteAccount", "role", "role", "logout", "unlock"], (await Audit(root)).Select(a => a.Action));
        Assert.Equal("Admin", (await Audit(root))[2].Detail);
    }

    [Fact]
    public async Task Backup_and_purges()
    {
        var (_, root, _) = await StartAsync();
        var backup = await Read<BackupResult>(await Post(root, "/api/admin/maintenance/backup"));
        Assert.True(File.Exists(Path.Combine(_data, "backups", backup.File)));
        Assert.True(backup.Bytes > 0);
        var again = await Read<BackupResult>(await Post(root, "/api/admin/maintenance/backup"));    // replaces today's
        Assert.Equal(backup.File, again.File);
        var o = await Read<AdminOverview>(await root.GetAsync("/api/admin/overview"));
        Assert.Equal(1, o.Backups);
        Assert.NotNull(o.LastBackup);

        Assert.Equal(0, (await Read<PurgeResult>(await Post(root, "/api/admin/maintenance/purge", new PurgeRequest(PurgeKind.Tournaments)))).Removed);
        Assert.Equal(0, (await Read<PurgeResult>(await Post(root, "/api/admin/maintenance/purge", new PurgeRequest(PurgeKind.Online)))).Removed);
        Assert.Equal(["purgeOnline", "purgeTournaments", "backup", "backup"], (await Audit(root)).Select(a => a.Action));
    }
}
