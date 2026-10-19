using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Olomoc.Accounts;
using Olomoc.Server.Data;
using Olomoc.Server.Tournaments;
using Olomoc.Tournaments;

namespace Olomoc.Server.Accounts;

/// <summary>
/// API des comptes (/api/account). Session par cookie (HttpOnly, SameSite=Strict) ; mots de
/// passe, verrouillage, jetons des liens et tampon de sécurité gérés par ASP.NET Core Identity.
/// L'e-mail ne sert qu'à récupérer le compte : confirmation, puis mot de passe oublié.
/// </summary>
public static class AccountEndpoints
{
    public const string CreatePolicy = "account-create", MailPolicy = "account-mail";
    public const string AdminRole = "Admin";
    public const string ConfirmTokenProvider = "EmailConfirmation";

    /// <summary>E-mails envoyés au plus par heure et par compte (confirmation, mot de passe oublié).</summary>
    public const int MailsPerHour = 3;

    public static void MapAccounts(this WebApplication app)
    {
        var api = app.MapGroup("/api/account");
        api.MapGet("me", Me).RequireRateLimiting(TournamentEndpoints.ReadPolicy);
        api.MapPost("register", Register).RequireRateLimiting(CreatePolicy);
        api.MapPost("login", Login).RequireRateLimiting(TournamentEndpoints.LoginPolicy);
        api.MapPost("logout", Logout).RequireRateLimiting(TournamentEndpoints.WritePolicy);
        api.MapPost("forgot", Forgot).RequireRateLimiting(MailPolicy);
        api.MapPost("reset", Reset).RequireRateLimiting(TournamentEndpoints.LoginPolicy);
        api.MapPost("confirm", Confirm).RequireRateLimiting(TournamentEndpoints.LoginPolicy);

        // Compte connecté.
        var mine = api.MapGroup("").RequireAuthorization();
        mine.MapPost("logout-others", LogoutOthers).RequireRateLimiting(TournamentEndpoints.WritePolicy);
        mine.MapPost("password", ChangePassword).RequireRateLimiting(TournamentEndpoints.LoginPolicy);
        mine.MapPost("email", ChangeEmail).RequireRateLimiting(MailPolicy);
        mine.MapPost("confirm/send", SendConfirmation).RequireRateLimiting(MailPolicy);
        mine.MapPost("delete", Delete).RequireRateLimiting(TournamentEndpoints.LoginPolicy);
        mine.MapPost("cabals/sync", CabalSyncEndpoint.Sync).RequireRateLimiting(TournamentEndpoints.WritePolicy);

        // Admin : mot de passe temporaire pour un compte dont l'e-mail ne permet pas la récupération.
        app.MapPost("/api/admin/accounts/{userName}/reset-password", AdminReset)
            .RequireAuthorization().RequireRateLimiting(TournamentEndpoints.WritePolicy);
    }

    // ---- Sans compte ----

    /// <summary>Compte de la session ; 204 sans session.</summary>
    private static async Task<IResult> Me(HttpContext ctx, UserManager<OlomocUser> users) =>
        await users.GetUserAsync(ctx.User) is { } user ? Results.Json(await View(user, users), TournamentJson.Options) : Results.NoContent();

    private static async Task<IResult> Register(RegisterRequest body, HttpContext ctx, UserManager<OlomocUser> users,
        SignInManager<OlomocUser> signIn, MailQueue mail, IConfiguration config)
    {
        var name = body.UserName?.Trim();
        var email = body.Email?.Trim();
        var problems = new List<Problem>();
        if (!AccountRules.IsValidUserName(name))
            problems.Add(Problem.Of("userName", AccountRules.MinUserName.ToString(), AccountRules.MaxUserName.ToString()));
        if (!AccountRules.IsValidEmail(email)) problems.Add(Problem.Of("email"));
        if (!AccountRules.IsValidPassword(body.Password)) problems.Add(Problem.Of("password", AccountRules.MinPassword.ToString()));
        if (problems.Count > 0) return Problems(problems, 400);
        if (await users.Users.CountAsync() >= config.GetValue("ACCOUNT_MAX", 10_000)) return Fail("accountsFull", 503);

        var now = DateTime.UtcNow;
        var user = new OlomocUser { UserName = name, Email = email, CreatedAt = now, LastLoginAt = now };
        var created = await users.CreateAsync(user, body.Password);
        if (!created.Succeeded) return Problems(Describe(created), 409);

        await QueueConfirmationAsync(ctx, user, users, mail, body.Language);
        await signIn.SignInAsync(user, isPersistent: true);
        return Results.Json(await View(user, users), TournamentJson.Options);
    }

