using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Hôte du site, démarré en mémoire : redirections, apps sous leur chemin, fichiers communs.</summary>
public class ServerTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/", "/builder/")]
    [InlineData("/builder", "/builder/")]
    [InlineData("/builder?x=1", "/builder/?x=1")]
    [InlineData("/pairings", "/pairings/")]
    public async Task Redirections(string path, string location)
    {
        var response = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/builder/", "/builder/")]
    [InlineData("/builder/play", "/builder/")]          // route de l'app : index.html, le routeur Blazor prend le relais
    [InlineData("/pairings/", "/pairings/")]
    [InlineData("/pairings/t/ABCDEF/admin", "/pairings/")]
    public async Task Apps_sous_leur_chemin(string path, string baseHref)
    {
        var response = await Client().GetAsync(path);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"<base href=\"{baseHref}\" />", html);
        Assert.Contains("_framework/blazor.webassembly.js", html);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/builder/_framework/blazor.webassembly.js")]
    [InlineData("/pairings/_framework/blazor.webassembly.js")]
    [InlineData(Assets.Base + "js/olomoc.js")]
    [InlineData(Assets.Base + "data/olomoc-catalog.json")]
    [InlineData(Assets.Base + "i18n/fr/ui.json")]
    [InlineData(Assets.Base + "css/app.css")]
    [InlineData(Assets.Base + "img/icons/darkness.png")]
    [InlineData(Assets.Base + "img/cards/E11.jpg")]
    [InlineData("/builder/cards")]
    public async Task Fichiers_servis(string path) =>
        (await Client().GetAsync(path)).EnsureSuccessStatusCode();

    [Theory]
    [InlineData("/builder/absent.json")]  // fichier absent : pas de repli sur index.html
    [InlineData("/autre")]
    public async Task Introuvable(string path) =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(path)).StatusCode);

    [Fact]
    public async Task Healthz() =>
        Assert.Equal("ok\n", await Client().GetStringAsync("/healthz"));
}
