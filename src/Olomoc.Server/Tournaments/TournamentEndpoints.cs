using System.Security.Cryptography;
using Olomoc.Tournaments;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Tournaments API (/api/tournaments). Public reading; registration by anyone who has the code;
/// a registration changed by its player (player token) or the organizer; everything else by the
/// organizer (organizer token). The rules are those of TournamentRules.
/// </summary>
public static class TournamentEndpoints
{
    public const string WritePolicy = "tournament-write", CreatePolicy = "tournament-create", LoginPolicy = "tournament-login",
        ReadPolicy = "tournament-read";

    /// <summary>Sessions opened with the password, per tournament: beyond that, the oldest are closed.</summary>
    private const int MaxSessions = 20;

    public static void MapTournaments(this WebApplication app)
    {
        var api = app.MapGroup("/api/tournaments");

        api.MapPost("", Create).RequireRateLimiting(CreatePolicy);
        api.MapGet("{code}", Get).RequireRateLimiting(ReadPolicy);
        api.MapGet("{code}/export", Export).RequireRateLimiting(ReadPolicy);
        api.MapPost("{code}/organizer/login", Login).RequireRateLimiting(LoginPolicy);

        var write = api.MapGroup("{code}").RequireRateLimiting(WritePolicy);
        write.MapDelete("", Delete);
        write.MapPut("organizer/password", (string code, PasswordRequest body, HttpContext ctx, TournamentStore store) =>
            Organizer(code, ctx, store, t => SetPassword(t, body.Password, ctx.Request.Headers[TournamentHeaders.Organizer])));
        write.MapPost("players", Register);
        write.MapPut("players/{id}", UpdateRegistration);
        write.MapDelete("players/{id}", Unregister);
        write.MapPut("players/{id}/dropped", SetDropped);
        write.MapPost("start", (string code, HttpContext ctx, TournamentStore store) =>
            Organizer(code, ctx, store, t => Done(TournamentRules.Start(t))));
        write.MapPost("rounds", PairNext);
        write.MapDelete("rounds/last", (string code, HttpContext ctx, TournamentStore store) =>
            Organizer(code, ctx, store, t => Done(TournamentRules.UndoLastRound(t))));
        write.MapPost("rounds/{round:int}/swap", (string code, int round, SwapRequest body, HttpContext ctx, TournamentStore store) =>
            Organizer(code, ctx, store, t => Done(TournamentRules.SwapPlayers(t, round, body.First, body.Second))));
        write.MapPut("rounds/{round:int}/tables/{table:int}", (string code, int round, int table, ResultRequest body, HttpContext ctx, TournamentStore store) =>
            Organizer(code, ctx, store, t => Done(TournamentRules.SetResult(t, round, table, body.VpA, body.VpB, body.CabalA, body.CabalB, body.First, body.End), status: 400)));
        write.MapPut("scenarios", (string code, ScenariosRequest body, HttpContext ctx, TournamentStore store) =>
            Organizer(code, ctx, store, t => Done(TournamentRules.SetScenarios(t, body.Scenarios), status: 400)));
        // End of the tournament: anonymous archive for the statistics (if it is counted).
        write.MapPost("finish", (string code, HttpContext ctx, TournamentStore store, StatsArchive stats) =>
            Organizer(code, ctx, store, t =>
            {
                var result = Done(TournamentRules.Finish(t));
                if (result.Item1) stats.Update(t, DateTimeOffset.UtcNow);
                return result;
            }));
        // Count the tournament in the statistics or not, at any time.
        write.MapPut("stats", (string code, StatsRequest body, HttpContext ctx, TournamentStore store, StatsArchive stats) =>
            Organizer(code, ctx, store, t =>
            {
                t.ExcludeFromStats = body.Exclude;
                stats.Update(t, DateTimeOffset.UtcNow);
                return (true, Results.NoContent());
            }));

        app.MapGet("/api/stats", (StatsArchive stats) => Results.Json(stats.Stats(), TournamentJson.Options))
            .RequireRateLimiting(ReadPolicy);
    }

    // ---- Creation and reading ----

    private static async Task<IResult> Create(CreateTournamentRequest body, TournamentStore store)
    {
        var problems = TournamentRules.CheckSettings(body.Name, body.RoundCount, body.CabalsPerPlayer).ToList();
        if (TournamentRules.CheckPassword(NullIfEmpty(body.Password)) is { } bad) problems.Add(bad);
        if (problems.Count > 0) return Problems(problems, 400);

        var token = Tokens.New();
        var t = await store.CreateAsync(t =>
        {
            t.Name = body.Name.Trim();
            t.Date = body.Date;
            t.RoundCount = body.RoundCount;
            t.CabalsPerPlayer = body.CabalsPerPlayer;
            t.Pairing = body.Pairing;
            t.AvoidSameAlignment = body.Pairing == PairingMode.Swiss && body.AvoidSameAlignment;
            t.ExcludeFromStats = body.ExcludeFromStats;
            t.OrganizerTokenHash = Tokens.Hash(token);
            if (NullIfEmpty(body.Password) is { } password) t.OrganizerPasswordHash = Tokens.HashPassword(password);
        });
        return t is null
            ? Problems(new[] { Problem.Of("storageFull") }, 503)
            : Results.Json(new TournamentCreated(t.Code, token), TournamentJson.Options, statusCode: 201);
    }

