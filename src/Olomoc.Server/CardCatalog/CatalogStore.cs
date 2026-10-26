using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;

namespace Olomoc.Server.CardCatalog;

/// <summary>
/// Card catalog served to the site: the repository's JSON files (shipped in the image, the base
/// for PRs) and, on top of them, the changes made in the editor, in the same format, in
/// DATA_DIR/catalog (only the changed or created entries). Merged entry by entry (id or code):
/// <list type="bullet">
/// <item><c>data/olomoc-catalog.json</c>: cogs and equipment/boons (equipment);</item>
/// <item><c>data/olomoc-factions.json</c>: factions (alignment, member cogs);</item>
/// <item><c>i18n/&lt;language&gt;/cards.json</c>: texts (cogs, equipment, factions, ministries).</item>
/// </list>
/// An entry without <c>"published": true</c> (draft) is only served to editors and admins: removed
/// for everyone else, with its texts and its place in the factions. At startup, a change that has
/// become identical to the repository (files exported then committed) is removed from DATA_DIR/catalog.
/// </summary>
public sealed class CatalogStore(IFileProvider webRoot, string overlayDirectory, ILogger<CatalogStore> log)
{
    public const string ContentPrefix = "_content/Olomoc.Shared/";
    public const string CatalogPath = "data/olomoc-catalog.json", FactionsPath = "data/olomoc-factions.json";
    public static string CardsPath(string language) => $"i18n/{language}/cards.json";

    /// <summary>File writing: like the repository's (indent of 2, unescaped accents, \n line endings).</summary>
    public static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // Arrays always written, even empty; other empty values are omitted (as in the repository).
    private static readonly HashSet<string> KeptEmptyArrays = ["abilities", "lifeLine", "ministries", "cogIds"];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, JsonObject> _base = new();
    private readonly Dictionary<string, JsonObject> _overlay = new();
    private readonly Dictionary<(string Path, bool Drafts), Served> _served = new();
    private readonly object _servedLock = new();
    private IReadOnlyList<string> _languages = [];

    public sealed record Served(byte[] Body, string ETag);

    /// <summary>Card catalog changed (editor): the server's catalog must be reloaded.</summary>
    public event Action? Changed;

    public IReadOnlyList<string> Languages => _languages;

    /// <summary>Merged files: catalog, factions, texts of each language.</summary>
    public IEnumerable<string> Paths => new[] { CatalogPath, FactionsPath }.Concat(_languages.Select(CardsPath));

    // ---- Loading ----

    public async Task InitializeAsync()
    {
        _languages = (JsonNode.Parse(await ReadBaseAsync("i18n/languages.json") ?? "[]") as JsonArray ?? [])
            .Select(l => l?["code"]?.GetValue<string>()).OfType<string>().ToList();
        foreach (var path in Paths)
        {
            _base[path] = JsonNode.Parse(await ReadBaseAsync(path) ?? "{}") as JsonObject ?? new JsonObject();
            var file = OverlayFile(path);
            if (!File.Exists(file)) continue;
            try
            {
                if (JsonNode.Parse(await File.ReadAllTextAsync(file)) is JsonObject overlay) _overlay[path] = overlay;
            }
            catch (JsonException e) { log.LogWarning(e, "Card catalog: {File} unreadable, ignored.", file); }
        }
        // Changes merged into the repository since: removed.
        foreach (var path in _overlay.Keys.ToList())
            if (Prune(path))
            {
                log.LogInformation("Card catalog: changes of {Path} already in the repository, removed from DATA_DIR/catalog.", path);
                await WriteOverlayAsync(path, _overlay[path].Count > 0 ? Text(_overlay[path]) : null);
            }
        lock (_servedLock) _served.Clear();
    }

