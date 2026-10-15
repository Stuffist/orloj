using Microsoft.AspNetCore.SignalR;
using Olomoc.Online;
using Olomoc.Server.Tournaments;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Server.Online;

/// <summary>
/// Synchronisation des parties en ligne (/hubs/online). Une connexion rejoint une partie avec le
/// jeton de son siège (ou sans jeton : spectateur), puis envoie ses commandes. Après chaque
/// changement, la salle reçoit « Changed » et chaque client relit sa propre vue (GetView) :
/// rien de secret ne transite vers une autre connexion que celle qui y a droit.
/// </summary>
public sealed class OnlineHub(OnlineStore store, TournamentStore tournaments, ServerCatalog catalog, OnlinePresence presence) : Hub
{
    public const string Path = "/hubs/online";
    public const string Changed = "Changed";

    private string? Code => Context.Items.TryGetValue("code", out var c) ? c as string : null;
    private int? MySeat => Context.Items.TryGetValue("seat", out var s) ? s as int? : null;

    public async Task<OnlineJoin> Join(string code, string? token)
    {
        if (await store.LoadAsync(code) is not { } game) return new(null, "notFound");
        var seat = await SeatOf(game, token);
        if (!string.IsNullOrEmpty(token) && seat is null) return new(null, "forbidden");

        await Leave();
        Context.Items["code"] = game.Code;
        Context.Items["seat"] = seat;
        await Groups.AddToGroupAsync(Context.ConnectionId, game.Code);
        if (seat is { } s && presence.Connect(game.Code, s, Context.ConnectionId))
            await Clients.OthersInGroup(game.Code).SendAsync(Changed, game.Revision);
        return new(OnlineRules.View(game, seat, presence.Connected(game.Code)));
    }

    public async Task<OnlineView?> GetView() =>
        Code is { } code && await store.LoadAsync(code) is { } game
            ? OnlineRules.View(game, MySeat, presence.Connected(game.Code))
            : null;

    public async Task<OnlineResult> Send(OnlineCommand command)
    {
        if (Code is not { } code || MySeat is not { } seat) return new(new[] { Problem.Of("forbidden") });
        var cat = await catalog.GetAsync();
        OnlineGame? updated = null;
        var wasLobby = false;
        var result = await store.UpdateAsync(code, g =>
        {
            wasLobby = g.Step == OnlineStep.Lobby;
            var r = OnlineRules.Apply(g, seat, command, cat, Random.Shared, DateTimeOffset.UtcNow);
            updated = g;
            return (r.Ok, r);
        });
        if (result is null) return new(new[] { Problem.Of("notFound") });
        if (result.Ok)
        {
            if (wasLobby && updated!.Step != OnlineStep.Lobby) await RecordCabals(updated);
            await Clients.Group(code).SendAsync(Changed, updated!.Revision);
        }
        return result;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Leave();
        await base.OnDisconnectedAsync(exception);
    }

    // Quitte la partie rejointe (autre partie, ou déconnexion) ; prévient la salle si le siège n'a plus de connexion.
    private async Task Leave()
    {
        if (Code is not { } code) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, code);
        if (MySeat is { } seat && presence.Disconnect(code, seat, Context.ConnectionId))
            await Clients.Group(code).SendAsync(Changed, -1);
        Context.Items.Remove("code");
        Context.Items.Remove("seat");
    }

    // Partie libre : jeton du siège. Partie de tournoi : jeton du joueur inscrit qui occupe le siège.
    private async Task<int?> SeatOf(OnlineGame game, string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (game.Tournament is { } tr)
        {
            var t = await tournaments.LoadAsync(tr.Code);
            var player = t?.Players.FirstOrDefault(p => Tokens.Matches(token, p.TokenHash));
            var index = player is null ? -1 : game.Seats.FindIndex(s => s.TournamentPlayerId == player.Id);
            return index < 0 ? null : index;
        }
        var i = game.Seats.FindIndex(s => Tokens.Matches(token, s.TokenHash));
        return i < 0 ? null : i;
    }

    // Partie de tournoi lancée : cabale jouée par chacun, notée à la table si l'organisateur ne l'a pas fait.
    private async Task RecordCabals(OnlineGame game)
    {
        if (game.Tournament is not { } tr) return;
        await tournaments.UpdateAsync(tr.Code, t =>
        {
            var table = t.Rounds.FirstOrDefault(r => r.Number == tr.Round)?.Tables.FirstOrDefault(x => x.Number == tr.Table);
            if (table is null) return (false, game);
            int? IndexOf(string? playerId, Seat seat)
            {
                var i = t.PlayerById(playerId ?? "")?.Cabals.FindIndex(c => seat.Cabal is not null && CabalLibrary.SameCards(c, seat.Cabal)) ?? -1;
                return i >= 0 ? i : null;
            }
            var changed = false;
            foreach (var seat in game.Seats)
            {
                if (seat.TournamentPlayerId == table.PlayerA && table.CabalA is null) { table.CabalA = IndexOf(table.PlayerA, seat); changed |= table.CabalA is not null; }
                if (seat.TournamentPlayerId == table.PlayerB && table.CabalB is null) { table.CabalB = IndexOf(table.PlayerB, seat); changed |= table.CabalB is not null; }
            }
            return (changed, game);
        });
    }
}

/// <summary>Au plus <see cref="PerSecond"/> appels par seconde et par connexion.</summary>
public sealed class OnlineRateFilter : IHubFilter
{
    public const int PerSecond = 20;

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var items = context.Context.Items;
        var now = Environment.TickCount64;
        var (start, count) = items.TryGetValue("rate", out var r) && r is (long s, int c) ? (s, c) : (now, 0);
        if (now - start >= 1000) (start, count) = (now, 0);
        items["rate"] = (start, count + 1);
        if (count >= PerSecond) throw new HubException("rate");
        return await next(context);
    }
}
