using System.Net;
using Olomoc.Models;
using Olomoc.Services;

namespace Olomoc.Tests;

/// <summary>
/// App services fed with the shared static files (src/Olomoc.Shared/wwwroot), as in the browser
/// (HttpClient served from the disk). Current language: the reference language.
/// </summary>
public sealed class AppData
{
    public static string Root { get; } = FindRoot();
    public static string WwwRoot => Path.Combine(Root, "src", "Olomoc.Shared", "wwwroot");

    private static readonly Lazy<Task<AppData>> Shared = new(CreateAsync);
    public static Task<AppData> GetAsync() => Shared.Value;

    public LocalizationService L { get; }
    public CatalogService Catalog { get; }

    private AppData(LocalizationService l, CatalogService catalog)
    {
        L = l;
        Catalog = catalog;
    }

    /// <summary>New instance, for the tests that change the language.</summary>
    public static async Task<AppData> CreateAsync()
    {
        var http = new HttpClient(new FileHandler(WwwRoot)) { BaseAddress = new Uri("http://local/") };
        var l = new LocalizationService(http);
        await l.EnsureLoadedAsync();
        await l.SetLanguageAsync(LocalizationService.ReferenceLanguage);
        var catalog = new CatalogService(http, l);
        await catalog.EnsureLoadedAsync();
        return new AppData(l, catalog);
    }

    public Cog Cog(string id) => Catalog.CogById(id) ?? throw new ArgumentException($"unknown cog: {id}");

    /// <summary>Equipment/boon by its id (stable, unlike the name, which the editor can change).</summary>
    public Equipment Equipment(string id) => Catalog.EquipmentById(id) ?? throw new ArgumentException($"unknown equipment/boon: {id}");

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CabalExport> _legal = new();

    /// <summary>
    /// A legal cabal of the faction, found in the current catalog (cogs of the faction, then equipment
    /// accepted by the legality check): for tests that need any valid cabal, whatever the errata.
    /// </summary>
    public CabalExport LegalCabal(string faction) => _legal.GetOrAdd(faction, f =>
    {
        var cat = Catalog;
        var none = new Equipment?[3];
        // Cogs of each rank the faction recruits without a composition rule.
        List<Cog> Recruits(int rank) => cat.Catalog.Cogs.Where(c => c.Published && c.Rank == rank)
            .Where(c => !cat.CheckLegality(f, [c, null, null], none).Issues.Any(i => i.Key == "outsideFaction")).ToList();
        var (ones, twos, threes) = (Recruits(1), Recruits(2), Recruits(3));
        foreach (var c3 in threes)
        foreach (var c2 in twos)
        foreach (var c1 in ones)
        {
            Cog?[] cogs = [c1, c2, c3];
            if (cat.CheckLegality(f, cogs, none).Issues.Any(i => i.Key is "duplicateCog" or "composition" or "outsideFaction")) continue;
            // Equipment of each slot: its own, the faction's, or a Ministry of one of the cogs.
            List<Equipment> For(Cog c) => cat.Catalog.Equipment.Where(e => e.Published
                && (e.CogId == c.Id || e.Faction == f || e.Ministry is { } m && cogs.Any(x => x!.Ministries.Contains(m)))).Take(12).ToList();
            foreach (var e1 in For(c1))
            foreach (var e2 in For(c2))
            foreach (var e3 in For(c3))
            {
                Equipment?[] equipment = [e1, e2, e3];
                if (cat.CheckLegality(f, cogs, equipment).IsLegal)
                    return new CabalExporter(L).ToModel("Test", f, cogs, equipment);
            }
        }
        throw new InvalidOperationException($"no legal cabal found for {f}");
    });

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Olomoc.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Olomoc.slnx not found above the test folder.");
    }

    // Requests to /_content/Olomoc.Shared/… (Assets.Base): files of WwwRoot.
    private sealed class FileHandler(string root) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith(Assets.Base)) path = path[Assets.Base.Length..];
            path = Path.Combine(root, path.TrimStart('/'));
            return Task.FromResult(File.Exists(path)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                  {
                      Content = new StreamContent(File.OpenRead(path)) { Headers = { ContentType = new("application/json") } }
                  }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
