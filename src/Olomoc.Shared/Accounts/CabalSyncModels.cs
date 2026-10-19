using Olomoc.Models;
using Olomoc.Tournaments;

namespace Olomoc.Accounts;

// Cabales du compte (/api/account/cabals/sync) : chaque appareil garde sa copie et envoie ses
// changements ; à nom égal, le plus récent l'emporte (sauvegarde ou suppression).

/// <summary>Limites des cabales d'un compte.</summary>
public static class CabalSyncRules
{
    /// <summary>Cabales au plus par compte.</summary>
    public const int MaxCabals = 200;

    /// <summary>Changements au plus par appel (le client en envoie <see cref="ChunkSize"/>).</summary>
    public const int MaxChanges = 20, ChunkSize = 10;

    public const int MaxNameLength = 80;

    /// <summary>Clé d'un nom : sans tenir compte de la casse ni des espaces autour.</summary>
    public static string Key(string name) => name.Trim().ToUpperInvariant();

    /// <summary>Un changement s'applique s'il est plus récent que ce que le serveur connaît pour ce nom.</summary>
    public static bool IsNewer(DateTimeOffset change, DateTimeOffset? known) => known is not { } k || change > k;
}

/// <summary>Sauvegarde (<see cref="Cabal"/> donnée) ou suppression (null) d'une cabale, à une date.</summary>
public sealed record CabalChange(string Name, DateTimeOffset At, CabalExport? Cabal);

public sealed record CabalSyncRequest(IReadOnlyList<CabalChange> Changes);

/// <summary>
/// Cabales du compte après les changements (la plus récente d'abord), nombre de changements
/// appliqués, et problèmes des changements refusés (args : nom de la cabale…).
/// </summary>
public sealed record CabalSyncResult(IReadOnlyList<SavedCabal> Cabals, int Applied, IReadOnlyList<Problem> Problems);
