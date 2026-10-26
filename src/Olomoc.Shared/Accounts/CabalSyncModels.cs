using Olomoc.Models;
using Olomoc.Tournaments;

namespace Olomoc.Accounts;

// Account cabals (/api/account/cabals/sync): each device keeps its copy and sends its changes;
// for the same name, the most recent one wins (save or deletion).

/// <summary>Limits of an account's cabals.</summary>
public static class CabalSyncRules
{
    /// <summary>Maximum cabals per account.</summary>
    public const int MaxCabals = 200;

    /// <summary>Maximum changes per call (the client sends <see cref="ChunkSize"/>).</summary>
    public const int MaxChanges = 20, ChunkSize = 10;

    public const int MaxNameLength = 80;

    /// <summary>Key of a name: case-insensitive, surrounding spaces ignored.</summary>
    public static string Key(string name) => name.Trim().ToUpperInvariant();

    /// <summary>A change applies if it is more recent than what the server knows for that name.</summary>
    public static bool IsNewer(DateTimeOffset change, DateTimeOffset? known) => known is not { } k || change > k;
}

/// <summary>Save (<see cref="Cabal"/> given) or deletion (null) of a cabal, at a date.</summary>
public sealed record CabalChange(string Name, DateTimeOffset At, CabalExport? Cabal);

public sealed record CabalSyncRequest(IReadOnlyList<CabalChange> Changes);

/// <summary>
/// Account cabals after the changes (most recent first), number of changes applied, and problems
/// of the refused changes (args: cabal name…).
/// </summary>
public sealed record CabalSyncResult(IReadOnlyList<SavedCabal> Cabals, int Applied, IReadOnlyList<Problem> Problems);
