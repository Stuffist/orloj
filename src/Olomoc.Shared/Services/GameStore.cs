using Microsoft.JSInterop;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Partie courante de l'appareil : une seule, locale (<see cref="Session"/>, sauvegardée dans le
/// localStorage après chaque modification pour survivre à un rechargement ou à la mise en veille
/// du téléphone) ou en ligne (<see cref="OnlineCode"/> : l'état est sur le serveur). L'historique
/// d'annulation de la partie locale reste en mémoire.
/// </summary>
public sealed class GameStore
{
    private const string StorageKey = "olomoc.game";
    private const string OnlineKey = "olomoc.game.online";

    private readonly IJSRuntime _js;
    private Task? _loading;

    public GameStore(IJSRuntime js) => _js = js;

    /// <summary>Partie locale en cours, ou null.</summary>
    public GameSession? Session { get; private set; }

    /// <summary>Code de la partie en ligne courante, ou null.</summary>
    public string? OnlineCode { get; private set; }

    /// <summary>Une partie est en cours sur l'appareil, locale ou en ligne (menu : point « partie en cours »).</summary>
    public bool HasGame => Session is not null || OnlineCode is not null;

    /// <summary>Partie lue, lancée, ouverte en ligne ou terminée.</summary>
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
        catch (JSException) { /* stockage indisponible : pas de partie à reprendre */ }
        Changed?.Invoke();
    }

    /// <summary>Nouvelle partie locale : remplace la partie courante, locale ou en ligne.</summary>
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
        catch (JSException) { /* navigation privée… : la partie vit le temps de l'onglet */ }
    }

    /// <summary>Fin de la partie locale.</summary>
    public async Task ClearAsync()
    {
        await RemoveLocalAsync();
        Changed?.Invoke();
    }

    /// <summary>Partie en ligne devenue la partie courante : la partie locale est effacée.</summary>
    public async Task OpenOnlineAsync(string code)
    {
        await RemoveLocalAsync();
        await SetOnlineAsync(code.ToUpperInvariant());
        Changed?.Invoke();
    }

    /// <summary>Partie en ligne quittée sur cet appareil (elle reste sur le serveur, reprise possible).</summary>
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
