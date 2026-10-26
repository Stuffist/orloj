using Olomoc.Models;

namespace Olomoc.Services;

public enum EquipmentSort { Ministry, Name }

/// <summary>Group of the list: Ministry code, <see cref="EquipmentBrowser.FactionPrefix"/> + faction code, or <see cref="EquipmentBrowser.CogAffiliated"/>.</summary>
public sealed record EquipmentGroup(string Key, IReadOnlyList<Equipment> Items);

/// <summary>
/// Equipment and boons sorted by affiliation: each Ministry in the catalog's order (the Axis, the
/// boons, last), then the cards of each faction (factions' order), then those affiliated with a
/// cog; within a group, A to Z in the current language. Used for choosing a cog's equipment and
/// for the catalog (filter by group, search in names and effects ignoring accents and case, sort
/// by Ministry or by name).
/// </summary>
public static class EquipmentBrowser
{
    /// <summary>Filter: all faction cards.</summary>
    public const string Factions = "FACTIONS";
    public const string FactionPrefix = "FACTION:", CogAffiliated = "COG";

    public static string GroupOf(Equipment e) =>
        e.Ministry ?? (e.Faction is { } f ? FactionPrefix + f : CogAffiliated);

    /// <summary>Sorted by affiliation group, then by name.</summary>
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

    /// <summary>Successive groups of a list already sorted by <see cref="Ordered"/>.</summary>
    public static IReadOnlyList<EquipmentGroup> Grouped(IReadOnlyList<Equipment> ordered) =>
        ordered.GroupBy(GroupOf).Select(g => new EquipmentGroup(g.Key, g.ToList())).ToList();

    /// <param name="group">Group to show alone (Ministry code, <see cref="Factions"/>,
    /// <see cref="FactionPrefix"/> + code…), or null for all.</param>
    /// <param name="only">Ids to keep alone (what the cabal can equip), or null for all.</param>
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

    /// <summary>Title of a group: name of the Ministry or of the faction.</summary>
    public static string GroupTitle(string key, LocalizationService l) =>
        key == CogAffiliated ? l.Ui("equipment.group.cog")
        : key.StartsWith(FactionPrefix, StringComparison.Ordinal) ? l.FactionName(key[FactionPrefix.Length..]).Text
        : l.MinistryName(key).Text;

    /// <summary>Ministry of a group (its emblem), otherwise null.</summary>
    public static string? GroupMinistry(string key) =>
        key == CogAffiliated || key.StartsWith(FactionPrefix, StringComparison.Ordinal) ? null : key;

    /// <summary>Faction of a group (its logo), otherwise null.</summary>
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
