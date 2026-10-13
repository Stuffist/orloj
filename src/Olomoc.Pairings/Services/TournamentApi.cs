using System.Net;
using System.Net.Http.Json;
using Olomoc.Tournaments;

namespace Olomoc.Pairings.Services;

/// <summary>Réponse de l'API : la valeur, ou les problèmes qui l'ont empêchée.</summary>
public sealed record ApiResult<T>(T? Value, IReadOnlyList<Problem> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Appels à l'API des tournois (/api/tournaments, même origine). Les jetons (organisateur,
/// joueur) passent dans les en-têtes. Serveur injoignable ou trop sollicité : problème
/// « network » ou « tooManyRequests », affiché comme les autres.
/// </summary>
public sealed class TournamentApi(HttpClient http)
{
    private const string Root = "/api/tournaments";

    public Task<ApiResult<TournamentCreated>> CreateAsync(CreateTournamentRequest request) =>
        SendAsync<TournamentCreated>(HttpMethod.Post, Root, request);

    public Task<ApiResult<TournamentView>> GetAsync(string code, string? organizer, string? player) =>
        SendAsync<TournamentView>(HttpMethod.Get, $"{Root}/{Uri.EscapeDataString(code)}", null, organizer, player);

    public Task<ApiResult<PlayerRegistered>> RegisterAsync(string code, RegistrationRequest request) =>
        SendAsync<PlayerRegistered>(HttpMethod.Post, $"{Root}/{code}/players", request);

    public Task<ApiResult<bool>> UpdateRegistrationAsync(string code, string playerId, RegistrationRequest request, string? organizer, string? player) =>
        SendAsync<bool>(HttpMethod.Put, $"{Root}/{code}/players/{playerId}", request, organizer, player);

    public Task<ApiResult<bool>> UnregisterAsync(string code, string playerId, string? organizer, string? player) =>
        SendAsync<bool>(HttpMethod.Delete, $"{Root}/{code}/players/{playerId}", null, organizer, player);

    public Task<ApiResult<bool>> SetDroppedAsync(string code, string playerId, bool dropped, string organizer) =>
        SendAsync<bool>(HttpMethod.Put, $"{Root}/{code}/players/{playerId}/dropped", new DroppedRequest(dropped), organizer);

    public Task<ApiResult<bool>> StartAsync(string code, string organizer) =>
        SendAsync<bool>(HttpMethod.Post, $"{Root}/{code}/start", null, organizer);

    public Task<ApiResult<PairingSummary>> PairAsync(string code, string organizer) =>
        SendAsync<PairingSummary>(HttpMethod.Post, $"{Root}/{code}/rounds", null, organizer);

    public Task<ApiResult<bool>> UndoRoundAsync(string code, string organizer) =>
        SendAsync<bool>(HttpMethod.Delete, $"{Root}/{code}/rounds/last", null, organizer);

    public Task<ApiResult<bool>> SetResultAsync(string code, int round, int table, int? vpA, int? vpB, string organizer) =>
        SendAsync<bool>(HttpMethod.Put, $"{Root}/{code}/rounds/{round}/tables/{table}", new ResultRequest(vpA, vpB), organizer);

    public Task<ApiResult<bool>> FinishAsync(string code, string organizer) =>
        SendAsync<bool>(HttpMethod.Post, $"{Root}/{code}/finish", null, organizer);

    /// <summary>Mot de passe organisateur : renvoie un jeton organisateur pour cet appareil.</summary>
    public Task<ApiResult<OrganizerLogin>> LoginAsync(string code, string password) =>
        SendAsync<OrganizerLogin>(HttpMethod.Post, $"{Root}/{Uri.EscapeDataString(code)}/organizer/login", new PasswordRequest(password));

    /// <summary>Définit, change ou retire (null) le mot de passe organisateur.</summary>
    public Task<ApiResult<bool>> SetPasswordAsync(string code, string? password, string organizer) =>
        SendAsync<bool>(HttpMethod.Put, $"{Root}/{code}/organizer/password", new PasswordRequest(password), organizer);

    public Task<ApiResult<bool>> DeleteAsync(string code, string organizer) =>
        SendAsync<bool>(HttpMethod.Delete, $"{Root}/{code}", null, organizer);

    public Task<ApiResult<string>> ExportAsync(string code, string organizer) =>
        SendAsync<string>(HttpMethod.Get, $"{Root}/{code}/export", null, organizer);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, string? organizer = null, string? player = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: TournamentJson.Options);
        if (!string.IsNullOrEmpty(organizer)) request.Headers.Add(TournamentHeaders.Organizer, organizer);
        if (!string.IsNullOrEmpty(player)) request.Headers.Add(TournamentHeaders.Player, player);
        try
        {
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                object? value = typeof(T) == typeof(bool) ? true
                    : typeof(T) == typeof(string) ? await response.Content.ReadAsStringAsync()
                    : await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options);
                return new((T?)value, Array.Empty<Problem>());
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return Failure<T>("tooManyRequests");
            var problems = await ReadProblemsAsync(response);
            return new(default, problems.Count > 0 ? problems : new[] { Problem.Of("server", ((int)response.StatusCode).ToString()) });
        }
        catch (HttpRequestException) { return Failure<T>("network"); }
    }

    private static async Task<IReadOnlyList<Problem>> ReadProblemsAsync(HttpResponseMessage response)
    {
        try { return (await response.Content.ReadFromJsonAsync<ProblemList>(TournamentJson.Options))?.Problems ?? Array.Empty<Problem>(); }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException) { return Array.Empty<Problem>(); }
    }

    private static ApiResult<T> Failure<T>(string key) => new(default, new[] { Problem.Of(key) });
}
