using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Olomoc.Accounts;
using Olomoc.Server.Accounts;
using Olomoc.Server.CardCatalog;
using Olomoc.Server.Data;
using Olomoc.Server.Online;
using Olomoc.Server.Tournaments;
using Olomoc.Tournaments;

namespace Olomoc.Server.Admin;

/// <summary>
/// Panneau d'administration (/api/admin, admins seulement) : chiffres de la base, tournois et
/// parties en ligne de tout le monde (actions limitées : nouveau lien d'administration,
/// statistiques, suppression), maintenance (sauvegarde, purges), journal. Les comptes sont dans
/// <see cref="AccountEndpoints"/> (/api/admin/accounts). Chaque action est journalisée.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Démarrage du serveur (vue d'ensemble).</summary>
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static void MapAdmin(this WebApplication app, string dataDirectory)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization().AddEndpointFilter(Roles.Require(AccountRole.Admin));
        var read = admin.MapGroup("").RequireRateLimiting(TournamentEndpoints.ReadPolicy);
        var write = admin.MapGroup("").RequireRateLimiting(TournamentEndpoints.WritePolicy);

        read.MapGet("overview", (OlomocDb db, TournamentStore tournaments, OnlineStore online, CatalogStore catalog,
            DbBackup backup, IMailSender mail, IConfiguration config) =>
            Overview(db, tournaments, online, catalog, backup, mail, config, dataDirectory));
        read.MapGet("tournaments", ListTournaments);
        read.MapGet("online", ListOnline);
        read.MapGet("audit", async (AdminAudit audit) => Results.Json(await audit.RecentAsync(), TournamentJson.Options));

        write.MapPost("tournaments/{code}/organizer-link", NewOrganizerLink);
        write.MapPut("tournaments/{code}/stats", SetStats);
        write.MapDelete("tournaments/{code}", DeleteTournament);
        write.MapDelete("online/{code}", DeleteOnline);
        write.MapPost("maintenance/backup", Backup);
        write.MapPost("maintenance/purge", Purge);
    }

    // ---- Vue d'ensemble ----

    private static async Task<IResult> Overview(OlomocDb db, TournamentStore tournaments, OnlineStore online, CatalogStore catalog,
        DbBackup backup, IMailSender mail, IConfiguration config, string dataDirectory)
    {
        var now = DateTimeOffset.UtcNow;
        var month = now.UtcDateTime.AddDays(-30);
        // Dates d'Identity (DateTimeOffset) : comparées ici, SQLite ne sait pas les comparer.
        var users = await db.Users.AsNoTracking().Select(u => new { u.CreatedAt, u.LastLoginAt, u.EmailConfirmed, u.LockoutEnd }).ToListAsync();
        var roles = await db.UserRoles.Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name }).ToListAsync();
        var admins = roles.Where(r => r.Name == Roles.Admin).Select(r => r.UserId).ToHashSet();
        var editors = roles.Where(r => r.Name == Roles.Editor && !admins.Contains(r.UserId)).Select(r => r.UserId).Distinct().Count();
        var accounts = new AccountMetrics(users.Count, config.GetValue("ACCOUNT_MAX", 10_000), users.Count(u => u.EmailConfirmed),
            editors, admins.Count, users.Count(u => u.CreatedAt >= month), users.Count(u => u.LastLoginAt >= month),
            users.Count(u => u.LockoutEnd > now), await db.SavedCabals.CountAsync(c => !c.Deleted));

        var all = await tournaments.ListAsync();
        var tournamentMetrics = new TournamentMetrics(all.Count, tournaments.MaxDocuments,
            all.Count(t => t.Phase == TournamentPhase.Registration), all.Count(t => t.Phase == TournamentPhase.Started),
            all.Count(t => t.Phase == TournamentPhase.Finished));

        var day = now.UtcDateTime.AddDays(-1);
        var onlineMetrics = new OnlineMetrics(await db.Documents.CountAsync(d => d.Kind == OnlineStore.Kind), online.MaxDocuments,
            await db.Documents.CountAsync(d => d.Kind == OnlineStore.Kind && d.UpdatedAt >= day));

        var backups = backup.Backups();
        var databaseBytes = new[] { OlomocDb.FileName, OlomocDb.FileName + "-wal" }
            .Select(name => new FileInfo(Path.Combine(dataDirectory, name))).Where(f => f.Exists).Sum(f => f.Length);
        return Results.Json(new AdminOverview(accounts, tournamentMetrics, onlineMetrics,
            await db.Documents.CountAsync(d => d.Kind == StatsArchive.Kind),
            catalog.Status().Values.Sum(entries => entries.Count),
            databaseBytes,
            backups.Count > 0 ? new DateTimeOffset(backups[0].LastWriteTimeUtc, TimeSpan.Zero) : null,
            backups.Count,
            mail is not FileMailSender,
            StartedAt), TournamentJson.Options);
    }

    // ---- Tournois ----

    /// <summary>Tous les tournois, le plus récemment modifié d'abord.</summary>
    private static async Task<IResult> ListTournaments(TournamentStore store) =>
        Results.Json((await store.ListAsync()).OrderByDescending(t => t.UpdatedAt).Select(t => new AdminTournament(
            t.Code, t.Name, t.Date, t.Phase, t.Players.Count, t.Rounds.Count, t.RoundCount, t.OrganizerPasswordHash.Length > 0,
            t.ExcludeFromStats, t.CreatedAt, t.UpdatedAt)).ToList(), TournamentJson.Options);

    /// <summary>
    /// Nouveau lien d'administration (organisateur qui a perdu le sien) : l'ancien cesse de marcher ;
    /// les sessions ouvertes par mot de passe restent.
    /// </summary>
    private static async Task<IResult> NewOrganizerLink(string code, HttpContext ctx, TournamentStore store, AdminAudit audit)
    {
        var token = Tokens.New();
        var name = await store.UpdateAsync(code, t =>
        {
            t.OrganizerTokenHash = Tokens.Hash(token);
            return (true, t.Name);
        });
        if (name is null) return NotFound();
        await audit.LogAsync(AdminName(ctx), "organizerLink", Code(code), name);
        return Results.Json(new TournamentCreated(Code(code), token), TournamentJson.Options);
    }

    private static async Task<IResult> SetStats(string code, StatsRequest body, HttpContext ctx, TournamentStore store, StatsArchive stats, AdminAudit audit)
    {
        var name = await store.UpdateAsync(code, t =>
        {
            t.ExcludeFromStats = body.Exclude;
            stats.Update(t, DateTimeOffset.UtcNow);
            return (true, t.Name);
        });
        if (name is null) return NotFound();
        await audit.LogAsync(AdminName(ctx), body.Exclude ? "excludeStats" : "includeStats", Code(code), name);
        return Results.NoContent();
    }

    /// <summary>Comme la suppression par l'organisateur : le tournoi et son archive de statistiques.</summary>
    private static async Task<IResult> DeleteTournament(string code, HttpContext ctx, TournamentStore store, StatsArchive stats, AdminAudit audit)
    {
        var name = await store.DeleteAsync(code, t =>
        {
            stats.Remove(t.Code);
            return (true, t.Name);
        });
        if (name is null) return NotFound();
        await audit.LogAsync(AdminName(ctx), "deleteTournament", Code(code), name);
        return Results.NoContent();
    }

    // ---- Parties en ligne ----

    private static async Task<IResult> ListOnline(OnlineStore store) =>
        Results.Json((await store.ListAsync()).OrderByDescending(g => g.UpdatedAt).Select(g => new AdminOnlineGame(
            g.Code, g.Seats.Select(s => s.Name).ToList(), g.Step, g.Cycle, g.Tournament, g.CreatedAt, g.UpdatedAt)).ToList(),
            TournamentJson.Options);

    /// <summary>Suppression ; les clients connectés relisent leur vue (partie introuvable).</summary>
    private static async Task<IResult> DeleteOnline(string code, HttpContext ctx, OnlineStore store, IHubContext<OnlineHub> hub, AdminAudit audit)
    {
        var seats = await store.DeleteAsync(code, g => (true, string.Join(" / ", g.Seats.Select(s => s.Name).Where(n => n.Length > 0))));
        if (seats is null) return NotFound();
        await hub.Clients.Group(Code(code)).SendAsync(OnlineHub.Changed, -1);
        await audit.LogAsync(AdminName(ctx), "deleteOnline", Code(code), seats.Length > 0 ? seats : null);
        return Results.NoContent();
    }

    // ---- Maintenance ----

    /// <summary>Sauvegarde maintenant (remplace celle du jour).</summary>
    private static async Task<IResult> Backup(HttpContext ctx, DbBackup backup, AdminAudit audit)
    {
        var path = await backup.BackupAsync(DateTime.UtcNow, ctx.RequestAborted, overwrite: true);
        var file = new FileInfo(path!);
        await audit.LogAsync(AdminName(ctx), "backup", file.Name);
        return Results.Json(new BackupResult(file.Name, file.Length), TournamentJson.Options);
    }

    /// <summary>Purge maintenant, avec les règles des tâches de fond.</summary>
    private static async Task<IResult> Purge(PurgeRequest body, HttpContext ctx, TournamentStore tournaments, OnlineStore online, AdminAudit audit)
    {
        var removed = body.Kind switch
        {
            PurgeKind.Tournaments => await tournaments.PurgeAsync(TournamentPurge.MaxAge, DateOnly.FromDateTime(DateTime.UtcNow)),
            PurgeKind.Online => await online.PurgeAsync(OnlinePurge.MaxAge, _ => true),
            _ => -1,
        };
        if (removed < 0) return Problems("kind", 400);
        await audit.LogAsync(AdminName(ctx), body.Kind == PurgeKind.Tournaments ? "purgeTournaments" : "purgeOnline", "", removed.ToString());
        return Results.Json(new PurgeResult(removed), TournamentJson.Options);
    }

    // ---- Outils ----

    /// <summary>Pseudo de l'admin de la session (claim de nom d'Identity).</summary>
    public static string AdminName(HttpContext ctx) => ctx.User.Identity?.Name ?? "?";

    private static string Code(string code) => code.Trim().ToUpperInvariant();

    private static IResult NotFound() => Problems("notFound", 404);

    private static IResult Problems(string key, int status) =>
        Results.Json(new ProblemList([Problem.Of(key)]), TournamentJson.Options, statusCode: status);
}
