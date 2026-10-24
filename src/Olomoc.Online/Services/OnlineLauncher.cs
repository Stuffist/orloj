using Microsoft.AspNetCore.Components;
using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Online.Services;

/// <summary>
/// Ouvre un salon et en fait la partie courante de l'appareil (la partie locale est remplacée),
/// puis y mène : depuis le cadre « Jouer en ligne » (sans cabale, choisie au salon) ou depuis une
/// partie locale (« Ouvrir en ligne » : sa cabale, siège prêt d'office).
/// </summary>
public sealed class OnlineLauncher(OnlineApi api, MyGames games, GameStore store, LocalizationService L, NavigationManager nav)
{
    /// <summary>Problèmes qui ont empêché la création (aucun : on est parti vers le salon).</summary>
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
