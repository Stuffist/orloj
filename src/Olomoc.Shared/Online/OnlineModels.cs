using Olomoc.Models;
using Olomoc.Tournaments;

namespace Olomoc.Online;

// Partie en ligne telle qu'elle est enregistrée par le serveur (un fichier JSON par partie).
// Le serveur fait autorité : les joueurs envoient des commandes (OnlineCommand), vérifiées par
// OnlineRules selon l'étape et le siège ; chacun ne reçoit que sa vue (OnlineView). Les jetons
// ne sont conservés que sous forme de hash.

/// <summary>
/// Étapes guidées de la partie : salon (cabales, secrètes jusqu'à ce que les deux soient prêts),
/// mise en place (LDR p.31 : numéro des joueurs, agenda), cycles, fin.
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
    /// <summary>Augmente à chaque modification : les clients relisent leur vue quand elle change.</summary>
    public int Revision { get; set; }
    public OnlineStep Step { get; set; } = OnlineStep.Lobby;
    /// <summary>Cycle commun (1 à 4 ; 5 : partie terminée), recopié dans l'état de chaque camp.</summary>
    public int Cycle { get; set; } = 1;
    /// <summary>Siège du joueur 1 (J1), une fois l'initiative tirée.</summary>
    public int? First { get; set; }
    public Scenario? Agenda { get; set; }
    public List<Seat> Seats { get; set; } = new();
    /// <summary>Table de tournoi d'où la partie a été ouverte.</summary>
    public OnlineTournamentRef? Tournament { get; set; }
    public List<OnlineLogEntry> Log { get; set; } = new();
}

public sealed class Seat
{
    public string Name { get; set; } = "";
    /// <summary>Jeton du siège (partie libre) ; vide pour une partie de tournoi.</summary>
    public string TokenHash { get; set; } = "";
    /// <summary>Joueur du tournoi qui occupe le siège : il s'identifie avec son jeton de joueur.</summary>
    public string? TournamentPlayerId { get; set; }
    /// <summary>Cabales permises (inscrites au tournoi) ; vide : n'importe quelle cabale légale.</summary>
    public List<CabalExport> AllowedCabals { get; set; } = new();
    public CabalExport? Cabal { get; set; }
    public bool Ready { get; set; }
    /// <summary>Dés de l'initiative (5 D6), une fois lancés.</summary>
    public List<int>? Initiative { get; set; }
    /// <summary>Suivi du camp (créé au lancement), modifié par son joueur.</summary>
    public GameState? State { get; set; }
    /// <summary>Augmente à chaque changement de <see cref="State"/> : une mise à jour doit partir de la dernière.</summary>
    public int StateRevision { get; set; }
    /// <summary>États précédents (JSON), pour annuler ; vidé à chaque fin de cycle.</summary>
    public List<string> Undo { get; set; } = new();
    public bool EndCycleReady { get; set; }
}

public sealed record OnlineTournamentRef(string Code, int Round, int Table);

/// <summary>Ligne du journal : texte de l'interface (clé « online.log.* ») et ses arguments ; Seat : qui.</summary>
public sealed record OnlineLogEntry(DateTimeOffset At, int? Seat, string Key, IReadOnlyList<string>? Args = null);
