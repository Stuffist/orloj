using Olomoc.Models;

namespace Olomoc.Builder.Shared;

// Card catalog editor: editable copies of an entry and of its texts in each language, converted
// to CogEdit / EquipmentEdit when saving (empty strings: missing).

public sealed class FrequencyDraft
{
    public int Count { get; set; } = 1;
    public string Per { get; set; } = "CYCLE";
}

public sealed class AbilityTextDraft
{
    public string Name { get; set; } = "";
    public string Effect { get; set; } = "";
}

public sealed class AbilityDraft
{
    public Timing Timing { get; set; } = Timing.Active;
    public int ActionCost { get; set; }
    public int DarknessCost { get; set; }
    public List<FrequencyDraft> Frequencies { get; set; } = new();
    public Dictionary<string, AbilityTextDraft> Texts { get; set; } = new();

    public static AbilityDraft New(IEnumerable<string> languages) => new() { Texts = languages.ToDictionary(l => l, _ => new AbilityTextDraft()) };

    public static List<AbilityDraft> From(List<Ability> abilities, IReadOnlyList<string> languages, Func<string, int, AbilityText?> text) =>
        abilities.Select((a, i) => new AbilityDraft
        {
            Timing = a.Timing, ActionCost = a.ActionCost, DarknessCost = a.DarknessCost,
            Frequencies = a.Frequencies.Select(f => new FrequencyDraft { Count = f.Count, Per = f.Per }).ToList(),
            Texts = languages.ToDictionary(l => l, l => new AbilityTextDraft { Name = text(l, i)?.Name ?? "", Effect = text(l, i)?.GameEffect ?? "" }),
        }).ToList();

    public Ability ToAbility() => new()
    {
        Timing = Timing, ActionCost = ActionCost, DarknessCost = DarknessCost,
        Frequencies = Frequencies.Select(f => new Frequency { Count = f.Count, Per = f.Per }).ToList(),
    };

    /// <summary>Ability texts in a language; none: empty list (fallback to the reference language).</summary>
    public static List<AbilityText> TextsIn(List<AbilityDraft> abilities, string language)
    {
        var texts = abilities.Select(a => a.Texts.GetValueOrDefault(language) ?? new()).ToList();
        return texts.All(t => t.Name.Trim().Length == 0 && t.Effect.Trim().Length == 0)
            ? new()
            : texts.Select(t => new AbilityText { Name = NullIfEmpty(t.Name), GameEffect = NullIfEmpty(t.Effect) }).ToList();
    }

    internal static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

    /// <summary>Life line written as letters: S (standard), R (protection), T (tainted).</summary>
public static class LifeLineText
{
    public static string Format(IEnumerable<HealthPointType> points) => string.Concat(points.Select(p => p.ToString()));

    public static List<HealthPointType>? Parse(string text)
    {
        var points = new List<HealthPointType>();
        foreach (var c in text.ToUpperInvariant().Where(c => !char.IsWhiteSpace(c)))
        {
            if (!Enum.TryParse<HealthPointType>(c.ToString(), out var p)) return null;
            points.Add(p);
        }
        return points;
    }
}

public sealed class CogTextDraft
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Instinct { get; set; } = "";
}

public sealed class CogDraft
{
    public bool IsNew { get; init; }
    public string Id { get; set; } = "";
    public int Rank { get; set; } = 1;
    public HashSet<string> Ministries { get; set; } = new();
    public HashSet<string> Factions { get; set; } = new();
    public int Tech { get; set; }
    public int Melee { get; set; }
    public int Ranged { get; set; }
    public int Dodge { get; set; }
    public string LifeLine { get; set; } = "";
    public int? BaseSize { get; set; }
    public string Set { get; set; } = "";
    public string ArtFront { get; set; } = "";
    public string ArtBack { get; set; } = "";
    public string ExtraMinistry { get; set; } = "";
    public EquipmentKind ExtraKind { get; set; } = EquipmentKind.EQUIPMENT;
    public bool Published { get; set; }
    public Dictionary<string, CogTextDraft> Texts { get; set; } = new();
    public List<AbilityDraft> Abilities { get; set; } = new();

