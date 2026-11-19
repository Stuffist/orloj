using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Olomoc.Online;

// Imports the table's assets from Occicat's Vassal module (a ZIP: buildFile.xml and images/):
// the 16″ game mats, the terrain of the "Décors" palette, the markers, the token of each cog
// and equipment/boon (matched to the card catalog by its French name). Images are copied as they
// are into src/Olomoc.Shared/wwwroot/img/table/ (emptied first), the manifest written to
// data/olomoc-table.json. In the module, 30 px = 1″ (a 16″ mat is 480 px, a 30 mm base about 35 px).
// Sizes fixed by the rules, not taken from the images: bases of the objectives 30 mm, push template 25 mm.

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/VassalImport -- <module.vmod> [repository]");
    return 1;
}
var root = args.Length > 1 ? args[1] : FindRoot(Directory.GetCurrentDirectory());
if (root is null)
{
    Console.Error.WriteLine("Repository not found (Olomoc.slnx): give its path as second argument.");
    return 1;
}
var web = Path.Combine(root, "src", "Olomoc.Shared", "wwwroot");
var output = Path.Combine(web, TableManifest.Images.TrimEnd('/'));
const double PxPerMm = 30 / TableManifest.MmPerInch;
const double ObjectiveBase = 30, PushTemplate = 25;

using var module = ZipFile.OpenRead(args[0]);
var build = XDocument.Parse(Read(module, "buildFile.xml"));
var version = Regex.Match(Read(module, "moduledata"), "<version>([^<]*)</version>").Groups[1].Value;
var prototypes = build.Descendants().Where(e => e.Name.LocalName == "VASSAL.build.module.PrototypeDefinition")
    .GroupBy(e => (string?)e.Attribute("name") ?? "").ToDictionary(g => g.Key, g => g.First().Value);
var slots = build.Descendants().Where(e => e.Name.LocalName == "VASSAL.build.widget.PieceSlot")
    .Select(e => (Name: (string?)e.Attribute("entryName") ?? "", Definition: e.Value)).ToList();

if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
var copied = new Dictionary<string, string>();          // content hash => published path (same image once)

// ---- Boards: the mats of the "Tapis" piece, the mining Block ones, the plain one ----
var boards = new List<TableBoard>();
foreach (var (id, image) in new[] { ("mez", "Tapis-Bloc.png"), ("karst", "Tapis-Karst.png"), ("bloc-minier-1", "tapis_bloc_minier.jpg"),
             ("bloc-minier-2", "tapis_bloc_minier_2.jpg"), ("plain", "fond_40_cm.png") })
{
    if (Copy(image, "boards", id) is not { } path) continue;
    var (w, _) = Size(image);
    boards.Add(new TableBoard(id, path, Mm(w)));
}

// ---- Terrain: pieces with a "Terrain N" prototype ----
var terrain = new List<TableTerrain>();
foreach (var (name, definition) in slots)
{
    if (Regex.Match(definition, @"prototype;Terrain (\d)") is not { Success: true } level) continue;
    if (Regex.Match(definition, @"piece;;;([^;]+);") is not { Success: true } image) continue;
    var file = image.Groups[1].Value;
    var number = Regex.Match(name, @"(\d+)\s*(\(|$)") is { Success: true } n ? int.Parse(n.Groups[1].Value) : (int?)null;
    var type = TerrainType(name);
    var id = Slug(type + (number is { } k ? $"-{k}" : ""));
    while (terrain.Any(t => t.Id == id)) id += "b";
    if (Copy(file, "terrain", id) is not { } path) continue;           // same image already imported
    var (w, h) = Size(file);
    terrain.Add(new TableTerrain(id, type, number, path, Mm(w), Mm(h), !name.Contains("non obstruant", StringComparison.OrdinalIgnoreCase),
        int.Parse(level.Groups[1].Value), Outline(definition)));
}

