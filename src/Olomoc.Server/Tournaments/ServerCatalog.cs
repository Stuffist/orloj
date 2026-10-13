using System.Net;
using Microsoft.Extensions.FileProviders;
using Olomoc.Services;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Référentiel côté serveur, pour valider les listes déposées avec les règles du constructeur :
/// les mêmes services (CatalogService, LocalizationService), alimentés par les fichiers communs
/// que le site sert (/_content/Olomoc.Shared/…) au lieu d'un appel HTTP.
/// </summary>
public sealed class ServerCatalog(IWebHostEnvironment environment)
{
    private readonly Lazy<Task<CatalogService>> _catalog = new(() => LoadAsync(environment.WebRootFileProvider));

    public Task<CatalogService> GetAsync() => _catalog.Value;

    private static async Task<CatalogService> LoadAsync(IFileProvider files)
    {
        var http = new HttpClient(new FileProviderHandler(files)) { BaseAddress = new Uri("http://olomoc.local/") };
        var l = new LocalizationService(http);
        await l.EnsureLoadedAsync();
        var catalog = new CatalogService(http, l);
        await catalog.EnsureLoadedAsync();
        return catalog;
    }

    private sealed class FileProviderHandler(IFileProvider files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var file = files.GetFileInfo(request.RequestUri!.AbsolutePath);
            return Task.FromResult(file.Exists
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(file.CreateReadStream()) { Headers = { ContentType = new("application/json") } } }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
