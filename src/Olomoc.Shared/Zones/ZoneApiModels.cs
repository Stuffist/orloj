using Olomoc.Tournaments.Stats;

namespace Olomoc.Zones;

// Exchanges between the /zones/ pages and the /api/zones API (Olomoc.Server). JSON: TournamentJson.Options.

/// <summary>Header carrying the token of a game entered from this device.</summary>
public static class ZoneHeaders
{
    public const string Entry = "X-Olomoc-Entry";
}

/// <summary>
/// Zone in the list: its description, state, number of games, dominance, hexagon on the map; <see cref="CanManage"/>:
/// manager or admin.
/// </summary>
public sealed record ZoneSummary(string Code, string Name, string Description, ZoneKind Kind, DateOnly? StartDate, DateOnly? EndDate,
    ZoneStatus Status, bool Hidden, int Games, Dominance Dominance, bool CanManage, string? Logo = null, int? Slot = null);

/// <summary>
/// A zone, with its statistics and its dominance. <see cref="ZoneAdmin"/>: the requester administers
/// the zones (editor or admin). <see cref="Photos"/>: a photo per game is allowed (site setting).
/// <see cref="Managers"/> (usernames): for its
/// managers and the zone admins only.
/// </summary>
public sealed record ZoneView(string Code, string Name, string Description, ZoneKind Kind, DateOnly? StartDate, DateOnly? EndDate,
    ZoneStatus Status, DominanceMode Mode, bool Hidden, bool Closed, Dominance Dominance, StatsView Stats,
    bool CanManage, bool ZoneAdmin, IReadOnlyList<string>? Managers, string? Logo = null, bool Photos = false);

/// <summary>
/// A recorded game: id, content, date of entry. <see cref="CanEdit"/>: through the account (its
/// author while the zone is open, a manager, an admin); the device's entry tokens are checked by the
/// browser itself. <see cref="Tournament"/>: code of the tournament it was copied from (then changed
/// through the tournament only), and its name. <see cref="Photo"/>: it has a photo (shown while photos
/// are allowed).
/// </summary>
public sealed record ZoneGameView(long Id, ZoneGame Game, DateTimeOffset CreatedAt, bool CanEdit,
    string? Tournament = null, string? TournamentName = null, bool Photo = false);

/// <summary>A page of games, most recent first; <see cref="Next"/>: to ask for the following ones (before=).</summary>
public sealed record ZoneGamesPage(IReadOnlyList<ZoneGameView> Games, long? Next, int Total);

/// <summary>Entry token: returned only once, kept by the device to change or delete the game.</summary>
public sealed record ZoneGameCreated(long Id, string EditToken);

/// <summary>Settings changed by a manager (or an admin); <see cref="Logo"/>: link to an image (https).</summary>
public sealed record ZoneSettingsRequest(string Name, string? Description, ZoneKind Kind, DateOnly? StartDate, DateOnly? EndDate,
    bool Closed, string? Logo = null);

/// <summary>Creation or change by an admin: the settings, plus the dominance mode, the visibility, the managers (usernames).</summary>
public sealed record ZoneAdminRequest(string Name, string? Description, ZoneKind Kind, DateOnly? StartDate, DateOnly? EndDate,
    bool Closed, DominanceMode Dominance, bool Hidden, IReadOnlyList<string>? Managers, string? Logo = null);

public sealed record ZoneCreated(string Code);