    /// <summary>New cog: draft (unpublished), one ability.</summary>
    public static CogDraft New(IReadOnlyList<string> languages) => new()
    {
        IsNew = true,
        Texts = languages.ToDictionary(l => l, _ => new CogTextDraft()),
        Abilities = [AbilityDraft.New(languages)],
    };

    public static CogDraft From(Cog cog, IReadOnlyDictionary<string, CardTexts> texts, FactionReference factions)
    {
        var languages = texts.Keys.ToList();
        CogText? Text(string l) => texts[l].Cogs.GetValueOrDefault(cog.Id);
        return new()
        {
            Id = cog.Id, Rank = cog.Rank, Ministries = cog.Ministries.ToHashSet(),
            Factions = factions.Factions.Where(f => f.CogIds.Contains(cog.Id)).Select(f => f.Code).ToHashSet(),
            Tech = cog.Stats.Tech, Melee = cog.Stats.Melee, Ranged = cog.Stats.Ranged, Dodge = cog.Stats.Dodge,
            LifeLine = LifeLineText.Format(cog.LifeLine), BaseSize = cog.BaseSize, Set = cog.Set ?? "",
            ArtFront = cog.Art?.Front ?? "", ArtBack = cog.Art?.Back ?? "",
            ExtraMinistry = cog.ExtraEquipment?.Ministry ?? "", ExtraKind = cog.ExtraEquipment?.Kind ?? EquipmentKind.EQUIPMENT,
            Published = cog.Published,
            Texts = languages.ToDictionary(l => l, l => new CogTextDraft { Name = Text(l)?.Name ?? "", Title = Text(l)?.Title ?? "", Instinct = Text(l)?.Instinct ?? "" }),
            Abilities = AbilityDraft.From(cog.Abilities, languages, (l, i) => Text(l)?.Abilities.ElementAtOrDefault(i)),
        };
    }

    public CogEdit ToEdit() => new(new Cog
    {
        Id = Id.Trim(), Rank = Rank, Ministries = Ministries.ToList(),
        Stats = new Stats { Tech = Tech, Melee = Melee, Ranged = Ranged, Dodge = Dodge },
        LifeLine = LifeLineText.Parse(LifeLine) ?? [],
        Abilities = Abilities.Select(a => a.ToAbility()).ToList(),
        BaseSize = BaseSize,
        ExtraEquipment = ExtraMinistry.Length > 0 ? new ExtraEquipmentRule { Ministry = ExtraMinistry, Kind = ExtraKind } : null,
        Art = ArtFront.Trim().Length > 0 ? new Art { Front = ArtFront.Trim(), Back = AbilityDraft.NullIfEmpty(ArtBack) } : null,
        Set = AbilityDraft.NullIfEmpty(Set),
        Published = Published,
    }, Texts.ToDictionary(kv => kv.Key, kv => (CogText?)new CogText
    {
        Name = AbilityDraft.NullIfEmpty(kv.Value.Name), Title = AbilityDraft.NullIfEmpty(kv.Value.Title),
        Instinct = AbilityDraft.NullIfEmpty(kv.Value.Instinct), Abilities = AbilityDraft.TextsIn(Abilities, kv.Key),
    }), Factions.ToList(), IsNew);
}

public sealed class BuildRuleDraft
{
    public BuildRuleType Type { get; set; } = BuildRuleType.RECRUIT_WEARER;
    public HashSet<int> Ranks { get; set; } = new();
    public string Ministry { get; set; } = "";
}

/// <summary>Exclusive affiliation of a card: none, Ministry, faction or cog.</summary>
public enum AffiliationKind { None, Ministry, Faction, Cog }

public sealed class EquipmentDraft
{
    public bool IsNew { get; init; }
    public string Id { get; set; } = "";
    public EquipmentKind Kind { get; set; } = EquipmentKind.EQUIPMENT;
    public AffiliationKind Affiliation { get; set; } = AffiliationKind.Ministry;
    public string AffiliationValue { get; set; } = "";
    public string LifeLinePrefix { get; set; } = "";
    public string Set { get; set; } = "";
    public string ArtFront { get; set; } = "";
    public bool Published { get; set; }
    public Dictionary<string, string> Names { get; set; } = new();
    public List<AbilityDraft> Abilities { get; set; } = new();
    public List<BuildRuleDraft> BuildRules { get; set; } = new();

