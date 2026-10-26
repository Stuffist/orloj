using System.Net;
using System.Net.Http.Json;
using Olomoc.Accounts;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Builder.Shared;

/// <summary>
/// Panneau d'administration (admins) : /api/admin (vue d'ensemble, tournois, parties en ligne,
/// maintenance, journal) et /api/admin/accounts (comptes). Problèmes : clés « account.problem. ».
/// </summary>
public sealed class AdminApi(HttpClient http)
{
    private const string Root = "/api/admin";

    public Task<AccountResult<AdminOverview>> OverviewAsync() => SendAsync<AdminOverview>(HttpMethod.Get, "overview", null);

    // ---- Comptes ----

    /// <summary>Pseudo contenant la recherche ; sans recherche, les éditeurs et admins, ou les derniers créés.</summary>
    public Task<AccountResult<List<AdminAccount>>> AccountsAsync(string search, bool recent = false) =>
        SendAsync<List<AdminAccount>>(HttpMethod.Get, $"accounts?search={Uri.EscapeDataString(search.Trim())}&recent={(recent ? "true" : "false")}", null);

    public Task<AccountResult<AccountSummary>> SetRoleAsync(string userName, AccountRole role) =>
        SendAsync<AccountSummary>(HttpMethod.Put, $"accounts/{Name(userName)}/role", new RoleRequest(role));

    public Task<AccountResult<TemporaryPassword>> ResetPasswordAsync(string userName) =>
        SendAsync<TemporaryPassword>(HttpMethod.Post, $"accounts/{Name(userName)}/reset-password", null);

    public Task<AccountResult<bool>> UnlockAsync(string userName) => SendAsync<bool>(HttpMethod.Post, $"accounts/{Name(userName)}/unlock", null);

    public Task<AccountResult<bool>> LogoutAsync(string userName) => SendAsync<bool>(HttpMethod.Post, $"accounts/{Name(userName)}/logout", null);

    public Task<AccountResult<bool>> DeleteAccountAsync(string userName) => SendAsync<bool>(HttpMethod.Delete, $"accounts/{Name(userName)}", null);

    // ---- Tournois ----

    public Task<AccountResult<List<AdminTournament>>> TournamentsAsync() => SendAsync<List<AdminTournament>>(HttpMethod.Get, "tournaments", null);

    /// <summary>Nouveau jeton organisateur (l'ancien lien d'administration cesse de marcher).</summary>
    public Task<AccountResult<TournamentCreated>> NewOrganizerLinkAsync(string code) =>
        SendAsync<TournamentCreated>(HttpMethod.Post, $"tournaments/{code}/organizer-link", null);

    public Task<AccountResult<bool>> SetExcludeFromStatsAsync(string code, bool exclude) =>
        SendAsync<bool>(HttpMethod.Put, $"tournaments/{code}/stats", new StatsRequest(exclude));

    public Task<AccountResult<bool>> DeleteTournamentAsync(string code) => SendAsync<bool>(HttpMethod.Delete, $"tournaments/{code}", null);

    // ---- Parties en ligne ----

    public Task<AccountResult<List<AdminOnlineGame>>> OnlineGamesAsync() => SendAsync<List<AdminOnlineGame>>(HttpMethod.Get, "online", null);

    public Task<AccountResult<bool>> DeleteOnlineGameAsync(string code) => SendAsync<bool>(HttpMethod.Delete, $"online/{code}", null);

    // ---- Maintenance et journal ----

    public Task<AccountResult<BackupResult>> BackupAsync() => SendAsync<BackupResult>(HttpMethod.Post, "maintenance/backup", null);

    public Task<AccountResult<PurgeResult>> PurgeAsync(PurgeKind kind) =>
        SendAsync<PurgeResult>(HttpMethod.Post, "maintenance/purge", new PurgeRequest(kind));

    public Task<AccountResult<List<AdminAction>>> AuditAsync() => SendAsync<List<AdminAction>>(HttpMethod.Get, "audit", null);

    private static string Name(string userName) => Uri.EscapeDataString(userName.Trim());

    private async Task<AccountResult<T>> SendAsync<T>(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, $"{Root}/{path}");
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: TournamentJson.Options);
        try
        {
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NoContent) return new(typeof(T) == typeof(bool) ? (T)(object)true : default, []);
            if (response.IsSuccessStatusCode) return new(await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options), []);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(default, [Problem.Of("signedOut")]);
            try
            {
                if ((await response.Content.ReadFromJsonAsync<ProblemList>(TournamentJson.Options))?.Problems is { Count: > 0 } problems)
                    return new(default, problems);
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException) { }
            return new(default, [response.StatusCode == HttpStatusCode.TooManyRequests ? Problem.Of("tooManyRequests") : Problem.Of("server", ((int)response.StatusCode).ToString())]);
        }
        catch (HttpRequestException) { return new(default, [Problem.Of("network")]); }
    }
}
