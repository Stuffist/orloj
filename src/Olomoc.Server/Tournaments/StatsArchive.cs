using System.Text.Json;
using Olomoc.Tournaments;
using Olomoc.Tournaments.Stats;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Archives anonymes des tournois terminés et comptés, une par tournoi (stats/{code}.json), hors de
/// la purge des tournois : les statistiques couvrent tout l'historique. Les chiffres sont calculés
/// une fois, gardés en mémoire, et recalculés après chaque ajout ou retrait d'archive.
/// </summary>
public sealed class StatsArchive
{
    private readonly string _directory;
    private readonly object _lock = new();
    private StatsView? _stats;

    public StatsArchive(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <summary>Archive le tournoi s'il est terminé et compté ; sinon retire son archive.</summary>
    public void Update(Tournament t, DateTimeOffset finishedAt)
    {
        if (t.Phase != TournamentPhase.Finished || t.ExcludeFromStats)
        {
            Remove(t.Code);
            return;
        }
        var path = PathOf(t.Code);
        lock (_lock)
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(ArchivedTournament.From(t, finishedAt), TournamentJson.Options));
            File.Move(path + ".tmp", path, overwrite: true);
            _stats = null;
        }
    }

    public void Remove(string code)
    {
        lock (_lock)
        {
            if (!File.Exists(PathOf(code))) return;
            File.Delete(PathOf(code));
            _stats = null;
        }
    }

    public bool Contains(string code) => File.Exists(PathOf(code));

    public StatsView Stats()
    {
        lock (_lock)
        {
            return _stats ??= TournamentStats.Compute(Directory.EnumerateFiles(_directory, "*.json").Select(Read).OfType<ArchivedTournament>());
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

    private string PathOf(string code) => Path.Combine(_directory, code + ".json");

    private static ArchivedTournament? Read(string path)
    {
        try { return JsonSerializer.Deserialize<ArchivedTournament>(File.ReadAllText(path), TournamentJson.Options); }
        catch (JsonException) { return null; }       // archive illisible : ignorée, laissée à examiner
    }
}
