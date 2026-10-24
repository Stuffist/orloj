using Olomoc.Online;
using Olomoc.Server.Tournaments;
using Olomoc.Tournaments;

namespace Olomoc.Server.Online;

/// <summary>
/// Création des parties en ligne : partie libre (lien du créateur et lien d'invitation), ou
/// partie d'une table de tournoi, ouverte par l'un de ses deux joueurs (jeton de joueur).
/// Tout le reste passe par le hub (<see cref="OnlineHub"/>).
/// </summary>
public static class OnlineEndpoints
{
    public static void MapOnline(this WebApplication app)
    {
        app.MapPost("/api/online", Create).RequireRateLimiting(TournamentEndpoints.CreatePolicy);
        app.MapPost("/api/tournaments/{code}/rounds/{round:int}/tables/{table:int}/online", OpenTable)
            .RequireRateLimiting(TournamentEndpoints.WritePolicy);
        app.MapHub<OnlineHub>(OnlineHub.Path);
    }

    private static async Task<IResult> Create(CreateOnlineRequest? body, OnlineStore store, ServerCatalog catalog)
    {
        var name = body?.Name?.Trim() ?? "";
        if (name.Length > OnlineRules.MaxNameLength) return Problems(Problem.Of("playerName", OnlineRules.MaxNameLength.ToString()), 400);
        // Cabale du créateur (partie locale ouverte en ligne) : légale, et son siège prêt s'il est nommé.
        var cabal = body?.Cabal;
        if (cabal is not null && OnlineRules.CheckCabal(cabal, await catalog.GetAsync()) is { Count: > 0 } problems)
            return Results.Json(new ProblemList(problems), TournamentJson.Options, statusCode: 400);
        var (token, invite) = (Tokens.New(), Tokens.New());
        var game = await store.CreateAsync(g =>
        {
            var now = DateTimeOffset.UtcNow;
            var ready = cabal is not null && name.Length > 0;
            g.Seats.Add(new Seat { Name = name, TokenHash = Tokens.Hash(token), Cabal = cabal, Ready = ready });
            g.Seats.Add(new Seat { TokenHash = Tokens.Hash(invite) });
            if (name.Length > 0) g.Log.Add(new OnlineLogEntry(now, 0, "joined"));
            if (ready) g.Log.Add(new OnlineLogEntry(now, 0, "ready"));
        });
        return game is null
            ? Problems(Problem.Of("storageFull"), 503)
            : Results.Json(new OnlineCreated(game.Code, token, invite), TournamentJson.Options, statusCode: 201);
    }

    /// <summary>
    /// Partie de la table (ronde en cours, deux joueurs), créée au premier appel puis retrouvée :
    /// les sièges sont ceux des joueurs inscrits, avec leurs cabales inscrites.
    /// </summary>
    private static async Task<IResult> OpenTable(string code, int round, int table, HttpContext ctx,
        TournamentStore tournaments, OnlineStore store)
    {
        if (await tournaments.LoadAsync(code) is not { } t) return Problems(Problem.Of("notFound"), 404);
        string? token = ctx.Request.Headers[TournamentHeaders.Player];
        var me = string.IsNullOrEmpty(token) ? null : t.Players.FirstOrDefault(p => Tokens.Matches(token, p.TokenHash));
        if (me is null) return Problems(Problem.Of("forbidden"), 403);
        if (t.Phase != TournamentPhase.Started || t.LastRound?.Number != round) return Problems(Problem.Of("onlineRound"), 409);
        if (t.LastRound.Tables.FirstOrDefault(x => x.Number == table) is not { PlayerB: { } playerB } found || !found.Involves(me.Id))
            return Problems(Problem.Of("forbidden"), 403);
        if (found.OnlineCode is { } existing && await store.LoadAsync(existing) is not null) return Opened(existing);

        Seat SeatOf(string playerId)
        {
            var p = t.PlayerById(playerId)!;
            return new Seat { Name = p.Name, TournamentPlayerId = p.Id, AllowedCabals = p.Cabals.ToList() };
        }
        var game = await store.CreateAsync(g =>
        {
            g.Seats.Add(SeatOf(found.PlayerA));
            g.Seats.Add(SeatOf(playerB));
            g.Tournament = new OnlineTournamentRef(t.Code, round, table);
        });
        if (game is null) return Problems(Problem.Of("storageFull"), 503);

        // Notée à la table ; si l'autre joueur en a ouvert une entre-temps, c'est la sienne qui compte.
        var kept = await tournaments.UpdateAsync(code, x =>
        {
            var tb = x.Rounds.FirstOrDefault(r => r.Number == round)?.Tables.FirstOrDefault(y => y.Number == table);
            if (tb is null) return (false, game.Code);
            if (tb.OnlineCode is { } other && other != game.Code) return (false, other);
            tb.OnlineCode = game.Code;
            return (true, game.Code);
        });
        if (kept is null) return Problems(Problem.Of("notFound"), 404);
        if (kept != game.Code)
        {
            if (await store.LoadAsync(kept) is not null)
            {
                await store.DeleteAsync(game.Code, _ => (true, game.Code));
                return Opened(kept);
            }
            // L'ancienne partie a été purgée : la nouvelle la remplace.
            await tournaments.UpdateAsync(code, x =>
            {
                var tb = x.Rounds.First(r => r.Number == round).Tables.First(y => y.Number == table);
                tb.OnlineCode = game.Code;
                return (true, game.Code);
            });
        }
        return Opened(game.Code, created: true);
    }

    private static IResult Opened(string code, bool created = false) =>
        Results.Json(new OnlineOpened(code), TournamentJson.Options, statusCode: created ? 201 : 200);

    private static IResult Problems(Problem problem, int status) =>
        Results.Json(new ProblemList(new[] { problem }), TournamentJson.Options, statusCode: status);
}
