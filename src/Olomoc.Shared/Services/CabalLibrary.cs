using System.Text.Json;
using Microsoft.JSInterop;
using Olomoc.Accounts;
using Olomoc.Models;
using Olomoc.Tournaments;

namespace Olomoc.Services;

/// <summary>Summary of a synchronization with the account.</summary>
/// <param name="FirstSync">First synchronization of this device with this account (its cabals were sent to it).</param>
/// <param name="Uploaded">Changes of this device applied to the account.</param>
public sealed record CabalSyncReport(DateTimeOffset At, string User, bool FirstSync, int Uploaded, IReadOnlyList<Problem> Problems);

/// <summary>
/// Saved cabals, in the JSON export format (<see cref="CabalExport"/>). The name identifies a
/// cabal: saving under a name already taken replaces the existing cabal.
/// <para>
/// Always a copy on the device (localStorage): everything works without an account and offline.
/// When signed in, this copy is synchronized with the account: each change is sent at once (or at
/// the next load, without network), and the account returns its cabals, including those of the
/// other devices. For the same name, the most recent one wins, deletion included.
/// </para>
/// <para>
/// The copy belongs to the last account synchronized here (<c>olomoc.cabals.account</c>). First
/// sign-in on the device: all its cabals go to the account. Another account afterwards: only those
/// made here without an account (not sent yet) follow it; the others, already in the previous
/// account, make way for its own.
/// </para>
/// </summary>
public sealed class CabalLibrary
{
    private const string StorageKey = "olomoc.cabals", DeletedKey = "olomoc.cabals.deleted", OwnerKey = "olomoc.cabals.account";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IJSRuntime _js;
    private readonly ICabalCloud? _cloud;
    private readonly SemaphoreSlim _sync = new(1, 1);     // one synchronization at a time
    private Task? _loading;
    private List<SavedCabal> _cabals = new();
    private List<DeletedCabal> _deleted = new();          // deletions not sent yet
    private string? _owner;                               // account of this copy
    private string? _lastUser;                            // account seen at the last synchronization

    /// <summary>Without an account (tests, app without a session): device copy only.</summary>
    public CabalLibrary(IJSRuntime js) : this(js, null) { }

    public CabalLibrary(IJSRuntime js, ICabalCloud? cloud)
    {
        _js = js;
        _cloud = cloud;
        if (cloud is not null) cloud.Changed += () => _ = OnAccountChangedAsync();
    }

    /// <summary>Saved cabals, most recent first.</summary>
    public IReadOnlyList<SavedCabal> Cabals => _cabals;

    /// <summary>Changes not sent to the account yet (saves, deletions).</summary>
    public int PendingCount => _cabals.Count(c => c.Pending) + _deleted.Count;

    /// <summary>Last synchronization (null: none yet, or no account).</summary>
    public CabalSyncReport? LastSync { get; private set; }

    /// <summary>List changed (synchronization, account change).</summary>
    public event Action? Changed;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    /// <summary>Reads the saves again: another page of the site (the builder, in another tab) may have added some.</summary>
    public Task ReloadAsync() => _loading = LoadAsync();

    private async Task LoadAsync()
    {
        _cabals = (await ReadAsync<List<SavedCabal>>(StorageKey) ?? new())
            .Where(c => c.Cabal is { Name: not null, Cogs: not null })
            .OrderByDescending(c => c.SavedAt)
            .ToList();
        _deleted = (await ReadAsync<List<DeletedCabal>>(DeletedKey) ?? new()).Where(d => d.Name is not null).ToList();
        _owner = await ReadAsync<string>(OwnerKey);
        await SyncAsync();
    }

    /// <summary>Cabal saved under this name (case-insensitive, surrounding spaces ignored).</summary>
    public SavedCabal? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _cabals.FirstOrDefault(c => SameName(c.Cabal.Name, name));

