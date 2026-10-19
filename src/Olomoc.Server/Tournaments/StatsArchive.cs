using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Data;
using Olomoc.Tournaments;
using Olomoc.Tournaments.Stats;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Archives anonymes des tournois terminés et comptés, une par tournoi (documents « stats » de la
/// base, sous le code du tournoi), hors de la purge des tournois : les statistiques couvrent tout
/// l'historique. Les chiffres sont calculés une fois, gardés en mémoire, et recalculés après
/// chaque ajout ou retrait d'archive.
/// </summary>
public sealed class StatsArchive(IDbContextFactory<OlomocDb> factory)
{
    public const string Kind = "stats";

    private readonly object _lock = new();
    private StatsView? _stats;

    /// <summary>Archive le tournoi s'il est terminé et compté ; sinon retire son archive.</summary>
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
            _stats = null;
        }
    }

    public void Remove(string code)
    {
        lock (_lock)
        {
            using var db = factory.CreateDbContext();
            if (db.Documents.Where(d => d.Kind == Kind && d.Code == code).ExecuteDelete() > 0) _stats = null;
        }
    }

    public bool Contains(string code)
    {
        using var db = factory.CreateDbContext();
        return db.Documents.Any(d => d.Kind == Kind && d.Code == code);
    }

    public StatsView Stats()
    {
        lock (_lock)
        {
            if (_stats is not null) return _stats;
            using var db = factory.CreateDbContext();
            var archives = db.Documents.Where(d => d.Kind == Kind).Select(d => d.Json).AsEnumerable().Select(Read).OfType<ArchivedTournament>();
            return _stats = TournamentStats.Compute(archives.ToList());
        }
    }

    /// <summary>Au démarrage : archive les tournois terminés et comptés qui n'en ont pas encore.</summary>
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
        catch (JsonException) { return null; }       // archive illisible : ignorée, laissée à examiner
    }
}
