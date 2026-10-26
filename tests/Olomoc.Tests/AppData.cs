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