    private async Task<string?> ReadBaseAsync(string path)
    {
        var file = webRoot.GetFileInfo(ContentPrefix + path);
        if (!file.Exists) return null;
        await using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    // ---- Reading ----

    /// <summary>Merged document (copy), drafts included.</summary>
    public JsonObject Merged(string path)
    {
        lock (_servedLock) return Merge(_base.GetValueOrDefault(path) ?? new JsonObject(), _overlay.GetValueOrDefault(path));
    }

    /// <summary>Served file (drafts included for an editor or an admin), or null if it is not part of the catalog.</summary>
    public Served? Serve(string path, bool drafts)
    {
        if (!Paths.Contains(path)) return null;
        lock (_servedLock)
        {
            if (_served.TryGetValue((path, drafts), out var cached)) return cached;
            var doc = Merged(path);
            if (!drafts) HideDrafts(path, doc);
            var body = Encoding.UTF8.GetBytes(Text(doc));
            return _served[(path, drafts)] = new(body, $"\"{Convert.ToHexString(SHA256.HashData(body))[..16]}\"");
        }
    }

    /// <summary>Changed (in the repository) or new entries, by collection: cogs, equipment, factions.</summary>
    public Dictionary<string, Dictionary<string, string>> Status()
    {
        lock (_servedLock) return StatusUnlocked();
    }

    private Dictionary<string, Dictionary<string, string>> StatusUnlocked()
    {
        // Entry changed (in its document or its texts); new if its repository document does not have it.
        var status = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (collection, main) in new[] { ("cogs", CatalogPath), ("equipment", CatalogPath), ("factions", FactionsPath) })
        {
            var keys = new[] { main }.Concat(_languages.Select(CardsPath))
                .SelectMany(path => Keys(_overlay.GetValueOrDefault(path)?[collection])).Distinct();
            status[collection] = keys.ToDictionary(key => key,
                key => Find(_base.GetValueOrDefault(main)?[collection], key) is null ? "new" : "modified");
        }
        return status;
    }

    /// <summary>Complete merged files (drafts included), at the repository paths: to commit back.</summary>
    public byte[] ExportZip()
    {
        lock (_servedLock) return ExportZipUnlocked();
    }

