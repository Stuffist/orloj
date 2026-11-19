using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Server.Data;
using Olomoc.Server.Online;
using Olomoc.Server.Zones;
using Olomoc.Services;
using Olomoc.Tournaments;
using Olomoc.Tournaments.Stats;
using Olomoc.Zones;
using Xunit;

namespace Olomoc.Tests;

/// <summary>
/// Results of the online games into the statistics: the Block zone receiving the online games (free
/// games), the tournament table (tournament games), the purge of a finished game nobody confirmed.
/// </summary>
public sealed class OnlineResultsApiTests : IAsyncDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-results-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _servers = new();
    private readonly List<HubConnection> _hubs = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var hub in _hubs) await hub.DisposeAsync();
        _servers.ForEach(s => s.Dispose());
        TestDb.Delete(_data);
    }

    private WebApplicationFactory<Program> Server()
    {
        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("RATE_CREATE_PER_HOUR", "1000"));
        _servers.Add(server);
        return server;
    }

    private async Task<HubConnection> Connect(WebApplicationFactory<Program> server)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server.Server.BaseAddress, "/hubs/online"), o =>
            {
                o.HttpMessageHandlerFactory = _ => server.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(o => TournamentJson.Configure(o.PayloadSerializerOptions))
            .Build();
        await hub.StartAsync();
        _hubs.Add(hub);
        return hub;
    }

    private static Task<OnlineView?> View(HubConnection hub) => hub.InvokeAsync<OnlineView?>("GetView");

    private static async Task Ok(HubConnection hub, OnlineCommand command)
    {
        var r = await hub.InvokeAsync<OnlineResult>("Send", command);
        Assert.True(r.Ok, string.Join(", ", r.Problems.Select(p => p.Key)));
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options))!;
    }

    private static async Task<(CabalExport A, CabalExport B)> Cabals()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(), slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        return (Cabal("Ana's", ("3R06", "E03"), ("2R08", "D01"), ("1R01", "E11")),
                Cabal("B", ("3R06", "E03"), ("2R11", "E07"), ("1R01", "E11")));
    }

    /// <summary>Both players joined and ready: initiative rolled, agenda chosen, then a Bile at 3 for seat 1 (game over).</summary>
    private static async Task PlayToTheEnd(HubConnection a, HubConnection b, int vpA = 0)
    {
        for (var i = 0; i < 50 && (await View(a))!.Step == OnlineStep.Initiative; i++)
        {
            await Ok(a, new(OnlineCommandType.RollInitiative));
            await Ok(b, new(OnlineCommandType.RollInitiative));
        }
        var view = (await View(a))!;
        await Ok(view.First == view.Me ? a : b, new(OnlineCommandType.ChooseAgenda, "VISITS"));

        var mine = (await View(a))!.Mine!;
        var state = GameSession.FromJson(GameSession.ToJson(mine.State!))!;
        state.VictoryPoints = vpA;
        await Ok(a, new(OnlineCommandType.UpdateState, State: state, BasedOn: mine.StateRevision));
        var theirs = (await View(b))!.Mine!;
        var lost = GameSession.FromJson(GameSession.ToJson(theirs.State!))!;
        lost.Bile = GameSession.MaxBile;
        await Ok(b, new(OnlineCommandType.UpdateState, State: lost, BasedOn: theirs.StateRevision));
        Assert.True((await View(a))!.Over);
    }

    /// <summary>A free game (Ana: seat 0, Bo: seat 1), played to its end.</summary>
    private async Task<(string Code, HubConnection A, HubConnection B)> FreeGame(WebApplicationFactory<Program> server, int vpA = 0)
    {
        var (cabalA, cabalB) = await Cabals();
        var created = await Read<OnlineCreated>(await server.CreateClient().PostAsJsonAsync("/api/online",
            new CreateOnlineRequest("Ana", cabalA), TournamentJson.Options));
        var a = await Connect(server);
        var b = await Connect(server);
        await a.InvokeAsync<OnlineJoin>("Join", created.Code, created.Token);
        await b.InvokeAsync<OnlineJoin>("Join", created.Code, created.InviteToken);
        await Ok(b, new(OnlineCommandType.SetName, "Bo"));
        await Ok(b, new(OnlineCommandType.SetCabal, Cabal: cabalB));
        await Ok(b, new(OnlineCommandType.SetReady, Flag: true));
        await PlayToTheEnd(a, b, vpA);
        return (created.Code, a, b);
    }

    private static async Task<string> ReceivingZone(WebApplicationFactory<Program> server, bool receives = true)
    {
        var zone = await server.Services.GetRequiredService<ZoneStore>().CreateAsync(z =>
        {
            z.Name = "En ligne";
            z.Kind = ZoneKind.Online;
            z.ReceivesOnline = receives;
        });
        return zone!.Code;
    }

    private static async Task<ZoneGamesPage> Games(HttpClient http, string zone) => await Read<ZoneGamesPage>(await http.GetAsync($"/api/zones/{zone}/games"));

    // The game unchanged for 8 days: a candidate for the purge.
    private static async Task Age(WebApplicationFactory<Program> server, string code)
    {
        await using var db = await server.Services.GetRequiredService<IDbContextFactory<OlomocDb>>().CreateDbContextAsync();
        await db.Documents.Where(d => d.Kind == OnlineStore.Kind && d.Code == code)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdatedAt, DateTime.UtcNow.AddDays(-8)));
    }

    private static Task<int> Purge(WebApplicationFactory<Program> server) =>
        OnlinePurge.PurgeAsync(server.Services.GetRequiredService<OnlineStore>(), server.Services.GetRequiredService<OnlineResults>());

    [Fact]
    public async Task A_confirmed_free_game_lands_once_in_the_zone_receiving_online_games()
    {
        var server = Server();
        var http = server.CreateClient();
        var zone = await ReceivingZone(server);
        await ReceivingZone(server, receives: false);          // another Online zone: not this one
        var (code, a, b) = await FreeGame(server, vpA: 6);

        await Ok(b, new(OnlineCommandType.ConfirmResult, Vp: [7, 2]));
        var view = (await View(a))!;
        Assert.Equal(OnlineStep.Finished, view.Step);
        Assert.True(view.Result!.Recorded);
        Assert.Equal(1, view.Result.ConfirmedBy);

        var game = Assert.Single((await Games(http, zone)).Games);
        Assert.True(game.Online);
        Assert.False(game.CanEdit);
        Assert.Equal((GameFormat.Duel, 7, 2, GameEnd.Bile, "VISITS"), (game.Game.Format, game.Game.A.Vp, game.Game.B.Vp, game.Game.End, game.Game.Scenario?.Id));
        Assert.Equal(("MAISEL", "2R08"), (game.Game.A.Players[0].Faction, game.Game.A.Players[0].Cabal!.Cogs.Single(c => c.Rank == 2).Cog.Id));
        Assert.All(game.Game.Players, p => Assert.Null(p.Name));
        Assert.All(game.Game.Players, p => Assert.Null(p.Cabal!.Name));
        Assert.NotNull(game.Game.First);
        Assert.Equal(1, (await Read<StatsView>(await http.GetAsync("/api/stats/all"))).Games);

        // Not changed from the zone; corrected from the game: the same row.
        var refused = await http.PutAsJsonAsync($"/api/zones/{zone}/games/{game.Id}", game.Game, TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("zoneGameFromOnline", (await refused.Content.ReadFromJsonAsync<ProblemList>(TournamentJson.Options))!.Problems[0].Key);
        await Ok(a, new(OnlineCommandType.ConfirmResult, Vp: [8, 2]));
        var corrected = Assert.Single((await Games(http, zone)).Games);
        Assert.Equal((game.Id, 8), (corrected.Id, corrected.Game.A.Vp));

        // Purged later: already recorded, the zone keeps it.
        await Age(server, code);
        Assert.Equal(1, await Purge(server));
        Assert.Single((await Games(http, zone)).Games);
    }

    [Fact]
    public async Task Without_a_receiving_zone_the_result_waits_for_the_purge()
    {
        var server = Server();
        var http = server.CreateClient();
        var (code, a, _) = await FreeGame(server);
        await Ok(a, new(OnlineCommandType.ConfirmResult, Vp: [4, 1]));
        Assert.False((await View(a))!.Result!.Recorded);

        // A zone receives them afterwards: the purge records the result, then deletes the game.
        var zone = await ReceivingZone(server);
        await Age(server, code);
        Assert.Equal(1, await Purge(server));
        Assert.Null(await server.Services.GetRequiredService<OnlineStore>().LoadAsync(code));
        Assert.Equal(4, Assert.Single((await Games(http, zone)).Games).Game.A.Vp);
    }

    [Fact]
    public async Task A_finished_game_nobody_confirmed_is_recorded_from_the_sheets_when_purged()
    {
        var server = Server();
        var http = server.CreateClient();
        var zone = await ReceivingZone(server);
        var (code, _, _) = await FreeGame(server, vpA: 5);
        // And an abandoned game (still in the lobby): never recorded.
        var (cabalA, _) = await Cabals();
        var abandoned = await Read<OnlineCreated>(await http.PostAsJsonAsync("/api/online", new CreateOnlineRequest("Cy", cabalA), TournamentJson.Options));
        Assert.Empty((await Games(http, zone)).Games);

        await Age(server, code);
        await Age(server, abandoned.Code);
        Assert.Equal(2, await Purge(server));
        var game = Assert.Single((await Games(http, zone)).Games);
        Assert.Equal((5, 0, GameEnd.Bile), (game.Game.A.Vp, game.Game.B.Vp, game.Game.End));
    }

    [Fact]
    public async Task A_tournament_game_writes_its_result_on_the_table_if_it_has_none()
    {
        var server = Server();
        var http = server.CreateClient();
        await ReceivingZone(server);
        var (cabalA, cabalB) = await Cabals();
        var t = await Read<TournamentCreated>(await http.PostAsJsonAsync("/api/tournaments", new CreateTournamentRequest("Open", null, 3, 1), TournamentJson.Options));
        async Task<PlayerRegistered> Register(string name, CabalExport cabal) => await Read<PlayerRegistered>(await http.PostAsJsonAsync(
            $"/api/tournaments/{t.Code}/players", new RegistrationRequest(name, "MAISEL", [cabal]), TournamentJson.Options));
        var ana = await Register("Ana", cabalA);
        var bo = await Register("Bo", cabalB);
        HttpRequestMessage Request(HttpMethod method, string url, string? player = null, string? organizer = null, object? body = null)
        {
            var r = new HttpRequestMessage(method, url);
            if (player is not null) r.Headers.Add(TournamentHeaders.Player, player);
            if (organizer is not null) r.Headers.Add(TournamentHeaders.Organizer, organizer);
            if (body is not null) r.Content = JsonContent.Create(body, body.GetType(), options: TournamentJson.Options);
            return r;
        }
        await http.SendAsync(Request(HttpMethod.Post, $"/api/tournaments/{t.Code}/start", organizer: t.OrganizerToken));
        await http.SendAsync(Request(HttpMethod.Post, $"/api/tournaments/{t.Code}/rounds", organizer: t.OrganizerToken));
        var code = (await Read<OnlineOpened>(await http.SendAsync(Request(HttpMethod.Post, $"/api/tournaments/{t.Code}/rounds/1/tables/1/online", ana.PlayerToken)))).Code;

        var a = await Connect(server);
        var b = await Connect(server);
        await a.InvokeAsync<OnlineJoin>("Join", code, ana.PlayerToken);
        await b.InvokeAsync<OnlineJoin>("Join", code, bo.PlayerToken);
        await Ok(a, new(OnlineCommandType.SetCabal, Cabal: cabalA));
        await Ok(b, new(OnlineCommandType.SetCabal, Cabal: cabalB));
        await Ok(a, new(OnlineCommandType.SetReady, Flag: true));
        await Ok(b, new(OnlineCommandType.SetReady, Flag: true));
        await PlayToTheEnd(a, b);
        var seatOfAna = (await View(a))!.Me!.Value;

        await Ok(a, new(OnlineCommandType.ConfirmResult, Vp: seatOfAna == 0 ? [9, 3] : [3, 9]));
        var table = (await Read<TournamentView>(await http.GetAsync($"/api/tournaments/{t.Code}"))).Rounds[0].Tables[0];
        var (vpAna, vpBo) = table.PlayerA == ana.PlayerId ? (table.VpA, table.VpB) : (table.VpB, table.VpA);
        Assert.Equal((9, 3, GameEnd.Bile), (vpAna, vpBo, table.End));
        Assert.NotNull(table.First);

        // The organizer's result stays: a new confirmation does not replace it.
        var round = $"/api/tournaments/{t.Code}/rounds/1/tables/1";
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Put, round, organizer: t.OrganizerToken, body: new ResultRequest(5, 5)))).StatusCode);
        await Ok(b, new(OnlineCommandType.ConfirmResult, Vp: [1, 1]));
        table = (await Read<TournamentView>(await http.GetAsync($"/api/tournaments/{t.Code}"))).Rounds[0].Tables[0];
        Assert.Equal((5, 5), (table.VpA, table.VpB));

        // Never in the zone receiving online games: it counts through the tournament.
        var zones = await Read<List<ZoneSummary>>(await http.GetAsync("/api/zones"));
        Assert.All(zones, z => Assert.Equal(0, z.Games));
    }
}
