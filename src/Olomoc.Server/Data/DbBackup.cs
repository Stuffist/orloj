using Microsoft.Data.Sqlite;

namespace Olomoc.Server.Data;

/// <summary>
/// Once a day (checked at startup, then every hour): consistent copy of the database to
/// DATA_DIR/backups/olomoc-YYYYMMDD.db (VACUUM INTO), the <see cref="Keep"/> most recent ones
/// kept. Copying olomoc.db while the server runs does not give a safe copy; these files do.
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
            catch (Exception e) when (e is SqliteException or IOException) { log.LogWarning(e, "Database backup interrupted."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    /// <summary>
    /// Today's backup, if it does not exist yet (or replaced: <paramref name="overwrite"/>, backup
    /// made by hand); returns its path, or null.
    /// </summary>
    public async Task<string?> BackupAsync(DateTime now, CancellationToken stop = default, bool overwrite = false)
    {
        await _gate.WaitAsync(stop);
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var path = Path.Combine(BackupDirectory, $"olomoc-{now:yyyyMMdd}.db");
            if (File.Exists(path) && !overwrite) return null;

            // Copy aside, then moved into place: today's backup stays intact if the copy fails.
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
            log.LogInformation("Database backed up to {Path}.", path);
            return path;
        }
        finally { _gate.Release(); }
    }

    private readonly SemaphoreSlim _gate = new(1, 1);     // background task and backup made by hand

    /// <summary>Existing backups, most recent first (last write time, UTC).</summary>
    public IReadOnlyList<FileInfo> Backups() =>
        Directory.Exists(BackupDirectory)
            ? new DirectoryInfo(BackupDirectory).EnumerateFiles("olomoc-*.db").OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : [];
}
