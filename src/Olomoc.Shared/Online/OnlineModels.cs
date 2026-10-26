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

public sealed record OnlineTournamentRef(string Code, int Round, int Table);

/// <summary>Log line: interface text (key "online.log.*") and its arguments; Seat: who.</summary>
public sealed record OnlineLogEntry(DateTimeOffset At, int? Seat, string Key, IReadOnlyList<string>? Args = null);
