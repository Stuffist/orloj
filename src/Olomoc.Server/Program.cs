using System.Threading.RateLimiting;
using Olomoc.Server.Hosting;
using Olomoc.Server.Tournaments;
using Olomoc.Tournaments;

var builder = WebApplication.CreateBuilder(args);
Tls.Configure(builder);                     // conteneur (DOMAIN défini) : HTTPS 443 + HTTP 80
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 64 * 1024;    // une inscription pèse ~2 Ko
    k.AddServerHeader = false;                  // pas de « Server: Kestrel »
});

// Tournois : fichiers JSON dans DATA_DIR (/data dans le conteneur, monté sur ./data).
var dataDirectory = builder.Configuration["DATA_DIR"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");
builder.Services.AddSingleton(new TournamentStore(Path.Combine(dataDirectory, "tournaments")));
builder.Services.AddSingleton(new StatsArchive(Path.Combine(dataDirectory, "stats")));    // jamais purgées
builder.Services.AddSingleton<ServerCatalog>();
builder.Services.AddHostedService<TournamentPurge>();
builder.Services.ConfigureHttpJsonOptions(o => TournamentJson.Configure(o.SerializerOptions));

// Limites par adresse IP : créations de tournoi, essais de mot de passe, écritures, lectures.
var createPerHour = builder.Configuration.GetValue("RATE_CREATE_PER_HOUR", 10);
var writesPerMinute = builder.Configuration.GetValue("RATE_WRITES_PER_MINUTE", 120);
var loginsPer15Minutes = builder.Configuration.GetValue("RATE_LOGINS_PER_15_MINUTES", 10);
var readsPerMinute = builder.Configuration.GetValue("RATE_READS_PER_MINUTE", 300);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(TournamentEndpoints.CreatePolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = createPerHour, Window = TimeSpan.FromHours(1) }));
    // Essais de mot de passe organisateur : peu nombreux, pour décourager les essais en série.
    o.AddPolicy(TournamentEndpoints.LoginPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = loginsPer15Minutes, Window = TimeSpan.FromMinutes(15) }));
    o.AddPolicy(TournamentEndpoints.WritePolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = writesPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Lectures : large pour un usage normal (une page de tournoi relit toutes les 30 s ; toute une
    // salle peut partager une adresse IP), mais décourage l'essai de codes en série.
    o.AddPolicy(TournamentEndpoints.ReadPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = readsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Tournois terminés avant les statistiques (ou dont l'archive manque) : archivés au démarrage.
await app.Services.GetRequiredService<StatsArchive>().BackfillAsync(app.Services.GetRequiredService<TournamentStore>());

if (app.Environment.IsDevelopment()) app.UseWebAssemblyDebugging();

// Revalidation à chaque chargement : un redéploiement (données, traductions) est vu aussitôt ;
// les fichiers inchangés répondent 304, et Blazor met en cache _framework par empreinte.
app.Use((ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    return next();
});
app.UseSecurityHeaders();

ClientApps.Map(app, "builder");
ClientApps.Map(app, "pairings");
app.UseStaticFiles();                       // fichiers communs : /_content/Olomoc.Shared/…
app.UseRateLimiter();

app.MapTournaments();
app.MapGet("/", () => Results.Redirect("/builder/"));
app.MapGet("/healthz", () => "ok\n");

app.Run();

/// <summary>Point d'entrée, visible des tests d'intégration (WebApplicationFactory).</summary>
public partial class Program;
