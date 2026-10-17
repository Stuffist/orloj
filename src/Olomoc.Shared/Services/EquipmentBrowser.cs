using Olomoc.Models;

namespace Olomoc.Services;

public enum EquipmentSort { Ministry, Name }

/// <summary>Groupe de la liste : code de Ministère, <see cref="EquipmentBrowser.FactionPrefix"/> + code de faction, ou <see cref="EquipmentBrowser.CogAffiliated"/>.</summary>
public sealed record EquipmentGroup(string Key, IReadOnlyList<Equipment> Items);

/// <summary>
/// Équipements et dons rangés par affiliation : chaque Ministère dans l'ordre du référentiel
/// (l'Axe, les dons, en dernier), puis les cartes de chaque faction (ordre des factions), puis
/// celles affiliées à un rouage ; dans un groupe, de A à Z dans la langue courante. Sert au
/// choix de l'équipement d'un rouage et au catalogue (filtre par groupe, recherche dans les
/// noms et les effets sans accents ni casse, tri par Ministère ou par nom).
/// </summary>
public static class EquipmentBrowser
{
    /// <summary>Filtre : toutes les cartes de faction.</summary>
    public const string Factions = "FACTIONS";
    public const string FactionPrefix = "FACTION:", CogAffiliated = "COG";

    public static string GroupOf(Equipment e) =>
        e.Ministry ?? (e.Faction is { } f ? FactionPrefix + f : CogAffiliated);

    /// <summary>Rangées par groupe d'affiliation, puis par nom.</summary>
    public static List<Equipment> Ordered(IEnumerable<Equipment> equipment, CatalogService catalog, LocalizationService l)
    {
        var order = catalog.Catalog.Ministries.Select(m => m.Code)
            .Concat(catalog.AllFactions.Select(f => FactionPrefix + f.Code))
            .Append(CogAffiliated)
            .Select((key, i) => (key, i)).ToDictionary(x => x.key, x => x.i);
        return equipment
            .OrderBy(e => order.GetValueOrDefault(GroupOf(e), int.MaxValue))
            .ThenBy(e => SortName(e, l), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Groupes successifs d'une liste déjà rangée par <see cref="Ordered"/>.</summary>
    public static IReadOnlyList<EquipmentGroup> Grouped(IReadOnlyList<Equipment> ordered) =>
        ordered.GroupBy(GroupOf).Select(g => new EquipmentGroup(g.Key, g.ToList())).ToList();

    /// <param name="group">Groupe à afficher seul (code de Ministère, <see cref="Factions"/>,
    /// <see cref="FactionPrefix"/> + code…), ou null pour tous.</param>
    /// <param name="only">Ids à garder seuls (ce que la cabale peut équiper), ou null pour tous.</param>
    public static IReadOnlyList<EquipmentGroup> Browse(CatalogService catalog, LocalizationService l, string? group, string? search, EquipmentSort sort,
                                                       IReadOnlySet<string>? only = null)
    {
        var words = CogQuery.Fold(search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var items = catalog.Catalog.Equipment
            .Where(e => InGroup(e, group))
            .Where(e => only is null || only.Contains(e.Id))
            .Where(e => words.Length == 0 || words.All(CogQuery.Fold(l.SearchText(e)).Contains))
            .ToList();

        if (sort == EquipmentSort.Name)
            return items.Count == 0 ? [] : [new EquipmentGroup("", items.OrderBy(e => SortName(e, l), StringComparer.Ordinal).ToList())];
        return Grouped(Ordered(items, catalog, l));
    }

    /// <summary>Titre d'un groupe : nom du Ministère ou de la faction.</summary>
    public static string GroupTitle(string key, LocalizationService l) =>
        key == CogAffiliated ? l.Ui("equipment.group.cog")
        : key.StartsWith(FactionPrefix, StringComparison.Ordinal) ? l.FactionName(key[FactionPrefix.Length..]).Text
        : l.MinistryName(key).Text;

    /// <summary>Ministère d'un groupe (son emblème), sinon null.</summary>
    public static string? GroupMinistry(string key) =>
        key == CogAffiliated || key.StartsWith(FactionPrefix, StringComparison.Ordinal) ? null : key;

    /// <summary>Faction d'un groupe (son logo), sinon null.</summary>
    public static string? GroupFaction(string key) =>
        key.StartsWith(FactionPrefix, StringComparison.Ordinal) ? key[FactionPrefix.Length..] : null;

    private static bool InGroup(Equipment e, string? group) => group switch
    {
        null => true,
        Factions => e.IsFactionAffiliated,
        _ => GroupOf(e) == group,
    };

    private static string SortName(Equipment e, LocalizationService l) => CogQuery.Fold(l.EquipmentName(e).Text);
}
