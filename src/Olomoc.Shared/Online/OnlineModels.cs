using Olomoc.Models;
using Olomoc.Tournaments;

namespace Olomoc.Online;

// Online game as stored by the server (one JSON document per game). The server is the authority:
// players send commands (OnlineCommand), checked by OnlineRules according to the step and the
// seat; each one only receives their view (OnlineView). Tokens are only kept as hashes.

/// <summary>
/// Guided steps of the game: lobby (cabals, secret until both are ready), setup (rulebook p.31:
/// player numbers, agenda), cycles, end.
/// </summary>
public enum OnlineStep { Lobby, Initiative, Agenda, Cycle, Finished }

public sealed class OnlineGame : IStoredDocument
{
    public const int CurrentVersion = 1;
    public const int SeatCount = 2;

    public int Version { get; set; } = CurrentVersion;
    public string Code { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Increases with each change: clients re-read their view when it changes.</summary>
    public int Revision { get; set; }
    public OnlineStep Step { get; set; } = OnlineStep.Lobby;
    /// <summary>Shared cycle (1 to 4; 5: game over), copied into the state of each side.</summary>
    public int Cycle { get; set; } = 1;
    /// <summary>Seat of player 1 (P1), once the initiative is rolled.</summary>
    public int? First { get; set; }
    public Scenario? Agenda { get; set; }
    public List<Seat> Seats { get; set; } = new();
    /// <summary>Tournament table the game was opened from.</summary>
    public OnlineTournamentRef? Tournament { get; set; }
    /// <summary>Board and pieces (null: lobby, or a game stored before the table).</summary>
    public OnlineTable? Table { get; set; }
    /// <summary>Result confirmed at the end of the game.</summary>
    public OnlineGameResult? Result { get; set; }
    public List<OnlineLogEntry> Log { get; set; } = new();
}

public sealed class Seat
{
    public string Name { get; set; } = "";
    /// <summary>Seat token (free game); empty for a tournament game.</summary>
    public string TokenHash { get; set; } = "";
    /// <summary>Tournament player holding the seat: they identify with their player token.</summary>
    public string? TournamentPlayerId { get; set; }
    /// <summary>Allowed cabals (registered for the tournament); empty: any legal cabal.</summary>
    public List<CabalExport> AllowedCabals { get; set; } = new();
    public CabalExport? Cabal { get; set; }
    public bool Ready { get; set; }
    /// <summary>Initiative dice (5 D6), once rolled.</summary>
    public List<int>? Initiative { get; set; }
    /// <summary>Tracking of the side (created at the start), changed by its player.</summary>
    public GameState? State { get; set; }
    /// <summary>Increases with each change of <see cref="State"/>: an update must start from the latest.</summary>
    public int StateRevision { get; set; }
    /// <summary>Previous states (JSON), to undo; emptied at each end of cycle.</summary>
    public List<string> Undo { get; set; } = new();
    public bool EndCycleReady { get; set; }
}

/// <summary>Kind of a piece of the table: a cog, an equipment/boon token, terrain, a marker, a movement template.</summary>
public enum PieceKind { Cog, Equipment, Terrain, Marker, Template }

/// <summary>
/// A piece of the table. Ref: cog or equipment id (card catalog), terrain or marker id
/// (<see cref="TableManifest"/>), template id. Seat: the side a cog's or an equipment token belongs to
/// (null: terrain, markers, templates). X and Y: centre, in mm from the top-left corner of the board
/// (both null: a cog put aside in a game stored before the tokens, never shown). Rotation: degrees, clockwise. Auras: ranges shown around it.
/// </summary>
public sealed class TablePiece
{
    public int Id { get; set; }
    public PieceKind Kind { get; set; }
    public string Ref { get; set; } = "";
    public int? Seat { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public int Rotation { get; set; }
    /// <summary>Ranges shown around the piece, in inches from the edge of its base (1 to 9), seen by everyone.</summary>
    public List<int> Auras { get; set; } = new();
    /// <summary>
    /// A magnetic movement template: put down near a base, it comes to touch it; a base dropped near it
    /// comes to touch it too (the gestures do it, in the browser).
    /// </summary>
    public bool Magnet { get; set; }

    public bool OnBoard => X is not null && Y is not null;
}

/// <summary>The table: board (game mat), pieces. Created when both players are ready (cabals known).</summary>
public sealed class OnlineTable
{
    public string Board { get; set; } = "";
    public List<TablePiece> Pieces { get; set; } = new();
    public int NextId { get; set; } = 1;
}

/// <summary>
/// Result of the game: VP of each seat, cause of the end; confirmed by a player (ConfirmedBy: their seat),
/// or taken from the sheets when a finished game nobody confirmed is purged (null). Recorded: copied
/// into the statistics (zone of the Block receiving online games, or the tournament table).
/// </summary>
public sealed record OnlineGameResult(IReadOnlyList<int> Vp, GameEnd End, int? ConfirmedBy, DateTimeOffset ConfirmedAt, bool Recorded = false);

public sealed record OnlineTournamentRef(string Code, int Round, int Table);

/// <summary>Log line: interface text (key "online.log.*") and its arguments; Seat: who.</summary>
public sealed record OnlineLogEntry(DateTimeOffset At, int? Seat, string Key, IReadOnlyList<string>? Args = null);
