using Olomoc.Server.Tournaments;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Stockage des tournois : purge des tournois inactifs, jamais des tournois à venir.</summary>
public sealed class TournamentStoreTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 13);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "olomoc-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<string> Add(TournamentStore store, DateOnly? date, int daysSinceChange)
    {
        var t = await store.CreateAsync(t => { t.Name = "T"; t.Date = date; });
        File.SetLastWriteTimeUtc(Path.Combine(_dir, t!.Code + ".json"), DateTime.UtcNow.AddDays(-daysSinceChange));
        return t.Code;
    }

    [Fact]
    public async Task Purge_des_inactifs_passes_ou_sans_date_jamais_des_tournois_a_venir()
    {
        var store = new TournamentStore(_dir);
        var oldPast = await Add(store, Today.AddDays(-100), 95);
        var oldUndated = await Add(store, null, 95);
        var oldToday = await Add(store, Today, 95);
        var oldFuture = await Add(store, Today.AddDays(200), 400);
        var recentPast = await Add(store, Today.AddDays(-100), 10);
        File.WriteAllText(Path.Combine(_dir, "ZZZZZZ.json"), "{ illisible");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "ZZZZZZ.json"), DateTime.UtcNow.AddDays(-200));

        Assert.Equal(2, await store.PurgeAsync(TimeSpan.FromDays(90), Today));

        Assert.Null(await store.LoadAsync(oldPast));
        Assert.Null(await store.LoadAsync(oldUndated));
        Assert.NotNull(await store.LoadAsync(oldToday));
        Assert.NotNull(await store.LoadAsync(oldFuture));
        Assert.NotNull(await store.LoadAsync(recentPast));
        Assert.True(File.Exists(Path.Combine(_dir, "ZZZZZZ.json")));       // illisible : laissé à examiner
    }

    [Fact]
    public async Task Suppression_seulement_si_acceptee()
    {
        var store = new TournamentStore(_dir);
        var code = await Add(store, null, 0);
        Assert.Equal("refusée", await store.DeleteAsync(code, _ => (false, "refusée")));
        Assert.NotNull(await store.LoadAsync(code));
        Assert.Equal("ok", await store.DeleteAsync(code, _ => (true, "ok")));
        Assert.Null(await store.LoadAsync(code));
        Assert.Null(await store.DeleteAsync(code, _ => (true, "ok")));       // n'existe plus
    }
}
