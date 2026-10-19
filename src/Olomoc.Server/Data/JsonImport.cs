using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Tournaments;

namespace Olomoc.Server.Data;

/// <summary>
/// Reprise des données d'avant la base (un fichier JSON par document) : au démarrage, chaque
/// dossier présent est importé dans la base, puis renommé « .imported » (gardé comme sauvegarde,
/// jamais réimporté). Les fichiers illisibles sont signalés et restent dans ce dossier.
/// </summary>
public static class JsonImport
{
    /// <summary>Dossiers de DATA_DIR et sorte de document correspondante.</summary>
    public static readonly IReadOnlyList<(string Folder, string Kind)> Folders =
    [
        ("tournaments", TournamentStore.Kind),
        ("stats", StatsArchive.Kind),
    ];

    public const string ImportedSuffix = ".imported";

    /// <summary>Importe les dossiers présents ; renvoie le nombre de documents ajoutés.</summary>
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
                    log.LogWarning("Import : {File} illisible, laissé dans {Folder}{Suffix}.", Path.GetFileName(file), folder, ImportedSuffix);
                    continue;
                }
                row.Kind = kind;
                row.Code = code;
                db.Documents.Add(row);
                count++;
            }
            await db.SaveChangesAsync();          // une transaction par dossier

            Directory.Move(directory, FreeName(directory + ImportedSuffix));
            log.LogInformation("Import : {Count} document(s) de {Folder} ajouté(s) à la base.", count, folder);
            added += count;
        }
        return added;
    }

    // Contenu JSON valide ; dates du document (createdAt, updatedAt), à défaut celle du fichier.
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

    // Dossier déjà importé une fois (restauré puis réimporté) : suffixe horodaté.
    private static string FreeName(string path) =>
        Directory.Exists(path) ? $"{path}-{DateTime.UtcNow:yyyyMMddHHmmss}" : path;
}
