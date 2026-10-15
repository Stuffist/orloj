using Olomoc.Models;

namespace Olomoc.Services;

public enum EquipmentSort { Ministry, Name }

/// <summary>Groupe de la liste : code de Ministère, <see cref="EquipmentBrowser.Factions"/> ou <see cref="EquipmentBrowser.CogAffiliated"/>.</summary>
public sealed record EquipmentGroup(string Key, IReadOnlyList<Equipment> Items);

/// <summary>
/// Catalogue des équipements et dons (page « Équipements ») : filtre par groupe, recherche
/// dans les noms et les effets (sans accents ni casse, langue courante et de référence), tri.
/// <list type="bullet">
/// <item>par Ministère : un groupe par Ministère dans l'ordre du référentiel (l'Axe, les dons,
/// en dernier), puis les cartes de faction (par faction), puis celles affiliées à un rouage ;</item>
/// <item>par nom : un seul groupe, de A à Z dans la langue courante.</item>
/// </list>
/// </summary>
public static class EquipmentBrowser
{
    public const string Factions = "FACTIONS", CogAffiliated = "COG";

    public static string GroupOf(Equipment e) => e.Ministry ?? (e.IsFactionAffiliated ? Factions : CogAffiliated);

    /// <param name="group">Groupe à afficher seul (code de Ministère, Factions…), ou null pour tous.</param>
    public static IReadOnlyList<EquipmentGroup> Browse(CatalogService catalog, LocalizationService l, string? group, string? search, EquipmentSort sort)
    {
        var words = CogQuery.Fold(search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string SortName(Equipment e) => CogQuery.Fold(l.EquipmentName(e).Text);
        var items = catalog.Catalog.Equipment
            .Where(e => group is null || GroupOf(e) == group)
            .Where(e => words.Length == 0 || words.All(CogQuery.Fold(l.SearchText(e)).Contains))
            .ToList();

        if (sort == EquipmentSort.Name)
            return items.Count == 0 ? [] : [new EquipmentGroup("", items.OrderBy(SortName, StringComparer.Ordinal).ToList())];

        var factionOrder = catalog.AllFactions.Select((f, i) => (f.Code, i)).ToDictionary(x => x.Code, x => x.i);
        var order = catalog.Catalog.Ministries.Select(m => m.Code).Append(Factions).Append(CogAffiliated).ToList();
        return items.GroupBy(GroupOf)
            .OrderBy(g => order.IndexOf(g.Key) is var i && i < 0 ? int.MaxValue : i)
            .Select(g => new EquipmentGroup(g.Key, g
                .OrderBy(e => e.Faction is { } f ? factionOrder.GetValueOrDefault(f, int.MaxValue) : 0)
                .ThenBy(SortName, StringComparer.Ordinal).ToList()))
            .ToList();
    }
}
