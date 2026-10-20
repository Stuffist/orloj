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
/// Référentiel servi au site : les fichiers JSON du dépôt (livrés dans l'image, base des PR)
/// et, par-dessus, les modifications faites dans l'éditeur, au même format, dans DATA_DIR/catalog (seules
/// les entrées modifiées ou créées). Fusion entrée par entrée (id ou code) :
/// <list type="bullet">
/// <item><c>data/olomoc-catalog.json</c> : rouages (cogs) et équipements/dons (equipment) ;</item>
/// <item><c>data/olomoc-factions.json</c> : factions (alignement, rouages membres) ;</item>
/// <item><c>i18n/&lt;langue&gt;/cards.json</c> : textes (cogs, equipment, factions, ministries).</item>
/// </list>
/// Une entrée sans <c>"published": true</c> (brouillon) n'est servie qu'aux éditeurs et aux admins : retirée pour les
/// autres, avec ses textes et sa place dans les factions. Au démarrage, une modification devenue
/// identique au dépôt (fichiers exportés puis recommités) est retirée de DATA_DIR/catalog.
/// </summary>
public sealed class CatalogStore(IFileProvider webRoot, string overlayDirectory, ILogger<CatalogStore> log)
{
    public const string ContentPrefix = "_content/Olomoc.Shared/";
    public const string CatalogPath = "data/olomoc-catalog.json", FactionsPath = "data/olomoc-factions.json";
    public static string CardsPath(string language) => $"i18n/{language}/cards.json";

    /// <summary>Écriture des fichiers : comme ceux du dépôt (indentation de 2, accents en clair, fin de ligne \n).</summary>
    public static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // Tableaux toujours écrits, même vides ; les autres, vides, sont omis (comme dans le dépôt).
    private static readonly HashSet<string> KeptEmptyArrays = ["abilities", "lifeLine", "ministries", "cogIds"];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, JsonObject> _base = new();
    private readonly Dictionary<string, JsonObject> _overlay = new();
    private readonly Dictionary<(string Path, bool Drafts), Served> _served = new();
    private readonly object _servedLock = new();
    private IReadOnlyList<string> _languages = [];

    public sealed record Served(byte[] Body, string ETag);

    /// <summary>Référentiel modifié (éditeur) : référentiel du serveur à recharger.</summary>
    public event Action? Changed;

    public IReadOnlyList<string> Languages => _languages;

    /// <summary>Fichiers fusionnés : référentiel, factions, textes de chaque langue.</summary>
    public IEnumerable<string> Paths => new[] { CatalogPath, FactionsPath }.Concat(_languages.Select(CardsPath));

    // ---- Chargement ----

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
            catch (JsonException e) { log.LogWarning(e, "Référentiel : {File} illisible, ignoré.", file); }
        }
        // Modifications intégrées au dépôt depuis : retirées.
        foreach (var path in _overlay.Keys.ToList())
            if (Prune(path))
            {
                log.LogInformation("Référentiel : modifications de {Path} déjà dans le dépôt, retirées de DATA_DIR/catalog.", path);
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

    // ---- Lecture ----

    /// <summary>Document fusionné (copie), brouillons compris.</summary>
    public JsonObject Merged(string path)
    {
        lock (_servedLock) return Merge(_base.GetValueOrDefault(path) ?? new JsonObject(), _overlay.GetValueOrDefault(path));
    }

    /// <summary>Fichier servi (brouillons compris pour un éditeur ou un admin), ou null s'il n'est pas du référentiel.</summary>
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

    /// <summary>Entrées modifiées (dans le dépôt) ou nouvelles, par collection : cogs, equipment, factions.</summary>
    public Dictionary<string, Dictionary<string, string>> Status()
    {
        lock (_servedLock) return StatusUnlocked();
    }

    private Dictionary<string, Dictionary<string, string>> StatusUnlocked()
    {
        // Entrée modifiée (dans son document ou ses textes) ; nouvelle si son document du dépôt ne l'a pas.
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

    /// <summary>Fichiers fusionnés complets (brouillons compris), aux chemins du dépôt : à recommiter.</summary>
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

    // ---- Modifications (éditeur) ----

    /// <summary>Applique des modifications au référentiel (sous verrou), puis les enregistre.</summary>
    public async Task EditAsync(Action<Editor> edit)
    {
        await _lock.WaitAsync();
        try
        {
            // Modifications en mémoire sous le verrou des lectures ; fichiers écrits ensuite.
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

    /// <summary>Modifications d'entrées, appliquées par <see cref="EditAsync"/>.</summary>
    public sealed class Editor(CatalogStore store)
    {
        internal HashSet<string> Touched { get; } = new();

        public JsonObject Merged(string path) => store.Merged(path);

        public JsonNode? Base(string path, string collection, string key) => Find(store._base.GetValueOrDefault(path)?[collection], key);

        /// <summary>Remplace (ou ajoute) une entrée ; écrite sous sa forme du dépôt.</summary>
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

        /// <summary>Retire la modification d'une entrée : celle du dépôt revient (ou l'entrée disparaît, si elle est nouvelle).</summary>
        public void Revert(string path, string collection, string key)
        {
            if (store._overlay.GetValueOrDefault(path)?[collection] is not { } values) return;
            if (values is JsonArray array && IndexOf(array, key) is >= 0 and var i) array.RemoveAt(i);
            else if (values is JsonObject dict) dict.Remove(key);
            Touched.Add(path);
        }
    }

    // ---- Fusion et filtre ----

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

    // Brouillons retirés pour les visiteurs : entrées, textes, places dans les factions.
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

    // Retire les modifications identiques au dépôt ; true si quelque chose a changé.
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

    // ---- Outils ----

    /// <summary>Fichier de textes (dictionnaires par id) ; les autres sont des tableaux d'entrées.</summary>
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

    /// <summary>Entrée sous sa forme du dépôt : sans valeurs nulles ni tableaux vides facultatifs.</summary>
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

    // Fichier des modifications d'un document ; null : plus aucune, fichier supprimé.
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
