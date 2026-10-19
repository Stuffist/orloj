using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Data;

namespace Olomoc.Tests;

/// <summary>Base SQLite d'un dossier de données temporaire (créée et mise à jour à l'ouverture).</summary>
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

    /// <summary>Document enregistré (sorte, code), ou null.</summary>
    public StoredDocumentRow? Find(string kind, string code)
    {
        using var db = CreateDbContext();
        return db.Documents.AsNoTracking().FirstOrDefault(d => d.Kind == kind && d.Code == code);
    }

    /// <summary>Modifie directement un document (dates, contenu illisible…).</summary>
    public void Change(string kind, string code, Action<StoredDocumentRow> change)
    {
        using var db = CreateDbContext();
        var row = db.Documents.Find(kind, code) ?? throw new InvalidOperationException($"{kind}/{code} absent");
        change(row);
        db.SaveChanges();
    }

    /// <summary>
    /// Supprime un dossier de données : les connexions SQLite gardées en réserve tiennent le
    /// fichier ouvert (Windows refuserait la suppression).
    /// </summary>
    public static void Delete(string dataDirectory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
    }
}
