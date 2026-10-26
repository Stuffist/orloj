using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Olomoc.Accounts;
using Olomoc.Server.Data;
using Olomoc.Tournaments;

namespace Olomoc.Server.Accounts;

/// <summary>
/// Account roles, stored in the database (Identity roles): player (no role), editor (card catalog
/// editor), admin (everything, including other accounts' roles). An account has at most one of the
/// two roles; admin includes editor.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin", Editor = "Editor";

    public static async Task<AccountRole> OfAsync(UserManager<OlomocUser> users, OlomocUser user) =>
        await users.IsInRoleAsync(user, Admin) ? AccountRole.Admin
        : await users.IsInRoleAsync(user, Editor) ? AccountRole.Editor
        : AccountRole.User;

    public static async Task SetAsync(UserManager<OlomocUser> users, OlomocUser user, AccountRole role)
    {
        foreach (var (name, wanted) in new[] { (Admin, role == AccountRole.Admin), (Editor, role == AccountRole.Editor) })
        {
            var has = await users.IsInRoleAsync(user, name);
            if (wanted && !has) await users.AddToRoleAsync(user, name);
            else if (!wanted && has) await users.RemoveFromRoleAsync(user, name);
        }
    }

    public static string? NameOf(AccountRole role) => role switch
    {
        AccountRole.Admin => Admin,
        AccountRole.Editor => Editor,
        _ => null,
    };

    /// <summary>Session role (cookie, rechecked with the security stamp): card catalog drafts visible.</summary>
    public static bool CanEdit(ClaimsPrincipal user) => user.IsInRole(Admin) || user.IsInRole(Editor);

    /// <summary>
    /// API filter: signed-in account whose role, read from the database (maybe changed a moment
    /// ago), reaches <paramref name="minimum"/>. 401 without a session, 403 otherwise.
    /// </summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(AccountRole minimum) =>
        async (ctx, next) =>
        {
            var users = ctx.HttpContext.RequestServices.GetRequiredService<UserManager<OlomocUser>>();
            if (await users.GetUserAsync(ctx.HttpContext.User) is not { } user) return Results.Unauthorized();
            if (await OfAsync(users, user) < minimum)
                return Results.Json(new ProblemList([Problem.Of("forbidden")]), TournamentJson.Options, statusCode: StatusCodes.Status403Forbidden);
            return await next(ctx);
        };

    /// <summary>
    /// At startup: roles created; admin role given to the accounts listed in OLOMOC_ADMINS (usernames
    /// separated by ";"), for the first admin or as a fallback. Never removed: roles are then managed
    /// in the admin panel. Only existing accounts receive it (create your account, then add it to the
    /// list: nobody becomes admin by grabbing a username first).
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, string? admins, ILogger log)
    {
        using var scope = services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OlomocUser>>();
        foreach (var name in new[] { Admin, Editor })
            if (!await roles.RoleExistsAsync(name)) await roles.CreateAsync(new IdentityRole(name));

        foreach (var name in (admins ?? "").Split(';', ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (await users.FindByNameAsync(name) is not { } user) log.LogWarning("Admin {Name}: no account with this name.", name);
            else if (await OfAsync(users, user) != AccountRole.Admin)
            {
                await SetAsync(users, user, AccountRole.Admin);
                log.LogInformation("Admin {Name}: role granted (OLOMOC_ADMINS).", user.UserName);
            }
        }
    }
}
