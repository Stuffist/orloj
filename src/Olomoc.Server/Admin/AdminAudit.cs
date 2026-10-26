using Microsoft.EntityFrameworkCore;
using Olomoc.Accounts;
using Olomoc.Server.Data;

namespace Olomoc.Server.Admin;

/// <summary>
/// Journal des actions des admins (table AdminActions) : qui, quoi, sur quoi, quand. Les entrées
/// de plus d'un an sont supprimées à chaque écriture.
/// </summary>
public sealed class AdminAudit(IDbContextFactory<OlomocDb> factory)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(365);
    public const int Shown = 100;

    public async Task LogAsync(string admin, string action, string target, string? detail = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        db.AdminActions.Add(new AdminActionRow { At = now, Admin = admin, Action = action, Target = Cut(target, 64), Detail = detail is null ? null : Cut(detail, 200) });
        await db.SaveChangesAsync();
        var limit = now - KeepFor;
        await db.AdminActions.Where(a => a.At < limit).ExecuteDeleteAsync();
    }

    /// <summary>Les plus récentes d'abord.</summary>
    public async Task<List<AdminAction>> RecentAsync(int count = Shown)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.AdminActions.AsNoTracking().OrderByDescending(a => a.Id).Take(count).ToListAsync();
        return rows.Select(a => new AdminAction(new DateTimeOffset(DateTime.SpecifyKind(a.At, DateTimeKind.Utc)), a.Admin, a.Action, a.Target, a.Detail)).ToList();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
