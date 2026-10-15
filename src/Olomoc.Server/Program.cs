using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Olomoc.Accounts;
using Olomoc.Server.Accounts;
using Olomoc.Server.CardCatalog;
using Olomoc.Server.Data;
using Olomoc.Server.Hosting;
using Olomoc.Server.Online;
using Olomoc.Server.Tournaments;
using Olomoc.Tournaments;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 64 * 1024;    // une inscription pèse ~2 Ko
    k.AddServerHeader = false;                  // pas de « Server: Kestrel »
});

// Données : base SQLite DATA_DIR/olomoc.db (/data dans le conteneur, monté sur ./data), avec ses
// sauvegardes quotidiennes (DATA_DIR/backups).
var dataDirectory = builder.Configuration["DATA_DIR"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDbContextFactory<OlomocDb>(o => OlomocDb.Configure(o, dataDirectory));
builder.Services.AddSingleton(sp => new TournamentStore(sp.GetRequiredService<IDbContextFactory<OlomocDb>>()));
builder.Services.AddSingleton<StatsArchive>();    // jamais purgées
builder.Services.AddHostedService(sp => new DbBackup(dataDirectory, sp.GetRequiredService<ILogger<DbBackup>>()));
// Référentiel : fichiers du dépôt, plus les modifications des admins (DATA_DIR/catalog).
builder.Services.AddSingleton(sp => new CatalogStore(sp.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider,
    Path.Combine(dataDirectory, "catalog"), sp.GetRequiredService<ILogger<CatalogStore>>()));
builder.Services.AddSingleton<ServerCatalog>();
builder.Services.AddHostedService<TournamentPurge>();
builder.Services.ConfigureHttpJsonOptions(o => TournamentJson.Configure(o.SerializerOptions));

// Comptes (ASP.NET Core Identity, mêmes tables que le reste) : session par cookie, chiffré avec
// des clés gardées dans la base (une session survit à un redéploiement).
builder.Services.AddDataProtection().SetApplicationName("Orloj").PersistKeysToDbContext<OlomocDb>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddAuthorization();
builder.Services.AddIdentityCore<OlomocUser>(o =>
    {
        o.User.AllowedUserNameCharacters = AccountRules.UserNameCharacters;
        o.User.RequireUniqueEmail = true;
        // Longueur seule (recommandation du NIST), pas de règles de composition.
        o.Password.RequiredLength = AccountRules.MinPassword;
        o.Password.RequiredUniqueChars = 1;
        o.Password.RequireDigit = o.Password.RequireLowercase = o.Password.RequireUppercase = o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Tokens.EmailConfirmationTokenProvider = AccountEndpoints.ConfirmTokenProvider;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<OlomocDb>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddTokenProvider<EmailConfirmationTokenProvider>(AccountEndpoints.ConfirmTokenProvider);
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));   // mot de passe oublié
// Tampon de sécurité relu toutes les minutes : mot de passe changé, compte supprimé… les autres
// appareils sont déconnectés dans la minute.
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
    o.ValidationInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("SESSION_CHECK_SECONDS", 60)));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "olomoc_session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;                    // jamais envoyé par un autre site (CSRF)
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // Secure en HTTPS (derrière Caddy)
    // 3 mois sans retaper son mot de passe ; renouvelé (3 mois de plus) à une visite passé la moitié.
    o.ExpireTimeSpan = TimeSpan.FromDays(90);
    o.SlidingExpiration = true;
    // API : 401 / 403, pas de redirection vers une page de connexion.
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});

// E-mails (confirmation, mot de passe oublié) : relais SMTP, ou fichiers dans DATA_DIR/mail.
var mailSettings = MailSettings.Read(builder.Configuration);
builder.Services.AddSingleton<IMailSender>(mailSettings.Host is null
    ? new FileMailSender(Path.Combine(dataDirectory, "mail"))
    : new SmtpMailSender(mailSettings));
