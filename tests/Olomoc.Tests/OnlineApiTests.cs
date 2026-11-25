using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Online games: creation, SignalR hub (seats, spectators, synchronization), tournament tables.</summary>
public sealed class OnlineApiTests : IAsyncDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _servers = new();
    private readonly List<HubConnection> _hubs = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var hub in _hubs) await hub.DisposeAsync();
        _servers.ForEach(s => s.Dispose());
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    private WebApplicationFactory<Program> Server()
    {
        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("RATE_CREATE_PER_HOUR", "1000"));
        _servers.Add(server);
        return server;
    }

    // SignalR client plugged into the in-memory server (long polling: no WebSocket in tests).
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

    private static Task<OnlineJoin> Join(HubConnection hub, string code, string? token) =>
        hub.InvokeAsync<OnlineJoin>("Join", code, token);

    private static Task<OnlineView?> View(HubConnection hub) => hub.InvokeAsync<OnlineView?>("GetView");

    private static async Task Ok(HubConnection hub, OnlineCommand command)
    {
        var r = await hub.InvokeAsync<OnlineResult>("Send", command);
        Assert.True(r.Ok, string.Join(", ", r.Problems.Select(p => p.Key)));
    }

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options))!;

    [Fact]
    public async Task Free_game_end_to_end()
    {
        var server = Server();
        var (_, cabalA, cabalB) = await OnlineTestGame.Cabals();
        var response = await server.CreateClient().PostAsJsonAsync("/api/online", new CreateOnlineRequest("Ana"), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Read<OnlineCreated>(response);

        var a = await Connect(server);
        var b = await Connect(server);
        var watcher = await Connect(server);
        Assert.Equal(0, (await Join(a, created.Code, created.Token)).View!.Me);
        Assert.Equal(1, (await Join(b, created.Code, created.InviteToken)).View!.Me);
        Assert.Null((await Join(watcher, created.Code, null)).View!.Me);
        Assert.Equal("forbidden", (await Join(await Connect(server), created.Code, "faux")).Error);
        Assert.Equal("forbidden", (await watcher.InvokeAsync<OnlineResult>("Send", new OnlineCommand(OnlineCommandType.SetReady, Flag: true))).Problems[0].Key);

        // Lobby: cabals secret until both are ready.
        await Ok(b, new(OnlineCommandType.SetName, "Bo"));
        await Ok(a, new(OnlineCommandType.SetCabal, Cabal: cabalA));
        await Ok(a, new(OnlineCommandType.SetReady, Flag: true));
        Assert.Null((await View(b))!.Seats[0].Cabal);
        await Ok(b, new(OnlineCommandType.SetCabal, Cabal: cabalB));
        await Ok(b, new(OnlineCommandType.SetReady, Flag: true));
        Assert.Equal("A", (await View(b))!.Seats[0].Cabal!.Name);

        // Initiative: roll on both sides until there is a winner (tie: roll again).
        for (var i = 0; i < 50 && (await View(a))!.Step == OnlineStep.Initiative; i++)
        {
            await Ok(a, new(OnlineCommandType.RollInitiative));
            await Ok(b, new(OnlineCommandType.RollInitiative));
        }
        var first = (await View(a))!.First!.Value;
        await Ok(first == 0 ? a : b, new(OnlineCommandType.ChooseAgenda, "VISITS"));
        Assert.Equal(OnlineStep.Cycle, (await View(a))!.Step);

        // A wounds one of their cogs: B and the spectator see it.
        var mine = (await View(a))!.Mine!;
        var session = new GameSession(mine.State!);
        session.Wound(0, 1, attack: false, ignoreProtection: false);
        await Ok(a, new(OnlineCommandType.UpdateState, State: session.State, BasedOn: mine.StateRevision));
        Assert.Equal(1, (await View(b))!.Seats[0].State!.Cogs[0].Marked);
        Assert.Equal(1, (await View(watcher))!.Seats[0].State!.Cogs[0].Marked);

        // The game survives a server restart (JSON document).
        var view = (await Join(await Connect(Server()), created.Code, created.InviteToken)).View!;
        Assert.Equal(1, view.Seats[0].State!.Cogs[0].Marked);
    }

    [Fact]
    public async Task Game_of_a_tournament_table()
    {
        var server = Server();
        var http = server.CreateClient();
        var (_, cabalA, cabalB) = await OnlineTestGame.Cabals();
        var t = await Read<TournamentCreated>(await http.PostAsJsonAsync("/api/tournaments",
            new CreateTournamentRequest("Open", null, 3, 2), TournamentJson.Options));
        async Task<PlayerRegistered> Register(string name) => await Read<PlayerRegistered>(await http.PostAsJsonAsync(
            $"/api/tournaments/{t.Code}/players", new RegistrationRequest(name, "MAISEL", new List<CabalExport> { cabalA, cabalB }), TournamentJson.Options));
        var ana = await Register("Ana");
        var bo = await Register("Bo");
        HttpRequestMessage Post(string url, string? player = null, string? organizer = null)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, url);
            if (player is not null) r.Headers.Add(TournamentHeaders.Player, player);
            if (organizer is not null) r.Headers.Add(TournamentHeaders.Organizer, organizer);
            return r;
        }
        var open = $"/api/tournaments/{t.Code}/rounds/1/tables/1/online";
        await http.SendAsync(Post($"/api/tournaments/{t.Code}/start", organizer: t.OrganizerToken));
        await http.SendAsync(Post($"/api/tournaments/{t.Code}/rounds", organizer: t.OrganizerToken));

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post(open))).StatusCode);
        var code = (await Read<OnlineOpened>(await http.SendAsync(Post(open, ana.PlayerToken)))).Code;
        Assert.Equal(code, (await Read<OnlineOpened>(await http.SendAsync(Post(open, bo.PlayerToken)))).Code);

        // Each one identifies with their player token; their registered cabals are offered.
        var a = await Connect(server);
        var b = await Connect(server);
        var viewA = (await Join(a, code, ana.PlayerToken)).View!;
        Assert.Equal("Ana", viewA.Mine!.Name);
        Assert.Equal(2, viewA.Mine.AllowedCabals.Count);
        await Join(b, code, bo.PlayerToken);

        // Cabals started: recorded on the tournament table (Ana: her 2nd registered cabal, Bo: his 1st).
        await Ok(a, new(OnlineCommandType.SetCabal, Cabal: cabalB));
        await Ok(b, new(OnlineCommandType.SetCabal, Cabal: cabalA));
        await Ok(a, new(OnlineCommandType.SetReady, Flag: true));
        await Ok(b, new(OnlineCommandType.SetReady, Flag: true));
        var table = (await Read<TournamentView>(await http.GetAsync($"/api/tournaments/{t.Code}"))).Rounds[0].Tables[0];
        Assert.Equal(code, table.OnlineCode);
        Assert.Equal(table.PlayerA == ana.PlayerId ? 1 : 0, table.CabalA);
        Assert.Equal(table.PlayerB == ana.PlayerId ? 1 : 0, table.CabalB);
    }
}
