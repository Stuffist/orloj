using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Olomoc.Accounts;
using Olomoc.Server.Accounts;
using Olomoc.Server.Admin;
using Olomoc.Server.CardCatalog;
using Olomoc.Server.Data;
using Olomoc.Server.Hosting;
using Olomoc.Server.Online;
using Olomoc.Server.Tournaments;
using Olomoc.Tournaments;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 64 * 1024;    // a registration weighs ~2 KB
    k.AddServerHeader = false;                  // no "Server: Kestrel"
});

// Data: SQLite database DATA_DIR/olomoc.db (/data in the container, mounted on ./data), with its
// daily backups (DATA_DIR/backups).
var dataDirectory = builder.Configuration["DATA_DIR"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDbContextFactory<OlomocDb>(o => OlomocDb.Configure(o, dataDirectory));
builder.Services.AddSingleton(sp => new TournamentStore(sp.GetRequiredService<IDbContextFactory<OlomocDb>>()));
builder.Services.AddSingleton<StatsArchive>();    // never purged
builder.Services.AddSingleton(sp => new DbBackup(dataDirectory, sp.GetRequiredService<ILogger<DbBackup>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<DbBackup>());   // also on demand (admin panel)
// Card catalog: repository files, plus the admins' changes (DATA_DIR/catalog).
builder.Services.AddSingleton(sp => new CatalogStore(sp.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider,
    Path.Combine(dataDirectory, "catalog"), sp.GetRequiredService<ILogger<CatalogStore>>()));
builder.Services.AddSingleton<ServerCatalog>();
builder.Services.AddSingleton<AdminAudit>();      // log of the admins' actions
builder.Services.AddHostedService<TournamentPurge>();
builder.Services.ConfigureHttpJsonOptions(o => TournamentJson.Configure(o.SerializerOptions));

// Accounts (ASP.NET Core Identity, same tables as everything else): cookie session, encrypted with
// keys kept in the database (a session survives a redeployment).
builder.Services.AddDataProtection().SetApplicationName("Orloj").PersistKeysToDbContext<OlomocDb>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddAuthorization();
builder.Services.AddIdentityCore<OlomocUser>(o =>
    {
        o.User.AllowedUserNameCharacters = AccountRules.UserNameCharacters;
        o.User.RequireUniqueEmail = true;
        // Length only (NIST recommendation), no composition rules.
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
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));   // forgotten password
// Security stamp re-read every minute: password changed, account deleted… other devices are signed
// out within a minute.
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
    o.ValidationInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("SESSION_CHECK_SECONDS", 60)));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "olomoc_session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;                    // never sent by another site (CSRF)
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // Secure over HTTPS (behind the reverse proxy)
    // 3 months without typing the password again; renewed (3 more months) on a visit past halfway.
    o.ExpireTimeSpan = TimeSpan.FromDays(90);
    o.SlidingExpiration = true;
    // API: 401 / 403, no redirection to a login page.
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});

// Emails (confirmation, forgotten password): SMTP relay, or files in DATA_DIR/mail.
var mailSettings = MailSettings.Read(builder.Configuration);
builder.Services.AddSingleton<IMailSender>(mailSettings.Host is null
    ? new FileMailSender(Path.Combine(dataDirectory, "mail"))
    : new SmtpMailSender(mailSettings));
builder.Services.AddSingleton<MailQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailQueue>());
// Check every 60 days (MAIL_CHECK_TO): keeps the SMTP key active, shows that sending works.
builder.Services.AddHostedService(sp => new MailCheck(builder.Configuration["MAIL_CHECK_TO"], sp.GetRequiredService<IMailSender>(),
    sp.GetRequiredService<IDbContextFactory<OlomocDb>>(), sp.GetRequiredService<ILogger<MailCheck>>()));

// Online games: documents in the database (kind "online"), synchronized by SignalR (WebSocket).
builder.Services.AddSingleton(sp => new OnlineStore(sp.GetRequiredService<IDbContextFactory<OlomocDb>>()));
builder.Services.AddSingleton<OnlinePresence>();
builder.Services.AddHostedService<OnlinePurge>();
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 32 * 1024;   // a command weighs a few KB at most (state of one side)
    o.AddFilter<OnlineRateFilter>();
}).AddJsonProtocol(o => TournamentJson.Configure(o.PayloadSerializerOptions));

// Limits per IP address: tournament creations, password attempts, writes, reads.
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
    // Organizer password attempts: few, to discourage brute force.
    o.AddPolicy(TournamentEndpoints.LoginPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = loginsPer15Minutes, Window = TimeSpan.FromMinutes(15) }));
    o.AddPolicy(TournamentEndpoints.WritePolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = writesPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Reads: generous for normal use (a tournament page reloads every 30 s; a whole venue can share
    // one IP address), but discourages trying codes in bulk.
    o.AddPolicy(TournamentEndpoints.ReadPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = readsPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Accounts: creations, and requests that send an email (on top of the per-account limit).
    o.AddPolicy(AccountEndpoints.CreatePolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = accountsPerHour, Window = TimeSpan.FromHours(1) }));
    o.AddPolicy(AccountEndpoints.MailPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "", _ => new() { PermitLimit = mailsPerHour, Window = TimeSpan.FromHours(1) }));
});

var app = builder.Build();

// Schema up to date, then import of the old JSON files (only once).
var dbFactory = app.Services.GetRequiredService<IDbContextFactory<OlomocDb>>();
await OlomocDb.MigrateAsync(dbFactory);
await JsonImport.RunAsync(dbFactory, dataDirectory, app.Logger);
await Olomoc.Server.Accounts.Roles.SeedAsync(app.Services, app.Configuration["OLOMOC_ADMINS"], app.Logger);
await app.Services.GetRequiredService<CatalogStore>().InitializeAsync();
if (mailSettings.Host is null)
    app.Logger.LogWarning("SMTP_HOST not set: emails written to {Directory} instead of being sent.", Path.Combine(dataDirectory, "mail"));

// Tournaments finished before the statistics existed (or whose archive is missing): archived at startup.
await app.Services.GetRequiredService<StatsArchive>().BackfillAsync(app.Services.GetRequiredService<TournamentStore>());

if (app.Environment.IsDevelopment()) app.UseWebAssemblyDebugging();

// Revalidation on every load: a redeployment (data, translations) is seen at once; unchanged files
// answer 304, and Blazor caches _framework by fingerprint.
app.Use((ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    return next();
});
app.UseSecurityHeaders();

ClientApps.Map(app);                        // browser app: /_framework, /builder, /pairings and /online sections
app.UseStaticFiles();                       // index.html, shared files: /_content/Olomoc.Shared/…
app.UseRateLimiter();
app.UseAuthentication();                    // after the files: only the API reads the session
app.UseAuthorization();

app.MapTournaments();
app.MapAccounts();
app.MapCatalog();                           // before the static files: merged card catalog
app.MapOnline();
app.MapAdmin(dataDirectory);                // admin panel (admins)
app.MapGet("/", () => Results.Redirect("/builder/"));
app.MapGet("/healthz", () => "ok\n");
ClientApps.MapFallbacks(app);               // app routes: index.html

app.Run();

/// <summary>Entry point, visible to the integration tests (WebApplicationFactory).</summary>
public partial class Program;
