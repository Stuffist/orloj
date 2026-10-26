using Olomoc.Models;

namespace Olomoc.Tournaments;

// Tournament as stored by the server (one JSON document per tournament, in the database).
// Players are referred to by their Id; secrets (tokens) are only kept as hashes.
// Official round sheet: one faction and two cabals per player, tournament points 0/1/3,
// cumulative VP difference.

public enum PairingMode { Swiss, Alignment }

/// <summary>Registrations open → tournament started (registrations closed, lists revealed) → finished.</summary>
public enum TournamentPhase { Registration, Started, Finished }

public sealed class Tournament : IStoredDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DateOnly? Date { get; set; }
    public int RoundCount { get; set; } = 3;            // planned rounds
    public int CabalsPerPlayer { get; set; } = 2;
    public PairingMode Pairing { get; set; } = PairingMode.Swiss;
    public bool AvoidSameAlignment { get; set; }        // Swiss round: prefer opposite alignments
    public bool ExcludeFromStats { get; set; }          // test tournament: outside the statistics
    public TournamentPhase Phase { get; set; } = TournamentPhase.Registration;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string OrganizerTokenHash { get; set; } = "";             // token of the admin link
    public string OrganizerPasswordHash { get; set; } = "";          // optional password (PBKDF2)
    public List<string> OrganizerSessionHashes { get; set; } = new(); // tokens opened with the password
    public List<Player> Players { get; set; } = new();
    public List<Round> Rounds { get; set; } = new();
    /// <summary>Scenario planned for each round (index = round number − 1); null: not chosen yet.</summary>
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
    public bool Dropped { get; set; }                   // dropped: no longer paired, stays in the standings
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
/// A table: PlayerA against PlayerB (null: bye), VP of each once entered, and the cabal each one
/// played (index among their registered cabals); for the statistics, if the organizer recorded
/// them: who was P1, and what ended the game.
/// </summary>
public sealed class Table
{
    public int Number { get; set; }
    public string PlayerA { get; set; } = "";
    public string? PlayerB { get; set; }
    public int? VpA { get; set; }
    public int? VpB { get; set; }
    public int? CabalA { get; set; }                    // cabal played (index), for the statistics
    public int? CabalB { get; set; }
    public TableSide? First { get; set; }               // P1
    public GameEnd? End { get; set; }
    /// <summary>Online game opened for this table (code), if there is one.</summary>
    public string? OnlineCode { get; set; }

    public bool IsBye => PlayerB is null;
    public bool HasResult => IsBye || (VpA.HasValue && VpB.HasValue);
    public bool Involves(string playerId) => PlayerA == playerId || PlayerB == playerId;
}
