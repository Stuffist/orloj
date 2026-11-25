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
    ChooseAgenda,                            // agenda (Flag: drawn at random, its decks drawn with it)
    UpdateState, Undo, SetEndCycle,          // cycles
    SetBoard, AddPiece, MovePiece, RemovePiece, SetAuras, SetMagnet, SetLock, SetClaim,   // table (from the initiative on)
    RollDice, RerollDice, SetDiceThreshold,  // shared dice roller (from the initiative on)
    SetDecks, SetUp,                         // the decks of the scenario, then « Placer + distribuer » (P1)
    Mechanism, ReshuffleArtifact, IndraSwap, // the Artefact (Indra: her cards taken at the setup)
    SetPlanning, RevealPhase,                // the planning (Decks: the 5 slots)
    ConfirmResult,                           // end of the game
    Restart,                                 // back to the lobby (names and cabals kept)
}

/// <summary>
/// A player's command. Text: name, agenda, board, ref of a new piece; Flag: ready, end of cycle, magnet, lock, claim; State
/// and BasedOn: new state of their side and the revision it starts from; Note: report for the log.
/// Table: Piece (kind of a new piece), Side (the side a new cog or equipment token belongs to, the side
/// claiming an objective), PieceId,
/// X and Y (mm), Rotation.
/// Auras: ranges shown around a piece (inches, 1 to 9; empty: none). Vp: victory points of each seat, for the result.
/// Dice: Text (slot "action" or "reaction"), Count (D6), Threshold (2 to 5).
/// Agenda: Text (agenda; Flag: drawn). Decks: [Artifact, Ministry] of the scenario (Flag: drawn at
/// random). SetUp: Text (the variant to place).
/// </summary>
public sealed record OnlineCommand(
    OnlineCommandType Type,
    string? Text = null,
    CabalExport? Cabal = null,
    bool? Flag = null,
    GameState? State = null,
    int? BasedOn = null,
    string? Note = null,
    PieceKind? Piece = null,
    int? PieceId = null,
    double? X = null,
    double? Y = null,
    int? Rotation = null,
    IReadOnlyList<int>? Vp = null,
    IReadOnlyList<int>? Auras = null,
    int? Side = null,
    int? Count = null,
    int? Threshold = null,
    IReadOnlyList<string?>? Decks = null);

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
    IReadOnlyList<OnlineLogEntry> Log,
    OnlineTable? Table = null,           // nothing secret on a table: the same for everyone
    OnlineGameResult? Result = null,
    IReadOnlyDictionary<string, OnlineDiceRoll>? Dice = null,
    OnlineDecksView? Decks = null,
    OnlinePlanningView? Planning = null)
{
    public SeatView? Mine => Me is { } i ? Seats[i] : null;
    public SeatView? Other => Me is { } i ? Seats[1 - i] : null;
    public bool Over => Step == OnlineStep.Finished || Seats.Any(s => s.Over) || Hunt >= GameSession.MaxHunt;
}

/// <summary>
/// The decks as a connection sees them (DeckRules.View): its ministry hand, each seat's hand size, the
/// fallen ministry, the Artefact pile's size, its discard, the card drawn; its Indra cards, and how
/// many each seat holds.
/// The ministry hands are public (each seat's, and the ministries it played).
/// </summary>
public sealed record OnlineDecksView(string? MinistryDeck, string? ArtifactDeck, IReadOnlyList<string> MyHand, IReadOnlyList<int> HandSizes,
    string? Fallen, int PileSize, IReadOnlyList<string> Discard, string? Drawn, int Mechanisms,
    IReadOnlyList<string> MyIndra, IReadOnlyList<int> IndraCounts,
    IReadOnlyList<IReadOnlyList<string>> Hands, IReadOnlyList<IReadOnlyList<string>> Played);

/// <summary>
/// The planning as a connection sees it (PlanningRules.View): its hand and its own slots; of each seat, the
/// cards revealed and their ranks, the phases revealed, whose planning is complete.
/// </summary>
public sealed record OnlinePlanningView(IReadOnlyList<string> MyHand, IReadOnlyList<string?> MySlots,
    IReadOnlyList<IReadOnlyList<string?>> Revealed, int Phase, IReadOnlyList<bool> Complete, IReadOnlyList<IReadOnlyList<int>> Ranks);

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
