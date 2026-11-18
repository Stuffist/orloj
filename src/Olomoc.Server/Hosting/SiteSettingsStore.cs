using System.Text.Json;
using Olomoc.Accounts;
using Olomoc.Tournaments;

namespace Olomoc.Server.Hosting;

/// <summary>
/// Site settings changed in the admin panel (<see cref="SiteSettings"/>): DATA_DIR/settings.json,
/// read once and kept in memory. Missing or unreadable file: the defaults (everything off).
/// </summary>
public sealed class SiteSettingsStore(string dataDirectory, ILogger<SiteSettingsStore> logger)
{
    private readonly string _path = Path.Combine(dataDirectory, "settings.json");
    private readonly object _lock = new();
    private SiteSettings? _current;

    public SiteSettings Current
    {
        get
        {
            lock (_lock) return _current ??= Read();
        }
    }

    public void Set(SiteSettings settings)
    {
        lock (_lock)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, TournamentJson.Options));
            File.Move(temp, _path, overwrite: true);
            _current = settings;
        }
    }

    private SiteSettings Read()
    {
        if (!File.Exists(_path)) return new SiteSettings();
        try { return JsonSerializer.Deserialize<SiteSettings>(File.ReadAllText(_path), TournamentJson.Options) ?? new SiteSettings(); }
        catch (Exception e) when (e is JsonException or IOException)
        {
            logger.LogWarning(e, "Unreadable site settings {Path}: defaults used.", _path);
            return new SiteSettings();
        }
    }
}
