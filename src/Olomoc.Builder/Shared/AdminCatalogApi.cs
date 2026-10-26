using System.Net;
using System.Net.Http.Json;
using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Builder.Shared;

/// <summary>
/// Card catalog editor (editors and admins): complete catalog (drafts included, as served to an
/// admin), texts of each language, state of the changes, and saving (/api/admin/catalog).
/// Independent of CatalogService: read again after each save.
/// </summary>
public sealed class AdminCatalogApi(HttpClient http)
{
    private const string Root = "/api/admin/catalog";
    public const string ExportUrl = Root + "/export";

    public Catalog Catalog { get; private set; } = new();
    public FactionReference Factions { get; private set; } = new();
    public Dictionary<string, CardTexts> Texts { get; private set; } = new();
    public CatalogStatus Status { get; private set; } = new(new(), []);

    /// <summary>Reads everything again; problems: not an admin (forbidden, signedOut), network…</summary>
    public async Task<IReadOnlyList<Problem>> LoadAsync()
    {
        var status = await SendAsync<CatalogStatus>(HttpMethod.Get, "status", null);
        if (!status.Ok) return status.Problems;
        try
        {
            Status = status.Value!;
            Catalog = await http.GetFromJsonAsync<Catalog>(Assets.Base + "data/olomoc-catalog.json", TournamentJson.Options) ?? new();
            Factions = await http.GetFromJsonAsync<FactionReference>(Assets.Base + "data/olomoc-factions.json", TournamentJson.Options) ?? new();
            var texts = new Dictionary<string, CardTexts>();
            foreach (var language in Status.Languages)
                texts[language] = await http.GetFromJsonAsync<CardTexts>($"{Assets.Base}i18n/{language}/cards.json", TournamentJson.Options) ?? new();
            Texts = texts;
            return [];
        }
        catch (HttpRequestException) { return [Problem.Of("network")]; }
    }

    /// <summary>"new", "modified" or null for an entry of a collection (cogs, equipment, factions).</summary>
    public string? StatusOf(string collection, string key) => Status.Entries.GetValueOrDefault(collection)?.GetValueOrDefault(key);

    public Task<AccountResult<bool>> SaveCogAsync(CogEdit edit) => SendAsync<bool>(HttpMethod.Put, $"cogs/{Uri.EscapeDataString(edit.Cog.Id)}", edit);

    public Task<AccountResult<bool>> SaveEquipmentAsync(EquipmentEdit edit) =>
        SendAsync<bool>(HttpMethod.Put, $"equipment/{Uri.EscapeDataString(edit.Equipment.Id)}", edit);

    public Task<AccountResult<bool>> SaveFactionAsync(string code, FactionEdit edit) => SendAsync<bool>(HttpMethod.Put, $"factions/{code}", edit);

    public Task<AccountResult<bool>> SetPublishedAsync(string collection, string id, bool published) =>
        SendAsync<bool>(HttpMethod.Put, $"{collection}/{Uri.EscapeDataString(id)}/published", new PublishedRequest(published));

    /// <summary>Cancels the changes of an entry (repository version); a new entry disappears.</summary>
    public Task<AccountResult<bool>> RevertAsync(string collection, string id) =>
        SendAsync<bool>(HttpMethod.Delete, $"{collection}/{Uri.EscapeDataString(id)}", null);

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
