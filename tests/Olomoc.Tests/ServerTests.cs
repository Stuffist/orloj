using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Olomoc.Server.Hosting;
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

    [Theory]
    [InlineData("/builder/")]
    [InlineData("/pairings/t/ABCDEF/admin")]
    [InlineData("/api/stats")]
    public async Task En_tetes_de_securite(string path)
    {
        var response = await Client().GetAsync(path);
        response.EnsureSuccessStatusCode();
        string Header(string name) => string.Join(",", response.Headers.TryGetValues(name, out var v) ? v : []);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Header("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", Header("Content-Security-Policy"));
        Assert.Equal("DENY", Header("X-Frame-Options"));
        Assert.Equal("nosniff", Header("X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header("Referrer-Policy"));
        Assert.Equal("", Header("Strict-Transport-Security"));        // HTTP : pas de HSTS
    }

    [Fact]
    public async Task HSTS_en_HTTPS()
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var response = await http.GetAsync("/healthz");
        Assert.Equal(SecurityHeaders.StrictTransportSecurity, string.Join(",", response.Headers.GetValues("Strict-Transport-Security")));
    }

    [Theory]
    [InlineData("olomoc.test", HttpStatusCode.OK)]
    [InlineData("localhost", HttpStatusCode.OK)]
    [InlineData("127.0.0.1", HttpStatusCode.OK)]                 // healthcheck du conteneur
    [InlineData("autre.example", HttpStatusCode.BadRequest)]
    public async Task Hotes_acceptes_avec_un_domaine(string host, HttpStatusCode status)
    {
        using var server = factory.WithWebHostBuilder(b => b.UseSetting("DOMAIN", "olomoc.test"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = host;
        Assert.Equal(status, (await server.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Healthz() =>
        Assert.Equal("ok\n", await Client().GetStringAsync("/healthz"));
}
