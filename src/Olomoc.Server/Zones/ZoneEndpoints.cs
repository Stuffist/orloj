using Microsoft.AspNetCore.Identity;
using Olomoc.Accounts;
using Olomoc.Server.Accounts;
using Olomoc.Server.Admin;
using Olomoc.Server.Data;
using Olomoc.Server.Tournaments;
using Olomoc.Tournaments;
using Olomoc.Zones;

namespace Olomoc.Server.Zones;

/// <summary>
/// Zones API (/api/zones): public reading; games entered by anyone (no account needed) in an open
/// zone, changed or deleted through the entry token, the author's account (while the zone is open),
/// a manager or a zone admin; settings by a manager or a zone admin; creation, managers, dominance mode,
/// visibility and deletion by a zone admin: an editor or an admin (/api/admin/zones, logged).
/// </summary>
public static class ZoneEndpoints
{
    /// <summary>Games entered per IP address and per hour.</summary>
    public const string EntryPolicy = "zone-entry";
    public const int PageSize = 50;

    public static void MapZones(this WebApplication app)
    {
        var api = app.MapGroup("/api/zones");
        var read = api.MapGroup("").RequireRateLimiting(TournamentEndpoints.ReadPolicy);
        read.MapGet("", List);
        read.MapGet("stats", async (ZoneStats stats) => Results.Json(await stats.AllAsync(), TournamentJson.Options));
        read.MapGet("{code}", Get);
        read.MapGet("{code}/games", Games);

        api.MapPost("{code}/games", AddGame).RequireRateLimiting(EntryPolicy);
        var write = api.MapGroup("{code}").RequireRateLimiting(TournamentEndpoints.WritePolicy);
        write.MapPut("games/{id:long}", UpdateGame);
        write.MapDelete("games/{id:long}", DeleteGame);
        write.MapPut("", UpdateSettings);

        // Zone administration: editors and admins.
        var admin = app.MapGroup("/api/admin/zones").RequireAuthorization().AddEndpointFilter(Roles.Require(AccountRole.Editor))
            .RequireRateLimiting(TournamentEndpoints.WritePolicy);
        admin.MapPost("", Create);
        admin.MapPut("{code}", AdminUpdate);
        admin.MapDelete("{code}", Delete);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // ---- Reading ----

    /// <summary>Zones: hidden ones only for the admins and their managers.</summary>
    private static async Task<IResult> List(HttpContext ctx, UserManager<OlomocUser> users, ZoneStore zones, ZoneGameStore games, ZoneStats stats)
    {
        var access = await Access.OfAsync(ctx, users);
        var counts = await games.CountsAsync();
        var list = new List<ZoneSummary>();
        foreach (var z in await zones.ListAsync())
        {
            var manages = access.Manages(z);
            if (z.Hidden && !manages) continue;
            list.Add(new ZoneSummary(z.Code, z.Name, z.Description, z.Kind, z.StartDate, z.EndDate, z.StatusOn(Today), z.Hidden,
                counts.GetValueOrDefault(z.Code), await stats.DominanceAsync(z), manages, z.Logo, z.Slot));
        }
        return Results.Json(list, TournamentJson.Options);
    }

    private static async Task<IResult> Get(string code, HttpContext ctx, UserManager<OlomocUser> users, ZoneStore zones, ZoneStats stats)
    {
        if (await zones.LoadAsync(code) is not { } z) return NotFound();
        var access = await Access.OfAsync(ctx, users);
        var manages = access.Manages(z);
        List<string>? managers = null;
        if (manages)
        {
            managers = new();
            foreach (var id in z.ManagerIds)
                if (await users.FindByIdAsync(id) is { UserName: { } name }) managers.Add(name);
        }
        return Results.Json(new ZoneView(z.Code, z.Name, z.Description, z.Kind, z.StartDate, z.EndDate, z.StatusOn(Today), z.Dominance,
            z.Hidden, z.Closed, await stats.DominanceAsync(z), await stats.OfZoneAsync(z.Code), manages, access.ZoneAdmin, managers, z.Logo),
            TournamentJson.Options);
    }

    private static async Task<IResult> Games(string code, long? before, int? take, HttpContext ctx, UserManager<OlomocUser> users,
        ZoneStore zones, ZoneGameStore games, TournamentStore tournaments)
    {
        if (await zones.LoadAsync(code) is not { } z) return NotFound();
        var access = await Access.OfAsync(ctx, users);
        var count = Math.Clamp(take ?? PageSize, 1, PageSize);
        var page = await games.PageAsync(z.Code, before, count + 1);
        var open = z.StatusOn(Today) == ZoneStatus.Open;
        // Games copied from tournaments: their name, while the tournament exists (purged after 90 days).
        var names = new Dictionary<string, string?>();
        foreach (var t in page.Select(g => g.TournamentCode).OfType<string>().Distinct())
            names[t] = (await tournaments.LoadAsync(t))?.Name;
        var views = page.Take(count).Select(g => new ZoneGameView(g.Id, g.Game, g.CreatedAt, access.CanEdit(z, g, open, null),
            g.TournamentCode, g.TournamentCode is { } t ? names[t] : null)).ToList();
        return Results.Json(new ZoneGamesPage(views, page.Count > count ? views[^1].Id : null, await games.CountAsync(z.Code)),
            TournamentJson.Options);
    }

    // ---- Games ----

    private static async Task<IResult> AddGame(string code, ZoneGame body, HttpContext ctx, UserManager<OlomocUser> users,
        ZoneStore zones, ZoneGameStore games, ZoneStats stats, ServerCatalog catalog)
    {
        if (await zones.LoadAsync(code) is not { } z) return NotFound();
        var access = await Access.OfAsync(ctx, users);
        var (clean, problems) = ZoneRules.CheckGame(body, z, await catalog.GetAsync(), Today, access.Manages(z));
        if (clean is null) return Problems(problems, 400);
        if (await games.CountAsync(z.Code) >= ZoneRules.MaxGamesPerZone) return Problems([Problem.Of("zoneFull", ZoneRules.MaxGamesPerZone.ToString())], 503);
        var token = Tokens.New();
        var id = await games.AddAsync(z.Code, clean, access.UserId, Tokens.Hash(token));
        stats.Invalidate(z.Code);
        return Results.Json(new ZoneGameCreated(id, token), TournamentJson.Options, statusCode: 201);
    }

    private static async Task<IResult> UpdateGame(string code, long id, ZoneGame body, HttpContext ctx, UserManager<OlomocUser> users,
        ZoneStore zones, ZoneGameStore games, ZoneStats stats, ServerCatalog catalog)
    {
        if (await zones.LoadAsync(code) is not { } z) return NotFound();
        if (await games.GetAsync(z.Code, id) is not { } stored) return NotFoundGame();
        var access = await Access.OfAsync(ctx, users);
        var open = z.StatusOn(Today) == ZoneStatus.Open;
        if (stored.TournamentCode is not null) return FromTournament();
        if (!access.CanEdit(z, stored, open, ctx.Request.Headers[ZoneHeaders.Entry])) return Forbidden(open);
        var (clean, problems) = ZoneRules.CheckGame(body, z, await catalog.GetAsync(), Today, access.Manages(z));
        if (clean is null) return Problems(problems, 400);
        if (!await games.UpdateAsync(z.Code, id, clean)) return NotFoundGame();
        stats.Invalidate(z.Code);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteGame(string code, long id, HttpContext ctx, UserManager<OlomocUser> users,
        ZoneStore zones, ZoneGameStore games, ZoneStats stats)
    {
        if (await zones.LoadAsync(code) is not { } z) return NotFound();
        if (await games.GetAsync(z.Code, id) is not { } stored) return NotFoundGame();
        var access = await Access.OfAsync(ctx, users);
        var open = z.StatusOn(Today) == ZoneStatus.Open;
        if (stored.TournamentCode is not null) return FromTournament();
        if (!access.CanEdit(z, stored, open, ctx.Request.Headers[ZoneHeaders.Entry])) return Forbidden(open);
        if (!await games.DeleteAsync(z.Code, id)) return NotFoundGame();
        stats.Invalidate(z.Code);
        return Results.NoContent();
    }

    // ---- Settings ----

    /// <summary>Managers and admins: name, description, logo, kind, dates, closing by hand.</summary>
    private static async Task<IResult> UpdateSettings(string code, ZoneSettingsRequest body, HttpContext ctx, UserManager<OlomocUser> users,
        ZoneStore zones, ZoneStats stats)
    {
        var access = await Access.OfAsync(ctx, users);
        if (access.UserId is null) return Results.Unauthorized();
        var problems = ZoneRules.CheckSettings(body.Name, body.Description, body.StartDate, body.EndDate, body.Logo);
        if (problems.Count > 0) return Problems(problems, 400);
        var result = await zones.UpdateAsync(code, z =>
        {
            if (!access.Manages(z)) return (false, Forbidden(true));
            Apply(z, body.Name, body.Description, body.Kind, body.StartDate, body.EndDate, body.Closed, body.Logo);
            return (true, Results.NoContent());
        });
        if (result is null) return NotFound();
        stats.Invalidate(code.ToUpperInvariant());
        return result;
    }

    // ---- Admins ----

    private static async Task<IResult> Create(ZoneAdminRequest body, HttpContext ctx, UserManager<OlomocUser> users,
        ZoneStore zones, AdminAudit audit)
    {
        var (managers, problems) = await CheckAdminAsync(body, users);
        if (problems.Count > 0) return Problems(problems, 400);
        var z = await zones.CreateAsync(z => ApplyAdmin(z, body, managers));
        if (z is null) return Problems([Problem.Of("zoneStorageFull")], 503);
        await zones.AssignSlotsAsync();                // its hexagon on the map: the most central free one
        await audit.LogAsync(AdminEndpoints.AdminName(ctx), "createZone", z.Code, z.Name);
        return Results.Json(new ZoneCreated(z.Code), TournamentJson.Options, statusCode: 201);
    }

    private static async Task<IResult> AdminUpdate(string code, ZoneAdminRequest body, HttpContext ctx, UserManager<OlomocUser> users,
        ZoneStore zones, ZoneStats stats, AdminAudit audit)
    {
        var (managers, problems) = await CheckAdminAsync(body, users);
        if (problems.Count > 0) return Problems(problems, 400);
        var name = await zones.UpdateAsync(code, z =>
        {
            ApplyAdmin(z, body, managers);
            return (true, z.Name);
        });
        if (name is null) return NotFound();
        stats.Invalidate(code.ToUpperInvariant());
        await audit.LogAsync(AdminEndpoints.AdminName(ctx), "updateZone", code.ToUpperInvariant(), name);
        return Results.NoContent();
    }

    /// <summary>The zone and its games.</summary>
    private static async Task<IResult> Delete(string code, HttpContext ctx, ZoneStore zones, ZoneGameStore games, ZoneStats stats, AdminAudit audit)
    {
        var name = await zones.DeleteAsync(code, z => (true, z.Name));
        if (name is null) return NotFound();
        var removed = await games.DeleteZoneAsync(code.ToUpperInvariant());
        await zones.AssignSlotsAsync();                // its hexagon freed: for a zone that had none
        stats.Invalidate(code.ToUpperInvariant());
        await audit.LogAsync(AdminEndpoints.AdminName(ctx), "deleteZone", code.ToUpperInvariant(), $"{name} ({removed})");
        return Results.NoContent();
    }

    // Settings, and managers found by their username.
    private static async Task<(List<string> Managers, List<Problem> Problems)> CheckAdminAsync(ZoneAdminRequest body, UserManager<OlomocUser> users)
    {
        var problems = ZoneRules.CheckSettings(body.Name, body.Description, body.StartDate, body.EndDate, body.Logo).ToList();
        if (!Enum.IsDefined(body.Dominance) || !Enum.IsDefined(body.Kind)) problems.Add(Problem.Of("zoneGame"));
        var names = (body.Managers ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > ZoneRules.MaxManagers) problems.Add(Problem.Of("zoneManagers", ZoneRules.MaxManagers.ToString()));
        var ids = new List<string>();
        foreach (var name in names.Take(ZoneRules.MaxManagers))
        {
            if (await users.FindByNameAsync(name) is { } user) ids.Add(user.Id);
            else problems.Add(Problem.Of("unknownAccount", name));
        }
        return (ids, problems);
    }

    private static void Apply(Zone z, string name, string? description, ZoneKind kind, DateOnly? start, DateOnly? end, bool closed,
        string? logo)
    {
        z.Name = name.Trim();
        z.Description = description?.Trim() ?? "";
        z.Kind = Enum.IsDefined(kind) ? kind : ZoneKind.Club;
        z.StartDate = start;
        z.EndDate = end;
        z.Closed = closed;
        z.Logo = ZoneRules.CleanLogo(logo);
    }

    private static void ApplyAdmin(Zone z, ZoneAdminRequest body, List<string> managers)
    {
        Apply(z, body.Name, body.Description, body.Kind, body.StartDate, body.EndDate, body.Closed, body.Logo);
        z.Dominance = body.Dominance;
        z.Hidden = body.Hidden;
        z.ManagerIds = managers;
    }

    // ---- Rights ----

    /// <summary>
    /// The requester: account (if signed in), and whether it administers the zones (editor or admin
    /// role, read from the database).
    /// </summary>
    private sealed record Access(string? UserId, bool ZoneAdmin)
    {
        private static readonly Access Anonymous = new(null, false);

        public static async Task<Access> OfAsync(HttpContext ctx, UserManager<OlomocUser> users)
        {
            if (ctx.User.Identity?.IsAuthenticated != true || await users.GetUserAsync(ctx.User) is not { } user) return Anonymous;
            return new(user.Id, await Roles.OfAsync(users, user) >= AccountRole.Editor);
        }

        public bool Manages(Zone z) => ZoneAdmin || UserId is not null && z.ManagerIds.Contains(UserId);

        /// <summary>
        /// A manager, always; otherwise the author (account or entry token), while the zone is open.
        /// Never a game copied from a tournament: it follows the tournament.
        /// </summary>
        public bool CanEdit(Zone z, StoredZoneGame g, bool open, string? entryToken) => g.TournamentCode is null &&
            (Manages(z) || open && (UserId is not null && g.SubmitterId == UserId || Tokens.Matches(entryToken, g.EditTokenHash)));
    }

    private static IResult Problems(IReadOnlyList<Problem> problems, int status) =>
        Results.Json(new ProblemList(problems), TournamentJson.Options, statusCode: status);

    private static IResult NotFound() => Problems([Problem.Of("zoneNotFound")], 404);
    private static IResult FromTournament() => Problems([Problem.Of("zoneGameFromTournament")], 409);
    private static IResult NotFoundGame() => Problems([Problem.Of("zoneGameNotFound")], 404);
    private static IResult Forbidden(bool open) => Problems([Problem.Of(open ? "zoneForbidden" : "zoneClosed")], 403);
}
