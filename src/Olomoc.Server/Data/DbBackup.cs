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

    /// <summary>Sauvegarde du jour, si elle n'existe pas encore ; renvoie son chemin, ou null.</summary>
    public async Task<string?> BackupAsync(DateTime now, CancellationToken stop = default)
    {
        Directory.CreateDirectory(BackupDirectory);
        var path = Path.Combine(BackupDirectory, $"olomoc-{now:yyyyMMdd}.db");
        if (File.Exists(path)) return null;

        await using (var connection = new SqliteConnection(OlomocDb.ConnectionString(dataDirectory)))
        {
            await connection.OpenAsync(stop);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path;";
            command.Parameters.AddWithValue("$path", path);
            await command.ExecuteNonQueryAsync(stop);
        }

        foreach (var old in Directory.EnumerateFiles(BackupDirectory, "olomoc-*.db").OrderDescending().Skip(Keep))
            File.Delete(old);
        log.LogInformation("Base sauvegardée dans {Path}.", path);
        return path;
    }
}
