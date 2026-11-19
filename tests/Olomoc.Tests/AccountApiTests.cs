using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Olomoc.Accounts;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>
/// Accounts API, in-memory server on a temporary data folder; emails written to DATA_DIR/mail (no
/// SMTP). Each HttpClient keeps its cookies: one client = one device.
/// </summary>
public sealed partial class AccountApiTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-accounts-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _servers = new();

    public void Dispose()
    {
        _servers.ForEach(s => s.Dispose());
        TestDb.Delete(_data);
    }

    private WebApplicationFactory<Program> Server(string? admins = null, string? publicUrl = null)
    {
        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("SESSION_CHECK_SECONDS", "0")
            .UseSetting("RATE_ACCOUNTS_PER_HOUR", "1000")
            .UseSetting("RATE_LOGINS_PER_15_MINUTES", "1000")
            .UseSetting("RATE_MAILS_PER_HOUR", "1000")
            .UseSetting("OLOMOC_ADMINS", admins ?? "")
            .UseSetting("PUBLIC_URL", publicUrl ?? ""));
        _servers.Add(server);
        return server;
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string url, object? body = null) =>
        http.PostAsync(url, body is null ? null : JsonContent.Create(body, body.GetType(), options: TournamentJson.Options));

    // Session account, or null (204).
    private static async Task<AccountView?> Me(HttpClient http)
    {
        var response = await http.GetAsync("/api/account/me");
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<AccountView>(TournamentJson.Options);
    }

    private static async Task<HttpResponseMessage> Register(HttpClient http, string name, string? email = null, string password = "motdepasse") =>
        await Post(http, "/api/account/register", new RegisterRequest(name, email ?? $"{name.ToLowerInvariant()}@example.org", password, "fr"));

    private static Task<HttpResponseMessage> Login(HttpClient http, string login, string password = "motdepasse") =>
        Post(http, "/api/account/login", new LoginRequest(login, password));

    private static async Task<string[]> Keys(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemList>(TournamentJson.Options))!.Problems.Select(p => p.Key).ToArray();

    // ---- Emails written to disk ----

    private string[] Mails(string to) =>
        Directory.Exists(Path.Combine(_data, "mail"))
            ? Directory.GetFiles(Path.Combine(_data, "mail")).Order().Select(File.ReadAllText).Where(m => m.StartsWith($"To: {to}\n")).ToArray()
            : [];

    /// <summary>Waits for the n-th email to this address (sent in the background); returns its link (account, token).</summary>
    private async Task<(string Page, string User, string Token)> MailLink(string to, int count)
    {
        for (var i = 0; i < 100 && Mails(to).Length < count; i++) await Task.Delay(50);
        var mails = Mails(to);
        Assert.Equal(count, mails.Length);
        var m = LinkPattern().Match(mails[^1]);
        Assert.True(m.Success, mails[^1]);
        return (m.Groups[1].Value, Uri.UnescapeDataString(m.Groups[2].Value), Uri.UnescapeDataString(m.Groups[3].Value));
    }

    [GeneratedRegex(@"/builder/account/(\w+)#user=([^&\s]+)&token=(\S+)")]
    private static partial Regex LinkPattern();

    // ---- Tests ----

    [Fact]
    public async Task Register_sign_in_sign_out()
    {
        var http = Server().CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Register(http, "Bohdan")).StatusCode);
        Assert.Equal(new AccountView("Bohdan", "bohdan@example.org", false, AccountRole.User), await Me(http));

        Assert.Equal(HttpStatusCode.NoContent, (await Post(http, "/api/account/logout")).StatusCode);
        Assert.Null(await Me(http));

        Assert.Equal(["credentials"], await Keys(await Login(http, "Bohdan", "mauvais!!")));
        Assert.Equal(HttpStatusCode.OK, (await Login(http, "bOHDAN")).StatusCode);          // username, case-insensitive
        await Post(http, "/api/account/logout");
        Assert.Equal(HttpStatusCode.OK, (await Login(http, "BOHDAN@example.org")).StatusCode);  // or by email
    }

    [Fact]
    public async Task Username_email_and_password_are_checked()
    {
        var server = Server();
        Assert.Equal(HttpStatusCode.OK, (await Register(server.CreateClient(), "Bohdan")).StatusCode);
        Assert.Equal(["userNameTaken"], await Keys(await Register(server.CreateClient(), "bohdan", "autre@example.org")));
        Assert.Equal(["emailTaken"], await Keys(await Register(server.CreateClient(), "Autre", "BOHDAN@example.org")));
        Assert.Equal(["userName", "email", "password"], await Keys(await Register(server.CreateClient(), "a b", "pas-un-email", "court")));
    }

    [Fact]
    public async Task Email_confirmed_through_the_link_received()
    {
        var http = Server().CreateClient();
        await Register(http, "Bohdan");
        var (page, user, token) = await MailLink("bohdan@example.org", 1);
        Assert.Equal("confirm", page);
        Assert.Equal(["invalidLink"], await Keys(await Post(http, "/api/account/confirm", new ConfirmEmailRequest(user, token + "x"))));

        // Link opened on another device, without a session: it is enough.
        Assert.Equal(HttpStatusCode.NoContent, (await Post(_servers[0].CreateClient(), "/api/account/confirm", new ConfirmEmailRequest(user, token))).StatusCode);
        Assert.True((await Me(http))!.EmailConfirmed);
    }

    [Fact]
    public async Task Email_links_use_PUBLIC_URL_rather_than_the_request_host()
    {
        var http = Server(publicUrl: "https://orloj.example/").CreateClient();
        http.DefaultRequestHeaders.Host = "attacker.example";
        await Register(http, "Bohdan");
        await MailLink("bohdan@example.org", 1);
        var mail = Assert.Single(Mails("bohdan@example.org"));
        Assert.Contains("https://orloj.example/builder/account/confirm#user=", mail);
        Assert.DoesNotContain("attacker.example", mail);
    }

    [Fact]
    public async Task Forgotten_password_only_for_a_confirmed_email()
    {
        var server = Server();
        var phone = server.CreateClient();
        await Register(phone, "Bohdan");
        var forgot = new ForgotPasswordRequest("bohdan", "en");

        // Unconfirmed email: no extra email.
        Assert.Equal(HttpStatusCode.NoContent, (await Post(server.CreateClient(), "/api/account/forgot", forgot)).StatusCode);
        var (_, user, confirmToken) = await MailLink("bohdan@example.org", 1);
        await Post(phone, "/api/account/confirm", new ConfirmEmailRequest(user, confirmToken));

        var computer = server.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await Post(computer, "/api/account/forgot", forgot)).StatusCode);
        var (page, _, token) = await MailLink("bohdan@example.org", 2);
        Assert.Equal("reset", page);

        Assert.Equal(HttpStatusCode.OK, (await Post(computer, "/api/account/reset", new ResetPasswordRequest(user, token, "nouveau mot de passe"))).StatusCode);
        Assert.Null(await Me(phone));                                 // the other sessions are closed

        // Single-use link; old password refused, new one accepted.
        Assert.Equal(["invalidLink"], await Keys(await Post(computer, "/api/account/reset", new ResetPasswordRequest(user, token, "encore un autre"))));
        Assert.Equal(["credentials"], await Keys(await Login(phone, "Bohdan")));
        Assert.Equal(HttpStatusCode.OK, (await Login(phone, "Bohdan", "nouveau mot de passe")).StatusCode);
    }

    [Fact]
    public async Task Lockout_after_five_failures()
    {
        var http = Server().CreateClient();
        await Register(http, "Bohdan");
        await Post(http, "/api/account/logout");
        for (var i = 0; i < 5; i++) await Login(http, "Bohdan", "mauvais!!");
        Assert.Equal(["lockedOut"], await Keys(await Login(http, "Bohdan")));          // even the right password
    }

    [Fact]
    public async Task Changing_the_password_signs_out_the_other_devices()
    {
        var server = Server();
        var phone = server.CreateClient();
        var computer = server.CreateClient();
        await Register(phone, "Bohdan");
        await Login(computer, "Bohdan");

        Assert.Equal(["wrongPassword"], await Keys(await Post(phone, "/api/account/password", new ChangePasswordRequest("mauvais!!", "nouveau mot de passe"))));
        Assert.Equal(HttpStatusCode.NoContent, (await Post(phone, "/api/account/password", new ChangePasswordRequest("motdepasse", "nouveau mot de passe"))).StatusCode);
        Assert.NotNull(await Me(phone));
        Assert.Null(await Me(computer));
    }

    [Fact]
    public async Task Admin_through_the_variable_and_roles_kept_in_the_database()
    {
        var first = Server();
        await Register(first.CreateClient(), "Root");
        await Register(first.CreateClient(), "Bob");

        // Restart with the list of admins (username, case-insensitive; "Fantome" does not exist).
        var second = Server(admins: "root; Fantome");
        var root = second.CreateClient();
        await Login(root, "Root");
        Assert.True((await Me(root))!.IsAdmin);
        var bob = second.CreateClient();
        await Login(bob, "Bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/admin/accounts")).StatusCode);

        // Editor: card catalog editor, right away, but not the roles.
        var role = JsonContent.Create(new RoleRequest(AccountRole.Editor), options: TournamentJson.Options);
        Assert.Equal(HttpStatusCode.OK, (await root.PutAsync("/api/admin/accounts/bob/role", role)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/admin/catalog/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/admin/accounts")).StatusCode);

        // Temporary password: Bob's sessions closed.
        var reset = await Post(root, "/api/admin/accounts/bob/reset-password");
        var temporary = (await reset.Content.ReadFromJsonAsync<TemporaryPassword>(TournamentJson.Options))!.Password;
        Assert.Null(await Me(bob));
        Assert.Equal(HttpStatusCode.OK, (await Login(bob, "Bob", temporary)).StatusCode);

        // Removed from the list: still admin (the role is in the database; the variable only grants it).
        var third = Server(admins: "");
        var rootLast = third.CreateClient();
        await Login(rootLast, "Root");
        Assert.True((await Me(rootLast))!.IsAdmin);
    }

    [Fact]
    public async Task Session_kept_after_a_restart()
    {
        var first = Server().CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var registered = await Register(first, "Bohdan");
        var cookie = registered.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("olomoc_session="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var again = Server().CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        again.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal("Bohdan", (await Me(again))!.UserName);
    }
}
