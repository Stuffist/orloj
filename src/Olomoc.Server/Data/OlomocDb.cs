using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Olomoc.Server.Data;

/// <summary>
/// Base du site : un seul fichier SQLite (DATA_DIR/olomoc.db). Schéma par migrations EF Core
/// (dossier Migrations), appliquées au démarrage.
/// </summary>
public sealed class OlomocDb(DbContextOptions<OlomocDb> options) : DbContext(options)
{
    public const string FileName = "olomoc.db";

    /// <summary>Documents JSON rangés par sorte et par code (tournois, archives de statistiques).</summary>
    public DbSet<StoredDocumentRow> Documents => Set<StoredDocumentRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var doc = model.Entity<StoredDocumentRow>();
        doc.ToTable("Documents");
        doc.HasKey(d => new { d.Kind, d.Code });
        doc.Property(d => d.Kind).HasMaxLength(16);
        doc.Property(d => d.Code).HasMaxLength(16);
        doc.HasIndex(d => new { d.Kind, d.UpdatedAt });     // purge des inactifs
    }

    public static string ConnectionString(string dataDirectory) =>
        new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, FileName) }.ToString();

    public static void Configure(DbContextOptionsBuilder options, string dataDirectory) =>
        options.UseSqlite(ConnectionString(dataDirectory));

    /// <summary>
    /// Au démarrage : crée ou met à jour le schéma, passe en journal WAL (lectures pendant les
    /// écritures, écritures plus sûres en cas d'arrêt brutal).
    /// </summary>
    public static async Task MigrateAsync(IDbContextFactory<OlomocDb> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}

/// <summary>
/// Document enregistré : sorte (tournament, stats…), code, dates (UTC) et contenu JSON. Les dates
/// sont en colonnes pour la purge ; tout le reste vit dans le document.
/// </summary>
public sealed class StoredDocumentRow
{
    public string Kind { get; set; } = "";
    public string Code { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Json { get; set; } = "";
}

/// <summary>Pour « dotnet ef migrations add » : la base est créée à part, sans démarrer le site.</summary>
public sealed class OlomocDbDesignFactory : IDesignTimeDbContextFactory<OlomocDb>
{
    public OlomocDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OlomocDb>();
        OlomocDb.Configure(options, Path.GetTempPath());
        return new OlomocDb(options.Options);
    }
}
