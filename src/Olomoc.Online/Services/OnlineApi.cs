using System.Net;
using System.Net.Http.Json;
using Olomoc.Models;
using Olomoc.Tournaments;

namespace Olomoc.Online.Services;

/// <summary>API response: the value, or the problems that prevented it.</summary>
public sealed record ApiResult<T>(T? Value, IReadOnlyList<Problem> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>Creation of an online game (/api/online, same origin); everything else goes through the hub.</summary>
public sealed class OnlineApi(HttpClient http)
{
    public async Task<ApiResult<OnlineCreated>> CreateAsync(string? name, CabalExport? cabal = null)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("/api/online", new CreateOnlineRequest(name, cabal), TournamentJson.Options);
            if (response.IsSuccessStatusCode)
                return new(await response.Content.ReadFromJsonAsync<OnlineCreated>(TournamentJson.Options), Array.Empty<Problem>());
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return Failure("tooManyRequests");
            var problems = await ReadProblemsAsync(response);
            return new(null, problems.Count > 0 ? problems : new[] { Problem.Of("server", ((int)response.StatusCode).ToString()) });
        }
        catch (HttpRequestException) { return Failure("network"); }
    }

    private static async Task<IReadOnlyList<Problem>> ReadProblemsAsync(HttpResponseMessage response)
    {
        try { return (await response.Content.ReadFromJsonAsync<ProblemList>(TournamentJson.Options))?.Problems ?? Array.Empty<Problem>(); }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException) { return Array.Empty<Problem>(); }
    }

    private static ApiResult<OnlineCreated> Failure(string key) => new(null, new[] { Problem.Of(key) });
}
