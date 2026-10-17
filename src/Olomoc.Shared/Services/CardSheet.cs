using System.Globalization;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Planche des cartes d'une cabale : une page A4 paysage, cartes à taille réelle (89 × 63 mm,
/// format paysage des cartes du jeu), centrées. Rang 3 à 1 de gauche à droite (comme le
/// constructeur), chaque rouage
/// au-dessus de son équipement/don ; écart fin entre les rouages, plus fin entre un rouage et
/// son équipement. Mêmes mesures pour l'impression (CSS, en %) et pour l'image PNG.
/// </summary>
public static class CardSheet
{
    public const double PageWidthMm = 297, PageHeightMm = 210;
    public const double CardWidthMm = 89, CardHeightMm = 63;
    public const double ColumnGapMm = 4, RowGapMm = 1.5;

    public static double LeftMm => (PageWidthMm - (3 * CardWidthMm + 2 * ColumnGapMm)) / 2;
    public static double TopMm => (PageHeightMm - (2 * CardHeightMm + RowGapMm)) / 2;

    /// <summary>Image d'une carte (rouage ou équipement/don), d'après son id.</summary>
    public static string ImagePath(string id) => $"{Assets.Base}img/cards/{id}.jpg";

    /// <summary>Emplacement d'une carte : colonne (3 − rang), ligne (0 rouage, 1 équipement), position en mm.</summary>
    public sealed record Cell(int Column, int Row, double XMm, double YMm, string Id);

    /// <summary>Emplacement (colonne 0 à 2, ligne 0 ou 1), avec la carte qu'on y pose (id, ou "" s'il est vide).</summary>
    public static Cell Slot(int column, int row, string id = "") =>
        new(column, row, LeftMm + column * (CardWidthMm + ColumnGapMm), TopMm + row * (CardHeightMm + RowGapMm), id);

    /// <summary>
    /// Cartes à poser, de gauche à droite : rang 3, 2, 1 (emplacements 2, 1, 0 des tableaux,
    /// rangés par rang) ; les vides sont omis.
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

    /// <summary>Position et taille en % de la page (affichage à toute échelle, impression comprise).</summary>
    public static string Style(Cell c) => string.Create(CultureInfo.InvariantCulture,
        $"left:{c.XMm / PageWidthMm * 100:0.###}%;top:{c.YMm / PageHeightMm * 100:0.###}%;" +
        $"width:{CardWidthMm / PageWidthMm * 100:0.###}%;height:{CardHeightMm / PageHeightMm * 100:0.###}%");

    // ---- Cabale dans l'adresse de la planche : « c=3R05.E11,2R04,1R01.E38 » (rang 1, 2, 3) ----

    public static string Encode(IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment) =>
        string.Join(',', Enumerable.Range(0, 3).Select(i =>
            (i < cogs.Count ? cogs[i]?.Id : null) is { } cog
                ? cog + (i < equipment.Count && equipment[i] is { } e ? "." + e.Id : "")
                : ""));

    /// <summary>Rouages et équipements/dons par emplacement ; ids inconnus ignorés.</summary>
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
