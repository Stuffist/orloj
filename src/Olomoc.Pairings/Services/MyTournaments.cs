using System.Text.Json;
using Microsoft.JSInterop;

namespace Olomoc.Pairings.Services;

/// <summary>Tournoi suivi sur cet appareil, avec les jetons reçus (organisateur, inscription).</summary>
public sealed record MyTournament(string Code, string Name, string? OrganizerToken, string? PlayerId, string? PlayerToken, DateTimeOffset SeenAt);

/// <summary>
/// Tournois suivis sur cet appareil (localStorage, clé « olomoc.tournaments ») : ceux qu'on
/// organise (jeton organisateur, seule clé d'administration) et ceux où l'on s'est inscrit
/// (jeton joueur). Perdre le jeton organisateur, c'est perdre l'administration : le lien
/// d'administration (qui le contient) est à garder.
/// </summary>
public sealed class MyTournaments(IJSRuntime js)
{
    private const string StorageKey = "olomoc.tournaments";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private Task? _loading;
    private List<MyTournament> _items = new();

    /// <summary>Le plus récemment consulté d'abord.</summary>
    public IReadOnlyList<MyTournament> All => _items;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public MyTournament? Get(string code) => _items.FirstOrDefault(t => Same(t.Code, code));

    /// <summary>Met à jour l'entrée du tournoi (les jetons non fournis sont conservés).</summary>
    public async Task RememberAsync(string code, string name, string? organizerToken = null, string? playerId = null, string? playerToken = null)
    {
        var old = Get(code);
        _items.RemoveAll(t => Same(t.Code, code));
        _items.Insert(0, new MyTournament(code.ToUpperInvariant(), name,
            organizerToken ?? old?.OrganizerToken, playerId ?? old?.PlayerId, playerToken ?? old?.PlayerToken, DateTimeOffset.Now));
        await StoreAsync();
    }

    /// <summary>Oublie l'inscription (désinscription) en gardant le reste.</summary>
    public async Task ForgetPlayerAsync(string code)
    {
        if (Get(code) is not { } t) return;
        _items[_items.IndexOf(t)] = t with { PlayerId = null, PlayerToken = null };
        await StoreAsync();
    }

    public async Task ForgetAsync(string code)
    {
        _items.RemoveAll(t => Same(t.Code, code));
        await StoreAsync();
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private async Task LoadAsync()
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(json))
                _items = JsonSerializer.Deserialize<List<MyTournament>>(json, JsonOpts)?.OrderByDescending(t => t.SeenAt).ToList() ?? new();
        }
        catch (JSException) { /* stockage indisponible */ }
        catch (JsonException) { /* contenu illisible : ignoré */ }
    }

    private async Task StoreAsync()
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(_items, JsonOpts)); }
        catch (JSException) { /* navigation privée… : le temps de l'onglet */ }
    }
}
