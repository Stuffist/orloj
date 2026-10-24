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

/// <summary>Parties en ligne : création, hub SignalR (sièges, spectateurs, synchronisation), tables de tournoi.</summary>
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

    private WebApplicationFactory<Program> Server(int createPerHour = 1000)
    {
        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("RATE_CREATE_PER_HOUR", createPerHour.ToString()));
        _servers.Add(server);
        return server;
    }

    // Client SignalR branché sur le serveur en mémoire (long polling : pas de WebSocket en test).
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

    private static async Task<(CabalExport A, CabalExport B)> MaiselCabals()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(),
                slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        return (Cabal("A", ("3R06", "E03" /* Phéromones */), ("2R08", "D01" /* Carapace */), ("1R01", "E11" /* Harmonium */)),
                Cabal("B", ("3R06", "E03" /* Phéromones */), ("2R11", "E07" /* Ration */), ("1R01", "E11" /* Harmonium */)));
    }

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options))!;

    private static async Task<OnlineCreated> Create(HttpClient http, string? name = "Ana")
    {
        var response = await http.PostAsJsonAsync("/api/online", new CreateOnlineRequest(name), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Read<OnlineCreated>(response);
    }

    // Lancer les dés de part et d'autre jusqu'à départager (égalité : on relance).
    private static async Task RollUntilFirst(HubConnection a, HubConnection b)
    {
        for (var i = 0; i < 50 && (await View(a))!.Step == OnlineStep.Initiative; i++)
        {
            await Ok(a, new(OnlineCommandType.RollInitiative));
            await Ok(b, new(OnlineCommandType.RollInitiative));
        }
        Assert.Equal(OnlineStep.Agenda, (await View(a))!.Step);
    }

    [Fact]
    public async Task Partie_locale_ouverte_en_ligne_cabale_prete()
    {
        var server = Server();
        var http = server.CreateClient();
        var (cabalA, cabalB) = await MaiselCabals();
        var response = await http.PostAsJsonAsync("/api/online", new CreateOnlineRequest("Ana", cabalA), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Read<OnlineCreated>(response);

        var a = await Connect(server);
        var b = await Connect(server);
        var mine = (await Join(a, created.Code, created.Token)).View!.Mine!;
        Assert.True(mine.Ready);
        Assert.Equal("A", mine.Cabal!.Name);
        var guest = (await Join(b, created.Code, created.InviteToken)).View!;
        Assert.True(guest.Seats[0].Ready);
        Assert.Null(guest.Seats[0].Cabal);                    // secrète jusqu'à ce que l'invité soit prêt

        await Ok(b, new(OnlineCommandType.SetName, "Bo"));
        await Ok(b, new(OnlineCommandType.SetCabal, Cabal: cabalB));
        await Ok(b, new(OnlineCommandType.SetReady, Flag: true));
        var started = (await View(a))!;
        Assert.Equal(OnlineStep.Initiative, started.Step);    // la partie commence au début
        Assert.Equal("A", started.Seats[0].Cabal!.Name);
    }

    [Fact]
    public async Task Salon_avec_une_cabale_illegale_refuse()
    {
        var http = Server().CreateClient();
        var (cabalA, _) = await MaiselCabals();
        var illegal = cabalA with { Faction = new CodeName("INCONNUE", "?") };
        var response = await http.PostAsJsonAsync("/api/online", new CreateOnlineRequest("Ana", illegal), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("faction", (await Read<ProblemList>(response)).Problems[0].Key);
    }

    [Fact]
    public async Task Partie_libre_de_bout_en_bout()
    {
        var server = Server();
        var http = server.CreateClient();
        var (cabalA, cabalB) = await MaiselCabals();
        var created = await Create(http);
        Assert.Equal(6, created.Code.Length);
        Assert.NotEqual(created.Token, created.InviteToken);

        var a = await Connect(server);
        var b = await Connect(server);
        var watcher = await Connect(server);
        Assert.Equal(0, (await Join(a, created.Code, created.Token)).View!.Me);
        Assert.Equal(1, (await Join(b, created.Code, created.InviteToken)).View!.Me);
        var spectator = (await Join(watcher, created.Code.ToLowerInvariant(), null)).View!;
        Assert.Null(spectator.Me);
        Assert.Equal("Ana", spectator.Seats[0].Name);
        Assert.True(spectator.Seats[0].Connected);

        Assert.Equal("forbidden", (await Join(await Connect(server), created.Code, "faux")).Error);
        Assert.Equal("notFound", (await Join(await Connect(server), "ZZZZZZ", null)).Error);
        Assert.Equal("forbidden", (await watcher.InvokeAsync<OnlineResult>("Send", new OnlineCommand(OnlineCommandType.SetReady, Flag: true))).Problems[0].Key);

        // Les autres connexions sont prévenues de chaque changement.
        var changed = new TaskCompletionSource();
        watcher.On<int>("Changed", _ => changed.TrySetResult());
        await Ok(b, new(OnlineCommandType.SetName, "Bo"));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Salon : cabales secrètes jusqu'à ce que les deux soient prêts.
        await Ok(a, new(OnlineCommandType.SetCabal, Cabal: cabalA));
        await Ok(a, new(OnlineCommandType.SetReady, Flag: true));
        Assert.Null((await View(b))!.Seats[0].Cabal);
        await Ok(b, new(OnlineCommandType.SetCabal, Cabal: cabalB));
        await Ok(b, new(OnlineCommandType.SetReady, Flag: true));
        Assert.Equal("A", (await View(b))!.Seats[0].Cabal!.Name);

        await RollUntilFirst(a, b);
        var first = (await View(a))!.First!.Value;
        await Ok(first == 0 ? a : b, new(OnlineCommandType.DrawAgenda));
        Assert.Equal(OnlineStep.Cycle, (await View(a))!.Step);

        // A blesse un de ses rouages : B et le spectateur le voient.
        var mine = (await View(a))!.Mine!;
        var session = new GameSession(mine.State!);
        session.Wound(0, 1, attack: false, ignoreProtection: false);
        await Ok(a, new(OnlineCommandType.UpdateState, State: session.State, BasedOn: mine.StateRevision, Note: "1 blessure"));
        Assert.Equal(1, (await View(b))!.Seats[0].State!.Cogs[0].Marked);
        Assert.Equal(1, (await View(watcher))!.Seats[0].State!.Cogs[0].Marked);
        Assert.Contains((await View(b))!.Log, e => e.Key == "note" && e.Args![0] == "1 blessure");

        // La partie survit à un redémarrage du serveur (fichier JSON).
        var again = Server();
        var reconnected = await Connect(again);
        var view = (await Join(reconnected, created.Code, created.InviteToken)).View!;
        Assert.Equal(OnlineStep.Cycle, view.Step);
        Assert.Equal(1, view.Seats[0].State!.Cogs[0].Marked);
    }

    [Fact]
    public async Task Partie_d_une_table_de_tournoi()
    {
        var server = Server();
        var http = server.CreateClient();
        var (cabalA, cabalB) = await MaiselCabals();
        var t = await Read<TournamentCreated>(await http.PostAsJsonAsync("/api/tournaments",
            new CreateTournamentRequest("Open", null, 3, 2, PairingMode.Swiss, false), TournamentJson.Options));
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

        // Avant le lancement : pas de partie en ligne.
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(Post(open, ana.PlayerToken))).StatusCode);
        await http.SendAsync(Post($"/api/tournaments/{t.Code}/start", organizer: t.OrganizerToken));
        await http.SendAsync(Post($"/api/tournaments/{t.Code}/rounds", organizer: t.OrganizerToken));

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post(open))).StatusCode);
        var opened = await http.SendAsync(Post(open, ana.PlayerToken));
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var code = (await Read<OnlineOpened>(opened)).Code;
        var again = await http.SendAsync(Post(open, bo.PlayerToken));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(code, (await Read<OnlineOpened>(again)).Code);
        var tournament = await Read<TournamentView>(await http.GetAsync($"/api/tournaments/{t.Code}"));
        Assert.Equal(code, tournament.Rounds[0].Tables[0].OnlineCode);

        // Chacun s'identifie avec son jeton de joueur ; ses cabales inscrites sont proposées.
        var a = await Connect(server);
        var b = await Connect(server);
        var viewA = (await Join(a, code, ana.PlayerToken)).View!;
        var viewB = (await Join(b, code, bo.PlayerToken)).View!;
        Assert.NotEqual(viewA.Me, viewB.Me);
        Assert.Equal("Ana", viewA.Mine!.Name);
        Assert.Equal(2, viewA.Mine.AllowedCabals.Count);
        Assert.Equal(new OnlineTournamentRef(t.Code, 1, 1), viewA.Tournament);
        Assert.Equal("forbidden", (await Join(await Connect(server), code, t.OrganizerToken)).Error);

        // Cabales lancées : notées à la table du tournoi (Ana : sa 2ᵉ cabale inscrite, Bo : sa 1ʳᵉ).
        await Ok(a, new(OnlineCommandType.SetCabal, Cabal: cabalB));
        await Ok(b, new(OnlineCommandType.SetCabal, Cabal: cabalA));
        await Ok(a, new(OnlineCommandType.SetReady, Flag: true));
        await Ok(b, new(OnlineCommandType.SetReady, Flag: true));
        var table = (await Read<TournamentView>(await http.GetAsync($"/api/tournaments/{t.Code}"))).Rounds[0].Tables[0];
        Assert.Equal(table.PlayerA == ana.PlayerId ? 1 : 0, table.CabalA);
        Assert.Equal(table.PlayerB == ana.PlayerId ? 1 : 0, table.CabalB);
    }

    [Fact]
    public async Task Creation_limitee_par_adresse()
    {
        var http = Server(createPerHour: 1).CreateClient();
        await Create(http);
        var refused = await http.PostAsJsonAsync("/api/online", new CreateOnlineRequest(), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task Nom_trop_long_refuse()
    {
        var http = Server().CreateClient();
        var refused = await http.PostAsJsonAsync("/api/online", new CreateOnlineRequest(new string('x', 41)), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }
}