    private static async Task<IResult> Get(string code, HttpContext ctx, TournamentStore store)
    {
        if (await store.LoadAsync(code) is not { } t) return NotFound();
        return Results.Json(View(t, IsOrganizer(ctx, t), PlayerOf(ctx, t)), TournamentJson.Options);
    }

    /// <summary>Complete tournament (organizer's backup), without the secrets.</summary>
    private static async Task<IResult> Export(string code, HttpContext ctx, TournamentStore store)
    {
        if (await store.LoadAsync(code) is not { } t) return NotFound();
        if (!IsOrganizer(ctx, t)) return Forbidden();
        t.OrganizerTokenHash = "";
        t.OrganizerPasswordHash = "";
        t.OrganizerSessionHashes.Clear();
        t.Players.ForEach(p => p.TokenHash = "");
        return Results.Json(t, TournamentJson.Options);
    }

    // ---- Organizer access on another device: password ----

    /// <summary>Right password: new organizer token for this device (the admin link stays valid).</summary>
    private static async Task<IResult> Login(string code, PasswordRequest body, TournamentStore store) =>
        await store.UpdateAsync(code, t =>
        {
            if (t.OrganizerPasswordHash.Length == 0) return (false, Problems(new[] { Problem.Of("noPassword") }, 409));
            if (!Tokens.VerifyPassword(body.Password, t.OrganizerPasswordHash)) return (false, Problems(new[] { Problem.Of("wrongPassword") }, 403));
            var token = Tokens.New();
            t.OrganizerSessionHashes.Add(Tokens.Hash(token));
            if (t.OrganizerSessionHashes.Count > MaxSessions) t.OrganizerSessionHashes.RemoveAt(0);
            return (true, Results.Json(new OrganizerLogin(token), TournamentJson.Options));
        }) ?? NotFound();