// ---- Markers: objectives, PoliSec and black cubes, Fiel, player markers ----
var markers = new List<TableMarker>();
foreach (var (id, image) in new[] { ("objective-p1", "pion_objectif_J1.png"), ("objective-p2", "pion_objectif_J2.png"), ("objective", "marqueur.png"),
             ("polisec", "Cube_vert.png"), ("black", "Cube_noir.png"), ("fiel", "noirceur.png"), ("p1", "pions-J1.png"), ("p2", "pions-J2.png") })
{
    if (Copy(image, "markers", id) is not { } path) continue;
    var (w, h) = Size(image);
    markers.Add(new TableMarker(id, path, id.StartsWith("objective") ? ObjectiveBase : Mm(Math.Max(w, h))));
}

// ---- Movement templates: the ruler of a normal move (stadium), the push one (disc) ----
var templates = new List<TableTemplate>();
foreach (var (id, image) in new[] { ("move", "reglette2024.png"), ("push", "poussee2024.png") })
{
    if (Copy(image, "templates", id) is not { } path) continue;
    var (w, h) = Size(image);
    templates.Add(id == "push" ? new TableTemplate(id, path, PushTemplate, PushTemplate)
        : new TableTemplate(id, path, Mm(Math.Max(w, h)), Mm(Math.Min(w, h))));
}

// ---- Tokens: "Player: <name>" prototypes (base: 30Rims, 40Rims, 50Rims), matched to the catalog ----
var cards = JsonNode.Parse(File.ReadAllText(Path.Combine(web, "i18n", "fr", "cards.json")))!;
// Names, and titles ("« Sonar »"): the module names some cogs by their nickname.
var names = new[] { "cogs", "equipment" }.SelectMany(c => cards[c]!.AsObject())
    .SelectMany(kv => new[] { (string?)kv.Value!["name"], (string?)kv.Value!["title"] }.OfType<string>().Select(n => (Id: kv.Key, Name: n)))
    .ToList();
var tokens = new List<TableToken>();
var unmatched = new List<string>();
foreach (var (key, definition) in prototypes.Where(p => p.Key.StartsWith("Player: ")))
{
    var name = key["Player: ".Length..];
    var image = Regex.Match(definition, @"emb2;Activate;[^\t]*?;1;true;0;0;([^;]+);");
    var rims = Regex.Match(definition, @"prototype;(\d\d)Rims");
    if (!image.Success || !rims.Success) continue;
    if (Match(name) is not { } id)
    {
        unmatched.Add(name);
        continue;
    }
    if (tokens.Any(t => t.Id == id)) continue;
    if (Copy(image.Groups[1].Value, "tokens", id, unique: false) is not { } path) continue;
    tokens.Add(new TableToken(id, path, int.Parse(rims.Groups[1].Value)));
}

var manifest = new TableManifest(TableManifest.CurrentVersion, $"Occicat Games, Vassal module {version}",
    boards, terrain, markers, tokens.OrderBy(t => t.Id).ToList(), templates);
File.WriteAllText(Path.Combine(web, TableManifest.File.Replace('/', Path.DirectorySeparatorChar)),
    JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n");

Console.WriteLine($"Module {version}: {boards.Count} boards, {terrain.Count} terrain, {markers.Count} markers, {tokens.Count} tokens, {templates.Count} templates.");
if (unmatched.Count > 0) Console.WriteLine($"Not in the catalog (left out): {string.Join(", ", unmatched)}");
return 0;

// ---- Helpers ----

static string? FindRoot(string? dir)
{
    for (; dir is not null; dir = Path.GetDirectoryName(dir))
        if (File.Exists(Path.Combine(dir, "Olomoc.slnx"))) return dir;
    return null;
}

static string Read(ZipArchive zip, string name)
{
    using var reader = new StreamReader(zip.GetEntry(name)!.Open());
    return reader.ReadToEnd();
}

byte[]? Bytes(string image)
{
    if (module.GetEntry("images/" + image) is not { } entry) return null;
    using var stream = entry.Open();
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
}

// Copies an image of the module to img/table/{folder}/{id}.{ext}; null if missing, or (unique) if
// the same image was already copied.
string? Copy(string image, string folder, string id, bool unique = true)
{
    if (Bytes(image) is not { } bytes)
    {
        Console.WriteLine($"Missing image: {image}");
        return null;
    }
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    if (unique && copied.ContainsKey(hash)) return null;
    var path = $"{folder}/{id}{Path.GetExtension(image).ToLowerInvariant()}";
    Directory.CreateDirectory(Path.Combine(output, folder));
    File.WriteAllBytes(Path.Combine(output, folder, Path.GetFileName(path)), bytes);
    copied[hash] = path;
    return TableManifest.Images + path;
}

// Width and height of a PNG (IHDR) or a JPEG (SOF marker).
(int W, int H) Size(string image)
{
    var b = Bytes(image)!;
    if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P')
        return (b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19], b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23]);
    for (var i = 2; i + 9 < b.Length;)
    {
        var marker = b[i + 1];
        var length = b[i + 2] << 8 | b[i + 3];
        if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            return (b[i + 7] << 8 | b[i + 8], b[i + 5] << 8 | b[i + 6]);
        i += 2 + length;
    }
    throw new InvalidDataException($"Unknown image size: {image}");
}

