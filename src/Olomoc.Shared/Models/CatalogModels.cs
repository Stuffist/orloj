using System.Text.Json.Serialization;

namespace Olomoc.Models;

// Language-neutral card catalog, identifiers aligned with the English rulebook.
// Texts (names, titles, game effects) are in the language packs (TextModels.cs).

public enum HealthPointType { S, R, T }     // Standard, Reinforced (Protection), Tainted
public enum Timing { Active, Permanent }
public enum EquipmentKind { EQUIPMENT, BOON }
public enum BuildRuleType { ALL_MINISTRIES, RECRUIT_WEARER, RECRUIT_MINISTRY }
public enum Alignment { LIGHT, DARK }       // Light, Darkness (faction reference)

public sealed class Ministry
{
    public string Code { get; init; } = "";
    public string? Emblem { get; init; }
}

public sealed class Stats
{
    public int Tech { get; init; }
    public int Melee { get; init; }
    public int Ranged { get; init; }
    public int Dodge { get; init; }
}

public sealed class Frequency
{
    public int Count { get; init; }
    public string Per { get; init; } = "";   // PLANNING_PHASE | CYCLE | GAME
}

/// <summary>Ability; its name and game effect are at the same rank in the language packs.</summary>
public sealed class Ability
{
    public Timing Timing { get; init; }
    public int ActionCost { get; init; }
    public int DarknessCost { get; init; }
    public List<Frequency> Frequencies { get; init; } = new();   // cumulative limits; empty = unlimited
}

public sealed class Art
{
    public string Front { get; init; } = "";
    public string? Back { get; init; }
}

public sealed class Cog
{
    public string Id { get; init; } = "";
    public List<string> Ministries { get; init; } = new();
    public int Rank { get; init; }
    public int? BaseSize { get; init; }       // printed on the back, missing from the stat line
    public Stats Stats { get; init; } = new();
    public List<HealthPointType> LifeLine { get; init; } = new();
    public List<Ability> Abilities { get; init; } = new();
    public ExtraEquipmentRule? ExtraEquipment { get; init; }    // extra card at the start of the game
    public Art? Art { get; init; }
    public string? Set { get; init; }
    /// <summary>true: published; missing or false: draft, visible to admins only.</summary>
    public bool Published { get; init; }
}

/// <summary>Extra card a cog can carry from the start of the first cycle (Medic Ruzicka).</summary>
public sealed class ExtraEquipmentRule
{
    public string Ministry { get; init; } = "";
    public EquipmentKind Kind { get; init; }

    public bool Allows(Equipment e) => e.Ministry == Ministry && e.Kind == Kind;
}

/// <summary>Equipment or boon card.</summary>
public sealed class Equipment
{
    public string Id { get; init; } = "";
    public EquipmentKind Kind { get; init; }
    public string? Ministry { get; init; }   // exclusive affiliation:
    public string? Faction { get; init; }     //   Ministry, faction,
    public string? CogId { get; init; }       //   or equipped cog
    public List<Ability> Abilities { get; init; } = new();
    public List<HealthPointType> LifeLinePrefix { get; init; } = new();   // HP added at the start of the life line
    public Art? Art { get; init; }
    public string? Set { get; init; }
    public List<BuildRule> BuildRules { get; init; } = new();
    /// <summary>true: published; missing or false: draft, visible to admins only.</summary>
    public bool Published { get; init; }

    [JsonIgnore] public bool IsFactionAffiliated  => Faction  is not null;
    [JsonIgnore] public bool IsMinistryAffiliated => Ministry is not null;
    [JsonIgnore] public bool IsCogAffiliated      => CogId    is not null;
    [JsonIgnore] public bool GrantsAllMinistries => BuildRules.Any(r => r.Type == BuildRuleType.ALL_MINISTRIES);
}

/// <summary>Effect of a card on the cabal's composition (see $defs/buildRule of the schema).</summary>
public sealed class BuildRule
{
    public BuildRuleType Type { get; init; }
    public List<int> Ranks { get; init; } = new();
    public string? Ministry { get; init; }

    /// <summary>Does the cog meet this rule's out-of-faction recruitment conditions?</summary>
    public bool CanRecruit(Cog cog, IReadOnlyCollection<string> ministries) => Type switch
    {
        BuildRuleType.RECRUIT_WEARER   => Ranks.Contains(cog.Rank),
        BuildRuleType.RECRUIT_MINISTRY => Ranks.Contains(cog.Rank) && ministries.Contains(Ministry!),
        _ => false
    };
}

public sealed class Catalog
{
    public string Version { get; init; } = "";
    public List<Ministry> Ministries { get; init; } = new();
    public List<Cog> Cogs { get; init; } = new();
    public List<Equipment> Equipment { get; init; } = new();
}

// ---- Faction reference (separate document) ----

public sealed class Faction
{
    public string Code { get; init; } = "";
    public Alignment Alignment { get; init; }
    public List<string> CogIds { get; init; } = new();
}

public sealed class FactionReference
{
    public string Version { get; init; } = "";
    public string? Source { get; init; }
    public List<Faction> Factions { get; init; } = new();
}
