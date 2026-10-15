using Microsoft.EntityFrameworkCore;
using Olomoc.Online;
using Olomoc.Server.Data;
using Olomoc.Server.Hosting;
using Olomoc.Tournaments;

namespace Olomoc.Server.Online;

/// <summary>Parties en ligne enregistrées dans la base, un document JSON par partie (voir <see cref="DocumentStore{T}"/>).</summary>
public sealed class OnlineStore(IDbContextFactory<OlomocDb> db, int maxGames = 2000)
    : DocumentStore<OnlineGame>(db, Kind, maxGames, TournamentJson.Options)
{
    public const string Kind = "online";
}

/// <summary>
/// Toutes les 6 heures : suppression des parties sans modification depuis 7 jours (une partie
/// se joue en une soirée ; le lien d'une partie interrompue reste valable une semaine).
/// </summary>
public sealed class OnlinePurge(OnlineStore store, ILogger<OnlinePurge> log) : BackgroundService
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                var removed = await store.PurgeAsync(MaxAge, _ => true);
                if (removed > 0) log.LogInformation("{Count} partie(s) en ligne inactive(s) depuis {Days} jours supprimée(s).", removed, MaxAge.Days);
            }
            catch (IOException e) { log.LogWarning(e, "Purge des parties en ligne interrompue."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
