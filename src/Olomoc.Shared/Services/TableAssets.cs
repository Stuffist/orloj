using System.Net.Http.Json;
using System.Text.Json;
using Olomoc.Online;

namespace Olomoc.Services;

/// <summary>
/// What can be put on the table of an online game (<see cref="TableManifest"/>, data/olomoc-table.json):
/// loaded once, in the browser and on the server (same HttpClient as the card catalog).
/// </summary>
public sealed class TableAssets(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public TableManifest Manifest { get; private set; } = new(TableManifest.CurrentVersion, "", [], [], [], [], []);
    public bool Loaded { get; private set; }

    private Task? _loading;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        Manifest = await http.GetFromJsonAsync<TableManifest>(Assets.Base + TableManifest.File, Json) ?? Manifest;
        Loaded = true;
    }
}
