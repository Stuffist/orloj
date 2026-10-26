using Microsoft.Data.Sqlite;

namespace Olomoc.Server.Data;

/// <summary>
/// Une fois par jour (vérifié au démarrage puis toutes les heures) : copie cohérente de la base dans
/// DATA_DIR/backups/olomoc-AAAAMMJJ.db (VACUUM INTO), les <see cref="Keep"/> plus récentes
/// gardées. Copier olomoc.db pendant que le serveur tourne ne donne pas une copie sûre ; ces
/// fichiers-là, si.
/// </summary>
public sealed class DbBackup(string dataDirectory, ILogger<DbBackup> log) : BackgroundService
{
    public const int Keep = 7;

    private string BackupDirectory => Path.Combine(dataDirectory, "backups");

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await BackupAsync(DateTime.UtcNow, stop); }
            catch (Exception e) when (e is SqliteException or IOException) { log.LogWarning(e, "Sauvegarde de la base interrompue."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    /// <summary>
    /// Sauvegarde du jour, si elle n'existe pas encore (ou remplacée : <paramref name="overwrite"/>,
    /// sauvegarde faite à la main) ; renvoie son chemin, ou null.
    /// </summary>
    public async Task<string?> BackupAsync(DateTime now, CancellationToken stop = default, bool overwrite = false)
    {
        await _gate.WaitAsync(stop);
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var path = Path.Combine(BackupDirectory, $"olomoc-{now:yyyyMMdd}.db");
            if (File.Exists(path) && !overwrite) return null;

            // Copie à part, puis à sa place : la sauvegarde du jour reste intacte si la copie échoue.
            var temporary = path + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);
            await using (var connection = new SqliteConnection(OlomocDb.ConnectionString(dataDirectory)))
            {
                await connection.OpenAsync(stop);
                await using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", temporary);
                await command.ExecuteNonQueryAsync(stop);
            }
            File.Move(temporary, path, overwrite: true);

            foreach (var old in Directory.EnumerateFiles(BackupDirectory, "olomoc-*.db").OrderDescending().Skip(Keep))
                File.Delete(old);
            log.LogInformation("Base sauvegardée dans {Path}.", path);
            return path;
        }
        finally { _gate.Release(); }
    }

    private readonly SemaphoreSlim _gate = new(1, 1);     // tâche de fond et sauvegarde à la main

    /// <summary>Sauvegardes présentes, la plus récente d'abord (heure de la dernière écriture, UTC).</summary>
    public IReadOnlyList<FileInfo> Backups() =>
        Directory.Exists(BackupDirectory)
            ? new DirectoryInfo(BackupDirectory).EnumerateFiles("olomoc-*.db").OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : [];
}
