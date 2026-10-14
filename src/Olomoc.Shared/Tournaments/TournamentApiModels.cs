using System.Text.Json;
using System.Text.Json.Serialization;
using Olomoc.Models;

namespace Olomoc.Tournaments;

// Échanges entre l'app /pairings/ et l'API /api/tournaments (Olomoc.Server).

/// <summary>Format JSON commun au serveur (API, fichiers) et au client : camelCase, énumérations en texte.</summary>
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

/// <summary>En-têtes porteurs des jetons (organisateur, joueur).</summary>
public static class TournamentHeaders
{
    public const string Organizer = "X-Olomoc-Organizer";
    public const string Player = "X-Olomoc-Player";
}

/// <summary>Mot de passe facultatif : donne l'administration sur un autre appareil, sans le lien.</summary>
public sealed record CreateTournamentRequest(
    string Name, DateOnly? Date, int RoundCount, int CabalsPerPlayer, PairingMode Pairing, bool AvoidSameAlignment,
    string? Password = null, bool ExcludeFromStats = false);

/// <summary>Jeton organisateur : renvoyé une seule fois, à garder par l'organisateur.</summary>
public sealed record TournamentCreated(string Code, string OrganizerToken);

public sealed record RegistrationRequest(string Name, string Faction, List<CabalExport> Cabals);

/// <summary>Jeton joueur : pour modifier ou retirer son inscription tant qu'elles sont ouvertes.</summary>
public sealed record PlayerRegistered(string PlayerId, string PlayerToken);

/// <summary>PV de chacun et cabale jouée (index), exigée avec deux cabales par joueur.</summary>
public sealed record ResultRequest(int? VpA, int? VpB, int? CabalA = null, int? CabalB = null);

public sealed record StatsRequest(bool Exclude);

public sealed record PasswordRequest(string? Password);

/// <summary>Jeton organisateur ouvert par le mot de passe, propre à l'appareil.</summary>
public sealed record OrganizerLogin(string OrganizerToken);

public sealed record DroppedRequest(bool Dropped);

public sealed record ProblemList(IReadOnlyList<Problem> Problems);

public sealed record PairingSummary(int Round, int Rematches, int SameAlignment);

/// <summary>
/// Tournoi tel que le voit le demandeur : listes masquées tant que les inscriptions sont ouvertes.
/// <see cref="HasPassword"/> : l'administration peut s'ouvrir par mot de passe.
/// </summary>
public sealed record TournamentView(
    string Code, string Name, DateOnly? Date, int RoundCount, int CabalsPerPlayer,
    PairingMode Pairing, bool AvoidSameAlignment, TournamentPhase Phase,
    IReadOnlyList<PlayerView> Players, IReadOnlyList<Round> Rounds, IReadOnlyList<Standing> Standings,
    bool IsOrganizer, string? MyPlayerId, bool HasPassword, bool ExcludeFromStats = false)
{
    public PlayerView? Player(string? id) => Players.FirstOrDefault(p => p.Id == id);
    public Round? LastRound => Rounds.Count == 0 ? null : Rounds[^1];
}

/// <summary>Joueur ; <see cref="Cabals"/> null tant qu'elles sont masquées au demandeur.</summary>
public sealed record PlayerView(string Id, string Name, string Faction, bool Dropped, IReadOnlyList<CabalExport>? Cabals);
