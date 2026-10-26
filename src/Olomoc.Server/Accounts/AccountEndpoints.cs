using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Olomoc.Accounts;
using Olomoc.Server.Admin;
using Olomoc.Server.Data;
using Olomoc.Server.Tournaments;
using Olomoc.Tournaments;

namespace Olomoc.Server.Accounts;

/// <summary>
/// Accounts API (/api/account). Cookie session (HttpOnly, SameSite=Strict); passwords, lockout,
/// link tokens and security stamp handled by ASP.NET Core Identity. The email is only used to
/// recover the account: confirmation, then forgotten password.
/// </summary>
public static class AccountEndpoints
{
    public const string CreatePolicy = "account-create", MailPolicy = "account-mail";
    public const string ConfirmTokenProvider = "EmailConfirmation";

    /// <summary>Maximum emails sent per hour and per account (confirmation, forgotten password).</summary>
    public const int MailsPerHour = 3;

    /// <summary>Maximum accounts listed per search (admin panel).</summary>
    public const int MaxListed = 50;

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

        // Signed-in account.
        var mine = api.MapGroup("").RequireAuthorization();
        mine.MapPost("logout-others", LogoutOthers).RequireRateLimiting(TournamentEndpoints.WritePolicy);
        mine.MapPost("password", ChangePassword).RequireRateLimiting(TournamentEndpoints.LoginPolicy);
        mine.MapPost("email", ChangeEmail).RequireRateLimiting(MailPolicy);
        mine.MapPost("confirm/send", SendConfirmation).RequireRateLimiting(MailPolicy);
        mine.MapPost("delete", Delete).RequireRateLimiting(TournamentEndpoints.LoginPolicy);
        mine.MapPost("cabals/sync", CabalSyncEndpoint.Sync).RequireRateLimiting(TournamentEndpoints.WritePolicy);

