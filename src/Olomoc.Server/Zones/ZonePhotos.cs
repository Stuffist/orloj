using Olomoc.Zones;

namespace Olomoc.Server.Zones;

/// <summary>
/// Photos of the games of the Block: one file per game, DATA_DIR/photos/{zone}/{game id}.{jpg|png|webp}.
/// The type is read from the file's first bytes (never trusted from the request); 5 MB at most.
/// Deleted with their game or their zone.
/// </summary>
public sealed class ZonePhotos(string dataDirectory)
{
    private static readonly (string Type, string Extension)[] Types = [("image/jpeg", ".jpg"), ("image/png", ".png"), ("image/webp", ".webp")];

    private readonly string _root = Path.Combine(dataDirectory, "photos");

    // Zone codes and game ids only: no path from the request.
    private string Folder(string zone) => Path.Combine(_root, zone.ToUpperInvariant());

    private string File(string zone, long id, string extension) => Path.Combine(Folder(zone), id.ToString() + extension);

    /// <summary>The game's photo: its file and type, or null.</summary>
    public (string Path, string Type)? Find(string zone, long id)
    {
        foreach (var (type, extension) in Types)
            if (System.IO.File.Exists(File(zone, id, extension))) return (File(zone, id, extension), type);
        return null;
    }

    /// <summary>
    /// Saves the photo (replacing the previous one). Returns the problem key: "photoTooLarge" (over
    /// <see cref="ZoneRules.MaxPhotoBytes"/>), "photoType" (not a JPEG, PNG or WebP image), or null.
    /// </summary>
    public async Task<string?> SaveAsync(string zone, long id, Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > ZoneRules.MaxPhotoBytes) return "photoTooLarge";
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        if (ZoneRules.PhotoType(bytes) is not { } type) return "photoType";
        Directory.CreateDirectory(Folder(zone));
        Delete(zone, id);
        var path = File(zone, id, Types.First(t => t.Type == type).Extension);
        await System.IO.File.WriteAllBytesAsync(path, bytes, ct);
        return null;
    }

    public void Delete(string zone, long id)
    {
        foreach (var (_, extension) in Types)
        {
            var path = File(zone, id, extension);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    public void DeleteZone(string zone)
    {
        if (Directory.Exists(Folder(zone))) Directory.Delete(Folder(zone), recursive: true);
    }
}
