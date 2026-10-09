using System.Net;
using OlomocListBuilder.Models;
using OlomocListBuilder.Services;

namespace OlomocListBuilder.Tests;

/// <summary>
/// Services de l'app alimentés par les fichiers de wwwroot, comme dans le navigateur
/// (HttpClient servi depuis le disque). Langue courante : la langue de référence.
/// </summary>
public sealed class AppData
{
    public static string Root { get; } = FindRoot();
    public static string WwwRoot => Path.Combine(Root, "wwwroot");

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
            if (File.Exists(Path.Combine(dir.FullName, "OlomocListBuilder.csproj")))
                return dir.FullName;
        throw new DirectoryNotFoundException("OlomocListBuilder.csproj introuvable au-dessus du dossier de test.");
    }

    private sealed class FileHandler(string root) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Path.Combine(root, request.RequestUri!.AbsolutePath.TrimStart('/'));
            return Task.FromResult(File.Exists(path)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                  {
                      Content = new StreamContent(File.OpenRead(path)) { Headers = { ContentType = new("application/json") } }
                  }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
