using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.JSInterop;
using Olomoc.Services;

namespace Olomoc.Online.Services;

/// <summary>Partie suivie sur cet appareil : jeton de son siège, et lien d'invitation pour le créateur.</summary>
public sealed record MyGame(string Code, string? Token, string? InviteToken, string? Title, DateTimeOffset SeenAt);

/// <summary>
/// Parties en ligne suivies sur cet appareil (localStorage, clé « olomoc.online »), et le nom du
/// joueur (« olomoc.online.name »). Le jeton du siège est la seule clé pour y jouer : le lien de
/// la partie (qui le contient) est à garder.
/// </summary>
public sealed class MyGames(IJSRuntime js, AccountService accounts, LocalizationService L)
{
    private const string StorageKey = "olomoc.online";
    private const string NameKey = "olomoc.online.name";
    private const int MaxGames = 30;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private Task? _loading;
    private List<MyGame> _items = new();
    private string? _name;           // dernier nom utilisé sur cet appareil

    /// <summary>La plus récemment ouverte d'abord.</summary>
    public IReadOnlyList<MyGame> All => _items;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public MyGame? Get(string code) => _items.FirstOrDefault(g => Same(g.Code, code));

    /// <summary>Met à jour l'entrée de la partie (ce qui n'est pas fourni est conservé).</summary>
    public async Task RememberAsync(string code, string? token = null, string? inviteToken = null, string? title = null)
    {
        var old = Get(code);
        _items.RemoveAll(g => Same(g.Code, code));
        _items.Insert(0, new MyGame(code.ToUpperInvariant(), token ?? old?.Token, inviteToken ?? old?.InviteToken,
            title ?? old?.Title, DateTimeOffset.Now));
        if (_items.Count > MaxGames) _items.RemoveRange(MaxGames, _items.Count - MaxGames);
        await StoreAsync();
    }

    public async Task ForgetAsync(string code)
    {
        _items.RemoveAll(g => Same(g.Code, code));
        await StoreAsync();
    }

    /// <summary>Nom à proposer : celui du compte connecté, sinon le dernier utilisé sur cet appareil (null sinon).</summary>
    public async Task<string?> KnownNameAsync()
    {
        await EnsureLoadedAsync();
        return await accounts.CurrentUserAsync() is { Length: > 0 } user ? user : _name;
    }

    /// <summary>Nom tiré au sort (« Joueur 4821 »), pour qui n'en donne pas.</summary>
    public async Task<string> RandomNameAsync()
    {
        await L.EnsureLoadedAsync();
        return L.Ui("online.name.random", RandomNumberGenerator.GetInt32(1000, 10000));
    }

    /// <summary>Nom du joueur sans rien saisir : celui du compte ou de l'appareil, sinon tiré au sort (et gardé).</summary>
    public async Task<string> DefaultNameAsync()
    {
        if (await KnownNameAsync() is { } known) return known;
        var name = await RandomNameAsync();
        await RememberNameAsync(name);
        return name;
    }

    /// <summary>Nom utilisé dans une partie : proposé la fois suivante (sans compte).</summary>
    public async Task RememberNameAsync(string name)
    {
        _name = name.Trim();
        try { await js.InvokeVoidAsync("localStorage.setItem", NameKey, _name); }
        catch (JSException) { /* stockage indisponible */ }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private async Task LoadAsync()
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(json))
                _items = JsonSerializer.Deserialize<List<MyGame>>(json, JsonOpts)?.OrderByDescending(g => g.SeenAt).ToList() ?? new();
            _name = await js.InvokeAsync<string?>("localStorage.getItem", NameKey) is { Length: > 0 } name ? name : null;
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
