using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Olomoc.Accounts;
using Olomoc.Models;
using Olomoc.Server.Tournaments;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>
/// Card catalog editor: repository files + admins' changes (DATA_DIR/catalog), drafts served to
/// admins only, export of the files to commit back.
/// </summary>
public sealed class CatalogEditorApiTests : IDisposable
{
    private const string Content = "/_content/Olomoc.Shared/";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _servers = new();

    public void Dispose()
    {
        _servers.ForEach(s => s.Dispose());
        TestDb.Delete(_data);
    }

    private WebApplicationFactory<Program> Server(string admins = "Root")
    {
        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("SESSION_CHECK_SECONDS", "0")
            .UseSetting("RATE_ACCOUNTS_PER_HOUR", "1000")
            .UseSetting("RATE_LOGINS_PER_15_MINUTES", "1000")
            .UseSetting("RATE_WRITES_PER_MINUTE", "10000")
            .UseSetting("OLOMOC_ADMINS", admins));
        _servers.Add(server);
        return server;
    }

    /// <summary>Server with the admin account Root (created, then role granted at restart) and a Bob account.</summary>
    private async Task<(WebApplicationFactory<Program> Server, HttpClient Admin, HttpClient Player, HttpClient Visitor)> Setup()
    {
        var first = Server();
        foreach (var name in new[] { "Root", "Bob" })
            (await first.CreateClient().PostAsJsonAsync("/api/account/register",
                new RegisterRequest(name, $"{name}@example.org", "motdepasse", "fr"), TournamentJson.Options)).EnsureSuccessStatusCode();
        var server = Server();
        async Task<HttpClient> Login(string name)
        {
            var http = server.CreateClient();
            (await http.PostAsJsonAsync("/api/account/login", new LoginRequest(name, "motdepasse"), TournamentJson.Options)).EnsureSuccessStatusCode();
            return http;
        }
        return (server, await Login("Root"), await Login("Bob"), server.CreateClient());
    }

    private static async Task<JsonObject> Get(HttpClient http, string path) =>
        (await http.GetFromJsonAsync<JsonObject>(Content + path))!;

    private static string[] Ids(JsonObject catalog, string collection) =>
        catalog[collection]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToArray();

    private static string[] Members(JsonObject factions, string code) =>
        factions["factions"]!.AsArray().First(f => f!["code"]!.GetValue<string>() == code)!["cogIds"]!.AsArray().Select(m => m!.GetValue<string>()).ToArray();

    private static Task<HttpResponseMessage> Put(HttpClient http, string url, object body) =>
        http.PutAsync(url, JsonContent.Create(body, body.GetType(), options: TournamentJson.Options));

    private static Cog NewCog(string id = "9R99") => new()
    {
        Id = id, Rank = 2, Ministries = ["HARMONY"], Stats = new Stats { Tech = 3, Melee = 2, Ranged = 1, Dodge = 4 },
        LifeLine = [HealthPointType.S, HealthPointType.S, HealthPointType.T, HealthPointType.S],
        Abilities = [new Ability { Timing = Timing.Active, DarknessCost = 1, Frequencies = [new Frequency { Count = 1, Per = "CYCLE" }] }],
    };

    private static CogEdit NewCogEdit() => new(NewCog(), new()
    {
        ["fr"] = new CogText { Name = "Essai Rouage", Title = "Testeur", Abilities = [new AbilityText { Name = "Essayer", GameEffect = "Rien." }] },
        ["en"] = new CogText { Name = "Test Cog", Title = "Tester", Abilities = [new AbilityText { Name = "Try", GameEffect = "Nothing." }] },
    }, ["MAISEL", "LIBUS"], true);

    private static async Task<string[]> Keys(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemList>(TournamentJson.Options))!.Problems.Select(p => p.Key).ToArray();

    private static int Dodge(JsonObject catalog, string id) =>
        catalog["cogs"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == id)!["stats"]!["dodge"]!.GetValue<int>();

    [Fact]
    public async Task Editor_reserved_to_editors_and_admins()
    {
        var (_, admin, player, visitor) = await Setup();
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/admin/catalog/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(player, "/api/admin/catalog/cogs/9R99", NewCogEdit())).StatusCode);

        // Bob becomes an editor: he saves.
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "/api/admin/accounts/Bob/role", new RoleRequest(AccountRole.Editor))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(player, "/api/admin/catalog/cogs/9R99", NewCogEdit())).StatusCode);
    }

    [Fact]
    public async Task New_cog_as_a_draft_then_published()
    {
        var (server, admin, _, visitor) = await Setup();
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, "/api/admin/catalog/cogs/9R99", NewCogEdit())).StatusCode);

        // Draft: visible to admins only (entry, texts, place in the factions).
        Assert.Contains("9R99", Ids(await Get(admin, "data/olomoc-catalog.json"), "cogs"));
        Assert.Contains("9R99", Members(await Get(admin, "data/olomoc-factions.json"), "LIBUS"));
        Assert.DoesNotContain("9R99", Ids(await Get(visitor, "data/olomoc-catalog.json"), "cogs"));
        Assert.DoesNotContain("9R99", Members(await Get(visitor, "data/olomoc-factions.json"), "MAISEL"));
        Assert.Null((await Get(visitor, "i18n/fr/cards.json"))["cogs"]!["9R99"]);
        Assert.Null((await server.Services.GetRequiredService<ServerCatalog>().GetAsync()).CogById("9R99"));   // registrations

        // Published: visible to all, registrations included.
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, "/api/admin/catalog/cogs/9R99/published", new PublishedRequest(true))).StatusCode);
        Assert.Contains("9R99", Ids(await Get(visitor, "data/olomoc-catalog.json"), "cogs"));
        Assert.NotNull((await server.Services.GetRequiredService<ServerCatalog>().GetAsync()).CogById("9R99"));

        // Deleted (new): disappears; no change left.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/catalog/cogs/9R99")).StatusCode);
        Assert.DoesNotContain("9R99", Ids(await Get(admin, "data/olomoc-catalog.json"), "cogs"));
        Assert.False(Directory.EnumerateFiles(Path.Combine(_data, "catalog"), "*.json", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Existing_cog_changed_then_back_to_the_repository()
    {
        var (_, admin, _, visitor) = await Setup();
        var catalog = (await Get(admin, "data/olomoc-catalog.json")).Deserialize<Catalog>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        var original = catalog.Cogs.First(c => c.Id == "3R06");
        var factions = new[] { "DYKOVIAN", "MAISEL", "RUBIN" };                // those of the repository

        var edited = new CogEdit(new Cog
        {
            Id = original.Id, Rank = original.Rank, Ministries = original.Ministries, Stats = new Stats { Tech = 2, Melee = 2, Ranged = 0, Dodge = 7 },
            LifeLine = original.LifeLine, Abilities = original.Abilities, Art = original.Art, Published = true,
        }, new() { ["en"] = new CogText { Name = "Bohdan Gross", Title = "Official portraitist" } }, [.. factions, "LIBUS"], false);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, "/api/admin/catalog/cogs/3R06", edited)).StatusCode);
        Assert.Equal(7, Dodge(await Get(visitor, "data/olomoc-catalog.json"), "3R06"));
        Assert.Contains("3R06", Members(await Get(visitor, "data/olomoc-factions.json"), "LIBUS"));

        // Back to the repository version.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/catalog/cogs/3R06")).StatusCode);
        Assert.Equal(6, Dodge(await Get(visitor, "data/olomoc-catalog.json"), "3R06"));
        Assert.DoesNotContain("3R06", Members(await Get(visitor, "data/olomoc-factions.json"), "LIBUS"));
        Assert.Empty((await admin.GetFromJsonAsync<CatalogStatus>("/api/admin/catalog/status", TournamentJson.Options))!.Entries.SelectMany(e => e.Value));
    }

    [Fact]
    public async Task Invalid_entries_refused()
    {
        var (_, admin, _, _) = await Setup();
        var edit = NewCogEdit();
        Assert.Equal(["idTaken"], await Keys(await Put(admin, "/api/admin/catalog/cogs/3R06", edit with { Cog = NewCog("3R06") })));
        var bad = new Cog { Id = "9R99", Rank = 4, Ministries = ["NULLE"], LifeLine = [], Abilities = [new Ability { ActionCost = 11 }] };
        Assert.Equal(["rank", "ministries", "lifeLine", "ability"], await Keys(await Put(admin, "/api/admin/catalog/cogs/9R99", edit with { Cog = bad })));
    }

    [Fact]
    public async Task Export_of_the_complete_files_to_commit_back()
    {
        var (_, admin, _, _) = await Setup();
        await Put(admin, "/api/admin/catalog/cogs/9R99", NewCogEdit());
        var response = await admin.GetAsync("/api/admin/catalog/export");
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal(
            ["src/Olomoc.Shared/wwwroot/data/olomoc-catalog.json", "src/Olomoc.Shared/wwwroot/data/olomoc-factions.json",
             "src/Olomoc.Shared/wwwroot/i18n/fr/cards.json", "src/Olomoc.Shared/wwwroot/i18n/en/cards.json"],
            zip.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(zip.GetEntry("src/Olomoc.Shared/wwwroot/data/olomoc-catalog.json")!.Open());
        var text = await reader.ReadToEndAsync();
        Assert.Contains("\"id\": \"9R99\"", text);
        Assert.DoesNotContain("\"published\": false", text);              // draft: field missing
    }
}