        // Admin (admin panel): account roles; temporary password for an account whose email cannot be
        // used for recovery; unlock, sign out, deletion. All logged.
        var admin = app.MapGroup("/api/admin/accounts").RequireAuthorization().AddEndpointFilter(Roles.Require(AccountRole.Admin));
        admin.MapGet("", ListAccounts).RequireRateLimiting(TournamentEndpoints.ReadPolicy);
        admin.MapPut("{userName}/role", SetRole).RequireRateLimiting(TournamentEndpoints.WritePolicy);
        admin.MapPost("{userName}/reset-password", AdminReset).RequireRateLimiting(TournamentEndpoints.WritePolicy);
        admin.MapPost("{userName}/unlock", Unlock).RequireRateLimiting(TournamentEndpoints.WritePolicy);
        admin.MapPost("{userName}/logout", LogoutEverywhere).RequireRateLimiting(TournamentEndpoints.WritePolicy);
        admin.MapDelete("{userName}", AdminDelete).RequireRateLimiting(TournamentEndpoints.WritePolicy);
    }

    // ---- Without an account ----

    /// <summary>Session account; 204 without a session.</summary>
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

    // Same answer whether the account exists or not, whether the email is sent or not (no disclosure).
    private static async Task<IResult> Forgot(ForgotPasswordRequest body, HttpContext ctx, UserManager<OlomocUser> users, MailQueue mail)
    {
        if (await FindAsync(users, body.Login) is { EmailConfirmed: true, Email: { } email } user && await CountMailAsync(user, users))
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            mail.Enqueue(AccountMails.Reset(email, user.UserName!, Link(ctx, "reset", user, token), body.Language));
        }
        return Results.NoContent();
    }

    /// <summary>New password (link received): other sessions are closed, this one is opened.</summary>
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

    // ---- Signed-in account ----

    /// <summary>Closes the sessions of the other devices (this one stays open).</summary>
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
            await signIn.RefreshSignInAsync(user);       // new stamp: the other devices are signed out
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
                var set = await users.SetEmailAsync(user, email);      // to be confirmed again
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

    /// <summary>
    /// Accounts whose username contains <paramref name="search"/>; without a search, the editors and
    /// admins, or the newest accounts (<paramref name="recent"/>). Admins first, then by username
    /// (newest: most recent first); never the email.
    /// </summary>
    private static async Task<IResult> ListAccounts(string? search, bool? recent, OlomocDb db, UserManager<OlomocUser> users)
    {
        var roles = await db.UserRoles.Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name }).ToListAsync();
        var roleOf = roles.GroupBy(r => r.UserId).ToDictionary(g => g.Key,
            g => g.Any(r => r.Name == Roles.Admin) ? AccountRole.Admin : AccountRole.Editor);
        var query = db.Users.AsNoTracking();
        var newest = string.IsNullOrWhiteSpace(search) && recent == true;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = users.NormalizeName(search.Trim());
            query = query.Where(u => u.NormalizedUserName!.Contains(normalized));
        }
        else if (!newest)
        {
            var staff = roleOf.Keys.ToList();
            query = query.Where(u => staff.Contains(u.Id));
        }
        query = newest ? query.OrderByDescending(u => u.CreatedAt) : query.OrderBy(u => u.NormalizedUserName);
        var found = await query.Take(MaxListed)
            .Select(u => new { u.Id, u.UserName, u.CreatedAt, u.LastLoginAt, u.EmailConfirmed, u.LockoutEnd }).ToListAsync();
        var ids = found.Select(u => u.Id).ToList();
        var cabals = await db.SavedCabals.Where(c => ids.Contains(c.UserId) && !c.Deleted).GroupBy(c => c.UserId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        var now = DateTimeOffset.UtcNow;
        var list = found.Select(u => new AdminAccount(u.UserName ?? "", roleOf.GetValueOrDefault(u.Id, AccountRole.User),
            Utc(u.CreatedAt), u.LastLoginAt is { } last ? Utc(last) : null, u.EmailConfirmed, u.LockoutEnd > now, cabals.GetValueOrDefault(u.Id)));
        if (!newest) list = list.OrderByDescending(a => a.Role).ThenBy(a => a.UserName, StringComparer.OrdinalIgnoreCase);
        return Results.Json(list.ToList(), TournamentJson.Options);
    }

    private static DateTimeOffset Utc(DateTime date) => new(DateTime.SpecifyKind(date, DateTimeKind.Utc));

    /// <summary>Role of another account (never one's own: an admin always stays admin, so there is always one).</summary>
    private static Task<IResult> SetRole(string userName, RoleRequest body, HttpContext ctx, UserManager<OlomocUser> users, AdminAudit audit) =>
        WithUser(ctx, users, async admin =>
        {
            if (!Enum.IsDefined(body.Role)) return Fail("role", 400);
            if (await users.FindByNameAsync(userName) is not { } user) return Fail("unknownUser", 404);
            if (user.Id == admin.Id) return Fail("ownRole", 400);
            await Roles.SetAsync(users, user, body.Role);
            await audit.LogAsync(admin.UserName!, "role", user.UserName!, body.Role.ToString());
            return Results.Json(new AccountSummary(user.UserName ?? "", body.Role), TournamentJson.Options);
        });

    private static async Task<IResult> AdminReset(string userName, HttpContext ctx, UserManager<OlomocUser> users, AdminAudit audit)
    {
        if (await users.FindByNameAsync(userName) is not { } user) return Fail("unknownUser", 404);
        var password = TemporaryPasswordText();
        var reset = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password);
        if (!reset.Succeeded) return Problems(Describe(reset), 500);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await audit.LogAsync(AdminEndpoints.AdminName(ctx), "resetPassword", user.UserName!);
        return Results.Json(new TemporaryPassword(password), TournamentJson.Options);
    }

    /// <summary>Ends the lockout (too many password attempts).</summary>
    private static async Task<IResult> Unlock(string userName, HttpContext ctx, UserManager<OlomocUser> users, AdminAudit audit)
    {
        if (await users.FindByNameAsync(userName) is not { } user) return Fail("unknownUser", 404);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await audit.LogAsync(AdminEndpoints.AdminName(ctx), "unlock", user.UserName!);
        return Results.NoContent();
    }

    /// <summary>Closes all sessions of another account (new security stamp).</summary>
    private static Task<IResult> LogoutEverywhere(string userName, HttpContext ctx, UserManager<OlomocUser> users, AdminAudit audit) =>
        WithUser(ctx, users, async admin =>
        {
            if (await users.FindByNameAsync(userName) is not { } user) return Fail("unknownUser", 404);
            if (user.Id == admin.Id) return Fail("ownAccount", 400);
            await users.UpdateSecurityStampAsync(user);
            await audit.LogAsync(admin.UserName!, "logout", user.UserName!);
            return Results.NoContent();
        });

    /// <summary>
    /// Deletes an account and its cabals; never one's own, nor an admin's (remove their role first).
    /// </summary>
    private static Task<IResult> AdminDelete(string userName, HttpContext ctx, UserManager<OlomocUser> users, AdminAudit audit) =>
        WithUser(ctx, users, async admin =>
        {
            if (await users.FindByNameAsync(userName) is not { } user) return Fail("unknownUser", 404);
            if (user.Id == admin.Id) return Fail("ownAccount", 400);
            if (await Roles.OfAsync(users, user) == AccountRole.Admin) return Fail("deleteAdmin", 400);
            var deleted = await users.DeleteAsync(user);
            if (!deleted.Succeeded) return Problems(Describe(deleted), 500);
            await audit.LogAsync(admin.UserName!, "deleteAccount", user.UserName!);
            return Results.NoContent();
        });

    // ---- Helpers ----

    public static async Task<AccountView> View(OlomocUser user, UserManager<OlomocUser> users) =>
        new(user.UserName ?? "", user.Email ?? "", user.EmailConfirmed, await Roles.OfAsync(users, user));

    // Session account; deleted in the meantime: session closed (401).
    private static async Task<IResult> WithUser(HttpContext ctx, UserManager<OlomocUser> users, Func<OlomocUser, Task<IResult>> action) =>
        await users.GetUserAsync(ctx.User) is { } user ? await action(user) : Results.Unauthorized();

    /// <summary>By username, or by email if it contains "@".</summary>
    private static async Task<OlomocUser?> FindAsync(UserManager<OlomocUser> users, string? login)
    {
        login = login?.Trim();
        if (string.IsNullOrEmpty(login)) return null;
        return await users.FindByNameAsync(login) ?? (login.Contains('@') ? await users.FindByEmailAsync(login) : null);
    }

    /// <summary>Confirmation email, within the <see cref="MailsPerHour"/> limit; false if the limit is reached.</summary>
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

    /// <summary>
    /// Link to a builder page; account and token in the fragment (never sent to the server, never logged).
    /// Base address: PUBLIC_URL when set (recommended: a forged Host header can then never end up in an
    /// email), otherwise the request's scheme and host (only safe with AllowedHosts restricted).
    /// </summary>
    private static string Link(HttpContext ctx, string page, OlomocUser user, string token)
    {
        var configured = ctx.RequestServices.GetRequiredService<IConfiguration>()["PUBLIC_URL"]?.Trim().TrimEnd('/');
        var origin = string.IsNullOrEmpty(configured) ? $"{ctx.Request.Scheme}://{ctx.Request.Host}" : configured;
        return $"{origin}/builder/account/{page}#user={Uri.EscapeDataString(user.Id)}&token={Uri.EscapeDataString(token)}";
    }

    // Readable and easy to copy: no 0/O, no 1/l/I.
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

/// <summary>Email confirmation tokens: their own lifetime (2 days), distinct from password resets (1 hour).</summary>
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
