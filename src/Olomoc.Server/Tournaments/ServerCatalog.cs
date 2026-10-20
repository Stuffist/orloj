using System.Net;
using Microsoft.Extensions.FileProviders;
using Olomoc.Server.CardCatalog;
using Olomoc.Services;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Référentiel côté serveur, pour valider les listes déposées avec les règles du constructeur :
/// les mêmes services (CatalogService, LocalizationService), alimentés par ce que le site sert
/// aux visiteurs (référentiel publié : <see cref="CatalogStore"/> ; autres fichiers communs)
/// au lieu d'un appel HTTP. Rechargé après chaque modification du référentiel.
/// </summary>
public sealed class ServerCatalog
{
    private readonly IWebHostEnvironment _environment;
    private readonly CatalogStore _store;
    private Lazy<Task<CatalogService>> _catalog;

    public ServerCatalog(IWebHostEnvironment environment, CatalogStore store)
    {
        _environment = environment;
        _store = store;
        _catalog = NewLoader();
        store.Changed += () => _catalog = NewLoader();
    }

    public Task<CatalogService> GetAsync() => _catalog.Value;

    private Lazy<Task<CatalogService>> NewLoader() => new(() => LoadAsync(_environment.WebRootFileProvider, _store));

    private static async Task<CatalogService> LoadAsync(IFileProvider files, CatalogStore store)
    {
        var http = new HttpClient(new FileProviderHandler(files, store)) { BaseAddress = new Uri("http://olomoc.local/") };
        var l = new LocalizationService(http);
        await l.EnsureLoadedAsync();
        var catalog = new CatalogService(http, l);
        await catalog.EnsureLoadedAsync();
        return catalog;
    }

    private sealed class FileProviderHandler(IFileProvider files, CatalogStore store) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/" + CatalogStore.ContentPrefix) && store.Serve(path[(CatalogStore.ContentPrefix.Length + 1)..], drafts: false) is { } served)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(served.Body) { Headers = { ContentType = new("application/json") } },
                });
            var file = files.GetFileInfo(path);
            return Task.FromResult(file.Exists
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(file.CreateReadStream()) { Headers = { ContentType = new("application/json") } } }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
