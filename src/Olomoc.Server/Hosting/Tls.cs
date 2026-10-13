using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Olomoc.Server.Hosting;

/// <summary>
/// HTTPS du conteneur, servi directement par Kestrel. Actif quand la variable DOMAIN est
/// définie (docker-compose) : HTTPS sur 443 (HTTP en clair reçu sur ce port : renvoyé vers
/// https://), HTTP sur 80 (healthcheck, diagnostic local).
/// Sans DOMAIN (développement) : adresses habituelles (launchSettings, ASPNETCORE_URLS).
/// </summary>
public static class Tls
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var domain = builder.Configuration["DOMAIN"];
        if (string.IsNullOrWhiteSpace(domain)) return;

        var certificates = new CertificateSource(domain, $"/etc/letsencrypt/live/{domain}");
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(80);
            kestrel.ListenAnyIP(443, listen =>
            {
                listen.Use(PlainHttpRedirect.Middleware);   // http://hôte:8443 → https://hôte:8443
                listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificates.Current);
            });
        });
    }
}

/// <summary>
/// Certificat servi : celui de Let's Encrypt de l'hôte (dossier live/ monté en lecture seule),
/// relu dès que certbot le renouvelle (contrôle toutes les 10 minutes, sans redémarrage) ;
/// à défaut, un certificat auto-signé généré au démarrage (poste local : le navigateur avertit).
/// </summary>
public sealed class CertificateSource(string domain, string liveDirectory)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private X509Certificate2? _current;
    private string? _loadedPem;                 // contenu de fullchain.pem du certificat servi
    private DateTime _nextCheck;

    public X509Certificate2 Current
    {
        get
        {
            lock (_lock)
            {
                if (_current is null || DateTime.UtcNow >= _nextCheck) Refresh();
                return _current!;
            }
        }
    }

    private void Refresh()
    {
        _nextCheck = DateTime.UtcNow + CheckInterval;
        var certPath = Path.Combine(liveDirectory, "fullchain.pem");
        var keyPath = Path.Combine(liveDirectory, "privkey.pem");
        try
        {
            if (File.Exists(certPath) && File.Exists(keyPath))
            {
                var pem = File.ReadAllText(certPath);
                if (pem == _loadedPem) return;
                _current = Reimported(X509Certificate2.CreateFromPem(pem, File.ReadAllText(keyPath)));
                _loadedPem = pem;
                Console.WriteLine($"olomoc: certificat {certPath} (expire le {_current.NotAfter:yyyy-MM-dd})");
                return;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Console.Error.WriteLine($"olomoc: certificat {certPath} illisible : {e.Message}");
        }
        if (_current is not null) return;     // on garde le certificat servi jusqu'ici
        Console.Error.WriteLine($"olomoc: aucun certificat dans {liveDirectory} : certificat auto-signé (usage local uniquement)");
        _current = SelfSigned(domain);
    }

    private static X509Certificate2 SelfSigned(string domain)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(domain);
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var now = DateTimeOffset.UtcNow;
        return Reimported(request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1)));
    }

    // Clé privée rattachée de façon durable au certificat (exigé par SslStream sous Windows).
    private static X509Certificate2 Reimported(X509Certificate2 certificate)
    {
        using (certificate) return new X509Certificate2(certificate.Export(X509ContentType.Pkcs12));
    }
}