    /// <summary>
    /// Sets, changes or removes (empty) the password; the sessions opened with the old one are
    /// closed, except the one of the device making the change.
    /// </summary>
    private static (bool, IResult) SetPassword(Tournament t, string? password, string? currentToken)
    {
        password = NullIfEmpty(password);
        if (TournamentRules.CheckPassword(password) is { } problem) return (false, Problems(new[] { problem }, 400));
        t.OrganizerPasswordHash = password is null ? "" : Tokens.HashPassword(password);
        t.OrganizerSessionHashes.RemoveAll(h => !Tokens.Matches(currentToken, h));
        return (true, Results.NoContent());
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Deletion by the organizer: the tournament and its statistics archive.</summary>
    private static async Task<IResult> Delete(string code, HttpContext ctx, TournamentStore store, StatsArchive stats) =>
        await store.DeleteAsync(code, t =>
        {
            if (!IsOrganizer(ctx, t)) return (false, Forbidden());
            stats.Remove(t.Code);
            return (true, Results.NoContent());
        }) ?? NotFound();

    // ---- Registrations ----

    private static async Task<IResult> Register(string code, RegistrationRequest body, TournamentStore store, ServerCatalog catalog)
    {
        var cat = await catalog.GetAsync();
        var token = Tokens.New();
        var result = await store.UpdateAsync(code, t =>
        {
            var problems = TournamentRules.CheckRegistration(t, cat, body.Name, body.Faction, body.Cabals);
            if (problems.Count > 0) return (false, Problems(problems, 400));
            var player = new Player
            {
                Id = NewPlayerId(t),
                Name = body.Name.Trim(),
                Faction = body.Faction,
                Cabals = body.Cabals,
                RegisteredAt = DateTimeOffset.UtcNow,
                TokenHash = Tokens.Hash(token),
            };
            t.Players.Add(player);
            return (true, Results.Json(new PlayerRegistered(player.Id, token), TournamentJson.Options, statusCode: 201));
        });
        return result ?? NotFound();
    }

    private static async Task<IResult> UpdateRegistration(string code, string id, RegistrationRequest body, HttpContext ctx,
        TournamentStore store, ServerCatalog catalog)
    {
        var cat = await catalog.GetAsync();
        return await store.UpdateAsync(code, t =>
        {
            if (t.PlayerById(id) is not { } player) return (false, NotFoundPlayer());
            if (!IsOrganizer(ctx, t) && PlayerOf(ctx, t) != player) return (false, Forbidden());
            var problems = TournamentRules.CheckRegistration(t, cat, body.Name, body.Faction, body.Cabals, id);
            if (problems.Count > 0) return (false, Problems(problems, 400));
            player.Name = body.Name.Trim();
            player.Faction = body.Faction;
            player.Cabals = body.Cabals;
            return (true, Results.NoContent());
        }) ?? NotFound();
    }

    private static async Task<IResult> Unregister(string code, string id, HttpContext ctx, TournamentStore store) =>
        await store.UpdateAsync(code, t =>
        {
            if (t.PlayerById(id) is not { } player) return (false, NotFoundPlayer());
            if (!IsOrganizer(ctx, t) && PlayerOf(ctx, t) != player) return (false, Forbidden());
            if (TournamentRules.CanEditRegistrations(t) is { } problem) return (false, Problems(new[] { problem }, 409));
            t.Players.Remove(player);
            return (true, Results.NoContent());
        }) ?? NotFound();

    private static Task<IResult> SetDropped(string code, string id, DroppedRequest body, HttpContext ctx, TournamentStore store) =>
        Organizer(code, ctx, store, t => Done(TournamentRules.SetDropped(t, id, body.Dropped)));

    // ---- Rounds ----

    private static Task<IResult> PairNext(string code, HttpContext ctx, TournamentStore store, ServerCatalog catalog) =>
        OrganizerAsync(code, ctx, store, async () => await catalog.GetAsync(), (t, cat) =>
        {
            if (TournamentRules.CanPair(t) is { } problem) return (false, Problems(new[] { problem }, 409));
            // Seed drawn every time: undoing then pairing again gives a new draw.
            var outcome = TournamentRules.PairNext(t, cat.AlignmentOf, RandomNumberGenerator.GetInt32(int.MaxValue));
            return (true, Results.Json(new PairingSummary(outcome.Round.Number, outcome.Rematches, outcome.SameAlignment),
                TournamentJson.Options, statusCode: 201));
        });

    // ---- Helpers ----

    private static TournamentView View(Tournament t, bool organizer, Player? me) => new(
        t.Code, t.Name, t.Date, t.RoundCount, t.CabalsPerPlayer, t.Pairing, t.AvoidSameAlignment, t.Phase,
        t.Players.Select(p => new PlayerView(p.Id, p.Name, p.Faction, p.Dropped,
            t.Phase != TournamentPhase.Registration || organizer || p == me ? p.Cabals : null)).ToList(),
        t.Rounds, Standings.Compute(t), organizer, me?.Id, t.OrganizerPasswordHash.Length > 0, t.ExcludeFromStats, t.Scenarios);

    // Token of the admin link, or of a session opened with the password.
    private static bool IsOrganizer(HttpContext ctx, Tournament t)
    {
        string? token = ctx.Request.Headers[TournamentHeaders.Organizer];
        return Tokens.Matches(token, t.OrganizerTokenHash) || t.OrganizerSessionHashes.Any(h => Tokens.Matches(token, h));
    }

    private static Player? PlayerOf(HttpContext ctx, Tournament t)
    {
        string? token = ctx.Request.Headers[TournamentHeaders.Player];
        return string.IsNullOrEmpty(token) ? null : t.Players.FirstOrDefault(p => Tokens.Matches(token, p.TokenHash));
    }

    private static string NewPlayerId(Tournament t)
    {
        string id;
        do id = Tokens.NewId(); while (t.PlayerById(id) is not null);
        return id;
    }

    // Operation reserved to the organizer, saved if it succeeds.
    private static async Task<IResult> Organizer(string code, HttpContext ctx, TournamentStore store, Func<Tournament, (bool, IResult)> action) =>
        await store.UpdateAsync(code, t => IsOrganizer(ctx, t) ? action(t) : (false, Forbidden())) ?? NotFound();

    private static async Task<IResult> OrganizerAsync<TDep>(string code, HttpContext ctx, TournamentStore store,
        Func<Task<TDep>> dependency, Func<Tournament, TDep, (bool, IResult)> action)
    {
        var dep = await dependency();
        return await store.UpdateAsync(code, t => IsOrganizer(ctx, t) ? action(t, dep) : (false, Forbidden())) ?? NotFound();
    }

    private static (bool, IResult) Done(Problem? problem, int status = 409) =>
        problem is null ? (true, Results.NoContent()) : (false, Problems(new[] { problem }, status));

    private static IResult Problems(IReadOnlyList<Problem> problems, int status) =>
        Results.Json(new ProblemList(problems), TournamentJson.Options, statusCode: status);

    private static IResult NotFound() => Problems(new[] { Problem.Of("notFound") }, 404);
    private static IResult NotFoundPlayer() => Problems(new[] { Problem.Of("unknownPlayer") }, 404);
    private static IResult Forbidden() => Problems(new[] { Problem.Of("forbidden") }, 403);
}
