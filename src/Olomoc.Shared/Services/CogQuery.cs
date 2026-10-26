using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Olomoc.Models;

namespace Olomoc.Services;

public enum StatKind { Tech, Melee, Ranged, Dodge }

/// <summary>Condition on a stat, as typed in the search ("tir>4").</summary>
/// <param name="Start">Position of the fragment in the input, so that it can be removed.</param>
public sealed record StatCondition(StatKind Stat, string Op, int Value, int Start, int Length)
{
    /// <summary>CSS class of the icon (see StatLine).</summary>
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
/// Cog search: free words (compared without accents or case to the cog's searchable text) and
/// cumulative conditions on the stats, e.g. "kantor tir>4 esq&lt;=3" (French keywords). The stat
/// keywords (three-letter codes, names) come from the language packs: the syntax accepts those
/// of every language, whatever the displayed language.
/// </summary>
public sealed class CogQuery
{
    private const string Ops = ">=|<=|≥|≤|>|<|=";

    public IReadOnlyList<StatCondition> Conditions { get; }
    public IReadOnlyList<string> Words { get; }

    public static readonly CogQuery Empty = new(Array.Empty<StatCondition>(), Array.Empty<string>());

    private CogQuery(IReadOnlyList<StatCondition> conditions, IReadOnlyList<string> words)
    {
        Conditions = conditions;
        Words = words;
    }

    /// <param name="keywords">Lowercase keyword → stat (see LocalizationService.StatKeywords).</param>
    public static CogQuery Parse(string? input, IReadOnlyDictionary<string, StatKind> keywords)
    {
        input ??= "";
        if (keywords.Count == 0) return new(Array.Empty<StatCondition>(), Fold(input).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // Longest keywords first: "technique" before "tec".
        var names = string.Join('|', keywords.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        var condition = new Regex($@"\b({names})\s*({Ops})\s*(\d+)", RegexOptions.IgnoreCase);
        // Condition being typed (operator without a number): ignored rather than treated as text.
        var pending = new Regex($@"\b({names})\s*({Ops})(?=\s|$)", RegexOptions.IgnoreCase);

        var conditions = condition.Matches(input)
            .Select(m => new StatCondition(
                keywords[m.Groups[1].Value.ToLowerInvariant()],
                m.Groups[2].Value switch { "≥" => ">=", "≤" => "<=", var op => op },
                int.TryParse(m.Groups[3].Value, out var v) ? v : int.MaxValue,
                m.Index, m.Length))
            .ToList();

        var rest = pending.Replace(condition.Replace(input, " "), " ");
        return new(conditions, Fold(rest).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <param name="searchText">Searchable text of the cog (names, titles), see LocalizationService.SearchText.</param>
    public bool Matches(Cog cog, string searchText)
    {
        if (!Conditions.All(c => c.Matches(cog.Stats))) return false;
        if (Words.Count == 0) return true;
        var haystack = Fold(searchText);
        return Words.All(haystack.Contains);
    }

    /// <summary>The input without the condition's fragment (for the "✕" chip).</summary>
    public static string Without(string input, StatCondition condition) =>
        string.Join(' ', input.Remove(condition.Start, condition.Length)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Lowercase without diacritics, normalized spaces: "Pétra" -> "petra".</summary>
    public static string Fold(string s)
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
