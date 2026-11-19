using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Olomoc.Accounts;
using Olomoc.Server.Accounts;
using Olomoc.Server.Hosting;
using Olomoc.Server.Tournaments;
using Olomoc.Server.Zones;
using Olomoc.Services;
using Olomoc.Tournaments;
using Olomoc.Tournaments.Stats;
using Olomoc.Zones;

namespace Olomoc.Server.Admin;

// Export of the data behind the statistics (/api/admin/export, editors and admins), to compute them
// again or to make other figures: the games of the finished and counted tournaments (archives) and
// of every zone (hidden ones included), anonymous like the archives (no player names, no comments).

/// <summary>A card of a cabal played: rank, cog and equipment/boon (catalog ids) and their names.</summary>
public sealed record ExportCard(int Rank, string Cog, string CogName, string? Equipment, string? EquipmentName);

/// <summary>
/// A player of a side: number in the tournament (registration order, from 1; null in a zone, where
/// players are not followed from one game to the next), faction, cabal played (if known).
/// </summary>
public sealed record ExportPlayer(int? Number, string Faction, string FactionName, IReadOnlyList<ExportCard>? Cabal);

public sealed record ExportSide(int Vp, IReadOnlyList<ExportPlayer> Players);

/// <summary>
/// A game: id ("T-{code}-{n}" for a tournament, "Z-{id}" for a zone), source ("tournament", "zone", or
/// "online": a zone game recorded from an online game), its tournament or zone, the tournament a zone
/// game was copied from (or the online game it was recorded from), the date, whether
/// it counts in the statistics of everything (/stats/: a zone game copied from an archived
/// tournament counts once, in the tournament; hidden zones never count; after a reset of the
/// statistics, only what was recorded afterwards), both sides, and if recorded:
/// P1, cause of the end, scenario and decks.
/// </summary>
public sealed record ExportGame(string Id, string Source, string Event, string? CopiedFrom, DateOnly? Date, GameFormat Format,
    bool Counted, ExportSide A, ExportSide B, TableSide? First, GameEnd? End, string? Scenario, string? Artifact, string? Ministry)
{
    public TableSide? Winner => A.Vp > B.Vp ? TableSide.A : B.Vp > A.Vp ? TableSide.B : null;
}

public sealed record ExportTournament(string Code, DateOnly? Date, DateTimeOffset FinishedAt, int CabalsPerPlayer, int Players, int Games);

public sealed record ExportZone(string Code, string Name, ZoneKind Kind, DateOnly? StartDate, DateOnly? EndDate, ZoneStatus Status,
    bool Hidden, DominanceMode Dominance, int Games);

public sealed record ExportData(DateTimeOffset ExportedAt, IReadOnlyList<ExportTournament> Tournaments, IReadOnlyList<ExportZone> Zones,
    IReadOnlyList<ExportGame> Games);

public static class DataExport
{
    public static void MapDataExport(this WebApplication app)
    {
        app.MapGet("/api/admin/export", Download).RequireAuthorization().AddEndpointFilter(Roles.Require(AccountRole.Editor))
            .RequireRateLimiting(TournamentEndpoints.ReadPolicy);
    }

    private static async Task<IResult> Download(HttpContext ctx, StatsArchive archive, ZoneStore zones, ZoneGameStore games,
        ServerCatalog catalog, AdminAudit audit, SiteSettingsStore settings)
    {
        var data = Build(DateTimeOffset.UtcNow, archive.CurrentArchives(), await zones.ListAsync(), await games.AllWithZoneAsync(),
            await catalog.GetAsync(), settings.Current.StatsSince);
        await audit.LogAsync(AdminEndpoints.AdminName(ctx), "export", "data", $"{data.Games.Count}");
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.File(Zip(data), "application/zip", $"olomoc-data-{data.ExportedAt:yyyy-MM-dd}.zip");
    }

    // ---- Data ----