builder.Services.AddSingleton<MailQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailQueue>());
// Contrôle tous les 60 jours (MAIL_CHECK_TO) : garde la clé SMTP active, montre que l'envoi marche.
builder.Services.AddHostedService(sp => new MailCheck(builder.Configuration["MAIL_CHECK_TO"], sp.GetRequiredService<IMailSender>(),
    sp.GetRequiredService<IDbContextFactory<OlomocDb>>(), sp.GetRequiredService<ILogger<MailCheck>>()));

// Parties en ligne : documents dans la base (sorte « online »), synchronisées par SignalR (WebSocket).
builder.Services.AddSingleton(sp => new OnlineStore(sp.GetRequiredService<IDbContextFactory<OlomocDb>>()));
builder.Services.AddSingleton<OnlinePresence>();
builder.Services.AddHostedService<OnlinePurge>();
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 32 * 1024;   // une commande pèse au plus quelques Ko (état d'un camp)
    o.AddFilter<OnlineRateFilter>();
}).AddJsonProtocol(o => TournamentJson.Configure(o.PayloadSerializerOptions));

// Limites par adresse IP : créations de tournoi, essais de mot de passe, écritures, lectures.
var createPerHour = builder.Configuration.GetValue("RATE_CREATE_PER_HOUR", 10);
var writesPerMinute = builder.Configuration.GetValue("RATE_WRITES_PER_MINUTE", 120);
var loginsPer15Minutes = builder.Configuration.GetValue("RATE_LOGINS_PER_15_MINUTES", 10);
var readsPerMinute = builder.Configuration.GetValue("RATE_READS_PER_MINUTE", 300);
var accountsPerHour = builder.Configuration.GetValue("RATE_ACCOUNTS_PER_HOUR", 5);
var mailsPerHour = builder.Configuration.GetValue("RATE_MAILS_PER_HOUR", 10);
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
    // Comptes : créations, et demandes qui envoient un e-mail (en plus de la limite par compte).
    o.AddPolicy(AccountEndpoints.CreatePolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = accountsPerHour, Window = TimeSpan.FromHours(1) }));
    o.AddPolicy(AccountEndpoints.MailPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = mailsPerHour, Window = TimeSpan.FromHours(1) }));
});

var app = builder.Build();

// Schéma à jour, puis reprise des anciens fichiers JSON (une seule fois).
var dbFactory = app.Services.GetRequiredService<IDbContextFactory<OlomocDb>>();
await OlomocDb.MigrateAsync(dbFactory);
await JsonImport.RunAsync(dbFactory, dataDirectory, app.Logger);
await Olomoc.Server.Accounts.Roles.SeedAsync(app.Services, app.Configuration["OLOMOC_ADMINS"], app.Logger);
await app.Services.GetRequiredService<CatalogStore>().InitializeAsync();
if (mailSettings.Host is null)
    app.Logger.LogWarning("SMTP_HOST non défini : e-mails écrits dans {Directory} au lieu d'être envoyés.", Path.Combine(dataDirectory, "mail"));

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

ClientApps.Map(app);                        // app du navigateur : /_framework, sections /builder, /pairings et /online
app.UseStaticFiles();                       // index.html, fichiers communs : /_content/Olomoc.Shared/…
app.UseRateLimiter();
app.UseAuthentication();                    // après les fichiers : seule l'API lit la session
app.UseAuthorization();

app.MapTournaments();
app.MapAccounts();
app.MapCatalog();                           // avant les fichiers statiques : référentiel fusionné
app.MapOnline();
app.MapGet("/", () => Results.Redirect("/builder/"));
app.MapGet("/healthz", () => "ok\n");
ClientApps.MapFallbacks(app);               // routes de l'app : index.html

app.Run();

/// <summary>Point d'entrée, visible des tests d'intégration (WebApplicationFactory).</summary>
public partial class Program;
