using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Data;
using Olomoc.Server.Hosting;
using Olomoc.Tournaments;
using Olomoc.Zones;

namespace Olomoc.Server.Zones;

/// <summary>Zones: one JSON document per zone (see <see cref="DocumentStore{T}"/>), never purged.</summary>
public sealed class ZoneStore(IDbContextFactory<OlomocDb> db, int maxZones = ZoneRules.MaxZones)
    : DocumentStore<Zone>(db, Kind, maxZones, TournamentJson.Options)
{
    public const string Kind = "zone";
}

/// <summary>
/// A stored game: row id, game, author's account, entry token hash, date of entry; tournament it was
/// copied from (if so).
/// </summary>
public sealed record StoredZoneGame(long Id, ZoneGame Game, string? SubmitterId, string EditTokenHash, DateTimeOffset CreatedAt,
    string? TournamentCode = null);

/// <summary>Games of the zones (ZoneGames table), most recent first.</summary>
public sealed class ZoneGameStore(IDbContextFactory<OlomocDb> factory)
{
    public async Task<int> CountAsync(string zone)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ZoneGames.CountAsync(g => g.ZoneCode == zone);
    }

    /// <summary>Number of games per zone.</summary>
    public async Task<Dictionary<string, int>> CountsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ZoneGames.GroupBy(g => g.ZoneCode).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    /// <summary>The games of a zone (statistics), or of all zones (null).</summary>
    public async Task<List<StoredZoneGame>> AllAsync(string? zone)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.ZoneGames.AsNoTracking().Where(g => zone == null || g.ZoneCode == zone).ToListAsync();
        return rows.Select(Read).OfType<StoredZoneGame>().ToList();
    }

    /// <summary>Codes of the zones of each game id, for the overall statistics.</summary>
    public async Task<List<(string Zone, StoredZoneGame Game)>> AllWithZoneAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.ZoneGames.AsNoTracking().ToListAsync();
        return rows.Select(r => (r.ZoneCode, Game: Read(r))).Where(x => x.Game is not null).Select(x => (x.ZoneCode, x.Game!)).ToList();
    }

    /// <summary>Games with an id below <paramref name="before"/> (all if null), most recent first.</summary>
    public async Task<List<StoredZoneGame>> PageAsync(string zone, long? before, int take)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.ZoneGames.AsNoTracking()
            .Where(g => g.ZoneCode == zone && (before == null || g.Id < before))
            .OrderByDescending(g => g.Id).Take(take).ToListAsync();
        return rows.Select(Read).OfType<StoredZoneGame>().ToList();
    }

    public async Task<StoredZoneGame?> GetAsync(string zone, long id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ZoneGames.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id && g.ZoneCode == zone) is { } row ? Read(row) : null;
    }

    public async Task<long> AddAsync(string zone, ZoneGame game, string? submitterId, string editTokenHash)
    {
        await using var db = await factory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var row = new ZoneGameRow
        {
            ZoneCode = zone, PlayedOn = game.PlayedOn, CreatedAt = now, UpdatedAt = now,
            SubmitterId = submitterId, EditTokenHash = editTokenHash, Json = Write(game),
        };
        db.ZoneGames.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    public async Task<bool> UpdateAsync(string zone, long id, ZoneGame game)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ZoneGames.Where(g => g.Id == id && g.ZoneCode == zone).ExecuteUpdateAsync(s => s
            .SetProperty(g => g.Json, Write(game))
            .SetProperty(g => g.PlayedOn, game.PlayedOn)
            .SetProperty(g => g.UpdatedAt, DateTime.UtcNow)) > 0;
    }

    public async Task<bool> DeleteAsync(string zone, long id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ZoneGames.Where(g => g.Id == id && g.ZoneCode == zone).ExecuteDeleteAsync() > 0;
    }

    /// <summary>
    /// The games copied from a tournament, by table (key: round × 1000 + table): those of
    /// <paramref name="tournament"/> in the zone become <paramref name="games"/>, in a single
    /// transaction. A game already there keeps its row (and its place in the list) and is updated if
    /// it changed; the others are added or removed. Returns whether anything changed.
    /// </summary>
    public async Task<bool> SyncTournamentAsync(string zone, string tournament, IReadOnlyDictionary<int, ZoneGame> games)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var rows = await db.ZoneGames.Where(g => g.ZoneCode == zone && g.TournamentCode == tournament).ToListAsync();
        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var row in rows.Where(r => r.TournamentTable is not { } key || !games.ContainsKey(key)))
        {
            db.ZoneGames.Remove(row);
            changed = true;
        }
        foreach (var (key, game) in games)
        {
            var json = Write(game);
            if (rows.FirstOrDefault(r => r.TournamentTable == key) is { } row)
            {
                if (row.Json == json) continue;
                row.Json = json;
                row.PlayedOn = game.PlayedOn;
                row.UpdatedAt = now;
            }
            else
                db.ZoneGames.Add(new ZoneGameRow
                {
                    ZoneCode = zone, PlayedOn = game.PlayedOn, CreatedAt = now, UpdatedAt = now,
                    TournamentCode = tournament, TournamentTable = key, EditTokenHash = "", Json = json,
                });
            changed = true;
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return changed;
    }

    public async Task<int> DeleteZoneAsync(string zone)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ZoneGames.Where(g => g.ZoneCode == zone).ExecuteDeleteAsync();
    }

    private static string Write(ZoneGame game) => JsonSerializer.Serialize(game, TournamentJson.Options);

    private static StoredZoneGame? Read(ZoneGameRow row)
    {
        try
        {
            return JsonSerializer.Deserialize<ZoneGame>(row.Json, TournamentJson.Options) is { } game
                ? new StoredZoneGame(row.Id, game, row.SubmitterId, row.EditTokenHash,
                    new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)), row.TournamentCode)
                : null;
        }
        catch (JsonException) { return null; }       // unreadable game: ignored, left to be examined
    }
}
