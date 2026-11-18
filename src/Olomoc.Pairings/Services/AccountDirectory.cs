using System.Net.Http.Json;
using Olomoc.Accounts;
using Olomoc.Models;
using Olomoc.Tournaments;

namespace Olomoc.Pairings.Services;

/// <summary>
/// Entry of a game in the Block: account names suggested as a player's name is typed
/// (/api/accounts/search), and the public cabals of the account chosen (/api/accounts/{name}/cabals,
/// kept for the session). Without network: nothing suggested, the entry goes on as before.
/// </summary>
public sealed class AccountDirectory(HttpClient http)
{
    private readonly Dictionary<string, Task<IReadOnlyList<CabalExport>>> _cabals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Account names starting with <paramref name="prefix"/> (from two characters).</summary>
    public async Task<IReadOnlyList<string>> SearchAsync(string prefix)
    {
        var text = prefix.Trim();
        if (text.Length < AccountDirectoryRules.MinPrefix) return [];
        try
        {
            return await http.GetFromJsonAsync<List<string>>($"/api/accounts/search?q={Uri.EscapeDataString(text)}", TournamentJson.Options) ?? [];
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or NotSupportedException) { return []; }
    }

    /// <summary>Public cabals of the account, most recent first (none for an unknown name).</summary>
    public Task<IReadOnlyList<CabalExport>> PublicCabalsAsync(string userName)
    {
        if (!_cabals.TryGetValue(userName, out var task)) _cabals[userName] = task = LoadAsync(userName);
        return task;
    }

    private async Task<IReadOnlyList<CabalExport>> LoadAsync(string userName)
    {
        try
        {
            return await http.GetFromJsonAsync<List<CabalExport>>($"/api/accounts/{Uri.EscapeDataString(userName)}/cabals", TournamentJson.Options) ?? [];
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or NotSupportedException)
        {
            _cabals.Remove(userName);                       // tried again next time
            return [];
        }
    }
}
