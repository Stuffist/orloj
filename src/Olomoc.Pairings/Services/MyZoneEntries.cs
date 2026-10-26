using System.Text.Json;
using Microsoft.JSInterop;

namespace Olomoc.Pairings.Services;

/// <summary>A game entered from this device: zone, id, entry token (to change or delete it).</summary>
public sealed record MyZoneEntry(string Zone, long Id, string Token);

/// <summary>A zone this device entered games in, for "My zones".</summary>
public sealed record MyZone(string Code, string Name, DateTimeOffset SeenAt);

/// <summary>
/// Games entered from this device and the zones concerned (localStorage, "olomoc.zones" key).
/// The tokens are the only way to change a game entered without an account; with an account, the
/// account is enough on any device.
/// </summary>
public sealed class MyZoneEntries(IJSRuntime js)
{
    private const string StorageKey = "olomoc.zones";
    private const int MaxEntries = 500;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record Stored(List<MyZone> Zones, List<MyZoneEntry> Entries);

    private Task? _loading;
    private Stored _data = new(new(), new());

    /// <summary>Most recently used first.</summary>
    public IReadOnlyList<MyZone> Zones => _data.Zones;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    public string? TokenOf(string zone, long id) =>
        _data.Entries.FirstOrDefault(e => Same(e.Zone, zone) && e.Id == id)?.Token;

    public async Task RememberAsync(string zone, string name, long id, string token)
    {
        _data.Entries.RemoveAll(e => Same(e.Zone, zone) && e.Id == id);
        _data.Entries.Insert(0, new MyZoneEntry(zone.ToUpperInvariant(), id, token));
        if (_data.Entries.Count > MaxEntries) _data.Entries.RemoveRange(MaxEntries, _data.Entries.Count - MaxEntries);
        await SeenAsync(zone, name, store: false);
        await StoreAsync();
    }

    /// <summary>Zone in "My zones" (name updated), most recent first.</summary>
    public async Task SeenAsync(string zone, string name, bool store = true)
    {
        _data.Zones.RemoveAll(z => Same(z.Code, zone));
        _data.Zones.Insert(0, new MyZone(zone.ToUpperInvariant(), name, DateTimeOffset.Now));
        if (store) await StoreAsync();
    }

    public bool Knows(string zone) => _data.Zones.Any(z => Same(z.Code, zone));

    public async Task ForgetEntryAsync(string zone, long id)
    {
        _data.Entries.RemoveAll(e => Same(e.Zone, zone) && e.Id == id);
        await StoreAsync();
    }

    /// <summary>Out of "My zones"; the entry tokens are kept (its games can still be changed).</summary>
    public async Task ForgetZoneAsync(string zone)
    {
        _data.Zones.RemoveAll(z => Same(z.Code, zone));
        await StoreAsync();
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private async Task LoadAsync()
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(json) && JsonSerializer.Deserialize<Stored>(json, JsonOpts) is { } data)
                _data = new(data.Zones ?? new(), data.Entries ?? new());
        }
        catch (JSException) { /* storage unavailable */ }
        catch (JsonException) { /* unreadable content: ignored */ }
    }

    private async Task StoreAsync()
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(_data, JsonOpts)); }
        catch (JSException) { /* private browsing…: as long as the tab lives */ }
    }
}
