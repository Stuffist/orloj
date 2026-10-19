using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Olomoc.Accounts;
using Olomoc.Models;
using Olomoc.Tournaments;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Cabales du compte (/api/account/cabals/sync) : à nom égal, le plus récent l'emporte.</summary>
public sealed class CabalSyncApiTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "olomoc-cabals-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _server;
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public CabalSyncApiTests() =>
        _server = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DATA_DIR", _data)
            .UseSetting("SESSION_CHECK_SECONDS", "0")
            .UseSetting("RATE_ACCOUNTS_PER_HOUR", "1000")
            .UseSetting("RATE_LOGINS_PER_15_MINUTES", "1000")
            .UseSetting("RATE_WRITES_PER_MINUTE", "10000"));

    public void Dispose()
    {
        _server.Dispose();
        TestDb.Delete(_data);
    }

    private static CabalExport Cabal(string name, string cog = "3R06", int cogs = 1) =>
        new(CabalExport.FormatId, CabalExport.CurrentVersion, "fr", name, new CodeName("MAISEL", "Ligue de Maisel"),
            Enumerable.Range(0, cogs).Select(i => new CabalSlot(3 - i % 3, new IdName(cog, "Rouage"), null)).ToList());

    private static CabalChange Save(string name, DateTimeOffset at, string cog = "3R06") => new(name, at, Cabal(name, cog));
    private static CabalChange Delete(string name, DateTimeOffset at) => new(name, at, null);

    /// <summary>Nouvel appareil connecté au compte (créé s'il n'existe pas).</summary>
    private async Task<HttpClient> Device(string user = "Bohdan")
    {
        var http = _server.CreateClient();
        var login = await http.PostAsJsonAsync("/api/account/login", new LoginRequest(user, "motdepasse"), TournamentJson.Options);
        if (!login.IsSuccessStatusCode)
            (await http.PostAsJsonAsync("/api/account/register", new RegisterRequest(user, $"{user}@example.org", "motdepasse", "fr"), TournamentJson.Options))
                .EnsureSuccessStatusCode();
        return http;
    }

    private static async Task<CabalSyncResult> Sync(HttpClient http, params CabalChange[] changes)
    {
        var response = await http.PostAsJsonAsync("/api/account/cabals/sync", new CabalSyncRequest(changes), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CabalSyncResult>(TournamentJson.Options))!;
    }

    private static string[] Names(CabalSyncResult r) => r.Cabals.Select(c => c.Cabal.Name!).ToArray();

    [Fact]
    public async Task Sans_session_refuse()
    {
        var response = await _server.CreateClient().PostAsJsonAsync("/api/account/cabals/sync", new CabalSyncRequest([]), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sauvegardee_sur_un_appareil_retrouvee_sur_l_autre()
    {
        var phone = await Device();
        var saved = await Sync(phone, Save("Ma cabale", T0), Save("Autre", T0.AddMinutes(1)));
        Assert.Equal(2, saved.Applied);
        Assert.Equal(["Autre", "Ma cabale"], Names(saved));                 // la plus récente d'abord

        var computer = await Device();
        var read = await Sync(computer);
        Assert.Equal(0, read.Applied);
        Assert.Equal(["Autre", "Ma cabale"], Names(read));
        Assert.Equal(T0, read.Cabals[1].SavedAt);
        Assert.False(read.Cabals[0].Pending);

        // Un autre compte ne voit rien.
        Assert.Empty((await Sync(await Device("Autre"))).Cabals);
    }

    [Fact]
    public async Task A_nom_egal_le_plus_recent_l_emporte()
    {
        var phone = await Device();
        var computer = await Device();
        await Sync(phone, Save("Ma cabale", T0.AddHours(2), "3R06"));

        // Plus ancienne (même nom, autre casse) : ignorée.
        var older = await Sync(computer, Save("MA CABALE ", T0.AddHours(1), "3R05"));
        Assert.Equal(0, older.Applied);
        Assert.Equal("3R06", older.Cabals.Single().Cabal.Cogs[0].Cog.Id);

        var newer = await Sync(computer, Save("ma cabale", T0.AddHours(3), "3R05"));
        Assert.Equal(1, newer.Applied);
        Assert.Equal(("ma cabale", "3R05"), (newer.Cabals.Single().Cabal.Name, newer.Cabals.Single().Cabal.Cogs[0].Cog.Id));
    }

    [Fact]
    public async Task Suppression_propagee_et_pas_de_retour_d_une_version_plus_ancienne()
    {
        var phone = await Device();
        var computer = await Device();
        await Sync(phone, Save("Ma cabale", T0));
        Assert.Empty((await Sync(computer, Delete("Ma cabale", T0.AddHours(1)))).Cabals);
        Assert.Empty((await Sync(phone)).Cabals);

        // L'appareil hors ligne renvoie sa vieille version : la suppression, plus récente, tient.
        Assert.Empty((await Sync(phone, Save("Ma cabale", T0.AddMinutes(30)))).Cabals);
        // Une sauvegarde plus récente la fait revenir.
        Assert.Single((await Sync(phone, Save("Ma cabale", T0.AddHours(2)))).Cabals);
        // Suppression d'une cabale que le compte n'a jamais eue : retenue aussi.
        await Sync(phone, Delete("Fantôme", T0.AddHours(5)));
        Assert.Equal(["Ma cabale"], Names(await Sync(computer, Save("Fantôme", T0.AddHours(4)))));
    }

    [Fact]
    public async Task Cabales_invalides_et_trop_de_changements_refuses()
    {
        var phone = await Device();
        var result = await Sync(phone, new CabalChange("Quatre", T0, Cabal("Quatre", cogs: 4)), new CabalChange("  ", T0, Cabal(" ")), Save("Bonne", T0));
        Assert.Equal(1, result.Applied);
        Assert.Equal(["cabalInvalid", "cabalInvalid"], result.Problems.Select(p => p.Key));
        Assert.Equal(["Bonne"], Names(result));

        var tooMany = Enumerable.Range(0, CabalSyncRules.MaxChanges + 1).Select(i => Save($"C{i}", T0)).ToArray();
        var response = await phone.PostAsJsonAsync("/api/account/cabals/sync", new CabalSyncRequest(tooMany), TournamentJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deux_cents_cabales_au_plus()
    {
        var phone = await Device();
        for (var i = 0; i < CabalSyncRules.MaxCabals; i += CabalSyncRules.MaxChanges)
            await Sync(phone, Enumerable.Range(i, CabalSyncRules.MaxChanges).Select(n => Save($"C{n}", T0)).ToArray());

        var refused = await Sync(phone, Save("De trop", T0));
        Assert.Equal(("cabalLimit", "De trop"), (refused.Problems.Single().Key, refused.Problems.Single().Args![1]));
        Assert.Equal(CabalSyncRules.MaxCabals, refused.Cabals.Count);

        // Remplacer une cabale existante reste possible ; en supprimer une libère une place.
        Assert.Equal(1, (await Sync(phone, Save("C0", T0.AddHours(1), "3R05"))).Applied);
        await Sync(phone, Delete("C1", T0.AddHours(1)));
        Assert.Equal(1, (await Sync(phone, Save("De trop", T0.AddHours(1)))).Applied);
    }

    [Fact]
    public async Task Compte_supprime_ses_cabales_aussi()
    {
        var phone = await Device();
        await Sync(phone, Save("Ma cabale", T0));
        (await phone.PostAsJsonAsync("/api/account/delete", new DeleteAccountRequest("motdepasse"), TournamentJson.Options)).EnsureSuccessStatusCode();
        Assert.Empty((await Sync(await Device())).Cabals);                   // même pseudo, nouveau compte
        using var db = (await TestDb.OpenAsync(_data)).CreateDbContext();
        Assert.Empty(db.SavedCabals);
    }
}
