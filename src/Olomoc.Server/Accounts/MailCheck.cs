using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Data;

namespace Olomoc.Server.Accounts;

/// <summary>
/// E-mail de contrôle (MAIL_CHECK_TO) : un court message tous les <see cref="Interval"/>
/// jours. Il garde la clé SMTP active (Brevo la désactive après 90 jours sans envoi) et montre
/// que l'envoi marche encore. Date du dernier envoi réussi dans la base : un redémarrage n'en
/// renvoie pas ; un échec est réessayé à la vérification suivante (toutes les 6 heures).
/// </summary>
public sealed class MailCheck(string? to, IMailSender sender, IDbContextFactory<OlomocDb> factory, ILogger<MailCheck> log)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(60);   // bien avant les 90 jours de Brevo
    private const string Kind = "settings", Code = "mail-check";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (string.IsNullOrWhiteSpace(to)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try { await CheckAsync(DateTime.UtcNow, stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "E-mail de contrôle non envoyé : vérifier SMTP_* dans le .env."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    /// <summary>Envoie le message s'il est dû ; renvoie true s'il est parti.</summary>
    public async Task<bool> CheckAsync(DateTime now, CancellationToken stop = default)
    {
        await using var db = await factory.CreateDbContextAsync(stop);
        var last = await db.Documents.FindAsync([Kind, Code], stop);
        if (last is not null && now - last.UpdatedAt < Interval) return false;

        await sender.SendAsync(AccountMails.Check(to!.Trim(), now), stop);
        if (last is null) db.Documents.Add(last = new StoredDocumentRow { Kind = Kind, Code = Code, CreatedAt = now, Json = "{}" });
        last.UpdatedAt = now;
        await db.SaveChangesAsync(stop);
        log.LogInformation("E-mail de contrôle envoyé à {To}.", to);
        return true;
    }
}
