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

    /// <summary>wwwroot of Olomoc.Shared in the repository, whatever the tests' output folder.</summary>
    private static string RepoWebRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Olomoc.Shared", "wwwroot")))
                return Path.Combine(dir.FullName, "src", "Olomoc.Shared", "wwwroot");
        throw new DirectoryNotFoundException("src/Olomoc.Shared/wwwroot");
    }

    private static async Task<JsonObject> Get(HttpClient http, string path) =>
        (await http.GetFromJsonAsync<JsonObject>(Content + path))!;

    private static string[] Ids(JsonObject catalog, string collection) =>
        catalog[collection]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToArray();

    private static string[] Members(JsonObject factions, string code) =>
        factions["factions"]!.AsArray().First(f => f!["code"]!.GetValue<string>() == code)!["cogIds"]!.AsArray().Select(m => m!.GetValue<string>()).ToArray();

    private static Task<HttpResponseMessage> Put(HttpClient http, string url, object body) =>
        http.PutAsync(url, JsonContent.Create(body, body.GetType(), options: TournamentJson.Options));

    private static Cog NewCog(string id = "9R99", bool published = false) => new()
    {
        Id = id, Rank = 2, Ministries = ["HARMONY"], Stats = new Stats { Tech = 3, Melee = 2, Ranged = 1, Dodge = 4 },
        LifeLine = [HealthPointType.S, HealthPointType.S, HealthPointType.T, HealthPointType.S],
        Abilities = [new Ability { Timing = Timing.Active, DarknessCost = 1, Frequencies = [new Frequency { Count = 1, Per = "CYCLE" }] }],
        Published = published,
    };

    private static CogEdit NewCogEdit(bool isNew = true) => new(NewCog(), new()
    {
        ["fr"] = new CogText { Name = "Essai Rouage", Title = "Testeur", Abilities = [new AbilityText { Name = "Essayer", GameEffect = "Rien." }] },
        ["en"] = new CogText { Name = "Test Cog", Title = "Tester", Abilities = [new AbilityText { Name = "Try", GameEffect = "Nothing." }] },
    }, ["MAISEL", "LIBUS"], isNew);

    private static async Task<string[]> Keys(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemList>(TournamentJson.Options))!.Problems.Select(p => p.Key).ToArray();

    [Fact]
    public async Task Without_changes_the_repository_catalog_is_served_as_is()
    {
        var http = Server().CreateClient();
        var root = RepoWebRoot();
        foreach (var path in new[] { "data/olomoc-catalog.json", "data/olomoc-factions.json", "i18n/fr/cards.json", "i18n/en/cards.json" })
        {
            var response = await http.GetAsync(Content + path);
            var served = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Path.Combine(root, path))), served), path);

            // Revalidation: 304 as long as nothing changes.
            var again = new HttpRequestMessage(HttpMethod.Get, Content + path);
            again.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(again)).StatusCode);
        }
    }

    [Fact]
    public async Task Editor_reserved_to_editors_and_admins()
    {
        var (_, admin, player, visitor) = await Setup();
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/admin/catalog/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/api/admin/catalog/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(player, "/api/admin/catalog/cogs/9R99", NewCogEdit())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/api/admin/catalog/export")).StatusCode);

        // Bob becomes an editor: he saves, and sees the drafts (current session).
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "/api/admin/accounts/Bob/role", new RoleRequest(AccountRole.Editor))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(player, "/api/admin/catalog/cogs/9R99", NewCogEdit())).StatusCode);
        Assert.Contains("9R99", Ids(await Get(player, "data/olomoc-catalog.json"), "cogs"));
        Assert.DoesNotContain("9R99", Ids(await Get(visitor, "data/olomoc-catalog.json"), "cogs"));
    }

    [Fact]
    public async Task New_cog_as_a_draft_then_published()
    {
        var (server, admin, player, visitor) = await Setup();
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, "/api/admin/catalog/cogs/9R99", NewCogEdit())).StatusCode);

        // Draft: visible to admins only (entry, texts, place in the factions).
        Assert.Contains("9R99", Ids(await Get(admin, "data/olomoc-catalog.json"), "cogs"));
        Assert.Contains("9R99", Members(await Get(admin, "data/olomoc-factions.json"), "LIBUS"));
        Assert.Equal("Test Cog", (await Get(admin, "i18n/en/cards.json"))["cogs"]!["9R99"]!["name"]!.GetValue<string>());
        foreach (var http in new[] { player, visitor })
        {
            Assert.DoesNotContain("9R99", Ids(await Get(http, "data/olomoc-catalog.json"), "cogs"));
            Assert.DoesNotContain("9R99", Members(await Get(http, "data/olomoc-factions.json"), "MAISEL"));
            Assert.Null((await Get(http, "i18n/fr/cards.json"))["cogs"]!["9R99"]);
        }
        Assert.Null((await server.Services.GetRequiredService<ServerCatalog>().GetAsync()).CogById("9R99"));   // registrations

        // Changes file: the only new entry, in the repository format.
        var overlay = JsonNode.Parse(File.ReadAllText(Path.Combine(_data, "catalog", "data", "olomoc-catalog.json")))!;
        var entry = overlay["cogs"]!.AsArray().Single()!;
        Assert.Equal("9R99", entry["id"]!.GetValue<string>());
        Assert.Null(entry["published"]);                                    // missing: draft
        Assert.Null(entry["baseSize"]);
        Assert.Null(overlay["equipment"]);
        var status = (await admin.GetFromJsonAsync<CatalogStatus>("/api/admin/catalog/status", TournamentJson.Options))!;
        Assert.Equal("new", status.Entries["cogs"]["9R99"]);
        Assert.Equal("modified", status.Entries["factions"]["MAISEL"]);

        // Published: visible to all, registrations included.
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, "/api/admin/catalog/cogs/9R99/published", new PublishedRequest(true))).StatusCode);
        Assert.Contains("9R99", Ids(await Get(visitor, "data/olomoc-catalog.json"), "cogs"));
        Assert.Contains("9R99", Members(await Get(visitor, "data/olomoc-factions.json"), "MAISEL"));
        Assert.True((await Get(visitor, "data/olomoc-catalog.json"))["cogs"]!.AsArray().Last()!["published"]!.GetValue<bool>());
        Assert.NotNull((await server.Services.GetRequiredService<ServerCatalog>().GetAsync()).CogById("9R99"));

        // Deleted (new): disappears, factions included; no change left.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/catalog/cogs/9R99")).StatusCode);
        Assert.DoesNotContain("9R99", Ids(await Get(admin, "data/olomoc-catalog.json"), "cogs"));
        Assert.DoesNotContain("9R99", Members(await Get(admin, "data/olomoc-factions.json"), "MAISEL"));
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

        // Saved again as is: no change kept.
        var same = new CogEdit(original, new() { ["fr"] = null, ["en"] = null }, [.. factions], false);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, "/api/admin/catalog/cogs/3R06", same)).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_data, "catalog", "data")) && File.Exists(Path.Combine(_data, "catalog", "data", "olomoc-catalog.json")));

        // English translation, dodge, and one more faction: served to all (published).
        var edited = new CogEdit(new Cog
        {
            Id = original.Id, Rank = original.Rank, Ministries = original.Ministries, Stats = new Stats { Tech = 2, Melee = 2, Ranged = 0, Dodge = 7 },
            LifeLine = original.LifeLine, Abilities = original.Abilities, Art = original.Art, Published = true,
        }, new() { ["en"] = new CogText { Name = "Bohdan Gross", Title = "Official portraitist" } }, [.. factions, "LIBUS"], false);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, "/api/admin/catalog/cogs/3R06", edited)).StatusCode);
        Assert.Equal(7, (await Get(visitor, "data/olomoc-catalog.json"))["cogs"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == "3R06")!["stats"]!["dodge"]!.GetValue<int>());
        Assert.Equal("Official portraitist", (await Get(visitor, "i18n/en/cards.json"))["cogs"]!["3R06"]!["title"]!.GetValue<string>());
        Assert.Contains("3R06", Members(await Get(visitor, "data/olomoc-factions.json"), "LIBUS"));
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, "/api/admin/catalog/cogs/3R05", new CogEdit(catalog.Cogs.First(c => c.Id == "3R05"),
            new() { ["en"] = new CogText { Title = "Only the English title" } }, ["BLOCKHAUS", "DYKOVIAN", "MAISEL"], false))).StatusCode);
        Assert.Equal("modified", (await admin.GetFromJsonAsync<CatalogStatus>("/api/admin/catalog/status", TournamentJson.Options))!.Entries["cogs"]["3R05"]);  // texts only
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/catalog/cogs/3R05")).StatusCode);
        Assert.Equal("modified", (await admin.GetFromJsonAsync<CatalogStatus>("/api/admin/catalog/status", TournamentJson.Options))!.Entries["cogs"]["3R06"]);

        // Back to the repository version: dodge, English text and factions.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/catalog/cogs/3R06")).StatusCode);
        Assert.Equal(6, (await Get(visitor, "data/olomoc-catalog.json"))["cogs"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == "3R06")!["stats"]!["dodge"]!.GetValue<int>());
        Assert.Null((await Get(visitor, "i18n/en/cards.json"))["cogs"]!["3R06"]);
        Assert.DoesNotContain("3R06", Members(await Get(visitor, "data/olomoc-factions.json"), "LIBUS"));
        Assert.Empty((await admin.GetFromJsonAsync<CatalogStatus>("/api/admin/catalog/status", TournamentJson.Options))!.Entries.SelectMany(e => e.Value));
    }

    [Fact]
    public async Task New_equipment_and_changed_faction()
    {
        var (_, admin, _, visitor) = await Setup();
        var equipment = new EquipmentEdit(new Equipment
        {
            Id = "E99", Kind = EquipmentKind.BOON, Faction = "RUBIN",
            Abilities = [new Ability { Timing = Timing.Permanent }],
            BuildRules = [new BuildRule { Type = BuildRuleType.RECRUIT_MINISTRY, Ministry = "AXIS", Ranks = [1] }],
            Published = true,
        }, new() { ["fr"] = new EquipmentText { Name = "Don d'essai" } }, true);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, "/api/admin/catalog/equipment/E99", equipment)).StatusCode);
        Assert.Contains("E99", Ids(await Get(visitor, "data/olomoc-catalog.json"), "equipment"));        // published from its creation
        Assert.Equal("Don d'essai", (await Get(visitor, "i18n/fr/cards.json"))["equipment"]!["E99"]!["name"]!.GetValue<string>());

        var faction = new FactionEdit(Alignment.DARK, new()
        {
            ["fr"] = new NamedText { Name = "Réseau Rubin" },
            ["en"] = new NamedText { Name = "Rubin Network", Description = "A network." },
        });
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, "/api/admin/catalog/factions/RUBIN", faction)).StatusCode);
        var factions = await Get(visitor, "data/olomoc-factions.json");
        Assert.Equal("DARK", factions["factions"]!.AsArray().First(f => f!["code"]!.GetValue<string>() == "RUBIN")!["alignment"]!.GetValue<string>());
        Assert.Equal("Rubin Network", (await Get(visitor, "i18n/en/cards.json"))["factions"]!["RUBIN"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_entries_refused()
    {
        var (_, admin, _, _) = await Setup();
        var edit = NewCogEdit();
        Assert.Equal(["id"], await Keys(await Put(admin, "/api/admin/catalog/cogs/9R98", edit)));                         // id in the address ≠ id of the cog
        Assert.Equal(["idTaken"], await Keys(await Put(admin, "/api/admin/catalog/cogs/3R06", edit with { Cog = NewCog("3R06") })));
        Assert.Equal(["idTaken"], await Keys(await Put(admin, "/api/admin/catalog/cogs/E11", edit with { Cog = NewCog("E11") })));     // id of an equipment
        Assert.Equal(["unknown"], await Keys(await Put(admin, "/api/admin/catalog/cogs/9R99", edit with { IsNew = false })));
        Assert.Equal(["factions"], await Keys(await Put(admin, "/api/admin/catalog/cogs/9R99", edit with { Factions = ["NULLE"] })));
        Assert.Equal(["name"], await Keys(await Put(admin, "/api/admin/catalog/cogs/9R99", edit with { Texts = new() { ["fr"] = new CogText { Title = "Sans nom" } } })));
        Assert.Equal(["language"], await Keys(await Put(admin, "/api/admin/catalog/cogs/9R99", edit with { Texts = new(edit.Texts) { ["de"] = new CogText() } })));
        var bad = new Cog { Id = "9R99", Rank = 4, Ministries = ["NULLE"], LifeLine = [], Abilities = [new Ability { ActionCost = 11 }] };
        Assert.Equal(["rank", "ministries", "lifeLine", "ability"], await Keys(await Put(admin, "/api/admin/catalog/cogs/9R99", edit with { Cog = bad })));
        var equipment = new EquipmentEdit(new Equipment { Id = "E99", Ministry = "AXIS", Faction = "RUBIN" }, new() { ["fr"] = new EquipmentText { Name = "X" } }, true);
        Assert.Equal(["affiliation"], await Keys(await Put(admin, "/api/admin/catalog/equipment/E99", equipment)));
    }

    [Fact]
    public async Task Changes_merged_into_the_repository_removed_at_startup()
    {
        // As after an export committed back: one entry identical to the repository, another still different.
        var root = RepoWebRoot();
        var repo = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "data/olomoc-catalog.json")))!;
        var same = repo["cogs"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == "3R06")!.DeepClone();
        var different = repo["cogs"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == "3R05")!.DeepClone();
        different["rank"] = 2;
        var file = Path.Combine(_data, "catalog", "data", "olomoc-catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, new JsonObject { ["cogs"] = new JsonArray(same, different) }.ToJsonString());

        var visitor = Server().CreateClient();
        Assert.Equal(2, (await Get(visitor, "data/olomoc-catalog.json"))["cogs"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == "3R05")!["rank"]!.GetValue<int>());
        var kept = JsonNode.Parse(File.ReadAllText(file))!["cogs"]!.AsArray();
        Assert.Equal(["3R05"], kept.Select(c => c!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Export_of_the_complete_files_to_commit_back()
    {
        var (_, admin, _, _) = await Setup();
        await Put(admin, "/api/admin/catalog/cogs/9R99", NewCogEdit());
        var response = await admin.GetAsync("/api/admin/catalog/export");
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal(
            ["src/Olomoc.Shared/wwwroot/data/olomoc-catalog.json", "src/Olomoc.Shared/wwwroot/data/olomoc-factions.json",
             "src/Olomoc.Shared/wwwroot/i18n/fr/cards.json", "src/Olomoc.Shared/wwwroot/i18n/en/cards.json"],
            zip.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(zip.GetEntry("src/Olomoc.Shared/wwwroot/data/olomoc-catalog.json")!.Open());
        var text = await reader.ReadToEndAsync();
        Assert.Contains("\"id\": \"9R99\"", text);
        Assert.DoesNotContain("\"published\": false", text);              // draft: field missing
        Assert.EndsWith("}\n", text);

        // File exported, committed back, redeployed: the change is removed from DATA_DIR/catalog at startup.
        var root = RepoWebRoot();
        var unchanged = File.ReadAllText(Path.Combine(root, "data/olomoc-catalog.json"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(unchanged)!["cogs"], JsonNode.Parse(text)!["cogs"]!.AsArray()
            .Where(c => c!["id"]!.GetValue<string>() != "9R99").Select(c => c!.DeepClone()).Aggregate(new JsonArray(), (a, c) => { a.Add(c); return a; })));
    }
}
