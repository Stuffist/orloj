using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Olomoc.Accounts;
using Olomoc.Models;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Account cabals (/api/account/cabals/sync): for the same name, the most recent one wins.</summary>
public sealed class CabalSyncApiTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-cabals-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _server;
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public CabalSyncApiTests() =>
        _server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("SESSION_CHECK_SECONDS", "0")
            .UseSetting("RATE_ACCOUNTS_PER_HOUR", "1000")
            .UseSetting("RATE_LOGINS_PER_15_MINUTES", "1000")
            .UseSetting("RATE_WRITES_PER_MINUTE", "10000"));

    public void Dispose()
    {
        _server.Dispose();
        TestDb.Delete(_data);
    }

    private static CabalExport Cabal(string name, string cog = "3R06") =>
        new(CabalExport.FormatId, CabalExport.CurrentVersion, "fr", name, new CodeName("MAISEL", "Ligue de Maisel"),
            [new CabalSlot(3, new IdName(cog, "Rouage"), null)]);

    private static CabalChange Save(string name, DateTimeOffset at, string cog = "3R06") => new(name, at, Cabal(name, cog));
    private static CabalChange Delete(string name, DateTimeOffset at) => new(name, at, null);

    /// <summary>New device signed in to the account (created if it does not exist).</summary>
    private async Task<HttpClient> Device(string user = "Bohdan")
    {
        var http = _server.CreateClient();
        var login = await http.PostAsJsonAsync("/api/account/login", new LoginRequest(user, "motdepasse"), TournamentJson.Options);
        if (!login.IsSuccessStatusCode)
            (await http.PostAsJsonAsync("/api/account/register", new RegisterRequest(user, $"{user}@example.org", "motdepasse", "fr"), TournamentJson.Options))
                .EnsureSuccessStatusCode();
        return http;
    }

    private static async Task<CabalSyncResult> Sync(HttpClient http, params CabalChange[] changes)
    {
        var response = await http.PostAsJsonAsync("/api/account/cabals/sync", new CabalSyncRequest(changes), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CabalSyncResult>(TournamentJson.Options))!;
    }

    private static string[] Names(CabalSyncResult r) => r.Cabals.Select(c => c.Cabal.Name!).ToArray();

    [Fact]
    public async Task Public_by_default_offered_by_username_until_made_private()
    {
        var bohdan = await Device();
        await Sync(bohdan, Save("Assaut", T0), Save("Secrète", T0.AddMinutes(1)));

        var anonymous = _server.CreateClient();
        async Task<string[]> Offered(string user) =>
            (await anonymous.GetFromJsonAsync<List<CabalExport>>($"/api/accounts/{user}/cabals", TournamentJson.Options))!.Select(c => c.Name!).ToArray();
        Assert.Equal(["Secrète", "Assaut"], await Offered("bohdan"));          // username: case-insensitive

        var hide = await bohdan.PutAsJsonAsync("/api/account/cabals/visibility", new CabalVisibilityRequest("secrète", false), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, hide.StatusCode);
        Assert.Equal(["Assaut"], await Offered("Bohdan"));
    }

    [Fact]
    public async Task Saved_on_one_device_found_on_the_other()
    {
        var phone = await Device();
        await Sync(phone, Save("Ma cabale", T0), Save("Autre", T0.AddMinutes(1)));
        Assert.Equal(["Autre", "Ma cabale"], Names(await Sync(await Device())));      // most recent first
        Assert.Empty((await Sync(await Device("Autre"))).Cabals);                    // another account sees nothing
    }

    [Fact]
    public async Task For_the_same_name_the_most_recent_wins()
    {
        var phone = await Device();
        var computer = await Device();
        await Sync(phone, Save("Ma cabale", T0.AddHours(2), "3R06"));

        // Older (same name, different case): ignored.
        var older = await Sync(computer, Save("MA CABALE ", T0.AddHours(1), "3R05"));
        Assert.Equal(0, older.Applied);
        Assert.Equal("3R06", older.Cabals.Single().Cabal.Cogs[0].Cog.Id);

        var newer = await Sync(computer, Save("ma cabale", T0.AddHours(3), "3R05"));
        Assert.Equal(1, newer.Applied);
        Assert.Equal("3R05", newer.Cabals.Single().Cabal.Cogs[0].Cog.Id);
    }

    [Fact]
    public async Task Deletion_propagated_and_no_return_of_an_older_version()
    {
        var phone = await Device();
        var computer = await Device();
        await Sync(phone, Save("Ma cabale", T0));
        Assert.Empty((await Sync(computer, Delete("Ma cabale", T0.AddHours(1)))).Cabals);

        // The offline device sends back its old version: the deletion, more recent, holds.
        Assert.Empty((await Sync(phone, Save("Ma cabale", T0.AddMinutes(30)))).Cabals);
        Assert.Single((await Sync(phone, Save("Ma cabale", T0.AddHours(2)))).Cabals);
    }
}