    public static EquipmentDraft New(IReadOnlyList<string> languages) => new()
    {
        IsNew = true,
        Names = languages.ToDictionary(l => l, _ => ""),
        Abilities = [AbilityDraft.New(languages)],
    };

    public static EquipmentDraft From(Equipment e, IReadOnlyDictionary<string, CardTexts> texts)
    {
        var languages = texts.Keys.ToList();
        EquipmentText? Text(string l) => texts[l].Equipment.GetValueOrDefault(e.Id);
        return new()
        {
            Id = e.Id, Kind = e.Kind,
            Affiliation = e.Ministry is not null ? AffiliationKind.Ministry : e.Faction is not null ? AffiliationKind.Faction
                : e.CogId is not null ? AffiliationKind.Cog : AffiliationKind.None,
            AffiliationValue = e.Ministry ?? e.Faction ?? e.CogId ?? "",
            LifeLinePrefix = LifeLineText.Format(e.LifeLinePrefix), Set = e.Set ?? "", ArtFront = e.Art?.Front ?? "",
            Published = e.Published,
            Names = languages.ToDictionary(l => l, l => Text(l)?.Name ?? ""),
            Abilities = AbilityDraft.From(e.Abilities, languages, (l, i) => Text(l)?.Abilities.ElementAtOrDefault(i)),
            BuildRules = e.BuildRules.Select(r => new BuildRuleDraft { Type = r.Type, Ranks = r.Ranks.ToHashSet(), Ministry = r.Ministry ?? "" }).ToList(),
        };
    }

    public EquipmentEdit ToEdit()
    {
        string? Value(AffiliationKind kind) => Affiliation == kind ? AbilityDraft.NullIfEmpty(AffiliationValue) : null;
        return new(new Equipment
        {
            Id = Id.Trim(), Kind = Kind,
            Ministry = Value(AffiliationKind.Ministry), Faction = Value(AffiliationKind.Faction), CogId = Value(AffiliationKind.Cog),
            Abilities = Abilities.Select(a => a.ToAbility()).ToList(),
            LifeLinePrefix = LifeLineText.Parse(LifeLinePrefix) ?? [],
            Art = ArtFront.Trim().Length > 0 ? new Art { Front = ArtFront.Trim() } : null,
            Set = AbilityDraft.NullIfEmpty(Set),
            BuildRules = BuildRules.Select(r => new BuildRule
            {
                Type = r.Type, Ranks = r.Ranks.Order().ToList(),
                Ministry = r.Type == BuildRuleType.RECRUIT_MINISTRY ? AbilityDraft.NullIfEmpty(r.Ministry) : null,
            }).ToList(),
            Published = Published,
        }, Names.ToDictionary(kv => kv.Key, kv => (EquipmentText?)new EquipmentText
        {
            Name = AbilityDraft.NullIfEmpty(kv.Value), Abilities = AbilityDraft.TextsIn(Abilities, kv.Key),
        }), IsNew);
    }
}

public sealed class NamedTextDraft
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class FactionDraft
{
    public string Code { get; init; } = "";
    public Alignment Alignment { get; set; }
    public Dictionary<string, NamedTextDraft> Texts { get; set; } = new();

    public static FactionDraft From(Faction f, IReadOnlyDictionary<string, CardTexts> texts) => new()
    {
        Code = f.Code, Alignment = f.Alignment,
        Texts = texts.ToDictionary(kv => kv.Key, kv => new NamedTextDraft
        {
            Name = kv.Value.Factions.GetValueOrDefault(f.Code)?.Name ?? "",
            Description = kv.Value.Factions.GetValueOrDefault(f.Code)?.Description ?? "",
        }),
    };

    public FactionEdit ToEdit() => new(Alignment, Texts.ToDictionary(kv => kv.Key, kv => (NamedText?)new NamedText
    {
        Name = AbilityDraft.NullIfEmpty(kv.Value.Name), Description = AbilityDraft.NullIfEmpty(kv.Value.Description),
    }));
}
