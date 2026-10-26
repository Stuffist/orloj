using System.Text.RegularExpressions;
using Olomoc.Tournaments;

namespace Olomoc.Models;

// Card catalog editor (admins, /api/admin/catalog): an entry and its texts in each language.
// Errors: Problem ("admin.problem." keys of ui.json).

/// <summary>Cog, its texts per language (null: unchanged) and its factions.</summary>
public sealed record CogEdit(Cog Cog, Dictionary<string, CogText?> Texts, List<string> Factions, bool IsNew);

/// <summary>Equipment or boon, and its texts per language (null: unchanged).</summary>
public sealed record EquipmentEdit(Equipment Equipment, Dictionary<string, EquipmentText?> Texts, bool IsNew);

/// <summary>Faction: alignment and texts per language (name, description).</summary>
public sealed record FactionEdit(Alignment Alignment, Dictionary<string, NamedText?> Texts);

public sealed record PublishedRequest(bool Published);

/// <summary>Changed ("modified") or new ("new") entries per collection, and text languages.</summary>
public sealed record CatalogStatus(Dictionary<string, Dictionary<string, string>> Entries, IReadOnlyList<string> Languages);

/// <summary>Rules of a card catalog entry, shared by the editor and the server.</summary>
public static partial class CatalogEditRules
{
    public const int MaxText = 2000, MaxAbilities = 10, MaxFrequencies = 3, MaxBuildRules = 5, MaxLifeLine = 30;
    public static readonly string[] FrequencyPeriods = ["PLANNING_PHASE", "CYCLE", "GAME"];

    [GeneratedRegex("^[A-Za-z0-9_-]{1,16}$")]
    public static partial Regex IdPattern();

    [GeneratedRegex(@"^[\w.-]{1,80}$")]
    private static partial Regex FilePattern();

    public static List<Problem> CheckCog(CogEdit edit, Catalog catalog, FactionReference factions, IReadOnlyList<string> languages, string referenceLanguage)
    {
        var cog = edit.Cog;
        var problems = CheckId(cog.Id, edit.IsNew, catalog, catalog.Cogs.Any(c => c.Id == cog.Id));
        if (cog.Rank is < 1 or > 3) problems.Add(Problem.Of("rank"));
        var ministries = catalog.Ministries.Select(m => m.Code).ToHashSet();
        if (cog.Ministries.Count == 0 || cog.Ministries.Any(m => !ministries.Contains(m)) || cog.Ministries.Distinct().Count() != cog.Ministries.Count)
            problems.Add(Problem.Of("ministries"));
        if (new[] { cog.Stats.Tech, cog.Stats.Melee, cog.Stats.Ranged, cog.Stats.Dodge }.Any(s => s is < 0 or > 20)) problems.Add(Problem.Of("stats"));
        if (cog.LifeLine.Count is 0 or > MaxLifeLine) problems.Add(Problem.Of("lifeLine", MaxLifeLine.ToString()));
        if (cog.BaseSize is < 20 or > 80) problems.Add(Problem.Of("baseSize"));
        if (cog.ExtraEquipment is { } extra && !ministries.Contains(extra.Ministry)) problems.Add(Problem.Of("extraEquipment"));
        if (edit.Factions.Any(f => factions.Factions.All(x => x.Code != f))) problems.Add(Problem.Of("factions"));
        CheckCard(cog.Abilities, cog.Art, problems);
        CheckTexts(edit.Texts.ToDictionary(kv => kv.Key, kv => Strings(kv.Value)), edit.IsNew, languages, referenceLanguage,
            edit.Texts.GetValueOrDefault(referenceLanguage)?.Name, problems);
        return problems;
    }

