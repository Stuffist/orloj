using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Online;

/// <summary>
/// New free game: creator's name, and their cabal if they already have one (local game opened
/// online): their seat is then ready right away.
/// </summary>
public sealed record CreateOnlineRequest(string? Name = null, CabalExport? Cabal = null);

/// <summary>Game created: its code, the creator's token (seat A) and the guest's (seat B).</summary>
public sealed record OnlineCreated(string Code, string Token, string InviteToken);

/// <summary>Game opened from a tournament table (the players identify with their player token).</summary>
public sealed record OnlineOpened(string Code);

public enum OnlineCommandType
{
    SetName, SetCabal, SetReady,            // lobby
    RollInitiative,                          // player numbers
    ChooseAgenda, DrawAgenda,                // agenda
    UpdateState, Undo, SetEndCycle,          // cycles
}

/// <summary>
/// A player's command. Text: name, agenda; Flag: ready, end of cycle; State and BasedOn: new state
/// of their side and the revision it starts from; Note: report for the log.
/// </summary>
public sealed record OnlineCommand(
    OnlineCommandType Type,
    string? Text = null,
    CabalExport? Cabal = null,
    bool? Flag = null,
    GameState? State = null,
    int? BasedOn = null,
    string? Note = null);

/// <summary>Answer to Join (hub): the game's view, or the problem that prevents joining it (notFound, forbidden).</summary>
public sealed record OnlineJoin(OnlineView? View, string? Error = null);

/// <summary>Answer to a command: problems (none if accepted), revision of the state of one's side.</summary>
public sealed record OnlineResult(IReadOnlyList<Problem> Problems, int? StateRevision = null)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// The game as a connection sees it. Me: its seat (null: spectator). Until both players are
/// ready, the other seat's cabal is not sent (secret composition, rulebook p.32).
/// </summary>
public sealed record OnlineView(
    string Code,
    int Revision,
    OnlineStep Step,
    int Cycle,
    int? First,
    Scenario? Agenda,
    int Hunt,                       // hunt shared by both players (OnlineRules.Hunt)
    int? Me,
    IReadOnlyList<SeatView> Seats,
    OnlineTournamentRef? Tournament,
    IReadOnlyList<OnlineLogEntry> Log)
{
    public SeatView? Mine => Me is { } i ? Seats[i] : null;
    public SeatView? Other => Me is { } i ? Seats[1 - i] : null;
    public bool Over => Step == OnlineStep.Finished || Seats.Any(s => s.Over) || Hunt >= GameSession.MaxHunt;
}

/// <summary>A seat as seen by the connection. Cabal and State: null while still secret (or missing).</summary>
public sealed record SeatView(
    string Name,
    bool Connected,
    bool HasCabal,
    bool Ready,
    string? Faction,
    CabalExport? Cabal,
    IReadOnlyList<CabalExport> AllowedCabals,
    IReadOnlyList<int>? Initiative,
    GameState? State,
    int StateRevision,
    bool CanUndo,
    bool EndCycleReady,
    bool Over);