    private byte[] ExportZipUnlocked()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var path in Paths)
            {
                var entry = zip.CreateEntry("src/Olomoc.Shared/wwwroot/" + path, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(Text(Merged(path)));
            }
        return buffer.ToArray();
    }

    // ---- Changes (editor) ----

    /// <summary>Applies changes to the catalog (under lock), then saves them.</summary>
    public async Task EditAsync(Action<Editor> edit)
    {
        await _lock.WaitAsync();
        try
        {
            // Changes in memory under the reads' lock; files written afterwards.
            var files = new Dictionary<string, string?>();
            lock (_servedLock)
            {
                var editor = new Editor(this);
                edit(editor);
                foreach (var path in editor.Touched)
                {
                    Prune(path);
                    if (_overlay.GetValueOrDefault(path) is not { Count: > 0 }) _overlay.Remove(path);
                    files[path] = _overlay.TryGetValue(path, out var overlay) ? Text(overlay) : null;
                }
                _served.Clear();
            }
            foreach (var (path, text) in files) await WriteOverlayAsync(path, text);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    /// <summary>Entry changes, applied by <see cref="EditAsync"/>.</summary>
    public sealed class Editor(CatalogStore store)
    {
        internal HashSet<string> Touched { get; } = new();

        public JsonObject Merged(string path) => store.Merged(path);

        public JsonNode? Base(string path, string collection, string key) => Find(store._base.GetValueOrDefault(path)?[collection], key);

        /// <summary>Replaces (or adds) an entry; written in its repository form.</summary>
        public void Set(string path, string collection, string key, JsonNode entry)
        {
            var overlay = store._overlay.TryGetValue(path, out var o) ? o : store._overlay[path] = new JsonObject();
            var value = Clean(entry.DeepClone())!;
            if (IsTexts(path))
            {
                var dict = overlay[collection] as JsonObject ?? (JsonObject)(overlay[collection] = new JsonObject());
                dict[key] = value;
            }
            else
            {
                var array = overlay[collection] as JsonArray ?? (JsonArray)(overlay[collection] = new JsonArray());
                var index = IndexOf(array, key);
                if (index >= 0) array[index] = value;
                else array.Add(value);
            }
            Touched.Add(path);
        }

        /// <summary>Removes the change of an entry: the repository's version comes back (or the entry disappears, if it is new).</summary>
        public void Revert(string path, string collection, string key)
        {
            if (store._overlay.GetValueOrDefault(path)?[collection] is not { } values) return;
            if (values is JsonArray array && IndexOf(array, key) is >= 0 and var i) array.RemoveAt(i);
            else if (values is JsonObject dict) dict.Remove(key);
            Touched.Add(path);
        }
    }

    // ---- Merge and filter ----

    private static JsonObject Merge(JsonObject baseDoc, JsonObject? overlay)
    {
        var merged = (JsonObject)baseDoc.DeepClone();
        if (overlay is null) return merged;
        foreach (var (name, values) in overlay)
        {
            if (values is JsonArray entries)
            {
                var target = merged[name] as JsonArray ?? (JsonArray)(merged[name] = new JsonArray());
                foreach (var entry in entries.OfType<JsonObject>())
                {
                    var index = IndexOf(target, KeyOf(entry));
                    if (index >= 0) target[index] = entry.DeepClone();
                    else target.Add(entry.DeepClone());
                }
            }
            else if (values is JsonObject dict)
            {
                var target = merged[name] as JsonObject ?? (JsonObject)(merged[name] = new JsonObject());
                foreach (var (key, value) in dict) target[key] = value?.DeepClone();
            }
        }
        return merged;
    }

    // Drafts removed for visitors: entries, texts, places in the factions.
    private void HideDrafts(string path, JsonObject doc)
    {
        var catalog = Merged(CatalogPath);
        var drafts = new Dictionary<string, HashSet<string>>
        {
            ["cogs"] = Drafts(catalog["cogs"]),
            ["equipment"] = Drafts(catalog["equipment"]),
        };
        if (path == CatalogPath)
            foreach (var (collection, ids) in drafts)
                if (doc[collection] is JsonArray array)
                    foreach (var entry in array.OfType<JsonObject>().Where(e => ids.Contains(KeyOf(e))).ToList()) array.Remove(entry);
        if (path == FactionsPath && doc["factions"] is JsonArray factions)
            foreach (var faction in factions.OfType<JsonObject>())
                if (faction["cogIds"] is JsonArray members)
                    foreach (var id in members.Where(m => m is not null && drafts["cogs"].Contains(m.GetValue<string>())).ToList()) members.Remove(id);
        if (path.StartsWith("i18n/"))
            foreach (var (collection, ids) in drafts)
                if (doc[collection] is JsonObject texts)
                    foreach (var id in ids) texts.Remove(id);
    }

    private static HashSet<string> Drafts(JsonNode? entries) =>
        (entries as JsonArray ?? []).OfType<JsonObject>()
            .Where(e => !(e["published"] is JsonValue v && v.TryGetValue<bool>(out var published) && published))
            .Select(KeyOf).ToHashSet();

    // Removes the changes identical to the repository; true if something changed.
    private bool Prune(string path)
    {
        if (!_overlay.TryGetValue(path, out var overlay)) return false;
        var baseDoc = _base.GetValueOrDefault(path);
        var changed = false;
        foreach (var (name, values) in overlay.ToList())
        {
            if (values is JsonArray array)
                foreach (var entry in array.OfType<JsonObject>().ToList())
                {
                    if (!JsonNode.DeepEquals(entry, Find(baseDoc?[name], KeyOf(entry)))) continue;
                    array.Remove(entry);
                    changed = true;
                }
            else if (values is JsonObject dict)
                foreach (var (key, value) in dict.ToList())
                {
                    if (!JsonNode.DeepEquals(value, Find(baseDoc?[name], key))) continue;
                    dict.Remove(key);
                    changed = true;
                }
            if (values is JsonArray { Count: 0 } or JsonObject { Count: 0 })
            {
                overlay.Remove(name);
                changed = true;
            }
        }
        return changed;
    }

    // ---- Helpers ----

    /// <summary>Texts file (dictionaries by id); the others are arrays of entries.</summary>
    public static bool IsTexts(string path) => path.StartsWith("i18n/", StringComparison.Ordinal);

    private static string KeyOf(JsonNode entry) =>
        entry["id"]?.GetValue<string>() ?? entry["code"]?.GetValue<string>() ?? "";

    private static int IndexOf(JsonArray array, string key)
    {
        for (var i = 0; i < array.Count; i++)
            if (array[i] is { } e && KeyOf(e) == key) return i;
        return -1;
    }

    private static JsonNode? Find(JsonNode? values, string key) => values switch
    {
        JsonArray array => IndexOf(array, key) is >= 0 and var i ? array[i] : null,
        JsonObject dict => dict[key],
        _ => null,
    };

    private static IEnumerable<string> Keys(JsonNode? values) => values switch
    {
        JsonArray array => array.OfType<JsonObject>().Select(KeyOf),
        JsonObject dict => dict.Select(kv => kv.Key),
        _ => [],
    };

    /// <summary>Entry in its repository form: no null values nor optional empty arrays.</summary>
    public static JsonNode? Clean(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var (name, value) in obj.ToList())
            {
                if (value is null || value is JsonArray { Count: 0 } && !KeptEmptyArrays.Contains(name)) obj.Remove(name);
                else Clean(value);
            }
        else if (node is JsonArray array)
            foreach (var item in array) Clean(item);
        return node;
    }

    public static string Text(JsonNode doc) => doc.ToJsonString(WriteOptions) + "\n";

    private string OverlayFile(string path) => Path.Combine(overlayDirectory, path.Replace('/', Path.DirectorySeparatorChar));

    // Changes file of a document; null: no more changes, file deleted.
    private async Task WriteOverlayAsync(string path, string? text)
    {
        var file = OverlayFile(path);
        if (text is null)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file + ".tmp", text, new UTF8Encoding(false));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}