    /// <summary>Saves the cabal, which must be named; replaces the one with the same name.</summary>
    public async Task SaveAsync(CabalExport cabal, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(cabal.Name)) throw new ArgumentException("Cabal without a name.", nameof(cabal));
        _cabals.RemoveAll(c => SameName(c.Cabal.Name, cabal.Name));
        _cabals.Insert(0, new SavedCabal(now, cabal, Pending: true));
        _deleted.RemoveAll(d => SameName(d.Name, cabal.Name));
        await StoreAsync();
        await SyncAsync();
    }

    public async Task DeleteAsync(SavedCabal saved)
    {
        if (!_cabals.Remove(saved)) return;
        _deleted.RemoveAll(d => SameName(d.Name, saved.Cabal.Name));
        _deleted.Add(new DeletedCabal(saved.Cabal.Name!, DateTimeOffset.Now));
        await StoreAsync();
        await SyncAsync();
    }

    /// <summary>
    /// Sends this device's changes to the signed-in account and takes back its cabals. Without an
    /// account or without network: nothing changes (the changes wait); returns null.
    /// </summary>
    public async Task<CabalSyncReport?> SyncAsync()
    {
        if (_cloud is null) return null;
        await _sync.WaitAsync();
        try
        {
            if (await _cloud.CurrentUserAsync() is not { } user) return null;
            var firstSync = _owner is null;
            if (_owner is not null && _owner != user) _deleted.Clear();     // deletions of the other account
            var sent = _cabals.Where(c => firstSync || c.Pending).ToList();
            var tombstones = _owner == user ? _deleted.ToList() : new List<DeletedCabal>();
            var changes = sent.Select(c => new CabalChange(c.Cabal.Name!, c.SavedAt, c.Cabal))
                .Concat(tombstones.Select(d => new CabalChange(d.Name, d.At, null)))
                .ToList();

            // In batches; the last call returns the account's cabals.
            CabalSyncResult? result = null;
            var applied = 0;
            var problems = new List<Problem>();
            var index = 0;
            do
            {
                var chunk = changes.Skip(index).Take(CabalSyncRules.ChunkSize).ToList();
                var response = await _cloud.SyncCabalsAsync(chunk);
                if (!response.Ok || response.Value is null)
                {
                    LastSync = new(DateTimeOffset.Now, user, firstSync, applied, response.Problems);
                    return LastSync;                                         // retried later
                }
                result = response.Value;
                applied += result.Applied;
                problems.AddRange(result.Problems);
                index += chunk.Count;
            } while (index < changes.Count);

            Merge(result!.Cabals, sent, tombstones);
            _owner = _lastUser = user;
            await StoreAsync();
            LastSync = new(DateTimeOffset.Now, user, firstSync, applied, problems);
            Changed?.Invoke();
            return LastSync;
        }
        finally { _sync.Release(); }
    }

    /// <summary>
    /// Device copy = the account's cabals, plus those from here that the account does not have (or
    /// has an older version of): changes made during the upload, or refused (limit); they stay to be sent.
    /// </summary>
    private void Merge(IReadOnlyList<SavedCabal> account, List<SavedCabal> sent, List<DeletedCabal> tombstones)
    {
        var merged = account.Select(c => c with { Pending = false }).ToList();
        foreach (var local in _cabals.Where(c => c.Pending || sent.Contains(c)))
        {
            var known = merged.FirstOrDefault(c => SameName(c.Cabal.Name, local.Cabal.Name));
            if (known is not null && known.SavedAt >= local.SavedAt) continue;
            if (known is not null) merged.Remove(known);
            merged.Add(local with { Pending = true });
        }
        _cabals = merged.OrderByDescending(c => c.SavedAt).ToList();
        _deleted.RemoveAll(tombstones.Contains);
    }

    // Sign-in (or account change): synchronization, then list updated on screen.
    private async Task OnAccountChangedAsync()
    {
        if (_cloud is null || _loading is null) return;          // not loaded yet: loading will synchronize
        await _loading;
        var user = await _cloud.CurrentUserAsync();
        if (user is null || user == _lastUser) return;
        await SyncAsync();
    }

    /// <summary>Same composition (faction, cogs and equipment/boons per rank), names aside.</summary>
    public static bool SameCards(CabalExport a, CabalExport b) => Cards(a) == Cards(b);

    private static string Cards(CabalExport c) =>
        c.Faction?.Code + "|" + string.Join("|", c.Cogs.OrderBy(s => s.Rank).Select(s => $"{s.Rank}:{s.Cog.Id}:{s.Equipment?.Id}"));

    private static bool SameName(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private async Task<T?> ReadAsync<T>(string key) where T : class
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", key);
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (JSException) { return null; }      // storage unavailable: no save
        catch (JsonException) { return null; }    // unreadable content: ignored, overwritten at the next save
    }

    private async Task StoreAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(_cabals, JsonOpts));
            await _js.InvokeVoidAsync("localStorage.setItem", DeletedKey, JsonSerializer.Serialize(_deleted, JsonOpts));
            if (_owner is not null) await _js.InvokeVoidAsync("localStorage.setItem", OwnerKey, JsonSerializer.Serialize(_owner, JsonOpts));
        }
        catch (JSException) { /* private browsing…: the save lives as long as the tab */ }
    }

    /// <summary>Cabal deleted on this device, deletion not sent to the account yet.</summary>
    private sealed record DeletedCabal(string Name, DateTimeOffset At);
}