    private static async Task<IResult> Login(LoginRequest body, UserManager<OlomocUser> users, SignInManager<OlomocUser> signIn)
    {
        if (await FindAsync(users, body.Login) is not { } user) return Fail("credentials", 400);
        var check = await signIn.CheckPasswordSignInAsync(user, body.Password ?? "", lockoutOnFailure: true);
        if (check.IsLockedOut) return Fail("lockedOut", 403);
        if (!check.Succeeded) return Fail("credentials", 400);

        user.LastLoginAt = DateTime.UtcNow;
        await users.UpdateAsync(user);
        await signIn.SignInAsync(user, isPersistent: true);
        return Results.Json(await View(user, users), TournamentJson.Options);
    }

    private static async Task<IResult> Logout(SignInManager<OlomocUser> signIn)
    {
        await signIn.SignOutAsync();
        return Results.NoContent();
    }

    // Même réponse que le compte existe ou non, que l'e-mail parte ou non (pas de divulgation).
    private static async Task<IResult> Forgot(ForgotPasswordRequest body, HttpContext ctx, UserManager<OlomocUser> users, MailQueue mail)
    {
        if (await FindAsync(users, body.Login) is { EmailConfirmed: true, Email: { } email } user && await CountMailAsync(user, users))
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            mail.Enqueue(AccountMails.Reset(email, user.UserName!, Link(ctx, "reset", user, token), body.Language));
        }
        return Results.NoContent();
    }

    /// <summary>Nouveau mot de passe (lien reçu) : les autres sessions sont fermées, celle-ci ouverte.</summary>
    private static async Task<IResult> Reset(ResetPasswordRequest body, UserManager<OlomocUser> users, SignInManager<OlomocUser> signIn)
    {
        if (!AccountRules.IsValidPassword(body.Password)) return Fail("password", 400, AccountRules.MinPassword.ToString());
        if (await users.FindByIdAsync(body.User ?? "") is not { } user) return Fail("invalidLink", 400);
        var reset = await users.ResetPasswordAsync(user, body.Token ?? "", body.Password);
        if (!reset.Succeeded) return Problems(Describe(reset), 400);

        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = DateTime.UtcNow;
        await users.UpdateAsync(user);
        await signIn.SignInAsync(user, isPersistent: true);
        return Results.Json(await View(user, users), TournamentJson.Options);
    }

    private static async Task<IResult> Confirm(ConfirmEmailRequest body, UserManager<OlomocUser> users)
    {
        if (await users.FindByIdAsync(body.User ?? "") is not { } user) return Fail("invalidLink", 400);
        if (user.EmailConfirmed) return Results.NoContent();
        var confirmed = await users.ConfirmEmailAsync(user, body.Token ?? "");
        return confirmed.Succeeded ? Results.NoContent() : Problems(Describe(confirmed), 400);
    }

    // ---- Compte connecté ----

    /// <summary>Ferme les sessions des autres appareils (celle-ci reste ouverte).</summary>
    private static Task<IResult> LogoutOthers(HttpContext ctx, UserManager<OlomocUser> users, SignInManager<OlomocUser> signIn) =>
        WithUser(ctx, users, async user =>
        {
            await users.UpdateSecurityStampAsync(user);
            await signIn.RefreshSignInAsync(user);
            return Results.NoContent();
        });

    private static Task<IResult> ChangePassword(ChangePasswordRequest body, HttpContext ctx, UserManager<OlomocUser> users,
        SignInManager<OlomocUser> signIn) =>
        WithUser(ctx, users, async user =>
        {
            if (!AccountRules.IsValidPassword(body.NewPassword)) return Fail("password", 400, AccountRules.MinPassword.ToString());
            var changed = await users.ChangePasswordAsync(user, body.CurrentPassword ?? "", body.NewPassword);
            if (!changed.Succeeded) return Problems(Describe(changed), 400);
            await signIn.RefreshSignInAsync(user);       // nouveau tampon : les autres appareils sont déconnectés
            return Results.NoContent();
        });

    private static Task<IResult> ChangeEmail(ChangeEmailRequest body, HttpContext ctx, UserManager<OlomocUser> users,
        SignInManager<OlomocUser> signIn, MailQueue mail) =>
        WithUser(ctx, users, async user =>
        {
            var email = body.Email?.Trim();
            if (!AccountRules.IsValidEmail(email)) return Fail("email", 400);
            if (!await users.CheckPasswordAsync(user, body.Password ?? "")) return Fail("wrongPassword", 400);
            if (!string.Equals(users.NormalizeEmail(email), user.NormalizedEmail, StringComparison.Ordinal))
            {
                var set = await users.SetEmailAsync(user, email);      // à confirmer de nouveau
                if (!set.Succeeded) return Problems(Describe(set), 409);
                await signIn.RefreshSignInAsync(user);
                await QueueConfirmationAsync(ctx, user, users, mail, body.Language);
            }
            return Results.Json(await View(user, users), TournamentJson.Options);
        });

    private static Task<IResult> SendConfirmation(LanguageRequest body, HttpContext ctx, UserManager<OlomocUser> users, MailQueue mail) =>
        WithUser(ctx, users, async user =>
        {
            if (user.EmailConfirmed) return Results.NoContent();
            return await QueueConfirmationAsync(ctx, user, users, mail, body.Language)
                ? Results.NoContent() : Fail("mailQuota", 429, MailsPerHour.ToString());
        });

    private static Task<IResult> Delete(DeleteAccountRequest body, HttpContext ctx, UserManager<OlomocUser> users,
        SignInManager<OlomocUser> signIn) =>
        WithUser(ctx, users, async user =>
        {
            if (!await users.CheckPasswordAsync(user, body.Password ?? "")) return Fail("wrongPassword", 400);
            var deleted = await users.DeleteAsync(user);
            if (!deleted.Succeeded) return Problems(Describe(deleted), 500);
            await signIn.SignOutAsync();
            return Results.NoContent();
        });

    // ---- Admin ----

    private static Task<IResult> AdminReset(string userName, HttpContext ctx, UserManager<OlomocUser> users) =>
        WithUser(ctx, users, async admin =>
        {
            if (!await users.IsInRoleAsync(admin, AdminRole)) return Fail("forbidden", 403);
            if (await users.FindByNameAsync(userName) is not { } user) return Fail("unknownUser", 404);
            var password = TemporaryPasswordText();
            var reset = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password);
            if (!reset.Succeeded) return Problems(Describe(reset), 500);
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            return Results.Json(new TemporaryPassword(password), TournamentJson.Options);
        });

    // ---- Outils ----

    public static async Task<AccountView> View(OlomocUser user, UserManager<OlomocUser> users) =>
        new(user.UserName ?? "", user.Email ?? "", user.EmailConfirmed, await users.IsInRoleAsync(user, AdminRole));

    // Compte de la session ; supprimé entre-temps : session fermée (401).
    private static async Task<IResult> WithUser(HttpContext ctx, UserManager<OlomocUser> users, Func<OlomocUser, Task<IResult>> action) =>
        await users.GetUserAsync(ctx.User) is { } user ? await action(user) : Results.Unauthorized();

    /// <summary>Par pseudo, ou par e-mail s'il contient « @ ».</summary>
    private static async Task<OlomocUser?> FindAsync(UserManager<OlomocUser> users, string? login)
    {
        login = login?.Trim();
        if (string.IsNullOrEmpty(login)) return null;
        return await users.FindByNameAsync(login) ?? (login.Contains('@') ? await users.FindByEmailAsync(login) : null);
    }

    /// <summary>E-mail de confirmation, dans la limite de <see cref="MailsPerHour"/> ; false si la limite est atteinte.</summary>
    private static async Task<bool> QueueConfirmationAsync(HttpContext ctx, OlomocUser user, UserManager<OlomocUser> users, MailQueue mail, string? language)
    {
        if (user.Email is not { } email || !await CountMailAsync(user, users)) return false;
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        mail.Enqueue(AccountMails.Confirm(email, user.UserName!, Link(ctx, "confirm", user, token), language));
        return true;
    }

    private static async Task<bool> CountMailAsync(OlomocUser user, UserManager<OlomocUser> users)
    {
        var now = DateTime.UtcNow;
        if (user.MailWindowStart is not { } start || now - start >= TimeSpan.FromHours(1))
        {
            user.MailWindowStart = now;
            user.MailsInWindow = 0;
        }
        if (user.MailsInWindow >= MailsPerHour) return false;
        user.MailsInWindow++;
        await users.UpdateAsync(user);
        return true;
    }

    // Lien vers une page du constructeur ; compte et jeton dans l'ancre (jamais envoyée au serveur, ni dans les journaux).
    private static string Link(HttpContext ctx, string page, OlomocUser user, string token) =>
        $"{ctx.Request.Scheme}://{ctx.Request.Host}/builder/account/{page}#user={Uri.EscapeDataString(user.Id)}&token={Uri.EscapeDataString(token)}";

    // Lisible et facile à recopier : ni 0/O, ni 1/l/I.
    private static string TemporaryPasswordText()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 14).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }

    private static IReadOnlyList<Problem> Describe(IdentityResult result) =>
        result.Errors.Select(e => e.Code switch
        {
            nameof(IdentityErrorDescriber.DuplicateUserName) => Problem.Of("userNameTaken"),
            nameof(IdentityErrorDescriber.DuplicateEmail) => Problem.Of("emailTaken"),
            nameof(IdentityErrorDescriber.InvalidUserName) => Problem.Of("userName", AccountRules.MinUserName.ToString(), AccountRules.MaxUserName.ToString()),
            nameof(IdentityErrorDescriber.InvalidEmail) => Problem.Of("email"),
            nameof(IdentityErrorDescriber.PasswordTooShort) => Problem.Of("password", AccountRules.MinPassword.ToString()),
            nameof(IdentityErrorDescriber.PasswordMismatch) => Problem.Of("wrongPassword"),
            nameof(IdentityErrorDescriber.InvalidToken) => Problem.Of("invalidLink"),
            _ => Problem.Of("server", e.Code),
        }).DistinctBy(p => p.Key).ToList();

    private static IResult Fail(string key, int status, params string[] args) => Problems(new[] { Problem.Of(key, args) }, status);

    private static IResult Problems(IReadOnlyList<Problem> problems, int status) =>
        Results.Json(new ProblemList(problems), TournamentJson.Options, statusCode: status);
}