    public static List<Problem> CheckEquipment(EquipmentEdit edit, Catalog catalog, FactionReference factions, IReadOnlyList<string> languages, string referenceLanguage)
    {
        var e = edit.Equipment;
        var problems = CheckId(e.Id, edit.IsNew, catalog, catalog.Equipment.Any(x => x.Id == e.Id));
        var ministries = catalog.Ministries.Select(m => m.Code).ToHashSet();
        var affiliations = new[] { e.Ministry, e.Faction, e.CogId }.Count(a => a is not null);
        if (affiliations > 1
            || e.Ministry is { } m && !ministries.Contains(m)
            || e.Faction is { } f && factions.Factions.All(x => x.Code != f)
            || e.CogId is { } c && catalog.Cogs.All(x => x.Id != c))
            problems.Add(Problem.Of("affiliation"));
        if (e.LifeLinePrefix.Count > 10) problems.Add(Problem.Of("lifeLinePrefix"));
        if (e.BuildRules.Count > MaxBuildRules || e.BuildRules.Any(r =>
                r.Ranks.Any(rank => rank is < 1 or > 3)
                || r.Type != BuildRuleType.ALL_MINISTRIES && r.Ranks.Count == 0
                || r.Type == BuildRuleType.RECRUIT_MINISTRY && (r.Ministry is null || !ministries.Contains(r.Ministry))))
            problems.Add(Problem.Of("buildRule"));
        CheckCard(e.Abilities, e.Art, problems);
        CheckTexts(edit.Texts.ToDictionary(kv => kv.Key, kv => Strings(kv.Value)), edit.IsNew, languages, referenceLanguage,
            edit.Texts.GetValueOrDefault(referenceLanguage)?.Name, problems);
        return problems;
    }

    public static List<Problem> CheckFaction(string code, FactionEdit edit, FactionReference factions, IReadOnlyList<string> languages, string referenceLanguage)
    {
        var problems = new List<Problem>();
        if (factions.Factions.All(f => f.Code != code)) problems.Add(Problem.Of("unknown", code));
        CheckTexts(edit.Texts.ToDictionary(kv => kv.Key, kv => kv.Value is { } t ? new[] { t.Name, t.Description } : null),
            false, languages, referenceLanguage, edit.Texts.GetValueOrDefault(referenceLanguage)?.Name, problems);
        return problems;
    }

    /// <summary>Texts of a cog, all fields together (empty: nothing to write in this language).</summary>
    public static string?[]? Strings(CogText? t) =>
        t is null ? null : new[] { t.Name, t.Title, t.Instinct }.Concat(t.Abilities.SelectMany(a => new[] { a.Name, a.GameEffect })).ToArray();

    public static string?[]? Strings(EquipmentText? t) =>
        t is null ? null : new[] { t.Name }.Concat(t.Abilities.SelectMany(a => new[] { a.Name, a.GameEffect })).ToArray();

    public static bool IsEmpty(string?[]? strings) => strings is null || strings.All(string.IsNullOrWhiteSpace);

    private static List<Problem> CheckId(string? value, bool isNew, Catalog catalog, bool exists)
    {
        var problems = new List<Problem>();
        var id = value ?? "";
        if (!IdPattern().IsMatch(id)) problems.Add(Problem.Of("id"));
        else if (isNew && (catalog.Cogs.Any(c => c.Id == id) || catalog.Equipment.Any(e => e.Id == id))) problems.Add(Problem.Of("idTaken", id));
        else if (!isNew && !exists) problems.Add(Problem.Of("unknown", id));
        return problems;
    }

    private static void CheckCard(List<Ability> abilities, Art? art, List<Problem> problems)
    {
        if (abilities.Count > MaxAbilities) problems.Add(Problem.Of("abilities", MaxAbilities.ToString()));
        for (var i = 0; i < abilities.Count; i++)
        {
            var a = abilities[i];
            if (a.ActionCost is < 0 or > 10 || a.DarknessCost is < 0 or > 10 || a.Frequencies.Count > MaxFrequencies
                || a.Frequencies.Any(f => f.Count is < 1 or > 10 || !FrequencyPeriods.Contains(f.Per)))
                problems.Add(Problem.Of("ability", (i + 1).ToString()));
        }
        if (art is { } a2 && (!FilePattern().IsMatch(a2.Front) || a2.Back is { } back && !FilePattern().IsMatch(back))) problems.Add(Problem.Of("art"));
    }

    private static void CheckTexts(Dictionary<string, string?[]?> texts, bool isNew, IReadOnlyList<string> languages, string referenceLanguage,
        string? referenceName, List<Problem> problems)
    {
        if (texts.Keys.Any(l => !languages.Contains(l))) problems.Add(Problem.Of("language"));
        if (texts.Values.Any(t => t is not null && t.Any(s => s?.Length > MaxText))) problems.Add(Problem.Of("textTooLong", MaxText.ToString()));
        // Name in the reference language: required for a new entry, or if it is changed.
        if ((isNew || texts.GetValueOrDefault(referenceLanguage) is not null) && string.IsNullOrWhiteSpace(referenceName)) problems.Add(Problem.Of("name"));
    }
}