    public static ExportData Build(DateTimeOffset now, StatsArchive.Archives archives, IReadOnlyList<Zone> zones,
        IReadOnlyList<(string Zone, StoredZoneGame Game)> zoneGames, CatalogService catalog, DateTimeOffset? since = null)
    {
        var names = new Names(catalog);
        var list = new List<ExportGame>();

        foreach (var t in archives.Tournaments.OrderBy(t => t.FinishedAt))
        {
            var players = t.Players.ToDictionary(p => p.Number);
            var date = t.Date ?? DateOnly.FromDateTime(t.FinishedAt.UtcDateTime);
            ExportSide Side(int number, int? cabal, int vp)
            {
                var p = players.GetValueOrDefault(number);
                var cards = p is not null && cabal is { } c && c >= 0 && c < p.Cabals.Count ? names.Cards(p.Cabals[c]) : null;
                return new(vp, [new ExportPlayer(number + 1, p?.Faction ?? "", names.Faction(p?.Faction), cards)]);
            }
            var counts = StatsReset.Counts(t.FinishedAt, since);
            list.AddRange(t.Games.Select((g, i) => new ExportGame($"T-{t.Code}-{i + 1}", "tournament", t.Code, null, date, GameFormat.Duel,
                counts, Side(g.A, g.CabalA, g.VpA), Side(g.B, g.CabalB, g.VpB), g.First, g.End, g.Scenario, g.Artifact, g.Ministry)));
        }

        var counted = zones.Where(z => !z.Hidden).Select(z => z.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (zone, stored) in zoneGames.OrderBy(x => x.Game.Id))
        {
            var g = stored.Game;
            ExportSide Side(ZoneSide s) => new(s.Vp, s.Players.Select(p => new ExportPlayer(null, p.Faction, names.Faction(p.Faction),
                p.Cabal is { } c ? names.Cards(c.Cogs.Select(x => new ArchivedSlot(x.Rank, x.Cog.Id, x.Equipment?.Id))) : null)).ToList());
            var inAll = counted.Contains(zone) && (stored.TournamentCode is not { } code || !archives.Codes.Contains(code))
                && StatsReset.Counts(stored.CreatedAt, since);
            list.Add(new ExportGame($"Z-{stored.Id}", stored.OnlineCode is null ? "zone" : "online", zone, stored.TournamentCode ?? stored.OnlineCode, g.PlayedOn, g.Format, inAll,
                Side(g.A), Side(g.B), g.First, g.End, g.Scenario?.Id, g.Scenario?.Artifact, g.Scenario?.Ministry));
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var perZone = zoneGames.GroupBy(x => x.Zone, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return new ExportData(now,
            archives.Tournaments.OrderBy(t => t.FinishedAt)
                .Select(t => new ExportTournament(t.Code, t.Date, t.FinishedAt, t.CabalsPerPlayer, t.Players.Count, t.Games.Count)).ToList(),
            zones.OrderBy(z => z.CreatedAt).Select(z => new ExportZone(z.Code, z.Name, z.Kind, z.StartDate, z.EndDate, z.StatusOn(today),
                z.Hidden, z.Dominance, perZone.GetValueOrDefault(z.Code))).ToList(),
            list);
    }

    // Names in the catalog's reference language (ids kept: they do not change).
    private sealed class Names(CatalogService catalog)
    {
        public string Faction(string? code) => code is null ? "" : catalog.Texts.FactionName(code).Text;

        public IReadOnlyList<ExportCard> Cards(IEnumerable<ArchivedSlot> slots) => slots
            .Select(s => new ExportCard(s.Rank, s.Cog, catalog.CogById(s.Cog) is { } c ? catalog.Texts.ReferenceName(c) : s.Cog,
                s.Equipment, s.Equipment is null ? null : catalog.EquipmentById(s.Equipment) is { } e ? catalog.Texts.ReferenceName(e) : s.Equipment))
            .ToList();
    }

    // ---- Files ----

    /// <summary>
    /// The archive: data.json (everything) and CSV tables for spreadsheets (UTF-8 with BOM, ";" as
    /// separator, as expected by a spreadsheet in French): tournaments, zones, games, players (one row
    /// per player of a game) and cards (one row per card of a cabal played).
    /// </summary>
    public static byte[] Zip(ExportData data)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "data.json", JsonSerializer.SerializeToUtf8Bytes(data, Json));
            foreach (var (name, rows) in Tables(data)) Add(zip, name, Csv(rows));
        }
        return buffer.ToArray();
    }

    private static readonly JsonSerializerOptions Json = new(TournamentJson.Options) { WriteIndented = true };

    public static IEnumerable<(string Name, IEnumerable<IReadOnlyList<object?>> Rows)> Tables(ExportData data)
    {
        yield return ("tournaments.csv", Prepend(["tournament", "date", "finished_at", "cabals_per_player", "players", "games"],
            data.Tournaments.Select(t => Row(t.Code, t.Date, t.FinishedAt, t.CabalsPerPlayer, t.Players, t.Games))));
        yield return ("zones.csv", Prepend(["zone", "name", "kind", "start", "end", "status", "hidden", "dominance", "games"],
            data.Zones.Select(z => Row(z.Code, z.Name, z.Kind, z.StartDate, z.EndDate, z.Status, z.Hidden, z.Dominance, z.Games))));
        yield return ("games.csv", Prepend(["game", "source", "event", "copied_from", "date", "format", "counted", "vp_a", "vp_b", "winner",
                "first", "end", "scenario", "artifact", "ministry"],
            data.Games.Select(g => Row(g.Id, g.Source, g.Event, g.CopiedFrom, g.Date, g.Format, g.Counted, g.A.Vp, g.B.Vp,
                g.Winner?.ToString() ?? "draw", g.First, g.End, g.Scenario, g.Artifact, g.Ministry))));
        yield return ("players.csv", Prepend(["game", "source", "event", "date", "counted", "side", "seat", "player", "faction", "faction_name",
                "vp", "vp_against", "result", "first", "opponents", "rank3_cog", "rank3_name"],
            Players(data).Select(x => Row(x.Game.Id, x.Game.Source, x.Game.Event, x.Game.Date, x.Game.Counted, x.Side, x.Seat,
                x.Player.Number, x.Player.Faction, x.Player.FactionName, x.Own.Vp, x.Other.Vp, Result(x.Own.Vp, x.Other.Vp),
                x.Game.First is { } f ? f == x.Side : (bool?)null, string.Join("+", x.Other.Players.Select(p => p.Faction)),
                x.Player.Cabal?.FirstOrDefault(c => c.Rank == 3)?.Cog, x.Player.Cabal?.FirstOrDefault(c => c.Rank == 3)?.CogName))));
        yield return ("cards.csv", Prepend(["game", "source", "event", "date", "counted", "side", "seat", "faction", "result",
                "rank", "cog", "cog_name", "equipment", "equipment_name"],
            Players(data).SelectMany(x => (x.Player.Cabal ?? []).Select(c => Row(x.Game.Id, x.Game.Source, x.Game.Event, x.Game.Date,
                x.Game.Counted, x.Side, x.Seat, x.Player.Faction, Result(x.Own.Vp, x.Other.Vp), c.Rank, c.Cog, c.CogName, c.Equipment,
                c.EquipmentName)))));
    }

    private sealed record PlayerRow(ExportGame Game, TableSide Side, int Seat, ExportPlayer Player, ExportSide Own, ExportSide Other);

    private static IEnumerable<PlayerRow> Players(ExportData data) => data.Games.SelectMany(g =>
        new[] { (Side: TableSide.A, Own: g.A, Other: g.B), (Side: TableSide.B, Own: g.B, Other: g.A) }
            .SelectMany(s => s.Own.Players.Select((p, i) => new PlayerRow(g, s.Side, i + 1, p, s.Own, s.Other))));

    private static string Result(int vp, int against) => vp > against ? "win" : vp < against ? "loss" : "draw";

    private static IReadOnlyList<object?> Row(params object?[] values) => values;

    private static IEnumerable<IReadOnlyList<object?>> Prepend(string[] header, IEnumerable<IReadOnlyList<object?>> rows) =>
        rows.Prepend(header);

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(content);
    }

    public const char Separator = ';';

    public static byte[] Csv(IEnumerable<IReadOnlyList<object?>> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows) text.AppendJoin(Separator, row.Select(Cell)).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    private static string Cell(object? value)
    {
        var text = value switch
        {
            null => "",
            bool b => b ? "1" : "0",
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return text.IndexOfAny([Separator, '"', '\n', '\r']) < 0 ? text : $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
