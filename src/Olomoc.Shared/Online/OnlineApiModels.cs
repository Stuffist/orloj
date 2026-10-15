using Olomoc.Models;
using Olomoc.Tournaments;

namespace Olomoc.Online;

public sealed record CreateOnlineRequest(string? Name = null);

/// <summary>Partie créée : son code, le jeton du créateur (siège A) et celui de l'invité (siège B).</summary>
public sealed record OnlineCreated(string Code, string Token, string InviteToken);

/// <summary>Partie ouverte depuis une table de tournoi (les joueurs s'y identifient avec leur jeton de joueur).</summary>
public sealed record OnlineOpened(string Code);

public enum OnlineCommandType
{
    SetName, SetCabal, SetReady,            // salon
    RollInitiative,                          // numéro des joueurs
    ChooseAgenda, DrawAgenda,                // agenda
    UpdateState, Undo, SetEndCycle,          // cycles
}

/// <summary>
/// Commande d'un joueur. Text : nom, agenda ; Flag : prêt, fin de cycle ; State et BasedOn : nouvel
/// état de son camp et la révision dont il part ; Note : compte rendu pour le journal.
/// </summary>
public sealed record OnlineCommand(
    OnlineCommandType Type,
    string? Text = null,
    CabalExport? Cabal = null,
    bool? Flag = null,
    GameState? State = null,
    int? BasedOn = null,
    string? Note = null);

/// <summary>Réponse à Join (hub) : la vue de la partie, ou le problème qui empêche de la rejoindre (notFound, forbidden).</summary>
public sealed record OnlineJoin(OnlineView? View, string? Error = null);

/// <summary>Réponse à une commande : problèmes (aucun si acceptée), révision de l'état de son camp.</summary>
public sealed record OnlineResult(IReadOnlyList<Problem> Problems, int? StateRevision = null)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// La partie telle que la voit une connexion. Me : son siège (null : spectateur). Avant que les
/// deux joueurs soient prêts, la cabale de l'autre siège n'est pas envoyée (composition secrète,
/// LDR p.32).
/// </summary>
public sealed record OnlineView(
    string Code,
    int Revision,
    OnlineStep Step,
    int Cycle,
    int? First,
    Scenario? Agenda,
    int? Me,
    IReadOnlyList<SeatView> Seats,
    OnlineTournamentRef? Tournament,
    IReadOnlyList<OnlineLogEntry> Log)
{
    public SeatView? Mine => Me is { } i ? Seats[i] : null;
    public SeatView? Other => Me is { } i ? Seats[1 - i] : null;
    public bool Over => Step == OnlineStep.Finished || Seats.Any(s => s.Over);
}

/// <summary>Un siège vu par la connexion. Cabal et State : null s'ils sont encore secrets (ou absents).</summary>
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