static double Mm(double px) => Math.Round(px / PxPerMm, 1);

// "Cuve 1" => cuve, "Caisses 3 (non obstruant)" => caisse, "Zone de Fiel 2" => zone-de-fiel.
static string TerrainType(string name)
{
    var text = Regex.Replace(name, @"\(.*?\)", "");
    text = Regex.Replace(text, @"\d+", "").Trim();
    var slug = Slug(text);
    return slug switch { "caisses" => "caisse", _ => slug };
}

static string Slug(string text)
{
    var plain = new string(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    return Regex.Replace(plain.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

static string Normal(string text) => Slug(text).Replace("-", " ");

// Catalog id of a token's name: overrides, then the same name, then a single card whose name
// contains all the words of the token's name ("Jakub" => "Jakub Myasnik").
string? Match(string name)
{
    if (Overrides.TryGetValue(name, out var forced)) return forced;
    var wanted = Normal(name);
    if (names.Where(n => Normal(n.Name) == wanted).Select(n => n.Id).Distinct().ToList() is [var exact]) return exact;
    var words = wanted.Split(' ');
    return names.Where(n => words.All(Normal(n.Name).Split(' ').Contains)).Select(n => n.Id).Distinct().ToList() is [var single] ? single : null;
}

// Outline of the piece (nonRect2 trait), simplified, in mm from the centre; null if none.
static IReadOnlyList<double[]>? Outline(string definition)
{
    var shape = Regex.Match(definition, @"nonRect2;[\d.]+;([^\t]*)");
    if (!shape.Success) return null;
    var text = shape.Groups[1].Value;
    var points = Regex.Matches(text, @"[MLml][ ,]+(-?[\d.]+)[ ,]+(-?[\d.]+)")
        .Select(m => (X: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Y: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
        .ToList();
    if (points.Count < 3) return null;
    return Simplify(points, 1.0).Select(p => new[] { Mm(p.X), Mm(p.Y) }).ToList();
}

// Ramer-Douglas-Peucker: removes the points closer than epsilon (px) to the line of their neighbours.
static List<(double X, double Y)> Simplify(List<(double X, double Y)> points, double epsilon)
{
    if (points.Count < 3) return points;
    var (a, b) = (points[0], points[^1]);
    var (index, distance) = (0, 0.0);
    for (var i = 1; i < points.Count - 1; i++)
    {
        var d = Distance(points[i], a, b);
        if (d > distance) (index, distance) = (i, d);
    }
    if (distance <= epsilon) return [a, b];
    var left = Simplify(points[..(index + 1)], epsilon);
    var right = Simplify(points[index..], epsilon);
    return [.. left[..^1], .. right];
}

static double Distance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
{
    var (dx, dy) = (b.X - a.X, b.Y - a.Y);
    var length = Math.Sqrt(dx * dx + dy * dy);
    if (length == 0) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
    return Math.Abs(dy * p.X - dx * p.Y + b.X * a.Y - b.Y * a.X) / length;
}

partial class Program
{
    // Names of the module that differ from the catalog's; null: left out (no French card).
    private static readonly Dictionary<string, string?> Overrides = new()
    {
        ["Kadlec_Libus"] = "1R23",
        ["Robert Kadlec"] = null,          // Ligue de Maisel's Kadlec (R13): English card only
        ["Munitions"] = "E09",             // Munitions à haute vélocité
    };
}
