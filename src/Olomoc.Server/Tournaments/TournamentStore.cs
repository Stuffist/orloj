using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Olomoc.Tournaments;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Tournois enregistrés sur le disque, un fichier JSON par tournoi (pas de base de données).
/// Une seule instance du serveur : un verrou par tournoi sérialise les modifications, et
/// chaque écriture passe par un fichier temporaire renommé (jamais de fichier à moitié écrit).
/// </summary>
public sealed class TournamentStore
{
    // Codes publics lisibles : ni 0/O, ni 1/I/L.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 6;

    private readonly string _directory;
    private readonly int _maxTournaments;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public TournamentStore(string directory, int maxTournaments = 1000)
    {
        _directory = directory;
        _maxTournaments = maxTournaments;
        Directory.CreateDirectory(directory);
    }

    public static string? Normalize(string? code)
    {
        var upper = code?.Trim().ToUpperInvariant();
        return upper is { Length: CodeLength } && upper.All(Alphabet.Contains) ? upper : null;
    }

    /// <summary>Codes des tournois enregistrés.</summary>
    public IEnumerable<string> Codes() =>
        Directory.EnumerateFiles(_directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>();

    public async Task<Tournament?> LoadAsync(string code)
    {
        if (Normalize(code) is not { } c || !File.Exists(PathOf(c))) return null;
        await using var stream = File.OpenRead(PathOf(c));
        return await JsonSerializer.DeserializeAsync<Tournament>(stream, TournamentJson.Options);
    }

    /// <summary>Nouveau tournoi sous un code inédit ; null si le nombre maximal est atteint.</summary>
    public async Task<Tournament?> CreateAsync(Action<Tournament> init)
    {
        if (Directory.EnumerateFiles(_directory, "*.json").Count() >= _maxTournaments) return null;
        while (true)
        {
            var code = new string(Enumerable.Range(0, CodeLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
            var gate = _locks.GetOrAdd(code, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                if (File.Exists(PathOf(code))) continue;
                var t = new Tournament { Code = code, CreatedAt = DateTimeOffset.UtcNow };
                init(t);
                await SaveAsync(t);
                return t;
            }
            finally { gate.Release(); }
        }
    }

    /// <summary>
    /// Modification sous verrou : <paramref name="change"/> reçoit le tournoi à jour et dit s'il
    /// faut l'enregistrer. Null si le tournoi n'existe pas.
    /// </summary>
    public async Task<T?> UpdateAsync<T>(string code, Func<Tournament, (bool Save, T Result)> change) where T : class
    {
        if (Normalize(code) is not { } c) return null;
        var gate = _locks.GetOrAdd(c, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (await LoadAsync(c) is not { } t) return null;
            var (save, result) = change(t);
            if (save) await SaveAsync(t);
            return result;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Suppression sous verrou, si <paramref name="decide"/> l'accepte (droits vérifiés sur le
    /// tournoi à jour). Null si le tournoi n'existe pas.
    /// </summary>
    public async Task<T?> DeleteAsync<T>(string code, Func<Tournament, (bool Delete, T Result)> decide) where T : class
    {
        if (Normalize(code) is not { } c) return null;
        var gate = _locks.GetOrAdd(c, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (await LoadAsync(c) is not { } t) return null;
            var (delete, result) = decide(t);
            if (delete) File.Delete(PathOf(c));
            return result;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Supprime les tournois sans modification depuis <paramref name="maxAge"/>, sauf ceux dont la
    /// date est aujourd'hui ou à venir (jamais supprimés) ; renvoie leur nombre.
    /// </summary>
    public async Task<int> PurgeAsync(TimeSpan maxAge, DateOnly today)
    {
        var limit = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (File.GetLastWriteTimeUtc(file) >= limit) continue;
            try
            {
                await DeleteAsync(Path.GetFileNameWithoutExtension(file), t => (t.Date is not { } date || date < today, Done));
                if (!File.Exists(file)) removed++;
            }
            catch (JsonException) { /* fichier illisible : laissé tel quel, à examiner */ }
        }
        return removed;
    }

    private static readonly object Done = new();

    private string PathOf(string code) => Path.Combine(_directory, code + ".json");

    private async Task SaveAsync(Tournament t)
    {
        t.UpdatedAt = DateTimeOffset.UtcNow;
        var path = PathOf(t.Code);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, t, TournamentJson.Options);
        File.Move(temp, path, overwrite: true);
    }
}
