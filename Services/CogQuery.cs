using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OlomocListBuilder.Models;

namespace OlomocListBuilder.Services;

public enum StatKind { Tech, Melee, Ranged, Dodge }

/// <summary>Condition sur une caractéristique, telle que saisie dans la recherche (« tir>4 »).</summary>
/// <param name="Start">Position du fragment dans la saisie, pour pouvoir le retirer.</param>
public sealed record StatCondition(StatKind Stat, string Op, int Value, int Start, int Length)
{
    public string Abbr => Stat switch
    {
        StatKind.Tech   => "TEC",
        StatKind.Melee  => "CBT",
        StatKind.Ranged => "TIR",
        _               => "ESQ",
    };

    /// <summary>Classe CSS de l'icône (voir StatLine).</summary>
    public string Icon => Stat.ToString().ToLowerInvariant();

    public string OpLabel => Op switch { ">=" => "≥", "<=" => "≤", _ => Op };

    public bool Matches(Stats s)
    {
        var v = Stat switch
        {
            StatKind.Tech   => s.Tech,
            StatKind.Melee  => s.Melee,
            StatKind.Ranged => s.Ranged,
            _               => s.Dodge,
        };
        return Op switch
        {
            ">"  => v > Value,
            "<"  => v < Value,
            ">=" => v >= Value,
            "<=" => v <= Value,
            _    => v == Value,
        };
    }
}

/// <summary>
/// Recherche de rouages : mots libres (nom ou titre, sans tenir compte des accents ni de la
/// casse) et conditions cumulables sur les caractéristiques, ex. « kantor tir>4 esq&lt;=3 ».
/// </summary>
public sealed partial class CogQuery
{
    private const string StatNames = "tec|technique|cbt|combat|tir|esq|esquive";
    private const string Ops = ">=|<=|≥|≤|>|<|=";

    [GeneratedRegex($@"\b({StatNames})\s*({Ops})\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionRx();

    // Condition en cours de frappe (opérateur sans nombre) : ignorée plutôt que traitée comme texte.
    [GeneratedRegex($@"\b({StatNames})\s*({Ops})(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PendingRx();

    public IReadOnlyList<StatCondition> Conditions { get; }
    public IReadOnlyList<string> Words { get; }

    public static readonly CogQuery Empty = Parse(null);

    private CogQuery(IReadOnlyList<StatCondition> conditions, IReadOnlyList<string> words)
    {
        Conditions = conditions;
        Words = words;
    }

    public static CogQuery Parse(string? input)
    {
        input ??= "";
        var conditions = ConditionRx().Matches(input)
            .Select(m => new StatCondition(
                StatOf(m.Groups[1].Value),
                m.Groups[2].Value switch { "≥" => ">=", "≤" => "<=", var op => op },
                int.TryParse(m.Groups[3].Value, out var v) ? v : int.MaxValue,
                m.Index, m.Length))
            .ToList();

        var rest = PendingRx().Replace(ConditionRx().Replace(input, " "), " ");
        var words = Fold(rest).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new CogQuery(conditions, words);
    }

    public bool Matches(Cog cog)
    {
        if (!Conditions.All(c => c.Matches(cog.Stats))) return false;
        if (Words.Count == 0) return true;
        var haystack = Fold(cog.Name + " " + cog.Title);
        return Words.All(haystack.Contains);
    }

    /// <summary>La saisie sans le fragment de la condition (pour la pastille « ✕ »).</summary>
    public static string Without(string input, StatCondition condition) =>
        string.Join(' ', input.Remove(condition.Start, condition.Length)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static StatKind StatOf(string name) => name.ToLowerInvariant() switch
    {
        "tec" or "technique" => StatKind.Tech,
        "cbt" or "combat"    => StatKind.Melee,
        "tir"                => StatKind.Ranged,
        _                    => StatKind.Dodge,
    };

    // Minuscules sans diacritiques, espaces normalisés : « Pétra » -> « petra ».
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsWhiteSpace(ch) ? ' ' : char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
