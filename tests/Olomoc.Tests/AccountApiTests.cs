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

    private WebApplicationFactory<Program> Server(string? admins = null, int accountsPerHour = 1000, string? publicUrl = null)
    {
        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("SESSION_CHECK_SECONDS", "0")
            .UseSetting("RATE_ACCOUNTS_PER_HOUR", accountsPerHour.ToString())
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
        Assert.Null(await Me(http));

        var registered = await Register(http, "Bohdan");
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        Assert.Equal(new AccountView("Bohdan", "bohdan@example.org", false, AccountRole.User), await Me(http));

        Assert.Equal(HttpStatusCode.NoContent, (await Post(http, "/api/account/logout")).StatusCode);
        Assert.Null(await Me(http));

        var wrong = await Login(http, "Bohdan", "mauvais!!");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(["credentials"], await Keys(wrong));
        Assert.Equal(["credentials"], await Keys(await Login(http, "Personne")));

        Assert.Equal(HttpStatusCode.OK, (await Login(http, "bOHDAN")).StatusCode);          // username, case-insensitive
        Assert.Equal("Bohdan", (await Me(http))!.UserName);
        await Post(http, "/api/account/logout");
        Assert.Equal(HttpStatusCode.OK, (await Login(http, "BOHDAN@example.org")).StatusCode);  // or by email
    }

    [Fact]
    public async Task Username_email_and_password_are_checked()
    {
        var server = Server();
        Assert.Equal(HttpStatusCode.OK, (await Register(server.CreateClient(), "Bohdan")).StatusCode);

        var sameName = await Register(server.CreateClient(), "bohdan", "autre@example.org");
        Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
        Assert.Equal(["userNameTaken"], await Keys(sameName));
        Assert.Equal(["emailTaken"], await Keys(await Register(server.CreateClient(), "Autre", "BOHDAN@example.org")));

        var bad = await Register(server.CreateClient(), "a b", "pas-un-email", "court");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(["userName", "email", "password"], await Keys(bad));
        Assert.Equal(["userName"], await Keys(await Register(server.CreateClient(), "ab")));
        Assert.Equal(["userName"], await Keys(await Register(server.CreateClient(), "Élodie")));
        Assert.Equal(["userName"], await Keys(await Register(server.CreateClient(), new string('x', 25))));
    }

    [Fact]
    public async Task Account_creation_limited_per_address()
    {
        var server = Server(accountsPerHour: 2);
        Assert.Equal(HttpStatusCode.OK, (await Register(server.CreateClient(), "Uno")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Register(server.CreateClient(), "Deux")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Register(server.CreateClient(), "Trois")).StatusCode);
    }

    [Fact]
    public async Task Email_confirmed_through_the_link_received()
    {
        var http = Server().CreateClient();
        await Register(http, "Bohdan");
        var (page, user, token) = await MailLink("bohdan@example.org", 1);
        Assert.Equal("confirm", page);

        var bad = await Post(http, "/api/account/confirm", new ConfirmEmailRequest(user, token + "x"));
        Assert.Equal(["invalidLink"], await Keys(bad));
        Assert.False((await Me(http))!.EmailConfirmed);

        // Link opened on another device, without a session: it is enough.
        var other = _servers[0].CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await Post(other, "/api/account/confirm", new ConfirmEmailRequest(user, token))).StatusCode);
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

        // Unknown account or unconfirmed email: same answer, no extra email.
        Assert.Equal(HttpStatusCode.NoContent, (await Post(server.CreateClient(), "/api/account/forgot", new ForgotPasswordRequest("Personne", "fr"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(server.CreateClient(), "/api/account/forgot", forgot)).StatusCode);
        var (_, user, confirmToken) = await MailLink("bohdan@example.org", 1);
        await Post(phone, "/api/account/confirm", new ConfirmEmailRequest(user, confirmToken));

        var computer = server.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await Post(computer, "/api/account/forgot", forgot)).StatusCode);
        var (page, _, token) = await MailLink("bohdan@example.org", 2);
        Assert.Equal("reset", page);
        Assert.Contains("Orloj — new password", Mails("bohdan@example.org")[^1]);       // language of the request

        Assert.Equal(["password"], await Keys(await Post(computer, "/api/account/reset", new ResetPasswordRequest(user, token, "court"))));
        var reset = await Post(computer, "/api/account/reset", new ResetPasswordRequest(user, token, "nouveau mot de passe"));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal("Bohdan", (await Me(computer))!.UserName);       // signed in on this device
        Assert.Null(await Me(phone));                                 // the other sessions are closed

        // Single-use link; old password refused, new one accepted.
        Assert.Equal(["invalidLink"], await Keys(await Post(computer, "/api/account/reset", new ResetPasswordRequest(user, token, "encore un autre"))));
        Assert.Equal(["credentials"], await Keys(await Login(phone, "Bohdan")));
        Assert.Equal(HttpStatusCode.OK, (await Login(phone, "Bohdan", "nouveau mot de passe")).StatusCode);
    }

    [Fact]
    public async Task Three_emails_per_hour_and_per_account()
    {
        var http = Server().CreateClient();
        await Register(http, "Bohdan");                                // 1: confirmation
        var resend = new LanguageRequest("fr");
        Assert.Equal(HttpStatusCode.NoContent, (await Post(http, "/api/account/confirm/send", resend)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(http, "/api/account/confirm/send", resend)).StatusCode);
        var fourth = await Post(http, "/api/account/confirm/send", resend);
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.Equal(["mailQuota"], await Keys(fourth));
        await MailLink("bohdan@example.org", AccountEndpointsMailsPerHour);
    }

    private const int AccountEndpointsMailsPerHour = Olomoc.Server.Accounts.AccountEndpoints.MailsPerHour;

    [Fact]
    public async Task Lockout_after_five_failures()
    {
        var http = Server().CreateClient();
        await Register(http, "Bohdan");
        await Post(http, "/api/account/logout");
        for (var i = 0; i < 5; i++) await Login(http, "Bohdan", "mauvais!!");
        var locked = await Login(http, "Bohdan");                     // even the right password
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        Assert.Equal(["lockedOut"], await Keys(locked));
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
        Assert.Equal(["password"], await Keys(await Post(phone, "/api/account/password", new ChangePasswordRequest("motdepasse", "court"))));
        Assert.NotNull(await Me(computer));

        Assert.Equal(HttpStatusCode.NoContent, (await Post(phone, "/api/account/password", new ChangePasswordRequest("motdepasse", "nouveau mot de passe"))).StatusCode);
        Assert.NotNull(await Me(phone));
        Assert.Null(await Me(computer));
        Assert.Equal(HttpStatusCode.OK, (await Login(computer, "Bohdan", "nouveau mot de passe")).StatusCode);

        // "Sign out the other devices": same effect, without changing the password.
        Assert.Equal(HttpStatusCode.NoContent, (await Post(phone, "/api/account/logout-others")).StatusCode);
        Assert.NotNull(await Me(phone));
        Assert.Null(await Me(computer));
    }

    [Fact]
    public async Task Changing_the_email_requires_confirming_it_again()
    {
        var server = Server();
        var http = server.CreateClient();
        await Register(http, "Bohdan");
        var (_, user, token) = await MailLink("bohdan@example.org", 1);
        await Post(http, "/api/account/confirm", new ConfirmEmailRequest(user, token));
        await Register(server.CreateClient(), "Autre", "autre@example.org");

        Assert.Equal(["wrongPassword"], await Keys(await Post(http, "/api/account/email", new ChangeEmailRequest("neuf@example.org", "mauvais!!", "fr"))));
        Assert.Equal(["emailTaken"], await Keys(await Post(http, "/api/account/email", new ChangeEmailRequest("autre@example.org", "motdepasse", "fr"))));

        var changed = await Post(http, "/api/account/email", new ChangeEmailRequest("neuf@example.org", "motdepasse", "fr"));
        Assert.Equal(new AccountView("Bohdan", "neuf@example.org", false, AccountRole.User), await changed.Content.ReadFromJsonAsync<AccountView>(TournamentJson.Options));
        var (_, _, newToken) = await MailLink("neuf@example.org", 1);
        await Post(http, "/api/account/confirm", new ConfirmEmailRequest(user, newToken));
        Assert.True((await Me(http))!.EmailConfirmed);
    }

    [Fact]
    public async Task Account_deletion_with_its_password()
    {
        var server = Server();
        var http = server.CreateClient();
        var other = server.CreateClient();
        await Register(http, "Bohdan");
        await Login(other, "Bohdan");

        Assert.Equal(["wrongPassword"], await Keys(await Post(http, "/api/account/delete", new DeleteAccountRequest("mauvais!!"))));
        Assert.Equal(HttpStatusCode.NoContent, (await Post(http, "/api/account/delete", new DeleteAccountRequest("motdepasse"))).StatusCode);
        Assert.Null(await Me(http));
        Assert.Null(await Me(other));
        Assert.Equal(["credentials"], await Keys(await Login(http, "Bohdan")));
        Assert.Equal(HttpStatusCode.OK, (await Register(http, "Bohdan")).StatusCode);       // username and email released
    }

    [Fact]
    public async Task Without_a_session_account_operations_are_refused()
    {
        var http = Server().CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, "/api/account/password", new ChangePasswordRequest("a", "b"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, "/api/account/delete", new DeleteAccountRequest("a"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, "/api/admin/accounts/x/reset-password")).StatusCode);
    }

    [Fact]
    public async Task Admin_through_the_variable_and_temporary_password()
    {
        var first = Server();
        var root = first.CreateClient();
        var bob = first.CreateClient();
        await Register(root, "Root");
        await Register(bob, "Bob");
        Assert.False((await Me(root))!.IsAdmin);

        // Restart with the list of admins (username, case-insensitive; "Fantome" does not exist).
        var second = Server(admins: "root; Fantome");
        var rootAgain = second.CreateClient();
        await Login(rootAgain, "Root");
        Assert.True((await Me(rootAgain))!.IsAdmin);

        var bobAgain = second.CreateClient();
        await Login(bobAgain, "Bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(bobAgain, "/api/admin/accounts/Root/reset-password")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rootAgain, "/api/admin/accounts/Personne/reset-password")).StatusCode);

        var reset = await Post(rootAgain, "/api/admin/accounts/bob/reset-password");
        var temporary = (await reset.Content.ReadFromJsonAsync<TemporaryPassword>(TournamentJson.Options))!.Password;
        Assert.Equal(14, temporary.Length);
        Assert.Null(await Me(bobAgain));                                  // Bob's sessions closed
        Assert.Equal(HttpStatusCode.OK, (await Login(bobAgain, "Bob", temporary)).StatusCode);

        // Removed from the list: still admin (the role is in the database; the variable only grants it).
        var third = Server(admins: "");
        var rootLast = third.CreateClient();
        await Login(rootLast, "Root");
        Assert.True((await Me(rootLast))!.IsAdmin);
    }

    private static Task<HttpResponseMessage> SetRole(HttpClient http, string userName, AccountRole role) =>
        http.PutAsync($"/api/admin/accounts/{userName}/role", JsonContent.Create(new RoleRequest(role), options: TournamentJson.Options));

    private static async Task<AccountSummary[]> Accounts(HttpClient http, string search = "") =>
        (await http.GetFromJsonAsync<AccountSummary[]>($"/api/admin/accounts?search={search}", TournamentJson.Options))!;

    [Fact]
    public async Task Roles_in_the_database_player_editor_admin()
    {
        var first = Server();
        foreach (var name in new[] { "Root", "Bob", "Carla", "Carlos" }) await Register(first.CreateClient(), name);
        var server = Server(admins: "Root");
        async Task<HttpClient> As(string name)
        {
            var http = server.CreateClient();
            (await Login(http, name)).EnsureSuccessStatusCode();
            return http;
        }
        var (root, bob, carla) = (await As("Root"), await As("Bob"), await As("Carla"));
        Assert.Equal(AccountRole.Admin, (await Me(root))!.Role);
        Assert.Equal(AccountRole.User, (await Me(bob))!.Role);

        // Player: no roles, no card catalog editor.
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/admin/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetRole(bob, "Bob", AccountRole.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/admin/catalog/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateClient().GetAsync("/api/admin/accounts")).StatusCode);

        // List: without a search, the staff; with one, the usernames that contain it (case-insensitive).
        Assert.Equal([new AccountSummary("Root", AccountRole.Admin)], await Accounts(root));
        Assert.Equal(["Carla", "Carlos"], (await Accounts(root, "carl")).Select(a => a.UserName));

        // Editor: card catalog editor, right away (current session), but not the roles.
        Assert.Equal(HttpStatusCode.OK, (await SetRole(root, "bob", AccountRole.Editor)).StatusCode);
        Assert.Equal(AccountRole.Editor, (await Me(bob))!.Role);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/admin/catalog/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/admin/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetRole(bob, "Carla", AccountRole.Editor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsync("/api/admin/accounts/Carla/reset-password", null)).StatusCode);

        // Admin: never their own role; other admins' roles, yes.
        Assert.Equal(["ownRole"], await Keys(await SetRole(root, "Root", AccountRole.User)));
        Assert.Equal(["unknownUser"], await Keys(await SetRole(root, "Personne", AccountRole.Editor)));
        Assert.Equal(HttpStatusCode.OK, (await SetRole(root, "Carla", AccountRole.Admin)).StatusCode);
        Assert.Equal([new AccountSummary("Carla", AccountRole.Admin), new AccountSummary("Root", AccountRole.Admin),
            new AccountSummary("Bob", AccountRole.Editor)], await Accounts(carla));
        Assert.Equal(HttpStatusCode.OK, (await SetRole(carla, "Root", AccountRole.Editor)).StatusCode);
        Assert.Equal(AccountRole.Editor, (await Me(root))!.Role);
        Assert.Equal(HttpStatusCode.Forbidden, (await root.GetAsync("/api/admin/accounts")).StatusCode);

        // Roles kept after a restart, without the variable (Root is no longer in it: stays editor).
        var again = Server(admins: "");
        var bobAgain = again.CreateClient();
        await Login(bobAgain, "Bob");
        Assert.Equal(AccountRole.Editor, (await Me(bobAgain))!.Role);
        var rootAgain = again.CreateClient();
        await Login(rootAgain, "Root");
        Assert.Equal(AccountRole.Editor, (await Me(rootAgain))!.Role);
    }

    [Fact]
    public async Task Check_email_every_sixty_days()
    {
        var db = await TestDb.OpenAsync(_data);
        var sender = new RecordingSender();
        var check = new Olomoc.Server.Accounts.MailCheck("admin@example.org", sender, db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Olomoc.Server.Accounts.MailCheck>.Instance);
        var day = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(await check.CheckAsync(day));
        Assert.False(await check.CheckAsync(day.AddDays(59)));
        Assert.True(await check.CheckAsync(day.AddDays(60)));
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("admin@example.org", sender.Sent[0].To);

        // Sending failure: nothing recorded, retried at the next check.
        sender.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => check.CheckAsync(day.AddDays(121)));
        sender.Fail = false;
        Assert.True(await check.CheckAsync(day.AddDays(121)));
    }

    [Fact]
    public async Task Check_email_at_startup_not_at_every_restart()
    {
        Server().WithWebHostBuilder(b => b.UseSetting("MAIL_CHECK_TO", "admin@example.org")).CreateClient();
        for (var i = 0; i < 100 && Mails("admin@example.org").Length == 0; i++) await Task.Delay(50);
        Assert.Single(Mails("admin@example.org"));
        Assert.Contains("Orloj — l'envoi des e-mails fonctionne", Mails("admin@example.org")[0]);

        Server().WithWebHostBuilder(b => b.UseSetting("MAIL_CHECK_TO", "admin@example.org")).CreateClient();
        await Task.Delay(1000);
        Assert.Single(Mails("admin@example.org"));
    }

    private sealed class RecordingSender : Olomoc.Server.Accounts.IMailSender
    {
        public List<Olomoc.Server.Accounts.OutgoingMail> Sent { get; } = new();
        public bool Fail { get; set; }

        public Task SendAsync(Olomoc.Server.Accounts.OutgoingMail mail, CancellationToken stop)
        {
            if (Fail) throw new IOException("relay unreachable");
            Sent.Add(mail);
            return Task.CompletedTask;
        }
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
