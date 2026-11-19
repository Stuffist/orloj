using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Zones;

/// <summary>
/// Rules of the zones: settings, and games entered (checked in the browser for the display, then
/// again by the server before saving). Problems: keys "pairings.problem.*" of the texts.
/// </summary>
public static class ZoneRules
{
    public const int MaxNameLength = TournamentRules.MaxNameLength, MaxDescriptionLength = 1000;
    public const int MaxZones = 500, MaxGamesPerZone = 10_000, MaxManagers = 10;
    public const int MaxLogoLength = 500, MaxCommentLength = 500;

    /// <summary>
    /// Photo of a game: JPEG, PNG or WebP, 5 MB at most. The browser sends it reduced to
    /// <see cref="PhotoMaxSide"/> pixels and re-encoded in JPEG (location and other metadata dropped).
    /// </summary>
    public const int MaxPhotoBytes = 5 * 1024 * 1024, PhotoMaxSide = 1600;

    /// <summary>Type of an image from its first bytes: "image/jpeg", "image/png", "image/webp", or null.</summary>
    public static string? PhotoType(ReadOnlySpan<byte> head) =>
        head is [0xFF, 0xD8, 0xFF, ..] ? "image/jpeg"
        : head is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] ? "image/png"
        : head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    public static int PlayersPerSide(GameFormat format) => format == GameFormat.Teams ? 2 : 1;

    public static IReadOnlyList<Problem> CheckSettings(string? name, string? description, DateOnly? start, DateOnly? end,
        string? logo = null)
    {
        var problems = new List<Problem>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength) problems.Add(Problem.Of("zoneName", MaxNameLength.ToString()));
        if ((description?.Length ?? 0) > MaxDescriptionLength) problems.Add(Problem.Of("zoneDescription", MaxDescriptionLength.ToString()));
        if (start is { } s && end is { } e && e < s) problems.Add(Problem.Of("zoneDates"));
        if (!string.IsNullOrWhiteSpace(logo) && CleanLogo(logo) is null) problems.Add(Problem.Of("zoneLogo", MaxLogoLength.ToString()));
        return problems;
    }

    /// <summary>
    /// Logo of a zone: an absolute https link to an image (the server stores the link only, never the
    /// image), without credentials; null when empty or invalid.
    /// </summary>
    public static string? CleanLogo(string? logo)
    {
        var text = logo?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLogoLength) return null;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0 && uri.Host.Length > 0 ? text : null;
    }

    /// <summary>
    /// A game entered in the zone: the zone open (except for its managers, <paramref name="manager"/>),
    /// a date in its period and not in the future, the format's number of players, known factions,
    /// VP, and required for the statistics: the legal cabal of each player, the scenario and its two
    /// decks, the side that played first, the end of the game. Names stay optional (never counted), like the comment. Games copied from a
    /// tournament do not come through here (they keep what the tournament recorded).
    /// Returns the cleaned game (names and comment trimmed, empty ones removed), or the problems.
    /// </summary>
    public static (ZoneGame? Clean, IReadOnlyList<Problem> Problems) CheckGame(ZoneGame? game, Zone zone, CatalogService catalog,
        DateOnly today, bool manager)
    {
        var problems = new List<Problem>();
        if (game is null) return (null, [Problem.Of("zoneGame")]);
        if (!manager)
        {
            var status = zone.StatusOn(today);
            if (status == ZoneStatus.Closed) return (null, [Problem.Of("zoneClosed")]);
            if (status == ZoneStatus.Upcoming) return (null, [Problem.Of("zoneUpcoming", Date(zone.StartDate))]);
        }

        // One day of margin: a game entered just after midnight, in a time zone ahead of the server.
        if (game.PlayedOn > today.AddDays(1)) problems.Add(Problem.Of("playedOnFuture"));
        else if (zone.StartDate is { } start && game.PlayedOn < start || zone.EndDate is { } end && game.PlayedOn > end)
            problems.Add(Problem.Of("playedOnOutside", Date(zone.StartDate), Date(zone.EndDate)));

        var perSide = PlayersPerSide(game.Format);
        if (game.A?.Players is not { } a || game.B?.Players is not { } b || a.Count != perSide || b.Count != perSide)
        {
            problems.Add(Problem.Of("zonePlayers"));
            return (null, problems);
        }
        if (game.A.Vp is < 0 or > TournamentRules.MaxVictoryPoints || game.B.Vp is < 0 or > TournamentRules.MaxVictoryPoints)
            problems.Add(Problem.Of("zoneVictoryPoints", TournamentRules.MaxVictoryPoints.ToString()));

        var players = a.Concat(b).ToList();
        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p?.Faction is null || catalog.AllFactions.All(f => f.Code != p.Faction))
            {
                problems.Add(Problem.Of("zoneFaction", (i + 1).ToString()));
                continue;
            }
            if ((p.Name?.Trim().Length ?? 0) > MaxNameLength) problems.Add(Problem.Of("zonePlayerName", MaxNameLength.ToString()));
            if (p.Cabal is { } cabal) problems.AddRange(TournamentRules.CheckCabal(catalog, p.Faction, cabal, i));
            else problems.Add(Problem.Of("zoneCabal", (i + 1).ToString()));
        }

        // Required: the scenario and both its decks, the side that played first, the end of the game.
        Scenario? scenario = null;
        if (game.Scenario is not { } s) problems.Add(Problem.Of("zoneScenario"));
        else
        {
            var (clean, problem) = TournamentRules.CheckScenario(s);
            if (problem is not null) problems.Add(problem);
            else if (s.Artifact is null || s.Ministry is null) problems.Add(Problem.Of("zoneDecks"));
            scenario = clean;
        }
        if (game.First is not { } first) problems.Add(Problem.Of("zoneFirst"));
        else if (!Enum.IsDefined(first)) problems.Add(Problem.Of("zoneGame"));
        if (game.End is not { } gameEnd) problems.Add(Problem.Of("zoneEnd"));
        else if (!Enum.IsDefined(gameEnd)) problems.Add(Problem.Of("zoneGame"));
        var comment = game.Comment?.Trim() is { Length: > 0 } c ? c : null;
        if (comment?.Length > MaxCommentLength) problems.Add(Problem.Of("zoneComment", MaxCommentLength.ToString()));

        if (problems.Count > 0) return (null, problems);
        return (game with
        {
            A = Clean(game.A),
            B = Clean(game.B),
            Scenario = scenario,
            Comment = comment,
        }, problems);
    }

    private static ZoneSide Clean(ZoneSide side) =>
        side with { Players = side.Players.Select(p => p with { Name = p.Name?.Trim() is { Length: > 0 } n ? n : null }).ToList() };

    private static string Date(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "";
}
