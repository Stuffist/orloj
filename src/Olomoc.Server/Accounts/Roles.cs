using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Olomoc.Accounts;
using Olomoc.Server.Data;
using Olomoc.Tournaments;

namespace Olomoc.Server.Accounts;

/// <summary>
/// Rôles des comptes, enregistrés dans la base (rôles Identity) : joueur (aucun rôle), éditeur
/// (éditeur du référentiel), admin (tout, dont les rôles des autres comptes). Un compte a au plus
/// un des deux rôles ; admin comprend éditeur.
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

    /// <summary>Rôle de la session (cookie, revérifié avec le tampon de sécurité) : brouillons du référentiel visibles.</summary>
    public static bool CanEdit(ClaimsPrincipal user) => user.IsInRole(Admin) || user.IsInRole(Editor);

    /// <summary>
    /// Filtre d'API : compte connecté dont le rôle, lu dans la base (changé à l'instant peut-être),
    /// atteint <paramref name="minimum"/>. 401 sans session, 403 sinon.
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
    /// Au démarrage : rôles créés ; rôle admin donné aux comptes listés dans OLOMOC_ADMINS (pseudos
    /// séparés par « ; »), pour le premier admin ou en secours. Jamais retiré : les rôles se gèrent
    /// ensuite dans « Mon compte ». Seuls des comptes existants le reçoivent (on crée son compte,
    /// puis on l'ajoute à la liste : personne ne devient admin en prenant un pseudo le premier).
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
            if (await users.FindByNameAsync(name) is not { } user) log.LogWarning("Admin {Name} : aucun compte de ce nom.", name);
            else if (await OfAsync(users, user) != AccountRole.Admin)
            {
                await SetAsync(users, user, AccountRole.Admin);
                log.LogInformation("Admin {Name} : rôle donné (OLOMOC_ADMINS).", user.UserName);
            }
        }
    }
}
