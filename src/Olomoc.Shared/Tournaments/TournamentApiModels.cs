using System.Text.Json;
using System.Text.Json.Serialization;
using Olomoc.Models;

namespace Olomoc.Tournaments;

// Exchanges between the /pairings/ app and the /api/tournaments API (Olomoc.Server).

/// <summary>JSON format shared by the server (API, files) and the client: camelCase, enums as text.</summary>
public static class TournamentJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }
}

/// <summary>Headers carrying the tokens (organizer, player).</summary>
public static class TournamentHeaders
{
    public const string Organizer = "X-Olomoc-Organizer";
    public const string Player = "X-Olomoc-Player";
}

/// <summary>
/// Optional password: gives admin access on another device, without the link. Optional zone (code):
/// the results count for it. <see cref="AutoRounds"/>: <see cref="RoundCount"/> ignored, deduced from the
/// players.
/// </summary>
public sealed record CreateTournamentRequest(
    string Name, DateOnly? Date, int RoundCount, int CabalsPerPlayer,
    string? Password = null, bool ExcludeFromStats = false, string? Zone = null, EventFormat Format = EventFormat.Tournament,
    bool AutoRounds = false, bool HideStandings = false);

/// <summary>Organizer token: returned only once, to be kept by the organizer.</summary>
public sealed record TournamentCreated(string Code, string OrganizerToken);

public sealed record RegistrationRequest(string Name, string Faction, List<CabalExport> Cabals);

/// <summary>Player token: to change or withdraw one's registration while registrations are open.</summary>
public sealed record PlayerRegistered(string PlayerId, string PlayerToken);

/// <summary>
/// VP of each player and cabal played (index), required with two cabals per player; optional: P1,
/// cause of the end of the game.
/// </summary>
public sealed record ResultRequest(int? VpA, int? VpB, int? CabalA = null, int? CabalB = null,
    TableSide? First = null, GameEnd? End = null);

/// <summary>Scenario of each round (index = round number − 1), null if not chosen yet.</summary>
public sealed record ScenariosRequest(List<Scenario?> Scenarios);

public sealed record StatsRequest(bool Exclude);

public sealed record StandingsRequest(bool Hidden);

public sealed record PasswordRequest(string? Password);

/// <summary>Organizer token opened with the password, specific to the device.</summary>
public sealed record OrganizerLogin(string OrganizerToken);

public sealed record DroppedRequest(bool Dropped);

/// <summary>Two players of the current round to swap (correction of a pairing).</summary>
public sealed record SwapRequest(string First, string Second);

public sealed record ProblemList(IReadOnlyList<Problem> Problems);

/// <summary>Paired round; compromises made for lack of anything better: rematches, mirror matches.</summary>
public sealed record PairingSummary(int Round, int Rematches, int Mirrors);

/// <summary>
/// Tournament as the requester sees it: lists hidden while registrations are open.
/// <see cref="HasPassword"/>: admin access can be opened with a password. <see cref="RoundCount"/>: the
/// planned rounds (auto while registrations are open: from the players registered so far).
/// <see cref="StandingsHidden"/>: <see cref="Standings"/> empty for everyone but the organizer.
/// </summary>
public sealed record TournamentView(
    string Code, string Name, DateOnly? Date, int RoundCount, int CabalsPerPlayer,
    TournamentPhase Phase,
    IReadOnlyList<PlayerView> Players, IReadOnlyList<Round> Rounds, IReadOnlyList<Standing> Standings,
    bool IsOrganizer, string? MyPlayerId, bool HasPassword, bool ExcludeFromStats = false,
    IReadOnlyList<Scenario?>? Scenarios = null, string? Zone = null, string? ZoneName = null,
    EventFormat Format = EventFormat.Tournament, bool AutoRounds = false, bool StandingsHidden = false)
{
    public PlayerView? Player(string? id) => Players.FirstOrDefault(p => p.Id == id);
    public Scenario? ScenarioOf(int roundNumber) => Scenarios?.ElementAtOrDefault(roundNumber - 1);
    public Round? LastRound => Rounds.Count == 0 ? null : Rounds[^1];
}

/// <summary>Player; <see cref="Cabals"/> null while they are hidden from the requester.</summary>
public sealed record PlayerView(string Id, string Name, string Faction, bool Dropped, IReadOnlyList<CabalExport>? Cabals);
