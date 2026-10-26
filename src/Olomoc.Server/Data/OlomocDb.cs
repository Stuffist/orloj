using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Olomoc.Server.Data;

/// <summary>
/// Base du site : un seul fichier SQLite (DATA_DIR/olomoc.db). Schéma par migrations EF Core
/// (dossier Migrations), appliquées au démarrage. Comptes (tables AspNet* d'ASP.NET Core
/// Identity), leurs cabales (SavedCabals) et clés de chiffrement des cookies de session
/// (DataProtectionKeys : une session survit à un redémarrage ou à un redéploiement).
/// </summary>
public sealed class OlomocDb(DbContextOptions<OlomocDb> options) : IdentityDbContext<OlomocUser>(options), IDataProtectionKeyContext
{
    public const string FileName = "olomoc.db";

    /// <summary>Documents JSON rangés par sorte et par code (tournois, archives de statistiques).</summary>
    public DbSet<StoredDocumentRow> Documents => Set<StoredDocumentRow>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>Cabales des comptes (et suppressions, pour les autres appareils).</summary>
    public DbSet<SavedCabalRow> SavedCabals => Set<SavedCabalRow>();

    /// <summary>Journal des actions des admins (panneau d'administration), gardé un an.</summary>
    public DbSet<AdminActionRow> AdminActions => Set<AdminActionRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);        // tables d'Identity
        var doc = model.Entity<StoredDocumentRow>();
        doc.ToTable("Documents");
        doc.HasKey(d => new { d.Kind, d.Code });
        doc.Property(d => d.Kind).HasMaxLength(16);
        doc.Property(d => d.Code).HasMaxLength(16);
        doc.HasIndex(d => new { d.Kind, d.UpdatedAt });     // purge des inactifs

        var cabal = model.Entity<SavedCabalRow>();
        cabal.ToTable("SavedCabals");
        cabal.Property(c => c.Name).HasMaxLength(Olomoc.Accounts.CabalSyncRules.MaxNameLength);
        cabal.Property(c => c.NormalizedName).HasMaxLength(Olomoc.Accounts.CabalSyncRules.MaxNameLength);
        cabal.HasIndex(c => new { c.UserId, c.NormalizedName }).IsUnique();
        cabal.HasOne<OlomocUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);   // compte supprimé : ses cabales aussi

        // Pseudos en clair (pas de clé étrangère) : l'entrée survit à la suppression du compte.
        var action = model.Entity<AdminActionRow>();
        action.ToTable("AdminActions");
        action.Property(a => a.Admin).HasMaxLength(Olomoc.Accounts.AccountRules.MaxUserName);
        action.Property(a => a.Action).HasMaxLength(32);
        action.Property(a => a.Target).HasMaxLength(64);
        action.Property(a => a.Detail).HasMaxLength(200);
        action.HasIndex(a => a.At);
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

/// <summary>
/// Cabale d'un compte, sous son nom (unique par compte, sans tenir compte de la casse) : JSON de
/// l'export, date de sauvegarde (UTC). Supprimée : <see cref="Deleted"/>, sans contenu, gardée
/// pour que les autres appareils l'apprennent.
/// </summary>
public sealed class SavedCabalRow
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public bool Deleted { get; set; }
    public string? Json { get; set; }
}

/// <summary>Action d'un admin (UTC) : clé de l'action, cible (pseudo ou code), détail facultatif.</summary>
public sealed class AdminActionRow
{
    public int Id { get; set; }
    public DateTime At { get; set; }
    public string Admin { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Detail { get; set; }
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
