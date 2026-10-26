using Olomoc.Tournaments.Stats;

namespace Olomoc.Zones;

// Exchanges between the /zones/ pages and the /api/zones API (Olomoc.Server). JSON: TournamentJson.Options.

/// <summary>Header carrying the token of a game entered from this device.</summary>
public static class ZoneHeaders
{
    public const string Entry = "X-Olomoc-Entry";
}

/// <summary>Zone in the list: its state, number of games, dominance; <see cref="CanManage"/>: manager or admin.</summary>
public sealed record ZoneSummary(string Code, string Name, ZoneKind Kind, DateOnly? StartDate, DateOnly? EndDate,
    ZoneStatus Status, bool Hidden, int Games, Dominance Dominance, bool CanManage);

/// <summary>
/// A zone, with its statistics and its dominance. <see cref="Managers"/> (usernames): for its
/// managers and the admins only.
/// </summary>
public sealed record ZoneView(string Code, string Name, string Description, ZoneKind Kind, DateOnly? StartDate, DateOnly? EndDate,
    ZoneStatus Status, DominanceMode Mode, bool Hidden, bool Closed, Dominance Dominance, StatsView Stats,
    bool CanManage, bool IsAdmin, IReadOnlyList<string>? Managers);

/// <summary>
/// A recorded game: id, content, date of entry. <see cref="CanEdit"/>: through the account (its
/// author while the zone is open, a manager, an admin); the device's entry tokens are checked by the
/// browser itself.
/// </summary>
public sealed record ZoneGameView(long Id, ZoneGame Game, DateTimeOffset CreatedAt, bool CanEdit);

/// <summary>A page of games, most recent first; <see cref="Next"/>: to ask for the following ones (before=).</summary>
public sealed record ZoneGamesPage(IReadOnlyList<ZoneGameView> Games, long? Next, int Total);

/// <summary>Entry token: returned only once, kept by the device to change or delete the game.</summary>
public sealed record ZoneGameCreated(long Id, string EditToken);

/// <summary>Settings changed by a manager (or an admin).</summary>
public sealed record ZoneSettingsRequest(string Name, string? Description, ZoneKind Kind, DateOnly? StartDate, DateOnly? EndDate,
    bool Closed);

/// <summary>Creation or change by an admin: the settings, plus the dominance mode, the visibility, the managers (usernames).</summary>
public sealed record ZoneAdminRequest(string Name, string? Description, ZoneKind Kind, DateOnly? StartDate, DateOnly? EndDate,
    bool Closed, DominanceMode Dominance, bool Hidden, IReadOnlyList<string>? Managers);

public sealed record ZoneCreated(string Code);
