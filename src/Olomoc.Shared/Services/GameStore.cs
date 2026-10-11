using Microsoft.JSInterop;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Partie en cours de l'appareil : une seule, sauvegardée dans le localStorage après chaque
/// modification pour survivre à un rechargement ou à la mise en veille du téléphone.
/// L'historique d'annulation reste en mémoire.
/// </summary>
public sealed class GameStore
{
    private const string StorageKey = "olomoc.game";

    private readonly IJSRuntime _js;
    private Task? _loading;

    public GameStore(IJSRuntime js) => _js = js;

    public GameSession? Session { get; private set; }

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var state = GameSession.FromJson(await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey));
            if (state is not null) Session = new GameSession(state);
        }
        catch (JSException) { /* stockage indisponible : pas de partie à reprendre */ }
    }

    public async Task StartAsync(GameState state)
    {
        Session = new GameSession(state);
        await SaveAsync();
    }

    public async Task SaveAsync()
    {
        if (Session is null) return;
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, GameSession.ToJson(Session.State)); }
        catch (JSException) { /* navigation privée… : la partie vit le temps de l'onglet */ }
    }

    public async Task ClearAsync()
    {
        Session = null;
        try { await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey); }
        catch (JSException) { }
    }
}
