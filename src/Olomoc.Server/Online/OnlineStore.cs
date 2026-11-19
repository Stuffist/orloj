using Microsoft.EntityFrameworkCore;
using Olomoc.Online;
using Olomoc.Server.Data;
using Olomoc.Server.Hosting;
using Olomoc.Tournaments;

namespace Olomoc.Server.Online;

/// <summary>Online games stored in the database, one JSON document per game (see <see cref="DocumentStore{T}"/>).</summary>
public sealed class OnlineStore(IDbContextFactory<OlomocDb> db, int maxGames = 2000)
    : DocumentStore<OnlineGame>(db, Kind, maxGames, TournamentJson.Options)
{
    public const string Kind = "online";
}

/// <summary>
/// Every 6 hours: deletion of the games unchanged for 7 days (a game is played in an evening;
/// the link of an interrupted game stays valid for a week). A finished game whose result is not
/// recorded yet goes into the statistics first (<see cref="OnlineResults"/>).
/// </summary>
public sealed class OnlinePurge(OnlineStore store, OnlineResults results, ILogger<OnlinePurge> log) : BackgroundService
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                var removed = await PurgeAsync(store, results);
                if (removed > 0) log.LogInformation("{Count} online game(s) inactive for {Days} days deleted.", removed, MaxAge.Days);
            }
            catch (IOException e) { log.LogWarning(e, "Purge of online games interrupted."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    /// <summary>
    /// Deletes the games unchanged for <see cref="MaxAge"/>, after recording the results of the finished ones
    /// (if they are not yet). Also the admin panel's purge by hand. Returns the number of games deleted.
    /// </summary>
    public static async Task<int> PurgeAsync(OnlineStore store, OnlineResults results)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var code in await store.StaleAsync(MaxAge))
            if (await store.LoadAsync(code) is { } game) await results.RecordBeforePurgeAsync(game, now);
        return await store.PurgeAsync(MaxAge, _ => true);
    }
}
