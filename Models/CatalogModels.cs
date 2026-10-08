using System.Text.Json.Serialization;

namespace OlomocListBuilder.Models;

// Les valeurs (noms, titres, descriptions) restent en français ; seules les clefs sont anglaises.

public enum HealthPip { N, A, T }          // Normal, Armored, Tainted
public enum Temporality { Active, Passive }
public enum GearKind { EQUIPMENT, GIFT }

public sealed class Ministry
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? Emblem { get; init; }
}

public sealed class Stats
{
    public int Technique { get; init; }
    public int Melee { get; init; }
    public int Ranged { get; init; }
    public int Dodge { get; init; }
}

public sealed class Frequency
{
    public int Count { get; init; }
    public string Per { get; init; } = "";   // PLANNING_PHASE | CYCLE | GAME
}

public sealed class Ability
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public Temporality Temporality { get; init; }
    public int ActionCost { get; init; }
    public int DarknessCost { get; init; }
    public Frequency? Frequency { get; init; }
}

public sealed class Art
{
    public string Recto { get; init; } = "";
    public string? Verso { get; init; }
}

public sealed class Cog
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Title { get; init; }
    public List<string> Ministries { get; init; } = new();
    public int Rank { get; init; }
    public int BaseSize { get; init; }
    public Stats Stats { get; init; } = new();
    public List<HealthPip> Health { get; init; } = new();
    public List<Ability> Abilities { get; init; } = new();
    public string? Instinct { get; init; }
    public Art? Art { get; init; }
    public string? Set { get; init; }
}

public sealed class Gear
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public GearKind Kind { get; init; }
    public string? Ministry { get; init; }   // affiliation exclusive :
    public string? Faction { get; init; }     //   ministère, faction,
    public string? CogId { get; init; }       //   ou rouage nommé
    public List<Ability> Abilities { get; init; } = new();
    public Art? Art { get; init; }
    public string? Set { get; init; }

    [JsonIgnore] public bool IsFactionGear  => Faction  is not null;
    [JsonIgnore] public bool IsMinistryGear => Ministry is not null;
    [JsonIgnore] public bool IsNamedGear    => CogId    is not null;
}

public sealed class Catalog
{
    public string Version { get; init; } = "";
    public List<Ministry> Ministries { get; init; } = new();
    public List<Cog> Cogs { get; init; } = new();
    public List<Gear> Gears { get; init; } = new();
}

// ---- Référentiel de faction (document distinct) ----

public sealed class FactionEntry
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public List<string> CogIds { get; init; } = new();
}

public sealed class FactionCatalog
{
    public string Version { get; init; } = "";
    public string? Source { get; init; }
    public List<FactionEntry> Factions { get; init; } = new();
}
