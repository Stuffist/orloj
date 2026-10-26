using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Data;

namespace Olomoc.Tests;

/// <summary>SQLite database of a temporary data folder (created and updated when opened).</summary>
public sealed class TestDb : IDbContextFactory<OlomocDb>
{
    private readonly DbContextOptions<OlomocDb> _options;

    private TestDb(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var options = new DbContextOptionsBuilder<OlomocDb>();
        OlomocDb.Configure(options, dataDirectory);
        _options = options.Options;
    }

    public static async Task<TestDb> OpenAsync(string dataDirectory)
    {
        var db = new TestDb(dataDirectory);
        await OlomocDb.MigrateAsync(db);
        return db;
    }

    public OlomocDb CreateDbContext() => new(_options);

    /// <summary>Stored document (kind, code), or null.</summary>
    public StoredDocumentRow? Find(string kind, string code)
    {
        using var db = CreateDbContext();
        return db.Documents.AsNoTracking().FirstOrDefault(d => d.Kind == kind && d.Code == code);
    }

    /// <summary>Changes a document directly (dates, unreadable content…).</summary>
    public void Change(string kind, string code, Action<StoredDocumentRow> change)
    {
        using var db = CreateDbContext();
        var row = db.Documents.Find(kind, code) ?? throw new InvalidOperationException($"{kind}/{code} missing");
        change(row);
        db.SaveChanges();
    }

    /// <summary>
    /// Deletes a data folder: the pooled SQLite connections keep the file open (Windows would
    /// refuse the deletion).
    /// </summary>
    public static void Delete(string dataDirectory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
    }
}
