using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Data;

namespace Olomoc.Server.Hosting;

/// <summary>
/// JSON documents stored in the database (Documents table), under a public 6-character code and a
/// kind specific to each store. A single server instance: one lock per document serializes the
/// changes (read, change, write).
/// </summary>
public class DocumentStore<T> where T : class, IStoredDocument, new()
{
    // Readable public codes: no 0/O, no 1/I/L.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 6;

    private readonly IDbContextFactory<OlomocDb> _db;
    private readonly string _kind;
    private readonly int _maxDocuments;
    private readonly JsonSerializerOptions _json;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public DocumentStore(IDbContextFactory<OlomocDb> db, string kind, int maxDocuments, JsonSerializerOptions json)
    {
        _db = db;
        _kind = kind;
        _maxDocuments = maxDocuments;
        _json = json;
    }

    public static string? Normalize(string? code)
    {
        var upper = code?.Trim().ToUpperInvariant();
        return upper is { Length: CodeLength } && upper.All(Alphabet.Contains) ? upper : null;
    }

    /// <summary>Maximum number of documents of this kind.</summary>
    public int MaxDocuments => _maxDocuments;

    /// <summary>Codes of the stored documents.</summary>
    public IEnumerable<string> Codes()
    {
        using var db = _db.CreateDbContext();
        return db.Documents.Where(d => d.Kind == _kind).Select(d => d.Code).ToList();
    }

    /// <summary>All documents of this kind (admin panel); unreadable ones are skipped.</summary>
    public async Task<List<T>> ListAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        var rows = await db.Documents.AsNoTracking().Where(d => d.Kind == _kind).Select(d => d.Json).ToListAsync();
        var docs = new List<T>(rows.Count);
        foreach (var json in rows)
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(json, _json) is { } doc) docs.Add(doc);
            }
            catch (JsonException) { /* unreadable document: left as is, to be examined */ }
        }
        return docs;
    }

    public async Task<T?> LoadAsync(string code)
    {
        if (Normalize(code) is not { } c) return null;
        await using var db = await _db.CreateDbContextAsync();
        var json = await db.Documents.Where(d => d.Kind == _kind && d.Code == c).Select(d => d.Json).FirstOrDefaultAsync();
        return json is null ? null : JsonSerializer.Deserialize<T>(json, _json);
    }

    /// <summary>New document under an unused code; null if the maximum number is reached.</summary>
    public async Task<T?> CreateAsync(Action<T> init)
    {
        await using var db = await _db.CreateDbContextAsync();
        if (await db.Documents.CountAsync(d => d.Kind == _kind) >= _maxDocuments) return null;
        while (true)
        {
            var code = new string(Enumerable.Range(0, CodeLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
            var gate = _locks.GetOrAdd(code, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                if (await db.Documents.AnyAsync(d => d.Kind == _kind && d.Code == code)) continue;
                var doc = new T { Code = code, CreatedAt = DateTimeOffset.UtcNow };
                init(doc);
                var row = new StoredDocumentRow { Kind = _kind, Code = code, CreatedAt = doc.CreatedAt.UtcDateTime };
                Write(row, doc);
                db.Documents.Add(row);
                await db.SaveChangesAsync();
                return doc;
            }
            finally { gate.Release(); }
        }
    }

    /// <summary>
    /// Change under lock: <paramref name="change"/> receives the up-to-date document and says whether
    /// to save it. Null if the document does not exist.
    /// </summary>
    public async Task<TResult?> UpdateAsync<TResult>(string code, Func<T, (bool Save, TResult Result)> change) where TResult : class
    {
        if (Normalize(code) is not { } c) return null;
        var gate = _locks.GetOrAdd(c, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var db = await _db.CreateDbContextAsync();
            if (await db.Documents.FindAsync(_kind, c) is not { } row || Read(row) is not { } doc) return null;
            var (save, result) = change(doc);
            if (save)
            {
                Write(row, doc);
                await db.SaveChangesAsync();
            }
            return result;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Deletion under lock, if <paramref name="decide"/> accepts it (rights checked on the
    /// up-to-date document). Null if the document does not exist.
    /// </summary>
    public async Task<TResult?> DeleteAsync<TResult>(string code, Func<T, (bool Delete, TResult Result)> decide) where TResult : class
    {
        if (Normalize(code) is not { } c) return null;
        var gate = _locks.GetOrAdd(c, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var db = await _db.CreateDbContextAsync();
            if (await db.Documents.FindAsync(_kind, c) is not { } row || Read(row) is not { } doc) return null;
            var (delete, result) = decide(doc);
            if (delete)
            {
                db.Documents.Remove(row);
                await db.SaveChangesAsync();
            }
            return result;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Deletes the documents unchanged since <paramref name="maxAge"/> that
    /// <paramref name="removable"/> accepts; returns their number.
    /// </summary>
    public async Task<int> PurgeAsync(TimeSpan maxAge, Func<T, bool> removable)
    {
        var limit = DateTime.UtcNow - maxAge;
        List<string> candidates;
        await using (var db = await _db.CreateDbContextAsync())
            candidates = await db.Documents.Where(d => d.Kind == _kind && d.UpdatedAt < limit).Select(d => d.Code).ToListAsync();
        var removed = 0;
        foreach (var code in candidates)
        {
            try
            {
                if (await DeleteAsync(code, doc => removable(doc) ? (true, Removed) : (false, Kept)) == Removed) removed++;
            }
            catch (JsonException) { /* unreadable document: left as is, to be examined */ }
        }
        return removed;
    }

    // DeleteAsync result when the purge deleted the document (Kept otherwise).
    private static readonly object Removed = new(), Kept = new();

    private T? Read(StoredDocumentRow row) => JsonSerializer.Deserialize<T>(row.Json, _json);

    private void Write(StoredDocumentRow row, T doc)
    {
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = doc.UpdatedAt.UtcDateTime;
        row.Json = JsonSerializer.Serialize(doc, _json);
    }
}
