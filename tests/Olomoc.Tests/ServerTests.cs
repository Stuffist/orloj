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
    [InlineData("/builder?x=1", "/builder/?x=1")]
    [InlineData("/pairings", "/pairings/")]
    public async Task Redirections(string path, string location)
    {
        var response = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    // The Block's former address (/zones/…): permanently moved to /bloc/…, the rest kept.
    [Fact]
    public async Task Former_block_address()
    {
        var response = await Client().GetAsync("/zones/ABCDEF/manage?x=1");
        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/bloc/ABCDEF/manage?x=1", response.Headers.Location?.OriginalString);
    }

    // A single app: every section route returns the same index.html (base "/"), the Blazor router takes over.
    [Theory]
    [InlineData("/builder/play")]
    [InlineData("/pairings/t/ABCDEF/admin")]
    [InlineData("/online/g/ABCDEF")]
    public async Task App_sections(string path)
    {
        var response = await Client().GetAsync(path);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<base href=\"/\" />", html);
        Assert.Contains("_framework/blazor.webassembly.js", html);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/_framework/blazor.webassembly.js")]
    [InlineData("/service-worker.js")]
    [InlineData(Assets.Base + "data/olomoc-catalog.json")]
    [InlineData(Assets.Base + "img/cards/E11.jpg")]
    public async Task Served_files(string path) =>
        (await Client().GetAsync(path)).EnsureSuccessStatusCode();

    [Theory]
    [InlineData("/builder/absent.json")]  // missing file: no fallback to index.html
    [InlineData("/autre")]
    public async Task Not_found(string path) =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(path)).StatusCode);

    [Fact]
    public async Task Security_headers()
    {
        var response = await Client().GetAsync("/builder/");
        string Header(string name) => string.Join(",", response.Headers.TryGetValues(name, out var v) ? v : []);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Header("Content-Security-Policy"));
        Assert.Equal("DENY", Header("X-Frame-Options"));
        Assert.Equal("nosniff", Header("X-Content-Type-Options"));
    }

    // Container behind a reverse proxy (docker-compose.yml): accepted names (AllowedHosts).
    [Theory]
    [InlineData("olomoc.test", HttpStatusCode.OK)]
    [InlineData("autre.example", HttpStatusCode.BadRequest)]
    public async Task Hosts_accepted_behind_the_proxy(string host, HttpStatusCode status)
    {
        using var server = factory.WithWebHostBuilder(b => b.UseSetting("AllowedHosts", "olomoc.test;localhost;127.0.0.1"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = host;
        Assert.Equal(status, (await server.CreateClient().SendAsync(request)).StatusCode);
    }
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
