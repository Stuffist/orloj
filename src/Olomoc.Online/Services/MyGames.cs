using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.JSInterop;
using Olomoc.Services;

namespace Olomoc.Online.Services;

/// <summary>Game followed on this device: its seat token, and the invitation link for the creator.</summary>
public sealed record MyGame(string Code, string? Token, string? InviteToken, string? Title, DateTimeOffset SeenAt);

/// <summary>
/// Online games followed on this device (localStorage, "olomoc.online" key), and the player's name
/// ("olomoc.online.name"). The seat token is the only key to play: the game's link (which contains
/// it) must be kept.
/// </summary>
public sealed class MyGames(IJSRuntime js, AccountService accounts, LocalizationService L)
{
    private const string StorageKey = "olomoc.online";
    private const string NameKey = "olomoc.online.name";
    private const int MaxGames = 30;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private Task? _loading;
    private List<MyGame> _items = new();
    private string? _name;           // last name used on this device

    /// <summary>Most recently opened first.</summary>
    public IReadOnlyList<MyGame> All => _items;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public MyGame? Get(string code) => _items.FirstOrDefault(g => Same(g.Code, code));

    /// <summary>Updates the game's entry (what is not given is kept).</summary>
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

    /// <summary>Name to suggest: the signed-in account's, otherwise the last one used on this device (null otherwise).</summary>
    public async Task<string?> KnownNameAsync()
    {
        await EnsureLoadedAsync();
        return await accounts.CurrentUserAsync() is { Length: > 0 } user ? user : _name;
    }

    /// <summary>Random name ("Player 4821"), for whoever does not give one.</summary>
    public async Task<string> RandomNameAsync()
    {
        await L.EnsureLoadedAsync();
        return L.Ui("online.name.random", RandomNumberGenerator.GetInt32(1000, 10000));
    }

    /// <summary>The player's name without typing anything: the account's or the device's, otherwise random (and kept).</summary>
    public async Task<string> DefaultNameAsync()
    {
        if (await KnownNameAsync() is { } known) return known;
        var name = await RandomNameAsync();
        await RememberNameAsync(name);
        return name;
    }

    /// <summary>Name used in a game: suggested next time (without an account).</summary>
    public async Task RememberNameAsync(string name)
    {
        _name = name.Trim();
        try { await js.InvokeVoidAsync("localStorage.setItem", NameKey, _name); }
        catch (JSException) { /* storage unavailable */ }
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
        catch (JSException) { /* storage unavailable */ }
        catch (JsonException) { /* unreadable content: ignored */ }
    }

    private async Task StoreAsync()
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(_items, JsonOpts)); }
        catch (JSException) { /* private browsing…: as long as the tab lives */ }
    }
}
