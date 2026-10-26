namespace Olomoc.Server.Tournaments;

/// <summary>
/// Once a day: deletion of the tournaments unchanged for 90 days whose date has passed or is
/// missing. A tournament dated today or later is never deleted.
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
                if (removed > 0) log.LogInformation("{Count} inactive tournament(s) deleted after {Days} days.", removed, MaxAge.Days);
            }
            catch (IOException e) { log.LogWarning(e, "Tournament purge interrupted."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
