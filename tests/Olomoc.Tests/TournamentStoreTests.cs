using Olomoc.Server.Data;
using Olomoc.Server.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Tournament storage: purge of inactive tournaments, never of upcoming ones; backups.</summary>
public sealed class TournamentStoreTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 13);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "olomoc-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestDb.Delete(_dir);

    private static async Task<string> Add(TestDb db, TournamentStore store, DateOnly? date, int daysSinceChange)
    {
        var t = await store.CreateAsync(t => { t.Name = "T"; t.Date = date; });
        db.Change(TournamentStore.Kind, t!.Code, row => row.UpdatedAt = DateTime.UtcNow.AddDays(-daysSinceChange));
        return t.Code;
    }

    [Fact]
    public async Task Purge_of_inactive_past_or_undated_never_upcoming_tournaments()
    {
        var db = await TestDb.OpenAsync(_dir);
        var store = new TournamentStore(db);
        var oldPast = await Add(db, store, Today.AddDays(-100), 95);
        var oldUndated = await Add(db, store, null, 95);
        var oldFuture = await Add(db, store, Today.AddDays(200), 400);
        var recentPast = await Add(db, store, Today.AddDays(-100), 10);

        Assert.Equal(2, await store.PurgeAsync(TimeSpan.FromDays(90), Today));

        Assert.Null(await store.LoadAsync(oldPast));
        Assert.Null(await store.LoadAsync(oldUndated));
        Assert.NotNull(await store.LoadAsync(oldFuture));
        Assert.NotNull(await store.LoadAsync(recentPast));
    }

    [Fact]
    public async Task Daily_backup_the_seven_most_recent_kept()
    {
        var db = await TestDb.OpenAsync(_dir);
        var code = (await new TournamentStore(db).CreateAsync(t => t.Name = "Sauvé"))!.Code;
        var backup = new DbBackup(_dir, Microsoft.Extensions.Logging.Abstractions.NullLogger<DbBackup>.Instance);

        var day = new DateTime(2026, 10, 1);
        Assert.NotNull(await backup.BackupAsync(day));
        Assert.Null(await backup.BackupAsync(day.AddHours(5)));            // already made that day
        for (var i = 1; i <= 8; i++) await backup.BackupAsync(day.AddDays(i));

        var files = Directory.GetFiles(Path.Combine(_dir, "backups")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(DbBackup.Keep, files.Count);
        Assert.Equal("olomoc-20261003.db", files[0]);

        // A backup is a complete database, readable as is.
        var copy = Path.Combine(_dir, "copie");
        Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(_dir, "backups", "olomoc-20261009.db"), Path.Combine(copy, OlomocDb.FileName));
        Assert.Equal("Sauvé", (await new TournamentStore(await TestDb.OpenAsync(copy)).LoadAsync(code))!.Name);
    }
}
