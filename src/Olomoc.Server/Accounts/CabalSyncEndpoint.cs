using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Olomoc.Accounts;
using Olomoc.Models;
using Olomoc.Server.Data;
using Olomoc.Tournaments;

namespace Olomoc.Server.Accounts;

/// <summary>
/// Account cabals (POST /api/account/cabals/sync): the device sends its changes (saves,
/// deletions), the server applies those newer than what it knows for that name and returns all
/// the account's cabals. Without changes: a plain read.
/// </summary>
public static class CabalSyncEndpoint
{
    /// <summary>Maximum size of a cabal (JSON): three cogs and their equipment weigh ~1 KB.</summary>
    public const int MaxCabalJson = 16 * 1024;

    // One synchronization at a time per account (two devices at once: no duplicates).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public static async Task<IResult> Sync(CabalSyncRequest body, HttpContext ctx, UserManager<OlomocUser> users, IDbContextFactory<OlomocDb> factory)
    {
        if (await users.GetUserAsync(ctx.User) is not { } user) return Results.Unauthorized();
        var changes = body.Changes ?? [];
        if (changes.Count > CabalSyncRules.MaxChanges)
            return Results.Json(new ProblemList([Problem.Of("tooManyChanges", CabalSyncRules.MaxChanges.ToString())]), TournamentJson.Options, statusCode: 400);

        var gate = Locks.GetOrAdd(user.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var rows = await db.SavedCabals.Where(c => c.UserId == user.Id).ToListAsync();
            var applied = 0;
            var problems = new List<Problem>();
            foreach (var change in changes)
            {
                var name = change?.Name?.Trim();
                if (change is null || string.IsNullOrEmpty(name) || name.Length > CabalSyncRules.MaxNameLength)
                {
                    problems.Add(Problem.Of("cabalInvalid", name ?? ""));
                    continue;
                }
                var key = CabalSyncRules.Key(name);
                var row = rows.FirstOrDefault(r => r.NormalizedName == key);
                if (!CabalSyncRules.IsNewer(change.At, row is null ? null : Utc(row.SavedAt))) continue;      // the account has a newer one

                string? json = null;
                if (change.Cabal is { } cabal)
                {
                    json = Valid(cabal) ? JsonSerializer.Serialize(cabal with { Name = name }, TournamentJson.Options) : null;
                    if (json is null || json.Length > MaxCabalJson)
                    {
                        problems.Add(Problem.Of("cabalInvalid", name));
                        continue;
                    }
                    if ((row is null || row.Deleted) && rows.Count(r => !r.Deleted) >= CabalSyncRules.MaxCabals)
                    {
                        problems.Add(Problem.Of("cabalLimit", CabalSyncRules.MaxCabals.ToString(), name));
                        continue;
                    }
                }
                if (row is null)
                {
                    // Deletion of an unknown cabal kept too: an older save coming from another device
                    // will not bring it back.
                    row = new SavedCabalRow { UserId = user.Id, NormalizedName = key };
                    db.SavedCabals.Add(row);
                    rows.Add(row);
                }
                row.Name = name;
                row.SavedAt = change.At.UtcDateTime;
                row.Deleted = json is null;
                row.Json = json;
                applied++;
            }
            if (applied > 0) await db.SaveChangesAsync();

            var cabals = rows.Where(r => !r.Deleted && r.Json is not null)
                .OrderByDescending(r => r.SavedAt)
                .Select(r => Read(r.Json!) is { } c ? new SavedCabal(Utc(r.SavedAt), c) : null)
                .OfType<SavedCabal>()
                .ToList();
            return Results.Json(new CabalSyncResult(cabals, applied, problems), TournamentJson.Options);
        }
        finally { gate.Release(); }
    }

    // Shape of a builder cabal: at most three cogs, each one identified.
    private static bool Valid(CabalExport cabal) =>
        cabal.Cogs is { Count: <= 3 } cogs && cogs.All(s => s?.Cog?.Id is { Length: > 0 });

    private static CabalExport? Read(string json)
    {
        try { return JsonSerializer.Deserialize<CabalExport>(json, TournamentJson.Options); }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
