namespace Olomoc.Server.Tournaments;

/// <summary>
/// Une fois par jour : suppression des tournois sans modification depuis 90 jours, dont la date
/// est passée ou absente. Un tournoi daté d'aujourd'hui ou plus tard n'est jamais supprimé.
/// </summary>
public sealed class TournamentPurge(TournamentStore store, ILogger<TournamentPurge> log) : BackgroundService
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                var removed = await store.PurgeAsync(MaxAge, DateOnly.FromDateTime(DateTime.UtcNow));
                if (removed > 0) log.LogInformation("{Count} tournoi(s) inactif(s) depuis {Days} jours supprimé(s).", removed, MaxAge.Days);
            }
            catch (IOException e) { log.LogWarning(e, "Purge des tournois interrompue."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
