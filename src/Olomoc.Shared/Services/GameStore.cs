using Microsoft.JSInterop;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// The device's current game: only one, local (<see cref="Session"/>, saved in localStorage after
/// each change to survive a reload or the phone going to sleep) or online (<see cref="OnlineCode"/>:
/// the state is on the server). The local game's undo history stays in memory.
/// </summary>
public sealed class GameStore
{
    private const string StorageKey = "olomoc.game";
    private const string OnlineKey = "olomoc.game.online";

    private readonly IJSRuntime _js;
    private Task? _loading;

    public GameStore(IJSRuntime js) => _js = js;

    /// <summary>Local game in progress, or null.</summary>
    public GameSession? Session { get; private set; }

    /// <summary>Code of the current online game, or null.</summary>
    public string? OnlineCode { get; private set; }

    /// <summary>A game is in progress on the device, local or online (menu: "game in progress" dot).</summary>
    public bool HasGame => Session is not null || OnlineCode is not null;

    /// <summary>Game read, started, opened online or finished.</summary>
    public event Action? Changed;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var state = GameSession.FromJson(await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey));
            if (state is not null) Session = new GameSession(state);
            OnlineCode = await _js.InvokeAsync<string?>("localStorage.getItem", OnlineKey) is { Length: > 0 } code ? code : null;
        }
        catch (JSException) { /* storage unavailable: no game to resume */ }
        Changed?.Invoke();
    }

    /// <summary>New local game: replaces the current game, local or online.</summary>
    public async Task StartAsync(GameState state)
    {
        Session = new GameSession(state);
        await SaveAsync();
        await SetOnlineAsync(null);
        Changed?.Invoke();
    }

    public async Task SaveAsync()
    {
        if (Session is null) return;
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, GameSession.ToJson(Session.State)); }
        catch (JSException) { /* private browsing…: the game lives as long as the tab */ }
    }

    /// <summary>End of the local game.</summary>
    public async Task ClearAsync()
    {
        await RemoveLocalAsync();
        Changed?.Invoke();
    }

    /// <summary>Online game became the current game: the local game is erased.</summary>
    public async Task OpenOnlineAsync(string code)
    {
        await RemoveLocalAsync();
        await SetOnlineAsync(code.ToUpperInvariant());
        Changed?.Invoke();
    }

    /// <summary>Online game left on this device (it stays on the server, can be resumed).</summary>
    public async Task LeaveOnlineAsync()
    {
        await SetOnlineAsync(null);
        Changed?.Invoke();
    }

    private async Task RemoveLocalAsync()
    {
        Session = null;
        try { await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey); }
        catch (JSException) { }
    }

    private async Task SetOnlineAsync(string? code)
    {
        OnlineCode = code;
        try
        {
            if (code is null) await _js.InvokeVoidAsync("localStorage.removeItem", OnlineKey);
            else await _js.InvokeVoidAsync("localStorage.setItem", OnlineKey, code);
        }
        catch (JSException) { }
    }
}
