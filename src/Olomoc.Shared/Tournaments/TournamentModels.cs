using Olomoc.Models;

namespace Olomoc.Tournaments;

// Tournoi tel qu'il est enregistré par le serveur (un document JSON par tournoi, dans la base).
// Les joueurs sont désignés par leur Id ; les secrets (jetons) ne sont conservés que sous forme
// de hash.
// Feuille de ronde officielle : une faction et deux cabales par joueur, points de tournoi
// 0/1/3, différence de PV cumulée.

public enum PairingMode { Swiss, Alignment }

/// <summary>Inscriptions ouvertes → tournoi lancé (inscriptions closes, listes révélées) → terminé.</summary>
public enum TournamentPhase { Registration, Started, Finished }

public sealed class Tournament : IStoredDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DateOnly? Date { get; set; }
    public int RoundCount { get; set; } = 3;            // rondes prévues
    public int CabalsPerPlayer { get; set; } = 2;
    public PairingMode Pairing { get; set; } = PairingMode.Swiss;
    public bool AvoidSameAlignment { get; set; }        // ronde suisse : préférer les alignements opposés
    public bool ExcludeFromStats { get; set; }          // tournoi d'essai : hors statistiques
    public TournamentPhase Phase { get; set; } = TournamentPhase.Registration;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string OrganizerTokenHash { get; set; } = "";             // jeton du lien d'administration
    public string OrganizerPasswordHash { get; set; } = "";          // mot de passe facultatif (PBKDF2)
    public List<string> OrganizerSessionHashes { get; set; } = new(); // jetons ouverts par mot de passe
    public List<Player> Players { get; set; } = new();
    public List<Round> Rounds { get; set; } = new();
    /// <summary>Scénario prévu pour chaque ronde (index = n° de ronde − 1) ; null : pas encore choisi.</summary>
    public List<Scenario?> Scenarios { get; set; } = new();

    public Player? PlayerById(string id) => Players.FirstOrDefault(p => p.Id == id);
    public Scenario? ScenarioOf(int roundNumber) => Scenarios.ElementAtOrDefault(roundNumber - 1);
    public Round? LastRound => Rounds.Count == 0 ? null : Rounds[^1];
    public IEnumerable<Player> ActivePlayers => Players.Where(p => !p.Dropped);
}

public sealed class Player
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Faction { get; set; } = "";
    public List<CabalExport> Cabals { get; set; } = new();
    public bool Dropped { get; set; }                   // abandon : n'est plus apparié, reste au classement
    public DateTimeOffset RegisteredAt { get; set; }
    public string TokenHash { get; set; } = "";
}

public sealed class Round
{
    public int Number { get; set; }
    public List<Table> Tables { get; set; } = new();

    public bool IsComplete => Tables.All(t => t.HasResult);
    public bool HasAnyResult => Tables.Any(t => t.IsBye is false && (t.VpA.HasValue || t.VpB.HasValue));
}

/// <summary>
/// Une table : PlayerA contre PlayerB (null : exempt, « bye »), PV de chacun une fois saisis, et
/// la cabale que chacun a jouée (index dans ses cabales inscrites) ; pour les statistiques, si
/// l'organisateur les a notés : qui était J1, et ce qui a mis fin à la partie.
/// </summary>
public sealed class Table
{
    public int Number { get; set; }
    public string PlayerA { get; set; } = "";
    public string? PlayerB { get; set; }
    public int? VpA { get; set; }
    public int? VpB { get; set; }
    public int? CabalA { get; set; }                    // cabale jouée (index), pour les statistiques
    public int? CabalB { get; set; }
    public TableSide? First { get; set; }               // J1
    public GameEnd? End { get; set; }
    /// <summary>Partie en ligne ouverte pour cette table (code), s'il y en a une.</summary>
    public string? OnlineCode { get; set; }

    public bool IsBye => PlayerB is null;
    public bool HasResult => IsBye || (VpA.HasValue && VpB.HasValue);
    public bool Involves(string playerId) => PlayerA == playerId || PlayerB == playerId;
}
