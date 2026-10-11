using System.Text.Json;
using Microsoft.JSInterop;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Cabales sauvegardées sur l'appareil (localStorage), au format de l'export JSON
/// (<see cref="CabalExport"/>). Le nom identifie une cabale : sauvegarder sous un nom déjà
/// pris remplace la cabale existante.
/// </summary>
public sealed class CabalLibrary
{
    private const string StorageKey = "olomoc.cabals";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IJSRuntime _js;
    private Task? _loading;
    private List<SavedCabal> _cabals = new();

    public CabalLibrary(IJSRuntime js) => _js = js;

    /// <summary>Cabales sauvegardées, la plus récente d'abord.</summary>
    public IReadOnlyList<SavedCabal> Cabals => _cabals;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json)) return;
            _cabals = (JsonSerializer.Deserialize<List<SavedCabal>>(json, JsonOpts) ?? new())
                .Where(c => c.Cabal is { Name: not null, Cogs: not null })
                .OrderByDescending(c => c.SavedAt)
                .ToList();
        }
        catch (JSException) { /* stockage indisponible : aucune sauvegarde */ }
        catch (JsonException) { /* contenu illisible : ignoré, écrasé à la prochaine sauvegarde */ }
    }

    /// <summary>Cabale sauvegardée sous ce nom (sans tenir compte de la casse ni des espaces autour).</summary>
    public SavedCabal? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _cabals.FirstOrDefault(c => SameName(c.Cabal.Name, name));

    /// <summary>Sauvegarde la cabale, qui doit être nommée ; remplace celle du même nom.</summary>
    public async Task SaveAsync(CabalExport cabal, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(cabal.Name)) throw new ArgumentException("Cabale sans nom.", nameof(cabal));
        _cabals.RemoveAll(c => SameName(c.Cabal.Name, cabal.Name));
        _cabals.Insert(0, new SavedCabal(now, cabal));
        await StoreAsync();
    }

    public async Task DeleteAsync(SavedCabal saved)
    {
        if (_cabals.Remove(saved)) await StoreAsync();
    }

    /// <summary>Même composition (faction, rouages et équipements/dons par rang), noms mis à part.</summary>
    public static bool SameCards(CabalExport a, CabalExport b) => Cards(a) == Cards(b);

    private static string Cards(CabalExport c) =>
        c.Faction?.Code + "|" + string.Join("|", c.Cogs.OrderBy(s => s.Rank).Select(s => $"{s.Rank}:{s.Cog.Id}:{s.Equipment?.Id}"));

    private static bool SameName(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private async Task StoreAsync()
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(_cabals, JsonOpts)); }
        catch (JSException) { /* navigation privée… : la sauvegarde vit le temps de l'onglet */ }
    }
}
