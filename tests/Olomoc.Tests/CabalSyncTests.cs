using Microsoft.JSInterop;
using Olomoc.Accounts;
using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>
/// Cabales de l'appareil synchronisées avec le compte (CabalLibrary) : compte en mémoire, qui
/// applique les mêmes règles que le serveur ; un FakeStorage = le localStorage d'un appareil.
/// </summary>
public class CabalSyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static CabalExport Cabal(string name, string cog = "3R06") =>
        new(CabalExport.FormatId, CabalExport.CurrentVersion, "fr", name, new CodeName("MAISEL", "Ligue de Maisel"),
            [new CabalSlot(3, new IdName(cog, "Rouage"), null)]);

    private static async Task<CabalLibrary> Open(FakeStorage device, FakeCloud cloud)
    {
        var library = new CabalLibrary(device, cloud);
        await library.EnsureLoadedAsync();
        return library;
    }

    private static string[] Names(CabalLibrary l) => l.Cabals.Select(c => c.Cabal.Name!).ToArray();

    [Fact]
    public async Task Sans_compte_tout_reste_sur_l_appareil()
    {
        var cloud = new FakeCloud();
        var library = await Open(new FakeStorage(), cloud);
        await library.SaveAsync(Cabal("Ma cabale"), T0);
        Assert.Equal(["Ma cabale"], Names(library));
        Assert.Equal(1, library.PendingCount);
        Assert.Null(library.LastSync);
        Assert.Equal(0, cloud.Calls);
    }

    [Fact]
    public async Task Premiere_connexion_les_cabales_de_l_appareil_partent_dans_le_compte()
    {
        var cloud = new FakeCloud();
        var phone = new FakeStorage();
        var library = await Open(phone, cloud);
        await library.SaveAsync(Cabal("Une"), T0);
        await library.SaveAsync(Cabal("Deux"), T0.AddMinutes(1));
        var changed = 0;
        library.Changed += () => changed++;

        await cloud.SignInAsync("Bohdan");                   // connexion : synchronisation aussitôt
        var report = library.LastSync!;
        Assert.Equal(("Bohdan", true, 2), (report.User, report.FirstSync, report.Uploaded));
        Assert.Empty(report.Problems);
        Assert.Equal(["Deux", "Une"], cloud.Names("Bohdan"));
        Assert.Equal(0, library.PendingCount);
        Assert.Equal(1, changed);

        // Second appareil, vide : il reçoit les cabales du compte.
        var computer = await Open(new FakeStorage(), cloud);
        Assert.Equal(["Deux", "Une"], Names(computer));
        Assert.False(computer.LastSync!.FirstSync && computer.LastSync.Uploaded > 0);
    }

    [Fact]
    public async Task Sauvegarde_et_suppression_suivent_sur_l_autre_appareil()
    {
        var cloud = new FakeCloud { User = "Bohdan" };
        var phone = await Open(new FakeStorage(), cloud);
        var computerStorage = new FakeStorage();
        var computer = await Open(computerStorage, cloud);

        await phone.SaveAsync(Cabal("Ma cabale"), T0);
        Assert.Equal(0, phone.PendingCount);
        await computer.ReloadAsync();
        Assert.Equal(["Ma cabale"], Names(computer));

        await computer.DeleteAsync(computer.Cabals[0]);
        await phone.ReloadAsync();
        Assert.Empty(phone.Cabals);

        // Copie rouverte sur l'ordinateur (autre onglet, plus tard) : rien ne réapparaît.
        Assert.Empty((await Open(computerStorage, cloud)).Cabals);
    }

    [Fact]
    public async Task Hors_connexion_les_changements_attendent_le_reseau()
    {
        var cloud = new FakeCloud { User = "Bohdan" };
        var storage = new FakeStorage();
        var library = await Open(storage, cloud);
        await library.SaveAsync(Cabal("Gardée"), T0);
        await library.SaveAsync(Cabal("Supprimée"), T0);

        cloud.Online = false;
        await library.SaveAsync(Cabal("Hors ligne"), T0.AddHours(1));
        await library.DeleteAsync(library.Find("Supprimée")!);
        Assert.Equal(2, library.PendingCount);
        Assert.Equal("network", library.LastSync!.Problems.Single().Key);
        Assert.Equal(["Gardée", "Supprimée"], cloud.Names("Bohdan"));

        // Rechargement plus tard, réseau revenu : les deux changements partent.
        cloud.Online = true;
        var later = await Open(storage, cloud);
        Assert.Equal(0, later.PendingCount);
        Assert.Equal(["Hors ligne", "Gardée"], cloud.Names("Bohdan"));
        Assert.Equal(["Hors ligne", "Gardée"], Names(later));
    }

    [Fact]
    public async Task Version_plus_recente_ailleurs_l_emporte_sur_le_changement_en_attente()
    {
        var cloud = new FakeCloud { User = "Bohdan" };
        var storage = new FakeStorage();
        var phone = await Open(storage, cloud);
        await phone.SaveAsync(Cabal("Ma cabale", "3R06"), T0);
        cloud.Online = false;
        await phone.SaveAsync(Cabal("Ma cabale", "3R05"), T0.AddHours(1));    // en attente

        cloud.Online = true;
        var computer = await Open(new FakeStorage(), cloud);
        await computer.SaveAsync(Cabal("Ma cabale", "3R04"), T0.AddHours(2));

        var phoneLater = await Open(storage, cloud);
        Assert.Equal("3R04", phoneLater.Cabals.Single().Cabal.Cogs[0].Cog.Id);
        Assert.Equal(0, phoneLater.PendingCount);
    }

    [Fact]
    public async Task Autre_compte_sur_le_meme_appareil()
    {
        var cloud = new FakeCloud { User = "Bohdan" };
        var storage = new FakeStorage();
        var library = await Open(storage, cloud);
        await library.SaveAsync(Cabal("De Bohdan"), T0);

        // Déconnecté : une cabale faite sur l'appareil, sans compte.
        await cloud.SignInAsync(null);
        await library.SaveAsync(Cabal("Sans compte"), T0.AddHours(1));
        Assert.Equal(["Sans compte", "De Bohdan"], Names(library));          // la copie reste visible

        // Un autre compte : seule la cabale faite hors compte le suit.
        await cloud.SignInAsync("Vera");
        Assert.Equal(["Sans compte"], cloud.Names("Vera"));
        Assert.Equal(["Sans compte"], Names(library));
        Assert.Equal(["De Bohdan"], cloud.Names("Bohdan"));
        Assert.False(library.LastSync!.FirstSync);
    }

    [Fact]
    public async Task Cabale_refusee_par_le_compte_reste_sur_l_appareil()
    {
        var cloud = new FakeCloud { User = "Bohdan", Limit = 1 };
        var library = await Open(new FakeStorage(), cloud);
        await library.SaveAsync(Cabal("Une"), T0);
        await library.SaveAsync(Cabal("Deux"), T0.AddMinutes(1));
        Assert.Equal(["Deux", "Une"], Names(library));
        Assert.Equal(1, library.PendingCount);
        Assert.Equal("cabalLimit", library.LastSync!.Problems.Single().Key);
        Assert.Equal(["Une"], cloud.Names("Bohdan"));
    }

    [Fact]
    public async Task Envoi_par_paquets()
    {
        var cloud = new FakeCloud();
        var library = await Open(new FakeStorage(), cloud);
        for (var i = 0; i < 25; i++) await library.SaveAsync(Cabal($"C{i}"), T0.AddMinutes(i));
        await cloud.SignInAsync("Bohdan");
        Assert.Equal(25, cloud.Names("Bohdan").Length);
        Assert.Equal(3, cloud.Calls);
        Assert.True(cloud.MaxChunk <= CabalSyncRules.ChunkSize);
        Assert.Equal(25, library.LastSync!.Uploaded);
    }

    /// <summary>Compte en mémoire : mêmes règles que le serveur (plus récent l'emporte, limite).</summary>
    private sealed class FakeCloud : ICabalCloud
    {
        private readonly Dictionary<string, Dictionary<string, (DateTimeOffset At, CabalExport? Cabal)>> _accounts = new();

        public string? User { get; set; }
        public bool Online { get; set; } = true;
        public int Limit { get; set; } = CabalSyncRules.MaxCabals;
        public int Calls { get; private set; }
        public int MaxChunk { get; private set; }

        public event Action? Changed;

        public async Task SignInAsync(string? user)
        {
            User = user;
            Changed?.Invoke();
            await Task.Delay(50);                    // synchronisation lancée par l'événement
        }

        public Task<string?> CurrentUserAsync() => Task.FromResult(User);

        public string[] Names(string user) => _accounts.GetValueOrDefault(user)?.Values
            .Where(v => v.Cabal is not null).OrderByDescending(v => v.At).Select(v => v.Cabal!.Name!).ToArray() ?? [];

        public Task<AccountResult<CabalSyncResult>> SyncCabalsAsync(IReadOnlyList<CabalChange> changes)
        {
            if (!Online) return Task.FromResult(new AccountResult<CabalSyncResult>(null, [Problem.Of("network")]));
            Calls++;
            MaxChunk = Math.Max(MaxChunk, changes.Count);
            if (!_accounts.TryGetValue(User!, out var account)) _accounts[User!] = account = new();
            var applied = 0;
            var problems = new List<Problem>();
            foreach (var change in changes)
            {
                var key = CabalSyncRules.Key(change.Name);
                if (!CabalSyncRules.IsNewer(change.At, account.TryGetValue(key, out var known) ? known.At : (DateTimeOffset?)null)) continue;
                if (change.Cabal is not null && known.Cabal is null && account.Values.Count(v => v.Cabal is not null) >= Limit)
                {
                    problems.Add(Problem.Of("cabalLimit", Limit.ToString(), change.Name));
                    continue;
                }
                account[key] = (change.At, change.Cabal);
                applied++;
            }
            var cabals = account.Values.Where(v => v.Cabal is not null).OrderByDescending(v => v.At)
                .Select(v => new SavedCabal(v.At, v.Cabal!)).ToList();
            return Task.FromResult(new AccountResult<CabalSyncResult>(new(cabals, applied, problems), []));
        }
    }

    /// <summary>localStorage en mémoire.</summary>
    private sealed class FakeStorage : IJSRuntime
    {
        private readonly Dictionary<string, string> _items = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            var key = (string)args![0]!;
            object? result = null;
            switch (identifier)
            {
                case "localStorage.getItem": result = _items.GetValueOrDefault(key); break;
                case "localStorage.setItem": _items[key] = (string)args[1]!; break;
                default: throw new NotSupportedException(identifier);
            }
            return ValueTask.FromResult((TValue)result!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
