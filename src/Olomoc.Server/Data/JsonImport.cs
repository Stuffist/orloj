using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Tournaments;

namespace Olomoc.Server.Data;

/// <summary>
/// Import of the data from before the database (one JSON file per document): at startup, each
/// existing folder is imported into the database, then renamed ".imported" (kept as a backup,
/// never imported again). Unreadable files are reported and stay in that folder.
/// </summary>
public static class JsonImport
{
    /// <summary>DATA_DIR folders and the matching document kind.</summary>
    public static readonly IReadOnlyList<(string Folder, string Kind)> Folders =
    [
        ("tournaments", TournamentStore.Kind),
        ("stats", StatsArchive.Kind),
    ];

    public const string ImportedSuffix = ".imported";

    /// <summary>Imports the existing folders; returns the number of documents added.</summary>
    public static async Task<int> RunAsync(IDbContextFactory<OlomocDb> factory, string dataDirectory, ILogger log)
    {
        var added = 0;
        foreach (var (folder, kind) in Folders)
        {
            var directory = Path.Combine(dataDirectory, folder);
            if (!Directory.Exists(directory)) continue;

            await using var db = await factory.CreateDbContextAsync();
            var existing = (await db.Documents.Where(d => d.Kind == kind).Select(d => d.Code).ToListAsync()).ToHashSet();
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var code = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
                if (TournamentStore.Normalize(code) is null || existing.Contains(code)) continue;
                if (Read(file) is not { } row)
                {
                    log.LogWarning("Import: {File} unreadable, left in {Folder}{Suffix}.", Path.GetFileName(file), folder, ImportedSuffix);
                    continue;
                }
                row.Kind = kind;
                row.Code = code;
                db.Documents.Add(row);
                count++;
            }
            await db.SaveChangesAsync();          // one transaction per folder

            Directory.Move(directory, FreeName(directory + ImportedSuffix));
            log.LogInformation("Import: {Count} document(s) from {Folder} added to the database.", count, folder);
            added += count;
        }
        return added;
    }

    // Valid JSON content; dates of the document (createdAt, updatedAt), otherwise the file's date.
    private static StoredDocumentRow? Read(string file)
    {
        try
        {
            var json = File.ReadAllText(file);
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) return null;
            var modified = File.GetLastWriteTimeUtc(file);
            return new StoredDocumentRow
            {
                CreatedAt = Date(parsed.RootElement, "createdAt") ?? modified,
                UpdatedAt = Date(parsed.RootElement, "updatedAt") ?? modified,
                Json = json,
            };
        }
        catch (JsonException) { return null; }
    }

    private static DateTime? Date(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var date) && date != default
            ? date.UtcDateTime : null;

    // Folder already imported once (restored then imported again): timestamped suffix.
    private static string FreeName(string path) =>
        Directory.Exists(path) ? $"{path}-{DateTime.UtcNow:yyyyMMddHHmmss}" : path;
}
