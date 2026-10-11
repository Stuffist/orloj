namespace OlomocListBuilder.Models;

// Export JSON d'une cabale (schemas/olomoc-cabal.schema.json). Les ids et codes font
// référence au référentiel ; les noms sont donnés dans la langue de l'export, pour lecture.

public sealed record IdName(string Id, string Name);

public sealed record CodeName(string Code, string Name);

/// <summary>Un emplacement de la cabale : rouage de ce rang et son équipement/don éventuel.</summary>
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

/// <summary>Cabale sauvegardée sur l'appareil (CabalLibrary) et sa date de sauvegarde.</summary>
public sealed record SavedCabal(DateTimeOffset SavedAt, CabalExport Cabal);
