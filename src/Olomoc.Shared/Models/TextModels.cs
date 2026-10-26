namespace Olomoc.Models;

// Language packs (wwwroot/i18n/<language>/): texts indexed by the ids and codes of the
// neutral card catalog. Everything is optional: a missing text falls back to the reference language.

public sealed class LanguageInfo
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class NamedText
{
    public string? Name { get; init; }
    public string? Description { get; init; }
}

public sealed class AbilityText
{
    public string? Name { get; init; }
    public string? GameEffect { get; init; }
}

public sealed class CogText
{
    public string? Name { get; init; }
    public string? Title { get; init; }
    public string? Instinct { get; init; }
    public List<AbilityText> Abilities { get; init; } = new();
}

public sealed class EquipmentText
{
    public string? Name { get; init; }
    public List<AbilityText> Abilities { get; init; } = new();
}

public sealed class CardTexts
{
    public string Language { get; init; } = "";
    public Dictionary<string, NamedText> Ministries { get; init; } = new();
    public Dictionary<string, NamedText> Factions { get; init; } = new();
    public Dictionary<string, CogText> Cogs { get; init; } = new();
    public Dictionary<string, EquipmentText> Equipment { get; init; } = new();
}

/// <summary>Resolved text; <see cref="IsFallback"/> if it comes from the reference language for lack of a translation.</summary>
public sealed record Localized(string Text, bool IsFallback = false)
{
    public override string ToString() => Text;
}

// ---- Legality messages, language-neutral: rendered by the interface ----

public enum IssueArgKind { Cog, Equipment, Faction, Ministry }

/// <summary>Argument of a message: one or more ids/codes, translated at display time.</summary>
public sealed record IssueArg(IssueArgKind Kind, IReadOnlyList<string> Values)
{
    public static IssueArg Cogs(IEnumerable<string> ids) => new(IssueArgKind.Cog, ids.ToList());
    public static IssueArg Cog(string id) => new(IssueArgKind.Cog, new[] { id });
    public static IssueArg Equipment(string id) => new(IssueArgKind.Equipment, new[] { id });
    public static IssueArg Faction(string code) => new(IssueArgKind.Faction, new[] { code });
    public static IssueArg Ministry(string code) => new(IssueArgKind.Ministry, new[] { code });
}

/// <summary>Legality problem: message key (ui.json, "legality." prefix) and arguments.</summary>
public sealed record LegalityIssue(string Key, params IssueArg[] Args);
