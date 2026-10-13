using Olomoc.Server.Hosting;
using Xunit;

namespace Olomoc.Tests;

/// <summary>HTTP en clair sur le port HTTPS : adresse https:// de renvoi.</summary>
public class PlainHttpRedirectTests
{
    [Theory]
    [InlineData("GET /builder/play?x=1 HTTP/1.1\r\nHost: vps.example.net:8443\r\n\r\n", "https://vps.example.net:8443/builder/play?x=1")]
    [InlineData("GET / HTTP/1.1\r\nUser-Agent: curl\r\nhost: localhost:8443\r\nAccept: */*\r\n\r\n", "https://localhost:8443/")]
    [InlineData("HEAD /healthz HTTP/1.0\r\nHost: localhost\r\n\r\n", "https://localhost/healthz")]
    [InlineData("GET /x HTTP/1.1\r\nHost: [::1]:8443\r\n\r\n", "https://[::1]:8443/x")]
    public void Meme_adresse_en_https(string head, string location) =>
        Assert.Equal(location, PlainHttpRedirect.Location(head));

    [Theory]
    [InlineData("GET /x HTTP/1.1\r\n\r\n")]                                  // sans Host
    [InlineData("GET x HTTP/1.1\r\nHost: a.net\r\n\r\n")]                   // chemin non absolu
    [InlineData("GET /a b HTTP/1.1\r\nHost: a.net\r\n\r\n")]                // espace dans le chemin
    [InlineData("GET /x HTTP/1.1\r\nHost: a.net/evil\r\n\r\n")]             // hôte invalide
    [InlineData("GET /x HTTP/1.1\r\nHost: a.net\revil\r\n\r\n")]            // retour chariot isolé dans l'hôte
    [InlineData("n'importe quoi")]
    public void Requete_illisible_refusee(string head) =>
        Assert.Null(PlainHttpRedirect.Location(head));
}
