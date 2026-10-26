namespace Olomoc.Models;

// JSON export of a cabal (schemas/olomoc-cabal.schema.json). Ids and codes refer to the card
// catalog; names are given in the export's language, for reading.

public sealed record IdName(string Id, string Name);

public sealed record CodeName(string Code, string Name);

/// <summary>A slot of the cabal: cog of this rank and its optional equipment/boon.</summary>
public sealed record CabalSlot(int Rank, IdName Cog, IdName? Equipment);

public sealed record CabalExport(
    string Format,
    int Version,
    string Language,
    string? Name,
    CodeName? Faction,
    IReadOnlyList<CabalSlot> Cogs)
{
    public const string FormatId = "olomoc-cabal";
    public const int CurrentVersion = 1;
}

/// <summary>
/// Saved cabal (CabalLibrary) and its save date. <see cref="Pending"/>: not sent to the account
/// yet (saved offline, or without an account).
/// </summary>
public sealed record SavedCabal(DateTimeOffset SavedAt, CabalExport Cabal, bool Pending = false);
