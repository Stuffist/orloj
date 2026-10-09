namespace OlomocListBuilder.Models;

// Paquets de langue (wwwroot/i18n/<langue>/) : textes indexés par les ids et codes du
// référentiel neutre. Tout est optionnel : un texte absent se replie sur la langue de référence.

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

/// <summary>Texte résolu ; <see cref="IsFallback"/> s'il vient de la langue de référence faute de traduction.</summary>
public sealed record Localized(string Text, bool IsFallback = false)
{
    public override string ToString() => Text;
}

// ---- Messages de légalité, neutres en langue : rendus par l'interface ----

public enum IssueArgKind { Cog, Equipment, Faction, Ministry }

/// <summary>Argument d'un message : un ou plusieurs ids/codes, traduits à l'affichage.</summary>
public sealed record IssueArg(IssueArgKind Kind, IReadOnlyList<string> Values)
{
    public static IssueArg Cogs(IEnumerable<string> ids) => new(IssueArgKind.Cog, ids.ToList());
    public static IssueArg Cog(string id) => new(IssueArgKind.Cog, new[] { id });
    public static IssueArg Equipment(string id) => new(IssueArgKind.Equipment, new[] { id });
    public static IssueArg Faction(string code) => new(IssueArgKind.Faction, new[] { code });
    public static IssueArg Ministry(string code) => new(IssueArgKind.Ministry, new[] { code });
}

/// <summary>Problème de légalité : clé de message (ui.json, préfixe « legality. ») et arguments.</summary>
public sealed record LegalityIssue(string Key, params IssueArg[] Args);
