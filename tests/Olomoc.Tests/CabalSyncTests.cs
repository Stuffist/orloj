using Microsoft.JSInterop;
using Olomoc.Accounts;
using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>
/// Device cabals synchronized with the account (CabalLibrary): in-memory account, which applies the
/// same rules as the server; one FakeStorage = the localStorage of one device.
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
    public async Task First_sign_in_the_device_cabals_go_to_the_account()
    {
        var cloud = new FakeCloud();
        var library = await Open(new FakeStorage(), cloud);
        await library.SaveAsync(Cabal("Une"), T0);
        await library.SaveAsync(Cabal("Deux"), T0.AddMinutes(1));

        await cloud.SignInAsync("Bohdan");                   // sign-in: synchronization right away
        Assert.Equal(["Deux", "Une"], cloud.Names("Bohdan"));
        Assert.Equal(0, library.PendingCount);

        // Second device, empty: it receives the account's cabals.
        Assert.Equal(["Deux", "Une"], Names(await Open(new FakeStorage(), cloud)));
    }

    [Fact]
    public async Task Save_and_deletion_follow_on_the_other_device()
    {
        var cloud = new FakeCloud { User = "Bohdan" };
        var phone = await Open(new FakeStorage(), cloud);
        var computerStorage = new FakeStorage();
        var computer = await Open(computerStorage, cloud);

        await phone.SaveAsync(Cabal("Ma cabale"), T0);
        await computer.ReloadAsync();
        Assert.Equal(["Ma cabale"], Names(computer));

        await computer.DeleteAsync(computer.Cabals[0]);
        await phone.ReloadAsync();
        Assert.Empty(phone.Cabals);
        Assert.Empty((await Open(computerStorage, cloud)).Cabals);         // reopened later: nothing reappears
    }

    [Fact]
    public async Task Offline_the_changes_wait_for_the_network()
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
        Assert.Equal(["Gardée", "Supprimée"], cloud.Names("Bohdan"));

        // Reload later, network back: both changes are sent.
        cloud.Online = true;
        var later = await Open(storage, cloud);
        Assert.Equal(0, later.PendingCount);
        Assert.Equal(["Hors ligne", "Gardée"], cloud.Names("Bohdan"));
    }

    [Fact]
    public async Task Another_account_on_the_same_device()
    {
        var cloud = new FakeCloud { User = "Bohdan" };
        var library = await Open(new FakeStorage(), cloud);
        await library.SaveAsync(Cabal("De Bohdan"), T0);

        // Signed out: a cabal made on the device, without an account.
        await cloud.SignInAsync(null);
        await library.SaveAsync(Cabal("Sans compte"), T0.AddHours(1));

        // Another account: only the cabal made without an account follows it.
        await cloud.SignInAsync("Vera");
        Assert.Equal(["Sans compte"], cloud.Names("Vera"));
        Assert.Equal(["Sans compte"], Names(library));
        Assert.Equal(["De Bohdan"], cloud.Names("Bohdan"));
    }

    /// <summary>In-memory account: same rule as the server (most recent wins).</summary>
    private sealed class FakeCloud : ICabalCloud
    {
        private readonly Dictionary<string, Dictionary<string, (DateTimeOffset At, CabalExport? Cabal)>> _accounts = new();

        public string? User { get; set; }
        public bool Online { get; set; } = true;

        public event Action? Changed;

        public async Task SignInAsync(string? user)
        {
            User = user;
            Changed?.Invoke();
            await Task.Delay(50);                    // synchronization started by the event
        }

        public Task<string?> CurrentUserAsync() => Task.FromResult(User);

        public string[] Names(string user) => _accounts.GetValueOrDefault(user)?.Values
            .Where(v => v.Cabal is not null).OrderByDescending(v => v.At).Select(v => v.Cabal!.Name!).ToArray() ?? [];

        public Task<AccountResult<CabalSyncResult>> SyncCabalsAsync(IReadOnlyList<CabalChange> changes)
        {
            if (!Online) return Task.FromResult(new AccountResult<CabalSyncResult>(null, [Problem.Of("network")]));
            if (!_accounts.TryGetValue(User!, out var account)) _accounts[User!] = account = new();
            var applied = 0;
            foreach (var change in changes)
            {
                var key = CabalSyncRules.Key(change.Name);
                if (!CabalSyncRules.IsNewer(change.At, account.TryGetValue(key, out var known) ? known.At : (DateTimeOffset?)null)) continue;
                account[key] = (change.At, change.Cabal);
                applied++;
            }
            var cabals = account.Values.Where(v => v.Cabal is not null).OrderByDescending(v => v.At)
                .Select(v => new SavedCabal(v.At, v.Cabal!, Public: true)).ToList();
            return Task.FromResult(new AccountResult<CabalSyncResult>(new(cabals, applied, []), []));
        }

        public Task<AccountResult<bool>> SetCabalVisibilityAsync(string name, bool isPublic) =>
            Task.FromResult(new AccountResult<bool>(true, []));
    }

    /// <summary>In-memory localStorage.</summary>
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
