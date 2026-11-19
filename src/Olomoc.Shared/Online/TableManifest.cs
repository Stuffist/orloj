namespace Olomoc.Online;

// What can be put on the table of an online game (data/olomoc-table.json, written by
// tools/VassalImport from Occicat's Vassal module): boards, terrain, markers, and the token of each
// cog and equipment/boon. Sizes and positions in millimetres, on a board of BoardSize × BoardSize.

/// <summary>A board (the 40 cm game mat): image, side in mm.</summary>
public sealed record TableBoard(string Id, string Image, double Size);

/// <summary>
/// A piece of terrain: type ("cuve", "caisse"…, texts "online.table.terrain.{type}") and number in
/// the module, image and size (mm), obstructing or not, level in the module (1 lowest to 4), outline
/// (mm, from the centre; for a line of sight to come).
/// </summary>
public sealed record TableTerrain(string Id, string Type, int? Number, string Image, double Width, double Height,
    bool Obstructing, int Level, IReadOnlyList<double[]>? Outline);

/// <summary>A marker (objectives, PoliSec, Fiel…, texts "online.table.marker.{id}"): image, diameter (mm).</summary>
public sealed record TableMarker(string Id, string Image, double Size);

/// <summary>Token of a cog or of an equipment/boon (card catalog id): image, base diameter (mm).</summary>
public sealed record TableToken(string Id, string Image, int Base);

public sealed record TableManifest(int Version, string Source, IReadOnlyList<TableBoard> Boards, IReadOnlyList<TableTerrain> Terrain,
    IReadOnlyList<TableMarker> Markers, IReadOnlyList<TableToken> Tokens)
{
    public const int CurrentVersion = 1;
    public const string File = "data/olomoc-table.json";
    /// <summary>Folder of the images, under the shared static files.</summary>
    public const string Images = "img/table/";
    /// <summary>Side of the game mat, in mm (40 cm).</summary>
    public const double BoardSize = 400;
    public const double MmPerInch = 25.4;
    /// <summary>Base of a cog without one in the catalog or the module.</summary>
    public const int DefaultBase = 30;

    public TableBoard? Board(string? id) => Boards.FirstOrDefault(b => b.Id == id);
    public TableTerrain? TerrainOf(string? id) => Terrain.FirstOrDefault(t => t.Id == id);
    public TableMarker? Marker(string? id) => Markers.FirstOrDefault(m => m.Id == id);
    public TableToken? Token(string? id) => Tokens.FirstOrDefault(t => t.Id == id);
}
