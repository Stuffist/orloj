using Microsoft.AspNetCore.Components;
using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Online.Services;

/// <summary>
/// Opens a lobby and makes it the device's current game (the local game is replaced), then goes
/// there: from the "Play online" box (without a cabal, chosen in the lobby) or from a local game
/// ("Open online": its cabal, seat ready right away).
/// </summary>
public sealed class OnlineLauncher(OnlineApi api, MyGames games, GameStore store, LocalizationService L, NavigationManager nav)
{
    /// <summary>Problems that prevented the creation (none: we went to the lobby).</summary>
    public async Task<IReadOnlyList<Problem>> CreateAsync(string? name, CabalExport? cabal = null)
    {
        name = name?.Trim() is { Length: > 0 } given ? given : await games.DefaultNameAsync();
        var result = await api.CreateAsync(name, cabal);
        if (result.Value is not { } created) return result.Problems;
        await games.RememberNameAsync(name);
        await games.RememberAsync(created.Code, created.Token, created.InviteToken, L.Ui("online.game.title", created.Code));
        await store.OpenOnlineAsync(created.Code);
        nav.NavigateTo($"online/g/{created.Code}");
        return result.Problems;
    }
}
