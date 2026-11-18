namespace Olomoc.Zones;

/// <summary>
/// Hexagons of the map of the Block (img/zones/map.jpg, a 2:1 city plan): centers in % of the plan's
/// width and height. A honeycomb of flat-topped hexagons 14 % of the width wide (columns 10.5 %
/// apart, rows 24.25 % apart, every other column shifted by half a row), the central district
/// first, then rings outwards over the city. Each zone keeps its slot (<see cref="Zone.Slot"/>).
/// </summary>
public static class ZoneMapLayout
{
    public static readonly IReadOnlyList<(double X, double Y)> Slots =
    [
        (47, 50),
        (57.5, 37.875), (36.5, 62.125), (47, 25.75), (47, 74.25), (36.5, 37.875), (57.5, 62.125),
        (68, 50), (26, 50), (68, 25.75), (26, 25.75), (68, 74.25), (26, 74.25),
        (57.5, 86.375), (36.5, 86.375), (57.5, 13.625), (36.5, 13.625),
        (78.5, 62.125), (15.5, 37.875), (78.5, 37.875), (15.5, 62.125),
        (78.5, 86.375), (15.5, 86.375), (78.5, 13.625), (15.5, 13.625),
    ];

    /// <summary>
    /// Gives the free slots, the most central first, to the zones that have none, the oldest first.
    /// Returns the zones changed.
    /// </summary>
    public static List<Zone> Assign(IEnumerable<Zone> zones)
    {
        var all = zones.ToList();
        var used = all.Where(z => z.Slot is not null).Select(z => z.Slot!.Value).ToHashSet();
        var free = new Queue<int>(Enumerable.Range(0, Slots.Count).Where(i => !used.Contains(i)));
        var changed = new List<Zone>();
        foreach (var z in all.Where(z => z.Slot is null).OrderBy(z => z.CreatedAt).ThenBy(z => z.Code))
        {
            if (!free.TryDequeue(out var slot)) break;
            z.Slot = slot;
            changed.Add(z);
        }
        return changed;
    }
}
