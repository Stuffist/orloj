using System.Globalization;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Card sheet of a cabal: one landscape A4 page, cards at actual size (89 × 63 mm, the game's
/// landscape card format), centered. Rank 3 to 1 from left to right (like the builder), each cog
/// above its equipment/boon; a thin gap between cogs, thinner between a cog and its equipment.
/// Same measurements for printing (CSS, in %) and for the PNG image.
/// </summary>
public static class CardSheet
{
    public const double PageWidthMm = 297, PageHeightMm = 210;
    public const double CardWidthMm = 89, CardHeightMm = 63;
    public const double ColumnGapMm = 4, RowGapMm = 1.5;

    public static double LeftMm => (PageWidthMm - (3 * CardWidthMm + 2 * ColumnGapMm)) / 2;
    public static double TopMm => (PageHeightMm - (2 * CardHeightMm + RowGapMm)) / 2;

    /// <summary>Image of a card (cog or equipment/boon), from its id.</summary>
    public static string ImagePath(string id) => $"{Assets.Base}img/cards/{id}.jpg";

    /// <summary>Position of a card: column (3 − rank), row (0 cog, 1 equipment), position in mm.</summary>
    public sealed record Cell(int Column, int Row, double XMm, double YMm, string Id);

    /// <summary>Slot (column 0 to 2, row 0 or 1), with the card placed there (id, or "" if it is empty).</summary>
    public static Cell Slot(int column, int row, string id = "") =>
        new(column, row, LeftMm + column * (CardWidthMm + ColumnGapMm), TopMm + row * (CardHeightMm + RowGapMm), id);

    /// <summary>
    /// Cards to place, from left to right: rank 3, 2, 1 (slots 2, 1, 0 of the arrays, sorted by
    /// rank); empty ones are omitted.
    /// </summary>
    public static IReadOnlyList<Cell> Cells(IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment)
    {
        var cells = new List<Cell>();
        for (var column = 0; column < 3; column++)
        {
            var slot = 2 - column;
            if (slot < cogs.Count && cogs[slot] is { } cog) cells.Add(Slot(column, 0, cog.Id));
            if (slot < equipment.Count && equipment[slot] is { } e) cells.Add(Slot(column, 1, e.Id));
        }
        return cells;
    }

    /// <summary>Position and size in % of the page (display at any scale, printing included).</summary>
    public static string Style(Cell c) => string.Create(CultureInfo.InvariantCulture,
        $"left:{c.XMm / PageWidthMm * 100:0.###}%;top:{c.YMm / PageHeightMm * 100:0.###}%;" +
        $"width:{CardWidthMm / PageWidthMm * 100:0.###}%;height:{CardHeightMm / PageHeightMm * 100:0.###}%");

    // ---- Cabal in the card sheet's address: "c=3R05.E11,2R04,1R01.E38" (rank 1, 2, 3) ----

    public static string Encode(IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment) =>
        string.Join(',', Enumerable.Range(0, 3).Select(i =>
            (i < cogs.Count ? cogs[i]?.Id : null) is { } cog
                ? cog + (i < equipment.Count && equipment[i] is { } e ? "." + e.Id : "")
                : ""));

    /// <summary>Cogs and equipment/boons per slot; unknown ids ignored.</summary>
    public static (Cog?[] Cogs, Equipment?[] Equipment) Decode(string? value, CatalogService catalog)
    {
        var cogs = new Cog?[3];
        var equipment = new Equipment?[3];
        var parts = (value ?? "").Split(',');
        for (var i = 0; i < Math.Min(3, parts.Length); i++)
        {
            var ids = parts[i].Split('.', 2);
            cogs[i] = ids[0].Length > 0 ? catalog.CogById(ids[0]) : null;
            if (cogs[i] is not null && ids.Length > 1) equipment[i] = catalog.EquipmentById(ids[1]);
        }
        return (cogs, equipment);
    }
}
