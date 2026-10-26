using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Olomoc.Server.Hosting;
using Xunit;

namespace Olomoc.Tests;

/// <summary>The site's host, started in memory: redirects, the app and its sections, shared files.</summary>
public class ServerTests(TempDataServer factory) : IClassFixture<TempDataServer>
{
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/", "/builder/")]
    [InlineData("/builder", "/builder/")]
    [InlineData("/builder?x=1", "/builder/?x=1")]
    [InlineData("/pairings", "/pairings/")]
    [InlineData("/online", "/online/")]
    public async Task Redirections(string path, string location)
    {
        var response = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    // A single app: every section route returns the same index.html (base "/"), the Blazor router
    // takes over, without reloading from one section to another.
    [Theory]
    [InlineData("/builder/")]
    [InlineData("/builder/play")]
    [InlineData("/builder/equipment")]
    [InlineData("/pairings/")]
    [InlineData("/pairings/stats")]
    [InlineData("/pairings/t/ABCDEF/admin")]
    [InlineData("/online/")]
    [InlineData("/online/g/ABCDEF")]
    public async Task App_sections(string path)
    {
        var response = await Client().GetAsync(path);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<base href=\"/\" />", html);
        Assert.Contains("_framework/blazor.webassembly.js", html);
        Assert.Contains($"<link rel=\"manifest\" href=\"{Assets.Base}manifest.webmanifest\" />", html);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    // Installable app: the site's manifest, with the type browsers expect.
    [Fact]
    public async Task Manifest()
    {
        var response = await Client().GetAsync(Assets.Base + "manifest.webmanifest");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        using var manifest = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("/builder/", manifest.RootElement.GetProperty("start_url").GetString());
        foreach (var icon in manifest.RootElement.GetProperty("icons").EnumerateArray())
            (await Client().GetAsync(Assets.Base + icon.GetProperty("src").GetString())).EnsureSuccessStatusCode();
    }

    // Service worker at the site root: its default scope ("/") covers the app and /_content/.
    [Fact]
    public async Task Service_worker_at_the_root()
    {
        var response = await Client().GetAsync("/service-worker.js");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/_framework/blazor.webassembly.js")]
    [InlineData(Assets.Base + "js/olomoc.js")]
    [InlineData(Assets.Base + "data/olomoc-catalog.json")]
    [InlineData(Assets.Base + "i18n/fr/ui.json")]
    [InlineData(Assets.Base + "css/app.css")]
    [InlineData(Assets.Base + "img/icons/darkness.png")]
    [InlineData(Assets.Base + "img/cards/E11.jpg")]
    [InlineData(Assets.Base + "img/emblems/factions/MAISEL.png")]
    [InlineData(Assets.Base + "img/emblems/ministries/AXIS.png")]
    [InlineData(Assets.Base + "img/app/favicon-32.png")]
    [InlineData(Assets.Base + "img/app/apple-touch-icon.png")]
    [InlineData("/builder/cards")]
    public async Task Served_files(string path) =>
        (await Client().GetAsync(path)).EnsureSuccessStatusCode();

    [Theory]
    [InlineData("/builder/absent.json")]  // missing file: no fallback to index.html
    [InlineData("/autre")]
    public async Task Not_found(string path) =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(path)).StatusCode);

    [Theory]
    [InlineData("/builder/")]
    [InlineData("/pairings/t/ABCDEF/admin")]
    [InlineData("/api/stats")]
    public async Task Security_headers(string path)
    {
        var response = await Client().GetAsync(path);
        response.EnsureSuccessStatusCode();
        string Header(string name) => string.Join(",", response.Headers.TryGetValues(name, out var v) ? v : []);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Header("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", Header("Content-Security-Policy"));
        Assert.Equal("DENY", Header("X-Frame-Options"));
        Assert.Equal("nosniff", Header("X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header("Referrer-Policy"));
        Assert.Equal("", Header("Strict-Transport-Security"));        // HTTP: no HSTS
    }

    [Fact]
    public async Task HSTS_over_HTTPS()
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var response = await http.GetAsync("/healthz");
        Assert.Equal(SecurityHeaders.StrictTransportSecurity, string.Join(",", response.Headers.GetValues("Strict-Transport-Security")));
    }

    // Container behind a reverse proxy (docker-compose.yml): accepted names (AllowedHosts) and X-Forwarded-*
    // headers read (ASPNETCORE_FORWARDEDHEADERS_ENABLED).
    private WebApplicationFactory<Program> BehindProxy() => factory.WithWebHostBuilder(b => b
        .UseSetting("AllowedHosts", "olomoc.test;localhost;127.0.0.1")
        .UseSetting("FORWARDEDHEADERS_ENABLED", "true"));

    [Theory]
    [InlineData("olomoc.test", HttpStatusCode.OK)]
    [InlineData("localhost", HttpStatusCode.OK)]
    [InlineData("127.0.0.1", HttpStatusCode.OK)]                 // container healthcheck
    [InlineData("autre.example", HttpStatusCode.BadRequest)]
    public async Task Hosts_accepted_behind_the_proxy(string host, HttpStatusCode status)
    {
        using var server = BehindProxy();
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = host;
        Assert.Equal(status, (await server.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Proxy_HTTPS_recognized()
    {
        using var server = BehindProxy();
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = "olomoc.test";
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        var response = await server.CreateClient().SendAsync(request);
        Assert.Equal(SecurityHeaders.StrictTransportSecurity, string.Join(",", response.Headers.GetValues("Strict-Transport-Security")));
    }

    [Fact]
    public async Task Proxy_headers_ignored_by_default()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await Client().SendAsync(request);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Healthz() =>
        Assert.Equal("ok\n", await Client().GetStringAsync("/healthz"));
}

/// <summary>Site started in memory on a temporary data folder (not the development one).</summary>
public sealed class TempDataServer : WebApplicationFactory<Program>
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-server-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("DATA_DIR", _data);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        TestDb.Delete(_data);
    }
}
