using System.Net;
using Olomoc.Models;
using Olomoc.Services;

namespace Olomoc.Tests;

/// <summary>
/// Services de l'app alimentés par les fichiers statiques communs (src/Olomoc.Shared/wwwroot),
/// comme dans le navigateur (HttpClient servi depuis le disque). Langue courante : la langue de référence.
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

    /// <summary>Nouvelle instance, pour les tests qui changent de langue.</summary>
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

    public Cog Cog(string id) => Catalog.CogById(id) ?? throw new ArgumentException($"rouage inconnu : {id}");

    /// <summary>Équipement/don par son nom dans la langue de référence.</summary>
    public Equipment Equipment(string referenceName) =>
        Catalog.Catalog.Equipment.Single(e => L.ReferenceName(e) == referenceName);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Olomoc.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Olomoc.slnx introuvable au-dessus du dossier de test.");
    }

    // Requêtes vers _content/Olomoc.Shared/… (Assets.Base) : fichiers de WwwRoot.
    private sealed class FileHandler(string root) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var relative = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (relative.StartsWith(Assets.Base)) relative = relative[Assets.Base.Length..];
            var path = Path.Combine(root, relative);
            return Task.FromResult(File.Exists(path)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                  {
                      Content = new StreamContent(File.OpenRead(path)) { Headers = { ContentType = new("application/json") } }
                  }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
