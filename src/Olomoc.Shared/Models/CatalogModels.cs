using System.Text.Json.Serialization;

namespace Olomoc.Models;

// Référentiel neutre en langue, identifiants alignés sur le livre de règles anglais.
// Les textes (noms, titres, effets de jeu) sont dans les paquets de langue (TextModels.cs).

public enum HealthPointType { S, R, T }     // Standard, Reinforced (Protection), Tainted
public enum Timing { Active, Permanent }
public enum EquipmentKind { EQUIPMENT, BOON }
public enum BuildRuleType { ALL_MINISTRIES, RECRUIT_WEARER, RECRUIT_MINISTRY }

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

/// <summary>Capacité ; son nom et son effet de jeu sont au même rang dans les paquets de langue.</summary>
public sealed class Ability
{
    public Timing Timing { get; init; }
    public int ActionCost { get; init; }
    public int DarknessCost { get; init; }
    public List<Frequency> Frequencies { get; init; } = new();   // limites cumulatives ; vide = illimitée
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
    public int? BaseSize { get; init; }       // imprimée au verso, absente du relevé
    public Stats Stats { get; init; } = new();
    public List<HealthPointType> LifeLine { get; init; } = new();
    public List<Ability> Abilities { get; init; } = new();
    public ExtraEquipmentRule? ExtraEquipment { get; init; }    // carte supplémentaire au début de la partie
    public Art? Art { get; init; }
    public string? Set { get; init; }
}

/// <summary>Carte supplémentaire qu'un rouage peut porter dès le début du premier cycle (Medic Ruzicka).</summary>
public sealed class ExtraEquipmentRule
{
    public string Ministry { get; init; } = "";
    public EquipmentKind Kind { get; init; }

    public bool Allows(Equipment e) => e.Ministry == Ministry && e.Kind == Kind;
}

/// <summary>Carte équipement ou don.</summary>
public sealed class Equipment
{
    public string Id { get; init; } = "";
    public EquipmentKind Kind { get; init; }
    public string? Ministry { get; init; }   // affiliation exclusive :
    public string? Faction { get; init; }     //   Ministère, faction,
    public string? CogId { get; init; }       //   ou rouage équipé
    public List<Ability> Abilities { get; init; } = new();
    public List<HealthPointType> LifeLinePrefix { get; init; } = new();   // PV ajoutés en tête de ligne de vie
    public Art? Art { get; init; }
    public string? Set { get; init; }
    public List<BuildRule> BuildRules { get; init; } = new();

    [JsonIgnore] public bool IsFactionAffiliated  => Faction  is not null;
    [JsonIgnore] public bool IsMinistryAffiliated => Ministry is not null;
    [JsonIgnore] public bool IsCogAffiliated      => CogId    is not null;
    [JsonIgnore] public bool GrantsAllMinistries => BuildRules.Any(r => r.Type == BuildRuleType.ALL_MINISTRIES);
}

/// <summary>Effet d'une carte sur la composition de la cabale (voir $defs/buildRule du schéma).</summary>
public sealed class BuildRule
{
    public BuildRuleType Type { get; init; }
    public List<int> Ranks { get; init; } = new();
    public string? Ministry { get; init; }

    /// <summary>Le rouage satisfait-il les conditions de recrutement hors faction de cette règle ?</summary>
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

// ---- Référentiel de faction (document distinct) ----

public sealed class Faction
{
    public string Code { get; init; } = "";
    public List<string> CogIds { get; init; } = new();
}

public sealed class FactionReference
{
    public string Version { get; init; } = "";
    public string? Source { get; init; }
    public List<Faction> Factions { get; init; } = new();
}
