using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Data;
using Olomoc.Server.Hosting;
using Olomoc.Tournaments;
using Olomoc.Tournaments.Stats;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Anonymous archives of the finished and counted tournaments, one per tournament ("stats"
/// documents of the database, under the tournament's code), outside the tournament purge: the
/// statistics cover the whole history. After a reset of the statistics (<see cref="StatsReset"/>),
/// only the tournaments finished afterwards count (the archives stay, for the export). The figures
/// are computed once, kept in memory, and computed again after each archive added or removed (or a
/// reset); those of a period (<see cref="StatsPeriod"/>) on demand.
/// </summary>
public sealed class StatsArchive(IDbContextFactory<OlomocDb> factory, SiteSettingsStore settings)
{
    public const string Kind = "stats";

    private readonly object _lock = new();
    private StatsView? _stats;

    /// <summary>Archives the tournament if it is finished and counted; otherwise removes its archive.</summary>
    public void Update(Tournament t, DateTimeOffset finishedAt)
    {
        if (t.Phase != TournamentPhase.Finished || t.ExcludeFromStats)
        {
            Remove(t.Code);
            return;
        }
        var json = JsonSerializer.Serialize(ArchivedTournament.From(t, finishedAt), TournamentJson.Options);
        lock (_lock)
        {
            using var db = factory.CreateDbContext();
            var now = DateTime.UtcNow;
            if (db.Documents.Find(Kind, t.Code) is { } row) row.Json = json;
            else db.Documents.Add(row = new StoredDocumentRow { Kind = Kind, Code = t.Code, CreatedAt = now, Json = json });
            row.UpdatedAt = now;
            db.SaveChanges();
            Forget();
        }
    }

    public void Remove(string code)
    {
        lock (_lock)
        {
            using var db = factory.CreateDbContext();
            if (db.Documents.Where(d => d.Kind == Kind && d.Code == code).ExecuteDelete() > 0) Forget();
        }
    }

    public bool Contains(string code)
    {
        using var db = factory.CreateDbContext();
        return db.Documents.Any(d => d.Kind == Kind && d.Code == code);
    }

    /// <summary>Statistics of the archived tournaments that count (since the reset; in the period).</summary>
    public Task<StatsView> StatsAsync(StatsPeriod? period = null)
    {
        lock (_lock)
        {
            var counted = LoadCounted();
            if (period is { IsAll: false } p)
                return Task.FromResult(TournamentStats.Compute(counted.Tournaments.Where(p.Contains)) with { Since = counted.Since });
            return Task.FromResult(_stats ??= TournamentStats.Compute(counted.Tournaments) with { Since = counted.Since });
        }
    }

    /// <summary>
    /// All the archives and the codes of their tournaments (the export). The same object until a change.
    /// </summary>
    public Archives CurrentArchives()
    {
        lock (_lock) return LoadArchives();
    }

    /// <summary>
    /// The archives that count (finished since the reset), and the codes of all the archived
    /// tournaments (for the statistics of everything: a zone game copied from an archived tournament
    /// is not counted twice). The same object until a change.
    /// </summary>
    public Counted CountedArchives()
    {
        lock (_lock) return LoadCounted();
    }

    public sealed record Counted(IReadOnlyList<ArchivedTournament> Tournaments, IReadOnlySet<string> Codes, DateTimeOffset? Since);

    private Counted? _counted;

    // Under the lock. Reset (or cancelled) since the last time: computed again.
    private Counted LoadCounted()
    {
        var since = settings.Current.StatsSince;
        if (_counted is { } cached && cached.Since == since) return cached;
        var all = LoadArchives();
        _stats = null;
        return _counted = new Counted(all.Tournaments.Where(t => StatsReset.Counts(t.FinishedAt, since)).ToList(), all.Codes, since);
    }

    public sealed record Archives(IReadOnlyList<ArchivedTournament> Tournaments, IReadOnlySet<string> Codes);

    private Archives? _archives;

    // Under the lock: an archive was added, changed or removed.
    private void Forget()
    {
        _stats = null;
        _archives = null;
        _counted = null;
    }

    // Under the lock.
    private Archives LoadArchives()
    {
        if (_archives is { } cached) return cached;
        using var db = factory.CreateDbContext();
        var rows = db.Documents.Where(d => d.Kind == Kind).Select(d => new { d.Code, d.Json }).AsEnumerable()
            .Select(d => (d.Code, Archive: Read(d.Json))).Where(x => x.Archive is not null).ToList();
        return _archives = new Archives(rows.Select(x => x.Archive!).ToList(), rows.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>At startup: archives the finished and counted tournaments that do not have one yet.</summary>
    public async Task<int> BackfillAsync(TournamentStore store)
    {
        var added = 0;
        foreach (var code in store.Codes().Where(c => !Contains(c)))
        {
            if (await store.LoadAsync(code) is not { Phase: TournamentPhase.Finished, ExcludeFromStats: false } t) continue;
            Update(t, t.UpdatedAt);
            added++;
        }
        return added;
    }

    private static ArchivedTournament? Read(string json)
    {
        try { return JsonSerializer.Deserialize<ArchivedTournament>(json, TournamentJson.Options); }
        catch (JsonException) { return null; }       // unreadable archive: ignored, left to be examined
    }
}
