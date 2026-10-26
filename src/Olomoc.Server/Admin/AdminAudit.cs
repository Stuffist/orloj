using Microsoft.EntityFrameworkCore;
using Olomoc.Accounts;
using Olomoc.Server.Data;

namespace Olomoc.Server.Admin;

/// <summary>
/// Log of the admins' actions (AdminActions table): who, what, on what, when. Entries older than
/// a year are deleted on every write.
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

    /// <summary>Most recent first.</summary>
    public async Task<List<AdminAction>> RecentAsync(int count = Shown)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.AdminActions.AsNoTracking().OrderByDescending(a => a.Id).Take(count).ToListAsync();
        return rows.Select(a => new AdminAction(new DateTimeOffset(DateTime.SpecifyKind(a.At, DateTimeKind.Utc)), a.Admin, a.Action, a.Target, a.Detail)).ToList();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
