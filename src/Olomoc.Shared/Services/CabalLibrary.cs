using System.Text.Json;
using Microsoft.JSInterop;
using Olomoc.Accounts;
using Olomoc.Models;
using Olomoc.Tournaments;

namespace Olomoc.Services;

/// <summary>Bilan d'une synchronisation avec le compte.</summary>
/// <param name="FirstSync">Première synchronisation de cet appareil avec ce compte (ses cabales y ont été envoyées).</param>
/// <param name="Uploaded">Changements de cet appareil appliqués au compte.</param>
public sealed record CabalSyncReport(DateTimeOffset At, string User, bool FirstSync, int Uploaded, IReadOnlyList<Problem> Problems);

/// <summary>
/// Cabales sauvegardées, au format de l'export JSON (<see cref="CabalExport"/>). Le nom identifie
/// une cabale : sauvegarder sous un nom déjà pris remplace la cabale existante.
/// <para>
/// Toujours une copie sur l'appareil (localStorage) : tout marche sans compte et hors connexion.
/// Connecté, cette copie est synchronisée avec le compte : chaque changement part aussitôt (ou
/// au prochain chargement, sans réseau), et le compte renvoie ses cabales, celles des autres
/// appareils comprises. À nom égal, le plus récent l'emporte, suppression comprise.
/// </para>
/// <para>
/// La copie appartient au dernier compte synchronisé ici (<c>olomoc.cabals.account</c>). Première
/// connexion de l'appareil : toutes ses cabales partent dans le compte. Un autre compte ensuite :
/// seules celles faites ici hors compte (pas encore envoyées) le suivent ; les autres, déjà dans
/// le compte précédent, laissent la place aux siennes.
/// </para>
/// </summary>
public sealed class CabalLibrary
{
    private const string StorageKey = "olomoc.cabals", DeletedKey = "olomoc.cabals.deleted", OwnerKey = "olomoc.cabals.account";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IJSRuntime _js;
    private readonly ICabalCloud? _cloud;
    private readonly SemaphoreSlim _sync = new(1, 1);     // une synchronisation à la fois
    private Task? _loading;
    private List<SavedCabal> _cabals = new();
    private List<DeletedCabal> _deleted = new();          // suppressions pas encore envoyées
    private string? _owner;                               // compte de cette copie
    private string? _lastUser;                            // compte vu à la dernière synchronisation

    /// <summary>Sans compte (tests, app sans session) : copie de l'appareil seulement.</summary>
    public CabalLibrary(IJSRuntime js) : this(js, null) { }

    public CabalLibrary(IJSRuntime js, ICabalCloud? cloud)
    {
        _js = js;
        _cloud = cloud;
        if (cloud is not null) cloud.Changed += () => _ = OnAccountChangedAsync();
    }

    /// <summary>Cabales sauvegardées, la plus récente d'abord.</summary>
    public IReadOnlyList<SavedCabal> Cabals => _cabals;

    /// <summary>Changements pas encore envoyés au compte (sauvegardes, suppressions).</summary>
    public int PendingCount => _cabals.Count(c => c.Pending) + _deleted.Count;

    /// <summary>Dernière synchronisation (null : aucune encore, ou pas de compte).</summary>
    public CabalSyncReport? LastSync { get; private set; }

    /// <summary>Liste changée (synchronisation, changement de compte).</summary>
    public event Action? Changed;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    /// <summary>Relit les sauvegardes : une autre page du site (le constructeur, dans un autre onglet) a pu en ajouter.</summary>
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

    /// <summary>Cabale sauvegardée sous ce nom (sans tenir compte de la casse ni des espaces autour).</summary>
    public SavedCabal? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _cabals.FirstOrDefault(c => SameName(c.Cabal.Name, name));

    /// <summary>Sauvegarde la cabale, qui doit être nommée ; remplace celle du même nom.</summary>
    public async Task SaveAsync(CabalExport cabal, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(cabal.Name)) throw new ArgumentException("Cabale sans nom.", nameof(cabal));
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
    /// Envoie les changements de cet appareil au compte connecté et reprend ses cabales. Sans
    /// compte ou sans réseau : rien ne change (les changements attendent) ; renvoie null.
    /// </summary>
    public async Task<CabalSyncReport?> SyncAsync()
    {
        if (_cloud is null) return null;
        await _sync.WaitAsync();
        try
        {
            if (await _cloud.CurrentUserAsync() is not { } user) return null;
            var firstSync = _owner is null;
            if (_owner is not null && _owner != user) _deleted.Clear();     // suppressions de l'autre compte
            var sent = _cabals.Where(c => firstSync || c.Pending).ToList();
            var tombstones = _owner == user ? _deleted.ToList() : new List<DeletedCabal>();
            var changes = sent.Select(c => new CabalChange(c.Cabal.Name!, c.SavedAt, c.Cabal))
                .Concat(tombstones.Select(d => new CabalChange(d.Name, d.At, null)))
                .ToList();

            // Par paquets ; le dernier appel renvoie les cabales du compte.
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
                    return LastSync;                                         // réessayé plus tard
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
    /// Copie de l'appareil = cabales du compte, plus celles d'ici que le compte n'a pas (ou en plus
    /// ancien) : changements faits pendant l'envoi, ou refusés (limite) ; elles restent à envoyer.
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

    // Connexion (ou changement de compte) : synchronisation, puis liste mise à jour à l'écran.
    private async Task OnAccountChangedAsync()
    {
        if (_cloud is null || _loading is null) return;          // pas encore chargée : le chargement synchronisera
        await _loading;
        var user = await _cloud.CurrentUserAsync();
        if (user is null || user == _lastUser) return;
        await SyncAsync();
    }

    /// <summary>Même composition (faction, rouages et équipements/dons par rang), noms mis à part.</summary>
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
        catch (JSException) { return null; }      // stockage indisponible : aucune sauvegarde
        catch (JsonException) { return null; }    // contenu illisible : ignoré, écrasé à la prochaine sauvegarde
    }

    private async Task StoreAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(_cabals, JsonOpts));
            await _js.InvokeVoidAsync("localStorage.setItem", DeletedKey, JsonSerializer.Serialize(_deleted, JsonOpts));
            if (_owner is not null) await _js.InvokeVoidAsync("localStorage.setItem", OwnerKey, JsonSerializer.Serialize(_owner, JsonOpts));
        }
        catch (JSException) { /* navigation privée… : la sauvegarde vit le temps de l'onglet */ }
    }

    /// <summary>Cabale supprimée sur cet appareil, suppression pas encore envoyée au compte.</summary>
    private sealed record DeletedCabal(string Name, DateTimeOffset At);
}
