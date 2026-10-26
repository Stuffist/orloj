using System.Text.Json;
using Microsoft.JSInterop;

namespace Olomoc.Pairings.Services;

/// <summary>Tournament followed on this device, with the tokens received (organizer, registration).</summary>
public sealed record MyTournament(string Code, string Name, string? OrganizerToken, string? PlayerId, string? PlayerToken, DateTimeOffset SeenAt);

/// <summary>
/// Tournaments followed on this device (localStorage, "olomoc.tournaments" key): those one
/// organizes (organizer token, the only admin key) and those one registered for (player token).
/// Losing the organizer token means losing the administration: the admin link (which contains it)
/// must be kept.
/// </summary>
public sealed class MyTournaments(IJSRuntime js)
{
    private const string StorageKey = "olomoc.tournaments";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private Task? _loading;
    private List<MyTournament> _items = new();

    /// <summary>Most recently viewed first.</summary>
    public IReadOnlyList<MyTournament> All => _items;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public MyTournament? Get(string code) => _items.FirstOrDefault(t => Same(t.Code, code));

    /// <summary>Updates the tournament's entry (tokens not given are kept).</summary>
    public async Task RememberAsync(string code, string name, string? organizerToken = null, string? playerId = null, string? playerToken = null)
    {
        var old = Get(code);
        _items.RemoveAll(t => Same(t.Code, code));
        _items.Insert(0, new MyTournament(code.ToUpperInvariant(), name,
            organizerToken ?? old?.OrganizerToken, playerId ?? old?.PlayerId, playerToken ?? old?.PlayerToken, DateTimeOffset.Now));
        await StoreAsync();
    }

    /// <summary>Forgets the registration (withdrawal) while keeping the rest.</summary>
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
        catch (JSException) { /* storage unavailable */ }
        catch (JsonException) { /* unreadable content: ignored */ }
    }

    private async Task StoreAsync()
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(_items, JsonOpts)); }
        catch (JSException) { /* private browsing…: as long as the tab lives */ }
    }
}
