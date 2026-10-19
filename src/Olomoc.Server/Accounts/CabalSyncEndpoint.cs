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
/// Cabales du compte (POST /api/account/cabals/sync) : l'appareil envoie ses changements
/// (sauvegardes, suppressions), le serveur applique ceux plus récents que ce qu'il connaît pour
/// ce nom et renvoie toutes les cabales du compte. Sans changement : simple lecture.
/// </summary>
public static class CabalSyncEndpoint
{
    /// <summary>Taille au plus d'une cabale (JSON) : trois rouages et leurs équipements pèsent ~1 Ko.</summary>
    public const int MaxCabalJson = 16 * 1024;

    // Une synchronisation à la fois par compte (deux appareils en même temps : pas de doublon).
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
                if (!CabalSyncRules.IsNewer(change.At, row is null ? null : Utc(row.SavedAt))) continue;      // le compte a plus récent

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
                    // Suppression d'une cabale inconnue gardée aussi : une sauvegarde plus ancienne, venue
                    // d'un autre appareil, ne la fera pas réapparaître.
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

    // Forme d'une cabale du constructeur : trois rouages au plus, chacun identifié.
    private static bool Valid(CabalExport cabal) =>
        cabal.Cogs is { Count: <= 3 } cogs && cogs.All(s => s?.Cog?.Id is { Length: > 0 });

    private static CabalExport? Read(string json)
    {
        try { return JsonSerializer.Deserialize<CabalExport>(json, TournamentJson.Options); }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
