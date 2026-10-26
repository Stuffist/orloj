using System.Net;
using System.Net.Http.Json;
using Olomoc.Tournaments;
using Olomoc.Tournaments.Stats;
using Olomoc.Zones;

namespace Olomoc.Pairings.Services;

/// <summary>
/// Calls to the zones API (/api/zones, same origin; session cookie for the managers and admins).
/// The entry token of a game entered from this device goes in a header. Server unreachable or
/// overloaded: "network" or "tooManyRequests" problem.
/// </summary>
public sealed class ZoneApi(HttpClient http)
{
    private const string Root = "/api/zones", Admin = "/api/admin/zones";

    public Task<ApiResult<List<ZoneSummary>>> ListAsync() => SendAsync<List<ZoneSummary>>(HttpMethod.Get, Root, null);

    public Task<ApiResult<StatsView>> AllStatsAsync() => SendAsync<StatsView>(HttpMethod.Get, $"{Root}/stats", null);

    public Task<ApiResult<ZoneView>> GetAsync(string code) => SendAsync<ZoneView>(HttpMethod.Get, $"{Root}/{Esc(code)}", null);

    public Task<ApiResult<ZoneGamesPage>> GamesAsync(string code, long? before) =>
        SendAsync<ZoneGamesPage>(HttpMethod.Get, $"{Root}/{Esc(code)}/games" + (before is { } b ? $"?before={b}" : ""), null);

    public Task<ApiResult<ZoneGameCreated>> AddGameAsync(string code, ZoneGame game) =>
        SendAsync<ZoneGameCreated>(HttpMethod.Post, $"{Root}/{Esc(code)}/games", game);

    public Task<ApiResult<bool>> UpdateGameAsync(string code, long id, ZoneGame game, string? token) =>
        SendAsync<bool>(HttpMethod.Put, $"{Root}/{Esc(code)}/games/{id}", game, token);

    public Task<ApiResult<bool>> DeleteGameAsync(string code, long id, string? token) =>
        SendAsync<bool>(HttpMethod.Delete, $"{Root}/{Esc(code)}/games/{id}", null, token);

    /// <summary>Settings (managers and admins).</summary>
    public Task<ApiResult<bool>> UpdateSettingsAsync(string code, ZoneSettingsRequest request) =>
        SendAsync<bool>(HttpMethod.Put, $"{Root}/{Esc(code)}", request);

    public Task<ApiResult<ZoneCreated>> CreateAsync(ZoneAdminRequest request) =>
        SendAsync<ZoneCreated>(HttpMethod.Post, Admin, request);

    public Task<ApiResult<bool>> AdminUpdateAsync(string code, ZoneAdminRequest request) =>
        SendAsync<bool>(HttpMethod.Put, $"{Admin}/{Esc(code)}", request);

    public Task<ApiResult<bool>> DeleteAsync(string code) => SendAsync<bool>(HttpMethod.Delete, $"{Admin}/{Esc(code)}", null);

    private static string Esc(string code) => Uri.EscapeDataString(code);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, string? entryToken = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: TournamentJson.Options);
        if (!string.IsNullOrEmpty(entryToken)) request.Headers.Add(ZoneHeaders.Entry, entryToken);
        try
        {
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                object? value = typeof(T) == typeof(bool) ? true : await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options);
                return new((T?)value, Array.Empty<Problem>());
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return Failure<T>("tooManyRequests");
            if (response.StatusCode == HttpStatusCode.Unauthorized) return Failure<T>("signedOut");
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
