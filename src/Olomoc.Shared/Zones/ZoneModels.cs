using Olomoc.Models;
using Olomoc.Tournaments;
using Olomoc.Tournaments.Stats;

namespace Olomoc.Zones;

// The Block: games played outside tournaments, recorded in zones (a club, an event, a
// period of the year, online). Each zone has its statistics and its dominance (Light or Darkness,
// and the leading faction of that alignment). Never a ranking of people: player names are optional,
// shown in the list of games, never counted.

/// <summary>Kind of zone: for the display only.</summary>
public enum ZoneKind { Club, Event, Period, Online }

/// <summary>How the dominance of a zone is decided: number of wins, win rate, VP scored.</summary>
public enum DominanceMode { Wins, WinRate, VictoryPoints }

/// <summary>Duel (1 v 1), or two teams of two players of the same alignment, Light against Darkness.</summary>
public enum GameFormat { Duel, Teams }

/// <summary>Upcoming (before its start date), open (games accepted), closed (by hand, or end date passed).</summary>
public enum ZoneStatus { Upcoming, Open, Closed }

/// <summary>Zone as stored by the server (one JSON document per zone).</summary>
public sealed class Zone : IStoredDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Place, schedule, contact… (plain text).</summary>
    public string Description { get; set; } = "";
    /// <summary>Link to an image (https), shown on the map and the zone page; never downloaded by the server.</summary>
    public string? Logo { get; set; }
    public ZoneKind Kind { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DominanceMode Dominance { get; set; }
    /// <summary>Closed by hand (reversible).</summary>
    public bool Closed { get; set; }
    /// <summary>Test zone: outside the list (reachable by its link) and the overall statistics.</summary>
    public bool Hidden { get; set; }
    /// <summary>
    /// Its hexagon on the map (index in <see cref="ZoneMapLayout.Slots"/>): given once, the most central
    /// free one, and kept whatever changes (a deleted zone frees it). Null: not given yet.
    /// </summary>
    public int? Slot { get; set; }
    /// <summary>Accounts (ids) that manage the zone, besides the admins.</summary>
    public List<string> ManagerIds { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ZoneStatus StatusOn(DateOnly today) =>
        Closed || EndDate is { } end && end < today ? ZoneStatus.Closed
        : StartDate is { } start && start > today ? ZoneStatus.Upcoming
        : ZoneStatus.Open;
}

/// <summary>A player: faction; optional: a name (never counted), the cabal played.</summary>
public sealed record ZonePlayer(string Faction, string? Name = null, CabalExport? Cabal = null);

/// <summary>A side: one player (duel) or two (team), and its VP.</summary>
public sealed record ZoneSide(IReadOnlyList<ZonePlayer> Players, int Vp);

/// <summary>
/// A game recorded in a zone: format, date, both sides; for the statistics (required when entered in
/// the Block, maybe missing from a tournament copy): the side that played first, the cause of the
/// end, the scenario (and decks); optional: a free comment (a notable event…).
/// </summary>
public sealed record ZoneGame(GameFormat Format, DateOnly PlayedOn, ZoneSide A, ZoneSide B,
    TableSide? First = null, GameEnd? End = null, Scenario? Scenario = null, string? Comment = null)
{
    public IEnumerable<ZonePlayer> Players => A.Players.Concat(B.Players);

    /// <summary>Winning side; null for a draw.</summary>
    public TableSide? Winner => A.Vp > B.Vp ? TableSide.A : B.Vp > A.Vp ? TableSide.B : null;

    /// <summary>For the statistics: factions and cards played, without the names.</summary>
    public StatGame ToStatGame() => new(Side(A), Side(B), First, End, Scenario?.Id, Scenario?.Artifact, Scenario?.Ministry);

    private static StatSide Side(ZoneSide s) => new(s.Players.Select(p => new StatPlayer(p.Faction,
        p.Cabal?.Cogs.Select(c => new ArchivedSlot(c.Rank, c.Cog.Id, c.Equipment?.Id)).ToList())).ToList(), s.Vp);
}
