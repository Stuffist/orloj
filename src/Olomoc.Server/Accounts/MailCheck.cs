using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Data;

namespace Olomoc.Server.Accounts;

/// <summary>
/// Check email (MAIL_CHECK_TO): a short message every <see cref="Interval"/> days. It keeps the SMTP
/// key active (Brevo disables it after 90 days without sending) and shows that sending still works.
/// Date of the last successful send in the database: a restart does not send another one; a failure
/// is retried at the next check (every 6 hours).
/// </summary>
public sealed class MailCheck(string? to, IMailSender sender, IDbContextFactory<OlomocDb> factory, ILogger<MailCheck> log)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(60);   // well before Brevo's 90 days
    private const string Kind = "settings", Code = "mail-check";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (string.IsNullOrWhiteSpace(to)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try { await CheckAsync(DateTime.UtcNow, stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Check email not sent: verify SMTP_* in the .env file."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    /// <summary>Sends the message if it is due; returns true if it was sent.</summary>
    public async Task<bool> CheckAsync(DateTime now, CancellationToken stop = default)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var last = await db.Documents.FindAsync([Kind, Code], stop);
        if (last is not null && now - last.UpdatedAt < Interval) return false;

        await sender.SendAsync(AccountMails.Check(to!.Trim(), now), stop);
        if (last is null) db.Documents.Add(last = new StoredDocumentRow { Kind = Kind, Code = Code, CreatedAt = now, Json = "{}" });
        last.UpdatedAt = now;
        await db.SaveChangesAsync(stop);
        log.LogInformation("Check email sent to {To}.", to);
        return true;
    }
}
