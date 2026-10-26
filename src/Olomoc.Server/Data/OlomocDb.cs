using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Olomoc.Server.Data;

/// <summary>
/// The site's database: a single SQLite file (DATA_DIR/olomoc.db). Schema through EF Core
/// migrations (Migrations folder), applied at startup. Accounts (ASP.NET Core Identity AspNet*
/// tables), their cabals (SavedCabals) and the encryption keys of the session cookies
/// (DataProtectionKeys: a session survives a restart or a redeployment).
/// </summary>
public sealed class OlomocDb(DbContextOptions<OlomocDb> options) : IdentityDbContext<OlomocUser>(options), IDataProtectionKeyContext
{
    public const string FileName = "olomoc.db";

    /// <summary>JSON documents stored by kind and by code (tournaments, statistics archives).</summary>
    public DbSet<StoredDocumentRow> Documents => Set<StoredDocumentRow>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>Account cabals (and deletions, for the other devices).</summary>
    public DbSet<SavedCabalRow> SavedCabals => Set<SavedCabalRow>();

    /// <summary>Log of the admins' actions (admin panel), kept for a year.</summary>
    public DbSet<AdminActionRow> AdminActions => Set<AdminActionRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);        // Identity tables
        var doc = model.Entity<StoredDocumentRow>();
        doc.ToTable("Documents");
        doc.HasKey(d => new { d.Kind, d.Code });
        doc.Property(d => d.Kind).HasMaxLength(16);
        doc.Property(d => d.Code).HasMaxLength(16);
        doc.HasIndex(d => new { d.Kind, d.UpdatedAt });     // purge of inactive documents

        var cabal = model.Entity<SavedCabalRow>();
        cabal.ToTable("SavedCabals");
        cabal.Property(c => c.Name).HasMaxLength(Olomoc.Accounts.CabalSyncRules.MaxNameLength);
        cabal.Property(c => c.NormalizedName).HasMaxLength(Olomoc.Accounts.CabalSyncRules.MaxNameLength);
        cabal.HasIndex(c => new { c.UserId, c.NormalizedName }).IsUnique();
        cabal.HasOne<OlomocUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);   // account deleted: its cabals too

        // Plain usernames (no foreign key): the entry survives the deletion of the account.
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
    /// At startup: creates or updates the schema, switches to WAL journaling (reads during
    /// writes, safer writes in case of a crash).
    /// </summary>
    public static async Task MigrateAsync(IDbContextFactory<OlomocDb> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}

/// <summary>
/// Stored document: kind (tournament, stats…), code, dates (UTC) and JSON content. The dates are
/// columns for the purge; everything else lives in the document.
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
/// An account's cabal, under its name (unique per account, case-insensitive): JSON of the export,
/// save date (UTC). Deleted: <see cref="Deleted"/>, without content, kept so that the other
/// devices learn about it.
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

/// <summary>An admin's action (UTC): action key, target (username or code), optional detail.</summary>
public sealed class AdminActionRow
{
    public int Id { get; set; }
    public DateTime At { get; set; }
    public string Admin { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>For "dotnet ef migrations add": the database is created separately, without starting the site.</summary>
public sealed class OlomocDbDesignFactory : IDesignTimeDbContextFactory<OlomocDb>
{
    public OlomocDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OlomocDb>();
        OlomocDb.Configure(options, Path.GetTempPath());
        return new OlomocDb(options.Options);
    }
}
