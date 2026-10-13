using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>API des tournois, serveur en mémoire sur un dossier de données temporaire.</summary>
public sealed class TournamentApiTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _servers = new();

    public void Dispose()
    {
        _servers.ForEach(s => s.Dispose());
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    private HttpClient Server(int createPerHour = 1000)
    {
        var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("RATE_CREATE_PER_HOUR", createPerHour.ToString()));
        _servers.Add(server);
        return server.CreateClient();
    }

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options))!;

    private static HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? organizer = null, string? player = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: TournamentJson.Options);
        if (organizer is not null) request.Headers.Add(TournamentHeaders.Organizer, organizer);
        if (player is not null) request.Headers.Add(TournamentHeaders.Player, player);
        return request;
    }

    private static async Task<(CabalExport A, CabalExport B)> MaiselCabals()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(),
                slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        return (Cabal("A", ("3R06", "Phéromones"), ("2R08", "Carapace"), ("1R01", "Harmonium")),
                Cabal("B", ("3R06", "Dose de Mélisse"), ("2R04", "Phéromones"), ("1R01", "Passe-droit du Census")));
    }

    private static async Task<TournamentCreated> Create(HttpClient http, string name = "Open de Lyon", int cabals = 2)
    {
        var response = await http.PostAsJsonAsync("/api/tournaments",
            new CreateTournamentRequest(name, new DateOnly(2026, 11, 7), 3, cabals, PairingMode.Swiss, true), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Read<TournamentCreated>(response);
    }

    private static async Task<PlayerRegistered> Register(HttpClient http, string code, string name, CabalExport a, CabalExport b)
    {
        var response = await http.PostAsJsonAsync($"/api/tournaments/{code}/players",
            new RegistrationRequest(name, "MAISEL", new List<CabalExport> { a, b }), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Read<PlayerRegistered>(response);
    }

    private static async Task<TournamentView> View(HttpClient http, string code, string? organizer = null, string? player = null) =>
        await Read<TournamentView>(await http.SendAsync(Request(HttpMethod.Get, $"/api/tournaments/{code}", organizer: organizer, player: player)));

    [Fact]
    public async Task Tournoi_de_bout_en_bout()
    {
        var http = Server();
        var (a, b) = await MaiselCabals();
        var t = await Create(http);
        Assert.Equal(6, t.Code.Length);
        var url = $"/api/tournaments/{t.Code}";

        // Inscriptions : 3 joueurs ; une liste illégale est refusée avec ses raisons.
        var ana = await Register(http, t.Code, "Ana", a, b);
        await Register(http, t.Code, "Bo", a, b);
        await Register(http, t.Code, "Cy", a, b);
        var refused = await http.PostAsJsonAsync($"{url}/players",
            new RegistrationRequest("Dan", "MAISEL", new List<CabalExport> { a, b with { Cogs = b.Cogs.Take(2).ToList() } }), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problems = (await Read<ProblemList>(refused)).Problems;
        Assert.All(problems, p => Assert.Equal(("cabalIllegal", 1), (p.Key, p.Cabal)));
        Assert.Contains(problems, p => p.Legality?.Key == "composition");

        // Listes masquées au public tant que les inscriptions sont ouvertes ; visibles du joueur et de l'orga.
        var anonymous = await View(http, t.Code);
        Assert.Equal(3, anonymous.Players.Count);
        Assert.All(anonymous.Players, p => Assert.Null(p.Cabals));
        Assert.False(anonymous.IsOrganizer);
        var mine = await View(http, t.Code, player: ana.PlayerToken);
        Assert.Equal(ana.PlayerId, mine.MyPlayerId);
        Assert.NotNull(mine.Player(ana.PlayerId)!.Cabals);
        Assert.Null(mine.Players.First(p => p.Id != ana.PlayerId).Cabals);
        Assert.All((await View(http, t.Code, organizer: t.OrganizerToken)).Players, p => Assert.Equal(2, p.Cabals!.Count));

        // Actions d'organisateur : jeton obligatoire.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync($"{url}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Post, $"{url}/start", player: ana.PlayerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Post, $"{url}/start", organizer: t.OrganizerToken))).StatusCode);
        Assert.All((await View(http, t.Code)).Players, p => Assert.NotNull(p.Cabals));

        // Inscriptions closes.
        var late = await http.PostAsJsonAsync($"{url}/players", new RegistrationRequest("Eve", "MAISEL", new List<CabalExport> { a, b }), TournamentJson.Options);
        Assert.Equal("registrationClosed", Assert.Single((await Read<ProblemList>(late)).Problems).Key);

        // Ronde 1 : 3 joueurs, un exempt.
        var paired = await http.SendAsync(Request(HttpMethod.Post, $"{url}/rounds", organizer: t.OrganizerToken));
        Assert.Equal(HttpStatusCode.Created, paired.StatusCode);
        Assert.Equal(1, (await Read<PairingSummary>(paired)).Round);
        var round = Assert.Single((await View(http, t.Code)).Rounds);
        var game = Assert.Single(round.Tables, x => !x.IsBye);
        Assert.Single(round.Tables, x => x.IsBye);

        // Ronde 2 refusée tant que la 1 n'est pas complète.
        var tooSoon = await http.SendAsync(Request(HttpMethod.Post, $"{url}/rounds", organizer: t.OrganizerToken));
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
        Assert.Equal("roundIncomplete", Assert.Single((await Read<ProblemList>(tooSoon)).Problems).Key);

        var badScore = await http.SendAsync(Request(HttpMethod.Put, $"{url}/rounds/1/tables/{game.Number}", new ResultRequest(5, null), t.OrganizerToken));
        Assert.Equal(HttpStatusCode.BadRequest, badScore.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await http.SendAsync(Request(HttpMethod.Put, $"{url}/rounds/1/tables/{game.Number}", new ResultRequest(12, 5), t.OrganizerToken))).StatusCode);

        var view = await View(http, t.Code);
        var leader = view.Standings[0];
        Assert.Equal((3, 7), (leader.Points, leader.Diff));
        Assert.Equal(game.PlayerA, leader.PlayerId);

        // Abandon, puis ronde 2 entre les deux restants.
        Assert.Equal(HttpStatusCode.NoContent,
            (await http.SendAsync(Request(HttpMethod.Put, $"{url}/players/{game.PlayerB}/dropped", new DroppedRequest(true), t.OrganizerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await http.SendAsync(Request(HttpMethod.Post, $"{url}/rounds", organizer: t.OrganizerToken))).StatusCode);
        var round2 = (await View(http, t.Code)).Rounds[1];
        Assert.DoesNotContain(round2.Tables, x => x.Involves(game.PlayerB!));

        // Annuler la ronde 2 (sans résultat), la refaire, la jouer, terminer.
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Delete, $"{url}/rounds/last", organizer: t.OrganizerToken))).StatusCode);
        Assert.Single((await View(http, t.Code)).Rounds);
        await http.SendAsync(Request(HttpMethod.Post, $"{url}/rounds", organizer: t.OrganizerToken));
        var table2 = (await View(http, t.Code)).Rounds[1].Tables.Single();
        await http.SendAsync(Request(HttpMethod.Put, $"{url}/rounds/2/tables/{table2.Number}", new ResultRequest(8, 8), t.OrganizerToken));
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Post, $"{url}/finish", organizer: t.OrganizerToken))).StatusCode);
        Assert.Equal(TournamentPhase.Finished, (await View(http, t.Code)).Phase);

        // Export : tournoi complet sans les secrets.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"{url}/export")).StatusCode);
        var export = await (await http.SendAsync(Request(HttpMethod.Get, $"{url}/export", organizer: t.OrganizerToken))).Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Open de Lyon\"", export);
        Assert.DoesNotContain(Olomoc.Server.Tournaments.Tokens.Hash(t.OrganizerToken), export);
        Assert.DoesNotContain(Olomoc.Server.Tournaments.Tokens.Hash(ana.PlayerToken), export);
        Assert.Contains("\"organizerTokenHash\":\"\"", export);
    }

    [Fact]
    public async Task Joueur_modifie_ou_retire_seulement_sa_propre_inscription()
    {
        var http = Server();
        var (a, b) = await MaiselCabals();
        var t = await Create(http);
        var url = $"/api/tournaments/{t.Code}/players";
        var ana = await Register(http, t.Code, "Ana", a, b);
        var bo = await Register(http, t.Code, "Bo", a, b);

        var rename = new RegistrationRequest("Ana Lys", "MAISEL", new List<CabalExport> { b, a });
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Put, $"{url}/{ana.PlayerId}", rename, player: bo.PlayerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Put, $"{url}/{ana.PlayerId}", rename, player: ana.PlayerToken))).StatusCode);
        Assert.Equal("Ana Lys", (await View(http, t.Code)).Player(ana.PlayerId)!.Name);

        var taken = await http.SendAsync(Request(HttpMethod.Put, $"{url}/{bo.PlayerId}", rename with { Name = "ana lys" }, player: bo.PlayerToken));
        Assert.Equal("nameTaken", Assert.Single((await Read<ProblemList>(taken)).Problems).Key);

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Delete, $"{url}/{bo.PlayerId}", player: ana.PlayerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Delete, $"{url}/{bo.PlayerId}", player: bo.PlayerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Delete, $"{url}/{ana.PlayerId}", organizer: t.OrganizerToken))).StatusCode);
        Assert.Empty((await View(http, t.Code)).Players);
    }

    [Fact]
    public async Task Mot_de_passe_ouvre_l_administration_sur_un_autre_appareil()
    {
        var http = Server();
        var created = await http.PostAsJsonAsync("/api/tournaments",
            new CreateTournamentRequest("Avec mot de passe", null, 3, 2, PairingMode.Swiss, false, "secret-du-club"), TournamentJson.Options);
        var t = await Read<TournamentCreated>(created);
        var url = $"/api/tournaments/{t.Code}";
        Assert.True((await View(http, t.Code)).HasPassword);

        // Mauvais mot de passe, puis le bon : nouveau jeton, différent du lien d'administration.
        var wrong = await http.PostAsJsonAsync($"{url}/organizer/login", new PasswordRequest("devine"), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("wrongPassword", Assert.Single((await Read<ProblemList>(wrong)).Problems).Key);
        var phone = await Read<OrganizerLogin>(await http.PostAsJsonAsync($"{url}/organizer/login", new PasswordRequest("secret-du-club"), TournamentJson.Options));
        Assert.NotEqual(t.OrganizerToken, phone.OrganizerToken);
        Assert.True((await View(http, t.Code, organizer: phone.OrganizerToken)).IsOrganizer);
        var tablet = await Read<OrganizerLogin>(await http.PostAsJsonAsync($"{url}/organizer/login", new PasswordRequest("secret-du-club"), TournamentJson.Options));

        // Changement depuis le téléphone : il reste ouvert, la tablette est fermée, le lien reste valable.
        var tooShort = await http.SendAsync(Request(HttpMethod.Put, $"{url}/organizer/password", new PasswordRequest("court"), phone.OrganizerToken));
        Assert.Equal("password", Assert.Single((await Read<ProblemList>(tooShort)).Problems).Key);
        Assert.Equal(HttpStatusCode.NoContent,
            (await http.SendAsync(Request(HttpMethod.Put, $"{url}/organizer/password", new PasswordRequest("nouveau-secret"), phone.OrganizerToken))).StatusCode);
        Assert.True((await View(http, t.Code, organizer: phone.OrganizerToken)).IsOrganizer);
        Assert.False((await View(http, t.Code, organizer: tablet.OrganizerToken)).IsOrganizer);
        Assert.True((await View(http, t.Code, organizer: t.OrganizerToken)).IsOrganizer);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await http.PostAsJsonAsync($"{url}/organizer/login", new PasswordRequest("secret-du-club"), TournamentJson.Options)).StatusCode);

        // Sans jeton organisateur : changement refusé. Retrait : plus de connexion par mot de passe.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PutAsJsonAsync($"{url}/organizer/password", new PasswordRequest("pirate-pirate"), TournamentJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await http.SendAsync(Request(HttpMethod.Put, $"{url}/organizer/password", new PasswordRequest(null), t.OrganizerToken))).StatusCode);
        Assert.False((await View(http, t.Code)).HasPassword);
        var none = await http.PostAsJsonAsync($"{url}/organizer/login", new PasswordRequest("nouveau-secret"), TournamentJson.Options);
        Assert.Equal("noPassword", Assert.Single((await Read<ProblemList>(none)).Problems).Key);

        // Export : ni hash de mot de passe, ni sessions.
        var export = await (await http.SendAsync(Request(HttpMethod.Get, $"{url}/export", organizer: t.OrganizerToken))).Content.ReadAsStringAsync();
        Assert.Contains("\"organizerPasswordHash\":\"\"", export);
        Assert.Contains("\"organizerSessionHashes\":[]", export);
    }

    [Fact]
    public async Task Mot_de_passe_trop_court_refuse_a_la_creation()
    {
        var response = await Server().PostAsJsonAsync("/api/tournaments",
            new CreateTournamentRequest("Open", null, 3, 2, PairingMode.Swiss, false, "1234"), TournamentJson.Options);
        Assert.Equal("password", Assert.Single((await Read<ProblemList>(response)).Problems).Key);
    }

    [Fact]
    public async Task Essais_de_mot_de_passe_limites()
    {
        var http = Server();
        var t = await Read<TournamentCreated>(await http.PostAsJsonAsync("/api/tournaments",
            new CreateTournamentRequest("Open", null, 3, 2, PairingMode.Swiss, false, "secret-du-club"), TournamentJson.Options));
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
            statuses.Add((await http.PostAsJsonAsync($"/api/tournaments/{t.Code}/organizer/login", new PasswordRequest("devine" + i), TournamentJson.Options)).StatusCode);
        Assert.Equal(Enumerable.Repeat(HttpStatusCode.Forbidden, 10).Append(HttpStatusCode.TooManyRequests), statuses);
    }

    [Fact]
    public async Task Suppression_par_l_organisateur_seulement()
    {
        var http = Server();
        var (a, b) = await MaiselCabals();
        var t = await Create(http);
        var ana = await Register(http, t.Code, "Ana", a, b);
        var url = $"/api/tournaments/{t.Code}";

        Assert.Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Delete, url, player: ana.PlayerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Delete, url, organizer: t.OrganizerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Request(HttpMethod.Delete, url, organizer: t.OrganizerToken))).StatusCode);
    }

    [Fact]
    public async Task Tournoi_conserve_apres_redemarrage()
    {
        var (a, b) = await MaiselCabals();
        var first = Server();
        var t = await Create(first, "Persistant");
        await Register(first, t.Code, "Ana", a, b);

        var second = Server();
        var view = await View(second, t.Code.ToLowerInvariant());      // code insensible à la casse
        Assert.Equal("Persistant", view.Name);
        Assert.Equal("Ana", Assert.Single(view.Players).Name);
        Assert.Equal(new DateOnly(2026, 11, 7), view.Date);
    }

    [Theory]
    [InlineData("/api/tournaments/ABCDEF")]       // code bien formé, inconnu
    [InlineData("/api/tournaments/../x")]
    [InlineData("/api/tournaments/O0O0O0")]       // caractères exclus des codes
    public async Task Tournoi_inconnu(string url) =>
        Assert.Equal(HttpStatusCode.NotFound, (await Server().GetAsync(url)).StatusCode);

    [Fact]
    public async Task Reglages_invalides_refuses()
    {
        var response = await Server().PostAsJsonAsync("/api/tournaments",
            new CreateTournamentRequest(" ", null, 0, 2, PairingMode.Alignment, false), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(new[] { "tournamentName", "roundCount" }, (await Read<ProblemList>(response)).Problems.Select(p => p.Key));
    }

    [Fact]
    public async Task Creations_limitees_par_adresse()
    {
        var http = Server(createPerHour: 2);
        await Create(http);
        await Create(http);
        var third = await http.PostAsJsonAsync("/api/tournaments",
            new CreateTournamentRequest("Trop", null, 3, 2, PairingMode.Swiss, false), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }
}
