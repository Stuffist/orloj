namespace Olomoc.Zones;

/// <summary>
/// Hexagons of the map of the Block (img/zones/map.jpg, a 2:1 city plan). A honeycomb of
/// flat-topped hexagons around the central district: at full size 14 % of the plan's width wide
/// (columns 10.5 % apart, rows 24.25 % apart, every other column shifted by half a row). Slot
/// <c>i</c> is always the same cell of the honeycomb: the first 25 cover the city, the next ones
/// widen the honeycomb ring by ring. When the zones shown go beyond the plan, the whole honeycomb
/// shrinks around the center (<see cref="Scale"/>): the hexagons get smaller, never change places
/// among themselves. Each zone keeps its slot (<see cref="Zone.Slot"/>).
/// </summary>
public static class ZoneMapLayout
{
    /// <summary>Center of the honeycomb and size of a hexagon at full size, in % of the plan's width and height.</summary>
    public const double CenterX = 47, CenterY = 50, Width = 14, Height = 24.25;

    // Room around the center (in %): to the left, to the right, up and down (a small margin).
    private const double Left = 47, Right = 53, Vertical = 49.5;

    // The first slots (columns, rows), center first: they cover the districts of the plan at full size.
    private static readonly (int C, int R)[] City =
    [
        (0, 0),
        (1, 0), (-1, 1), (0, -1), (0, 1), (-1, 0), (1, 1),
        (2, 0), (-2, 0), (2, -1), (-2, -1), (2, 1), (-2, 1),
        (1, 2), (-1, 2), (1, -1), (-1, -1),
        (3, 1), (-3, 0), (3, 0), (-3, 1),
        (3, 2), (-3, 2), (3, -1), (-3, -1),
    ];

    /// <summary>
    /// Offsets of the hexagons' centers from the center of the honeycomb, at full size (in % of the
    /// plan's width and height): the city first, then the cells that widen the honeycomb least, one
    /// per possible zone.
    /// </summary>
    public static readonly IReadOnlyList<(double Dx, double Dy)> Slots = Build();

    /// <summary>
    /// Size of the hexagons (1: full size) so that the given slots all fit on the plan: 1 while they
    /// fit at full size.
    /// </summary>
    public static double Scale(IEnumerable<int> slots) =>
        Math.Min(1, 1 / slots.Where(s => s >= 0 && s < Slots.Count).Select(s => Need(Slots[s])).DefaultIfEmpty(1).Max());

    /// <summary>Center of a hexagon on the plan (in %) at the given size.</summary>
    public static (double X, double Y) Center(int slot, double scale) =>
        (CenterX + Slots[slot].Dx * scale, CenterY + Slots[slot].Dy * scale);

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

    // How much the plan would need to be wider/taller for this hexagon to fit at full size (≤ 1: it fits).
    private static double Need((double Dx, double Dy) o) =>
        Math.Max((Math.Abs(o.Dx) + Width / 2) / (o.Dx < 0 ? Left : Right), (Math.Abs(o.Dy) + Height / 2) / Vertical);

    private static (double Dx, double Dy) Offset((int C, int R) cell) =>
        (cell.C * Width * .75, (cell.R - (cell.C % 2 != 0 ? .5 : 0)) * Height);

    private static List<(double Dx, double Dy)> Build()
    {
        const int Reach = 40;
        var city = City.ToHashSet();
        var more = Enumerable.Range(-Reach, 2 * Reach + 1)
            .SelectMany(c => Enumerable.Range(-Reach, 2 * Reach + 1).Select(r => (C: c, R: r)))
            .Where(cell => !city.Contains(cell))
            .Select(cell => (Cell: cell, Offset: Offset(cell)))
            // The cells that need the least room first; then the closest (the plan is twice as wide as tall).
            .OrderBy(x => Need(x.Offset)).ThenBy(x => x.Offset.Dx * x.Offset.Dx + x.Offset.Dy * x.Offset.Dy / 4)
            .ThenBy(x => x.Cell.C).ThenBy(x => x.Cell.R)
            .Take(ZoneRules.MaxZones - City.Length)
            .Select(x => x.Offset);
        return [.. City.Select(Offset), .. more];
    }
}
