using Olomoc.Server.Data;
using Olomoc.Server.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Stockage des tournois : purge des tournois inactifs, jamais des tournois à venir.</summary>
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
    public async Task Purge_des_inactifs_passes_ou_sans_date_jamais_des_tournois_a_venir()
    {
        var db = await TestDb.OpenAsync(_dir);
        var store = new TournamentStore(db);
        var oldPast = await Add(db, store, Today.AddDays(-100), 95);
        var oldUndated = await Add(db, store, null, 95);
        var oldToday = await Add(db, store, Today, 95);
        var oldFuture = await Add(db, store, Today.AddDays(200), 400);
        var recentPast = await Add(db, store, Today.AddDays(-100), 10);
        var unreadable = await Add(db, store, null, 200);
        db.Change(TournamentStore.Kind, unreadable, row => row.Json = "{ illisible");

        Assert.Equal(2, await store.PurgeAsync(TimeSpan.FromDays(90), Today));

        Assert.Null(await store.LoadAsync(oldPast));
        Assert.Null(await store.LoadAsync(oldUndated));
        Assert.NotNull(await store.LoadAsync(oldToday));
        Assert.NotNull(await store.LoadAsync(oldFuture));
        Assert.NotNull(await store.LoadAsync(recentPast));
        Assert.NotNull(db.Find(TournamentStore.Kind, unreadable));        // illisible : laissé à examiner
    }

    [Fact]
    public async Task Suppression_seulement_si_acceptee()
    {
        var db = await TestDb.OpenAsync(_dir);
        var store = new TournamentStore(db);
        var code = await Add(db, store, null, 0);
        Assert.Equal("refusée", await store.DeleteAsync(code, _ => (false, "refusée")));
        Assert.NotNull(await store.LoadAsync(code));
        Assert.Equal("ok", await store.DeleteAsync(code, _ => (true, "ok")));
        Assert.Null(await store.LoadAsync(code));
        Assert.Null(await store.DeleteAsync(code, _ => (true, "ok")));       // n'existe plus
    }

    [Fact]
    public async Task Modification_enregistree_seulement_si_demandee()
    {
        var db = await TestDb.OpenAsync(_dir);
        var store = new TournamentStore(db);
        var code = await Add(db, store, null, 3);
        var before = db.Find(TournamentStore.Kind, code)!.UpdatedAt;

        Assert.Equal("non", await store.UpdateAsync(code, t => { t.Name = "Perdu"; return (false, "non"); }));
        Assert.Equal("T", (await store.LoadAsync(code))!.Name);
        Assert.Equal(before, db.Find(TournamentStore.Kind, code)!.UpdatedAt);

        Assert.Equal("oui", await store.UpdateAsync(code, t => { t.Name = "Gardé"; return (true, "oui"); }));
        Assert.Equal("Gardé", (await store.LoadAsync(code))!.Name);
        Assert.True(db.Find(TournamentStore.Kind, code)!.UpdatedAt > before);
        Assert.Null(await store.UpdateAsync("ZZZZZZ", t => (true, "?")));
    }

    [Fact]
    public async Task Nombre_maximal_de_tournois()
    {
        var db = await TestDb.OpenAsync(_dir);
        var store = new TournamentStore(db, maxTournaments: 2);
        Assert.NotNull(await store.CreateAsync(_ => { }));
        Assert.NotNull(await store.CreateAsync(_ => { }));
        Assert.Null(await store.CreateAsync(_ => { }));
        Assert.Equal(2, store.Codes().Count());
    }

    [Fact]
    public async Task Sauvegarde_quotidienne_les_sept_plus_recentes_gardees()
    {
        var db = await TestDb.OpenAsync(_dir);
        var code = (await new TournamentStore(db).CreateAsync(t => t.Name = "Sauvé"))!.Code;
        var backup = new DbBackup(_dir, Microsoft.Extensions.Logging.Abstractions.NullLogger<DbBackup>.Instance);

        var day = new DateTime(2026, 10, 1);
        var first = await backup.BackupAsync(day);
        Assert.NotNull(first);
        Assert.Null(await backup.BackupAsync(day.AddHours(5)));            // déjà faite ce jour-là
        for (var i = 1; i <= 8; i++) await backup.BackupAsync(day.AddDays(i));

        var files = Directory.GetFiles(Path.Combine(_dir, "backups")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(DbBackup.Keep, files.Count);
        Assert.Equal("olomoc-20261003.db", files[0]);

        // Une sauvegarde est une base complète, lisible telle quelle.
        var copy = Path.Combine(_dir, "copie");
        Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(_dir, "backups", "olomoc-20261009.db"), Path.Combine(copy, OlomocDb.FileName));
        Assert.Equal("Sauvé", (await new TournamentStore(await TestDb.OpenAsync(copy)).LoadAsync(code))!.Name);
    }
}