/// <summary>Jetons de confirmation d'e-mail : durée de vie propre (2 jours), distincte de celle des réinitialisations (1 heure).</summary>
public sealed class EmailConfirmationTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public EmailConfirmationTokenProviderOptions()
    {
        Name = "EmailConfirmationDataProtectorTokenProvider";
        TokenLifespan = TimeSpan.FromDays(2);
    }
}

public sealed class EmailConfirmationTokenProvider(IDataProtectionProvider protection,
    IOptions<EmailConfirmationTokenProviderOptions> options, ILogger<DataProtectorTokenProvider<OlomocUser>> log)
    : DataProtectorTokenProvider<OlomocUser>(protection, options, log);

/// <summary>
/// Au démarrage : rôle Admin des comptes listés dans OLOMOC_ADMINS (pseudos séparés par « ; »),
/// retiré aux autres. Seuls des comptes existants le reçoivent : on crée son compte, puis on
/// l'ajoute à la liste (personne ne peut devenir admin en prenant un pseudo le premier).
/// </summary>
public static class AdminRoles
{
    public static async Task SyncAsync(IServiceProvider services, string? setting, ILogger log)
    {
        using var scope = services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OlomocUser>>();
        if (!await roles.RoleExistsAsync(AccountEndpoints.AdminRole)) await roles.CreateAsync(new IdentityRole(AccountEndpoints.AdminRole));

        var wanted = (setting ?? "").Split(';', ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(n => users.NormalizeName(n)).ToHashSet();
        foreach (var user in await users.GetUsersInRoleAsync(AccountEndpoints.AdminRole))
            if (!wanted.Contains(user.NormalizedUserName!)) await users.RemoveFromRoleAsync(user, AccountEndpoints.AdminRole);
        foreach (var name in wanted)
        {
            if (await users.FindByNameAsync(name) is not { } user) log.LogWarning("Admin {Name} : aucun compte de ce nom.", name);
            else if (!await users.IsInRoleAsync(user, AccountEndpoints.AdminRole)) await users.AddToRoleAsync(user, AccountEndpoints.AdminRole);
        }
    }
}
